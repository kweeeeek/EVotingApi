using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// One election per branch per kind per year (EventBranchExclusivity). The database query (same kind, same year, active, not catch-up,
    /// not the event itself) is covered by the integration run; these tests pin the pure parts of the rule.
    /// </summary>
    public class EventBranchExclusivityTests
    {
        [Fact]
        public void Only_the_catch_up_General_Assembly_is_exempt()
        {
            Assert.True(EventBranchExclusivity.IsExempt(EventTypes.GeneralAssembly, isCatchUp: true));
            Assert.False(EventBranchExclusivity.IsExempt(EventTypes.GeneralAssembly, isCatchUp: false));
            Assert.False(EventBranchExclusivity.IsExempt(EventTypes.Representative, isCatchUp: true));
            Assert.False(EventBranchExclusivity.IsExempt(EventTypes.Representative, isCatchUp: false));
        }

        [Fact]
        public void A_new_event_checks_every_branch_once()
        {
            var check = EventBranchExclusivity.BranchesToCheck(new[] { 1, 2, 2, 3 }, existing: null, new List<int>(), 2026);
            Assert.Equal(new[] { 1, 2, 3 }, check.OrderBy(x => x));
        }

        [Fact]
        public void An_edit_checks_only_the_branches_it_adds()
        {
            var check = EventBranchExclusivity.BranchesToCheck(new[] { 1, 2, 5 }, (2026, false), new List<int> { 1, 2, 3 }, 2026);
            Assert.Equal(new[] { 5 }, check);
        }

        [Fact]
        public void An_edit_that_keeps_its_branches_checks_nothing()
        {
            var check = EventBranchExclusivity.BranchesToCheck(new[] { 1, 2 }, (2026, false), new List<int> { 1, 2, 3 }, 2026);
            Assert.Empty(check);
        }

        [Fact]
        public void Moving_the_event_to_another_year_checks_all_its_branches()
        {
            var check = EventBranchExclusivity.BranchesToCheck(new[] { 1, 2 }, (2026, false), new List<int> { 1, 2 }, 2027);
            Assert.Equal(new[] { 1, 2 }, check.OrderBy(x => x));
        }

        [Fact]
        public void Turning_the_catch_up_flag_off_checks_all_its_branches()
        {
            var check = EventBranchExclusivity.BranchesToCheck(new[] { 1, 2 }, (2026, true), new List<int> { 1, 2 }, 2026);
            Assert.Equal(new[] { 1, 2 }, check.OrderBy(x => x));
        }

        [Fact]
        public void The_message_names_at_most_five_branches()
        {
            var rows = Enumerable.Range(1, 7)
                .Select(i => new BranchUsageRow { BranchId = i, BranchName = $"B{i}", EventId = 10, EventTitle = "GA 1" }).ToList();
            var text = EventBranchExclusivity.Describe(rows);
            Assert.StartsWith("B1 (in \"GA 1\"), B2", text);
            Assert.DoesNotContain("B6", text);
            Assert.EndsWith("and 2 more", text);
        }
    }
}
