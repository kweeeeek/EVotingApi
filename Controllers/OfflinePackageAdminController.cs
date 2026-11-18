using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Builds the kiosk package (.evpkg, see <see cref="KioskPackageFile"/>) for a General Assembly event: what a kiosk downloads while
    /// online, for a kiosk that will be at a venue with no signal - even one that was never online. The Admin app saves it to a USB stick;
    /// the kiosk imports it with Ctrl+Shift+Alt+I.
    ///
    /// The content is built with EXACTLY the rules of the kiosk's own online endpoints (EventsController): member-snapshot (eligibility =
    /// IsEligibleToVote and branch assigned to the event), candidates (approved, active, within 180 days), position-rules (saved rules,
    /// or the legacy defaults for a General Assembly without rules), plus the members who have already voted in this event.
    /// The event does not have to be Opened yet (a package can be made the day before): offline, the kiosk follows the schedule.
    /// Permission: OFFLINE_PACKAGE_EXPORT (personal data of every member). Every export is audited.
    /// </summary>
    [Route("api/v1/admin/offline-packages")]
    public class OfflinePackageAdminController : AdminControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly IConfiguration _config;
        private readonly ILogger<OfflinePackageAdminController> _logger;

        public OfflinePackageAdminController(ApiDbContext db, IAuditService audit, IConfiguration config, ILogger<OfflinePackageAdminController> logger)
        {
            _db = db;
            _audit = audit;
            _config = config;
            _logger = logger;
        }

        public sealed class PackageResponse
        {
            public string FileName { get; set; } = "";
            public string FileContent { get; set; } = "";
            public string ExportId { get; set; } = "";
            public string FileSha256 { get; set; } = "";
            public DateTime CreatedAtUtc { get; set; }
            public int EventId { get; set; }
            public string EventTitle { get; set; } = "";
            public DateTime ScheduleFrom { get; set; }
            public DateTime ScheduleTo { get; set; }
            public int MemberCount { get; set; }
            public int EligibleCount { get; set; }
            public int CandidateCount { get; set; }
            public int VotedCount { get; set; }
        }

        [HttpPost("{eventId:int}")]
        [RequirePermission(PermissionCodes.OfflinePackageExport)]
        public async Task<IActionResult> Create(int eventId, CancellationToken ct)
        {
            var secret = Environment.GetEnvironmentVariable("EVOTING_KIOSK_SECRET");
            if (string.IsNullOrWhiteSpace(secret)) secret = _config["Security:KioskSecret"];
            if (string.IsNullOrWhiteSpace(secret) || SecretSettings.LooksLikePlaceholder(secret))
                return Fail(StatusCodes.Status503ServiceUnavailable,
                    "Kiosk packages cannot be made: this server has no kiosk secret configured.", "KIOSK_SECRET_NOT_CONFIGURED");

            var ev = await _db.Events.AsNoTracking().Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.EventId == eventId && e.IsActive, ct);
            if (ev == null) return NotFoundError("Active election");
            if (ev.EventType != EventTypes.GeneralAssembly)
                return Fail(StatusCodes.Status409Conflict, "Kiosk packages are for General Assembly elections only.", "WRONG_EVENT_TYPE");

            // ── members: same rules as EventsController.GetMemberSnapshot ──────────────────────────────
            var assigned = ev.EventBranches.Select(b => b.BranchId).ToHashSet();
            var members = await _db.Members.AsNoTracking().Include(m => m.Branch).Where(m => m.IsActive).ToListAsync(ct);
            var packMembers = members.Select(m => new KioskPackageFile.PackageMember
            {
                MemberId = m.MemberId,
                AccountCode = m.AccountCode,
                MemberName = m.MemberName,
                BranchId = m.BranchId,
                BranchName = m.Branch?.BranchName ?? "",
                IsEligible = m.IsEligibleToVote && assigned.Contains(m.BranchId),
                PhotoBase64 = m.MemberImage != null ? Convert.ToBase64String(m.MemberImage) : null
            }).ToList();

            // ── candidates: same rules as EventsController.GetCandidates ────────────────────────────────
            var candidacies = await _db.Candidacies.AsNoTracking().Include(c => c.Member)
                .Where(c => c.Status == "Approved" && c.IsActive &&
                            (c.DateApproved == null
                                ? c.DateFiled >= ev.ScheduleFrom.AddDays(-180)
                                : c.DateApproved >= ev.ScheduleFrom.AddDays(-180)))
                .OrderBy(c => c.Position)
                .ThenBy(c => c.MemberId != null ? c.Member!.MemberName : c.WalkInCandidateName ?? "")
                .ToListAsync(ct);
            var packCandidates = candidacies.Select(c => new KioskPackageFile.PackageCandidate
            {
                CandidacyId = c.CandidacyId,
                CandidateName = c.CandidateDisplayName,
                Position = c.Position,
                Status = c.Status,
                PreviousPositions = c.PreviousPositions,
                Reason = c.Reason,
                PhotoBase64 = c.CandidateImage != null ? Convert.ToBase64String(c.CandidateImage) : null
            }).ToList();
            if (packCandidates.Count == 0)
                return Fail(StatusCodes.Status409Conflict, "This election has no approved candidates yet. Approve the candidates first, then make the package.", "NO_CANDIDATES");

            // ── position rules: same as EventsController.GetPositionRules (legacy defaults for a GA without rules) ─
            var rules = await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == eventId).OrderBy(r => r.Id)
                .Select(r => new KioskPackageFile.PackageRule { Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes, TotalSeats = r.TotalSeats })
                .ToListAsync(ct);
            if (rules.Count == 0)
                rules = new()
                {
                    new() { Position = "Board of Director", MinVotes = 1, MaxVotes = 11, TotalSeats = 11 },
                    new() { Position = "Audit Committee", MinVotes = 1, MaxVotes = 2, TotalSeats = 2 },
                    new() { Position = "Election Committee", MinVotes = 1, MaxVotes = 1, TotalSeats = 1 },
                    new() { Position = "Secretary", MinVotes = 1, MaxVotes = 2, TotalSeats = 2 },
                };

            // ── members who already voted in this event (mobile, kiosks, earlier imports) ───────────────
            var voted = await _db.VoterLogs.AsNoTracking().Where(l => l.EventId == eventId && l.HasVoted)
                .Select(l => l.MemberId).Distinct().ToListAsync(ct);

            var payload = new KioskPackageFile.Payload
            {
                Event = new KioskPackageFile.PackageEvent
                {
                    EventId = ev.EventId,
                    Title = ev.Title,
                    Venue = ev.Venue,
                    ScheduleFrom = ev.ScheduleFrom,
                    ScheduleTo = ev.ScheduleTo,
                    Status = ev.Status,
                    IsActive = ev.IsActive,
                    EventType = ev.EventType
                },
                PositionRules = rules,
                Candidates = packCandidates,
                Members = packMembers,
                VotedMemberIds = voted
            };

            var now = DateTime.UtcNow;
            var written = KioskPackageFile.Write(secret, payload, now);
            var res = new PackageResponse
            {
                FileName = $"EVENT{ev.EventId}_{DateTime.Now:yyyyMMdd-HHmm}{KioskPackageFile.FileExtension}",
                FileContent = written.Text,
                ExportId = written.ExportId,
                FileSha256 = written.Sha256,
                CreatedAtUtc = now,
                EventId = ev.EventId,
                EventTitle = ev.Title,
                ScheduleFrom = ev.ScheduleFrom,
                ScheduleTo = ev.ScheduleTo,
                MemberCount = packMembers.Count,
                EligibleCount = packMembers.Count(m => m.IsEligible),
                CandidateCount = packCandidates.Count,
                VotedCount = voted.Count
            };

            await _audit.WriteAsync(AuditActions.OfflinePackageExported, AuditEntities.Event, ev.EventId,
                $"ExportId={res.ExportId}; FileSha256={res.FileSha256}; Members={res.MemberCount}; Eligible={res.EligibleCount}; " +
                $"Candidates={res.CandidateCount}; AlreadyVoted={res.VotedCount}", ct: ct);
            _logger.LogInformation("Kiosk package made: EventId={EventId} Export={Export} Members={Members} Candidates={Candidates} Voted={Voted}",
                ev.EventId, res.ExportId, res.MemberCount, res.CandidateCount, res.VotedCount);

            return Ok(res);
        }
    }
}
