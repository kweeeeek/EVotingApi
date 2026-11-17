-- ============================================================================================================
-- 007_voting_passes.sql
--
-- WHAT   Creates table VotingPasses: the random voting pass (EVP1-...) printed in the Registration QR code.
--        Only the SHA-256 of a pass is stored (PassHash), never the pass itself.
--
-- SAFE   * Creates one new table. No existing column, table or row is changed.
--        * Idempotent (CREATE TABLE IF NOT EXISTS).
--        * REQUIRED before deploying the API build that contains VotingPass: member delete removes a member's passes
--          first and fails while this table is missing.
--
-- HOW    1. mysqldump the database first.  2. Test on a COPY.  3. mysql -u <admin> <database> < 007_voting_passes.sql
-- ROLLBACK  DROP TABLE IF EXISTS `VotingPasses`;   (only while the new API build is not deployed)
-- ============================================================================================================
SET NAMES utf8mb4;

CREATE TABLE IF NOT EXISTS `VotingPasses` (
    `VotingPassId` bigint      NOT NULL AUTO_INCREMENT,
    `EventId`      int         NOT NULL,
    `MemberId`     int         NOT NULL,
    `PassHash`     varchar(64) CHARACTER SET utf8mb4 NOT NULL,
    `IssuedAt`     datetime(6) NOT NULL,
    `IssuedBy`     varchar(60) CHARACTER SET utf8mb4 NOT NULL,
    `RevokedAt`    datetime(6) NULL,
    CONSTRAINT `PK_VotingPasses` PRIMARY KEY (`VotingPassId`),
    CONSTRAINT `FK_VotingPasses_Events`  FOREIGN KEY (`EventId`)  REFERENCES `Events` (`EventId`)   ON DELETE RESTRICT,
    CONSTRAINT `FK_VotingPasses_Members` FOREIGN KEY (`MemberId`) REFERENCES `Members` (`MemberId`) ON DELETE RESTRICT,
    UNIQUE KEY `UQ_VotingPasses_PassHash` (`PassHash`),
    KEY `IX_VotingPasses_Event_Member` (`EventId`, `MemberId`)
) CHARACTER SET=utf8mb4;

SELECT 'VotingPasses ready' AS info, COUNT(*) AS rows_now FROM `VotingPasses`;
