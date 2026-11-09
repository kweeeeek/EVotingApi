using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Representative Election — pure rules (no EF, no HTTP).
//
//  Everything that DECIDES something (is this ballot valid? who wins? is the roster editable? is this import
//  row acceptable?) lives here as plain functions, so it is unit-tested directly and used identically by the
//  online ballot, the offline sync, the import validator and the Admin screens.
//  The client never decides any of it.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

namespace EVoting.Api.Services
{
    public static class RepresentativeLimits
    {
        public const int CandidateCode = 30;
        public const int CandidateName = 200;
        public const int Reason = 2000;
        public const int PreviousPositions = 500;
        public const int SpecialSkills = 500;
        public const int Education = 200;
        public const int PhoneNumber = 20;
        public const int ShortText = 20;

        /// <summary>Largest ballot a single request may carry (defence against a hostile payload; real ballots are far smaller).</summary>
        public const int MaxSelectionsPerRequest = 500;
        /// <summary>Largest MaxVotes an administrator may configure — it must always fit in one request.</summary>
        public const int MaxVotesPerPosition = MaxSelectionsPerRequest;
        public const int MaxSeatsPerPosition = 5000;
        public const int MaxCandidatesPerPosition = 100_000;
        public const int MaxPositionsPerEvent = 50;
        public const int MaxBallotNumber = 1_000_000;

        public const int DefaultPageSize = 50;
        public const int MaxPageSize = 200;
        public const int MaxSyncPageSize = 500;
        public const int MaxThumbnailBytes = 96 * 1024;
    }

    /// <summary>Operational limits of the candidate import. All configurable (section "RepresentativeImport"); none is a hard-coded 1,000.</summary>
    public sealed class RepresentativeImportOptions
    {
        public const string Section = "RepresentativeImport";

        /// <summary>Most data rows one batch may hold (1,000, 2,000 and far larger rosters are supported).</summary>
        public int MaxRowsPerBatch { get; set; } = 20_000;
        /// <summary>Most rows accepted by one staging request; larger files are sent in several requests.</summary>
        public int MaxRowsPerRequest { get; set; } = 2_000;
        /// <summary>Uncommitted batches (UPLOADED / VALIDATING / VALIDATED / INVALID) one event may have at the same time.</summary>
        public int MaxOpenBatchesPerEvent { get; set; } = 10;
        /// <summary>Rows per multi-row INSERT statement when staging.</summary>
        public int InsertChunkRows { get; set; } = 250;
    }

    /// <summary>Stable machine-readable reasons returned in <c>ApiError.Code</c>.</summary>
    public static class RepCodes
    {
        // event / voter
        public const string EventNotFound = "EVENT_NOT_FOUND";
        public const string WrongEventType = "WRONG_EVENT_TYPE";
        public const string EventNotOpen = "EVENT_NOT_OPEN";
        public const string OutsideVotingWindow = "OUTSIDE_VOTING_WINDOW";
        public const string TokenMismatch = "TOKEN_MISMATCH";
        public const string MemberNotFound = "MEMBER_NOT_FOUND";
        public const string NotEligible = "NOT_ELIGIBLE";
        public const string BranchNotParticipating = "BRANCH_NOT_PARTICIPATING";
        public const string AlreadyVoted = "ALREADY_VOTED";
        public const string InvalidReceipt = "INVALID_RECEIPT_CODE";
        public const string ReceiptInUse = "RECEIPT_IN_USE";
        public const string InvalidCastTime = "INVALID_CAST_TIME";
        /// <summary>The database stayed too busy; NOTHING was recorded and the same request may be sent again.</summary>
        public const string TemporaryFailure = "TEMPORARY_FAILURE";

        // ballot
        public const string NoSelection = "NO_SELECTION";
        public const string TooManySelections = "TOO_MANY_SELECTIONS";
        public const string DuplicateSelection = "DUPLICATE_SELECTION";
        public const string NoPositionRules = "NO_POSITION_RULES";
        public const string UnknownCandidate = "UNKNOWN_CANDIDATE";
        public const string CandidateWrongEvent = "CANDIDATE_WRONG_EVENT";
        public const string CandidateNotEligible = "CANDIDATE_NOT_ELIGIBLE";
        public const string PositionNotConfigured = "POSITION_NOT_CONFIGURED";
        public const string BelowMinimum = "BELOW_MINIMUM";
        public const string AboveMaximum = "ABOVE_MAXIMUM";

        // roster / lock
        public const string EventOpenLock = "EVENT_OPEN_LOCK";
        public const string HasBallots = "HAS_BALLOTS";
        public const string RosterLocked = "ROSTER_LOCKED";
        public const string PositionFull = "POSITION_FULL";
        public const string AlreadyAssigned = "ALREADY_ASSIGNED";
        public const string BallotNumberTaken = "BALLOT_NUMBER_TAKEN";
        public const string ProfileInOpenEvent = "PROFILE_IN_OPEN_EVENT";
        public const string CodeImmutable = "CODE_IMMUTABLE";
        public const string NotReady = "EVENT_NOT_READY";
        public const string CodeExists = "CANDIDATE_CODE_EXISTS";
        public const string MemberLinked = "MEMBER_ALREADY_LINKED";
        public const string ProfileInactive = "PROFILE_INACTIVE";
        public const string ProfileOnRoster = "PROFILE_ON_ROSTER";
        public const string ProfileHasVotes = "PROFILE_HAS_VOTES";
        public const string NotLocked = "ROSTER_NOT_LOCKED";
        public const string PhotoInvalid = "PHOTO_INVALID";

        // import
        public const string BatchNotFound = "BATCH_NOT_FOUND";
        public const string BatchState = "BATCH_STATE";
        public const string NotConfirmed = "NOT_CONFIRMED";
        public const string ConflictsUnresolved = "CONFLICTS_UNRESOLVED";
        public const string ValidationStale = "VALIDATION_STALE";
        public const string TooManyRows = "TOO_MANY_ROWS";
        public const string TooManyOpenBatches = "TOO_MANY_OPEN_BATCHES";
        public const string ImportFailed = "IMPORT_FAILED";
    }

    // ── Election clock ───────────────────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Event schedules are stored as the election's LOCAL wall-clock time (the Admin saves them that way and the scheduler compares
    /// them with local "now"). The existing offline sync interprets them in "Singapore Standard Time" (UTC+8, the Philippines); the
    /// Representative ballot uses exactly the same interpretation so both election types agree on when voting is open.
    /// </summary>
    public static class ElectionClock
    {
        public static readonly TimeZoneInfo Zone = Resolve();

        private static TimeZoneInfo Resolve()
        {
            foreach (var id in new[] { "Singapore Standard Time", "Asia/Manila", "Asia/Singapore" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        }

        public static DateTime ToUtc(DateTime electionLocal) =>
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(electionLocal, DateTimeKind.Unspecified), Zone);

        /// <summary>True when <paramref name="utcInstant"/> lies in [ScheduleFrom, ScheduleTo) of the event (both given as election-local times).</summary>
        public static bool IsWithinWindow(DateTime scheduleFromLocal, DateTime scheduleToLocal, DateTime utcInstant)
        {
            var from = ToUtc(scheduleFromLocal);
            var to = ToUtc(scheduleToLocal);
            return to > from && utcInstant >= from && utcInstant < to;
        }

        public static DateTime AsUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    // ── Candidate code / text normalisation ──────────────────────────────────────────────────────────────────
    public static class CandidateCodeRules
    {
        // \z (the very end of the text), not $: in a .NET regular expression '$' also matches just before a final line break.
        private static readonly Regex Pattern = new(@"^[A-Z0-9][A-Z0-9._/\-]*\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public const string Requirement =
            "Candidate code must be 1–30 characters: letters, digits and . _ / - (starting with a letter or digit).";

        /// <summary>Stored form: trimmed, upper-case, no inner spaces allowed.</summary>
        public static string Normalize(string? raw) => (raw ?? "").Trim().ToUpperInvariant();

        public static bool IsValid(string normalized) =>
            normalized.Length is >= 1 and <= RepresentativeLimits.CandidateCode && Pattern.IsMatch(normalized);
    }

    public static class RepText
    {
        private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex Phone = new(@"^[0-9+\-()./ ]{3,20}\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Single-line text: NFC, tabs/line-breaks → space, runs of spaces collapsed, trimmed. Empty → null.</summary>
        public static string? OneLine(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw.Normalize(NormalizationForm.FormC);
            var sb = new StringBuilder(t.Length);
            foreach (var c in t) sb.Append(char.IsControl(c) ? ' ' : c);
            var s = Spaces.Replace(sb.ToString(), " ").Trim();
            return s.Length == 0 ? null : s;
        }

        /// <summary>Multi-line text: NFC, CRLF → LF, other control characters removed, trimmed. Empty → null.</summary>
        public static string? MultiLine(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n');
            var sb = new StringBuilder(t.Length);
            foreach (var c in t) if (c == '\n' || !char.IsControl(c)) sb.Append(c);
            var s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        public static bool IsPhone(string value) => Phone.IsMatch(value);

        /// <summary>Case-insensitive lookup in an allowed list; returns the list's own spelling.</summary>
        public static string? Canonical(string value, IEnumerable<string> allowed) =>
            allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));

        /// <summary>Neutralises spreadsheet formula injection for text written into CSV / Excel exports.</summary>
        public static string Safe(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        }
    }

    // ── Position rules ───────────────────────────────────────────────────────────────────────────────────────
    public sealed class PositionRuleValidation
    {
        /// <summary>Field → message (keys look like <c>rules[2].maxVotes</c>, zero-based like the submitted list).</summary>
        public Dictionary<string, string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public bool Ok => Errors.Count == 0;
    }

    public static class PositionRuleValidator
    {
        /// <summary>
        /// Validates the complete rule set of one event. <paramref name="rules"/> are normalised in place (trimmed; General Assembly
        /// positions take the canonical spelling). <paramref name="assignedByPosition"/> holds, per position (case-insensitive), the
        /// candidates already on the ballot: a Representative roster is a hard limit, for General Assembly it is advisory.
        /// </summary>
        public static PositionRuleValidation Validate(
            IList<PositionRuleRow> rules, string eventType, IReadOnlyDictionary<string, int> assignedByPosition)
        {
            var result = new PositionRuleValidation();
            bool representative = eventType == EventTypes.Representative;

            if (rules == null || rules.Count == 0)
            {
                result.Errors["rules"] = "Add at least one position.";
                return result;
            }
            if (rules.Count > RepresentativeLimits.MaxPositionsPerEvent)
                result.Errors["rules"] = $"An event can have at most {RepresentativeLimits.MaxPositionsPerEvent} positions.";

            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                string key(string field) => $"rules[{i}].{field}";
                string label = $"Row {i + 1}";

                var position = RepText.OneLine(r.Position);
                if (position == null)
                {
                    result.Errors[key("position")] = $"{label}: select a position.";
                }
                else if (position.Length > FieldLimits.CandidatePosition)
                {
                    result.Errors[key("position")] = $"{label}: the position name is longer than {FieldLimits.CandidatePosition} characters.";
                }
                else
                {
                    if (!representative)
                    {
                        var canonical = RepText.Canonical(position, CandidatePositions.All);
                        if (canonical == null)
                            result.Errors[key("position")] =
                                $"{label}: '{position}' is not a General Assembly position. Choose one of: {string.Join(", ", CandidatePositions.All)}.";
                        else position = canonical;
                    }
                    if (!result.Errors.ContainsKey(key("position")))
                    {
                        if (seen.TryGetValue(position, out var first))
                            result.Errors[key("position")] = $"{label}: '{position}' is already configured in row {first + 1}. A position can appear only once per event.";
                        else
                            seen[position] = i;
                    }
                    r.Position = position;
                }

                if (r.MinVotes < 0)
                    result.Errors[key("minVotes")] = $"{label}: minimum votes cannot be negative.";
                if (r.MaxVotes < 1)
                    result.Errors[key("maxVotes")] = $"{label}: maximum votes must be at least 1.";
                else if (r.MaxVotes > RepresentativeLimits.MaxVotesPerPosition)
                    result.Errors[key("maxVotes")] = $"{label}: maximum votes cannot exceed {RepresentativeLimits.MaxVotesPerPosition}.";
                if (r.MinVotes >= 0 && r.MaxVotes >= 1 && r.MinVotes > r.MaxVotes)
                    result.Errors[key("minVotes")] = $"{label}: minimum votes ({r.MinVotes}) cannot be greater than maximum votes ({r.MaxVotes}).";

                if (r.TotalSeats < 1)
                    result.Errors[key("totalSeats")] = $"{label}: total seats must be at least 1.";
                else if (r.TotalSeats > RepresentativeLimits.MaxSeatsPerPosition)
                    result.Errors[key("totalSeats")] = $"{label}: total seats cannot exceed {RepresentativeLimits.MaxSeatsPerPosition}.";

                int assigned = 0;
                if (position != null) assignedByPosition.TryGetValue(position, out assigned);

                if (r.MaxCandidates.HasValue)
                {
                    int mc = r.MaxCandidates.Value;
                    if (mc < 1)
                        result.Errors[key("maxCandidates")] = $"{label}: max candidates must be a positive number.";
                    else if (mc > RepresentativeLimits.MaxCandidatesPerPosition)
                        result.Errors[key("maxCandidates")] = $"{label}: max candidates cannot exceed {RepresentativeLimits.MaxCandidatesPerPosition}.";
                    else
                    {
                        if (r.MinVotes > mc)
                            result.Errors[key("maxCandidates")] =
                                $"{label}: max candidates ({mc}) is lower than minimum votes ({r.MinVotes}); no ballot could ever be valid.";
                        else if (assigned > mc && representative)
                            result.Errors[key("maxCandidates")] =
                                $"{label}: {assigned} candidates are already assigned to '{position}', which is more than the limit of {mc}. Remove candidates first or raise the limit.";
                        else if (assigned > mc)
                            result.Warnings.Add($"'{position}' currently has {assigned} eligible approved candidates, more than the limit of {mc}. The election cannot be opened until this is corrected.");

                        if (!result.Errors.ContainsKey(key("maxCandidates")))
                        {
                            if (mc < r.TotalSeats)
                                result.Warnings.Add($"'{position}': max candidates ({mc}) is lower than total seats ({r.TotalSeats}); not every seat can be filled.");
                            if (mc < r.MaxVotes)
                                result.Warnings.Add($"'{position}': max candidates ({mc}) is lower than maximum votes ({r.MaxVotes}); voters can never use all their votes.");
                        }
                    }
                }
            }
            return result;
        }
    }

    // ── Who may change rules / roster ────────────────────────────────────────────────────────────────────────
    public sealed record EventLock(bool Locked, string? Code, string? Reason)
    {
        public static readonly EventLock Open = new(false, null, null);
    }

    public static class EventLockPolicy
    {
        /// <summary>
        /// Position rules and the Representative roster are editable only while an event is still being prepared:
        /// not open, no ballot cast yet and — for a Representative event — never opened before (the roster freezes the first time voting opens).
        /// </summary>
        public static EventLock ForRulesAndRoster(string status, string eventType, DateTime? rosterLockedAt, long ballotCount)
        {
            if (string.Equals(status, "Opened", StringComparison.OrdinalIgnoreCase))
                return new EventLock(true, RepCodes.EventOpenLock,
                    "The election is open. Close it first; rules and candidates cannot change while voting is open.");
            if (ballotCount > 0)
                return new EventLock(true, RepCodes.HasBallots,
                    $"{ballotCount} ballot(s) have already been cast in this election, so its rules and candidates are frozen.");
            if (eventType == EventTypes.Representative && rosterLockedAt.HasValue)
                return new EventLock(true, RepCodes.RosterLocked,
                    $"The candidate roster was locked when voting first opened ({rosterLockedAt.Value:yyyy-MM-dd HH:mm}). " +
                    "If the election was opened by mistake and has no ballots, an administrator can unlock the roster.");
            return EventLock.Open;
        }
    }

    // ── Ballot validation ────────────────────────────────────────────────────────────────────────────────────
    public sealed record BallotRule(string Position, int MinVotes, int MaxVotes, int TotalSeats, int? MaxCandidates);

    /// <param name="EventId">The event the candidate belongs to (checked against the ballot's event).</param>
    public sealed record BallotCandidate(int Id, int EventId, string Position, string Status, bool ProfileActive);

    public sealed record BallotCheck(bool Ok, string? Code, string? Message)
    {
        public static readonly BallotCheck Valid = new(true, null, null);
        public static BallotCheck Fail(string code, string message) => new(false, code, message);
    }

    public static class RepresentativeBallotRules
    {
        /// <summary>
        /// Server-side ballot rules. Inputs come from the DATABASE: the rules of the event, the candidate rows found for the submitted ids,
        /// and how many approved candidates each position really has. Nothing business-critical is taken from the client — the position of a
        /// selection is the position of its roster entry.
        /// </summary>
        /// <param name="candidatesById">Rows found for the submitted ids (any event); a missing id means the id does not exist.</param>
        /// <param name="approvedByPosition">Approved, active candidates currently on THIS event's ballot per position (case-insensitive).</param>
        public static BallotCheck Check(
            int eventId,
            IReadOnlyList<int>? selectedIds,
            IReadOnlyDictionary<int, BallotCandidate> candidatesById,
            IReadOnlyList<BallotRule> rules,
            IReadOnlyDictionary<string, int> approvedByPosition)
        {
            if (selectedIds == null || selectedIds.Count == 0)
                return BallotCheck.Fail(RepCodes.NoSelection, "Select at least one candidate.");
            if (selectedIds.Count > RepresentativeLimits.MaxSelectionsPerRequest)
                return BallotCheck.Fail(RepCodes.TooManySelections, "The ballot contains too many selections.");
            if (selectedIds.Distinct().Count() != selectedIds.Count)
                return BallotCheck.Fail(RepCodes.DuplicateSelection, "The same candidate was selected more than once.");
            if (rules.Count == 0)
                return BallotCheck.Fail(RepCodes.NoPositionRules,
                    "This election has no position rules configured, so no ballot can be accepted.");

            var ruleByPosition = new Dictionary<string, BallotRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in rules)
                if (!ruleByPosition.TryAdd(rule.Position.Trim(), rule))
                    return BallotCheck.Fail(RepCodes.PositionNotConfigured,
                        $"The election defines the position '{rule.Position}' more than once; voting is disabled until this is corrected.");

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in selectedIds)
            {
                if (!candidatesById.TryGetValue(id, out var c))
                    return BallotCheck.Fail(RepCodes.UnknownCandidate, $"Candidate {id} does not exist.");
                if (c.EventId != eventId)
                    return BallotCheck.Fail(RepCodes.CandidateWrongEvent, $"Candidate {id} is not on the ballot of this election.");
                if (c.Status != RepresentativeCandidateStatuses.Approved || !c.ProfileActive)
                    return BallotCheck.Fail(RepCodes.CandidateNotEligible, $"Candidate {id} is not an approved candidate of this election.");
                var position = c.Position.Trim();
                if (!ruleByPosition.ContainsKey(position))
                    return BallotCheck.Fail(RepCodes.PositionNotConfigured, $"Candidate {id} runs for '{position}', which has no voting rule.");
                counts[position] = counts.GetValueOrDefault(position) + 1;
            }

            // Every configured position is checked, so a client cannot dodge a required position by leaving it out.
            foreach (var rule in ruleByPosition.Values)
            {
                counts.TryGetValue(rule.Position.Trim(), out var picked);
                approvedByPosition.TryGetValue(rule.Position.Trim(), out var available);

                // You cannot be asked for more picks than there are candidates (MaxVotes is capped by availability implicitly).
                int effectiveMin = Math.Min(rule.MinVotes, available);
                if (picked < effectiveMin)
                    return BallotCheck.Fail(RepCodes.BelowMinimum,
                        $"'{rule.Position}' requires at least {effectiveMin} selection(s); {picked} were submitted.");
                if (picked > rule.MaxVotes)
                    return BallotCheck.Fail(RepCodes.AboveMaximum,
                        $"'{rule.Position}' allows at most {rule.MaxVotes} selection(s); {picked} were submitted.");
            }
            return BallotCheck.Valid;
        }
    }

    // ── Ranking / winners ────────────────────────────────────────────────────────────────────────────────────
    public sealed record RankInput(int Id, string Name, string Code, int Votes);

    public sealed record RankOutput(int Id, int Votes, int Rank, bool IsWinner, bool IsTieForLastSeat);

    public sealed record RankedPositionResult(IReadOnlyList<RankOutput> Ranked, int SeatsFilled, bool HasUnresolvedTie);

    public static class RepresentativeRanking
    {
        /// <summary>
        /// Display order: votes high → low, then name, then code, then id — fully deterministic, so two runs on the same data
        /// always print the same list.
        /// Rank is a competition rank by votes only (1, 2, 2, 4): candidates with equal votes share a rank.
        /// Winners follow the existing General Assembly rule (ResultsAggregator.Decide): with at least as many seats as candidates everybody
        /// is elected; otherwise the first <c>seats</c> candidates are elected, except that candidates whose votes equal those of the first
        /// candidate outside the seats are a TIE for the last seat(s) — the count alone cannot decide them, so nobody in that group is declared elected.
        /// </summary>
        public static RankedPositionResult Rank(IEnumerable<RankInput> candidates, int totalSeats)
        {
            var ordered = candidates
                .OrderByDescending(c => c.Votes)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Id)
                .ToList();

            var outcome = new int[ordered.Count];          // 0 none, 1 winner, 2 tie
            if (totalSeats >= 1 && ordered.Count > 0)
            {
                if (ordered.Count <= totalSeats)
                    Array.Fill(outcome, 1);
                else
                {
                    int cutoff = ordered[totalSeats - 1].Votes;
                    bool tied = ordered[totalSeats].Votes == cutoff;
                    for (int i = 0; i < ordered.Count; i++)
                    {
                        int v = ordered[i].Votes;
                        outcome[i] = v > cutoff ? 1 : v == cutoff ? (tied ? 2 : 1) : 0;
                    }
                }
            }

            var result = new List<RankOutput>(ordered.Count);
            int rank = 0, previousVotes = -1;
            for (int i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Votes != previousVotes) { rank = i + 1; previousVotes = ordered[i].Votes; }
                result.Add(new RankOutput(ordered[i].Id, ordered[i].Votes, rank, outcome[i] == 1, outcome[i] == 2));
            }
            return new RankedPositionResult(result, outcome.Count(o => o == 1), outcome.Any(o => o == 2));
        }

        /// <summary>Share of the BALLOTS cast (not of the selections): a ballot can select several candidates, so a share of selections would be misleading.</summary>
        public static decimal PercentOfBallots(int votes, int ballots) =>
            ballots <= 0 ? 0m : decimal.Round(votes * 100m / ballots, 2, MidpointRounding.AwayFromZero);

        public static decimal Turnout(int ballots, int eligible) =>
            eligible <= 0 ? 0m : decimal.Round(ballots * 100m / eligible, 2, MidpointRounding.AwayFromZero);
    }

    // ── Roster version ───────────────────────────────────────────────────────────────────────────────────────
    public sealed record RosterRuleFact(string Position, int MinVotes, int MaxVotes, int TotalSeats, int? MaxCandidates);

    public sealed record RosterCandidateFact(int Id, int ProfileId, string Code, string Name, string Position, int? BallotNumber, string Status, long PhotoVersion);

    public static class RosterVersion
    {
        /// <summary>
        /// A fingerprint of everything a voter or kiosk sees on the ballot: the rules and every candidate (identity, position, number, status, name,
        /// photo stamp). It changes when anything changes, so a kiosk compares one short string to know whether its cached roster is current.
        /// </summary>
        public static string Compute(int eventId, string eventType, IEnumerable<RosterRuleFact> rules, IEnumerable<RosterCandidateFact> candidates)
        {
            // Every piece of text is written as "<length>:<text>|", so no character inside a name or a position (a '|', a line break) can be
            // mistaken for the end of a field: two different rosters can never produce the same string. Numbers are digits only.
            // Positions are compared the way the ballot compares them (trimmed, upper case), also for the sort order.
            static StringBuilder Text(StringBuilder sb, string value) => sb.Append(value.Length).Append(':').Append(value).Append('|');
            static string Key(string position) => position.Trim().ToUpperInvariant();

            var sb = new StringBuilder();
            sb.Append("v2|").Append(eventId).Append('|');
            Text(sb, eventType).Append('\n');
            foreach (var r in rules.OrderBy(r => Key(r.Position), StringComparer.Ordinal))
            {
                Text(sb.Append("R|"), Key(r.Position));
                sb.Append(r.MinVotes).Append('|').Append(r.MaxVotes).Append('|').Append(r.TotalSeats).Append('|')
                  .Append(r.MaxCandidates?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('\n');
            }
            foreach (var c in candidates.OrderBy(c => c.Id))
            {
                sb.Append("C|").Append(c.Id).Append('|').Append(c.ProfileId).Append('|');
                Text(sb, c.Code);
                Text(sb, c.Name);
                Text(sb, Key(c.Position));
                sb.Append(c.BallotNumber?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|');
                Text(sb, c.Status);
                sb.Append(c.PhotoVersion).Append('\n');
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }
    }

    // ── Import rows ──────────────────────────────────────────────────────────────────────────────────────────
    public sealed class ImportRowCheck
    {
        public string? CandidateCode { get; set; }
        public string? CandidateName { get; set; }
        public string? AccountCode { get; set; }
        public string? Position { get; set; }
        public string? CellNo { get; set; }
        public string? TelNo { get; set; }
        public string? CivilStatus { get; set; }
        public string? Gender { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? HighestEducation { get; set; }
        public string? PreviousPositions { get; set; }
        public string? SpecialSkills { get; set; }
        public string? Reason { get; set; }
        public int? BallotNumber { get; set; }
        public List<(string Code, string Message)> Errors { get; } = new();

        public bool HasErrors => Errors.Count > 0;
        public string ErrorCodes => string.Join(",", Errors.Select(e => e.Code).Distinct());
        public string ErrorMessage => string.Join(" ", Errors.Select(e => e.Message));
    }

    public static class RepresentativeImportRowRules
    {
        public const string CodeInvalid = "CODE_INVALID";
        public const string NameRequired = "NAME_REQUIRED";
        public const string PositionRequired = "POSITION_REQUIRED";
        public const string TooLong = "TOO_LONG";
        public const string PhoneInvalid = "PHONE_INVALID";
        public const string CivilStatusInvalid = "CIVIL_STATUS_INVALID";
        public const string GenderInvalid = "GENDER_INVALID";
        public const string DobInvalid = "DOB_INVALID";
        public const string BallotNumberInvalid = "BALLOT_NUMBER_INVALID";
        public const string DuplicateInFile = "DUPLICATE_IN_FILE";
        public const string CodeConflictInFile = "CODE_CONFLICT_IN_FILE";
        public const string MemberNotFound = "MEMBER_NOT_FOUND";
        public const string MemberAlreadyLinked = "MEMBER_ALREADY_LINKED";
        public const string PositionNotConfigured = "POSITION_NOT_CONFIGURED";
        public const string AlreadyAssigned = "ALREADY_ASSIGNED";
        public const string PositionFull = "POSITION_FULL";
        public const string BallotNumberTaken = "BALLOT_NUMBER_TAKEN";
        public const string ProfileDiffers = "PROFILE_DIFFERS";
        public const string ProfileInactive = "PROFILE_INACTIVE";
        public const string ProfileInOpenEvent = "PROFILE_IN_OPEN_EVENT";
        public const string ProfileHasVotes = "PROFILE_HAS_VOTES";
        public const string NameMissingForNewProfile = "NAME_REQUIRED";

        // The codes <see cref="Check"/> itself can produce. Everything else a staged row can carry comes from the roster-level planner
        // (duplicates, capacity, profile conflicts, …) and is judged again every time the batch is validated.
        private static readonly HashSet<string> FieldLevelCodes = new(StringComparer.Ordinal)
        {
            CodeInvalid, PositionRequired, TooLong, PhoneInvalid, CivilStatusInvalid, GenderInvalid, DobInvalid, BallotNumberInvalid
        };

        /// <summary>
        /// True when a stored error-code list (comma separated) consists only of field-level codes — i.e. the row is wrong by itself, whatever the
        /// roster looks like, and must stay INVALID without being re-examined.
        /// </summary>
        public static bool IsFieldLevel(string? errorCodes)
        {
            if (string.IsNullOrWhiteSpace(errorCodes)) return false;
            foreach (var part in errorCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!FieldLevelCodes.Contains(part)) return false;
            return true;
        }

        /// <summary>Field-level checks of one spreadsheet row. Everything that needs the database (matching, duplicates, capacity) happens later, set-based.</summary>
        public static ImportRowCheck Check(RepImportRowIn row, DateTime todayLocal)
        {
            var o = new ImportRowCheck();
            void Err(string code, string message) => o.Errors.Add((code, message));

            // Candidate code
            var code = CandidateCodeRules.Normalize(RepText.OneLine(row.CandidateCode));
            if (code.Length == 0) Err(CodeInvalid, "Candidate code is required.");
            else if (!CandidateCodeRules.IsValid(code)) Err(CodeInvalid, CandidateCodeRules.Requirement);
            else o.CandidateCode = code;

            // Name (a missing name is acceptable only when the row names a member; decided later when the member is known)
            o.CandidateName = RepText.OneLine(row.CandidateName);
            if (o.CandidateName is { Length: > RepresentativeLimits.CandidateName })
                Err(TooLong, $"Candidate name is longer than {RepresentativeLimits.CandidateName} characters.");

            // Account code
            o.AccountCode = RepText.OneLine(row.AccountCode);
            if (o.AccountCode is { Length: > FieldLimits.AccountCode })
                Err(TooLong, $"Account code is longer than {FieldLimits.AccountCode} characters.");

            // Position
            o.Position = RepText.OneLine(row.Position);
            if (o.Position == null) Err(PositionRequired, "Position is required.");
            else if (o.Position.Length > FieldLimits.CandidatePosition)
                Err(TooLong, $"Position is longer than {FieldLimits.CandidatePosition} characters.");

            // Phones
            o.CellNo = RepText.OneLine(row.CellNo);
            o.TelNo = RepText.OneLine(row.TelNo);
            if (o.CellNo != null && !RepText.IsPhone(o.CellNo)) Err(PhoneInvalid, "Cell number may contain digits, spaces and + - ( ) . / only (3–20 characters).");
            if (o.TelNo != null && !RepText.IsPhone(o.TelNo)) Err(PhoneInvalid, "Telephone number may contain digits, spaces and + - ( ) . / only (3–20 characters).");

            // Civil status / gender: same vocabularies as the General Assembly candidacy form
            var civil = RepText.OneLine(row.CivilStatus);
            if (civil != null)
            {
                var c = RepText.Canonical(civil, CandidacyLists.CivilStatuses);
                if (c == null) Err(CivilStatusInvalid, $"Civil status must be one of: {string.Join(", ", CandidacyLists.CivilStatuses)}.");
                else o.CivilStatus = c;
            }
            var gender = RepText.OneLine(row.Gender);
            if (gender != null)
            {
                var g = RepText.Canonical(gender, CandidacyLists.Genders);
                if (g == null) Err(GenderInvalid, $"Gender must be one of: {string.Join(", ", CandidacyLists.Genders)}.");
                else o.Gender = g;
            }

            // Date of birth
            if (!string.IsNullOrWhiteSpace(row.DateOfBirthRaw) && row.DateOfBirth == null)
                Err(DobInvalid, $"Date of birth '{row.DateOfBirthRaw!.Trim()}' is not a valid date.");
            else if (row.DateOfBirth.HasValue)
            {
                var d = row.DateOfBirth.Value.Date;
                if (d > todayLocal.Date) Err(DobInvalid, "Date of birth cannot be in the future.");
                else if (d.Year < 1900) Err(DobInvalid, "Date of birth is before 1900.");
                else o.DateOfBirth = d;
            }

            // Free text
            o.HighestEducation = RepText.OneLine(row.HighestEducation);
            if (o.HighestEducation is { Length: > RepresentativeLimits.Education })
                Err(TooLong, $"Highest education is longer than {RepresentativeLimits.Education} characters.");
            o.PreviousPositions = RepText.MultiLine(row.PreviousPositions);
            if (o.PreviousPositions is { Length: > RepresentativeLimits.PreviousPositions })
                Err(TooLong, $"Previous positions is longer than {RepresentativeLimits.PreviousPositions} characters.");
            o.SpecialSkills = RepText.MultiLine(row.SpecialSkills);
            if (o.SpecialSkills is { Length: > RepresentativeLimits.SpecialSkills })
                Err(TooLong, $"Special skills is longer than {RepresentativeLimits.SpecialSkills} characters.");
            o.Reason = RepText.MultiLine(row.Reason);
            if (o.Reason is { Length: > RepresentativeLimits.Reason })
                Err(TooLong, $"Reason is longer than {RepresentativeLimits.Reason} characters.");

            // Ballot number
            var bn = RepText.OneLine(row.BallotNumber);
            if (bn != null)
            {
                if (int.TryParse(bn, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= RepresentativeLimits.MaxBallotNumber)
                    o.BallotNumber = n;
                else
                    Err(BallotNumberInvalid, $"Ballot number must be a whole number from 1 to {RepresentativeLimits.MaxBallotNumber}.");
            }
            return o;
        }
    }

    // ── Casting: time rules and HTTP mapping ─────────────────────────────────────────────────────────────────
    public sealed record CastTimeCheck(bool Ok, string? Code, string? Message, DateTime CastAtUtc)
    {
        public static CastTimeCheck Fail(string code, string message) => new(false, code, message, default);
    }

    public static class RepresentativeCastTime
    {
        /// <summary>How far a kiosk clock may run ahead of the server before an offline ballot is refused as future-dated.</summary>
        public static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Online ballot: voting is accepted only while the election is Opened AND the server clock lies inside [ScheduleFrom, ScheduleTo)
        /// (both interpreted as election-local time, exactly as the General Assembly offline sync does).
        /// </summary>
        public static CastTimeCheck CheckOnline(string status, DateTime scheduleFromLocal, DateTime scheduleToLocal, DateTime nowUtc)
        {
            if (!string.Equals(status, "Opened", StringComparison.OrdinalIgnoreCase))
                return CastTimeCheck.Fail(RepCodes.EventNotOpen, "Voting is not open for this election.");
            var from = ElectionClock.ToUtc(scheduleFromLocal);
            var to = ElectionClock.ToUtc(scheduleToLocal);
            if (to <= from)
                return CastTimeCheck.Fail(RepCodes.OutsideVotingWindow, "This election has an invalid voting schedule, so no ballot can be accepted.");
            if (nowUtc < from) return CastTimeCheck.Fail(RepCodes.OutsideVotingWindow, "Voting has not started yet for this election.");
            if (nowUtc >= to) return CastTimeCheck.Fail(RepCodes.OutsideVotingWindow, "Voting has already ended for this election.");
            return new CastTimeCheck(true, null, null, nowUtc);
        }

        /// <summary>
        /// Offline ballot (cast on a kiosk without connectivity, synchronised later - possibly after the election closed):
        /// the moment the voter pressed "submit" must lie inside the voting window and must not be in the future (beyond a small clock skew).
        /// The election must have been opened at some point (a never-opened election has no valid ballots).
        /// </summary>
        public static CastTimeCheck CheckOffline(bool everOpened, DateTime scheduleFromLocal, DateTime scheduleToLocal, DateTime castAt, DateTime nowUtc)
        {
            if (!everOpened)
                return CastTimeCheck.Fail(RepCodes.EventNotOpen, "This election was never opened, so it cannot have an offline ballot.");
            if (castAt == default)
                return CastTimeCheck.Fail(RepCodes.InvalidCastTime, "The offline ballot does not carry a valid cast time.");
            var cast = ElectionClock.AsUtc(castAt);
            if (cast > nowUtc.Add(ClockSkewTolerance))
                return CastTimeCheck.Fail(RepCodes.InvalidCastTime, "The offline ballot is dated in the future; it cannot be accepted.");
            var from = ElectionClock.ToUtc(scheduleFromLocal);
            var to = ElectionClock.ToUtc(scheduleToLocal);
            if (to <= from)
                return CastTimeCheck.Fail(RepCodes.OutsideVotingWindow, "This election has an invalid voting schedule, so no ballot can be accepted.");
            if (cast < from) return CastTimeCheck.Fail(RepCodes.OutsideVotingWindow, "The offline ballot was cast before voting opened.");
            if (cast >= to) return CastTimeCheck.Fail(RepCodes.OutsideVotingWindow, "The offline ballot was cast after voting ended.");
            return new CastTimeCheck(true, null, null, cast);
        }
    }

    public static class RepresentativeHttp
    {
        /// <summary>
        /// One place that says which HTTP status each stable code is answered with:
        /// 404 unknown record · 403 not allowed for this voter/session · 409 state conflict (closed, already voted, wrong type)
        /// · 422 the ballot or the voter fails a rule · 400 malformed request.
        /// </summary>
        public static int StatusFor(string? code) => code switch
        {
            RepCodes.EventNotFound or RepCodes.MemberNotFound => StatusCodes.Status404NotFound,
            RepCodes.TokenMismatch or RepCodes.BranchNotParticipating => StatusCodes.Status403Forbidden,
            RepCodes.WrongEventType or RepCodes.EventNotOpen or RepCodes.OutsideVotingWindow or RepCodes.AlreadyVoted
                or RepCodes.ReceiptInUse => StatusCodes.Status409Conflict,
            RepCodes.InvalidReceipt or RepCodes.InvalidCastTime => StatusCodes.Status400BadRequest,
            RepCodes.TemporaryFailure => StatusCodes.Status503ServiceUnavailable,
            RepCodes.NotEligible or RepCodes.NoSelection or RepCodes.TooManySelections or RepCodes.DuplicateSelection
                or RepCodes.NoPositionRules or RepCodes.UnknownCandidate or RepCodes.CandidateWrongEvent or RepCodes.CandidateNotEligible
                or RepCodes.PositionNotConfigured or RepCodes.BelowMinimum or RepCodes.AboveMaximum => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status400BadRequest
        };

        /// <summary>Codes that will fail identically on every retry (the kiosk must stop retrying and show the ballot to an administrator).</summary>
        public static bool IsPermanent(string? code) => code != null && code != RepCodes.TemporaryFailure;
    }

    /// <summary>
    /// A General Assembly vote path was asked to record a ballot for a Representative event (or the reverse). Raised BEFORE anything is written.
    /// Deriving from InvalidOperationException keeps the existing offline-sync handling: it is a permanent business rejection.
    /// </summary>
    public sealed class WrongEventTypeException : InvalidOperationException
    {
        public int EventId { get; }
        public string EventType { get; }

        public WrongEventTypeException(int eventId, string eventType)
            : base(eventType == EventTypes.Representative
                ? $"Event {eventId} is a Representative election. Its ballot must be submitted to the Representative voting endpoint, not the General Assembly one."
                : $"Event {eventId} is a General Assembly election. Its ballot must be submitted to the General Assembly voting endpoint.")
        {
            EventId = eventId;
            EventType = eventType;
        }
    }
}
