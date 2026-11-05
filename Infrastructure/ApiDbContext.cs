using EVoting.Api.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Infrastructure
{
    /// <summary>
    /// EF Core context for the API.  Maps to the SAME MySQL schema used by
    /// the Admin Server and Kiosk — no schema changes required.
    /// </summary>
    public class ApiDbContext : DbContext
    {
        public ApiDbContext(DbContextOptions<ApiDbContext> options) : base(options) { }

        // ── Admin / Management tables ────────────────────────────────
        public DbSet<UserEntity> Users { get; set; }
        public DbSet<BranchEntity> Branches { get; set; }
        public DbSet<RoleEntity> Roles { get; set; }
        public DbSet<PermissionEntity> Permissions { get; set; }
        public DbSet<RolePermissionEntity> RolePermissions { get; set; }
        public DbSet<MemberEntity> Members { get; set; }
        public DbSet<CandidacyEntity> Candidacies { get; set; }
        public DbSet<EventEntity> Events { get; set; }
        public DbSet<EventBranchEntity> EventBranches { get; set; }
        public DbSet<AuditLogEntity> AuditLogs { get; set; }

        // ── Voting tables (created by Kiosk migration SQL) ───────────
        public DbSet<VoteEntity> Votes { get; set; }
        public DbSet<VoterLogEntity> VoterLogs { get; set; }

        // ── Event position rules ─────────────────────────────────────
        public DbSet<EventPositionRuleEntity> EventPositionRules { get; set; }

        // ── Representative Election module ───────────────────────────
        public DbSet<RepresentativeCandidateProfileEntity> RepresentativeCandidateProfiles { get; set; }
        public DbSet<RepresentativeCandidatePhotoEntity> RepresentativeCandidatePhotos { get; set; }
        public DbSet<RepresentativeEventCandidateEntity> RepresentativeEventCandidates { get; set; }
        public DbSet<RepresentativeVoteEntity> RepresentativeVotes { get; set; }
        public DbSet<RepresentativeImportBatchEntity> RepresentativeImportBatches { get; set; }
        public DbSet<RepresentativeImportRowEntity> RepresentativeImportRows { get; set; }

        // ── System-wide settings (member eligibility thresholds, …) ──
        public DbSet<SystemSettingEntity> SystemSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ── Table name mappings (match existing schema exactly) ──
            modelBuilder.Entity<UserEntity>().ToTable("Users");
            modelBuilder.Entity<BranchEntity>().ToTable("Branches");
            modelBuilder.Entity<RoleEntity>().ToTable("Roles");
            modelBuilder.Entity<PermissionEntity>().ToTable("Permissions");
            modelBuilder.Entity<RolePermissionEntity>().ToTable("RolePermissions");
            modelBuilder.Entity<MemberEntity>().ToTable("Members");
            modelBuilder.Entity<CandidacyEntity>().ToTable("Candidacies");
            modelBuilder.Entity<EventEntity>().ToTable("Events");
            modelBuilder.Entity<EventBranchEntity>().ToTable("EventBranches");
            modelBuilder.Entity<AuditLogEntity>().ToTable("AuditLogs");
            modelBuilder.Entity<VoteEntity>().ToTable("votes");
            modelBuilder.Entity<VoterLogEntity>().ToTable("voterlogs");
            modelBuilder.Entity<EventPositionRuleEntity>().ToTable("EventPositionRules");

            // ── Indexes ──────────────────────────────────────────────
            modelBuilder.Entity<UserEntity>()
                .HasIndex(u => u.Username).IsUnique().HasDatabaseName("IX_Users_Username");

            modelBuilder.Entity<MemberEntity>()
                .HasIndex(m => m.AccountCode).IsUnique().HasDatabaseName("IX_Members_AccountCode");

            modelBuilder.Entity<VoterLogEntity>()
                .HasIndex(v => new { v.MemberId, v.EventId }).IsUnique();

            // Voter Lookup (server-side paging). Both are COVERING indexes: the list, its count and its search are answered from the index alone,
            // in name order, without reading a member row (members carry the photo column). Measured on 1,000,000 members in
            // docs/representative-election/06-voter-lookup-paging.md. Equality columns first (the two flags), then the sort column (MemberName;
            // MemberId, the primary key, is appended by InnoDB), then the columns the filters and the search read.
            modelBuilder.Entity<MemberEntity>()
                .HasIndex(m => new { m.IsActive, m.IsEligibleToVote, m.MemberName, m.AccountCode, m.BranchId })
                .HasDatabaseName("IX_Members_VoterLookup_Cover");
            // "who has voted in this election" as an index-only range: (EventId, HasVoted) -> MemberId
            modelBuilder.Entity<VoterLogEntity>()
                .HasIndex(v => new { v.EventId, v.HasVoted, v.MemberId })
                .HasDatabaseName("IX_VoterLogs_Event_Voted_Member");

            modelBuilder.Entity<VoteEntity>()
                .HasIndex(v => new { v.MemberId, v.EventId, v.CandidacyId }).IsUnique();

            // ── Relationships ────────────────────────────────────────
            modelBuilder.Entity<UserEntity>()
                .HasOne(u => u.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(u => u.RoleId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<RolePermissionEntity>()
                .HasOne(rp => rp.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<RolePermissionEntity>()
                .HasOne(rp => rp.Permission)
                .WithMany()
                .HasForeignKey(rp => rp.PermissionId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventBranchEntity>()
                .HasOne(eb => eb.Event)
                .WithMany(e => e.EventBranches)
                .HasForeignKey(eb => eb.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<EventBranchEntity>()
                .HasOne(eb => eb.Branch)
                .WithMany()
                .HasForeignKey(eb => eb.BranchId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<MemberEntity>()
                .HasOne(m => m.Branch)
                .WithMany()
                .HasForeignKey(m => m.BranchId);

            modelBuilder.Entity<CandidacyEntity>()
                .HasOne(c => c.Member)
                .WithMany()
                .HasForeignKey(c => c.MemberId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<EventPositionRuleEntity>()
                .HasOne(r => r.Event)
                .WithMany()
                .HasForeignKey(r => r.EventId)
                .OnDelete(DeleteBehavior.Cascade);

            ConfigureEventAndRuleExtensions(modelBuilder);
            ConfigureRepresentativeElection(modelBuilder);
            ConfigureSystemSettings(modelBuilder);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        //  Columns / constraints added to EXISTING tables by migration 20261104140000_AddRepresentativeElection.
        //  Everything is additive: defaults and NULLs keep every pre-existing row and every older client valid.
        // ══════════════════════════════════════════════════════════════════════════════════════════════
        private static void ConfigureEventAndRuleExtensions(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EventEntity>(e =>
            {
                e.Property(x => x.EventType)
                    .HasMaxLength(30)
                    .IsRequired()
                    .HasDefaultValue(EventTypes.GeneralAssembly);

                e.Property(x => x.RosterLockedAt).HasColumnType("datetime(6)");

                e.ToTable("Events", t => t.HasCheckConstraint(
                    "CK_Events_EventType",
                    "`EventType` IN ('GENERAL_ASSEMBLY','REPRESENTATIVE')"));
            });

            modelBuilder.Entity<EventPositionRuleEntity>(r =>
            {
                // One rule per position per event (case-insensitive under the table collation).
                r.HasIndex(x => new { x.EventId, x.Position })
                    .IsUnique()
                    .HasDatabaseName("UQ_EventPositionRules_EventId_Position");

                r.ToTable("EventPositionRules", t => t.HasCheckConstraint(
                    "CK_EventPositionRules_MaxCandidates",
                    "`MaxCandidates` IS NULL OR `MaxCandidates` > 0"));
            });
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════════
        //  Representative Election tables. Every relationship to an election record is RESTRICT (no cascades);
        //  the only cascade is photo → its own profile, which is a 1:1 dependent blob, not an election record.
        // ══════════════════════════════════════════════════════════════════════════════════════════════
        private static void ConfigureRepresentativeElection(ModelBuilder modelBuilder)
        {
            // ── Profiles ─────────────────────────────────────────────────────────────────────────────
            modelBuilder.Entity<RepresentativeCandidateProfileEntity>(p =>
            {
                p.ToTable("RepresentativeCandidateProfiles");
                p.HasIndex(x => x.CandidateCode).IsUnique().HasDatabaseName("UQ_RepCandidateProfiles_CandidateCode");
                // A member can have at most one profile (NULLs are not compared by a MySQL unique index).
                p.HasIndex(x => x.MemberId).IsUnique().HasDatabaseName("UQ_RepCandidateProfiles_MemberId");
                p.HasIndex(x => x.CandidateName).HasDatabaseName("IX_RepCandidateProfiles_CandidateName");
                p.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
                p.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");
                p.Property(x => x.PhotoUpdatedAt).HasColumnType("datetime(6)");

                p.HasOne(x => x.Member)
                    .WithMany()
                    .HasForeignKey(x => x.MemberId)
                    .HasConstraintName("FK_RepCandidateProfiles_Members")
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // ── Photos (1:1, read only by the photo endpoints) ──────────────────────────────────────
            modelBuilder.Entity<RepresentativeCandidatePhotoEntity>(p =>
            {
                p.ToTable("RepresentativeCandidatePhotos");
                p.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");
                p.HasOne(x => x.Profile)
                    .WithOne()
                    .HasForeignKey<RepresentativeCandidatePhotoEntity>(x => x.RepresentativeCandidateProfileId)
                    .HasConstraintName("FK_RepCandidatePhotos_Profiles")
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // ── Import batches + staging rows ───────────────────────────────────────────────────────
            modelBuilder.Entity<RepresentativeImportBatchEntity>(b =>
            {
                b.ToTable("RepresentativeCandidateImportBatches", t => t.HasCheckConstraint(
                    "CK_RepImportBatches_Status",
                    "`Status` IN ('UPLOADED','VALIDATING','VALIDATED','INVALID','COMMITTED','FAILED','CANCELLED')"));
                b.HasIndex(x => new { x.EventId, x.CreatedAt }).HasDatabaseName("IX_RepImportBatches_Event_CreatedAt");
                b.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
                b.Property(x => x.ValidatedAt).HasColumnType("datetime(6)");
                b.Property(x => x.CommittedAt).HasColumnType("datetime(6)");
                b.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");

                b.HasOne(x => x.Event)
                    .WithMany()
                    .HasForeignKey(x => x.EventId)
                    .HasConstraintName("FK_RepImportBatches_Events")
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<RepresentativeImportRowEntity>(r =>
            {
                r.ToTable("RepresentativeCandidateImportRows", t => t.HasCheckConstraint(
                    "CK_RepImportRows_RowStatus",
                    "`RowStatus` IN ('PENDING','VALID','INVALID','CONFLICT','COMMITTED','SKIPPED')"));
                r.HasIndex(x => new { x.ImportBatchId, x.RowNumber }).IsUnique().HasDatabaseName("UQ_RepImportRows_Batch_RowNumber");
                r.HasIndex(x => new { x.ImportBatchId, x.RowStatus }).HasDatabaseName("IX_RepImportRows_Batch_RowStatus");
                r.HasIndex(x => new { x.ImportBatchId, x.CandidateCode }).HasDatabaseName("IX_RepImportRows_Batch_CandidateCode");

                r.HasOne(x => x.Batch)
                    .WithMany()
                    .HasForeignKey(x => x.ImportBatchId)
                    .HasConstraintName("FK_RepImportRows_Batches")
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // ── Event roster (assignment of a profile to an event + position) ────────────────────────
            modelBuilder.Entity<RepresentativeEventCandidateEntity>(c =>
            {
                c.ToTable("RepresentativeEventCandidates", t => t.HasCheckConstraint(
                    "CK_RepEventCandidates_Status",
                    "`Status` IN ('APPROVED','WITHDRAWN')"));

                // The business rule from the specification: a profile is assigned at most once per event + position.
                c.HasIndex(x => new { x.EventId, x.Position, x.RepresentativeCandidateProfileId })
                    .IsUnique().HasDatabaseName("UQ_RepEventCandidates_Event_Position_Profile");

                // Target of the composite foreign key held by RepresentativeVotes.
                c.HasAlternateKey(x => new { x.RepresentativeEventCandidateId, x.EventId })
                    .HasName("UQ_RepEventCandidates_Id_Event");

                // Printed ballot number, when used, is unique within an event + position (NULLs are ignored).
                c.HasIndex(x => new { x.EventId, x.Position, x.BallotNumber })
                    .IsUnique().HasDatabaseName("UQ_RepEventCandidates_Event_Position_Ballot");

                c.HasIndex(x => new { x.EventId, x.Position, x.Status }).HasDatabaseName("IX_RepEventCandidates_Event_Position_Status");
                c.HasIndex(x => x.RepresentativeCandidateProfileId).HasDatabaseName("IX_RepEventCandidates_Profile");
                c.HasIndex(x => x.ImportBatchId).HasDatabaseName("IX_RepEventCandidates_ImportBatch");
                c.Property(x => x.CreatedAt).HasColumnType("datetime(6)");
                c.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");

                c.HasOne(x => x.Event)
                    .WithMany()
                    .HasForeignKey(x => x.EventId)
                    .HasConstraintName("FK_RepEventCandidates_Events")
                    .OnDelete(DeleteBehavior.Restrict);

                c.HasOne(x => x.Profile)
                    .WithMany()
                    .HasForeignKey(x => x.RepresentativeCandidateProfileId)
                    .HasConstraintName("FK_RepEventCandidates_Profiles")
                    .OnDelete(DeleteBehavior.Restrict);

                c.HasOne(x => x.ImportBatch)
                    .WithMany()
                    .HasForeignKey(x => x.ImportBatchId)
                    .HasConstraintName("FK_RepEventCandidates_ImportBatches")
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // ── Votes ────────────────────────────────────────────────────────────────────────────────
            modelBuilder.Entity<RepresentativeVoteEntity>(v =>
            {
                v.ToTable("RepresentativeVotes");

                // The same voter cannot select the same candidate twice in one event.
                v.HasIndex(x => new { x.MemberId, x.EventId, x.RepresentativeEventCandidateId })
                    .IsUnique().HasDatabaseName("UQ_RepVotes_Member_Event_Candidate");
                // Serves the composite FK (column order must match) AND the results aggregation, which LEFT JOINs every
                // roster row to its votes on exactly (candidate id, event id) and counts from this covering index.
                v.HasIndex(x => new { x.RepresentativeEventCandidateId, x.EventId }).HasDatabaseName("IX_RepVotes_Candidate_Event");
                v.HasIndex(x => x.EventId).HasDatabaseName("IX_RepVotes_Event");
                v.HasIndex(x => x.ReceiptCode).HasDatabaseName("IX_RepVotes_ReceiptCode");
                v.Property(x => x.VotedAt).HasColumnType("datetime(6)");

                // THE integrity guarantee: (candidate id, event id) must exist together in RepresentativeEventCandidates,
                // so the database refuses a vote whose candidate belongs to a different event than the vote itself.
                v.HasOne(x => x.EventCandidate)
                    .WithMany()
                    .HasForeignKey(x => new { x.RepresentativeEventCandidateId, x.EventId })
                    .HasPrincipalKey(c => new { c.RepresentativeEventCandidateId, c.EventId })
                    .HasConstraintName("FK_RepVotes_EventCandidates_Candidate_Event")
                    .OnDelete(DeleteBehavior.Restrict);

                v.HasOne(x => x.Event)
                    .WithMany()
                    .HasForeignKey(x => x.EventId)
                    .HasConstraintName("FK_RepVotes_Events")
                    .OnDelete(DeleteBehavior.Restrict);

                v.HasOne(x => x.Member)
                    .WithMany()
                    .HasForeignKey(x => x.MemberId)
                    .HasConstraintName("FK_RepVotes_Members")
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }

        private static void ConfigureSystemSettings(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SystemSettingEntity>(s =>
            {
                s.ToTable("SystemSettings");
                s.Property(x => x.UpdatedAt).HasColumnType("datetime(6)");
            });
        }
    }
}