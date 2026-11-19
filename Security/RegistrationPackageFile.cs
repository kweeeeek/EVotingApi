using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EVoting.Api.Models.Responses;

namespace EVoting.Api.Security
{
    /// <summary>
    /// The "registration package" (.evreg): the member list of a General Assembly election (same rows as
    /// GET registration/members/roster) plus the election itself (same shape as GET events/current), for Registration desks at a
    /// venue with no signal. The Admin app saves it to a USB stick; the Registration web page imports it.
    ///
    /// A browser cannot know the kiosk secret, so this file is protected with a PASSPHRASE the server generates for each package and
    /// shows ONCE to the administrator (never stored, never written next to the file): PBKDF2-SHA256 -> AES-256-GCM, exactly what the
    /// browser's WebCrypto does. The readable header is bound to the ciphertext as associated data.
    ///
    /// SAME FORMAT as EVoting.Registration.Web/js/offline/package.js - change both together.
    /// </summary>
    public static class RegistrationPackageFile
    {
        public const string Format = "EVOTING-REGISTRATION-PACKAGE";
        public const int Version = 1;
        public const int Iterations = 310_000;
        public const string FileExtension = ".evreg";

        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public sealed class Payload
        {
            public EventResponse Event { get; set; } = new();
            public List<RegistrationMemberResponse> Members { get; set; } = new();
        }

        public sealed record Written(string Text, string ExportId, string Sha256, string Passphrase);

        /// <summary>
        /// 24 characters from an alphabet without look-alikes (no 0/O, 1/I/L), in groups of 4: about 118 bits, typed once per desk.
        /// </summary>
        public static string NewPassphrase()
        {
            const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
            var sb = new StringBuilder();
            for (int i = 0; i < 24; i++)
            {
                if (i > 0 && i % 4 == 0) sb.Append('-');
                sb.Append(alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]);
            }
            return sb.ToString();
        }

        /// <summary>The passphrase as the browser uses it: dashes and spaces removed, upper case.</summary>
        public static string NormalizePassphrase(string passphrase) =>
            new string(passphrase.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        public static Written Write(Payload payload, DateTime createdAtUtc, string? passphrase = null)
        {
            passphrase ??= NewPassphrase();
            var exportId = Guid.NewGuid().ToString("N");
            var created = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var salt = RandomNumberGenerator.GetBytes(16);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var saltB64 = Convert.ToBase64String(salt);

            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(NormalizePassphrase(passphrase)), salt, Iterations, HashAlgorithmName.SHA256, 32);
            var aad = AssociatedData(payload.Event.EventId, exportId, created, payload.Members.Count, saltB64);
            var plain = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, 16))
                aes.Encrypt(nonce, plain, cipher, tag, aad);

            var header = new Dictionary<string, object>
            {
                ["format"] = Format,
                ["version"] = Version,
                ["eventId"] = payload.Event.EventId,
                ["eventTitle"] = payload.Event.Title,
                ["exportId"] = exportId,
                ["createdAtUtc"] = created,
                ["memberCount"] = payload.Members.Count,
                ["kdf"] = "PBKDF2-SHA256",
                ["iterations"] = Iterations,
                ["salt"] = saltB64,
                ["nonce"] = Convert.ToBase64String(nonce),
                // WebCrypto's AES-GCM expects ciphertext || tag, which is what this is.
                ["data"] = Convert.ToBase64String(cipher.Concat(tag).ToArray()),
            };
            var text = JsonSerializer.Serialize(header, new JsonSerializerOptions(Json) { WriteIndented = true });
            return new Written(text, exportId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), passphrase);
        }

        /// <summary>The readable header fields that cannot be altered. eventTitle is display only and is NOT bound (the encrypted event is authoritative).</summary>
        internal static byte[] AssociatedData(int eventId, string exportId, string createdAtUtc, int memberCount, string saltB64) =>
            Encoding.UTF8.GetBytes(string.Join("|",
                Format, Version.ToString(CultureInfo.InvariantCulture), eventId.ToString(CultureInfo.InvariantCulture), exportId,
                createdAtUtc, memberCount.ToString(CultureInfo.InvariantCulture), Iterations.ToString(CultureInfo.InvariantCulture), saltB64));
    }
}
