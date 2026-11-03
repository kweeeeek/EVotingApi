using EVoting.Api.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EVoting.Api.Tests
{
    public class StationCredentialPolicyTests
    {
        private const string SecretA = "Zk3n0w-this-is-a-long-random-station-secret-A";
        private const string SecretB = "another-long-random-station-secret-B-0123456789";

        private static KeyValuePair<string, string?> Line(string id, string secret) =>
            new(id, StationCredentialPolicy.ComputeSha256Hex(secret));

        [Fact]
        public void Nothing_configured_reports_NotConfigured_not_InvalidCredentials()
        {
            var p = new StationCredentialPolicy(null, null);
            Assert.False(p.IsConfigured);
            Assert.Equal(StationAuthOutcome.NotConfigured, p.Validate("REG-01", SecretA, out _));
        }

        [Fact]
        public void Per_station_secret_authenticates_and_normalizes_the_id()
        {
            var p = new StationCredentialPolicy(new[] { Line("reg-pc-01", SecretA) }, null);
            Assert.True(p.IsConfigured);
            Assert.Equal(1, p.EnrolledStationCount);

            var outcome = p.Validate("  Reg-PC-01 ", SecretA, out var id);

            Assert.Equal(StationAuthOutcome.Authenticated, outcome);
            Assert.Equal("REG-PC-01", id);
        }

        [Fact]
        public void Wrong_secret_for_an_enrolled_station_is_rejected()
        {
            var p = new StationCredentialPolicy(new[] { Line("REG-01", SecretA) }, null);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-01", SecretB, out var id));
            Assert.Equal("", id);
        }

        [Fact]
        public void One_stations_secret_does_not_open_another_station()
        {
            var p = new StationCredentialPolicy(new[] { Line("REG-01", SecretA), Line("REG-02", SecretB) }, null);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-02", SecretA, out _));
            Assert.Equal(StationAuthOutcome.Authenticated, p.Validate("REG-02", SecretB, out _));
        }

        [Fact]
        public void Unknown_station_without_a_shared_secret_is_rejected_even_with_a_secret_that_exists_elsewhere()
        {
            var p = new StationCredentialPolicy(new[] { Line("REG-01", SecretA) }, null);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-99", SecretA, out _));
        }

        [Fact]
        public void Shared_secret_is_accepted_only_for_stations_that_have_no_individual_line()
        {
            var p = new StationCredentialPolicy(new[] { Line("REG-01", SecretA) }, SecretB);

            // REG-01 is enrolled individually: the shared secret must NOT override its own line.
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-01", SecretB, out _));
            Assert.Equal(StationAuthOutcome.Authenticated, p.Validate("REG-01", SecretA, out _));

            // REG-77 has no line: the shared secret works, the other station's secret does not.
            Assert.Equal(StationAuthOutcome.Authenticated, p.Validate("REG-77", SecretB, out var id));
            Assert.Equal("REG-77", id);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-77", SecretA, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("has space")]
        [InlineData("semi;colon")]
        [InlineData("new\nline")]
        [InlineData("slash/slash")]
        [InlineData("ünicode")]
        public void Malformed_station_ids_never_authenticate(string? id)
        {
            var p = new StationCredentialPolicy(new[] { Line("REG-01", SecretA) }, SecretA);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate(id, SecretA, out var normalized));
            Assert.Equal("", normalized);
        }

        [Fact]
        public void Station_id_longer_than_64_characters_is_rejected()
        {
            var p = new StationCredentialPolicy(null, SecretA);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate(new string('A', 65), SecretA, out _));
            Assert.Equal(StationAuthOutcome.Authenticated, p.Validate(new string('A', 64), SecretA, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Empty_secret_never_authenticates(string? secret)
        {
            var p = new StationCredentialPolicy(new[] { Line("REG-01", SecretA) }, SecretA);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-01", secret, out _));
        }

        [Fact]
        public void Secret_over_256_characters_is_rejected_before_it_is_hashed()
        {
            var big = new string('x', 257);
            var p = new StationCredentialPolicy(new[] { Line("REG-01", big) }, null);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-01", big, out _));
        }

        [Fact]
        public void Malformed_enrolment_lines_are_ignored_and_named_but_never_become_a_valid_login()
        {
            var p = new StationCredentialPolicy(new KeyValuePair<string, string?>[]
            {
                new("REG-01", "not-hex"),
                new("bad id with spaces", StationCredentialPolicy.ComputeSha256Hex(SecretA)),
                new("REG-03", null),
                Line("REG-OK", SecretA)
            }, null);

            Assert.Equal(1, p.EnrolledStationCount);
            Assert.Equal(3, p.RejectedEntries.Count);
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-01", "not-hex", out _));
            Assert.Equal(StationAuthOutcome.Authenticated, p.Validate("REG-OK", SecretA, out _));
            // The log text names the entry and the reason, never a hash or a secret.
            Assert.All(p.RejectedEntries, e => Assert.DoesNotContain(SecretA, e));
        }

        [Fact]
        public void Only_malformed_lines_means_not_configured()
        {
            var p = new StationCredentialPolicy(new KeyValuePair<string, string?>[] { new("REG-01", "zzz") }, null);
            Assert.False(p.IsConfigured);
            Assert.Equal(StationAuthOutcome.NotConfigured, p.Validate("REG-01", SecretA, out _));
        }

        // ── Settings loader ──────────────────────────────────────────────────────

        private static IConfiguration Config(params (string key, string? value)[] items) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(items.Select(i => new KeyValuePair<string, string?>(i.key, i.value)))
                .Build();

        [Fact]
        public void Loader_reads_per_station_lines_from_the_configuration_section()
        {
            var cfg = Config(("Security:RegistrationStations:REG-01", StationCredentialPolicy.ComputeSha256Hex(SecretA)));
            var p = StationCredentialSettings.Load(cfg, _ => null);
            Assert.Equal(StationAuthOutcome.Authenticated, p.Validate("reg-01", SecretA, out _));
        }

        [Fact]
        public void Loader_prefers_the_environment_json_over_appsettings()
        {
            var cfg = Config(("Security:RegistrationStations:REG-01", StationCredentialPolicy.ComputeSha256Hex(SecretA)));
            var json = "{\"REG-02\":\"" + StationCredentialPolicy.ComputeSha256Hex(SecretB) + "\"}";
            var p = StationCredentialSettings.Load(cfg, name => name == StationCredentialSettings.StationsEnvVar ? json : null);

            Assert.Equal(StationAuthOutcome.Authenticated, p.Validate("REG-02", SecretB, out _));
            Assert.Equal(StationAuthOutcome.InvalidCredentials, p.Validate("REG-01", SecretA, out _));
        }

        [Fact]
        public void Loader_reads_the_shared_secret_from_environment_first_then_configuration()
        {
            var cfg = Config(("Security:RegistrationStationSecret", SecretA));
            Assert.Equal(StationAuthOutcome.Authenticated,
                StationCredentialSettings.Load(cfg, _ => null).Validate("ANY-PC", SecretA, out _));

            var env = StationCredentialSettings.Load(cfg, n => n == StationCredentialSettings.SharedSecretEnvVar ? SecretB : null);
            Assert.Equal(StationAuthOutcome.Authenticated, env.Validate("ANY-PC", SecretB, out _));
            Assert.Equal(StationAuthOutcome.InvalidCredentials, env.Validate("ANY-PC", SecretA, out _));
        }

        [Fact]
        public void Loader_treats_malformed_environment_json_as_fail_closed()
        {
            var p = StationCredentialSettings.Load(Config(), n => n == StationCredentialSettings.StationsEnvVar ? "{not json" : null);
            Assert.False(p.IsConfigured);
            Assert.NotEmpty(p.RejectedEntries);
        }

        [Fact]
        public void Loader_refuses_template_placeholder_text_like_every_other_secret_setting()
        {
            var cfg = Config(("Security:RegistrationStationSecret", "<set via EVOTING_REGISTRATION_STATION_SECRET>"));
            Assert.Throws<InvalidOperationException>(() => StationCredentialSettings.Load(cfg, _ => null));
        }

        [Fact]
        public void Nothing_in_environment_or_configuration_is_not_configured()
        {
            var p = StationCredentialSettings.Load(Config(), _ => null);
            Assert.False(p.IsConfigured);
        }
    }
}
