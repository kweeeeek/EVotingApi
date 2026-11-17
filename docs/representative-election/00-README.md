# Representative Election module — documentation index

Branch `feature/representative-election`, base `0b5a180` (= `main` when the work started). All twelve documents exist. Read **`12-change-ledger.md`** first: it is the summary of what changed, what was tested,
what was **not** done, how to deploy and how to roll back (its section 1 is one page). The documents refer to each other by these file names.

| File | Subject |
|---|---|
| `01-audit-and-design.md` | Source and database audit (Part 2.4), architecture report, entity relationships, design decisions, risks |
| `02-import-and-column-mapping.md` | Excel template column → staging field → profile/assignment field (Part 4); the import flow, row rules, conflicts, commit, limits, measured sizes |
| `03-migration-review.md` | The two EF migrations, the hand-run SQL scripts, delete behaviour, risks, executed evidence |
| `04-rollback.md` | Rollback and recovery (referenced from the migration's rollback guard) |
| `05-api-contracts.md` | Endpoint catalogue with permissions and error codes |
| `06-voter-lookup-paging.md` | Server-side paging contract and index measurements (referenced from `ApiDbContext.cs`) |
| `06-eligibility-policy.md` | Eligibility settings and the open-election policy (referenced from `EligibilityService.cs`) |
| `07-permissions.md` | Permission mapping and navigation gating |
| `08-kiosk-offline.md` | Kiosk and offline contract; what the unmodified Kiosk does; the work list for the Kiosk repository |
| `09-testing.md` | Part 15 requirement-by-requirement mapping, totals of the final run, mutation checks, and the full list of what was **not** run (generated from the recorded results) |
| `10-ga-regression-report.md` | General Assembly differential regression (original build vs new build, and original build on the new schema) |
| `11-deployment.md` | Deployment order, configuration, post-deployment checks, review of event schedules |
| `12-change-ledger.md` | Final change ledger: files, database, API, UI, behaviour that differs, test results, unfinished items, limitations, deployment, rollback, Part 17 rules, Part 18 definition of done |

Honest status of the whole task: the API side is implemented and was tested over HTTP against scratch copies of the supplied database. The Admin WPF screens are implemented and were tested at view-model level against the real API,
plus a static XAML check. **Not done or not run:** the Kiosk application (a Representative election cannot yet be voted on a Kiosk), the real `dotnet build` / `dotnet test` / `dotnet ef` of the project files, WPF rendering, the real Excel library.
Nothing was run against a production database. `12-change-ledger.md` §8 and §9 and `09-testing.md` §8 list everything else.
