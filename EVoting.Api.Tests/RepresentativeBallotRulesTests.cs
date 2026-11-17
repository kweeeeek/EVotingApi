using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// The server-side ballot rules of a Representative election (Part 15 Representative tests 16-19, Position Rules test 6).
    /// The rules, the candidate rows and the per-position counts come from the database; nothing is taken from the client.
    /// </summary>
    public class RepresentativeBallotRulesTests
    {
        private const int EventId = 31;

        private static BallotRule Rule(string position, int min, int max, int seats = 1, int? maxCandidates = null) =>
            new(position, min, max, seats, maxCandidates);

        private static BallotCandidate Cand(int id, string position, int eventId = EventId, string status = "APPROVED", bool profileActive = true) =>
            new(id, eventId, position, status, profileActive);

        private static Dictionary<int, BallotCandidate> Candidates(params BallotCandidate[] list) => list.ToDictionary(c => c.Id);

        private static IReadOnlyDictionary<string, int> Approved(params (string Position, int Count)[] items)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (p, c) in items) d[p] = c;
            return d;
        }

        private static BallotCheck Check(int[]? selected, BallotRule[] rules, Dictionary<int, BallotCandidate> candidates, IReadOnlyDictionary<string, int>? approved = null) =>
            RepresentativeBallotRules.Check(EventId, selected, candidates, rules, approved ?? Approved());

        // ── what is accepted ──────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_ballot_inside_the_minimum_and_maximum_of_every_position_is_valid()
        {
            var rules = new[] { Rule("Delegate", 1, 3, 3), Rule("Alternate", 0, 2, 2) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Delegate"), Cand(3, "Delegate"), Cand(4, "Alternate"), Cand(5, "Alternate"));

            var check = Check(new[] { 1, 2, 4 }, rules, cands, Approved(("Delegate", 3), ("Alternate", 2)));

            Assert.True(check.Ok);
            Assert.Null(check.Code);
            Assert.Null(check.Message);
        }

        [Fact]
        public void Selecting_exactly_the_minimum_and_exactly_the_maximum_is_valid()
        {
            var rules = new[] { Rule("Delegate", 2, 3, 3) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Delegate"), Cand(3, "Delegate"), Cand(4, "Delegate"));
            var approved = Approved(("Delegate", 4));

            Assert.True(Check(new[] { 1, 2 }, rules, cands, approved).Ok);
            Assert.True(Check(new[] { 1, 2, 3 }, rules, cands, approved).Ok);
        }

        [Fact]
        public void A_position_with_a_minimum_of_zero_may_be_left_out_as_long_as_something_else_is_selected()
        {
            var rules = new[] { Rule("Delegate", 1, 1), Rule("Alternate", 0, 2, 2) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Alternate"));

            Assert.True(Check(new[] { 1 }, rules, cands, Approved(("Delegate", 1), ("Alternate", 1))).Ok);
        }

        [Fact]
        public void Position_names_are_compared_without_regard_to_case_or_surrounding_spaces()
        {
            var rules = new[] { Rule("Delegate", 1, 1) };
            var cands = Candidates(Cand(1, "  delegate "));

            Assert.True(Check(new[] { 1 }, rules, cands, Approved(("Delegate", 1))).Ok);
        }

        [Fact]
        public void The_largest_ballot_a_request_may_carry_is_500_selections()
        {
            var rules = new[] { Rule("Delegate", 0, 500, 500) };
            var list = Enumerable.Range(1, 500).Select(i => Cand(i, "Delegate")).ToArray();

            Assert.True(Check(Enumerable.Range(1, 500).ToArray(), rules, Candidates(list), Approved(("Delegate", 500))).Ok);
        }

        // ── what is refused, in the order the rules are checked ───────────────────────────────────────────

        [Fact]
        public void No_selection_at_all_is_refused()
        {
            var rules = new[] { Rule("Delegate", 0, 1) };
            var cands = Candidates(Cand(1, "Delegate"));

            Assert.Equal(RepCodes.NoSelection, Check(null, rules, cands).Code);
            Assert.Equal(RepCodes.NoSelection, Check(Array.Empty<int>(), rules, cands).Code);
        }

        [Fact]
        public void More_than_500_selections_are_refused_before_anything_is_looked_up()
        {
            var check = Check(Enumerable.Range(1, 501).ToArray(), new[] { Rule("Delegate", 0, 500) }, Candidates());
            Assert.False(check.Ok);
            Assert.Equal(RepCodes.TooManySelections, check.Code);
        }

        [Fact]
        public void The_same_candidate_twice_is_refused()
        {
            var rules = new[] { Rule("Delegate", 0, 3, 3) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Delegate"));

            var check = Check(new[] { 1, 2, 1 }, rules, cands, Approved(("Delegate", 2)));
            Assert.Equal(RepCodes.DuplicateSelection, check.Code);
            Assert.Equal("The same candidate was selected more than once.", check.Message);
        }

        [Fact]
        public void An_event_without_position_rules_accepts_no_ballot()
        {
            var check = Check(new[] { 1 }, Array.Empty<BallotRule>(), Candidates(Cand(1, "Delegate")));
            Assert.Equal(RepCodes.NoPositionRules, check.Code);
        }

        [Fact]
        public void A_position_defined_twice_disables_voting_until_it_is_corrected()
        {
            var rules = new[] { Rule("Delegate", 0, 1), Rule("DELEGATE ", 0, 2) };
            var check = Check(new[] { 1 }, rules, Candidates(Cand(1, "Delegate")), Approved(("Delegate", 1)));
            Assert.Equal(RepCodes.PositionNotConfigured, check.Code);
            Assert.Contains("more than once", check.Message);
        }

        [Fact]
        public void An_id_that_does_not_exist_is_refused()
        {
            var check = Check(new[] { 99 }, new[] { Rule("Delegate", 0, 1) }, Candidates(Cand(1, "Delegate")));
            Assert.Equal(RepCodes.UnknownCandidate, check.Code);
            Assert.Contains("Candidate 99 does not exist", check.Message);
        }

        [Fact]
        public void A_candidate_that_belongs_to_another_election_is_refused()
        {
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Delegate", eventId: EventId + 1));
            var check = Check(new[] { 1, 2 }, new[] { Rule("Delegate", 0, 2, 2) }, cands, Approved(("Delegate", 1)));
            Assert.Equal(RepCodes.CandidateWrongEvent, check.Code);
            Assert.Contains("Candidate 2 is not on the ballot of this election", check.Message);
        }

        [Fact]
        public void A_withdrawn_candidate_is_refused()
        {
            var cands = Candidates(Cand(1, "Delegate", status: "WITHDRAWN"));
            var check = Check(new[] { 1 }, new[] { Rule("Delegate", 0, 1) }, cands);
            Assert.Equal(RepCodes.CandidateNotEligible, check.Code);
        }

        [Fact]
        public void A_candidate_whose_profile_was_deactivated_is_refused()
        {
            var cands = Candidates(Cand(1, "Delegate", profileActive: false));
            var check = Check(new[] { 1 }, new[] { Rule("Delegate", 0, 1) }, cands);
            Assert.Equal(RepCodes.CandidateNotEligible, check.Code);
        }

        [Fact]
        public void A_candidate_status_is_compared_exactly_not_loosely()
        {
            var cands = Candidates(Cand(1, "Delegate", status: "approved"));
            Assert.Equal(RepCodes.CandidateNotEligible, Check(new[] { 1 }, new[] { Rule("Delegate", 0, 1) }, cands).Code);
        }

        [Fact]
        public void A_candidate_at_a_position_that_has_no_rule_is_refused()
        {
            var cands = Candidates(Cand(1, "Treasurer"));
            var check = Check(new[] { 1 }, new[] { Rule("Delegate", 0, 1) }, cands);
            Assert.Equal(RepCodes.PositionNotConfigured, check.Code);
            Assert.Contains("'Treasurer'", check.Message);
        }

        [Fact]
        public void The_position_of_a_selection_is_the_position_of_its_roster_entry_not_something_the_client_sends()
        {
            // The ballot carries only candidate ids. A candidate of "Alternate" counts towards "Alternate" whatever else the client claims.
            var rules = new[] { Rule("Delegate", 0, 1), Rule("Alternate", 0, 1) };
            var cands = Candidates(Cand(1, "Alternate"), Cand(2, "Alternate"));

            var check = Check(new[] { 1, 2 }, rules, cands, Approved(("Alternate", 2)));
            Assert.Equal(RepCodes.AboveMaximum, check.Code);
            Assert.Contains("'Alternate' allows at most 1", check.Message);
        }

        // ── minimum and maximum ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Fewer_selections_than_the_minimum_are_refused()
        {
            var rules = new[] { Rule("Delegate", 2, 3, 3) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Delegate"), Cand(3, "Delegate"));

            var check = Check(new[] { 1 }, rules, cands, Approved(("Delegate", 3)));
            Assert.Equal(RepCodes.BelowMinimum, check.Code);
            Assert.Equal("'Delegate' requires at least 2 selection(s); 1 were submitted.", check.Message);
        }

        [Fact]
        public void More_selections_than_the_maximum_are_refused()
        {
            var rules = new[] { Rule("Delegate", 0, 2, 2) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Delegate"), Cand(3, "Delegate"));

            var check = Check(new[] { 1, 2, 3 }, rules, cands, Approved(("Delegate", 3)));
            Assert.Equal(RepCodes.AboveMaximum, check.Code);
            Assert.Equal("'Delegate' allows at most 2 selection(s); 3 were submitted.", check.Message);
        }

        [Fact]
        public void Every_configured_position_is_checked_so_a_required_position_cannot_be_left_out()
        {
            var rules = new[] { Rule("Delegate", 1, 2, 2), Rule("Alternate", 1, 2, 2) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Alternate"));

            var check = Check(new[] { 1 }, rules, cands, Approved(("Delegate", 1), ("Alternate", 1)));
            Assert.Equal(RepCodes.BelowMinimum, check.Code);
            Assert.Contains("'Alternate'", check.Message);
        }

        [Fact]
        public void A_voter_is_never_asked_for_more_picks_than_the_position_has_candidates()
        {
            // minimum 2, but only one approved candidate stands: one pick satisfies the rule
            var rules = new[] { Rule("Delegate", 2, 3, 3) };
            var cands = Candidates(Cand(1, "Delegate"));

            Assert.True(Check(new[] { 1 }, rules, cands, Approved(("Delegate", 1))).Ok);
        }

        [Fact]
        public void The_minimum_is_lowered_only_as_far_as_the_candidates_that_really_exist()
        {
            // minimum 2, one candidate stands, and the voter picks nobody there: still short
            var rules = new[] { Rule("Delegate", 2, 3, 3), Rule("Alternate", 0, 1) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Alternate"));

            var check = Check(new[] { 2 }, rules, cands, Approved(("Delegate", 1), ("Alternate", 1)));
            Assert.Equal(RepCodes.BelowMinimum, check.Code);
            Assert.Contains("at least 1 selection(s); 0 were submitted", check.Message);
        }

        [Fact]
        public void A_position_nobody_stands_for_needs_no_selection()
        {
            var rules = new[] { Rule("Delegate", 1, 1), Rule("Alternate", 2, 2, 2) };
            var cands = Candidates(Cand(1, "Delegate"));

            // "Alternate" has no approved candidate: its minimum of 2 drops to 0
            Assert.True(Check(new[] { 1 }, rules, cands, Approved(("Delegate", 1))).Ok);
        }

        [Fact]
        public void The_maximum_is_not_lowered_by_the_number_of_candidates()
        {
            // a maximum above the number of candidates is just never reachable; it must not become a reason to refuse
            var rules = new[] { Rule("Delegate", 0, 5, 5) };
            var cands = Candidates(Cand(1, "Delegate"), Cand(2, "Delegate"));

            Assert.True(Check(new[] { 1, 2 }, rules, cands, Approved(("Delegate", 2))).Ok);
        }

        [Fact]
        public void The_checks_run_in_a_fixed_order_so_the_same_bad_ballot_always_gets_the_same_answer()
        {
            // duplicate + unknown id: the duplicate is reported first
            var check = Check(new[] { 99, 99 }, new[] { Rule("Delegate", 0, 1) }, Candidates());
            Assert.Equal(RepCodes.DuplicateSelection, check.Code);
        }
    }

    /// <summary>The HTTP status each stable code is answered with, and which codes a kiosk must not retry.</summary>
    public class RepresentativeHttpTests
    {
        [Theory]
        [InlineData("EVENT_NOT_FOUND", 404)]
        [InlineData("MEMBER_NOT_FOUND", 404)]
        [InlineData("TOKEN_MISMATCH", 403)]
        [InlineData("BRANCH_NOT_PARTICIPATING", 403)]
        [InlineData("WRONG_EVENT_TYPE", 409)]
        [InlineData("EVENT_NOT_OPEN", 409)]
        [InlineData("OUTSIDE_VOTING_WINDOW", 409)]
        [InlineData("ALREADY_VOTED", 409)]
        [InlineData("RECEIPT_IN_USE", 409)]
        [InlineData("INVALID_RECEIPT_CODE", 400)]
        [InlineData("INVALID_CAST_TIME", 400)]
        [InlineData("TEMPORARY_FAILURE", 503)]
        [InlineData("NOT_ELIGIBLE", 422)]
        [InlineData("NO_SELECTION", 422)]
        [InlineData("TOO_MANY_SELECTIONS", 422)]
        [InlineData("DUPLICATE_SELECTION", 422)]
        [InlineData("NO_POSITION_RULES", 422)]
        [InlineData("UNKNOWN_CANDIDATE", 422)]
        [InlineData("CANDIDATE_WRONG_EVENT", 422)]
        [InlineData("CANDIDATE_NOT_ELIGIBLE", 422)]
        [InlineData("POSITION_NOT_CONFIGURED", 422)]
        [InlineData("BELOW_MINIMUM", 422)]
        [InlineData("ABOVE_MAXIMUM", 422)]
        public void Each_stable_code_has_its_documented_status(string code, int status)
        {
            Assert.Equal(status, RepresentativeHttp.StatusFor(code));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("SOMETHING_NEW")]
        public void An_unknown_code_is_a_malformed_request(string? code)
        {
            Assert.Equal(400, RepresentativeHttp.StatusFor(code));
        }

        [Fact]
        public void A_temporary_failure_is_the_only_code_a_kiosk_may_retry()
        {
            Assert.False(RepresentativeHttp.IsPermanent(RepCodes.TemporaryFailure));
            Assert.False(RepresentativeHttp.IsPermanent(null));
            Assert.True(RepresentativeHttp.IsPermanent(RepCodes.AlreadyVoted));
            Assert.True(RepresentativeHttp.IsPermanent(RepCodes.BelowMinimum));
            Assert.True(RepresentativeHttp.IsPermanent(RepCodes.OutsideVotingWindow));
            Assert.True(RepresentativeHttp.IsPermanent("SOMETHING_NEW"));
        }

        [Fact]
        public void Every_ballot_rule_code_is_answered_with_422()
        {
            foreach (var code in new[] { RepCodes.NoSelection, RepCodes.TooManySelections, RepCodes.DuplicateSelection, RepCodes.NoPositionRules,
                                         RepCodes.UnknownCandidate, RepCodes.CandidateWrongEvent, RepCodes.CandidateNotEligible,
                                         RepCodes.PositionNotConfigured, RepCodes.BelowMinimum, RepCodes.AboveMaximum })
                Assert.Equal(422, RepresentativeHttp.StatusFor(code));
        }

        [Fact]
        public void The_stable_codes_are_distinct_upper_case_words()
        {
            var values = typeof(RepCodes).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!).ToList();

            Assert.True(values.Count > 40);
            Assert.Equal(values.Count, values.Distinct().Count());
            Assert.All(values, v => Assert.True(v.Length > 0 && v.All(c => (c >= 'A' && c <= 'Z') || c == '_'), "code '" + v + "' is not UPPER_SNAKE_CASE"));
        }
    }

    /// <summary>A General Assembly vote path asked to record a Representative ballot (or the reverse) is refused before anything is written.</summary>
    public class WrongEventTypeExceptionTests
    {
        [Fact]
        public void A_Representative_event_on_the_General_Assembly_path_names_the_right_endpoint()
        {
            var ex = new WrongEventTypeException(31, EventTypes.Representative);
            Assert.Equal(31, ex.EventId);
            Assert.Equal(EventTypes.Representative, ex.EventType);
            Assert.Contains("Event 31 is a Representative election", ex.Message);
            Assert.Contains("Representative voting endpoint", ex.Message);
        }

        [Fact]
        public void A_General_Assembly_event_on_the_Representative_path_names_the_right_endpoint()
        {
            var ex = new WrongEventTypeException(7, EventTypes.GeneralAssembly);
            Assert.Contains("Event 7 is a General Assembly election", ex.Message);
            Assert.Contains("General Assembly voting endpoint", ex.Message);
        }

        [Fact]
        public void It_is_an_InvalidOperationException_so_the_existing_offline_sync_treats_it_as_a_permanent_rejection()
        {
            Exception ex = new WrongEventTypeException(1, EventTypes.Representative);
            Assert.True(ex is InvalidOperationException);
        }
    }
}
