# Status — Representative election work (save point 2026-11-05)

Branch `feature/representative-election`, branched from `main` at `0b5a180`. This page is a save point: what exists, what has been checked, what remains.
It will be replaced by the final change ledger (`12-change-ledger.md`) when the work is finished.
**The work is not finished and the branch is not ready to merge.**

## What exists on this branch

* General Assembly stays the default event type. A new event type `REPRESENTATIVE` has its own candidate profiles, per-election roster, ballots and results.
  Seven new tables, added by two additive EF migrations and by the same change as hand-run SQL (`deploy/sql/005_*` and `006_*`, each with a verify and a rollback script).
* `EventPositionRules.MaxCandidates` (new; distinct from `MaxVotes` and `TotalSeats`) and editable position rules in the event API.
* Representative candidate import (template, staging, preview, error report, explicit confirmation, transactional commit), roster management, voting API,
  offline-sync API for the kiosk, results and export.
* Member eligibility thresholds kept in the database (Settings > Member Eligibility API) with the open-election lock described in `06-eligibility-policy.md`.
* Server-side paged voter lookup (plus two indexes).
* New permissions `SETTINGS_ELIGIBILITY_VIEW` / `SETTINGS_ELIGIBILITY_EDIT`; the new admin endpoints use the server-side permission checks (`RequirePermission`).
* A separate fix (`4ac94c3`): `PasswordService` kept 64 MB of memory per Argon2 hash/verify.

The Admin WPF screens for all of this live in the separate Admin solution and are not part of this repository.
The Kiosk application has **not** been changed; its contract is in `08-kiosk-offline.md`.

## What has been checked, and how

Everything below ran against **scratch copies** of the database dump supplied with the task (MySQL 8.0.46). Nothing was run against a production database.

* SQL scripts versus EF migrations: 48 / 48 checks (identical schema and history, safe to run twice, half-applied state, rollback restores the original,
  rollback refused once a ballot exists, duplicate-rule case) — `03-migration-review.md` §6.
* General Assembly regression, original build versus new build, the same 12 flows over HTTP: 81 / 81 inline checks pass on both builds;
  100 calls compared: 86 identical, 14 differ only by added fields, 0 different.
  This found and fixed one defect: the kiosk position rules came back in alphabetical order because of the new unique index (now `ORDER BY Id`).
* Representative, eligibility, voter-lookup, import and offline flows: HTTP-level scripts and Admin view-model suites against the real API
  (the full list will be written to `09-testing.md` after the final full run).
* `EVoting.Api.Tests`: 73 / 73 through a minimal stand-in runner. The real xUnit runner has not been used.

## What remains (in this order)

1. Old API build on the new schema, General Assembly differential → `10-ga-regression-report.md` (`04-rollback.md` §7 points there).
2. Remaining checks: General Assembly bulk offline sync against a Representative event (RO-13b), the scheduler auto-open gate, one more mutation check on the results-screen logic.
3. xUnit tests inside `EVoting.Api.Tests` for the Representative, position-rule and eligibility code; fix the remaining `First`/`FirstOrDefault` calls that have no `OrderBy`.
4. Documents still to write: `01`, `02`, `05`, `06-voter-lookup-paging`, `07`, `09`, `11`, `12`.
5. Final full regression run with recorded totals, then the final change ledger with rollback instructions.
6. Kiosk changes (separate repository) — contract only so far.

## Limits of the evidence so far

* The tests used an in-process cache, not a real Redis. The test harness compiles with the .NET 10 SDK while the projects target net9.0.
  The Argon2 fix could not be measured on .NET 9.
* The EF migration files and the model snapshot were produced with an offline stand-in for `dotnet ef`.
  Please run `dotnet ef migrations has-pending-model-changes` once on a normal development machine.
* `READ COMMITTED` voting transactions need `binlog_format=ROW` on the production MySQL; the General Assembly path still uses `RepeatableRead`.
* Large-table timings were taken on a small machine with a warm cache.
* Not done: a manual single-candidate "Add" dialog in the Admin (the API supports it), the Kiosk changes, and anything that needs WPF rendering or kiosk hardware.
* This repository is public. Audit notes about the existing system that concern production configuration are delivered to the project owner separately and are not listed here.
