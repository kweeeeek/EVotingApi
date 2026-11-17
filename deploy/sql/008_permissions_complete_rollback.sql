-- ============================================================================================================
-- 008_permissions_complete_rollback.sql
-- Removes ONLY the three permissions added by 008 (DASHBOARD_VIEW, RESULTS_VIEW, VOTER_LOOKUP_VIEW) and their grants.
-- Use it only together with going back to the API build that checks EVENT_VIEW for those screens; with the new build
-- deployed, removing them locks every role out of the Dashboard, Results and Voters Lookup.
-- The older codes that 008 may have filled in are left alone (the code needs them).
-- ============================================================================================================
SET NAMES utf8mb4;

DELETE rp FROM `RolePermissions` rp
JOIN `Permissions` p ON p.`Id` = rp.`PermissionId`
WHERE p.`Code` IN ('DASHBOARD_VIEW', 'RESULTS_VIEW', 'VOTER_LOOKUP_VIEW');

DELETE FROM `Permissions` WHERE `Code` IN ('DASHBOARD_VIEW', 'RESULTS_VIEW', 'VOTER_LOOKUP_VIEW');

SELECT COUNT(*) AS permissions_now FROM `Permissions`;
