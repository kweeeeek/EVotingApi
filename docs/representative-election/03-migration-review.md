# 03 — Database migration review

Part 11 of the engineering task. Two EF Core migrations were added; both are additive. **Nothing in this work was run against a production
database** — every statement below was executed only on scratch copies of the sanitized dump you supplied (MySQL 8.0.46).

## 1. Files

| File | What it is |
|---|---|
| `Infrastructure/Migrations/20261104140000_AddRepresentativeElection.cs` (+ `.Designer.cs`) | The Representative module: columns on two existing tables, seven new tables, constraints, indexes, two permission rows. `Up()` and `Down()` are produced from EF's own model diff. |
| `Infrastructure/Migrations/20261104140000_AddRepresentativeElection.Custom.cs` | Hand-written parts a model diff cannot express: the permission seed/unseed and the **rollback guard** (refuses to drop tables once a Representative ballot exists). |
| `Infrastructure/Migrations/20261104150000_AddVoterLookupIndexes.cs` (+ `.Designer.cs`) | Two covering indexes for the paged Voter Lookup. Indexes only; no data. |
| `Infrastructure/Migrations/ApiDbContextModelSnapshot.cs` | The model snapshot after both migrations. |
| `deploy/sql/005_representative_election.sql`, `…_verify.sql`, `…_rollback.sql` | The same change as hand-runnable, **idempotent** SQL for a DBA (also writes the `__EFMigrationsHistory` row). |
| `deploy/sql/006_voter_lookup_indexes.sql`, `…_verify.sql`, `…_rollback.sql` | Same for the index migration. |

### History table — important background
Your database's `__EFMigrationsHistory` already contains `20260427064815_InitialCreate` and `20260602000000_AddAuditLogsAndMissingPermissions`,
but **the API repository contained no `Migrations` folder at all** (those migrations were applied from other tooling). The new migrations
therefore start from the schema of your dump; the snapshot describes the whole model so later `dotnet ef migrations add` calls diff correctly.
A consequence for rollback: EF can only target migrations it can see, so the rollback target is **`0`** (see `04-rollback.md`).

The migration files, designer files and snapshot were generated with an offline stand-in for `dotnet ef` (the Design package could not be
downloaded in the build environment) that uses EF's own model differ and SQL generator. They were checked (§6) but **were not produced by the
real `dotnet ef migrations add`**; running `dotnet ef migrations has-pending-model-changes` once on your machine is a cheap confirmation.

## 2. Changes to existing tables (all additive)

| Table | Change | Default / effect on existing rows |
|---|---|---|
| `Events` | `EventType varchar(30) NOT NULL DEFAULT 'GENERAL_ASSEMBLY'` + `CK_Events_EventType` (`GENERAL_ASSEMBLY`/`REPRESENTATIVE`) | Every existing election becomes `GENERAL_ASSEMBLY`. |
| `Events` | `RosterLockedAt datetime(6) NULL` | NULL = roster not frozen. |
| `Events` | `EligibilityMinShareCapital`, `EligibilityMinSavingsDeposit` `decimal(18,2) NULL` | NULL until an election is opened (evidence of the thresholds in force). |
| `EventPositionRules` | `MaxCandidates int NULL` + `CK_EventPositionRules_MaxCandidates` (`NULL` or `> 0`) | NULL = no limit (all existing rules). Distinct from `MaxVotes` (picks per ballot) and `TotalSeats` (winners). |
| `EventPositionRules` | `UNIQUE UQ_EventPositionRules_EventId_Position (EventId, Position)` | **Can fail on existing data** if an event has the same position twice — see §5. |
| `Permissions` / `RolePermissions` | Rows `SETTINGS_ELIGIBILITY_VIEW`, `SETTINGS_ELIGIBILITY_EDIT`, granted to **RoleId 1 only** | Other roles get nothing until an administrator grants it. |
| `Members`, `VoterLogs` (006) | `IX_Members_VoterLookup_Cover (IsActive, IsEligibleToVote, MemberName, AccountCode, BranchId)`, `IX_VoterLogs_Event_Voted_Member (EventId, HasVoted, MemberId)` | Indexes only. |

No column is dropped, renamed, re-typed or rewritten; no existing row is updated by the schema statements. The old API ignores the new columns, so the
schema can be applied **before** the new API is deployed.

## 3. New tables

| Table | Key | Purpose / notes |
|---|---|---|
| `RepresentativeCandidateProfiles` | `int` auto-increment | Reusable person. `CandidateCode varchar(30)` unique (stored trimmed, upper-case); `MemberId int NULL` unique when set (FK → `members`, RESTRICT); name, phones, civil status, gender, date of birth, education, previous positions, skills; `PhotoUpdatedAt` (version stamp); `IsActive`. |
| `RepresentativeCandidatePhotos` | `int` = profile id (1:1) | `Image`/`Thumbnail` `mediumblob`, `ContentType`, `Sha256`. Kept apart so a candidate list never drags image bytes. **The only CASCADE** (photo → its own profile). |
| `RepresentativeEventCandidates` | `int` auto-increment | A profile on one event's roster at one position (`Position varchar(100)`, `Reason`, `BallotNumber`, `Status` `APPROVED`/`WITHDRAWN`, `ImportBatchId`). UNIQUE `(EventId, Position, ProfileId)` (the specified business rule), UNIQUE `(EventId, Position, BallotNumber)` (NULLs ignored), UNIQUE `(Id, EventId)` (target of the composite FK below). |
| `RepresentativeVotes` | `bigint` auto-increment | One row per selection. `UNIQUE (MemberId, EventId, RepresentativeEventCandidateId)`; indexes on `(CandidateId, EventId)`, `EventId`, `ReceiptCode`. **Composite FK `(RepresentativeEventCandidateId, EventId)` → roster `(Id, EventId)`** so the database itself refuses a vote whose candidate belongs to another event. |
| `RepresentativeCandidateImportBatches` | `int` | One uploaded file: status workflow (`UPLOADED → VALIDATING → VALIDATED \| INVALID → COMMITTED \| FAILED \| CANCELLED`, CHECK-guarded), counters, who/when, file SHA-256. |
| `RepresentativeCandidateImportRows` | `bigint` | Staged rows (kept after commit for review): one column per template column, `RowStatus`, `PlannedAction`, `MatchedProfileId`, `Resolution`, `ErrorCodes`, `ErrorMessage`. UNIQUE `(ImportBatchId, RowNumber)`. |
| `SystemSettings` | `varchar(100)` `SettingKey` | Key/value store for the eligibility thresholds (+ applied stamp), with a `Version` for optimistic concurrency. |

Key types follow the existing schema (every referenced key is `int`; the two tables that can grow large — votes and staged rows — use `bigint`).
All new tables are `InnoDB`, `utf8mb4`, and take the database's collation; on the scratch copy that is `utf8mb4_0900_ai_ci`, the same as the existing tables
(verify check 08/08b makes this explicit, because the API compares roster positions with rule positions across tables).

### Delete behaviour (Part 11: "do not cascade-delete submitted election records")

| Relationship | ON DELETE |
|---|---|
| Vote → roster entry (composite), → event, → member | RESTRICT |
| Roster entry → event, → profile, → import batch | RESTRICT |
| Profile → member | RESTRICT |
| Import batch → event; import row → batch | RESTRICT |
| Photo → its profile | CASCADE (a dependent blob, not an election record) |

Application rules on top of that: a profile with roster entries or votes is **deactivated, not deleted** (`PROFILE_ON_ROSTER` / `PROFILE_HAS_VOTES`);
removing a roster entry is refused after the roster is locked, and a roster entry that already has votes is never deleted. Deleting an *event* that has
votes remains refused (409) as before; the optional purge path now covers Representative votes too.

## 4. Statement-by-statement risk

| Statement | Table size in the dump you supplied | Risk | Mitigation |
|---|---|---|---|
| `ALTER TABLE Events ADD …` (4 columns, defaults/NULL) | 1 event | none | – |
| `CREATE UNIQUE INDEX … ON EventPositionRules` | 4 rules | **fails if duplicates exist** | Run first, before any other change; pre-flight query in the script lists the offenders; tested (§6 H): nothing else changes when it fails. |
| `ALTER TABLE EventPositionRules ADD MaxCandidates` + CHECK | 4 rules | none | – |
| 7 × `CREATE TABLE` | new | none | – |
| Permission `INSERT … WHERE NOT EXISTS` | – | none (idempotent) | – |
| `CREATE INDEX` on `Members` / `VoterLogs` (006) | 67,515 members, 3 voter logs (1,000,000 members were generated for the scale tests) | InnoDB builds secondary indexes online; a large table takes longer and uses disk | Apply outside voting hours; indexes only, drop with the 006 rollback. |

MySQL DDL is **not transactional**: a statement that fails leaves the earlier ones applied. Both paths are written so that re-running completes whatever is
missing (the SQL scripts check `information_schema` before every statement; EF's history table records the migration only after `Up()` completes).

## 5. Pre-flight before applying to a real database

1. `mysqldump --single-transaction` the database (see `04-rollback.md` §2).
2. Run on a **copy** first; run the `*_verify.sql` scripts and read every row.
3. Look for duplicate rules (the 005 script prints them first; or run
   `SELECT EventId, Position, COUNT(*) FROM EventPositionRules GROUP BY EventId, Position HAVING COUNT(*) > 1;`).
   Decide which row to keep and delete the extra yourself; the script never deletes data.
4. Apply the schema **before** the new API (`11-deployment.md`).

## 6. Evidence (executed)

`tests/sql_parity.sh` (scratch databases `rrms_test_sq*`; reproduces from the pristine dump each time) — **48 / 48 checks passed** on the final run:

| Section | What was proved |
|---|---|
| A | EF migration vs hand-run SQL (005 + 006) on identical copies of the dump: **identical schema** (371 fingerprint lines — columns, types, defaults, collations, indexes, FKs with delete rules, CHECK clauses, tables/engines) and identical `__EFMigrationsHistory`; both seed the 2 permissions and grant them to role 1. |
| B | `005_…_verify.sql`: 22 checks, all PASS after migration; on a database that is *not* migrated it errors out instead of passing. `006_…_verify.sql`: 5 PASS. |
| C | Idempotency: running 005/006 twice, or on an EF-migrated database, changes nothing and does not duplicate history. |
| D | Half-migrated database (EF applied 005 only) + both scripts = fully migrated. |
| E | Rollback scripts (006 then 005) restore the **original schema, history and permissions** exactly; refused without the confirmation variable; re-applying afterwards gives the full schema again. |
| F | With a Representative ballot present, the SQL rollback **and** EF `Down()` are refused for the right reason and change nothing; the ballot survives. |
| G | EF `Down()` (target `0`) without ballots restores the original schema and history; `Up()` again reproduces the reference schema. |
| H | With duplicate position rules, the SQL script and EF `Up()` both stop at the first step, change nothing and write no history row; after removing the duplicate the script completes and equals the reference schema. |
| I | EF snapshot equals the compiled model (0 differences). |

Problems this run found in the deliverables and that were fixed: the usage line of `005_…_rollback.sql` did not work as written (the mysql client does not
accept `SET …; SOURCE …` in one `-e`), and the EF rollback target must be `0` (EF cannot resolve the two history rows that have no code in the repository).

**Not done:** running the migration through the real `dotnet ef` tooling; running against a MariaDB or a different MySQL minor version; timing the index build
on a production-sized disk; any run against the production database (forbidden by the task).
