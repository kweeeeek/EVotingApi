using EVoting.Api.Hubs;
using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Requests;
using EVoting.Api.Models.Responses;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using System.Security.Claims;
using System.Text.Json;

namespace EVoting.Api.Controllers
{
    // ══════════════════════════════════════════════════════════════════════════
    //  EventsController  (WebSocket upgrade)
    //
    //  Key change: UpdateStatus() now calls IHubContext<ElectionHub> to push
    //  "EventStatusChanged" to all connected kiosks the instant the admin
    //  opens or closes the election.
    //
    //  OLD flow:
    //    Admin PATCH /status  →  kiosk polls GET /events/current every 10 s
    //                            → notices change on the next poll cycle
    //
    //  NEW flow:
    //    Admin PATCH /status  →  SignalR push "EventStatusChanged" to "kiosks" group
    //                            → every kiosk reacts within milliseconds
    //
    //  Everything else (caching, auth, candidate loading) is unchanged.
    // ══════════════════════════════════════════════════════════════════════════

    [ApiController]
    [Route("api/v1/events")]
    public class EventsController : ControllerBase
    {
        private const string CacheKeyCurrentEvent = "current_open_event";
        private static string CandidatesCacheKey(int eventId) => $"candidates_{eventId}";

        private readonly ApiDbContext _db;
        private readonly IDistributedCache _cache;
        private readonly IConfiguration _config;
        private readonly IHubContext<ElectionHub> _hub;
        private readonly ILogger<EventsController> _logger;
        private readonly IEventStateService _state;
        private readonly IAuditService _audit;

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public EventsController(
            ApiDbContext db,
            IDistributedCache cache,
            IConfiguration config,
            IHubContext<ElectionHub> hub,
            ILogger<EventsController> logger,
            IEventStateService state,
            IAuditService audit)
        {
            _db = db;
            _cache = cache;
            _config = config;
            _hub = hub;
            _logger = logger;
            _state = state;
            _audit = audit;
        }

        // ── GET /api/v1/events/current ────────────────────────────────────────
        [HttpGet("current")]
        [Authorize(Roles = "Kiosk,Voter,Admin")]
        public async Task<IActionResult> GetCurrent(CancellationToken ct)
        {
            if (!int.TryParse(_config["Cache:CurrentEventTtlSeconds"], out int ttlSeconds))
                ttlSeconds = 60;

            var cached = await GetCachedAsync<EventResponse>(CacheKeyCurrentEvent, ct);
            if (cached != null) return Ok(cached);

            var ev = await _db.Events
                .AsNoTracking()
                .Include(e => e.EventBranches)
                    .ThenInclude(eb => eb.Branch)
                .FirstOrDefaultAsync(e => e.Status == "Opened" && e.IsActive, ct);

            if (ev == null)
                return NotFound(new ApiError { Error = "No election event is currently open." });

            var response = MapEvent(ev);
            await SetCachedAsync(CacheKeyCurrentEvent, response,
                TimeSpan.FromSeconds(ttlSeconds), ct);

            return Ok(response);
        }

        // ── GET /api/v1/events ────────────────────────────────────────────────
        [HttpGet]
        [Authorize(Roles = "Admin")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> List(CancellationToken ct)
        {
            var events = await _db.Events
                .AsNoTracking()
                .Where(e => e.IsActive)
                .Include(e => e.EventBranches)
                    .ThenInclude(eb => eb.Branch)
                .OrderByDescending(e => e.ScheduleFrom)
                .ToListAsync(ct);

            return Ok(events.Select(MapEvent));
        }

        // ── GET /api/v1/events/{id}/candidates ────────────────────────────────
        [HttpGet("{id:int}/candidates")]
        [Authorize(Roles = "Kiosk,Voter,Admin")]
        public async Task<IActionResult> GetCandidates(int id, CancellationToken ct)
        {
            if (!int.TryParse(_config["Cache:CandidatesTtlMinutes"], out int ttlMinutes))
                ttlMinutes = 2;
            var cacheKey = CandidatesCacheKey(id);

            var cached = await GetCachedAsync<List<CandidateResponse>>(cacheKey, ct);
            if (cached != null) return Ok(cached);

            var ev = await _db.Events.AsNoTracking()
                .FirstOrDefaultAsync(e => e.EventId == id && e.IsActive, ct);

            if (ev == null)
                return NotFound(new ApiError { Error = "Event not found." });

            var candidates = await _db.Candidacies
                .AsNoTracking()
                .Include(c => c.Member)
                .Where(c => c.Status == "Approved" && c.IsActive &&
                            (c.DateApproved == null
                                ? c.DateFiled >= ev.ScheduleFrom.AddDays(-180)
                                : c.DateApproved >= ev.ScheduleFrom.AddDays(-180)))
                .OrderBy(c => c.Position)
                .ThenBy(c => c.MemberId != null
                    ? c.Member!.MemberName : c.WalkInCandidateName ?? "")
                .ToListAsync(ct);

            var response = candidates.Select(c => new CandidateResponse
            {
                CandidacyId = c.CandidacyId,
                CandidateName = c.CandidateDisplayName,
                Position = c.Position,
                Status = c.Status,
                PreviousPositions = c.PreviousPositions,
                Gender = c.Gender,
                HighestEducation = c.HighestEducation,
                Reason = c.Reason,
                PhotoBase64 = c.CandidateImage != null
                    ? Convert.ToBase64String(c.CandidateImage) : null
            }).ToList();

            await SetCachedAsync(cacheKey, response,
                TimeSpan.FromMinutes(ttlMinutes), ct);

            return Ok(response);
        }

        // ── GET /api/v1/events/{id}/member-snapshot ───────────────────────────
        // FEATURE (OFFLINE VERIFICATION): kiosk-only bulk fetch of every active
        // member with eligibility precomputed against THIS event, cached to the
        // kiosk's local (encrypted) SQLite at startup. Deliberately excludes
        // "has voted" — see MemberSnapshotRow doc comment. Kiosk-only (not
        // Voter/Admin): this is PII for the full roster, not a single lookup,
        // and Admin already has its own paginated /admin/members endpoint for
        // its own purposes. Not cached in Redis — called once per kiosk at
        // startup, not once per voter, so cache churn isn't worth it here.
        [HttpGet("{id:int}/member-snapshot")]
        [Authorize(Roles = "Kiosk")]
        public async Task<IActionResult> GetMemberSnapshot(int id, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking()
                .Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.EventId == id && e.IsActive, ct);

            if (ev == null)
                return NotFound(new ApiError { Error = "Event not found." });

            var assignedBranchIds = ev.EventBranches.Select(eb => eb.BranchId).ToHashSet();

            var members = await _db.Members
                .AsNoTracking()
                .Include(m => m.Branch)
                .Where(m => m.IsActive)
                .ToListAsync(ct);

            var snapshot = members.Select(m => new MemberSnapshotRow
            {
                MemberId = m.MemberId,
                AccountCode = m.AccountCode,
                MemberName = m.MemberName,
                BranchId = m.BranchId,
                BranchName = m.Branch?.BranchName ?? "",
                // Same three conditions AuthController.VoterVerify checks online —
                // kept in sync deliberately so offline and online agree.
                IsEligible = m.IsEligibleToVote &&
                              assignedBranchIds.Contains(m.BranchId),
                PhotoBase64 = m.MemberImage != null
                    ? Convert.ToBase64String(m.MemberImage) : null
            }).ToList();

            _logger.LogInformation(
                "Member snapshot served: EventId={EventId} KioskId={KioskId} MemberCount={Count}",
                id, User.FindFirstValue("kioskId") ?? "(unknown)", snapshot.Count);

            return Ok(snapshot);
        }


        [HttpGet("{id:int}/turnout")]
        [Authorize(Roles = "Admin")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> GetTurnout(int id, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking()
                .Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.EventId == id, ct);
            if (ev == null)
                return NotFound(new ApiError { Error = "Event not found." });

            var branchIds = ev.EventBranches.Select(eb => eb.BranchId).ToList();
            int eligible = await _db.Members
                .CountAsync(m => branchIds.Contains(m.BranchId) &&
                                 m.IsEligibleToVote && m.IsActive, ct);
            int voted = await _db.VoterLogs
                .CountAsync(v => v.EventId == id && v.HasVoted, ct);

            return Ok(new TurnoutResponse
            {
                EventId = id,
                TotalEligible = eligible,
                TotalVoted = voted,
                TurnoutPercent = eligible > 0
                    ? Math.Round((double)voted / eligible * 100, 2) : 0
            });
        }

        // ── GET /api/v1/events/{id}/results ───────────────────────────────────
        [HttpGet("{id:int}/results")]
        [Authorize(Roles = "Admin")]
        [RequirePermission(PermissionCodes.EventView)]
        public async Task<IActionResult> GetResults(int id, CancellationToken ct)
        {
            var votes = await _db.Votes
                .AsNoTracking()
                .Where(v => v.EventId == id)
                .GroupBy(v => new { v.Position, v.CandidacyId })
                .Select(g => new { g.Key.Position, g.Key.CandidacyId, Count = g.Count() })
                .ToListAsync(ct);

            var candidacyIds = votes.Select(v => v.CandidacyId).Distinct().ToList();
            var candidacies = await _db.Candidacies
                .AsNoTracking()
                .Include(c => c.Member)
                .Where(c => candidacyIds.Contains(c.CandidacyId))
                .ToDictionaryAsync(c => c.CandidacyId, ct);

            var results = votes
                .GroupBy(v => v.Position)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    int total = g.Sum(v => v.Count);
                    return new VoteResultResponse
                    {
                        Position = g.Key,
                        Candidates = g
                            .OrderByDescending(v => v.Count)
                            .Select(v => new CandidateResult
                            {
                                CandidacyId = v.CandidacyId,
                                CandidateName = candidacies.TryGetValue(v.CandidacyId, out var c)
                                    ? c.CandidateDisplayName : "Unknown",
                                VoteCount = v.Count,
                                Percentage = total > 0
                                    ? Math.Round((double)v.Count / total * 100, 2) : 0
                            }).ToList()
                    };
                }).ToList();

            return Ok(results);
        }

        // ── POST /api/v1/events ───────────────────────────────────────────────
        [HttpPost]
        [Authorize(Roles = "Admin")]
        [RequirePermission(PermissionCodes.EventAdd)]
        public async Task<IActionResult> Create(
            [FromBody] CreateEventRequest req, CancellationToken ct)
        {
            if (req.ScheduleFrom >= req.ScheduleTo)
                return BadRequest(new ApiError { Error = "ScheduleTo must be after ScheduleFrom." });

            var ev = new Infrastructure.Entities.EventEntity
            {
                Title = req.Title,
                Venue = req.Venue,
                ScheduleFrom = req.ScheduleFrom,
                ScheduleTo = req.ScheduleTo,
                Status = "Closed",
                IsActive = true,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };

            foreach (var branchId in req.BranchIds.Distinct())
                ev.EventBranches.Add(new Infrastructure.Entities.EventBranchEntity
                { BranchId = branchId });

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Events.Add(ev);
            await _db.SaveChangesAsync(ct);
            _audit.Stage(AuditActions.Create, AuditEntities.Event, ev.EventId,
                $"Title='{ev.Title}' (created through POST /events; the Admin application uses /admin/events).");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Event created: EventId={EventId} Title={Title}", ev.EventId, ev.Title);
            return CreatedAtAction(nameof(GetCurrent), new { id = ev.EventId }, MapEvent(ev));
        }

        // ── PATCH /api/v1/events/{id}/status ──────────────────────────────────
        /// <summary>
        /// Open or close an election. After saving, pushes "EventStatusChanged"
        /// to all connected kiosks via SignalR so they react immediately —
        /// no polling required on the kiosk side.
        /// </summary>
        [HttpPatch("{id:int}/status")]
        [Authorize(Roles = "Admin")]
        [RequirePermission(PermissionCodes.EventEdit)]
        public async Task<IActionResult> UpdateStatus(
            int id, [FromBody] UpdateEventStatusRequest req, CancellationToken ct)
        {
            // Delegates to the shared state service: single-open invariant, atomic transition, audit trail,
            // cache eviction and SignalR push now live in ONE place (they were duplicated across the Admin and API).
            // Behaviour of this endpoint is unchanged: opening while another event is open still answers 409.
            var result = req.Status == "Opened"
                ? await _state.OpenAsync(id, closeOthers: false, ct)
                : await _state.CloseAsync(id, ct);

            if (!result.Ok)
                return StatusCode(result.StatusCode, new ApiError
                {
                    Error = result.Error ?? "The request failed.",
                    Detail = result.Detail,
                    Code = result.Code
                });

            _logger.LogInformation("Event {EventId} status changed to {Status}", id, req.Status);
            return Ok(new { message = $"Event status updated to {req.Status}.", eventId = id });
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<T?> GetCachedAsync<T>(string key, CancellationToken ct)
            where T : class
        {
            try
            {
                var bytes = await _cache.GetAsync(key, ct);
                if (bytes == null || bytes.Length == 0) return null;
                return JsonSerializer.Deserialize<T>(bytes, _json);
            }
            catch { return null; }
        }

        private async Task SetCachedAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct)
        {
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _json);
                await _cache.SetAsync(key, bytes,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl },
                    ct);
            }
            catch { /* Redis write failure is non-fatal */ }
        }

        private static EventResponse MapEvent(Infrastructure.Entities.EventEntity e) => new()
        {
            EventId = e.EventId,
            Title = e.Title,
            Venue = e.Venue,
            ScheduleFrom = e.ScheduleFrom,
            ScheduleTo = e.ScheduleTo,
            Status = e.Status,
            IsActive = e.IsActive,
            Branches = e.EventBranches
                .Where(eb => eb.Branch != null)
                .Select(eb => new BranchResponse
                {
                    BranchId = eb.Branch!.BranchId,
                    BranchName = eb.Branch.BranchName
                }).ToList()
        };
        // ── GET /api/v1/events/{id}/position-rules ─────────────────────────────
        [HttpGet("{id:int}/position-rules")]
        //[Authorize(Policy = "KioskOrAdmin")]
        [Authorize(Roles = "Kiosk,Admin")]
        public async Task<IActionResult> GetPositionRules(int id, CancellationToken ct)
        {
            var rules = await _db.EventPositionRules
                .AsNoTracking()
                .Where(r => r.EventId == id)
                .Select(r => new { r.Position, r.MinVotes, r.MaxVotes, r.TotalSeats })
                .ToListAsync(ct);

            if (rules.Count == 0)
                rules = new[]
                {
                    new { Position = "Board of Director",  MinVotes = 1, MaxVotes = 11, TotalSeats = 11 },
                    new { Position = "Audit Committee",    MinVotes = 1, MaxVotes = 2,  TotalSeats = 2  },
                    new { Position = "Election Committee", MinVotes = 1, MaxVotes = 1,  TotalSeats = 1  },
                    new { Position = "Secretary",          MinVotes = 1, MaxVotes = 2,  TotalSeats = 2  }
                }.ToList();

            return Ok(new { eventId = id, rules });
        }

    }
}