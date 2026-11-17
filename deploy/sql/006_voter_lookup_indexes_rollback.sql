-- ============================================================================================================
-- 006_voter_lookup_indexes_rollback.sql                    UNDOES 006_voter_lookup_indexes.sql
--
-- Drops the two Voter Lookup indexes and removes the migration from __EFMigrationsHistory. No data is touched, so it is always
-- safe to run (the Voter Lookup list keeps working; it is only slower on a very large membership). It does NOT undo 005.
--   mysql -u <admin> <database> < 006_voter_lookup_indexes_rollback.sql
-- ============================================================================================================
SET NAMES utf8mb4;

SET @db_selected := DATABASE();
SET @abort := IF(@db_selected IS NULL, 'CALL __evoting_no_database_selected__use_USE_statement_first()', 'SELECT 1');
PREPARE s FROM @abort; EXECUTE s; DEALLOCATE PREPARE s;

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = 'members' AND index_name = 'IX_Members_VoterLookup_Cover') > 0,
            'DROP INDEX `IX_Members_VoterLookup_Cover` ON `Members`',
            'SELECT ''index IX_Members_VoterLookup_Cover not present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = 'voterlogs' AND index_name = 'IX_VoterLogs_Event_Voted_Member') > 0,
            'DROP INDEX `IX_VoterLogs_Event_Voted_Member` ON `voterlogs`',
            'SELECT ''index IX_VoterLogs_Event_Voted_Member not present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

DELETE FROM `__EFMigrationsHistory` WHERE MigrationId = '20261104150000_AddVoterLookupIndexes';

SELECT 'Rolled back 006. Migrations still recorded:' AS info, GROUP_CONCAT(MigrationId ORDER BY MigrationId SEPARATOR ', ') AS migrations FROM `__EFMigrationsHistory`;
