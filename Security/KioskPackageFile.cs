using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EVoting.Api.Security
{
    /// <summary>
    /// The "kiosk package" (.evpkg): everything a kiosk downloads while online (event + schedule, member roster with eligibility,
    /// candidates with photos, position rules, members who already voted), written by the Admin app to a USB stick so a kiosk
    /// that cannot reach the server - even one that was never online - can still verify voters and show the ballot offline.
    ///
    /// Same protection as <see cref="KioskVoteFile"/> (AES-256-GCM, key from the kiosk secret via HKDF, header bound as associated
    /// data) but with its OWN key-derivation salt, so a package can never be mistaken for a vote file or the other way round.
    /// It holds the personal data of every member: only kiosks (and this server) can read it.
    ///
    /// SAME FORMAT as EVoting.Kiosk/Services/KioskPackageFile.cs - change both together.
    /// </summary>
    public static class KioskPackageFile
    {
        public const string Format = "EVOTING-KIOSK-PACKAGE";
        public const int Version = 1;
        public const string FileExtension = ".evpkg";

        private static readonly byte[] KdfSalt = Encoding.UTF8.GetBytes("EVoting.KioskPackage.v1");
        private static readonly byte[] KdfInfo = Encoding.UTF8.GetBytes("aes-256-gcm file key");
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public sealed class Payload
        {
            public PackageEvent Event { get; set; } = new();
            public List<PackageRule> PositionRules { get; set; } = new();
            public List<PackageCandidate> Candidates { get; set; } = new();
            public List<PackageMember> Members { get; set; } = new();
            public List<int> VotedMemberIds { get; set; } = new();
        }

        public sealed class PackageEvent
        {
            public int EventId { get; set; }
            public string Title { get; set; } = "";
            public string Venue { get; set; } = "";
            public DateTime ScheduleFrom { get; set; }
            public DateTime ScheduleTo { get; set; }
            public string Status { get; set; } = "";
            public bool IsActive { get; set; }
            public string EventType { get; set; } = "";
        }

        public sealed class PackageRule
        {
            public string Position { get; set; } = "";
            public int MinVotes { get; set; }
            public int MaxVotes { get; set; }
            public int TotalSeats { get; set; }
        }

        public sealed class PackageCandidate
        {
            public int CandidacyId { get; set; }
            public string CandidateName { get; set; } = "";
            public string Position { get; set; } = "";
            public string Status { get; set; } = "";
            public string? PreviousPositions { get; set; }
            public string? Reason { get; set; }
            public string? PhotoBase64 { get; set; }
        }

        public sealed class PackageMember
        {
            public int MemberId { get; set; }
            public string AccountCode { get; set; } = "";
            public string MemberName { get; set; } = "";
            public int? BranchId { get; set; }
            public string BranchName { get; set; } = "";
            public bool IsEligible { get; set; }
            public string? PhotoBase64 { get; set; }
        }

        public sealed record Written(string Text, string ExportId, string Sha256);

        public static Written Write(string kioskSecret, Payload payload, DateTime createdAtUtc)
        {
            var exportId = Guid.NewGuid().ToString("N");
            var created = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
            var header = new Dictionary<string, object>
            {
                ["format"] = Format,
                ["version"] = Version,
                ["eventId"] = payload.Event.EventId,
                ["exportId"] = exportId,
                ["createdAtUtc"] = created,
                ["memberCount"] = payload.Members.Count,
                ["candidateCount"] = payload.Candidates.Count,
                ["votedCount"] = payload.VotedMemberIds.Count,
            };
            var aad = AssociatedData(payload.Event.EventId, exportId, created, payload.Members.Count, payload.Candidates.Count, payload.VotedMemberIds.Count);

            var nonce = RandomNumberGenerator.GetBytes(12);
            var plain = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(DeriveKey(kioskSecret), 16))
                aes.Encrypt(nonce, plain, cipher, tag, aad);

            header["nonce"] = Convert.ToBase64String(nonce);
            header["data"] = Convert.ToBase64String(cipher.Concat(tag).ToArray());
            var text = JsonSerializer.Serialize(header, new JsonSerializerOptions(Json) { WriteIndented = true });
            return new Written(text, exportId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }

        internal static byte[] AssociatedData(int eventId, string exportId, DateTime createdUtc, int members, int candidates, int voted) =>
            Encoding.UTF8.GetBytes(string.Join("|",
                Format, Version.ToString(CultureInfo.InvariantCulture), eventId.ToString(CultureInfo.InvariantCulture), exportId,
                DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
                members.ToString(CultureInfo.InvariantCulture), candidates.ToString(CultureInfo.InvariantCulture), voted.ToString(CultureInfo.InvariantCulture)));

        private static byte[] DeriveKey(string kioskSecret) =>
            HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(kioskSecret), 32, KdfSalt, KdfInfo);
    }
}
