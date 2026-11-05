using System.ComponentModel.DataAnnotations;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Services;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
//  Wire contracts of the Representative Election module and of the system settings introduced with it.
//  JSON is camelCase. Admin screens use EVoting.Api.Models.Admin; voters and kiosks use EVoting.Api.Models.Representative.
//  The WPF Admin keeps a copy of the Admin contracts, verified against this file by its reflection contract test.
//  Lists NEVER carry image bytes: photos have their own endpoints (thumbnail / full) and a version stamp.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

namespace EVoting.Api.Models.Admin
{
    // ── Event position rules (General Assembly and Representative) ───────────────────────────────────────────
    public class PositionRuleRow
    {
        /// <summary>Database id of an existing rule; null for a row the administrator just added.</summary>
        public int? Id { get; set; }
        [Required, MaxLength(FieldLimits.CandidatePosition)] public string Position { get; set; } = "";
        /// <summary>Minimum selections a voter must make at this position on one ballot.</summary>
        public int MinVotes { get; set; }
        /// <summary>Maximum selections a voter may make at this position on one ballot.</summary>
        public int MaxVotes { get; set; }
        /// <summary>Maximum candidates allowed to take part at this position in this event (NULL = no limit).</summary>
        public int? MaxCandidates { get; set; }
        /// <summary>Number of winning seats.</summary>
        public int TotalSeats { get; set; }
        /// <summary>Read-only. Representative: approved roster entries at this position. General Assembly: currently eligible approved candidacies.</summary>
        public int AssignedCandidates { get; set; }
    }

    public class PositionRulesResponse
    {
        public int EventId { get; set; }
        public string EventType { get; set; } = EventTypes.GeneralAssembly;
        /// <summary>False while the election is open, after ballots exist, or (Representative) once the roster is locked.</summary>
        public bool Editable { get; set; }
        public string? LockCode { get; set; }
        public string? LockReason { get; set; }
        /// <summary>True when the event has no saved rules yet and the built-in General Assembly defaults are being shown.</summary>
        public bool IsDefault { get; set; }
        public List<PositionRuleRow> Rules { get; set; } = new();
        /// <summary>Non-blocking remarks about the saved/loaded configuration (e.g. fewer seats than candidates allowed).</summary>
        public List<string> Warnings { get; set; } = new();
        /// <summary>Concurrency token: send it back as ExpectedUpdatedAt when saving.</summary>
        public DateTime UpdatedAt { get; set; }
        /// <summary>Positions an administrator may choose from (General Assembly: the fixed candidacy positions; Representative: free text).</summary>
        public List<string> SuggestedPositions { get; set; } = new();
        public bool CustomPositionsAllowed { get; set; }
    }

    public class PositionRulesSaveRequest
    {
        [Required, MinLength(1, ErrorMessage = "Add at least one position.")] public List<PositionRuleRow> Rules { get; set; } = new();
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    // ── Candidate profiles ───────────────────────────────────────────────────────────────────────────────────
    public class RepProfileRow
    {
        public int ProfileId { get; set; }
        public string CandidateCode { get; set; } = "";
        public string CandidateName { get; set; } = "";
        public int? MemberId { get; set; }
        public string? AccountCode { get; set; }
        public bool HasPhoto { get; set; }
        /// <summary>Photo version stamp (cache key); null when there is no photo.</summary>
        public DateTime? PhotoUpdatedAt { get; set; }
        public bool IsActive { get; set; }
        /// <summary>Number of events this profile is on the roster of.</summary>
        public int EventAssignments { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class RepProfileDetail : RepProfileRow
    {
        public string? CellNo { get; set; }
        public string? TelNo { get; set; }
        public string? CivilStatus { get; set; }
        public string? Gender { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? HighestEducation { get; set; }
        public string? PreviousPositions { get; set; }
        public string? SpecialSkills { get; set; }
    }

    public class RepProfileUpsertRequest
    {
        [Required, MaxLength(RepresentativeLimits.CandidateCode)] public string CandidateCode { get; set; } = "";
        /// <summary>Required unless AccountCode names an existing member (then the member's name is used). Checked by the service, not by model binding.</summary>
        [MaxLength(RepresentativeLimits.CandidateName)] public string CandidateName { get; set; } = "";
        [MaxLength(FieldLimits.AccountCode)] public string? AccountCode { get; set; }
        [MaxLength(FieldLimits.CellNo)] public string? CellNo { get; set; }
        [MaxLength(FieldLimits.CellNo)] public string? TelNo { get; set; }
        [MaxLength(20)] public string? CivilStatus { get; set; }
        [MaxLength(20)] public string? Gender { get; set; }
        public DateTime? DateOfBirth { get; set; }
        [MaxLength(FieldLimits.Education)] public string? HighestEducation { get; set; }
        [MaxLength(FieldLimits.LongText)] public string? PreviousPositions { get; set; }
        [MaxLength(FieldLimits.LongText)] public string? SpecialSkills { get; set; }
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    /// <summary>Image data is Base64 (the same convention as the General Assembly candidacy photo). Null/empty removes the photo.</summary>
    public class RepProfilePhotoRequest
    {
        public string? ImageBase64 { get; set; }
        /// <summary>Optional small version (≤ 48 KB) for kiosk lists; when omitted the full image is served for both sizes.</summary>
        public string? ThumbnailBase64 { get; set; }
    }

    // ── Event roster ─────────────────────────────────────────────────────────────────────────────────────────
    public class RepRosterRow
    {
        public int AssignmentId { get; set; }
        public int ProfileId { get; set; }
        public string CandidateCode { get; set; } = "";
        public string CandidateName { get; set; } = "";
        public string? AccountCode { get; set; }
        public string Position { get; set; } = "";
        public int? BallotNumber { get; set; }
        public string Status { get; set; } = "";
        public string? Reason { get; set; }
        public bool HasPhoto { get; set; }
        public DateTime? PhotoUpdatedAt { get; set; }
        public int? ImportBatchId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>One roster row with the full profile, for the export file and the detail pane.</summary>
    public class RepRosterExportRow : RepRosterRow
    {
        public string? CellNo { get; set; }
        public string? TelNo { get; set; }
        public string? CivilStatus { get; set; }
        public string? Gender { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public string? HighestEducation { get; set; }
        public string? PreviousPositions { get; set; }
        public string? SpecialSkills { get; set; }
    }

    public class RepRosterPositionSummary
    {
        public string Position { get; set; } = "";
        public int MinVotes { get; set; }
        public int MaxVotes { get; set; }
        public int TotalSeats { get; set; }
        public int? MaxCandidates { get; set; }
        public int Assigned { get; set; }
        /// <summary>MaxCandidates − Assigned (null when no limit is configured).</summary>
        public int? FreeSlots { get; set; }
    }

    public class RepRosterSummaryResponse
    {
        public int EventId { get; set; }
        public string EventTitle { get; set; } = "";
        public string EventType { get; set; } = EventTypes.Representative;
        public string EventStatus { get; set; } = "";
        public int TotalAssigned { get; set; }
        /// <summary>True while candidates can still be added or removed (event never opened, closed, no ballots).</summary>
        public bool CanEdit { get; set; }
        public string? LockCode { get; set; }
        public string? LockReason { get; set; }
        public DateTime? RosterLockedAt { get; set; }
        /// <summary>Content hash of everything that defines the ballot (roster + rules); changes when anything changes.</summary>
        public string RosterVersion { get; set; } = "";
        public List<RepRosterPositionSummary> Positions { get; set; } = new();
        /// <summary>Problems that would stop the election from opening (empty when it is ready).</summary>
        public List<string> ReadinessIssues { get; set; } = new();
        /// <summary>Remarks that do not stop the election from opening.</summary>
        public List<string> ReadinessWarnings { get; set; } = new();
    }

    /// <summary>Add one candidate to the roster: an existing profile (ProfileId or CandidateCode) or a new one (Profile).</summary>
    public class RepAssignRequest
    {
        public int? ProfileId { get; set; }
        [MaxLength(RepresentativeLimits.CandidateCode)] public string? CandidateCode { get; set; }
        /// <summary>Creates the profile when no existing profile matches CandidateCode.</summary>
        public RepProfileUpsertRequest? NewProfile { get; set; }
        [Required, MaxLength(FieldLimits.CandidatePosition)] public string Position { get; set; } = "";
        [MaxLength(RepresentativeLimits.Reason)] public string? Reason { get; set; }
        public int? BallotNumber { get; set; }
    }

    // ── Candidate import (client parses the spreadsheet; the server stages, validates and commits) ───────────
    public class RepImportBatchCreateRequest
    {
        [Required] public int EventId { get; set; }
        [Required, MaxLength(255)] public string FileName { get; set; } = "";
        [MaxLength(64)] public string? FileSha256 { get; set; }
        /// <summary>Number of data rows the client is going to send (checked again at validate time).</summary>
        [Range(1, int.MaxValue)] public int ExpectedRows { get; set; }
    }

    public class RepImportRowIn
    {
        /// <summary>1-based spreadsheet row number (used in error reports).</summary>
        [Range(1, int.MaxValue)] public int RowNumber { get; set; }
        [MaxLength(60)] public string? CandidateCode { get; set; }
        [MaxLength(300)] public string? CandidateName { get; set; }
        [MaxLength(40)] public string? AccountCode { get; set; }
        [MaxLength(150)] public string? Position { get; set; }
        [MaxLength(40)] public string? CellNo { get; set; }
        [MaxLength(40)] public string? TelNo { get; set; }
        [MaxLength(40)] public string? CivilStatus { get; set; }
        [MaxLength(40)] public string? Gender { get; set; }
        public DateTime? DateOfBirth { get; set; }
        /// <summary>The cell text when it could not be read as a date (reported as a row error).</summary>
        [MaxLength(60)] public string? DateOfBirthRaw { get; set; }
        [MaxLength(300)] public string? HighestEducation { get; set; }
        [MaxLength(700)] public string? PreviousPositions { get; set; }
        [MaxLength(700)] public string? SpecialSkills { get; set; }
        [MaxLength(2500)] public string? Reason { get; set; }
        [MaxLength(20)] public string? BallotNumber { get; set; }
    }

    public class RepImportStageRequest
    {
        [Required, MinLength(1)] public List<RepImportRowIn> Rows { get; set; } = new();
    }

    public class RepImportStageResponse
    {
        public int Accepted { get; set; }
        public int TotalStaged { get; set; }
    }

    public class RepImportBatchRow
    {
        public int BatchId { get; set; }
        public int EventId { get; set; }
        public string EventTitle { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "";
        public int ExpectedRows { get; set; }
        public int TotalRows { get; set; }
        public int ValidRows { get; set; }
        public int InvalidRows { get; set; }
        public int ConflictRows { get; set; }
        public int NewProfiles { get; set; }
        public int ReusedProfiles { get; set; }
        public int NewAssignments { get; set; }
        public string? Message { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ValidatedAt { get; set; }
        public DateTime? CommittedAt { get; set; }
        /// <summary>Concurrency token for resolve/commit.</summary>
        public DateTime UpdatedAt { get; set; }
        public bool CanValidate { get; set; }
        public bool CanCommit { get; set; }
        public bool CanCancel { get; set; }
    }

    public class RepImportPreviewRow
    {
        public int RowNumber { get; set; }
        public string? CandidateCode { get; set; }
        public string? CandidateName { get; set; }
        public string? AccountCode { get; set; }
        public string? Position { get; set; }
        public string RowStatus { get; set; } = "";
        public string? PlannedAction { get; set; }
        public string? ErrorCodes { get; set; }
        public string? ErrorMessage { get; set; }
        public string? Resolution { get; set; }
        public int? MatchedProfileId { get; set; }
        public string? MatchedProfileName { get; set; }
    }

    public class RepImportResolution
    {
        [Range(1, int.MaxValue)] public int RowNumber { get; set; }
        /// <summary>USE_EXISTING (keep the stored profile, only assign it), UPDATE_PROFILE (overwrite it with the file's values) or SKIP.</summary>
        [Required, MaxLength(20)] public string Resolution { get; set; } = "";
    }

    public class RepImportResolveRequest
    {
        public List<RepImportResolution> Items { get; set; } = new();
        /// <summary>Applied to every conflict row without its own entry (optional).</summary>
        [MaxLength(20)] public string? DefaultResolution { get; set; }
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    public class RepImportCommitRequest
    {
        /// <summary>Must be true: the administrator has reviewed the preview and explicitly confirms.</summary>
        public bool Confirm { get; set; }
        /// <summary>When true the commit is refused unless every row of the file is valid (all-or-nothing for the whole file).</summary>
        public bool RequireAllValid { get; set; }
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    /// <summary>Unlocks the roster of a Representative election that was opened by mistake (closed, no ballots).</summary>
    public class RepRosterUnlockRequest
    {
        public bool Confirm { get; set; }
        [MaxLength(300)] public string? Reason { get; set; }
    }

    public class RepImportHistoryResponse
    {
        public List<RepImportBatchRow> Batches { get; set; } = new();
    }

    /// <summary>Activate / deactivate a candidate profile (profiles are never deleted).</summary>
    public class RepProfileActiveRequest
    {
        public bool IsActive { get; set; }
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    public class RepImportTemplateColumn
    {
        /// <summary>Header text in the Excel template (matched case-insensitively, ignoring spaces).</summary>
        public string Header { get; set; } = "";
        /// <summary>Name of the corresponding field in <see cref="RepImportRowIn"/>.</summary>
        public string Field { get; set; } = "";
        public bool Required { get; set; }
        public string Description { get; set; } = "";
        public string Example { get; set; } = "";
        /// <summary>Maximum length of the stored value (0 = not text).</summary>
        public int MaxLength { get; set; }
    }

    /// <summary>The server owns the file format: the Admin builds the downloadable template and reads uploaded files from this description.</summary>
    public class RepImportTemplateResponse
    {
        public List<RepImportTemplateColumn> Columns { get; set; } = new();
        public List<string> Notes { get; set; } = new();
        public int MaxRowsPerBatch { get; set; }
        public int MaxRowsPerRequest { get; set; }
        public int MaxOpenBatchesPerEvent { get; set; }
    }
}

namespace EVoting.Api.Models.Representative
{
    // ── Ballot (voters and kiosks) ───────────────────────────────────────────────────────────────────────────
    public class RepBallotPosition
    {
        public string Position { get; set; } = "";
        public int MinVotes { get; set; }
        public int MaxVotes { get; set; }
        public int TotalSeats { get; set; }
        public int CandidateCount { get; set; }
    }

    /// <summary>What a voter needs to start a Representative ballot. Candidates are fetched page by page.</summary>
    public class RepBallotResponse
    {
        public int EventId { get; set; }
        public string EventType { get; set; } = EventTypes.Representative;
        public string Title { get; set; } = "";
        public DateTime ScheduleFrom { get; set; }
        public DateTime ScheduleTo { get; set; }
        /// <summary>Opened / Closed, exactly as the event shows it.</summary>
        public string Status { get; set; } = "";
        /// <summary>True when a ballot submitted right now would be accepted by the schedule rules (Opened AND inside the window).</summary>
        public bool IsVotingOpen { get; set; }
        /// <summary>The server clock, so a kiosk can detect that its own clock is wrong.</summary>
        public DateTime ServerTimeUtc { get; set; }
        public string RosterVersion { get; set; } = "";
        /// <summary>Candidates on the ballot (approved, active) over all positions. A person running for two positions counts twice.</summary>
        public int TotalCandidates { get; set; }
        public List<RepBallotPosition> Positions { get; set; } = new();
    }

    /// <summary>The answer to an accepted ballot (online or synchronised offline). It never echoes the voter's selections.</summary>
    public class RepVoteReceiptResponse
    {
        public string Message { get; set; } = "";
        public int EventId { get; set; }
        public string ReceiptCode { get; set; } = "";
        public DateTime VotedAt { get; set; }
        public int SelectionCount { get; set; }
        /// <summary>True when this exact ballot (same receipt) was already on file: a safe retry; nothing new was recorded.</summary>
        public bool AlreadyProcessed { get; set; }
        /// <summary>Offline sync only: the kiosk's roster version differs from the current one, yet the ballot is still valid and was accepted.</summary>
        public bool RosterChanged { get; set; }
    }

    public class RepOfflineVoteItemResult
    {
        /// <summary>Echoes the item's own receipt code so the kiosk can map the result back to its local queue row.</summary>
        public string ReceiptCode { get; set; } = "";
        public bool Success { get; set; }
        public bool AlreadyProcessed { get; set; }
        /// <summary>True for a business rejection that will fail identically on every retry; false for a transient server problem (retry later).</summary>
        public bool IsPermanentRejection { get; set; }
        public string? Code { get; set; }
        public string Message { get; set; } = "";
        public bool RosterChanged { get; set; }
    }

    public class RepOfflineVoteSyncBulkResponse
    {
        public List<RepOfflineVoteItemResult> Results { get; set; } = new();
        public int Accepted { get; set; }
        public int Replayed { get; set; }
        public int Rejected { get; set; }
        public int RetryLater { get; set; }
    }

    /// <summary>A candidate as a voter or kiosk sees it. No contact data; no image bytes (use the photo endpoint).</summary>
    public class RepCandidateCard
    {
        /// <summary>RepresentativeEventCandidateId — the identifier a ballot submits.</summary>
        public int Id { get; set; }
        public string CandidateCode { get; set; } = "";
        public string CandidateName { get; set; } = "";
        public string Position { get; set; } = "";
        public int? BallotNumber { get; set; }
        public string? Reason { get; set; }
        public string? Gender { get; set; }
        public string? HighestEducation { get; set; }
        public string? PreviousPositions { get; set; }
        public string? SpecialSkills { get; set; }
        public bool HasPhoto { get; set; }
        /// <summary>Photo version stamp (UTC ticks) usable as a cache key.</summary>
        public long PhotoVersion { get; set; }
    }

    public class RepCandidatePage
    {
        public int EventId { get; set; }
        public string RosterVersion { get; set; } = "";
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
        public List<RepCandidateCard> Items { get; set; } = new();
    }

    /// <summary>Keyset-paged roster for kiosk synchronisation (stable under concurrent reads).</summary>
    public class RepRosterSyncPage
    {
        public int EventId { get; set; }
        public string EventType { get; set; } = EventTypes.Representative;
        public string RosterVersion { get; set; } = "";
        public int Total { get; set; }
        /// <summary>Pass back as afterId to read the next page; null when this is the last page.</summary>
        public int? NextAfterId { get; set; }
        public List<RepCandidateCard> Items { get; set; } = new();
        /// <summary>Included on the first page only.</summary>
        public List<RepBallotPosition>? Positions { get; set; }
    }

    // ── Casting ──────────────────────────────────────────────────────────────────────────────────────────────
    public class RepSubmitVoteRequest
    {
        [Required] public int EventId { get; set; }
        /// <summary>RepresentativeEventCandidateId values. The position of each is derived by the server, never taken from the client.</summary>
        [Required, MinLength(1), MaxLength(RepresentativeLimits.MaxSelectionsPerRequest)] public List<int> CandidateIds { get; set; } = new();
        /// <summary>Optional client-proposed receipt code (same format as the General Assembly receipt) that makes a retry idempotent.</summary>
        [MaxLength(30)] public string? ReceiptCode { get; set; }
        /// <summary>The roster version the voter saw; a mismatch is reported but never rejects a valid ballot.</summary>
        [MaxLength(64)] public string? RosterVersion { get; set; }
    }

    public class RepOfflineVoteSyncRequest
    {
        [Required] public int MemberId { get; set; }
        [Required] public int EventId { get; set; }
        [Required, MinLength(1), MaxLength(RepresentativeLimits.MaxSelectionsPerRequest)] public List<int> CandidateIds { get; set; } = new();
        [Required, MaxLength(30)] public string ReceiptCode { get; set; } = "";
        [Required] public DateTime CastAtUtc { get; set; }
        [MaxLength(64)] public string? RosterVersion { get; set; }
    }

    public class RepOfflineVoteSyncBulkRequest
    {
        [Required, MinLength(1), MaxLength(500)] public List<RepOfflineVoteSyncRequest> Votes { get; set; } = new();
    }

    // ── Results ──────────────────────────────────────────────────────────────────────────────────────────────
    public class RepCandidateResult
    {
        public int Id { get; set; }
        public string CandidateCode { get; set; } = "";
        public string CandidateName { get; set; } = "";
        public string Position { get; set; } = "";
        public int? BallotNumber { get; set; }
        /// <summary>Selections this candidate received (Candidate Vote Count).</summary>
        public int Votes { get; set; }
        /// <summary>1-based rank within the position; tied candidates share a rank (competition ranking: 1,2,2,4).</summary>
        public int Rank { get; set; }
        /// <summary>True when the candidate holds one of the position's seats and is not part of an unresolved tie.</summary>
        public bool IsWinner { get; set; }
        /// <summary>True when this candidate shares the last seat's vote count with another candidate and the seat cannot be decided by votes.</summary>
        public bool IsTieForLastSeat { get; set; }
        /// <summary>Votes as a share of the BALLOTS cast (turnout-style), not of the selections — a ballot can select several candidates.</summary>
        public decimal PercentOfBallots { get; set; }
    }

    public class RepPositionResult
    {
        public string Position { get; set; } = "";
        public int MinVotes { get; set; }
        public int MaxVotes { get; set; }
        public int TotalSeats { get; set; }
        public int? MaxCandidates { get; set; }
        public int Candidates { get; set; }
        /// <summary>Total candidate selections made at this position.</summary>
        public int Selections { get; set; }
        /// <summary>Distinct ballots that selected at least one candidate at this position.</summary>
        public int BallotsWithSelection { get; set; }
        public int SeatsFilled { get; set; }
        public bool HasUnresolvedTie { get; set; }
        public List<RepCandidateResult> Results { get; set; } = new();
    }

    public class RepResultsResponse
    {
        public int EventId { get; set; }
        public string EventTitle { get; set; } = "";
        public string EventStatus { get; set; } = "";
        public DateTime GeneratedAt { get; set; }
        /// <summary>Total Voters = unique submitted ballots.</summary>
        public int TotalVoters { get; set; }
        /// <summary>Total Candidate Selections = vote records.</summary>
        public int TotalSelections { get; set; }
        /// <summary>Members who are currently eligible and active in the participating branches.</summary>
        public int EligibleVoters { get; set; }
        public decimal TurnoutPercent { get; set; }
        /// <summary>Voter-log rows marked as voted - must equal TotalVoters; a difference is reported, never hidden.</summary>
        public int BallotsInVoterLogs { get; set; }
        public bool IsReconciled { get; set; }
        public List<RepPositionResult> Positions { get; set; } = new();
    }
}

namespace EVoting.Api.Models.Admin
{
    // ── Settings: member eligibility ─────────────────────────────────────────────────────────────────────────
    public class EligibilitySettingsResponse
    {
        public decimal MinShareCapital { get; set; }
        public decimal MinSavingsDeposit { get; set; }
        /// <summary>"database" once an administrator has saved the values, "configuration" while the appsettings defaults apply.</summary>
        public string Source { get; set; } = "configuration";
        public int Version { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        /// <summary>The rules that are NOT configurable, in words (loan status, active membership …).</summary>
        public List<string> OtherRules { get; set; } = new();
        /// <summary>True when stored eligibility flags were computed with different thresholds than the saved ones (a recompute is pending).</summary>
        public bool RecomputePending { get; set; }
        public bool Locked { get; set; }
        public string? LockReason { get; set; }
        public int ActiveMembers { get; set; }
        public int EligibleMembers { get; set; }
    }

    public class EligibilitySettingsUpdateRequest
    {
        [Range(0, 1_000_000_000_000)] public decimal MinShareCapital { get; set; }
        [Range(0, 1_000_000_000_000)] public decimal MinSavingsDeposit { get; set; }
        /// <summary>Version the client loaded; a mismatch answers 409.</summary>
        public int? ExpectedVersion { get; set; }
        [MaxLength(300)] public string? Reason { get; set; }
    }

    public class EligibilityImpactRequest
    {
        [Range(0, 1_000_000_000_000)] public decimal MinShareCapital { get; set; }
        [Range(0, 1_000_000_000_000)] public decimal MinSavingsDeposit { get; set; }
    }

    /// <summary>Answer to a save: the stored settings, and what the recalculation of the member flags did (null when nothing changed).</summary>
    public class EligibilitySaveResponse
    {
        public EligibilitySettingsResponse Settings { get; set; } = new();
        public EligibilityRecomputeResponse? Recompute { get; set; }
        public bool Unchanged { get; set; }
        public string Message { get; set; } = "";
    }

    public class EligibilityImpactResponse
    {
        public int ActiveMembers { get; set; }
        public int EligibleNow { get; set; }
        public int EligibleAfter { get; set; }
        public int BecomingEligible { get; set; }
        public int BecomingIneligible { get; set; }
    }

    public class EligibilityRecomputeResponse
    {
        public int Processed { get; set; }
        public int Changed { get; set; }
        public int BecameEligible { get; set; }
        public int BecameIneligible { get; set; }
        public string Message { get; set; } = "";
    }
}
