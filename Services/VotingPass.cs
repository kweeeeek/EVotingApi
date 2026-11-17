using System.Security.Cryptography;
using System.Text;

namespace EVoting.Api.Services
{
    /// <summary>
    /// The voting pass carried by the Registration QR code: <c>EVP1-</c> followed by 20 characters of Crockford base32
    /// (100 random bits). Example: <c>EVP1-7K3QM9X2RD4TB8NWZ6HC</c>.
    /// <para>
    /// Why the format matters: the mobile pages and the kiosk already put whatever the QR holds into the <c>accountCode</c> field of
    /// voter-verify. The pass is 25 characters of letters, digits and one dash, which every existing client accepts as an "account code",
    /// so a client that has never heard of passes still sends it unchanged - and the API recognises it by its prefix. Old clients
    /// therefore work with the new QR without an update. A real account code (digits only, e.g. 00200000000123) can never start with "EVP1-".
    /// </para>
    /// </summary>
    public static class VotingPass
    {
        public const string Prefix = "EVP1-";
        public const int BodyLength = 20;                      // 20 x 5 bits = 100 bits
        private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford: no I, L, O, U

        /// <summary>A new random pass (cryptographic RNG).</summary>
        public static string Generate()
        {
            var sb = new StringBuilder(Prefix.Length + BodyLength).Append(Prefix);
            for (int i = 0; i < BodyLength; i++)
                sb.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
            return sb.ToString();
        }

        /// <summary>True when the text has the shape of a pass (cheap check; says nothing about whether it was issued).</summary>
        public static bool LooksLikePass(string? raw) => Normalize(raw) != null;

        /// <summary>
        /// The canonical pass text, or null when <paramref name="raw"/> is not one. Upper case; spaces dropped; the Crockford
        /// look-alikes a person might type (O→0, I/L→1) are mapped, so a pass read aloud or typed from the slip still matches.
        /// </summary>
        public static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw.Trim().Replace(" ", "").ToUpperInvariant();
            if (!t.StartsWith(Prefix, StringComparison.Ordinal)) return null;
            var body = t.Substring(Prefix.Length).Replace("-", "");
            if (body.Length != BodyLength) return null;

            var sb = new StringBuilder(Prefix.Length + BodyLength).Append(Prefix);
            foreach (var ch in body)
            {
                var c = ch switch { 'O' => '0', 'I' => '1', 'L' => '1', _ => ch };
                if (Alphabet.IndexOf(c) < 0) return null;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>Lower-case hex SHA-256 of the normalised pass (what is stored in VotingPasses.PassHash).</summary>
        public static string Hash(string normalizedPass) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPass))).ToLowerInvariant();
    }
}
