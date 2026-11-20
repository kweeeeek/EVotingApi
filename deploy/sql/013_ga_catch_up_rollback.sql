-- Rollback of 013_ga_catch_up.sql. Run ONLY together with putting back the API build from before (2026-11-19 password change).
SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Events') AND column_name = 'IsCatchUp') = 1,
            'ALTER TABLE `Events` DROP COLUMN `IsCatchUp`',
            'SELECT ''column Events.IsCatchUp not present - nothing to do'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;
