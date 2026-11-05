using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Services
{
    /// <summary>Small shared read helpers (one place for "how many candidates does a position have" and "is this event frozen").</summary>
    public static class RosterQueries
    {
        /// <summary>
        /// Candidates currently on the ballot per position (case-insensitive keys).
        /// Representative: approved roster entries. General Assembly: approved, active candidacies inside the event's 180-day window
        /// (exactly the set the existing ballot endpoint offers).
        /// </summary>
        public static async Task<Dictionary<string, int>> AssignedByPositionAsync(ApiDbContext db, EventEntity ev, CancellationToken ct)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (ev.EventType == EventTypes.Representative)
            {
                var rows = await db.RepresentativeEventCandidates.AsNoTracking()
                    .Where(c => c.EventId == ev.EventId && c.Status == RepresentativeCandidateStatuses.Approved)
                    .GroupBy(c => c.Position)
                    .Select(g => new { Position = g.Key, Count = g.Count() })
                    .ToListAsync(ct);
                foreach (var r in rows) result[r.Position.Trim()] = r.Count;
            }
            else
            {
                var since = ev.ScheduleFrom.AddDays(-180);
                var rows = await db.Candidacies.AsNoTracking()
                    .Where(c => c.Status == "Approved" && c.IsActive &&
                                (c.DateApproved != null ? c.DateApproved >= since : c.DateFiled >= since))
                    .GroupBy(c => c.Position)
                    .Select(g => new { Position = g.Key, Count = g.Count() })
                    .ToListAsync(ct);
                foreach (var r in rows) result[(r.Position ?? "").Trim()] = r.Count;
            }
            return result;
        }

        /// <summary>Ballots cast so far (voter-log rows that voted, shared by both election types) plus any stray vote rows.</summary>
        public static async Task<long> BallotCountAsync(ApiDbContext db, EventEntity ev, CancellationToken ct)
        {
            long logged = await db.VoterLogs.AsNoTracking().LongCountAsync(v => v.EventId == ev.EventId && v.HasVoted, ct);
            if (logged > 0) return logged;
            // A vote row without a voter log should not exist, but if it does the event is certainly not "empty".
            return ev.EventType == EventTypes.Representative
                ? (await db.RepresentativeVotes.AsNoTracking().AnyAsync(v => v.EventId == ev.EventId, ct) ? 1L : 0L)
                : (await db.Votes.AsNoTracking().AnyAsync(v => v.EventId == ev.EventId, ct) ? 1L : 0L);
        }

        public static async Task<EventLock> LockAsync(ApiDbContext db, EventEntity ev, CancellationToken ct) =>
            EventLockPolicy.ForRulesAndRoster(ev.Status, ev.EventType, ev.RosterLockedAt, await BallotCountAsync(db, ev, ct));

        /// <summary>
        /// Loads the event row WITH a row lock (SELECT … FOR UPDATE) inside the caller's transaction. Every roster/rules/import/open/close
        /// change takes this lock first, so they are serialised per event and cannot interleave (same lock order everywhere → no deadlock).
        /// </summary>
        public static async Task<EventEntity?> LockEventRowAsync(ApiDbContext db, int eventId, CancellationToken ct) =>
            (await db.Events.FromSqlRaw("SELECT * FROM Events WHERE EventId = {0} AND IsActive = 1 FOR UPDATE", eventId).ToListAsync(ct)).FirstOrDefault();

        /// <summary>Fingerprint of the ballot (rules + every roster entry incl. withdrawn ones). Cheap: two narrow projections, no images.</summary>
        public static async Task<string> VersionAsync(ApiDbContext db, EventEntity ev, CancellationToken ct)
        {
            var rules = await db.EventPositionRules.AsNoTracking().Where(r => r.EventId == ev.EventId)
                .Select(r => new RosterRuleFact(r.Position, r.MinVotes, r.MaxVotes, r.TotalSeats, r.MaxCandidates)).ToListAsync(ct);
            var cands = await db.RepresentativeEventCandidates.AsNoTracking().Where(c => c.EventId == ev.EventId)
                .Select(c => new
                {
                    c.RepresentativeEventCandidateId, c.RepresentativeCandidateProfileId, c.Position, c.BallotNumber, c.Status,
                    c.Profile!.CandidateCode, c.Profile.CandidateName, c.Profile.PhotoUpdatedAt, c.Profile.IsActive
                }).ToListAsync(ct);
            return RosterVersion.Compute(ev.EventId, ev.EventType, rules,
                cands.Select(c => new RosterCandidateFact(
                    c.RepresentativeEventCandidateId, c.RepresentativeCandidateProfileId, c.CandidateCode, c.CandidateName,
                    c.Position, c.BallotNumber, c.IsActive ? c.Status : c.Status + "!", c.PhotoUpdatedAt?.Ticks ?? 0)));
        }
    }
}
