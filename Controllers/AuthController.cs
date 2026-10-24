using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Admin;
using EVoting.Api.Models.Requests;
using EVoting.Api.Models.Responses;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Controllers
{
    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly IPasswordService _passwords;
        private readonly ITokenService _tokens;
        private readonly IConfiguration _config;
        private readonly ILogger<AuthController> _logger;
        private readonly IAuditService _audit;

        public AuthController(
            ApiDbContext db, IPasswordService passwords,
            ITokenService tokens, IConfiguration config,
            ILogger<AuthController> logger, IAuditService audit)
        {
            _db = db;
            _passwords = passwords;
            _tokens = tokens;
            _config = config;
            _logger = logger;
            _audit = audit;
        }

        /// <summary>
        /// Login/logout audit rows are best-effort: an audit-table problem must never prevent an administrator
        /// from signing in (all DATA changes, by contrast, are audited atomically and fail closed).
        /// </summary>
        private async Task TryAuditAsync(string action, int? userId, string? username, string? details, CancellationToken ct)
        {
            try
            {
                await _audit.WriteAsync(action, AuditEntities.User, userId, details, userId, username, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write {Action} audit row for {Username}", action, username);
            }
        }

        // ── POST /api/v1/auth/admin-login ─────────────────────────────────────
        /// <summary>Authenticate an admin user. Returns a JWT valid for 8 hours.</summary>
        [HttpPost("admin-login")]
        [AllowAnonymous]
        [EnableRateLimiting("admin-login")]
        public async Task<IActionResult> AdminLogin(
            [FromBody] AdminLoginRequest req, CancellationToken ct)
        {
            var user = await _db.Users
                .Include(u => u.Role)
                    .ThenInclude(r => r!.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
                .Include(u => u.Branch)
                .FirstOrDefaultAsync(u => u.Username == req.Username && u.IsActive, ct);

            // Unified error message — prevents username enumeration
            bool valid = user != null && _passwords.Verify(req.Password, user.PasswordHash);
            if (!valid)
            {
                _logger.LogWarning("Failed admin login attempt for username={Username} IP={IP}",
                    req.Username, HttpContext.Connection.RemoteIpAddress);
                await TryAuditAsync(AuditActions.LoginFailed, null, req.Username, "Invalid username or password.", ct);
                return Unauthorized(new ApiError { Error = "Invalid username or password.", Code = "INVALID_CREDENTIALS" });
            }

            var token = _tokens.GenerateAdminToken(user!);
            // BUG FIX: int.Parse throws on non-numeric config value; use TryParse with fallback.
            if (!int.TryParse(_config["Jwt:AdminTokenExpiryMinutes"], out int expiry))
                expiry = 480;

            _logger.LogInformation("Admin login: UserId={Id} Username={Username}", user!.Id, user.Username);
            await TryAuditAsync(AuditActions.Login, user.Id, user.Username, "Signed in to the Admin application.", ct);

            return Ok(new TokenResponse
            {
                AccessToken = token,
                ExpiresIn = expiry * 60,
                Role = "Admin"
            });
        }

        // ── GET /api/v1/auth/me ───────────────────────────────────────────────
        /// <summary>
        /// The signed-in administrator's profile and CURRENT permissions (read from the database, not the token).
        /// Replaces the legacy Admin's "query MySQL for the user right after login" step; call it again after
        /// permissions change to refresh the client's session.
        /// </summary>
        [HttpGet("me")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Me([FromServices] IPermissionService permissions, CancellationToken ct)
        {
            var id = RequirePermissionAttribute.UserIdOf(User);
            if (id == null) return Unauthorized();

            var u = await _db.Users.AsNoTracking()
                .Where(x => x.Id == id && x.IsActive)
                .Select(x => new
                {
                    x.Id, x.Username, x.FirstName, x.MiddleName, x.LastName, x.Email,
                    x.BranchId, BranchName = x.Branch != null ? x.Branch.BranchName : null,
                    x.RoleId, RoleName = x.Role != null ? x.Role.RoleName : null,
                    HasPhoto = x.UserImage != null && x.UserImage.Length > 0
                })
                .FirstOrDefaultAsync(ct);

            // A valid token for an account that has since been deactivated must stop working immediately.
            if (u == null)
                return Unauthorized(new ApiError { Error = "This account is no longer active.", Code = "ACCOUNT_INACTIVE" });

            var perms = await permissions.GetPermissionsAsync(u.Id, ct);
            return Ok(new MeResponse
            {
                UserId = u.Id,
                Username = u.Username,
                FirstName = u.FirstName,
                FullName = string.Join(" ", new[] { u.FirstName, u.MiddleName, u.LastName }.Where(p => !string.IsNullOrWhiteSpace(p))),
                Email = u.Email,
                BranchId = u.BranchId,
                BranchName = u.BranchName,
                RoleId = u.RoleId,
                RoleName = u.RoleName,
                HasPhoto = u.HasPhoto,
                Permissions = perms.OrderBy(p => p, StringComparer.Ordinal).ToList()
            });
        }

        [HttpGet("me/photo")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> MyPhoto(CancellationToken ct)
        {
            var id = RequirePermissionAttribute.UserIdOf(User);
            if (id == null) return Unauthorized();
            var bytes = await _db.Users.AsNoTracking().Where(x => x.Id == id && x.IsActive)
                .Select(x => x.UserImage).FirstOrDefaultAsync(ct);
            return bytes == null || bytes.Length == 0 ? NoContent() : File(bytes, ImageSniffer.ContentType(bytes));
        }

        // ── POST /api/v1/auth/logout ──────────────────────────────────────────
        /// <summary>
        /// Records the sign-out in the audit trail. Tokens are stateless JWTs, so this cannot revoke the token itself
        /// (the client discards it); a deactivated account is cut off immediately by the DB-backed permission check.
        /// </summary>
        [HttpPost("logout")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Logout(CancellationToken ct)
        {
            var a = _audit.Actor;
            await TryAuditAsync(AuditActions.Logout, a.UserId, a.Username, "Signed out of the Admin application.", ct);
            return Ok(new MessageResponse { Message = "Signed out." });
        }

        // ── POST /api/v1/auth/registration-login ──────────────────────────────
        /// <summary>
        /// Authenticate a Registration Station user. Returns a JWT valid for
        /// the same duration as an admin token (Jwt:AdminTokenExpiryMinutes).
        ///
        /// Mirrors AdminLogin exactly: same Users-table lookup, same
        /// IPasswordService.Verify call (Argon2 + EVOTING_PEPPER), same
        /// unified "Invalid username or password" message to prevent
        /// username enumeration. The only difference is the Role claim
        /// stamped on the resulting token ("Registration" vs "Admin"),
        /// produced by ITokenService.GenerateRegistrationToken.
        ///
        /// Rate-limited under the same "admin-login" policy as AdminLogin —
        /// both are interactive human logins from a station UI, not
        /// machine-to-machine calls, so they share a throttling profile.
        /// </summary>
        [HttpPost("registration-login")]
        [AllowAnonymous]
        [EnableRateLimiting("admin-login")]
        public async Task<IActionResult> RegistrationLogin(
            [FromBody] AdminLoginRequest req, CancellationToken ct)
        {
            var user = await _db.Users
                .Include(u => u.Role)
                    .ThenInclude(r => r!.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
                .Include(u => u.Branch)
                .FirstOrDefaultAsync(u => u.Username == req.Username && u.IsActive, ct);

            // Unified error message — prevents username enumeration
            bool valid = user != null && _passwords.Verify(req.Password, user.PasswordHash);
            if (!valid)
            {
                _logger.LogWarning("Failed registration-station login attempt for username={Username} IP={IP}",
                    req.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new ApiError { Error = "Invalid username or password." });
            }

            var token = _tokens.GenerateRegistrationToken(user!);
            if (!int.TryParse(_config["Jwt:AdminTokenExpiryMinutes"], out int expiry))
                expiry = 480;

            _logger.LogInformation("Registration-station login: UserId={Id} Username={Username}",
                user!.Id, user.Username);

            return Ok(new TokenResponse
            {
                AccessToken = token,
                ExpiresIn = expiry * 60,
                Role = "Registration"
            });
        }

        // ── POST /api/v1/auth/kiosk-login ─────────────────────────────────────
        /// <summary>
        /// Authenticate a kiosk device. DeviceSecret is validated against
        /// the EVOTING_KIOSK_SECRET environment variable (or appsettings).
        /// Returns a kiosk JWT valid for 8 hours.
        /// </summary>
        [HttpPost("kiosk-login")]
        [AllowAnonymous]
        [EnableRateLimiting("kiosk-login")]
        public IActionResult KioskLogin([FromBody] KioskLoginRequest req)
        {
            // No built-in default: a secret that is written in the source is known to everybody who has the source.
            var expectedSecret = Environment.GetEnvironmentVariable("EVOTING_KIOSK_SECRET");
            if (string.IsNullOrWhiteSpace(expectedSecret)) expectedSecret = _config["Security:KioskSecret"];
            if (string.IsNullOrWhiteSpace(expectedSecret) || SecretSettings.LooksLikePlaceholder(expectedSecret))
            {
                _logger.LogError("Kiosk sign-in refused: no kiosk secret is configured (set EVOTING_KIOSK_SECRET or Security:KioskSecret).");
                return StatusCode(503, new ApiError
                {
                    Error = "Kiosk sign-in is not available: this server has no kiosk secret configured.",
                    Code = "KIOSK_SECRET_NOT_CONFIGURED"
                });
            }

            // Constant-time comparison to prevent timing attacks
            bool valid = CryptographicCompare(req.DeviceSecret, expectedSecret)
                      && !string.IsNullOrWhiteSpace(req.KioskId);

            if (!valid)
            {
                _logger.LogWarning("Failed kiosk auth: KioskId={KioskId} IP={IP}",
                    req.KioskId, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new ApiError { Error = "Invalid kiosk credentials." });
            }

            var token = _tokens.GenerateKioskToken(req.KioskId);
            // BUG FIX: int.Parse throws on non-numeric config value; use TryParse with fallback.
            if (!int.TryParse(_config["Jwt:KioskTokenExpiryMinutes"], out int expiry))
                expiry = 480;

            _logger.LogInformation("Kiosk login: KioskId={KioskId}", req.KioskId);

            return Ok(new TokenResponse
            {
                AccessToken = token,
                ExpiresIn = expiry * 60,
                Role = "Kiosk"
            });
        }

        // ── POST /api/v1/auth/voter-verify ────────────────────────────────────
        /// <summary>
        /// Verify a voter by account code. Requires a valid Kiosk JWT.
        /// Returns a short-lived (15 min) voter JWT plus member and event info.
        /// </summary>
        [HttpPost("voter-verify")]
        [Authorize(Roles = "Kiosk")]
        [EnableRateLimiting("voter-verify")]
        public async Task<IActionResult> VoterVerify(
            [FromBody] VoterVerifyRequest req, CancellationToken ct)
        {
            // Find the open event
            var ev = await _db.Events
                .AsNoTracking()
                .Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.Status == "Opened" && e.IsActive, ct);

            if (ev == null)
                return NotFound(new ApiError { Error = "No election event is currently open." });

            // Find the member
            var member = await _db.Members
                .AsNoTracking()
                .Include(m => m.Branch)
                .FirstOrDefaultAsync(m =>
                    m.AccountCode == req.AccountCode &&
                    m.IsActive, ct);

            if (member == null)
                return NotFound(new ApiError { Error = "Member not found." });

            if (!member.IsEligibleToVote)
                // FIX (MEDIUM): Use 422 Unprocessable for eligibility failure so
                // KioskApiClient can distinguish it from branch-not-assigned (403).
                // Previously both returned 403 which the kiosk showed as
                // "branch not assigned" even when the real issue was eligibility.
                return UnprocessableEntity(new ApiError
                {
                    Error = "Member is not eligible to vote.",
                    Detail = member.VotingRemarks ?? "Please see an administrator."
                });

            // Check branch is in this event
            bool branchAllowed = ev.EventBranches.Any(eb => eb.BranchId == member.BranchId);
            if (!branchAllowed)
                return StatusCode(403, new ApiError
                {
                    Error = "Branch not assigned to this election.",
                    Detail = $"Branch '{member.Branch?.BranchName}' is not participating in this event."
                });

            // Check if already voted
            bool hasVoted = await _db.VoterLogs
                .AnyAsync(v => v.MemberId == member.MemberId && v.EventId == ev.EventId && v.HasVoted, ct);
            if (hasVoted)
                return Conflict(new ApiError { Error = "Member has already voted in this election." });

            var voterToken = _tokens.GenerateVoterToken(member.MemberId, ev.EventId, member.MemberName);
            // BUG FIX: int.Parse throws on non-numeric config value; use TryParse with fallback.
            if (!int.TryParse(_config["Jwt:VoterTokenExpiryMinutes"], out int expiry))
                expiry = 15;

            _logger.LogInformation("Voter verified: MemberId={MemberId} EventId={EventId}",
                member.MemberId, ev.EventId);

            return Ok(new VoterVerifyResponse
            {
                AccessToken = voterToken,
                ExpiresIn = expiry * 60,
                Member = new VoterInfo
                {
                    MemberId = member.MemberId,
                    Name = member.MemberName,
                    Branch = member.Branch?.BranchName ?? "",
                    // FIX (HIGH): Pass BranchId so KioskApiClient can populate
                    // MemberRow.BranchId. Without this, MemberVerifyViewModel's
                    // !_member.BranchId.HasValue check always fires, rejecting
                    // every voter with "branch not assigned".
                    BranchId = member.BranchId,
                    AccountCode = member.AccountCode
                },
                Event = new EventInfo
                {
                    EventId = ev.EventId,
                    Title = ev.Title,
                    Venue = ev.Venue,
                    ScheduleFrom = ev.ScheduleFrom,
                    ScheduleTo = ev.ScheduleTo,
                    Status = ev.Status
                }
            });
        }

        // ── Helper ────────────────────────────────────────────────────────────
        /// <summary>
        /// Constant-time string comparison that does not leak the secret length
        /// via a timing side-channel.
        ///
        /// Both inputs are hashed with SHA-256 before comparison so they are
        /// always the same length (32 bytes). FixedTimeEquals then compares
        /// them in O(1) time regardless of where they first differ.
        /// </summary>
        private static bool CryptographicCompare(string a, string b)
        {
            // Hash both values to a fixed-length digest before comparing.
            // This eliminates the length-leaking short-circuit that a naive
            // byte-array comparison would produce when a.Length != b.Length.
            var aHash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(a));
            var bHash = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(b));
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(aHash, bHash);
        }
    }
}
