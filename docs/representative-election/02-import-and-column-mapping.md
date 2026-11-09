# 02 — Representative candidate import and column mapping

This document describes how an Excel file of candidates becomes the candidate roster of one Representative election: the columns of the file and where each one is stored, how the file is read, how every row is judged,
what happens to a candidate who already exists, and how the commit works. Everything here is taken from the code of this branch; the last section says what was executed and what was not.

## 1. In short

* One file feeds **one Representative election** (`EventType = REPRESENTATIVE`). A General Assembly election refuses an import (`409 WRONG_EVENT_TYPE`); the General Assembly candidate template and its import are not touched.
* The Admin application opens the workbook **on the administrator's computer** and sends the *rows* to the API. The server never receives the `.xlsx` file; it receives the file name, the file's SHA-256 and the rows.
* **Nothing is imported until the administrator has seen the preview and confirmed.** The commit is one database transaction: either every accepted row is added or none is.
* A person is recognised **only by `CandidateCode`**, never by name. A file that disagrees with a stored profile never overwrites it silently: the row becomes a *conflict* that needs an explicit decision.
* Each row says "this person, at this position, in this election". The person (profile) is reusable across elections; the position, the reason and the ballot number belong to this election only.

## 2. The flow

```
Admin:  choose file ─► read and check the workbook here ─► send rows in parts
API  :  create batch ─► stage rows ─► validate ─► ┬─► commit  (after the administrator confirms)
                                                  ├─► resolve conflicts ─► validate again ─► …
                                                  └─► cancel  (at any point before commit)
```

| Step | Endpoint (`/api/v1/admin/…`) | What it does | Refused with |
|---|---|---|---|
| Template | `GET representative-imports/template` | The 14 columns, their limits and notes, and the configured row limits. The Admin builds the downloadable template **from this answer**, so the template cannot drift from what the server accepts. | — |
| Create batch | `POST representative-imports` | Opens a batch (`UPLOADED`) with the file name, SHA-256 and the number of rows the Admin is going to send. | `409 WRONG_EVENT_TYPE`; `409 NO_POSITION_RULES` (every candidate needs a configured position); the roster lock codes `EVENT_OPEN_LOCK`, `HAS_BALLOTS`, `ROSTER_LOCKED`; `400 TOO_MANY_ROWS`; `409 TOO_MANY_OPEN_BATCHES` |
| Stage rows | `POST representative-imports/{id}/rows` | Stores up to `MaxRowsPerRequest` rows per request, after the field-level checks of §5.1. Sending a row number again replaces that row (a retried request does not count twice). | `409 BATCH_STATE` (only an `UPLOADED` batch takes rows); `400 TOO_MANY_ROWS`; `400 VALIDATION` (row numbers outside 1–1 048 576 or repeated in one request); the lock codes again |
| Validate | `POST …/{id}/validate` | Judges every row (§5) against the database as it is now and stores each row's verdict. | `409 ROWS_MISSING` (the number of staged rows differs from the number announced; the batch goes back to `UPLOADED`); `409 BATCH_STATE` |
| Preview | `GET …/{id}/rows?status&search&page&pageSize` | The rows with their verdict, filterable by status; `search` matches a code prefix or part of a name. `pageSize` 1–500 (default 100). | `404` |
| Error report | `GET …/{id}/error-report?all=` | CSV of the rows that need attention (default) or of every row (§9). | `404` |
| Resolve | `POST …/{id}/resolve` | Records the decision for conflict rows (§6), then judges the whole batch again. | `409 BATCH_STATE` (only a `VALIDATED` batch); `409 CONCURRENCY` (the batch changed since the Admin read it); `400 VALIDATION` |
| Commit | `POST …/{id}/commit` | Adds the accepted rows in one transaction (§7). Needs `confirm = true`. | `400 NOT_CONFIRMED`; `409 BATCH_STATE`; `409 CONCURRENCY`; `409 VALIDATION_STALE`; `409 CONFLICTS_UNRESOLVED`; `409 NOT_ALL_VALID`; `409 NOTHING_TO_COMMIT`; the lock codes; `500 IMPORT_FAILED` |
| Cancel | `POST …/{id}/cancel` | `UPLOADED`, `VALIDATED`, `INVALID` or `FAILED` (and a `VALIDATING` that has been stuck for over 10 minutes) → `CANCELLED`. Nothing is imported. | `409 BATCH_STATE` |
| History | `GET /events/{eventId}/representative-imports?take=` | Batches of one election, newest first (`take` 1–200, default 50): who uploaded which file, when, with what outcome. | — |

Permissions (`07-permissions.md`): running an import needs `CANDIDATE_ADD`; reading batches, rows and error reports needs `CANDIDATE_VIEW` or `CANDIDATE_ADD`; the history needs `CANDIDATE_VIEW` or `EVENT_VIEW`; the template also `REPORT_EXPORT`.
Error codes and their HTTP status are listed in `05-api-contracts.md`.

### Batch status

`UPLOADED → VALIDATING → VALIDATED | INVALID → COMMITTED`, and `FAILED` or `CANCELLED` (a database CHECK constraint allows only these seven).

| Status | Meaning | Can be committed? |
|---|---|---|
| `UPLOADED` | Created; rows are still arriving. | no |
| `VALIDATING` | A validation is running (a second one is refused; one stuck for 10 minutes can be taken over). | no |
| `VALIDATED` | Judged; at least one row is ready or in conflict. | yes, when at least one row is ready and no conflict is left |
| `INVALID` | Judged; not a single row is ready or in conflict. | no |
| `COMMITTED` | The roster entries exist. A batch is committed once; a second commit is `409 BATCH_STATE`. | — |
| `FAILED` | A validation or commit failed and was rolled back; the batch says why. It can be validated again. | after a new validation |
| `CANCELLED` | Discarded by the administrator. | — |

### Row status

`PENDING → VALID | INVALID | CONFLICT → COMMITTED | SKIPPED`

| Status | Meaning |
|---|---|
| `PENDING` | Staged, not judged yet. |
| `VALID` | Will be added at commit; `PlannedAction` says how (§7). |
| `INVALID` | Cannot be imported; `ErrorCodes` and `ErrorMessage` say why (§5). Stays out of the roster. |
| `CONFLICT` | The candidate exists with different details; needs a decision (§6). |
| `SKIPPED` | The administrator chose to leave this row out. |
| `COMMITTED` | Imported. Rows are kept after the commit, so the batch can be reviewed later. |

## 3. The file

### 3.1 Columns and where each value goes

The workbook has one sheet of candidates, one row per candidate **per position** (the same person may appear on several rows with different positions). The template's header row has these 14 columns. The columns the template marks as required (amber) are `CandidateCode` and `Position`;
`CandidateName` is needed as well for a candidate who does not exist yet, unless `AccountCode` names a member.

| # | Excel header | Also accepted as a header | Row field (JSON) | Required | Stored length | Lands in | Stored as |
|---|---|---|---|---|---|---|---|
| 1 | `CandidateCode` | `RepCode` | `candidateCode` | **yes** | 30 | profile | `RepresentativeCandidateProfiles.CandidateCode` (unique) |
| 2 | `CandidateName` | `Name`, `MemberName` | `candidateName` | for a new candidate, unless `AccountCode` names a member | 200 | profile | `CandidateName` |
| 3 | `AccountCode` | `MembersID` | `accountCode` | no | 20 | profile (link) | `MemberId` (looked up, see below); unique when set |
| 4 | `Position` | — | `position` | **yes** | 100 | **assignment** | `RepresentativeEventCandidates.Position` |
| 5 | `CellNo` | `CellPhone`, `Mobile`, `MobileNo` | `cellNo` | no | 20 | profile | `CellNo` |
| 6 | `TelNo` | `Telephone`, `Tel` | `telNo` | no | 20 | profile | `TelNo` |
| 7 | `CivilStatus` | — | `civilStatus` | no | 20 | profile | `CivilStatus` |
| 8 | `Gender` | `Sex` | `gender` | no | 20 | profile | `Gender` |
| 9 | `DateOfBirth` | `BirthDate`, `DOB` | `dateOfBirth` (+ `dateOfBirthRaw`) | no | date | profile | `DateOfBirth` (`date`) |
| 10 | `HighestEducation` | `Education` | `highestEducation` | no | 200 | profile | `HighestEducation` |
| 11 | `PreviousPositions` | — | `previousPositions` | no | 500 | profile | `PreviousPositions` |
| 12 | `SpecialSkills` | — | `specialSkills` | no | 500 | profile | `SpecialSkills` |
| 13 | `Reason` | `ReasonForCandidacy` | `reason` | no | 2 000 | **assignment** | `RepresentativeEventCandidates.Reason` |
| 14 | `BallotNumber` | `BallotNo` | `ballotNumber` | no | whole number 1–1 000 000 | **assignment** | `RepresentativeEventCandidates.BallotNumber` (unique within the election and position) |

Columns 1–2 and 4–13 are the **11 columns of the existing General Assembly candidate import** (`AccountCode, Position, CellNo, TelNo, CivilStatus, Gender, DateOfBirth, HighestEducation, PreviousPositions, SpecialSkills, Reason`) plus the three that a Representative candidate needs:
`CandidateCode` (a stable identity that does not depend on a membership), `CandidateName` (a candidate need not be a member) and `BallotNumber` (the printed number; optional).

Every staged row first lands, column for column, in `RepresentativeCandidateImportRows` (same names and lengths), together with `RowNumber`, `RowStatus`, `PlannedAction`, `MatchedProfileId`, `Resolution`, `ErrorCodes` and `ErrorMessage`.
At commit the values move from there into the two tables above; the election, the status `APPROVED` and the batch id are added to the roster entry.

How the columns relate to a member: `AccountCode` is looked up among **active members** (upper or lower case); if it matches, the profile is linked to that member (`MemberId`), and a candidate without a name of their own takes the member's name.
A member can have only one profile and a profile only one member; an import can add the link to a profile that has none, but can never change an existing link.

### 3.2 What is a profile, what is an assignment

| Belongs to the person (profile, reused by every election) | Belongs to this election (assignment, one per election, position and person) |
|---|---|
| `CandidateCode`, `CandidateName`, the member link, `CellNo`, `TelNo`, `CivilStatus`, `Gender`, `DateOfBirth`, `HighestEducation`, `PreviousPositions`, `SpecialSkills` | `Position`, `Reason`, `BallotNumber`, status (`APPROVED` / `WITHDRAWN`), the batch that created it |

Removing a candidate from a roster never deletes the profile.

### 3.3 How the text is cleaned

| Kind | Fields | Rule |
|---|---|---|
| single line | code, name, account code, position, phones, civil status, gender, education, ballot number | Unicode normalised (NFC); tabs, line breaks and other control characters become one space; runs of spaces collapse; trimmed; empty → no value |
| several lines | previous positions, special skills, reason | NFC; Windows line breaks become `\n`; other control characters removed; trimmed; empty → no value |
| code | `CandidateCode` | trimmed and **upper-cased**; 1–30 characters; letters, digits and `. _ / -`; must start with a letter or digit; no inner spaces |

All values are stored through parameterised statements (`utf8mb4`); a name such as `O'Brien; DROP TABLE Members;--` or one with accents or Chinese characters is stored exactly as typed.
Text that starts with `= + - @` is only dangerous when it is written back into a spreadsheet, so the **CSV error report** prefixes such cells with an apostrophe (§9).

### 3.4 Two limits per text column

A value is checked twice. The request itself accepts somewhat more than the column holds (for example a name up to 300 characters for a 200-character column), so that a value that is merely **too long** is reported **on its row**
(`TOO_LONG`, with the row number) instead of failing a whole request; only a payload far beyond the column (a hostile request) is refused as a whole with `400 VALIDATION`.

| Field | Request accepts up to | Stored limit |
|---|---|---|
| `candidateCode` | 60 | 30 |
| `candidateName` | 300 | 200 |
| `accountCode` | 40 | 20 |
| `position` | 150 | 100 |
| `cellNo`, `telNo` | 40 | 20 |
| `civilStatus`, `gender` | 40 | 20 (must match a list, §5.1) |
| `highestEducation` | 300 | 200 |
| `previousPositions`, `specialSkills` | 700 | 500 |
| `reason` | 2 500 | 2 000 |
| `ballotNumber` (text) | 20 | 1–1 000 000 |
| `dateOfBirthRaw` (text, when the cell is not a date) | 60 | — |

## 4. How the Admin reads the workbook

The reading is done by `RepresentativeSheetParser` (the same code for the template and for a roster export edited and sent back).

* **Header row.** The first row, on the first sheet that has one, that names both `CandidateCode` and `Position`. The sheet called `Candidates` (the template's) is tried first, then the others in file order.
  Headers are matched ignoring upper/lower case, blanks and punctuation (`Candidate Code`, `candidate_code` and `CANDIDATECODE` are the same), so the column **order is free** and **unknown columns are ignored**. The accepted alternative names are in the table of §3.1.
  A file without such a header is refused before anything is sent, with the standard text that lists the two required columns.
* **Rows that are not data.** Blank rows, the template's own grey sample row (`REP-0001` / `Juan Dela Cruz`) and a footer line (a row whose only filled cell contains "Do not modify" or "Total:") are set aside and counted. The screen says how many.
* **Nothing else is dropped on the administrator's computer.** A row with no code, no position, a repeated code, a bad date or a bad number is sent like any other row; the server marks it `INVALID` with its row number, so it appears in the preview and the error report and can stop an all-or-nothing commit.
  Dropping such rows silently would hide exactly the mistakes the administrator has to see.
* **Row numbers** are the numbers Excel shows, so "row 14" in a message is row 14 of the sheet.
* **Dates.** An Excel date cell is a date. A whole number typed into a General cell is read as an Excel date serial. Text is read as a date only in an unambiguous form (`yyyy-MM-dd`, `yyyy/MM/dd`, `d-MMM-yyyy`, `d MMMM yyyy`, `MMMM d, yyyy` and their variants);
  anything else, for example `03/04/1980`, which could be 3 April or 4 March, is sent as text and **reported as `DOB_INVALID`**, not guessed.
* **File checks before anything is sent**, in this order: the file must open as an `.xlsx` workbook (otherwise "could not be read as an Excel workbook (.xlsx)"); it must be no larger than **25 MB**; it must contain the header row; it must contain at least one data row (a file with only the sample row says what was set aside);
  it must not hold more rows than the server's limit (§8). The file never reaches the server, so the size limit is the Admin's; the row limit is the server's (`GET …/template`).
* **Sending.** The Admin creates the batch, then sends the rows in parts of at most 500 (or the server's per-request limit, if smaller), showing progress and a Stop button. After every part it checks that the server stored exactly what was sent;
  if not, or if any part fails, the batch is **cancelled** and nothing is checked or imported ("a half-sent file is never checked"). Only then does it ask the server to validate. If the server is still validating a very large file when the Admin's wait ends, the batch stays open and is offered in the history, where it can be continued or cancelled.
* **The template** is a workbook with a sheet `Candidates` (header row with the required columns in amber, one grey sample row, 5 000 prepared blank rows formatted as text for codes and phone numbers so leading zeros survive, and as `yyyy-mm-dd` for dates) and a sheet `Instructions` generated from the server's own column description.
  The **roster export** uses the same headers (plus `Status` and `AddedAt`), so an exported roster can be edited and imported into another election.

## 5. How a row is judged

### 5.1 Checks on the row itself (when it is staged)

These need nothing from the database. A row that fails any of them is stored as `INVALID` and **stays INVALID whatever the roster looks like**.

| Code | When |
|---|---|
| `CODE_INVALID` | the code is missing, longer than 30 characters, or contains anything but letters, digits and `. _ / -`, or does not start with a letter or digit |
| `POSITION_REQUIRED` | no position |
| `TOO_LONG` | a value exceeds the stored limit of its column (§3.4) |
| `PHONE_INVALID` | a phone number with other characters than digits, blanks and `+ - ( ) . /`, or shorter than 3 or longer than 20 characters |
| `CIVIL_STATUS_INVALID` | not one of `Single`, `Married`, `Widowed`, `Separated`, `Divorced` (any capitalisation; stored in the list's spelling) |
| `GENDER_INVALID` | not one of `Male`, `Female`, `Prefer not to say` |
| `DOB_INVALID` | the cell is not a date (§4), the date is in the future, or before 1900 |
| `BALLOT_NUMBER_INVALID` | not a whole number from 1 to 1 000 000 |

The lists for civil status and gender are the ones of the General Assembly candidacy form. Highest education is free text.

### 5.2 Checks against the file and the database (when it is validated)

Rows are judged **in spreadsheet order**, and the first row that claims a place keeps it. The same function judges the file when it is previewed and again inside the commit transaction against fresh data (§7), so what was previewed is what is verified before anything is written.

| Order | Check | Result |
|---|---|---|
| 1 | The position must be one of the election's configured position rules; the stored spelling is the rule's. | `INVALID` `POSITION_NOT_CONFIGURED` |
| 2 | The same candidate at the same position twice in the file. | the later row is `INVALID` `DUPLICATE_IN_FILE` ("duplicate of row N") |
| 3 | The same code on two rows with different details (name, account code, phones, civil status, gender, education, previous positions, skills, date of birth). A blank cell never disagrees. | `INVALID` `CODE_CONFLICT_IN_FILE`: one code identifies exactly one person |
| 4 | `AccountCode` given but no **active** member has it. | `INVALID` `MEMBER_NOT_FOUND` |
| 5 | The member is already linked to another profile, the stored profile is linked to another member, or two codes in the file claim the same member. | `INVALID` `MEMBER_ALREADY_LINKED` |
| 6 | A **new** candidate (no profile with this code) needs a name of their own or the name of the member named by `AccountCode`. | `INVALID` `NAME_REQUIRED`; otherwise planned action `CREATE_PROFILE` |
| 7 | An existing candidate whose profile is deactivated. | `INVALID` `PROFILE_INACTIVE` (restore the profile first) |
| 8 | An existing candidate whose profile differs from the file (§6). | `CONFLICT` `PROFILE_DIFFERS` until decided; same details → planned action `REUSE_PROFILE` |
| 9 | The candidate is already on this election's roster at this position (any status; the unique key covers withdrawn entries too). | `INVALID` `ALREADY_ASSIGNED` |
| 10 | The ballot number is already used at this position, on the roster or by an earlier row of the file. | `INVALID` `BALLOT_NUMBER_TAKEN` |
| 11 | The position's candidate limit (`MaxCandidates`, if set) is reached: candidates already on the roster plus the accepted rows before this one. | `INVALID` `POSITION_FULL`: "the candidate limit has been reached; this candidate was not added" |
| 12 | Otherwise. | `VALID` |

Rows 9–11 are what protects the position rules: an import cannot put more candidates on a position than its `MaxCandidates`, in spreadsheet order, and cannot repeat a person or a ballot number.
The database enforces the same two uniqueness rules again (§7), so even a concurrent change cannot slip a duplicate in.

## 6. Candidates that already exist: conflicts

When the code matches a stored profile, the file's **non-blank** values are compared with it (name, cell number, telephone, civil status, gender, highest education, previous positions, special skills, date of birth, and "member link" when the file names a member and the profile has none).
**A blank cell never means "erase"**; it means "no information".

* **No difference** → the profile is reused as it is (`REUSE_PROFILE`).
* **A difference** → the row is a `CONFLICT` and the batch cannot be committed until every conflict has a decision. The administrator chooses, per row or for all conflicts at once:

| Decision | What happens |
|---|---|
| `USE_EXISTING` | The stored profile stays exactly as it is; the person is added to the roster with the position, reason and ballot number of the row. |
| `UPDATE_PROFILE` | The file's non-blank values overwrite the stored ones (blank cells keep the stored value) and a missing member link is added. **Refused** when the candidate is on the roster of an election that is open now (`PROFILE_IN_OPEN_EVENT`), and refused when the **name** would change for a candidate who has received votes in an earlier election (`PROFILE_HAS_VOTES`); those rows become `INVALID`. |
| `SKIP` | The row is left out (`SKIPPED`). |

Resolving stores the decisions and then judges the whole batch again, because a decision changes what happens to rows and the capacity of a position. A decision is only accepted for a row that is a conflict (otherwise `400`: "row N has no conflict to resolve").
The Admin asks for a confirmation before applying one decision to all conflicts, and says so more strongly for `UPDATE_PROFILE`.

## 7. The commit

1. `confirm` must be `true` (the Admin asks the question "Add N candidate(s) to the roster of …?", saying what is created, what is left out and that it is all or nothing).
2. In one transaction the event row is locked (`SELECT … FOR UPDATE`, the same first lock as every roster change, so imports, assignments, rule edits and opening/closing are serialised) and then the batch row. The batch must be `VALIDATED`, the event a Representative election and its roster unlocked.
3. **The file is judged again against the data as it is now.** If any row's verdict differs from what was previewed (someone filled a slot, added a profile, opened the election …), **nothing is imported**: the preview is refreshed and the answer is `409 VALIDATION_STALE`; the administrator reviews and confirms again.
4. Conflicts left → `409 CONFLICTS_UNRESOLVED`. "Import only if every row is valid" (`requireAllValid`) with invalid rows → `409 NOT_ALL_VALID`. No valid row → `409 NOTHING_TO_COMMIT`.
5. Four set-based statements write the result (no statement per candidate):
   1. **New profiles**, one per distinct code (rows of the same code agree by now, so blanks are merged); the member link comes from `AccountCode`, the name falls back to the member's.
   2. **Approved updates** (`UPDATE_PROFILE`): only the values the file provides; a missing member link is added, never replaced.
   3. **Roster entries** (`APPROVED`, tagged with the batch), in spreadsheet order. The unique keys *(election, position, profile)* and *(election, position, ballot number)* are the last line of defence.
   4. The imported rows are marked `COMMITTED`.
6. The batch becomes `COMMITTED` with a summary ("Imported N candidate(s) (X new profile(s), Y existing profile(s) reused …)"), and an audit entry is written in the same transaction. The event row itself is not modified, so an open event editor does not become "stale".
7. If anything fails, the transaction is **rolled back** (no profile and no roster entry from the batch remains), the batch becomes `FAILED` with a plain explanation, the failure is audited (`REP_IMPORT_FAILED`) and the answer is `500 IMPORT_FAILED`. The same batch can be validated and committed again once the cause is gone.
   If the answer to a commit is lost on the way, the Admin asks the server what happened instead of guessing, and pressing Import again adds nothing twice.

## 8. Limits, configuration and measured speed

Nothing is fixed at 1 000 rows. The limits are the `RepresentativeImport` configuration section (not present in `appsettings.json`, so the defaults of the class apply; set the keys in `appsettings` or as environment variables, for example `RepresentativeImport__MaxRowsPerBatch`). The values must be positive; the API does not check them.

| Key | Default | Meaning |
|---|---|---|
| `MaxRowsPerBatch` | 20 000 | most data rows in one file/batch |
| `MaxRowsPerRequest` | 2 000 | most rows in one staging request (the Admin sends at most 500) |
| `MaxOpenBatchesPerEvent` | 10 | unfinished batches (`UPLOADED`, `VALIDATING`, `VALIDATED`, `INVALID`, `FAILED`) one election may have; cancel or commit some to continue |
| `InsertChunkRows` | 250 | rows per multi-row `INSERT` when staging |

The Admin reads `MaxRowsPerBatch` from the template call each time, so a changed setting is honoured without a new Admin build. The file size limit (25 MB) is a constant of the Admin.

Measured on the scratch copy of the supplied database (the test client and the API on one 2-core machine, 2026-11-09; wall-clock seconds of each API step, files cut into requests of 2 000 rows):

| File | Create | Stage | Validate | Commit | Total |
|---|---:|---:|---:|---:|---:|
| 1 000 new candidates | 0.15 | 0.29 | 0.24 | 0.16 | 0.84 |
| 2 000 new candidates | 0.01 | 0.38 | 0.22 | 0.22 | 0.83 |
| 5 000 rows, 750 of them deliberately bad (4 250 imported) | 0.02 | 0.73 | 0.60 | 0.39 | 1.74 |
| the same 1 000 candidates into a second election (profiles reused, none created) | 0.02 | 0.18 | 0.20 | 0.14 | 0.54 |
| 12 000 new candidates (6 requests) | 0.02 | 2.10 | 1.25 | 0.91 | 4.27 |

After the 12 000-candidate import the roster pages, searches, summary and export answered in 38–238 ms (each within its stated budget), and the `EXPLAIN` of the roster page, the profile search and the profile matching each use an index.
A file with 20 000 rows (the default limit) was **not** timed; the figures above are what was measured. The Admin's own time to read a large `.xlsx` was not measured either (§11).

## 9. The error report, the history and the audit trail

* **Error report** (`GET …/error-report`): UTF-8 CSV with a byte-order mark (so Excel reads accents correctly), file name `representative-import-{batch}-errors.csv` (or `…-rows.csv` with `all=true`), columns
  `Row, CandidateCode, CandidateName, AccountCode, Position, Status, Action, Decision, ErrorCodes, Message`. By default it lists the rows that are `INVALID`, `CONFLICT` or `SKIPPED`; `all=true` lists every row. Cells starting with `= + - @`, a tab or a carriage return are prefixed with an apostrophe so a spreadsheet does not run them as formulas.
  At most 200 000 rows are written. Each download is audited (`EXPORT_REPORT`).
* **Preview**: the Admin pages the rows (25, 50, 100 or 200 per page, default 50), filters by status and searches by code or name; a row without a name shows the name it will end up with (the member's, or the stored profile's).
* **History**: every batch of an election stays listed with its file name, who sent it, when, the counts and the outcome; the staged rows are kept after the commit.
* **Audit** (`AuditLogs`): `REP_IMPORT_UPLOADED` (batch created), `REP_IMPORT_VALIDATED` (every validation, including the one after a decision), `REP_IMPORT_COMMITTED`, `REP_IMPORT_CANCELLED`, `REP_IMPORT_FAILED`, and `EXPORT_REPORT`. The file's SHA-256 is stored on the batch.

## 10. The task's 14 steps and where each one is

| # | Step of the specification | Where |
|---|---|---|
| 1 | Upload the Excel file | The Admin's file dialog; the file is read on the administrator's computer and **is not uploaded**: the server receives its name, SHA-256 and rows |
| 2 | Validate file type and size | Type: the file must open as an `.xlsx` workbook. Size: at most 25 MB (Admin) and at most `MaxRowsPerBatch` rows (server) |
| 3 | Validate worksheet and headers | §4: the header row must name `CandidateCode` and `Position`; the `Candidates` sheet is tried first |
| 4 | Parse the candidate records | `RepresentativeSheetParser` (§4) |
| 5 | Create an import batch | `POST representative-imports` (`UPLOADED`) |
| 6 | Stage the rows | `POST …/rows`, bulk multi-row `INSERT`, in parts |
| 7 | Match existing candidates | By `CandidateCode` only (§5.2, §6) |
| 8 | Duplicates inside the file | `DUPLICATE_IN_FILE`, `CODE_CONFLICT_IN_FILE` |
| 9 | Duplicate event assignments | `ALREADY_ASSIGNED`, and the unique key in the database |
| 10 | Required fields and types | §5.1 and §5.2 |
| 11 | Preview valid and invalid rows | `GET …/rows` and the Admin's preview |
| 12 | Error report | `GET …/error-report` |
| 13 | Confirmation before commit | `confirm = true`; the Admin's question |
| 14 | Transactional commit | §7 |

## 11. What was verified, and what was not

**Executed** (scratch copies of the supplied database; nothing touched a production system):

* The API import suite — 75 checks, all passing on 2026-11-09: the template, refusals (wrong event type, no position rules, too many rows, zero rows, unknown election, roles without permission, no token), a 17-row file with every kind of error judged to its exact expected status, reason and action,
  preview filters and paging, the confirmation rule, conflict resolution (`USE_EXISTING`, `UPDATE_PROFILE`, `SKIP`, an outdated batch version), the error report and its formula protection, the commit (exactly the valid rows, profiles created and reused, member link, reason and ballot number on the assignment, text with quotes and non-ASCII letters stored exactly),
  re-importing the same people, cancel, a commit that fails half-way (an injected database error: nothing kept, batch `FAILED`, audited, the same batch committed afterwards), four simultaneous commits of one batch (exactly one wins), six simultaneous assignments into one free slot (exactly one wins), the roster lock while the election is open and after it closed, the stale preview at commit (`VALIDATION_STALE`), and the retried-request rules.
* The import scale suite — 17 checks, all passing on 2026-11-09 (§8).
* The Admin's import screen against the real API — sixteen cases (`IMP-01` … `IMP-16`: the template, a small file from file to roster, errors and the CSV, conflicts and decisions, 1 000 and 2 200-row files in parts, a failing final save, a roster changed after the preview, a failing part and the Stop button, refused files, roles, an import left open, a lost answer to the confirmation, the election box, unknown fields, reading the workbook, a deleted or outdated batch).
  All sixteen passed on 2026-11-09, after the 25 MB limit was added (its check is in `IMP-09`; with the limit check removed, that case fails, so the check is covered). The totals of the final full run are in `09-testing.md`.

**Not executed or not verified:**

* The ClosedXML adapters of the WPF project (the code that turns a real `.xlsx` file into cells and back) were **not run**; the parser and the template builder were tested on in-memory sheet data, not on a file produced by Excel. Opening a real workbook produced by Excel (merged cells, formulas, hidden sheets, other locales) is untested.
* The WPF windows were not rendered; the screen's behaviour was tested at view-model level.
* A file of 20 000 rows, and a file near the 25 MB limit, were not run; the largest file tested is 12 000 rows.
* The commit of a very large batch holds the event's row lock for its duration (seconds at 12 000 rows); no concurrent user load was measured.
* The 25 MB size limit is a judgement, not derived from a measurement: a 20 000-row file is expected to be a few megabytes.
