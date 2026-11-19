-- ============================================================================================================
-- 012_must_change_password.sql
--
-- WHAT   Adds Users.MustChangePassword. When an administrator gives an account a password (new user, reset), the
--        Registration sign-in makes the person choose their own password before the desk opens.
--
-- SAFE   * Adds one column with DEFAULT 0: every existing account keeps working exactly as before.
--        * Idempotent (checks information_schema first).
--        * REQUIRED before deploying the API build that contains MustChangePassword (it reads the column at every sign-in).
--
-- HOW    1. mysqldump the database first.  2. Run this file in Workbench on the LIVE database.
-- ROLLBACK  012_must_change_password_rollback.sql   (only together with the previous API build)
-- ============================================================================================================
SET NAMES utf8mb4;

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Users') AND column_name = 'MustChangePassword') = 0,
            'ALTER TABLE `Users` ADD `MustChangePassword` tinyint(1) NOT NULL DEFAULT 0',
            'SELECT ''column Users.MustChangePassword already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

SELECT 'Users.MustChangePassword ready' AS info, SUM(MustChangePassword) AS must_change_now, COUNT(*) AS users FROM `Users`;
