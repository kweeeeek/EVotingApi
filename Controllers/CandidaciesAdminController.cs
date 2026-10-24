using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Candidacy (candidate filing) management. Replaces the legacy direct-DB CandidacyRepository / CandidacyEditor logic.
    /// Rules enforced HERE, independently of any client:
    ///   • while an election is OPEN, candidacies cannot be added / edited / deleted / approved (legacy: UI-only lock);
    ///   • one active filing per member per position (Withdrawn / Rejected filings do not count);
    ///   • candidates must be at least 18 years old.
    /// </summary>
    [Route("api/v1/admin/candidacies")]
    public class CandidaciesAdminController : AdminControllerBase
    {
        private static readonly Regex CellRx = new(@"^[0-9]{11}$", RegexOptions.Compiled);

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;

        public CandidaciesAdminController(ApiDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        // ── Election lock ────────────────────────────────────────────────────
        private async Task<(int EventId, string Title)?> OpenEventAsync(CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking()
                .Where(e => e.IsActive && e.Status == "Opened")
                .Select(e => new { e.EventId, e.Title })
                .FirstOrDefaultAsync(ct);
            return ev == null ? null : (ev.EventId, ev.Title);
        }

        private async Task<IActionResult?> LockedAsync(CancellationToken ct)
        {
            var open = await OpenEventAsync(ct);
            return open == null ? null : Fail(StatusCodes.Status409Conflict,
                $"Candidate records are locked while the election \"{open.Value.Title}\" is open.",
                "EVENT_OPEN_LOCK", "Close the election to add, edit, delete or approve candidacies.");
        }

        // ── Projections ──────────────────────────────────────────────────────
        private static IQueryable<CandidacyDetail> ProjectDetail(IQueryable<CandidacyEntity> q) =>
            q.Select(c => new CandidacyDetail
            {
                CandidacyId = c.CandidacyId,
                MemberId = c.MemberId,
                CandidateName = c.Member != null ? c.Member.MemberName : (c.WalkInCandidateName ?? "(Walk-in)"),
                MemberAccountCode = c.Member != null ? c.Member.AccountCode : "—",
                BranchName = c.Member != null && c.Member.Branch != null ? c.Member.Branch.BranchName : "N/A",
                Position = c.Position,
                Status = c.Status,
                CellNo = c.CellNo ?? "N/A",
                DateFiled = c.DateFiled,
                HasPhoto = c.CandidateImage != null && c.CandidateImage.Length > 0,
                UpdatedAt = c.UpdatedAt,
                WalkInCandidateName = c.WalkInCandidateName,
                Reason = c.Reason,
                TelNo = c.TelNo ?? "",
                CivilStatus = c.CivilStatus ?? "",
                Gender = c.Gender ?? "",
                DateOfBirth = c.DateOfBirth,
                HighestEducation = c.HighestEducation ?? "",
                PreviousPositions = c.PreviousPositions ?? "",
                SpecialSkills = c.SpecialSkills ?? "",
                RejectionReason = c.RejectionReason ?? "",
                DateApproved = c.DateApproved,
                DateRejected = c.DateRejected
            });

        // Candidacies are written in UTC (legacy used DateTime.UtcNow) → mark them so clients convert to local time.
        private static T Finish<T>(T r) where T : CandidacyRow
        {
            r.CandidacyCode = $"CND-{r.CandidacyId:D4}";
            r.DateFiled = TimeConv.AsUtc(r.DateFiled);
            r.UpdatedAt = TimeConv.AsUtc(r.UpdatedAt);
            if (r is CandidacyDetail d)
            {
                d.DateApproved = TimeConv.AsUtc(d.DateApproved);
                d.DateRejected = TimeConv.AsUtc(d.DateRejected);
                // DateOfBirth is a calendar date, not an instant — leave it unspecified.
            }
            return r;
        }

        private IQueryable<CandidacyEntity> BuildQuery(
            string? search, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? position, string? status)
        {
            var q = _db.Candidacies.AsNoTracking().Where(c => c.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                int codeId = 0;
                bool isCode = s.StartsWith("CND-", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[4..], out codeId);
                q = q.Where(c =>
                    (c.Member != null && (c.Member.MemberName.Contains(s) || c.Member.AccountCode.Contains(s))) ||
                    (c.WalkInCandidateName != null && c.WalkInCandidateName.Contains(s)) ||
                    c.Position.Contains(s) ||
                    c.Status.Contains(s) ||
                    (isCode && c.CandidacyId == codeId));
            }

            // Filtering by position/status happens HERE, before paging. (The legacy grid filtered AFTER the page was
            // fetched, so page counts and totals were wrong and matching rows on other pages were invisible.)
            if (!string.IsNullOrWhiteSpace(position)) q = q.Where(c => c.Position == position);
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(c => c.Status == status);

            // DateFiled is stored in UTC; the client converts its local day boundaries to instants.
            if (fromUtc.HasValue) { var f = fromUtc.Value.UtcDateTime; q = q.Where(c => c.DateFiled >= f); }
            if (toUtc.HasValue) { var t = toUtc.Value.UtcDateTime; q = q.Where(c => c.DateFiled <= t); }
            return q;
        }

        // ── Reads ────────────────────────────────────────────────────────────
        [HttpGet]
        [RequirePermission(PermissionCodes.CandidateView)]
        public async Task<IActionResult> List(
            [FromQuery] string? search, [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] string? position, [FromQuery] string? status,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        {
            (page, pageSize) = ClampPaging(page, pageSize);
            var q = BuildQuery(search, fromUtc, toUtc, position, status);
            var total = await q.CountAsync(ct);
            var rows = await ProjectDetail(q.OrderByDescending(c => c.CandidacyId).Skip((page - 1) * pageSize).Take(pageSize))
                .ToListAsync(ct);
            // Items are typed CandidacyRow, so System.Text.Json serialises only the light list shape (no long text fields).
            return Ok(Paged(rows.Select(r => (CandidacyRow)Finish(r)).ToList(), total, page, pageSize));
        }

        [HttpGet("{id:int}")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> Get(int id, CancellationToken ct)
        {
            var row = await ProjectDetail(_db.Candidacies.AsNoTracking().Where(c => c.CandidacyId == id && c.IsActive))
                .FirstOrDefaultAsync(ct);
            return row == null ? NotFoundError("Candidacy") : Ok(Finish(row));
        }

        [HttpGet("{id:int}/photo")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> Photo(int id, CancellationToken ct)
        {
            var bytes = await _db.Candidacies.AsNoTracking()
                .Where(c => c.CandidacyId == id && c.IsActive)
                .Select(c => c.CandidateImage).FirstOrDefaultAsync(ct);
            return bytes == null || bytes.Length == 0 ? NoContent() : File(bytes, ImageSniffer.ContentType(bytes));
        }

        [HttpGet("lock-state")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateAdd,
                           PermissionCodes.CandidateEdit, PermissionCodes.CandidateDelete)]
        public async Task<IActionResult> LockState(CancellationToken ct)
        {
            var open = await OpenEventAsync(ct);
            return Ok(new CandidacyLockState
            {
                IsLocked = open != null,
                EventId = open?.EventId,
                EventTitle = open?.Title
            });
        }

        [HttpGet("options")]
        public IActionResult Options() => Ok(new CandidacyOptionsResponse
        {
            Positions = CandidatePositions.All.ToList(),
            Statuses = CandidacyStatuses.All.ToList(),
            CivilStatuses = CandidacyLists.CivilStatuses.ToList(),
            Genders = CandidacyLists.Genders.ToList(),
            EducationLevels = CandidacyLists.EducationLevels.ToList()
        });

        [HttpGet("export")]
        [RequirePermission(PermissionCodes.ReportExport, PermissionCodes.CandidateView)]
        public async Task<IActionResult> Export(
            [FromQuery] string? search, [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] string? position, [FromQuery] string? status,
            [FromQuery] bool includePhotos = false, CancellationToken ct = default)
        {
            var cap = includePhotos ? 1000 : 20000;
            var q = BuildQuery(search, fromUtc, toUtc, position, status);
            var rows = await ProjectDetail(q.OrderByDescending(c => c.CandidacyId).Take(cap + 1)).ToListAsync(ct);
            if (rows.Count > cap)
            {
                Response.Headers["X-Export-Truncated"] = "true";
                rows.RemoveAt(rows.Count - 1);
            }

            var result = rows.Select(r =>
            {
                var e = new CandidacyExportRow
                {
                    CandidacyId = r.CandidacyId, MemberId = r.MemberId, CandidateName = r.CandidateName,
                    MemberAccountCode = r.MemberAccountCode, BranchName = r.BranchName, Position = r.Position,
                    Status = r.Status, CellNo = r.CellNo, DateFiled = r.DateFiled, HasPhoto = r.HasPhoto,
                    UpdatedAt = r.UpdatedAt, WalkInCandidateName = r.WalkInCandidateName, Reason = r.Reason,
                    TelNo = r.TelNo, CivilStatus = r.CivilStatus, Gender = r.Gender, DateOfBirth = r.DateOfBirth,
                    HighestEducation = r.HighestEducation, PreviousPositions = r.PreviousPositions,
                    SpecialSkills = r.SpecialSkills, RejectionReason = r.RejectionReason,
                    DateApproved = r.DateApproved, DateRejected = r.DateRejected
                };
                return Finish(e);
            }).ToList();

            if (includePhotos)
            {
                var ids = result.Where(r => r.HasPhoto).Select(r => r.CandidacyId).ToList();
                var photos = await _db.Candidacies.AsNoTracking()
                    .Where(c => ids.Contains(c.CandidacyId))
                    .Select(c => new { c.CandidacyId, c.CandidateImage }).ToListAsync(ct);
                var map = photos.Where(p => p.CandidateImage != null)
                    .ToDictionary(p => p.CandidacyId, p => Convert.ToBase64String(p.CandidateImage!));
                foreach (var r in result) r.PhotoBase64 = map.GetValueOrDefault(r.CandidacyId);
            }
            return Ok(result);
        }

        // ── Create / Update / Delete ─────────────────────────────────────────
        [HttpPost]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Create([FromBody] CandidacyUpsertRequest req, CancellationToken ct)
        {
            var locked = await LockedAsync(ct);
            if (locked != null) return locked;

            var fail = await ValidateAsync(req, existing: null, ct);
            if (fail != null) return fail;
            TryDecodePhoto(req.PhotoBase64, out var photo, out _);

            var now = DateTime.UtcNow;
            var c = new CandidacyEntity
            {
                CreatedAt = now,
                UpdatedAt = now,
                DateFiled = now,
                IsActive = true
            };
            Apply(c, req, photo, now, previousStatus: null);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Candidacies.Add(c);
            await _db.SaveChangesAsync(ct);
            _audit.Stage(AuditActions.Create, AuditEntities.Candidacy, c.CandidacyId,
                $"Position='{c.Position}', Status='{c.Status}', {(c.MemberId.HasValue ? $"MemberId={c.MemberId}" : $"WalkIn='{c.WalkInCandidateName}'")}.");
            StageStatusAudit(c, previousStatus: null);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var row = await ProjectDetail(_db.Candidacies.AsNoTracking().Where(x => x.CandidacyId == c.CandidacyId)).FirstAsync(ct);
            return StatusCode(StatusCodes.Status201Created, Finish(row));
        }

        [HttpPut("{id:int}")]
        [RequirePermission(PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> Update(int id, [FromBody] CandidacyUpsertRequest req, CancellationToken ct)
        {
            var locked = await LockedAsync(ct);
            if (locked != null) return locked;

            var c = await _db.Candidacies.FirstOrDefaultAsync(x => x.CandidacyId == id && x.IsActive, ct);
            if (c == null) return NotFoundError("Candidacy");
            if (IsStale(req.ExpectedUpdatedAt, c.UpdatedAt)) return ConcurrencyError("candidacy");

            var fail = await ValidateAsync(req, existing: c, ct);
            if (fail != null) return fail;
            TryDecodePhoto(req.PhotoBase64, out var photo, out _);

            var previousStatus = c.Status;
            var now = DateTime.UtcNow;
            Apply(c, req, photo, now, previousStatus);

            _audit.Stage(AuditActions.Update, AuditEntities.Candidacy, c.CandidacyId,
                $"Position='{c.Position}', Status='{c.Status}'.");
            StageStatusAudit(c, previousStatus);
            await _db.SaveChangesAsync(ct);

            var row = await ProjectDetail(_db.Candidacies.AsNoTracking().Where(x => x.CandidacyId == id)).FirstAsync(ct);
            return Ok(Finish(row));
        }

        [HttpDelete("{id:int}")]
        [RequirePermission(PermissionCodes.CandidateDelete)]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            var locked = await LockedAsync(ct);
            if (locked != null) return locked;

            var c = await _db.Candidacies.FirstOrDefaultAsync(x => x.CandidacyId == id && x.IsActive, ct);
            if (c == null) return NotFoundError("Candidacy");

            var now = DateTime.UtcNow;
            c.IsActive = false;
            c.DeletedAt = now;
            c.UpdatedAt = now;
            _audit.Stage(AuditActions.SoftDelete, AuditEntities.Candidacy, c.CandidacyId,
                $"Position='{c.Position}', Status='{c.Status}'.");
            await _db.SaveChangesAsync(ct);
            return Ok(new MessageResponse { Message = "Candidacy deleted." });
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private static void Apply(CandidacyEntity c, CandidacyUpsertRequest r, byte[]? photo, DateTime now, string? previousStatus)
        {
            if (r.MemberId.HasValue)
            {
                c.MemberId = r.MemberId;
                c.WalkInCandidateName = null;
            }
            else
            {
                c.MemberId = null;
                c.WalkInCandidateName = (r.WalkInCandidateName ?? "").Trim();
            }

            c.Position = r.Position.Trim();
            c.Reason = r.Reason.Trim();
            c.CellNo = r.CellNo.Trim();
            c.TelNo = (r.TelNo ?? "").Trim();
            c.CivilStatus = r.CivilStatus.Trim();
            c.Gender = r.Gender.Trim();
            c.DateOfBirth = r.DateOfBirth.Date;
            c.HighestEducation = r.HighestEducation.Trim();
            c.PreviousPositions = (r.PreviousPositions ?? "").Trim();
            c.SpecialSkills = (r.SpecialSkills ?? "").Trim();
            c.RejectionReason = (r.RejectionReason ?? "").Trim();

            // Candidacies.CandidateImage is "longblob NOT NULL": "no photo" is an empty array, never NULL.
            if (r.RemovePhoto) c.CandidateImage = Array.Empty<byte>();
            else if (photo != null) c.CandidateImage = photo;
            else c.CandidateImage ??= Array.Empty<byte>();

            // Status transition bookkeeping (dates follow the status).
            var status = r.Status.Trim();
            if (status != previousStatus)
            {
                switch (status)
                {
                    case CandidacyStatuses.Approved: c.DateApproved = now; c.DateRejected = null; break;
                    case CandidacyStatuses.Rejected: c.DateRejected = now; c.DateApproved = null; break;
                    default: c.DateApproved = null; c.DateRejected = null; break;
                }
            }
            c.Status = status;
            c.UpdatedAt = now;
        }

        private void StageStatusAudit(CandidacyEntity c, string? previousStatus)
        {
            if (c.Status == previousStatus) return;
            if (c.Status == CandidacyStatuses.Approved)
                _audit.Stage(AuditActions.ApproveCandidacy, AuditEntities.Candidacy, c.CandidacyId, $"Candidacy {c.CandidacyId} approved.");
            else if (c.Status == CandidacyStatuses.Rejected)
                _audit.Stage(AuditActions.RejectCandidacy, AuditEntities.Candidacy, c.CandidacyId, $"Candidacy {c.CandidacyId} rejected.");
        }

        private async Task<IActionResult?> ValidateAsync(CandidacyUpsertRequest r, CandidacyEntity? existing, CancellationToken ct)
        {
            var errors = new Dictionary<string, string>();

            if (!CandidatePositions.IsValid(r.Position?.Trim())) errors["position"] = "Select a valid position.";
            if (!CandidacyStatuses.IsValid(r.Status?.Trim())) errors["status"] = "Select a valid status.";
            if (string.IsNullOrWhiteSpace(r.Reason)) errors["reason"] = "Reason for candidacy is required.";
            if (!CellRx.IsMatch((r.CellNo ?? "").Trim())) errors["cellNo"] = "Cell number must be exactly 11 digits.";
            if (!CandidacyLists.CivilStatuses.Contains(r.CivilStatus?.Trim())) errors["civilStatus"] = "Select a civil status.";
            if (!CandidacyLists.Genders.Contains(r.Gender?.Trim())) errors["gender"] = "Select a gender.";
            if (!CandidacyLists.EducationLevels.Contains(r.HighestEducation?.Trim())) errors["highestEducation"] = "Select the highest education.";

            var dob = r.DateOfBirth.Date;
            if (dob > DateTime.Today.AddYears(-18)) errors["dateOfBirth"] = "Candidate must be at least 18 years old.";
            else if (dob < new DateTime(1900, 1, 1)) errors["dateOfBirth"] = "Enter a valid date of birth.";

            if (!r.MemberId.HasValue && string.IsNullOrWhiteSpace(r.WalkInCandidateName))
                errors["walkInCandidateName"] = "Select a registered member or enter the walk-in candidate's name.";

            if (!TryDecodePhoto(r.PhotoBase64, out _, out var photoError)) errors["photo"] = photoError!;
            if (errors.Count > 0) return Invalid(errors);

            if (r.MemberId.HasValue)
            {
                if (!await _db.Members.AsNoTracking().AnyAsync(m => m.MemberId == r.MemberId && m.IsActive, ct))
                    return Invalid("memberId", "The selected member does not exist or was deleted.");

                // One live filing per member per position. Applies on create and when member/position change.
                var position = (r.Position ?? "").Trim();
                int? exceptId = existing?.CandidacyId;
                bool changed = existing == null || existing.MemberId != r.MemberId || existing.Position != position;
                if (changed && await _db.Candidacies.AsNoTracking().AnyAsync(c =>
                        c.IsActive && c.MemberId == r.MemberId && c.Position == position &&
                        c.Status != CandidacyStatuses.Withdrawn && c.Status != CandidacyStatuses.Rejected &&
                        (exceptId == null || c.CandidacyId != exceptId), ct))
                    return Duplicate("This member has already filed a candidacy for this position.", "position");
            }
            return null;
        }
    }
}
