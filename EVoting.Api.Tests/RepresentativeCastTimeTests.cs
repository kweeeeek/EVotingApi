using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// The election clock and the time rules of casting (Part 15 Representative tests 21 and 24: online ballot, offline synchronisation).
    /// Event schedules are stored as the election's local wall-clock time, which both election types read as Singapore / Philippine time (UTC+8).
    /// The tests build every instant explicitly in UTC, so they give the same answer on a server in any time zone.
    /// </summary>
    public class ElectionClockTests
    {
        private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0, int s = 0) => new(y, m, d, h, min, s, DateTimeKind.Utc);

        [Fact]
        public void Election_local_time_is_eight_hours_ahead_of_UTC()
        {
            var utc = ElectionClock.ToUtc(new DateTime(2026, 11, 4, 8, 0, 0));
            Assert.Equal(Utc(2026, 11, 4, 0, 0, 0), utc);
        }

        [Fact]
        public void The_clock_numbers_are_taken_as_wall_clock_whatever_kind_the_stored_value_claims()
        {
            // a value read from the database has no kind; one built elsewhere may say Utc or Local - the digits are what the Admin typed
            var unspecified = ElectionClock.ToUtc(new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Unspecified));
            var claimsUtc = ElectionClock.ToUtc(new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc));
            Assert.Equal(unspecified, claimsUtc);
        }

        [Fact]
        public void A_local_evening_time_falls_on_the_same_UTC_day_and_a_local_early_morning_on_the_day_before()
        {
            Assert.Equal(Utc(2026, 11, 4, 14, 0, 0), ElectionClock.ToUtc(new DateTime(2026, 11, 4, 22, 0, 0)));
            Assert.Equal(Utc(2026, 11, 3, 18, 30, 0), ElectionClock.ToUtc(new DateTime(2026, 11, 4, 2, 30, 0)));
        }

        [Fact]
        public void The_voting_window_includes_its_first_instant_and_excludes_its_last()
        {
            var from = new DateTime(2026, 11, 4, 8, 0, 0);       // 00:00 UTC
            var to = new DateTime(2026, 11, 4, 17, 0, 0);        // 09:00 UTC
            Assert.False(ElectionClock.IsWithinWindow(from, to, Utc(2026, 11, 3, 23, 59, 59)));
            Assert.True(ElectionClock.IsWithinWindow(from, to, Utc(2026, 11, 4, 0, 0, 0)));
            Assert.True(ElectionClock.IsWithinWindow(from, to, Utc(2026, 11, 4, 8, 59, 59)));
            Assert.False(ElectionClock.IsWithinWindow(from, to, Utc(2026, 11, 4, 9, 0, 0)));
        }

        [Fact]
        public void A_window_that_ends_before_or_when_it_starts_contains_no_instant()
        {
            var t = new DateTime(2026, 11, 4, 8, 0, 0);
            Assert.False(ElectionClock.IsWithinWindow(t, t, Utc(2026, 11, 4, 0, 0, 0)));
            Assert.False(ElectionClock.IsWithinWindow(t.AddHours(9), t, Utc(2026, 11, 4, 1, 0, 0)));
        }

        [Fact]
        public void AsUtc_keeps_a_UTC_value_labels_an_unlabelled_one_as_UTC_and_converts_a_local_one()
        {
            var utc = Utc(2026, 11, 4, 3, 0, 0);
            Assert.Equal(utc, ElectionClock.AsUtc(utc));
            Assert.Equal(DateTimeKind.Utc, ElectionClock.AsUtc(utc).Kind);

            var unlabelled = new DateTime(2026, 11, 4, 3, 0, 0, DateTimeKind.Unspecified);
            Assert.Equal(utc, ElectionClock.AsUtc(unlabelled));
            Assert.Equal(DateTimeKind.Utc, ElectionClock.AsUtc(unlabelled).Kind);

            // a Local value round-trips to the same instant on a machine in any time zone
            var local = utc.ToLocalTime();
            Assert.Equal(DateTimeKind.Local, local.Kind);
            Assert.Equal(utc, ElectionClock.AsUtc(local));
        }
    }

    public class RepresentativeCastTimeTests
    {
        // the voting window used throughout: 08:00 - 17:00 election-local = 00:00 - 09:00 UTC on 2026-11-04
        private static readonly DateTime From = new(2026, 11, 4, 8, 0, 0);
        private static readonly DateTime To = new(2026, 11, 4, 17, 0, 0);

        private static DateTime Utc(int d, int h, int min = 0, int s = 0) => new(2026, 11, d, h, min, s, DateTimeKind.Utc);

        // ── online ────────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void An_online_ballot_inside_the_window_of_an_open_election_is_accepted_and_stamped_with_the_server_time()
        {
            var now = Utc(4, 3, 15);
            var r = RepresentativeCastTime.CheckOnline("Opened", From, To, now);
            Assert.True(r.Ok);
            Assert.Null(r.Code);
            Assert.Equal(now, r.CastAtUtc);
        }

        [Theory]
        [InlineData("Closed")]
        [InlineData("Pending")]
        [InlineData("")]
        public void An_online_ballot_for_an_election_that_is_not_open_is_refused(string status)
        {
            var r = RepresentativeCastTime.CheckOnline(status, From, To, Utc(4, 3));
            Assert.False(r.Ok);
            Assert.Equal(RepCodes.EventNotOpen, r.Code);
        }

        [Fact]
        public void The_open_status_is_recognised_without_regard_to_case()
        {
            Assert.True(RepresentativeCastTime.CheckOnline("opened", From, To, Utc(4, 3)).Ok);
            Assert.True(RepresentativeCastTime.CheckOnline("OPENED", From, To, Utc(4, 3)).Ok);
        }

        [Fact]
        public void An_online_ballot_before_the_window_is_refused_even_if_the_status_says_open()
        {
            var r = RepresentativeCastTime.CheckOnline("Opened", From, To, Utc(3, 23, 59, 59));
            Assert.Equal(RepCodes.OutsideVotingWindow, r.Code);
            Assert.Contains("not started", r.Message);
        }

        [Fact]
        public void An_online_ballot_at_the_exact_start_is_accepted()
        {
            Assert.True(RepresentativeCastTime.CheckOnline("Opened", From, To, Utc(4, 0, 0, 0)).Ok);
        }

        [Fact]
        public void An_online_ballot_in_the_last_second_is_accepted_and_at_the_end_instant_it_is_refused()
        {
            Assert.True(RepresentativeCastTime.CheckOnline("Opened", From, To, Utc(4, 8, 59, 59)).Ok);

            var r = RepresentativeCastTime.CheckOnline("Opened", From, To, Utc(4, 9, 0, 0));
            Assert.Equal(RepCodes.OutsideVotingWindow, r.Code);
            Assert.Contains("already ended", r.Message);
        }

        [Fact]
        public void A_schedule_that_ends_before_it_starts_accepts_no_ballot()
        {
            var r = RepresentativeCastTime.CheckOnline("Opened", To, From, Utc(4, 3));
            Assert.Equal(RepCodes.OutsideVotingWindow, r.Code);
            Assert.Contains("invalid voting schedule", r.Message);

            Assert.False(RepresentativeCastTime.CheckOnline("Opened", From, From, Utc(4, 3)).Ok);
        }

        // ── offline ───────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void An_offline_ballot_cast_inside_the_window_is_accepted_with_its_own_cast_time_not_the_sync_time()
        {
            var cast = Utc(4, 5, 30);
            var syncedLater = Utc(4, 12, 0);               // the election has closed by now
            var r = RepresentativeCastTime.CheckOffline(true, From, To, cast, syncedLater);
            Assert.True(r.Ok);
            Assert.Equal(cast, r.CastAtUtc);
        }

        [Fact]
        public void An_offline_ballot_for_an_election_that_was_never_opened_is_refused()
        {
            var r = RepresentativeCastTime.CheckOffline(false, From, To, Utc(4, 5), Utc(4, 6));
            Assert.Equal(RepCodes.EventNotOpen, r.Code);
        }

        [Fact]
        public void An_offline_ballot_without_a_cast_time_is_refused()
        {
            var r = RepresentativeCastTime.CheckOffline(true, From, To, default, Utc(4, 6));
            Assert.Equal(RepCodes.InvalidCastTime, r.Code);
        }

        [Fact]
        public void An_offline_ballot_cast_before_the_window_is_refused()
        {
            var r = RepresentativeCastTime.CheckOffline(true, From, To, Utc(3, 23, 59, 59), Utc(4, 6));
            Assert.Equal(RepCodes.OutsideVotingWindow, r.Code);
            Assert.Contains("before voting opened", r.Message);
        }

        [Fact]
        public void An_offline_ballot_cast_at_the_end_instant_or_after_is_refused_and_one_second_before_is_accepted()
        {
            var sync = Utc(4, 12);
            Assert.True(RepresentativeCastTime.CheckOffline(true, From, To, Utc(4, 8, 59, 59), sync).Ok);

            var r = RepresentativeCastTime.CheckOffline(true, From, To, Utc(4, 9, 0, 0), sync);
            Assert.Equal(RepCodes.OutsideVotingWindow, r.Code);
            Assert.Contains("after voting ended", r.Message);
        }

        [Fact]
        public void An_offline_ballot_dated_in_the_future_is_refused_beyond_five_minutes_of_clock_skew()
        {
            var now = Utc(4, 5, 0, 0);
            Assert.Equal(TimeSpan.FromMinutes(5), RepresentativeCastTime.ClockSkewTolerance);

            Assert.True(RepresentativeCastTime.CheckOffline(true, From, To, now.AddMinutes(5), now).Ok);

            var r = RepresentativeCastTime.CheckOffline(true, From, To, now.AddMinutes(5).AddSeconds(1), now);
            Assert.Equal(RepCodes.InvalidCastTime, r.Code);
            Assert.Contains("future", r.Message);
        }

        [Fact]
        public void A_cast_time_without_a_kind_is_read_as_UTC_and_one_in_local_time_is_converted()
        {
            var cast = Utc(4, 5, 30);
            var unlabelled = DateTime.SpecifyKind(cast, DateTimeKind.Unspecified);
            Assert.Equal(cast, RepresentativeCastTime.CheckOffline(true, From, To, unlabelled, Utc(4, 12)).CastAtUtc);

            var local = cast.ToLocalTime();
            Assert.Equal(cast, RepresentativeCastTime.CheckOffline(true, From, To, local, Utc(4, 12)).CastAtUtc);
        }

        [Fact]
        public void An_offline_ballot_on_a_schedule_that_ends_before_it_starts_is_refused()
        {
            var r = RepresentativeCastTime.CheckOffline(true, To, From, Utc(4, 5), Utc(4, 6));
            Assert.Equal(RepCodes.OutsideVotingWindow, r.Code);
            Assert.Contains("invalid voting schedule", r.Message);
        }
    }
}
