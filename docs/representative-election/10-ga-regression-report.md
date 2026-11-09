# 10 — General Assembly regression report

Part 15 requires proof that the existing General Assembly (GA) behaviour still works. This report compares the **original API build** with the **new build**
on the twelve required GA tests, and also runs the original build against the **new database schema** (the "roll back the application only" case of `04-rollback.md`).
Everything ran on scratch copies of the supplied database dump (MySQL 8.0.46); nothing was run against a production database. Run date: 2026-11-09 (final runs, after the last code change to the API).

## 1. Result in one paragraph

All 81 inline checks pass on all three combinations. Of 100 recorded calls, the new build answers 86 exactly like the original, 14 with the same answer plus added fields
(`eventType`, `candidateCount`, and the paged voter-lookup fields), and **0 differently**. The comparison found one real difference — a list whose order was never specified and changed when a new unique index
appeared (the kiosk position rules) — and it is fixed (§5.1). The original build on the new schema works, with that one ordering difference (§6).

## 2. Method

One script drives the twelve GA flows over HTTP and records, for every call, the status code and the body. Volatile values (tokens, timestamps, random receipt codes) are replaced by stable markers, photo bytes by a hash, and member data is reduced to
field names, so no personal data is stored. Inline checks assert the business outcome of each step, which also catches a flow that is
broken in *both* builds. The same script ran three times, each on a freshly restored database:

| Run | API build | Database schema | Inline checks | ERR lines in the API log |
|---|---|---|---|---|
| A — baseline | original (`main` at `0b5a180`, built separately; the build contains no Representative type) | original dump, no migration | 81 / 81 pass | 14 |
| B — new | this branch | both migrations applied | 81 / 81 pass | 14 |
| C — rollback case | original | both migrations applied | 81 / 81 pass | 14 |

The 14 `ERR` lines are the same in the three logs. Each is a validation rejection (`ArgumentException`, answered to the client with 400) for an invalid ballot that the script sends on purpose: an unapproved or unknown candidate,
a duplicate selection, too many or too few choices for a position, and one bad item in a bulk batch. There is no `MySqlException` and no `DbUpdateException` in any log, and no other exception type.

A comparison tool then lists every difference between two transcripts and classifies it: `ADDED` (a field only the newer side returns), `REMOVED`, or `CHANGED` (different value, status or list length).

## 3. What the twelve required tests cover

| # | Required GA test | Calls compared | Inline checks | What is asserted (examples) |
|---|---|---|---|---|
| 1 | Event creation | 8 | 5 | create, read back, duplicate title refused, invalid dates refused |
| 2 | Branch assignment | 6 | 5 | list, change on a draft, restore, unknown branch refused, outdated copy refused (409) |
| 3 | Candidacy retrieval | 15 | 11 | nine candidacies created, list, read one |
| 4 | Candidate approval | 10 | 9 | approve, reject with reason, unknown id 404, approval refused while an election is open |
| 5 | Candidate image retrieval | 2 | 3 | image bytes unchanged, absent image is 204/404, image on the ballot |
| 6 | Ballot retrieval | 5 | 4 | approved candidacies only, rules, kiosk member snapshot, token required |
| 7 | Vote submission | 17 | 15 | accepted ballot, no second vote, duplicate/over-limit/under-minimum/unknown/unapproved candidate, wrong event, empty, no token, kiosk token, client receipt replay |
| 8 | Receipt generation | 3 | 2 | receipt looked up without signing in; unknown receipt keeps its original answer |
| 9 | VoterLogs update | 1 | 1 | the vote-status endpoint; the rows themselves are checked in 7 (one vote row per selection, one voter-log row with receipt) |
| 10 | Offline synchronisation | 8 | 9 | single ballot, replay, second ballot for the same voter, kiosk token required, unapproved candidate, bulk batch with one result per ballot, bulk replay, empty bulk |
| 11 | Results (ResultsAggregator) | 7 | 6 | counts equal vote rows, zero-vote candidacies kept, turnout counts online and offline voters, dashboard, voter lookup answers; turnout-by-branch, an unknown event and the audit log are compared call by call |
| 12 | Event lifecycle | 18 | 11 | 404 before opening, open, kiosk sees it, close, no current event, results remain, event with votes cannot be deleted, event without votes can |
| | **Total** | **100** | **81** | |

Test 8 of the Position Rules list ("regression test for existing General Assembly results") is covered by tests 11 and 12 above, and the rules a GA ballot is served with by test 6.

## 4. Original build against new build (run A against run B)

`identical: 86   additive-only: 14   different: 0   (of 100)`

The 14 additive-only calls touch seven endpoints. An old client that ignores unknown fields is unaffected.

| Endpoint | Added by the new build |
|---|---|
| `GET /api/v1/admin/events` | `items[].eventType` |
| `POST /api/v1/admin/events` | `eventType`, `candidateCount` |
| `GET /api/v1/admin/events/{id}` | `eventType`, `candidateCount` |
| `PUT /api/v1/admin/events/{id}` | `eventType`, `candidateCount` |
| `GET /api/v1/events/current` | `eventType` |
| `POST /api/v1/auth/voter-verify` | `event.eventType` |
| `GET /api/v1/admin/voter-lookup` | `items`, `totalCount`, `totalPages` (the old names `data` and `total` are still returned) |

Every GA event reports `eventType = "GENERAL_ASSEMBLY"`.

## 5. What the comparison found

### 5.1 A real difference, fixed: kiosk position rules

`GET /api/v1/events/{id}/position-rules` had no ORDER BY. After the new unique index on `(EventId, Position)` MySQL answered from that index, so the positions came back alphabetically (Audit Committee before
Board of Director) — a visible change of the ballot order on a General Assembly kiosk. Fixed with `ORDER BY Id` (the order the rules were created in). The same difference appears, reproducibly, whenever the
**original** code runs on the new schema (§6); with the new build it is gone (§4).

### 5.2 A list whose order within one second is undefined: made repeatable

`GET /api/v1/admin/audit/{eventId}` orders by `VotedAt`, a `datetime` column with one-second precision. Voters who voted in the same second share a value, and the database then returns them in no defined order.

* **Not caused by the new index.** An early note attributed a difference in this list to the new voter-lookup index; the measurement below does not support that.
* **What was measured** (2026-11-09, two scratch databases, one with the original schema and one migrated, one election whose voter-log rows all have the same `VotedAt`, member ids in random order):
  MySQL answers this query from `IX_VoterLogs_EventId` on **both** schemas (the new index is only listed as *possible*). With 6 tied rows they arrive in `VoterLogId` order on both; with 40 000 tied rows they arrive
  in neither `VoterLogId` nor `MemberId` order on both. So the original code leaves the order of rows within one second to the database, and the new index does not change that.
* **What caused the one-run differences**: the script finishes in a few seconds, so whether two ballots land in the same second or in neighbouring seconds changes from run to run; when a ballot crosses a second boundary,
  the list is correctly ordered by time and differs from a run in which all ballots fell into one second. That is the clock, not the code.
* **What was changed**: a second sort key, `VoterLogId` (the order in which the voters first appeared in the election), so the order within a second is the same on every call. It is a robustness improvement, not
  the repair of a regression. And the script sets the `VotedAt` of the test election's ballots to one instant (one UPDATE on the scratch database, the same in every run) before it reads the list, so the comparison
  tests the order of tied rows and does not depend on the clock.

**Other list queries.** A scan of the API source found 68 list queries without any ORDER BY. Each was reviewed from its source line. Most are internal (row locks, counts, groupings that feed a dictionary, lookups by key).
The ones that reach a client from a table that gained an index were examined in detail: the kiosk member snapshot — checked with `EXPLAIN`, MySQL picks the same index before and after the migration, so its order is
unchanged; the member grid — already has a total order (`MemberId` as the last key); the voter lookup — new, total order; turnout by branch — sorted by name in memory. Nothing else needed a change.

## 6. The original build on the new schema (application-only rollback)

Run C, compared with run A: `identical: 99   additive-only: 0   different: 1   (of 100)`. All 81 checks passed and the API log shows nothing beyond the 14 deliberate rejections, so the original code **reads and writes**
the migrated database (creating events relies on the defaults of the new columns; deleting events is not blocked by the new foreign keys). The one difference is the ordering effect of §5.1, because the original code
still lacks the ORDER BY:

| Call | Difference | Consequence |
|---|---|---|
| `GET /api/v1/events/{id}/position-rules` | positions listed alphabetically instead of in creation order | the order of positions on a General Assembly kiosk ballot may change |

If the application has to go back while the schema stays, expect this. To avoid the ballot-order change, either keep the new build, or roll the schema back as well (only possible while no Representative ballot exists —
`04-rollback.md` §3). **Not run:** the original build while a Representative event exists; `04-rollback.md` §4 tells the operator to close those events first, based on reading the code.

## 7. Limits of this evidence

* One script, written by the same author as the changes, on scratch copies of one database; a real Redis was not used.
* Tokens, timestamps and receipt codes are normalised, so a difference in only those values would not be seen. Photo bytes are compared by a hash.
* Time-driven behaviour was kept out of the comparison. The dump's open election overlaps any new schedule, so the script closes that election and moves its window into the past with one UPDATE on the scratch database
  (the same for all runs), and the audit-list fixture of §5.2 sets all ballot times of the test election to one instant. The automatic opening and closing by the scheduler is **not** part of this comparison; it has its own test (`SG-01`…`SG-10b`, see `09-testing.md` when written).
* Simultaneous GA ballots (concurrency) were not compared between the builds.
* Only the API was compared; the Admin application and the Kiosk application were not involved.
* The script and the comparison tool are part of the author's verification kit and are not stored in this repository.
