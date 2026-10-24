using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Election event management. Replaces the legacy direct-DB EventRepository / EventEditor / Eventmanagement logic.
    /// Schedules are LOCAL wall-clock times (the scheduler compares them with DateTime.Now) — they are never converted.
    /// </summary>
    [Route("api/v1/admin/events")]
    public class EventsAdminController : AdminControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly IEventStateService _state;

        public EventsAdminController(ApiDbContext db, IAuditService audit, IEventStateService state)
        {
            _db = db;
            _audit = audit;
            _state = state;
        }

        // ── Reads ────────────────────────────────────────────────────────────
        private IQueryable<EventEntity> BuildQuery(string? search, DateTime? from, DateTime? to)
        {
            var q = _db.Events.AsNoTracking().Where(e => e.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(e => e.Title.Contains(s) || e.Venue.Contains(s) || e.Status.Contains(s));
            }

            // Filter on ScheduleFrom (legacy behaviour). The legacy UI defaulted "to" = today, which HID every
            // future-scheduled event; the "to" bound is now optional and unset by default.
            var (start, end) = TimeConv.LocalRange(from, to);
            if (start.HasValue) q = q.Where(e => e.ScheduleFrom >= start.Value);
            if (end.HasValue) q = q.Where(e => e.ScheduleFrom <= end.Value);
            return q;
        }

        private async Task<Dictionary<int, List<(int BranchId, string Name)>>> BranchesOfAsync(
            List<int> eventIds, CancellationToken ct)
        {
            var rows = await _db.EventBranches.AsNoTracking()
                .Where(eb => eventIds.Contains(eb.EventId))
                .Select(eb => new { eb.EventId, eb.BranchId, Name = eb.Branch != null ? eb.Branch.BranchName : "" })
                .ToListAsync(ct);
            return rows.GroupBy(r => r.EventId)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Name).Select(x => (x.BranchId, x.Name)).ToList());
        }

        private static EventRow ToRow(EventEntity e, List<(int BranchId, string Name)>? branches) => new()
        {
            EventId = e.EventId,
            Title = e.Title,
            Venue = e.Venue,
            ScheduleFrom = e.ScheduleFrom,
            ScheduleTo = e.ScheduleTo,
            Status = e.Status,
            BranchNames = branches == null ? "" : string.Join(", ", branches.Select(b => b.Name)),
            BranchCount = branches?.Count ?? 0,
            CreatedAt = e.CreatedAt,
            UpdatedAt = e.UpdatedAt
        };

        [HttpGet]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> List(
            [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        {
            (page, pageSize) = ClampPaging(page, pageSize);
            var q = BuildQuery(search, from, to);
            var total = await q.CountAsync(ct);
            var events = await q.OrderByDescending(e => e.ScheduleFrom).ThenByDescending(e => e.EventId)
                .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var branches = await BranchesOfAsync(events.Select(e => e.EventId).ToList(), ct);
            var rows = events.Select(e => ToRow(e, branches.GetValueOrDefault(e.EventId))).ToList();
            return Ok(Paged(rows, total, page, pageSize));
        }

        [HttpGet("{id:int}")]
        [RequirePermission(PermissionCodes.EventView, PermissionCodes.EventEdit)]
        public async Task<IActionResult> Get(int id, CancellationToken ct)
        {
            var e = await _db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.EventId == id && x.IsActive, ct);
            if (e == null) return NotFoundError("Event");
            var detail = await LoadDetailAsync(id, ct);
            detail.VoterLogCount = await _db.VoterLogs.AsNoTracking().CountAsync(v => v.EventId == id, ct);
            detail.VoteCount = await _db.Votes.AsNoTracking().CountAsync(v => v.EventId == id, ct);
            return Ok(detail);
        }

        [HttpGet("check-title")]
        [RequirePermission(PermissionCodes.EventView, PermissionCodes.EventAdd, PermissionCodes.EventEdit)]
        public async Task<IActionResult> CheckTitle([FromQuery] string title, [FromQuery] int? exceptId, CancellationToken ct)
        {
            var t = (title ?? "").Trim();
            var taken = await _db.Events.AsNoTracking()
                .AnyAsync(e => e.IsActive && e.Title == t && (exceptId == null || e.EventId != exceptId), ct);
            return Ok(new AvailabilityResponse { Taken = taken });
        }

        // ── Create / Update ──────────────────────────────────────────────────
        [HttpPost]
        [RequirePermission(PermissionCodes.EventAdd)]
        public async Task<IActionResult> Create([FromBody] EventUpsertRequest req, CancellationToken ct)
        {
            var fail = await ValidateAsync(req, existing: null, scheduleChanged: true, ct);
            if (fail != null) return fail;

            var now = DateTime.Now;
            var ev = new EventEntity
            {
                Title = req.Title.Trim(),
                Venue = req.Venue.Trim(),
                ScheduleFrom = req.ScheduleFrom,
                ScheduleTo = req.ScheduleTo,
                Status = "Closed",              // the scheduler (or an admin) opens it later
                CreatedAt = now,
                UpdatedAt = now,
                IsActive = true
            };

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Events.Add(ev);
            await _db.SaveChangesAsync(ct);

            _db.EventBranches.AddRange(req.BranchIds.Distinct().Select(b => new EventBranchEntity { EventId = ev.EventId, BranchId = b }));
            _db.EventPositionRules.AddRange(DefaultPositionRules.All.Select(r => new EventPositionRuleEntity
            {
                EventId = ev.EventId, Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes, TotalSeats = r.TotalSeats
            }));
            _audit.Stage(AuditActions.Create, AuditEntities.Event, ev.EventId,
                $"Title='{ev.Title}', Venue='{ev.Venue}', {ev.ScheduleFrom:yyyy-MM-dd HH:mm} → {ev.ScheduleTo:yyyy-MM-dd HH:mm}, Branches=[{string.Join(",", req.BranchIds.Distinct())}].");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return StatusCode(StatusCodes.Status201Created, await LoadDetailAsync(ev.EventId, ct));
        }

        [HttpPut("{id:int}")]
        [RequirePermission(PermissionCodes.EventEdit)]
        public async Task<IActionResult> Update(int id, [FromBody] EventUpsertRequest req, CancellationToken ct)
        {
            var ev = await _db.Events.Include(e => e.EventBranches).FirstOrDefaultAsync(e => e.EventId == id && e.IsActive, ct);
            if (ev == null) return NotFoundError("Event");
            if (IsStale(req.ExpectedUpdatedAt, ev.UpdatedAt)) return ConcurrencyError("event");

            bool scheduleChanged = ev.ScheduleFrom != req.ScheduleFrom || ev.ScheduleTo != req.ScheduleTo;
            var fail = await ValidateAsync(req, ev, scheduleChanged, ct);
            if (fail != null) return fail;

            var wanted = req.BranchIds.Distinct().ToHashSet();
            var current = ev.EventBranches.Select(eb => eb.BranchId).ToHashSet();
            bool branchesChanged = !wanted.SetEquals(current);

            // The branch list decides WHO may vote. Changing it while ballots are being cast would silently
            // enfranchise or disenfranchise voters mid-election.
            if (branchesChanged && ev.Status == "Opened")
                return Fail(StatusCodes.Status409Conflict,
                    "The participating branches cannot be changed while the election is open.",
                    "EVENT_OPEN_LOCK", "Close the election first, or leave the branch list unchanged.");

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            ev.Title = req.Title.Trim();
            ev.Venue = req.Venue.Trim();
            ev.ScheduleFrom = req.ScheduleFrom;
            ev.ScheduleTo = req.ScheduleTo;
            ev.UpdatedAt = DateTime.Now;

            if (branchesChanged)
            {
                _db.EventBranches.RemoveRange(ev.EventBranches.Where(eb => !wanted.Contains(eb.BranchId)));
                _db.EventBranches.AddRange(wanted.Where(b => !current.Contains(b))
                    .Select(b => new EventBranchEntity { EventId = id, BranchId = b }));
            }

            // Position rules: keep whatever exists. (The legacy update DELETED all rules and re-seeded defaults,
            // silently discarding any customised ones.) Seed defaults only for events that have none.
            if (!await _db.EventPositionRules.AnyAsync(r => r.EventId == id, ct))
                _db.EventPositionRules.AddRange(DefaultPositionRules.All.Select(r => new EventPositionRuleEntity
                {
                    EventId = id, Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes, TotalSeats = r.TotalSeats
                }));

            _audit.Stage(AuditActions.Update, AuditEntities.Event, id,
                $"Title='{ev.Title}', Venue='{ev.Venue}', {ev.ScheduleFrom:yyyy-MM-dd HH:mm} → {ev.ScheduleTo:yyyy-MM-dd HH:mm}" +
                (branchesChanged ? $", Branches=[{string.Join(",", wanted)}]." : "."));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Ok(await LoadDetailAsync(id, ct));
        }

        // ── Open / Close ─────────────────────────────────────────────────────
        [HttpPost("{id:int}/open")]
        [RequirePermission(PermissionCodes.EventEdit)]
        public async Task<IActionResult> Open(int id, [FromBody] OpenEventRequest? req, CancellationToken ct)
        {
            var r = await _state.OpenAsync(id, req?.CloseOthers ?? false, ct);
            return StateResponse(r, "opened");
        }

        [HttpPost("{id:int}/close")]
        [RequirePermission(PermissionCodes.EventEdit)]
        public async Task<IActionResult> Close(int id, CancellationToken ct)
        {
            var r = await _state.CloseAsync(id, ct);
            return StateResponse(r, "closed");
        }

        private IActionResult StateResponse(EventStateResult r, string verb)
        {
            if (!r.Ok) return Fail(r.StatusCode, r.Error ?? "The request failed.", r.Code, r.Detail);
            return Ok(new EventStateResponse
            {
                EventId = r.EventId,
                Status = r.Status,
                ClosedEventIds = r.ClosedEventIds,
                Warning = r.Warning,
                Message = r.Changed ? $"Event {verb}." : $"Event was already {r.Status.ToLowerInvariant()}."
            });
        }

        // ── Delete ───────────────────────────────────────────────────────────
        [HttpDelete("{id:int}")]
        [RequirePermission(PermissionCodes.EventDelete)]
        public async Task<IActionResult> Delete(int id, [FromQuery] bool purgeVotes = false, CancellationToken ct = default)
        {
            await EventStateService.Gate.WaitAsync(ct);
            try
            {
                var ev = await _db.Events.FirstOrDefaultAsync(e => e.EventId == id && e.IsActive, ct);
                if (ev == null) return NotFoundError("Event");

                int voteCount = await _db.Votes.CountAsync(v => v.EventId == id, ct);
                int logCount = await _db.VoterLogs.CountAsync(v => v.EventId == id, ct);
                if ((voteCount > 0 || logCount > 0) && !purgeVotes)
                    return Fail(StatusCodes.Status409Conflict,
                        $"This event has {voteCount} recorded vote(s) and {logCount} voter-log entr{(logCount == 1 ? "y" : "ies")}. " +
                        "Deleting it permanently destroys them — the election result cannot be recovered.",
                        "HAS_VOTING_RECORDS", "Confirm to delete the event together with its votes and voter logs.");

                bool wasOpen = ev.Status == "Opened";
                await using var tx = await _db.Database.BeginTransactionAsync(ct);

                int votesDeleted = 0, logsDeleted = 0;
                if (purgeVotes)
                {
                    votesDeleted = await _db.Votes.Where(v => v.EventId == id).ExecuteDeleteAsync(ct);
                    logsDeleted = await _db.VoterLogs.Where(v => v.EventId == id).ExecuteDeleteAsync(ct);
                }

                // Force Closed so a deleted-but-still-"Opened" event can never keep the kiosks in voting mode.
                ev.IsActive = false;
                ev.Status = "Closed";
                ev.UpdatedAt = DateTime.Now;
                _audit.Stage(AuditActions.SoftDelete, AuditEntities.Event, id,
                    $"Title='{ev.Title}'. Purged {votesDeleted} vote(s) and {logsDeleted} voter-log entr{(logsDeleted == 1 ? "y" : "ies")}." +
                    (wasOpen ? " Event was open and has been closed." : ""));
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                string? warning = wasOpen ? await _state.PublishAsync(new[] { ev }, ct) : null;
                return Ok(new DeleteEventResponse
                {
                    Message = $"Event '{ev.Title}' deleted." + (warning != null ? " " + warning : ""),
                    VotesDeleted = votesDeleted,
                    VoterLogsDeleted = logsDeleted
                });
            }
            finally
            {
                EventStateService.Gate.Release();
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private async Task<EventDetail> LoadDetailAsync(int id, CancellationToken ct)
        {
            var e = await _db.Events.AsNoTracking().FirstAsync(x => x.EventId == id, ct);
            var branches = (await BranchesOfAsync(new List<int> { id }, ct)).GetValueOrDefault(id) ?? new();
            var row = ToRow(e, branches);
            return new EventDetail
            {
                EventId = row.EventId, Title = row.Title, Venue = row.Venue,
                ScheduleFrom = row.ScheduleFrom, ScheduleTo = row.ScheduleTo, Status = row.Status,
                BranchNames = row.BranchNames, BranchCount = row.BranchCount,
                CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt,
                BranchIds = branches.Select(b => b.BranchId).ToList()
            };
        }

        private async Task<IActionResult?> ValidateAsync(EventUpsertRequest r, EventEntity? existing, bool scheduleChanged, CancellationToken ct)
        {
            var errors = new Dictionary<string, string>();
            var title = (r.Title ?? "").Trim();
            if (title.Length == 0) errors["title"] = "Event title is required.";
            if (string.IsNullOrWhiteSpace(r.Venue)) errors["venue"] = "Venue is required.";
            if (r.ScheduleTo <= r.ScheduleFrom) errors["scheduleTo"] = "The end of the schedule must be after its start.";
            if (r.BranchIds.Count == 0) errors["branchIds"] = "Select at least one branch.";
            if (errors.Count > 0) return Invalid(errors);

            var ids = r.BranchIds.Distinct().ToList();
            var activeBranches = await _db.Branches.AsNoTracking().CountAsync(b => ids.Contains(b.BranchId) && b.IsActive, ct);
            if (activeBranches != ids.Count)
                return Invalid("branchIds", "One or more selected branches do not exist or were deleted.");

            // Title uniqueness on create AND update (the legacy editor only checked on create).
            int? exceptId = existing?.EventId;
            if (await _db.Events.AsNoTracking().AnyAsync(e => e.IsActive && e.Title == title && (exceptId == null || e.EventId != exceptId), ct))
                return Duplicate("An event with this title already exists.", "title");

            // Only ONE election can be open at a time, so two active events must never share a time window
            // (the scheduler would otherwise open both).
            if (scheduleChanged)
            {
                var clash = await _db.Events.AsNoTracking()
                    .Where(e => e.IsActive && (exceptId == null || e.EventId != exceptId)
                             && e.ScheduleFrom < r.ScheduleTo && e.ScheduleTo > r.ScheduleFrom)
                    .Select(e => e.Title).Take(3).ToListAsync(ct);
                if (clash.Count > 0)
                    return Fail(StatusCodes.Status409Conflict,
                        $"This schedule overlaps with: {string.Join(", ", clash.Select(t => $"\"{t}\""))}.",
                        "SCHEDULE_CONFLICT", "Only one election can run at a time — choose a window that does not overlap.",
                        new() { ["scheduleFrom"] = "Overlaps an existing event." });
            }
            return null;
        }
    }
}
