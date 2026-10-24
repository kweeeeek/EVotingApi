using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Member registry. Replaces the legacy direct-DB MembersRepository / MemberEditor / MembersManagement logic.
    /// Eligibility is computed HERE only (single source of truth) — clients never decide who may vote.
    /// </summary>
    [Route("api/v1/admin/members")]
    public class MembersAdminController : AdminControllerBase
    {
        private static readonly Regex ContactRx = new(@"^[0-9]{11}$", RegexOptions.Compiled);
        private const int ExportCap = 100_000;
        /// <summary>The legacy grid offered pages of up to 3,000 rows; the member list keeps that (every other list is capped at 200).</summary>
        public const int MaxMemberPageSize = 3000;
        private const int MaxBulkIds = 20_000;

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly EligibilityOptions _elig;

        public MembersAdminController(ApiDbContext db, IAuditService audit, IOptions<EligibilityOptions> elig)
        {
            _db = db;
            _audit = audit;
            _elig = elig.Value;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Query building (semantics ported 1:1 from the legacy BuildFilterQuery / ApplySort)
        // ══════════════════════════════════════════════════════════════════════
        private IQueryable<MemberEntity> BuildQuery(string? search, DateTime? from, DateTime? to)
        {
            var q = _db.Members.AsNoTracking().Where(m => m.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                var lower = s.ToLowerInvariant();
                bool pastDue = lower.Contains("past") || lower.Contains("due");
                bool active = lower.StartsWith("active");
                bool paid = lower.StartsWith("paid");
                bool none = lower.StartsWith("none");
                bool eligible = lower == "yes" || lower.StartsWith("elig");
                bool notEligible = lower == "no" || lower.StartsWith("not");

                q = q.Where(m =>
                    m.AccountCode.StartsWith(s) ||
                    m.MemberName.StartsWith(s) ||
                    m.MemberAddress.Contains(s) ||
                    (m.Branch != null && m.Branch.BranchName.Contains(s)) ||
                    (pastDue && m.LoanStatus == LoanStatus.PastDue) ||
                    (active && m.LoanStatus == LoanStatus.Active) ||
                    (paid && m.LoanStatus == LoanStatus.Paid) ||
                    (none && m.LoanStatus == LoanStatus.None) ||
                    (eligible && m.IsEligibleToVote) ||
                    (notEligible && !m.IsEligibleToVote));
            }

            // Members.CreatedAt is LOCAL server time (legacy "FIX BUG 6"): the filter compares local calendar dates.
            var (start, end) = TimeConv.LocalRange(from, to);
            if (start.HasValue) q = q.Where(m => m.CreatedAt >= start.Value);
            if (end.HasValue) q = q.Where(m => m.CreatedAt <= end.Value);
            return q;
        }

        private static IQueryable<MemberEntity> ApplySort(IQueryable<MemberEntity> q, string? sortBy, bool desc)
        {
            // Every column a user can sort by (loan status, share capital, branch...) has equal values for many members. Without a
            // unique tie-breaker the database may return equal rows in a different order for each page request, so a client that
            // pages through the list sees one member twice and misses another. MemberId makes the order total and repeatable.
            IOrderedQueryable<MemberEntity> ordered = (sortBy ?? "").ToLowerInvariant() switch
            {
                "accountcode" => desc ? q.OrderByDescending(m => m.AccountCode) : q.OrderBy(m => m.AccountCode),
                "membername" => desc ? q.OrderByDescending(m => m.MemberName) : q.OrderBy(m => m.MemberName),
                "memberaddress" => desc ? q.OrderByDescending(m => m.MemberAddress) : q.OrderBy(m => m.MemberAddress),
                "membercontactnumber" => desc ? q.OrderByDescending(m => m.MemberContactNumber) : q.OrderBy(m => m.MemberContactNumber),
                "branchname" => desc
                    ? q.OrderByDescending(m => m.Branch != null ? m.Branch.BranchName : "")
                    : q.OrderBy(m => m.Branch != null ? m.Branch.BranchName : ""),
                "sharecapital" => desc ? q.OrderByDescending(m => m.ShareCapital) : q.OrderBy(m => m.ShareCapital),
                "savingsdeposit" => desc ? q.OrderByDescending(m => m.SavingsDeposit) : q.OrderBy(m => m.SavingsDeposit),
                "loanstatus" => desc ? q.OrderByDescending(m => m.LoanStatus) : q.OrderBy(m => m.LoanStatus),
                "eligibletovote" => desc ? q.OrderByDescending(m => m.IsEligibleToVote) : q.OrderBy(m => m.IsEligibleToVote),
                "datecreated" => desc ? q.OrderByDescending(m => m.CreatedAt) : q.OrderBy(m => m.CreatedAt),
                // default (and stable tiebreak): newest first, exactly like the legacy grid
                _ => desc || string.IsNullOrEmpty(sortBy) ? q.OrderByDescending(m => m.MemberId) : q.OrderBy(m => m.MemberId)
            };
            return desc ? ordered.ThenByDescending(m => m.MemberId) : ordered.ThenBy(m => m.MemberId);
        }

        private sealed class Raw
        {
            public int MemberId { get; set; }
            public string AccountCode { get; set; } = "";
            public string MemberName { get; set; } = "";
            public string MemberAddress { get; set; } = "";
            public string? MemberContactNumber { get; set; }
            public int BranchId { get; set; }
            public string BranchName { get; set; } = "";
            public decimal ShareCapital { get; set; }
            public decimal SavingsDeposit { get; set; }
            public LoanStatus LoanStatus { get; set; }
            public bool IsEligibleToVote { get; set; }
            public string? VotingRemarks { get; set; }
            public DateTime? QrIssuedAt { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }

        private static IQueryable<Raw> Project(IQueryable<MemberEntity> q) =>
            q.Select(m => new Raw
            {
                MemberId = m.MemberId,
                AccountCode = m.AccountCode,
                MemberName = m.MemberName,
                MemberAddress = m.MemberAddress,
                MemberContactNumber = m.MemberContactNumber,
                BranchId = m.BranchId,
                BranchName = m.Branch != null ? m.Branch.BranchName : "",
                ShareCapital = m.ShareCapital,
                SavingsDeposit = m.SavingsDeposit,
                LoanStatus = m.LoanStatus,
                IsEligibleToVote = m.IsEligibleToVote,
                VotingRemarks = m.VotingRemarks,
                QrIssuedAt = m.QrIssuedAt,
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt
            });

        private static MemberRow ToRow(Raw r) => new()
        {
            MemberId = r.MemberId,
            AccountCode = r.AccountCode,
            MemberName = r.MemberName,
            MemberAddress = r.MemberAddress,
            MemberContactNumber = r.MemberContactNumber,
            BranchId = r.BranchId,
            BranchName = r.BranchName,
            ShareCapital = r.ShareCapital,
            SavingsDeposit = r.SavingsDeposit,
            LoanStatus = r.LoanStatus.ToString(),
            IsEligibleToVote = r.IsEligibleToVote,
            VotingRemarks = r.VotingRemarks,
            QrIssuedAt = TimeConv.AsUtc(r.QrIssuedAt),      // stamped by the Registration station in UTC
            CreatedAt = r.CreatedAt,
            UpdatedAt = r.UpdatedAt
        };

        // ══════════════════════════════════════════════════════════════════════
        //  Reads
        // ══════════════════════════════════════════════════════════════════════
        [HttpGet("page")]
        [RequirePermission(PermissionCodes.MemberView)]
        public async Task<IActionResult> Page(
            [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] string? sortBy, [FromQuery] bool desc = true,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        {
            (page, pageSize) = ClampPaging(page, pageSize, MaxMemberPageSize);
            var q = BuildQuery(search, from, to);
            var total = await q.CountAsync(ct);
            var raw = await Project(ApplySort(q, sortBy, desc).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Ok(Paged(raw.Select(ToRow).ToList(), total, page, pageSize));
        }

        [HttpGet("{id:int}")]
        [RequirePermission(PermissionCodes.MemberView, PermissionCodes.MemberEdit)]
        public async Task<IActionResult> Get(int id, CancellationToken ct)
        {
            var raw = await Project(_db.Members.AsNoTracking().Where(m => m.MemberId == id && m.IsActive))
                .FirstOrDefaultAsync(ct);
            return raw == null ? NotFoundError("Member") : Ok(ToRow(raw));
        }

        [HttpGet("check-code")]
        [RequirePermission(PermissionCodes.MemberView, PermissionCodes.MemberAdd, PermissionCodes.MemberEdit)]
        public async Task<IActionResult> CheckCode([FromQuery] string code, [FromQuery] int? exceptId, CancellationToken ct)
        {
            var c = (code ?? "").Trim();
            // The unique index IX_Members_AccountCode covers soft-deleted rows, so check ALL members.
            var hit = await _db.Members.AsNoTracking()
                .Where(m => m.AccountCode == c && (exceptId == null || m.MemberId != exceptId))
                .Select(m => new { m.IsActive })
                .FirstOrDefaultAsync(ct);
            return Ok(new AvailabilityResponse { Taken = hit != null, Deactivated = hit != null && !hit.IsActive });
        }

        [HttpGet("lookup")]
        [RequirePermission(PermissionCodes.MemberView, PermissionCodes.CandidateAdd, PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> Lookup([FromQuery] string term, [FromQuery] int take = 20, [FromQuery] bool eligibleOnly = false, CancellationToken ct = default)
        {
            var t = (term ?? "").Trim();
            if (t.Length < 2) return Ok(new List<MemberLookupItem>());
            take = Math.Clamp(take, 1, 50);
            // eligibleOnly: the candidacy form offers registered members who may vote (the legacy form listed exactly those; anybody else is a walk-in).
            // A member whose name equals the term comes first, so typing a full name always finds that member even when 50 other names contain it.
            var rows = await _db.Members.AsNoTracking()
                .Where(m => m.IsActive && (!eligibleOnly || m.IsEligibleToVote) && (m.MemberName.Contains(t) || m.AccountCode.StartsWith(t)))
                .OrderBy(m => m.MemberName == t || m.AccountCode == t ? 0 : 1)
                .ThenBy(m => m.MemberName)
                .ThenBy(m => m.MemberId)
                .Take(take)
                .Select(m => new MemberLookupItem
                {
                    MemberId = m.MemberId,
                    AccountCode = m.AccountCode,
                    MemberName = m.MemberName,
                    ContactNumber = m.MemberContactNumber
                })
                .ToListAsync(ct);
            return Ok(rows);
        }

        [HttpPost("eligibility-preview")]
        [RequirePermission(PermissionCodes.MemberView, PermissionCodes.MemberAdd, PermissionCodes.MemberEdit)]
        public IActionResult EligibilityPreview([FromBody] EligibilityPreviewRequest req)
        {
            if (!Enum.TryParse<LoanStatus>(req.LoanStatus, true, out var loan) || !Enum.IsDefined(loan))
                return Invalid("loanStatus", "Loan status must be None, Active, PastDue or Paid.");
            var (ok, remarks) = MemberEligibilityPolicy.Evaluate(req.ShareCapital, req.SavingsDeposit, loan, _elig);
            return Ok(new EligibilityPreviewResponse
            {
                IsEligible = ok,
                Remarks = remarks,
                MinShareCapital = _elig.MinShareCapital,
                MinSavingsDeposit = _elig.MinSavingsDeposit
            });
        }

        [HttpGet("export")]
        [RequirePermission(PermissionCodes.ReportExport, PermissionCodes.MemberView)]
        public async Task<IActionResult> Export(
            [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] bool eligibleOnly = false, CancellationToken ct = default)
        {
            var q = BuildQuery(search, from, to);
            if (eligibleOnly) q = q.Where(m => m.IsEligibleToVote);

            // One request for the whole export (no per-page round trips). Capped to protect the server.
            var raw = await Project(q.OrderByDescending(m => m.MemberId).Take(ExportCap + 1)).ToListAsync(ct);
            if (raw.Count > ExportCap)
            {
                Response.Headers["X-Export-Truncated"] = "true";
                raw.RemoveAt(raw.Count - 1);
            }
            return Ok(raw.Select(ToRow).ToList());
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Create / Update
        // ══════════════════════════════════════════════════════════════════════
        [HttpPost]
        [RequirePermission(PermissionCodes.MemberAdd)]
        public async Task<IActionResult> Create([FromBody] MemberUpsertRequest req, CancellationToken ct)
        {
            var fail = await ValidateAsync(req, exceptId: null, ct);
            if (fail != null) return fail;
            var loan = Enum.Parse<LoanStatus>(req.LoanStatus, true);
            var (ok, remarks) = MemberEligibilityPolicy.Evaluate(req.ShareCapital, req.SavingsDeposit, loan, _elig);

            var now = DateTime.Now;
            var m = new MemberEntity
            {
                AccountCode = req.AccountCode.Trim(),
                MemberName = req.MemberName.Trim(),
                MemberAddress = req.MemberAddress.Trim(),
                MemberContactNumber = string.IsNullOrWhiteSpace(req.MemberContactNumber) ? null : req.MemberContactNumber.Trim(),
                BranchId = req.BranchId,
                ShareCapital = req.ShareCapital,
                SavingsDeposit = req.SavingsDeposit,
                LoanStatus = loan,
                IsEligibleToVote = ok,
                VotingRemarks = remarks,
                CreatedAt = now,
                UpdatedAt = now,
                IsActive = true
            };

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Members.Add(m);
            await _db.SaveChangesAsync(ct);
            _audit.Stage(AuditActions.Create, AuditEntities.Member, m.MemberId,
                $"AccountCode='{m.AccountCode}', Name='{m.MemberName}', BranchId={m.BranchId}.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var raw = await Project(_db.Members.AsNoTracking().Where(x => x.MemberId == m.MemberId)).FirstAsync(ct);
            return StatusCode(StatusCodes.Status201Created, ToRow(raw));
        }

        [HttpPut("{id:int}")]
        [RequirePermission(PermissionCodes.MemberEdit)]
        public async Task<IActionResult> Update(int id, [FromBody] MemberUpsertRequest req, CancellationToken ct)
        {
            var m = await _db.Members.FirstOrDefaultAsync(x => x.MemberId == id && x.IsActive, ct);
            if (m == null) return NotFoundError("Member");
            if (IsStale(req.ExpectedUpdatedAt, m.UpdatedAt)) return ConcurrencyError("member");

            var fail = await ValidateAsync(req, exceptId: id, ct);
            if (fail != null) return fail;
            var loan = Enum.Parse<LoanStatus>(req.LoanStatus, true);
            var (ok, remarks) = MemberEligibilityPolicy.Evaluate(req.ShareCapital, req.SavingsDeposit, loan, _elig);

            // ONLY the editor-owned columns are written. (The legacy repository called Members.Update(newEntity),
            // which rewrote every column and therefore reset QrIssuedAt to NULL on each edit.)
            m.AccountCode = req.AccountCode.Trim();
            m.MemberName = req.MemberName.Trim();
            m.MemberAddress = req.MemberAddress.Trim();
            m.MemberContactNumber = string.IsNullOrWhiteSpace(req.MemberContactNumber) ? null : req.MemberContactNumber.Trim();
            m.BranchId = req.BranchId;
            m.ShareCapital = req.ShareCapital;
            m.SavingsDeposit = req.SavingsDeposit;
            m.LoanStatus = loan;
            m.IsEligibleToVote = ok;
            m.VotingRemarks = remarks;
            m.UpdatedAt = DateTime.Now;

            _audit.Stage(AuditActions.Update, AuditEntities.Member, m.MemberId,
                $"AccountCode='{m.AccountCode}', Name='{m.MemberName}', BranchId={m.BranchId}.");
            await _db.SaveChangesAsync(ct);

            var raw = await Project(_db.Members.AsNoTracking().Where(x => x.MemberId == id)).FirstAsync(ct);
            return Ok(ToRow(raw));
        }

        private async Task<IActionResult?> ValidateAsync(MemberUpsertRequest r, int? exceptId, CancellationToken ct)
        {
            var errors = new Dictionary<string, string>();
            var code = (r.AccountCode ?? "").Trim();

            if (code.Length == 0) errors["accountCode"] = "Account code is required.";
            if (string.IsNullOrWhiteSpace(r.MemberName)) errors["memberName"] = "Member name is required.";
            if (string.IsNullOrWhiteSpace(r.MemberAddress)) errors["memberAddress"] = "Address is required.";

            var contact = (r.MemberContactNumber ?? "").Trim();
            if (contact.Length > 0 && !ContactRx.IsMatch(contact))
                errors["memberContactNumber"] = "Invalid number — 11 digits required.";

            if (!Enum.TryParse<LoanStatus>(r.LoanStatus, true, out var loan) || !Enum.IsDefined(loan))
                errors["loanStatus"] = "Loan status must be None, Active, PastDue or Paid.";

            // [Range] bounds the amounts; a third decimal place would be rounded away by the database without a word.
            if (!MoneyRules.IsValid(r.ShareCapital)) errors["shareCapital"] = MoneyRules.Message("Share capital");
            if (!MoneyRules.IsValid(r.SavingsDeposit)) errors["savingsDeposit"] = MoneyRules.Message("Savings deposit");

            if (errors.Count > 0) return Invalid(errors);

            if (!await _db.Branches.AnyAsync(b => b.BranchId == r.BranchId && b.IsActive, ct))
                return Invalid("branchId", "The selected branch does not exist or was deleted.");

            var hit = await _db.Members.AsNoTracking()
                .Where(m => m.AccountCode == code && (exceptId == null || m.MemberId != exceptId))
                .Select(m => new { m.IsActive })
                .FirstOrDefaultAsync(ct);
            if (hit != null)
                return Duplicate(hit.IsActive
                    ? "Account code is already taken."
                    : "Account code belongs to a previously deleted member. Use Import to restore that member, or choose another code.",
                    "accountCode");
            return null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Delete (single + bulk) — destructive: voting history needs explicit confirmation
        // ══════════════════════════════════════════════════════════════════════
        private sealed class History
        {
            public HashSet<int> MembersWithHistory { get; } = new();
            public HashSet<int> EventIds { get; } = new();
            public List<int> CandidacyIds { get; } = new();
            public int VoterLogRows { get; set; }
            public int VoteRowsAsVoter { get; set; }
            public int VoteRowsForCandidacies { get; set; }
            public int TotalRows => VoterLogRows + VoteRowsAsVoter + VoteRowsForCandidacies;
        }

        private async Task<History> HistoryAsync(List<int> memberIds, CancellationToken ct)
        {
            var h = new History();
            foreach (var chunk in memberIds.Chunk(1000))
            {
                var c = chunk.ToList();

                var logs = await _db.VoterLogs.AsNoTracking()
                    .Where(v => c.Contains(v.MemberId))
                    .Select(v => new { v.MemberId, v.EventId }).ToListAsync(ct);
                foreach (var l in logs) { h.VoterLogRows++; h.MembersWithHistory.Add(l.MemberId); h.EventIds.Add(l.EventId); }

                var votes = await _db.Votes.AsNoTracking()
                    .Where(v => c.Contains(v.MemberId))
                    .GroupBy(v => new { v.MemberId, v.EventId })
                    .Select(g => new { g.Key.MemberId, g.Key.EventId, Count = g.Count() }).ToListAsync(ct);
                foreach (var v in votes) { h.VoteRowsAsVoter += v.Count; h.MembersWithHistory.Add(v.MemberId); h.EventIds.Add(v.EventId); }

                var cands = await _db.Candidacies.AsNoTracking()
                    .Where(x => x.MemberId != null && c.Contains(x.MemberId.Value))
                    .Select(x => new { x.CandidacyId, MemberId = x.MemberId!.Value }).ToListAsync(ct);
                if (cands.Count > 0)
                {
                    var cids = cands.Select(x => x.CandidacyId).ToList();
                    h.CandidacyIds.AddRange(cids);
                    var received = await _db.Votes.AsNoTracking()
                        .Where(v => cids.Contains(v.CandidacyId))
                        .GroupBy(v => new { v.CandidacyId, v.EventId })
                        .Select(g => new { g.Key.CandidacyId, g.Key.EventId, Count = g.Count() }).ToListAsync(ct);
                    foreach (var rv in received)
                    {
                        h.VoteRowsForCandidacies += rv.Count;
                        h.EventIds.Add(rv.EventId);
                        h.MembersWithHistory.Add(cands.First(x => x.CandidacyId == rv.CandidacyId).MemberId);
                    }
                }
            }
            return h;
        }

        private async Task<bool> AnyOpenEventAsync(HashSet<int> eventIds, CancellationToken ct) =>
            eventIds.Count > 0 &&
            await _db.Events.AsNoTracking().AnyAsync(e => e.IsActive && e.Status == "Opened" && eventIds.Contains(e.EventId), ct);

        /// <summary>Removes members + dependent rows. Caller supplies an open transaction.</summary>
        private async Task<(int Votes, int Logs, int Candidacies)> PurgeAsync(List<int> memberIds, CancellationToken ct)
        {
            int votes = 0, logs = 0, cands = 0;
            foreach (var chunk in memberIds.Chunk(500))
            {
                var c = chunk.ToList();
                var cids = await _db.Candidacies.Where(x => x.MemberId != null && c.Contains(x.MemberId.Value))
                    .Select(x => x.CandidacyId).ToListAsync(ct);

                votes += await _db.Votes.Where(v => c.Contains(v.MemberId) || cids.Contains(v.CandidacyId)).ExecuteDeleteAsync(ct);
                logs += await _db.VoterLogs.Where(v => c.Contains(v.MemberId)).ExecuteDeleteAsync(ct);
                cands += await _db.Candidacies.Where(x => x.MemberId != null && c.Contains(x.MemberId.Value)).ExecuteDeleteAsync(ct);
                await _db.Members.Where(m => c.Contains(m.MemberId)).ExecuteDeleteAsync(ct);
            }
            return (votes, logs, cands);
        }

        [HttpDelete("{id:int}")]
        [RequirePermission(PermissionCodes.MemberDelete)]
        public async Task<IActionResult> Delete(int id, [FromQuery] bool purgeVotingRecords = false, CancellationToken ct = default)
        {
            var m = await _db.Members.AsNoTracking().Where(x => x.MemberId == id && x.IsActive)
                .Select(x => new { x.MemberId, x.AccountCode, x.MemberName }).FirstOrDefaultAsync(ct);
            if (m == null) return NotFoundError("Member");

            var h = await HistoryAsync(new List<int> { id }, ct);
            if (h.TotalRows > 0)
            {
                if (await AnyOpenEventAsync(h.EventIds, ct))
                    return Fail(StatusCodes.Status409Conflict,
                        "This member has voting records in an election that is currently OPEN. Close the election before deleting.",
                        "ELECTION_OPEN");
                if (!purgeVotingRecords)
                    return Fail(StatusCodes.Status409Conflict,
                        $"This member has voting history ({h.VoterLogRows} voter-log, {h.VoteRowsAsVoter} vote and " +
                        $"{h.VoteRowsForCandidacies} received-vote record(s)). Deleting permanently removes those records and changes election results.",
                        "HAS_VOTING_HISTORY", "Confirm to delete the member together with this voting history.");
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var (votes, logs, cands) = await PurgeAsync(new List<int> { id }, ct);
            _audit.Stage(AuditActions.Delete, AuditEntities.Member, id,
                $"AccountCode='{m.AccountCode}', Name='{m.MemberName}'. Removed {votes} vote, {logs} voter-log and {cands} candidacy row(s).");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(new MessageResponse { Message = $"Member '{m.MemberName}' deleted." });
        }

        [HttpPost("bulk-delete")]
        [RequirePermission(PermissionCodes.MemberDelete)]
        public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteMembersRequest req, CancellationToken ct)
        {
            if ((req.Ids == null) == (req.Filter == null))
                return Invalid("ids", "Provide either a list of ids or a filter — not both and not neither.");

            List<int> ids;
            if (req.Ids != null)
            {
                ids = req.Ids.Distinct().ToList();
                if (ids.Count == 0) return Invalid("ids", "No members selected.");
            }
            else
            {
                var q = BuildQuery(req.Filter!.Search, req.Filter.From, req.Filter.To);
                ids = await q.Select(m => m.MemberId).ToListAsync(ct);
                // The user confirmed a specific number. If the live match differs, delete NOTHING.
                if (ids.Count != req.ExpectedCount)
                    return Fail(StatusCodes.Status409Conflict,
                        $"The number of matching members changed (confirmed {req.ExpectedCount}, now {ids.Count}). Nothing was deleted.",
                        "COUNT_MISMATCH", "Refresh the list and confirm again.");
            }
            if (ids.Count > MaxBulkIds)
                return Invalid("ids", $"At most {MaxBulkIds:N0} members can be deleted in one request.");

            var existing = new List<(int MemberId, string AccountCode)>();
            foreach (var chunk in ids.Chunk(1000))
            {
                var c = chunk.ToList();
                var rows = await _db.Members.AsNoTracking()
                    .Where(m => m.IsActive && c.Contains(m.MemberId))
                    .Select(m => new { m.MemberId, m.AccountCode }).ToListAsync(ct);
                existing.AddRange(rows.Select(r => (r.MemberId, r.AccountCode)));
            }
            var existingIds = existing.Select(e => e.MemberId).ToList();
            var response = new BulkDeleteResponse { NotFound = ids.Count - existingIds.Count };

            var h = await HistoryAsync(existingIds, ct);
            List<int> deletable = existingIds;
            if (h.MembersWithHistory.Count > 0)
            {
                if (await AnyOpenEventAsync(h.EventIds, ct))
                    return Fail(StatusCodes.Status409Conflict,
                        "Some selected members have voting records in an election that is currently OPEN. Close the election first.",
                        "ELECTION_OPEN");
                if (!req.PurgeVotingRecords)
                {
                    deletable = existingIds.Where(i => !h.MembersWithHistory.Contains(i)).ToList();
                    response.SkippedHasVotingHistory = h.MembersWithHistory.Count;
                    response.Messages.Add(
                        $"{h.MembersWithHistory.Count} member(s) were skipped because they have voting history. " +
                        "Delete them separately and confirm the removal of their voting records.");
                }
            }

            if (deletable.Count > 0)
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                var (votes, logs, cands) = await PurgeAsync(deletable, ct);

                var deletableSet = deletable.ToHashSet();
                var codes = existing.Where(e => deletableSet.Contains(e.MemberId)).ToList();
                if (codes.Count <= 100)
                    foreach (var c in codes)
                        _audit.Stage(AuditActions.Delete, AuditEntities.Member, c.MemberId, $"AccountCode='{c.AccountCode}' (bulk delete).");
                _audit.Stage(AuditActions.BulkDelete, AuditEntities.Member, null,
                    $"Deleted {deletable.Count} member(s); removed {votes} vote, {logs} voter-log, {cands} candidacy row(s)." +
                    (req.Filter != null ? $" Filter: search='{req.Filter.Search}'." : ""));
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            response.Deleted = deletable.Count;
            return Ok(response);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Import (preview + commit share ONE analysis so they can never disagree)
        // ══════════════════════════════════════════════════════════════════════
        private sealed class Planned
        {
            public MemberImportRow Row { get; init; } = new();
            public ImportRowResult Result { get; init; } = new();
            public MemberEntity? Entity { get; set; }
            public int? RestoreMemberId { get; set; }
        }

        private async Task<List<Planned>> AnalyzeAsync(MemberImportRequest req, CancellationToken ct)
        {
            var branches = (await _db.Branches.AsNoTracking().Where(b => b.IsActive)
                    .Select(b => new { b.BranchId, b.BranchName }).ToListAsync(ct))
                .GroupBy(b => b.BranchName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().BranchId, StringComparer.OrdinalIgnoreCase);

            var existing = (await _db.Members.AsNoTracking()
                    .Select(m => new { m.MemberId, m.AccountCode, m.IsActive }).ToListAsync(ct))
                .GroupBy(m => m.AccountCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plan = new List<Planned>();
            var now = DateTime.Now;

            foreach (var row in req.Rows)
            {
                var code = (row.AccountCode ?? "").Trim();
                var name = (row.MemberName ?? "").Trim();
                var p = new Planned
                {
                    Row = row,
                    Result = new ImportRowResult { RowNumber = row.RowNumber, Key = code, Name = name }
                };
                plan.Add(p);

                string? problem = null;
                decimal share = 0m, savings = 0m;
                int branchId = 0;
                var loan = LoanStatus.None;

                if (code.Length == 0) problem = "Account code is required.";
                else if (code.Length > FieldLimits.AccountCode) problem = $"Account code exceeds {FieldLimits.AccountCode} characters.";
                else if (name.Length == 0) problem = "Member name is required.";
                else if (name.Length > FieldLimits.MemberName) problem = $"Member name exceeds {FieldLimits.MemberName} characters.";
                else if ((row.MemberAddress ?? "").Trim().Length > FieldLimits.MemberAddress) problem = $"Address exceeds {FieldLimits.MemberAddress} characters.";
                else if ((row.ContactNumber ?? "").Trim().Length > FieldLimits.MemberContact) problem = $"Contact number exceeds {FieldLimits.MemberContact} characters.";
                else if (!ImportParsing.TryParseMoney(row.ShareCapital, out share)) problem = $"Invalid share capital '{row.ShareCapital}'.";
                else if (!ImportParsing.TryParseMoney(row.SavingsDeposit, out savings)) problem = $"Invalid savings deposit '{row.SavingsDeposit}'.";
                else if (!ImportParsing.TryParseLoanStatus(row.LoanStatus, out loan)) problem = $"Unknown loan status '{row.LoanStatus?.Trim()}'. Use None, Active, Past Due or Paid.";
                else
                {
                    // The legacy importer silently put unmatched/blank branches into Branch 1. That mis-assigns members
                    // (and therefore voting rights per event branch), so unknown branches are now rejected explicitly.
                    var bn = (row.BranchName ?? "").Trim();
                    if (bn.Length == 0 || !branches.TryGetValue(bn, out branchId))
                        problem = $"Branch '{bn}' was not found.";
                }

                if (problem != null)
                {
                    p.Result.Status = "Invalid"; p.Result.Message = problem;
                    continue;
                }

                if (!seen.Add(code))
                {
                    p.Result.Status = "Duplicate"; p.Result.Message = "Account code appears more than once in the file.";
                    continue;
                }

                var (ok, remarks) = MemberEligibilityPolicy.Evaluate(share, savings, loan, _elig);
                p.Result.IsEligible = ok;
                p.Result.VotingRemarks = remarks;

                if (existing.TryGetValue(code, out var ex))
                {
                    if (ex.IsActive)
                    {
                        p.Result.Status = "Duplicate"; p.Result.Message = "A member with this account code already exists.";
                        continue;
                    }
                    p.Result.Status = "Restore";
                    p.Result.Message = "Matches a previously deleted member — it will be restored with the imported values.";
                    p.RestoreMemberId = ex.MemberId;
                }
                else
                {
                    p.Result.Status = "New";
                }

                p.Entity = new MemberEntity
                {
                    AccountCode = code,
                    MemberName = name,
                    MemberAddress = (row.MemberAddress ?? "").Trim(),
                    MemberContactNumber = string.IsNullOrWhiteSpace(row.ContactNumber) ? null : row.ContactNumber.Trim(),
                    BranchId = branchId,
                    ShareCapital = share,
                    SavingsDeposit = savings,
                    LoanStatus = loan,
                    IsEligibleToVote = ok,
                    VotingRemarks = remarks,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsActive = true
                };
            }
            return plan;
        }

        private static ImportResponse Summarize(List<Planned> plan, bool committed) => new()
        {
            Committed = committed,
            Imported = plan.Count(p => p.Result.Status == "New"),
            Restored = plan.Count(p => p.Result.Status == "Restore"),
            Duplicates = plan.Count(p => p.Result.Status == "Duplicate"),
            Invalid = plan.Count(p => p.Result.Status == "Invalid"),
            Rows = plan.Select(p => p.Result).ToList()
        };

        [HttpPost("import/preview")]
        [RequirePermission(PermissionCodes.MemberAdd)]
        public async Task<IActionResult> ImportPreview([FromBody] MemberImportRequest req, CancellationToken ct) =>
            Ok(Summarize(await AnalyzeAsync(req, ct), committed: false));

        [HttpPost("import")]
        [RequirePermission(PermissionCodes.MemberAdd)]
        public async Task<IActionResult> Import([FromBody] MemberImportRequest req, CancellationToken ct)
        {
            var plan = await AnalyzeAsync(req, ct);
            var inserts = plan.Where(p => p.Result.Status == "New" && p.Entity != null).Select(p => p.Entity!).ToList();
            var restores = plan.Where(p => p.Result.Status == "Restore" && p.Entity != null && p.RestoreMemberId != null).ToList();

            if (inserts.Count == 0 && restores.Count == 0)
                return Ok(Summarize(plan, committed: false));     // nothing valid to write

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            foreach (var chunk in inserts.Chunk(500))
            {
                _db.Members.AddRange(chunk);
                await _db.SaveChangesAsync(ct);
            }

            if (restores.Count > 0)
            {
                var ids = restores.Select(r => r.RestoreMemberId!.Value).ToList();
                var tracked = await _db.Members.Where(m => ids.Contains(m.MemberId)).ToDictionaryAsync(m => m.MemberId, ct);
                foreach (var r in restores)
                {
                    var t = tracked[r.RestoreMemberId!.Value];
                    var e = r.Entity!;
                    t.MemberName = e.MemberName; t.MemberAddress = e.MemberAddress; t.MemberContactNumber = e.MemberContactNumber;
                    t.BranchId = e.BranchId; t.ShareCapital = e.ShareCapital; t.SavingsDeposit = e.SavingsDeposit;
                    t.LoanStatus = e.LoanStatus; t.IsEligibleToVote = e.IsEligibleToVote; t.VotingRemarks = e.VotingRemarks;
                    t.IsActive = true; t.DeletedAt = null; t.UpdatedAt = e.UpdatedAt;
                    _audit.Stage(AuditActions.Restore, AuditEntities.Member, t.MemberId, $"AccountCode='{t.AccountCode}' restored by import.");
                }
            }

            var summary = Summarize(plan, committed: true);
            _audit.Stage(AuditActions.Import, AuditEntities.Member, null,
                $"Imported {summary.Imported} new and restored {summary.Restored} member(s) from '{req.SourceFileName ?? "file"}'. " +
                $"Skipped: {summary.Duplicates} duplicate, {summary.Invalid} invalid.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Ok(summary);
        }
    }
}
