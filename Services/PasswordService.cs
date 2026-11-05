using System.Security.Cryptography;
using System.Text;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.SecureArray;

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
        private const int SaltLength  = 16;    // what Argon2.Hash(...) generated, so stored hashes keep the same shape

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

        // WHY NOT Argon2.Hash(...) / Argon2.Verify(...)?
        // Those one-line helpers of Isopoh.Cryptography.Argon2 1.0.x create an Argon2 object (which owns the 64 MB work area, pinned) and never dispose it,
        // so EVERY call kept 64 MB for the life of the process: 8 sign-ins left 493 MB behind even after a forced full garbage collection, and one
        // user import (up to 100 accounts, each hashed) would have kept 6.4 GB. Here the same computation is done with the library's public pieces
        // and everything disposable is disposed. The parameters, the pepper handling and the text format of the hash are exactly what they were,
        // so every stored password still verifies and every new hash still verifies in the legacy Admin (EVoting.Api.Tests/PasswordServiceTests.cs).

        public bool Verify(string inputPassword, string storedHash)
        {
            if (string.IsNullOrEmpty(inputPassword) || string.IsNullOrEmpty(storedHash))
                return false;

            var config = new Argon2Config
            {
                Password = Encoding.UTF8.GetBytes(inputPassword + _pepper),
                ClearPassword = true
            };
            SecureArray<byte>? expected = null;
            try
            {
                // The type, version, cost, lanes, salt and hash length are read from the stored text, so a hash made with other settings still verifies.
                if (!config.DecodeString(storedHash, out expected) || expected == null)
                    return false;
                config.Threads = Math.Max(1, config.Lanes);

                using var argon2 = new Argon2(config);
                using var actual = argon2.Hash();
                return CryptographicOperations.FixedTimeEquals(expected.Buffer, actual.Buffer);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                return false;   // a stored value that is not an Argon2 hash never matches (the library's own Verify answered false as well)
            }
            finally
            {
                expected?.Dispose();
            }
        }

        public string Hash(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be null or empty.", nameof(password));

            var config = new Argon2Config
            {
                Type = Argon2Type.DataIndependentAddressing,
                Version = Argon2Version.Nineteen,
                TimeCost = TimeCost,
                MemoryCost = MemoryCost,
                Lanes = Parallelism,
                Threads = Parallelism,
                HashLength = HashLength,
                Password = Encoding.UTF8.GetBytes(password + _pepper),
                Salt = RandomNumberGenerator.GetBytes(SaltLength),
                ClearPassword = true
            };
            using var argon2 = new Argon2(config);
            using var hash = argon2.Hash();
            return config.EncodeString(hash.Buffer);
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
