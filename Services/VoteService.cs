using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Requests;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Security.Cryptography;

namespace EVoting.Api.Services
{
    public interface IVoteService
    {
        Task<string> CastVotesAsync(
            int memberId,
            int eventId,
            List<VoteSelectionItem> selections,
            CancellationToken ct);

        Task<bool> HasVotedAsync(
            int memberId,
            int eventId,
            CancellationToken ct);

        Task<string?> GetReceiptStatusAsync(
            string receiptCode,
            CancellationToken ct);

        /// <summary>
        /// Records a vote that was already cast on a kiosk while it had no
        /// connectivity to this API. Called later, once the kiosk reconnects,
        /// under the KIOSK's own token rather than a per-voter one.
        ///
        /// Two things make this different from CastVotesAsync:
        ///   1. clientReceiptCode/castAtUtc come FROM THE KIOSK, not generated
        ///      here — the voter already has this exact code on a printed
        ///      receipt / SMS from the moment they voted, so the server record
        ///      must match it instead of minting a new one.
        ///   2. If this exact receipt code is already recorded (the kiosk
        ///      retrying a sync whose first attempt actually succeeded but
        ///      whose response the kiosk never received), that is treated as
        ///      a safe replay — not a conflict.
        /// </summary>
        Task<(string ReceiptCode, bool WasReplay)> CastOfflineVoteAsync(
            int memberId,
            int eventId,
            List<VoteSelectionItem> selections,
            string clientReceiptCode,
            DateTime castAtUtc,
            CancellationToken ct);


    }

    public class VoteService : IVoteService
    {
        private readonly ApiDbContext _db;
        private readonly ILogger<VoteService> _logger;

        public VoteService(
            ApiDbContext db,
            ILogger<VoteService> logger)
        {
            _db = db;
            _logger = logger;
        }

        private static readonly TimeZoneInfo ElectionTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");

        private static readonly TimeSpan OfflineClockSkewTolerance =
            TimeSpan.FromMinutes(5);

        public async Task<bool> HasVotedAsync(
            int memberId,
            int eventId,
            CancellationToken ct) =>
            await _db.VoterLogs
                .AnyAsync(
                    v =>
                        v.MemberId == memberId &&
                        v.EventId == eventId &&
                        v.HasVoted,
                    ct);

        public async Task<string?> GetReceiptStatusAsync(
            string receiptCode,
            CancellationToken ct)
        {
            var log = await _db.VoterLogs
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    v => v.ReceiptCode == receiptCode,
                    ct);

            if (log == null)
                return null;

            return log.HasVoted ? "Processed" : "Pending";
        }

        /// <summary>
        /// Atomically cast all votes for one member in one transaction.
        ///
        /// Duplicate-vote safety:
        ///   Layer 1 — Pre-flight HasVotedAsync() check exists at the caller level.
        ///   Layer 2 — SELECT FOR UPDATE inside REPEATABLE READ transaction.
        ///   Layer 3 — UNIQUE constraint on the vote table.
        ///
        /// Receipt code uses CSPRNG.
        /// </summary>
        public async Task<string> CastVotesAsync(
            int memberId,
            int eventId,
            List<VoteSelectionItem> selections,
            CancellationToken ct)
        {
            if (selections == null || selections.Count == 0)
                throw new ArgumentException(
                    "No candidates selected.",
                    nameof(selections));

            var receiptCode = GenerateReceiptCode(eventId);
            var now = DateTime.UtcNow;

            await using var tx = await _db.Database
                .BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    ct);

            try
            {
                // Exclusive row lock for the same member + event.
                var logs = await _db.VoterLogs
                    .FromSqlRaw(
                        "SELECT * FROM voterlogs " +
                        "WHERE MemberId = {0} AND EventId = {1} FOR UPDATE",
                        memberId,
                        eventId)
                    .ToListAsync(ct);

                var log = logs.FirstOrDefault();

                if (log?.HasVoted == true)
                    throw new InvalidOperationException(
                        "Member has already voted in this event.");

                var ev = await LoadActiveEventOrThrowAsync(
                    eventId,
                    ct);

                await ValidateSelectionsOrThrowAsync(
                    selections,
                    ev,
                    ct);

                foreach (var sel in selections)
                {
                    _db.Votes.Add(new VoteEntity
                    {
                        MemberId = memberId,
                        EventId = eventId,
                        CandidacyId = sel.CandidacyId,
                        Position = sel.Position,
                        VotedAt = now,
                        ReceiptCode = receiptCode
                    });
                }

                if (log == null)
                {
                    _db.VoterLogs.Add(new VoterLogEntity
                    {
                        MemberId = memberId,
                        EventId = eventId,
                        HasVoted = true,
                        VotedAt = now,
                        ReceiptCode = receiptCode
                    });
                }
                else
                {
                    log.HasVoted = true;
                    log.VotedAt = now;
                    log.ReceiptCode = receiptCode;
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger.LogInformation(
                    "Vote committed: MemberId={MemberId} " +
                    "EventId={EventId} Receipt={Receipt} " +
                    "Selections={Count}",
                    memberId,
                    eventId,
                    receiptCode,
                    selections.Count);

                return receiptCode;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        /// <summary>
        /// Records a vote cast earlier on a kiosk that had no connectivity.
        ///
        /// Uses the same row-locking strategy as CastVotesAsync and performs
        /// fresh member/event/candidate validation because this endpoint is
        /// authenticated as the kiosk rather than the voter.
        /// </summary>
        public async Task<(string ReceiptCode, bool WasReplay)> CastOfflineVoteAsync(
            int memberId,
            int eventId,
            List<VoteSelectionItem> selections,
            string clientReceiptCode,
            DateTime castAtUtc,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientReceiptCode))
            {
                throw new ArgumentException(
                    "A receipt code from the kiosk is required.",
                    nameof(clientReceiptCode));
            }

            if (selections == null || selections.Count == 0)
            {
                throw new ArgumentException(
                    "No candidates selected.",
                    nameof(selections));
            }

            // Guard against invalid kiosk timestamps.
            /*var castAt =
                (castAtUtc == default || castAtUtc > DateTime.UtcNow)
                    ? DateTime.UtcNow
                    : castAtUtc;*/

            await using var tx = await _db.Database
                .BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    ct);

            try
            {
                // Exclusive row lock prevents race conditions between:
                // - online vote
                // - offline sync
                // - another sync for the same member + event
                var logs = await _db.VoterLogs
                    .FromSqlRaw(
                        "SELECT * FROM voterlogs " +
                        "WHERE MemberId = {0} AND EventId = {1} FOR UPDATE",
                        memberId,
                        eventId)
                    .ToListAsync(ct);

                var log = logs.FirstOrDefault();

                if (log?.HasVoted == true)
                {
                    // Same receipt means the original sync succeeded but
                    // the kiosk did not receive the response.
                    if (string.Equals(
                            log.ReceiptCode,
                            clientReceiptCode,
                            StringComparison.Ordinal))
                    {
                        // This is a read-only replay path.
                        await tx.RollbackAsync(ct);

                        // IMPORTANT:
                        // ReceiptCode is nullable in the entity, but this
                        // method guarantees a non-null string. Therefore we
                        // explicitly enforce the data-integrity invariant.
                        if (string.IsNullOrWhiteSpace(log.ReceiptCode))
                        {
                            _logger.LogError(
                                "Voter log integrity error: " +
                                "HasVoted=true but ReceiptCode is missing. " +
                                "MemberId={MemberId} EventId={EventId}",
                                memberId,
                                eventId);

                            throw new InvalidOperationException(
                                "Existing voter record is invalid because " +
                                "its receipt code is missing.");
                        }

                        _logger.LogInformation(
                            "Offline vote replay recognized (already synced): " +
                            "MemberId={MemberId} EventId={EventId} Receipt={Receipt}",
                            memberId,
                            eventId,
                            clientReceiptCode);

                        return (
                            log.ReceiptCode,
                            true);
                    }

                    // Different receipt = genuine cross-device conflict.
                    throw new InvalidOperationException(
                        "Member has already voted in this event.");
                }

                // Re-validate member eligibility.
                var member = await _db.Members
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        m =>
                            m.MemberId == memberId &&
                            m.IsActive,
                        ct);

                if (member == null)
                {
                    throw new InvalidOperationException(
                        "Member not found or inactive.");
                }

                if (!member.IsEligibleToVote)
                {
                    throw new InvalidOperationException(
                        "Member is not eligible to vote.");
                }

                // Validate event.
                var ev = await LoadActiveEventOrThrowAsync(
                    eventId,
                    ct);

               /* ValidateOnlineVotingWindowOrThrow(
                    ev, now);*/

                var serverNowUtc = DateTime.UtcNow;

                var castAt = ValidateOfflineCastTimeOrThrow(
                    ev,
                    castAtUtc,
                    serverNowUtc);

                // Validate branch assignment.
                var branchAllowed = await _db.EventBranches
                    .AnyAsync(
                        eb =>
                            eb.EventId == eventId &&
                            eb.BranchId == member.BranchId,
                        ct);

                if (!branchAllowed)
                {
                    throw new InvalidOperationException(
                        "Member's branch is not assigned to this election.");
                }

                // Validate candidates/selections.
                await ValidateSelectionsOrThrowAsync(
                    selections,
                    ev,
                    ct);

                // Insert votes.
                foreach (var sel in selections)
                {
                    _db.Votes.Add(new VoteEntity
                    {
                        MemberId = memberId,
                        EventId = eventId,
                        CandidacyId = sel.CandidacyId,
                        Position = sel.Position,
                        VotedAt = castAt,
                        ReceiptCode = clientReceiptCode
                    });
                }

                // Insert/update voter log.
                if (log == null)
                {
                    _db.VoterLogs.Add(new VoterLogEntity
                    {
                        MemberId = memberId,
                        EventId = eventId,
                        HasVoted = true,
                        VotedAt = castAt,
                        ReceiptCode = clientReceiptCode
                    });
                }
                else
                {
                    log.HasVoted = true;
                    log.VotedAt = castAt;
                    log.ReceiptCode = clientReceiptCode;
                }

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                _logger.LogInformation(
                    "Offline vote synced: MemberId={MemberId} " +
                    "EventId={EventId} Receipt={Receipt} " +
                    "CastAt={CastAt} Selections={Count}",
                    memberId,
                    eventId,
                    clientReceiptCode,
                    castAt,
                    selections.Count);

                return (
                    clientReceiptCode,
                    false);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        // ── Shared validation ─────────────────────────────────────────────
        private static void ValidateOnlineVotingWindowOrThrow(
            EventEntity ev,
            DateTime serverNowUtc)
                {
                    if (!ev.IsActive)
                    {
                        throw new InvalidOperationException(
                            $"Event {ev.EventId} is inactive.");
                    }

                    if (!string.Equals(
                            ev.Status,
                            "Opened",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Voting is not currently open for this event.");
                    }

                    var scheduleFromUtc = ConvertElectionLocalToUtc(ev.ScheduleFrom);
                    var scheduleToUtc = ConvertElectionLocalToUtc(ev.ScheduleTo);

                    if (scheduleToUtc <= scheduleFromUtc)
                    {
                        throw new InvalidOperationException(
                            $"Event {ev.EventId} has an invalid voting schedule.");
                    }

                    if (serverNowUtc < scheduleFromUtc)
                    {
                        throw new InvalidOperationException(
                            "Voting has not started yet for this event.");
                    }

                    if (serverNowUtc >= scheduleToUtc)
                    {
                        throw new InvalidOperationException(
                            "Voting has already closed for this event.");
                    }
                }

        private static DateTime ValidateOfflineCastTimeOrThrow(
            EventEntity ev,
            DateTime castAtUtc,
            DateTime serverNowUtc)
        {
            if (castAtUtc == default)
            {
                throw new ArgumentException(
                    "The offline vote does not contain a valid cast timestamp.",
                    nameof(castAtUtc));
            }

            // Always compare UTC-to-UTC.
            // A JSON timestamp ending in Z should normally arrive as Utc,
            // but Normalize explicitly so Kind cannot cause accidental conversion.
            var normalizedCastAtUtc = castAtUtc.Kind switch
            {
                DateTimeKind.Utc =>
                    castAtUtc,

                DateTimeKind.Local =>
                    castAtUtc.ToUniversalTime(),

                _ =>
                    DateTime.SpecifyKind(
                        castAtUtc,
                        DateTimeKind.Utc)
            };

            // Allow a small device/server clock difference, but reject
            // materially future-dated votes.
            if (normalizedCastAtUtc >
                serverNowUtc.Add(OfflineClockSkewTolerance))
            {
                throw new ArgumentException(
                    "The offline vote timestamp is in the future. " +
                    "The vote cannot be accepted.",
                    nameof(castAtUtc));
            }

            var scheduleFromUtc = ConvertElectionLocalToUtc(ev.ScheduleFrom);
            var scheduleToUtc = ConvertElectionLocalToUtc(ev.ScheduleTo);

            if (scheduleToUtc <= scheduleFromUtc)
            {
                throw new InvalidOperationException(
                    $"Event {ev.EventId} has an invalid voting schedule.");
            }

            if (normalizedCastAtUtc < scheduleFromUtc)
            {
                throw new InvalidOperationException(
                    "The offline vote was cast before the voting window opened.");
            }

            if (normalizedCastAtUtc >= scheduleToUtc)
            {
                throw new InvalidOperationException(
                    "The offline vote was cast after the voting window closed.");
            }

            return normalizedCastAtUtc;
        }

        private static DateTime ConvertElectionLocalToUtc(DateTime electionLocalTime)
        {
            // ScheduleFrom/ScheduleTo are stored as local election wall-clock time.
            // Treat them explicitly as UTC+8 election time before conversion.
            var unspecified = DateTime.SpecifyKind(
                electionLocalTime,
                DateTimeKind.Unspecified);

            return TimeZoneInfo.ConvertTimeToUtc(
                unspecified,
                ElectionTimeZone);
        }



        private async Task<EventEntity> LoadActiveEventOrThrowAsync(
            int eventId,
            CancellationToken ct)
        {
            var ev = await _db.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    e =>
                        e.EventId == eventId &&
                        e.IsActive,
                    ct);

            if (ev == null)
            {
                throw new ArgumentException(
                    $"Event {eventId} not found or inactive.",
                    nameof(eventId));
            }

            return ev;
        }

        private async Task ValidateSelectionsOrThrowAsync(
             List<VoteSelectionItem> selections,
             EventEntity ev,
             CancellationToken ct)
                {
                    if (selections == null || selections.Count == 0)
                    {
                        throw new ArgumentException(
                            "No candidates selected.",
                            nameof(selections));
                    }

                    // ---------------------------------------------------------------------
                    // 1. Load and validate the event's authoritative position rules.
                    // ---------------------------------------------------------------------
                    var rules = await _db.EventPositionRules
                        .AsNoTracking()
                        .Where(r => r.EventId == ev.EventId)
                        .ToListAsync(ct);

                    if (rules.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"Event {ev.EventId} has no position rules configured. " +
                            "Voting is disabled until valid position rules are configured.");
                    }

                    // Position names are treated case-insensitively.
                    var rulesByPosition = new Dictionary<string, EventPositionRuleEntity>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var rule in rules)
                    {
                        var position = rule.Position?.Trim();

                        if (string.IsNullOrWhiteSpace(position))
                        {
                            throw new InvalidOperationException(
                                $"Event {ev.EventId} contains a position rule with an empty position.");
                        }

                        if (rule.MinVotes < 0)
                        {
                            throw new InvalidOperationException(
                                $"Invalid position rule for '{position}': MinVotes cannot be negative.");
                        }

                        if (rule.MaxVotes < rule.MinVotes)
                        {
                            throw new InvalidOperationException(
                                $"Invalid position rule for '{position}': " +
                                $"MaxVotes ({rule.MaxVotes}) cannot be less than " +
                                $"MinVotes ({rule.MinVotes}).");
                        }

                        if (rule.TotalSeats < 1)
                        {
                            throw new InvalidOperationException(
                                $"Invalid position rule for '{position}': " +
                                "TotalSeats must be at least 1.");
                        }

                        if (!rulesByPosition.TryAdd(position, rule))
                        {
                            throw new InvalidOperationException(
                                $"Event {ev.EventId} contains duplicate position rules for '{position}'.");
                        }
                    }

                    // ---------------------------------------------------------------------
                    // 2. Reject duplicate candidacy IDs in the submitted ballot.
                    // ---------------------------------------------------------------------
                    var duplicateCandidacyIds = selections
                        .GroupBy(s => s.CandidacyId)
                        .Where(g => g.Count() > 1)
                        .Select(g => g.Key)
                        .ToList();

                    if (duplicateCandidacyIds.Count > 0)
                    {
                        throw new ArgumentException(
                            "The submitted ballot contains duplicate candidate selections.",
                            nameof(selections));
                    }

                    // ---------------------------------------------------------------------
                    // 3. Load all submitted candidacies in one query.
                    // ---------------------------------------------------------------------
                    var submittedIds = selections
                        .Select(s => s.CandidacyId)
                        .Distinct()
                        .ToList();

                    var candidacies = await _db.Candidacies
                        .AsNoTracking()
                        .Where(c =>
                            submittedIds.Contains(c.CandidacyId)
                            && c.Status == "Approved"
                            && c.IsActive
                            && (
                                c.DateApproved != null
                                    ? c.DateApproved >= ev.ScheduleFrom.AddDays(-180)
                                    : c.DateFiled >= ev.ScheduleFrom.AddDays(-180)
                            ))
                        .ToListAsync(ct);

                    if (candidacies.Count != submittedIds.Count)
                    {
                        throw new ArgumentException(
                            "One or more selected candidates are invalid, " +
                            "not approved, inactive, or no longer eligible for this election.",
                            nameof(selections));
                    }

                    var candidacyById = candidacies.ToDictionary(c => c.CandidacyId);

                    // ---------------------------------------------------------------------
                    // 4. Validate every submitted selection:
                    //    - Position must exist in EventPositionRules.
                    //    - Submitted Position must match the actual Candidacy.Position.
                    // ---------------------------------------------------------------------
                    foreach (var selection in selections)
                    {
                        var submittedPosition = selection.Position?.Trim();

                        if (string.IsNullOrWhiteSpace(submittedPosition))
                        {
                            throw new ArgumentException(
                                $"Selection for CandidacyId={selection.CandidacyId} " +
                                "does not contain a position.",
                                nameof(selections));
                        }

                        if (!rulesByPosition.ContainsKey(submittedPosition))
                        {
                            throw new ArgumentException(
                                $"Position '{submittedPosition}' is not configured " +
                                $"for Event {ev.EventId}.",
                                nameof(selections));
                        }

                        if (!candidacyById.TryGetValue(
                                selection.CandidacyId,
                                out var candidacy))
                        {
                            throw new ArgumentException(
                                $"CandidacyId={selection.CandidacyId} is invalid.",
                                nameof(selections));
                        }

                        var actualPosition = candidacy.Position?.Trim();

                        if (!string.Equals(
                                submittedPosition,
                                actualPosition,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new ArgumentException(
                                $"CandidacyId={selection.CandidacyId} belongs to position " +
                                $"'{actualPosition}', but the submitted ballot specified " +
                                $"'{submittedPosition}'.",
                                nameof(selections));
                        }
                    }

                    // ---------------------------------------------------------------------
                    // 5. Count selections per position.
                    // ---------------------------------------------------------------------
                    var submittedCountsByPosition = selections
                        .GroupBy(
                            s => s.Position.Trim(),
                            StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => g.Count(),
                            StringComparer.OrdinalIgnoreCase);

                    // ---------------------------------------------------------------------
                    // 6. Enforce MinVotes / MaxVotes for EVERY configured position.
                    //
                    // This is intentionally fail-closed. A client cannot omit a required
                    // position simply by excluding it from the submitted payload.
                    // ---------------------------------------------------------------------
                    foreach (var rule in rulesByPosition.Values)
                    {
                        submittedCountsByPosition.TryGetValue(
                            rule.Position.Trim(),
                            out var count);

                        if (count < rule.MinVotes)
                        {
                            throw new ArgumentException(
                                $"Position '{rule.Position}' requires at least " +
                                $"{rule.MinVotes} vote(s), but only {count} were submitted.",
                                nameof(selections));
                        }

                        if (count > rule.MaxVotes)
                        {
                            throw new ArgumentException(
                                $"Position '{rule.Position}' allows at most " +
                                $"{rule.MaxVotes} vote(s), but {count} were submitted.",
                                nameof(selections));
                        }
                    }
        }

        /// <summary>
        /// Cryptographically random receipt code.
        ///
        /// Format:
        /// EVT{eventId:D2}-{8 hex}-{8 hex}
        ///
        /// Example:
        /// EVT03-A1B2C3D4-E5F6A7B8
        ///
        /// MemberId is intentionally excluded for privacy.
        /// </summary>
        private static string GenerateReceiptCode(
            int eventId)
        {
            Span<byte> bytes = stackalloc byte[8];

            RandomNumberGenerator.Fill(bytes);

            var hex = Convert.ToHexString(bytes);

            return $"EVT{eventId:D2}-{hex[..8]}-{hex[8..]}";
        }
    }
}