# 11 — Deployment

Read with `03-migration-review.md` (what the schema change is) and `04-rollback.md` (how to go back). Nothing in this work was run against a production database or deployed anywhere; every step below was
rehearsed on scratch copies, and §8 says what that rehearsal could not cover.

## 1. What is deployed, and in which order it matters

| Part | What changes | Depends on |
|---|---|---|
| **Database** | Two additive changes: `20261104140000_AddRepresentativeElection` (new columns and tables, two permission rows) and `20261104150000_AddVoterLookupIndexes` (two indexes) | nothing |
| **API** (`EVoting.Api`) | New build | the database changes above — **apply them first** |
| **Admin** (WPF) | New build | the new API |
| **Kiosk** | **No change.** A General Assembly election runs on the Kiosk as before. A Representative election needs the Kiosk changes in `08-kiosk-offline.md` §4 | — |
| **Registration Station** | No change | — |

**The API does not migrate the database at start-up** (there is no `Migrate()` call). The new API reads the new `Events` columns in every query, so started against the old schema it would fail, for both election types.
Apply the schema before the API. That order is safe for the running system: the old API keeps working on the new schema because it ignores the new columns (`10-ga-regression-report.md` §6).

## 2. Before you start

1. **Pick the moment.** Apply the schema and restart the API when **no election is open**. The schema changes are additive, but the restart ends in-flight requests, and the two new indexes are built on the `Members` and `VoterLogs` tables
   (creating a secondary index on InnoDB normally lets reads and writes continue, but the build time was **not** measured on production-size hardware).
2. **A backup you have restored once** (`04-rollback.md` §2): `mysqldump --single-transaction …`, restored into a differently named database and the application started against it.
3. **Duplicate position rules.** The new unique key on `EventPositionRules (EventId, Position)` fails if an event lists the same position twice. Run
   `SELECT EventId, Position, COUNT(*) FROM EventPositionRules GROUP BY EventId, Position HAVING COUNT(*) > 1;`. If a row comes back, decide which rule to keep and delete the other yourself; the scripts never delete data.
4. **MySQL settings.** If binary logging is on, `binlog_format` must be `ROW` (`SELECT @@binlog_format;`). Representative ballots are written under `READ COMMITTED`, which MySQL refuses to log in `STATEMENT` mode. `ROW` is the default of MySQL 8.0.
   (General Assembly ballots still use `RepeatableRead`.) The scripts were tested on MySQL 8.0.46 only.
5. **Database accounts.** The account the API runs as needs the same rights on the seven new tables as on the existing ones: `SELECT, INSERT, UPDATE, DELETE`. It needs DDL rights only if you let EF apply the migrations; with the SQL scripts the DBA's own account does that.
6. **Review the schedules of existing events** (§6). This is the one change in behaviour that is not about the new feature.
7. **Secrets stay outside source control.** `appsettings.json` ships with the connection string, `Jwt:Key` and `Security:PasswordPepper` **empty**; they come from the host (environment variables or your secret store), as before. This work adds no secret.

## 3. Order of operations

### 3.1 Rehearse on a copy (always first)

1. Restore the backup into a scratch database.
2. Apply `deploy/sql/005_representative_election.sql`, then `005_representative_election_verify.sql` — **every row must say `PASS`**; then `006_voter_lookup_indexes.sql` and `006_voter_lookup_indexes_verify.sql`.
   The scripts are idempotent (each statement checks `information_schema` first), also record the migrations in `__EFMigrationsHistory`, and may be re-run after an interruption. The database must be selected
   (`mysql -u <admin> -p <scratch database> < 005_representative_election.sql`; with no database selected the script stops by itself).
3. Start the new API against the scratch database and work through §5.

Instead of the scripts you may use EF (`dotnet ef database update`); the result is the same schema (proved by comparing both on identical copies, `03-migration-review.md` §6). Use **one** method per database. If you did use both, run the verify scripts.

### 3.2 Production

1. Take the backup (§2.2). Confirm no election is open.
2. Apply `005_…sql`, then `005_…_verify.sql`; then `006_…sql` and `006_…_verify.sql`. Read every result row.
3. Deploy the new API the way you deployed earlier builds; set any configuration from §4 beforehand. Check `GET /health/ready` (database reachable) and `GET /health/live`.
4. Deploy the new Admin (built on Windows with the .NET 9 SDK). Its settings do not change.
5. Sign in to the Admin as a user of the role that received the new permissions (§4, last row) and run §5.
6. Leave the Kiosks as they are.

### 3.3 If something is wrong

`04-rollback.md` decides: application-only rollback is always possible (schema is additive); a schema rollback is possible **only while no Representative ballot exists**, and both rollback paths refuse otherwise.
After an application-only rollback to the old API, expect one visible difference on General Assembly kiosks: the positions of the ballot may be listed alphabetically instead of in creation order (`10-ga-regression-report.md` §6).

## 4. Configuration

| Key | Default | Meaning |
|---|---|---|
| `RepresentativeImport:MaxRowsPerBatch` | 20 000 | most candidate rows one import batch may hold |
| `RepresentativeImport:MaxRowsPerRequest` | 2 000 | rows accepted per staging request (larger files go in several requests) |
| `RepresentativeImport:MaxOpenBatchesPerEvent` | 10 | uncommitted import batches one election may have at once |
| `RepresentativeImport:InsertChunkRows` | 250 | rows per multi-row `INSERT` while staging |
| `Eligibility:MinShareCapital`, `Eligibility:MinSavingsDeposit` | 3000 / 4000 | now a **fallback**: they apply until an administrator saves thresholds in *Settings › Member Eligibility*; after that the saved (database) values apply. The screen shows which source is in force. |
| `RateLimit:VoteSubmitPerMinute` | 60 | one fixed window **shared by every kiosk of the server**, plus a queue of 10. Representative ballots and syncs use the same policy as General Assembly ballots. For event day, size it from the number of kiosks and the ballots per minute you expect (a bulk offline sync is one request however many ballots it carries). |

None of the `RepresentativeImport` keys is in the shipped `appsettings.json`; the defaults above apply unless you add them.
The eligibility thresholds are cached for five minutes in the distributed cache and evicted on save. With **more than one API instance** and no shared cache (no Redis), each instance has its own cache and may use the previous
thresholds for up to five minutes after a save (measured: `06-eligibility-policy.md` §6). With Redis, the instances agree at once.
If Redis is configured but **unreachable**, cached reads and saves wait for the Redis client's connection timeout (11 to 12 seconds in the test) before they fall back to the database: the screens become slow, not broken. Monitor Redis.

**Permissions.** The migration adds `SETTINGS_ELIGIBILITY_VIEW` and `SETTINGS_ELIGIBILITY_EDIT` and grants both to `RoleId 1` only. Any other role that should see or change the thresholds must be granted them by an administrator
(`07-permissions.md` §5). The Representative screens use permission codes that already exist (`07-permissions.md` §2).

## 5. Post-deployment checks

On the rehearsal copy first, then on production (read-only unless stated):

| # | Check | Expected |
|---|---|---|
| 1 | `005_…_verify.sql`, `006_…_verify.sql` | every row `PASS` |
| 2 | `GET /health/ready` | healthy |
| 3 | Admin › Events | existing elections are listed, each with type *General Assembly* |
| 4 | Open an existing election in the editor | the same four rules as before, now editable; nothing else changed |
| 5 | Admin › Voter Lookup (an existing election) | pages of 50; the total and page count are shown; paging and the voted filter work |
| 6 | Admin › Settings › Member Eligibility | 3000 / 4000, source *configuration* |
| 7 | A Kiosk (or the Kiosk contract): `events/current`, `events/{id}/candidates`, `events/{id}/position-rules` for a General Assembly election | same answers as before, plus the `eventType` field; positions in the usual order |
| 8 | Registration Station: member search | unchanged |
| 9 | **Rehearsal copy only:** create a Representative election, import a small candidate file, open it, cast a ballot, read the results | the flow of `02-import-and-column-mapping.md` and `05-api-contracts.md` works end to end |

Do not create a Representative election on production for a rehearsal: it is real election data. Rehearse on the copy.

## 6. Review the schedules of existing events before deploying

In the original build the 10-second scheduler could not save its own changes (its audit rows lacked an IP address and details, which the audit table requires), so events were opened and closed by hand only. The scheduler now saves.
From the moment the new API runs:

* a **Closed** event whose schedule window contains the current time is **opened** automatically (unless an administrator closed it by hand after its window began), and
* an **Opened** event whose window has ended is **closed** automatically,

both within about ten seconds, and each is written to the audit log with the actor `SYSTEM`. Before deploying, look at every event's schedule and status and correct any whose dates no longer reflect the intent.
Opening is subject to the same readiness checks as a manual open (`05-api-contracts.md` §3), and a blocked attempt is audited once per event.

## 7. What event day looks like after the deployment

* A General Assembly election is run exactly as before.
* A Representative election: candidates and rules are prepared in the Admin; the roster freezes the first time voting opens; an unvoted roster can be unlocked with a reason; the eligibility thresholds cannot be changed while any election is open.
  **Voting itself needs the Kiosk changes of `08-kiosk-offline.md` §4**; an unmodified Kiosk is refused cleanly (`409 WRONG_EVENT_TYPE`) and nothing is written.
* The roster fingerprint format is `v2`. Only the informational `rosterChanged` flag of a Representative ballot answer compares fingerprints. A Kiosk cache or queued ballot made against an earlier draft of this work carries the old format and is
  flagged as changed once; nothing is rejected because of it, and a committed ballot is never altered by it.

## 8. What was not verified for deployment

* The API was compiled by an offline build of the same source files with a newer SDK, and its unit tests were run with a minimal test runner. **The first `dotnet build`, `dotnet test` and `dotnet publish` of the project files on a machine
  with NuGet access are yours to run** (`12-change-ledger.md` lists the commands). Please also run `dotnet ef migrations has-pending-model-changes` once: the migration files were produced without the real `dotnet ef`.
* The Admin was never built as a WPF application here (no Windows runtime); its view models were run against the real API and its XAML was checked statically. The real `.xlsx` reading and writing was not exercised.
* No web-server, reverse-proxy, TLS or service-manager configuration was touched or tested.
* Index build time and the speed of the lists at your real table sizes were measured on a small machine with a warm cache; treat them as order-of-magnitude figures.
* Nothing was run against a production database, with MariaDB, or with a MySQL version other than 8.0.46.
