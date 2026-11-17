using EVoting.Api.Hubs;
using EVoting.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace EVoting.Api.Services
{
    /// <summary>
    /// Real-time notices after a Representative ballot is recorded. Fire-and-forget: a failed push is logged and never affects the ballot,
    /// which is already committed.
    ///   MemberVoted                  → every kiosk (member id + event id only, never a selection) so an offline-capable kiosk can refuse a repeat voter at once
    ///   TurnoutUpdated               → the election's group (same payload as General Assembly)
    ///   RepresentativeResultsUpdated → administrators (a small notice; the Admin screen reloads the results when it arrives)
    /// Turnout/results notices are COALESCED per election: at most one runs at a time and at most one every two seconds, so a burst of ballots
    /// costs a bounded amount of database work (the General Assembly path learned this the hard way at ~10 ballots/second).
    /// </summary>
    public interface IRepresentativeLiveUpdates
    {
        void BallotCast(int eventId, int memberId);
        void BallotsCast(int eventId, IEnumerable<int> memberIds);
    }

    public sealed class RepresentativeLiveUpdates : IRepresentativeLiveUpdates
    {
        private sealed class State { public int Running; public int Dirty; }

        private static readonly ConcurrentDictionary<int, State> States = new();
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);

        private readonly IHubContext<ElectionHub> _hub;
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<RepresentativeLiveUpdates> _logger;

        public RepresentativeLiveUpdates(IHubContext<ElectionHub> hub, IServiceScopeFactory scopes, ILogger<RepresentativeLiveUpdates> logger)
        {
            _hub = hub;
            _scopes = scopes;
            _logger = logger;
        }

        public void BallotCast(int eventId, int memberId) => BallotsCast(eventId, new[] { memberId });

        public void BallotsCast(int eventId, IEnumerable<int> memberIds)
        {
            var members = memberIds.ToList();
            if (members.Count == 0) return;
            _ = SendMemberVotedAsync(eventId, members);
            _ = PushAsync(eventId);
        }

        private async Task SendMemberVotedAsync(int eventId, List<int> memberIds)
        {
            try
            {
                foreach (var memberId in memberIds)
                    await _hub.Clients.Group(ElectionHub.KioskGroup).SendAsync("MemberVoted", new { eventId, memberId }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MemberVoted push failed for EventId={EventId}; the ballots were still recorded.", eventId);
            }
        }

        private async Task PushAsync(int eventId)
        {
            var state = States.GetOrAdd(eventId, _ => new State());
            Volatile.Write(ref state.Dirty, 1);
            if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0) return;     // someone is pushing; they will see Dirty and push again

            try
            {
                while (Interlocked.Exchange(ref state.Dirty, 0) == 1)
                {
                    await PushCoreAsync(eventId);
                    await Task.Delay(MinInterval);
                }
            }
            finally
            {
                Volatile.Write(ref state.Running, 0);
            }
            if (Volatile.Read(ref state.Dirty) == 1) _ = PushAsync(eventId);       // a ballot landed between the last check and Running = 0
        }

        private async Task PushCoreAsync(int eventId)
        {
            // Own scope + no request token: this outlives the HTTP request that started it (see the General Assembly controller for why).
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            try
            {
                var branchIds = await db.EventBranches.AsNoTracking().Where(b => b.EventId == eventId).Select(b => b.BranchId).ToListAsync();
                var eligible = await db.Members.AsNoTracking().CountAsync(m => branchIds.Contains(m.BranchId) && m.IsEligibleToVote && m.IsActive);
                var voted = await db.VoterLogs.AsNoTracking().CountAsync(v => v.EventId == eventId && v.HasVoted);
                var now = DateTime.UtcNow;

                await _hub.Clients.Group(ElectionHub.EventGroup(eventId)).SendAsync("TurnoutUpdated", new
                {
                    eventId,
                    totalEligible = eligible,
                    totalVoted = voted,
                    turnoutPercent = eligible > 0 ? Math.Round((double)voted / eligible * 100, 2) : 0,
                    updatedAt = now
                });
                await _hub.Clients.Group(ElectionHub.AdminGroup).SendAsync("RepresentativeResultsUpdated", new { eventId, totalVoters = voted, updatedAt = now });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Representative live push failed for EventId={EventId}; ballots were still recorded.", eventId);
            }
        }
    }
}
