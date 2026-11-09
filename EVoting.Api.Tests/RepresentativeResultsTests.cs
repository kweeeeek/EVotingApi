using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// Ranking and winners of one position (Part 15 Representative tests 22-23, Position Rules test 7).
    /// Winners follow the General Assembly rule: when the count cannot separate the last seat from the first candidate outside the seats,
    /// nobody in that group is declared elected.
    /// </summary>
    public class RepresentativeRankingTests
    {
        private static RankInput C(int id, string name, int votes, string? code = null) => new(id, name, code ?? ("C" + id), votes);

        private static string Order(RankedPositionResult r) => string.Join(",", r.Ranked.Select(x => x.Id));
        private static string Ranks(RankedPositionResult r) => string.Join(",", r.Ranked.Select(x => x.Rank));
        private static string Winners(RankedPositionResult r) => string.Join(",", r.Ranked.Where(x => x.IsWinner).Select(x => x.Id));
        private static string Ties(RankedPositionResult r) => string.Join(",", r.Ranked.Where(x => x.IsTieForLastSeat).Select(x => x.Id));

        [Fact]
        public void Candidates_are_listed_by_votes_high_to_low()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 3), C(2, "Ben", 9), C(3, "Cai", 5) }, totalSeats: 1);
            Assert.Equal("2,3,1", Order(r));
            Assert.Equal("9,5,3", string.Join(",", r.Ranked.Select(x => x.Votes)));
        }

        [Fact]
        public void The_top_candidates_up_to_the_number_of_seats_are_elected()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 10), C(2, "Ben", 8), C(3, "Cai", 5), C(4, "Dee", 1) }, totalSeats: 2);
            Assert.Equal("1,2", Winners(r));
            Assert.Equal(2, r.SeatsFilled);
            Assert.False(r.HasUnresolvedTie);
            Assert.Equal("", Ties(r));
        }

        [Fact]
        public void Equal_votes_share_a_competition_rank_and_the_next_rank_skips()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 10), C(2, "Ben", 8), C(3, "Cai", 8), C(4, "Dee", 5) }, totalSeats: 4);
            Assert.Equal("1,2,2,4", Ranks(r));
        }

        [Fact]
        public void Three_candidates_on_the_same_votes_all_have_rank_1()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 7), C(2, "Ben", 7), C(3, "Cai", 7) }, totalSeats: 3);
            Assert.Equal("1,1,1", Ranks(r));
        }

        [Fact]
        public void A_tie_that_straddles_the_last_seat_elects_nobody_in_the_tied_group()
        {
            // 10, 8, 8, 5 for two seats: the first 8 would take the last seat, the second 8 is just outside it - the count cannot decide
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 10), C(2, "Ben", 8), C(3, "Cai", 8), C(4, "Dee", 5) }, totalSeats: 2);
            Assert.Equal("1", Winners(r));
            Assert.Equal("2,3", Ties(r));
            Assert.Equal(1, r.SeatsFilled);
            Assert.True(r.HasUnresolvedTie);
        }

        [Fact]
        public void A_tie_that_fits_inside_the_seats_elects_everybody_in_it()
        {
            // 10, 8, 8, 5 for three seats: both 8s fit
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 10), C(2, "Ben", 8), C(3, "Cai", 8), C(4, "Dee", 5) }, totalSeats: 3);
            Assert.Equal("1,2,3", Winners(r));
            Assert.Equal(3, r.SeatsFilled);
            Assert.False(r.HasUnresolvedTie);
        }

        [Fact]
        public void When_there_are_at_least_as_many_seats_as_candidates_everybody_is_elected_even_with_no_votes()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 4), C(2, "Ben", 0) }, totalSeats: 2);
            Assert.Equal("1,2", Winners(r));
            Assert.Equal(2, r.SeatsFilled);

            var more = RepresentativeRanking.Rank(new[] { C(1, "Ana", 0) }, totalSeats: 5);
            Assert.Equal("1", Winners(more));
        }

        [Fact]
        public void Candidates_with_zero_votes_are_part_of_the_ranking()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 5), C(2, "Ben", 0), C(3, "Cai", 3), C(4, "Dee", 0) }, totalSeats: 1);
            Assert.Equal(4, r.Ranked.Count);
            Assert.Equal("1,3,2,4", Order(r));
            Assert.Equal("1,2,3,3", Ranks(r));
            Assert.Equal("1", Winners(r));
        }

        [Fact]
        public void Two_candidates_with_no_votes_tie_for_the_last_seat_when_only_one_seat_is_left()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 5), C(2, "Ben", 0), C(3, "Cai", 0) }, totalSeats: 2);
            Assert.Equal("1", Winners(r));
            Assert.Equal("2,3", Ties(r));
            Assert.True(r.HasUnresolvedTie);
        }

        [Fact]
        public void An_election_nobody_voted_in_elects_nobody_when_there_are_more_candidates_than_seats()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 0), C(2, "Ben", 0), C(3, "Cai", 0) }, totalSeats: 1);
            Assert.Equal("", Winners(r));
            Assert.Equal("1,2,3", Ties(r));
            Assert.Equal(0, r.SeatsFilled);
            Assert.True(r.HasUnresolvedTie);
        }

        [Fact]
        public void The_number_of_seats_is_preserved_as_given_even_when_no_candidate_can_fill_them()
        {
            // 5 seats, 2 candidates: both elected, and the seat count itself is not capped or changed by the ranking
            var r = RepresentativeRanking.Rank(new[] { C(1, "Ana", 3), C(2, "Ben", 1) }, totalSeats: 5);
            Assert.Equal(2, r.SeatsFilled);
        }

        [Fact]
        public void Zero_seats_elect_nobody_and_an_empty_list_is_an_empty_result()
        {
            var none = RepresentativeRanking.Rank(new[] { C(1, "Ana", 3) }, totalSeats: 0);
            Assert.Equal("", Winners(none));
            Assert.False(none.HasUnresolvedTie);

            var empty = RepresentativeRanking.Rank(Array.Empty<RankInput>(), totalSeats: 3);
            Assert.Empty(empty.Ranked);
            Assert.Equal(0, empty.SeatsFilled);
            Assert.False(empty.HasUnresolvedTie);
        }

        [Fact]
        public void Equal_votes_are_listed_by_name_then_code_then_id_so_two_runs_print_the_same_list()
        {
            var input = new[]
            {
                C(1, "bravo", 5, "B"),
                C(2, "Alpha", 5, "Z"),
                C(3, "Alpha", 5, "A"),
                C(4, "Alpha", 5, "A"),
            };
            var forward = RepresentativeRanking.Rank(input, 4);
            var backward = RepresentativeRanking.Rank(input.AsEnumerable().Reverse(), 4);

            Assert.Equal("3,4,2,1", Order(forward));          // Alpha/A (id 3), Alpha/A (id 4), Alpha/Z, bravo - names compared without regard to case
            Assert.Equal(Order(forward), Order(backward));
        }

        [Fact]
        public void The_input_order_never_changes_who_is_elected()
        {
            var input = new[] { C(1, "Ana", 4), C(2, "Ben", 9), C(3, "Cai", 9), C(4, "Dee", 2), C(5, "Eli", 7) };
            var a = RepresentativeRanking.Rank(input, 2);
            var b = RepresentativeRanking.Rank(input.AsEnumerable().Reverse().ToArray(), 2);
            Assert.Equal(Winners(a), Winners(b));
            Assert.Equal(Order(a), Order(b));
            Assert.Equal("2,3", Winners(a));
        }

        [Fact]
        public void The_rank_never_depends_on_the_name_only_on_the_votes()
        {
            var r = RepresentativeRanking.Rank(new[] { C(1, "Zed", 6), C(2, "Abe", 6) }, 2);
            Assert.Equal("1,1", Ranks(r));
        }

        // ── percentages ───────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(1, 3, "33.33")]
        [InlineData(2, 3, "66.67")]
        [InlineData(3, 3, "100.00")]
        [InlineData(0, 10, "0.00")]
        [InlineData(1, 8, "12.50")]
        [InlineData(1, 800, "0.13")]        // 0.125 rounds away from zero, not to the even neighbour
        public void A_share_is_a_percentage_of_the_ballots_rounded_to_two_places(int votes, int ballots, string expected)
        {
            Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), RepresentativeRanking.PercentOfBallots(votes, ballots));
        }

        [Theory]
        [InlineData(5, 0)]
        [InlineData(0, 0)]
        [InlineData(5, -1)]
        public void A_share_of_no_ballots_is_zero_not_an_error(int votes, int ballots)
        {
            Assert.Equal(0m, RepresentativeRanking.PercentOfBallots(votes, ballots));
        }

        [Theory]
        [InlineData(10, 10, "100.00")]
        [InlineData(1, 3, "33.33")]
        [InlineData(1, 800, "0.13")]
        [InlineData(0, 500, "0.00")]
        public void Turnout_is_ballots_cast_over_eligible_voters(int ballots, int eligible, string expected)
        {
            Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), RepresentativeRanking.Turnout(ballots, eligible));
        }

        [Theory]
        [InlineData(5, 0)]
        [InlineData(0, 0)]
        [InlineData(5, -3)]
        public void Turnout_with_no_eligible_voters_is_zero(int ballots, int eligible)
        {
            Assert.Equal(0m, RepresentativeRanking.Turnout(ballots, eligible));
        }
    }

    /// <summary>
    /// The fingerprint of a roster (rules + candidates) a kiosk compares to know whether its cached ballot is current.
    /// </summary>
    public class RosterVersionTests
    {
        private static List<RosterRuleFact> Rules() => new()
        {
            new("Delegate", 1, 3, 3, 10),
            new("Alternate", 0, 2, 2, null),
        };

        private static List<RosterCandidateFact> Candidates() => new()
        {
            new(1, 11, "C-001", "Ana Reyes", "Delegate", 1, "APPROVED", 100),
            new(2, 12, "C-002", "Ben Cruz", "Delegate", 2, "APPROVED", 0),
            new(3, 13, "C-003", "Cai Lim", "Alternate", null, "APPROVED", 55),
        };

        private static string Version(List<RosterRuleFact>? rules = null, List<RosterCandidateFact>? cands = null, int eventId = 31, string type = "REPRESENTATIVE") =>
            RosterVersion.Compute(eventId, type, rules ?? Rules(), cands ?? Candidates());

        [Fact]
        public void The_version_is_a_64_character_lower_case_hexadecimal_string_and_repeatable()
        {
            var v = Version();
            Assert.Equal(64, v.Length);
            Assert.True(v.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
            Assert.Equal(v, Version());
        }

        [Fact]
        public void The_order_in_which_rules_and_candidates_are_listed_does_not_matter()
        {
            var shuffledRules = Rules(); shuffledRules.Reverse();
            var shuffledCands = Candidates(); shuffledCands.Reverse();
            Assert.Equal(Version(), Version(shuffledRules, shuffledCands));
        }

        [Fact]
        public void Position_spelling_in_case_or_with_spaces_does_not_change_the_version()
        {
            var rules = new List<RosterRuleFact> { new(" delegate ", 1, 3, 3, 10), new("ALTERNATE", 0, 2, 2, null) };
            var cands = new List<RosterCandidateFact>
            {
                new(1, 11, "C-001", "Ana Reyes", "DELEGATE", 1, "APPROVED", 100),
                new(2, 12, "C-002", "Ben Cruz", " delegate", 2, "APPROVED", 0),
                new(3, 13, "C-003", "Cai Lim", "alternate ", null, "APPROVED", 55),
            };
            Assert.Equal(Version(), Version(rules, cands));
        }

        [Fact]
        public void Anything_a_voter_sees_on_the_ballot_changes_the_version()
        {
            var baseline = Version();
            var changed = new Dictionary<string, string>();

            string With(Func<List<RosterCandidateFact>, List<RosterCandidateFact>> edit) => Version(cands: edit(Candidates()));
            string WithRule(Func<List<RosterRuleFact>, List<RosterRuleFact>> edit) => Version(rules: edit(Rules()));
            List<RosterCandidateFact> Replace(List<RosterCandidateFact> l, int index, RosterCandidateFact f) { l[index] = f; return l; }
            List<RosterRuleFact> ReplaceRule(List<RosterRuleFact> l, int index, RosterRuleFact f) { l[index] = f; return l; }

            changed["code"] = With(l => Replace(l, 0, l[0] with { Code = "C-009" }));
            changed["name"] = With(l => Replace(l, 0, l[0] with { Name = "Ana Reyes Jr" }));
            changed["position"] = With(l => Replace(l, 0, l[0] with { Position = "Alternate" }));
            changed["ballot number"] = With(l => Replace(l, 0, l[0] with { BallotNumber = 9 }));
            changed["ballot number removed"] = With(l => Replace(l, 0, l[0] with { BallotNumber = null }));
            changed["status"] = With(l => Replace(l, 0, l[0] with { Status = "WITHDRAWN" }));
            changed["photo stamp"] = With(l => Replace(l, 0, l[0] with { PhotoVersion = 101 }));
            changed["profile id"] = With(l => Replace(l, 0, l[0] with { ProfileId = 99 }));
            changed["candidate removed"] = With(l => { l.RemoveAt(2); return l; });
            changed["candidate added"] = With(l => { l.Add(new RosterCandidateFact(4, 14, "C-004", "Dee Uy", "Delegate", 3, "APPROVED", 0)); return l; });
            changed["rule min"] = WithRule(l => ReplaceRule(l, 0, l[0] with { MinVotes = 2 }));
            changed["rule max"] = WithRule(l => ReplaceRule(l, 0, l[0] with { MaxVotes = 4 }));
            changed["rule seats"] = WithRule(l => ReplaceRule(l, 0, l[0] with { TotalSeats = 4 }));
            changed["rule max candidates"] = WithRule(l => ReplaceRule(l, 0, l[0] with { MaxCandidates = 11 }));
            changed["rule max candidates removed"] = WithRule(l => ReplaceRule(l, 0, l[0] with { MaxCandidates = null }));
            changed["rule removed"] = WithRule(l => { l.RemoveAt(1); return l; });
            changed["event id"] = Version(eventId: 32);
            changed["event type"] = Version(type: "GENERAL_ASSEMBLY");

            foreach (var (what, version) in changed)
                Assert.NotEqual(baseline, version);

            // and no two different changes produce the same fingerprint
            Assert.Equal(changed.Count, changed.Values.Distinct().Count());
        }

        [Fact]
        public void No_ballot_number_and_ballot_number_zero_are_different_things()
        {
            var none = Version(cands: new List<RosterCandidateFact> { new(1, 11, "C", "N", "Delegate", null, "APPROVED", 0) });
            var zero = Version(cands: new List<RosterCandidateFact> { new(1, 11, "C", "N", "Delegate", 0, "APPROVED", 0) });
            Assert.NotEqual(none, zero);
        }

        [Fact]
        public void An_empty_roster_still_has_a_version_and_it_differs_from_a_filled_one()
        {
            var empty = RosterVersion.Compute(31, "REPRESENTATIVE", new List<RosterRuleFact>(), new List<RosterCandidateFact>());
            Assert.Equal(64, empty.Length);
            Assert.NotEqual(empty, Version());
        }

        [Fact]
        public void A_separator_character_inside_a_name_cannot_make_two_different_rosters_look_the_same()
        {
            // Candidate A has the name "X|Y" at position "P"; candidate B has the name "X" at position "Y|P". If the fingerprint simply joined the
            // fields with '|' both would read "...|X|Y|P|..." and a kiosk would keep a stale ballot.
            var rules = new List<RosterRuleFact> { new("P", 0, 1, 1, null), new("Y|P", 0, 1, 1, null) };
            var a = RosterVersion.Compute(1, "REPRESENTATIVE", rules, new List<RosterCandidateFact> { new(1, 1, "C", "X|Y", "P", 1, "APPROVED", 0) });
            var b = RosterVersion.Compute(1, "REPRESENTATIVE", rules, new List<RosterCandidateFact> { new(1, 1, "C", "X", "Y|P", 1, "APPROVED", 0) });
            Assert.NotEqual(a, b);
        }
    }
}
