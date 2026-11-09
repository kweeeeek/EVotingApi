# 07 — Permissions and navigation

## 1. Principles

* **The API decides.** Every admin route carries `[RequirePermission(…)]`; the permissions are read from the database for the signed-in user (cached for 15 seconds, so a change on the Roles screen is effective within that time).
  One attribute lists permissions of which **any one** is enough; several attributes on one action must all be satisfied. A missing token is 401, a missing permission is 403 with the permission names in the answer.
* **The Admin application only mirrors it.** It hides or disables a menu entry or button the API would refuse, so a user is not offered something that fails. It never grants anything; the API checks every request again.
* **Kiosk and voter routes use roles, not permissions.** `Kiosk` (the existing station login) and `Voter` (issued by `voter-verify`, bound to one election) — `05-api-contracts.md` §1.
* **No role is silently widened.** The migration adds two new permissions and grants them only to the seeded super-administrator role (`RoleId 1`). Every other role receives them only when an administrator ticks them on the Roles screen.

## 2. Permissions

Existing permissions are reused; two are new.

| Permission | New? | Used for in this module |
|---|---|---|
| `EVENT_VIEW` | existing | read position rules, rosters, import history, results; the Admin's election list; voter lookup |
| `EVENT_EDIT` | existing | edit position rules, open/close, unlock a roster |
| `EVENT_ADD` | existing | create an event (including its type) |
| `EVENT_DELETE` | existing | delete an event (a Representative event purges its own ballots) |
| `CANDIDATE_VIEW` | existing | read profiles, rosters, import batches |
| `CANDIDATE_ADD` | existing | create a profile, assign a candidate, run an import |
| `CANDIDATE_EDIT` | existing | edit a profile, its photo, activate / deactivate |
| `CANDIDATE_DELETE` | existing | remove a candidate from a roster |
| `REPORT_VIEW` | existing | read Representative results (with `EVENT_VIEW`) |
| `REPORT_EXPORT` | existing | export results (CSV) and the roster; read the import template (the roster export uses its column list) |
| `SETTINGS_ELIGIBILITY_VIEW` | **new** (`View Member Eligibility Settings`) | read the eligibility settings and the impact preview |
| `SETTINGS_ELIGIBILITY_EDIT` | **new** (`Edit Member Eligibility Settings`) | change the thresholds and recalculate |

## 3. Who can do what, by action

Endpoint-level rules as coded (`any one of` the listed permissions):

| Action | Endpoint | Permission(s) |
|---|---|---|
| View position rules | `GET /admin/events/{id}/position-rules` | `EVENT_VIEW`, `EVENT_EDIT` |
| Save position rules | `PUT /admin/events/{id}/position-rules` | `EVENT_EDIT` |
| Open / close an event | `POST /admin/events/{id}/open`, `/close` | `EVENT_EDIT` |
| Unlock a roster | `POST …/representative-roster/unlock` | `EVENT_EDIT` |
| List / read profiles | `GET /admin/representative-profiles[/{id}]` | `CANDIDATE_VIEW` (read one: or `CANDIDATE_EDIT`) |
| Create a profile | `POST /admin/representative-profiles` | `CANDIDATE_ADD` |
| Edit a profile, photo, active flag | `PUT /admin/representative-profiles/{id}[/photo\|/active]` | `CANDIDATE_EDIT` |
| Read a profile photo | `GET …/{id}/photo` | `CANDIDATE_VIEW`, `CANDIDATE_EDIT` |
| View a roster, summary | `GET …/representative-roster[/summary]` | `CANDIDATE_VIEW`, `EVENT_VIEW` |
| Export a roster | `GET …/representative-roster/export` | `REPORT_EXPORT`, `CANDIDATE_VIEW` |
| Assign a candidate | `POST …/representative-roster` | `CANDIDATE_ADD` |
| Remove a candidate | `DELETE …/representative-roster/{id}` | `CANDIDATE_DELETE` |
| Import: upload, stage, validate, resolve, commit, cancel | `POST …/representative-imports…` | `CANDIDATE_ADD` |
| Import: read batch, rows, error report | `GET …/representative-imports/{id}…` | `CANDIDATE_VIEW`, `CANDIDATE_ADD` |
| Import: template | `GET …/representative-imports/template` | `CANDIDATE_VIEW`, `CANDIDATE_ADD`, `REPORT_EXPORT` |
| Import: history of an election | `GET /admin/events/{id}/representative-imports` | `CANDIDATE_VIEW`, `EVENT_VIEW` |
| Representative results | `GET /events/{id}/representative-results` | `EVENT_VIEW`, `REPORT_VIEW` |
| Export results (CSV) | `…/representative-results/export` | `REPORT_EXPORT` |
| Eligibility settings, impact | `GET /admin/settings/eligibility`, `POST …/impact` | `SETTINGS_ELIGIBILITY_VIEW`, `SETTINGS_ELIGIBILITY_EDIT` |
| Change eligibility, recalculate | `PUT /admin/settings/eligibility`, `POST …/recompute` | `SETTINGS_ELIGIBILITY_EDIT` |
| Voter lookup | `GET /admin/voter-lookup` | `EVENT_VIEW` |

### Reading the table for your own roles

Open the Roles screen, note the permissions a role holds, and look each action up in the table above: the action is allowed when the role holds **any one** of the permissions listed for it (an endpoint with several permission attributes needs all of them).
The result for the roles of a given installation is a deployment decision and is kept with the deployment notes, not in this repository.

Points to decide when roles are reviewed:

* **The roster export carries each candidate's contact numbers, date of birth and education** (no images). It is allowed with `REPORT_EXPORT` or `CANDIDATE_VIEW`, so any role holding either can export it. A role that must not see candidates' contact details should hold neither permission.
* The import history of an election and the roster summary can be read with `EVENT_VIEW` alone, so a role that may only view elections sees who uploaded which file and the per-position counts. The staged rows and error reports need `CANDIDATE_VIEW` or `CANDIDATE_ADD`, the template `CANDIDATE_VIEW`, `CANDIDATE_ADD` or `REPORT_EXPORT`; creating, importing and removing candidates always needs `CANDIDATE_ADD` / `CANDIDATE_DELETE`.
* The Representative results screen needs `EVENT_VIEW` (the API also accepts `REPORT_VIEW`); a role with `REPORT_VIEW` only does not see the menu entry.

## 4. Admin navigation (`ShellViewModel`)

A menu entry is enabled when the user holds **any** of the permissions listed.

| Entry | Permission(s) | Added by this module? |
|---|---|---|
| Manage Events | `EVENT_VIEW` | no (the event editor gained the event type and the position-rules editor, which needs `EVENT_EDIT` to change anything) |
| Representative Candidates | `EVENT_VIEW` | yes — needs the election box (the list of elections) |
| Import Representatives | `CANDIDATE_ADD` | yes |
| Representative Results | `EVENT_VIEW` | yes |
| Voter Lookup | `EVENT_VIEW` | existing screen, now paged on the server |
| Member Eligibility | `SETTINGS_ELIGIBILITY_VIEW` | yes |
| Manage Candidates, Candidates List | `CANDIDATE_VIEW` | no (General Assembly candidacies) |

On-screen actions follow the table of §3: *Remove* needs `CANDIDATE_DELETE` and an editable roster; *Unlock* needs `EVENT_EDIT`, a locked roster and an event that is not open; *Export* needs `REPORT_EXPORT` or `CANDIDATE_VIEW`;
*Import* needs `CANDIDATE_ADD`; saving eligibility settings needs `SETTINGS_ELIGIBILITY_EDIT` (the screen is read-only with only the view permission); the Representative results export needs `REPORT_EXPORT`.

## 5. Granting the new permissions

Use the Roles screen: tick *View Member Eligibility Settings* and/or *Edit Member Eligibility Settings* for the roles that should have them. Nothing needs a restart (cache: 15 seconds).
`deploy/sql/005_representative_election.sql` seeds the same two permission rows and the same grant to role 1, so a database updated by the script and one updated by the EF migration end in the same state (`03-migration-review.md` §6).

## 6. What was verified

* API: the two permission rows and the role-1 grant after the migration, and the identical result of the hand-run SQL, are part of the SQL-versus-EF parity run and the rollback run (`03-migration-review.md` §6).
  Each API test suite asserts a sample of 403 / 200 answers for the roles it uses (a clerk, an operator, a committee member) at the endpoints it touches. **Not every cell of the matrix in §3 was executed**; the matrix is derived from the attributes in the code.
* Admin: the menu gating and the on-screen gating at view-model level against the real API (`09-testing.md`). The WPF rendering of a hidden or disabled control was **not** run.
