using System.Text.Json;
using System.Text.Json.Serialization;
using EVoting.Api.Models.Requests;
using EVoting.Api.Models.Responses;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// Wire-format (JSON) compatibility tests. They use the SAME serializer settings Program.cs configures for the API
    /// (camelCase, nulls omitted) and prove that:
    ///   * requests/responses that the deployed Kiosk and Registration clients already send/read still round-trip, and
    ///   * the additive members introduced for the modernization are optional (old callers are unaffected).
    /// </summary>
    public class ApiDtoContractTests
    {
        private static readonly JsonSerializerOptions Api = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };

        // ── POST /api/v1/votes ───────────────────────────────────────────────────

        [Fact]
        public void Vote_request_from_a_kiosk_that_predates_the_receipt_code_still_deserializes()
        {
            const string legacyKioskBody =
                "{\"eventId\":3,\"selections\":[{\"candidacyId\":11,\"position\":\"Chairperson\"}]}";

            var req = JsonSerializer.Deserialize<SubmitVoteRequest>(legacyKioskBody, Api)!;

            Assert.Equal(3, req.EventId);
            Assert.Single(req.Selections);
            Assert.Equal(11, req.Selections[0].CandidacyId);
            Assert.Equal("Chairperson", req.Selections[0].Position);
            Assert.Null(req.ReceiptCode);   // => the server takes the original, unchanged code path
        }

        [Fact]
        public void Vote_request_may_carry_a_client_receipt_code()
        {
            const string body =
                "{\"eventId\":3,\"selections\":[{\"candidacyId\":11,\"position\":\"Chairperson\"}]," +
                "\"receiptCode\":\"EVT03-A1B2C3D4-E5F6A7B8\"}";

            var req = JsonSerializer.Deserialize<SubmitVoteRequest>(body, Api)!;

            Assert.Equal("EVT03-A1B2C3D4-E5F6A7B8", req.ReceiptCode);
        }

        [Fact]
        public void Vote_response_keeps_every_field_the_kiosk_reads_and_adds_alreadyProcessed()
        {
            var json = JsonSerializer.Serialize(new VoteSubmitResponse
            {
                Message = "ok",
                ReceiptCode = "EVT03-A1B2C3D4-E5F6A7B8",
                VotedAt = new DateTime(2026, 5, 1, 1, 2, 3, DateTimeKind.Utc)
            }, Api);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // What KioskApiClient.VoteSubmitDto reads today:
            Assert.Equal("EVT03-A1B2C3D4-E5F6A7B8", root.GetProperty("receiptCode").GetString());
            Assert.Equal("ok", root.GetProperty("message").GetString());
            Assert.True(root.TryGetProperty("votedAt", out _));
            // Additive:
            Assert.False(root.GetProperty("alreadyProcessed").GetBoolean());
        }

        // ── POST /api/v1/auth/registration-station-login ──────────────────────────

        [Fact]
        public void Station_login_request_uses_camelCase_stationId_and_stationSecret()
        {
            var req = JsonSerializer.Deserialize<RegistrationStationLoginRequest>(
                "{\"stationId\":\"REG-PC-01\",\"stationSecret\":\"s3cret\"}", Api)!;

            Assert.Equal("REG-PC-01", req.StationId);
            Assert.Equal("s3cret", req.StationSecret);
        }

        [Fact]
        public void Station_login_answers_with_the_same_TokenResponse_shape_as_the_other_logins()
        {
            var json = JsonSerializer.Serialize(
                new TokenResponse { AccessToken = "jwt", ExpiresIn = 28800, Role = "Registration" }, Api);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("jwt", root.GetProperty("accessToken").GetString());
            Assert.Equal("Bearer", root.GetProperty("tokenType").GetString());
            Assert.Equal(28800, root.GetProperty("expiresIn").GetInt32());
            Assert.Equal("Registration", root.GetProperty("role").GetString());
        }

        [Fact]
        public void Error_body_carries_a_machine_readable_code_the_station_client_can_branch_on()
        {
            var json = JsonSerializer.Serialize(
                new ApiError { Error = "Invalid station credentials.", Code = "INVALID_STATION_CREDENTIALS" }, Api);

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("INVALID_STATION_CREDENTIALS", doc.RootElement.GetProperty("code").GetString());
            Assert.False(doc.RootElement.TryGetProperty("detail", out _));   // nulls are omitted
        }

        // ── GET /api/v1/registration/* (what RegistrationApiClient deserializes) ──

        [Fact]
        public void Registration_member_response_exposes_every_field_the_station_needs_including_ineligible_members()
        {
            var json = JsonSerializer.Serialize(new RegistrationMemberResponse
            {
                MemberId = 7,
                AccountCode = "00123",
                MemberName = "DELA CRUZ, JUAN",
                MemberContactNumber = "09170000000",
                MemberAddress = "Somewhere",
                BranchId = 2,
                Branch = "Main",
                ShareCapital = 2500m,
                SavingsDeposit = 100m,
                LoanStatus = 1,
                IsEligibleToVote = false,
                VotingRemarks = "Share capital below the minimum",
                QrIssuedAt = null,
                IsActive = true
            }, Api);

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            foreach (var name in new[]
            {
                "memberId", "accountCode", "memberName", "memberContactNumber", "memberAddress", "branchId", "branch",
                "shareCapital", "savingsDeposit", "loanStatus", "isEligibleToVote", "votingRemarks", "isActive"
            })
                Assert.True(r.TryGetProperty(name, out _), "missing property: " + name);

            Assert.False(r.GetProperty("isEligibleToVote").GetBoolean());
            Assert.Equal("Share capital below the minimum", r.GetProperty("votingRemarks").GetString());
            Assert.False(r.TryGetProperty("qrIssuedAt", out _));   // null => omitted => client must treat "absent" as "not issued"
        }

        [Fact]
        public void Qr_bulk_request_and_response_shapes_are_unchanged()
        {
            var req = JsonSerializer.Deserialize<QrIssuanceBulkRequest>(
                "{\"items\":[{\"memberId\":5,\"issuedAtUtc\":\"2026-05-01T01:02:03Z\"}]}", Api)!;
            Assert.Single(req.Items);
            Assert.Equal(5, req.Items[0].MemberId);
            Assert.Equal(DateTimeKind.Utc, req.Items[0].IssuedAtUtc.Kind);

            var resp = JsonSerializer.Serialize(
                new QrIssuanceBulkResponse { SucceededMemberIds = new() { 5 }, FailedMemberIds = new() { 6 } }, Api);
            using var doc = JsonDocument.Parse(resp);
            Assert.Equal(5, doc.RootElement.GetProperty("succeededMemberIds")[0].GetInt32());
            Assert.Equal(6, doc.RootElement.GetProperty("failedMemberIds")[0].GetInt32());
        }

        // ── Offline sync (Kiosk) – unchanged by this work, pinned so it stays that way ──

        [Fact]
        public void Offline_sync_bulk_item_result_keeps_the_permanent_rejection_flag()
        {
            var json = JsonSerializer.Serialize(new OfflineVoteSyncBulkResponse
            {
                Results = new()
                {
                    new OfflineVoteSyncBulkItemResult { ReceiptCode = "R1", Success = false, IsPermanentRejection = true, Message = "no" }
                }
            }, Api);

            using var doc = JsonDocument.Parse(json);
            var item = doc.RootElement.GetProperty("results")[0];
            Assert.Equal("R1", item.GetProperty("receiptCode").GetString());
            Assert.True(item.GetProperty("isPermanentRejection").GetBoolean());
            Assert.False(item.GetProperty("success").GetBoolean());
            Assert.False(item.GetProperty("alreadyProcessed").GetBoolean());
        }
    }
}
