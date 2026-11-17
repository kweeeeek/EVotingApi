using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Pomelo.EntityFrameworkCore.MySql.Metadata;

#nullable disable

namespace EVoting.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRepresentativeElection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Schema changes. The unique index on EventPositionRules runs first: it is the only step that can fail
            //    because of existing data, and MySQL DDL is not transactional (see the .Custom.cs companion).

            migrationBuilder.CreateIndex(
                name: "UQ_EventPositionRules_EventId_Position",
                table: "EventPositionRules",
                columns: new[] { "EventId", "Position" },
                unique: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EligibilityMinSavingsDeposit",
                table: "Events",
                type: "decimal(18,2)", nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EligibilityMinShareCapital",
                table: "Events",
                type: "decimal(18,2)", nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "Events",
                type: "varchar(30)", maxLength: 30, nullable: false, defaultValue: "GENERAL_ASSEMBLY")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "RosterLockedAt",
                table: "Events",
                type: "datetime(6)", nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxCandidates",
                table: "EventPositionRules",
                type: "int", nullable: true);

            migrationBuilder.CreateTable(
                name: "RepresentativeCandidateImportBatches",
                columns: table => new
                {
                    ImportBatchId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FileSha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ExpectedRows = table.Column<int>(type: "int", nullable: false),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    ValidRows = table.Column<int>(type: "int", nullable: false),
                    InvalidRows = table.Column<int>(type: "int", nullable: false),
                    ConflictRows = table.Column<int>(type: "int", nullable: false),
                    NewProfiles = table.Column<int>(type: "int", nullable: false),
                    ReusedProfiles = table.Column<int>(type: "int", nullable: false),
                    NewAssignments = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ValidatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CommittedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepresentativeCandidateImportBatches", x => x.ImportBatchId);
                    table.CheckConstraint("CK_RepImportBatches_Status", "`Status` IN ('UPLOADED','VALIDATING','VALIDATED','INVALID','COMMITTED','FAILED','CANCELLED')");
                    table.ForeignKey(
                        name: "FK_RepImportBatches_Events",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RepresentativeCandidateProfiles",
                columns: table => new
                {
                    RepresentativeCandidateProfileId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    CandidateCode = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MemberId = table.Column<int>(type: "int", nullable: true),
                    CandidateName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CellNo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TelNo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CivilStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Gender = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DateOfBirth = table.Column<DateTime>(type: "date", nullable: true),
                    HighestEducation = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PreviousPositions = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SpecialSkills = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PhotoUpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepresentativeCandidateProfiles", x => x.RepresentativeCandidateProfileId);
                    table.ForeignKey(
                        name: "FK_RepCandidateProfiles_Members",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "MemberId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    SettingKey = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SettingValue = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Description = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Version = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.SettingKey);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RepresentativeCandidateImportRows",
                columns: table => new
                {
                    ImportRowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    ImportBatchId = table.Column<int>(type: "int", nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    CandidateCode = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CandidateName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AccountCode = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Position = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CellNo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TelNo = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CivilStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Gender = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DateOfBirth = table.Column<DateTime>(type: "date", nullable: true),
                    HighestEducation = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PreviousPositions = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    SpecialSkills = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BallotNumber = table.Column<int>(type: "int", nullable: true),
                    RowStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PlannedAction = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MatchedProfileId = table.Column<int>(type: "int", nullable: true),
                    Resolution = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErrorCodes = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErrorMessage = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepresentativeCandidateImportRows", x => x.ImportRowId);
                    table.CheckConstraint("CK_RepImportRows_RowStatus", "`RowStatus` IN ('PENDING','VALID','INVALID','CONFLICT','COMMITTED','SKIPPED')");
                    table.ForeignKey(
                        name: "FK_RepImportRows_Batches",
                        column: x => x.ImportBatchId,
                        principalTable: "RepresentativeCandidateImportBatches",
                        principalColumn: "ImportBatchId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RepresentativeCandidatePhotos",
                columns: table => new
                {
                    RepresentativeCandidateProfileId = table.Column<int>(type: "int", nullable: false),
                    Image = table.Column<byte[]>(type: "mediumblob", nullable: false),
                    Thumbnail = table.Column<byte[]>(type: "mediumblob", nullable: true),
                    ContentType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sha256 = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepresentativeCandidatePhotos", x => x.RepresentativeCandidateProfileId);
                    table.ForeignKey(
                        name: "FK_RepCandidatePhotos_Profiles",
                        column: x => x.RepresentativeCandidateProfileId,
                        principalTable: "RepresentativeCandidateProfiles",
                        principalColumn: "RepresentativeCandidateProfileId",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RepresentativeEventCandidates",
                columns: table => new
                {
                    RepresentativeEventCandidateId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    RepresentativeCandidateProfileId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BallotNumber = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImportBatchId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepresentativeEventCandidates", x => x.RepresentativeEventCandidateId);
                    table.UniqueConstraint("UQ_RepEventCandidates_Id_Event", x => new { x.RepresentativeEventCandidateId, x.EventId });
                    table.CheckConstraint("CK_RepEventCandidates_Status", "`Status` IN ('APPROVED','WITHDRAWN')");
                    table.ForeignKey(
                        name: "FK_RepEventCandidates_Events",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepEventCandidates_ImportBatches",
                        column: x => x.ImportBatchId,
                        principalTable: "RepresentativeCandidateImportBatches",
                        principalColumn: "ImportBatchId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepEventCandidates_Profiles",
                        column: x => x.RepresentativeCandidateProfileId,
                        principalTable: "RepresentativeCandidateProfiles",
                        principalColumn: "RepresentativeCandidateProfileId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RepresentativeVotes",
                columns: table => new
                {
                    RepresentativeVoteId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    MemberId = table.Column<int>(type: "int", nullable: false),
                    EventId = table.Column<int>(type: "int", nullable: false),
                    RepresentativeEventCandidateId = table.Column<int>(type: "int", nullable: false),
                    VotedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReceiptCode = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RepresentativeVotes", x => x.RepresentativeVoteId);
                    table.ForeignKey(
                        name: "FK_RepVotes_EventCandidates_Candidate_Event",
                        columns: x => new { x.RepresentativeEventCandidateId, x.EventId },
                        principalTable: "RepresentativeEventCandidates",
                        principalColumns: new[] { "RepresentativeEventCandidateId", "EventId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepVotes_Events",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RepVotes_Members",
                        column: x => x.MemberId,
                        principalTable: "Members",
                        principalColumn: "MemberId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Events_EventType",
                table: "Events",
                sql: "`EventType` IN ('GENERAL_ASSEMBLY','REPRESENTATIVE')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_EventPositionRules_MaxCandidates",
                table: "EventPositionRules",
                sql: "`MaxCandidates` IS NULL OR `MaxCandidates` > 0");

            migrationBuilder.CreateIndex(
                name: "IX_RepImportBatches_Event_CreatedAt",
                table: "RepresentativeCandidateImportBatches",
                columns: new[] { "EventId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RepImportRows_Batch_CandidateCode",
                table: "RepresentativeCandidateImportRows",
                columns: new[] { "ImportBatchId", "CandidateCode" });

            migrationBuilder.CreateIndex(
                name: "IX_RepImportRows_Batch_RowStatus",
                table: "RepresentativeCandidateImportRows",
                columns: new[] { "ImportBatchId", "RowStatus" });

            migrationBuilder.CreateIndex(
                name: "UQ_RepImportRows_Batch_RowNumber",
                table: "RepresentativeCandidateImportRows",
                columns: new[] { "ImportBatchId", "RowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepCandidateProfiles_CandidateName",
                table: "RepresentativeCandidateProfiles",
                column: "CandidateName");

            migrationBuilder.CreateIndex(
                name: "UQ_RepCandidateProfiles_CandidateCode",
                table: "RepresentativeCandidateProfiles",
                column: "CandidateCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RepCandidateProfiles_MemberId",
                table: "RepresentativeCandidateProfiles",
                column: "MemberId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepEventCandidates_Event_Position_Status",
                table: "RepresentativeEventCandidates",
                columns: new[] { "EventId", "Position", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RepEventCandidates_ImportBatch",
                table: "RepresentativeEventCandidates",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_RepEventCandidates_Profile",
                table: "RepresentativeEventCandidates",
                column: "RepresentativeCandidateProfileId");

            migrationBuilder.CreateIndex(
                name: "UQ_RepEventCandidates_Event_Position_Ballot",
                table: "RepresentativeEventCandidates",
                columns: new[] { "EventId", "Position", "BallotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UQ_RepEventCandidates_Event_Position_Profile",
                table: "RepresentativeEventCandidates",
                columns: new[] { "EventId", "Position", "RepresentativeCandidateProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RepVotes_Candidate_Event",
                table: "RepresentativeVotes",
                columns: new[] { "RepresentativeEventCandidateId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_RepVotes_Event",
                table: "RepresentativeVotes",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_RepVotes_ReceiptCode",
                table: "RepresentativeVotes",
                column: "ReceiptCode");

            migrationBuilder.CreateIndex(
                name: "UQ_RepVotes_Member_Event_Candidate",
                table: "RepresentativeVotes",
                columns: new[] { "MemberId", "EventId", "RepresentativeEventCandidateId" },
                unique: true);

            // 2. Permission rows for the new Settings > Member Eligibility screen.
            SeedPermissions(migrationBuilder);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rolling back is only meaningful before the first Representative ballot is cast; see the rollback notes.
            RollbackGuard(migrationBuilder);
            UnseedPermissions(migrationBuilder);

            migrationBuilder.DropCheckConstraint(
                name: "CK_EventPositionRules_MaxCandidates",
                table: "EventPositionRules");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Events_EventType",
                table: "Events");

            migrationBuilder.DropTable(
                name: "RepresentativeVotes");

            migrationBuilder.DropTable(
                name: "RepresentativeEventCandidates");

            migrationBuilder.DropTable(
                name: "RepresentativeCandidatePhotos");

            migrationBuilder.DropTable(
                name: "RepresentativeCandidateImportRows");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "RepresentativeCandidateProfiles");

            migrationBuilder.DropTable(
                name: "RepresentativeCandidateImportBatches");

            migrationBuilder.DropColumn(
                name: "MaxCandidates",
                table: "EventPositionRules");

            migrationBuilder.DropColumn(
                name: "RosterLockedAt",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "EligibilityMinShareCapital",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "EligibilityMinSavingsDeposit",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "UQ_EventPositionRules_EventId_Position",
                table: "EventPositionRules");

        }
    }
}
