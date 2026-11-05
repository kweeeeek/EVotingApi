using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EVoting.Api.Infrastructure.Entities
{
    // ════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Representative Election module.
    //
    //  Design rules (see docs/representative-election/01-audit-and-design.md):
    //   * An election is still ONE row in Events; EventType tells the platform which ballot it uses.
    //   * Candidate PROFILES are reusable people; an ASSIGNMENT puts a profile on one event's roster at one position.
    //   * Votes are stored in their own table. A composite foreign key (candidate-assignment id + event id) lets the
    //     database itself refuse a vote for a candidate that belongs to another event.
    //   * Nothing here cascades onto submitted election records: every relationship to an election record is Restrict.
    // ════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Reusable candidate person. Identified by the stable <see cref="CandidateCode"/>, never by name.</summary>
    public class RepresentativeCandidateProfileEntity
    {
        [Key] public int RepresentativeCandidateProfileId { get; set; }

        /// <summary>Stable business identifier, stored trimmed + UPPER-CASE. Unique across all profiles.</summary>
        [MaxLength(30)] public string CandidateCode { get; set; } = "";

        /// <summary>Set only when the candidate is an existing cooperative member (resolved from AccountCode). Unique when set.</summary>
        public int? MemberId { get; set; }
        public virtual MemberEntity? Member { get; set; }

        [MaxLength(200)] public string CandidateName { get; set; } = "";
        [MaxLength(20)] public string? CellNo { get; set; }
        [MaxLength(20)] public string? TelNo { get; set; }
        [MaxLength(20)] public string? CivilStatus { get; set; }
        [MaxLength(20)] public string? Gender { get; set; }
        [Column(TypeName = "date")] public DateTime? DateOfBirth { get; set; }
        [MaxLength(200)] public string? HighestEducation { get; set; }
        [MaxLength(500)] public string? PreviousPositions { get; set; }
        [MaxLength(500)] public string? SpecialSkills { get; set; }

        /// <summary>
        /// Version stamp of the photo (NULL = no photo). Photos live in <see cref="RepresentativeCandidatePhotoEntity"/>
        /// so that a candidate LIST can never drag image bytes along; this stamp doubles as the cache key / ETag.
        /// </summary>
        public DateTime? PhotoUpdatedAt { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>Full-size image + small thumbnail for one profile (1:1). Only the photo endpoints read this table.</summary>
    public class RepresentativeCandidatePhotoEntity
    {
        [Key] public int RepresentativeCandidateProfileId { get; set; }
        public virtual RepresentativeCandidateProfileEntity? Profile { get; set; }

        [Column(TypeName = "mediumblob")] public byte[] Image { get; set; } = Array.Empty<byte>();
        [Column(TypeName = "mediumblob")] public byte[]? Thumbnail { get; set; }
        [MaxLength(50)] public string ContentType { get; set; } = "image/jpeg";
        [MaxLength(64)] public string Sha256 { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>One candidate on one event's ballot, at one position.</summary>
    public class RepresentativeEventCandidateEntity
    {
        [Key] public int RepresentativeEventCandidateId { get; set; }

        public int EventId { get; set; }
        public virtual EventEntity? Event { get; set; }

        public int RepresentativeCandidateProfileId { get; set; }
        public virtual RepresentativeCandidateProfileEntity? Profile { get; set; }

        [MaxLength(100)] public string Position { get; set; } = "";
        [MaxLength(2000)] public string? Reason { get; set; }

        /// <summary>Optional printed ballot number; unique within (event, position) when supplied.</summary>
        public int? BallotNumber { get; set; }

        /// <summary>APPROVED (on the ballot) or WITHDRAWN (kept for history, never offered to voters).</summary>
        [MaxLength(20)] public string Status { get; set; } = RepresentativeCandidateStatuses.Approved;

        public int? ImportBatchId { get; set; }
        public virtual RepresentativeImportBatchEntity? ImportBatch { get; set; }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>One selection on a submitted Representative ballot.</summary>
    public class RepresentativeVoteEntity
    {
        [Key] public long RepresentativeVoteId { get; set; }

        public int MemberId { get; set; }
        public virtual MemberEntity? Member { get; set; }

        public int EventId { get; set; }
        public virtual EventEntity? Event { get; set; }

        public int RepresentativeEventCandidateId { get; set; }
        /// <summary>Composite foreign key (RepresentativeEventCandidateId, EventId): the candidate must belong to EventId.</summary>
        public virtual RepresentativeEventCandidateEntity? EventCandidate { get; set; }

        public DateTime VotedAt { get; set; }
        [MaxLength(30)] public string ReceiptCode { get; set; } = "";
    }

    /// <summary>One uploaded candidate file (audit trail + staging header).</summary>
    public class RepresentativeImportBatchEntity
    {
        [Key] public int ImportBatchId { get; set; }

        public int EventId { get; set; }
        public virtual EventEntity? Event { get; set; }

        [MaxLength(255)] public string FileName { get; set; } = "";
        [MaxLength(64)] public string? FileSha256 { get; set; }

        /// <summary>UPLOADED → VALIDATING → VALIDATED | INVALID → COMMITTED | FAILED (or CANCELLED).</summary>
        [MaxLength(20)] public string Status { get; set; } = RepresentativeImportStatuses.Uploaded;

        public int ExpectedRows { get; set; }
        public int TotalRows { get; set; }
        public int ValidRows { get; set; }
        public int InvalidRows { get; set; }
        public int ConflictRows { get; set; }
        public int NewProfiles { get; set; }
        public int ReusedProfiles { get; set; }
        public int NewAssignments { get; set; }

        [MaxLength(1000)] public string? Message { get; set; }

        public int? CreatedByUserId { get; set; }
        [MaxLength(50)] public string? CreatedByUsername { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? ValidatedAt { get; set; }
        public DateTime? CommittedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>One staged spreadsheet row. Kept after commit so the batch can be reviewed later.</summary>
    public class RepresentativeImportRowEntity
    {
        [Key] public long ImportRowId { get; set; }

        public int ImportBatchId { get; set; }
        public virtual RepresentativeImportBatchEntity? Batch { get; set; }

        /// <summary>1-based spreadsheet row number, used in error reports.</summary>
        public int RowNumber { get; set; }

        [MaxLength(30)] public string? CandidateCode { get; set; }
        [MaxLength(200)] public string? CandidateName { get; set; }
        [MaxLength(20)] public string? AccountCode { get; set; }
        [MaxLength(100)] public string? Position { get; set; }
        [MaxLength(20)] public string? CellNo { get; set; }
        [MaxLength(20)] public string? TelNo { get; set; }
        [MaxLength(20)] public string? CivilStatus { get; set; }
        [MaxLength(20)] public string? Gender { get; set; }
        [Column(TypeName = "date")] public DateTime? DateOfBirth { get; set; }
        [MaxLength(200)] public string? HighestEducation { get; set; }
        [MaxLength(500)] public string? PreviousPositions { get; set; }
        [MaxLength(500)] public string? SpecialSkills { get; set; }
        [MaxLength(2000)] public string? Reason { get; set; }
        public int? BallotNumber { get; set; }

        /// <summary>PENDING → VALID | INVALID | CONFLICT → COMMITTED | SKIPPED.</summary>
        [MaxLength(20)] public string RowStatus { get; set; } = RepresentativeImportRowStatuses.Pending;

        /// <summary>What commit will do: CREATE_PROFILE, REUSE_PROFILE or UPDATE_PROFILE (always followed by an assignment).</summary>
        [MaxLength(20)] public string? PlannedAction { get; set; }

        /// <summary>Existing profile matched by CandidateCode (never by name).</summary>
        public int? MatchedProfileId { get; set; }

        /// <summary>Administrator's explicit decision for a CONFLICT row: USE_EXISTING, UPDATE_PROFILE or SKIP.</summary>
        [MaxLength(20)] public string? Resolution { get; set; }

        [MaxLength(500)] public string? ErrorCodes { get; set; }
        [MaxLength(1000)] public string? ErrorMessage { get; set; }
    }

    /// <summary>Small key/value table for system-wide, administrator-editable settings (e.g. eligibility thresholds).</summary>
    public class SystemSettingEntity
    {
        [Key, MaxLength(100)] public string SettingKey { get; set; } = "";
        [MaxLength(1000)] public string SettingValue { get; set; } = "";
        [MaxLength(500)] public string? Description { get; set; }

        /// <summary>Incremented on every change; used for optimistic concurrency from the Admin screen.</summary>
        public int Version { get; set; } = 1;
        public DateTime UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
    }

    // ── Controlled vocabularies (stored as text; guarded by CHECK constraints) ──────────────────────────────
    public static class EventTypes
    {
        public const string GeneralAssembly = "GENERAL_ASSEMBLY";
        public const string Representative = "REPRESENTATIVE";

        public static bool IsValid(string? v) => v == GeneralAssembly || v == Representative;

        /// <summary>Null/blank → GENERAL_ASSEMBLY (the default for every pre-existing event). Unknown text → null.</summary>
        public static string? Normalize(string? v)
        {
            if (string.IsNullOrWhiteSpace(v)) return GeneralAssembly;
            var t = v.Trim().ToUpperInvariant().Replace(' ', '_').Replace('-', '_');
            return IsValid(t) ? t : null;
        }
    }

    public static class RepresentativeCandidateStatuses
    {
        public const string Approved = "APPROVED";
        public const string Withdrawn = "WITHDRAWN";
    }

    public static class RepresentativeImportStatuses
    {
        public const string Uploaded = "UPLOADED";
        public const string Validating = "VALIDATING";
        public const string Validated = "VALIDATED";
        public const string Invalid = "INVALID";
        public const string Committed = "COMMITTED";
        public const string Failed = "FAILED";
        public const string Cancelled = "CANCELLED";

        public static readonly string[] All = { Uploaded, Validating, Validated, Invalid, Committed, Failed, Cancelled };
    }

    public static class RepresentativeImportRowStatuses
    {
        public const string Pending = "PENDING";
        public const string Valid = "VALID";
        public const string Invalid = "INVALID";
        public const string Conflict = "CONFLICT";
        public const string Committed = "COMMITTED";
        public const string Skipped = "SKIPPED";

        public static readonly string[] All = { Pending, Valid, Invalid, Conflict, Committed, Skipped };
    }
}
