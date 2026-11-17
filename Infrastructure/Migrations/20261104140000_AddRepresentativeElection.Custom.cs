using Microsoft.EntityFrameworkCore.Migrations;

namespace EVoting.Api.Infrastructure.Migrations
{
    // Hand-written parts of migration 20261104140000_AddRepresentativeElection. The schema operations in the companion
    // file are produced from EF's own model diff; this file holds what a model diff cannot express.
    //
    // MySQL DDL is NOT transactional (every CREATE/ALTER commits implicitly), so a failure half-way leaves the steps
    // that already ran in place. Two measures keep that safe:
    //   * Up(): the only step that can fail because of existing DATA (the unique index on EventPositionRules(EventId,
    //     Position) when duplicate rules exist) runs FIRST, so a failure there changes nothing.
    //   * deploy/sql/005_representative_election.sql is the same change written to be re-runnable: after fixing the
    //     cause of a failure it completes whatever is missing and records the migration in __EFMigrationsHistory.
    public partial class AddRepresentativeElection
    {
        /// <summary>Permission codes introduced by this migration (Settings > Member Eligibility).</summary>
        internal static readonly (string Code, string Name)[] NewPermissions =
        {
            ("SETTINGS_ELIGIBILITY_VIEW", "View Member Eligibility Settings"),
            ("SETTINGS_ELIGIBILITY_EDIT", "Edit Member Eligibility Settings"),
        };

        /// <summary>
        /// Adds the two permission rows (only when missing) and grants them to the seeded administrator role (RoleId 1),
        /// exactly as migration 20260602000000 did for AUDIT_VIEW / CANDIDATE_*. Other roles are NOT changed; an
        /// administrator assigns the permissions to further roles from the Roles screen.
        /// </summary>
        private static void SeedPermissions(MigrationBuilder migrationBuilder)
        {
            foreach (var (code, name) in NewPermissions)
            {
                migrationBuilder.Sql(
                    "INSERT INTO `Permissions` (`Code`, `Name`) " +
                    $"SELECT '{code}', '{name}' FROM DUAL " +
                    $"WHERE NOT EXISTS (SELECT 1 FROM `Permissions` WHERE `Code` = '{code}');");
            }

            var inList = string.Join(", ", NewPermissions.Select(p => $"'{p.Code}'"));
            migrationBuilder.Sql(
                "INSERT INTO `RolePermissions` (`RoleId`, `PermissionId`) " +
                "SELECT r.`RoleId`, p.`Id` FROM `Roles` r JOIN `Permissions` p ON p.`Code` IN (" + inList + ") " +
                "WHERE r.`RoleId` = 1 AND NOT EXISTS " +
                "(SELECT 1 FROM `RolePermissions` rp WHERE rp.`RoleId` = r.`RoleId` AND rp.`PermissionId` = p.`Id`);");
        }

        private static void UnseedPermissions(MigrationBuilder migrationBuilder)
        {
            var inList = string.Join(", ", NewPermissions.Select(p => $"'{p.Code}'"));
            migrationBuilder.Sql(
                "DELETE FROM `RolePermissions` WHERE `PermissionId` IN " +
                "(SELECT `Id` FROM (SELECT `Id` FROM `Permissions` WHERE `Code` IN (" + inList + ")) AS x);");
            migrationBuilder.Sql("DELETE FROM `Permissions` WHERE `Code` IN (" + inList + ");");
        }

        /// <summary>
        /// Down() drops tables that hold election data. It refuses to run once any Representative ballot exists:
        /// the scalar sub-query returns two rows, which MySQL rejects ("Subquery returns more than 1 row") BEFORE any
        /// table is touched. Nothing is dropped; the failing statement (visible in the EF log) names the reason.
        /// </summary>
        private static void RollbackGuard(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "/* ROLLBACK REFUSED: RepresentativeVotes already contains ballots. Export them first; see docs/representative-election/04-rollback.md */ " +
                "SELECT (SELECT 1 UNION ALL SELECT 2) FROM (SELECT 1 FROM `RepresentativeVotes` LIMIT 1) AS ballots_exist;");
        }
    }
}
