using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EVoting.Api.Infrastructure.Migrations
{
    /// <summary>
    /// Two covering indexes for the server-side paged Voter Lookup. Nothing else changes: no column, table or row is touched.
    /// They are a performance aid only - the application returns the same rows without them, just slower on a very large membership.
    ///   IX_Members_VoterLookup_Cover   (IsActive, IsEligibleToVote, MemberName, AccountCode, BranchId)   members, in name order, without reading a member row
    ///   IX_VoterLogs_Event_Voted_Member (EventId, HasVoted, MemberId)                                   "who voted in this election" as an index-only range
    /// Both are created online by InnoDB (ALGORITHM=INPLACE, LOCK=NONE): readers and writers keep working while they build.
    /// </summary>
    public partial class AddVoterLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_VoterLogs_Event_Voted_Member",
                table: "voterlogs",
                columns: new[] { "EventId", "HasVoted", "MemberId" });

            migrationBuilder.CreateIndex(
                name: "IX_Members_VoterLookup_Cover",
                table: "Members",
                columns: new[] { "IsActive", "IsEligibleToVote", "MemberName", "AccountCode", "BranchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Members_VoterLookup_Cover",
                table: "Members");

            migrationBuilder.DropIndex(
                name: "IX_VoterLogs_Event_Voted_Member",
                table: "voterlogs");
        }
    }
}
