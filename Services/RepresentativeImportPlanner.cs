using EVoting.Api.Infrastructure.Entities;
using System.Globalization;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Representative candidate import — the decision logic, as one pure function.
    //
    //  Input : the staged rows of ONE batch (already field-checked) and a snapshot of what the database knows
    //          (profiles by code, members by account code, current roster, position rules).
    //  Output: for every row its status (VALID / INVALID / CONFLICT / SKIPPED), what commit will do with it
    //          (CREATE_PROFILE / REUSE_PROFILE / UPDATE_PROFILE) and a plain-language reason.
    //
    //  No EF, no HTTP, no clock: the same function runs when the administrator previews the file and again, inside the
    //  commit transaction, against fresh data — so what was previewed is exactly what is verified before anything is written.
    //  Rows are judged in spreadsheet order; the first row that claims a position slot keeps it.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    public static class ImportPlannedActions
    {
        public const string CreateProfile = "CREATE_PROFILE";
        public const string ReuseProfile = "REUSE_PROFILE";
        public const string UpdateProfile = "UPDATE_PROFILE";
    }

    public static class ImportResolutions
    {
        public const string UseExisting = "USE_EXISTING";
        public const string UpdateProfile = "UPDATE_PROFILE";
        public const string Skip = "SKIP";
        public static readonly string[] All = { UseExisting, UpdateProfile, Skip };

        public static string? Normalize(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            var t = v.Trim().ToUpperInvariant().Replace(' ', '_').Replace('-', '_');
            return All.Contains(t) ? t : null;
        }
    }

    /// <summary>A staged row as the planner sees it. <see cref="FieldErrorCodes"/> is non-null for rows that failed the per-row field checks at staging time.</summary>
    public sealed record PlanRow(
        long ImportRowId, int RowNumber,
        string? CandidateCode, string? CandidateName, string? AccountCode, string? Position,
        string? CellNo, string? TelNo, string? CivilStatus, string? Gender, DateTime? DateOfBirth,
        string? HighestEducation, string? PreviousPositions, string? SpecialSkills, string? Reason, int? BallotNumber,
        string? FieldErrorCodes, string? FieldErrorMessage, string? Resolution);

    public sealed record PlanProfile(
        int ProfileId, string Code, string Name, int? MemberId,
        string? CellNo, string? TelNo, string? CivilStatus, string? Gender, DateTime? DateOfBirth,
        string? HighestEducation, string? PreviousPositions, string? SpecialSkills,
        bool IsActive, bool InOpenEvent, bool HasVotes);

    public sealed record PlanMember(int MemberId, string AccountCode, string MemberName, int? LinkedProfileId, string? LinkedProfileCode);

    public sealed record PlanRule(string Position, int? MaxCandidates);

    public sealed class PlanContext
    {
        /// <summary>Keyed by upper-case candidate code.</summary>
        public IReadOnlyDictionary<string, PlanProfile> ProfilesByCode { get; init; } = new Dictionary<string, PlanProfile>();
        /// <summary>Active members only, keyed by account code (case-insensitive).</summary>
        public IReadOnlyDictionary<string, PlanMember> MembersByAccount { get; init; } = new Dictionary<string, PlanMember>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<PlanRule> Rules { get; init; } = Array.Empty<PlanRule>();
        /// <summary>(profile id, position) pairs already on the event's roster, any status (the unique key covers withdrawn rows too).</summary>
        public IReadOnlySet<(int ProfileId, string Position)> ExistingAssignments { get; init; } = new HashSet<(int, string)>();
        /// <summary>Approved roster entries per position (case-insensitive keys).</summary>
        public IReadOnlyDictionary<string, int> ApprovedByPosition { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        /// <summary>(position, ballot number) pairs already used on the roster.</summary>
        public IReadOnlySet<(string Position, int Ballot)> ExistingBallotNumbers { get; init; } = new HashSet<(string, int)>();
    }

    public sealed record PlanResult(
        long ImportRowId, int RowNumber, string RowStatus, string? PlannedAction, int? MatchedProfileId,
        string? CanonicalPosition, string? ResolvedName, string? ErrorCodes, string? ErrorMessage);

    public sealed record PlanSummary(int Total, int Valid, int Invalid, int Conflict, int Skipped, int NewProfiles, int ReusedProfiles, int UpdatedProfiles, int NewAssignments)
    {
        /// <summary>Anything an administrator must decide or fix before the file is clean.</summary>
        public bool IsClean => Invalid == 0 && Conflict == 0;
    }

    public static class RepresentativeImportPlanner
    {
        private const string TooLongNote = "…";

        public static (List<PlanResult> Results, PlanSummary Summary) Plan(IReadOnlyList<PlanRow> rows, PlanContext ctx)
        {
            var rulesByPosition = new Dictionary<string, PlanRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ctx.Rules) rulesByPosition[r.Position.Trim()] = r;

            var results = new List<PlanResult>(rows.Count);
            var seenAssignment = new Dictionary<(string Code, string Position), int>();
            var firstRowOfCode = new Dictionary<string, PlanRow>();
            var memberClaim = new Dictionary<int, (string Code, int Row)>();
            var plannedByPosition = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var plannedBallots = new HashSet<(string Position, int Ballot)>();
            var existingBallots = new HashSet<(string Position, int Ballot)>(ctx.ExistingBallotNumbers.Select(b => (b.Position.ToUpperInvariant(), b.Ballot)));
            var existingAssignments = new HashSet<(int, string)>(ctx.ExistingAssignments.Select(a => (a.ProfileId, a.Position.ToUpperInvariant())));

            int valid = 0, invalid = 0, conflict = 0, skipped = 0;
            var newCodes = new HashSet<string>();
            var reused = new HashSet<int>();
            var updated = new HashSet<int>();

            foreach (var row in rows.OrderBy(r => r.RowNumber))
            {
                PlanResult Bad(string code, string message, string? position = null, string? name = null, int? profileId = null)
                {
                    invalid++;
                    return new PlanResult(row.ImportRowId, row.RowNumber, RepresentativeImportRowStatuses.Invalid, null, profileId, position ?? row.Position, name ?? row.CandidateName, code, Clip(message, 1000));
                }

                // 0. Per-row field errors found when the row was staged stay as they are.
                if (!string.IsNullOrEmpty(row.FieldErrorCodes))
                {
                    invalid++;
                    results.Add(new PlanResult(row.ImportRowId, row.RowNumber, RepresentativeImportRowStatuses.Invalid, null, null, row.Position, row.CandidateName,
                        Clip(row.FieldErrorCodes, 500), Clip(row.FieldErrorMessage, 1000)));
                    continue;
                }

                var code = row.CandidateCode ?? "";

                // 1. Position must be one of the event's configured positions (the canonical spelling is the rule's).
                if (row.Position == null || !rulesByPosition.TryGetValue(row.Position.Trim(), out var rule))
                {
                    results.Add(Bad(RepresentativeImportRowRules.PositionNotConfigured,
                        $"Position '{row.Position}' is not configured for this event. Add it to the event's position rules first."));
                    continue;
                }
                var position = rule.Position.Trim();
                var positionKey = position.ToUpperInvariant();

                // 2. The same candidate twice at the same position inside the file.
                if (seenAssignment.TryGetValue((code, positionKey), out var firstRow))
                {
                    results.Add(Bad(RepresentativeImportRowRules.DuplicateInFile,
                        $"Duplicate of row {firstRow}: candidate {code} is already listed for '{position}' in this file.", position));
                    continue;
                }
                seenAssignment[(code, positionKey)] = row.RowNumber;

                // 3. One code, two different people inside the file.
                if (firstRowOfCode.TryGetValue(code, out var first))
                {
                    var diff = DifferencesBetweenRows(first, row);
                    if (diff.Count > 0)
                    {
                        results.Add(Bad(RepresentativeImportRowRules.CodeConflictInFile,
                            $"Candidate code {code} is also used in row {first.RowNumber} with different {string.Join(", ", diff)}. One code identifies exactly one person.", position));
                        continue;
                    }
                }
                else firstRowOfCode[code] = row;

                // 4. Member link (optional).
                PlanMember? member = null;
                if (row.AccountCode != null)
                {
                    if (!ctx.MembersByAccount.TryGetValue(row.AccountCode, out member))
                    {
                        results.Add(Bad(RepresentativeImportRowRules.MemberNotFound, $"No active member has account code '{row.AccountCode}'.", position));
                        continue;
                    }
                }

                ctx.ProfilesByCode.TryGetValue(code, out var profile);

                if (member != null)
                {
                    if (profile?.MemberId != null && profile.MemberId.Value != member.MemberId)
                    {
                        results.Add(Bad(RepresentativeImportRowRules.MemberAlreadyLinked,
                            $"Candidate {code} is linked to a different member; an import cannot change that link.", position, profile.Name, profile.ProfileId));
                        continue;
                    }
                    if (member.LinkedProfileId.HasValue && (profile == null || profile.ProfileId != member.LinkedProfileId.Value))
                    {
                        results.Add(Bad(RepresentativeImportRowRules.MemberAlreadyLinked,
                            $"Member '{member.AccountCode}' is already linked to candidate {member.LinkedProfileCode}. A member can have only one candidate profile.", position));
                        continue;
                    }
                    if (memberClaim.TryGetValue(member.MemberId, out var claim) && !string.Equals(claim.Code, code, StringComparison.Ordinal))
                    {
                        results.Add(Bad(RepresentativeImportRowRules.MemberAlreadyLinked,
                            $"Member '{member.AccountCode}' is also used by candidate {claim.Code} in row {claim.Row}. A member can have only one candidate profile.", position));
                        continue;
                    }
                    memberClaim.TryAdd(member.MemberId, (code, row.RowNumber));
                }

                // 5. Create / reuse / conflict.
                string action;
                string? resolvedName = row.CandidateName ?? member?.MemberName;
                if (profile == null)
                {
                    if (resolvedName == null)
                    {
                        results.Add(Bad(RepresentativeImportRowRules.NameMissingForNewProfile,
                            "Candidate name is required for a new candidate (or give the AccountCode of an existing member).", position));
                        continue;
                    }
                    action = ImportPlannedActions.CreateProfile;
                }
                else
                {
                    resolvedName = row.CandidateName ?? profile.Name;
                    if (!profile.IsActive)
                    {
                        results.Add(Bad(RepresentativeImportRowRules.ProfileInactive, $"Candidate {code} is deactivated. Restore the profile before assigning it.", position, profile.Name, profile.ProfileId));
                        continue;
                    }

                    var differs = DifferencesFromProfile(row, profile, member);
                    if (differs.Count == 0)
                    {
                        action = ImportPlannedActions.ReuseProfile;
                    }
                    else
                    {
                        var decision = ImportResolutions.Normalize(row.Resolution);
                        if (decision == null)
                        {
                            conflict++;
                            results.Add(new PlanResult(row.ImportRowId, row.RowNumber, RepresentativeImportRowStatuses.Conflict, null, profile.ProfileId, position, profile.Name,
                                RepresentativeImportRowRules.ProfileDiffers,
                                Clip($"Candidate {code} already exists with different {string.Join(", ", differs)}. Choose: use the stored profile, update it with this file's values, or skip the row.", 1000)));
                            continue;
                        }
                        if (decision == ImportResolutions.Skip)
                        {
                            skipped++;
                            results.Add(new PlanResult(row.ImportRowId, row.RowNumber, RepresentativeImportRowStatuses.Skipped, null, profile.ProfileId, position, profile.Name,
                                null, "Skipped by the administrator."));
                            continue;
                        }
                        if (decision == ImportResolutions.UpdateProfile)
                        {
                            if (profile.InOpenEvent)
                            {
                                results.Add(Bad(RepresentativeImportRowRules.ProfileInOpenEvent,
                                    $"Candidate {code} is on the roster of an election that is open now; the profile cannot be changed. Use the stored profile instead.", position, profile.Name, profile.ProfileId));
                                continue;
                            }
                            if (profile.HasVotes && differs.Contains("name"))
                            {
                                results.Add(Bad(RepresentativeImportRowRules.ProfileHasVotes,
                                    $"Candidate {code} has received votes in a past election, so the name cannot be changed. Use the stored profile instead.", position, profile.Name, profile.ProfileId));
                                continue;
                            }
                            action = ImportPlannedActions.UpdateProfile;
                        }
                        else action = ImportPlannedActions.ReuseProfile;      // USE_EXISTING
                    }

                    // 6. Already on this roster at this position?
                    if (existingAssignments.Contains((profile.ProfileId, positionKey)))
                    {
                        results.Add(Bad(RepresentativeImportRowRules.AlreadyAssigned,
                            $"Candidate {code} is already on this election's roster for '{position}'.", position, profile.Name, profile.ProfileId));
                        continue;
                    }
                }

                // 7. Printed ballot number unique within the position.
                if (row.BallotNumber.HasValue)
                {
                    var key = (positionKey, row.BallotNumber.Value);
                    if (existingBallots.Contains(key) || plannedBallots.Contains(key))
                    {
                        results.Add(Bad(RepresentativeImportRowRules.BallotNumberTaken,
                            $"Ballot number {row.BallotNumber} is already used at '{position}'.", position, resolvedName, profile?.ProfileId));
                        continue;
                    }
                }

                // 8. Position capacity (MaxCandidates). Rows are admitted in spreadsheet order until the position is full.
                if (rule.MaxCandidates.HasValue)
                {
                    ctx.ApprovedByPosition.TryGetValue(position, out var approved);
                    plannedByPosition.TryGetValue(position, out var planned);
                    if (approved + planned + 1 > rule.MaxCandidates.Value)
                    {
                        results.Add(Bad(RepresentativeImportRowRules.PositionFull,
                            $"The candidate limit for '{position}' has been reached ({rule.MaxCandidates.Value} of {rule.MaxCandidates.Value}); this candidate was not added. " +
                            "Raise the limit in the event's position rules or remove candidates.", position, resolvedName, profile?.ProfileId));
                        continue;
                    }
                }

                // 9. Accepted: claim the slot.
                plannedByPosition[position] = (plannedByPosition.TryGetValue(position, out var n) ? n : 0) + 1;
                if (row.BallotNumber.HasValue) plannedBallots.Add((positionKey, row.BallotNumber.Value));

                valid++;
                if (action == ImportPlannedActions.CreateProfile) newCodes.Add(code);
                else if (action == ImportPlannedActions.UpdateProfile) { updated.Add(profile!.ProfileId); reused.Add(profile.ProfileId); }
                else reused.Add(profile!.ProfileId);

                results.Add(new PlanResult(row.ImportRowId, row.RowNumber, RepresentativeImportRowStatuses.Valid, action, profile?.ProfileId, position, resolvedName, null, null));
            }

            return (results, new PlanSummary(rows.Count, valid, invalid, conflict, skipped, newCodes.Count, reused.Count, updated.Count, valid));
        }

        // ── comparisons ──────────────────────────────────────────────────────────────────────────────────────
        private static bool SameText(string? a, string? b) =>
            string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Fields that the file gives for a code in two rows and that disagree. A blank cell never disagrees.</summary>
        private static List<string> DifferencesBetweenRows(PlanRow a, PlanRow b)
        {
            var d = new List<string>();
            void T(string label, string? x, string? y) { if (x != null && y != null && !SameText(x, y)) d.Add(label); }
            T("name", a.CandidateName, b.CandidateName);
            T("account code", a.AccountCode, b.AccountCode);
            T("cell number", a.CellNo, b.CellNo);
            T("telephone", a.TelNo, b.TelNo);
            T("civil status", a.CivilStatus, b.CivilStatus);
            T("gender", a.Gender, b.Gender);
            T("highest education", a.HighestEducation, b.HighestEducation);
            T("previous positions", a.PreviousPositions, b.PreviousPositions);
            T("special skills", a.SpecialSkills, b.SpecialSkills);
            if (a.DateOfBirth.HasValue && b.DateOfBirth.HasValue && a.DateOfBirth.Value.Date != b.DateOfBirth.Value.Date) d.Add("date of birth");
            return d;
        }

        /// <summary>Fields the file provides that differ from the stored profile. A blank cell means "no information", never "erase".</summary>
        private static List<string> DifferencesFromProfile(PlanRow r, PlanProfile p, PlanMember? member)
        {
            var d = new List<string>();
            void T(string label, string? file, string? stored) { if (file != null && !SameText(file, stored)) d.Add(label); }
            T("name", r.CandidateName, p.Name);
            T("cell number", r.CellNo, p.CellNo);
            T("telephone", r.TelNo, p.TelNo);
            T("civil status", r.CivilStatus, p.CivilStatus);
            T("gender", r.Gender, p.Gender);
            T("highest education", r.HighestEducation, p.HighestEducation);
            T("previous positions", r.PreviousPositions, p.PreviousPositions);
            T("special skills", r.SpecialSkills, p.SpecialSkills);
            if (r.DateOfBirth.HasValue && (!p.DateOfBirth.HasValue || r.DateOfBirth.Value.Date != p.DateOfBirth.Value.Date)) d.Add("date of birth");
            if (member != null && p.MemberId == null) d.Add("member link");
            return d;
        }

        private static string? Clip(string? s, int max) => s == null ? null : (s.Length <= max ? s : s.Substring(0, max - TooLongNote.Length) + TooLongNote);
    }
}
