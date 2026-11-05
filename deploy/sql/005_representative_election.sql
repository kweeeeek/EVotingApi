-- ============================================================================================================
-- 005_representative_election.sql                         migration 20261104140000_AddRepresentativeElection
--
-- WHAT   Adds the Representative Election module to an existing eVoting database:
--          Events.EventType (default 'GENERAL_ASSEMBLY'), Events.RosterLockedAt, Events.EligibilityMin*,
--          EventPositionRules.MaxCandidates (+ UNIQUE (EventId, Position)),
--          7 new tables (RepresentativeCandidateProfiles/Photos/ImportBatches/ImportRows/EventCandidates/Votes, SystemSettings)
--          and two permission rows (SETTINGS_ELIGIBILITY_VIEW / _EDIT) granted to RoleId 1.
--        It is the SAME change that `dotnet ef database update` applies; this script exists so the DBA can apply it by hand
--        and ALSO records the migration in __EFMigrationsHistory, so the EF history stays consistent.
--
-- SAFE  * Additive only: nothing is dropped, renamed or rewritten; existing rows keep their values and every older
--          client / the old API keep working (new columns have defaults or are NULL).
--        * Idempotent: every statement checks information_schema first. Run it on a fresh copy, on a half-migrated
--          database, or twice - the result is the same.
--        * MySQL DDL is not transactional. If a statement fails, the earlier ones stay applied; fix the cause and re-run.
--          The only statement that can fail because of data is the unique index on EventPositionRules (duplicate rules);
--          it runs first and a pre-flight query above it shows the offending rows.
--
-- HOW    1. mysqldump the database first.   2. Test on a COPY (never first on production).
--        3. mysql -u <admin> <database> < 005_representative_election.sql      (the database must be selected)
--        4. Run 005_representative_election_verify.sql and read every result set.
--        5. Deploy the new API AFTER this script (the old API ignores the new columns, so applying it first is safe).
--        Rollback: 005_representative_election_rollback.sql (refuses once a Representative ballot exists).
-- ============================================================================================================

-- the CHECK constraints below embed text literals; make sure they are stored as utf8mb4 whatever the client default is
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

-- index UQ_EventPositionRules_EventId_Position
-- PRE-FLIGHT: the unique index below fails if an event already has the same position twice. Show any such rows first.
SELECT EventId, Position, COUNT(*) AS DuplicateRules FROM `EventPositionRules` GROUP BY EventId, Position HAVING COUNT(*) > 1;
-- If the query above returned rows: keep one rule per (EventId, Position), delete the extras, then re-run this script.
-- (No rows deleted automatically - which duplicate is the right one is your decision.)
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('EventPositionRules') AND index_name = 'UQ_EventPositionRules_EventId_Position') = 0,
            'CREATE UNIQUE INDEX `UQ_EventPositionRules_EventId_Position` ON `EventPositionRules` (`EventId`, `Position`)',
            'SELECT ''index UQ_EventPositionRules_EventId_Position already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- column Events.EligibilityMinSavingsDeposit
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Events') AND column_name = 'EligibilityMinSavingsDeposit') = 0,
            'ALTER TABLE `Events` ADD `EligibilityMinSavingsDeposit` decimal(18,2) NULL',
            'SELECT ''column Events.EligibilityMinSavingsDeposit already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- column Events.EligibilityMinShareCapital
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Events') AND column_name = 'EligibilityMinShareCapital') = 0,
            'ALTER TABLE `Events` ADD `EligibilityMinShareCapital` decimal(18,2) NULL',
            'SELECT ''column Events.EligibilityMinShareCapital already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- column Events.EventType
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Events') AND column_name = 'EventType') = 0,
            'ALTER TABLE `Events` ADD `EventType` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT ''GENERAL_ASSEMBLY''',
            'SELECT ''column Events.EventType already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- column Events.RosterLockedAt
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Events') AND column_name = 'RosterLockedAt') = 0,
            'ALTER TABLE `Events` ADD `RosterLockedAt` datetime(6) NULL',
            'SELECT ''column Events.RosterLockedAt already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- column EventPositionRules.MaxCandidates
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('EventPositionRules') AND column_name = 'MaxCandidates') = 0,
            'ALTER TABLE `EventPositionRules` ADD `MaxCandidates` int NULL',
            'SELECT ''column EventPositionRules.MaxCandidates already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- table RepresentativeCandidateImportBatches
CREATE TABLE IF NOT EXISTS `RepresentativeCandidateImportBatches` (
    `ImportBatchId` int NOT NULL AUTO_INCREMENT,
    `EventId` int NOT NULL,
    `FileName` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `FileSha256` varchar(64) CHARACTER SET utf8mb4 NULL,
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `ExpectedRows` int NOT NULL,
    `TotalRows` int NOT NULL,
    `ValidRows` int NOT NULL,
    `InvalidRows` int NOT NULL,
    `ConflictRows` int NOT NULL,
    `NewProfiles` int NOT NULL,
    `ReusedProfiles` int NOT NULL,
    `NewAssignments` int NOT NULL,
    `Message` varchar(1000) CHARACTER SET utf8mb4 NULL,
    `CreatedByUserId` int NULL,
    `CreatedByUsername` varchar(50) CHARACTER SET utf8mb4 NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `ValidatedAt` datetime(6) NULL,
    `CommittedAt` datetime(6) NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_RepresentativeCandidateImportBatches` PRIMARY KEY (`ImportBatchId`),
    CONSTRAINT `CK_RepImportBatches_Status` CHECK (`Status` IN ('UPLOADED','VALIDATING','VALIDATED','INVALID','COMMITTED','FAILED','CANCELLED')),
    CONSTRAINT `FK_RepImportBatches_Events` FOREIGN KEY (`EventId`) REFERENCES `Events` (`EventId`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

-- table RepresentativeCandidateProfiles
CREATE TABLE IF NOT EXISTS `RepresentativeCandidateProfiles` (
    `RepresentativeCandidateProfileId` int NOT NULL AUTO_INCREMENT,
    `CandidateCode` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
    `MemberId` int NULL,
    `CandidateName` varchar(200) CHARACTER SET utf8mb4 NOT NULL,
    `CellNo` varchar(20) CHARACTER SET utf8mb4 NULL,
    `TelNo` varchar(20) CHARACTER SET utf8mb4 NULL,
    `CivilStatus` varchar(20) CHARACTER SET utf8mb4 NULL,
    `Gender` varchar(20) CHARACTER SET utf8mb4 NULL,
    `DateOfBirth` date NULL,
    `HighestEducation` varchar(200) CHARACTER SET utf8mb4 NULL,
    `PreviousPositions` varchar(500) CHARACTER SET utf8mb4 NULL,
    `SpecialSkills` varchar(500) CHARACTER SET utf8mb4 NULL,
    `PhotoUpdatedAt` datetime(6) NULL,
    `IsActive` tinyint(1) NOT NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_RepresentativeCandidateProfiles` PRIMARY KEY (`RepresentativeCandidateProfileId`),
    CONSTRAINT `FK_RepCandidateProfiles_Members` FOREIGN KEY (`MemberId`) REFERENCES `Members` (`MemberId`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

-- table SystemSettings
CREATE TABLE IF NOT EXISTS `SystemSettings` (
    `SettingKey` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `SettingValue` varchar(1000) CHARACTER SET utf8mb4 NOT NULL,
    `Description` varchar(500) CHARACTER SET utf8mb4 NULL,
    `Version` int NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    `UpdatedByUserId` int NULL,
    CONSTRAINT `PK_SystemSettings` PRIMARY KEY (`SettingKey`)
) CHARACTER SET=utf8mb4;

-- table RepresentativeCandidateImportRows
CREATE TABLE IF NOT EXISTS `RepresentativeCandidateImportRows` (
    `ImportRowId` bigint NOT NULL AUTO_INCREMENT,
    `ImportBatchId` int NOT NULL,
    `RowNumber` int NOT NULL,
    `CandidateCode` varchar(30) CHARACTER SET utf8mb4 NULL,
    `CandidateName` varchar(200) CHARACTER SET utf8mb4 NULL,
    `AccountCode` varchar(20) CHARACTER SET utf8mb4 NULL,
    `Position` varchar(100) CHARACTER SET utf8mb4 NULL,
    `CellNo` varchar(20) CHARACTER SET utf8mb4 NULL,
    `TelNo` varchar(20) CHARACTER SET utf8mb4 NULL,
    `CivilStatus` varchar(20) CHARACTER SET utf8mb4 NULL,
    `Gender` varchar(20) CHARACTER SET utf8mb4 NULL,
    `DateOfBirth` date NULL,
    `HighestEducation` varchar(200) CHARACTER SET utf8mb4 NULL,
    `PreviousPositions` varchar(500) CHARACTER SET utf8mb4 NULL,
    `SpecialSkills` varchar(500) CHARACTER SET utf8mb4 NULL,
    `Reason` varchar(2000) CHARACTER SET utf8mb4 NULL,
    `BallotNumber` int NULL,
    `RowStatus` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `PlannedAction` varchar(20) CHARACTER SET utf8mb4 NULL,
    `MatchedProfileId` int NULL,
    `Resolution` varchar(20) CHARACTER SET utf8mb4 NULL,
    `ErrorCodes` varchar(500) CHARACTER SET utf8mb4 NULL,
    `ErrorMessage` varchar(1000) CHARACTER SET utf8mb4 NULL,
    CONSTRAINT `PK_RepresentativeCandidateImportRows` PRIMARY KEY (`ImportRowId`),
    CONSTRAINT `CK_RepImportRows_RowStatus` CHECK (`RowStatus` IN ('PENDING','VALID','INVALID','CONFLICT','COMMITTED','SKIPPED')),
    CONSTRAINT `FK_RepImportRows_Batches` FOREIGN KEY (`ImportBatchId`) REFERENCES `RepresentativeCandidateImportBatches` (`ImportBatchId`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

-- table RepresentativeCandidatePhotos
CREATE TABLE IF NOT EXISTS `RepresentativeCandidatePhotos` (
    `RepresentativeCandidateProfileId` int NOT NULL,
    `Image` mediumblob NOT NULL,
    `Thumbnail` mediumblob NULL,
    `ContentType` varchar(50) CHARACTER SET utf8mb4 NOT NULL,
    `Sha256` varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_RepresentativeCandidatePhotos` PRIMARY KEY (`RepresentativeCandidateProfileId`),
    CONSTRAINT `FK_RepCandidatePhotos_Profiles` FOREIGN KEY (`RepresentativeCandidateProfileId`) REFERENCES `RepresentativeCandidateProfiles` (`RepresentativeCandidateProfileId`) ON DELETE CASCADE
) CHARACTER SET=utf8mb4;

-- table RepresentativeEventCandidates
CREATE TABLE IF NOT EXISTS `RepresentativeEventCandidates` (
    `RepresentativeEventCandidateId` int NOT NULL AUTO_INCREMENT,
    `EventId` int NOT NULL,
    `RepresentativeCandidateProfileId` int NOT NULL,
    `Position` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `Reason` varchar(2000) CHARACTER SET utf8mb4 NULL,
    `BallotNumber` int NULL,
    `Status` varchar(20) CHARACTER SET utf8mb4 NOT NULL,
    `ImportBatchId` int NULL,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NOT NULL,
    CONSTRAINT `PK_RepresentativeEventCandidates` PRIMARY KEY (`RepresentativeEventCandidateId`),
    CONSTRAINT `UQ_RepEventCandidates_Id_Event` UNIQUE (`RepresentativeEventCandidateId`, `EventId`),
    CONSTRAINT `CK_RepEventCandidates_Status` CHECK (`Status` IN ('APPROVED','WITHDRAWN')),
    CONSTRAINT `FK_RepEventCandidates_Events` FOREIGN KEY (`EventId`) REFERENCES `Events` (`EventId`) ON DELETE RESTRICT,
    CONSTRAINT `FK_RepEventCandidates_ImportBatches` FOREIGN KEY (`ImportBatchId`) REFERENCES `RepresentativeCandidateImportBatches` (`ImportBatchId`) ON DELETE RESTRICT,
    CONSTRAINT `FK_RepEventCandidates_Profiles` FOREIGN KEY (`RepresentativeCandidateProfileId`) REFERENCES `RepresentativeCandidateProfiles` (`RepresentativeCandidateProfileId`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

-- table RepresentativeVotes
CREATE TABLE IF NOT EXISTS `RepresentativeVotes` (
    `RepresentativeVoteId` bigint NOT NULL AUTO_INCREMENT,
    `MemberId` int NOT NULL,
    `EventId` int NOT NULL,
    `RepresentativeEventCandidateId` int NOT NULL,
    `VotedAt` datetime(6) NOT NULL,
    `ReceiptCode` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
    CONSTRAINT `PK_RepresentativeVotes` PRIMARY KEY (`RepresentativeVoteId`),
    CONSTRAINT `FK_RepVotes_EventCandidates_Candidate_Event` FOREIGN KEY (`RepresentativeEventCandidateId`, `EventId`) REFERENCES `RepresentativeEventCandidates` (`RepresentativeEventCandidateId`, `EventId`) ON DELETE RESTRICT,
    CONSTRAINT `FK_RepVotes_Events` FOREIGN KEY (`EventId`) REFERENCES `Events` (`EventId`) ON DELETE RESTRICT,
    CONSTRAINT `FK_RepVotes_Members` FOREIGN KEY (`MemberId`) REFERENCES `Members` (`MemberId`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;

-- check CK_Events_EventType
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Events') AND constraint_name = 'CK_Events_EventType' AND constraint_type = 'CHECK') = 0,
            'ALTER TABLE `Events` ADD CONSTRAINT `CK_Events_EventType` CHECK (`EventType` IN (''GENERAL_ASSEMBLY'',''REPRESENTATIVE''))',
            'SELECT ''check CK_Events_EventType already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- check CK_EventPositionRules_MaxCandidates
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('EventPositionRules') AND constraint_name = 'CK_EventPositionRules_MaxCandidates' AND constraint_type = 'CHECK') = 0,
            'ALTER TABLE `EventPositionRules` ADD CONSTRAINT `CK_EventPositionRules_MaxCandidates` CHECK (`MaxCandidates` IS NULL OR `MaxCandidates` > 0)',
            'SELECT ''check CK_EventPositionRules_MaxCandidates already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepImportBatches_Event_CreatedAt
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeCandidateImportBatches') AND index_name = 'IX_RepImportBatches_Event_CreatedAt') = 0,
            'CREATE INDEX `IX_RepImportBatches_Event_CreatedAt` ON `RepresentativeCandidateImportBatches` (`EventId`, `CreatedAt`)',
            'SELECT ''index IX_RepImportBatches_Event_CreatedAt already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepImportRows_Batch_CandidateCode
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeCandidateImportRows') AND index_name = 'IX_RepImportRows_Batch_CandidateCode') = 0,
            'CREATE INDEX `IX_RepImportRows_Batch_CandidateCode` ON `RepresentativeCandidateImportRows` (`ImportBatchId`, `CandidateCode`)',
            'SELECT ''index IX_RepImportRows_Batch_CandidateCode already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepImportRows_Batch_RowStatus
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeCandidateImportRows') AND index_name = 'IX_RepImportRows_Batch_RowStatus') = 0,
            'CREATE INDEX `IX_RepImportRows_Batch_RowStatus` ON `RepresentativeCandidateImportRows` (`ImportBatchId`, `RowStatus`)',
            'SELECT ''index IX_RepImportRows_Batch_RowStatus already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index UQ_RepImportRows_Batch_RowNumber
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeCandidateImportRows') AND index_name = 'UQ_RepImportRows_Batch_RowNumber') = 0,
            'CREATE UNIQUE INDEX `UQ_RepImportRows_Batch_RowNumber` ON `RepresentativeCandidateImportRows` (`ImportBatchId`, `RowNumber`)',
            'SELECT ''index UQ_RepImportRows_Batch_RowNumber already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepCandidateProfiles_CandidateName
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeCandidateProfiles') AND index_name = 'IX_RepCandidateProfiles_CandidateName') = 0,
            'CREATE INDEX `IX_RepCandidateProfiles_CandidateName` ON `RepresentativeCandidateProfiles` (`CandidateName`)',
            'SELECT ''index IX_RepCandidateProfiles_CandidateName already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index UQ_RepCandidateProfiles_CandidateCode
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeCandidateProfiles') AND index_name = 'UQ_RepCandidateProfiles_CandidateCode') = 0,
            'CREATE UNIQUE INDEX `UQ_RepCandidateProfiles_CandidateCode` ON `RepresentativeCandidateProfiles` (`CandidateCode`)',
            'SELECT ''index UQ_RepCandidateProfiles_CandidateCode already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index UQ_RepCandidateProfiles_MemberId
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeCandidateProfiles') AND index_name = 'UQ_RepCandidateProfiles_MemberId') = 0,
            'CREATE UNIQUE INDEX `UQ_RepCandidateProfiles_MemberId` ON `RepresentativeCandidateProfiles` (`MemberId`)',
            'SELECT ''index UQ_RepCandidateProfiles_MemberId already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepEventCandidates_Event_Position_Status
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeEventCandidates') AND index_name = 'IX_RepEventCandidates_Event_Position_Status') = 0,
            'CREATE INDEX `IX_RepEventCandidates_Event_Position_Status` ON `RepresentativeEventCandidates` (`EventId`, `Position`, `Status`)',
            'SELECT ''index IX_RepEventCandidates_Event_Position_Status already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepEventCandidates_ImportBatch
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeEventCandidates') AND index_name = 'IX_RepEventCandidates_ImportBatch') = 0,
            'CREATE INDEX `IX_RepEventCandidates_ImportBatch` ON `RepresentativeEventCandidates` (`ImportBatchId`)',
            'SELECT ''index IX_RepEventCandidates_ImportBatch already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepEventCandidates_Profile
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeEventCandidates') AND index_name = 'IX_RepEventCandidates_Profile') = 0,
            'CREATE INDEX `IX_RepEventCandidates_Profile` ON `RepresentativeEventCandidates` (`RepresentativeCandidateProfileId`)',
            'SELECT ''index IX_RepEventCandidates_Profile already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index UQ_RepEventCandidates_Event_Position_Ballot
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeEventCandidates') AND index_name = 'UQ_RepEventCandidates_Event_Position_Ballot') = 0,
            'CREATE UNIQUE INDEX `UQ_RepEventCandidates_Event_Position_Ballot` ON `RepresentativeEventCandidates` (`EventId`, `Position`, `BallotNumber`)',
            'SELECT ''index UQ_RepEventCandidates_Event_Position_Ballot already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index UQ_RepEventCandidates_Event_Position_Profile
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeEventCandidates') AND index_name = 'UQ_RepEventCandidates_Event_Position_Profile') = 0,
            'CREATE UNIQUE INDEX `UQ_RepEventCandidates_Event_Position_Profile` ON `RepresentativeEventCandidates` (`EventId`, `Position`, `RepresentativeCandidateProfileId`)',
            'SELECT ''index UQ_RepEventCandidates_Event_Position_Profile already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepVotes_Candidate_Event
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeVotes') AND index_name = 'IX_RepVotes_Candidate_Event') = 0,
            'CREATE INDEX `IX_RepVotes_Candidate_Event` ON `RepresentativeVotes` (`RepresentativeEventCandidateId`, `EventId`)',
            'SELECT ''index IX_RepVotes_Candidate_Event already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepVotes_Event
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeVotes') AND index_name = 'IX_RepVotes_Event') = 0,
            'CREATE INDEX `IX_RepVotes_Event` ON `RepresentativeVotes` (`EventId`)',
            'SELECT ''index IX_RepVotes_Event already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index IX_RepVotes_ReceiptCode
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeVotes') AND index_name = 'IX_RepVotes_ReceiptCode') = 0,
            'CREATE INDEX `IX_RepVotes_ReceiptCode` ON `RepresentativeVotes` (`ReceiptCode`)',
            'SELECT ''index IX_RepVotes_ReceiptCode already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- index UQ_RepVotes_Member_Event_Candidate
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('RepresentativeVotes') AND index_name = 'UQ_RepVotes_Member_Event_Candidate') = 0,
            'CREATE UNIQUE INDEX `UQ_RepVotes_Member_Event_Candidate` ON `RepresentativeVotes` (`MemberId`, `EventId`, `RepresentativeEventCandidateId`)',
            'SELECT ''index UQ_RepVotes_Member_Event_Candidate already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

-- permission rows (already idempotent: NOT EXISTS)
INSERT INTO `Permissions` (`Code`, `Name`) SELECT 'SETTINGS_ELIGIBILITY_VIEW', 'View Member Eligibility Settings' FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM `Permissions` WHERE `Code` = 'SETTINGS_ELIGIBILITY_VIEW');

-- permission rows (already idempotent: NOT EXISTS)
INSERT INTO `Permissions` (`Code`, `Name`) SELECT 'SETTINGS_ELIGIBILITY_EDIT', 'Edit Member Eligibility Settings' FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM `Permissions` WHERE `Code` = 'SETTINGS_ELIGIBILITY_EDIT');

-- permission rows (already idempotent: NOT EXISTS)
INSERT INTO `RolePermissions` (`RoleId`, `PermissionId`) SELECT r.`RoleId`, p.`Id` FROM `Roles` r JOIN `Permissions` p ON p.`Code` IN ('SETTINGS_ELIGIBILITY_VIEW', 'SETTINGS_ELIGIBILITY_EDIT') WHERE r.`RoleId` = 1 AND NOT EXISTS (SELECT 1 FROM `RolePermissions` rp WHERE rp.`RoleId` = r.`RoleId` AND rp.`PermissionId` = p.`Id`);

-- record the migration so `dotnet ef database update` later sees it as applied
INSERT IGNORE INTO `__EFMigrationsHistory` (`MigrationId`, `ProductVersion`)
VALUES ('20261104140000_AddRepresentativeElection', '9.0.0');

SELECT 'Migration recorded:' AS info, MigrationId, ProductVersion FROM `__EFMigrationsHistory` WHERE MigrationId = '20261104140000_AddRepresentativeElection';
