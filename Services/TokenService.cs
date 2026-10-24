using EVoting.Api.Infrastructure.Entities;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace EVoting.Api.Services
{
    public interface ITokenService
    {
        string GenerateAdminToken(UserEntity user);
        string GenerateRegistrationToken(UserEntity user);
        string GenerateKioskToken(string kioskId);
        string GenerateVoterToken(int memberId, int eventId, string memberName);
    }

    public class TokenService : ITokenService
    {
        private readonly IConfiguration _config;

        public TokenService(IConfiguration config) => _config = config;

        private SigningCredentials GetCredentials()
        {
            //jwt key ay para sa pag sign ng token, dapat nakaset sa environment variable o sa appsettings.json
            var jwtKey = Environment.GetEnvironmentVariable("EVOTING_JWT_KEY")
                      ?? _config["Jwt:Key"]
                      ?? throw new InvalidOperationException(
                          "JWT signing key not configured. Set EVOTING_JWT_KEY or Jwt:Key.");

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            return new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        }

        // gagawa ng token para sa admin user, kasama ang mga claims tulad ng user id, username, email, role, full name, branch id, at permission codes
        public string GenerateAdminToken(UserEntity user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name,           user.Username),
                new(ClaimTypes.Email,          user.Email),
                new(ClaimTypes.Role,           "Admin"),
                new("fullName",                user.FullName),
                new("branchId",                user.BranchId?.ToString() ?? ""),
            };

            // Embed permission codes so controllers can check without DB hit
            foreach (var perm in user.PermissionCodes)
                claims.Add(new Claim("permission", perm));

         
            if (!int.TryParse(_config["Jwt:AdminTokenExpiryMinutes"], out int adminExpiry))
                adminExpiry = 480;

            return BuildToken(claims, adminExpiry);
        }

        //gagawa ng token para sa registration user, kasama ang mga claims tulad ng user id, username, email, role, full name, branch id, at permission codes
        public string GenerateRegistrationToken(UserEntity user)
        {
            
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name,           user.Username),
                new(ClaimTypes.Email,          user.Email),
                new(ClaimTypes.Role,           "Registration"),
                new("fullName",                user.FullName),
                new("branchId",                user.BranchId?.ToString() ?? ""),
            };

           
            foreach (var perm in user.PermissionCodes)
                claims.Add(new Claim("permission", perm));

           
            if (!int.TryParse(_config["Jwt:AdminTokenExpiryMinutes"], out int regExpiry))
                regExpiry = 480;

            return BuildToken(claims, regExpiry);
        }

        //gagawa ng token para sa kiosk, kasama ang mga claims tulad ng kiosk id at role
        public string GenerateKioskToken(string kioskId)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, kioskId),
                new(ClaimTypes.Role,           "Kiosk"),
                new("kioskId",                 kioskId),
            };

            if (!int.TryParse(_config["Jwt:KioskTokenExpiryMinutes"], out int kioskExpiry))
                kioskExpiry = 480;

            return BuildToken(claims, kioskExpiry);
        }


        //gagawa ng token para sa voter, kasama ang mga claims tulad ng member id, event id, member name, at role
        public string GenerateVoterToken(int memberId, int eventId, string memberName)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, memberId.ToString()),
                new(ClaimTypes.Name,           memberName),
                new(ClaimTypes.Role,           "Voter"),
                new("memberId",                memberId.ToString()),
                new("eventId",                 eventId.ToString()),
            };

            if (!int.TryParse(_config["Jwt:VoterTokenExpiryMinutes"], out int voterExpiry))
                voterExpiry = 15;

            return BuildToken(claims, voterExpiry);
        }

        //gagawa ng token gamit ang mga claims at expiry time
        private string BuildToken(IEnumerable<Claim> claims, int expiryMinutes)
        {
            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
                signingCredentials: GetCredentials());

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}