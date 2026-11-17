using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// Position rules of an event (Part 15 "Position Rules" tests 1-7): which positions exist, how many a voter may / must pick, how many
    /// candidates may take part and how many seats there are. One validator is used for General Assembly and Representative events.
    /// </summary>
    public class PositionRuleValidatorTests
    {
        private static readonly IReadOnlyDictionary<string, int> NoAssignments =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private static PositionRuleRow Rule(string position, int min = 0, int max = 1, int seats = 1, int? maxCandidates = null) =>
            new() { Position = position, MinVotes = min, MaxVotes = max, TotalSeats = seats, MaxCandidates = maxCandidates };

        private static PositionRuleValidation Rep(params PositionRuleRow[] rules) =>
            PositionRuleValidator.Validate(rules.ToList(), EventTypes.Representative, NoAssignments);

        private static PositionRuleValidation Ga(params PositionRuleRow[] rules) =>
            PositionRuleValidator.Validate(rules.ToList(), EventTypes.GeneralAssembly, NoAssignments);

        private static IReadOnlyDictionary<string, int> Assigned(params (string Position, int Count)[] items)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, c) in items) d[p] = c;
            return d;
        }

        // ── the rule set as a whole ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_complete_representative_rule_set_is_accepted_without_remarks()
        {
            var r = Rep(Rule("Delegate", min: 1, max: 3, seats: 3, maxCandidates: 10), Rule("Alternate Delegate", 0, 2, 2));
            Assert.True(r.Ok);
            Assert.Empty(r.Errors);
            Assert.Empty(r.Warnings);
        }

        [Fact]
        public void An_empty_rule_set_is_refused()
        {
            var r = Rep();
            Assert.False(r.Ok);
            Assert.Equal("Add at least one position.", r.Errors["rules"]);
        }

        [Fact]
        public void A_missing_rule_list_is_refused_not_a_crash()
        {
            var r = PositionRuleValidator.Validate(null!, EventTypes.Representative, NoAssignments);
            Assert.False(r.Ok);
            Assert.True(r.Errors.ContainsKey("rules"));
        }

        [Fact]
        public void At_most_50_positions_per_event()
        {
            var fifty = Enumerable.Range(1, 50).Select(i => Rule("Position " + i)).ToArray();
            Assert.True(Rep(fifty).Ok);

            var fiftyOne = Enumerable.Range(1, 51).Select(i => Rule("Position " + i)).ToArray();
            var r = Rep(fiftyOne);
            Assert.False(r.Ok);
            Assert.Contains("at most 50", r.Errors["rules"]);
        }

        // ── the position name ─────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_blank_position_is_refused(string? position)
        {
            var r = Rep(Rule(position!));
            Assert.Equal("Row 1: select a position.", r.Errors["rules[0].position"]);
        }

        [Fact]
        public void A_position_name_is_limited_to_100_characters()
        {
            Assert.True(Rep(Rule(new string('P', 100))).Ok);

            var r = Rep(Rule(new string('P', 101)));
            Assert.Contains("longer than 100", r.Errors["rules[0].position"]);
        }

        [Fact]
        public void Representative_positions_are_free_text_and_are_cleaned_in_place()
        {
            var row = Rule("  Youth   Delegate ");
            var r = Rep(row);
            Assert.True(r.Ok);
            Assert.Equal("Youth Delegate", row.Position);
        }

        [Fact]
        public void A_General_Assembly_position_must_be_one_of_the_four_fixed_positions()
        {
            var r = Ga(Rule("Delegate"));
            Assert.False(r.Ok);
            Assert.Contains("is not a General Assembly position", r.Errors["rules[0].position"]);
            Assert.Contains("Board of Director", r.Errors["rules[0].position"]);
        }

        [Fact]
        public void A_General_Assembly_position_takes_the_canonical_spelling()
        {
            var row = Rule("board of DIRECTOR", min: 0, max: 5, seats: 5);
            var r = Ga(row);
            Assert.True(r.Ok);
            Assert.Equal("Board of Director", row.Position);
        }

        [Fact]
        public void All_four_General_Assembly_positions_are_accepted_together()
        {
            var r = Ga(Rule("Board of Director", 0, 5, 5), Rule("Election Committee", 0, 3, 3), Rule("Audit Committee", 0, 3, 3), Rule("Secretary", 0, 1, 1));
            Assert.True(r.Ok);
        }

        [Fact]
        public void A_position_can_appear_only_once_and_case_does_not_make_it_a_different_position()
        {
            var r = Rep(Rule("Delegate"), Rule("Alternate"), Rule("DELEGATE"));
            Assert.False(r.Ok);
            Assert.False(r.Errors.ContainsKey("rules[0].position"));
            Assert.Contains("already configured in row 1", r.Errors["rules[2].position"]);
        }

        // ── minimum / maximum selections ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Minimum_votes_cannot_be_negative_but_zero_is_allowed()
        {
            Assert.True(Rep(Rule("Delegate", min: 0, max: 1)).Ok);
            var r = Rep(Rule("Delegate", min: -1, max: 1));
            Assert.Contains("cannot be negative", r.Errors["rules[0].minVotes"]);
        }

        [Fact]
        public void Maximum_votes_is_at_least_1_and_at_most_500()
        {
            var low = Rep(Rule("Delegate", min: 0, max: 0));
            Assert.Contains("at least 1", low.Errors["rules[0].maxVotes"]);

            Assert.True(Rep(Rule("Delegate", min: 0, max: 1)).Ok);
            Assert.True(Rep(Rule("Delegate", min: 0, max: 500)).Ok);

            var high = Rep(Rule("Delegate", min: 0, max: 501));
            Assert.Contains("cannot exceed 500", high.Errors["rules[0].maxVotes"]);
        }

        [Fact]
        public void Minimum_votes_cannot_be_greater_than_maximum_votes()
        {
            var r = Rep(Rule("Delegate", min: 4, max: 3));
            Assert.Contains("minimum votes (4) cannot be greater than maximum votes (3)", r.Errors["rules[0].minVotes"]);
        }

        [Fact]
        public void Minimum_equal_to_maximum_is_allowed_for_an_exact_number_of_picks()
        {
            Assert.True(Rep(Rule("Delegate", min: 3, max: 3, seats: 3)).Ok);
        }

        // ── seats ─────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Total_seats_is_at_least_1_and_at_most_5000()
        {
            Assert.Contains("at least 1", Rep(Rule("Delegate", seats: 0)).Errors["rules[0].totalSeats"]);
            Assert.True(Rep(Rule("Delegate", seats: 1)).Ok);
            Assert.True(Rep(Rule("Delegate", seats: 5000)).Ok);
            Assert.Contains("cannot exceed 5000", Rep(Rule("Delegate", seats: 5001)).Errors["rules[0].totalSeats"]);
        }

        // ── maximum candidates (MaxCandidates) ────────────────────────────────────────────────────────────

        [Fact]
        public void No_candidate_limit_is_allowed_and_means_unlimited()
        {
            var r = Rep(Rule("Delegate", min: 0, max: 5, seats: 5, maxCandidates: null));
            Assert.True(r.Ok);
            Assert.Empty(r.Warnings);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void A_candidate_limit_must_be_a_positive_number(int limit)
        {
            var r = Rep(Rule("Delegate", maxCandidates: limit));
            Assert.Contains("must be a positive number", r.Errors["rules[0].maxCandidates"]);
        }

        [Fact]
        public void A_candidate_limit_is_at_most_100000()
        {
            Assert.True(Rep(Rule("Delegate", maxCandidates: 100_000)).Ok);
            Assert.Contains("cannot exceed 100000", Rep(Rule("Delegate", maxCandidates: 100_001)).Errors["rules[0].maxCandidates"]);
        }

        [Fact]
        public void A_candidate_limit_below_the_minimum_votes_could_never_produce_a_valid_ballot()
        {
            var r = Rep(Rule("Delegate", min: 3, max: 5, seats: 3, maxCandidates: 2));
            Assert.Contains("no ballot could ever be valid", r.Errors["rules[0].maxCandidates"]);
        }

        [Fact]
        public void A_candidate_limit_equal_to_the_minimum_votes_is_allowed()
        {
            Assert.True(Rep(Rule("Delegate", min: 3, max: 3, seats: 3, maxCandidates: 3)).Ok);
        }

        [Fact]
        public void A_representative_event_cannot_get_a_limit_below_the_candidates_already_on_its_roster()
        {
            var r = PositionRuleValidator.Validate(new List<PositionRuleRow> { Rule("Delegate", min: 0, max: 5, seats: 5, maxCandidates: 4) },
                EventTypes.Representative, Assigned(("delegate", 6)));
            Assert.False(r.Ok);
            Assert.Contains("6 candidates are already assigned to 'Delegate', which is more than the limit of 4", r.Errors["rules[0].maxCandidates"]);
        }

        [Fact]
        public void A_limit_equal_to_the_candidates_already_on_the_roster_is_allowed()
        {
            var r = PositionRuleValidator.Validate(new List<PositionRuleRow> { Rule("Delegate", min: 0, max: 5, seats: 5, maxCandidates: 6) },
                EventTypes.Representative, Assigned(("Delegate", 6)));
            Assert.True(r.Ok);
        }

        [Fact]
        public void In_a_General_Assembly_event_a_limit_below_the_current_candidates_is_only_a_warning()
        {
            var r = PositionRuleValidator.Validate(new List<PositionRuleRow> { Rule("Secretary", min: 0, max: 1, seats: 1, maxCandidates: 2) },
                EventTypes.GeneralAssembly, Assigned(("Secretary", 5)));
            Assert.True(r.Ok);
            Assert.Single(r.Warnings);
            Assert.Contains("5 eligible approved candidates, more than the limit of 2", r.Warnings[0]);
            Assert.Contains("cannot be opened until this is corrected", r.Warnings[0]);
        }

        [Fact]
        public void A_limit_below_the_seats_or_below_the_maximum_votes_is_a_warning_not_an_error()
        {
            var r = Rep(Rule("Delegate", min: 0, max: 5, seats: 4, maxCandidates: 3));
            Assert.True(r.Ok);
            Assert.Equal(2, r.Warnings.Count);
            Assert.Contains(r.Warnings, w => w.Contains("lower than total seats (4)"));
            Assert.Contains(r.Warnings, w => w.Contains("lower than maximum votes (5)"));
        }

        [Fact]
        public void No_warning_when_the_limit_covers_the_seats_and_the_maximum_votes()
        {
            var r = Rep(Rule("Delegate", min: 0, max: 5, seats: 5, maxCandidates: 5));
            Assert.True(r.Ok);
            Assert.Empty(r.Warnings);
        }

        // ── several problems at once ──────────────────────────────────────────────────────────────────────

        [Fact]
        public void Every_problem_of_every_row_is_reported_in_one_answer_with_zero_based_keys()
        {
            var r = Rep(
                Rule("Delegate", min: 0, max: 1, seats: 1),               // row 1 fine
                Rule("Alternate", min: -1, max: 0, seats: 0),             // row 2: three field errors
                Rule("", min: 0, max: 1, seats: 1));                      // row 3: no position
            Assert.False(r.Ok);
            Assert.False(r.Errors.Keys.Any(k => k.StartsWith("rules[0]")));
            Assert.True(r.Errors.ContainsKey("rules[1].minVotes"));
            Assert.True(r.Errors.ContainsKey("rules[1].maxVotes"));
            Assert.True(r.Errors.ContainsKey("rules[1].totalSeats"));
            Assert.True(r.Errors.ContainsKey("rules[2].position"));
            Assert.Contains("Row 2:", r.Errors["rules[1].maxVotes"]);
            Assert.Contains("Row 3:", r.Errors["rules[2].position"]);
        }
    }

    public class EventLockPolicyTests
    {
        [Theory]
        [InlineData("Opened")]
        [InlineData("opened")]
        [InlineData("OPENED")]
        public void While_voting_is_open_rules_and_roster_are_locked(string status)
        {
            var l = EventLockPolicy.ForRulesAndRoster(status, EventTypes.Representative, null, 0);
            Assert.True(l.Locked);
            Assert.Equal(RepCodes.EventOpenLock, l.Code);
            Assert.Contains("Close it first", l.Reason);
        }

        [Fact]
        public void Once_a_ballot_exists_the_rules_are_frozen_even_after_the_election_closed()
        {
            var l = EventLockPolicy.ForRulesAndRoster("Closed", EventTypes.GeneralAssembly, null, 17);
            Assert.True(l.Locked);
            Assert.Equal(RepCodes.HasBallots, l.Code);
            Assert.Contains("17 ballot(s)", l.Reason);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(int.MaxValue)]
        public void A_single_ballot_is_enough_to_freeze_the_rules(int ballots)
        {
            // the boundary that matters most: the first ballot, not the second, ends editing (found by the mutation check of these tests)
            var l = EventLockPolicy.ForRulesAndRoster("Closed", EventTypes.Representative, null, ballots);
            Assert.True(l.Locked);
            Assert.Equal(RepCodes.HasBallots, l.Code);
            Assert.Contains($"{ballots} ballot(s)", l.Reason);
        }

        [Fact]
        public void No_ballot_at_all_does_not_freeze_the_rules()
        {
            var l = EventLockPolicy.ForRulesAndRoster("Closed", EventTypes.GeneralAssembly, null, 0);
            Assert.False(l.Locked);
            Assert.Null(l.Code);
        }

        [Fact]
        public void A_representative_roster_stays_locked_after_the_first_opening_until_an_administrator_unlocks_it()
        {
            var l = EventLockPolicy.ForRulesAndRoster("Closed", EventTypes.Representative, new DateTime(2026, 11, 4, 9, 30, 0), 0);
            Assert.True(l.Locked);
            Assert.Equal(RepCodes.RosterLocked, l.Code);
            Assert.Contains("2026-11-04 09:30", l.Reason);
            Assert.Contains("administrator can unlock", l.Reason);
        }

        [Fact]
        public void The_roster_lock_does_not_apply_to_a_General_Assembly_event()
        {
            var l = EventLockPolicy.ForRulesAndRoster("Closed", EventTypes.GeneralAssembly, new DateTime(2026, 11, 4, 9, 30, 0), 0);
            Assert.False(l.Locked);
            Assert.Null(l.Code);
        }

        [Theory]
        [InlineData("Closed")]
        [InlineData("Pending")]
        [InlineData("")]
        public void An_event_that_is_not_open_has_no_ballots_and_was_never_opened_can_be_edited(string status)
        {
            var l = EventLockPolicy.ForRulesAndRoster(status, EventTypes.Representative, null, 0);
            Assert.False(l.Locked);
            Assert.Null(l.Code);
            Assert.Null(l.Reason);
        }

        [Fact]
        public void Open_voting_is_reported_before_ballots_and_ballots_before_the_roster_lock()
        {
            var opened = EventLockPolicy.ForRulesAndRoster("Opened", EventTypes.Representative, DateTime.UtcNow, 5);
            Assert.Equal(RepCodes.EventOpenLock, opened.Code);

            var ballots = EventLockPolicy.ForRulesAndRoster("Closed", EventTypes.Representative, DateTime.UtcNow, 5);
            Assert.Equal(RepCodes.HasBallots, ballots.Code);
        }

        [Fact]
        public void The_shared_Open_value_means_not_locked()
        {
            Assert.False(EventLock.Open.Locked);
            Assert.Null(EventLock.Open.Code);
            Assert.Null(EventLock.Open.Reason);
        }
    }
}
