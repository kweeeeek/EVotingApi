# deploy/sql: database scripts

| File | What |
|---|---|
| `rrms-complete-schema-2026-11-20.sql` | The **complete database structure** as of 2026-11-20 (the 2026-11-12 database + migrations 005..013). Reference rows only (permissions, roles, branches, migration history): **no members, users, votes or audit data**. For a NEW empty database (new server, staging, tests). It starts with `CREATE DATABASE rrms`: **never run it on the live server.** Verified: 23 tables, 35 permissions. |
| `005_...` .. `013_...` | Migrations, in number order, for an existing database. Each has a `_rollback` (and some a `_verify`). All are idempotent. |
| `check_permissions.sql` | Read-only: shows which roles hold which permission codes. |
| `dump-live-database.bat` | Run **on the server**: a consistent full dump of the live database (`--single-transaction`, no table locks). The dump holds **all member and vote data**: keep it on the server or an encrypted drive. `*-live-*.sql` files are git-ignored so a dump can never be committed. |

## Migrations

| # | Adds | Needed by |
|---|---|---|
| 005 | Representative election | API with the Representative module |
| 006 | Voter lookup indexes | performance |
| 007 | Voting passes | Registration QR passes |
| 008 | Complete permission list | Admin menu / reports |
| 009 | `OFFLINE_VOTE_IMPORT` permission | Kiosk USB vote import |
| 010 | `OFFLINE_PACKAGE_EXPORT` permission | Kiosk / Registration USB packages |
| 011 | `QrIssuances` table | QR per election + registration desk |
| 012 | `Users.MustChangePassword` | Own password at first Registration sign-in |
| 013 | `Events.IsCatchUp` | Catch-up General Assembly |

Order when deploying: **SQL first, then the API** (the API reads the new columns).
