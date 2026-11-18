-- Removes ONLY the permission added by 009 (OFFLINE_VOTE_IMPORT) and its grants. Votes already imported are NOT touched.
SET NAMES utf8mb4;
DELETE rp FROM `RolePermissions` rp JOIN `Permissions` p ON p.`Id` = rp.`PermissionId` WHERE p.`Code` = 'OFFLINE_VOTE_IMPORT';
DELETE FROM `Permissions` WHERE `Code` = 'OFFLINE_VOTE_IMPORT';
