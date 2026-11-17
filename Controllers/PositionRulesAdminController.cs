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
    /// Event-specific position rules (Position / Min votes / Max votes / Max candidates / Total seats) for BOTH election types.
    /// The Admin editor reads and saves here; the ballot validation, the Representative roster limit and the results read the same rows.
    ///   MinVotes / MaxVotes  — selections a voter must / may make on one ballot
    ///   MaxCandidates        — candidates allowed to take part at the position in this event (NULL = no limit)
    ///   TotalSeats           — winning seats (meaning unchanged; the results use it)
    /// </summary>
    [Route("api/v1/admin/events/{id:int}/position-rules")]
    public class PositionRulesAdminController : AdminControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;

        public PositionRulesAdminController(ApiDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.EventView, PermissionCodes.EventEdit)]
        public async Task<IActionResult> Get(int id, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == id && e.IsActive, ct);
            if (ev == null) return NotFoundError("Event");
            return Ok(await BuildResponseAsync(ev, ct));
        }

        [HttpPut]
        [RequirePermission(PermissionCodes.EventEdit)]
        public async Task<IActionResult> Save(int id, [FromBody] PositionRulesSaveRequest req, CancellationToken ct)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Serialises with the roster editor, the import commit and Open/Close for this event.
            var locked = await _db.Events
                .FromSqlRaw("SELECT * FROM Events WHERE EventId = {0} AND IsActive = 1 FOR UPDATE", id)
                .ToListAsync(ct);
            var ev = locked.FirstOrDefault();
            if (ev == null) return NotFoundError("Event");
            if (IsStale(req.ExpectedUpdatedAt, ev.UpdatedAt)) return ConcurrencyError("event");

            var gate = await RosterQueries.LockAsync(_db, ev, ct);
            if (gate.Locked)
                return Fail(StatusCodes.Status409Conflict, gate.Reason!, gate.Code, "Position rules can only be changed while the election is being prepared.");

            var assigned = await RosterQueries.AssignedByPositionAsync(_db, ev, ct);
            var rules = req.Rules;
            var check = PositionRuleValidator.Validate(rules, ev.EventType, assigned);
            if (!check.Ok)
                return Invalid(check.Errors);

            var existing = await _db.EventPositionRules.Where(r => r.EventId == id).ToListAsync(ct);
            var byId = existing.ToDictionary(r => r.Id);

            // Rows that disappear or change their position name must not strand Representative candidates assigned to the old name.
            var errors = new Dictionary<string, string>();
            if (ev.EventType == EventTypes.Representative)
            {
                var keptIds = rules.Where(r => r.Id.HasValue && byId.ContainsKey(r.Id.Value)).Select(r => r.Id!.Value).ToHashSet();
                foreach (var old in existing)
                {
                    var incoming = rules.FirstOrDefault(r => r.Id == old.Id);
                    bool gone = !keptIds.Contains(old.Id);
                    bool renamed = incoming != null && !string.Equals(incoming.Position, old.Position, StringComparison.OrdinalIgnoreCase);
                    if (!gone && !renamed) continue;
                    if (assigned.TryGetValue(old.Position.Trim(), out var n) && n > 0)
                    {
                        int idx = incoming != null ? rules.IndexOf(incoming) : 0;
                        errors[incoming != null ? $"rules[{idx}].position" : "rules"] =
                            $"'{old.Position}' has {n} candidate(s) assigned, so it cannot be {(gone ? "removed" : "renamed")}. Remove those candidates first.";
                    }
                }
                if (errors.Count > 0) return Invalid(errors);
            }

            // ── Persist the difference ──────────────────────────────────────────────────────────────────
            var dupIds = rules.Where(r => r.Id.HasValue).GroupBy(r => r.Id!.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupIds.Count > 0)
                return Invalid("rules", "The same rule row was submitted more than once. Reload the rules and try again.");

            var before = existing.ToDictionary(r => r.Id, r => Describe(r.Position, r.MinVotes, r.MaxVotes, r.MaxCandidates, r.TotalSeats));
            var summary = new List<string>();
            var incomingIds = rules.Where(r => r.Id.HasValue).Select(r => r.Id!.Value).ToHashSet();

            var toDelete = existing.Where(r => !incomingIds.Contains(r.Id)).ToList();
            foreach (var d in toDelete) summary.Add($"-{before[d.Id]}");
            _db.EventPositionRules.RemoveRange(toDelete);

            // Phase 1: free the names. A rename or swap inside one save would trip UQ (EventId, Position) in mid-statement,
            // so a row that changes its name first moves to a temporary unique one (all inside this transaction).
            foreach (var r in rules.Where(r => r.Id.HasValue && byId.ContainsKey(r.Id.Value)))
            {
                var row = byId[r.Id!.Value];
                if (!string.Equals(row.Position, r.Position, StringComparison.Ordinal))
                    row.Position = $"~tmp{row.Id}";
            }
            if (_db.ChangeTracker.HasChanges())
                await _db.SaveChangesAsync(ct);

            // Phase 2: final values.
            foreach (var r in rules)
            {
                if (r.Id.HasValue && byId.TryGetValue(r.Id.Value, out var row))
                {
                    row.Position = r.Position;
                    row.MinVotes = r.MinVotes;
                    row.MaxVotes = r.MaxVotes;
                    row.MaxCandidates = r.MaxCandidates;
                    row.TotalSeats = r.TotalSeats;
                    var after = Describe(row.Position, row.MinVotes, row.MaxVotes, row.MaxCandidates, row.TotalSeats);
                    if (before[row.Id] != after) summary.Add($"~{before[row.Id]} → {after}");
                }
                else
                {
                    _db.EventPositionRules.Add(new EventPositionRuleEntity
                    {
                        EventId = id, Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes,
                        MaxCandidates = r.MaxCandidates, TotalSeats = r.TotalSeats
                    });
                    summary.Add($"+{Describe(r.Position, r.MinVotes, r.MaxVotes, r.MaxCandidates, r.TotalSeats)}");
                }
            }

            ev.UpdatedAt = DateTime.Now;
            _audit.Stage(AuditActions.UpdatePositionRules, AuditEntities.Event, id,
                $"Position rules of '{ev.Title}' ({(ev.EventType == EventTypes.Representative ? "Representative" : "General Assembly")}) saved: " +
                (summary.Count == 0 ? "no change." : string.Join("; ", summary) + ".") +
                " Format: position min/max/maxCandidates/seats.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var fresh = await _db.Events.AsNoTracking().FirstAsync(e => e.EventId == id, ct);
            return Ok(await BuildResponseAsync(fresh, ct));
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────
        private static string Describe(string position, int min, int max, int? maxCandidates, int seats) =>
            $"{position} {min}/{max}/{(maxCandidates.HasValue ? maxCandidates.Value.ToString() : "-")}/{seats}";

        private async Task<PositionRulesResponse> BuildResponseAsync(EventEntity ev, CancellationToken ct)
        {
            var rows = await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == ev.EventId).OrderBy(r => r.Id).ToListAsync(ct);
            var assigned = await RosterQueries.AssignedByPositionAsync(_db, ev, ct);
            var gate = await RosterQueries.LockAsync(_db, ev, ct);

            bool isDefault = rows.Count == 0 && ev.EventType == EventTypes.GeneralAssembly;
            var list = isDefault
                ? DefaultPositionRules.All.Select(d => new PositionRuleRow
                    { Id = null, Position = d.Position, MinVotes = d.MinVotes, MaxVotes = d.MaxVotes, TotalSeats = d.TotalSeats }).ToList()
                : rows.Select(r => new PositionRuleRow
                    { Id = r.Id, Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes, MaxCandidates = r.MaxCandidates, TotalSeats = r.TotalSeats }).ToList();

            foreach (var r in list)
                r.AssignedCandidates = assigned.TryGetValue(r.Position.Trim(), out var n) ? n : 0;

            var warnings = PositionRuleValidator.Validate(
                list.Select(r => new PositionRuleRow
                {
                    Id = r.Id, Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes, MaxCandidates = r.MaxCandidates, TotalSeats = r.TotalSeats
                }).ToList(), ev.EventType, assigned).Warnings;

            var suggestions = ev.EventType == EventTypes.GeneralAssembly
                ? CandidatePositions.All.ToList()
                : await _db.EventPositionRules.AsNoTracking()
                    .Where(r => r.Event!.EventType == EventTypes.Representative)
                    .Select(r => r.Position).Distinct().OrderBy(p => p).Take(100).ToListAsync(ct);

            return new PositionRulesResponse
            {
                EventId = ev.EventId,
                EventType = ev.EventType,
                Editable = !gate.Locked,
                LockCode = gate.Code,
                LockReason = gate.Reason,
                IsDefault = isDefault,
                Rules = list,
                Warnings = warnings,
                UpdatedAt = ev.UpdatedAt,
                SuggestedPositions = suggestions,
                CustomPositionsAllowed = ev.EventType == EventTypes.Representative
            };
        }
    }
}
