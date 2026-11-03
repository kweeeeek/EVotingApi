using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EVoting.Api.Security
{
    /// <summary>Result of checking a Registration Station's non-interactive credential.</summary>
    public enum StationAuthOutcome
    {
        Authenticated,
        /// <summary>No station credential is configured on this server at all (the endpoint answers 503, like kiosk-login).</summary>
        NotConfigured,
        /// <summary>Unknown station, wrong secret, or a malformed request. Deliberately one outcome: no enumeration of station ids.</summary>
        InvalidCredentials
    }

    /// <summary>
    /// Station ids are LABELS (audit, diagnostics, per-station lookup) - never proof of identity. The machine name of the
    /// Registration PC is the usual value, so the accepted alphabet is deliberately small and everything is compared in
    /// upper case.
    /// </summary>
    public static class StationId
    {
        public const int MaxLength = 64;

        /// <summary>Letters, digits, '.', '_' and '-' only; 1..64 characters; normalised to upper-case invariant.</summary>
        public static bool TryNormalize(string? raw, out string normalized)
        {
            normalized = "";
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var s = raw.Trim();
            if (s.Length == 0 || s.Length > MaxLength) return false;

            foreach (var ch in s)
            {
                var ok = char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-';
                if (!ok) return false;
            }

            normalized = s.ToUpperInvariant();
            return true;
        }
    }

    /// <summary>
    /// Decides whether the (stationId, secret) pair presented by a Registration Station is acceptable.
    /// Pure logic - no I/O, no logging of secrets - so it can be unit tested without a web host.
    ///
    /// Two kinds of server-side enrolment, both optional:
    ///  1. Per-station: station id -> SHA-256(secret) in hex. The server never stores the plaintext secret, and one stolen
    ///     station can be revoked by deleting its line. This is the recommended production setup.
    ///  2. One shared enrolment secret (EVOTING_REGISTRATION_STATION_SECRET): accepted for a station id that has NO
    ///     per-station line. A station that does have a per-station line is checked against that line only.
    ///
    /// The secret is a high-entropy random value generated at provisioning time (see the deployment guide), so a plain
    /// SHA-256 digest is adequate here; it is compared in constant time.
    /// </summary>
    public sealed class StationCredentialPolicy
    {
        // Compared against when nothing is enrolled for the presented id, so a failed lookup costs the same as a failed compare.
        private static readonly byte[] DummyDigest = SHA256.HashData(Encoding.UTF8.GetBytes("evoting-station-dummy-digest"));

        private readonly Dictionary<string, byte[]> _perStation;
        private readonly byte[]? _sharedSecretDigest;

        /// <summary>Station ids that were configured but could not be used (bad id, bad hash). Names only - never values.</summary>
        public IReadOnlyList<string> RejectedEntries { get; }

        public StationCredentialPolicy(IEnumerable<KeyValuePair<string, string?>>? perStationSha256Hex, string? sharedSecret)
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var rejected = new List<string>();

            if (perStationSha256Hex != null)
            {
                foreach (var kv in perStationSha256Hex)
                {
                    if (!StationId.TryNormalize(kv.Key, out var id))
                    {
                        rejected.Add($"'{Truncate(kv.Key)}': not a valid station id");
                        continue;
                    }

                    var digest = TryParseSha256Hex(kv.Value);
                    if (digest == null)
                    {
                        rejected.Add($"'{id}': value is not a 64-character hexadecimal SHA-256 digest");
                        continue;
                    }

                    map[id] = digest;
                }
            }

            _perStation = map;
            RejectedEntries = rejected;
            _sharedSecretDigest = string.IsNullOrWhiteSpace(sharedSecret)
                ? null
                : SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));
        }

        /// <summary>False when neither a per-station line nor a shared secret is usable.</summary>
        public bool IsConfigured => _perStation.Count > 0 || _sharedSecretDigest != null;

        public int EnrolledStationCount => _perStation.Count;

        public StationAuthOutcome Validate(string? stationId, string? secret, out string normalizedStationId)
        {
            normalizedStationId = "";

            if (!IsConfigured) return StationAuthOutcome.NotConfigured;

            var idOk = StationId.TryNormalize(stationId, out var id);
            var secretOk = !string.IsNullOrEmpty(secret) && secret.Length <= 256;

            var presented = SHA256.HashData(Encoding.UTF8.GetBytes(secretOk ? secret! : string.Empty));

            byte[]? expected = null;
            if (idOk && !_perStation.TryGetValue(id, out expected))
                expected = _sharedSecretDigest;

            var haveExpected = idOk && expected != null;
            var equal = CryptographicOperations.FixedTimeEquals(presented, expected ?? DummyDigest);

            if (idOk && secretOk && haveExpected && equal)
            {
                normalizedStationId = id;
                return StationAuthOutcome.Authenticated;
            }

            return StationAuthOutcome.InvalidCredentials;
        }

        /// <summary>The digest to put in Security:RegistrationStations for a given secret (used by tests and the provisioning docs).</summary>
        public static string ComputeSha256Hex(string secret) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

        internal static byte[]? TryParseSha256Hex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var v = value.Trim();
            if (v.Length != 64) return null;
            try { return Convert.FromHexString(v); }
            catch (FormatException) { return null; }
        }

        private static string Truncate(string? s) =>
            s == null ? "" : (s.Length <= 24 ? s : s.Substring(0, 24) + "...");
    }

    /// <summary>
    /// Reads the station enrolment from the environment / configuration. Precedence per source mirrors the rest of the API:
    /// environment variable first, then appsettings.
    ///   EVOTING_REGISTRATION_STATIONS        JSON object  {"REG-PC-01":"&lt;sha256 hex&gt;", ...}
    ///   Security:RegistrationStations        same thing as a configuration section
    ///   EVOTING_REGISTRATION_STATION_SECRET  optional shared enrolment secret
    ///   Security:RegistrationStationSecret
    /// </summary>
    public static class StationCredentialSettings
    {
        public const string StationsEnvVar = "EVOTING_REGISTRATION_STATIONS";
        public const string SharedSecretEnvVar = "EVOTING_REGISTRATION_STATION_SECRET";
        public const string StationsSection = "Security:RegistrationStations";
        public const string SharedSecretKey = "Security:RegistrationStationSecret";

        public static StationCredentialPolicy Load(
            Microsoft.Extensions.Configuration.IConfiguration config,
            Func<string, string?>? getEnv = null)
        {
            getEnv ??= Environment.GetEnvironmentVariable;

            var entries = new List<KeyValuePair<string, string?>>();

            var json = getEnv(StationsEnvVar);
            if (!string.IsNullOrWhiteSpace(json))
            {
                // Template text must stop start-up exactly like every other secret setting.
                SecretSettings.RejectPlaceholder($"{StationsEnvVar}", json);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in doc.RootElement.EnumerateObject())
                            entries.Add(new KeyValuePair<string, string?>(
                                p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null));
                    }
                }
                catch (JsonException)
                {
                    // Fail closed: nothing from a malformed document is trusted. The caller logs RejectedEntries.
                    entries.Add(new KeyValuePair<string, string?>($"{StationsEnvVar}", "(malformed JSON)"));
                }
            }
            else
            {
                foreach (var child in config.GetSection(StationsSection).GetChildren())
                {
                    SecretSettings.RejectPlaceholder($"{StationsSection}:{child.Key}", child.Value);
                    entries.Add(new KeyValuePair<string, string?>(child.Key, child.Value));
                }
            }

            var shared = getEnv(SharedSecretEnvVar);
            if (string.IsNullOrWhiteSpace(shared)) shared = config[SharedSecretKey];
            SecretSettings.RejectPlaceholder($"{SharedSecretEnvVar} / {SharedSecretKey}", shared);

            return new StationCredentialPolicy(entries, shared);
        }
    }
}
