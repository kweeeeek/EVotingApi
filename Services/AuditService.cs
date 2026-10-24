using EVoting.Api.Infrastructure;
using EVoting.Api.Security;
using System.Security.Claims;

namespace EVoting.Api.Services
{
    public interface IAuditService
    {
        /// <summary>The authenticated caller (“SYSTEM”/null when anonymous, e.g. a failed login).</summary>
        (int? UserId, string Username, string? Ip) Actor { get; }

        /// <summary>
        /// Adds an audit row to the CURRENT DbContext. It is committed by the caller's next SaveChangesAsync(),
        /// i.e. atomically with the business change — a change can never be committed without its audit trail.
        /// </summary>
        void Stage(string action, string entityType, int? entityId, string? details);

        /// <summary>Stage + save immediately (for requests that change nothing else, e.g. LOGIN_FAILED, EXPORT_REPORT).</summary>
        Task WriteAsync(string action, string entityType, int? entityId, string? details,
                        int? userId = null, string? username = null, CancellationToken ct = default);
    }

    public sealed class AuditService : IAuditService
    {
        private readonly ApiDbContext _db;
        private readonly IHttpContextAccessor _http;

        public AuditService(ApiDbContext db, IHttpContextAccessor http)
        {
            _db = db;
            _http = http;
        }

        public (int? UserId, string Username, string? Ip) Actor
        {
            get
            {
                var ctx = _http.HttpContext;
                var user = ctx?.User;
                int? id = user?.Identity?.IsAuthenticated == true ? RequirePermissionAttribute.UserIdOf(user) : null;
                var name = user?.FindFirst(ClaimTypes.Name)?.Value
                        ?? user?.FindFirst("unique_name")?.Value
                        ?? AuditWriter.SystemUser;
                return (id, name, ctx?.Connection.RemoteIpAddress?.ToString());
            }
        }

        public void Stage(string action, string entityType, int? entityId, string? details)
        {
            var a = Actor;
            _db.AuditLogs.Add(AuditWriter.Create(action, entityType, entityId, details, a.UserId, a.Username, a.Ip));
        }

        public async Task WriteAsync(string action, string entityType, int? entityId, string? details,
                                     int? userId = null, string? username = null, CancellationToken ct = default)
        {
            var a = Actor;
            _db.AuditLogs.Add(AuditWriter.Create(
                action, entityType, entityId, details,
                userId ?? a.UserId, username ?? a.Username, a.Ip));
            await _db.SaveChangesAsync(ct);
        }
    }
}
