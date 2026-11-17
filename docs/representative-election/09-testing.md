# 09 — Testing: what was run, what passed, what was not run

Written 2026-11-10 from the recorded results of one final run (`tests/final_all.sh`, tag `2026-11-10`) made on commit `e4ed41c` with nothing uncommitted.
The tables below are **generated** from the result files of that run. A check cited in a Part 15 table was looked up in those files before the document was written; a missing or failed check would have stopped the generator.
The tests themselves are in the test kit that accompanies the work (`tests/*.py`, `EVoting.Admin.LiveTests`) and in `EVoting.Api.Tests`; §7 says how to run them again.

**Which run each part comes from.** Everything below comes from that final run, with two exceptions that are marked where they appear: the **mutation checks** (§6.6, §6.7) are separate, longer runs made earlier the same day (between 00:48 and 06:16 UTC), and the **older Admin test project** (§6.9) was run on its own. The files in which the mutation checks plant defects did not change afterwards: no commit after the unit-mutation run touched a file of the rule code (the API commits since then changed documents and the request-logging files only), and no product file of the Admin carries a modification time later than 00:48 UTC, before the first screen run began. The tests grew after those runs (the 12 tests of `RequestLogLevelTests`; the Admin tests added for the defects the screen runs missed), so the final suites are a superset of the ones the mutation checks ran against.

**History of the Admin results.** The Admin screen suites were run several times while the work went on; every figure for the Admin suites in this document comes from the single final run named above, which repeated all API and Admin suites on one commit. The earlier runs, in order: the first final run had 106 Admin checks, all passed. Comparing the new Admin test project with the older one (`tests/EVoting.Tests`, §6.9) then found one defect in the new Voter Lookup screen - while the question "which election is open?" had failed (for example because the server could not be reached), the Search Voter tab said "No active election. Open an event first." although the real situation was "unknown" - which was fixed in `VoterSearchViewModel.cs` and covered by `VP-19` (107 checks, all passed). The deliberate-defect checks of the screens (§6.7) then showed 24 screen defects that the tests did not detect on their first run; 12 tests were added for them (119 checks). **Only test files changed for that**: no product file of the Admin changed after the 107-check run. The results of the two earlier Admin runs are kept in the test kit under `tests/results/before-statusline-fix-2026-11-10/` and `tests/results/before-mutation-gaps-2026-11-10/`.

## 1. Summary

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

Grand total of recorded integration and screen checks: **619 passed, 0 failed** (plus 81 × 3 General Assembly checks, 22 Redis checks and 601 unit tests, counted separately above).

**What this does not prove** is in §8. In particular the real xUnit, the real `dotnet build` of the project files, the real `dotnet ef`, a WPF window and the Kiosk application were not run.

## 2. Environment

* A Linux workspace with two CPU cores. MySQL 8.0.46 (a scratch server holding copies of the supplied dump), API built from the repository sources, Redis 7.0.15 from the operating-system packages.
* The API project targets .NET 9; no NuGet access was available, so the **same source files** were compiled by an offline harness project with .NET SDK 10.0.112 (C# 14) and run on the .NET 10 runtime. Nothing was run on .NET 9.
* Nothing touched a production database; every database used here is a throw-away copy (names `rrms_test_*`, `rrms_scale1`); the test helpers refuse any other name.
* The scheduler of the API runs in these tests as it does in production (every 10 seconds). Time-dependent tests choose their own election windows in the future or past and do not depend on the date of the run.

## 3. Suites and totals

| Id | Script | What it covers | Passed | Failed |
|---|---|---|---|---|
| `t01` | `t01_events_rules.py` | Event type, participating branches, position rules (API over HTTP, real MySQL) | 50 | 0 |
| `t02` | `t02_open_gate.py` | The readiness check before an election opens | 16 | 0 |
| `t03` | `t03_eligibility.py` | Eligibility thresholds: API, recalculation, audit, parity with the C# rule on every member | 65 | 0 |
| `t04` | `t04_profiles_roster.py` | Candidate profiles, photos, roster, locks, permissions | 88 | 0 |
| `t05` | `t05_import.py` | Candidate import: staging, validation, conflicts, commit, rollback, concurrency | 75 | 0 |
| `t06` | `t06_import_scale.py` | Imports of 1,000 to 12,000 candidates; roster and profile list timings; query plans | 17 | 0 |
| `t07` | `t07_rep_voting.py` | Representative ballot, candidates, photos, online vote, offline sync, results, closing | 129 | 0 |
| `t07b` | `t07b_rep_race_stress.py` | 1,250 concurrent ballots: no duplicate, none lost, no request answered 500 | 6 | 0 |
| `t08` | `t08_voter_lookup.py` | Server-side paging, search, filters, SQL shape of the voter lookup | 29 | 0 |
| `t08b-real` | `t08b_voter_lookup_scale.py (PROFILE=real)` | Voter lookup at the production membership size (67,515) | 4 | 0 |
| `t08b-stress` | `t08b_voter_lookup_scale.py (PROFILE=stress)` | Voter lookup at about one million members | 5 | 0 |
| `t10` | `t10_scheduler_gate.py` | The 10-second scheduler and the readiness check | 13 | 0 |
| `t12` | `t12_cancelled_requests.py` | A search the caller cancels mid-flight: not logged as a failed request, no 5xx, nothing else harmed (one-million-member copy) | 3 | 0 |
| `L-VP` | `EVoting.Admin.LiveTests` | Admin voter-list screen logic against the real API | 21 | 0 |
| `L-RO` | `EVoting.Admin.LiveTests` | Admin roster screen logic against the real API | 14 | 0 |
| `L-IM` | `EVoting.Admin.LiveTests` | Admin import screen logic against the real API | 17 | 0 |
| `L-RS` | `EVoting.Admin.LiveTests` | Admin results screen logic against the real API | 22 | 0 |
| `L-PR` | `EVoting.Admin.LiveTests` | Admin position-rules grid (editor and client) against the real API | 24 | 0 |
| `L-EL` | `EVoting.Admin.LiveTests` | Admin eligibility settings screen and client against the real API | 21 | 0 |
| `t09` | `t09_ga_regression.py` ×3 | General Assembly flows 1–12 (§5) | 81 per run | 0 |
| `t11` | `t11_redis_cache.py` | Eligibility cache against a real Redis (§6.2) | 16 | 0 |
| `t11b` | `t11b_no_redis_control.py` | The same without Redis: the stale window must appear (control) | 6 | 0 |

Each integration batch starts from a freshly restored database and a freshly started API, so no suite depends on the leftovers of another.

## 4. Part 15 — requirement by requirement

A requirement is listed as covered only when a check that executed in this run proves it. Ids are those printed by the scripts (`PASS [RV-45] …`); where a requirement spans an API check and a screen check, both are listed.

### General Assembly regression tests (1–12) — each id passed in all three configurations of §5

| # | Requirement | Executed checks (script: ids) | Checks | Passed |
|---|---|---|---|---|
| 1 | Existing event creation | `t09` GA-01* | 5 | 5 |
| 2 | Existing branch assignment | `t09` GA-02* | 5 | 5 |
| 3 | Existing candidacy retrieval | `t09` GA-03* | 11 | 11 |
| 4 | Existing candidate approval | `t09` GA-04* | 9 | 9 |
| 5 | Existing candidate image retrieval | `t09` GA-05* | 3 | 3 |
| 6 | Existing ballot retrieval | `t09` GA-06* | 4 | 4 |
| 7 | Existing vote submission | `t09` GA-07* | 15 | 15 |
| 8 | Existing receipt generation | `t09` GA-07a, GA-07k, GA-07l, GA-07m, GA-08* | 6 | 6 |
| 9 | Existing VoterLogs update | `t09` GA-07c, GA-09a | 2 | 2 |
| 10 | Existing offline synchronization | `t09` GA-10* | 9 | 9 |
| 11 | Existing ResultsAggregator | `t09` GA-11* | 6 | 6 |
| 12 | Existing event lifecycle | `t09` GA-12* | 11 | 11 |

Every one of the 12 items above has at least one executed check; 86 of 86 cited checks passed.

### Representative Election tests (1–25)

| # | Requirement | Executed checks (script: ids) | Checks | Passed |
|---|---|---|---|---|
| 1 | Create Representative event | `t01` EV-03, EV-04, EV-05, EV-06, EV-07, EV-08a, PERM-03 | 7 | 7 |
| 2 | Assign participating branches | `t01` RB-*; `t04` LK-01c; `t07` RV-02, RV-55, RV-55b, RO-06 | 11 | 11 |
| 3 | Configure position rules | `t01` PR-02, PR-02b, PR-02c, PR-09a, PR-09b, PR-10, PR-12, PR-13; `L-PR` PRA-03, PRV-03 | 10 | 10 |
| 4 | Import 1,000 candidate profiles | `t06` SC-S1000; `L-IM` IMP-05 | 2 | 2 |
| 5 | Import more than 1,000 profiles | `t06` SC-S2000, SC-S5000, SC-S5000b, SC-S12000, SC-R*, SC-X*; `t05` IM-02c | 17 | 17 |
| 6 | Reuse an existing CandidateProfile in another event | `t04` RS-12, RS-12b; `t06` SC-REUSE; `t05` IM-08c, IM-12e | 5 | 5 |
| 7 | Reject duplicate event assignments | `t04` RS-03, RS-04; `t05` IM-11, IM-11b, IM-15b, DB-01 | 6 | 6 |
| 8 | Reject invalid candidate profiles | `t04` PR-03, PR-03b, PR-03c, RS-06b; `t05` IM-03, IM-03b, IM-17e, DB-02, DB-03 | 9 | 9 |
| 9 | Reject duplicate import rows | `t05` IM-03b, IM-03d2, IM-11, IM-17, IM-17c | 5 | 5 |
| 10 | Preview invalid rows | `t05` IM-03, IM-03c, IM-04, IM-04b, IM-04c, IM-07, IM-07b, IM-07c; `L-IM` IMP-03 | 9 | 9 |
| 11 | Commit candidate import transactionally | `t05` IM-08, IM-08b, IM-08c, IM-09, IM-13b, IM-14, IM-14b, IM-14c, IM-14d, IM-14e, IM-15; `t06` SC-S1000; `L-IM` IMP-06 | 13 | 13 |
| 12 | Retrieve the complete candidate roster | `t04` RS-09, RS-11; `t07` RV-10, RV-11d, RV-20; `t06` SC-R1, SC-R2, SC-R7 | 8 | 8 |
| 13 | Search and paginate candidates | `t04` RS-09b, RS-09c, RS-09d, PR-07, PR-07b; `t07` RV-11, RV-11b, RV-11c, RV-12*, RV-13*; `t06` SC-R3, SC-R4, SC-R8 | 19 | 19 |
| 14 | Verify shared candidate visibility across branches | `t07` RV-04, RV-14; `t04` RS-10 | 3 | 3 |
| 15 | Reject voters from excluded branches | `t07` RV-02, RV-55, RV-55b, RO-06 | 4 | 4 |
| 16 | Reject a candidate assigned to another event | `t07` RV-48, RV-48b, RO-07, RV-60c, RV-30d; `t04` RS-14d | 6 | 6 |
| 17 | Reject more than MaxVotes selections | `t07` RV-45, RV-45b, RV-50, RO-07b | 4 | 4 |
| 18 | Reject fewer than MinVotes selections when required | `t07` RV-46 | 1 | 1 |
| 19 | Reject duplicate candidate selection | `t07` RV-47, RV-60b | 2 | 2 |
| 20 | Prevent repeated ballot submission | `t07` RV-42, RV-42b, RV-42c, RV-60, RV-71, RV-71b, RO-03 | 7 | 7 |
| 21 | Verify receipt generation | `t07` RV-40, RV-41, RV-43, RV-43b, RV-44, RV-44b, RV-44c | 7 | 7 |
| 22 | Verify correct result counts | `t07` RR-01, RR-02, RR-04, RR-05, RR-05b, RR-06, RR-06b, RR-07, RR-08; `L-RS` RES-01, RES-02 | 11 | 11 |
| 23 | Include candidates with zero votes | `t07` RR-03, RR-13; `L-RS` RES-01 | 3 | 3 |
| 24 | Verify offline Representative synchronization | `t07` RO-*, RC-03 | 27 | 27 |
| 25 | Verify concurrent submissions | `t07` RV-70, RV-70b, RV-71, RV-71b, RV-72, RV-72b; `t07b` ST-*; `t05` IM-15, IM-15b | 14 | 14 |

Every one of the 25 items above has at least one executed check; 210 of 210 cited checks passed.

### Position Rules tests (1–8)

| # | Requirement | Executed checks (script: ids) | Checks | Passed |
|---|---|---|---|---|
| 1 | Position selection | `t01` PR-02, PR-03, PR-07c, GA-02, GA-03; `t04` RS-05, RS-05b; `L-PR` PRA-02, PRA-09 | 9 | 9 |
| 2 | Minimum selection configuration | `t01` PR-02b, PR-04, PR-06; `L-PR` PRA-03, PRA-05 | 5 | 5 |
| 3 | Maximum selection configuration | `t01` PR-02b, PR-04, PR-07a; `L-PR` PRA-03 | 4 | 4 |
| 4 | Maximum candidate roster configuration | `t01` PR-02b, PR-05-zero, PR-05-negative, PR-06, PR-08; `L-PR` PRA-03, PRA-04 | 7 | 7 |
| 5 | MaxCandidates enforcement | `t04` RS-07, RS-07b, RS-07c, RS-08; `t05` IM-03d, IM-13c, IM-15b; `t02` OG-02, OG-02b; `L-PR` PRA-07, PRV-05 | 11 | 11 |
| 6 | MinVotes and MaxVotes enforcement | `t07` RV-45, RV-45b, RV-46, RV-50, RO-07b; `t09` GA-07d2, GA-07d3 | 7 | 7 |
| 7 | TotalSeats preservation | `t01` PR-02b; `L-PR` PRA-04; `t07` RR-06b | 3 | 3 |
| 8 | Regression test for existing General Assembly results | `t09` GA-11*; `t01` GA-00, GA-01, GA-01b; `t02` OG-10 | 10 | 10 |

Every one of the 8 items above has at least one executed check; 56 of 56 cited checks passed.

### VoterLookup tests (1–10)

| # | Requirement | Executed checks (script: ids) | Checks | Passed |
|---|---|---|---|---|
| 1 | Page 1 retrieval | `t08` VL-01, VL-01b; `L-VP` VP-01 | 3 | 3 |
| 2 | Last page retrieval | `t08` VL-03, VL-03b, VL-03c; `L-VP` VP-03 | 4 | 4 |
| 3 | Invalid page number | `t08` VL-04; `L-VP` VP-06 | 2 | 2 |
| 4 | Invalid page size | `t08` VL-05, VL-05b; `L-VP` VP-05 | 3 | 3 |
| 5 | Search combined with pagination | `t08` VL-07, VL-07c, VL-07d, VL-07e, VL-09d; `L-VP` VP-08 | 6 | 6 |
| 6 | Page reset after search — API and view model; not rendered | `t08` VL-07b; `L-VP` VP-07, VP-09, VP-10 | 4 | 4 |
| 7 | Correct total record count | `t08` VL-01, VL-06-*, VL-09, VL-10, VL-11; `t08b-real` VS-01; `t08b-stress` VS-01; `L-VP` VP-01 | 10 | 10 |
| 8 | Large member dataset performance | `t08b-real` VS-01, VS-02, VS-04, VS-05; `t08b-stress` VS-01, VS-02, VS-03a, VS-03b, VS-04; `L-VP` VP-18 | 10 | 10 |
| 9 | No unnecessary image loading | `t08` VL-12, VL-13, VL-13b; `L-VP` VP-13 | 4 | 4 |
| 10 | No stale overlapping UI requests — screen logic (view model) against the real API; the WPF window itself was not rendered | `L-VP` VP-11, VP-12 | 2 | 2 |

Every one of the 10 items above has at least one executed check; 48 of 48 cited checks passed.

### Eligibility Settings tests (1–12)

| # | Requirement | Executed checks (script: ids) | Checks | Passed |
|---|---|---|---|---|
| 1 | Retrieve current eligibility thresholds | `t03` EL-01, EL-01b, EL-01c; `L-EL` ELA-02, ELV-01 | 5 | 5 |
| 2 | Update minimum Share Capital | `t03` EL-07, EL-07b, EL-07c; `L-EL` ELA-04 | 4 | 4 |
| 3 | Update minimum Savings Deposit | `t03` EL-08, EL-08b | 2 | 2 |
| 4 | Save and reload values | `t03` EL-09, EL-09b; `L-EL` ELA-04 | 3 | 3 |
| 5 | Reject invalid negative values | `t03` EL-06*; `L-EL` ELA-06, ELV-02 | 8 | 8 |
| 6 | Verify decimal accuracy | `t03` EL-09b, EL-10, EL-10b, EL-11-B, EL-11-Bremark, EL-11-C; `L-EL` ELA-05 | 7 | 7 |
| 7 | Eligibility of a member meeting both thresholds | `t03` EL-11-A, EL-11-D, EL-11-F | 3 | 3 |
| 8 | Ineligibility when one threshold is not met | `t03` EL-11-B, EL-11-C, EL-11-G | 3 | 3 |
| 9 | Other eligibility rules are preserved | `t03` EL-02b, EL-07c, EL-11-E, EL-11-F, EL-11-G, EL-11-Gremark; `L-EL` ELA-09 | 7 | 7 |
| 10 | Audit log creation | `t03` EL-13, EL-13b, EL-13c, EL-13d; `L-EL` ELA-08 | 5 | 5 |
| 11 | Redis cache consistency | `t11` RD-*; `t11b` NC-*; `t03` EL-16 | 23 | 23 |
| 12 | Behaviour of OPEN events under the chosen policy | `t03` EL-18, EL-18b, EL-18c, EL-18d, EL-18e, EL-18f, EL-19, EL-19b; `t10` SG-08, SG-09, SG-10; `L-EL` ELA-01, ELV-06 | 13 | 13 |

Every one of the 12 items above has at least one executed check; 83 of 83 cited checks passed.

Notes on the mapping:

* *Voter lookup 10 (no stale overlapping UI requests)* is proved at the level of the screen's view model against the real API (`VP-11`: a slow answer for an old filter never replaces the rows of a newer one; `VP-12`: every request the screen sends is valid). The WPF window itself was not rendered.
* *Eligibility 11 (Redis cache consistency)* was proved against a real Redis server (§6.2), and a control run without Redis shows that the same test detects an inconsistency. `EL-16` proves the in-process cache is evicted on save.
* *Eligibility 12 (OPEN events)*: the chosen policy is that thresholds cannot be changed or recalculated while an election is open (`409 EVENT_OPEN_LOCK`); the thresholds in force are recorded on the event when it opens and stay with it after it closes (`EL-18b`, `EL-19b`).

## 5. The General Assembly differential

`t09_ga_regression.py` drives the twelve General Assembly flows of Part 15 over HTTP, with 81 inline checks, and records every call (normalised: tokens, timestamps, random receipts and image bytes replaced by markers). It ran three times, each on a freshly restored database:

| Run | API build | Schema | Checks passed |
|---|---|---|---|
| Original build, original schema | original (commit 0b5a180) | original | 81 of 81 |
| New build, new schema | new | migrated | 81 of 81 |
| Original build on the new schema | original (commit 0b5a180) | migrated | 81 of 81 |

* **Original build/schema versus new build/schema:** 100 calls compared — **86 identical, 14 identical apart from added fields** (`eventType`, `candidateCount`, and the new paging fields of the voter lookup), **0 different**.
* **Original build on the new schema** (what an application-only rollback looks like): 100 calls compared — 99 identical, 1 different. The one difference is the order of the position rules on the kiosk ballot (alphabetical instead of creation order); it is documented in `10-ga-regression-report.md` §6 and in `11-deployment.md` §3.3.

Details and the two defects this comparison found (and the third that was not a defect) are in `10-ga-regression-report.md`.

## 6. Other evidence

### 6.1 Database scripts

`sql_parity.sh`: the hand-run SQL scripts (`deploy/sql/005_*`, `006_*`) were compared with the EF migrations on identical copies of the dump, their verify scripts were run, their rollback scripts were run with and without a Representative ballot, and re-runs were checked for idempotence: **48 checks passed, 0 failed**. The offline model tool also reported 0 differences between the model snapshot and the current model (the real `dotnet ef` was not run, §8).

### 6.2 Eligibility cache against a real Redis

`t11_redis_cache.py` ran two API instances against one MySQL database and one real Redis 7.0 server. It proves: the thresholds are cached in Redis with a TTL of at most five minutes; a save on one instance is visible on the other at once (in both directions); with Redis stopped, reads and saves still succeed from the database and the API logs the documented warning and error; after Redis returns both instances agree again. The control (`t11b`) runs the same two instances **without** Redis and must show the stale window; it did.

**History of this control.** The first attempt at a final run (2026-11-09, on commit `00e4605`) failed `NC-01` in the control: its first version did not close the old election of the freshly loaded database before saving, so the save was refused with `409 EVENT_OPEN_LOCK` (the open-election lock of `06-eligibility-policy.md`) and the check then saw the old thresholds. The fault was in the test, not in the product; the refusal was reproduced by saving immediately after a start. The control now closes whatever is open first (`NC-00a`), checks that the save was accepted (`NC-01a`) and restores the thresholds (`NC-03`). The **whole** final run was then repeated from its first step, and repeated once more after the last change to the API (the request-logging change of §6.4); every number in this document comes from that last run. The records of the first attempt are kept in the test kit (`results/attempt1-2026-11-09/`).

| Id | Result | What it proves |
|---|---|---|
| RD-00 | PASS | real Redis answers PONG and both API instances answer |
| RD-01a | PASS | both instances decide with the starting thresholds 3000/4000 |
| RD-01b | PASS | the key evoting:eligibility_thresholds_v1 now exists in Redis with a TTL of at most 300 s |
| RD-02a | PASS | A saves 3300/4300 |
| RD-02b | PASS | B's preview already decides with 3300 (3200 < 3300 -> not eligible): the cache entry B had was replaced through Redis |
| RD-02c | PASS | ...and the settings screen of B agrees (source database) |
| RD-03 | PASS | B saves 3400/4400 and A's preview uses 3400 at once |
| RD-04a | PASS | Redis is really down |
| RD-04b | PASS | with Redis down, A's preview still decides with the saved thresholds 3400 (read from the database) |
| RD-04c | PASS | with Redis down, a save on A still succeeds |
| RD-04d | PASS | ...and B (also without Redis) decides with the new thresholds read from the database (3450 < 3500 -> not eligible) |
| RD-05a | PASS | A logged 'Eligibility cache read failed; reading the database.' (warning) |
| RD-05b | PASS | A logged 'Could not evict eligibility_thresholds_v1; other API instances may use the previous thresholds ...' (error) |
| RD-06a | PASS | Redis is back |
| RD-06b | PASS | both instances decide with 3500 after Redis returned |
| RD-06c | PASS | a save on A after the outage is again visible on B's preview at once (3000) |
| NC-00a | PASS | set-up: no election is open (the thresholds cannot be saved while one is) |
| NC-00 | PASS | B has cached the starting thresholds |
| NC-01a | PASS | A's save of the new thresholds is accepted |
| NC-01 | PASS | A decides with the new thresholds at once (its own cache was evicted) |
| NC-02 | PASS | WITHOUT Redis, B still decides with the OLD thresholds (its cache is its own) - the stale window Redis removes |
| NC-03 | PASS | clean-up: the starting thresholds are restored |

### 6.3 Performance

Voter lookup, wall-clock time as the client sees it (median of 5 calls), on a two-core machine with a warm cache. The scenario names have been generalised (the test uses a real surname and account code from the dump; they are not published).

**Production membership size (67,515 listed voters, a scratch copy of the dump).** The script asserts that every scenario answers within 500 ms (`VS-05`).

| Scenario | listed / matching | rows | median ms | min – max ms |
|---|---|---|---|---|
| ALL page 1 (50) | 67,515 | 50 | **128** | 118 – 140 |
| ALL page 675 (50) | 67,515 | 50 | **291** | 255 – 344 |
| ALL last page (50) | 67,515 | 15 | **261** | 243 – 263 |
| ALL page 1 (100) | 67,515 | 100 | **112** | 105 – 117 |
| ALL branch 502 p1 | 12,957 | 50 | **11** | 11 – 20 |
| ALL search a common surname p1 | 1,040 | 50 | **166** | 156 – 173 |
| ALL search a common surname p20 | 1,040 | 50 | **298** | 271 – 391 |
| ALL search one account code | 2 | 2 | **302** | 291 – 341 |
| ALL search with no match | 0 | 0 | **130** | 124 – 147 |
| ALL search a receipt text that every voter's receipt contains | 22,505 | 50 | **277** | 273 – 309 |
| ALL hasVoted=true p1 | 22,505 | 50 | **59** | 52 – 66 |
| ALL hasVoted=true p225 | 22,505 | 50 | **247** | 220 – 269 |
| ALL hasVoted=false p1 | 45,010 | 50 | **157** | 139 – 181 |
| ALL hasVoted=false last page | 45,010 | 10 | **318** | 296 – 346 |
| ALL branch 502 + voted + search | 109 | 50 | **73** | 48 – 92 |
| TWO page 1 (50) | 1,700 | 50 | **12** | 8 – 13 |
| TWO last page | 1,700 | 50 | **11** | 10 – 14 |
| TWO search a common surname | 81 | 50 | **155** | 139 – 192 |

**Stress size (about 970,000 listed voters, synthetic members).** The script asserts page navigation within 1.5 s and every scenario within 5 s (`VS-03a`, `VS-03b`).

| Scenario | listed / matching | rows | median ms | min – max ms |
|---|---|---|---|---|
| ALL page 1 (50) | 972,130 | 50 | **189** | 185 – 193 |
| ALL page 9,000 (50) | 972,130 | 50 | **771** | 764 – 894 |
| ALL last page (50) | 972,130 | 30 | **928** | 733 – 1,021 |
| ALL page 1 (100) | 972,130 | 100 | **238** | 175 – 289 |
| ALL branch 502 p1 | 29,441 | 50 | **19** | 13 – 34 |
| ALL search a common surname p1 | 18,704 | 50 | **892** | 883 – 987 |
| ALL search a common surname p20 | 18,704 | 50 | **2,245** | 1,987 – 2,407 |
| ALL search one account code | 1 | 1 | **2,899** | 2,791 – 3,040 |
| ALL search with no match | 0 | 0 | **445** | 365 – 476 |
| ALL search a receipt text that every voter's receipt contains | 324,037 | 50 | **2,909** | 2,806 – 3,089 |
| ALL hasVoted=true p1 | 324,037 | 50 | **2,319** | 2,161 – 2,447 |
| ALL hasVoted=true p3,240 | 324,037 | 50 | **3,830** | 3,658 – 4,512 |
| ALL hasVoted=false p1 | 648,093 | 50 | **2,070** | 1,717 – 2,104 |
| ALL hasVoted=false last page | 648,093 | 43 | **3,685** | 3,429 – 3,943 |
| ALL branch 502 + voted + search | 22 | 22 | **130** | 122 – 148 |
| TWO page 1 (50) | 34,008 | 50 | **21** | 20 – 25 |
| TWO last page | 34,008 | 8 | **46** | 43 – 54 |
| TWO search a common surname | 6 | 6 | **2,245** | 2,160 – 2,269 |

The plan of every COUNT and page-id statement uses an index (`VS-04`). The photo column is never read by a list request (`VL-13`). Import and roster timings at 12,000 candidates are asserted by `SC-R1`…`SC-R8` (page and search under 1.5 s, summary under 3 s, export under 10 s) and their query plans by `SC-X1`…`SC-X3`.

### 6.4 Concurrency

`t07b_rep_race_stress.py` sends 1,250 requests in parallel groups (the same voter 15 times with and without a receipt, the same voter with different selections, and 60 different voters at the same instant, five times over; the number is counted from the script - 25 repetitions of 15 + 15 + 8 requests plus five bursts of 60 - because the line it prints itself was not kept in the recorded results). Expected and observed: one stored ballot per voter, every retry with the same receipt answered as a replay, no `5xx`, and no request answered 500 in the API log (`ST-6`). The log is not empty, though: EF Core writes its own ERROR record for every statement that fails, also when the service handles the failure - in this run 272 records, of which 162 name a duplicate-key violation (a same-voter race that the service resolved as a replay or a 409), 1 a deadlock (retried), and 0 are unexplained. `t05` `IM-15` and `IM-15b` do the same for four simultaneous commits of one import batch and six simultaneous assignments into one free slot.

**General Assembly ballots under the same races** (`t09b_ga_concurrency.py`, the original `POST /api/v1/votes` path; run on the original build with the original schema and on the new build with the migrated schema, each on its own freshly restored scratch database):

| Check | What is raced | Original build | New build |
|---|---|---|---|
| `GC-1` | 25 x (15 identical General Assembly submissions, same receipt): every answer 200, one original + 14 replays, one stored ballot | PASS | PASS |
| `GC-2` | 25 x (15 identical submissions, no receipt): exactly one 200, fourteen 409 'already voted', one stored ballot | PASS | PASS |
| `GC-3` | 25 x (8 submissions of one voter with different selections and receipts): exactly one 200, seven 409, ONE ballot in the database and it is exactly the winner's | PASS | PASS |
| `GC-4` | 5 x (60 different voters at the same instant): every 200 stores that voter's complete ballot once with its own receipt; every other answer left no vote rows and no 'voted' mark | PASS | PASS |
| `GC-5` | the 21 voters whose ballot was refused in those bursts voted successfully when they tried again | PASS | PASS |
| `GC-6` | every answer that was not 200/409 is a 500, and the API log explains each one: one request record per 500, each a MySQL deadlock; no ERROR record is unexplained | PASS | PASS |

What the two runs measured (the 60-voter bursts use voters with adjacent member ids, the harder case; see the sweep below):

| | requests sent | ballots of the 60-voter bursts answered 500 | refused ballots that voted successfully on retry | ERROR records in the API log: total = requests answered 500 + EF Core's own + the application's own; how many of the total name a deadlock; how many are unexplained |
|---|---|---|---|---|
| original build | 1271 | **21 of 300** (7.0 %) | all 21 | 4010 = 21 + 3968 + 21; 1034 name a deadlock; 0 unexplained |
| new build | 1277 | **27 of 300** (9.0 %) | all 27 | 4302 = 27 + 4248 + 27; 1116 name a deadlock; 0 unexplained |

A `500` here is the database refusing one of two colliding transactions after the service had retried it four times (MySQL deadlock 1213). It belongs to the original voting path and happens in the original build as well as in the new one. The checks above show what matters for the election: a refused ballot leaves nothing behind (no vote rows, no 'voted' mark), the voter's next attempt succeeds, and no ballot was lost or counted twice. The ERROR records are EF Core's own report of every failed statement, including the ones the service retried successfully, which is why there are so many more of them than `500`s.

**From how many simultaneous voters does the original voting path answer 500?** (`t09c_ga_burst_sweep.py`; each cell: ballots answered 500 of ballots submitted, six bursts per size, every voter a different member with an open session)

| Voters submitting at the same instant | original build, adjacent member ids | new build, adjacent member ids | original build, spread member ids | new build, spread member ids |
|---|---|---|---|---|
| 5 | 0 of 30 (0.0 %) | 0 of 30 (0.0 %) | - | - |
| 10 | 0 of 60 (0.0 %) | 0 of 60 (0.0 %) | - | - |
| 20 | 1 of 120 (0.8 %) | 0 of 120 (0.0 %) | - | - |
| 30 | 8 of 180 (4.4 %) | 17 of 180 (9.4 %) | 0 of 180 (0.0 %) | 0 of 180 (0.0 %) |
| 40 | 20 of 240 (8.3 %) | 41 of 240 (17.1 %) | - | - |
| 60 | 66 of 360 (18.3 %) | 78 of 360 (21.7 %) | 0 of 360 (0.0 %) | 0 of 360 (0.0 %) |

"Adjacent member ids" means the next members by number, whose keys sit next to each other in the index; "spread" means the same number of members chosen over the whole range. The pattern fits InnoDB's gap locks - the original path looks for a voter's row with `SELECT ... FOR UPDATE` before creating it, and a lock on a row that does not exist yet covers the gap around it, so neighbours in the index collide and members far apart rarely do - but this was not confirmed with lock traces. This is a scratch machine (two cores; the API, the database and the test on one virtual machine): the pattern and the order of magnitude matter, not the exact percentages, which changed from run to run. In every run, every refused ballot left nothing behind and succeeded on retry (`CS-1`), and every `500` was a deadlock named in the API log (`CS-2`).

Because single runs of this kind vary a great deal (a rate can double or halve between two runs), the comparison of the two builds was repeated at 40 simultaneous voters with adjacent member ids, in many more bursts: original build **122 of 1200** ballots answered 500 (10.2 %), new build **142 of 1200** (11.8 %). The new build is judged against the original by this repeat, not by any single run above.

**Requests the caller abandons** (`t12_cancelled_requests.py`, run on the one-million-member copy, where a search takes about a second): 30 searches are cut off after 80 ms, the way the Admin's search box cuts off the previous request when the next letter is typed.

| Check | What | Result | Observed |
|---|---|---|---|
| `CR-01` | 30 searches were cut off while the server was working on them: the API noticed at least 24 of them (statistics 4xx +30, the 499 the handler writes) | PASS | 4xx +30, 5xx +0 |
| `CR-02` | none of them is logged as a failed request (without the change Serilog writes 'responded 500' with a stack trace for each) and the application logs no error for them | PASS | 0 ERROR record(s): answered 500 0 (caller gave up 0), application 0, unexplained 0, EF's own 0 [] |
| `CR-03` | no 5xx was counted for them, and the next normal request is answered 200 with the same totals as before | PASS | 5xx +0; next request 200 |

The same test against the build before `RequestLogLevel` was added (commit `5aae959`, one run): `CR-01` PASS, `CR-03` PASS, **`CR-02` FAIL** - 30 ERROR record(s): answered 500 30 (caller gave up 30), unexplained 0 []. So the test does detect the problem, and the change removes it. EF Core can still write its own one-line note (`An error occurred using the connection`) when a connection is cancelled while it is being opened; the test allows that and prints how many there were.

### 6.5 Unit tests

`EVoting.Api.Tests` — **601 passed, 0 failed** of 601, run through a minimal runner (`harness/xunitshim`) that compiles the test sources unchanged against a small stand-in for xUnit. **This is not xUnit**: parallelism, fixtures, traits and the richer assertions are absent. The first run with the real package (`dotnet test EVoting.Api.Tests`) is yours (§8).

| Test class | Tests |
|---|---|
| `ApiDtoContractTests` | 9 |
| `AuditWriterTests` | 8 |
| `CandidateCodeRulesTests` | 27 |
| `ControllerAuthorizationContractTests` | 7 |
| `ElectionClockTests` | 6 |
| `EligibilityThresholdsTests` | 7 |
| `EventLockPolicyTests` | 15 |
| `EventTypesAndResolutionsTests` | 22 |
| `MemberEligibilityPolicyTests` | 24 |
| `MoneyRulesTests` | 15 |
| `PasswordServiceTests` | 15 |
| `PositionRuleValidatorTests` | 30 |
| `ReceiptCodeFormatTests` | 16 |
| `RepImportRequestCapsTests` | 15 |
| `RepresentativeBallotRulesTests` | 25 |
| `RepresentativeCastTimeTests` | 17 |
| `RepresentativeHttpTests` | 29 |
| `RepresentativeImportPlannerTests` | 58 |
| `RepresentativeImportRowRulesTests` | 69 |
| `RepresentativeRankingTests` | 31 |
| `RepTextTests` | 42 |
| `RequestLogLevelTests` | 12 |
| `RosterVersionTests` | 7 |
| `StationCredentialPolicyTests` | 26 |
| `VoterLookupPagingTests` | 66 |
| `WrongEventTypeExceptionTests` | 3 |

### 6.6 Mutation check of the unit tests

`mutate_api_units.py` plants 49 deliberate defects (one at a time) in a copy of the pure rule code (ballot limits, tie handling, rounding, import row rules, lock policy, cast-time windows, roster fingerprint, …), builds the test project and runs it. A defect is *detected* when at least one test fails. **49 of 49 detected.**
No defect survived.

### 6.7 Mutation check of the Admin screens

The same method applied to the screen logic: one defect at a time in the real view models, then the live tests of that screen against the real API.

| Screen | Detected on the first complete run | Detected after the added tests | Not detected |
|---|---|---|---|
| Search Voter tab (paging, filters, export) | 7 of 10 | 10 of 10 | none |
| position-rules grid (event editor) | 18 of 18 | 18 of 18 | none |
| roster screen | 34 of 34 | 34 of 34 | none |
| import screen and workbook reader | 71 of 72 | 72 of 72 | none |
| eligibility settings screen | 7 of 13 | 13 of 13 | none |
| Representative results screen | 30 of 44 | 44 of 44 | none |

The first run of the **import screen and workbook reader** group was stopped by the time limit of the tool that ran it, after 70 of 72 defects had been tried (each of those is recorded in the output file). The remaining 2 (P11, P12) were run afterwards in the same way, that run starting with a run without any defect that passed. The working copy that the stopped run left behind is rebuilt from the real tree before every run, so no defect was carried over.

The first complete run of a group did not detect every defect. For each one it missed, a test was added (or the case was judged, as the last column says), and the missed defects were then run again with the complete, updated set of tests of that screen (the run without any defect passed first). Tests were only added, never weakened, so what the first run detected stays detected.

| Screen | Defect | First run | After the added test | What was done |
|---|---|---|---|---|
| Search Voter tab (paging, filters, export) | V05: the search text is sent untrimmed | not caught | detected | A real gap, and one the wire alone cannot show: the service layer trims the text once more before it goes on the wire. `VP-12` now also checks that the filter the screen itself applied holds the trimmed text. |
| Search Voter tab (paging, filters, export) | V08: Export is offered without the permission to view elections | not caught | detected | A real gap: the case happens when the role loses the permission while the list is still on screen. New check `VP-20` takes the permission away from the open session (Export goes off and the tooltip says why) and gives it back (Export returns). |
| Search Voter tab (paging, filters, export) | V10: the empty-result line does not say that nothing matches | not caught | detected | A real gap: no check searched for a name that matches nobody. New check `VP-21`: such a search says "No matching members.", shows no rows, cannot be exported, and clearing the search brings the list back. |
| import screen and workbook reader | I47: a late answer about the batch replaces the one now on screen | not caught | detected | A real gap that the test transport could not show: a request that is held back is read by the server only afterwards, so an answer was never older than what was on screen. The transport of the live tests now can hold an answer back after the server has given it, and new check `IMP-17` uses it: the screen is entered again, its new read of the batch is held back, a decision is made meanwhile, and the old answer must not bring the old picture back. |
| eligibility settings screen | E03: Save is offered to a role that may only view | not caught | detected | A real gap: `ELV-07` looked at Save before the view-only user had typed anything, and with nothing typed Save is off for another reason. It now types an amount first and checks that Save stays off, with the reason "Your role may view these settings but not change them.", and that Save comes back when the permission does. |
| eligibility settings screen | E05: the save is sent without the version the screen was opened on | not caught | detected | A real gap that depended on test order: the screen tests alone ran with the settings still at version 0, where "always send 0" happens to be right. New check `ELV-09` first stores a version above zero, opens the screen, saves, and saves a second time from the same screen; both saves must be accepted. |
| eligibility settings screen | E08: Finish recalculation is offered when nothing is pending | not caught | detected | A real gap: `ELV-08` checked that Finish recalculation is offered while a recalculation is pending, and that nothing is pending afterwards, but not that the offer is gone. It now checks that too. |
| eligibility settings screen | E11: a change of the savings amount alone is not noticed | not caught | detected | A real gap: every earlier test changed the share capital, or both amounts together. `ELV-09` now changes the savings amount alone and checks that the screen notices it, offers Save, and that the save is accepted. |
| eligibility settings screen | E12: a refusal of the save keeps the typed amounts out of the box (they are replaced by the saved ones) | not caught | detected | A real gap: only an election that was already open when the screen loaded was tested. New check `ELV-10` types amounts, lets an election open behind the screen's back, then saves: the API refuses, the user is told which election is open, the screen locks, and the typed amounts are still in the boxes. |
| eligibility settings screen | E13: the optional reason is sent untrimmed | not caught | detected | A real gap: no check looked at the reason that goes on the wire. `ELV-09` now types a reason with spaces around it and checks that the request carries it without them. |
| Representative results screen | R07: the winner flag is shown before the first ballot | not caught | detected | A real gap: every position of the fixture had more candidates than seats, so before the first ballot the server never flagged anybody as a winner and the rule "no winner before the first ballot" was never exercised for winners. New check `RES-16` uses a position with no more candidates than seats (the server flags both candidates as winners with 0 votes) and requires that the screen calls nobody anything. |
| Representative results screen | R16: the ballot number of a candidate is left out | not caught | detected | A real gap: no candidate of the fixture had a ballot number, so the column could be empty without anyone noticing. `RES-16` gives one candidate ballot number 7 and requires 7 in the grid and nothing for the candidates who have none. |
| Representative results screen | R17: a position without a number of seats shows 0 instead of "not set" | not caught | detected | A real gap that was hard to reach: the API refuses a rule with fewer than 1 seat, so "not set" appears only for a roster position that has no rule (data the roster screens never produce, which the server still lists). `RES-16` creates such a position in the test database and requires "not set" instead of 0. |
| Representative results screen | R20: the line under the grid does not say that the election has no positions | not caught | detected | A real gap: no check opened an election without a single position rule. `RES-16` does, and requires the line "This election has no positions yet." |
| Representative results screen | R23: a difference between the voter log and the ballots is hidden while voting is open | not caught | detected | A real gap: a difference between the voter log and the ballots was only checked on a closed election. `RES-17` opens an election, removes one voter-log entry and requires the warning with the advice for an open election (exact text), and the stricter text once the election is closed. |
| Representative results screen | R29: a late answer for the election left behind is accepted (only the order of the requests is checked) | not caught | detected | A real gap: while the election box is being read again, a choice made by the user does not go through the usual reset, so only the check for the chosen election protects the screen. `RES-20` (part C) holds the box read back, chooses the other election meanwhile, lets an answer for the election just left arrive, and requires that it changes nothing. |
| Representative results screen | R30: a late answer that was overtaken by a newer request is accepted (only the election is checked) | not caught | detected | A real gap: no check let an old answer arrive after a newer request had been answered. `RES-20` (part A) holds back the answer of the timer's request after the server gave it, starts a newer request with a push, and requires that the old answer, when it arrives, does not bring the older figures back. |
| Representative results screen | R31: the failure of an old request marks the screen out of date | not caught | detected | A real gap: no check made an old request fail after a newer one had succeeded. `RES-20` (part B) holds the timer's request back and then fails it, and requires that the newer figures are not marked out of date and that no error is shown. |
| Representative results screen | R35: the timer keeps reading after the user has left the screen | not caught | detected | A real gap: the test timer only ticked while it was switched on, so a tick that was already queued when the user left could not be simulated. The test timer can now deliver such a tick (`FireQueued`) and `RES-18` requires that it reads nothing. |
| Representative results screen | R37: the connection coming back does not read the figures again | not caught | detected | A real gap: the live connection was never switched in these tests. `RES-18` takes it down, adds a ballot meanwhile, brings it back and requires one new read; it also requires no read for a connection that did not drop and none after the user left. |
| Representative results screen | R38: a reload that finds another election chosen keeps the figures of the previous one | not caught | detected | A real gap: no check made the chosen election disappear from the election box. `RES-22` changes the kind of the chosen election (so the box falls back to the newest one), holds the new election's answer back, and requires that nothing of the old election, nor its filters, stays on screen. |
| Representative results screen | R39: the box of elections is not read again when the results report another status | not caught | detected | A real gap: the earlier checks changed an election's status with a push, which reads the box anyway. `RES-19` opens and closes an election with no push and requires the box to show the new status once the figures are read (both ways: by choosing the election again, and by the timer). |
| Representative results screen | R40: a position filter that no longer exists stays selected after a refresh | not caught | detected | A real gap: no check removed a position while a filter on it was set. `RES-21` takes the position out of the rules and refreshes with the filter set, and requires the filter to go back to "All positions" (and that a filter that still makes sense stays). |
| Representative results screen | R43: the export does not warn that the file of an open election is not final | not caught | detected | A real gap: the export was only checked on a closed election. `RES-17` exports while voting is open and requires the sentence that the file is not final, and that the file of a closed election does not carry it. |

### 6.8 XAML static check

The WPF project could not be built or rendered here (no Windows runtime). Its XAML was checked statically against the real view-model sources: type names, bindings, templates, triggers, resource references. Result of the last chain run (`tools/run-xaml-check.sh`): 50 XAML files checked, **0 errors, 1 warning(s)** (the report is in the Admin kit). A passing check says the XAML is consistent with the code, **not** that it looks right.

The one warning is old and is not in a file this work changed: the converter `FontAwesomeIconConverter` (supplied with the Admin) declares no `[ValueConversion]`, so the checker cannot compare its input and output types with the bindings that use it.

### 6.9 The older Admin test project

`tests/EVoting.Tests` is an offline harness that came with the Admin (it is not shipped). It compiles the API sources of an **earlier snapshot of the API** beside it and runs the real controllers on an in-memory stand-in for EF Core, with the real Admin core library on top. It could not be pointed at this branch: the stand-in lacks about 25 members that the new code uses (raw SQL, bulk updates, relationship builders). Extending the stand-in would have tested the stand-in, not the product, so the project was run against the earlier snapshot of the API, once with the Admin as supplied and once with the Admin as delivered (same machine, same day):

| | Passed | Failed | Skipped | Selected |
|---|---:|---:|---:|---:|
| Admin as supplied | 1140 | 7 | 7 | 1154 |
| Admin as delivered | 1132 | 13 | 7 | 1152 |

The delivered run was made a second time on the final delivered tree, after two small helpers had been added to the Admin tests. The totals and the list of failing checks were identical to the first run, and so was every pass/fail line; the table shows the second run.

* **7 checks fail in both runs.** They fail on the Admin as supplied and have nothing to do with this work: “shell: before data arrives the status bar shows the legacy defaults; with no election open they stay”; “shell: a user without EVENT_VIEW still sees which election is open, but no turnout is requested and no screen…”; “members export: banner, stripe, info line, header, hints, banded rows with the right colours and number forma…”; “styled template: banner, instructions, header, hints, the sample row, 100 banded blank rows, footer; contact …”; “the WPF Admin and its core contain no database code, no connection string, no password hashing, no mediator f…”; “project files: the core has no package or project reference; the WPF project has the three allowed packages a…”; “a scan of the Admin source tree for secret-looking text”.
* **6 more fail only in the delivered run**, all for one reason: the earlier API snapshot has no such routes or permission codes. They compare the Admin with the server's route table and permission list: “permission code SettingsEligibilityView is 'SETTINGS_ELIGIBILITY_VIEW' in the Admin and on the server”; “permission code SettingsEligibilityEdit is 'SETTINGS_ELIGIBILITY_EDIT' in the Admin and on the server”; “permission codes: the same 28 on both sides, in capitals with underscores, none twice”; “every member of the route table exists on the server (the path it builds matches a controller route)”; “every call to the server uses a route-table member, and the server offers that verb on that route”; “every call finds exactly one controller action (no route is ambiguous), and none of them is an anonymous one …”. The same comparison for the new routes is made by `tests/EVoting.Admin.LiveTests` against the real API (§3, “Admin screen logic against the real API”).
* Expectations that changed **on purpose** were updated: the sidebar (14 entries and their permissions), the page catalogue (14 screens), the registered services (three new), the Voter Lookup wording (the toolbar no longer shows the voted / pending split of the whole list; the status line shows the page range), and the “open every page” checks, which now cover the ten screens the earlier API has routes for.
* Four checks that described the old whole-list loading of Voter Lookup were **retired**, each with a comment naming the live check that replaces it (`VP-01`…`VP-04`, `VP-08`, `VP-14`, `VP-15`, `VP-17`).
* This comparison found **one real defect** in the new Voter Lookup screen (the status line claimed “no active election” while the election was unknown). It was fixed and is now covered by `VP-19` and by the mutation check of the voter tab (§6.7).

## 7. How to run it again

```bash
# API unit tests (needs NuGet; this is the first real-xUnit run)
dotnet test EVoting.Api.Tests
# the integration kit: scratch MySQL 8.0 + the API on a copy of the dump + python3 with 'requests'
bash tests/all_suites.sh "t01_events_rules.py+t02_open_gate.py" t03_eligibility.py t04_profiles_roster.py t05_import.py \
                         t06_import_scale.py t07_rep_voting.py t07b_rep_race_stress.py t08_voter_lookup.py t10_scheduler_gate.py
bash tests/ga_three_runs.sh && python3 tests/compare_ga.py baseline new     # General Assembly differential
bash tests/sql_parity.sh                                                      # SQL scripts versus EF migrations
bash tests/run_redis_test.sh                                                  # needs redis-server
bash tests/run_redis_negative_control.sh                                      # the control: the same two instances without Redis
bash tests/run_admin_live.sh                                                  # Admin screen logic against the API
bash tests/final_all.sh <tag>                                                 # everything above, strictly one after another, with the recorded files
```

The kit refuses to touch any database whose name does not start with `rrms_`. Its README lists the set-up (scratch server, dump restore, the development users the scripts create).

## 8. What was not run

Say of any item here that it is **unproven**, not that it fails.

| Not run | Why it matters | What to do |
|---|---|---|
| **Real xUnit** (`dotnet test EVoting.Api.Tests`) | the unit tests ran through a minimal stand-in | run it once on a machine with NuGet access |
| **The real `dotnet build` / `publish` of `EVoting.Api.csproj`** on .NET 9 | the sources were compiled by an offline harness with a newer SDK; package versions and analyzers of the real project were not exercised | build the solution |
| **The real `dotnet ef`** (`migrations has-pending-model-changes`, `database update`) | the migration files and the model snapshot were produced by an offline stand-in and compared with the SQL scripts (§6.1) | run `dotnet ef migrations has-pending-model-changes` once |
| **WPF** (building, rendering, input) | the Admin was never built as a WPF application here; view-model logic ran against the real API, XAML was checked statically | build the Admin on Windows and click through the screens listed in `11-deployment.md` §5 |
| **The real `.xlsx` reader and writer** (ClosedXML) in the Admin | the live tests feed workbooks through a stand-in; the import, template and export file formats were exercised through that stand-in only | import and export one small workbook |
| **The Kiosk application** and the Registration Station | not part of this repository and not modified; the Kiosk work list is `08-kiosk-offline.md` §4 | see that document |
| Kiosk hardware, tablets, networks | — | — |
| **A production database**, MariaDB, any MySQL other than 8.0.46 | the scripts and queries were tested on 8.0.46 only | rehearse on a restored copy (`11-deployment.md` §3.1) |
| Redis in a cluster, with authentication or TLS | only a single local server was tested | test your own setup |
| Build time of the two new indexes and write load of a recalculation on a production-size server | measured only on a small machine with a warm cache | measure on the rehearsal copy |
| The older Admin test project (`tests/EVoting.Tests`) against **this** branch of the API | its in-memory EF stand-in lacks about 25 members the new code uses; it ran against an earlier API snapshot instead (§6.9) | extend the stand-in, or retire the project; the live tests of `tests/EVoting.Admin.LiveTests` cover the same Admin logic against the real API |

