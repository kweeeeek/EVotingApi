using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// User management. Replaces the legacy Admin's direct-DB UserRepository / UserEditor / UserManagement logic.
    /// Passwords are hashed HERE (the pepper never leaves the server).
    /// </summary>
    [Route("api/v1/admin/users")]
    public class UsersAdminController : AdminControllerBase
    {
        private const int MaxImportRows = 100;

        private static readonly Regex UsernameRx = new(@"^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
        private static readonly Regex EmailRx = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
        private static readonly Regex ContactRx = new(@"^[0-9]{11}$", RegexOptions.Compiled);

        private readonly ApiDbContext _db;
        private readonly IPasswordService _pw;
        private readonly IAuditService _audit;
        private readonly IPermissionService _perms;
        private readonly ILogger<UsersAdminController> _logger;

        public UsersAdminController(ApiDbContext db, IPasswordService pw, IAuditService audit,
                                    IPermissionService perms, ILogger<UsersAdminController> logger)
        {
            _db = db;
            _pw = pw;
            _audit = audit;
            _perms = perms;
            _logger = logger;
        }

        private static string NameOf(UserEntity u) =>
            string.Join(" ", new[] { u.FirstName, u.MiddleName, u.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // ── Queries ──────────────────────────────────────────────────────────
        private IQueryable<UserEntity> BuildQuery(string? search, DateTime? from, DateTime? to)
        {
            var q = _db.Users.AsNoTracking().Where(u => u.IsActive);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                bool isNumeric = int.TryParse(s, out var searchId);
                bool composite = UserCodes.TryParse(s, out var cBranch, out var cRole, out var cUser);

                q = q.Where(u =>
                    u.FirstName.Contains(s) ||
                    u.LastName.Contains(s) ||
                    u.Username.Contains(s) ||
                    u.Email.Contains(s) ||
                    (isNumeric && (u.BranchId == searchId || u.RoleId == searchId || u.Id == searchId)) ||
                    (composite && u.BranchId == cBranch && u.RoleId == cRole && u.Id == cUser) ||
                    (u.Branch != null && u.Branch.BranchName.Contains(s)) ||
                    (u.Role != null && u.Role.RoleName.Contains(s)));
            }

            // Users.CreatedAt is stored in LOCAL server time (legacy UserRepository.Add used DateTime.Now).
            var (start, end) = TimeConv.LocalRange(from, to);
            if (start.HasValue) q = q.Where(u => u.CreatedAt >= start.Value);
            if (end.HasValue) q = q.Where(u => u.CreatedAt <= end.Value);
            return q;
        }

        private static IQueryable<UserRow> Project(IQueryable<UserEntity> q) =>
            q.Select(u => new UserRow
            {
                Id = u.Id,
                FirstName = u.FirstName,
                MiddleName = u.MiddleName,
                LastName = u.LastName,
                Address = u.Address ?? "",
                ContactNo = u.ContactNo ?? "",
                Email = u.Email,
                Username = u.Username,
                BranchId = u.BranchId,
                BranchName = u.Branch != null ? u.Branch.BranchName : "",
                RoleId = u.RoleId,
                RoleName = u.Role != null ? u.Role.RoleName : "",
                HasPhoto = u.UserImage != null && u.UserImage.Length > 0,
                CreatedAt = u.CreatedAt,
                UpdatedAt = u.UpdatedAt
            });

        private static UserRow Finish(UserRow r)
        {
            r.UserCode = UserCodes.Format(r.BranchId, r.RoleId, r.Id);
            r.FullName = string.Join(" ", new[] { r.FirstName, r.MiddleName, r.LastName }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return r;
        }

        [HttpGet]
        [RequirePermission(PermissionCodes.UserView)]
        public async Task<IActionResult> List(
            [FromQuery] string? search, [FromQuery] DateTime? from, [FromQuery] DateTime? to,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
        {
            (page, pageSize) = ClampPaging(page, pageSize);
            var q = BuildQuery(search, from, to);
            var total = await q.CountAsync(ct);
            var rows = await Project(q.OrderByDescending(u => u.Id).Skip((page - 1) * pageSize).Take(pageSize))
                .ToListAsync(ct);
            rows.ForEach(r => Finish(r));
            return Ok(Paged(rows, total, page, pageSize));
        }

        [HttpGet("{id:int}")]
        [RequirePermission(PermissionCodes.UserView, PermissionCodes.UserEdit)]
        public async Task<IActionResult> Get(int id, CancellationToken ct)
        {
            var row = await Project(_db.Users.AsNoTracking().Where(u => u.Id == id && u.IsActive))
                .FirstOrDefaultAsync(ct);
            return row == null ? NotFoundError("User") : Ok(Finish(row));
        }

        [HttpGet("{id:int}/photo")]
        [RequirePermission(PermissionCodes.UserView, PermissionCodes.UserEdit)]
        public async Task<IActionResult> Photo(int id, CancellationToken ct)
        {
            var bytes = await _db.Users.AsNoTracking()
                .Where(u => u.Id == id && u.IsActive)
                .Select(u => u.UserImage)
                .FirstOrDefaultAsync(ct);
            return bytes == null || bytes.Length == 0 ? NoContent() : File(bytes, ImageSniffer.ContentType(bytes));
        }

        [HttpGet("check-username")]
        [RequirePermission(PermissionCodes.UserView, PermissionCodes.UserAdd, PermissionCodes.UserEdit)]
        public async Task<IActionResult> CheckUsername(
            [FromQuery] string username, [FromQuery] int? exceptId, CancellationToken ct)
        {
            var u = (username ?? "").Trim();
            // The unique index IX_Users_Username covers soft-deleted rows too, so check ALL users.
            var hit = await _db.Users.AsNoTracking()
                .Where(x => x.Username == u && (exceptId == null || x.Id != exceptId))
                .Select(x => new { x.IsActive })
                .FirstOrDefaultAsync(ct);
            return Ok(new AvailabilityResponse { Taken = hit != null, Deactivated = hit != null && !hit.IsActive });
        }

        // ── Create ───────────────────────────────────────────────────────────
        [HttpPost]
        [RequirePermission(PermissionCodes.UserAdd)]
        public async Task<IActionResult> Create([FromBody] UserUpsertRequest req, CancellationToken ct)
        {
            var fail = await ValidateAsync(req, isCreate: true, existing: null, ct);
            if (fail != null) return fail;

            TryDecodePhoto(req.PhotoBase64, out var photo, out _);   // validated in ValidateAsync
            var now = DateTime.Now;
            var user = new UserEntity
            {
                FirstName = req.FirstName.Trim(),
                MiddleName = (req.MiddleName ?? "").Trim(),
                LastName = req.LastName.Trim(),
                Address = req.Address.Trim(),
                ContactNo = req.ContactNo.Trim(),
                Email = req.Email.Trim(),
                Username = req.Username.Trim(),
                PasswordHash = _pw.Hash(req.Password!),
                BranchId = req.BranchId,
                RoleId = req.RoleId,
                // Users.UserImage is "longblob NOT NULL": "no photo" is stored as an empty array, never NULL.
                UserImage = photo ?? Array.Empty<byte>(),
                CreatedAt = now,
                UpdatedAt = now,
                IsActive = true
            };

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);                       // need the identity value for the audit row
            _audit.Stage(AuditActions.Create, AuditEntities.User, user.Id,
                $"Username='{user.Username}', Name='{NameOf(user)}', BranchId={user.BranchId}, RoleId={user.RoleId}.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("User {UserId} created by {Actor}", user.Id, _audit.Actor.Username);
            return StatusCode(StatusCodes.Status201Created, await LoadRowAsync(user.Id, ct));
        }

        // ── Update ───────────────────────────────────────────────────────────
        [HttpPut("{id:int}")]
        [RequirePermission(PermissionCodes.UserEdit)]
        public async Task<IActionResult> Update(int id, [FromBody] UserUpsertRequest req, CancellationToken ct)
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
            if (user == null) return NotFoundError("User");
            if (IsStale(req.ExpectedUpdatedAt, user.UpdatedAt)) return ConcurrencyError("user");

            var fail = await ValidateAsync(req, isCreate: false, existing: user, ct);
            if (fail != null) return fail;

            TryDecodePhoto(req.PhotoBase64, out var photo, out _);
            bool passwordReset = !string.IsNullOrEmpty(req.Password);
            bool roleChanged = user.RoleId != req.RoleId;

            // Self-lockout guard: an administrator must not strip their OWN access by moving themselves to a role
            // that cannot manage users (they could never undo it).
            if (CurrentUserId == id && roleChanged)
            {
                var newRoleHasUserEdit = await _db.RolePermissions.AsNoTracking()
                    .AnyAsync(rp => rp.RoleId == req.RoleId && rp.Permission!.Code == PermissionCodes.UserEdit, ct);
                if (!newRoleHasUserEdit)
                    return Fail(StatusCodes.Status409Conflict,
                        "You cannot move your own account to a role that cannot edit users — you would lock yourself out.",
                        "SELF_LOCKOUT");
            }

            user.FirstName = req.FirstName.Trim();
            user.MiddleName = (req.MiddleName ?? "").Trim();
            user.LastName = req.LastName.Trim();
            user.Address = req.Address.Trim();
            user.ContactNo = req.ContactNo.Trim();
            user.Email = req.Email.Trim();
            user.Username = req.Username.Trim();
            user.BranchId = req.BranchId;
            user.RoleId = req.RoleId;
            if (passwordReset) user.PasswordHash = _pw.Hash(req.Password!);
            // "longblob NOT NULL" column: removing the photo stores an empty array, never NULL.
            if (req.RemovePhoto) user.UserImage = Array.Empty<byte>();
            else if (photo != null) user.UserImage = photo;
            user.UpdatedAt = DateTime.Now;

            _audit.Stage(AuditActions.Update, AuditEntities.User, user.Id,
                $"Username='{user.Username}', Name='{NameOf(user)}', BranchId={user.BranchId}, RoleId={user.RoleId}." +
                (passwordReset ? " PASSWORD RESET" : "") + (roleChanged ? " ROLE CHANGED" : ""));
            await _db.SaveChangesAsync(ct);

            _perms.Invalidate();   // role/branch change must take effect immediately
            return Ok(await LoadRowAsync(user.Id, ct));
        }

        // ── Delete (soft) ────────────────────────────────────────────────────
        [HttpDelete("{id:int}")]
        [RequirePermission(PermissionCodes.UserDelete)]
        public async Task<IActionResult> Delete(int id, CancellationToken ct)
        {
            if (CurrentUserId == id)
                return Fail(StatusCodes.Status409Conflict,
                    "You cannot delete the account you are currently signed in with.", "SELF_DELETE");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id && u.IsActive, ct);
            if (user == null) return NotFoundError("User");

            var now = DateTime.Now;
            user.IsActive = false;
            user.DeletedAt = now;
            user.UpdatedAt = now;
            _audit.Stage(AuditActions.SoftDelete, AuditEntities.User, user.Id,
                $"Deactivated account '{user.Username}' ({NameOf(user)}).");
            await _db.SaveChangesAsync(ct);

            _perms.Invalidate();   // a deactivated user loses access immediately, even with a still-valid token
            return Ok(new MessageResponse { Message = $"User '{user.Username}' deleted." });
        }

        // ── Import ───────────────────────────────────────────────────────────
        /// <param name="dryRun">When true, validates and reports per-row outcomes without writing anything.</param>
        [HttpPost("import")]
        [RequirePermission(PermissionCodes.UserAdd)]
        public async Task<IActionResult> Import([FromBody] UserImportRequest req, [FromQuery] bool dryRun = false, CancellationToken ct = default)
        {
            if (req.Rows.Count > MaxImportRows)
                return Invalid("rows", $"Import at most {MaxImportRows} users per request.");

            var branches = await _db.Branches.AsNoTracking().Where(b => b.IsActive)
                .Select(b => new { b.BranchId, b.BranchName }).ToListAsync(ct);
            var roles = await _db.Roles.AsNoTracking().Where(r => r.IsActive)
                .Select(r => new { r.RoleId, r.RoleName }).ToListAsync(ct);

            var existingUsernames = (await _db.Users.AsNoTracking().Select(u => u.Username).ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var response = new ImportResponse();
            var toAdd = new List<UserEntity>();
            var now = DateTime.Now;

            foreach (var row in req.Rows)
            {
                var res = new ImportRowResult
                {
                    RowNumber = row.RowNumber,
                    Key = (row.Username ?? "").Trim(),
                    Name = (row.FullName ?? "").Trim()
                };
                response.Rows.Add(res);

                string? problem = ValidateImportRow(row, branches.Select(b => (b.BranchId, b.BranchName)),
                    roles.Select(r => (r.RoleId, r.RoleName)), out var branchId, out var roleId, out var first, out var middle, out var last);
                if (problem != null)
                {
                    res.Status = "Invalid"; res.Message = problem; response.Invalid++;
                    continue;
                }

                var username = res.Key;
                if (existingUsernames.Contains(username) || !seen.Add(username))
                {
                    res.Status = "Duplicate";
                    res.Message = existingUsernames.Contains(username)
                        ? $"Username '{username}' is already in use (possibly by a deactivated account)."
                        : $"Username '{username}' appears more than once in the file.";
                    response.Duplicates++;
                    continue;
                }

                res.Status = "Created";
                response.Imported++;
                if (dryRun) continue;      // no hashing, no entity: nothing is written

                toAdd.Add(new UserEntity
                {
                    FirstName = first,
                    MiddleName = middle,
                    LastName = last,
                    Address = (row.Address ?? "").Trim(),
                    ContactNo = (row.ContactNo ?? "").Trim(),
                    Email = (row.Email ?? "").Trim(),
                    Username = username,
                    // Same as the legacy importer: a random temporary password nobody knows, so an administrator
                    // must reset it before the account's first login.
                    PasswordHash = _pw.Hash(_pw.GenerateTemporaryPassword()),
                    UserImage = Array.Empty<byte>(),      // "longblob NOT NULL": imported users have no photo
                    BranchId = branchId,
                    RoleId = roleId,
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsActive = true
                });
            }

            if (!dryRun && toAdd.Count > 0)
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                _db.Users.AddRange(toAdd);
                _audit.Stage(AuditActions.Import, AuditEntities.User, null,
                    $"Imported {toAdd.Count} user(s) from '{req.SourceFileName ?? "file"}'. " +
                    $"Skipped: {response.Duplicates} duplicate, {response.Invalid} invalid.");
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                response.Committed = true;
            }
            return Ok(response);
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        private async Task<UserRow?> LoadRowAsync(int id, CancellationToken ct)
        {
            var row = await Project(_db.Users.AsNoTracking().Where(u => u.Id == id)).FirstOrDefaultAsync(ct);
            return row == null ? null : Finish(row);
        }

        private async Task<IActionResult?> ValidateAsync(UserUpsertRequest r, bool isCreate, UserEntity? existing, CancellationToken ct)
        {
            var errors = new Dictionary<string, string>();

            var username = (r.Username ?? "").Trim();
            if (!UsernameRx.IsMatch(username))
                errors["username"] = "Username must be 3–16 characters: letters, digits or underscore.";

            if (!EmailRx.IsMatch((r.Email ?? "").Trim()))
                errors["email"] = "Enter a valid email address.";

            if (string.IsNullOrWhiteSpace(r.Address))
                errors["address"] = "Address is required.";

            if (!ContactRx.IsMatch((r.ContactNo ?? "").Trim()))
                errors["contactNo"] = "Contact number must be exactly 11 digits.";

            if (isCreate && string.IsNullOrEmpty(r.Password))
                errors["password"] = "Password is required for a new user.";
            else if (!string.IsNullOrEmpty(r.Password) && !PasswordPolicy.IsAcceptable(r.Password))
                errors["password"] = PasswordPolicy.Requirement;

            if (!TryDecodePhoto(r.PhotoBase64, out _, out var photoError))
                errors["photo"] = photoError!;

            if (errors.Count > 0) return Invalid(errors);

            // Referential checks
            if (!await _db.Branches.AnyAsync(b => b.BranchId == r.BranchId && b.IsActive, ct))
                return Invalid("branchId", "The selected branch does not exist or was deleted.");

            // A role that is flagged inactive is still acceptable when it is the user's CURRENT role
            // (the seeded SuperAdmin row is created without IsActive).
            int? currentRoleId = existing?.RoleId;
            if (!await _db.Roles.AnyAsync(x => x.RoleId == r.RoleId && (x.IsActive || x.RoleId == currentRoleId), ct))
                return Invalid("roleId", "The selected role does not exist or was deleted.");

            // Username uniqueness over ALL users (the DB unique index includes deactivated accounts).
            int? exceptId = existing?.Id;
            var clash = await _db.Users.AsNoTracking()
                .Where(u => u.Username == username && (exceptId == null || u.Id != exceptId))
                .Select(u => new { u.IsActive })
                .FirstOrDefaultAsync(ct);
            if (clash != null)
                return Duplicate(clash.IsActive
                    ? "Username is already taken."
                    : "Username belongs to a deactivated account and cannot be reused.", "username");

            return null;
        }

        private static string? ValidateImportRow(
            UserImportRow row,
            IEnumerable<(int Id, string Name)> branches, IEnumerable<(int Id, string Name)> roles,
            out int branchId, out int roleId, out string first, out string middle, out string last)
        {
            branchId = roleId = 0;
            first = middle = last = "";

            var username = (row.Username ?? "").Trim();
            if (!UsernameRx.IsMatch(username))
                return "Username must be 3–16 characters: letters, digits or underscore.";

            var tokens = (row.FullName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 2)
                return "Full name must include a first and a last name.";
            first = tokens[0];
            last = tokens[^1];
            middle = tokens.Length > 2 ? string.Join(" ", tokens[1..^1]) : "";
            if (first.Length > FieldLimits.Name || middle.Length > FieldLimits.Name || last.Length > FieldLimits.Name)
                return $"Name parts may not exceed {FieldLimits.Name} characters.";

            var email = (row.Email ?? "").Trim();
            if (email.Length > 0 && (!EmailRx.IsMatch(email) || email.Length > FieldLimits.Name))
                return "Invalid email address.";

            var contact = (row.ContactNo ?? "").Trim();
            if (contact.Length > 0 && !ContactRx.IsMatch(contact))
                return "Contact number must be exactly 11 digits.";

            if ((row.Address ?? "").Trim().Length > FieldLimits.UserAddress)
                return $"Address may not exceed {FieldLimits.UserAddress} characters.";

            // The legacy importer silently fell back to Branch 1 / Role 2 for unknown names — i.e. it could grant
            // an unintended role. Unknown or blank names are now REJECTED per row instead.
            var bName = (row.BranchName ?? "").Trim();
            var b = branches.Where(x => string.Equals(x.Name, bName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (bName.Length == 0 || b.Count == 0) return $"Branch '{bName}' was not found.";
            branchId = b[0].Id;

            var rName = (row.RoleName ?? "").Trim();
            var rr = roles.Where(x => string.Equals(x.Name, rName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rName.Length == 0 || rr.Count == 0) return $"Role '{rName}' was not found.";
            roleId = rr[0].Id;

            return null;
        }
    }
}
