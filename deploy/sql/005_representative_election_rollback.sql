-- ============================================================================================================
-- 005_representative_election_rollback.sql                 UNDOES 005_representative_election.sql
--
-- !!  DESTRUCTIVE: drops the Representative Election tables and the columns the migration added.            !!
-- !!  Take a mysqldump first. It REFUSES to run when any Representative ballot exists, and it will not run  !!
-- !!  at all unless you confirm with:   SET @confirm_rollback := 'YES';   (same session, before this script) !!
--
-- Use it to back out a migration that was applied by mistake or on a test copy. Once real Representative
-- ballots exist the correct recovery is to restore the backup taken before the migration, NOT to roll back.
-- General Assembly tables (votes, voterlogs, candidacies, ...) are never touched.
--   HOW (the confirmation variable must be set in the SAME mysql session that runs this file; the client does not accept
--   "SET ...; SOURCE ..." in a single -e argument, so use one of these):
--     interactive (Windows or Linux):   mysql -u <admin> -p <database>
--                                       mysql> SET @confirm_rollback := 'YES';
--                                       mysql> SOURCE C:/path/to/005_representative_election_rollback.sql;
--     Linux/macOS shell, one line:      ( echo "SET @confirm_rollback := 'YES';"; cat 005_representative_election_rollback.sql ) | mysql -u <admin> -p <database>
--   Roll back 006_voter_lookup_indexes_rollback.sql FIRST (it only drops two indexes), then this file.
--   Without the variable the script stops at its first statement with "PROCEDURE ...__rollback_refused__set_confirm_rollback_to_YES_first does not exist".
-- ============================================================================================================
SET NAMES utf8mb4;

-- 1. refuse without explicit confirmation
SET @abort := IF(IFNULL(@confirm_rollback, '') <> 'YES',
                 'CALL __rollback_refused__set_confirm_rollback_to_YES_first()', 'SELECT ''confirmed'' AS info');
PREPARE s FROM @abort; EXECUTE s; DEALLOCATE PREPARE s;

-- 2. refuse when ballots exist (the table may not exist if the migration was never applied)
SET @ballots := IF((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND LOWER(table_name) = 'representativevotes') = 0,
                   0, NULL);
SET @sql := IF(@ballots IS NULL, 'SELECT COUNT(*) INTO @ballots FROM `RepresentativeVotes`', 'SELECT 0 INTO @ballots');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;
SET @abort := IF(@ballots > 0, 'CALL __rollback_refused__ballots_exist__restore_the_backup()', 'SELECT ''no representative ballots'' AS info');
PREPARE s FROM @abort; EXECUTE s; DEALLOCATE PREPARE s;

-- 3. permissions added by the migration
DELETE FROM `RolePermissions` WHERE `PermissionId` IN
  (SELECT `Id` FROM (SELECT `Id` FROM `Permissions` WHERE `Code` IN ('SETTINGS_ELIGIBILITY_VIEW','SETTINGS_ELIGIBILITY_EDIT')) AS x);
DELETE FROM `Permissions` WHERE `Code` IN ('SETTINGS_ELIGIBILITY_VIEW','SETTINGS_ELIGIBILITY_EDIT');

-- 4. tables, children first
DROP TABLE IF EXISTS `RepresentativeVotes`;
DROP TABLE IF EXISTS `RepresentativeEventCandidates`;
DROP TABLE IF EXISTS `RepresentativeCandidatePhotos`;
DROP TABLE IF EXISTS `RepresentativeCandidateImportRows`;
DROP TABLE IF EXISTS `SystemSettings`;
DROP TABLE IF EXISTS `RepresentativeCandidateProfiles`;
DROP TABLE IF EXISTS `RepresentativeCandidateImportBatches`;

-- 5. check constraints, columns and the unique index on the existing tables (each only if present)
SET @sql := IF((SELECT COUNT(*) FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND LOWER(table_name) = 'eventpositionrules'
                 AND constraint_name = 'CK_EventPositionRules_MaxCandidates') > 0,
               'ALTER TABLE `EventPositionRules` DROP CHECK `CK_EventPositionRules_MaxCandidates`', 'SELECT 1');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;
SET @sql := IF((SELECT COUNT(*) FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND LOWER(table_name) = 'events'
                 AND constraint_name = 'CK_Events_EventType') > 0,
               'ALTER TABLE `Events` DROP CHECK `CK_Events_EventType`', 'SELECT 1');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

SET @sql := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = 'eventpositionrules' AND column_name = 'MaxCandidates') > 0,
               'ALTER TABLE `EventPositionRules` DROP COLUMN `MaxCandidates`', 'SELECT 1');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;
SET @sql := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = 'eventpositionrules'
                 AND index_name = 'UQ_EventPositionRules_EventId_Position') > 0,
               'ALTER TABLE `EventPositionRules` DROP INDEX `UQ_EventPositionRules_EventId_Position`', 'SELECT 1');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

SET @sql := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = 'events'
                 AND column_name IN ('EventType','RosterLockedAt','EligibilityMinShareCapital','EligibilityMinSavingsDeposit')) = 4,
               'ALTER TABLE `Events` DROP COLUMN `EventType`, DROP COLUMN `RosterLockedAt`, DROP COLUMN `EligibilityMinShareCapital`, DROP COLUMN `EligibilityMinSavingsDeposit`',
               'SELECT ''Events columns already removed or partly removed - drop any that remain by hand'' AS info');
PREPARE s FROM @sql; EXECUTE s; DEALLOCATE PREPARE s;

-- 6. forget the migration so a later `dotnet ef database update` re-applies it
DELETE FROM `__EFMigrationsHistory` WHERE `MigrationId` = '20261104140000_AddRepresentativeElection';

SELECT 'rollback complete' AS info;
