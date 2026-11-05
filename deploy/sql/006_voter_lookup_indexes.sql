-- ============================================================================================================
-- 006_voter_lookup_indexes.sql                              migration 20261104150000_AddVoterLookupIndexes
--
-- WHAT   Adds two covering indexes that make the server-side paged Voter Lookup fast on a very large membership:
--          Members.IX_Members_VoterLookup_Cover      (IsActive, IsEligibleToVote, MemberName, AccountCode, BranchId)
--          voterlogs.IX_VoterLogs_Event_Voted_Member (EventId, HasVoted, MemberId)
--        It is the SAME change that `dotnet ef database update` applies; this script exists so the DBA can apply it by hand
--        and ALSO records the migration in __EFMigrationsHistory, so the EF history stays consistent.
--
-- SAFE  * Only two CREATE INDEX statements. No column, table, constraint or row is changed or removed, so old and new API
--          versions keep working and the application returns the same rows with or without the indexes (only the speed differs).
--        * InnoDB builds a secondary index online (ALGORITHM=INPLACE, LOCK=NONE): reads and writes continue during the build.
--          On the real membership (tens of thousands of rows) it takes seconds; still, prefer a quiet moment, not event day.
--        * Idempotent: each statement checks information_schema first. Run it twice and the second run changes nothing.
--        * Needs 005_representative_election.sql applied first (the script stops with a message when it is not).
--        * Extra disk: roughly 10 MB per 100,000 members for the first index, 1 MB per 100,000 voter logs for the second.
--
-- HOW    1. mysqldump the database first.   2. Test on a COPY (never first on production).
--        3. mysql -u <admin> <database> < 006_voter_lookup_indexes.sql          (the database must be selected)
--        4. Run 006_voter_lookup_indexes_verify.sql and read every result set.
--        Rollback: 006_voter_lookup_indexes_rollback.sql (drops only these two indexes; always safe).
-- ============================================================================================================
SET NAMES utf8mb4;

SET @db_selected := DATABASE();
SELECT IF(@db_selected IS NULL, 'NO DATABASE SELECTED - run: USE <your database>;  then re-run this script', CONCAT('Applying to database: ', @db_selected)) AS info;
-- abort immediately when no database is selected (calling a procedure that cannot exist stops the script)
SET @abort := IF(@db_selected IS NULL, 'CALL __evoting_no_database_selected__use_USE_statement_first()', 'SELECT 1');
PREPARE s FROM @abort; EXECUTE s; DEALLOCATE PREPARE s;

CREATE TABLE IF NOT EXISTS `__EFMigrationsHistory` (
    `MigrationId` varchar(150) CHARACTER SET utf8mb4 NOT NULL,
    `ProductVersion` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) CHARACTER SET=utf8mb4;

-- the previous migration must already be recorded (keeps the EF history in order)
SET @abort := IF((SELECT COUNT(*) FROM `__EFMigrationsHistory` WHERE MigrationId = '20261104140000_AddRepresentativeElection') = 0,
                 'CALL __apply_005_representative_election_first()', 'SELECT ''005 is recorded - continuing'' AS info');
PREPARE s FROM @abort; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_VoterLogs_Event_Voted_Member
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('voterlogs') AND index_name = 'IX_VoterLogs_Event_Voted_Member') = 0,
            'CREATE INDEX `IX_VoterLogs_Event_Voted_Member` ON `voterlogs` (`EventId`, `HasVoted`, `MemberId`) ALGORITHM=INPLACE LOCK=NONE',
            'SELECT ''index IX_VoterLogs_Event_Voted_Member already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_Members_VoterLookup_Cover
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Members') AND index_name = 'IX_Members_VoterLookup_Cover') = 0,
            'CREATE INDEX `IX_Members_VoterLookup_Cover` ON `Members` (`IsActive`, `IsEligibleToVote`, `MemberName`, `AccountCode`, `BranchId`) ALGORITHM=INPLACE LOCK=NONE',
            'SELECT ''index IX_Members_VoterLookup_Cover already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- refresh the optimizer statistics of the two tables so the new indexes are costed correctly straight away
ANALYZE TABLE `Members`, `voterlogs`;

-- record the migration so `dotnet ef database update` later sees it as applied
INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261104150000_AddVoterLookupIndexes', '9.0.0');

SELECT 'Migration recorded:' AS info, MigrationId, ProductVersion FROM `__EFMigrationsHistory` WHERE MigrationId = '20261104150000_AddVoterLookupIndexes';
