using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Konscious.Security.Cryptography;

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

        // F-50. The Isopoh.Cryptography.Argon2 1.0.2 library used here before locked its 64 MB work area into RAM ("secure memory").
        // On the Windows server every staff sign-in then committed up to ~2 GB, much of it never returned (live server 2026-11-13:
        // 76 MB -> 2.5 GB after three sign-ins; 11 GB after ~20, then 14-25 s per sign-in while that memory was paged in and out).
        // Its successor 2.0.0 crashed the API in testing (heap corruption). Konscious.Security.Cryptography.Argon2 computes the same
        // Argon2 in plain managed memory that the garbage collector frees normally. The stored text is read and written here exactly
        // as 1.0.2 wrote it ("$argon2i$v=19$m=65536,t=3,p=4$<salt>$<hash>", padded Base64; unpadded Base64 is read too), so every
        // stored password still verifies (EVoting.Api.Tests/PasswordLibraryUpgradeTests.cs holds hashes made by the deployed 1.0.2).

        // Limits for reading a stored hash (ours: m=64 MB, t=3, p=4): a damaged value must not make one sign-in allocate gigabytes.
        private const int MaxMemoryKb = 1024 * 1024;
        private const int MaxTimeCost = 32;
        private const int MaxLanes    = 16;

        // Each check needs 64 MB and 4 threads for ~0.2-0.4 s. Without a limit, 40 desks signing in at the same moment hold 2.5 GB at
        // once (test machine: 2.3-3.9 GB). At most this many run together; the others wait their turn (a fraction of a second each).
        // 28 logical CPUs (the Altos P10 F9) -> 6 at once -> at most ~400 MB for password checks, whatever the burst.
        private static readonly SemaphoreSlim Gate = new(MaxConcurrentChecks, MaxConcurrentChecks);
        internal static int MaxConcurrentChecks => Math.Clamp(Environment.ProcessorCount / 4, 2, 6);

        public bool Verify(string inputPassword, string storedHash)
        {
            if (string.IsNullOrEmpty(inputPassword) || string.IsNullOrEmpty(storedHash))
                return false;
            if (!TryParse(storedHash, out var stored))
                return false;   // not an Argon2 hash we can read: never matches (1.0.2 answered false as well)

            byte[] password = Encoding.UTF8.GetBytes(inputPassword + _pepper);
            byte[]? actual = null;
            try
            {
                actual = Compute(stored.Type, password, stored.Salt, stored.TimeCost, stored.MemoryKb, stored.Lanes, stored.Hash.Length);
                return CryptographicOperations.FixedTimeEquals(stored.Hash, actual);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
                if (actual != null) CryptographicOperations.ZeroMemory(actual);
            }
        }

        public string Hash(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be null or empty.", nameof(password));

            byte[] pw = Encoding.UTF8.GetBytes(password + _pepper);
            byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
            try
            {
                byte[] hash = Compute("argon2i", pw, salt, TimeCost, MemoryCost, Parallelism, HashLength);
                return string.Create(CultureInfo.InvariantCulture,
                    $"$argon2i$v=19$m={MemoryCost},t={TimeCost},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pw);
            }
        }

        private static byte[] Compute(string type, byte[] password, byte[] salt, int timeCost, int memoryKb, int lanes, int length)
        {
            Gate.Wait();
            try
            {
                return ComputeNow(type, password, salt, timeCost, memoryKb, lanes, length);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static byte[] ComputeNow(string type, byte[] password, byte[] salt, int timeCost, int memoryKb, int lanes, int length)
        {
            using Argon2 argon2 = type switch
            {
                "argon2i" => new Argon2i(password),
                "argon2d" => new Argon2d(password),
                _ => new Argon2id(password),
            };
            argon2.Salt = salt;
            argon2.Iterations = timeCost;
            argon2.MemorySize = memoryKb;
            argon2.DegreeOfParallelism = lanes;
            return argon2.GetBytes(length);
        }

        private readonly record struct StoredHash(string Type, int MemoryKb, int TimeCost, int Lanes, byte[] Salt, byte[] Hash);

        /// <summary>Reads "$argon2i$v=19$m=65536,t=3,p=4$salt$hash" (type argon2i/argon2d/argon2id, version 19 only).</summary>
        private static bool TryParse(string text, out StoredHash stored)
        {
            stored = default;
            var parts = text.Split('$');
            if (parts.Length != 6 || parts[0].Length != 0)
                return false;
            if (parts[1] is not ("argon2i" or "argon2d" or "argon2id") || parts[2] != "v=19")
                return false;
            int m = 0, t = 0, p = 0;
            foreach (var pair in parts[3].Split(','))
            {
                var kv = pair.Split('=');
                if (kv.Length != 2 || !int.TryParse(kv[1], NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                    return false;
                switch (kv[0])
                {
                    case "m": m = value; break;
                    case "t": t = value; break;
                    case "p": p = value; break;
                    default: return false;
                }
            }
            if (t < 1 || t > MaxTimeCost || p < 1 || p > MaxLanes || m < 8 * p || m > MaxMemoryKb)
                return false;
            if (!TryBase64(parts[4], out var salt) || salt.Length < 8)
                return false;
            if (!TryBase64(parts[5], out var hash) || hash.Length < 4 || hash.Length > 1024)
                return false;
            stored = new StoredHash(parts[1], m, t, p, salt, hash);
            return true;
        }

        private static bool TryBase64(string text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text.Length == 0)
                return false;
            try
            {
                bytes = Convert.FromBase64String(text.PadRight(text.Length + (4 - text.Length % 4) % 4, '='));
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
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
