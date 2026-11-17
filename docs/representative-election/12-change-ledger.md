# 12 — Change ledger

Part 16, Stage 5 of the engineering task. Everything in §2 and the route table of §4 is **generated** from the repository (`git diff 0b5a180..44ba92a` and the controller attributes), so it cannot drift from the code.
Branch `feature/representative-election`, base `0b5a180` (= `main` when the work started). This ledger describes the branch up to commit `44ba92a`; the commit that adds this file follows it and changes nothing else. The code that the final test run exercised is commit `e4ed41c`; every commit after it changed documents only. Written 2026-11-10.

## 1. In one page

| | |
|---|---|
| What was built | The **Representative election** (event type `REPRESENTATIVE`): reusable candidate profiles, a per-election roster, staged and validated candidate import, position rules with a candidate limit, ballots stored in their own table, offline synchronisation, results, audit and permissions; **server-side paged Voter Lookup**; **configurable eligibility thresholds**; the Admin screens for all of it. |
| What was **not** touched | The General Assembly tables, ballots and results; the Kiosk and Registration Station applications. |
| Database | Two additive migrations (§3). No column dropped, renamed or rewritten. |
| Existing routes changed | Additive fields and guards only (§4), plus the behaviour differences of §6. |
| Verified | The API over HTTP against scratch copies of the supplied database, a General Assembly differential run against the original build, SQL scripts versus EF migration, unit tests, mutation checks, the Admin view models against the real API (§7). |
| **Not** verified | Real xUnit, the real `dotnet build` of the project file, WPF rendering, the real Excel reader/writer, kiosk hardware, a production database, anything on .NET 9 (§9). |
| Open | The Kiosk client work for a Representative election (`08-kiosk-offline.md` §4) (§8). |

## 2. Files

### New files (60)

**Controllers** (6)

| File | Lines +/− |
|---|---|
| `Controllers/EligibilitySettingsController.cs` | +67 / −0 |
| `Controllers/PositionRulesAdminController.cs` | +195 / −0 |
| `Controllers/RepresentativeImportAdminController.cs` | +131 / −0 |
| `Controllers/RepresentativeProfilesAdminController.cs` | +70 / −0 |
| `Controllers/RepresentativeRosterAdminController.cs` | +55 / −0 |
| `Controllers/RepresentativeVotingController.cs` | +206 / −0 |

**Services** (13)

| File | Lines +/− |
|---|---|
| `Services/EligibilityService.cs` | +429 / −0 |
| `Services/EventOpenGate.cs` | +147 / −0 |
| `Services/RepresentativeBallotService.cs` | +260 / −0 |
| `Services/RepresentativeImportPlanner.cs` | +350 / −0 |
| `Services/RepresentativeImportService.cs` | +822 / −0 |
| `Services/RepresentativeLiveUpdates.cs` | +114 / −0 |
| `Services/RepresentativeResultsService.cs` | +178 / −0 |
| `Services/RepresentativeRosterService.cs` | +731 / −0 |
| `Services/RepresentativeRules.cs` | +792 / −0 |
| `Services/RepresentativeVoteService.cs` | +460 / −0 |
| `Services/RosterQueries.cs` | +79 / −0 |
| `Services/ServiceResult.cs` | +39 / −0 |
| `Services/VoterLookupService.cs` | +191 / −0 |

**Infrastructure (entities, context, migrations)** (7)

| File | Lines +/− |
|---|---|
| `Infrastructure/Entities/RepresentativeEntities.cs` | +244 / −0 |
| `Infrastructure/Migrations/20261104140000_AddRepresentativeElection.Custom.cs` | +67 / −0 |
| `Infrastructure/Migrations/20261104140000_AddRepresentativeElection.Designer.cs` | +1439 / −0 |
| `Infrastructure/Migrations/20261104140000_AddRepresentativeElection.cs` | +486 / −0 |
| `Infrastructure/Migrations/20261104150000_AddVoterLookupIndexes.Designer.cs` | +1443 / −0 |
| `Infrastructure/Migrations/20261104150000_AddVoterLookupIndexes.cs` | +42 / −0 |
| `Infrastructure/Migrations/ApiDbContextModelSnapshot.cs` | +1440 / −0 |

**Models** (2)

| File | Lines +/− |
|---|---|
| `Models/RepresentativeModels.cs` | +600 / −0 |
| `Models/VoterLookupModels.cs` | +39 / −0 |

**Unit tests (`EVoting.Api.Tests`)** (12)

| File | Lines +/− |
|---|---|
| `EVoting.Api.Tests/AuditWriterTests.cs` | +83 / −0 |
| `EVoting.Api.Tests/EligibilityRulesTests.cs` | +274 / −0 |
| `EVoting.Api.Tests/PasswordServiceTests.cs` | +130 / −0 |
| `EVoting.Api.Tests/RepresentativeBallotRulesTests.cs` | +381 / −0 |
| `EVoting.Api.Tests/RepresentativeCastTimeTests.cs` | +224 / −0 |
| `EVoting.Api.Tests/RepresentativeImportPlannerTests.cs` | +679 / −0 |
| `EVoting.Api.Tests/RepresentativeImportRowRulesTests.cs` | +502 / −0 |
| `EVoting.Api.Tests/RepresentativePositionRuleTests.cs` | +380 / −0 |
| `EVoting.Api.Tests/RepresentativeResultsTests.cs` | +327 / −0 |
| `EVoting.Api.Tests/RepresentativeTextRulesTests.cs` | +281 / −0 |
| `EVoting.Api.Tests/RequestLogLevelTests.cs` | +67 / −0 |
| `EVoting.Api.Tests/VoterLookupPagingTests.cs` | +240 / −0 |

**SQL scripts (`deploy/sql`)** (6)

| File | Lines +/− |
|---|---|
| `deploy/sql/005_representative_election.sql` | +339 / −0 |
| `deploy/sql/005_representative_election_rollback.sql` | +76 / −0 |
| `deploy/sql/005_representative_election_verify.sql` | +102 / −0 |
| `deploy/sql/006_voter_lookup_indexes.sql` | +61 / −0 |
| `deploy/sql/006_voter_lookup_indexes_rollback.sql` | +26 / −0 |
| `deploy/sql/006_voter_lookup_indexes_verify.sql` | +39 / −0 |

**Documentation (`docs/representative-election`)** (13)

| File | Lines +/− |
|---|---|
| `docs/representative-election/00-README.md` | +24 / −0 |
| `docs/representative-election/01-audit-and-design.md` | +273 / −0 |
| `docs/representative-election/02-import-and-column-mapping.md` | +301 / −0 |
| `docs/representative-election/03-migration-review.md` | +115 / −0 |
| `docs/representative-election/04-rollback.md` | +105 / −0 |
| `docs/representative-election/05-api-contracts.md` | +197 / −0 |
| `docs/representative-election/06-eligibility-policy.md` | +107 / −0 |
| `docs/representative-election/06-voter-lookup-paging.md` | +140 / −0 |
| `docs/representative-election/07-permissions.md` | +95 / −0 |
| `docs/representative-election/08-kiosk-offline.md` | +183 / −0 |
| `docs/representative-election/09-testing.md` | +472 / −0 |
| `docs/representative-election/10-ga-regression-report.md` | +115 / −0 |
| `docs/representative-election/11-deployment.md` | +121 / −0 |

**Other** (1)

| File | Lines +/− |
|---|---|
| `Middleware/RequestLogLevel.cs` | +25 / −0 |

### Modified files (20)

**Controllers** (7)

| File | Lines +/− |
|---|---|
| `Controllers/AdminController.cs` | +16 / −59 |
| `Controllers/AdminControllerBase.cs` | +6 / −0 |
| `Controllers/AuthController.cs` | +2 / −1 |
| `Controllers/EventsAdminController.cs` | +54 / −13 |
| `Controllers/EventsController.cs` | +36 / −5 |
| `Controllers/MembersAdminController.cs` | +12 / −11 |
| `Controllers/VotesController.cs` | +15 / −0 |

**Services** (7)

| File | Lines +/− |
|---|---|
| `Services/AdminRules.cs` | +11 / −2 |
| `Services/AuditConstants.cs` | +33 / −3 |
| `Services/EventStateService.cs` | +21 / −1 |
| `Services/PasswordService.cs` | +52 / −8 |
| `Services/ReceiptCodeFormat.cs` | +12 / −0 |
| `Services/VoteService.cs` | +5 / −0 |
| `Services/VotingSchedulerService.cs` | +25 / −1 |

**Infrastructure (entities, context, migrations)** (2)

| File | Lines +/− |
|---|---|
| `Infrastructure/ApiDbContext.cs` | +223 / −0 |
| `Infrastructure/Entities/Entities.cs` | +29 / −0 |

**Models** (2)

| File | Lines +/− |
|---|---|
| `Models/AdminModels.cs` | +13 / −0 |
| `Models/ApiModels.cs` | +6 / −0 |

**Security and start-up** (2)

| File | Lines +/− |
|---|---|
| `Program.cs` | +15 / −1 |
| `Security/PermissionCodes.cs` | +5 / −0 |

### Deleted files (0)

None.


## 3. Database changes

Full detail and risk review: `03-migration-review.md`; hand-run equivalents `deploy/sql/005_*` and `006_*`.

| Migration | Changes |
|---|---|
| `20261104140000_AddRepresentativeElection` | `Events`: `EventType` (default `GENERAL_ASSEMBLY`, CHECK), `RosterLockedAt`, `EligibilityMinShareCapital`, `EligibilityMinSavingsDeposit`. `EventPositionRules`: `MaxCandidates` (CHECK), unique key `(EventId, Position)`. New tables: `RepresentativeCandidateProfiles`, `RepresentativeCandidatePhotos`, `RepresentativeEventCandidates`, `RepresentativeVotes` (composite foreign key `(candidate id, event id)`), `RepresentativeCandidateImportBatches`, `RepresentativeCandidateImportRows`, `SystemSettings`. Permission rows `SETTINGS_ELIGIBILITY_VIEW`, `SETTINGS_ELIGIBILITY_EDIT` granted to `RoleId 1`. |
| `20261104150000_AddVoterLookupIndexes` | `Members (IsActive, IsEligibleToVote, MemberName, AccountCode, BranchId)` and `VoterLogs (EventId, HasVoted, MemberId)` — indexes only. |

Every relationship to an election record is `RESTRICT`; the only `CASCADE` is a photo to its own profile. Rollback is refused once a Representative ballot exists (§11).

## 4. API additions

**Changed existing routes (additive):** the answers of `GET/POST/PUT /admin/events…`, `GET /admin/events` (filter), `GET /events/current`, `POST /auth/voter-verify`, `GET /events/{id}/position-rules` gain `eventType` and related fields;
`POST /admin/events/{id}/open` can answer `409 EVENT_NOT_READY`; `GET /admin/voter-lookup` is server-paged; General Assembly vote, candidate and result routes answer `409 WRONG_EVENT_TYPE` for a Representative election. Details: `05-api-contracts.md` §2.

Permission codes listed together mean that **any one** of them is enough. Rate limit `vote` is the existing shared policy.

### New API routes (39)

| Method | Route | Permission / role |
|---|---|---|
| GET | `/api/v1/admin/events/{eventId:int}/representative-imports` | CANDIDATE_VIEW, EVENT_VIEW |
| GET | `/api/v1/admin/events/{eventId:int}/representative-roster` | CANDIDATE_VIEW, EVENT_VIEW |
| POST | `/api/v1/admin/events/{eventId:int}/representative-roster` | CANDIDATE_ADD |
| GET | `/api/v1/admin/events/{eventId:int}/representative-roster/export` | REPORT_EXPORT, CANDIDATE_VIEW |
| GET | `/api/v1/admin/events/{eventId:int}/representative-roster/summary` | CANDIDATE_VIEW, EVENT_VIEW |
| POST | `/api/v1/admin/events/{eventId:int}/representative-roster/unlock` | EVENT_EDIT |
| DELETE | `/api/v1/admin/events/{eventId:int}/representative-roster/{assignmentId:int}` | CANDIDATE_DELETE |
| GET | `/api/v1/admin/events/{id:int}/position-rules` | EVENT_VIEW, EVENT_EDIT |
| PUT | `/api/v1/admin/events/{id:int}/position-rules` | EVENT_EDIT |
| POST | `/api/v1/admin/representative-imports` | CANDIDATE_ADD |
| GET | `/api/v1/admin/representative-imports/template` | CANDIDATE_VIEW, CANDIDATE_ADD, REPORT_EXPORT |
| GET | `/api/v1/admin/representative-imports/{batchId:int}` | CANDIDATE_VIEW, CANDIDATE_ADD |
| POST | `/api/v1/admin/representative-imports/{batchId:int}/cancel` | CANDIDATE_ADD |
| POST | `/api/v1/admin/representative-imports/{batchId:int}/commit` | CANDIDATE_ADD |
| GET | `/api/v1/admin/representative-imports/{batchId:int}/error-report` | CANDIDATE_VIEW, CANDIDATE_ADD |
| POST | `/api/v1/admin/representative-imports/{batchId:int}/resolve` | CANDIDATE_ADD |
| GET | `/api/v1/admin/representative-imports/{batchId:int}/rows` | CANDIDATE_VIEW, CANDIDATE_ADD |
| POST | `/api/v1/admin/representative-imports/{batchId:int}/rows` | CANDIDATE_ADD |
| POST | `/api/v1/admin/representative-imports/{batchId:int}/validate` | CANDIDATE_ADD |
| GET | `/api/v1/admin/representative-profiles` | CANDIDATE_VIEW |
| POST | `/api/v1/admin/representative-profiles` | CANDIDATE_ADD |
| GET | `/api/v1/admin/representative-profiles/{id:int}` | CANDIDATE_VIEW, CANDIDATE_EDIT |
| PUT | `/api/v1/admin/representative-profiles/{id:int}` | CANDIDATE_EDIT |
| PUT | `/api/v1/admin/representative-profiles/{id:int}/active` | CANDIDATE_EDIT |
| GET | `/api/v1/admin/representative-profiles/{id:int}/photo` | CANDIDATE_VIEW, CANDIDATE_EDIT |
| PUT | `/api/v1/admin/representative-profiles/{id:int}/photo` | CANDIDATE_EDIT |
| GET | `/api/v1/admin/settings/eligibility` | SETTINGS_ELIGIBILITY_VIEW, SETTINGS_ELIGIBILITY_EDIT |
| PUT | `/api/v1/admin/settings/eligibility` | SETTINGS_ELIGIBILITY_EDIT |
| POST | `/api/v1/admin/settings/eligibility/impact` | SETTINGS_ELIGIBILITY_VIEW, SETTINGS_ELIGIBILITY_EDIT |
| POST | `/api/v1/admin/settings/eligibility/recompute` | SETTINGS_ELIGIBILITY_EDIT |
| GET | `/api/v1/events/{eventId:int}/representative-ballot` | role Kiosk,Voter,Admin |
| GET | `/api/v1/events/{eventId:int}/representative-candidates` | role Kiosk,Voter,Admin |
| GET | `/api/v1/events/{eventId:int}/representative-candidates/{candidateId:int}/photo` | role Kiosk,Voter,Admin |
| GET | `/api/v1/events/{eventId:int}/representative-results` | EVENT_VIEW, REPORT_VIEW |
| GET | `/api/v1/events/{eventId:int}/representative-results/export` | REPORT_EXPORT |
| GET | `/api/v1/events/{eventId:int}/representative-roster` | role Kiosk |
| POST | `/api/v1/representative-votes` | role Voter · rate limit `vote` |
| POST | `/api/v1/representative-votes/offline-sync` | role Kiosk · rate limit `vote` |
| POST | `/api/v1/representative-votes/offline-sync-bulk` | role Kiosk · rate limit `vote` |


## 5. UI changes (Admin, WPF)

The Admin changes are delivered as a separate package with its own change list (59 new files, 32 modified, 0 deleted, compared file by file with the `EVoting.Admin.zip` that was supplied).
The new files are 19 in the platform-neutral core library, 14 WPF view files, 17 files of the new live-test project and 9 tool files (a contract generator, a stub and generated type-check sources).
The modified files are 16 in the core library, 5 WPF files, 7 in the older test harness and 4 tool files.

| Screen | Change |
|---|---|
| Events | election-type chooser and Type column; **Positions and voting rules** editor (min votes, max votes, max candidates, total seats) in the event editor |
| Representative roster | search, position filter, counts and free slots, export, remove while the roster is not locked, unlock with a reason |
| Representative import | template download, file choice, preview of valid / invalid / conflict rows, error report, explicit confirmation, import history |
| Representative results | per-position ranking, ties, turnout, CSV export |
| Settings › Member Eligibility | thresholds, impact preview, lock while an election is open, recalculation notice |
| Voter Lookup / Voter Search | server-side paging (page size 25, 50 or 100; default 50), total and page count, no overlapping stale requests |
| Navigation | entries gated by permission |

Not built: a manual single-candidate "Add" dialog (the API supports it; candidates come in through the import).

## 6. Behaviour that differs from the original

| # | Area | Before | Now | Why |
|---|---|---|---|---|
| 1 | Voter lookup | loaded every eligible member and every voter-log row of the election into memory, then paged; default page size 100; a page below 1 was served as the first page and a size below 1 was raised to 1, silently | three bounded database statements; default page size 50; page or page size below 1 → `400`; size above 500 cut to 500; the answer also carries `totalCount`, `totalPages`, `items` (the old names `total`, `data` remain) | a million-member electorate cannot be materialised per request |
| 2 | Voter lookup search | a receipt-code search matched a member's receipts from **any** election | matches this election's receipts only | a receipt belongs to one election |
| 3 | Kiosk position rules | order not specified; changed to alphabetical when the new unique key appeared | ordered by creation (`Id`), as the original answered | the ballot order on a General Assembly kiosk must not change (`10-ga-regression-report.md` §5.1) |
| 4 | Audit list of an election | voters who voted in the same second came back in no defined order | a second sort key, `VoterLogId` | repeatable order (`10-ga-regression-report.md` §5.2) |
| 5 | Event creation | every event received the four General Assembly default rules | only a General Assembly event does; a Representative event defines its own | the four positions are General Assembly offices |
| 6 | Opening an election | opened whenever requested | refused with `EVENT_NOT_READY` when the eligibility flags are out of step with the saved thresholds (interrupted recalculation), when a General Assembly position exceeds a `maxCandidates` that was set, or when a Representative election is incomplete; the same gate applies to the scheduler | one readiness check for both paths |
| 7 | Opening an election | — | records the eligibility thresholds in force on the event; a Representative roster freezes | evidence and integrity |
| 8 | Eligibility thresholds | configuration only (`Eligibility:*`) | editable in the Admin, stored in `SystemSettings`, audited, recalculated for all members; refused while an election is open; configuration values remain the fallback | Part 9 |
| 9 | Scheduler | its save failed on every tick (audit rows without an IP address and details), so nothing opened or closed by schedule | saves; a due event opens and a past-due open event closes within about ten seconds, audited as `SYSTEM` | defect found while testing; **review event schedules before deploying** (`11-deployment.md` §6) |
| 10 | Audit rows without an HTTP request | `Details` and `IpAddress` NULL | empty text | the audit table requires both |
| 11 | Staff password hashing | each Argon2 hash or verify kept a 64 MB work area for the life of the process (about 64 MB per staff sign-in and per imported account) | the work area is released; same parameters and same stored text format (a hash made by the old code verifies, and the reverse) | found while load-testing the API; **restart the API after deploying**, and run `dotnet test` once on the server (the leak could only be measured on the newer runtime used here) |
| 12 | General Assembly vote, candidate and result routes | — | answer `409 WRONG_EVENT_TYPE` (offline sync `422`) when the election is a Representative one, and write nothing | a General Assembly path must never record a ballot for a Representative election |
| 13 | Voter Lookup screen (Admin) | one list without pages; the toolbar read "N eligible members · X voted · Y pending" | pages of 25, 50 or 100 (default 50) with First, Previous, Next and Last; the toolbar reads "N eligible members" (or "N matching members" for a filtered list); **the voted / pending split of the whole list is no longer on this tab** (the Turnout tab and the Live Dashboard still show it); Export still writes every matching member (it reads all pages and checks them against the server's total) | a paged screen knows only the total, and the count of voters per filter would need a fourth query on every request |
| 14 | Log of a request the caller abandons | Serilog's request line wrote every exception that passed through it as an ERROR "responded 500" with a stack trace, also when the caller had simply closed the connection (the exception handler already treated that as "nothing failed") | logged at Debug (`RequestLogLevel`, 12 unit tests); a cancellation while the caller is still waiting, any other exception and any 5xx answer stay errors | the Admin's search box cancels the previous request on every key stroke; on a one-million-member copy 30 cancelled searches wrote 30 ERROR records before the change and none after (`09-testing.md` §6.4). EF Core's own one-line note about a connection cancelled while it is being opened can still appear |

Nothing else about an existing General Assembly route changed: a recorded set of 100 calls gave 86 identical answers, 14 identical apart from added fields, and 0 different (`10-ga-regression-report.md`).

## 7. Test results

Totals of the final run (every number is generated from the recorded results; the requirement-by-requirement mapping of Part 15 and the full list of what was **not** run are in `09-testing.md`):

| Layer | What ran | Result |
|---|---|---|
| API over HTTP, real MySQL 8.0.46 (scratch copy of the supplied dump) | the integration suites of `09-testing.md` §3 | **500 checks passed, 0 failed** |
| Admin screen logic against the real API | the live suites of `09-testing.md` §3 | **119 passed, 0 failed** |
| General Assembly differential | 81 scripted checks in each of 3 configurations; 100 recorded calls compared | original vs new: **86 identical, 14 additive, 0 different**; original build on the new schema: 99 identical, 1 different (`09-testing.md` §5) |
| General Assembly ballots under concurrent submission | `t09b_ga_concurrency.py`: the races of `t07b`, through the original voting endpoint | original build: **6 passed, 0 failed**; new build: **6 passed, 0 failed** (`09-testing.md` §6.4) |
| Searches cancelled by the caller | `t12_cancelled_requests.py` on the one-million-member copy | **3 passed, 0 failed**; the same test on the build before the change failed `CR-02` (`09-testing.md` §6.4) |
| SQL scripts versus EF migrations | `sql_parity.sh` | **48 passed, 0 failed** |
| Real Redis (two API instances) | `t11_redis_cache.py` + negative control | **16 of 16 passed**; control 6 of 6 |
| API unit tests | `EVoting.Api.Tests` through a **minimal runner, not real xUnit** | **601 passed, 0 failed** (601 tests) |
| Mutation check of the unit tests | 49 deliberate defects, one at a time | **49 detected**, 0 not detected |
| Mutation check of the Admin screens | 191 deliberate defects in 6 screens, one at a time | **191 detected** after tests were added for the 24 that the first complete run missed (that run detected 167 of 191), 0 not detected - screen by screen in `09-testing.md` §6.7 |
| Older Admin test project (`tests/EVoting.Tests`), against an **earlier API snapshot** | the Admin as supplied and as delivered | supplied: **1140 passed, 7 failed**; delivered: **1132 passed, 13 failed** - the failures are explained one by one in `09-testing.md` §6.9 |


## 8. Unfinished items

1. **The Kiosk client** does not yet run a Representative election (`08-kiosk-offline.md` §4 is the work list). The API side of the kiosk and offline contract is implemented and tested.
2. **A manual single-candidate "Add" dialog** in the Admin (the API supports it).
3. **Real xUnit** was never run: the unit tests ran through a minimal runner. Run `dotnet test EVoting.Api.Tests` on a machine with NuGet access.
4. **The real project build** (`dotnet build` of `EVoting.Api.csproj` on .NET 9) was not run; the sources were compiled by an offline build with a newer SDK. Run `dotnet ef migrations has-pending-model-changes` as well.
5. **The older Admin test project** (`tests/EVoting.Tests`, an offline harness that is not shipped) cannot run against this branch without extending its in-memory EF stand-in (about 25 members). It was run against the earlier API snapshot it was built for: with the Admin as supplied it ended at 1140 passed, 7 failed, 7 skipped (1154 selected); with the Admin as delivered at 1132 passed, 13 failed, 7 skipped (1152 selected). The same 7 checks fail in both runs, so they do not come from this work (two wording checks of the shell, the banner texts of two workbook layouts, and three source or project scans of the Admin as supplied). The 6 that fail only in the second run compare the Admin with that snapshot's permission list and route table, which do not have the new permission codes and routes; the same comparisons are made against the real API by the live tests. Expectations that the new screens changed on purpose (sidebar, page catalogue, registered services, Voter Lookup wording) were updated, 4 checks that described reading the whole voter list were retired with a comment naming the live checks that replace them (`VP-01`…`VP-04`, `VP-08`, `VP-14`, `VP-15`, `VP-17`), and the comparison found one real defect (the Voter Lookup status line said "No active election" while the election was unknown), which is fixed and covered by `VP-19`.
6. **A decision for the owner of the General Assembly path** (not made here, because it changes the original code): that path answers HTTP 500 to some of the voters (in the scratch tests roughly 5-10 % of them at 30 and about 20 % at 60) when many voters with neighbouring member numbers submit in the very same instant (a MySQL deadlock after four retries); below about 20 simultaneous voters it was almost never seen. It happens in the original build as well as in the new one; no ballot is lost or counted twice and the refused voter can submit again; the Representative path is not affected (§9 and `09-testing.md` §6.4 have the figures and the recommended change).

## 9. Known limitations

* Cache consistency of the eligibility thresholds was verified against a real single Redis 7.0 server with two API instances, including a stop and restart of Redis (`06-eligibility-policy.md` §6). Redis clusters, authentication and TLS were not tested. With Redis configured but unreachable, a cached read or a save takes 11 to 12 seconds before it falls back to the database.
* Timings at one million members were measured on a two-core machine with a warm cache. A name or account-code search at that size takes 2 to 4 seconds (a substring search no ordinary index can walk); at the production size it is a fraction of a second (`06-voter-lookup-paging.md`).
* `READ COMMITTED` Representative ballots require `binlog_format=ROW` when binary logging is on.
* **General Assembly ballots in a burst (original code, unchanged).** The original `POST /api/v1/votes` looks for a voter's row with `SELECT ... FOR UPDATE` before creating it. The pattern fits InnoDB's gap locks (a lock on a row that does not exist yet covers the gap around it, so voters whose member numbers are neighbours in the index collide); this was not confirmed with lock traces. After four retries the loser gets HTTP 500. In the burst sweep of `09-testing.md` §6.4 (two-core scratch machine) there were almost none up to 20 simultaneous voters (one ballot in 120 at 20, in one of the runs) and clear errors from about 30 (original and new build alike: roughly 5-10 % of the ballots at 30 and about 20 % at 60; single runs vary a great deal); with the same number of voters spread over the member range there were none. Nothing is lost or doubled and a refused voter can submit again. The recommended change is small - use `READ COMMITTED` as the Representative path does, or create the voter-log row first and treat a duplicate as "already voted" - but it changes the original path, so it needs the General Assembly regression and a new burst run, and is left to you.
* The API log is noisy under load: EF Core writes an ERROR record for every failed statement, also when the service then retries or resolves it (a duplicate key, a deadlock). The race tests report these records and require that no request was answered 500 unexplained; the counts are in `09-testing.md` §6.4.
* The migration files and model snapshot were generated with an offline stand-in for `dotnet ef`; the SQL scripts were compared with the EF result statement by statement (48 checks) but the real tool was not run.
* WPF rendering, the real Excel reader and writer (ClosedXML) and kiosk hardware were not exercised.
* The Admin's 25 MB import file limit is a client-side constant; the server never receives the workbook.
* The roster fingerprint format is `v2`; a cache or queued ballot made against an earlier draft is flagged `rosterChanged` once (informational only).
* A tie for the last seat elects nobody in the tied group and is flagged for the election committee to resolve; the system does not run a tie-break.
* Any statement above about "the original" refers to the code at commit `0b5a180`.

## 10. Deployment

### 10.1 First commands to run (on a machine with NuGet access)

Nothing in this work could run the real build; these are the first things to run, in this order, before anything is deployed:

```bash
git fetch origin && git checkout feature/representative-election
dotnet build EVoting.Api.sln -c Release                                   # the first real build of the project files (.NET 9, NuGet restore)
dotnet test EVoting.Api.Tests/EVoting.Api.Tests.csproj                    # the first run with the real xUnit
dotnet ef migrations has-pending-model-changes --project EVoting.Api.csproj   # expected: no pending model changes
```

For the Admin (on Windows, .NET 9 SDK): `dotnet build EVoting.sln -c Release`, then open the screens listed in `11-deployment.md` §5. If any of these four commands reports a problem, stop and report it: it is something the offline build could not see.

### 10.2 Order of operations

Order, configuration, checks and the schedule review: `11-deployment.md`. In short: tested backup → rehearse the two SQL scripts (or EF) on a copy and read every verify row → apply to the database **before** the API → deploy the API → deploy the Admin → post-deployment checks. The Kiosk is unchanged.

## 11. Rollback

Full procedures: `04-rollback.md`.

| Situation | Action |
|---|---|
| Problem in the new application, schema fine | redeploy the previous API build; the schema is additive (close or disable Representative elections first) |
| Schema applied, **no Representative ballot exists** | `006_…_rollback.sql`, then `005_…_rollback.sql` (needs `@confirm_rollback = 'YES'`), or `dotnet ef database update 0`; both restore the original schema, history and permissions |
| **A Representative ballot exists** | rollback is refused by both paths; export the results, then restore the pre-deployment backup or keep the schema and roll the application back |

## 12. Part 17 — strict rules

| # | Rule | How it was kept |
|---|---|---|
| 1 | Do not rewrite the existing system | additive changes; the shared files changed by a small, listed amount (§2) |
| 2 | Do not remove General Assembly functionality | differential run: 0 differences in 100 calls; 0 files deleted |
| 3 | Do not redirect General Assembly votes into `RepresentativeVotes` | a General Assembly vote path refuses a Representative event and writes nothing (`WRONG_EVENT_TYPE`) |
| 4 | Do not mix `RepresentativeEventCandidateId` with `CandidacyId` | separate tables, separate columns; the composite foreign key ties a vote to its own event's roster entry |
| 5 | No duplicate event or branch infrastructure | one `Events` row per election; branches use `EventBranches` |
| 6 | Do not duplicate `VoterLogs` | the Representative ballot writes the same `voterlogs` row in the same transaction |
| 7 | Do not assume behaviour from class names | every claim about the original was read from the code (`01-audit-and-design.md`) |
| 8 | Check route conventions | new routes follow `api/v1/admin/...` / `api/v1/events/...`, `RequirePermission` and the standard error body |
| 9 | Do not hard-code candidate counts | limits are per-position rules (`MaxCandidates`) and server settings (`RepresentativeImport:*`); imports of 1,000, 2,000, 5,000 and 12,000 candidates were run |
| 10 | Do not hard-code eligibility thresholds in several places | one service, one setting store, one fallback in configuration |
| 11 | Do not trust client-supplied eligibility or ballot rules | the server reads eligibility, rules, positions and candidate ownership from the database |
| 12 | Do not overwrite profiles silently | a differing profile is a `CONFLICT` the administrator must decide on |
| 13 | Do not delete historical vote data | every relationship is `RESTRICT`; profiles with votes are deactivated, not deleted; rollback refuses with ballots |
| 14 | Do not commit secrets | none added; `appsettings.json` carries empty secrets |
| 15, 16 | Do not modify or deploy to production | nothing was run against production |
| 17 | Do not force-push | none |
| 18 | No unnecessary external dependencies | none added to the API project |
| 19 | No in-memory pagination of large lists | paging, counting and filtering are done by the database (`06-voter-lookup-paging.md`, import and roster lists) |
| 20 | Do not claim an integration works without testing it | §7 lists what ran; §8 and §9 list what did not |

## 13. Part 18 — definition of done

Status key: **Met** = implemented and the evidence in `09-testing.md` was executed; **Met, with a stated gap** = met at the level that could be run here, the gap is named; **Open** = not done.

| # | Definition of done | Status | Evidence and the gap, if any |
|---|---|---|---|
| 1 | General Assembly continues working | **Met** | 81 scripted checks in each of three configurations; 100 recorded calls compared with the original build (86 identical, 14 identical apart from added fields, 0 different); General Assembly cases inside the Representative suites. The Kiosk application itself was not run (`09-testing.md` §5) |
| 2 | Representative Election can be created | **Met** | `t01` EV-03…EV-10; Admin editor `PRV-03` |
| 3 | Representative branches can be configured | **Met** | `t01` RB-01…RB-06; `t04` LK-01c; voter refusal `t07` RV-02, RV-55 |
| 4 | Position rules can be configured | **Met** | `t01` PR-01…PR-14; Admin grid `PRA-*`, `PRV-*` |
| 5 | Candidate profile import works | **Met, with a stated gap** | `t05` (75 checks), `t06` (up to 12,000 rows), Admin `IMP-01`…`IMP-17`. The real Excel reader and writer (ClosedXML) were not exercised; the import was driven through the Admin's reader interface with prepared workbooks |
| 6 | Candidate assignments are event-specific | **Met** | assignment table per election with a composite key to the election; `t04` RS-01…RS-17 |
| 7 | Candidate visibility is shared | **Met** | the same list for every branch, voter, kiosk and administrator: `t07` RV-14, `t04` RS-10 |
| 8 | More than 1,000 candidates can be handled | **Met** | `t06`: 1,000, 2,000, 5,000 (with bad rows) and 12,000 candidates; paging and search under 1.5 s on 12,000 |
| 9 | Representative votes are stored independently | **Met** | table `RepresentativeVotes`; General Assembly routes refuse a Representative election and write nothing (`t07` RV-53, RG-01) |
| 10 | Duplicate and cross-event votes are prevented | **Met** | application checks, unique keys and a composite foreign key (`t07` RV-42, RV-48, RV-60…RV-60c); 1,250 concurrent requests (`t07b`) |
| 11 | Results are computed correctly | **Met** | `t07` RR-01…RR-08 and RR-10…RR-13 against an independent SQL count and an independent reference ranking; Admin `RES-01`…`RES-22` |
| 12 | Kiosk/offline compatibility is implemented and verified | **Open (Kiosk client); met for the API** | The server side — roster download, photos, ballot rules, roster fingerprint, offline queue, single and bulk synchronisation, replays, rejections, audit — is implemented and verified over HTTP (`t07` RV-20…RV-20d, RV-30…RV-30e, RO-01…RO-15). **The Kiosk application was not modified**, so a Representative election cannot yet be voted on a Kiosk; an unmodified Kiosk is refused cleanly (`409 WRONG_EVENT_TYPE`). The work list is `08-kiosk-offline.md` §4 |
| 13 | VoterLookup has server-side pagination | **Met** | `t08`, Admin `VP-01`…`VP-21`; at 67,515 listed voters the slowest median of the scenarios was 318 ms; at about 972,130 it was 3,830 ms |
| 14 | Minimum Share Capital is configurable | **Met** | `t03` EL-07; Admin screen `ELV-*` |
| 15 | Minimum Savings Deposit is configurable | **Met** | `t03` EL-08 |
| 16 | Eligibility is centrally enforced by the API | **Met** | one rule class and one settings store; the stored flag of every member equals the C# rule after every change (`t03` EL-02b, EL-07c, EL-10b, EL-21); the ballot re-checks eligibility at casting time (`t07` RV-54). Offline eligibility on a Kiosk depends on the member data the Kiosk holds (`08-kiosk-offline.md` §4.8) |
| 17 | Relevant audit logs exist | **Met** | every change of rules, roster, import, settings, unlock, open-blocked, scheduler action, offline rejection and export is audited with actor and reason; no ballot choice or member data in the audit text (`t01` PR-02c, `t03` EL-13…EL-13d, `t05` IM-08i, `t07` RO-03b, RO-15, RR-11b) |
| 18 | Database migrations are reviewed | **Met, with a stated gap** | reviewed in `03-migration-review.md`; the SQL scripts and the EF migrations produce the same schema (48 checks); rollback tested. The real `dotnet ef` was not run (the migration files came from an offline stand-in) |
| 19 | Existing General Assembly regression tests pass | **Met** | the repository's existing test classes pass (they are part of the 601 unit tests); the repository had no General Assembly integration tests, so the differential run of `09-testing.md` §5 was written for this work |
| 20 | The affected projects build successfully | **Met only in the offline harness** | the API sources compiled with 0 errors with .NET SDK 10.0.112; the Admin core and its live tests compile; the WPF project was type-checked against stubs. **The first real `dotnet build` of the project files (.NET 9, NuGet) is yours to run** — `11-deployment.md` §8 |
