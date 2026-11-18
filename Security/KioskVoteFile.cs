using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EVoting.Api.Security
{
    /// <summary>
    /// The file a kiosk writes to a USB stick when it cannot reach the server ("Export pending votes"), and that the Admin app
    /// uploads here so the votes are recorded exactly as the kiosk's own automatic sync would record them.
    ///
    /// SECRECY + AUTHENTICITY: the vote list (member ids next to their ballot) is encrypted with AES-256-GCM under a key derived
    /// (HKDF-SHA256) from the kiosk secret - the same secret every kiosk already needs to sign in and that only kiosks and this
    /// server hold. Whoever carries the USB stick cannot read the ballots, and a file that was edited, or written by anything
    /// that does not know the kiosk secret, fails to decrypt and is refused as a whole. The readable header (kiosk, export id,
    /// time, count) is bound to the ciphertext as associated data, so it cannot be altered either.
    ///
    /// The SAME format is implemented in EVoting.Kiosk (Services/KioskVoteFile.cs). Change both together; the test vector in
    /// EVoting.Api.Tests/KioskVoteFileTests.cs pins the format.
    /// </summary>
    public static class KioskVoteFile
    {
        public const string Format = "EVOTING-KIOSK-VOTES";
        public const int Version = 1;
        public const string FileExtension = ".evvotes";

        private static readonly byte[] KdfSalt = Encoding.UTF8.GetBytes("EVoting.KioskVoteExport.v1");
        private static readonly byte[] KdfInfo = Encoding.UTF8.GetBytes("aes-256-gcm file key");

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public sealed class Envelope
        {
            public string Format { get; set; } = "";
            public int Version { get; set; }
            public string KioskId { get; set; } = "";
            public string ExportId { get; set; } = "";
            public DateTime CreatedAtUtc { get; set; }
            public int VoteCount { get; set; }
            public string Nonce { get; set; } = "";
            public string Data { get; set; } = "";
        }

        public sealed class Payload
        {
            public List<Vote> Votes { get; set; } = new();
        }

        public sealed class Vote
        {
            public int MemberId { get; set; }
            public int EventId { get; set; }
            /// <summary>Position -> candidacy ids, exactly as the kiosk's PendingVotes.SelectionsJson holds it.</summary>
            public Dictionary<string, List<int>> Selections { get; set; } = new();
            public string ReceiptCode { get; set; } = "";
            public DateTime CastAtUtc { get; set; }
        }

        public sealed record Opened(Envelope Header, Payload Payload, string FileSha256);

        public sealed class InvalidFileException : Exception
        {
            public InvalidFileException(string message) : base(message) { }
        }

        /// <summary>Writes a file (used by tests; the kiosk has its own copy of this method).</summary>
        public static string Write(string kioskSecret, string kioskId, Payload payload, DateTime createdAtUtc, string? exportId = null, byte[]? nonce = null)
        {
            var header = new Envelope
            {
                Format = Format,
                Version = Version,
                KioskId = kioskId,
                ExportId = exportId ?? Guid.NewGuid().ToString("N"),
                CreatedAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc),
                VoteCount = payload.Votes.Count,
            };
            nonce ??= RandomNumberGenerator.GetBytes(12);
            var plain = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(DeriveKey(kioskSecret), 16))
                aes.Encrypt(nonce, plain, cipher, tag, AssociatedData(header));
            header.Nonce = Convert.ToBase64String(nonce);
            header.Data = Convert.ToBase64String(cipher.Concat(tag).ToArray());
            return JsonSerializer.Serialize(header, new JsonSerializerOptions(Json) { WriteIndented = true });
        }

        /// <summary>Checks and decrypts a file. Throws <see cref="InvalidFileException"/> with a message for the administrator.</summary>
        public static Opened Open(string kioskSecret, string fileText)
        {
            if (string.IsNullOrWhiteSpace(fileText))
                throw new InvalidFileException("The file is empty.");

            Envelope? header;
            try { header = JsonSerializer.Deserialize<Envelope>(fileText, Json); }
            catch (JsonException) { throw new InvalidFileException("This is not a kiosk vote file (it is not readable)."); }

            if (header == null || header.Format != Format)
                throw new InvalidFileException("This is not a kiosk vote file.");
            if (header.Version != Version)
                throw new InvalidFileException($"This kiosk vote file has format version {header.Version}; this server reads version {Version}. Update the API or the kiosk so they match.");

            byte[] nonce, data;
            try
            {
                nonce = Convert.FromBase64String(header.Nonce);
                data = Convert.FromBase64String(header.Data);
            }
            catch (FormatException) { throw new InvalidFileException("The kiosk vote file is damaged."); }
            if (nonce.Length != 12 || data.Length < 16)
                throw new InvalidFileException("The kiosk vote file is damaged.");

            var cipher = data.AsSpan(0, data.Length - 16);
            var tag = data.AsSpan(data.Length - 16);
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(DeriveKey(kioskSecret), 16);
                aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(header));
            }
            catch (CryptographicException)
            {
                throw new InvalidFileException(
                    "The kiosk vote file was changed after it was written, is damaged, or comes from a kiosk with a different kiosk secret. Nothing was imported.");
            }

            var payload = JsonSerializer.Deserialize<Payload>(plain, Json)
                          ?? throw new InvalidFileException("The kiosk vote file is damaged.");
            if (payload.Votes.Count != header.VoteCount)
                throw new InvalidFileException("The kiosk vote file is damaged (vote count does not match).");

            var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileText)));
            return new Opened(header, payload, sha);
        }

        private static byte[] DeriveKey(string kioskSecret) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(kioskSecret), 32, KdfSalt, KdfInfo);

        /// <summary>The readable header fields, bound to the ciphertext so none of them can be altered.</summary>
        private static byte[] AssociatedData(Envelope h) => Encoding.UTF8.GetBytes(string.Join("|",
            h.Format, h.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), h.KioskId, h.ExportId,
            DateTime.SpecifyKind(h.CreatedAtUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture),
            h.VoteCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }
}
