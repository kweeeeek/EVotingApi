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
        }
    }
}