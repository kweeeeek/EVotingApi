# 08 — Kiosk and offline compatibility

Part 6 of the engineering task. This document records (1) what the Kiosk application does today, (2) what the API now does
for kiosks, (3) exactly what the Kiosk client has to change to run a Representative election, and (4) what was and was not
verified.

## 1. Status — read this first

| Item | State |
|---|---|
| **API side** of the kiosk/offline flow for Representative elections | **Implemented and tested** over HTTP against a real MySQL (checks RV-20…RV-72, RO-01…RO-15, RC-01…RC-04, listed below). |
| **General Assembly kiosk contract** (`/events/current`, `/candidates`, `/position-rules`, `/votes`, `/votes/offline-sync`, `/votes/offline-sync-bulk`, `/events/{id}/member-snapshot`) | **Unchanged**, plus additive fields only. Verified by the original-vs-new differential run (`10-ga-regression-report.md`): 0 differences. |
| **The Kiosk application itself** (WPF tablet app, separate repository `EVoting.Kiosk`) | **Not modified.** Its source was read (read-only) to derive the contract; it was not built or run here (no Windows/WPF runtime in the build environment). |
| Running a Representative election on an **unmodified** Kiosk | **Not possible, by design** — see §3. A GA election keeps working on the unmodified Kiosk. |
| Kiosk hardware (tablet, printer, QR scanner), real power/network loss | **Not tested.** The offline behaviour was exercised through the API contract only. |

The requirement "Kiosk/offline compatibility is implemented and verified" in the Definition of Done is therefore met for
**the API contract and GA backward compatibility**, and is **open for the Kiosk client** (§4 is the work list).

## 2. What the Kiosk does today (General Assembly contract, from the Kiosk source)

| Step | Kiosk call | Notes |
|---|---|---|
| Kiosk sign-in | `POST /api/v1/auth/kiosk-login` | Kiosk JWT (`Kiosk` role). Re-done when the token expires or a 401 comes back. |
| Find the open election | `GET /api/v1/events/current` | 404 = nothing open. |
| Offline member cache | `GET /api/v1/events/{id}/member-snapshot` | Every active member with `IsEligible` precomputed (`IsEligibleToVote` **and** the member's branch participates). Cached in the Kiosk's encrypted SQLite (`CachedMember`). |
| Voter verification | `POST /api/v1/auth/voter-verify` | Online: returns a 30-minute Voter JWT bound to member + event. Offline: answered from `CachedMember`. |
| Ballot rules | `GET /api/v1/events/{id}/position-rules` | Cached locally. |
| Candidates | `GET /api/v1/events/{id}/candidates` | One response with every candidacy and Base64 photos; cached to `{cache}/{eventId}/manifest.json + {candidacyId}.img`. |
| Cast (online) | `POST /api/v1/votes` | Voter JWT; selections are `{candidacyId, position}`. |
| Cast (offline) | local `PendingVotes` row | `SelectionsJson = {"position":[candidacyId,…]}`, receipt `EVT{event}-{8hex}-{8hex}`, `CastAtUtc`; unique `(MemberId, EventId)`. |
| Sync (every 20 s) | `POST /api/v1/votes/offline-sync` (single) or `…/offline-sync-bulk` (`{votes:[…]}`) | Kiosk JWT. Bulk always answers 200 with one result per ballot; `isPermanentRejection` tells the Kiosk which ballots to stop retrying. A conflicting ballot has its `SelectionsJson` wiped (ballot secrecy). |

None of this was changed for General Assembly elections.

## 3. What an unmodified Kiosk experiences in a Representative election

The API refuses to let a General Assembly code path write into a Representative election. Each refusal carries
`code = WRONG_EVENT_TYPE` so it is diagnosable, and nothing is ever recorded:

| Unmodified-Kiosk call | Representative election result |
|---|---|
| `GET /events/current` | 200. Now carries an **additive** `eventType` field (`REPRESENTATIVE`); an old client ignores it. |
| `GET /events/{id}/position-rules` | 200 with the event's own rules (now also `maxCandidates`, ignored by old clients). A Representative event with **no** rules returns an **empty** list — the General Assembly default positions are *not* invented for it. |
| `GET /events/{id}/candidates` | **409 `WRONG_EVENT_TYPE`**, `detail` points to `/representative-candidates`. The old Kiosk cannot show a ballot. |
| `POST /votes` | **409 `WRONG_EVENT_TYPE`** (checks RV-53). |
| `POST /votes/offline-sync` | **422 `WRONG_EVENT_TYPE`** (RO-13). |
| `POST /votes/offline-sync-bulk` | 200; that item is a *permanent* rejection with the message (the exception is an `InvalidOperationException`, handled by the existing branch), so an old Kiosk stops retrying it. |
| `GET /events/{id}/member-snapshot` | Unchanged and valid: the snapshot depends on the member's eligibility flag and the event's branches, not on the election type. |

**Operational rule: do not use an un-updated Kiosk for a Representative election.** For a General Assembly election
no change is needed.

## 4. What the Kiosk client must adopt (work list for the Kiosk repository)

The server contract it must implement is below. Nothing here needs a new server feature.

### 4.1 Choose the ballot by event type
`GET /events/current` → `eventType`. `GENERAL_ASSEMBLY` (or absent) → the existing flow. `REPRESENTATIVE` → this section.

### 4.2 Ballot rules and the roster version
`GET /api/v1/events/{id}/representative-ballot` (Kiosk · Voter · Admin; `Cache-Control: no-store`)

```
{ eventId, eventType, title, scheduleFrom, scheduleTo, status, isVotingOpen, serverTimeUtc,
  rosterVersion,            // 64-hex fingerprint of the ballot (see below)
  totalCandidates,
  positions: [ { position, minVotes, maxVotes, totalSeats, candidateCount } ] }   // the administrator's order
```

* `isVotingOpen` is true only when the event is `Opened` **and** inside its window. Use `serverTimeUtc` to detect a wrong tablet clock.
* `rosterVersion` = SHA-256 (hex) over a text form of the position rules (position, min, max, seats, max candidates) **and** every
  roster candidate (roster id, profile id, code, name, position, ballot number, status, photo stamp), computed over sorted
  inputs so it does not depend on row order. Changing any of those changes it (`RosterVersion.Compute`).

### 4.3 Roster download for the local cache (thousands of candidates)
`GET /api/v1/events/{id}/representative-roster?afterId=0&pageSize=200` (**Kiosk only**; voter/admin get 403 — RV-20d)

```
{ eventId, eventType, rosterVersion, total, nextAfterId /* null on the last page */,
  items: [ { id, candidateCode, candidateName, position, ballotNumber, reason, gender, highestEducation,
             previousPositions, specialSkills, hasPhoto, photoVersion } ],
  positions: [ … ]            // first page only
}
```

* Keyset paging: pass `nextAfterId` back as `afterId` until it is `null`. Default 200 rows, at most 500 per page (RV-20c).
* Every page repeats `rosterVersion`. **If it changes between pages, discard the partial download and start again** (RV-20).
* `items[].id` (`RepresentativeEventCandidateId`) is the only identifier a ballot submits. Contact data (phones, date of birth,
  civil status) and image bytes are **never** in a card (RV-10b).
* The same list is served to every branch (RV-14): candidate visibility is shared, only *who may vote* depends on the branch.
* A voter-facing screen may use the paged, searchable `GET /events/{id}/representative-candidates?search=&position=&page=&pageSize=`
  (default 50, max 200).

### 4.4 Photos — lazy thumbnails with a cache key
`GET /api/v1/events/{id}/representative-candidates/{candidateId}/photo` — default `size=thumb` (≤ 96 KiB), `size=full` for the original.
`204` = no photo. The response has an `ETag` derived from the photo's version; send `If-None-Match` and a `304` costs no body (RV-30c).
Cache on disk by `(photo id, photoVersion)`; fetch thumbnails only for the cards actually shown. A candidate id from another
election is 404 for this route (RV-30d). **Do not** download every photo at start-up.

### 4.5 Casting while offline — the queue entry
Keep the existing `PendingVotes` table and its `(MemberId, EventId)` unique index. Store the selections as
`{"<position>": [rosterId,…]}` exactly as today (the positions are only for display), plus the roster version the voter saw.
Generate the receipt locally in the existing format (`EVT{eventId:D2}-{8 hex}-{8 hex}`, ≤ 30 chars).

### 4.6 Synchronising
`POST /api/v1/representative-votes/offline-sync-bulk` (Kiosk token, up to **500** ballots, rate-limited policy `vote`)

```
{ "votes": [ { "memberId": 123, "eventId": 7,
               "candidateIds": [101, 102, 205],      // roster ids; positions are derived by the server
               "receiptCode": "EVT07-1A2B3C4D-5E6F7A8B",
               "castAtUtc": "2026-11-05T01:30:00Z",
               "rosterVersion": "<64 hex, optional>" } ] }
```
Answer (always 200 unless the request itself is malformed):
```
{ "results": [ { "receiptCode", "success", "alreadyProcessed", "isPermanentRejection", "code", "message", "rosterChanged" } ],
  "accepted": n, "replayed": n, "rejected": n, "retryLater": n }
```

How the Kiosk must treat each result:

| Result | Meaning | Kiosk action |
|---|---|---|
| `success`, `alreadyProcessed=false` | Recorded. | Mark Synced. |
| `success`, `alreadyProcessed=true` | The same receipt was already on file (lost response). **Not an error** (RO-02, RO-11). | Mark Synced. |
| `success`, `rosterChanged=true` | The roster changed after the ballot was cast, but the ballot is still valid and was accepted (RO-09). | Mark Synced; optionally log. |
| `!success`, `isPermanentRejection=true` | A business rejection that will fail identically forever (`ALREADY_VOTED`, `NOT_ELIGIBLE`, `BRANCH_NOT_PARTICIPATING`, `ABOVE_MAXIMUM`, `BELOW_MINIMUM`, `CANDIDATE_WRONG_EVENT`, `CANDIDATE_NOT_ELIGIBLE`, `OUTSIDE_VOTING_WINDOW`, `INVALID_CAST_TIME`, `INVALID_RECEIPT_CODE`, `RECEIPT_IN_USE`, …). | Stop retrying; mark Conflict; wipe the selections (as today); show to staff. |
| `!success`, `isPermanentRejection=false` (`code = TEMPORARY_FAILURE`) | The database was too busy; **nothing was recorded**. | Leave Pending; retry on the next cycle (the receipt makes the retry idempotent). |

The single-ballot route `POST /api/v1/representative-votes/offline-sync` returns the same information as an HTTP status:
200 (recorded or replay), 4xx (permanent), **503 `TEMPORARY_FAILURE`** (retry).

Rules the server applies to an offline ballot (all verified, see §5): the ballot is judged against the **current** roster and
rules, not the kiosk's cached copy; `castAtUtc` must not be in the future (400 `INVALID_CAST_TIME`) and must fall inside the
voting window (409 `OUTSIDE_VOTING_WINDOW`) — **but a ballot cast inside the window is still accepted when the Kiosk syncs
after the election closed** (RC-03), and an election that was never opened accepts nothing (RC-04). A candidate withdrawn
after the ballot was cast makes the ballot **rejected** (422 `CANDIDATE_NOT_ELIGIBLE`, audited — RO-08), it is never
silently accepted.

### 4.7 Voter verification, online cast
Unchanged: `POST /auth/voter-verify` works for a Representative election and reports `eventType` (RV-01); a member of a
non-participating branch gets 403 (RV-02). The online ballot is `POST /api/v1/representative-votes` with
`{ eventId, candidateIds:[…], receiptCode?, rosterVersion? }` and the Voter JWT. The answer never echoes the selections
(RV-40b). A Voter token only works for its own election (403 `TOKEN_MISMATCH`, RV-06/RV-51).

### 4.8 Eligibility offline
Eligibility is a stored flag on the member (`Members.IsEligibleToVote`) maintained centrally by the eligibility service. The
existing `member-snapshot` therefore reflects the configured thresholds; no Kiosk change is needed for it. Because the
thresholds cannot change while an election is open (`06-eligibility-policy.md`), a snapshot taken after opening stays valid
for the whole election.

## 5. What was verified (API contract, executed)

Executed over real HTTP against the scratch MySQL copy; every id below is a recorded check in the test log
(`09-testing.md` has the run totals).

* **Roster sync** — RV-20 (5-per-page keyset sync delivers all 14 candidacies exactly once, same version on every page),
  RV-20b (rules on first page only), RV-20c (500 cap), RV-20d (kiosk-only).
* **Shared visibility** — RV-14 (branch 1 voters, branch 2 voters, a kiosk and an administrator see the identical list).
* **Photos** — RV-30…RV-30e (thumb/full, 204, ETag/304, cross-election 404/403).
* **Offline ballots** — RO-01 (accepted, original receipt kept), RO-01b (stored with the cast time, not the sync time), RO-02
  (replay), RO-03/03b (different receipt for a member who voted → 409, audited without selections), RO-04/04b (future/pre-window),
  RO-05/05b (receipt validation), RO-06…06c (branch, eligibility, unknown member), RO-07/07b (wrong-event candidate, rule break),
  RO-08/08b (roster changed → rejected + audited), RO-09 (stale version but valid → accepted + flagged), RO-10…10c (bulk of 6:
  2 accepted, 1 replay, 3 rejected, database holds only the 2 valid), RO-11 (duplicate batch → 0 new), RO-12 (>500 → 400), RO-13
  (GA endpoint refuses), RO-14 (kiosk token required), RO-15 (audit summary row per sync).
* **Close/reopen** — RC-01…RC-04.
* **Concurrency** — RV-70/70b (40 simultaneous voters), RV-71/71b (one voter, 15 identical parallel ballots → exactly one
  recorded), RV-72/72b (same with one receipt → one original + 14 replays); `t07b` (race stress).
* **GA kiosk contract unchanged** — original build vs new build: 0 differences (`10-ga-regression-report.md`).

## 6. Not verified / known limits

1. The Kiosk application was not changed, built or run. §4 is a contract, not a delivered feature.
2. No physical-device test: printing receipts, QR scanning, tablet clock drift, encrypted SQLite, real network drop-outs.
3. The kiosk-simulator behaviour (cache → disconnect → queue → reconnect → bulk sync) was reproduced at API level by
   the RO-* checks; a client that implements §4 should repeat it end-to-end on a tablet.
4. `GET /events/{id}/member-snapshot` was **not** touched. It loads every active member **including the photo column** into
   memory per request (pre-existing; it was designed for ~1,700 members). For very large memberships schedule its first call
   outside peak voting or convert it to the paged/lazy-photo style used here.
5. Rate limiting: the `vote` policy still applies to the Representative POSTs. Raise it for event day (see `11-deployment.md`).
