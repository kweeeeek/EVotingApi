using EVoting.Api.Hubs;
using EVoting.Api.Models.Requests;
using EVoting.Api.Models.Responses;
using EVoting.Api.Services;
using EVoting.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace EVoting.Api.Controllers
{
    [ApiController]
    [Route("api/v1/votes")]
    public class VotesController : ControllerBase
    {
        private readonly IVoteService                  _voteService;
        private readonly IHubContext<ElectionHub>      _hub;
        private readonly ILogger<VotesController>      _logger;
        private readonly IServiceScopeFactory          _scopeFactory;

        public VotesController(
            IVoteService voteService,
            IHubContext<ElectionHub> hub,
            ILogger<VotesController> logger,
            IServiceScopeFactory scopeFactory)
        {
            _voteService  = voteService;
            _hub          = hub;
            _logger       = logger;
            _scopeFactory = scopeFactory;
        }

        // ── POST /api/v1/votes ────────────────────────────────────────────────
        /// <summary>
        /// Submit a vote. Requires a valid Voter JWT (issued by /auth/voter-verify).
        /// After a successful cast, pushes live TurnoutUpdated and LiveResultsUpdated
        /// events to all SignalR clients subscribed to the event group.
        /// </summary>
        [HttpPost]
        [Authorize(Roles = "Voter")]
        [EnableRateLimiting("vote")]
        public async Task<IActionResult> SubmitVote(
            [FromBody] SubmitVoteRequest req, CancellationToken ct)
        {
            var memberIdClaim = User.FindFirstValue("memberId");
            var eventIdClaim  = User.FindFirstValue("eventId");

            if (!int.TryParse(memberIdClaim, out int memberId) ||
                !int.TryParse(eventIdClaim, out int eventId))
                return Unauthorized(new ApiError { Error = "Invalid voter token." });

            if (req.EventId != eventId)
                return BadRequest(new ApiError
                {
                    Error  = "Event mismatch.",
                    Detail = "The event in your token does not match the submitted event."
                });

            if (await _voteService.HasVotedAsync(memberId, eventId, ct))
                return Conflict(new ApiError { Error = "You have already voted in this election." });

            string receipt;
            try
            {
                receipt = await _voteService.CastVotesAsync(memberId, eventId, req.Selections, ct);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already voted"))
            {
                return Conflict(new ApiError { Error = ex.Message });
            }

            
            _ = PushLiveUpdatesAsync(eventId);
            _ = PushMemberVotedAsync(eventId, memberId);

            return Ok(new VoteSubmitResponse
            {
                Message     = "Your vote has been recorded successfully.",
                ReceiptCode = receipt,
                VotedAt     = DateTime.UtcNow
            });
        }

        // ── POST /api/v1/votes/offline-sync ───────────────────────────────────
        /// <summary>
        /// Records a vote that was cast on a kiosk while it had no connectivity.
        /// Requires a valid Kiosk JWT (the per-voter JWT from the original
        /// verify call has long expired by the time a kiosk reconnects and
        /// syncs its local queue). Idempotent: re-submitting the same
        /// ReceiptCode after a successful sync returns the same result instead
        /// of a conflict, so the kiosk can retry safely if it never saw the
        /// first response.
        /// </summary>
        [HttpPost("offline-sync")]
        [Authorize(Roles = "Kiosk")]
        [EnableRateLimiting("vote")]
        public async Task<IActionResult> SyncOfflineVote(
            [FromBody] OfflineVoteSyncRequest req, CancellationToken ct)
        {
            var kioskId = User.FindFirstValue("kioskId") ?? "(unknown)";

            try
            {
                var (receipt, wasReplay) = await _voteService.CastOfflineVoteAsync(
                    req.MemberId, req.EventId, req.Selections,
                    req.ReceiptCode, req.CastAtUtc, ct);

                _logger.LogInformation(
                    "Offline vote sync accepted: KioskId={KioskId} MemberId={MemberId} " +
                    "EventId={EventId} Receipt={Receipt} Replay={Replay}",
                    kioskId, req.MemberId, req.EventId, receipt, wasReplay);

                // Only push live dashboard updates for a genuinely new vote —
                // a replayed sync changed nothing in the DB.
                if (!wasReplay)
                {
                    _ = PushLiveUpdatesAsync(req.EventId);
                    _ = PushMemberVotedAsync(req.EventId, req.MemberId);
                }

                return Ok(new OfflineVoteSyncResponse
                {
                    Message = wasReplay
                        ? "This vote was already recorded — no action needed."
                        : "Offline vote synced and recorded successfully.",
                    ReceiptCode      = receipt,
                    AlreadyProcessed = wasReplay
                });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already voted"))
            {
                // Genuine conflict — a DIFFERENT receipt already on file for
                // this member+event. Needs a human to look at it; never
                // silently accepted twice.
                _logger.LogWarning(
                    "Offline vote sync CONFLICT: KioskId={KioskId} MemberId={MemberId} " +
                    "EventId={EventId} Receipt={Receipt} — {Reason}",
                    kioskId, req.MemberId, req.EventId, req.ReceiptCode, ex.Message);
                return Conflict(new ApiError { Error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // Business rejection that isn't a duplicate — member not
                // found/inactive, not eligible, or branch not assigned.
                // Re-checked fresh here since this call runs under the
                // kiosk's token, not a just-verified voter's.
                _logger.LogWarning(
                    "Offline vote sync rejected: KioskId={KioskId} MemberId={MemberId} " +
                    "EventId={EventId} Receipt={Receipt} — {Reason}",
                    kioskId, req.MemberId, req.EventId, req.ReceiptCode, ex.Message);
                return UnprocessableEntity(new ApiError { Error = ex.Message });
            }
        }

        // ── POST /api/v1/votes/offline-sync-bulk ──────────────────────────────
        /// <summary>
        /// Same job as SyncOfflineVote above, but for many queued votes in one
        /// HTTP round-trip — see VoteSyncBackgroundService on the kiosk side,
        /// which chunks its whole Pending queue into batches of this shape
        /// instead of one call per vote. Each item is validated and cast
        /// completely independently (its own transaction via
        /// CastOfflineVoteAsync) — one item failing never aborts the rest of
        /// the batch, and the response always returns 200 with a per-item
        /// result array rather than an overall pass/fail status code, since a
        /// batch is almost always a mix of outcomes.
        ///
        /// The per-item IsPermanentRejection distinction is the piece that
        /// doesn't exist on the single-item endpoint: a genuine business
        /// rejection (already voted, not eligible, etc.) is permanent and the
        /// kiosk correctly stops retrying it, but an unexpected exception
        /// (e.g. a transient DB/connection issue) must NOT be treated the
        /// same way — that's what silently discarded a handful of
        /// legitimately-cast ballots during testing, traced back to a
        /// separate bug (see PushLiveUpdatesAsync below). IsPermanentRejection
        /// = false tells the kiosk to leave that vote Pending and simply
        /// retry it, instead of ballot secrecy walking in and wiping a vote
        /// that never actually got a real answer from the server.
        /// </summary>
        [HttpPost("offline-sync-bulk")]
        [Authorize(Roles = "Kiosk")]
        [EnableRateLimiting("vote")]
        public async Task<IActionResult> SyncOfflineVotesBulk(
            [FromBody] OfflineVoteSyncBulkRequest req, CancellationToken ct)
        {
            var kioskId = User.FindFirstValue("kioskId") ?? "(unknown)";
            var results = new List<OfflineVoteSyncBulkItemResult>(req.Votes.Count);
            var affectedEventIds = new HashSet<int>();
            // Separate from affectedEventIds: that one's deduped per-event for the
            // dashboard refresh, but MemberVoted is meaningful per-member — a kiosk
            // needs to know exactly which members to mark, not just that "something
            // changed" for the event.
            var newlyVotedMembers = new List<(int EventId, int MemberId)>();

            foreach (var item in req.Votes)
            {
                try
                {
                    var (receipt, wasReplay) = await _voteService.CastOfflineVoteAsync(
                        item.MemberId, item.EventId, item.Selections,
                        item.ReceiptCode, item.CastAtUtc, ct);

                    _logger.LogInformation(
                        "Offline vote sync accepted (bulk): KioskId={KioskId} MemberId={MemberId} " +
                        "EventId={EventId} Receipt={Receipt} Replay={Replay}",
                        kioskId, item.MemberId, item.EventId, receipt, wasReplay);

                    if (!wasReplay)
                    {
                        affectedEventIds.Add(item.EventId);
                        newlyVotedMembers.Add((item.EventId, item.MemberId));
                    }

                    results.Add(new OfflineVoteSyncBulkItemResult
                    {
                        ReceiptCode      = item.ReceiptCode,
                        Success          = true,
                        AlreadyProcessed = wasReplay,
                        Message = wasReplay
                            ? "This vote was already recorded — no action needed."
                            : "Offline vote synced and recorded successfully."
                    });
                }
                catch (InvalidOperationException ex)
                {
                    // Every InvalidOperationException CastOfflineVoteAsync
                    // throws is a genuine, permanent business rejection
                    // (already voted; not found/inactive; not eligible;
                    // branch not assigned) — re-attempting can't change the
                    // outcome, so it's safe to mark this one permanent.
                    _logger.LogWarning(
                        "Offline vote sync rejected (bulk): KioskId={KioskId} MemberId={MemberId} " +
                        "EventId={EventId} Receipt={Receipt} — {Reason}",
                        kioskId, item.MemberId, item.EventId, item.ReceiptCode, ex.Message);

                    results.Add(new OfflineVoteSyncBulkItemResult
                    {
                        ReceiptCode           = item.ReceiptCode,
                        Success               = false,
                        IsPermanentRejection  = true,
                        Message               = ex.Message
                    });
                }
                catch (Exception ex)
                {
                    // Anything else is an unexpected/infrastructure-level
                    // failure, not a considered business answer — deliberately
                    // NOT marked permanent, so the kiosk leaves this one
                    // Pending and retries it on a future sync cycle instead
                    // of destroying a legitimately-cast ballot over it.
                    _logger.LogError(ex,
                        "Offline vote sync error (bulk, will retry): KioskId={KioskId} MemberId={MemberId} " +
                        "EventId={EventId} Receipt={Receipt}",
                        kioskId, item.MemberId, item.EventId, item.ReceiptCode);

                    results.Add(new OfflineVoteSyncBulkItemResult
                    {
                        ReceiptCode           = item.ReceiptCode,
                        Success               = false,
                        IsPermanentRejection  = false,
                        Message               = "A temporary server error occurred — this vote will be retried automatically."
                    });
                }
            }

            // One push per affected event (not per vote) — a chunk of, say,
            // 200 votes for the same event needs exactly one refreshed
            // dashboard update, not 200 of them.
            foreach (var eventId in affectedEventIds)
                _ = PushLiveUpdatesAsync(eventId);

            // One MemberVoted push per newly-synced member, so every online
            // kiosk can mark them locally (see PushMemberVotedAsync below).
            foreach (var (eventId, memberId) in newlyVotedMembers)
                _ = PushMemberVotedAsync(eventId, memberId);

            return Ok(new OfflineVoteSyncBulkResponse { Results = results });
        }

        // ── GET /api/v1/votes/receipt/{code} ──────────────────────────────────
        [HttpGet("receipt/{code}")]
        [AllowAnonymous]
        public async Task<IActionResult> VerifyReceipt(string code, CancellationToken ct)
        {
            var status = await _voteService.GetReceiptStatusAsync(code, ct);

            if (status == null)
                return Ok(new ReceiptVerifyResponse
                {
                    Found       = false,
                    ReceiptCode = code,
                    Status      = "Not Found"
                });

            return Ok(new ReceiptVerifyResponse
            {
                Found       = true,
                ReceiptCode = code,
                Status      = status
            });
        }

        // ── Private: push SignalR updates after a vote ────────────────────────

        /// <summary>
        /// Tells every currently-connected kiosk "this member has voted," so a
        /// kiosk that falls back to its offline cache (see
        /// OfflineCapableVoteRepository.HasMemberVotedAsync) can reject a voter
        /// immediately instead of queuing a vote that would only surface as a
        /// Conflict — with a receipt already printed — hours later at sync time.
        ///
        /// Broadcasts to ElectionHub.KioskGroup (every authenticated kiosk
        /// connection, auto-joined on connect) rather than the per-event group:
        /// this system only ever has one open event at a time (see
        /// GetOpenEventAsync / CachedEvent on the kiosk side), so there is no
        /// real multi-event audience to distinguish, and broadcasting to
        /// KioskGroup means a kiosk doesn't need to call SubscribeToEvent first —
        /// its existing heartbeat connection is already a member.
        ///
        /// Carries ONLY memberId + eventId — never candidate selections — so a
        /// kiosk caching this locally never holds ballot content for a vote it
        /// didn't itself receive.
        ///
        /// IMPORTANT — this does NOT replace the server's UNIQUE constraint as
        /// the authoritative duplicate-vote check. A kiosk that is genuinely
        /// offline cannot receive this broadcast either (no connectivity, full
        /// stop) — that race is still only closed when both kiosks reconnect and
        /// sync. This narrows a different, more common window: a kiosk whose
        /// SignalR connection is up (or was up recently) but whose vote-cast
        /// HTTP call is failing.
        ///
        /// Fire-and-forget like PushLiveUpdatesAsync, for the same reason (must
        /// not block the HTTP response) — but unlike that method, this doesn't
        /// touch the database, so it needs no DI scope of its own; CancellationToken.None
        /// is still used rather than the request's ct, since the request may
        /// already be torn down by the time this actually sends.
        /// </summary>
        private Task PushMemberVotedAsync(int eventId, int memberId)
        {
            try
            {
                return _hub.Clients.Group(ElectionHub.KioskGroup)
                    .SendAsync("MemberVoted", new { eventId, memberId }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "MemberVoted push failed for EventId={EventId} MemberId={MemberId} — vote was still recorded.",
                    eventId, memberId);
                return Task.CompletedTask;
            }
        }

        // ── Live-update coalescing ────────────────────────────────────────
        // Every successful vote used to start its own PushLiveUpdatesCoreAsync,
        // which counts every eligible member, counts voter logs and GROUPs BY
        // every vote cast in the event. That is O(votes so far) work PER VOTE,
        // unbounded in concurrency, each on its own pooled DB connection. In a
        // load test at ~10 voters/s it saturated MySQL CPU, exhausted the
        // connection pool ("All pooled connections are in use") and turned
        // vote and voter-verify requests into 500s once the event passed ~50k
        // votes.
        //
        // The dashboards only need the latest totals, so pushes are coalesced
        // per event: at most one runs at a time and at most one every
        // LivePushMinInterval. Votes that arrive while a push is running or
        // waiting just mark the event "dirty"; the running loop sends one more
        // push with the newest data, so the final state is always delivered.
        private sealed class LivePushState
        {
            public int Running;
            public int Dirty;
        }

        private static readonly ConcurrentDictionary<int, LivePushState> LivePushStates = new();
        private static readonly TimeSpan LivePushMinInterval = TimeSpan.FromSeconds(2);

        private async Task PushLiveUpdatesAsync(int eventId)
        {
            var state = LivePushStates.GetOrAdd(eventId, _ => new LivePushState());
            Volatile.Write(ref state.Dirty, 1);

            // Someone else is already pushing; they will see Dirty and push again.
            if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
                return;

            try
            {
                while (Interlocked.Exchange(ref state.Dirty, 0) == 1)
                {
                    await PushLiveUpdatesCoreAsync(eventId);
                    await Task.Delay(LivePushMinInterval);
                }
            }
            finally
            {
                Volatile.Write(ref state.Running, 0);
            }

            // A vote may have landed between the last Dirty check and Running = 0.
            if (Volatile.Read(ref state.Dirty) == 1)
                _ = PushLiveUpdatesAsync(eventId);
        }

        private async Task PushLiveUpdatesCoreAsync(int eventId)
        {
            // Deliberately fire-and-forget from both call sites above — the
            // HTTP response must not wait on a SignalR broadcast. Which means
            // this method must NOT touch a request-scoped ApiDbContext, and
            // must NOT use the request's CancellationToken: both are torn
            // down as soon as the response finishes, but this method can
            // still be mid-query at that exact moment. The request-scoped
            // MySqlConnection gets closed out from under it — self-contained
            // "Connection must be Open; current state is Closed" here — and,
            // worse, gets handed back to the pool in whatever half-read
            // protocol state it was left in, corrupting it for whichever
            // unrelated request the pool hands that same connection to next
            // ("Packet received out-of-order. Expected 1; got 2." on THEIR
            // otherwise-unrelated query — see offline-sync's 422 rejections
            // during a burst of many queued votes syncing back-to-back).
            // A dedicated scope + CancellationToken.None give this its own
            // connection with a lifetime independent of the request that
            // triggered it, so it can safely finish (or fail on its own,
            // caught below) after the response has already been sent.
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            var ct = CancellationToken.None;

            try
            {
                var group = ElectionHub.EventGroup(eventId);

                // 1. Turnout update (admins + kiosks with a subscription)
                var ev = await db.Events.AsNoTracking()
                    .Include(e => e.EventBranches)
                    .FirstOrDefaultAsync(e => e.EventId == eventId, ct);

                if (ev != null)
                {
                    var branchIds = ev.EventBranches.Select(eb => eb.BranchId).ToList();
                    int eligible  = await db.Members
                        .CountAsync(m => branchIds.Contains(m.BranchId) &&
                                        m.IsEligibleToVote && m.IsActive, ct);
                    int voted = await db.VoterLogs
                        .CountAsync(v => v.EventId == eventId && v.HasVoted, ct);

                    await _hub.Clients.Group(group).SendAsync("TurnoutUpdated", new
                    {
                        eventId,
                        totalEligible  = eligible,
                        totalVoted     = voted,
                        turnoutPercent = eligible > 0
                            ? Math.Round((double)voted / eligible * 100, 2) : 0,
                        updatedAt      = DateTime.UtcNow
                    }, ct);
                }

                // 2. Live results update (admin-only group)
                var votes = await db.Votes
                    .AsNoTracking()
                    .Where(v => v.EventId == eventId)
                    .GroupBy(v => new { v.Position, v.CandidacyId })
                    .Select(g => new
                    {
                        g.Key.Position,
                        g.Key.CandidacyId,
                        Count = g.Count()
                    })
                    .ToListAsync(ct);

                var candidacyIds = votes.Select(v => v.CandidacyId).Distinct().ToList();
                var candidacies  = await db.Candidacies
                    .AsNoTracking()
                    .Include(c => c.Member)
                    .Where(c => candidacyIds.Contains(c.CandidacyId))
                    .ToDictionaryAsync(c => c.CandidacyId, ct);

                var results = votes
                    .GroupBy(v => v.Position)
                    .OrderBy(g => g.Key)
                    .Select(g =>
                    {
                        int total = g.Sum(v => v.Count);
                        return new
                        {
                            position = g.Key,
                            candidates = g
                                .OrderByDescending(v => v.Count)
                                .Select(v => new
                                {
                                    candidacyId   = v.CandidacyId,
                                    candidateName = candidacies.TryGetValue(v.CandidacyId, out var c)
                                        ? c.CandidateDisplayName : "Unknown",
                                    voteCount  = v.Count,
                                    percentage = total > 0
                                        ? Math.Round((double)v.Count / total * 100, 2) : 0
                                }).ToList()
                        };
                    }).ToList();

                // Results go only to the admins group (not to kiosks or voters)
                await _hub.Clients.Group(ElectionHub.AdminGroup).SendAsync(
                    "LiveResultsUpdated",
                    new { eventId, results, updatedAt = DateTime.UtcNow },
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SignalR push failed for EventId={EventId} — vote was still recorded.", eventId);
            }
        }
    }
}
