# 06 — Voter lookup: server-side paging

The Admin screen "Search Voter" used to read the **whole** list: it asked for 500 rows at a time until it had every member of the election's branches (136 requests at 67 515 members) and paged them on the client, and for each of those requests the API
itself loaded the whole list again before cutting out its 500 rows. `GET /api/v1/admin/voter-lookup` now pages in the database, and the screen asks for the one page it shows (25, 50 or 100 rows, default 50).
The request and answer fields are in `05-api-contracts.md` §10; this document says how it works, which indexes it uses and what was measured. All figures come from scratch copies of the supplied database
(MySQL 8.0.46, 2 CPU cores, API and database on the same machine); no production system was involved.

## 1. What the endpoint returns

The voters of one election: **active, eligible members of the election's participating branches**, with the voter-log fields (`hasVoted`, `votedAt`, `receiptCode`) of this election. This is the list the old endpoint produced;
only the way it is produced changed. Order is member name, then member id, so equal names never swap places between pages.

Filters: `search` (name, account code, or this election's receipt code; cut to 100 characters), `branchId`, `hasVoted`. Paging: `page` ≥ 1, `pageSize` 1–500 (default 50). Answer: `totalCount`, `totalPages`, `items`, plus the old names `total` and `data` with the same values.
A page past the end is an empty page, not an error.

### Differences from the previous endpoint

The rows, their order and the totals are the same (checked against the original build on the same data, §4.1). What did change in the contract:

| | Previous build | Now |
|---|---|---|
| `pageSize` when not sent | 100 | 50 (the Admin screen always sends its own value) |
| `pageSize` above the ceiling | cut to 500; the answer says which size was used | unchanged |
| `page` or `pageSize` below 1 | `pageSize` silently raised to 1; a `page` below 1 behaved like page 1 | `400 VALIDATION` |
| Answer fields | `eventId`, `total`, `page`, `pageSize`, `data` | the same five, plus `totalCount`, `totalPages`, `items` |
| Work per request | read every member of the election and every voter-log row into memory, filter and page there | three short statements, one page of rows (§2) |

## 2. How a request is answered

Three short statements, none of which reads the whole member table:

1. **COUNT** of the filtered list (exact `totalCount`).
2. **One page of member ids** (`ORDER BY MemberName, MemberId LIMIT/OFFSET`).
3. **The rows of those ids** (members with their branch, and the voter-log rows of this election) — at most `pageSize` of each. No photo or other wide column is read (`MemberImage`, address, contact and share columns are never selected).

Two details keep it fast on a very large membership:

* The `IsActive`, `IsEligibleToVote` and `HasVoted` conditions are sent as parameters (`col = @p`). EF Core would otherwise write a bare boolean (`WHERE col`), which MySQL reads as `col <> 0` — a range, which can neither walk an index in name order nor use a `(EventId, HasVoted, …)` index.
* A receipt-code search first looks up this election's matching receipts, and only when the typed text could be part of a receipt code at all (letters E, V, T, A–F, digits and `-`). A typical surname (it contains letters outside that set) never touches the receipts. When more than 200 voters match through their receipt, the test runs per member instead of from an id list (correct, just slower).

## 3. Indexes (migration `20261104150000_AddVoterLookupIndexes`, script `deploy/sql/006_voter_lookup_indexes.sql`)

| Index | Columns | Used for |
|---|---|---|
| `IX_Members_VoterLookup_Cover` | `(IsActive, IsEligibleToVote, MemberName, AccountCode, BranchId)` | members in name order and the count/search, without reading member rows |
| `IX_VoterLogs_Event_Voted_Member` | `(EventId, HasVoted, MemberId)` | "who voted in this election" as an index-only range |

They are a performance aid only: no column, table or row changes. An index changes how fast a statement runs, not what it returns (the statements order by member name, then member id, so no row order depends on an index).
`006_voter_lookup_indexes_verify.sql` and `…_rollback.sql` exist; the rollback drops both indexes and removes the migration from the history, touching no data (`03-migration-review.md`, `04-rollback.md`).

Which index MySQL uses is its own decision, made from the statistics of your data. In the plans recorded for this document, the production-size run used mostly indexes that already existed (`IX_Members_IsActive_IsEligible`, `IX_Members_MemberName`,
`IX_Members_Branch_Active_Eligible_Name`) together with the new `IX_VoterLogs_Event_Voted_Member` for the voted filters. The new `IX_Members_VoterLookup_Cover` was chosen at the million-member size, not at 67 515 members.
Whether the member index is worth keeping on your server at today's size is therefore a question for your database administrator; it costs a little write time and disk space and is what keeps the list usable if the membership grows a great deal.

## 4. Measurements

### 4.1 Original build against new build, same data (measured 2026-11-09)

The dump's election 31 (all branches, 67 515 active members; the dump holds only 3 voter-log rows for it), median of 7 calls, wall-clock time as the client sees it. The rows and totals are identical in both builds.

| Request | Original build | New build |
|---|---|---|
| page 1 (50 rows) | 414 ms | 64 ms |
| page 675 (50 rows, the middle of the list) | 397 ms | 162 ms |
| page 1 (500 rows) | 417 ms | 70 ms |
| search a common surname, page 1 (1 040 matches) | 133 ms | 96 ms |
| voted only, page 1 (3 voters) | 364 ms | 9 ms |

The original build loads every member of the election into memory for each request (its search is the one step done in the database, which is why the surname request, with 1 040 matches to load, is the fast one). Its cost therefore grows with the membership **and** with the memory of the request; with a larger membership or a busy election (one voter-log row per voter) it would be slower than shown. That was not measured.

### 4.2 The new build at the production size (measured 2026-11-09)

The all-branch election lists 67 515 voters, 22 505 of them with a voted voter-log row (seeded on the scratch database for this test; the dump itself holds 3). The two-branch election lists 1 700 voters.
Median of 5 calls with the minimum and maximum beside it; scratch copy of the dump, API and database on the same 2-core machine.

| Scenario | listed / matching | rows returned | median ms | min – max |
|---|---:|---:|---:|---:|
| page 1 (50 rows) | 67 515 | 50 | **72** | 69 – 84 |
| page 675 (50 rows; the middle of the list) | 67 515 | 50 | **174** | 160 – 214 |
| last page (page 1 351) | 67 515 | 15 | **172** | 151 – 174 |
| page 1 (100 rows) | 67 515 | 100 | **69** | 67 – 77 |
| branch 502, page 1 | 12 957 | 50 | **13** | 12 – 21 |
| search a common surname, page 1 | 1 040 | 50 | **107** | 100 – 117 |
| search a common surname, page 20 | 1 040 | 50 | **181** | 161 – 199 |
| search one account code | 2 | 2 | **243** | 202 – 259 |
| search with no match | 0 | 0 | **91** | 86 – 99 |
| search a text that every voter's receipt contains | 22 505 | 50 | **236** | 217 – 245 |
| voted only, page 1 | 22 505 | 50 | **42** | 37 – 52 |
| voted only, page 225 | 22 505 | 50 | **180** | 169 – 199 |
| not voted, page 1 | 45 010 | 50 | **107** | 103 – 147 |
| not voted, last page | 45 010 | 10 | **223** | 203 – 245 |
| branch 502 + voted only + search | 109 | 50 | **43** | 39 – 55 |
| two-branch election: page 1 (50 rows) | 1 700 | 50 | **9** | 8 – 12 |
| two-branch election: last page | 1 700 | 50 | **8** | 8 – 8 |
| two-branch election: search the same surname | 81 | 50 | **86** | 83 – 88 |

The test script asserts that every scenario answers within 500 ms at this size (check `VS-05`); it passed, the slowest median being 243 ms. The plan of every COUNT and page-id statement uses an index (`VS-04`): no statement scans the whole member table.
At this size MySQL mostly chose indexes that already existed (§3), so the figures above owe more to the three-statement shape (§2) than to the new indexes.

### 4.3 A million members (stress, measured 2026-11-09)

The scratch database was filled up to 1 067 515 members: 972 130 listed in the all-branch election (324 037 of them voted) and 34 008 in the two-branch election. Median of 5 calls.
This is a stress figure, **not** the production size; no such membership exists.

| Scenario | listed / matching | rows returned | median ms | min – max |
|---|---:|---:|---:|---:|
| page 1 (50 rows) | 972 130 | 50 | **209** | 171 – 254 |
| page 9 000 (50 rows; about the middle of the list) | 972 130 | 50 | **849** | 743 – 928 |
| last page (page 19 443) | 972 130 | 30 | **790** | 778 – 928 |
| page 1 (100 rows) | 972 130 | 100 | **179** | 167 – 217 |
| branch 502, page 1 | 29 441 | 50 | **13** | 12 – 25 |
| search a very common surname, page 1 | 18 704 | 50 | **1 011** | 912 – 1 221 |
| search a very common surname, page 20 | 18 704 | 50 | **2 253** | 2 247 – 2 929 |
| search one account code | 1 | 1 | **2 842** | 2 783 – 3 069 |
| search with no match | 0 | 0 | **430** | 378 – 469 |
| search a text that every voter's receipt contains | 324 037 | 50 | **2 992** | 2 912 – 3 109 |
| voted only, page 1 | 324 037 | 50 | **2 154** | 2 030 – 2 415 |
| voted only, page 3 240 | 324 037 | 50 | **3 641** | 3 465 – 3 881 |
| not voted, page 1 | 648 093 | 50 | **2 037** | 1 973 – 2 071 |
| not voted, last page | 648 093 | 43 | **3 618** | 3 488 – 3 790 |
| branch 502 + voted only + search | 22 | 22 | **148** | 142 – 162 |
| two-branch election: page 1 (50 rows) | 34 008 | 50 | **31** | 29 – 33 |
| two-branch election: last page | 34 008 | 8 | **42** | 36 – 61 |
| two-branch election: search a surname | 6 | 6 | **2 252** | 2 164 – 2 286 |

The test script asserts that page navigation (no search, no voted filter) stays within 1 500 ms and that every scenario, searches and voted filters included, stays within 5 000 ms (`VS-03a`, `VS-03b`); both passed.
The same scenarios were also run on 2026-11-04. The two runs agree to within 14 percent in every row above 100 ms (the largest differences: a receipt search that matches every voter, 2 646 ms then and 2 992 ms now; the two-branch surname search, 2 025 ms then and 2 252 ms now) and to within 8 ms in the rows below 50 ms.
Both runs shared the 2-core machine with the other test processes, so differences of this size say little about the code.
The final regression of 2026-11-10 measured the production-size and the million-member scenarios once more (`09-testing.md` §6.3). The numbers of listed and matching members are identical to the tables above; the medians differ by up to 26 percent in the rows above 100 ms (the largest: the account-code search at the production size, 243 ms above and 179 ms there), the same kind of run-to-run variation. The asserted limits (`VS-03a`, `VS-03b`, `VS-05`) held in every run.

What is slow at this size, and why: a name search is a substring search (`LIKE '%text%'`), which no ordinary index can walk in name order, and the account-code search works the same way. The voted / not-voted filters count and page across hundreds of thousands of voter-log rows,
and a receipt search that matches every voter has to test every member. These take 2 to 4 seconds at a million members and 0.04 to 0.25 seconds at the production size. If the membership ever came near the stress size, the next steps would be a full-text (n-gram) index for the name search
and an exact or prefix match for account codes. Neither was built, because nothing at today's size needs it, and changing what a search matches would change what the screen returns.

## 5. What was not measured

* A real browser or the WPF screen; the times are API round trips.
* Concurrent users; each scenario ran alone.
* Production hardware, a remote database, or a production data distribution (names, branch sizes).
* Memory use of the original build at a million members (it loads the whole list).
