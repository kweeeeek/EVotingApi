using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Services
{
    /// <summary>
    /// ONE EVENT PER BRANCH PER KIND PER YEAR (2026-11-21). A branch may take part in only one election of the same kind
    /// (General Assembly or Representative) whose schedule starts in the same calendar year:
    /// <list type="bullet">
    /// <item>General Assembly 1 and General Assembly 2 of 2026 cannot share a branch; neither can Representative 1 and Representative 2;</item>
    /// <item>a Representative election and a General Assembly never block each other (every member votes in both);</item>
    /// <item>the CATCH-UP General Assembly (see <see cref="GaCatchUp"/>) is exempt both ways: it may take every branch, and its branches
    /// do not block any other General Assembly;</item>
    /// <item>deleted (inactive) events do not count; an event of an earlier year does not count.</item>
    /// </list>
    /// An existing event that already shares a branch (created before this rule) keeps it: only the branches a change ADDS are checked,
    /// unless the change moves the event to another year or turns its catch-up flag off - then all its branches are checked.
    /// </summary>
    public static class EventBranchExclusivity
    {
        public const string Code = "BRANCH_IN_OTHER_EVENT";

        /// <summary>True for the catch-up General Assembly: it neither is blocked nor blocks.</summary>
        public static bool IsExempt(string? eventType, bool isCatchUp) => isCatchUp && eventType == EventTypes.GeneralAssembly;

        /// <summary>
        /// The branches of a request that must not be used by another event of the same kind and year.
        /// </summary>
        /// <param name="requested">The branches the event will have after the change.</param>
        /// <param name="existing">The event as it is now, or null when it is being created.</param>
        /// <param name="currentBranches">The branches the existing event has now (ignored when creating).</param>
        /// <param name="newYear">The year of the new ScheduleFrom.</param>
        public static IReadOnlyCollection<int> BranchesToCheck(
            IEnumerable<int> requested, (int Year, bool Exempt)? existing, IReadOnlyCollection<int> currentBranches, int newYear)
        {
            var wanted = requested.Distinct().ToList();
            if (existing == null || existing.Value.Year != newYear || existing.Value.Exempt) return wanted;
            return wanted.Where(b => !currentBranches.Contains(b)).ToList();
        }

        /// <summary>
        /// Which branches the OTHER events of this kind and year already use (catch-up and deleted events excluded), one row per branch and event.
        /// </summary>
        public static Task<List<BranchUsageRow>> UsageAsync(ApiDbContext db, string eventType, int year, int? exceptEventId, CancellationToken ct)
        {
            var start = new DateTime(year, 1, 1);
            var end = start.AddYears(1);
            return db.EventBranches.AsNoTracking()
                .Where(eb => eb.Event != null && eb.Event.IsActive && eb.Event.EventType == eventType
                          && !(eb.Event.IsCatchUp && eb.Event.EventType == EventTypes.GeneralAssembly)
                          && eb.Event.ScheduleFrom >= start && eb.Event.ScheduleFrom < end
                          && (exceptEventId == null || eb.EventId != exceptEventId))
                .OrderBy(eb => eb.BranchId).ThenBy(eb => eb.EventId)
                .Select(eb => new BranchUsageRow
                {
                    BranchId = eb.BranchId,
                    BranchName = eb.Branch != null ? eb.Branch.BranchName : "",
                    EventId = eb.EventId,
                    EventTitle = eb.Event!.Title
                })
                .ToListAsync(ct);
        }

        /// <summary>"Alpha Branch (in \"General Assembly 1\"), Beta Branch (in ...)" - at most <paramref name="max"/> named, then "and N more".</summary>
        public static string Describe(IReadOnlyList<BranchUsageRow> clashes, int max = 5)
        {
            var named = clashes.Take(max).Select(c => $"{(c.BranchName.Length > 0 ? c.BranchName : "Branch " + c.BranchId)} (in \"{c.EventTitle}\")");
            var text = string.Join(", ", named);
            return clashes.Count > max ? $"{text} and {clashes.Count - max} more" : text;
        }
    }
}
