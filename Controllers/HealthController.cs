using EVoting.Api.Infrastructure;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;

namespace EVoting.Api.Controllers
{
    // ══════════════════════════════════════════════════════════════════════════
    //  HealthController  (expanded for Server PC setup)
    //
    //  Endpoints
    //  ──────────
    //  GET  /api/v1/health/live        Liveness — process alive?
    //  GET  /api/v1/health/ready       Readiness — MySQL + Redis reachable?
    //  POST /api/v1/health/heartbeat   Kiosk sends this every 30 seconds
    //  GET  /api/v1/health/kiosks      Admin: list all kiosk statuses
    //
    //  The ASP.NET Core built-in health check endpoints (/health/live,
    //  /health/ready, /health/detail) are registered in Program.cs via
    //  MapHealthChecks(). The endpoints here are the application-level
    //  equivalents that include kiosk status data.
    // ══════════════════════════════════════════════════════════════════════════

    [ApiController]
    [Route("api/v1/health")]
    public class HealthController : ControllerBase
    {
        private readonly ApiDbContext        _db;
        private readonly IKioskStatusService _kioskStatus;
        private readonly IDistributedCache   _cache;
        private readonly IConfiguration      _config;
        private readonly ILogger<HealthController> _logger;

        public HealthController(
            ApiDbContext db,
            IKioskStatusService kioskStatus,
            IDistributedCache cache,
            IConfiguration config,
            ILogger<HealthController> logger)
        {
            _db          = db;
            _kioskStatus = kioskStatus;
            _cache       = cache;
            _config      = config;
            _logger      = logger;
        }

        // ── GET /api/v1/health/live ───────────────────────────────────────────
        /// <summary>
        /// Liveness probe — returns 200 if the process is running.
        /// Nginx uses this to determine if the upstream is alive.
        /// </summary>
        [HttpGet("live")]
        [AllowAnonymous]
        public IActionResult Live() =>
            Ok(new { status = "alive", utc = DateTime.UtcNow });

        // ── GET /api/v1/health/ready ──────────────────────────────────────────
        /// <summary>
        /// Readiness probe — checks MySQL and Redis.
        /// Returns 503 if either is unreachable so Nginx can stop routing traffic.
        /// </summary>
        [HttpGet("ready")]
        [AllowAnonymous]
        public async Task<IActionResult> Ready(CancellationToken ct)
        {
            var result = new Dictionary<string, object>
            {
                ["status"] = "ready",
                ["utc"]    = DateTime.UtcNow
            };

            // MySQL check. EF Core's CanConnectAsync ANSWERS false for an unreachable database (it only throws for unexpected failures), so
            // the answer must be read: ignoring it reported "connected" while the database was down.
            bool reachable;
            try
            {
                reachable = await _db.Database.CanConnectAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // This endpoint is anonymous: the exception text (host names, user names, parts of the connection string) stays in the server log.
                _logger.LogError(ex, "Readiness check: the database is not reachable.");
                reachable = false;
            }
            if (!reachable)
            {
                result["mysql"]  = "unavailable";
                result["status"] = "unhealthy";
                return StatusCode(503, result);
            }
            result["mysql"] = "connected";

            // Redis check (non-fatal — API degrades gracefully without cache)
            try
            {
                await _cache.GetAsync("__health_probe__", ct);
                result["redis"] = "connected";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Readiness check: the cache is not reachable.");
                result["redis"]  = "unavailable";
                result["status"] = "degraded";
                // Return 200 degraded — Redis miss is non-fatal, API still serves votes
            }

            return Ok(result);
        }

        // ── POST /api/v1/health/heartbeat ─────────────────────────────────────
        /// <summary>
        /// Kiosk devices call this every 30 seconds to signal they are alive.
        /// Requires a valid Kiosk JWT. The KioskId is read from the JWT claim,
        /// not the request body, so it cannot be spoofed.
        /// </summary>
        [HttpPost("heartbeat")]
        [Authorize(Roles = "Kiosk")]
        public IActionResult Heartbeat()
        {
            var kioskId = User.FindFirst("kioskId")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? "unknown";

            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

            _kioskStatus.RecordHeartbeat(kioskId, ip);

            return Ok(new { received = true, kioskId, utc = DateTime.UtcNow });
        }

       
        [HttpGet("kiosks")]
        [Authorize(Roles = "Admin")]
        public IActionResult GetKioskStatuses()
        {
            // BUG FIX: int.Parse throws unhandled FormatException if config value is non-numeric.
            // Use int.TryParse with a safe fallback instead.
            if (!int.TryParse(_config["Cache:KioskHeartbeatTimeoutSeconds"], out int timeoutSeconds))
                timeoutSeconds = 90;

            var statuses = _kioskStatus.GetAllStatuses(timeoutSeconds);
            int online   = statuses.Count(s => s.IsOnline);

            return Ok(new
            {
                total   = statuses.Count,
                online,
                offline = statuses.Count - online,
                kiosks  = statuses.Select(s => new
                {
                    s.KioskId,
                    s.IpAddress,
                    s.IsOnline,
                    s.LastSeen,
                    s.SecondsAgo
                })
            });
        }
    }
}
