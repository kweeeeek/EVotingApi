using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace EVoting.Api.Security
{
    /// <summary>
    /// Resolves a user's CURRENT permissions from the database (cached for a few seconds).
    ///
    /// Why not trust the JWT "permission" claims? They are frozen at login for up to 8 hours:
    /// a revoked permission, a deactivated user, or a changed role would stay effective until the
    /// token expires. Authorization must be enforced server-side and independently of the client.
    ///
    /// NOTE: Role.IsActive is intentionally NOT consulted. The seeded SuperAdmin role is created
    /// by HasData without IsActive, so gating on it would lock the seeded administrator out.
    /// </summary>
    public interface IPermissionService
    {
        Task<IReadOnlySet<string>> GetPermissionsAsync(int userId, CancellationToken ct = default);
        Task<bool> HasAnyAsync(int userId, IReadOnlyCollection<string> anyOf, CancellationToken ct = default);

        /// <summary>Drop all cached permission sets (call after role/permission/user changes).</summary>
        void Invalidate();
    }

    public sealed class PermissionService : IPermissionService
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);
        private static int _generation;

        private readonly ApiDbContext _db;
        private readonly IMemoryCache _cache;

        public PermissionService(ApiDbContext db, IMemoryCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<IReadOnlySet<string>> GetPermissionsAsync(int userId, CancellationToken ct = default)
        {
            var key = $"perm:{Volatile.Read(ref _generation)}:{userId}";
            if (_cache.TryGetValue(key, out IReadOnlySet<string>? cached) && cached != null)
                return cached;

            var roleId = await _db.Users.AsNoTracking()
                .Where(u => u.Id == userId && u.IsActive)
                .Select(u => u.RoleId)
                .FirstOrDefaultAsync(ct);

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (roleId.HasValue)
            {
                var codes = await _db.RolePermissions.AsNoTracking()
                    .Where(rp => rp.RoleId == roleId.Value)
                    .Select(rp => rp.Permission!.Code)
                    .ToListAsync(ct);
                foreach (var c in codes) set.Add(c);
            }

            IReadOnlySet<string> result = set;
            _cache.Set(key, result, Ttl);
            return result;
        }

        public async Task<bool> HasAnyAsync(int userId, IReadOnlyCollection<string> anyOf, CancellationToken ct = default)
        {
            if (anyOf.Count == 0) return true;
            var perms = await GetPermissionsAsync(userId, ct);
            foreach (var code in anyOf)
                if (perms.Contains(code)) return true;
            return false;
        }

        public void Invalidate() => Interlocked.Increment(ref _generation);
    }

    /// <summary>
    /// Requires the caller to hold AT LEAST ONE of the listed permissions (OR within one attribute).
    /// Several attributes on the same action are ANDed. Combine with [Authorize(Roles = "Admin")].
    /// Responds 403 with the standard ApiError body when the permission is missing.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequirePermissionAttribute : Attribute, IAsyncAuthorizationFilter
    {
        public string[] AnyOf { get; }

        public RequirePermissionAttribute(params string[] anyOf) => AnyOf = anyOf;

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var user = context.HttpContext.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            var userId = UserIdOf(user);
            if (userId == null)
            {
                context.Result = Forbidden("Your session does not identify a user.");
                return;
            }

            var svc = context.HttpContext.RequestServices.GetRequiredService<IPermissionService>();
            if (await svc.HasAnyAsync(userId.Value, AnyOf, context.HttpContext.RequestAborted))
                return;

            context.Result = Forbidden(
                "You do not have permission to perform this action.",
                string.Join(" or ", AnyOf));
        }

        private static ObjectResult Forbidden(string message, string? required = null) =>
            new(new ApiError { Error = message, Detail = required == null ? null : $"Required: {required}", Code = "FORBIDDEN" })
            { StatusCode = StatusCodes.Status403Forbidden };

        /// <summary>User id from the JWT; tolerant of inbound claim-type mapping settings.</summary>
        public static int? UserIdOf(ClaimsPrincipal user)
        {
            var raw = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? user.FindFirst("nameid")?.Value
                   ?? user.FindFirst("sub")?.Value;
            return int.TryParse(raw, out var id) ? id : null;
        }
    }
}
