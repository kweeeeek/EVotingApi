using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Representative;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySqlConnector;
using System.Data;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Casting a Representative ballot — online (voter token) and synchronised from an offline kiosk.
    //
    //  Everything below is decided by the SERVER from the DATABASE; the request carries only the voter's choice
    //  (roster ids). The position of a selection is the position of its roster entry; the rules (min/max per
    //  position), the election's type/state/schedule, the participating branches and the voter's eligibility are
    //  all read here, inside the ballot's own transaction.
    //
    //  The 14 checks, in the order they are applied:
    //    1  the election exists                           8  every candidate is on THIS election's ballot
    //    2  it is a REPRESENTATIVE election               9  every candidate is approved, and its profile active
    //    3  it is Opened                                 10  positions come from the roster, never from the client
    //    4  the schedule allows voting now               11  no candidate is selected twice
    //    5  the voter is authenticated and verified      12  minimum / maximum votes per position are respected
    //    6  the voter is eligible                        13  the voter has not voted already
    //    7  the voter's branch participates              14  a retry cannot record the ballot twice (idempotency)
    //  (3-4 are replaced by "was opened, and was cast inside the window" for a ballot synchronised later.)
    //
    //  Duplicate protection, three layers (the UI is none of them):
    //    layer 1  the voter's voterlogs row is locked (SELECT ... FOR UPDATE) when it already exists; the ballot runs in a READ COMMITTED
    //             transaction, so a row that does not exist yet is NOT gap-locked (see CastWithRetryAsync for why that matters)
    //    layer 2  UNIQUE (MemberId, EventId) on voterlogs - this is what actually serialises two ballots of the same voter
    //    layer 3  UNIQUE (MemberId, EventId, RepresentativeEventCandidateId) on RepresentativeVotes, plus the composite
    //             foreign key (candidate id, event id) that makes the database itself refuse a candidate of another election.
    //  The election row is read with a SHARED lock first, so closing the election, unlocking its roster or editing its rules
    //  waits for ballots in flight and can never overtake one.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    public sealed record OfflineSyncOutcome(
        RepOfflineVoteSyncBulkResponse Response,
        IReadOnlyList<ServiceResult<RepVoteReceiptResponse>?> Items,
        IReadOnlyList<(int EventId, int MemberId)> NewlyAccepted);

    public interface IRepresentativeVoteService
    {
        /// <summary>Online ballot of the voter named by the (already authenticated) voter token.</summary>
        Task<ServiceResult<RepVoteReceiptResponse>> CastAsync(int memberId, int tokenEventId, RepSubmitVoteRequest req, CancellationToken ct);

        /// <summary>
        /// Ballots recorded on a kiosk while it had no connectivity. Each ballot is validated and written independently, in its own
        /// transaction: one rejection never affects the others. A null item means "temporary server problem - retry".
        /// </summary>
        Task<OfflineSyncOutcome> SyncOfflineAsync(IReadOnlyList<RepOfflineVoteSyncRequest> votes, string kioskId, CancellationToken ct);
    }

    public sealed class RepresentativeVoteService : IRepresentativeVoteService
    {
        private const int MaxDeadlockAttempts = 4;

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly IRosterVersionProvider _versions;
        private readonly ILogger<RepresentativeVoteService> _logger;

        public RepresentativeVoteService(ApiDbContext db, IAuditService audit, IRosterVersionProvider versions, ILogger<RepresentativeVoteService> logger)
        {
            _db = db;
            _audit = audit;
            _versions = versions;
            _logger = logger;
        }

        private sealed class CastInput
        {
            public int MemberId;
            public int EventId;
            public List<int> CandidateIds = new();
            public string? ClientReceipt;
            public bool Offline;
            public DateTime CastAt;
            public string? RosterVersion;
        }

        private static ServiceResult<RepVoteReceiptResponse> Fail(string code, string message, string? detail = null) =>
            ServiceResult<RepVoteReceiptResponse>.Failure(RepresentativeHttp.StatusFor(code), message, code, detail);

        private static string? NormalizeReceipt(string? raw) => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Online
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        public async Task<ServiceResult<RepVoteReceiptResponse>> CastAsync(int memberId, int tokenEventId, RepSubmitVoteRequest req, CancellationToken ct)
        {
            // Check 5: the voter token was issued for ONE election; a ballot for any other election is refused whatever the body says.
            if (req.EventId != tokenEventId)
                return Fail(RepCodes.TokenMismatch, "Your voter session belongs to a different election.",
                    "Verify the voter again for the election that is open now.");

            try
            {
                return await CastWithRetryAsync(new CastInput
                {
                    MemberId = memberId,
                    EventId = req.EventId,
                    CandidateIds = (req.CandidateIds ?? new List<int>()).ToList(),
                    ClientReceipt = NormalizeReceipt(req.ReceiptCode),
                    Offline = false,
                    RosterVersion = req.RosterVersion
                }, ct);
            }
            catch (Exception ex) when (IsMySqlError(ex, MySqlErrorCode.LockDeadlock) || IsMySqlError(ex, MySqlErrorCode.LockWaitTimeout))
            {
                // The database stayed too busy for the retry budget. The ballot's transaction was rolled back, so NOTHING was recorded and
                // submitting it again (with the same receipt code, if one was proposed) is safe. Say so instead of an anonymous 500.
                _db.ChangeTracker.Clear();
                _logger.LogError(ex, "Representative ballot not recorded after {Max} attempts (database contention): EventId={EventId} MemberId={MemberId}",
                    MaxDeadlockAttempts, req.EventId, memberId);
                return ServiceResult<RepVoteReceiptResponse>.Failure(StatusCodes.Status503ServiceUnavailable,
                    "The server is busy and did not record this ballot. Nothing was saved - please submit it again in a moment.",
                    RepCodes.TemporaryFailure);
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Offline sync
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        public async Task<OfflineSyncOutcome> SyncOfflineAsync(IReadOnlyList<RepOfflineVoteSyncRequest> votes, string kioskId, CancellationToken ct)
        {
            var label = SafeLabel(kioskId);
            var response = new RepOfflineVoteSyncBulkResponse();
            var items = new List<ServiceResult<RepVoteReceiptResponse>?>(votes.Count);
            var accepted = new List<(int, int)>();
            int firstEventId = votes.Count > 0 ? votes[0].EventId : 0;

            foreach (var v in votes)
            {
                ct.ThrowIfCancellationRequested();
                var receipt = NormalizeReceipt(v.ReceiptCode);
                ServiceResult<RepVoteReceiptResponse>? result = null;
                try
                {
                    result = await CastWithRetryAsync(new CastInput
                    {
                        MemberId = v.MemberId,
                        EventId = v.EventId,
                        CandidateIds = (v.CandidateIds ?? new List<int>()).ToList(),
                        ClientReceipt = receipt,
                        Offline = true,
                        CastAt = v.CastAtUtc,
                        RosterVersion = v.RosterVersion
                    }, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // Not a considered business answer (connection lost, deadlocks beyond the retry budget ...): the kiosk must keep the
                    // ballot and retry, never discard a legitimately cast ballot over a hiccup.
                    _db.ChangeTracker.Clear();
                    _logger.LogError(ex, "Representative offline sync error (will be retried): EventId={EventId} Receipt={Receipt}", v.EventId, receipt);
                }
                items.Add(result);

                var item = new RepOfflineVoteItemResult { ReceiptCode = v.ReceiptCode ?? "" };
                if (result == null)
                {
                    item.Success = false;
                    item.IsPermanentRejection = false;
                    item.Message = "A temporary server error occurred - this ballot will be retried automatically.";
                    response.RetryLater++;
                }
                else if (result.Ok)
                {
                    item.Success = true;
                    item.AlreadyProcessed = result.Value!.AlreadyProcessed;
                    item.RosterChanged = result.Value.RosterChanged;
                    item.Message = result.Value.Message;
                    if (item.AlreadyProcessed) response.Replayed++;
                    else { response.Accepted++; accepted.Add((v.EventId, v.MemberId)); }
                }
                else
                {
                    item.Success = false;
                    item.IsPermanentRejection = RepresentativeHttp.IsPermanent(result.Code);
                    item.Code = result.Code;
                    item.Message = result.Error ?? "The ballot was rejected.";
                    response.Rejected++;
                    _logger.LogWarning("Representative offline ballot REJECTED: Kiosk={Kiosk} EventId={EventId} Receipt={Receipt} Code={Code}",
                        label, v.EventId, receipt, result.Code);
                    await TryAuditAsync(AuditActions.RepOfflineRejected, v.EventId,
                        $"kiosk={label}; receipt={receipt ?? "(none)"}; {result.Code}: {result.Error}", label, ct);
                }
                response.Results.Add(item);
            }

            if (votes.Count > 0)
                await TryAuditAsync(AuditActions.RepOfflineSynced, firstEventId,
                    $"kiosk={label}; received={votes.Count}; accepted={response.Accepted}; replayed={response.Replayed}; rejected={response.Rejected}; retry={response.RetryLater}",
                    label, ct);

            return new OfflineSyncOutcome(response, items, accepted);
        }

        private async Task TryAuditAsync(string action, int eventId, string details, string kioskLabel, CancellationToken ct)
        {
            try
            {
                _db.ChangeTracker.Clear();
                await _audit.WriteAsync(action, AuditEntities.Event, eventId, details, null, $"kiosk:{kioskLabel}", ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write the {Action} audit row for EventId={EventId}", action, eventId);
            }
        }

        /// <summary>A caller-supplied kiosk id, reduced to harmless characters before it reaches a log line or the 50-character audit column.</summary>
        internal static string SafeLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "(unknown)";
            var s = raw.Trim();
            if (s.Length > 32) s = s.Substring(0, 32);
            return new string(s.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' ? ch : '?').ToArray());
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  The ballot itself
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        // Why READ COMMITTED: under REPEATABLE READ, "SELECT ... FOR UPDATE" on a voterlogs row that does not exist yet takes a GAP lock, and
        // two ballots of DIFFERENT voters whose keys fall into the same gap then block each other's INSERT - MySQL aborts one with error 1213.
        // The first end-to-end test (40 voters at the same instant) hit exactly that: 49 first-attempt deadlocks and one ballot that ran
        // out of retries. READ COMMITTED takes no gap locks, so different voters no longer collide; the same voter still serialises on
        // UNIQUE (MemberId, EventId). A deadlock can still happen (two submissions of ONE voter with different selections), so the ballot
        // is retried: it is one transaction that was rolled back, nothing was written, and every duplicate / replay check runs again.
        private async Task<ServiceResult<RepVoteReceiptResponse>> CastWithRetryAsync(CastInput x, CancellationToken ct)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await CastOnceAsync(x, ct);
                }
                catch (Exception ex) when (attempt < MaxDeadlockAttempts && IsMySqlError(ex, MySqlErrorCode.LockDeadlock))
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogWarning("Deadlock while recording a Representative ballot (attempt {Attempt}/{Max}); retrying.", attempt, MaxDeadlockAttempts);
                    await Task.Delay(Random.Shared.Next(15, 60) * attempt, ct);
                }
            }
        }

        private static bool IsMySqlError(Exception? ex, MySqlErrorCode code)
        {
            for (; ex != null; ex = ex.InnerException)
                if (ex is MySqlException my && my.ErrorCode == code) return true;
            return false;
        }

        private static async Task SafeRollbackAsync(IDbContextTransaction tx)
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* the connection may already be gone; the original error matters more */ }
        }

        private async Task<ServiceResult<RepVoteReceiptResponse>> CastOnceAsync(CastInput x, CancellationToken ct)
        {
            // Checks that need no database (a malformed request never opens a transaction).
            if (x.CandidateIds.Count == 0) return Fail(RepCodes.NoSelection, "Select at least one candidate.");
            if (x.CandidateIds.Count > RepresentativeLimits.MaxSelectionsPerRequest)
                return Fail(RepCodes.TooManySelections, "The ballot contains too many selections.");
            if (x.CandidateIds.Distinct().Count() != x.CandidateIds.Count)
                return Fail(RepCodes.DuplicateSelection, "The same candidate was selected more than once.");
            if (x.ClientReceipt != null && !ReceiptCodeFormat.IsValid(x.ClientReceipt, x.EventId))
                return Fail(RepCodes.InvalidReceipt, "The receipt code must look like EVT{event}-{8 hex}-{8 hex} and belong to this election.");
            if (x.Offline && x.ClientReceipt == null)
                return Fail(RepCodes.InvalidReceipt, "An offline ballot must carry the receipt code that was given to the voter.");

            // Entities left over from an earlier ballot of the same request (a bulk sync) must not shadow the fresh rows read below.
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            try
            {
                // Check 1-2: the election, read with a SHARED lock (see the header: close/unlock/rule edits wait for ballots in flight).
                var ev = (await _db.Events
                        .FromSqlRaw("SELECT * FROM Events WHERE EventId = {0} AND IsActive = 1 FOR SHARE", x.EventId)
                        .AsNoTracking().ToListAsync(ct)).FirstOrDefault();
                if (ev == null)
                {
                    await SafeRollbackAsync(tx);
                    return Fail(RepCodes.EventNotFound, "Election not found.");
                }
                if (ev.EventType != EventTypes.Representative)
                {
                    await SafeRollbackAsync(tx);
                    return Fail(RepCodes.WrongEventType, "This is a General Assembly election; its ballot is submitted to the General Assembly voting endpoint.");
                }

                // Layer 1: the voter's own log row, locked when it exists. When it does not exist yet nothing is locked (READ COMMITTED);
                // two simultaneous ballots of this voter then meet at the UNIQUE index (layer 2), and the loser is told what is true now.
                var log = (await _db.VoterLogs
                        .FromSqlRaw("SELECT * FROM voterlogs WHERE MemberId = {0} AND EventId = {1} FOR UPDATE", x.MemberId, x.EventId)
                        .ToListAsync(ct)).FirstOrDefault();

                // Check 13 + 14: already voted. The SAME receipt is a safe retry; anything else is a genuine second ballot.
                if (log?.HasVoted == true)
                {
                    var count = await _db.RepresentativeVotes.AsNoTracking().CountAsync(v => v.MemberId == x.MemberId && v.EventId == x.EventId, ct);
                    await SafeRollbackAsync(tx);
                    if (x.ClientReceipt != null && string.Equals(log.ReceiptCode, x.ClientReceipt, StringComparison.Ordinal))
                        return ServiceResult<RepVoteReceiptResponse>.Success(Receipt(x.EventId, log.ReceiptCode!, log.VotedAt ?? DateTime.UtcNow, count, replay: true));
                    return Fail(RepCodes.AlreadyVoted, "This member has already voted in this election.");
                }

                // Check 3-4: Opened and inside the schedule (online) / was opened and cast inside the window (offline).
                var nowUtc = DateTime.UtcNow;
                var time = x.Offline
                    ? RepresentativeCastTime.CheckOffline(ev.RosterLockedAt != null, ev.ScheduleFrom, ev.ScheduleTo, x.CastAt, nowUtc)
                    : RepresentativeCastTime.CheckOnline(ev.Status, ev.ScheduleFrom, ev.ScheduleTo, nowUtc);
                if (!time.Ok)
                {
                    await SafeRollbackAsync(tx);
                    return Fail(time.Code!, time.Message!);
                }

                // Check 5-7: the voter is a real, active, eligible member of a participating branch (re-checked now; the token proves only that
                // the member was verified a few minutes ago, and the kiosk of an offline ballot cannot be trusted with these facts).
                var member = await _db.Members.AsNoTracking().Where(m => m.MemberId == x.MemberId)
                    .Select(m => new { m.IsActive, m.IsEligibleToVote, m.BranchId }).FirstOrDefaultAsync(ct);
                if (member == null || !member.IsActive)
                {
                    await SafeRollbackAsync(tx);
                    return Fail(RepCodes.MemberNotFound, "Member not found or inactive.");
                }
                if (!member.IsEligibleToVote)
                {
                    await SafeRollbackAsync(tx);
                    return Fail(RepCodes.NotEligible, "Member is not eligible to vote.");
                }
                var branchParticipates = await _db.EventBranches.AsNoTracking().AnyAsync(b => b.EventId == x.EventId && b.BranchId == member.BranchId, ct);
                if (!branchParticipates)
                {
                    await SafeRollbackAsync(tx);
                    return Fail(RepCodes.BranchNotParticipating, "The member's branch is not participating in this election.");
                }

                // Check 8-12: the ballot against the roster and rules in the DATABASE.
                var ids = x.CandidateIds;
                var found = await _db.RepresentativeEventCandidates.AsNoTracking()
                    .Where(c => ids.Contains(c.RepresentativeEventCandidateId))
                    .Select(c => new { c.RepresentativeEventCandidateId, c.EventId, c.Position, c.Status, ProfileActive = c.Profile!.IsActive })
                    .ToListAsync(ct);
                var byId = found.ToDictionary(c => c.RepresentativeEventCandidateId,
                    c => new BallotCandidate(c.RepresentativeEventCandidateId, c.EventId, c.Position, c.Status, c.ProfileActive));
                var rules = (await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == x.EventId).OrderBy(r => r.Id).ToListAsync(ct))
                    .Select(r => new BallotRule(r.Position, r.MinVotes, r.MaxVotes, r.TotalSeats, r.MaxCandidates)).ToList();
                var availableRows = await _db.RepresentativeEventCandidates.AsNoTracking()
                    .Where(c => c.EventId == x.EventId && c.Status == RepresentativeCandidateStatuses.Approved && c.Profile!.IsActive)
                    .GroupBy(c => c.Position).Select(g => new { Position = g.Key, Count = g.Count() }).ToListAsync(ct);
                var available = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in availableRows) available[a.Position.Trim()] = available.GetValueOrDefault(a.Position.Trim()) + a.Count;

                var check = RepresentativeBallotRules.Check(x.EventId, ids, byId, rules, available);
                if (!check.Ok)
                {
                    await SafeRollbackAsync(tx);
                    return Fail(check.Code!, check.Message!);
                }

                // Check 14: a client-proposed receipt must not already belong to somebody else's ballot.
                var receipt = x.ClientReceipt ?? ReceiptCodeFormat.Generate(x.EventId);
                if (x.ClientReceipt != null)
                {
                    var owner = await _db.RepresentativeVotes.AsNoTracking().Where(v => v.ReceiptCode == receipt)
                        .Select(v => new { v.MemberId, v.EventId }).FirstOrDefaultAsync(ct);
                    if (owner != null)
                    {
                        await SafeRollbackAsync(tx);
                        _db.ChangeTracker.Clear();
                        // READ COMMITTED: the original submission of THIS voter can have committed between the voter-log read above and this
                        // read. Then the receipt is the voter's own, the request is a retry that raced the original, and it is answered exactly
                        // like any other retry (the stored ballot's receipt) - not as a clash with somebody else's ballot.
                        if (owner.MemberId == x.MemberId && owner.EventId == x.EventId)
                            return await ResolveDuplicateAsync(x, ct);
                        return Fail(RepCodes.ReceiptInUse, "That receipt code is already in use by another ballot.");
                    }
                }

                // Write: one row per selection + the voter log, committed together.
                var votedAt = x.Offline ? time.CastAtUtc : nowUtc;
                foreach (var id in ids.OrderBy(i => i))       // same order in every transaction: two submissions of one voter cannot deadlock on row order
                    _db.RepresentativeVotes.Add(new RepresentativeVoteEntity
                    {
                        MemberId = x.MemberId, EventId = x.EventId, RepresentativeEventCandidateId = id, VotedAt = votedAt, ReceiptCode = receipt
                    });
                if (log == null)
                    _db.VoterLogs.Add(new VoterLogEntity { MemberId = x.MemberId, EventId = x.EventId, HasVoted = true, VotedAt = votedAt, ReceiptCode = receipt });
                else
                {
                    log.HasVoted = true;
                    log.VotedAt = votedAt;
                    log.ReceiptCode = receipt;
                }
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger.LogInformation("Representative ballot committed: EventId={EventId} Receipt={Receipt} Selections={Count} Offline={Offline}",
                    x.EventId, receipt, ids.Count, x.Offline);

                var response = Receipt(x.EventId, receipt, votedAt, ids.Count, replay: false);
                response.RosterChanged = await RosterChangedAsync(ev, x.RosterVersion, ct);
                return ServiceResult<RepVoteReceiptResponse>.Success(response);
            }
            catch (DbUpdateException ex) when (IsMySqlError(ex, MySqlErrorCode.DuplicateKeyEntry))
            {
                // Layers 2/3 fired: another ballot for this voter slipped in. Report what is true now.
                await SafeRollbackAsync(tx);
                _db.ChangeTracker.Clear();
                return await ResolveDuplicateAsync(x, ct);
            }
            catch (DbUpdateException ex) when (IsMySqlError(ex, MySqlErrorCode.NoReferencedRow2) || IsMySqlError(ex, MySqlErrorCode.NoReferencedRow))
            {
                // The composite foreign key refused a candidate that is not on this election's roster (belt and braces behind check 8).
                await SafeRollbackAsync(tx);
                _db.ChangeTracker.Clear();
                return Fail(RepCodes.CandidateWrongEvent, "A selected candidate is not on the ballot of this election.");
            }
            catch
            {
                await SafeRollbackAsync(tx);
                throw;
            }
        }

        private async Task<ServiceResult<RepVoteReceiptResponse>> ResolveDuplicateAsync(CastInput x, CancellationToken ct)
        {
            var log = await _db.VoterLogs.AsNoTracking().FirstOrDefaultAsync(v => v.MemberId == x.MemberId && v.EventId == x.EventId && v.HasVoted, ct);
            if (log != null && x.ClientReceipt != null && string.Equals(log.ReceiptCode, x.ClientReceipt, StringComparison.Ordinal))
            {
                var count = await _db.RepresentativeVotes.AsNoTracking().CountAsync(v => v.MemberId == x.MemberId && v.EventId == x.EventId, ct);
                return ServiceResult<RepVoteReceiptResponse>.Success(Receipt(x.EventId, log.ReceiptCode!, log.VotedAt ?? DateTime.UtcNow, count, replay: true));
            }
            return Fail(RepCodes.AlreadyVoted, "This member has already voted in this election.");
        }

        private async Task<bool> RosterChangedAsync(EventEntity ev, string? clientVersion, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientVersion)) return false;
            try { return !string.Equals(await _versions.GetAsync(ev, ct), clientVersion.Trim(), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }      // informational only; never let it affect a committed ballot
        }

        private static RepVoteReceiptResponse Receipt(int eventId, string code, DateTime votedAt, int selections, bool replay) => new()
        {
            EventId = eventId,
            ReceiptCode = code,
            VotedAt = votedAt,
            SelectionCount = selections,
            AlreadyProcessed = replay,
            Message = replay ? "This ballot was already recorded - no action needed." : "Your ballot has been recorded successfully."
        };
    }
}
