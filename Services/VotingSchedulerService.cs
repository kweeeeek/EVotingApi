using EVoting.Api.Hubs;
using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace EVoting.Api.Services
{
    public class VotingSchedulerService : BackgroundService
    {
        private readonly IServiceProvider               _services;
        private readonly IHubContext<ElectionHub>       _hub;
        private readonly ILogger<VotingSchedulerService> _logger;

        // 10 s — events close within 10 seconds of ScheduleTo
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);

        public VotingSchedulerService(
            IServiceProvider services,
            IHubContext<ElectionHub> hub,
            ILogger<VotingSchedulerService> logger)
        {
            _services = services;
            _hub      = hub;
            _logger   = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "[Scheduler] Started. Checking every {Interval}s using LOCAL time.",
                CheckInterval.TotalSeconds);

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try   { await CheckAndUpdateSchedulesAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "[Scheduler] Error during schedule check."); }

                await Task.Delay(CheckInterval, stoppingToken);
            }

            _logger.LogInformation("[Scheduler] Stopped.");
        }

        private async Task CheckAndUpdateSchedulesAsync(CancellationToken ct)
        {
            // Serialise with manual open/close so a scheduler tick can never interleave with an admin action.
            await EventStateService.Gate.WaitAsync(ct);
            try { await CheckAndUpdateSchedulesCoreAsync(ct); }
            finally { EventStateService.Gate.Release(); }
        }

        // Events whose "manually closed — not re-opening" decision has already been logged (avoid log spam every 10 s).
        private readonly HashSet<int> _suppressedLogged = new();
        // Same for "another election is still running inside its own window — not pre-empting it".
        private readonly HashSet<int> _overlapLogged = new();

        /// <summary>
        /// FIX: the scheduler used to re-open ANY Closed event whose window was active, so an administrator who closed an
        /// election early saw it re-opened within 10 seconds. The most recent OPEN_EVENT/CLOSE_EVENT audit row decides:
        /// a manual (non-SYSTEM) close at/after the window start suppresses auto-open. If the audit lookup itself fails
        /// the legacy behaviour (auto-open) is kept, so an audit problem can never stop a scheduled election.
        /// </summary>
        private async Task<bool> IsAutoOpenSuppressedAsync(ApiDbContext db, EventEntity ev, CancellationToken ct)
        {
            try
            {
                var last = await db.AuditLogs.AsNoTracking()
                    .Where(a => a.EntityType == AuditEntities.Event && a.EntityId == ev.EventId &&
                                (a.Action == AuditActions.OpenEvent || a.Action == AuditActions.CloseEvent))
                    .OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.AuditId)
                    .Select(a => new { a.Action, a.Username, a.OccurredAtUtc })
                    .FirstOrDefaultAsync(ct);

                bool suppressed = last != null &&
                    AutoOpenGuard.IsSuppressed(last.Action, last.Username, last.OccurredAtUtc, ev.ScheduleFrom);

                if (suppressed && _suppressedLogged.Add(ev.EventId))
                    _logger.LogInformation(
                        "[Scheduler] EventId={EventId} '{Title}' was closed manually inside its window — NOT auto-opening.",
                        ev.EventId, ev.Title);
                return suppressed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Scheduler] Manual-close lookup failed for EventId={EventId}; using schedule only.", ev.EventId);
                return false;
            }
        }

        private async Task CheckAndUpdateSchedulesCoreAsync(CancellationToken ct)
        {
            await using var scope = _services.CreateAsyncScope();
            var db    = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
            var cache = scope.ServiceProvider.GetRequiredService<IDistributedCache>();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();   // no HttpContext here ⇒ actor = SYSTEM

            // ── TIMEZONE: Admin saves schedules as DateTime.Now (local Philippines time).
            // API server is on the same LAN machine → also local time.
            // NEVER use DateTime.UtcNow here — 8-hour gap would break all schedule checks.
            var now = DateTime.Now;

            var events = await db.Events
                .Where(e => e.IsActive)
                .ToListAsync(ct);

            bool anyChanged = false;

            // FIX (Bug 2): Track changed events in an EXPLICIT list.
            // Old code used:  events.Where(e => e.UpdatedAt == now)
            // That DateTime equality fails at millisecond precision after SaveChangesAsync()
            // returns — the comparison matched ZERO events → SignalR never fired.
            var changedEvents = new List<EventEntity>();

            foreach (var ev in events)
            {
                // ── Auto-OPEN ────────────────────────────────────────────────
                if (ev.Status == "Closed" &&
                    now >= ev.ScheduleFrom &&
                    now <  ev.ScheduleTo)
                {
                    if (await IsAutoOpenSuppressedAsync(db, ev, ct))
                        continue;

                    // FIX: legacy data can contain two events whose windows overlap (the Admin API now refuses to create
                    // them). The scheduler then flipped between the two on EVERY tick — each one closed the other — and a
                    // later window also pre-empted an election that was still running inside its own window.
                    // An election that is open AND still inside its own window is never pre-empted: it finishes first.
                    var runningInWindow = events.FirstOrDefault(o => o.EventId != ev.EventId && o.Status == "Opened" &&
                                                                     now >= o.ScheduleFrom && now < o.ScheduleTo);
                    if (runningInWindow != null)
                    {
                        if (_overlapLogged.Add(ev.EventId))
                            _logger.LogWarning(
                                "[Scheduler] EventId={EventId} '{Title}' is due to open, but EventId={OtherId} '{OtherTitle}' is open and still inside its own window (overlapping schedules) — NOT pre-empting it.",
                                ev.EventId, ev.Title, runningInWindow.EventId, runningInWindow.Title);
                        continue;
                    }

                    // Close any other open events first (only one open at a time)
                    foreach (var other in events.Where(e => e.EventId != ev.EventId && e.Status == "Opened"))
                    {
                        other.Status    = "Closed";
                        other.UpdatedAt = now;
                        changedEvents.Add(other);
                        audit.Stage(AuditActions.CloseEvent, AuditEntities.Event, other.EventId,
                            $"Event '{other.Title}' closed automatically because the scheduled window of '{ev.Title}' started.");
                        _logger.LogInformation(
                            "[Scheduler] Auto-closed EventId={EventId} '{Title}' (superseded).",
                            other.EventId, other.Title);
                    }

                    ev.Status    = "Opened";
                    ev.UpdatedAt = now;
                    anyChanged   = true;
                    changedEvents.Add(ev);
                    audit.Stage(AuditActions.OpenEvent, AuditEntities.Event, ev.EventId,
                        $"Event '{ev.Title}' opened automatically (scheduled start {ev.ScheduleFrom:yyyy-MM-dd HH:mm}).");

                    _logger.LogInformation(
                        "[Scheduler] Auto-OPENED EventId={EventId} '{Title}' (ScheduleFrom={From:g}).",
                        ev.EventId, ev.Title, ev.ScheduleFrom);
                }
                // ── Auto-CLOSE ───────────────────────────────────────────────
                else if (ev.Status == "Opened" && now >= ev.ScheduleTo)
                {
                    ev.Status    = "Closed";
                    ev.UpdatedAt = now;
                    anyChanged   = true;
                    changedEvents.Add(ev);
                    audit.Stage(AuditActions.CloseEvent, AuditEntities.Event, ev.EventId,
                        $"Event '{ev.Title}' closed automatically (scheduled end {ev.ScheduleTo:yyyy-MM-dd HH:mm} passed).");

                    _logger.LogInformation(
                        "[Scheduler] Auto-CLOSED EventId={EventId} '{Title}' (ScheduleTo={To:g} passed).",
                        ev.EventId, ev.Title, ev.ScheduleTo);
                }
            }

            if (!anyChanged) return;

            await db.SaveChangesAsync(ct);

            // Evict cache so kiosk poll returns fresh data immediately
            try   { await cache.RemoveAsync("current_open_event", ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Scheduler] Cache evict failed (non-fatal)."); }

            // ── SignalR push — uses changedEvents (FIX Bug 2) ──────────────────
            foreach (var ev in changedEvents)
            {
                var payload = new
                {
                    eventId       = ev.EventId,
                    title         = ev.Title,
                    status        = ev.Status,
                    changedAt     = now,
                    autoScheduled = true
                };

                // The change is already committed and the cache evicted. A SignalR outage must not skip the notifications
                // of the remaining events nor surface as a failed tick — kiosks fall back to polling GET /events/current.
                try
                {
                    await Task.WhenAll(
                        _hub.Clients.Group(ElectionHub.KioskGroup)
                            .SendAsync("EventStatusChanged", payload, ct),
                        _hub.Clients.Group(ElectionHub.AdminGroup)
                            .SendAsync("EventStatusChanged", payload, ct)
                    );

                    _logger.LogInformation(
                        "[Scheduler] SignalR pushed EventStatusChanged: EventId={EventId} Status={Status}",
                        ev.EventId, ev.Status);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "[Scheduler] SignalR push for EventId={EventId} failed; kiosks will pick the change up on their next refresh.",
                        ev.EventId);
                }
            }
        }
    }
}
