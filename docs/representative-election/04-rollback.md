# 04 — Rollback and recovery

Part 11 asks for a rollback or recovery strategy. This document is the operator's version. Every command here was exercised on scratch copies
(evidence in `03-migration-review.md` §6); none was run against production.

## 1. The rule of thumb

| Situation | Do this |
|---|---|
| Migration applied, **no Representative ballot exists yet** (a mistake, or a failed rehearsal) | Roll back with the SQL scripts or EF `database update 0` (§3). |
| **Any Representative ballot exists** | **Do not roll back.** Both rollback paths refuse. Restore the pre-migration backup, or keep the schema and roll back the application only (§4). |
| New API misbehaves, schema is fine | Redeploy the previous API build (§4). The new schema is additive. |
| A migration step failed half-way (MySQL DDL is not transactional) | Fix the cause and **re-run the same script/migration** — it completes whatever is missing (§5). |

## 2. Before anything: take a backup you have tested

```
mysqldump -u <admin> -p --single-transaction --routines --triggers --default-character-set=utf8mb4 <database> > evoting-before-representative.sql
```

Restore it into a *different* database name once and run the application against it. A backup that was never restored is not a backup.
Also copy the API's `appsettings` / environment configuration you are replacing.

## 3. Rolling back the schema (only while no Representative ballot exists)

### 3.1 With the SQL scripts (what a DBA runs by hand)

1. `006_voter_lookup_indexes_rollback.sql` — drops the two covering indexes and forgets its history row. Safe at any time.
2. `005_representative_election_rollback.sql` — **destructive**. It
   * refuses unless `@confirm_rollback` is `'YES'` in the **same** mysql session (the client does not accept `SET …; SOURCE …` in one `-e` argument):

     ```
     mysql -u <admin> -p <database>
     mysql> SET @confirm_rollback := 'YES';
     mysql> SOURCE C:/path/to/005_representative_election_rollback.sql;
     ```
     (Linux/macOS one-liner: `( echo "SET @confirm_rollback := 'YES';"; cat 005_representative_election_rollback.sql ) | mysql -u <admin> -p <database>`)
   * refuses (error `__rollback_refused__ballots_exist__restore_the_backup`) if `RepresentativeVotes` contains any row,
   * removes the two permission rows, the seven tables (children first), the CHECK constraints, the unique index, the added columns, and the migration's history row,
   * never touches a General Assembly table.

Result, verified: schema, `__EFMigrationsHistory` and permissions are identical to the original dump (test sections E/G).
Re-applying the scripts afterwards gives the full schema again.

### 3.2 With EF

```
dotnet ef database update 0
```
**Why `0` and not an earlier migration id:** your history table lists `20260427064815_InitialCreate` and `20260602000000_AddAuditLogsAndMissingPermissions`,
but the API assembly contains only the two new migrations, so EF cannot resolve those ids as a target (it throws "The migration … was not found"). `0` reverts
every migration EF *can* see — exactly the two added here — and leaves the two foreign history rows alone. To undo only the indexes:
`dotnet ef database update 20261104140000_AddRepresentativeElection`.

`Down()` of the first migration starts with the **rollback guard**: if any ballot exists it executes a statement that MySQL rejects
("Subquery returns more than 1 row", the comment on the statement names the reason) **before** it touches any table. Verified: schema and ballot unchanged.

### 3.3 What a rollback does *not* undo
* Audit log rows written while the module was live (they stay; they are history).
* Eligibility flags on members that were recalculated with new thresholds — re-run the recalculation, or restore the backup, if the old thresholds must return.
* Anything done outside the module's tables (events created with `EventType = 'REPRESENTATIVE'` disappear with the column; they were Representative-only data).

## 4. Rolling back the application only

The schema is additive and the General Assembly paths are unchanged, so the previous API build can run on the new schema for **General Assembly** elections.

* Before you redeploy the old API: **close or disable every Representative event.** The old API has no concept of `EventType`; it would present a Representative event as an
  ordinary one with no candidacies and could open it.
* The old API keeps working with the unique index `UQ_EventPositionRules_EventId_Position` and the new columns (it ignores them; new columns have defaults).
* Measured difference of "old API + new schema" (`10-ga-regression-report.md` §6): two lists come back in another order because the **old** code has no complete `ORDER BY`.
  The kiosk position-rules endpoint lists the positions alphabetically instead of in creation order (so the order of positions on a General Assembly ballot can change), and the audit
  list orders voters who voted in the same second differently. The new build fixes both with an explicit ordering. Everything else in the 100 compared calls was identical.

## 5. A failed or interrupted migration

MySQL commits each DDL statement immediately. If the migration stops half-way:

1. Read the error. The only statement that can fail because of *data* is the unique index on `EventPositionRules (EventId, Position)` — it runs **first**, so a failure there changes nothing.
   The script prints the duplicate rules; decide which to keep, delete the others by hand, re-run.
2. For any other failure (disk full, lost connection, permissions) fix the cause and run the **same** `005_…sql` / `006_…sql` again. Each statement checks `information_schema` first; whatever
   is already there is skipped. (EF's own `Migrate()` can be re-run too; it will resume at the migration that has no history row, and the SQL scripts are the safe way to finish a half-applied one.)
3. Run `005_representative_election_verify.sql` and `006_voter_lookup_indexes_verify.sql`; every row must say `PASS`.

## 6. After Representative ballots exist — recovery options

1. **Keep the schema, fix the application.** Roll back or patch the API only (§4 — but note the old API cannot serve Representative voting or results).
2. **Export before anything destructive.** The results endpoint `GET /api/v1/events/{id}/representative-results/export` produces a CSV (audited); for raw data:
   ```
   SELECT v.RepresentativeVoteId, v.EventId, v.MemberId, c.Position, p.CandidateCode, p.CandidateName, v.VotedAt, v.ReceiptCode
   FROM RepresentativeVotes v
   JOIN RepresentativeEventCandidates c ON c.RepresentativeEventCandidateId = v.RepresentativeEventCandidateId AND c.EventId = v.EventId
   JOIN RepresentativeCandidateProfiles p ON p.RepresentativeCandidateProfileId = c.RepresentativeCandidateProfileId
   WHERE v.EventId = <id> ORDER BY v.RepresentativeVoteId;
   ```
3. **Restore the backup** taken in §2 into a clean database and cut the application over to it. Ballots cast after the backup are lost with it — which is why the
   decision to roll back is only taken before the election opens.
4. Reconcile with `voterlogs`: `005_…_verify.sql` check 15 lists Representative ballots without a `voterlogs` row (must be 0).

## 7. Evidence (executed)

* SQL rollback: refused without confirmation (and changed nothing); with confirmation restored the pristine schema, history and permissions; re-apply worked.
* SQL rollback and EF `Down()` with a ballot present: refused for the right reason, schema and ballot untouched.
* EF `Down()` to `0` without ballots restored the pristine schema and history; `Up()` again reproduced the reference schema.
* Duplicate-rule pre-flight: nothing applied, no history row, then success after the duplicate was removed.
* Old API build on the new schema: see `10-ga-regression-report.md` §6.
