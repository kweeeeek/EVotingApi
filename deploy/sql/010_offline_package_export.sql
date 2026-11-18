-- ============================================================================================================
-- 010_offline_package_export.sql
--
-- WHAT   Adds ONE permission: OFFLINE_PACKAGE_EXPORT ("Make Kiosk USB Package") - Admin > Results > Kiosk USB Package.
--        It is given to RoleId 1 (the seeded super administrator) ONLY. Give it to another role on the Roles screen
--        if that role must make kiosk packages (they hold the personal data of every member).
--
-- SAFE   Only INSERTs. No row is changed or deleted. Idempotent: running it twice adds nothing the second time.
--        Run it BEFORE (or right after) deploying the API build that has the package; until it is run, nobody may make one.
--
-- HOW    1. mysqldump the database first.  2. Run on the LIVE database in Workbench (or mysql ... < this file).
--        3. Run check_permissions.sql afterwards: result 1 must be empty.
-- ROLLBACK  010_offline_package_export_rollback.sql
-- ============================================================================================================
SET NAMES utf8mb4;

INSERT INTO `Permissions` (`Code`, `Name`)
SELECT 'OFFLINE_PACKAGE_EXPORT', 'Make Kiosk USB Package'
WHERE NOT EXISTS (SELECT 1 FROM `Permissions` p WHERE p.`Code` = 'OFFLINE_PACKAGE_EXPORT');

INSERT INTO `RolePermissions` (`RoleId`, `PermissionId`)
SELECT r.`RoleId`, p.`Id`
FROM `Roles` r JOIN `Permissions` p ON p.`Code` = 'OFFLINE_PACKAGE_EXPORT'
WHERE r.`RoleId` = 1
  AND NOT EXISTS (SELECT 1 FROM `RolePermissions` x WHERE x.`RoleId` = r.`RoleId` AND x.`PermissionId` = p.`Id`);

SELECT r.`RoleId`, r.`RoleName`, p.`Code`
FROM `RolePermissions` rp JOIN `Roles` r ON r.`RoleId` = rp.`RoleId` JOIN `Permissions` p ON p.`Id` = rp.`PermissionId`
WHERE p.`Code` = 'OFFLINE_PACKAGE_EXPORT';
