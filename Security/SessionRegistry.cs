using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;

namespace EVoting.Api.Security
{
    /// <summary>
    /// One sign-in per account per application ("single session").
    ///
    /// Every interactive sign-in (Admin app, Registration web) gets a fresh random session id. It is written into the token
    /// (claim <see cref="ClaimName"/>) and stored as the account's CURRENT session for that application. A request whose token
    /// carries an older session id - the account signed in again on another computer - is refused with 401 SESSION_REPLACED.
    ///
    ///  * Per application: the same account may be signed in to the Admin app on one PC and to Registration on another.
    ///    Settings Security:SingleSession:Admin / Security:SingleSession:Registration (default true) switch each one off
    ///    without a new build (restart the API).
    ///  * Station tokens (registration-station-login), kiosk and voter tokens are not affected.
    ///  * Storage is the distributed cache (Redis), so a restart of the API keeps the sessions. If Redis cannot be read the
    ///    check FAILS OPEN (the token is accepted, a warning is logged): a cache outage must not lock every administrator
    ///    out during an election; it only falls back to the old behaviour (several sign-ins allowed) until Redis is back.
    ///  * A token with a session id but no stored record (Redis was wiped) is adopted as the current session.
    ///  * A token WITHOUT a session id (issued before this change) is refused while the check is on: everybody signs in
    ///    once again after the deployment.
    /// </summary>
    public interface ISessionRegistry
    {
        /// <summary>Starts a new session for the account and returns its id. <paramref name="replaced"/>: another session was active.</summary>
        Task<(string SessionId, bool Replaced)> StartAsync(int userId, string app, TimeSpan lifetime, CancellationToken ct);

        /// <summary>True when the principal may continue (not an interactive user token, check switched off, or its session is current).</summary>
        Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken ct);

        /// <summary>Sign-out: forgets the session if it is still the current one (the token then stops working).</summary>
        Task EndAsync(ClaimsPrincipal principal, CancellationToken ct);
    }

    public sealed class SessionRegistry : ISessionRegistry
    {
        public const string ClaimName = "sess";
        public const string AppAdmin = "admin";
        public const string AppRegistration = "registration";
        public const string ReplacedCode = "SESSION_REPLACED";
        public const string ReplacedMessage = "This account was signed in on another computer. Sign in again to continue here.";
        /// <summary>HttpContext.Items flag set when a token was refused for this reason (read by the 401 challenge).</summary>
        public const string ReplacedItem = "evoting.sessionReplaced";

        private static readonly TimeSpan LocalTtl = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan OutageBackoff = TimeSpan.FromSeconds(30);
        private const string OutageKey = "sess:outage";
        private const string SignedOut = "signed-out";

        private readonly IDistributedCache _store;
        private readonly IMemoryCache _local;
        private readonly IConfiguration _config;
        private readonly ILogger<SessionRegistry> _logger;

        public SessionRegistry(IDistributedCache store, IMemoryCache local, IConfiguration config, ILogger<SessionRegistry> logger)
        {
            _store = store; _local = local; _config = config; _logger = logger;
        }

        private static string Key(int userId, string app) => $"sess:{app}:{userId}";

        private bool Enforced(string app) =>
            !bool.TryParse(_config[app == AppAdmin ? "Security:SingleSession:Admin" : "Security:SingleSession:Registration"], out var on) || on;

        /// <summary>Which application an interactive user token belongs to, or null for any other token.</summary>
        private static (int UserId, string App)? Identify(ClaimsPrincipal p)
        {
            if (p.FindFirst("authKind")?.Value == "station") return null;
            if (!int.TryParse(p.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)) return null;
            if (p.IsInRole("Admin")) return (id, AppAdmin);
            if (p.IsInRole("Registration")) return (id, AppRegistration);
            return null;
        }

        public async Task<(string SessionId, bool Replaced)> StartAsync(int userId, string app, TimeSpan lifetime, CancellationToken ct)
        {
            var sid = Guid.NewGuid().ToString("N");
            var key = Key(userId, app);
            bool replaced = false;
            try
            {
                var previous = await _store.GetStringAsync(key, ct);
                replaced = !string.IsNullOrEmpty(previous) && previous != SignedOut;
                await _store.SetStringAsync(key, sid,
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime + TimeSpan.FromMinutes(5) }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sign-in still succeeds; requests are accepted while the store cannot be read (fail open, see IsCurrentAsync).
                _logger.LogWarning(ex, "Single-session store unavailable at sign-in (UserId={UserId}, App={App})", userId, app);
            }
            _local.Set(key, sid, LocalTtl);
            return (sid, replaced);
        }

        public async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            if (Identify(principal) is not { } who || !Enforced(who.App)) return true;

            var sid = principal.FindFirst(ClaimName)?.Value;
            if (string.IsNullOrEmpty(sid)) return false; // token from before single-session: sign in again

            var key = Key(who.UserId, who.App);
            if (_local.TryGetValue(key, out string? cached) && cached != null) return cached == sid;
            if (_local.TryGetValue(OutageKey, out _)) return true;

            try
            {
                var current = await _store.GetStringAsync(key, ct);
                if (string.IsNullOrEmpty(current))
                {
                    // No record (store wiped / expired early): this token becomes the current session.
                    var exp = principal.FindFirst("exp")?.Value;
                    var left = long.TryParse(exp, out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.UtcNow
                        : TimeSpan.FromHours(8);
                    if (left < TimeSpan.FromMinutes(1)) left = TimeSpan.FromMinutes(1);
                    await _store.SetStringAsync(key, sid,
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = left + TimeSpan.FromMinutes(5) }, ct);
                    current = sid;
                }
                _local.Set(key, current, LocalTtl);
                return current == sid;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _local.Set(OutageKey, true, OutageBackoff);
                _logger.LogWarning(ex, "Single-session store unavailable; tokens are accepted without the check for {Seconds}s", OutageBackoff.TotalSeconds);
                return true;
            }
        }

        public async Task EndAsync(ClaimsPrincipal principal, CancellationToken ct)
        {
            if (Identify(principal) is not { } who) return;
            var sid = principal.FindFirst(ClaimName)?.Value;
            if (string.IsNullOrEmpty(sid)) return;
            var key = Key(who.UserId, who.App);
            try
            {
                if (await _store.GetStringAsync(key, ct) == sid)
                {
                    // Not removed: an empty record would let this same token be adopted again. The marker matches no token.
                    var exp = principal.FindFirst("exp")?.Value;
                    var left = long.TryParse(exp, out var unix)
                        ? DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.UtcNow
                        : TimeSpan.FromHours(8);
                    if (left < TimeSpan.FromMinutes(1)) left = TimeSpan.FromMinutes(1);
                    await _store.SetStringAsync(key, SignedOut,
                        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = left + TimeSpan.FromMinutes(5) }, ct);
                    _local.Set(key, SignedOut, LocalTtl);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Single-session store unavailable at sign-out (UserId={UserId}, App={App})", who.UserId, who.App);
            }
        }
    }
}
