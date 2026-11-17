-- check_permissions.sql  (READ-ONLY: walang binabago)
-- Patakbuhin sa LIVE database sa Workbench. Tatlong resulta ang lalabas.
-- Ang 33 code sa ibaba ay ang mismong listahan sa EVoting.Api/Security/PermissionCodes.cs
-- (pareho rin sa Admin app at sa DefaultDatabase.sql).

DROP TEMPORARY TABLE IF EXISTS tmp_expected_permissions;
CREATE TEMPORARY TABLE tmp_expected_permissions (Code VARCHAR(64) PRIMARY KEY, Name VARCHAR(128))
  DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
INSERT INTO tmp_expected_permissions VALUES
 ('ROLE_MANAGER','Manage Role Permissions'),('BRANCH_VIEW','View Branches'),('BRANCH_ADD','Add Branch'),
 ('BRANCH_EDIT','Edit Branch'),('BRANCH_DELETE','Delete Branch'),('USER_VIEW','View Users'),('USER_ADD','Add User'),
 ('USER_EDIT','Edit User'),('USER_DELETE','Delete User'),('ROLE_VIEW','View Roles'),('ROLE_ADD','Add Role'),
 ('ROLE_EDIT','Edit Role'),('ROLE_DELETE','Delete Role'),('MEMBER_VIEW','View Members'),('MEMBER_ADD','Add Member'),
 ('MEMBER_EDIT','Edit Member'),('MEMBER_DELETE','Delete Member'),('EVENT_VIEW','View Events'),('EVENT_ADD','Add Event'),
 ('EVENT_EDIT','Edit Event'),('EVENT_DELETE','Delete Event'),('REPORT_VIEW','View Reports'),('REPORT_EXPORT','Export Reports'),
 ('CANDIDATE_VIEW','View Candidates'),('CANDIDATE_ADD','Add Candidate'),('CANDIDATE_EDIT','Edit Candidate'),
 ('CANDIDATE_DELETE','Delete Candidate'),('AUDIT_VIEW','View Audit Logs'),
 ('SETTINGS_ELIGIBILITY_VIEW','View Member Eligibility Settings'),('SETTINGS_ELIGIBILITY_EDIT','Edit Member Eligibility Settings'),
 -- bago (008): sariling permission ng Dashboard, Results at Voters Lookup
 ('DASHBOARD_VIEW','View Live Dashboard'),('RESULTS_VIEW','View Election Results'),('VOTER_LOOKUP_VIEW','View Voter Lookup');

-- 1) KULANG sa database (kailangan ng code pero wala sa Permissions table)
SELECT e.Code AS kulang_na_permission, e.Name
FROM tmp_expected_permissions e
LEFT JOIN Permissions p ON p.Code = e.Code
WHERE p.Id IS NULL
ORDER BY e.Code;

-- 2) SOBRA / DOBLE sa database (wala sa code, o higit sa isang row ang iisang code)
SELECT x.Code, x.bilang,
       CASE WHEN x.nasa_code = 0 THEN 'hindi ginagamit ng code' ELSE 'DOBLE' END AS problema
FROM (
    SELECT p.Code, COUNT(*) AS bilang,
           (SELECT COUNT(*) FROM tmp_expected_permissions e WHERE e.Code = p.Code) AS nasa_code
    FROM Permissions p
    GROUP BY p.Code
) AS x
WHERE x.nasa_code = 0 OR x.bilang > 1;

-- 3) Bawat role: ilan ang hawak na permission, at alin ang WALA sa kanya
SELECT r.RoleId, r.RoleName AS Role,
       COUNT(DISTINCT p.Code) AS hawak,
       (SELECT GROUP_CONCAT(e.Code ORDER BY e.Code SEPARATOR ', ')
          FROM tmp_expected_permissions e
         WHERE e.Code NOT IN (SELECT p2.Code FROM RolePermissions rp2 JOIN Permissions p2 ON p2.Id = rp2.PermissionId
                               WHERE rp2.RoleId = r.RoleId)) AS wala_sa_role
FROM Roles r
LEFT JOIN RolePermissions rp ON rp.RoleId = r.RoleId
LEFT JOIN Permissions p ON p.Id = rp.PermissionId
GROUP BY r.RoleId, r.RoleName
ORDER BY r.RoleId;

DROP TEMPORARY TABLE IF EXISTS tmp_expected_permissions;
