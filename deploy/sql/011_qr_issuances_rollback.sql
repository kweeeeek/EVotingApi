-- Rollback of 011_qr_issuances.sql. Run ONLY together with putting back the API build from before 2026-11-19 (QR per election).
-- It deletes the per-election QR history (who/which desk issued each slip). Members.QrIssuedAt is not touched.
DROP TABLE IF EXISTS `QrIssuances`;
SELECT 'QrIssuances removed' AS info;
