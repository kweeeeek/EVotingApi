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
    /// Read access to the administrative audit trail (AuditLogs). Rows are WRITTEN only by the server, atomically with
    /// the change they describe — there is deliberately no endpoint that lets a client insert, edit or delete audit rows
    /// (the single exception, <c>report-exported</c>, records that an export happened and is stamped with the caller's identity).
    /// </summary>
    [Route("api/v1/admin/audit-logs")]
    public class AuditLogsController : AdminControllerBase
    {
        private const int ExportCap = 50_000;

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;

        public AuditLogsController(ApiDbContext db, IAuditService audit)
        {
            _db = db;
            _audit = audit;
        }

        /// <param name="fromUtc">Inclusive lower bound as an instant (ISO-8601 with offset/Z). The CLIENT converts its local day boundaries.</param>
        /// <param name="toUtc">Inclusive upper bound as an instant.</param>
        private IQueryable<AuditLogEntity> BuildQuery(
            DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? action, string? entityType, string? search)
        {
            var q = _db.AuditLogs.AsNoTracking();

            // OccurredAtUtc is stored in UTC. The legacy viewer compared it with LOCAL day boundaries, so in UTC+8
            // the first 8 hours of the chosen day were missed and the first 8 hours of the next day were included.
            if (fromUtc.HasValue) { var f = fromUtc.Value.UtcDateTime; q = q.Where(a => a.OccurredAtUtc >= f); }
            if (toUtc.HasValue) { var t = toUtc.Value.UtcDateTime; q = q.Where(a => a.OccurredAtUtc <= t); }

            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action == action);
            if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(a => a.EntityType == entityType);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(a =>
                    (a.Username != null && a.Username.Contains(s)) ||
                    (a.Details != null && a.Details.Contains(s)) ||
                    (a.IpAddress != null && a.IpAddress.Contains(s)) ||
                    a.Action.Contains(s) ||
                    a.EntityType.Contains(s));
            }
            return q;
        }

        private static IQueryable<AuditRow> Project(IQueryable<AuditLogEntity> q) =>
            q.Select(a => new AuditRow
            {
                AuditId = a.AuditId,
                OccurredAtUtc = a.OccurredAtUtc,
                Action = a.Action,
                EntityType = a.EntityType,
                EntityId = a.EntityId,
                UserId = a.UserId,
                Username = a.Username,
                IpAddress = a.IpAddress,
                Details = a.Details
            });

        private static AuditRow Finish(AuditRow r)
        {
            r.OccurredAtUtc = TimeConv.AsUtc(r.OccurredAtUtc);   // → serialised with 'Z'
            return r;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.AuditView)]
        public async Task<IActionResult> List(
            [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] string? action, [FromQuery] string? entityType, [FromQuery] string? search,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
        {
            (page, pageSize) = ClampPaging(page, pageSize);
            var q = BuildQuery(fromUtc, toUtc, action, entityType, search);
            var total = await q.CountAsync(ct);
            var rows = await Project(q.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.AuditId)
                    .Skip((page - 1) * pageSize).Take(pageSize))
                .ToListAsync(ct);
            rows.ForEach(r => Finish(r));
            return Ok(Paged(rows, total, page, pageSize));
        }

        [HttpGet("filters")]
        [RequirePermission(PermissionCodes.AuditView)]
        public async Task<IActionResult> Filters(CancellationToken ct)
        {
            var actions = await _db.AuditLogs.AsNoTracking().Select(a => a.Action).Distinct().ToListAsync(ct);
            var types = await _db.AuditLogs.AsNoTracking().Select(a => a.EntityType).Distinct().ToListAsync(ct);
            return Ok(new AuditFiltersResponse
            {
                Actions = actions.Union(AuditActions.All).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                EntityTypes = types.Union(new[]
                    {
                        AuditEntities.Member, AuditEntities.Role, AuditEntities.Branch,
                        AuditEntities.User, AuditEntities.Event, AuditEntities.Candidacy
                    })
                    .OrderBy(x => x, StringComparer.Ordinal).ToList()
            });
        }

        [HttpGet("export")]
        [RequirePermission(PermissionCodes.AuditView)]
        public async Task<IActionResult> Export(
            [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] string? action, [FromQuery] string? entityType, [FromQuery] string? search,
            CancellationToken ct = default)
        {
            var q = BuildQuery(fromUtc, toUtc, action, entityType, search);
            var rows = await Project(q.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.AuditId).Take(ExportCap + 1))
                .ToListAsync(ct);
            if (rows.Count > ExportCap)
            {
                Response.Headers["X-Export-Truncated"] = "true";
                rows.RemoveAt(rows.Count - 1);
            }
            rows.ForEach(r => Finish(r));
            return Ok(rows);
        }

        /// <summary>Records that the caller exported a report (the file itself is produced client-side).</summary>
        [HttpPost("report-exported")]
        [RequirePermission(PermissionCodes.ReportExport, PermissionCodes.ReportView, PermissionCodes.AuditView,
                           PermissionCodes.EventView, PermissionCodes.MemberView, PermissionCodes.CandidateView,
                           PermissionCodes.UserView)]
        public async Task<IActionResult> ReportExported([FromBody] ReportExportedRequest req, CancellationToken ct)
        {
            await _audit.WriteAsync(AuditActions.ExportReport, AuditEntities.Report, null,
                $"{req.ReportName.Trim()}" + (string.IsNullOrWhiteSpace(req.Details) ? "" : $": {req.Details.Trim()}"), ct: ct);
            return Ok(new MessageResponse { Message = "Export recorded." });
        }
    }
}
