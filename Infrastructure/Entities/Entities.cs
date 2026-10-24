using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EVoting.Api.Infrastructure.Entities
{
    // ── Users ─────────────────────────────────────────────────────────────────
    public class UserEntity
    {
        public int Id { get; set; }
        [MaxLength(50)] public string FirstName { get; set; } = "";
        [MaxLength(50)] public string MiddleName { get; set; } = "";
        [MaxLength(50)] public string LastName { get; set; } = "";
        [MaxLength(200)] public string? Address { get; set; }
        [MaxLength(20)] public string? ContactNo { get; set; }
        [MaxLength(50)] public string Email { get; set; } = "";
        [MaxLength(50)] public string Username { get; set; } = "";
        [MaxLength(500)] public string PasswordHash { get; set; } = "";
        public int? BranchId { get; set; }
        public int? RoleId { get; set; }
        public byte[]? UserImage { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public bool IsActive { get; set; }

        [ForeignKey("BranchId")] public virtual BranchEntity? Branch { get; set; }
        [ForeignKey("RoleId")] public virtual RoleEntity? Role { get; set; }

        [NotMapped]
        public IReadOnlySet<string> PermissionCodes =>
            Role?.RolePermissions
                .Where(rp => rp.Permission != null)
                .Select(rp => rp.Permission!.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool HasPermission(string code) => PermissionCodes.Contains(code);
       
        public string FullName =>
            string.Join(" ", new[] { FirstName, MiddleName, LastName }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    // ── Branches ──────────────────────────────────────────────────────────────
    public class BranchEntity
    {
        [Key] public int BranchId { get; set; }
      
        [MaxLength(50)] public string BranchName { get; set; } = "";
        [MaxLength(50)] public string BranchAddress { get; set; } = "";
        [MaxLength(15)] public string? BranchContactNumber { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
     
        public DateTime DeletedAt { get; set; }
        public bool IsActive { get; set; }
    }

    // ── Roles ─────────────────────────────────────────────────────────────────
    public class RoleEntity
    {
        [Key] public int RoleId { get; set; }
        [MaxLength(50)] public string RoleName { get; set; } = "";

        // DB column is varchar(50) NOT NULL (legacy entity: [Required][MaxLength(50)]); this entity previously declared 200.
        [MaxLength(50)] public string? RoleDescription { get; set; }

        // Real columns of the Roles table (present in the legacy Admin entity but missing here, which made API-side
        // role create / soft-delete impossible). NOTE: HasData seeded the SuperAdmin row WITHOUT IsActive, so
        // Role.IsActive must never be used for authorization decisions.
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime DeletedAt { get; set; }
        public bool IsActive { get; set; }

        public ICollection<RolePermissionEntity> RolePermissions { get; set; } = new List<RolePermissionEntity>();
        public ICollection<UserEntity> Users { get; set; } = new List<UserEntity>();
    }

    // ── Permissions ───────────────────────────────────────────────────────────
    public class PermissionEntity
    {
        [Key] public int Id { get; set; }
        [MaxLength(50)] public string Code { get; set; } = "";
        [MaxLength(100)] public string Name { get; set; } = "";
    }

    // ── RolePermissions ───────────────────────────────────────────────────────
    public class RolePermissionEntity
    {
        [Key] public int Id { get; set; }
        public int RoleId { get; set; }
        public int PermissionId { get; set; }
        [ForeignKey("RoleId")] public virtual RoleEntity? Role { get; set; }
        [ForeignKey("PermissionId")] public virtual PermissionEntity? Permission { get; set; }
    }

    // ── Members ───────────────────────────────────────────────────────────────
    public enum LoanStatus { None, Active, PastDue, Paid }

    public class MemberEntity
    {
        [Key] public int MemberId { get; set; }
        [MaxLength(20)] public string AccountCode { get; set; } = "";
        [MaxLength(100)] public string MemberName { get; set; } = "";
        [MaxLength(200)] public string MemberAddress { get; set; } = "";
        [MaxLength(15)] public string? MemberContactNumber { get; set; }
        public int BranchId { get; set; }
        [ForeignKey("BranchId")] public virtual BranchEntity? Branch { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal ShareCapital { get; set; }
        [Column(TypeName = "decimal(18,2)")] public decimal SavingsDeposit { get; set; }
        public LoanStatus LoanStatus { get; set; }
        public bool IsEligibleToVote { get; set; }
        [MaxLength(500)] public string? VotingRemarks { get; set; }

        public DateTime? QrIssuedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public bool IsActive { get; set; }
        public byte[]? MemberImage { get; set; }
    }

    // ── Candidacies ───────────────────────────────────────────────────────────
    public class CandidacyEntity
    {
        [Key] public int CandidacyId { get; set; }
        public int? MemberId { get; set; }
        [ForeignKey("MemberId")] public virtual MemberEntity? Member { get; set; }
        [MaxLength(200)] public string? WalkInCandidateName { get; set; }
        [NotMapped]
        public string CandidateDisplayName =>
            Member?.MemberName ?? WalkInCandidateName ?? "(Unknown)";
        [MaxLength(100)] public string Position { get; set; } = "";
        public string Reason { get; set; } = "";
        [MaxLength(20)] public string? CellNo { get; set; }
        [MaxLength(20)] public string? TelNo { get; set; }
        [MaxLength(20)] public string? CivilStatus { get; set; }
        [MaxLength(20)] public string? Gender { get; set; }
        public DateTime DateOfBirth { get; set; }
        [MaxLength(200)] public string? HighestEducation { get; set; }
        [MaxLength(500)] public string? PreviousPositions { get; set; }
        [MaxLength(500)] public string? SpecialSkills { get; set; }
        public byte[]? CandidateImage { get; set; }
        [MaxLength(20)] public string Status { get; set; } = "";
        public string? RejectionReason { get; set; }
        public DateTime DateFiled { get; set; }
        public DateTime? DateApproved { get; set; }
        public DateTime? DateRejected { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public bool IsActive { get; set; }
    }

    // ── Events ────────────────────────────────────────────────────────────────
    public class EventEntity
    {
        [Key] public int EventId { get; set; }
        [MaxLength(100)] public string Title { get; set; } = "";
        [MaxLength(150)] public string Venue { get; set; } = "";
        public DateTime ScheduleFrom { get; set; }
        public DateTime ScheduleTo { get; set; }
        [MaxLength(20)] public string Status { get; set; } = "Closed";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsActive { get; set; } = true;
        public ICollection<EventBranchEntity> EventBranches { get; set; } = new List<EventBranchEntity>();
    }

    // ── EventBranches ─────────────────────────────────────────────────────────
    public class EventBranchEntity
    {
        [Key] public int EventBranchId { get; set; }
        public int EventId { get; set; }
        public int BranchId { get; set; }
        [ForeignKey("EventId")] public virtual EventEntity? Event { get; set; }
        [ForeignKey("BranchId")] public virtual BranchEntity? Branch { get; set; }
    }

    // ── AuditLog ──────────────────────────────────────────────────────────────
    // Maps the table created by migration 20260602000000_AddAuditLogsAndMissingPermissions
    // (AuditId, UserId, Username, Action, EntityType, EntityId, Details, IpAddress, OccurredAtUtc).
    // The previous mapping (AuditLogId/TableName/RecordId/OldValues/NewValues/CreatedAt) described a table that does
    // not exist and would have failed with "Unknown column" on the first query.
    public class AuditLogEntity
    {
        [Key] public long AuditId { get; set; }
        public int? UserId { get; set; }
        [MaxLength(50)] public string? Username { get; set; }
        [MaxLength(50)] public string Action { get; set; } = "";
        [MaxLength(100)] public string EntityType { get; set; } = "";
        public int? EntityId { get; set; }
        [MaxLength(2000)] public string? Details { get; set; }
        [MaxLength(45)] public string? IpAddress { get; set; }
        public DateTime OccurredAtUtc { get; set; }
    }

    // ── Votes (kiosk voting tables) ───────────────────────────────────────────
    public class VoteEntity
    {
        [Key] public int VoteId { get; set; }
        public int MemberId { get; set; }
        public int EventId { get; set; }
        public int CandidacyId { get; set; }
        [MaxLength(100)] public string Position { get; set; } = "";
        public DateTime VotedAt { get; set; }

        [MaxLength(30)] public string ReceiptCode { get; set; } = "";
    }

    public class VoterLogEntity
    {
        [Key] public int VoterLogId { get; set; }
        public int MemberId { get; set; }
        public int EventId { get; set; }
        public bool HasVoted { get; set; }
        public DateTime? VotedAt { get; set; }
        // BUG FIX: MaxLength(22) was too small for the generated receipt format (23+ chars).
        [MaxLength(30)] public string? ReceiptCode { get; set; }
    }

    // ── EventPositionRules ────────────────────────────────────────────────────
    /// <summary>
    /// Defines per-position voting constraints for a specific event
    /// (e.g. a voter may choose 1–11 Board of Director candidates).
    /// When no rows exist for an event the controller falls back to defaults.
    /// </summary>
    public class EventPositionRuleEntity
    {
        [Key] public int Id { get; set; }
        public int EventId { get; set; }
        [ForeignKey("EventId")] public virtual EventEntity? Event { get; set; }
        [MaxLength(100)] public string Position { get; set; } = "";
        public int MinVotes { get; set; } = 1;
        public int MaxVotes { get; set; } = 1;
        public int TotalSeats { get; set; } = 1;
    }
}