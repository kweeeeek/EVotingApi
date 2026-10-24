using EVoting.Api.Hubs;
using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace EVoting.Api.Services
{
    public sealed class EventStateResult
    {
        public bool Ok { get; init; }
        public int StatusCode { get; init; } = StatusCodes.Status200OK;
        public string? Error { get; init; }
        public string? Detail { get; init; }
        public string? Code { get; init; }
        public int EventId { get; init; }
        public string Status { get; init; } = "";
        public List<int> ClosedEventIds { get; init; } = new();
        public string? Warning { get; init; }
        public bool Changed { get; init; }
    }

    /// <summary>
    /// The ONE place that opens/closes elections. Used by the Admin endpoints, the legacy PATCH /events/{id}/status
    /// and (for its lock) the scheduler. Guarantees: single-open invariant, atomic transition, audit trail,
    /// cache eviction and live push to kiosks + admins.
    /// </summary>
    public interface IEventStateService
    {
        Task<EventStateResult> OpenAsync(int eventId, bool closeOthers, CancellationToken ct);
        Task<EventStateResult> CloseAsync(int eventId, CancellationToken ct);

        /// <summary>Evict the cached "current event" and push EventStatusChanged to kiosks and admins.</summary>
        Task<string?> PublishAsync(IEnumerable<EventEntity> changed, CancellationToken ct);
    }

    public sealed class EventStateService : IEventStateService
    {
        public const string CacheKeyCurrentEvent = "current_open_event";

        /// <summary>Serialises state transitions inside this process (manual open/close and the scheduler).</summary>
        public static readonly SemaphoreSlim Gate = new(1, 1);

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly IDistributedCache _cache;
        private readonly IHubContext<ElectionHub> _hub;
        private readonly ILogger<EventStateService> _logger;

        public EventStateService(ApiDbContext db, IAuditService audit, IDistributedCache cache,
                                 IHubContext<ElectionHub> hub, ILogger<EventStateService> logger)
        {
            _db = db;
            _audit = audit;
            _cache = cache;
            _hub = hub;
            _logger = logger;
        }

        public async Task<EventStateResult> OpenAsync(int eventId, bool closeOthers, CancellationToken ct)
        {
            await Gate.WaitAsync(ct);
            try
            {
                var ev = await _db.Events.FirstOrDefaultAsync(e => e.EventId == eventId && e.IsActive, ct);
                if (ev == null)
                    return new EventStateResult { StatusCode = StatusCodes.Status404NotFound, Error = "Event not found.", Code = "NOT_FOUND" };

                if (ev.Status == "Opened")
                    return new EventStateResult { Ok = true, EventId = eventId, Status = "Opened", Changed = false };

                var others = await _db.Events
                    .Where(e => e.IsActive && e.Status == "Opened" && e.EventId != eventId)
                    .ToListAsync(ct);

                if (others.Count > 0 && !closeOthers)
                    return new EventStateResult
                    {
                        StatusCode = StatusCodes.Status409Conflict,
                        Error = "Another event is already open.",
                        Detail = "Close the current event before opening a new one.",
                        Code = "ANOTHER_EVENT_OPEN"
                    };

                var now = DateTime.Now;
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                foreach (var o in others)
                {
                    o.Status = "Closed";
                    o.UpdatedAt = now;
                    _audit.Stage(AuditActions.CloseEvent, AuditEntities.Event, o.EventId,
                        $"Event '{o.Title}' closed automatically because '{ev.Title}' was opened.");
                }

                ev.Status = "Opened";
                ev.UpdatedAt = now;
                _audit.Stage(AuditActions.OpenEvent, AuditEntities.Event, ev.EventId, $"Event '{ev.Title}' opened.");

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                var changed = others.Append(ev).ToList();
                var warning = await PublishAsync(changed, ct);
                return new EventStateResult
                {
                    Ok = true, EventId = eventId, Status = "Opened", Changed = true,
                    ClosedEventIds = others.Select(o => o.EventId).ToList(), Warning = warning
                };
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<EventStateResult> CloseAsync(int eventId, CancellationToken ct)
        {
            await Gate.WaitAsync(ct);
            try
            {
                var ev = await _db.Events.FirstOrDefaultAsync(e => e.EventId == eventId && e.IsActive, ct);
                if (ev == null)
                    return new EventStateResult { StatusCode = StatusCodes.Status404NotFound, Error = "Event not found.", Code = "NOT_FOUND" };

                if (ev.Status == "Closed")
                    return new EventStateResult { Ok = true, EventId = eventId, Status = "Closed", Changed = false };

                ev.Status = "Closed";
                ev.UpdatedAt = DateTime.Now;
                // Recorded as a MANUAL close (the acting admin's name is on the row). The scheduler reads this
                // to avoid re-opening an election an administrator deliberately ended inside its window.
                _audit.Stage(AuditActions.CloseEvent, AuditEntities.Event, ev.EventId, $"Event '{ev.Title}' closed.");
                await _db.SaveChangesAsync(ct);

                var warning = await PublishAsync(new[] { ev }, ct);
                return new EventStateResult { Ok = true, EventId = eventId, Status = "Closed", Changed = true, Warning = warning };
            }
            finally
            {
                Gate.Release();
            }
        }

        public async Task<string?> PublishAsync(IEnumerable<EventEntity> changed, CancellationToken ct)
        {
            string? warning = null;
            try
            {
                await _cache.RemoveAsync(CacheKeyCurrentEvent, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not evict {Key}; kiosks may see the previous state until the cache TTL elapses.", CacheKeyCurrentEvent);
                warning = "The state change was saved, but the server cache could not be cleared; kiosks may take a few seconds longer to notice.";
            }

            foreach (var ev in changed)
            {
                var payload = new
                {
                    eventId = ev.EventId,
                    title = ev.Title,
                    status = ev.Status,
                    changedAt = DateTime.Now,
                    autoScheduled = false
                };
                try
                {
                    await _hub.Clients.Group(ElectionHub.KioskGroup).SendAsync("EventStatusChanged", payload, ct);
                    await _hub.Clients.Group(ElectionHub.AdminGroup).SendAsync("EventStatusChanged", payload, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SignalR push for event {EventId} failed.", ev.EventId);
                    warning = "The state change was saved, but the live notification to kiosks failed; they will pick it up on their next refresh.";
                }
            }
            return warning;
        }
    }
}
