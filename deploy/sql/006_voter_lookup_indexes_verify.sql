-- ============================================================================================================
-- 006_voter_lookup_indexes_verify.sql          READ-ONLY - run after 006_voter_lookup_indexes.sql
--
-- Every row of the result is one check:  PASS when `actual` equals `expected`, otherwise FAIL. Nothing is modified.
--   mysql -u <user> <database> < 006_voter_lookup_indexes_verify.sql
-- ============================================================================================================
SET NAMES utf8mb4;

SELECT check_name, expected, actual, IF(expected = actual, 'PASS', 'FAIL') AS result FROM (

  SELECT '01 migration recorded in __EFMigrationsHistory' AS check_name, '1' AS expected,
         CAST((SELECT COUNT(*) FROM `__EFMigrationsHistory` WHERE MigrationId = '20261104150000_AddVoterLookupIndexes') AS CHAR) AS actual

  UNION ALL SELECT '02 Members.IX_Members_VoterLookup_Cover columns', 'IsActive,IsEligibleToVote,MemberName,AccountCode,BranchId',
         IFNULL((SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics
                  WHERE table_schema = DATABASE() AND LOWER(table_name) = 'members' AND index_name = 'IX_Members_VoterLookup_Cover'), '<missing>')

  UNION ALL SELECT '03 voterlogs.IX_VoterLogs_Event_Voted_Member columns', 'EventId,HasVoted,MemberId',
         IFNULL((SELECT GROUP_CONCAT(column_name ORDER BY seq_in_index) FROM information_schema.statistics
                  WHERE table_schema = DATABASE() AND LOWER(table_name) = 'voterlogs' AND index_name = 'IX_VoterLogs_Event_Voted_Member'), '<missing>')

  -- both are plain (non-unique) secondary indexes: they cannot reject an existing or future row
  UNION ALL SELECT '04 both indexes are non-unique', '2',
         CAST((SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics WHERE table_schema = DATABASE() AND non_unique = 1
                  AND index_name IN ('IX_Members_VoterLookup_Cover', 'IX_VoterLogs_Event_Voted_Member')) AS CHAR)

  -- the order the list is sorted by must come straight out of the index: IsActive, IsEligibleToVote, then MemberName
  UNION ALL SELECT '05 index order lets MySQL read members in name order', '3',
         CAST((SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND LOWER(table_name) = 'members'
                  AND index_name = 'IX_Members_VoterLookup_Cover' AND ((seq_in_index = 1 AND column_name = 'IsActive')
                  OR (seq_in_index = 2 AND column_name = 'IsEligibleToVote') OR (seq_in_index = 3 AND column_name = 'MemberName'))) AS CHAR)

) AS checks ORDER BY check_name;

-- Optional, read-only: how the optimizer reads the first page of an election. Replace 1 by a real EventId and look for
-- "Covering index scan ... IX_Members_VoterLookup_Cover" (no "Table scan", no "Filesort") in the result.
-- EXPLAIN FORMAT=TREE
-- SELECT m.MemberId FROM Members m WHERE m.IsActive = TRUE AND m.IsEligibleToVote = TRUE
--   AND m.BranchId IN (SELECT BranchId FROM EventBranches WHERE EventId = 1) ORDER BY m.MemberName, m.MemberId LIMIT 50;
