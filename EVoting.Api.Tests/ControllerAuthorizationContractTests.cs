using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using EVoting.Api.Controllers;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// Reflection-level guard rails: the authorization surface that the Admin, Kiosk and Registration clients depend on,
    /// pinned so that an accidental edit (or a future "casual" change) is caught in CI. These need the real ASP.NET Core and
    /// JWT packages. In the authoring sandbox they were executed against ASP.NET Core 8 and IdentityModel 7.7.3 through a small
    /// xUnit-compatible runner (see docs/07-BUILD-AND-TEST-RESULTS.md in the Registration/Kiosk package); the first run with the
    /// real xUnit on .NET 9 (dotnet test) is still to be done.
    /// </summary>
    public class ControllerAuthorizationContractTests
    {
        private static string[] RolesOf(MemberInfo m) =>
            m.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
             .SelectMany(a => (a.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
             .ToArray();

        private static bool IsRole(Claim c, string role) =>
            (c.Type == ClaimTypes.Role || c.Type == "role") && c.Value == role;

        private static MethodInfo Action<T>(string name) =>
            typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"{typeof(T).Name}.{name} was renamed or removed");

        [Fact]
        public void Current_event_is_readable_by_Registration_in_addition_to_Kiosk_Voter_and_Admin()
        {
            var roles = RolesOf(Action<EventsController>(nameof(EventsController.GetCurrent)));

            foreach (var expected in new[] { "Kiosk", "Voter", "Admin", "Registration" })
                Assert.Contains(expected, roles);
        }

        [Fact]
        public void Registration_endpoints_stay_limited_to_Registration_and_Admin()
        {
            var roles = RolesOf(typeof(RegistrationController));
            Assert.Equal(new[] { "Registration", "Admin" }, roles);
        }

        [Fact]
        public void Kiosk_only_endpoints_were_not_widened()
        {
            Assert.Equal(new[] { "Kiosk" }, RolesOf(Action<EventsController>(nameof(EventsController.GetMemberSnapshot))));
            Assert.Equal(new[] { "Kiosk" }, RolesOf(Action<VotesController>(nameof(VotesController.SyncOfflineVotesBulk))));
            Assert.Equal(new[] { "Voter" }, RolesOf(Action<VotesController>(nameof(VotesController.SubmitVote))));
        }

        [Fact]
        public void Station_login_is_anonymous_and_has_its_own_rate_limit_policy()
        {
            var m = Action<AuthController>(nameof(AuthController.RegistrationStationLogin));

            Assert.NotNull(m.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Equal("station-login", m.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
        }

        [Fact]
        public void Existing_interactive_registration_login_is_still_there()
        {
            Assert.NotNull(Action<AuthController>(nameof(AuthController.RegistrationLogin)));
        }

        [Fact]
        public void Station_token_is_Registration_role_without_user_id_or_permissions()
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = new string('k', 48),
                ["Jwt:Issuer"] = "EVoting.Api",
                ["Jwt:Audience"] = "EVoting.Clients",
                ["Jwt:StationTokenExpiryMinutes"] = "60"
            }).Build();

            var token = new TokenService(cfg).GenerateRegistrationStationToken("REG-PC-01");
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

            // TokenService builds its tokens with "new JwtSecurityToken(claims: ...)", which keeps the long claim-type URIs
            // (ClaimTypes.Role, ClaimTypes.NameIdentifier). Checked against the real IdentityModel assemblies; the short
            // JWT names are accepted too so that this test does not break if the token builder ever changes.
            Assert.Contains(jwt.Claims, c => IsRole(c, "Registration"));
            Assert.Contains(jwt.Claims, c => c.Type == "stationId" && c.Value == "REG-PC-01");
            Assert.Contains(jwt.Claims, c => c.Type == "authKind" && c.Value == "station");
            Assert.DoesNotContain(jwt.Claims, c => c.Type == "permission");
            Assert.DoesNotContain(jwt.Claims, c => IsRole(c, "Admin"));

            // The name identifier is a label ("station:REG-PC-01"), not a number => RequirePermission-protected endpoints can
            // never resolve a user for this token (UserIdOf looks at NameIdentifier, then "nameid", then "sub").
            Assert.Contains(jwt.Claims, c => (c.Type == ClaimTypes.NameIdentifier || c.Type == "nameid") && c.Value == "station:REG-PC-01");
            var principal = new ClaimsPrincipal(new ClaimsIdentity(jwt.Claims));
            Assert.Null(RequirePermissionAttribute.UserIdOf(principal));

            // The token has an expiry but no not-before claim, so measure the remaining lifetime from now.
            var remaining = jwt.ValidTo - DateTime.UtcNow;
            Assert.InRange(remaining.TotalMinutes, 58.5, 60.5);
        }

        [Fact]
        public void Station_token_lifetime_defaults_to_eight_hours()
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = new string('k', 48),
                ["Jwt:Issuer"] = "EVoting.Api",
                ["Jwt:Audience"] = "EVoting.Clients"
            }).Build();

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(new TokenService(cfg).GenerateRegistrationStationToken("REG-PC-01"));
            Assert.InRange((jwt.ValidTo - DateTime.UtcNow).TotalMinutes, 478.5, 480.5);
        }
    }
}
