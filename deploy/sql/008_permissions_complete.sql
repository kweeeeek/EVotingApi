-- ============================================================================================================
-- 008_permissions_complete.sql
--
-- WHAT   1. Makes the Permissions table hold EVERY permission the API checks (33 codes): the 30 that already existed
--           in code (any that are missing on this database are added) and three new ones that give screens that
--           used to ride on EVENT_VIEW their own switch:
--             DASHBOARD_VIEW     Live Dashboard
--             RESULTS_VIEW       Results (General Assembly and Representative)
--             VOTER_LOOKUP_VIEW  Voters Lookup (who voted, QR issued, branch turnout, receipt)
--           The Reports page uses the existing REPORT_VIEW / REPORT_EXPORT.
--        2. Nobody loses access on the day of the upgrade: every role that holds EVENT_VIEW today is ALSO given the three
--           new permissions (that is exactly what EVENT_VIEW allowed before). Take them away afterwards in
--           Admin > Maintenance > Permissions for the roles that should not have them.
--        3. RoleId 1 (the seeded super administrator) is given all 33.
--
-- SAFE   * Only INSERTs. No row is changed or deleted. Idempotent: running it twice adds nothing the second time.
--        * Run it BEFORE deploying the API build that checks the new codes (otherwise only roles without those
--          rows are refused the Dashboard / Results / Voters Lookup until it is run).
--
-- HOW    1. mysqldump the database first.  2. Run on the LIVE database in Workbench (or mysql ... < this file).
--        3. Run check_permissions.sql afterwards: result 1 must be empty.
-- ROLLBACK  008_permissions_complete_rollback.sql (removes only the three new codes and their grants).
-- ============================================================================================================
SET NAMES utf8mb4;

-- ── 1. every permission the API checks ─────────────────────────────────────────────────────────────────────
DROP TEMPORARY TABLE IF EXISTS tmp_perm;
CREATE TEMPORARY TABLE tmp_perm (Code VARCHAR(64) PRIMARY KEY, Name VARCHAR(128))
  DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
INSERT INTO tmp_perm VALUES
 ('ROLE_MANAGER','Manage Role Permissions'),
 ('BRANCH_VIEW','View Branches'),('BRANCH_ADD','Add Branch'),('BRANCH_EDIT','Edit Branch'),('BRANCH_DELETE','Delete Branch'),
 ('USER_VIEW','View Users'),('USER_ADD','Add User'),('USER_EDIT','Edit User'),('USER_DELETE','Delete User'),
 ('ROLE_VIEW','View Roles'),('ROLE_ADD','Add Role'),('ROLE_EDIT','Edit Role'),('ROLE_DELETE','Delete Role'),
 ('MEMBER_VIEW','View Members'),('MEMBER_ADD','Add Member'),('MEMBER_EDIT','Edit Member'),('MEMBER_DELETE','Delete Member'),
 ('EVENT_VIEW','View Events'),('EVENT_ADD','Add Event'),('EVENT_EDIT','Edit Event'),('EVENT_DELETE','Delete Event'),
 ('REPORT_VIEW','View Reports'),('REPORT_EXPORT','Export Reports'),
 ('CANDIDATE_VIEW','View Candidates'),('CANDIDATE_ADD','Add Candidate'),('CANDIDATE_EDIT','Edit Candidate'),('CANDIDATE_DELETE','Delete Candidate'),
 ('AUDIT_VIEW','View Audit Logs'),
 ('SETTINGS_ELIGIBILITY_VIEW','View Member Eligibility Settings'),('SETTINGS_ELIGIBILITY_EDIT','Edit Member Eligibility Settings'),
 ('DASHBOARD_VIEW','View Live Dashboard'),
 ('RESULTS_VIEW','View Election Results'),
 ('VOTER_LOOKUP_VIEW','View Voter Lookup');

-- what is missing on THIS database before the insert (for the record)
SELECT t.Code AS added_permission, t.Name FROM tmp_perm t
WHERE NOT EXISTS (SELECT 1 FROM `Permissions` p WHERE p.`Code` = t.Code) ORDER BY t.Code;

INSERT INTO `Permissions` (`Code`, `Name`)
SELECT t.Code, t.Name FROM tmp_perm t
WHERE NOT EXISTS (SELECT 1 FROM `Permissions` p WHERE p.`Code` = t.Code);

DROP TEMPORARY TABLE IF EXISTS tmp_perm;

-- ── 2. roles that hold EVENT_VIEW keep what EVENT_VIEW used to allow ──────────────────────────────────────────
INSERT INTO `RolePermissions` (`RoleId`, `PermissionId`)
SELECT DISTINCT rp.`RoleId`, np.`Id`
FROM `RolePermissions` rp
JOIN `Permissions` ev ON ev.`Id` = rp.`PermissionId` AND ev.`Code` = 'EVENT_VIEW'
JOIN `Permissions` np ON np.`Code` IN ('DASHBOARD_VIEW', 'RESULTS_VIEW', 'VOTER_LOOKUP_VIEW')
WHERE NOT EXISTS (SELECT 1 FROM `RolePermissions` x WHERE x.`RoleId` = rp.`RoleId` AND x.`PermissionId` = np.`Id`);

-- ── 3. the seeded super administrator holds everything ───────────────────────────────────────────────────────
INSERT INTO `RolePermissions` (`RoleId`, `PermissionId`)
SELECT r.`RoleId`, p.`Id`
FROM `Roles` r CROSS JOIN `Permissions` p
WHERE r.`RoleId` = 1
  AND NOT EXISTS (SELECT 1 FROM `RolePermissions` x WHERE x.`RoleId` = r.`RoleId` AND x.`PermissionId` = p.`Id`);

-- ── result ───────────────────────────────────────────────────────────────────────────────────────────────────
SELECT COUNT(*) AS permissions_now FROM `Permissions`;
SELECT r.`RoleId`, r.`RoleName`, COUNT(rp.`PermissionId`) AS permissions_held
FROM `Roles` r LEFT JOIN `RolePermissions` rp ON rp.`RoleId` = r.`RoleId`
GROUP BY r.`RoleId`, r.`RoleName` ORDER BY r.`RoleId`;
