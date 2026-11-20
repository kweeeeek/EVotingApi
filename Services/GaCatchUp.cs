using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Services
{
    /// <summary>
    /// CATCH-UP GENERAL ASSEMBLY (2026-11-20). The General Assembly is held as regional events (e.g. North, South), each with its own
    /// branches. After them, one catch-up General Assembly event (Events.IsCatchUp = 1, normally with ALL branches) gives the members
    /// who have not voted yet a last chance. "Already voted" is normally checked per event only, so the catch-up adds one rule:
    ///
    ///   a member who has a voted VoterLogs row in an EARLIER General Assembly may not vote in the catch-up event.
    ///
    /// Earlier = an active GENERAL_ASSEMBLY event (any, catch-up or not) other than this one that STARTED before the catch-up starts and
    /// not more than <see cref="LookBackDays"/> days before it, so the General Assembly of a previous year never blocks anybody.
    /// Representative elections never count. Applied everywhere a General Assembly voter is accepted: voter verification (kiosk and
    /// mobile), Registration QR issuing, the vote itself (online, offline sync, USB import), and the kiosk offline data (member snapshot,
    /// USB package) so an offline kiosk refuses such a member too.
    /// </summary>
    public static class GaCatchUp
    {
        public const int LookBackDays = 180;
        public const string Code = "VOTED_IN_EARLIER_GA";

        /// <summary>Contains "already voted" on purpose: existing callers map that text to 409 / Conflict.</summary>
        public const string Message = "Member has already voted in an earlier General Assembly. This election is only for members who have not voted yet.";

        public static bool Applies(EventEntity ev) => ev.IsCatchUp && ev.EventType == EventTypes.GeneralAssembly;

        /// <summary>Ids of the earlier General Assembly events whose voters are excluded from <paramref name="ev"/> (empty when it is not a catch-up event).</summary>
        public static IQueryable<int> EarlierEventIds(ApiDbContext db, EventEntity ev)
        {
            if (!Applies(ev)) return db.Events.Where(e => false).Select(e => e.EventId);
            int id = ev.EventId;
            var start = ev.ScheduleFrom;
            var since = ev.ScheduleFrom.AddDays(-LookBackDays);
            return db.Events.AsNoTracking()
                .Where(e => e.IsActive && e.EventId != id && e.EventType == EventTypes.GeneralAssembly
                         && e.ScheduleFrom < start && e.ScheduleFrom >= since)
                .Select(e => e.EventId);
        }

        /// <summary>Members who voted in an earlier General Assembly (empty when <paramref name="ev"/> is not a catch-up event).</summary>
        public static IQueryable<int> EarlierVoterIds(ApiDbContext db, EventEntity ev)
        {
            var earlier = EarlierEventIds(db, ev);
            return db.VoterLogs.AsNoTracking().Where(v => v.HasVoted && earlier.Contains(v.EventId)).Select(v => v.MemberId);
        }

        public static async Task<bool> VotedEarlierAsync(ApiDbContext db, EventEntity ev, int memberId, CancellationToken ct)
        {
            if (!Applies(ev)) return false;
            var earlier = EarlierEventIds(db, ev);
            return await db.VoterLogs.AsNoTracking().AnyAsync(v => v.MemberId == memberId && v.HasVoted && earlier.Contains(v.EventId), ct);
        }

        /// <summary>The cast paths of VoteService: same exception type and wording family as "already voted in this event".</summary>
        public static async Task ThrowIfVotedEarlierAsync(ApiDbContext db, EventEntity ev, int memberId, CancellationToken ct)
        {
            if (await VotedEarlierAsync(db, ev, memberId, ct)) throw new InvalidOperationException(Message);
        }
    }
}
