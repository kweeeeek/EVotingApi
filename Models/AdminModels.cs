using EVoting.Api.Services;
using System.ComponentModel.DataAnnotations;

namespace EVoting.Api.Models.Admin
{
    // ═════════════════════════════════════════════════════════════════════════
    //  Wire contracts for the Admin (WPF) client.  JSON is camelCase.
    //  Every update request accepts an OPTIONAL ExpectedUpdatedAt: when supplied and the stored
    //  record has since been changed by someone else, the API answers 409 (code CONCURRENCY) instead of
    //  silently overwriting.  Dates stored in UTC are serialised with a 'Z'; local-convention dates are not.
    //  The client's copy of these types is verified against this file by a reflection contract test.
    // ═════════════════════════════════════════════════════════════════════════

    // ── Common ───────────────────────────────────────────────────────────────
    public class PagedResponse<T>
    {
        public List<T> Items { get; set; } = new();
        public int Total { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }

    public class AvailabilityResponse
    {
        public bool Taken { get; set; }
        /// <summary>True when the value belongs to a deactivated (soft-deleted) record that still reserves it.</summary>
        public bool Deactivated { get; set; }
    }

    public class MessageResponse
    {
        public string Message { get; set; } = "";
    }

    // ── Auth ─────────────────────────────────────────────────────────────────
    public class MeResponse
    {
        public int UserId { get; set; }
        public string Username { get; set; } = "";
        public string FullName { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string? Email { get; set; }
        public int? BranchId { get; set; }
        public string? BranchName { get; set; }
        public int? RoleId { get; set; }
        public string? RoleName { get; set; }
        public bool HasPhoto { get; set; }
        public List<string> Permissions { get; set; } = new();
    }

    // ── Users ────────────────────────────────────────────────────────────────
    public class UserRow
    {
        public int Id { get; set; }
        public string UserCode { get; set; } = "";
        public string FullName { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string MiddleName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Address { get; set; } = "";
        public string ContactNo { get; set; } = "";
        public string Email { get; set; } = "";
        public string Username { get; set; } = "";
        public int? BranchId { get; set; }
        public string BranchName { get; set; } = "";
        public int? RoleId { get; set; }
        public string RoleName { get; set; } = "";
        public bool HasPhoto { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class UserUpsertRequest
    {
        [Required, MaxLength(FieldLimits.Name)] public string FirstName { get; set; } = "";
        [MaxLength(FieldLimits.Name)] public string? MiddleName { get; set; }
        [Required, MaxLength(FieldLimits.Name)] public string LastName { get; set; } = "";
        [Required, MaxLength(FieldLimits.UserAddress)] public string Address { get; set; } = "";
        [Required, MaxLength(FieldLimits.ContactNo)] public string ContactNo { get; set; } = "";
        [Required, MaxLength(FieldLimits.Name)] public string Email { get; set; } = "";
        [Required] public string Username { get; set; } = "";

        /// <summary>Required on create. On update: blank/null keeps the current password.</summary>
        public string? Password { get; set; }

        [Range(1, int.MaxValue, ErrorMessage = "Branch is required.")] public int BranchId { get; set; }
        [Range(1, int.MaxValue, ErrorMessage = "Role is required.")] public int RoleId { get; set; }

        /// <summary>Base64 image bytes. Null = leave unchanged (update) / none (create).</summary>
        public string? PhotoBase64 { get; set; }
        public bool RemovePhoto { get; set; }
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    public class UserImportRow
    {
        public int RowNumber { get; set; }
        public string? FullName { get; set; }
        public string? Address { get; set; }
        public string? ContactNo { get; set; }
        public string? Email { get; set; }
        public string? Username { get; set; }
        public string? BranchName { get; set; }
        public string? RoleName { get; set; }
    }

    public class UserImportRequest
    {
        /// <summary>Capped at 100 per request: every imported account gets an Argon2 hash (≈0.1–0.3 s each).</summary>
        [Required, MinLength(1), MaxLength(100)] public List<UserImportRow> Rows { get; set; } = new();
        [MaxLength(260)] public string? SourceFileName { get; set; }
    }

    public class ImportRowResult
    {
        public int RowNumber { get; set; }
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>Users: Created | Duplicate | Invalid.  Members: New | Restore | Duplicate | Invalid.</summary>
        public string Status { get; set; } = "";
        public string? Message { get; set; }
        public bool? IsEligible { get; set; }
        public string? VotingRemarks { get; set; }
    }

    public class ImportResponse
    {
        public bool Committed { get; set; }
        public int Imported { get; set; }
        public int Restored { get; set; }
        public int Duplicates { get; set; }
        public int Invalid { get; set; }
        public List<ImportRowResult> Rows { get; set; } = new();
    }

    // ── Roles & permissions ──────────────────────────────────────────────────
    public class RoleRow
    {
        public int RoleId { get; set; }
        public string RoleName { get; set; } = "";
        public string RoleDescription { get; set; } = "";
        public int ActiveUserCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class RoleDetail : RoleRow
    {
        public List<int> PermissionIds { get; set; } = new();
    }

    public class RoleUpsertRequest
    {
        [Required, MaxLength(FieldLimits.Name)] public string RoleName { get; set; } = "";
        [MaxLength(FieldLimits.RoleDescription)] public string? RoleDescription { get; set; }
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    public class RoleOption
    {
        public int RoleId { get; set; }
        public string RoleName { get; set; } = "";
    }

    public class PermissionRow
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class SetRolePermissionsRequest
    {
        [Required] public List<int> PermissionIds { get; set; } = new();
    }

    public class SetRolePermissionsResponse
    {
        public List<string> Added { get; set; } = new();
        public List<string> Removed { get; set; } = new();
        public int TotalAssigned { get; set; }
    }

    // ── Members ──────────────────────────────────────────────────────────────
    public class MemberRow
    {
        public int MemberId { get; set; }
        public string AccountCode { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string MemberAddress { get; set; } = "";
        public string? MemberContactNumber { get; set; }
        public int BranchId { get; set; }
        public string BranchName { get; set; } = "";
        public decimal ShareCapital { get; set; }
        public decimal SavingsDeposit { get; set; }
        public string LoanStatus { get; set; } = "None";
        public bool IsEligibleToVote { get; set; }
        public string? VotingRemarks { get; set; }
        public DateTime? QrIssuedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class MemberUpsertRequest
    {
        [Required, MaxLength(FieldLimits.AccountCode)] public string AccountCode { get; set; } = "";
        [Required, MaxLength(FieldLimits.MemberName)] public string MemberName { get; set; } = "";
        [Required, MaxLength(FieldLimits.MemberAddress)] public string MemberAddress { get; set; } = "";
        [MaxLength(FieldLimits.MemberContact)] public string? MemberContactNumber { get; set; }
        [Range(1, int.MaxValue, ErrorMessage = "Branch is required.")] public int BranchId { get; set; }
        [Range(0, 1_000_000_000_000)] public decimal ShareCapital { get; set; }
        [Range(0, 1_000_000_000_000)] public decimal SavingsDeposit { get; set; }
        /// <summary>None | Active | PastDue | Paid</summary>
        public string LoanStatus { get; set; } = "None";
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    public class EligibilityPreviewRequest
    {
        public decimal ShareCapital { get; set; }
        public decimal SavingsDeposit { get; set; }
        public string LoanStatus { get; set; } = "None";
    }

    public class EligibilityPreviewResponse
    {
        public bool IsEligible { get; set; }
        public string Remarks { get; set; } = "";
        public decimal MinShareCapital { get; set; }
        public decimal MinSavingsDeposit { get; set; }
    }

    public class MemberFilter
    {
        public string? Search { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
    }

    public class BulkDeleteMembersRequest
    {
        /// <summary>Explicit ids. Mutually exclusive with <see cref="Filter"/>.</summary>
        public List<int>? Ids { get; set; }

        /// <summary>"Delete everything matching this filter". <see cref="ExpectedCount"/> is then mandatory.</summary>
        public MemberFilter? Filter { get; set; }

        /// <summary>The number the user confirmed. If the live match count differs, nothing is deleted (409).</summary>
        public int ExpectedCount { get; set; }

        /// <summary>Must be true to remove members who have voter-log / vote rows (this also deletes those rows).</summary>
        public bool PurgeVotingRecords { get; set; }
    }

    public class BulkDeleteResponse
    {
        public int Deleted { get; set; }
        public int SkippedHasVotingHistory { get; set; }
        public int NotFound { get; set; }
        public List<string> Messages { get; set; } = new();
    }

    public class MemberImportRow
    {
        public int RowNumber { get; set; }
        public string? AccountCode { get; set; }
        public string? MemberName { get; set; }
        public string? MemberAddress { get; set; }
        public string? ContactNumber { get; set; }
        public string? BranchName { get; set; }
        public string? ShareCapital { get; set; }
        public string? SavingsDeposit { get; set; }
        public string? LoanStatus { get; set; }
    }

    public class MemberImportRequest
    {
        [Required, MinLength(1), MaxLength(20000)] public List<MemberImportRow> Rows { get; set; } = new();
        [MaxLength(260)] public string? SourceFileName { get; set; }
    }

    public class MemberLookupItem
    {
        public int MemberId { get; set; }
        public string AccountCode { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string? ContactNumber { get; set; }
    }

    // ── Candidacies ──────────────────────────────────────────────────────────
    public class CandidacyRow
    {
        public int CandidacyId { get; set; }
        public string CandidacyCode { get; set; } = "";
        public int? MemberId { get; set; }
        public string CandidateName { get; set; } = "";
        public string MemberAccountCode { get; set; } = "";
        public string BranchName { get; set; } = "";
        public string Position { get; set; } = "";
        public string Status { get; set; } = "";
        public string CellNo { get; set; } = "";
        public DateTime DateFiled { get; set; }
        public bool HasPhoto { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CandidacyDetail : CandidacyRow
    {
        public string? WalkInCandidateName { get; set; }
        public string Reason { get; set; } = "";
        public string TelNo { get; set; } = "";
        public string CivilStatus { get; set; } = "";
        public string Gender { get; set; } = "";
        public DateTime DateOfBirth { get; set; }
        public string HighestEducation { get; set; } = "";
        public string PreviousPositions { get; set; } = "";
        public string SpecialSkills { get; set; } = "";
        public string RejectionReason { get; set; } = "";
        public DateTime? DateApproved { get; set; }
        public DateTime? DateRejected { get; set; }
    }

    public class CandidacyUpsertRequest
    {
        /// <summary>Registered member. Null ⇒ walk-in candidate (WalkInCandidateName required).</summary>
        public int? MemberId { get; set; }
        [MaxLength(FieldLimits.WalkInName)] public string? WalkInCandidateName { get; set; }
        [Required, MaxLength(FieldLimits.CandidatePosition)] public string Position { get; set; } = "";
        [Required, MaxLength(20)] public string Status { get; set; } = "";
        [Required] public string Reason { get; set; } = "";
        [Required, MaxLength(FieldLimits.CellNo)] public string CellNo { get; set; } = "";
        [MaxLength(FieldLimits.CellNo)] public string? TelNo { get; set; }
        [Required, MaxLength(20)] public string CivilStatus { get; set; } = "";
        [Required, MaxLength(20)] public string Gender { get; set; } = "";
        [Required] public DateTime DateOfBirth { get; set; }
        [Required, MaxLength(FieldLimits.Education)] public string HighestEducation { get; set; } = "";
        [MaxLength(FieldLimits.LongText)] public string? PreviousPositions { get; set; }
        [MaxLength(FieldLimits.LongText)] public string? SpecialSkills { get; set; }
        public string? RejectionReason { get; set; }
        public string? PhotoBase64 { get; set; }
        public bool RemovePhoto { get; set; }
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    public class CandidacyLockState
    {
        public bool IsLocked { get; set; }
        public int? EventId { get; set; }
        public string? EventTitle { get; set; }
    }

    public class CandidacyOptionsResponse
    {
        public List<string> Positions { get; set; } = new();
        public List<string> Statuses { get; set; } = new();
        public List<string> CivilStatuses { get; set; } = new();
        public List<string> Genders { get; set; } = new();
        public List<string> EducationLevels { get; set; } = new();
    }

    public class CandidacyExportRow : CandidacyDetail
    {
        public string? PhotoBase64 { get; set; }
    }

    // ── Events ───────────────────────────────────────────────────────────────
    public class EventRow
    {
        public int EventId { get; set; }
        public string Title { get; set; } = "";
        public string Venue { get; set; } = "";
        public DateTime ScheduleFrom { get; set; }
        public DateTime ScheduleTo { get; set; }
        public string Status { get; set; } = "Closed";
        public string BranchNames { get; set; } = "";
        public int BranchCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class EventDetail : EventRow
    {
        public List<int> BranchIds { get; set; } = new();
        public int VoterLogCount { get; set; }
        public int VoteCount { get; set; }
    }

    public class EventUpsertRequest
    {
        [Required, MaxLength(FieldLimits.EventTitle)] public string Title { get; set; } = "";
        [Required, MaxLength(FieldLimits.EventVenue)] public string Venue { get; set; } = "";
        [Required] public DateTime ScheduleFrom { get; set; }
        [Required] public DateTime ScheduleTo { get; set; }
        [Required, MinLength(1, ErrorMessage = "Select at least one branch.")] public List<int> BranchIds { get; set; } = new();
        public DateTime? ExpectedUpdatedAt { get; set; }
    }

    public class OpenEventRequest
    {
        /// <summary>When another election is open: true closes it atomically, false answers 409.</summary>
        public bool CloseOthers { get; set; }
    }

    public class EventStateResponse
    {
        public int EventId { get; set; }
        public string Status { get; set; } = "";
        public List<int> ClosedEventIds { get; set; } = new();
        public string Message { get; set; } = "";
        /// <summary>Set when the change was saved but a side effect (cache eviction / live push) failed.</summary>
        public string? Warning { get; set; }
    }

    public class DeleteEventResponse
    {
        public string Message { get; set; } = "";
        public int VotesDeleted { get; set; }
        public int VoterLogsDeleted { get; set; }
    }

    // ── Audit ────────────────────────────────────────────────────────────────
    public class AuditRow
    {
        public long AuditId { get; set; }
        /// <summary>Always UTC (serialised with 'Z').</summary>
        public DateTime OccurredAtUtc { get; set; }
        public string Action { get; set; } = "";
        public string EntityType { get; set; } = "";
        public int? EntityId { get; set; }
        public int? UserId { get; set; }
        public string? Username { get; set; }
        public string? IpAddress { get; set; }
        public string? Details { get; set; }
    }

    public class AuditFiltersResponse
    {
        public List<string> Actions { get; set; } = new();
        public List<string> EntityTypes { get; set; } = new();
    }

    public class ReportExportedRequest
    {
        [Required, MaxLength(100)] public string ReportName { get; set; } = "";
        [MaxLength(1000)] public string? Details { get; set; }
    }
}
