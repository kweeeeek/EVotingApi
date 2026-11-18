using EVoting.Api.Hubs;
using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Admin;
using EVoting.Api.Models.Requests;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Import of a kiosk vote file carried on a USB stick (kiosk: Ctrl+Shift+Alt+E "Export pending votes").
    /// For a venue with no signal at all: the kiosk's queued votes reach the server without the kiosk ever going online.
    ///
    ///   preview  - opens the file and says what importing it would do. Writes nothing.
    ///   commit   - records every vote through EXACTLY the same rules as the kiosk's automatic sync
    ///              (VoteService.CastOfflineVoteAsync): one vote per member, the vote that reached the server first stands
    ///              (a member who already voted on mobile keeps that vote), cast time must be inside the voting schedule.
    ///
    /// Importing the same file twice, or importing a file AND letting the kiosk sync later, is safe: a vote already recorded
    /// with the same receipt is recognised as already recorded and is never counted twice.
    /// Permission: OFFLINE_VOTE_IMPORT. Every commit is written to the audit trail (kiosk, export id, file SHA-256, totals).
    /// </summary>
    [Route("api/v1/admin/offline-vote-imports")]
    public class OfflineVoteImportAdminController : AdminControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly IVoteService _votes;
        private readonly IAuditService _audit;
        private readonly IConfiguration _config;
        private readonly IHubContext<ElectionHub> _hub;
        private readonly ILogger<OfflineVoteImportAdminController> _logger;

        public OfflineVoteImportAdminController(ApiDbContext db, IVoteService votes, IAuditService audit, IConfiguration config,
            IHubContext<ElectionHub> hub, ILogger<OfflineVoteImportAdminController> logger)
        {
            _db = db;
            _votes = votes;
            _audit = audit;
            _config = config;
            _hub = hub;
            _logger = logger;
        }

        [HttpPost("preview")]
        [RequirePermission(PermissionCodes.OfflineVoteImport)]
        [RequestSizeLimit(32 * 1024 * 1024)]
        public async Task<IActionResult> Preview([FromBody] OfflineVoteFileRequest req, CancellationToken ct)
        {
            if (!TryOpen(req, out var file, out var error)) return error!;
            var votes = file!.Payload.Votes;

            var memberIds = votes.Select(v => v.MemberId).Distinct().ToList();
            var eventIds = votes.Select(v => v.EventId).Distinct().ToList();
            var logs = await _db.VoterLogs.AsNoTracking()
                .Where(l => memberIds.Contains(l.MemberId) && eventIds.Contains(l.EventId) && l.HasVoted)
                .Select(l => new { l.MemberId, l.EventId, l.ReceiptCode })
                .ToListAsync(ct);
            var recorded = logs.ToDictionary(l => (l.MemberId, l.EventId), l => l.ReceiptCode);
            var titles = await _db.Events.AsNoTracking().Where(e => eventIds.Contains(e.EventId))
                .ToDictionaryAsync(e => e.EventId, e => e.Title, ct);

            var res = new OfflineVoteImportPreviewResponse
            {
                KioskId = file.Header.KioskId,
                ExportId = file.Header.ExportId,
                CreatedAtUtc = file.Header.CreatedAtUtc,
                FileSha256 = file.FileSha256,
                VoteCount = votes.Count,
                EarliestCastAtUtc = votes.Count == 0 ? null : votes.Min(v => v.CastAtUtc),
                LatestCastAtUtc = votes.Count == 0 ? null : votes.Max(v => v.CastAtUtc),
                Events = votes.GroupBy(v => v.EventId).OrderBy(g => g.Key).Select(g => new OfflineVoteImportEventCount
                {
                    EventId = g.Key,
                    Title = titles.TryGetValue(g.Key, out var t) ? t : "(unknown election)",
                    Votes = g.Count()
                }).ToList(),
                ImportedBefore = await _db.AuditLogs.AsNoTracking().AnyAsync(a =>
                    a.Action == AuditActions.OfflineVotesImported && a.Details != null && a.Details.Contains("ExportId=" + file.Header.ExportId), ct),
            };

            foreach (var v in votes)
            {
                if (!recorded.TryGetValue((v.MemberId, v.EventId), out var receipt)) res.NewVotes++;
                else if (string.Equals(receipt, v.ReceiptCode, StringComparison.Ordinal)) res.AlreadyRecorded++;
                else res.MemberAlreadyVoted++;
            }
            return Ok(res);
        }

        [HttpPost("commit")]
        [RequirePermission(PermissionCodes.OfflineVoteImport)]
        [RequestSizeLimit(32 * 1024 * 1024)]
        public async Task<IActionResult> Commit([FromBody] OfflineVoteFileRequest req, CancellationToken ct)
        {
            if (!TryOpen(req, out var file, out var error)) return error!;
            var h = file!.Header;
            var res = new OfflineVoteImportResultResponse
            {
                KioskId = h.KioskId,
                ExportId = h.ExportId,
                FileSha256 = file.FileSha256,
                VoteCount = file.Payload.Votes.Count
            };
            var problems = new List<(KioskVoteFile.Vote Vote, string Outcome, string Reason)>();
            var newlyVoted = new List<(int EventId, int MemberId)>();

            // Oldest first, as the kiosk's own sync does.
            foreach (var v in file.Payload.Votes.OrderBy(v => v.CastAtUtc))
            {
                var selections = v.Selections
                    .SelectMany(kv => kv.Value.Select(id => new VoteSelectionItem { CandidacyId = id, Position = kv.Key }))
                    .ToList();
                try
                {
                    var (_, wasReplay) = await _votes.CastOfflineVoteAsync(v.MemberId, v.EventId, selections, v.ReceiptCode, v.CastAtUtc, ct);
                    if (wasReplay) res.AlreadyRecorded++;
                    else { res.Accepted++; newlyVoted.Add((v.EventId, v.MemberId)); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (InvalidOperationException ex)
                {
                    // Same meaning as VotesController.SyncOfflineVotesBulk: a final business answer (already voted, not eligible, outside the schedule ...).
                    res.Rejected++;
                    problems.Add((v, "Rejected", ex.Message));
                }
                catch (Exception ex)
                {
                    // Anything else is not a considered answer about this vote (election not active, a bad request, a database hiccup):
                    // nothing was recorded for it, and importing the same file again later is safe.
                    _logger.LogWarning(ex, "Offline vote import: vote not recorded (retriable). Kiosk={Kiosk} Export={Export} Member={Member} Receipt={Receipt}",
                        h.KioskId, h.ExportId, v.MemberId, v.ReceiptCode);
                    _db.ChangeTracker.Clear();
                    res.Failed++;
                    problems.Add((v, "Failed", ex is ArgumentException ? ex.Message.Split(" (Parameter")[0] : "A temporary server error occurred. Import this file again later."));
                }
            }

            if (problems.Count > 0)
            {
                var ids = problems.Select(p => p.Vote.MemberId).Distinct().ToList();
                var members = await _db.Members.AsNoTracking().Where(m => ids.Contains(m.MemberId))
                    .ToDictionaryAsync(m => m.MemberId, m => new { m.AccountCode, m.MemberName }, ct);
                res.Problems = problems.Select(p => new OfflineVoteImportProblem
                {
                    MemberId = p.Vote.MemberId,
                    AccountCode = members.TryGetValue(p.Vote.MemberId, out var m) ? m.AccountCode : "",
                    MemberName = m?.MemberName ?? "",
                    EventId = p.Vote.EventId,
                    ReceiptCode = p.Vote.ReceiptCode,
                    CastAtUtc = p.Vote.CastAtUtc,
                    Outcome = p.Outcome,
                    Reason = p.Reason
                }).ToList();
            }

            _db.ChangeTracker.Clear();
            await _audit.WriteAsync(AuditActions.OfflineVotesImported, AuditEntities.OfflineVoteImport, null,
                $"KioskId={h.KioskId}; ExportId={h.ExportId}; ExportedAtUtc={h.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}; FileSha256={file.FileSha256}; " +
                $"Votes={res.VoteCount}; Accepted={res.Accepted}; AlreadyRecorded={res.AlreadyRecorded}; Rejected={res.Rejected}; Failed={res.Failed}",
                ct: ct);

            _logger.LogInformation("Offline vote file imported: Kiosk={Kiosk} Export={Export} Votes={Votes} Accepted={Accepted} AlreadyRecorded={Replay} Rejected={Rejected} Failed={Failed}",
                h.KioskId, h.ExportId, res.VoteCount, res.Accepted, res.AlreadyRecorded, res.Rejected, res.Failed);

            // Online kiosks mark these members as voted (same push as the automatic sync).
            foreach (var (eventId, memberId) in newlyVoted)
            {
                try { _ = _hub.Clients.Group(ElectionHub.KioskGroup).SendAsync("MemberVoted", new { eventId, memberId }, CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "MemberVoted push failed after import (votes are recorded)."); }
            }

            return Ok(res);
        }

        private bool TryOpen(OfflineVoteFileRequest req, out KioskVoteFile.Opened? file, out IActionResult? error)
        {
            file = null;
            error = null;
            var secret = Environment.GetEnvironmentVariable("EVOTING_KIOSK_SECRET");
            if (string.IsNullOrWhiteSpace(secret)) secret = _config["Security:KioskSecret"];
            if (string.IsNullOrWhiteSpace(secret) || SecretSettings.LooksLikePlaceholder(secret))
            {
                error = Fail(StatusCodes.Status503ServiceUnavailable,
                    "Kiosk vote files cannot be read: this server has no kiosk secret configured.", "KIOSK_SECRET_NOT_CONFIGURED");
                return false;
            }
            try
            {
                file = KioskVoteFile.Open(secret, req?.FileContent ?? "");
                return true;
            }
            catch (KioskVoteFile.InvalidFileException ex)
            {
                error = Fail(StatusCodes.Status400BadRequest, ex.Message, "INVALID_KIOSK_VOTE_FILE");
                return false;
            }
        }
    }
}
