using EVoting.Api.Services;
using Isopoh.Cryptography.Argon2;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// PasswordService hashes and verifies with Isopoh.Cryptography.Argon2 1.0.x. That library's one-line helpers (Argon2.Hash / Argon2.Verify) never dispose
    /// the 64 MB work area they allocate, so every sign-in and every imported account used to keep 64 MB for the life of the process. These tests pin down
    /// (1) that the service releases its memory, and (2) that it still reads and writes exactly the hashes the library's helpers (and the legacy Admin) do.
    /// </summary>
    public class PasswordServiceTests
    {
        // The service prefers the EVOTING_PEPPER environment variable over configuration, so the tests use whichever one it will use.
        private static readonly string Pepper = Environment.GetEnvironmentVariable("EVOTING_PEPPER") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : "unit-test-pepper-not-a-secret";

        private static PasswordService Create() => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Security:PasswordPepper"] = Pepper })
            .Build());

        private static long HeapMegabytes()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            return GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);
        }

        [Fact]
        public void The_right_password_is_accepted_and_a_wrong_one_rejected()
        {
            var svc = Create();
            var stored = svc.Hash("Correct#Horse1");
            Assert.True(svc.Verify("Correct#Horse1", stored));
            Assert.False(svc.Verify("Correct#Horse2", stored));
        }

        [Fact]
        public void A_hash_keeps_the_legacy_parameters_and_text_format()
        {
            var parts = Create().Hash("Correct#Horse1").Split('$');
            // "" | argon2i | v=19 | m=65536,t=3,p=4 | salt (16 bytes, base64) | hash (32 bytes, base64)
            Assert.Equal(6, parts.Length);
            Assert.Equal("argon2i", parts[1]);
            Assert.Equal("v=19", parts[2]);
            Assert.Equal("m=65536,t=3,p=4", parts[3]);
            Assert.Equal(24, parts[4].Length);
            Assert.Equal(44, parts[5].Length);
        }

        [Fact]
        public void Every_hash_has_a_new_salt()
        {
            var svc = Create();
            Assert.NotEqual(svc.Hash("same password"), svc.Hash("same password"));
        }

        [Fact]
        public void A_hash_made_by_the_library_helper_still_verifies()
        {
            // every stored password was made by Argon2.Hash(password + pepper, 3, 65536, 4, Argon2i, 32)
            var stored = Argon2.Hash("Correct#Horse1" + Pepper, 3, 65536, 4, Argon2Type.DataIndependentAddressing, 32);
            var svc = Create();
            Assert.True(svc.Verify("Correct#Horse1", stored));
            Assert.False(svc.Verify("Correct#Horse2", stored));
        }

        [Fact]
        public void A_hash_made_here_verifies_with_the_library_helper_the_legacy_Admin_uses()
        {
            var stored = Create().Hash("Correct#Horse1");
            Assert.True(Argon2.Verify(stored, "Correct#Horse1" + Pepper));
            Assert.False(Argon2.Verify(stored, "Correct#Horse2" + Pepper));
        }

        [Fact]
        public void A_hash_with_other_settings_is_verified_with_the_settings_in_its_own_text()
        {
            var stored = Argon2.Hash("Correct#Horse1" + Pepper, 2, 32768, 2, Argon2Type.DataDependentAddressing, 32);
            Assert.True(Create().Verify("Correct#Horse1", stored));
        }

        [Theory]
        [InlineData("")]
        [InlineData("x")]
        [InlineData("not a hash at all")]
        [InlineData("$argon2i$v=19$m=65536,t=3,p=4$")]
        [InlineData("$argon2i$v=19$m=65536,t=3,p=4$AAAA$BBBB")]
        [InlineData("$argon2i$v=19$m=notanumber,t=3,p=4$c29tZXNhbHRzb21lc2FsdA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
        public void A_value_that_is_not_an_Argon2_hash_is_rejected_without_throwing(string stored)
        {
            Assert.False(Create().Verify("Correct#Horse1", stored));
        }

        [Fact]
        public void An_empty_password_is_rejected_and_cannot_be_hashed()
        {
            var svc = Create();
            var stored = svc.Hash("Correct#Horse1");
            Assert.False(svc.Verify("", stored));
            Assert.Throws<ArgumentException>(() => svc.Hash(""));
        }

        [Fact]
        public void Sign_in_checks_do_not_keep_their_64_MB_work_area()
        {
            var svc = Create();
            var stored = svc.Hash("Correct#Horse1");
            var before = HeapMegabytes();
            for (var i = 0; i < 6; i++) Assert.True(svc.Verify("Correct#Horse1", stored));
            var after = HeapMegabytes();
            // the leak was 64 MB per call (384 MB for six)
            Assert.True(after - before < 150, $"managed heap grew from {before} MB to {after} MB over 6 sign-in checks");
        }

        [Fact]
        public void Hashing_does_not_keep_the_64_MB_work_area()
        {
            var svc = Create();
            svc.Hash("warm up");
            var before = HeapMegabytes();
            for (var i = 0; i < 6; i++) svc.Hash("Correct#Horse" + i);
            var after = HeapMegabytes();
            Assert.True(after - before < 150, $"managed heap grew from {before} MB to {after} MB over 6 hashes");
        }
    }
}
