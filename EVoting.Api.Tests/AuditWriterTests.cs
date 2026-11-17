using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// AuditLogs.Details and AuditLogs.IpAddress are NOT NULL in the database (Username too). The election scheduler writes audit rows with no HTTP request, so it
    /// has no IP address; a NULL there made SaveChanges fail on every 10-second tick, which also lost the event status change saved in the same call
    /// (the scheduled open / close never happened). AuditWriter.Create is the single place that builds these rows.
    /// </summary>
    public class AuditWriterTests
    {
        [Fact]
        public void A_row_written_without_an_http_request_has_empty_strings_not_nulls()
        {
            var row = AuditWriter.Create(AuditActions.OpenEvent, AuditEntities.Event, 7, details: null, userId: null, username: null, ip: null);

            Assert.Equal("", row.IpAddress);
            Assert.Equal("", row.Details);
            Assert.Equal(AuditWriter.SystemUser, row.Username);
            Assert.Equal(AuditActions.OpenEvent, row.Action);
            Assert.Equal(AuditEntities.Event, row.EntityType);
            Assert.Equal(7, row.EntityId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_or_blank_user_is_recorded_as_SYSTEM(string? username)
        {
            var row = AuditWriter.Create(AuditActions.CloseEvent, AuditEntities.Event, 1, "x", null, username, "10.0.0.1");
            Assert.Equal("SYSTEM", row.Username);
        }

        [Fact]
        public void Given_values_are_kept()
        {
            var row = AuditWriter.Create(AuditActions.UpdateEligibilitySettings, AuditEntities.Settings, null, "Minimum share capital 3000 -> 3500", 12, "devadmin", "192.168.1.20");

            Assert.Equal("Minimum share capital 3000 -> 3500", row.Details);
            Assert.Equal("devadmin", row.Username);
            Assert.Equal(12, row.UserId);
            Assert.Equal("192.168.1.20", row.IpAddress);
            Assert.Null(row.EntityId);
        }

        [Fact]
        public void Values_longer_than_the_columns_are_clipped_so_the_insert_cannot_fail()
        {
            var longText = new string('x', 5000);
            var row = AuditWriter.Create(longText, longText, 1, longText, 1, longText, longText);

            Assert.Equal(50, row.Action.Length);
            Assert.Equal(100, row.EntityType.Length);
            Assert.Equal(2000, row.Details!.Length);
            Assert.Equal(50, row.Username!.Length);
            Assert.Equal(45, row.IpAddress!.Length);          // the longest textual IPv6 address
        }

        [Fact]
        public void Every_audit_action_code_fits_the_action_column()
        {
            Assert.All(AuditActions.All, code =>
            {
                Assert.True(code.Length <= 50, $"'{code}' is longer than the Action column (50)");
                Assert.Equal(code.ToUpperInvariant(), code);
            });
            Assert.Equal(AuditActions.All.Length, AuditActions.All.Distinct().Count());
        }

        [Fact]
        public void The_time_is_utc_now_unless_given()
        {
            var fixedTime = new DateTime(2026, 11, 9, 16, 0, 0, DateTimeKind.Utc);
            Assert.Equal(fixedTime, AuditWriter.Create("A", "B", null, null, null, null, null, fixedTime).OccurredAtUtc);

            var before = DateTime.UtcNow;
            var stamped = AuditWriter.Create("A", "B", null, null, null, null, null).OccurredAtUtc;
            Assert.InRange(stamped, before, DateTime.UtcNow);
        }
    }
}
