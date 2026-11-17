# 05 — API contracts

Everything below is read from the code of this branch (`Controllers/`, `Models/`, `Services/`); nothing is planned. Sections 1–2 are the conventions, sections 3–11 the endpoints, section 12 the error codes.
Executed evidence for the behaviour is listed in `09-testing.md`. Base path of every route: `/api/v1`.

## 1. Conventions

| Topic | Rule |
|---|---|
| JSON | camelCase names; properties that are `null` are **left out** of the body (`DefaultIgnoreCondition = WhenWritingNull`). A field described as "optional" below is simply absent when it has no value. |
| Error body | `{ "error": "<message>", "detail": "<hint>", "code": "<STABLE_CODE>", "fields": { "<camelCaseField>": "<message>" } }`. Only `error` is always present. Clients decide on `code`, never on the text. |
| Admin routes | `Authorization: Bearer <admin token>`; each action needs the permission shown in the tables (any one of the listed permissions unless stated otherwise). A missing/expired token is 401, a missing permission 403. |
| Kiosk routes | token with role `Kiosk` (the registration/kiosk station login that already exists). |
| Voter routes | token with role `Voter`, issued by `POST /auth/voter-verify`. The token is bound to **one** election (`eventId` claim): using it for any other election is `403 TOKEN_MISMATCH`. |
| Optimistic concurrency | Writes that edit a record carry `expectedUpdatedAt` (or `expectedVersion` for settings). A stale copy is `409 CONCURRENCY`; reload and re-apply. |
| Paging | `page` is 1-based; a page past the end is a normal empty page; `pageSize` above the endpoint's maximum is cut to the maximum (voter lookup states the size it used in the answer). |
| Event type | `GENERAL_ASSEMBLY` (default; also for clients that predate the field) or `REPRESENTATIVE`. Fixed when the event is created. |
| Rate limit | The three voting `POST`s use the existing `vote` rate-limit policy. |

HTTP status per voting/ballot error code (`RepresentativeHttp.StatusFor`): **404** unknown record · **403** not allowed for this voter/session · **409** state conflict (closed, already voted, wrong type) ·
**422** the ballot or the voter fails a rule · **400** malformed request · **503** temporary (nothing was recorded; the same request may be sent again).

## 2. Existing endpoints that changed (additive only)

Old clients that ignore unknown fields are unaffected (`10-ga-regression-report.md` §4: 86 of 100 recorded calls identical, 14 identical plus new fields, 0 different).

| Endpoint | Change |
|---|---|
| `GET /admin/events` | `items[].eventType`; optional query `eventType` filter (an unknown value matches nothing). |
| `POST /admin/events` | optional body field `eventType` (default `GENERAL_ASSEMBLY`; anything else is `400 VALIDATION`). The four General Assembly default rules are created for a General Assembly event only. |
| `GET/POST/PUT /admin/events/{id}` | answers carry `eventType`, `candidateCount` (Representative: roster size; 0 for GA), `rosterLockedAt`, `eligibilityMinShareCapital`, `eligibilityMinSavingsDeposit` (the thresholds recorded at opening). `voteCount` of a Representative event counts selections. An update that names a different type is `409 EVENT_TYPE_IMMUTABLE`. |
| `POST /admin/events/{id}/open` | refused with `409 EVENT_NOT_READY` (reasons in `detail`) when the readiness checks fail, and audited (`OPEN_EVENT_BLOCKED`). Opening freezes a Representative roster and records the eligibility thresholds. See §3. |
| `DELETE /admin/events/{id}` | a Representative event counts and purges its own ballots (`RepresentativeVotes`). |
| `GET /events/current`, `POST /auth/voter-verify` | `eventType` (also `event.eventType`) so a kiosk knows which ballot to show. |
| `GET /events/{id}/position-rules` | rows are ordered by creation (`Id`), and carry `maxCandidates` when a limit is set. A Representative event without rules gets an empty list, not the General Assembly defaults. |
| `GET /admin/voter-lookup` | server-side paging, see §10. |
| `GET /admin/audit/{eventId}` | rows with the same one-second `votedAt` are listed by `voterLogId` (repeatable order; `10-ga-regression-report.md` §5.2). |

General Assembly endpoints that refuse a Representative election (`409 WRONG_EVENT_TYPE` unless stated): `GET /events/{id}/candidates`, `GET /events/{id}/results`, `POST /votes`; `POST /votes/offline-sync` answers **422** `WRONG_EVENT_TYPE`,
and in `POST /votes/offline-sync-bulk` the ballot gets `isPermanentRejection = true`. Nothing is written in any of these cases.

## 3. Open / close and the readiness checks

`POST /admin/events/{id}/open` (permission `EVENT_EDIT`; body `{ "closeOthers": false }`) and the 10-second scheduler apply the **same** checks (`Services/EventOpenGate.cs`).
When a check fails the event is not opened, the answer is `409 EVENT_NOT_READY` with the numbered reasons in `detail`, and `OPEN_EVENT_BLOCKED` is audited (the scheduler audits it once per event, not on every tick).

| Event type | Opening is refused when |
|---|---|
| Both | the member eligibility flags are not in step with the saved thresholds (an interrupted recalculation) — `06-eligibility-policy.md` |
| General Assembly | a position's `maxCandidates` is set and its approved, active candidacies (filed or approved within 180 days before the event's start) exceed it. Nothing else is checked, so the legacy flow is unchanged when no limit is configured. |
| Representative | no participating branch · no position rules · empty roster (approved candidates of active profiles) · a position has fewer candidates than its `minVotes` · more than its `maxCandidates` · candidates assigned to a position that has no rule |

Remarks that do **not** block (returned as `warning` of the open answer): the scheduled window has already ended (the scheduler closes it again within seconds), a Representative window that has not started (ballots are refused until it does), a position without candidates, fewer candidates than seats.

A successful open records the thresholds in force (`eligibilityMinShareCapital`/`…MinSavingsDeposit`) and, for a Representative event, freezes the roster (`rosterLockedAt`; audited `ROSTER_LOCKED`).

## 4. Position rules (both event types)

| Method & path | Permission | Request → answer |
|---|---|---|
| `GET /admin/events/{id}/position-rules` | `EVENT_VIEW` or `EVENT_EDIT` | → `PositionRulesResponse` |
| `PUT /admin/events/{id}/position-rules` | `EVENT_EDIT` | `{ rules: [PositionRuleRow], expectedUpdatedAt }` → `PositionRulesResponse` |

`PositionRuleRow`: `id?`, `position`, `minVotes`, `maxVotes`, `maxCandidates?` (null = no limit), `totalSeats`, `assignedCandidates` (answer only).
`PositionRulesResponse`: `eventId`, `eventType`, `editable`, `lockCode?`, `lockReason?`, `isDefault` (a General Assembly event with no saved rows shows the four legacy defaults), `rules`, `warnings[]`, `updatedAt`, `suggestedPositions[]`, `customPositionsAllowed` (true for Representative).

Save rules (`PositionRuleValidator`): at least one position and at most 50; the position name is required and unique per event (case-insensitive); a General Assembly event accepts only the four General Assembly positions;
`minVotes ≥ 0`; `maxVotes` 1–500 and not below `minVotes`; `totalSeats` 1–5 000; `maxCandidates` (optional) 1–100 000 and not below `minVotes`. For a Representative event `maxCandidates` may not be below the candidates already assigned (for General Assembly this is a warning, and the election cannot be opened until it is corrected);
a Representative position that has candidates cannot be removed or renamed. Softer findings (for example `maxCandidates` below `totalSeats` or below `maxVotes`) come back in `warnings`. The save is one transaction that locks the event row, so it cannot interleave with a roster edit, an import commit or Open/Close.
Errors: `404 NOT_FOUND`, `409 CONCURRENCY`, `409 EVENT_OPEN_LOCK` (event open) / `409 HAS_BALLOTS` (ballots exist) / `409 ROSTER_LOCKED`, `400 VALIDATION` (`fields` name the row, e.g. `rules[2].maxVotes`).

## 5. Representative candidate profiles (reusable across elections)

| Method & path | Permission | Request → answer |
|---|---|---|
| `GET /admin/representative-profiles?search&active&page&pageSize` | `CANDIDATE_VIEW` | paged `RepProfileRow` list (default 25) |
| `GET /admin/representative-profiles/{id}` | `CANDIDATE_VIEW` or `CANDIDATE_EDIT` | `RepProfileDetail` |
| `POST /admin/representative-profiles` | `CANDIDATE_ADD` | `RepProfileUpsertRequest` → 201 `RepProfileDetail` |
| `PUT /admin/representative-profiles/{id}` | `CANDIDATE_EDIT` | `RepProfileUpsertRequest` (`expectedUpdatedAt`) |
| `PUT /admin/representative-profiles/{id}/active` | `CANDIDATE_EDIT` | `{ isActive, expectedUpdatedAt }` |
| `PUT /admin/representative-profiles/{id}/photo` | `CANDIDATE_EDIT` | `{ imageBase64?, thumbnailBase64? }` (both null removes the photo) |
| `GET /admin/representative-profiles/{id}/photo?size=thumb\|full` | `CANDIDATE_VIEW` or `CANDIDATE_EDIT` | image bytes with `ETag`; `204` when none; `304` on `If-None-Match` |

`RepProfileUpsertRequest`: `candidateCode` (letters, digits, `. _ / -`, ≤ 30; immutable once the profile is on a roster), `candidateName` (≤ 200), `accountCode?` (links an existing member; one profile per member), `cellNo?`, `telNo?`, `civilStatus?`, `gender?`, `dateOfBirth?`, `highestEducation?`, `previousPositions?`, `specialSkills?`, `expectedUpdatedAt?`.
Photos are checked by content (PNG, JPEG, GIF or BMP; the image at most 4 MB, the thumbnail at most 96 KB); a bad image is `400 PHOTO_INVALID` (an undecodable or oversized `imageBase64` is `400 VALIDATION` with `fields.imageBase64`). Errors: `409 CANDIDATE_CODE_EXISTS`, `MEMBER_ALREADY_LINKED`, `CODE_IMMUTABLE`, `PROFILE_IN_OPEN_EVENT`, `409 CONCURRENCY`, `400 VALIDATION`.

## 6. Representative roster (one election)

| Method & path | Permission | Notes |
|---|---|---|
| `GET /admin/events/{eventId}/representative-roster/summary` | `CANDIDATE_VIEW` or `EVENT_VIEW` | `RepRosterSummaryResponse`: per-position counts and free slots, `canEdit`, lock state, `rosterVersion` (fingerprint), `readinessIssues[]`, `readinessWarnings[]` |
| `GET /admin/events/{eventId}/representative-roster?search&position&status&page&pageSize` | same | paged `RepRosterRow` (default 50) |
| `GET /admin/events/{eventId}/representative-roster/export` | `REPORT_EXPORT` or `CANDIDATE_VIEW` | every row with the full profile, no images |
| `POST /admin/events/{eventId}/representative-roster` | `CANDIDATE_ADD` | exactly one of `profileId`, `candidateCode` or `newProfile` (a `RepProfileUpsertRequest`), plus `position` (must be a position of the election), `reason?`, `ballotNumber?` (1–1 000 000, unique within the position) → 201 `RepRosterRow` |
| `DELETE /admin/events/{eventId}/representative-roster/{assignmentId}` | `CANDIDATE_DELETE` | removes the **assignment**; the profile stays |
| `POST /admin/events/{eventId}/representative-roster/unlock` | `EVENT_EDIT` | `{ confirm: true, reason? }`: re-opens editing of an event that was opened by mistake — only when it is **closed and has no ballot**; audited `ROSTER_UNLOCKED` |

Errors: `409 WRONG_EVENT_TYPE` (a General Assembly event), `409 EVENT_OPEN_LOCK` / `ROSTER_LOCKED` / `HAS_BALLOTS`, `409 POSITION_FULL` (the position's `maxCandidates` is reached), `409 ALREADY_ASSIGNED`, `409 BALLOT_NUMBER_TAKEN`, `409 PROFILE_INACTIVE`,
`400 POSITION_NOT_CONFIGURED`, `400 NOT_CONFIRMED`, `409 ROSTER_NOT_LOCKED`, `404 NOT_FOUND`, `400 VALIDATION`.

## 7. Representative import (staged: upload → validate → resolve → commit)

Nothing reaches the roster before `commit`. All writes need `CANDIDATE_ADD`; reading needs `CANDIDATE_VIEW` (or `CANDIDATE_ADD`).

| Method & path | Purpose |
|---|---|
| `GET /admin/representative-imports/template` | The file format and the configured limits: `columns[]` (`header`, `field`, `required`, `maxLength`, `description`, `example`), `notes[]`, `maxRowsPerBatch` (default 20 000), `maxRowsPerRequest` (2 000), `maxOpenBatchesPerEvent` (10). Also readable with `REPORT_EXPORT`. |
| `POST /admin/representative-imports` | `{ eventId, fileName, fileSha256?, expectedRows }` → `RepImportBatchRow` (201) |
| `POST /admin/representative-imports/{batchId}/rows` | `{ rows: [RepImportRowIn] }` → `{ accepted, totalStaged }`; a large file is sent in several requests |
| `POST /…/{batchId}/validate` | server-side validation of every staged row (set-based); → `RepImportBatchRow` with `validRows`, `invalidRows`, `conflictRows`, `newProfiles`, `reusedProfiles`, `newAssignments` |
| `POST /…/{batchId}/resolve` | `{ items: [{ rowNumber, resolution }], expectedUpdatedAt }`; resolution per conflicting row: use the stored profile, update it, or skip |
| `POST /…/{batchId}/commit` | `{ confirm: true, requireAllValid, expectedUpdatedAt }`; one transaction that locks the event; fails as a whole or not at all |
| `POST /…/{batchId}/cancel` | discards the batch |
| `GET /…/{batchId}` · `GET /…/{batchId}/rows?status&search&page&pageSize` | batch status · rows with outcome (`VALID`, `INVALID`, `CONFLICT`, `SKIPPED`, `COMMITTED`) |
| `GET /…/{batchId}/error-report?all=false` | CSV of the rows that need attention (`all=true`: every row) |
| `GET /admin/events/{eventId}/representative-imports?take=50` | import history of one election, newest first |

Errors: `409 WRONG_EVENT_TYPE`, `409 BATCH_STATE` (the action is not allowed in the batch's current state), `400 TOO_MANY_ROWS`, `409 TOO_MANY_OPEN_BATCHES`, `409 VALIDATION_STALE` (the roster, rules or profiles changed after validation — validate again),
`409 CONFLICTS_UNRESOLVED`, `400 NOT_CONFIRMED`, `409 EVENT_OPEN_LOCK` / `ROSTER_LOCKED` / `HAS_BALLOTS`, `409 CONCURRENCY`, `500 IMPORT_FAILED` (nothing was committed; the batch keeps its state and message). Column mapping: `02-import-and-column-mapping.md`.

## 8. Ballot, candidates and the kiosk roster (voter, kiosk, admin)

| Method & path | Role | Answer |
|---|---|---|
| `GET /events/{eventId}/representative-ballot` | Kiosk, Voter, Admin | `RepBallotResponse`: title, schedule, `status`, `isVotingOpen`, `serverTimeUtc`, `rosterVersion`, `totalCandidates`, `positions[]` (`position`, `minVotes`, `maxVotes`, `totalSeats`, `candidateCount`). `Cache-Control: no-store`. |
| `GET /events/{eventId}/representative-candidates?search&position&page&pageSize` | Kiosk, Voter, Admin | `RepCandidatePage` (default 50, max 200). **The same list for every branch** (a voter's branch does not filter candidates). Never embeds photos. |
| `GET /events/{eventId}/representative-candidates/{candidateId}/photo?size=thumb\|full` | Kiosk, Voter, Admin | image with `ETag` and `Cache-Control: private, max-age=300`; `204` when none; `304` on `If-None-Match` |
| `GET /events/{eventId}/representative-roster?afterId=0&pageSize=200` | Kiosk | `RepRosterSyncPage`: keyset pages (max 500) for the kiosk's local cache; the first page also carries the positions; every page repeats `rosterVersion` so a roster that changes during a download is noticed |

## 9. Casting ballots

| Method & path | Role | Request |
|---|---|---|
| `POST /representative-votes` | Voter | `{ eventId, candidateIds[], receiptCode?, rosterVersion? }` |
| `POST /representative-votes/offline-sync` | Kiosk | `{ memberId, eventId, candidateIds[], receiptCode, castAtUtc, rosterVersion? }` |
| `POST /representative-votes/offline-sync-bulk` | Kiosk | `{ votes: [offline-sync item] }`, at most 500 |

Selections are **roster ids only**. The server derives each candidate's position and applies the election's rules from the database; the client never decides anything. A ballot is accepted whole or not at all: it is validated (event type and status, voting window,
voter eligibility and branch, no duplicate selection, each candidate on this election's roster and eligible, `minVotes`/`maxVotes` per position, at least one selection overall), then one `RepresentativeVotes` row per selected candidate and the voter-log row (with the receipt) are written in one transaction.

* Receipt: a client-proposed `receiptCode` must look like `EVT{event}-{8 hex}-{8 hex}` and belong to this election (`400 INVALID_RECEIPT_CODE`); without one the server generates it. Sending the same ballot again returns the same receipt with `alreadyProcessed = true` and writes nothing; a receipt already used by a different ballot is `409 RECEIPT_IN_USE`.
* Answer: `RepVoteReceiptResponse` (`receiptCode`, `votedAt`, `selectionCount`, `alreadyProcessed`, `rosterChanged`).
* Offline ballots are judged against the election window by `castAtUtc` (`400 INVALID_CAST_TIME` for an impossible time; `409 OUTSIDE_VOTING_WINDOW`). `rosterChanged = true` tells the kiosk its cached roster was out of date.
* Bulk: always `200` with one result per ballot (`RepOfflineVoteItemResult`: `receiptCode`, `success`, `alreadyProcessed`, `isPermanentRejection`, `code?`, `message`, `rosterChanged`) and the counters `accepted`, `replayed`, `rejected`, `retryLater`.
  A rejection never aborts the others. `isPermanentRejection = true` means the same ballot will fail on every retry (the kiosk stops retrying it and shows it to an administrator); `TEMPORARY_FAILURE` is the only code that is retried.
* Errors: `404 EVENT_NOT_FOUND` / `MEMBER_NOT_FOUND` · `403 TOKEN_MISMATCH` / `BRANCH_NOT_PARTICIPATING` · `409 WRONG_EVENT_TYPE` / `EVENT_NOT_OPEN` / `OUTSIDE_VOTING_WINDOW` / `ALREADY_VOTED` / `RECEIPT_IN_USE` ·
  `422 NOT_ELIGIBLE` / `NO_SELECTION` / `TOO_MANY_SELECTIONS` / `DUPLICATE_SELECTION` / `NO_POSITION_RULES` / `UNKNOWN_CANDIDATE` / `CANDIDATE_WRONG_EVENT` / `CANDIDATE_NOT_ELIGIBLE` / `POSITION_NOT_CONFIGURED` / `BELOW_MINIMUM` / `ABOVE_MAXIMUM` · `503 TEMPORARY_FAILURE`.

## 10. Voter lookup (paged on the server)

`GET /admin/voter-lookup?eventId&search&branchId&hasVoted&page=1&pageSize=50` — permission `EVENT_VIEW`; works for both event types.
`search` matches name, account code or **this election's** receipt code; `hasVoted` is true/false (omit for everybody). `pageSize` default 50, maximum 500 (the ceiling of the previous API, which an older Admin build still uses; a larger value is cut to 500, never rejected, and the answer says which size was used); `page` or `pageSize` below 1 is `400 VALIDATION`; the search text is cut to 100 characters.
Answer: `{ eventId, page, pageSize, totalCount, totalPages, items[], total, data[] }` where `items`/`totalCount` are the new names and `data`/`total` the old ones with the same values. Row: `memberId`, `accountCode`, `memberName`, `branch`, `branchId`, `isEligible`, `hasVoted`, `votedAt?`, `receiptCode`.
Details and measurements: `06-voter-lookup-paging.md`.

## 11. Results, eligibility settings

| Method & path | Permission | Notes |
|---|---|---|
| `GET /events/{eventId}/representative-results` | `EVENT_VIEW` or `REPORT_VIEW` | `RepResultsResponse`: per position every candidate (zero-vote ones too) with `votes`, `rank`, `isWinner`, `isTieForLastSeat`, `percentOfBallots`; `seatsFilled`, `hasUnresolvedTie`; turnout (`totalVoters`, `eligibleVoters`, `turnoutPercent`) and `isReconciled` (ballots in the voter log equal ballots recorded). Tied candidates share a rank; nobody in a tie for the last seat is declared elected. `409 WRONG_EVENT_TYPE` for a General Assembly event. |
| `GET /events/{eventId}/representative-results/export` | `REPORT_EXPORT` | the same as CSV (audited) |
| `GET /admin/settings/eligibility` | `SETTINGS_ELIGIBILITY_VIEW` or `…_EDIT` | `minShareCapital`, `minSavingsDeposit`, `source`, `version`, `updatedAt?`, `updatedBy?`, `otherRules[]`, `recomputePending`, `locked`, `lockReason?`, `activeMembers`, `eligibleMembers` |
| `POST /admin/settings/eligibility/impact` | `SETTINGS_ELIGIBILITY_VIEW` or `…_EDIT` | `{ minShareCapital, minSavingsDeposit }` → counts only (`eligibleNow`, `eligibleAfter`, `becomingEligible`, `becomingIneligible`); writes nothing |
| `PUT /admin/settings/eligibility` | `SETTINGS_ELIGIBILITY_EDIT` | `{ minShareCapital, minSavingsDeposit, expectedVersion, reason? }` → saves and recalculates; `Unchanged` when nothing differs. `409 CONCURRENCY`, `409 EVENT_OPEN_LOCK` (an election is open), `400 VALIDATION` (money rules: ≥ 0, two decimals, upper limit) |
| `POST /admin/settings/eligibility/recompute` | `SETTINGS_ELIGIBILITY_EDIT` | re-runs the recalculation for the saved thresholds (after an interrupted save); `409 EVENT_OPEN_LOCK` while an election is open |

## 12. Error codes

Codes returned by the Representative, roster, import and settings endpoints. Status as returned by the code in this branch (`RepresentativeHttp.StatusFor` for the voting codes, the service result for the others).

| Code | Status | Meaning |
|---|---|---|
| `VALIDATION` | 400 | a field is invalid; `fields` names it |
| `NOT_FOUND` | 404 | the record does not exist (or is deleted) |
| `CONCURRENCY` | 409 | the record was changed after you read it |
| `EVENT_NOT_FOUND`, `MEMBER_NOT_FOUND` | 404 | unknown election / member (voting) |
| `WRONG_EVENT_TYPE` | 409 (422 on the GA single offline endpoint) | the endpoint belongs to the other election type |
| `EVENT_TYPE_IMMUTABLE` | 409 | an update tried to change the type of an event |
| `EVENT_NOT_READY` | 409 | opening refused by the readiness checks; reasons in `detail` |
| `EVENT_OPEN_LOCK` | 409 | the election is open; the data cannot be edited |
| `HAS_BALLOTS` | 409 | ballots exist; the data cannot be edited |
| `ROSTER_LOCKED` | 409 | the roster froze when voting first opened |
| `ROSTER_NOT_LOCKED` | 409 | unlock requested for a roster that is not locked |
| `POSITION_FULL` | 409 | the position's `maxCandidates` is reached |
| `ALREADY_ASSIGNED`, `BALLOT_NUMBER_TAKEN` | 409 | duplicate candidate in the election / duplicate ballot number in the position |
| `CANDIDATE_CODE_EXISTS`, `MEMBER_ALREADY_LINKED`, `CODE_IMMUTABLE`, `PROFILE_IN_OPEN_EVENT`, `PROFILE_INACTIVE`, `PROFILE_ON_ROSTER`, `PROFILE_HAS_VOTES` | 409 | profile rules |
| `PHOTO_INVALID` | 400 | the image is not an accepted picture |
| `BATCH_STATE`, `TOO_MANY_OPEN_BATCHES`, `VALIDATION_STALE`, `CONFLICTS_UNRESOLVED` | 409 | import workflow rules |
| `TOO_MANY_ROWS`, `NOT_CONFIRMED` | 400 | import size / missing confirmation |
| `IMPORT_FAILED` | 500 | the commit failed as a whole; nothing was committed |
| `TOKEN_MISMATCH`, `BRANCH_NOT_PARTICIPATING` | 403 | voter token for another election / branch not in the election |
| `EVENT_NOT_OPEN`, `OUTSIDE_VOTING_WINDOW`, `ALREADY_VOTED`, `RECEIPT_IN_USE` | 409 | voting state conflicts |
| `INVALID_RECEIPT_CODE`, `INVALID_CAST_TIME` | 400 | malformed receipt / impossible cast time |
| `NOT_ELIGIBLE`, `NO_SELECTION`, `TOO_MANY_SELECTIONS`, `DUPLICATE_SELECTION`, `NO_POSITION_RULES`, `UNKNOWN_CANDIDATE`, `CANDIDATE_WRONG_EVENT`, `CANDIDATE_NOT_ELIGIBLE`, `POSITION_NOT_CONFIGURED`, `BELOW_MINIMUM`, `ABOVE_MAXIMUM` | 422 | the voter or the ballot fails a rule |
| `TEMPORARY_FAILURE` | 503 | the database stayed busy; nothing was recorded; retry |

`BATCH_NOT_FOUND` is defined in `RepCodes` but is not returned: an unknown batch is `404 NOT_FOUND`.
