-- Rollback of 012_must_change_password.sql. Run ONLY together with putting back the API build from before (2026-11-19 QR per election).
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Users') AND column_name = 'MustChangePassword') = 1,
            'ALTER TABLE `Users` DROP COLUMN `MustChangePassword`',
            'SELECT ''column Users.MustChangePassword not present - nothing to do'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;
