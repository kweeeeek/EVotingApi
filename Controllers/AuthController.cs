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
        private readonly ISessionRegistry _sessions;

        public AuthController(
            ApiDbContext db, IPasswordService passwords,
            ITokenService tokens, IConfiguration config,
            ILogger<AuthController> logger, IAuditService audit, ISessionRegistry sessions)
        {
            _sessions = sessions;
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
        private async Task TryAuditAsync(
            string action, int? userId, string? username, string? details, CancellationToken ct,
            string entityType = AuditEntities.User)
        {
            try
            {
                await _audit.WriteAsync(action, entityType, userId, details, userId, username, ct);
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

            // BUG FIX: int.Parse throws on non-numeric config value; use TryParse with fallback.
            if (!int.TryParse(_config["Jwt:AdminTokenExpiryMinutes"], out int expiry))
                expiry = 480;

            // Single session: this sign-in becomes the account's only valid Admin session (an earlier one on another PC ends).
            var (sessionId, replaced) = await _sessions.StartAsync(user!.Id, SessionRegistry.AppAdmin, TimeSpan.FromMinutes(expiry), ct);
            var token = _tokens.GenerateAdminToken(user!, sessionId);

            _logger.LogInformation("Admin login: UserId={Id} Username={Username} ReplacedSession={Replaced}", user!.Id, user.Username, replaced);
            await TryAuditAsync(AuditActions.Login, user.Id, user.Username,
                replaced ? "Signed in to the Admin application (the session on another computer was ended)." : "Signed in to the Admin application.", ct);

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
            await _sessions.EndAsync(User, ct); // the token itself stops working now
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
        [EnableRateLimiting("registration-login")]
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

            // A password the administrator gave (new account, reset): the person must choose their own before the desk opens.
            // The answer carries only a short PasswordChange token; no Registration session is started yet.
            if (user!.MustChangePassword)
            {
                _logger.LogInformation("Registration login needs a new password: UserId={Id} Username={Username}", user.Id, user.Username);
                return Ok(new TokenResponse
                {
                    AccessToken = _tokens.GeneratePasswordChangeToken(user),
                    ExpiresIn = TokenService.PasswordChangeTokenMinutes * 60,
                    Role = "PasswordChange",
                    MustChangePassword = true
                });
            }

            return Ok(await StartRegistrationSessionAsync(user, ct));
        }

        /// <summary>Starts the account's only Registration session (single session) and returns its token.</summary>
        private async Task<TokenResponse> StartRegistrationSessionAsync(Infrastructure.Entities.UserEntity user, CancellationToken ct)
        {
            if (!int.TryParse(_config["Jwt:AdminTokenExpiryMinutes"], out int expiry))
                expiry = 480;

            // Single session: this sign-in becomes the account's only valid Registration session.
            var (sessionId, replaced) = await _sessions.StartAsync(user.Id, SessionRegistry.AppRegistration, TimeSpan.FromMinutes(expiry), ct);
            var token = _tokens.GenerateRegistrationToken(user, sessionId);

            _logger.LogInformation("Registration-station login: UserId={Id} Username={Username} ReplacedSession={Replaced}",
                user.Id, user.Username, replaced);

            return new TokenResponse
            {
                AccessToken = token,
                ExpiresIn = expiry * 60,
                Role = "Registration"
            };
        }

        // ── POST /api/v1/auth/registration-change-password ─────────────────────
        /// <summary>
        /// The person chooses their own password after the Registration sign-in answered MustChangePassword. Called with the short
        /// PasswordChange token; the current (administrator's) password is asked again. The new one must meet the password policy
        /// and differ from the current one. On success the flag is cleared and the normal Registration session starts (same answer
        /// as registration-login), so the desk opens at once.
        /// </summary>
        [HttpPost("registration-change-password")]
        [Authorize(Roles = "PasswordChange")]
        [EnableRateLimiting("registration-login")]
        public async Task<IActionResult> RegistrationChangePassword(
            [FromBody] ChangePasswordRequest req, [FromServices] IAuditService audit, CancellationToken ct)
        {
            if (!int.TryParse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var userId))
                return Unauthorized(new ApiError { Error = "Sign in again.", Code = "SIGN_IN_AGAIN" });

            var user = await _db.Users
                .Include(u => u.Role)
                    .ThenInclude(r => r!.RolePermissions)
                        .ThenInclude(rp => rp.Permission)
                .Include(u => u.Branch)
                .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, ct);
            if (user == null)
                return Unauthorized(new ApiError { Error = "Sign in again.", Code = "SIGN_IN_AGAIN" });

            if (!_passwords.Verify(req.CurrentPassword, user.PasswordHash))
                return BadRequest(new ApiError { Error = "The current password is wrong. Sign in again.", Code = "WRONG_PASSWORD" });
            if (!PasswordPolicy.IsAcceptable(req.NewPassword))
                return BadRequest(new ApiError { Error = PasswordPolicy.Requirement, Code = "WEAK_PASSWORD" });
            if (req.NewPassword == req.CurrentPassword)
                return BadRequest(new ApiError { Error = "Choose a password different from the one you were given.", Code = "SAME_PASSWORD" });
            if (req.NewPassword.Contains(user.Username, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new ApiError { Error = "The password must not contain your user name.", Code = "WEAK_PASSWORD" });

            user.PasswordHash = _passwords.Hash(req.NewPassword);
            user.MustChangePassword = false;
            user.UpdatedAt = DateTime.Now;
            audit.Stage(AuditActions.PasswordChanged, AuditEntities.User, user.Id, $"Username='{user.Username}' chose a new password at the Registration sign-in.");
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("Registration user changed the password: UserId={Id} Username={Username}", user.Id, user.Username);
            return Ok(await StartRegistrationSessionAsync(user, ct));
        }

        // ── POST /api/v1/auth/registration-station-login ──────────────────────
        /// <summary>
        /// ADDITIVE. Non-interactive sign-in of a Registration Station: the desktop app signs itself in at start-up with a
        /// credential that was provisioned on that PC (it is not part of the application source and nobody types it).
        ///
        /// What this does and does not prove:
        ///  * The station id (normally the PC's machine name) is only a LABEL. Knowing it proves nothing.
        ///  * The StationSecret is what authenticates, checked in constant time against the enrolment held by this server
        ///    (per-station SHA-256 digests, or an optional shared enrolment secret). Nothing is ever compared in plain text.
        ///  * The token carries the "Registration" role only - no user id, no permission claims - so it reaches exactly the
        ///    endpoints the interactive Registration login reaches and no administrative one.
        ///
        /// Same safeguards as kiosk-login/admin-login: its own rate-limit policy, 503 when the server has no enrolment
        /// configured (never "open"), one uniform 401 for every kind of wrong credential, audit rows for success and
        /// failure, and the secret is never logged.
        /// </summary>
        [HttpPost("registration-station-login")]
        [AllowAnonymous]
        [EnableRateLimiting("station-login")]
        public async Task<IActionResult> RegistrationStationLogin(
            [FromBody] RegistrationStationLoginRequest req,
            [FromServices] StationCredentialPolicy policy,
            CancellationToken ct)
        {
            var outcome = policy.Validate(req.StationId, req.StationSecret, out var stationId);

            if (outcome == StationAuthOutcome.NotConfigured)
            {
                _logger.LogError(
                    "Registration-station sign-in refused: no station credential is configured " +
                    "(set EVOTING_REGISTRATION_STATIONS and/or EVOTING_REGISTRATION_STATION_SECRET).");
                return StatusCode(503, new ApiError
                {
                    Error = "Registration Station sign-in is not available: this server has no station credential configured.",
                    Code = "STATION_AUTH_NOT_CONFIGURED"
                });
            }

            if (outcome != StationAuthOutcome.Authenticated)
            {
                // The id came from the caller: clip it and keep only harmless characters before it reaches a log line.
                var label = SafeStationLabel(req.StationId);
                _logger.LogWarning("Failed registration-station sign-in: StationId={StationId} IP={IP}",
                    label, HttpContext.Connection.RemoteIpAddress);
                await TryAuditAsync(AuditActions.LoginFailed, null, $"station:{label}",
                    "Invalid station credentials.", ct, AuditEntities.Station);
                return Unauthorized(new ApiError
                {
                    Error = "Invalid station credentials.",
                    Code = "INVALID_STATION_CREDENTIALS"
                });
            }

            var token = _tokens.GenerateRegistrationStationToken(stationId);
            var expiry = int.TryParse(_config["Jwt:StationTokenExpiryMinutes"], out int m) && m > 0 ? m : 480;

            _logger.LogInformation("Registration-station sign-in: StationId={StationId}", stationId);
            await TryAuditAsync(AuditActions.Login, null, $"station:{stationId}",
                "Registration Station signed in.", ct, AuditEntities.Station);

            return Ok(new TokenResponse
            {
                AccessToken = token,
                ExpiresIn = expiry * 60,
                Role = "Registration"
            });
        }

        /// <summary>Caller-supplied station id made safe for log lines and the 50-character audit username column.</summary>
        private static string SafeStationLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "(none)";
            var s = raw.Trim();
            if (s.Length > 32) s = s.Substring(0, 32);
            var chars = s.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-' ? ch : '?');
            return new string(chars.ToArray());
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

            // ── Who is this? ──────────────────────────────────────────────────
            // The account code is the member's IDENTITY. A voting pass (EVP1-..., the random token in the Registration QR) is
            // PROOF that the slip came from the Registration desk. Old clients send whatever the QR holds as "accountCode",
            // so a pass is recognised in either field.
            var passText = VotingPass.Normalize(req.VotingPass) ?? VotingPass.Normalize(req.AccountCode);
            bool viaPass = passText != null;

            // At the General Assembly VENUE every voter has been to the Registration desk, so a venue kiosk can insist on the
            // pass and refuse a typed account code (which can be guessed). The mobile pages are NOT venue kiosks: members who
            // cannot attend still vote there with their account code, so their gateways (kioskId starting with the configured
            // prefix, default "MOBILE-") are exempt. Note: kioskId is chosen by the caller of kiosk-login, so this is an
            // operating rule for honest kiosks, not a boundary against someone who holds the kiosk secret.
            var kioskId = User.FindFirst("kioskId")?.Value ?? "";
            var mobilePrefix = _config["Security:VotingPass:MobileGatewayKioskPrefix"];
            if (string.IsNullOrWhiteSpace(mobilePrefix)) mobilePrefix = "MOBILE-";
            bool isMobileGateway = kioskId.StartsWith(mobilePrefix, StringComparison.OrdinalIgnoreCase);
            bool passRequired = !isMobileGateway &&
                                ev.EventType == Infrastructure.Entities.EventTypes.GeneralAssembly &&
                                _config.GetValue("Security:VotingPass:RequiredAtGeneralAssemblyKiosks", false);

            Infrastructure.Entities.MemberEntity? member;
            if (viaPass)
            {
                var hash = VotingPass.Hash(passText!);
                var vp = await _db.VotingPasses.AsNoTracking().FirstOrDefaultAsync(p => p.PassHash == hash, ct);
                if (vp == null || vp.RevokedAt != null || vp.EventId != ev.EventId)
                {
                    _logger.LogWarning("Voter verify: voting pass not valid for EventId={EventId} (unknown, revoked or other election) KioskId={KioskId}",
                        ev.EventId, kioskId);
                    return NotFound(new ApiError
                    {
                        Error = "This QR code is not valid for the election that is open now. Please go to the Registration desk.",
                        Code = "VOTING_PASS_INVALID"
                    });
                }

                member = await _db.Members.AsNoTracking().Include(m => m.Branch)
                    .FirstOrDefaultAsync(m => m.MemberId == vp.MemberId && m.IsActive, ct);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(req.AccountCode))
                    return BadRequest(new ApiError { Error = "The account code is required.", Code = "VALIDATION" });

                if (passRequired)
                    return StatusCode(403, new ApiError
                    {
                        Error = "At the General Assembly venue, scan the QR code from the Registration desk.",
                        Detail = "A typed account code is not accepted at the venue kiosks. Members who cannot attend vote on the mobile page.",
                        Code = "VOTING_PASS_REQUIRED"
                    });

                member = await _db.Members
                    .AsNoTracking()
                    .Include(m => m.Branch)
                    .FirstOrDefaultAsync(m =>
                        m.AccountCode == req.AccountCode &&
                        m.IsActive, ct);
            }

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

            _logger.LogInformation("Voter verified: MemberId={MemberId} EventId={EventId} Via={Via} KioskId={KioskId}",
                member.MemberId, ev.EventId, viaPass ? "pass" : "account-code", kioskId);

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
                    Status = ev.Status,
                    EventType = ev.EventType
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
