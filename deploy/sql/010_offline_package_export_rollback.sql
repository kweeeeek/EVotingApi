-- Removes ONLY the permission added by 010 (OFFLINE_PACKAGE_EXPORT) and its grants. Packages already made are NOT affected.
SET NAMES utf8mb4;
DELETE rp FROM `RolePermissions` rp JOIN `Permissions` p ON p.`Id` = rp.`PermissionId` WHERE p.`Code` = 'OFFLINE_PACKAGE_EXPORT';
DELETE FROM `Permissions` WHERE `Code` = 'OFFLINE_PACKAGE_EXPORT';
