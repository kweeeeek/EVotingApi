-- ============================================================================================================
-- rrms-complete-schema-2026-11-20.sql
-- The COMPLETE E-Voting database structure as of 2026-11-20: the 2026-11-12 database + migrations 005..013 (011, 012 and 013 at the end).
-- Holds NO member, user, vote or audit data. Holds only reference rows: Permissions (35), Roles, RolePermissions,
-- Branches, SystemSettings and the EF migrations history.
-- Use it to create an EMPTY database (new server, staging, testing). For the real data use a dump of the LIVE
-- database (dump-live-database.bat on the server).
-- WARNING: it starts with CREATE DATABASE `rrms` / USE `rrms`. Do NOT run it on the live server.
-- ============================================================================================================
-- MySQL dump 10.13  Distrib 8.0.46, for Linux (x86_64)
--
-- MySQL dump 10.13  Distrib 8.0.46, for Linux (x86_64)
--
-- Host: localhost    Database: rrms
-- ------------------------------------------------------
-- Server version	8.0.46

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Current Database: `rrms`
--

CREATE DATABASE /*!32312 IF NOT EXISTS*/ `rrms` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */ /*!80016 DEFAULT ENCRYPTION='N' */;

USE `rrms`;

--
-- Table structure for table `__efmigrationshistory`
--

DROP TABLE IF EXISTS `__efmigrationshistory`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `__efmigrationshistory` (
  `MigrationId` varchar(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `ProductVersion` varchar(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  PRIMARY KEY (`MigrationId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `auditlogs`
--

DROP TABLE IF EXISTS `auditlogs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `auditlogs` (
  `AuditId` bigint NOT NULL AUTO_INCREMENT,
  `UserId` int DEFAULT NULL,
  `Username` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Action` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `EntityType` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `EntityId` int DEFAULT NULL,
  `Details` varchar(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `IpAddress` varchar(45) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `OccurredAtUtc` datetime(6) NOT NULL,
  PRIMARY KEY (`AuditId`),
  KEY `IX_AuditLogs_UserId_Date` (`UserId`,`OccurredAtUtc`),
  KEY `IX_AuditLogs_Entity` (`EntityType`,`EntityId`)
) ENGINE=InnoDB AUTO_INCREMENT=220 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `branches`
--

DROP TABLE IF EXISTS `branches`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `branches` (
  `BranchId` int NOT NULL AUTO_INCREMENT,
  `BranchName` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `BranchAddress` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `BranchContactNumber` varchar(15) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  `DeletedAt` datetime(6) NOT NULL,
  `IsActive` tinyint(1) NOT NULL,
  PRIMARY KEY (`BranchId`)
) ENGINE=InnoDB AUTO_INCREMENT=529 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `candidacies`
--

DROP TABLE IF EXISTS `candidacies`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `candidacies` (
  `CandidacyId` int NOT NULL AUTO_INCREMENT,
  `MemberId` int DEFAULT NULL,
  `WalkInCandidateName` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Position` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Reason` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `CellNo` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `TelNo` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CivilStatus` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Gender` varchar(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `DateOfBirth` datetime(6) NOT NULL,
  `HighestEducation` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `PreviousPositions` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `SpecialSkills` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CandidateImage` longblob,
  `Status` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `RejectionReason` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci,
  `DateFiled` datetime(6) NOT NULL,
  `DateApproved` datetime(6) DEFAULT NULL,
  `DateRejected` datetime(6) DEFAULT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  `DeletedAt` datetime(6) DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL,
  PRIMARY KEY (`CandidacyId`),
  KEY `IX_Candidacies_MemberId_Position` (`MemberId`,`Position`),
  KEY `IX_Candidacies_Status` (`Status`),
  CONSTRAINT `FK_Candidacies_Members_MemberId` FOREIGN KEY (`MemberId`) REFERENCES `members` (`MemberId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `eventbranches`
--

DROP TABLE IF EXISTS `eventbranches`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `eventbranches` (
  `EventBranchId` int NOT NULL AUTO_INCREMENT,
  `EventId` int NOT NULL,
  `BranchId` int NOT NULL,
  PRIMARY KEY (`EventBranchId`),
  UNIQUE KEY `IX_EventBranches_EventId_BranchId` (`EventId`,`BranchId`),
  KEY `IX_EventBranches_BranchId` (`BranchId`),
  CONSTRAINT `FK_EventBranches_Branches_BranchId` FOREIGN KEY (`BranchId`) REFERENCES `branches` (`BranchId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_EventBranches_Events_EventId` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=63 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `eventpositionrules`
--

DROP TABLE IF EXISTS `eventpositionrules`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `eventpositionrules` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `EventId` int NOT NULL,
  `Position` varchar(100) NOT NULL,
  `MinVotes` int NOT NULL DEFAULT '1',
  `MaxVotes` int NOT NULL DEFAULT '1',
  `TotalSeats` int NOT NULL DEFAULT '1',
  `MaxCandidates` int DEFAULT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `UQ_EventPositionRules_EventId_Position` (`EventId`,`Position`),
  KEY `FK_EventPositionRules_Events_EventId` (`EventId`),
  CONSTRAINT `FK_EventPositionRules_Events_EventId` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE CASCADE,
  CONSTRAINT `CK_EventPositionRules_MaxCandidates` CHECK (((`MaxCandidates` is null) or (`MaxCandidates` > 0)))
) ENGINE=InnoDB AUTO_INCREMENT=28 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `events`
--

DROP TABLE IF EXISTS `events`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `events` (
  `EventId` int NOT NULL AUTO_INCREMENT,
  `Title` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Venue` varchar(150) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `ScheduleFrom` datetime(6) NOT NULL,
  `ScheduleTo` datetime(6) NOT NULL,
  `Status` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  `IsActive` tinyint(1) NOT NULL,
  `EligibilityMinSavingsDeposit` decimal(18,2) DEFAULT NULL,
  `EligibilityMinShareCapital` decimal(18,2) DEFAULT NULL,
  `EventType` varchar(30) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL DEFAULT 'GENERAL_ASSEMBLY',
  `RosterLockedAt` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`EventId`),
  CONSTRAINT `CK_Events_EventType` CHECK ((`EventType` in (_utf8mb4'GENERAL_ASSEMBLY',_utf8mb4'REPRESENTATIVE')))
) ENGINE=InnoDB AUTO_INCREMENT=17 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `members`
--

DROP TABLE IF EXISTS `members`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `members` (
  `MemberId` int NOT NULL AUTO_INCREMENT,
  `AccountCode` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `MemberName` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `MemberAddress` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `MemberContactNumber` varchar(15) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `BranchId` int NOT NULL,
  `ShareCapital` decimal(18,2) NOT NULL,
  `SavingsDeposit` decimal(18,2) NOT NULL,
  `LoanStatus` int NOT NULL,
  `IsEligibleToVote` tinyint(1) NOT NULL,
  `VotingRemarks` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `QrIssuedAt` datetime(6) DEFAULT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  `DeletedAt` datetime(6) DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL,
  `MemberImage` longblob,
  PRIMARY KEY (`MemberId`),
  UNIQUE KEY `IX_Members_AccountCode` (`AccountCode`),
  KEY `IX_Members_CreatedAt` (`CreatedAt`),
  KEY `IX_Members_MemberName` (`MemberName`),
  KEY `IX_Members_Branch_Active_Eligible_Name` (`BranchId`,`IsActive`,`IsEligibleToVote`,`MemberName`),
  KEY `IX_Members_IsActive_IsEligible` (`IsActive`,`IsEligibleToVote`),
  KEY `IX_Members_QrIssuedAt` (`QrIssuedAt`),
  KEY `IX_Members_VoterLookup_Cover` (`IsActive`,`IsEligibleToVote`,`MemberName`,`AccountCode`,`BranchId`),
  CONSTRAINT `FK_Members_Branches_BranchId` FOREIGN KEY (`BranchId`) REFERENCES `branches` (`BranchId`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=67516 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `permissions`
--

DROP TABLE IF EXISTS `permissions`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `permissions` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Code` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Name` longtext CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  PRIMARY KEY (`Id`)
) ENGINE=InnoDB AUTO_INCREMENT=36 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `representativecandidateimportbatches`
--

DROP TABLE IF EXISTS `representativecandidateimportbatches`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `representativecandidateimportbatches` (
  `ImportBatchId` int NOT NULL AUTO_INCREMENT,
  `EventId` int NOT NULL,
  `FileName` varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `FileSha256` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Status` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `ExpectedRows` int NOT NULL,
  `TotalRows` int NOT NULL,
  `ValidRows` int NOT NULL,
  `InvalidRows` int NOT NULL,
  `ConflictRows` int NOT NULL,
  `NewProfiles` int NOT NULL,
  `ReusedProfiles` int NOT NULL,
  `NewAssignments` int NOT NULL,
  `Message` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CreatedByUserId` int DEFAULT NULL,
  `CreatedByUsername` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `ValidatedAt` datetime(6) DEFAULT NULL,
  `CommittedAt` datetime(6) DEFAULT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  PRIMARY KEY (`ImportBatchId`),
  KEY `IX_RepImportBatches_Event_CreatedAt` (`EventId`,`CreatedAt`),
  CONSTRAINT `FK_RepImportBatches_Events` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE RESTRICT,
  CONSTRAINT `CK_RepImportBatches_Status` CHECK ((`Status` in (_utf8mb4'UPLOADED',_utf8mb4'VALIDATING',_utf8mb4'VALIDATED',_utf8mb4'INVALID',_utf8mb4'COMMITTED',_utf8mb4'FAILED',_utf8mb4'CANCELLED')))
) ENGINE=InnoDB AUTO_INCREMENT=5 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `representativecandidateimportrows`
--

DROP TABLE IF EXISTS `representativecandidateimportrows`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `representativecandidateimportrows` (
  `ImportRowId` bigint NOT NULL AUTO_INCREMENT,
  `ImportBatchId` int NOT NULL,
  `RowNumber` int NOT NULL,
  `CandidateCode` varchar(30) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CandidateName` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `AccountCode` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Position` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CellNo` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `TelNo` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CivilStatus` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Gender` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `DateOfBirth` date DEFAULT NULL,
  `HighestEducation` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `PreviousPositions` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `SpecialSkills` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Reason` varchar(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `BallotNumber` int DEFAULT NULL,
  `RowStatus` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `PlannedAction` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `MatchedProfileId` int DEFAULT NULL,
  `Resolution` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `ErrorCodes` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `ErrorMessage` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  PRIMARY KEY (`ImportRowId`),
  UNIQUE KEY `UQ_RepImportRows_Batch_RowNumber` (`ImportBatchId`,`RowNumber`),
  KEY `IX_RepImportRows_Batch_CandidateCode` (`ImportBatchId`,`CandidateCode`),
  KEY `IX_RepImportRows_Batch_RowStatus` (`ImportBatchId`,`RowStatus`),
  CONSTRAINT `FK_RepImportRows_Batches` FOREIGN KEY (`ImportBatchId`) REFERENCES `representativecandidateimportbatches` (`ImportBatchId`) ON DELETE RESTRICT,
  CONSTRAINT `CK_RepImportRows_RowStatus` CHECK ((`RowStatus` in (_utf8mb4'PENDING',_utf8mb4'VALID',_utf8mb4'INVALID',_utf8mb4'CONFLICT',_utf8mb4'COMMITTED',_utf8mb4'SKIPPED')))
) ENGINE=InnoDB AUTO_INCREMENT=3527 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `representativecandidatephotos`
--

DROP TABLE IF EXISTS `representativecandidatephotos`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `representativecandidatephotos` (
  `RepresentativeCandidateProfileId` int NOT NULL,
  `Image` mediumblob NOT NULL,
  `Thumbnail` mediumblob,
  `ContentType` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Sha256` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  PRIMARY KEY (`RepresentativeCandidateProfileId`),
  CONSTRAINT `FK_RepCandidatePhotos_Profiles` FOREIGN KEY (`RepresentativeCandidateProfileId`) REFERENCES `representativecandidateprofiles` (`RepresentativeCandidateProfileId`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `representativecandidateprofiles`
--

DROP TABLE IF EXISTS `representativecandidateprofiles`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `representativecandidateprofiles` (
  `RepresentativeCandidateProfileId` int NOT NULL AUTO_INCREMENT,
  `CandidateCode` varchar(30) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `MemberId` int DEFAULT NULL,
  `CandidateName` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `CellNo` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `TelNo` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `CivilStatus` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Gender` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `DateOfBirth` date DEFAULT NULL,
  `HighestEducation` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `PreviousPositions` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `SpecialSkills` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `PhotoUpdatedAt` datetime(6) DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  PRIMARY KEY (`RepresentativeCandidateProfileId`),
  UNIQUE KEY `UQ_RepCandidateProfiles_CandidateCode` (`CandidateCode`),
  UNIQUE KEY `UQ_RepCandidateProfiles_MemberId` (`MemberId`),
  KEY `IX_RepCandidateProfiles_CandidateName` (`CandidateName`),
  CONSTRAINT `FK_RepCandidateProfiles_Members` FOREIGN KEY (`MemberId`) REFERENCES `members` (`MemberId`) ON DELETE RESTRICT
) ENGINE=InnoDB AUTO_INCREMENT=2023 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `representativeeventcandidates`
--

DROP TABLE IF EXISTS `representativeeventcandidates`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `representativeeventcandidates` (
  `RepresentativeEventCandidateId` int NOT NULL AUTO_INCREMENT,
  `EventId` int NOT NULL,
  `RepresentativeCandidateProfileId` int NOT NULL,
  `Position` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Reason` varchar(2000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `BallotNumber` int DEFAULT NULL,
  `Status` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `ImportBatchId` int DEFAULT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  PRIMARY KEY (`RepresentativeEventCandidateId`),
  UNIQUE KEY `UQ_RepEventCandidates_Id_Event` (`RepresentativeEventCandidateId`,`EventId`),
  UNIQUE KEY `UQ_RepEventCandidates_Event_Position_Profile` (`EventId`,`Position`,`RepresentativeCandidateProfileId`),
  UNIQUE KEY `UQ_RepEventCandidates_Event_Position_Ballot` (`EventId`,`Position`,`BallotNumber`),
  KEY `IX_RepEventCandidates_Event_Position_Status` (`EventId`,`Position`,`Status`),
  KEY `IX_RepEventCandidates_ImportBatch` (`ImportBatchId`),
  KEY `IX_RepEventCandidates_Profile` (`RepresentativeCandidateProfileId`),
  CONSTRAINT `FK_RepEventCandidates_Events` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_RepEventCandidates_ImportBatches` FOREIGN KEY (`ImportBatchId`) REFERENCES `representativecandidateimportbatches` (`ImportBatchId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_RepEventCandidates_Profiles` FOREIGN KEY (`RepresentativeCandidateProfileId`) REFERENCES `representativecandidateprofiles` (`RepresentativeCandidateProfileId`) ON DELETE RESTRICT,
  CONSTRAINT `CK_RepEventCandidates_Status` CHECK ((`Status` in (_utf8mb4'APPROVED',_utf8mb4'WITHDRAWN')))
) ENGINE=InnoDB AUTO_INCREMENT=2023 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `representativevotes`
--

DROP TABLE IF EXISTS `representativevotes`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `representativevotes` (
  `RepresentativeVoteId` bigint NOT NULL AUTO_INCREMENT,
  `MemberId` int NOT NULL,
  `EventId` int NOT NULL,
  `RepresentativeEventCandidateId` int NOT NULL,
  `VotedAt` datetime(6) NOT NULL,
  `ReceiptCode` varchar(30) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  PRIMARY KEY (`RepresentativeVoteId`),
  UNIQUE KEY `UQ_RepVotes_Member_Event_Candidate` (`MemberId`,`EventId`,`RepresentativeEventCandidateId`),
  KEY `IX_RepVotes_Candidate_Event` (`RepresentativeEventCandidateId`,`EventId`),
  KEY `IX_RepVotes_Event` (`EventId`),
  KEY `IX_RepVotes_ReceiptCode` (`ReceiptCode`),
  CONSTRAINT `FK_RepVotes_EventCandidates_Candidate_Event` FOREIGN KEY (`RepresentativeEventCandidateId`, `EventId`) REFERENCES `representativeeventcandidates` (`RepresentativeEventCandidateId`, `EventId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_RepVotes_Events` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_RepVotes_Members` FOREIGN KEY (`MemberId`) REFERENCES `members` (`MemberId`) ON DELETE RESTRICT
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `rolepermissions`
--

DROP TABLE IF EXISTS `rolepermissions`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `rolepermissions` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `RoleId` int NOT NULL,
  `PermissionId` int NOT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `IX_RolePermissions_RoleId_PermissionId` (`RoleId`,`PermissionId`),
  KEY `IX_RolePermissions_PermissionId` (`PermissionId`),
  CONSTRAINT `FK_RolePermissions_Permissions_PermissionId` FOREIGN KEY (`PermissionId`) REFERENCES `permissions` (`Id`) ON DELETE CASCADE,
  CONSTRAINT `FK_RolePermissions_Roles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `roles` (`RoleId`) ON DELETE CASCADE
) ENGINE=InnoDB AUTO_INCREMENT=170 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `roles`
--

DROP TABLE IF EXISTS `roles`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `roles` (
  `RoleId` int NOT NULL AUTO_INCREMENT,
  `RoleName` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `RoleDescription` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  `DeletedAt` datetime(6) NOT NULL,
  `IsActive` tinyint(1) NOT NULL,
  PRIMARY KEY (`RoleId`)
) ENGINE=InnoDB AUTO_INCREMENT=6 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `systemsettings`
--

DROP TABLE IF EXISTS `systemsettings`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `systemsettings` (
  `SettingKey` varchar(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `SettingValue` varchar(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Description` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci DEFAULT NULL,
  `Version` int NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  `UpdatedByUserId` int DEFAULT NULL,
  PRIMARY KEY (`SettingKey`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `users`
--

DROP TABLE IF EXISTS `users`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `users` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `FirstName` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `MiddleName` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `LastName` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Address` varchar(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `ContactNo` varchar(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Email` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `Username` varchar(50) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `PasswordHash` varchar(500) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `BranchId` int DEFAULT NULL,
  `RoleId` int DEFAULT NULL,
  `UserImage` longblob NOT NULL,
  `CreatedAt` datetime(6) NOT NULL,
  `UpdatedAt` datetime(6) NOT NULL,
  `DeletedAt` datetime(6) DEFAULT NULL,
  `IsActive` tinyint(1) NOT NULL,
  PRIMARY KEY (`Id`),
  UNIQUE KEY `IX_Users_Username` (`Username`),
  KEY `IX_Users_BranchId` (`BranchId`),
  KEY `IX_Users_Email` (`Email`),
  KEY `IX_Users_IsActive` (`IsActive`),
  KEY `IX_Users_RoleId` (`RoleId`),
  CONSTRAINT `FK_Users_Branches_BranchId` FOREIGN KEY (`BranchId`) REFERENCES `branches` (`BranchId`),
  CONSTRAINT `FK_Users_Roles_RoleId` FOREIGN KEY (`RoleId`) REFERENCES `roles` (`RoleId`)
) ENGINE=InnoDB AUTO_INCREMENT=18 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `voterlogs`
--

DROP TABLE IF EXISTS `voterlogs`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `voterlogs` (
  `VoterLogId` int NOT NULL AUTO_INCREMENT,
  `MemberId` int NOT NULL,
  `EventId` int NOT NULL,
  `HasVoted` tinyint(1) NOT NULL DEFAULT '0',
  `VotedAt` datetime DEFAULT NULL,
  `ReceiptCode` varchar(30) DEFAULT NULL,
  PRIMARY KEY (`VoterLogId`),
  UNIQUE KEY `UQ_VoterLogs_Member_Event` (`MemberId`,`EventId`),
  KEY `IX_VoterLogs_EventId` (`EventId`),
  KEY `IX_VoterLogs_EventId_HasVoted` (`EventId`,`HasVoted`),
  KEY `IX_VoterLogs_Event_Voted_Member` (`EventId`,`HasVoted`,`MemberId`),
  CONSTRAINT `FK_VoterLogs_Events_EventId` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_VoterLogs_Members_MemberId` FOREIGN KEY (`MemberId`) REFERENCES `members` (`MemberId`) ON DELETE RESTRICT
) ENGINE=InnoDB AUTO_INCREMENT=2 DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `votes`
--

DROP TABLE IF EXISTS `votes`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `votes` (
  `VoteId` int NOT NULL AUTO_INCREMENT,
  `MemberId` int NOT NULL,
  `EventId` int NOT NULL,
  `CandidacyId` int NOT NULL,
  `Position` varchar(100) NOT NULL,
  `VotedAt` datetime NOT NULL,
  `ReceiptCode` varchar(30) NOT NULL,
  PRIMARY KEY (`VoteId`),
  UNIQUE KEY `UQ_Votes_Member_Event_Candidacy` (`MemberId`,`EventId`,`CandidacyId`),
  KEY `IX_Votes_EventId` (`EventId`),
  KEY `IX_Votes_CandidacyId` (`CandidacyId`),
  KEY `IX_Votes_ReceiptCode` (`ReceiptCode`),
  KEY `IX_Votes_MemberId_EventId` (`MemberId`,`EventId`),
  KEY `IX_Votes_MemberId_EventId_Position` (`MemberId`,`EventId`,`Position`),
  KEY `IX_Votes_EventId_CandidacyId` (`EventId`,`CandidacyId`),
  CONSTRAINT `FK_Votes_Candidacies_CandidacyId` FOREIGN KEY (`CandidacyId`) REFERENCES `candidacies` (`CandidacyId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_Votes_Events_EventId` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_Votes_Members_MemberId` FOREIGN KEY (`MemberId`) REFERENCES `members` (`MemberId`) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Table structure for table `votingpasses`
--

DROP TABLE IF EXISTS `votingpasses`;
/*!40101 SET @saved_cs_client     = @@character_set_client */;
/*!50503 SET character_set_client = utf8mb4 */;
CREATE TABLE `votingpasses` (
  `VotingPassId` bigint NOT NULL AUTO_INCREMENT,
  `EventId` int NOT NULL,
  `MemberId` int NOT NULL,
  `PassHash` varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `IssuedAt` datetime(6) NOT NULL,
  `IssuedBy` varchar(60) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci NOT NULL,
  `RevokedAt` datetime(6) DEFAULT NULL,
  PRIMARY KEY (`VotingPassId`),
  UNIQUE KEY `UQ_VotingPasses_PassHash` (`PassHash`),
  KEY `FK_VotingPasses_Members` (`MemberId`),
  KEY `IX_VotingPasses_Event_Member` (`EventId`,`MemberId`),
  CONSTRAINT `FK_VotingPasses_Events` FOREIGN KEY (`EventId`) REFERENCES `events` (`EventId`) ON DELETE RESTRICT,
  CONSTRAINT `FK_VotingPasses_Members` FOREIGN KEY (`MemberId`) REFERENCES `members` (`MemberId`) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;
/*!40101 SET character_set_client = @saved_cs_client */;

--
-- Dumping events for database 'rrms'
--

--
-- Dumping routines for database 'rrms'
--
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-11-18  8:16:07
-- MySQL dump 10.13  Distrib 8.0.46, for Linux (x86_64)
--
-- Host: localhost    Database: rrms
-- ------------------------------------------------------
-- Server version	8.0.46

/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */;
/*!40101 SET @OLD_CHARACTER_SET_RESULTS=@@CHARACTER_SET_RESULTS */;
/*!40101 SET @OLD_COLLATION_CONNECTION=@@COLLATION_CONNECTION */;
/*!50503 SET NAMES utf8mb4 */;
/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */;
/*!40103 SET TIME_ZONE='+00:00' */;
/*!40014 SET @OLD_UNIQUE_CHECKS=@@UNIQUE_CHECKS, UNIQUE_CHECKS=0 */;
/*!40014 SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS, FOREIGN_KEY_CHECKS=0 */;
/*!40101 SET @OLD_SQL_MODE=@@SQL_MODE, SQL_MODE='NO_AUTO_VALUE_ON_ZERO' */;
/*!40111 SET @OLD_SQL_NOTES=@@SQL_NOTES, SQL_NOTES=0 */;

--
-- Dumping data for table `permissions`
--

LOCK TABLES `permissions` WRITE;
/*!40000 ALTER TABLE `permissions` DISABLE KEYS */;
INSERT INTO `permissions` VALUES (1,'ROLE_MANAGER','Manage Role Permissions'),(2,'BRANCH_VIEW','View Branches'),(3,'BRANCH_ADD','Add Branch'),(4,'BRANCH_EDIT','Edit Branch'),(5,'BRANCH_DELETE','Delete Branch'),(6,'USER_VIEW','View Users'),(7,'USER_ADD','Add User'),(8,'USER_EDIT','Edit User'),(9,'USER_DELETE','Delete User'),(10,'ROLE_VIEW','View Roles'),(11,'ROLE_ADD','Add Role'),(12,'ROLE_EDIT','Edit Role'),(13,'ROLE_DELETE','Delete Role'),(14,'MEMBER_VIEW','View Members'),(15,'MEMBER_ADD','Add Member'),(16,'MEMBER_EDIT','Edit Member'),(17,'MEMBER_DELETE','Delete Member'),(18,'EVENT_VIEW','View Events'),(19,'EVENT_ADD','Add Event'),(20,'EVENT_EDIT','Edit Event'),(21,'EVENT_DELETE','Delete Event'),(22,'REPORT_VIEW','View Reports'),(23,'REPORT_EXPORT','Export Reports'),(24,'CANDIDATE_VIEW','View Candidates'),(25,'CANDIDATE_ADD','Add Candidate'),(26,'CANDIDATE_EDIT','Edit Candidate'),(27,'CANDIDATE_DELETE','Delete Candidate'),(28,'AUDIT_VIEW','View Audit Logs'),(29,'SETTINGS_ELIGIBILITY_VIEW','View Eligibility'),(30,'SETTINGS_ELIGIBILITY_EDIT','Edit Eligibility'),(31,'DASHBOARD_VIEW','View Live Dashboard'),(32,'RESULTS_VIEW','View Election Results'),(33,'VOTER_LOOKUP_VIEW','View Voter Lookup'),(34,'OFFLINE_VOTE_IMPORT','Import Kiosk Votes from USB'),(35,'OFFLINE_PACKAGE_EXPORT','Make Kiosk USB Package');
/*!40000 ALTER TABLE `permissions` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Dumping data for table `roles`
--

LOCK TABLES `roles` WRITE;
/*!40000 ALTER TABLE `roles` DISABLE KEYS */;
INSERT INTO `roles` VALUES (1,'Admin','System Administrator','0001-01-01 00:00:00.000000','0001-01-01 00:00:00.000000','0001-01-01 00:00:00.000000',0),(2,'Registration In Charge','front desk','2026-05-05 03:02:19.751938','2026-05-05 03:02:19.751938','0001-01-01 00:00:00.000000',1),(3,'Election Commitee','Manage Candidates and events','2026-05-05 03:03:01.139130','2026-05-05 03:03:01.139130','0001-01-01 00:00:00.000000',1),(4,'Poll Clerk','In Charge and Assisting in kioksk','2026-05-05 03:03:38.968080','2026-05-05 03:03:38.968080','0001-01-01 00:00:00.000000',1),(5,'Admin','Manage Users, Branches , Roles, Events, Members','2026-05-05 03:04:11.658676','2026-05-05 03:06:24.161770','0001-01-01 00:00:00.000000',1);
/*!40000 ALTER TABLE `roles` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Dumping data for table `rolepermissions`
--

LOCK TABLES `rolepermissions` WRITE;
/*!40000 ALTER TABLE `rolepermissions` DISABLE KEYS */;
INSERT INTO `rolepermissions` VALUES (1,1,1),(2,1,2),(3,1,3),(4,1,4),(5,1,5),(6,1,6),(7,1,7),(8,1,8),(9,1,9),(10,1,10),(11,1,11),(12,1,12),(13,1,13),(14,1,14),(15,1,15),(16,1,16),(17,1,17),(18,1,18),(19,1,19),(20,1,20),(21,1,21),(22,1,22),(23,1,23),(24,1,24),(25,1,25),(26,1,26),(27,1,27),(71,1,28),(150,1,29),(151,1,30),(157,1,31),(162,1,32),(167,1,33),(168,1,34),(169,1,35),(102,2,14),(101,2,18),(103,2,22),(100,2,23),(156,2,31),(161,2,32),(166,2,33),(110,3,14),(104,3,15),(105,3,17),(109,3,18),(111,3,22),(107,3,23),(108,3,24),(106,3,26),(155,3,31),(160,3,32),(165,3,33),(116,4,14),(117,4,18),(119,4,22),(118,4,23),(154,4,31),(159,4,32),(164,4,33),(145,5,1),(127,5,2),(124,5,3),(126,5,4),(125,5,5),(136,5,6),(133,5,7),(135,5,8),(134,5,9),(141,5,10),(137,5,11),(146,5,12),(147,5,13),(123,5,14),(120,5,15),(122,5,16),(121,5,17),(132,5,18),(129,5,19),(131,5,20),(130,5,21),(143,5,22),(142,5,23),(128,5,24),(144,5,28),(148,5,29),(149,5,30),(153,5,31),(158,5,32),(163,5,33);
/*!40000 ALTER TABLE `rolepermissions` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Dumping data for table `branches`
--

LOCK TABLES `branches` WRITE;
/*!40000 ALTER TABLE `branches` DISABLE KEYS */;
INSERT INTO `branches` VALUES (1,'Head Office','Santa Cruz, Ilocos Sur','9123456789','0001-01-01 00:00:00.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(2,'Main Branch','Poblacion Este, Santa Cruz, Ilocos Sur','09123456790','2026-04-27 15:33:38.000000','2026-06-04 02:53:47.489025','0001-01-01 00:00:00.000000',1),(3,'Bangar Branch','Bangar, La Union','9123456791','2026-05-21 01:04:27.310237','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(4,'Villasis Branch','Villasis, Pangasinan','9123456792','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(5,'Bacnotan Branch','Bacnotan, La Union','9123456793','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(6,'Bauang Branch','Bauang, La Union','9123456794','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(7,'Mangaldan Branch','Mangaldan, Pangasinan','9123456795','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(8,'Bantay Branch','Bantay, Ilocos Sur','9123456796','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(9,'San Juan Branch','San Juan, Ilocos Sur','9123456797','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(10,'Santiago Branch','Santiago, Ilocos Sur','9123456798','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(11,'Narvacan Branch','Narvacan, Ilocos Sur','9123456799','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(12,'Umingan Branch','Umingan, Pangasinan','9123456800','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(13,'Bayambang Branch','Bayambang, Pangasinan','9123456801','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(14,'Mangatarem Branch','Mangatarem, Pangasinan','9123456802','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(15,'Cuyapo Branch','Cuyapo, Tarlac','9123456803','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(16,'Alaminos Branch','Alaminos, Pangasinan','9123456804','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(17,'Paniqui Branch','Paniqui, Tarlac','9123456805','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(18,'Victoria Branch','Victoria, Tarlac','9123456806','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(19,'Pinili Branch','Pinili, Ilocos Norte','9123456807','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(20,'San Nicolas Branch','San Nicolas, Ilocos Norte','9123456808','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(21,'Bangued Branch','Bangued, Abra','9123456809','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(22,'Agoo Branch','Agoo, La Union','9123456810','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(23,'Asingan Branch','Asingan, Pangasinan','9123456811','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(24,'Pozorrubio Branch','Pozorrubio, Pangasinan','9123456812','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(25,'San Jose Branch','San Jose, Nueva Ecija','9123456813','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(26,'Talavera Branch','Talavera, Nueva Ecija','9123456814','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(27,'Infanta Branch','Infanta, Pangasinan','9123456789','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(28,'Baguio Branch','Baguio City, Benguet','9123456789','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(501,'Head Office YTM','Santa Cruz, Ilocos Sur','9123456790','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(502,'Main Branch YTM','Santa Cruz, Ilocos Sur','9123456791','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(503,'Bangar Branch YTM','Bangar, La Union','9123456792','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(504,'Villasis Branch YTM','Villasis, Pangasinan','9123456793','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(505,'Bacnotan Branch YTM','Bacnotan, La Union','9123456794','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(506,'Bauang Branch YTM','Bauang, La Union','9123456795','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(507,'Mangaldan Branch YTM','Mangaldan, Pangasinan','9123456796','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(508,'Bantay Branch YTM','Bantay, Ilocos Sur','9123456797','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(509,'San Juan Branch YTM','San Juan, Ilocos Sur','9123456798','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(510,'Santiago Branch YTM','Santiago, Ilocos Sur','9123456799','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(511,'Narvacan Branch YTM','Narvacan, Ilocos Sur','9123456800','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(512,'Umingan Branch YTM','Umingan, Pangasinan','9123456801','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(513,'Bayambang Branch YTM','Bayambang, Pangasinan','9123456802','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(514,'Mangatarem Branch YTM','Mangatarem, Pangasinan','9123456803','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(515,'Cuyapo Branch YTM','Cuyapo, Tarlac','9123456804','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(516,'Alaminos Branch YTM','Alaminos, Pangasinan','9123456805','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(517,'Paniqui Branch YTM','Paniqui, Tarlac','9123456806','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(518,'Victoria Branch YTM','Victoria, Tarlac','9123456807','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(519,'Pinili Branch YTM','Pinili, Ilocos Norte','9123456808','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(520,'San Nicolas Branch YTM','San Nicolas, Ilocos Norte','9123456809','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(521,'Bangued Branch YTM','Bangued, Abra','9123456810','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(522,'Agoo Branch YTM','Agoo, La Union','9123456811','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(523,'Asingan Branch YTM','Asingan, Pangasinan','9123456812','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(524,'Pozorrubio Branch YTM','Pozorrubio, Pangasinan','9123456813','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(525,'San Jose Branch YTM','San Jose, Nueva Ecija','9123456814','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(526,'Talavera Branch YTM','Talavera, Nueva Ecija','9123456815','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(527,'Infanta Branch YTM','Infanta, Pangasinan','9123456816','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1),(528,'Baguio Branch YTM','Baguio City, Benguet','9123456817','2026-06-04 10:50:55.000000','2026-06-04 10:50:55.000000','0001-01-01 00:00:00.000000',1);
/*!40000 ALTER TABLE `branches` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Dumping data for table `systemsettings`
--

LOCK TABLES `systemsettings` WRITE;
/*!40000 ALTER TABLE `systemsettings` DISABLE KEYS */;
/*!40000 ALTER TABLE `systemsettings` ENABLE KEYS */;
UNLOCK TABLES;

--
-- Dumping data for table `__efmigrationshistory`
--

LOCK TABLES `__efmigrationshistory` WRITE;
/*!40000 ALTER TABLE `__efmigrationshistory` DISABLE KEYS */;
INSERT INTO `__efmigrationshistory` VALUES ('20261104140000_AddRepresentativeElection','9.0.0'),('20261104150000_AddVoterLookupIndexes','9.0.0');
/*!40000 ALTER TABLE `__efmigrationshistory` ENABLE KEYS */;
UNLOCK TABLES;
/*!40103 SET TIME_ZONE=@OLD_TIME_ZONE */;

/*!40101 SET SQL_MODE=@OLD_SQL_MODE */;
/*!40014 SET FOREIGN_KEY_CHECKS=@OLD_FOREIGN_KEY_CHECKS */;
/*!40014 SET UNIQUE_CHECKS=@OLD_UNIQUE_CHECKS */;
/*!40101 SET CHARACTER_SET_CLIENT=@OLD_CHARACTER_SET_CLIENT */;
/*!40101 SET CHARACTER_SET_RESULTS=@OLD_CHARACTER_SET_RESULTS */;
/*!40101 SET COLLATION_CONNECTION=@OLD_COLLATION_CONNECTION */;
/*!40111 SET SQL_NOTES=@OLD_SQL_NOTES */;

-- Dump completed on 2026-11-18  8:16:08

-- ============================================================================================================
-- Added 2026-11-19: migrations 011 (QrIssuances) and 012 (Users.MustChangePassword), unchanged from deploy/sql.
-- ============================================================================================================
USE `rrms`;
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
