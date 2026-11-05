-- ============================================================================================================
-- 005_representative_election_verify.sql        READ-ONLY - run after 005_representative_election.sql
--
-- Every row of the result is one check:  PASS when `actual` equals `expected`, otherwise FAIL.
-- Nothing is modified. Run it on the migrated TEST copy first, then on production after the real migration.
--   mysql -u <user> <database> < 005_representative_election_verify.sql
-- ============================================================================================================
SET NAMES utf8mb4;

SELECT check_name, expected, actual, IF(expected = actual, 'PASS', 'FAIL') AS result FROM (

  -- 1. the migration is recorded in EF's history table
  SELECT '01 migration recorded in __EFMigrationsHistory' AS check_name, '1' AS expected,
         CAST((SELECT COUNT(*) FROM `__EFMigrationsHistory` WHERE MigrationId = '20261104140000_AddRepresentativeElection') AS CHAR) AS actual

  -- 2. the seven new tables
  UNION ALL SELECT '02 new tables present (7)', '7',
         CAST((SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND LOWER(table_name) IN
           ('representativecandidateprofiles','representativecandidatephotos','representativecandidateimportbatches',
            'representativecandidateimportrows','representativeeventcandidates','representativevotes','systemsettings')) AS CHAR)

  -- 3. new columns on existing tables
  UNION ALL SELECT '03 Events.EventType varchar(30) NOT NULL default GENERAL_ASSEMBLY', 'varchar(30)|NO|GENERAL_ASSEMBLY',
         (SELECT CONCAT(column_type,'|',is_nullable,'|',IFNULL(column_default,'<null>')) FROM information_schema.columns
           WHERE table_schema = DATABASE() AND LOWER(table_name) = 'events' AND column_name = 'EventType')
  UNION ALL SELECT '04 Events.RosterLockedAt datetime(6) NULL', 'datetime(6)|YES',
         (SELECT CONCAT(column_type,'|',is_nullable) FROM information_schema.columns
           WHERE table_schema = DATABASE() AND LOWER(table_name) = 'events' AND column_name = 'RosterLockedAt')
  UNION ALL SELECT '05 Events.EligibilityMin* decimal(18,2) NULL (2 columns)', '2',
         CAST((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = 'events'
           AND column_name IN ('EligibilityMinShareCapital','EligibilityMinSavingsDeposit') AND column_type = 'decimal(18,2)' AND is_nullable = 'YES') AS CHAR)
  UNION ALL SELECT '06 EventPositionRules.MaxCandidates int NULL', 'int|YES',
         (SELECT CONCAT(column_type,'|',is_nullable) FROM information_schema.columns
           WHERE table_schema = DATABASE() AND LOWER(table_name) = 'eventpositionrules' AND column_name = 'MaxCandidates')

  -- 4. every pre-existing event kept a valid type
  UNION ALL SELECT '07 events with an invalid EventType', '0',
         CAST((SELECT COUNT(*) FROM `Events` WHERE EventType NOT IN ('GENERAL_ASSEMBLY','REPRESENTATIVE')) AS CHAR)

  -- 4b. collations: the API compares a roster position with a rule position (and codes with codes) across tables; a table created
  --     with a different collation than the existing ones would make those comparisons fail ("Illegal mix of collations") or behave differently.
  UNION ALL SELECT '08 new tables whose collation differs from the Events table', '0',
         CAST((SELECT COUNT(*) FROM information_schema.tables t WHERE t.table_schema = DATABASE()
                AND LOWER(t.table_name) IN ('representativecandidateprofiles','representativecandidatephotos','representativecandidateimportbatches',
                                            'representativecandidateimportrows','representativeeventcandidates','representativevotes','systemsettings')
                AND t.table_collation <> (SELECT e.table_collation FROM information_schema.tables e
                                           WHERE e.table_schema = DATABASE() AND LOWER(e.table_name) = 'events')) AS CHAR)
  UNION ALL SELECT '08b roster Position and rule Position columns share ONE collation', '1',
         CAST((SELECT COUNT(DISTINCT collation_name) FROM information_schema.columns WHERE table_schema = DATABASE() AND column_name = 'Position'
                AND LOWER(table_name) IN ('representativeeventcandidates','eventpositionrules')) AS CHAR)

  -- 5. constraints
  UNION ALL SELECT '09 CHECK constraints present (Events, EventPositionRules, 3 new tables)', '5',
         CAST((SELECT COUNT(*) FROM information_schema.table_constraints WHERE table_schema = DATABASE() AND constraint_type = 'CHECK'
           AND constraint_name IN ('CK_Events_EventType','CK_EventPositionRules_MaxCandidates','CK_RepImportBatches_Status',
                                   'CK_RepImportRows_RowStatus','CK_RepEventCandidates_Status')) AS CHAR)
  UNION ALL SELECT '10 composite FK RepresentativeVotes -> RepresentativeEventCandidates (candidate id, event id)',
         'RepresentativeEventCandidateId,EventId->RepresentativeEventCandidateId,EventId',
         (SELECT CONCAT(GROUP_CONCAT(column_name ORDER BY ordinal_position),'->',GROUP_CONCAT(referenced_column_name ORDER BY ordinal_position))
            FROM information_schema.key_column_usage WHERE table_schema = DATABASE() AND constraint_name = 'FK_RepVotes_EventCandidates_Candidate_Event')
  UNION ALL SELECT '11 no ON DELETE CASCADE anywhere in the new election tables (only photo -> its own profile)', '1',
         CAST((SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE()
                AND LOWER(table_name) LIKE 'representative%' AND delete_rule = 'CASCADE') AS CHAR)
  UNION ALL SELECT '12 unique indexes present (rules, profiles x2, roster x2, votes, import rows)', '7',
         CAST((SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics WHERE table_schema = DATABASE() AND non_unique = 0
           AND index_name IN ('UQ_EventPositionRules_EventId_Position','UQ_RepCandidateProfiles_CandidateCode','UQ_RepCandidateProfiles_MemberId',
                              'UQ_RepEventCandidates_Event_Position_Profile','UQ_RepEventCandidates_Event_Position_Ballot',
                              'UQ_RepVotes_Member_Event_Candidate','UQ_RepImportRows_Batch_RowNumber')) AS CHAR)

  -- 6. permissions
  UNION ALL SELECT '13 permission rows SETTINGS_ELIGIBILITY_VIEW / _EDIT', '2',
         CAST((SELECT COUNT(*) FROM `Permissions` WHERE Code IN ('SETTINGS_ELIGIBILITY_VIEW','SETTINGS_ELIGIBILITY_EDIT')) AS CHAR)

  -- 7. data integrity (all must be 0)
  UNION ALL SELECT '14 representative votes whose candidate belongs to another event', '0',
         CAST((SELECT COUNT(*) FROM `RepresentativeVotes` v JOIN `RepresentativeEventCandidates` c ON c.RepresentativeEventCandidateId = v.RepresentativeEventCandidateId
                WHERE c.EventId <> v.EventId) AS CHAR)
  UNION ALL SELECT '15 representative votes without a voterlogs submission row', '0',
         CAST((SELECT COUNT(*) FROM `RepresentativeVotes` v WHERE NOT EXISTS
                (SELECT 1 FROM `voterlogs` l WHERE l.MemberId = v.MemberId AND l.EventId = v.EventId AND l.HasVoted = 1)) AS CHAR)
  UNION ALL SELECT '16 representative votes recorded for a GENERAL_ASSEMBLY event', '0',
         CAST((SELECT COUNT(*) FROM `RepresentativeVotes` v JOIN `Events` e ON e.EventId = v.EventId WHERE e.EventType <> 'REPRESENTATIVE') AS CHAR)
  UNION ALL SELECT '17 general-assembly votes recorded for a REPRESENTATIVE event', '0',
         CAST((SELECT COUNT(*) FROM `votes` v JOIN `Events` e ON e.EventId = v.EventId WHERE e.EventType = 'REPRESENTATIVE') AS CHAR)
  UNION ALL SELECT '18 roster rows assigned to a position that has no rule in that event', '0',
         CAST((SELECT COUNT(*) FROM `RepresentativeEventCandidates` c WHERE NOT EXISTS
                (SELECT 1 FROM `EventPositionRules` r WHERE r.EventId = c.EventId AND r.Position = c.Position)) AS CHAR)
  UNION ALL SELECT '19 positions holding more candidates than MaxCandidates allows', '0',
         CAST((SELECT COUNT(*) FROM (SELECT c.EventId, c.Position, COUNT(*) AS n, MAX(r.MaxCandidates) AS lim
                FROM `RepresentativeEventCandidates` c JOIN `EventPositionRules` r ON r.EventId = c.EventId AND r.Position = c.Position
                WHERE c.Status = 'APPROVED' GROUP BY c.EventId, c.Position) x WHERE x.lim IS NOT NULL AND x.n > x.lim) AS CHAR)
  UNION ALL SELECT '20 rules with MinVotes > MaxVotes', '0',
         CAST((SELECT COUNT(*) FROM `EventPositionRules` WHERE MinVotes > MaxVotes) AS CHAR)
  UNION ALL SELECT '21 representative ballots whose selections break MinVotes/MaxVotes of a position', '0',
         CAST((SELECT COUNT(*) FROM (
                SELECT v.MemberId, v.EventId, c.Position, COUNT(*) AS picks, MAX(r.MinVotes) AS mn, MAX(r.MaxVotes) AS mx
                FROM `RepresentativeVotes` v
                JOIN `RepresentativeEventCandidates` c ON c.RepresentativeEventCandidateId = v.RepresentativeEventCandidateId
                JOIN `EventPositionRules` r ON r.EventId = c.EventId AND r.Position = c.Position
                GROUP BY v.MemberId, v.EventId, c.Position) b WHERE b.picks < b.mn OR b.picks > b.mx) AS CHAR)
) AS checks
ORDER BY check_name;
