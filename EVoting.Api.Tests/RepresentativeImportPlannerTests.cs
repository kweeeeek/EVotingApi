using System.Diagnostics;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// The decision logic of the candidate import as one pure function (Part 15 Representative tests 4-7, 9-11 and Position Rules test 5):
    /// 1,000 and more candidates, reuse of a stored profile, duplicate assignments, duplicate rows, the preview of invalid rows and the
    /// MaxCandidates limit. The same function judges the file at preview and again inside the commit transaction.
    /// </summary>
    public class RepresentativeImportPlannerTests
    {
        // ── builders ──────────────────────────────────────────────────────────────────────────────────────

        private static PlanRow Row(int n, string? code, string? name = null, string? position = "Delegate", string? account = null, int? ballot = null,
            string? resolution = null, string? cell = null, string? tel = null, string? civil = null, string? gender = null, DateTime? dob = null,
            string? education = null, string? previous = null, string? skills = null, string? fieldCodes = null, string? fieldMessage = null) =>
            new(ImportRowId: n * 10L, RowNumber: n, CandidateCode: code, CandidateName: name, AccountCode: account, Position: position,
                CellNo: cell, TelNo: tel, CivilStatus: civil, Gender: gender, DateOfBirth: dob, HighestEducation: education,
                PreviousPositions: previous, SpecialSkills: skills, Reason: null, BallotNumber: ballot,
                FieldErrorCodes: fieldCodes, FieldErrorMessage: fieldMessage, Resolution: resolution);

        private static PlanProfile Profile(int id, string code, string name, int? memberId = null, bool active = true, bool inOpenEvent = false,
            bool hasVotes = false, string? cell = null, DateTime? dob = null) =>
            new(ProfileId: id, Code: code, Name: name, MemberId: memberId, CellNo: cell, TelNo: null, CivilStatus: null, Gender: null, DateOfBirth: dob,
                HighestEducation: null, PreviousPositions: null, SpecialSkills: null, IsActive: active, InOpenEvent: inOpenEvent, HasVotes: hasVotes);

        private static PlanMember Member(int id, string account, string name, int? linkedProfileId = null, string? linkedProfileCode = null) =>
            new(id, account, name, linkedProfileId, linkedProfileCode);

        private static PlanContext Ctx(
            PlanRule[]? rules = null, PlanProfile[]? profiles = null, PlanMember[]? members = null,
            (int ProfileId, string Position)[]? assignments = null, (string Position, int Count)[]? approved = null,
            (string Position, int Ballot)[]? ballots = null)
        {
            var approvedByPosition = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, c) in approved ?? Array.Empty<(string, int)>()) approvedByPosition[p] = c;
            var byAccount = new Dictionary<string, PlanMember>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members ?? Array.Empty<PlanMember>()) byAccount[m.AccountCode] = m;
            return new PlanContext
            {
                Rules = rules ?? new[] { new PlanRule("Delegate", null), new PlanRule("Alternate", null) },
                ProfilesByCode = (profiles ?? Array.Empty<PlanProfile>()).ToDictionary(p => p.Code.ToUpperInvariant()),
                MembersByAccount = byAccount,
                ExistingAssignments = new HashSet<(int ProfileId, string Position)>(assignments ?? Array.Empty<(int, string)>()),
                ApprovedByPosition = approvedByPosition,
                ExistingBallotNumbers = new HashSet<(string Position, int Ballot)>(ballots ?? Array.Empty<(string, int)>()),
            };
        }

        private static (List<PlanResult> Results, PlanSummary Summary) Plan(PlanContext ctx, params PlanRow[] rows) =>
            RepresentativeImportPlanner.Plan(rows, ctx);

        private static PlanResult One(PlanContext ctx, PlanRow row) => Plan(ctx, row).Results.Single();

        // ── new candidates ────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void New_candidates_are_planned_as_new_profiles_and_new_assignments()
        {
            var (results, s) = Plan(Ctx(), Row(1, "A1", "Ana"), Row(2, "A2", "Ben"), Row(3, "A3", "Cai"));

            Assert.All(results, r =>
            {
                Assert.Equal("VALID", r.RowStatus);
                Assert.Equal(ImportPlannedActions.CreateProfile, r.PlannedAction);
                Assert.Null(r.MatchedProfileId);
                Assert.Equal("Delegate", r.CanonicalPosition);
                Assert.Null(r.ErrorCodes);
            });
            Assert.Equal(new PlanSummary(Total: 3, Valid: 3, Invalid: 0, Conflict: 0, Skipped: 0, NewProfiles: 3, ReusedProfiles: 0, UpdatedProfiles: 0, NewAssignments: 3), s);
            Assert.True(s.IsClean);
        }

        [Fact]
        public void The_position_is_matched_without_regard_to_case_and_takes_the_spelling_of_the_event_rule()
        {
            var r = One(Ctx(), Row(1, "A1", "Ana", position: "  delegate "));
            Assert.Equal("VALID", r.RowStatus);
            Assert.Equal("Delegate", r.CanonicalPosition);
        }

        [Fact]
        public void A_position_the_event_does_not_have_is_invalid_and_says_what_to_do()
        {
            var r = One(Ctx(), Row(1, "A1", "Ana", position: "Treasurer"));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal(RepresentativeImportRowRules.PositionNotConfigured, r.ErrorCodes);
            Assert.Contains("'Treasurer' is not configured for this event", r.ErrorMessage);
            Assert.Contains("Add it to the event's position rules first", r.ErrorMessage);
            Assert.Null(r.PlannedAction);
        }

        [Fact]
        public void A_new_candidate_without_a_name_and_without_a_member_is_invalid()
        {
            var r = One(Ctx(), Row(1, "A1", name: null));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("NAME_REQUIRED", r.ErrorCodes);
        }

        [Fact]
        public void A_new_candidate_takes_the_name_of_the_member_named_by_the_account_code()
        {
            var ctx = Ctx(members: new[] { Member(10, "0001234", "Maria Santos") });
            var r = One(ctx, Row(1, "A1", name: null, account: "0001234"));
            Assert.Equal("VALID", r.RowStatus);
            Assert.Equal("Maria Santos", r.ResolvedName);
        }

        [Fact]
        public void A_name_written_in_the_file_is_preferred_to_the_name_of_the_member()
        {
            var ctx = Ctx(members: new[] { Member(10, "0001234", "Maria Santos") });
            var r = One(ctx, Row(1, "A1", name: "Ria Santos", account: "0001234"));
            Assert.Equal("Ria Santos", r.ResolvedName);
        }

        [Fact]
        public void The_account_code_is_matched_without_regard_to_case()
        {
            var ctx = Ctx(members: new[] { Member(10, "AB-1234", "Maria Santos") });
            Assert.Equal("VALID", One(ctx, Row(1, "A1", null, account: "ab-1234")).RowStatus);
        }

        [Fact]
        public void An_account_code_that_belongs_to_no_active_member_is_invalid()
        {
            var r = One(Ctx(), Row(1, "A1", "Ana", account: "9999"));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("MEMBER_NOT_FOUND", r.ErrorCodes);
            Assert.Equal("No active member has account code '9999'.", r.ErrorMessage);
        }

        // ── the same candidate more than once in the file ─────────────────────────────────────────────────

        [Fact]
        public void The_same_candidate_twice_for_the_same_position_makes_the_second_row_a_duplicate()
        {
            var (results, s) = Plan(Ctx(), Row(1, "A1", "Ana"), Row(2, "A1", "Ana"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("INVALID", results[1].RowStatus);
            Assert.Equal("DUPLICATE_IN_FILE", results[1].ErrorCodes);
            Assert.Equal("Duplicate of row 1: candidate A1 is already listed for 'Delegate' in this file.", results[1].ErrorMessage);
            Assert.Equal(1, s.Valid);
            Assert.Equal(1, s.Invalid);
        }

        [Fact]
        public void A_duplicate_is_found_even_when_the_position_is_spelled_differently()
        {
            var (results, _) = Plan(Ctx(), Row(1, "A1", "Ana", position: "Delegate"), Row(2, "A1", "Ana", position: "DELEGATE"));
            Assert.Equal("DUPLICATE_IN_FILE", results[1].ErrorCodes);
        }

        [Fact]
        public void The_same_person_for_two_positions_is_two_assignments_of_one_profile()
        {
            var (results, s) = Plan(Ctx(), Row(1, "A1", "Ana", position: "Delegate"), Row(2, "A1", "Ana", position: "Alternate"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("VALID", results[1].RowStatus);
            Assert.Equal(1, s.NewProfiles);
            Assert.Equal(2, s.NewAssignments);
        }

        [Fact]
        public void One_code_for_two_different_people_is_refused_and_the_differing_fields_are_named()
        {
            var (results, _) = Plan(Ctx(),
                Row(1, "A1", "Ana", position: "Delegate", cell: "0917 111 1111"),
                Row(2, "A1", "Anna", position: "Alternate", cell: "0917 222 2222"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("INVALID", results[1].RowStatus);
            Assert.Equal("CODE_CONFLICT_IN_FILE", results[1].ErrorCodes);
            Assert.Equal("Candidate code A1 is also used in row 1 with different name, cell number. One code identifies exactly one person.", results[1].ErrorMessage);
        }

        [Fact]
        public void A_blank_cell_never_disagrees_with_a_filled_one_and_case_is_ignored()
        {
            var (results, _) = Plan(Ctx(),
                Row(1, "A1", "Ana Reyes", position: "Delegate", cell: "0917 111 1111"),
                Row(2, "A1", "ANA REYES", position: "Alternate", cell: null));
            Assert.Equal("VALID", results[1].RowStatus);
        }

        [Fact]
        public void The_date_of_birth_is_compared_by_date_only()
        {
            var (same, _) = Plan(Ctx(),
                Row(1, "A1", "Ana", "Delegate", dob: new DateTime(1990, 5, 6, 0, 0, 0)),
                Row(2, "A1", "Ana", "Alternate", dob: new DateTime(1990, 5, 6, 23, 59, 0)));
            Assert.Equal("VALID", same[1].RowStatus);

            var (other, _) = Plan(Ctx(),
                Row(1, "A1", "Ana", "Delegate", dob: new DateTime(1990, 5, 6)),
                Row(2, "A1", "Ana", "Alternate", dob: new DateTime(1990, 5, 7)));
            Assert.Equal("CODE_CONFLICT_IN_FILE", other[1].ErrorCodes);
            Assert.Contains("date of birth", other[1].ErrorMessage);
        }

        [Fact]
        public void Two_different_codes_naming_the_same_member_are_refused_on_the_second_row()
        {
            var ctx = Ctx(members: new[] { Member(10, "0001234", "Maria Santos") });
            var (results, _) = Plan(ctx,
                Row(1, "A1", null, "Delegate", account: "0001234"),
                Row(2, "A2", null, "Alternate", account: "0001234"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("INVALID", results[1].RowStatus);
            Assert.Equal("MEMBER_ALREADY_LINKED", results[1].ErrorCodes);
            Assert.Equal("Member '0001234' is also used by candidate A1 in row 1. A member can have only one candidate profile.", results[1].ErrorMessage);
        }

        [Fact]
        public void The_same_code_and_member_on_two_rows_is_fine()
        {
            var ctx = Ctx(members: new[] { Member(10, "0001234", "Maria Santos") });
            var (results, _) = Plan(ctx,
                Row(1, "A1", null, "Delegate", account: "0001234"),
                Row(2, "A1", null, "Alternate", account: "0001234"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("VALID", results[1].RowStatus);
        }

        // ── members that are linked to a profile already ──────────────────────────────────────────────────

        [Fact]
        public void A_member_already_linked_to_another_candidate_cannot_be_given_a_second_profile()
        {
            var ctx = Ctx(members: new[] { Member(10, "0001234", "Maria Santos", linkedProfileId: 5, linkedProfileCode: "OLD-1") });
            var r = One(ctx, Row(1, "A1", "Maria", account: "0001234"));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("MEMBER_ALREADY_LINKED", r.ErrorCodes);
            Assert.Equal("Member '0001234' is already linked to candidate OLD-1. A member can have only one candidate profile.", r.ErrorMessage);
        }

        [Fact]
        public void The_profile_a_member_is_linked_to_can_be_used_again_with_the_same_member()
        {
            var ctx = Ctx(
                profiles: new[] { Profile(5, "OLD-1", "Maria Santos", memberId: 10) },
                members: new[] { Member(10, "0001234", "Maria Santos", linkedProfileId: 5, linkedProfileCode: "OLD-1") });
            var r = One(ctx, Row(1, "OLD-1", null, account: "0001234"));
            Assert.Equal("VALID", r.RowStatus);
            Assert.Equal(ImportPlannedActions.ReuseProfile, r.PlannedAction);
            Assert.Equal(5, r.MatchedProfileId);
        }

        [Fact]
        public void A_stored_profile_linked_to_one_member_cannot_be_moved_to_another_by_an_import()
        {
            var ctx = Ctx(
                profiles: new[] { Profile(5, "A1", "Ana", memberId: 11) },
                members: new[] { Member(10, "0001234", "Maria Santos") });
            var r = One(ctx, Row(1, "A1", "Ana", account: "0001234"));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("MEMBER_ALREADY_LINKED", r.ErrorCodes);
            Assert.Contains("an import cannot change that link", r.ErrorMessage);
            Assert.Equal(5, r.MatchedProfileId);
        }

        // ── stored profiles: reuse, differences and the administrator's decision ──────────────────────────

        [Fact]
        public void A_stored_profile_that_agrees_with_the_file_is_reused()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes", cell: "0917 000 0001") });
            var (results, s) = Plan(ctx, Row(1, "A1", "ana reyes", cell: "0917 000 0001"));      // codes reach the planner already normalised; the name is compared ignoring case
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal(ImportPlannedActions.ReuseProfile, results[0].PlannedAction);
            Assert.Equal(5, results[0].MatchedProfileId);
            Assert.Equal(0, s.NewProfiles);
            Assert.Equal(1, s.ReusedProfiles);
            Assert.Equal(0, s.UpdatedProfiles);
        }

        [Fact]
        public void A_row_that_gives_only_the_code_and_the_position_reuses_the_stored_profile_unchanged()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") });
            var r = One(ctx, Row(1, "A1", name: null));
            Assert.Equal("VALID", r.RowStatus);
            Assert.Equal(ImportPlannedActions.ReuseProfile, r.PlannedAction);
            Assert.Equal("Ana Reyes", r.ResolvedName);
        }

        [Fact]
        public void A_stored_profile_can_be_reused_in_a_second_election_it_is_not_on_yet()
        {
            // Part 15 Representative test 6: the profile exists (from another event) and this event's roster has nobody yet
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") }, assignments: Array.Empty<(int, string)>());
            var r = One(ctx, Row(1, "A1", "Ana Reyes"));
            Assert.Equal("VALID", r.RowStatus);
            Assert.Equal(ImportPlannedActions.ReuseProfile, r.PlannedAction);
        }

        [Fact]
        public void A_stored_profile_that_differs_from_the_file_waits_for_the_decision_of_the_administrator()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") });
            var (results, s) = Plan(ctx, Row(1, "A1", "Ana R. Reyes"));
            var r = results.Single();
            Assert.Equal("CONFLICT", r.RowStatus);
            Assert.Equal("PROFILE_DIFFERS", r.ErrorCodes);
            Assert.Equal("Candidate A1 already exists with different name. Choose: use the stored profile, update it with this file's values, or skip the row.", r.ErrorMessage);
            Assert.Equal(5, r.MatchedProfileId);
            Assert.Equal("Ana Reyes", r.ResolvedName);
            Assert.Equal(1, s.Conflict);
            Assert.False(s.IsClean);
        }

        [Theory]
        [InlineData("cell number")]
        [InlineData("date of birth")]
        [InlineData("member link")]
        public void The_differences_are_named_in_the_conflict(string field)
        {
            var ctx = Ctx(
                profiles: new[] { Profile(5, "A1", "Ana Reyes", cell: "0917 000 0001", dob: new DateTime(1980, 1, 1)) },
                members: new[] { Member(10, "0001234", "Maria Santos") });
            var row = field switch
            {
                "cell number" => Row(1, "A1", "Ana Reyes", cell: "0917 999 9999"),
                "date of birth" => Row(1, "A1", "Ana Reyes", dob: new DateTime(1981, 1, 1)),
                _ => Row(1, "A1", "Ana Reyes", account: "0001234"),          // the stored profile has no member link yet
            };
            var r = One(ctx, row);
            Assert.Equal("CONFLICT", r.RowStatus);
            Assert.Contains("different " + field, r.ErrorMessage);
        }

        [Fact]
        public void A_date_of_birth_that_differs_only_in_the_time_of_day_is_no_difference()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes", dob: new DateTime(1980, 1, 1)) });
            var r = One(ctx, Row(1, "A1", "Ana Reyes", dob: new DateTime(1980, 1, 1, 17, 30, 0)));
            Assert.Equal("VALID", r.RowStatus);
        }

        [Fact]
        public void Use_existing_keeps_the_stored_profile()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") });
            var (results, s) = Plan(ctx, Row(1, "A1", "Ana R. Reyes", resolution: "USE_EXISTING"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal(ImportPlannedActions.ReuseProfile, results[0].PlannedAction);
            Assert.Equal(1, s.ReusedProfiles);
            Assert.Equal(0, s.UpdatedProfiles);
        }

        [Fact]
        public void Update_profile_takes_the_values_of_the_file_and_counts_as_an_update()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") });
            var (results, s) = Plan(ctx, Row(1, "A1", "Ana R. Reyes", resolution: "update profile"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal(ImportPlannedActions.UpdateProfile, results[0].PlannedAction);
            Assert.Equal("Ana R. Reyes", results[0].ResolvedName);
            Assert.Equal(1, s.UpdatedProfiles);
            Assert.Equal(1, s.ReusedProfiles);
        }

        [Fact]
        public void Skip_leaves_the_row_out_and_it_neither_counts_as_valid_nor_blocks_the_file()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") });
            var (results, s) = Plan(ctx, Row(1, "A1", "Ana R. Reyes", resolution: "SKIP"), Row(2, "A2", "Ben"));
            Assert.Equal("SKIPPED", results[0].RowStatus);
            Assert.Equal("Skipped by the administrator.", results[0].ErrorMessage);
            Assert.Null(results[0].ErrorCodes);
            Assert.Equal(1, s.Skipped);
            Assert.Equal(1, s.Valid);
            Assert.True(s.IsClean);
        }

        [Fact]
        public void An_unknown_resolution_text_is_the_same_as_no_decision()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") });
            Assert.Equal("CONFLICT", One(ctx, Row(1, "A1", "Ana R. Reyes", resolution: "overwrite")).RowStatus);
        }

        [Fact]
        public void A_profile_on_the_roster_of_an_open_election_cannot_be_updated_but_can_still_be_reused()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes", inOpenEvent: true) });

            var update = One(ctx, Row(1, "A1", "Ana R. Reyes", resolution: "UPDATE_PROFILE"));
            Assert.Equal("INVALID", update.RowStatus);
            Assert.Equal("PROFILE_IN_OPEN_EVENT", update.ErrorCodes);

            var reuse = One(ctx, Row(1, "A1", "Ana R. Reyes", resolution: "USE_EXISTING"));
            Assert.Equal("VALID", reuse.RowStatus);
        }

        [Fact]
        public void A_profile_with_votes_in_a_past_election_cannot_change_its_name_but_can_change_other_details()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes", hasVotes: true, cell: "0917 000 0001") });

            var rename = One(ctx, Row(1, "A1", "Ana R. Reyes", resolution: "UPDATE_PROFILE"));
            Assert.Equal("INVALID", rename.RowStatus);
            Assert.Equal("PROFILE_HAS_VOTES", rename.ErrorCodes);

            var phone = One(ctx, Row(1, "A1", "Ana Reyes", cell: "0917 999 9999", resolution: "UPDATE_PROFILE"));
            Assert.Equal("VALID", phone.RowStatus);
            Assert.Equal(ImportPlannedActions.UpdateProfile, phone.PlannedAction);
        }

        [Fact]
        public void A_deactivated_profile_cannot_be_assigned()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes", active: false) });
            var r = One(ctx, Row(1, "A1", "Ana Reyes"));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("PROFILE_INACTIVE", r.ErrorCodes);
            Assert.Contains("is deactivated", r.ErrorMessage);
        }

        // ── the roster of the event ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_candidate_already_on_the_roster_for_that_position_is_rejected_whatever_the_case_of_the_position()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") }, assignments: new[] { (5, "delegate") });
            var r = One(ctx, Row(1, "A1", "Ana Reyes", position: "Delegate"));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("ALREADY_ASSIGNED", r.ErrorCodes);
            Assert.Equal("Candidate A1 is already on this election's roster for 'Delegate'.", r.ErrorMessage);
        }

        [Fact]
        public void The_same_candidate_on_the_roster_for_another_position_may_be_assigned_to_this_one()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "A1", "Ana Reyes") }, assignments: new[] { (5, "Alternate") });
            Assert.Equal("VALID", One(ctx, Row(1, "A1", "Ana Reyes", position: "Delegate")).RowStatus);
        }

        [Fact]
        public void A_ballot_number_already_used_at_the_position_is_rejected()
        {
            var ctx = Ctx(ballots: new[] { ("delegate", 5) });
            var r = One(ctx, Row(1, "A1", "Ana", ballot: 5));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("BALLOT_NUMBER_TAKEN", r.ErrorCodes);
            Assert.Equal("Ballot number 5 is already used at 'Delegate'.", r.ErrorMessage);
        }

        [Fact]
        public void The_same_ballot_number_twice_in_the_file_at_one_position_is_rejected_on_the_second_row_only()
        {
            var (results, _) = Plan(Ctx(), Row(1, "A1", "Ana", ballot: 7), Row(2, "A2", "Ben", ballot: 7));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("BALLOT_NUMBER_TAKEN", results[1].ErrorCodes);
        }

        [Fact]
        public void The_same_ballot_number_at_two_different_positions_is_fine()
        {
            var (results, _) = Plan(Ctx(), Row(1, "A1", "Ana", "Delegate", ballot: 7), Row(2, "A2", "Ben", "Alternate", ballot: 7));
            Assert.All(results, r => Assert.Equal("VALID", r.RowStatus));
        }

        [Fact]
        public void A_rejected_row_does_not_keep_its_ballot_number_from_a_later_row()
        {
            // row 1 is invalid for another reason, so number 7 stays free for row 2
            var (results, _) = Plan(Ctx(), Row(1, "A1", null, ballot: 7), Row(2, "A2", "Ben", ballot: 7));
            Assert.Equal("INVALID", results[0].RowStatus);
            Assert.Equal("VALID", results[1].RowStatus);
        }

        // ── MaxCandidates ─────────────────────────────────────────────────────────────────────────────────

        private static PlanContext Limited(int limit, int alreadyApproved = 0) =>
            Ctx(rules: new[] { new PlanRule("Delegate", limit), new PlanRule("Alternate", null) },
                approved: alreadyApproved > 0 ? new[] { ("Delegate", alreadyApproved) } : null);

        [Fact]
        public void Rows_are_admitted_in_spreadsheet_order_until_the_position_is_full()
        {
            var (results, s) = Plan(Limited(limit: 2, alreadyApproved: 1), Row(1, "A1", "Ana"), Row(2, "A2", "Ben"), Row(3, "A3", "Cai"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("INVALID", results[1].RowStatus);
            Assert.Equal("POSITION_FULL", results[1].ErrorCodes);
            Assert.Equal("POSITION_FULL", results[2].ErrorCodes);
            Assert.Equal("The candidate limit for 'Delegate' has been reached (2 of 2); this candidate was not added. Raise the limit in the event's position rules or remove candidates.",
                results[1].ErrorMessage);
            Assert.Equal(1, s.Valid);
            Assert.Equal(2, s.Invalid);
        }

        [Fact]
        public void The_row_number_and_not_the_order_of_the_list_decides_who_gets_the_last_place()
        {
            var (results, _) = Plan(Limited(limit: 1), Row(3, "A3", "Cai"), Row(1, "A1", "Ana"), Row(2, "A2", "Ben"));
            Assert.Equal("1,2,3", string.Join(",", results.Select(r => r.RowNumber)));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("POSITION_FULL", results[1].ErrorCodes);
            Assert.Equal("POSITION_FULL", results[2].ErrorCodes);
        }

        [Fact]
        public void A_position_filled_to_exactly_its_limit_is_accepted()
        {
            var (results, _) = Plan(Limited(limit: 2), Row(1, "A1", "Ana"), Row(2, "A2", "Ben"));
            Assert.All(results, r => Assert.Equal("VALID", r.RowStatus));
        }

        [Fact]
        public void A_position_that_is_already_at_its_limit_takes_nobody_new()
        {
            var r = One(Limited(limit: 3, alreadyApproved: 3), Row(1, "A1", "Ana"));
            Assert.Equal("POSITION_FULL", r.ErrorCodes);
        }

        [Fact]
        public void The_limit_of_one_position_does_not_affect_another_position()
        {
            var (results, _) = Plan(Limited(limit: 1), Row(1, "A1", "Ana", "Delegate"), Row(2, "A2", "Ben", "Delegate"), Row(3, "A3", "Cai", "Alternate"), Row(4, "A4", "Dee", "Alternate"));
            Assert.Equal("VALID", results[0].RowStatus);
            Assert.Equal("POSITION_FULL", results[1].ErrorCodes);
            Assert.Equal("VALID", results[2].RowStatus);
            Assert.Equal("VALID", results[3].RowStatus);
        }

        [Fact]
        public void Rows_that_are_invalid_or_skipped_do_not_use_up_a_place()
        {
            var ctx = Ctx(rules: new[] { new PlanRule("Delegate", 1) }, profiles: new[] { Profile(5, "A9", "Old Name") });
            var (results, _) = Plan(ctx,
                Row(1, "A1", null),                                   // invalid: no name
                Row(2, "A2", "Ben", fieldCodes: "TOO_LONG"),          // invalid at staging
                Row(3, "A9", "New Name", resolution: "SKIP"),         // skipped
                Row(4, "A4", "Dee"));                                 // the only one that really takes the place
            Assert.Equal("INVALID,INVALID,SKIPPED,VALID", string.Join(",", results.Select(r => r.RowStatus)));
        }

        [Fact]
        public void Without_a_limit_any_number_of_candidates_is_admitted()
        {
            var rows = Enumerable.Range(1, 300).Select(i => Row(i, "C" + i, "Name " + i)).ToArray();
            var (_, s) = Plan(Ctx(rules: new[] { new PlanRule("Delegate", null) }), rows);
            Assert.Equal(300, s.Valid);
        }

        // ── rows that were wrong already when they were staged ────────────────────────────────────────────

        [Fact]
        public void A_row_with_field_errors_keeps_them_as_they_were_stored()
        {
            var r = One(Ctx(), Row(1, "A1", "Ana", fieldCodes: "CODE_INVALID,TOO_LONG", fieldMessage: "Candidate code is required. Name is too long."));
            Assert.Equal("INVALID", r.RowStatus);
            Assert.Equal("CODE_INVALID,TOO_LONG", r.ErrorCodes);
            Assert.Equal("Candidate code is required. Name is too long.", r.ErrorMessage);
            Assert.Null(r.PlannedAction);
        }

        [Fact]
        public void Stored_error_text_is_cut_to_the_size_of_the_columns()
        {
            var r = One(Ctx(), Row(1, "A1", "Ana", fieldCodes: new string('C', 900), fieldMessage: new string('m', 5000)));
            Assert.Equal(500, r.ErrorCodes!.Length);
            Assert.Equal(1000, r.ErrorMessage!.Length);
            Assert.EndsWith("…", r.ErrorMessage);
        }

        // ── the summary and the function as a whole ───────────────────────────────────────────────────────

        [Fact]
        public void Every_row_gets_exactly_one_result_and_the_counts_add_up()
        {
            var ctx = Ctx(
                profiles: new[] { Profile(5, "P1", "Stored One"), Profile(6, "P2", "Stored Two") },
                rules: new[] { new PlanRule("Delegate", 3), new PlanRule("Alternate", null) });
            var rows = new[]
            {
                Row(1, "N1", "New One"),                                   // valid
                Row(2, "N1", "New One"),                                   // duplicate -> invalid
                Row(3, "P1", "Stored One"),                                // reuse -> valid
                Row(4, "P2", "Other Name"),                                // conflict
                Row(5, "N5", "New Five", position: "Nowhere"),             // invalid
                Row(6, "P2", "Other Name", position: "Alternate", resolution: "SKIP"),   // skipped
                Row(7, "N7", "New Seven"),                                 // valid
                Row(8, "N8", "New Eight"),                                 // POSITION_FULL (limit 3: N1, P1, N7 take the places)
            };
            var (results, s) = Plan(ctx, rows);

            Assert.Equal(rows.Length, results.Count);
            Assert.Equal(rows.Length, s.Total);
            Assert.Equal(s.Total, s.Valid + s.Invalid + s.Conflict + s.Skipped);
            Assert.Equal(3, s.Valid);
            Assert.Equal(3, s.Invalid);
            Assert.Equal(1, s.Conflict);
            Assert.Equal(1, s.Skipped);
            Assert.Equal(s.Valid, s.NewAssignments);
            Assert.Equal(2, s.NewProfiles);
            Assert.Equal(1, s.ReusedProfiles);
            Assert.False(s.IsClean);
            Assert.Equal(rows.Select(r => r.ImportRowId), results.Select(r => r.ImportRowId));
        }

        [Fact]
        public void Judging_the_same_file_twice_gives_the_same_verdicts()
        {
            var ctx = Ctx(profiles: new[] { Profile(5, "P1", "Stored One") }, rules: new[] { new PlanRule("Delegate", 2), new PlanRule("Alternate", null) });
            var rows = new[] { Row(1, "N1", "A"), Row(2, "N1", "A"), Row(3, "P1", "Different"), Row(4, "N4", "D"), Row(5, "N5", "E") };

            var first = RepresentativeImportPlanner.Plan(rows, ctx);
            var second = RepresentativeImportPlanner.Plan(rows, ctx);

            Assert.Equal(first.Results.Count, second.Results.Count);
            for (int i = 0; i < first.Results.Count; i++)
                Assert.Equal(first.Results[i], second.Results[i]);
            Assert.Equal(first.Summary, second.Summary);
        }

        [Fact]
        public void An_empty_file_gives_an_empty_clean_summary()
        {
            var (results, s) = Plan(Ctx());
            Assert.Empty(results);
            Assert.Equal(0, s.Total);
            Assert.True(s.IsClean);
        }

        // ── 1,000 and more candidates (Part 15 Representative tests 4 and 5) ──────────────────────────────

        [Theory]
        [InlineData(1000)]
        [InlineData(1001)]
        [InlineData(2500)]
        [InlineData(20000)]
        public void A_roster_of_one_thousand_and_far_more_candidates_is_judged_in_one_pass(int count)
        {
            var rows = Enumerable.Range(1, count).Select(i => Row(i, "C" + i.ToString("00000"), "Candidate " + i, ballot: i)).ToArray();
            var watch = Stopwatch.StartNew();
            var (results, s) = Plan(Ctx(rules: new[] { new PlanRule("Delegate", null) }), rows);
            watch.Stop();

            Assert.Equal(count, results.Count);
            Assert.Equal(count, s.Valid);
            Assert.Equal(count, s.NewProfiles);
            Assert.Equal(0, s.Invalid);
            Assert.True(s.IsClean);
            // a generous guard against an accidentally quadratic loop (the real time is a few tens of milliseconds)
            Assert.True(watch.ElapsedMilliseconds < 5000, "planning " + count + " rows took " + watch.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void A_limit_below_the_size_of_a_large_file_admits_exactly_the_first_rows()
        {
            var rows = Enumerable.Range(1, 2500).Select(i => Row(i, "C" + i, "Candidate " + i)).ToArray();
            var (results, s) = Plan(Ctx(rules: new[] { new PlanRule("Delegate", 1000) }), rows);

            Assert.Equal(1000, s.Valid);
            Assert.Equal(1500, s.Invalid);
            Assert.All(results.Take(1000), r => Assert.Equal("VALID", r.RowStatus));
            Assert.All(results.Skip(1000), r => Assert.Equal("POSITION_FULL", r.ErrorCodes));
        }

        [Fact]
        public void A_large_file_with_many_stored_profiles_and_many_members_is_judged_correctly()
        {
            var profiles = Enumerable.Range(1, 1500).Select(i => Profile(i, "C" + i, "Candidate " + i)).ToArray();
            var members = Enumerable.Range(1, 1500).Select(i => Member(100000 + i, "AC" + i, "Member " + i)).ToArray();
            var rows = Enumerable.Range(1, 3000).Select(i => Row(i, "C" + i, "Candidate " + i)).ToArray();

            var (_, s) = Plan(Ctx(rules: new[] { new PlanRule("Delegate", null) }, profiles: profiles, members: members), rows);

            Assert.Equal(3000, s.Valid);
            Assert.Equal(1500, s.NewProfiles);          // codes 1501-3000 have no stored profile
            Assert.Equal(1500, s.ReusedProfiles);       // codes 1-1500 are stored profiles
        }
    }
}
