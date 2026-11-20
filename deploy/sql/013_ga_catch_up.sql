-- ============================================================================================================
-- 013_ga_catch_up.sql
--
-- WHAT   Adds Events.IsCatchUp. A General Assembly event with IsCatchUp = 1 is the "last chance" election after the regional
--        General Assemblies (e.g. North and South): a member who already voted in an earlier General Assembly (started before
--        it, within 180 days) may not vote in it. Set with the Admin app (Events > edit > "Catch-up").
--
-- SAFE   * Adds one column with DEFAULT 0: every existing event keeps working exactly as before.
--        * Idempotent (checks information_schema first).
--        * REQUIRED before deploying the API build that contains the catch-up election (it reads the column on every event).
--
-- HOW    1. mysqldump the database first.  2. Run this file in Workbench on the LIVE database.
-- ROLLBACK  013_ga_catch_up_rollback.sql   (only together with the previous API build)
-- ============================================================================================================
SET NAMES utf8mb4;

SET @ddl := IF((SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND LOWER(table_name) = LOWER('Events') AND column_name = 'IsCatchUp') = 0,
            'ALTER TABLE `Events` ADD `IsCatchUp` tinyint(1) NOT NULL DEFAULT 0',
            'SELECT ''column Events.IsCatchUp already present - skipped'' AS info');
PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

SELECT 'Events.IsCatchUp ready' AS info, SUM(IsCatchUp) AS catch_up_events, COUNT(*) AS events FROM `Events`;
