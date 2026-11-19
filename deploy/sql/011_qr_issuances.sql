-- ============================================================================================================
-- 011_qr_issuances.sql
--
-- WHAT   Creates table QrIssuances: every Registration QR slip, PER ELECTION, with the desk number (1-30) and the user.
--        Until now only Members.QrIssuedAt existed (one "last QR" per member, overwritten by every election), so a member
--        registered at the Representative Assembly looked "already issued" at the General Assembly.
--
-- SAFE   * Creates one new table. No existing column, table or row is changed (Members.QrIssuedAt stays and is still written).
--        * Idempotent (CREATE TABLE IF NOT EXISTS).
--        * Rows are removed together with their election or member (ON DELETE CASCADE), so deleting either keeps working.
--        * REQUIRED before deploying the API build that contains QrIssuance: Registration fails while this table is missing.
--
-- HOW    1. mysqldump the database first.  2. Run this file in Workbench on the LIVE database (not while an election is open).
-- ROLLBACK  011_qr_issuances_rollback.sql   (only together with the previous API build)
-- ============================================================================================================
SET NAMES utf8mb4;

CREATE TABLE IF NOT EXISTS `QrIssuances` (
    `QrIssuanceId`  bigint      NOT NULL AUTO_INCREMENT,
    `EventId`       int         NOT NULL,
    `MemberId`      int         NOT NULL,
    `DeskNo`        int         NULL,
    `IssuedBy`      varchar(60) CHARACTER SET utf8mb4 NOT NULL,
    `IssuedAtUtc`   datetime(6) NOT NULL,
    `RecordedAtUtc` datetime(6) NOT NULL,
    `Offline`       tinyint(1)  NOT NULL DEFAULT 0,
    CONSTRAINT `PK_QrIssuances` PRIMARY KEY (`QrIssuanceId`),
    CONSTRAINT `FK_QrIssuances_Events`  FOREIGN KEY (`EventId`)  REFERENCES `Events` (`EventId`)   ON DELETE CASCADE,
    CONSTRAINT `FK_QrIssuances_Members` FOREIGN KEY (`MemberId`) REFERENCES `Members` (`MemberId`) ON DELETE CASCADE,
    KEY `IX_QrIssuances_Event_Member` (`EventId`, `MemberId`),
    KEY `IX_QrIssuances_Event_Desk_Time` (`EventId`, `DeskNo`, `IssuedAtUtc`)
) CHARACTER SET=utf8mb4;

SELECT 'QrIssuances ready' AS info, COUNT(*) AS rows_now FROM `QrIssuances`;
