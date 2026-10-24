using System.ComponentModel.DataAnnotations;

namespace EVoting.Api.Models.Requests
{
    // ── Auth ──────────────────────────────────────────────────────────────────
    public class AdminLoginRequest
    {
        [Required] public string Username { get; set; } = "";
        [Required] public string Password { get; set; } = "";
    }

    public class KioskLoginRequest
    {
        [Required] public string KioskId { get; set; } = "";
        [Required] public string DeviceSecret { get; set; } = "";
    }

    public class VoterVerifyRequest
    {
        [Required] public string AccountCode { get; set; } = "";
    }

    // ── Votes ─────────────────────────────────────────────────────────────────
    public class VoteSelectionItem
    {
        [Required] public int CandidacyId { get; set; }
        [Required] public string Position { get; set; } = "";
    }

    public class SubmitVoteRequest
    {
        [Required] public int EventId { get; set; }
        // MaxLength(50) guards against a malicious client sending thousands of candidacy entries
        // in one request, which would cause a large INSERT loop inside the DB transaction.
        // 50 is well above any realistic ballot (max ~15 Board of Directors seats).
        [Required, MinLength(1), MaxLength(50)] public List<VoteSelectionItem> Selections { get; set; } = new();
    }

    /// <summary>
    /// A vote that was cast on a kiosk while it had no connectivity, submitted
    /// later under the kiosk's own (long-lived) token once it reconnects.
    /// Unlike SubmitVoteRequest, MemberId travels explicitly in the body — there
    /// is no fresh Voter JWT to read it from by the time this runs — so
    /// VoteService.CastOfflineVoteAsync independently re-checks eligibility
    /// instead of trusting the caller.
    /// </summary>
    public class OfflineVoteSyncRequest
    {
        [Required] public int MemberId { get; set; }
        [Required] public int EventId { get; set; }
        [Required, MinLength(1), MaxLength(50)] public List<VoteSelectionItem> Selections { get; set; } = new();
        /// <summary>The receipt code the kiosk already generated and gave the voter at the moment of voting.</summary>
        [Required, MaxLength(30)] public string ReceiptCode { get; set; } = "";
        /// <summary>When the voter actually cast this vote (UTC), not when it happened to sync.</summary>
        [Required] public DateTime CastAtUtc { get; set; }
    }

    /// <summary>
    /// Many offline votes synced in a single HTTP round-trip instead of one
    /// call per vote — see VotesController.SyncOfflineVotesBulk. 500 is well
    /// above the kiosk's own chunk size (200 — see VoteSyncBackgroundService),
    /// so a well-behaved caller never hits this; it exists as defense in depth
    /// against a malformed or hostile request forcing a huge single batch.
    /// </summary>
    public class OfflineVoteSyncBulkRequest
    {
        [Required, MinLength(1), MaxLength(500)]
        public List<OfflineVoteSyncRequest> Votes { get; set; } = new();
    }

    // ── Events ────────────────────────────────────────────────────────────────
    public class CreateEventRequest
    {
        [Required, MaxLength(100)] public string Title { get; set; } = "";
        [Required, MaxLength(150)] public string Venue { get; set; } = "";
        [Required] public DateTime ScheduleFrom { get; set; }
        [Required] public DateTime ScheduleTo { get; set; }
        public List<int> BranchIds { get; set; } = new();
    }

    public class UpdateEventStatusRequest
    {
        [Required, RegularExpression("^(Opened|Closed)$", ErrorMessage = "Status must be 'Opened' or 'Closed'")]
        public string Status { get; set; } = "";
    }

    // ── Candidates ────────────────────────────────────────────────────────────
    public class UpdateCandidacyStatusRequest
    {
        [Required, RegularExpression("^(Approved|Rejected|Pending)$")]
        public string Status { get; set; } = "";
        public string? RejectionReason { get; set; }
    }

    // ── Branches ──────────────────────────────────────────────────────────────
    // Same shape for create and update — Admin's BranchEditor.cs collects the
    // same three fields for both, so one request type covers both endpoints.
    public class BranchRequest
    {
        [Required, MaxLength(50)] public string BranchName { get; set; } = "";
        [Required, MaxLength(50)] public string BranchAddress { get; set; } = "";
        [MaxLength(15)] public string? BranchContactNumber { get; set; }

        /// <summary>Optional optimistic-concurrency token: the UpdatedAt the client loaded. Mismatch ⇒ 409.</summary>
        public DateTime? ExpectedUpdatedAt { get; set; }
    }
}

namespace EVoting.Api.Models.Responses
{
    // ── Auth ──────────────────────────────────────────────────────────────────
    public class TokenResponse
    {
        public string AccessToken { get; set; } = "";
        public string TokenType { get; set; } = "Bearer";
        public int ExpiresIn { get; set; }
        public string Role { get; set; } = "";
    }

    public class VoterVerifyResponse
    {
        public string AccessToken { get; set; } = "";
        public string TokenType { get; set; } = "Bearer";
        public int ExpiresIn { get; set; }
        public VoterInfo Member { get; set; } = new();
        public EventInfo Event { get; set; } = new();
    }

    public class VoterInfo
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Branch { get; set; } = "";
        // FIX (HIGH): BranchId was missing — KioskApiClient needs it to pass the
        // MemberVerifyViewModel branch check. Without it, BranchId is always null
        // and every voter gets "branch not assigned" even when the API already
        // confirmed the branch is valid server-side.
        public int BranchId { get; set; }
        public string AccountCode { get; set; } = "";
    }

    public class EventInfo
    {
        public int EventId { get; set; }
        public string Title { get; set; } = "";
        public string Venue { get; set; } = "";
        public DateTime ScheduleFrom { get; set; }
        public DateTime ScheduleTo { get; set; }
        public string Status { get; set; } = "";
    }

    // ── Votes ─────────────────────────────────────────────────────────────────
    public class VoteSubmitResponse
    {
        public string Message { get; set; } = "";
        public string ReceiptCode { get; set; } = "";
        public DateTime VotedAt { get; set; }
    }

    public class OfflineVoteSyncResponse
    {
        public string Message { get; set; } = "";
        public string ReceiptCode { get; set; } = "";
        /// <summary>True if this exact vote was already on file (a retried sync) — nothing new was recorded.</summary>
        public bool AlreadyProcessed { get; set; }
    }

    public class OfflineVoteSyncBulkResponse
    {
        public List<OfflineVoteSyncBulkItemResult> Results { get; set; } = new();
    }

    public class OfflineVoteSyncBulkItemResult
    {
        /// <summary>Echoes the item's own receiptCode so the kiosk can map each result back to its local PendingVote row.</summary>
        public string ReceiptCode { get; set; } = "";
        public bool Success { get; set; }
        /// <summary>
        /// True only for a genuine, permanent business rejection (already
        /// voted, member not found/inactive, not eligible, branch not
        /// assigned) that will fail identically no matter how many times
        /// it's retried. False for anything else — including an unexpected
        /// server/infrastructure error — so the kiosk knows to leave that
        /// vote Pending and retry it, instead of discarding a legitimately
        /// cast ballot over what might be a one-off hiccup.
        /// </summary>
        public bool IsPermanentRejection { get; set; }
        /// <summary>True if this exact vote was already on file (a retried sync) — nothing new was recorded.</summary>
        public bool AlreadyProcessed { get; set; }
        public string Message { get; set; } = "";
    }

    public class ReceiptVerifyResponse
    {
        public bool Found { get; set; }
        public string ReceiptCode { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime? VotedAt { get; set; }
    }

    // ── Events ────────────────────────────────────────────────────────────────
    public class EventResponse
    {
        public int EventId { get; set; }
        public string Title { get; set; } = "";
        public string Venue { get; set; } = "";
        public DateTime ScheduleFrom { get; set; }
        public DateTime ScheduleTo { get; set; }
        public string Status { get; set; } = "";
        public bool IsActive { get; set; }
        public List<BranchResponse> Branches { get; set; } = new();
    }

    // ── Candidates ────────────────────────────────────────────────────────────
    public class CandidateResponse
    {
        public int CandidacyId { get; set; }
        public string CandidateName { get; set; } = "";
        public string Position { get; set; } = "";
        public string Status { get; set; } = "";
        public string? PreviousPositions { get; set; }
        public string? Gender { get; set; }
        public string? HighestEducation { get; set; }
        public string? Reason { get; set; }
        public string? PhotoBase64 { get; set; }
    }

    // ── Members ───────────────────────────────────────────────────────────────
    public class MemberResponse
    {
        public int MemberId { get; set; }
        public string AccountCode { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string Branch { get; set; } = "";
        public bool IsEligible { get; set; }
        public string? Remarks { get; set; }
        public bool HasVoted { get; set; }
    }

    public class RegistrationMemberResponse
    {
        public int MemberId { get; set; }
        public string AccountCode { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string? MemberContactNumber { get; set; }
        public string? MemberAddress { get; set; }
        public int BranchId { get; set; }
        public string Branch { get; set; } = "";
        public decimal ShareCapital { get; set; }
        public decimal SavingsDeposit { get; set; }
        public int LoanStatus { get; set; }
        public bool IsEligibleToVote { get; set; }
        public string? VotingRemarks { get; set; }
        public DateTime? QrIssuedAt { get; set; }
        public bool IsActive { get; set; }
    }

    // ── Registration offline QR-issuance sync ───────────────────────────────
    // FEATURE (REGISTRATION OFFLINE SUPPORT): a Registration Station queues
    // QR issuances locally while it can't reach this API (see
    // EVoting.Registration's PendingQrIssuance) and bulk-sends them here once
    // reconnected. See RegistrationController.MarkQrIssuedBulk for why this
    // is a plain idempotent overwrite rather than a vote-style
    // unique-constraint-guarded insert.
    public class QrIssuanceBulkItemRequest
    {
        public int MemberId { get; set; }
        /// <summary>When the QR was actually generated on the station — not when this sync request happens to arrive.</summary>
        public DateTime IssuedAtUtc { get; set; }
    }

    public class QrIssuanceBulkRequest
    {
        public List<QrIssuanceBulkItemRequest> Items { get; set; } = new();
    }

    public class QrIssuanceBulkResponse
    {
        public List<int> SucceededMemberIds { get; set; } = new();
        /// <summary>MemberId wasn't found (e.g. deactivated/removed since the station cached its roster) — not retried.</summary>
        public List<int> FailedMemberIds { get; set; } = new();
    }

    /// <summary>
    /// Offline verification snapshot: member roster with precomputed eligibility for a specific event.
    /// Deliberately excludes HasVoted status — the kiosk doesn't need to know vote history when verifying
    /// initial identity; it only needs MemberId and eligibility to decide if the ballot page should load.
    /// </summary>
    public class MemberSnapshotRow
    {
        public int MemberId { get; set; }
        public string AccountCode { get; set; } = "";
        public string MemberName { get; set; } = "";
        public int BranchId { get; set; }
        public string BranchName { get; set; } = "";
        public bool IsEligible { get; set; }
        public string? PhotoBase64 { get; set; }
    }

    // ── Branches ─────────────────────────────────────────────────────────────
    public class BranchResponse
    {
        public int BranchId { get; set; }
        public string BranchName { get; set; } = "";
        public string BranchAddress { get; set; } = "";
        public string? BranchContactNumber { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>GetById/Create/Update shape — adds UpdatedAt and a computed
    /// HasActiveUsers so BranchEditor's delete pre-check can reuse this call
    /// instead of needing a sixth endpoint.</summary>
    public class BranchDetailResponse
    {
        public int BranchId { get; set; }
        public string BranchName { get; set; } = "";
        public string BranchAddress { get; set; } = "";
        public string? BranchContactNumber { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool HasActiveUsers { get; set; }
    }

    public class NameCheckResponse
    {
        public bool Taken { get; set; }
    }

    // ── Results ───────────────────────────────────────────────────────────────
    public class VoteResultResponse
    {
        public string Position { get; set; } = "";
        public List<CandidateResult> Candidates { get; set; } = new();
    }

    public class CandidateResult
    {
        public int CandidacyId { get; set; }
        public string CandidateName { get; set; } = "";
        public int VoteCount { get; set; }
        public double Percentage { get; set; }
    }

    // ── Turnout ───────────────────────────────────────────────────────────────
    public class TurnoutResponse
    {
        public int EventId { get; set; }
        public int TotalEligible { get; set; }
        public int TotalVoted { get; set; }
        public double TurnoutPercent { get; set; }
    }

    // ── API Error ─────────────────────────────────────────────────────────────
    public class ApiError
    {
        public string Error { get; set; } = "";
        public string? Detail { get; set; }

        /// <summary>Machine-readable reason (e.g. EVENT_OPEN_LOCK, CONCURRENCY, DUPLICATE, FORBIDDEN, VALIDATION). Optional.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Code { get; set; }

        /// <summary>Per-field validation messages keyed by camelCase field name. Optional.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, string>? Fields { get; set; }
    }
}
