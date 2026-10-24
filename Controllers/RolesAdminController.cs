using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Controllers
{
    /// <summary>Roles and role-permission assignment (replaces the legacy RoleRepository / PermissionManager direct-DB logic).</summary>
    [Route("api/v1/admin")]
    public class RolesAdminController : AdminControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly IPermissionService _perms;

        public RolesAdminController(ApiDbContext db, IAuditService audit, IPermissionService perms)
        {
            _db = db;
            _audit = audit;
            _perms = perms;
        }

        private static IQueryable<RoleRow> Project(IQueryable<RoleEntity> q) =>
            q.Select(r => new RoleRow
            {
                RoleId = r.RoleId,
                RoleName = r.RoleName,
                RoleDescription = r.RoleDescription ?? "",
                ActiveUserCount = r.Users.Count(u => u.IsActive),
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt
            });

        // Roles.CreatedAt/UpdatedAt are stored in UTC (legacy RoleRepository used DateTime.UtcNow).
        private static RoleRow Finish(RoleRow r)
        {
            r.CreatedAt = TimeConv.AsUtc(r.CreatedAt);
            r.UpdatedAt = TimeConv.AsUtc(r.UpdatedAt);
            return r;
        }

        // ── Roles: list / options / get ──────────────────────────────────────
        /// <param name="fromUtc">Inclusive instant. The CLIENT converts its local day boundaries (CreatedAt is stored in UTC).</param>
        /// <param name="toUtc">Inclusive instant.</param>
        [HttpGet("roles")]
        [RequirePermission(PermissionCodes.RoleView)]
        public async Task<IActionResult> List(
            [FromQuery] string? search, [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        {
            (page, pageSize) = ClampPaging(page, pageSize);
            var q = _db.Roles.AsNoTracking().Where(r => r.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                bool isId = int.TryParse(s, out var id);
                q = q.Where(r => r.RoleName.Contains(s) ||
                                 (r.RoleDescription != null && r.RoleDescription.Contains(s)) ||
                                 (isId && r.RoleId == id));
            }

            if (fromUtc.HasValue) { var f = fromUtc.Value.UtcDateTime; q = q.Where(r => r.CreatedAt >= f); }
            if (toUtc.HasValue) { var t = toUtc.Value.UtcDateTime; q = q.Where(r => r.CreatedAt <= t); }

            var total = await q.CountAsync(ct);
            var rows = await Project(q.OrderByDescending(r => r.RoleId).Skip((page - 1) * pageSize).Take(pageSize))
                .ToListAsync(ct);
            rows.ForEach(r => Finish(r));
            return Ok(Paged(rows, total, page, pageSize));
        }

        /// <summary>Dropdown of active roles for the user editor. <paramref name="includeId"/> keeps a user's CURRENT role selectable even if the role row is flagged inactive (e.g. the seeded SuperAdmin).</summary>
        [HttpGet("roles/options")]
        public async Task<IActionResult> Options([FromQuery] int? includeId, CancellationToken ct)
        {
            var rows = await _db.Roles.AsNoTracking()
                .Where(r => r.IsActive || (includeId != null && r.RoleId == includeId))
                .OrderBy(r => r.RoleName)
                .Select(r => new RoleOption { RoleId = r.RoleId, RoleName = r.RoleName })
                .ToListAsync(ct);
            return Ok(rows);
        }

        [HttpGet("roles/{id:int}")]
        [RequirePermission(PermissionCodes.RoleView, PermissionCodes.RoleEdit, PermissionCodes.RoleManager)]
        public async Task<IActionResult> Get(int id, CancellationToken ct)
        {
            var row = await Project(_db.Roles.AsNoTracking().Where(r => r.RoleId == id && r.IsActive))
                .FirstOrDefaultAsync(ct);
            if (row == null) return NotFoundError("Role");

            var ids = await _db.RolePermissions.AsNoTracking()
                .Where(rp => rp.RoleId == id).Select(rp => rp.PermissionId).ToListAsync(ct);
            Finish(row);
            return Ok(new RoleDetail
            {
                RoleId = row.RoleId,
                RoleName = row.RoleName,
                RoleDescription = row.RoleDescription,
                ActiveUserCount = row.ActiveUserCount,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                PermissionIds = ids
            });
        }

        [HttpGet("roles/check-name")]
        [RequirePermission(PermissionCodes.RoleView, PermissionCodes.RoleAdd, PermissionCodes.RoleEdit)]
        public async Task<IActionResult> CheckName([FromQuery] string name, [FromQuery] int? exceptId, CancellationToken ct)
        {
            var n = (name ?? "").Trim();
            var taken = await _db.Roles.AsNoTracking()
                .AnyAsync(r => r.IsActive && r.RoleName == n && (exceptId == null || r.RoleId != exceptId), ct);
            return Ok(new AvailabilityResponse { Taken = taken });
        }

        // ── Roles: create / update / delete ──────────────────────────────────
        [HttpPost("roles")]
        [RequirePermission(PermissionCodes.RoleAdd)]
        public async Task<IActionResult> Create([FromBody] RoleUpsertRequest req, CancellationToken ct)
        {
            var name = req.RoleName.Trim();
            if (name.Length == 0) return Invalid("roleName", "Role name is required.");
            if (await _db.Roles.AnyAsync(r => r.IsActive && r.RoleName == name, ct))
                return Duplicate("Role name is already taken.", "roleName");

            var now = DateTime.UtcNow;
            var role = new RoleEntity
            {
                RoleName = name,
                RoleDescription = (req.RoleDescription ?? "").Trim(),
                CreatedAt = now,
                UpdatedAt = now,
                IsActive = true
            };

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Roles.Add(role);
            await _db.SaveChangesAsync(ct);
            _audit.Stage(AuditActions.Create, AuditEntities.Role, role.RoleId, $"RoleName='{role.RoleName}'.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            var row = await Project(_db.Roles.AsNoTracking().Where(r => r.RoleId == role.RoleId)).FirstAsync(ct);
            return StatusCode(StatusCodes.Status201Created, Finish(row));
        }

        [HttpPut("roles/{id:int}")]
        [RequirePermission(PermissionCodes.RoleEdit)]
        public async Task<IActionResult> Update(int id, [FromBody] RoleUpsertRequest req, CancellationToken ct)
        {
            var role = await _db.Roles.FirstOrDefaultAsync(r => r.RoleId == id && r.IsActive, ct);
            if (role == null) return NotFoundError("Role");
            if (IsStale(req.ExpectedUpdatedAt, role.UpdatedAt)) return ConcurrencyError("role");

            var name = req.RoleName.Trim();
            if (name.Length == 0) return Invalid("roleName", "Role name is required.");
            if (await _db.Roles.AnyAsync(r => r.IsActive && r.RoleName == name && r.RoleId != id, ct))
                return Duplicate("Role name is already taken.", "roleName");

            role.RoleName = name;
            role.RoleDescription = (req.RoleDescription ?? "").Trim();
            role.UpdatedAt = DateTime.UtcNow;
            _audit.Stage(AuditActions.Update, AuditEntities.Role, role.RoleId, $"RoleName='{role.RoleName}'.");
            await _db.SaveChangesAsync(ct);

            var row = await Project(_db.Roles.AsNoTracking().Where(r => r.RoleId == id)).FirstAsync(ct);
            return Ok(Finish(row));
        }

        [HttpDelete("roles/{id:int}")]
        [RequirePermission(PermissionCodes.RoleDelete)]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            var role = await _db.Roles.FirstOrDefaultAsync(r => r.RoleId == id && r.IsActive, ct);
            if (role == null) return NotFoundError("Role");

            int activeUsers = await _db.Users.CountAsync(u => u.IsActive && u.RoleId == id, ct);
            if (activeUsers > 0)
                return Fail(StatusCodes.Status409Conflict,
                    $"Cannot delete this role — {activeUsers} active user(s) are still assigned to it.",
                    "ROLE_IN_USE", "Reassign them to another role first.");

            var now = DateTime.UtcNow;
            role.IsActive = false;
            role.DeletedAt = now;
            role.UpdatedAt = now;
            _audit.Stage(AuditActions.SoftDelete, AuditEntities.Role, role.RoleId, $"RoleName='{role.RoleName}'.");
            await _db.SaveChangesAsync(ct);
            _perms.Invalidate();
            return Ok(new MessageResponse { Message = $"Role '{role.RoleName}' deleted." });
        }

        // ── Permissions ──────────────────────────────────────────────────────
        [HttpGet("permissions")]
        [RequirePermission(PermissionCodes.RoleView, PermissionCodes.RoleManager)]
        public async Task<IActionResult> Permissions(CancellationToken ct)
        {
            var rows = await _db.Permissions.AsNoTracking()
                .OrderBy(p => p.Id)
                .Select(p => new PermissionRow { Id = p.Id, Code = p.Code, Name = p.Name })
                .ToListAsync(ct);
            return Ok(rows);
        }

        /// <summary>Replaces a role's permission set. Diff-based (adds the missing, removes the extra) inside one transaction.</summary>
        [HttpPut("roles/{id:int}/permissions")]
        [RequirePermission(PermissionCodes.RoleManager)]
        public async Task<IActionResult> SetPermissions(int id, [FromBody] SetRolePermissionsRequest req, CancellationToken ct)
        {
            var role = await _db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.RoleId == id, ct);
            if (role == null) return NotFoundError("Role");

            var wanted = req.PermissionIds.Distinct().ToList();
            var known = await _db.Permissions.AsNoTracking().ToListAsync(ct);
            var unknown = wanted.Where(w => known.All(k => k.Id != w)).ToList();
            if (unknown.Count > 0)
                return Invalid("permissionIds", $"Unknown permission id(s): {string.Join(", ", unknown)}.");

            // Self-lockout guard: the caller must not strip ROLE_MANAGER from the role they are using right now.
            var me = CurrentUserId;
            if (me.HasValue)
            {
                var myRoleId = await _db.Users.AsNoTracking().Where(u => u.Id == me.Value)
                    .Select(u => u.RoleId).FirstOrDefaultAsync(ct);
                var managerId = known.FirstOrDefault(k => k.Code == PermissionCodes.RoleManager)?.Id;
                if (myRoleId == id && managerId.HasValue && !wanted.Contains(managerId.Value))
                    return Fail(StatusCodes.Status409Conflict,
                        "You cannot remove 'Manage Role Permissions' from your own role — you would lock yourself out.",
                        "SELF_LOCKOUT");
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var existing = await _db.RolePermissions.Where(rp => rp.RoleId == id).ToListAsync(ct);
            var existingIds = existing.Select(e => e.PermissionId).ToHashSet();

            var toRemove = existing.Where(e => !wanted.Contains(e.PermissionId)).ToList();
            var toAdd = wanted.Where(w => !existingIds.Contains(w)).ToList();

            _db.RolePermissions.RemoveRange(toRemove);
            _db.RolePermissions.AddRange(toAdd.Select(pid => new RolePermissionEntity { RoleId = id, PermissionId = pid }));

            var added = toAdd.Select(pid => known.First(k => k.Id == pid).Code).ToList();
            var removed = toRemove.Select(e => known.FirstOrDefault(k => k.Id == e.PermissionId)?.Code ?? $"#{e.PermissionId}").ToList();

            if (added.Count > 0 || removed.Count > 0)
                _audit.Stage(AuditActions.UpdatePermissions, AuditEntities.Role, id,
                    $"Role '{role.RoleName}': +[{string.Join(", ", added)}] -[{string.Join(", ", removed)}].");

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _perms.Invalidate();   // takes effect on the very next request — no re-login needed

            return Ok(new SetRolePermissionsResponse { Added = added, Removed = removed, TotalAssigned = wanted.Count });
        }
    }
}
