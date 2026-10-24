using EVoting.Api.Models.Responses;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EVoting.Api.Controllers
{
    // ══════════════════════════════════════════════════════════════════════════
    //  MonitoringController
    //
    //  Endpoints
    //  ──────────
    //  GET /api/v1/monitoring/system     Process/runtime/OS metrics for the API
    //  GET /api/v1/monitoring/database   MySQL connection/perf/storage metrics
    //  GET /api/v1/monitoring/requests   API traffic, error, and latency stats
    //  GET /api/v1/monitoring/nginx      Nginx stub_status (optional; see nginx.conf)
    //  GET /api/v1/monitoring/overview   All of the above in one call
    //
    //  All endpoints require the Admin role. This surfaces internal
    //  diagnostics — exception messages, table sizes, in-flight query text —
    //  so it stays behind the same auth boundary as /api/v1/admin rather
    //  than the anonymous /api/v1/health/* liveness checks.
    // ══════════════════════════════════════════════════════════════════════════
    [ApiController]
    [Route("api/v1/monitoring")]
    [Authorize(Roles = "Admin")]
    public class MonitoringController : ControllerBase
    {
        private readonly ISystemMonitoringService _system;
        private readonly IDatabaseMonitoringService _database;
        private readonly IRequestMetricsService _requests;
        private readonly INginxMonitoringService _nginx;

        public MonitoringController(
            ISystemMonitoringService system,
            IDatabaseMonitoringService database,
            IRequestMetricsService requests,
            INginxMonitoringService nginx)
        {
            _system = system;
            _database = database;
            _requests = requests;
            _nginx = nginx;
        }

        // ── GET /api/v1/monitoring/system ─────────────────────────────────────
        [HttpGet("system")]
        public IActionResult GetSystem() => Ok(_system.GetSnapshot());

        // ── GET /api/v1/monitoring/database ───────────────────────────────────
        [HttpGet("database")]
        public async Task<IActionResult> GetDatabase(CancellationToken ct) =>
            Ok(await _database.GetSnapshotAsync(ct));

        // ── GET /api/v1/monitoring/requests ───────────────────────────────────
        [HttpGet("requests")]
        public IActionResult GetRequests() => Ok(_requests.GetSnapshot());

        // ── GET /api/v1/monitoring/nginx ──────────────────────────────────────
        [HttpGet("nginx")]
        public async Task<IActionResult> GetNginx(CancellationToken ct) =>
            Ok(await _nginx.GetSnapshotAsync(ct));

        // ── GET /api/v1/monitoring/overview ───────────────────────────────────
        /// <summary>
        /// All four sections in one response. The dashboard normally polls the
        /// individual endpoints instead (so it can refresh the cheap in-memory
        /// sections more often than the MySQL/Nginx ones) — this is here for
        /// convenience (curl, external scripts, a single first paint) and for
        /// a clean way to compute one overall status across every source.
        /// </summary>
        [HttpGet("overview")]
        public async Task<IActionResult> GetOverview(CancellationToken ct)
        {
            // Only the database/nginx calls do real I/O — run them concurrently.
            // System/requests are cheap, synchronous, in-memory reads.
            var databaseTask = _database.GetSnapshotAsync(ct);
            var nginxTask = _nginx.GetSnapshotAsync(ct);

            var systemSnapshot = _system.GetSnapshot();
            var requestsSnapshot = _requests.GetSnapshot();

            await Task.WhenAll(databaseTask, nginxTask);

            var databaseSnapshot = await databaseTask;
            var nginxSnapshot = await nginxTask;

            return Ok(new MonitoringOverviewResponse
            {
                GeneratedAtUtc = DateTime.UtcNow,
                Status = ComputeOverallStatus(databaseSnapshot, requestsSnapshot),
                System = systemSnapshot,
                Database = databaseSnapshot,
                Requests = requestsSnapshot,
                Nginx = nginxSnapshot
            });
        }

        /// <summary>
        /// A simple, deliberately conservative heuristic — tune the
        /// thresholds to taste. Nginx availability never affects this status
        /// since that panel is optional/bonus by design.
        /// </summary>
        private static string ComputeOverallStatus(
            DatabaseMonitoringSnapshot database, RequestMetricsSnapshot requests)
        {
            if (!database.IsConnected) return "unhealthy";

            if (requests.ErrorRatePercent > 5 || (database.ConnectionUsagePercent ?? 0) > 90)
                return "degraded";

            return "healthy";
        }
    }
}
