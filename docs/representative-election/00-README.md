# Representative Election module — documentation index

Work in progress on branch `feature/representative-election`. This index says which documents exist **now**; code comments elsewhere in the repository already point at
some of the planned file names, so those names are fixed.

| File | Subject | Status |
|---|---|---|
| `01-audit-and-design.md` | Source/database audit (Part 2.4), architecture report, ERD, API contract design, WPF plan | **planned** (referenced from `RepresentativeEntities.cs`) |
| `02-import-and-column-mapping.md` | Excel template column → staging field → profile/assignment field (Part 4); the import flow, row rules, conflicts, commit, limits | written (2026-11-09) |
| `03-migration-review.md` | The two EF migrations, SQL scripts, delete behaviour, risks, executed evidence | written |
| `04-rollback.md` | Rollback and recovery (referenced from the migration's rollback guard) | written |
| `05-api-contracts.md` | Endpoint catalogue with permissions and error codes | written (2026-11-09) |
| `06-voter-lookup-paging.md` | Server-side paging contract and index measurements (referenced from `ApiDbContext.cs`) | written (2026-11-09) |
| `06-eligibility-policy.md` | Eligibility settings and the OPEN-election policy (referenced from `EligibilityService.cs`) | written |
| `07-permissions.md` | Permission mapping and navigation gating | written (2026-11-09) |
| `08-kiosk-offline.md` | Kiosk and offline contract; what the unmodified Kiosk does; work list for the Kiosk repository | written |
| `09-testing.md` | Part 15 test mapping: executed vs not executed, totals | **planned** (after the final full run) |
| `10-ga-regression-report.md` | General Assembly differential regression (original build vs new build, and original build on the new schema) | written (2026-11-09) |
| `11-deployment.md` | Deployment order, configuration, post-deploy steps | **planned** |
| `12-change-ledger.md` | Final change ledger incl. rollback instructions (Part 16/17) | **planned** |
| `STATUS.md` | Save point: what exists, what was checked, what remains, limits of the evidence | written (2026-11-05) |

Honest status of the whole task: the API side is implemented and was tested against scratch copies of the supplied database; the Admin WPF screens are implemented and were tested
at view-model level against the real API plus a static XAML check, but WPF rendering and the Kiosk application itself were **not** run. Nothing was run against a production database.
