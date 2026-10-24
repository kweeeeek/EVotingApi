using Isopoh.Cryptography.Argon2;

namespace EVoting.Api.Services
{
    /// <summary>
    /// Mirrors the legacy Admin's PasswordHelper exactly (same parameters, same pepper handling) so hashes are
    /// compatible across both apps. NOTE: the legacy implementation uses Argon2i (DataIndependentAddressing), NOT
    /// Argon2id as this comment previously claimed; Verify reads the variant from the stored hash, so both verify.
    /// </summary>
    public interface IPasswordService
    {
        bool Verify(string inputPassword, string storedHash);

        /// <summary>
        /// Hashes a password with the EXACT parameters the legacy Admin used (Argon2i / DataIndependentAddressing,
        /// t=3, m=64 MB, p=4, 32-byte output, password + pepper). The pepper never leaves the server.
        /// </summary>
        string Hash(string password);

        /// <summary>Cryptographically random temporary password satisfying the upper/lower/digit/symbol rule.</summary>
        string GenerateTemporaryPassword();
    }

    public class PasswordService : IPasswordService
    {
        // MUST match Admin Server constants exactly — never change after first deploy
        private const int TimeCost    = 3;
        private const int MemoryCost  = 65536; // 64 MB
        private const int Parallelism = 4;     // FIX: must be passed explicitly to Hash()
        private const int HashLength  = 32;

        private readonly string _pepper;

        //old constructor, commented out for clarity
        /*  public PasswordService(IConfiguration config)
          {
              _pepper = Environment.GetEnvironmentVariable("EVOTING_PEPPER")
                     ?? config["Security:PasswordPepper"]
                     ?? throw new InvalidOperationException(
                         "PasswordPepper is not configured. Set EVOTING_PEPPER env var or Security:PasswordPepper in appsettings.");
          }*/

        public PasswordService(IConfiguration config)
        {
            var pepper = Environment.GetEnvironmentVariable("EVOTING_PEPPER");
            if (string.IsNullOrWhiteSpace(pepper))
                pepper = config["Security:PasswordPepper"];
            if (string.IsNullOrWhiteSpace(pepper))
                throw new InvalidOperationException(
                    "PasswordPepper is not configured. Set EVOTING_PEPPER env var or Security:PasswordPepper in appsettings.");
            _pepper = pepper;
        }

        public bool Verify(string inputPassword, string storedHash)
        {
            if (string.IsNullOrEmpty(inputPassword) || string.IsNullOrEmpty(storedHash))
                return false;

            return Argon2.Verify(storedHash, inputPassword + _pepper);
        }

        public string Hash(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be null or empty.", nameof(password));

            return Argon2.Hash(
                password: password + _pepper,
                timeCost: TimeCost,
                memoryCost: MemoryCost,
                parallelism: Parallelism,
                type: Argon2Type.DataIndependentAddressing,
                hashLength: HashLength);
        }

        public string GenerateTemporaryPassword()
        {
            const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
            const string lower = "abcdefghijkmnpqrstuvwxyz";
            const string digit = "23456789";
            const string symbol = "!@#$%&*?";
            const string all = upper + lower + digit + symbol;

            var chars = new List<char> { Pick(upper), Pick(lower), Pick(digit), Pick(symbol) };
            while (chars.Count < 14) chars.Add(Pick(all));

            // Fisher–Yates shuffle with a CSPRNG so the guaranteed characters are not in fixed positions.
            for (int i = chars.Count - 1; i > 0; i--)
            {
                int j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
                (chars[i], chars[j]) = (chars[j], chars[i]);
            }
            return new string(chars.ToArray());

            static char Pick(string set) =>
                set[System.Security.Cryptography.RandomNumberGenerator.GetInt32(set.Length)];
        }
    }
}
