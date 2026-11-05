using EVoting.Api.Models.Representative;
using EVoting.Api.Models.Responses;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

namespace EVoting.Api.Controllers
{
    /// <summary>Turns a service outcome into the standard response: the value on success, the standard ApiError body otherwise.</summary>
    internal static class ServiceResultMvc
    {
        public static IActionResult ToActionResult<T>(this ControllerBase controller, ServiceResult<T> r)
        {
            if (r.Ok)
                return r.StatusCode == StatusCodes.Status200OK ? controller.Ok(r.Value) : controller.StatusCode(r.StatusCode, r.Value);
            return new ObjectResult(new ApiError { Error = r.Error ?? "The request failed.", Code = r.Code, Detail = r.Detail, Fields = r.Fields })
            {
                StatusCode = r.StatusCode
            };
        }
    }

    /// <summary>
    /// The voter-, kiosk- and tablet-facing side of a Representative election: the ballot rules, the candidates, the kiosk roster sync,
    /// candidate photos, casting a ballot and synchronising offline ballots.
    ///
    ///   GET  /api/v1/events/{eventId}/representative-ballot                          Kiosk · Voter · Admin   rules, schedule, roster version
    ///   GET  /api/v1/events/{eventId}/representative-candidates                      Kiosk · Voter · Admin   paged, searchable, SAME list for every branch
    ///   GET  /api/v1/events/{eventId}/representative-candidates/{id}/photo           Kiosk · Voter · Admin   thumb (default) or ?size=full; ETag
    ///   GET  /api/v1/events/{eventId}/representative-roster                          Kiosk                   keyset pages for the kiosk's local cache
    ///   POST /api/v1/representative-votes                                            Voter                   the online ballot
    ///   POST /api/v1/representative-votes/offline-sync                               Kiosk                   one offline ballot
    ///   POST /api/v1/representative-votes/offline-sync-bulk                          Kiosk                   up to 500 offline ballots, each independent
    ///
    /// A Voter token is bound to ONE election: reading or voting for any other election with it is refused (TOKEN_MISMATCH).
    /// The General Assembly endpoints refuse a Representative election and these refuse a General Assembly one (WRONG_EVENT_TYPE).
    /// </summary>
    [ApiController]
    [ApiErrorModelState]
    [Route("api/v1")]
    public class RepresentativeVotingController : ControllerBase
    {
        private readonly IRepresentativeBallotService _ballot;
        private readonly IRepresentativeVoteService _votes;
        private readonly IRepresentativeLiveUpdates _live;
        private readonly ILogger<RepresentativeVotingController> _logger;

        public RepresentativeVotingController(
            IRepresentativeBallotService ballot, IRepresentativeVoteService votes, IRepresentativeLiveUpdates live,
            ILogger<RepresentativeVotingController> logger)
        {
            _ballot = ballot;
            _votes = votes;
            _live = live;
            _logger = logger;
        }

        /// <summary>A voter session may only read the election it was verified for.</summary>
        private IActionResult? VoterElectionMismatch(int eventId)
        {
            if (!User.IsInRole("Voter")) return null;
            if (int.TryParse(User.FindFirstValue("eventId"), out var tokenEvent) && tokenEvent == eventId) return null;
            return StatusCode(StatusCodes.Status403Forbidden, new ApiError
            {
                Error = "Your voter session belongs to a different election.",
                Code = RepCodes.TokenMismatch
            });
        }

        // ── reading the ballot ─────────────────────────────────────────────────────────────────────────────
        [HttpGet("events/{eventId:int}/representative-ballot")]
        [Authorize(Roles = "Kiosk,Voter,Admin")]
        public async Task<IActionResult> Ballot(int eventId, CancellationToken ct)
        {
            if (VoterElectionMismatch(eventId) is { } denied) return denied;
            Response.Headers.CacheControl = "no-store";
            return this.ToActionResult(await _ballot.GetBallotAsync(eventId, ct));
        }

        [HttpGet("events/{eventId:int}/representative-candidates")]
        [Authorize(Roles = "Kiosk,Voter,Admin")]
        public async Task<IActionResult> Candidates(
            int eventId, [FromQuery] string? search, [FromQuery] string? position,
            [FromQuery] int page = 1, [FromQuery] int pageSize = RepresentativeLimits.DefaultPageSize, CancellationToken ct = default)
        {
            if (VoterElectionMismatch(eventId) is { } denied) return denied;
            Response.Headers.CacheControl = "no-store";
            return this.ToActionResult(await _ballot.ListCandidatesAsync(eventId, search, position, page, pageSize, ct));
        }

        /// <summary>size=thumb (default) returns the small version when one exists; size=full the original. ETag = the photo's version stamp.</summary>
        [HttpGet("events/{eventId:int}/representative-candidates/{candidateId:int}/photo")]
        [Authorize(Roles = "Kiosk,Voter,Admin")]
        public async Task<IActionResult> Photo(int eventId, int candidateId, [FromQuery] string? size, CancellationToken ct)
        {
            if (VoterElectionMismatch(eventId) is { } denied) return denied;
            var r = await _ballot.GetCandidatePhotoAsync(eventId, candidateId, !string.Equals(size, "full", StringComparison.OrdinalIgnoreCase), ct);
            if (!r.Ok) return this.ToActionResult(r);
            if (r.Value == null) return NoContent();

            var photo = r.Value;
            var etag = $"\"{photo.Version.Ticks:x}-{(photo.IsThumbnail ? "t" : "f")}\"";
            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "private, max-age=300";
            if (Request.Headers.IfNoneMatch.Any(v => v == etag)) return StatusCode(StatusCodes.Status304NotModified);
            return File(photo.Bytes, photo.ContentType);
        }

        /// <summary>
        /// The kiosk's roster download: keyset pages (afterId = last id received; default 200 rows, at most 500). The first page also carries the
        /// position rules. Every page repeats the roster version, so a kiosk notices a roster that changed in the middle of its download.
        /// </summary>
        [HttpGet("events/{eventId:int}/representative-roster")]
        [Authorize(Roles = "Kiosk")]
        public async Task<IActionResult> Roster(int eventId, [FromQuery] int afterId = 0, [FromQuery] int pageSize = 200, CancellationToken ct = default)
        {
            Response.Headers.CacheControl = "no-store";
            return this.ToActionResult(await _ballot.SyncRosterAsync(eventId, afterId, pageSize, ct));
        }

        // ── casting ────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>
        /// The online ballot. Requires the Voter token issued by /auth/voter-verify. Selections are roster ids only; the server derives each
        /// position and applies the election's rules from the database. A ReceiptCode proposed by the client makes a retry idempotent.
        /// </summary>
        [HttpPost("representative-votes")]
        [Authorize(Roles = "Voter")]
        [EnableRateLimiting("vote")]
        public async Task<IActionResult> Cast([FromBody] RepSubmitVoteRequest req, CancellationToken ct)
        {
            if (!int.TryParse(User.FindFirstValue("memberId"), out var memberId) || !int.TryParse(User.FindFirstValue("eventId"), out var tokenEventId))
                return Unauthorized(new ApiError { Error = "Invalid voter token.", Code = "INVALID_TOKEN" });

            var result = await _votes.CastAsync(memberId, tokenEventId, req, ct);
            if (result.Ok && !result.Value!.AlreadyProcessed)
                _live.BallotCast(req.EventId, memberId);          // a replay changed nothing: no dashboard push
            return this.ToActionResult(result);
        }

        /// <summary>One ballot recorded on a kiosk while it was offline. Kiosk token required (the voter's own token expired long ago).</summary>
        [HttpPost("representative-votes/offline-sync")]
        [Authorize(Roles = "Kiosk")]
        [EnableRateLimiting("vote")]
        public async Task<IActionResult> SyncOne([FromBody] RepOfflineVoteSyncRequest req, CancellationToken ct)
        {
            var kioskId = User.FindFirstValue("kioskId") ?? "(unknown)";
            var outcome = await _votes.SyncOfflineAsync(new[] { req }, kioskId, ct);
            var item = outcome.Items[0];
            if (item == null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ApiError
                {
                    Error = "A temporary server error occurred; keep the ballot and retry later.",
                    Code = RepCodes.TemporaryFailure
                });
            if (item.Ok && !item.Value!.AlreadyProcessed)
                _live.BallotCast(req.EventId, req.MemberId);
            return this.ToActionResult(item);
        }

        /// <summary>
        /// Up to 500 offline ballots in one round trip. Always 200 with one result per ballot: a rejection never aborts the others, and
        /// IsPermanentRejection tells the kiosk which ballots to stop retrying (the rest stay queued).
        /// </summary>
        [HttpPost("representative-votes/offline-sync-bulk")]
        [Authorize(Roles = "Kiosk")]
        [EnableRateLimiting("vote")]
        public async Task<IActionResult> SyncBulk([FromBody] RepOfflineVoteSyncBulkRequest req, CancellationToken ct)
        {
            var kioskId = User.FindFirstValue("kioskId") ?? "(unknown)";
            var outcome = await _votes.SyncOfflineAsync(req.Votes, kioskId, ct);
            foreach (var group in outcome.NewlyAccepted.GroupBy(a => a.EventId))
                _live.BallotsCast(group.Key, group.Select(a => a.MemberId));
            _logger.LogInformation(
                "Representative offline sync from {Kiosk}: received={Received} accepted={Accepted} replayed={Replayed} rejected={Rejected} retry={Retry}",
                RepresentativeVoteService.SafeLabel(kioskId), req.Votes.Count, outcome.Response.Accepted, outcome.Response.Replayed,
                outcome.Response.Rejected, outcome.Response.RetryLater);
            return Ok(outcome.Response);
        }
    }

    /// <summary>The administrator's view of a Representative election's results.</summary>
    [Route("api/v1/events/{eventId:int}/representative-results")]
    public class RepresentativeResultsController : AdminControllerBase
    {
        private readonly IRepresentativeResultsService _results;

        public RepresentativeResultsController(IRepresentativeResultsService results) => _results = results;

        /// <summary>Per-position ranking with every candidate (zero-vote ones too), turnout and winners. EVENT_VIEW or REPORT_VIEW.</summary>
        [HttpGet]
        [RequirePermission(PermissionCodes.EventView, PermissionCodes.ReportView)]
        public async Task<IActionResult> Get(int eventId, CancellationToken ct) => FromResult(await _results.GetResultsAsync(eventId, ct));

        /// <summary>The same results as a CSV file (audited). REPORT_EXPORT.</summary>
        [HttpGet("export")]
        [RequirePermission(PermissionCodes.ReportExport)]
        public async Task<IActionResult> Export(int eventId, CancellationToken ct)
        {
            var r = await _results.ExportCsvAsync(eventId, ct);
            return r.Ok ? File(r.Value!.Content, "text/csv; charset=utf-8", r.Value.FileName) : FromResult(r);
        }
    }
}
