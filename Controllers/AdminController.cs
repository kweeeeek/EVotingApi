using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Requests;
using EVoting.Api.Models.Responses;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Controllers
{
    [ApiController]
    [Route("api/v1/admin")]
    [Authorize(Roles = "Admin")]
    [ApiErrorModelState]
    public class AdminController : ControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly ILogger<AdminController> _logger;
        private readonly IAuditService _audit;

        public AdminController(ApiDbContext db, ILogger<AdminController> logger, IAuditService audit)
        {
            _db = db;
            _logger = logger;
            _audit = audit;
        }

        // ═══ MEMBERS ══════════════════════════════════════════════════════════

        // ── GET /api/v1/admin/members ─────────────────────────────────────────
        /// <summary>List members with optional search and pagination.</summary>
        [HttpGet("members")]
        [RequirePermission(PermissionCodes.MemberView)]
        public async Task<IActionResult> GetMembers(
            [FromQuery] string? search,
            [FromQuery] int? branchId,
            [FromQuery] bool? eligible,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            CancellationToken ct = default)
        {
            // Clamp pageSize to prevent a single request dumping the entire table.
            // 200 is well above any practical need and keeps response sizes bounded.
            pageSize = Math.Clamp(pageSize, 1, 200);

            var query = _db.Members
                .AsNoTracking()
                .Include(m => m.Branch)
                .Where(m => m.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(m =>
                    m.MemberName.Contains(search) ||
                    m.AccountCode.Contains(search));

            if (branchId.HasValue)
                query = query.Where(m => m.BranchId == branchId.Value);

            if (eligible.HasValue)
                query = query.Where(m => m.IsEligibleToVote == eligible.Value);

            int total = await query.CountAsync(ct);

            var members = await query
                .OrderBy(m => m.MemberName)
                .ThenBy(m => m.MemberId)               // tie-breaker: equal names must not change places between pages
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return Ok(new
            {
                total,
                page,
                pageSize,
                data = members.Select(m => new MemberResponse
                {
                    MemberId = m.MemberId,
                    AccountCode = m.AccountCode,
                    MemberName = m.MemberName,
                    Branch = m.Branch?.BranchName ?? "",
                    IsEligible = m.IsEligibleToVote,
                    Remarks = m.VotingRemarks
                })
            });
        }

        // ── GET /api/v1/admin/members/{id}/vote-status ───────────────────────
        /// <summary>Check if a specific member has voted in an event.</summary>
        [HttpGet("members/{memberId:int}/vote-status/{eventId:int}")]
        [RequirePermission(PermissionCodes.MemberView, PermissionCodes.EventView)]
        public async Task<IActionResult> GetMemberVoteStatus(
            int memberId, int eventId, CancellationToken ct)
        {
            var member = await _db.Members.AsNoTracking()
                .Include(m => m.Branch)
                .FirstOrDefaultAsync(m => m.MemberId == memberId, ct);
            if (member == null)
                return NotFound(new ApiError { Error = "Member not found." });

            var log = await _db.VoterLogs.AsNoTracking()
                .FirstOrDefaultAsync(v => v.MemberId == memberId && v.EventId == eventId, ct);

            return Ok(new MemberResponse
            {
                MemberId = member.MemberId,
                AccountCode = member.AccountCode,
                MemberName = member.MemberName,
                Branch = member.Branch?.BranchName ?? "",
                IsEligible = member.IsEligibleToVote,
                Remarks = member.VotingRemarks,
                HasVoted = log?.HasVoted ?? false
            });
        }

        // ═══ CANDIDATES ═══════════════════════════════════════════════════════

        // ── GET /api/v1/admin/candidates ──────────────────────────────────────
        /// <summary>List all candidacies, optionally filtered by status.</summary>
        [HttpGet("candidates")]
        [RequirePermission(PermissionCodes.CandidateView)]
        public async Task<IActionResult> GetCandidates(
            [FromQuery] string? status,
            [FromQuery] string? position,
            CancellationToken ct = default)
        {
            var query = _db.Candidacies
                .AsNoTracking()
                .Include(c => c.Member)
                .Where(c => c.IsActive);

            if (!string.IsNullOrWhiteSpace(status))
                query = query.Where(c => c.Status == status);

            if (!string.IsNullOrWhiteSpace(position))
                query = query.Where(c => c.Position == position);

            var candidates = await query
                .OrderBy(c => c.Position)
                // BUG FIX: CandidateDisplayName is [NotMapped] — EF Core cannot translate it to SQL.
                // Use the underlying translatable expression instead.
                .ThenBy(c => c.MemberId != null ? c.Member!.MemberName : c.WalkInCandidateName ?? "")
                .ToListAsync(ct);

            return Ok(candidates.Select(c => new CandidateResponse
            {
                CandidacyId = c.CandidacyId,
                CandidateName = c.CandidateDisplayName,
                Position = c.Position,
                Status = c.Status,
                PreviousPositions = c.PreviousPositions,
                Gender = c.Gender,
                HighestEducation = c.HighestEducation,
                Reason = c.Reason,
                PhotoBase64 = c.CandidateImage != null
                    ? Convert.ToBase64String(c.CandidateImage) : null
            }));
        }

        // ── PATCH /api/v1/admin/candidates/{id}/status ────────────────────────
        /// <summary>
        /// Approve, reject or reset a candidacy. Locked while an election is open (previously a UI-only rule),
        /// audited, and keeps DateApproved / DateRejected consistent with the status.
        /// </summary>
        [HttpPatch("candidates/{id:int}/status")]
        [RequirePermission(PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> UpdateCandidacyStatus(
            int id, [FromBody] UpdateCandidacyStatusRequest req, CancellationToken ct)
        {
            var openTitle = await _db.Events.AsNoTracking()
                .Where(e => e.IsActive && e.Status == "Opened")
                .Select(e => e.Title).FirstOrDefaultAsync(ct);
            if (openTitle != null)
                return Conflict(new ApiError
                {
                    Error = $"Candidate records are locked while the election \"{openTitle}\" is open.",
                    Detail = "Close the election to approve or reject candidacies.",
                    Code = "EVENT_OPEN_LOCK"
                });

            var candidate = await _db.Candidacies.FirstOrDefaultAsync(c => c.CandidacyId == id && c.IsActive, ct);
            if (candidate == null)
                return NotFound(new ApiError { Error = "Candidacy not found." });

            var previous = candidate.Status;
            var now = DateTime.UtcNow;
            candidate.Status = req.Status;
            candidate.UpdatedAt = now;

            if (req.Status != previous)
            {
                switch (req.Status)
                {
                    case "Approved": candidate.DateApproved = now; candidate.DateRejected = null; break;
                    case "Rejected": candidate.DateRejected = now; candidate.DateApproved = null; break;
                    default: candidate.DateApproved = null; candidate.DateRejected = null; break;
                }
            }
            if (req.Status == "Rejected")
                candidate.RejectionReason = (req.RejectionReason ?? "").Trim();

            var action = req.Status == previous ? AuditActions.Update
                       : req.Status == "Approved" ? AuditActions.ApproveCandidacy
                       : req.Status == "Rejected" ? AuditActions.RejectCandidacy
                       : AuditActions.Update;
            _audit.Stage(action, AuditEntities.Candidacy, id, $"Status {previous} → {req.Status}.");
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Candidacy {CandidacyId} status -> {Status}", id, req.Status);
            return Ok(new { message = $"Candidacy {req.Status.ToLower()}.", candidacyId = id });
        }

        // ═══ BRANCHES ═════════════════════════════════════════════════════════

        // ── GET /api/v1/admin/branches ────────────────────────────────────────
        /// <summary>List active branches.</summary>
        [HttpGet("branches")]
        public async Task<IActionResult> GetBranches(CancellationToken ct)
        {
            var branches = await _db.Branches
                .AsNoTracking()
                // FIX: this had no IsActive filter before (the entity had no IsActive
                // property to filter on) — soft-deleted branches were leaking through.
                // Matches the filter BranchesRepository.GetAll already applies client-side.
                .Where(b => b.IsActive)
                .OrderBy(b => b.BranchName)
                .Select(b => new BranchResponse
                {
                    BranchId = b.BranchId,
                    BranchName = b.BranchName,
                    BranchAddress = b.BranchAddress,
                    BranchContactNumber = b.BranchContactNumber,
                    CreatedAt = b.CreatedAt
                })
                .ToListAsync(ct);

            // Branches.CreatedAt is a UTC column: flag it so clients convert to local time.
            branches.ForEach(x => x.CreatedAt = TimeConv.AsUtc(x.CreatedAt));
            return Ok(branches);
        }

        // ── GET /api/v1/admin/branches/{id} ───────────────────────────────────
        /// <summary>Load one branch for the Edit form. HasActiveUsers doubles as
        /// the delete pre-check so BranchEditor/BranchesManagement don't need a
        /// separate endpoint for that.</summary>
        [HttpGet("branches/{id:int}")]
        [RequirePermission(PermissionCodes.BranchView, PermissionCodes.BranchAdd, PermissionCodes.BranchEdit)]
        public async Task<IActionResult> GetBranchById(int id, CancellationToken ct)
        {
            var branch = await _db.Branches.AsNoTracking()
                .FirstOrDefaultAsync(b => b.BranchId == id && b.IsActive, ct);
            if (branch == null)
                return NotFound(new ApiError { Error = "Branch not found." });

            bool hasActiveUsers = await _db.Users.AnyAsync(u => u.IsActive && u.BranchId == id, ct);

            return Ok(new BranchDetailResponse
            {
                BranchId = branch.BranchId,
                BranchName = branch.BranchName,
                BranchAddress = branch.BranchAddress,
                BranchContactNumber = branch.BranchContactNumber,
                CreatedAt = TimeConv.AsUtc(branch.CreatedAt),
                UpdatedAt = TimeConv.AsUtc(branch.UpdatedAt),
                HasActiveUsers = hasActiveUsers
            });
        }

        // ── GET /api/v1/admin/branches/check-name ─────────────────────────────
        /// <summary>Live uniqueness check — powers BranchEditor's inline
        /// "Branch name is already listed!" validation before Save is even attempted.</summary>
        [HttpGet("branches/check-name")]
        [RequirePermission(PermissionCodes.BranchView, PermissionCodes.BranchAdd, PermissionCodes.BranchEdit)]
        public async Task<IActionResult> CheckBranchName(
            [FromQuery] string name, [FromQuery] int? exceptId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Ok(new NameCheckResponse { Taken = false });

            bool taken = await _db.Branches.AnyAsync(b =>
                b.IsActive && b.BranchName == name &&
                (!exceptId.HasValue || b.BranchId != exceptId.Value), ct);

            return Ok(new NameCheckResponse { Taken = taken });
        }

        // ── POST /api/v1/admin/branches ───────────────────────────────────────
        /// <summary>Create a branch (audited, atomic).</summary>
        [HttpPost("branches")]
        [RequirePermission(PermissionCodes.BranchAdd)]
        public async Task<IActionResult> CreateBranch([FromBody] BranchRequest req, CancellationToken ct)
        {
            var invalid = ValidateBranch(req);
            if (invalid != null) return invalid;

            var name = req.BranchName.Trim();
            // Server-side safety net: closes the race window between the live check-name and this request.
            bool duplicate = await _db.Branches.AnyAsync(b => b.IsActive && b.BranchName == name, ct);
            if (duplicate)
                return Conflict(new ApiError { Error = "Branch name is already listed.", Code = "DUPLICATE",
                    Fields = new() { ["branchName"] = "Branch name is already listed." } });

            var now = DateTime.UtcNow;
            var branch = new Infrastructure.Entities.BranchEntity
            {
                BranchName = name,
                BranchAddress = req.BranchAddress.Trim(),
                BranchContactNumber = string.IsNullOrWhiteSpace(req.BranchContactNumber) ? null : req.BranchContactNumber.Trim(),
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            };

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Branches.Add(branch);
            await _db.SaveChangesAsync(ct);
            _audit.Stage(AuditActions.Create, AuditEntities.Branch, branch.BranchId, $"BranchName='{branch.BranchName}'.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("Branch created: BranchId={BranchId} Name={Name}", branch.BranchId, branch.BranchName);
            return Ok(ToBranchDetail(branch, hasActiveUsers: false));
        }

        // ── PUT /api/v1/admin/branches/{id} ───────────────────────────────────
        /// <summary>Update a branch (audited; optional optimistic concurrency).</summary>
        [HttpPut("branches/{id:int}")]
        [RequirePermission(PermissionCodes.BranchEdit)]
        public async Task<IActionResult> UpdateBranch(int id, [FromBody] BranchRequest req, CancellationToken ct)
        {
            var branch = await _db.Branches.FirstOrDefaultAsync(b => b.BranchId == id && b.IsActive, ct);
            if (branch == null)
                return NotFound(new ApiError { Error = "Branch not found.", Code = "NOT_FOUND" });

            if (Concurrency.IsStale(req.ExpectedUpdatedAt, branch.UpdatedAt))
                return Conflict(new ApiError
                {
                    Error = "This branch was changed by someone else after you opened it.",
                    Detail = "Reload the record and re-apply your changes.",
                    Code = "CONCURRENCY"
                });

            var invalid = ValidateBranch(req);
            if (invalid != null) return invalid;

            var name = req.BranchName.Trim();
            bool duplicate = await _db.Branches.AnyAsync(b =>
                b.IsActive && b.BranchName == name && b.BranchId != id, ct);
            if (duplicate)
                return Conflict(new ApiError { Error = "Branch name is already listed.", Code = "DUPLICATE",
                    Fields = new() { ["branchName"] = "Branch name is already listed." } });

            branch.BranchName = name;
            branch.BranchAddress = req.BranchAddress.Trim();
            branch.BranchContactNumber = string.IsNullOrWhiteSpace(req.BranchContactNumber) ? null : req.BranchContactNumber.Trim();
            branch.UpdatedAt = DateTime.UtcNow;
            _audit.Stage(AuditActions.Update, AuditEntities.Branch, id, $"BranchName='{branch.BranchName}'.");
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Branch updated: BranchId={BranchId} Name={Name}", id, branch.BranchName);
            bool hasActiveUsers = await _db.Users.AnyAsync(u => u.IsActive && u.BranchId == id, ct);
            return Ok(ToBranchDetail(branch, hasActiveUsers));
        }

        // ── DELETE /api/v1/admin/branches/{id} ────────────────────────────────
        /// <summary>Soft delete. Refuses while active users are still assigned to the branch (as the legacy repository did).</summary>
        [HttpDelete("branches/{id:int}")]
        [RequirePermission(PermissionCodes.BranchDelete)]
        public async Task<IActionResult> DeleteBranch(int id, CancellationToken ct)
        {
            var branch = await _db.Branches.FirstOrDefaultAsync(b => b.BranchId == id && b.IsActive, ct);
            if (branch == null)
                return NotFound(new ApiError { Error = "Branch not found.", Code = "NOT_FOUND" });

            int activeUserCount = await _db.Users.CountAsync(u => u.IsActive && u.BranchId == id, ct);
            if (activeUserCount > 0)
                return Conflict(new ApiError
                {
                    Error = $"Cannot delete this branch — {activeUserCount} active user(s) are still assigned to it.",
                    Detail = "Please reassign them to another branch first.",
                    Code = "BRANCH_IN_USE"
                });

            var now = DateTime.UtcNow;
            branch.IsActive = false;
            branch.DeletedAt = now;
            branch.UpdatedAt = now;
            _audit.Stage(AuditActions.SoftDelete, AuditEntities.Branch, id, $"BranchName='{branch.BranchName}'.");
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Branch deleted: BranchId={BranchId}", id);
            return Ok(new { message = "Branch deleted.", branchId = id });
        }

        // ── Branch helpers ────────────────────────────────────────────────────
        private IActionResult? ValidateBranch(BranchRequest req)
        {
            var errors = new Dictionary<string, string>();
            if (string.IsNullOrWhiteSpace(req.BranchName)) errors["branchName"] = "Branch name is required.";
            if (string.IsNullOrWhiteSpace(req.BranchAddress)) errors["branchAddress"] = "Branch address is required.";
            var contact = (req.BranchContactNumber ?? "").Trim();
            if (contact.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(contact, "^[0-9]{11}$"))
                errors["branchContactNumber"] = "Contact number must be exactly 11 digits.";
            if (errors.Count == 0) return null;
            return BadRequest(new ApiError { Error = errors.Values.First(), Code = "VALIDATION", Fields = errors });
        }

        // Branches.CreatedAt/UpdatedAt are UTC; flag them so clients convert to local time (and echo UpdatedAt back unchanged).
        private static BranchDetailResponse ToBranchDetail(Infrastructure.Entities.BranchEntity b, bool hasActiveUsers) => new()
        {
            BranchId = b.BranchId,
            BranchName = b.BranchName,
            BranchAddress = b.BranchAddress,
            BranchContactNumber = b.BranchContactNumber,
            CreatedAt = TimeConv.AsUtc(b.CreatedAt),
            UpdatedAt = TimeConv.AsUtc(b.UpdatedAt),
            HasActiveUsers = hasActiveUsers
        };

        // ── GET /api/v1/admin/events/{eventId}/branches ──────────────────────────
        // Returns ONLY the branches enrolled in a specific event.
        // This is used by the Voter Lookup branch dropdown so the filter
        // only shows branches that actually have eligible voters for the event,
        // matching exactly what the voter-lookup and turnout-by-branch endpoints use.
        [HttpGet("events/{eventId:int}/branches")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> GetEventBranches(int eventId, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking()
                .Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.EventId == eventId, ct);
            if (ev == null) return NotFound(new ApiError { Error = $"Event {eventId} not found." });

            var branchIds = ev.EventBranches.Select(eb => eb.BranchId).ToList();
            var branches = await _db.Branches.AsNoTracking()
                .Where(b => branchIds.Contains(b.BranchId))
                .OrderBy(b => b.BranchName)
                .Select(b => new BranchResponse { BranchId = b.BranchId, BranchName = b.BranchName })
                .ToListAsync(ct);

            return Ok(branches);
        }

        // ═══ AUDIT LOG ════════════════════════════════════════════════════════

        // ── GET /api/v1/admin/audit/{eventId} ────────────────────────────────
        /// <summary>Full voter audit log for an event — who voted and when.</summary>
        [HttpGet("audit/{eventId:int}")]
        [RequirePermission(PermissionCodes.AuditView, PermissionCodes.EventView)]
        public async Task<IActionResult> GetAuditLog(int eventId, CancellationToken ct)
        {
            var logs = await _db.VoterLogs
                .AsNoTracking()
                .Where(v => v.EventId == eventId && v.HasVoted)
                .OrderBy(v => v.VotedAt)
                .Select(v => new
                {
                    v.VoterLogId,
                    v.MemberId,
                    v.EventId,
                    v.HasVoted,
                    v.VotedAt,
                    v.ReceiptCode
                })
                .ToListAsync(ct);

            return Ok(new
            {
                eventId,
                totalVoted = logs.Count,
                records = logs
            });
        }

        // ── GET /api/v1/admin/voter-lookup ──────────────────────────────────────
        [HttpGet("voter-lookup")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> VoterLookup(
            [FromQuery] int eventId, [FromQuery] string? search,
            [FromQuery] int? branchId, [FromQuery] bool? hasVoted,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 100,
            CancellationToken ct = default)
        {
            pageSize = Math.Clamp(pageSize, 1, 500);
            var ev = await _db.Events.AsNoTracking()
                .Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.EventId == eventId, ct);
            if (ev == null) return NotFound(new ApiError { Error = $"Event {eventId} not found." });

            var branchIds = ev.EventBranches
                .Select(eb => eb.BranchId).ToList();

            //ano ang katumbas nito sa sql?
            // SELECT * FROM VoterLogs WHERE EventId = @eventId
            var logs = await _db.VoterLogs
                .AsNoTracking()
                .Where(v => v.EventId == eventId)
                .ToListAsync(ct);
            var logByMember = logs.ToDictionary(l => l.MemberId);
            // BUG FIX: "voted" must mean the log row says HasVoted. A voter-log row can exist without a completed vote
            // (HasVoted = false, receipt status "Pending"); the filter used to treat every ROW as a vote, so "voted only" listed
            // members whose own HasVoted column said false and "not voted" hid them.
            var votedIds = logs.Where(l => l.HasVoted).Select(l => l.MemberId).ToHashSet();

            // SELECT * FROM Members WHERE IsActive = 1 AND IsEligibleToVote = 1 AND BranchId IN (@branchIds)
            var memberQ = _db.Members
                .AsNoTracking()
                .Include(m => m.Branch)
                .Where(m => m.IsActive && m.IsEligibleToVote && branchIds
                .Contains(m.BranchId));

            // Filter by branch if specified
            if (branchId.HasValue) memberQ = memberQ.
                    Where(m => m.BranchId == branchId.Value);

            if (!string.IsNullOrWhiteSpace(search)) memberQ = memberQ.Where(m => m.MemberName.Contains(search) || m.AccountCode.Contains(search) || (_db.VoterLogs.Any(v => v.MemberId == m.MemberId && v.ReceiptCode != null && v.ReceiptCode.Contains(search))));

            var members = await memberQ.OrderBy(m => m.MemberName).ThenBy(m => m.MemberId).ToListAsync(ct);   // tie-breaker: stable pages
            var filtered = hasVoted.HasValue ? (hasVoted.Value ? members.Where(m => votedIds.Contains(m.MemberId)).ToList() : members.Where(m => !votedIds.Contains(m.MemberId)).ToList()) : members;

            int total = filtered.Count;
            var paged = filtered.Skip((page - 1) * pageSize).Take(pageSize).Select(m =>
            {
                var log = logByMember.GetValueOrDefault(m.MemberId);
                return new
                {
                    m.MemberId,
                    m.AccountCode,
                    m.MemberName,
                    Branch = m.Branch?.BranchName ?? "",
                    m.BranchId,
                    IsEligible = m.IsEligibleToVote,
                    HasVoted = log?.HasVoted ?? false,
                    // VoterLogs.VotedAt is written with DateTime.UtcNow: flag it so clients show it in local time.
                    VotedAt = TimeConv.AsUtc(log?.VotedAt),
                    ReceiptCode = log?.ReceiptCode ?? ""
                };
            }).ToList();

            return Ok(new { eventId, total, page, pageSize, data = paged });
        }


        // ── GET /api/v1/admin/qr-issued ──────────────────────────────────────────
        [HttpGet("qr-issued")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> QrIssued(
            [FromQuery] string? search, [FromQuery] int? branchId,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 100,
            CancellationToken ct = default)
        {
            pageSize = Math.Clamp(pageSize, 1, 500);

            var memberQ = _db.Members.AsNoTracking().Include(m => m.Branch)
                .Where(m => m.IsActive && m.QrIssuedAt != null);

            if (branchId.HasValue) memberQ = memberQ.Where(m => m.BranchId == branchId.Value);
            if (!string.IsNullOrWhiteSpace(search))
                memberQ = memberQ.Where(m => m.MemberName.Contains(search) || m.AccountCode.Contains(search));

            int total = await memberQ.CountAsync(ct);

            var paged = await memberQ
                .OrderByDescending(m => m.QrIssuedAt)
                .ThenByDescending(m => m.MemberId)     // tie-breaker: many QR codes are issued in the same second
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(m => new
                {
                    m.MemberId,
                    m.AccountCode,
                    m.MemberName,
                    Branch = m.Branch != null ? m.Branch.BranchName : "",
                    m.BranchId,
                    IsEligible = m.IsEligibleToVote,
                    m.QrIssuedAt
                })
                .ToListAsync(ct);

            // Members.QrIssuedAt is stamped in UTC by the Registration station: flag it so clients convert it to local time.
            var data = paged.Select(x => new
            {
                x.MemberId, x.AccountCode, x.MemberName, x.Branch, x.BranchId, x.IsEligible,
                QrIssuedAt = TimeConv.AsUtc(x.QrIssuedAt)
            }).ToList();
            return Ok(new { total, page, pageSize, data });
        }




        // ── GET /api/v1/admin/turnout-by-branch/{eventId} ────────────────────────
        [HttpGet("turnout-by-branch/{eventId:int}")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> TurnoutByBranch(int eventId, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking().Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.EventId == eventId, ct);
            if (ev == null) return NotFound(new ApiError { Error = $"Event {eventId} not found." });

            var branchIds = ev.EventBranches.Select(eb => eb.BranchId).ToList();
            var branches = await _db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.BranchId)).ToListAsync(ct);

            var votedByBranch = await _db.VoterLogs.AsNoTracking()
                .Where(v => v.EventId == eventId && v.HasVoted)
                .Join(_db.Members, v => v.MemberId, m => m.MemberId, (v, m) => new { m.BranchId })
                .GroupBy(x => x.BranchId).Select(g => new { BranchId = g.Key, Voted = g.Count() }).ToListAsync(ct);

            var eligibleByBranch = await _db.Members.AsNoTracking()
                .Where(m => m.IsActive && m.IsEligibleToVote && branchIds.Contains(m.BranchId))
                .GroupBy(m => m.BranchId).Select(g => new { BranchId = g.Key, Eligible = g.Count() }).ToListAsync(ct);

            var votedDict = votedByBranch.ToDictionary(x => x.BranchId, x => x.Voted);
            var eligibleDict = eligibleByBranch.ToDictionary(x => x.BranchId, x => x.Eligible);

            var result = branches.Select(b =>
            {
                int eligible = eligibleDict.GetValueOrDefault(b.BranchId, 0);
                int voted = votedDict.GetValueOrDefault(b.BranchId, 0);
                return new
                {
                    b.BranchId,
                    b.BranchName,
                    Eligible = eligible,
                    Voted = voted,
                    Pending = eligible - voted,
                    TurnoutPercent = eligible > 0 ? Math.Round((double)voted / eligible * 100, 2) : 0.0
                };
            }).OrderBy(b => b.BranchName).ToList();

            return Ok(new
            {
                eventId,
                totalEligible = result.Sum(b => b.Eligible),
                totalVoted = result.Sum(b => b.Voted),
                turnoutPercent = result.Sum(b => b.Eligible) > 0
                    ? Math.Round((double)result.Sum(b => b.Voted) / result.Sum(b => b.Eligible) * 100, 2) : 0.0,
                branches = result
            });
        }




        // ═══ HEALTH / SYSTEM ══════════════════════════════════════════════════

        // ── GET /api/v1/admin/dashboard ───────────────────────────────────────
        /// <summary>Quick summary stats for the admin dashboard.</summary>
        [HttpGet("dashboard")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> Dashboard(CancellationToken ct)
        {
            // Fetch the current event first (needed for the totalVoted query below)
            var currentEvent = await _db.Events
                .AsNoTracking()
                .Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.Status == "Opened" && e.IsActive, ct);


            int totalMembers = await _db.Members.CountAsync(m => m.IsActive, ct);
            int eligibleMembers = await _db.Members.CountAsync(m => m.IsActive && m.IsEligibleToVote, ct);
            int pendingCandidates = await _db.Candidacies
                .CountAsync(c => c.Status == "Pending" && c.IsActive, ct);
            int totalVoted = currentEvent == null
                ? 0
                : await _db.VoterLogs.CountAsync(v => v.EventId == currentEvent.EventId && v.HasVoted, ct);

            return Ok(new
            {
                totalMembers,
                eligibleMembers,
                totalVoted,
                pendingCandidates,
                currentEvent = currentEvent == null ? null : new
                {
                    currentEvent.EventId,
                    currentEvent.Title,
                    currentEvent.Status,
                    currentEvent.ScheduleFrom,
                    currentEvent.ScheduleTo,
                    branchCount = currentEvent.EventBranches.Count
                }
            });
        }
    }
}