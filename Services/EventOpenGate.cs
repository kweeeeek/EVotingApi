using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Services
{
    public sealed record OpenGateResult(IReadOnlyList<string> Issues, IReadOnlyList<string> Warnings)
    {
        public bool CanOpen => Issues.Count == 0;
        public static readonly OpenGateResult Ready = new(Array.Empty<string>(), Array.Empty<string>());
    }

    /// <summary>
    /// What must be true before an election may open, and what opening records. ONE implementation shared by the manual Open
    /// (<see cref="EventStateService"/>) and the scheduler's automatic open, so both enforce the same rules.
    ///
    /// General Assembly events keep their existing behaviour (no new blockers) except for two checks that only exist because of new,
    /// opt-in features: a position's MaxCandidates (only when an administrator configured one) and an unfinished eligibility recompute.
    /// </summary>
    public interface IEventOpenGate
    {
        /// <summary>Read-only: the problems that stop the election opening, and remarks that do not.</summary>
        Task<OpenGateResult> EvaluateAsync(EventEntity ev, CancellationToken ct);

        /// <summary>
        /// Called inside the transaction that sets the event to Opened: freezes a Representative roster (first opening only) and records the
        /// eligibility thresholds in force. Stages audit rows; the caller saves.
        /// </summary>
        Task StampOpenedAsync(EventEntity ev, DateTime now, CancellationToken ct);
    }

    public sealed class EventOpenGate : IEventOpenGate
    {
        private readonly ApiDbContext _db;
        private readonly IEligibilityService _eligibility;
        private readonly IAuditService _audit;

        public EventOpenGate(ApiDbContext db, IEligibilityService eligibility, IAuditService audit)
        {
            _db = db;
            _eligibility = eligibility;
            _audit = audit;
        }

        public async Task<OpenGateResult> EvaluateAsync(EventEntity ev, CancellationToken ct)
        {
            var issues = new List<string>();
            var warnings = new List<string>();

            if (!await _eligibility.FlagsInSyncAsync(ct))
                issues.Add("Member eligibility has not been fully recalculated for the current eligibility settings. " +
                           "Open Settings > Member Eligibility and run the recalculation first.");

            var rules = await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == ev.EventId).ToListAsync(ct);

            if (ev.EventType == EventTypes.Representative)
                await EvaluateRepresentativeAsync(ev, rules, issues, warnings, ct);
            else
                await EvaluateGeneralAssemblyAsync(ev, rules, issues, ct);

            if (DateTime.UtcNow >= ElectionClock.ToUtc(ev.ScheduleTo))
                warnings.Add("The scheduled voting window has already ended; the scheduler will close this election again within seconds. " +
                             "Edit the schedule first if voting should happen.");
            else if (ev.EventType == EventTypes.Representative && DateTime.UtcNow < ElectionClock.ToUtc(ev.ScheduleFrom))
                warnings.Add("The scheduled voting window has not started yet; Representative ballots are refused until it starts.");

            return new OpenGateResult(issues, warnings);
        }

        private async Task EvaluateRepresentativeAsync(
            EventEntity ev, List<EventPositionRuleEntity> rules, List<string> issues, List<string> warnings, CancellationToken ct)
        {
            if (!await _db.EventBranches.AsNoTracking().AnyAsync(b => b.EventId == ev.EventId, ct))
                issues.Add("No participating branch is selected.");

            if (rules.Count == 0)
            {
                issues.Add("No position rules are configured. Add at least one position with its minimum/maximum votes and seats.");
                return;
            }

            var approved = await _db.RepresentativeEventCandidates.AsNoTracking()
                .Where(c => c.EventId == ev.EventId && c.Status == RepresentativeCandidateStatuses.Approved && c.Profile!.IsActive)
                .GroupBy(c => c.Position)
                .Select(g => new { Position = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var countBy = approved.ToDictionary(a => a.Position.Trim(), a => a.Count, StringComparer.OrdinalIgnoreCase);

            if (approved.Sum(a => a.Count) == 0)
                issues.Add("The candidate roster is empty. Import or assign candidates first.");

            foreach (var rule in rules.OrderBy(r => r.Position, StringComparer.OrdinalIgnoreCase))
            {
                countBy.TryGetValue(rule.Position.Trim(), out var n);
                if (n < rule.MinVotes)
                    issues.Add($"'{rule.Position}' needs at least {rule.MinVotes} candidate(s) so a ballot can meet its minimum, but has {n}.");
                else if (n == 0)
                    warnings.Add($"'{rule.Position}' has no candidates.");
                if (rule.MaxCandidates.HasValue && n > rule.MaxCandidates.Value)
                    issues.Add($"'{rule.Position}' has {n} candidates, more than its limit of {rule.MaxCandidates.Value}.");
                if (n > 0 && n < rule.TotalSeats)
                    warnings.Add($"'{rule.Position}' has {n} candidate(s) for {rule.TotalSeats} seat(s); some seats cannot be filled.");
            }

            var ruleNames = new HashSet<string>(rules.Select(r => r.Position.Trim()), StringComparer.OrdinalIgnoreCase);
            foreach (var orphan in countBy.Where(kv => !ruleNames.Contains(kv.Key)))
                issues.Add($"{orphan.Value} candidate(s) are assigned to '{orphan.Key}', which has no position rule. Add the rule or remove the candidates.");
        }

        private async Task EvaluateGeneralAssemblyAsync(EventEntity ev, List<EventPositionRuleEntity> rules, List<string> issues, CancellationToken ct)
        {
            var limited = rules.Where(r => r.MaxCandidates.HasValue).ToList();
            if (limited.Count == 0) return;                      // legacy rules: nothing new is enforced

            var since = ev.ScheduleFrom.AddDays(-180);
            var counts = await _db.Candidacies.AsNoTracking()
                .Where(c => c.Status == "Approved" && c.IsActive &&
                            (c.DateApproved != null ? c.DateApproved >= since : c.DateFiled >= since))
                .GroupBy(c => c.Position)
                .Select(g => new { Position = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var countBy = counts.ToDictionary(c => (c.Position ?? "").Trim(), c => c.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var rule in limited)
            {
                countBy.TryGetValue(rule.Position.Trim(), out var n);
                if (n > rule.MaxCandidates!.Value)
                    issues.Add($"'{rule.Position}' has {n} approved candidates, more than its limit of {rule.MaxCandidates.Value}. " +
                               "Reject or withdraw candidacies, or raise the limit in the event's position rules.");
            }
        }

        public async Task StampOpenedAsync(EventEntity ev, DateTime now, CancellationToken ct)
        {
            var t = await _eligibility.GetThresholdsAsync(ct);
            ev.EligibilityMinShareCapital = t.MinShareCapital;
            ev.EligibilityMinSavingsDeposit = t.MinSavingsDeposit;

            if (ev.EventType == EventTypes.Representative && ev.RosterLockedAt == null)
            {
                ev.RosterLockedAt = now;
                _audit.Stage(AuditActions.RosterLocked, AuditEntities.Event, ev.EventId,
                    $"Candidate roster and position rules of '{ev.Title}' locked because voting opened.");
            }
        }
    }
}
