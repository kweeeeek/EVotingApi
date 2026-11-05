# 06 — Member eligibility settings and the OPEN-election policy

Part 9 of the engineering task. Settings > Member Eligibility lets an authorised administrator change the minimum Share Capital and minimum Savings Deposit
without redeploying the API, and the API is the single authority on what those thresholds do.

## 1. What the audit found

| Question | Finding |
|---|---|
| Where were the thresholds defined? | `appsettings.json` → `Eligibility:MinShareCapital = 3000`, `MinSavingsDeposit = 4000` (bound to `EligibilityOptions`). The legacy Admin application had its own copies of the rule as well. |
| Who evaluated the rule? | `MemberEligibilityPolicy.Evaluate` in the Admin API (member create, update, import, preview). |
| Who *consumes* eligibility? | Everybody reads the **persisted flag** `Members.IsEligibleToVote` (+ `VotingRemarks`): voter verification (`AuthController`), online and offline vote casting, the kiosk member snapshot, voter lookup, the registration station, dashboards. Nothing re-computes it at read time. |
| The rule as implemented | eligible ⇔ `ShareCapital ≥ MinShareCapital` **AND** `SavingsDeposit ≥ MinSavingsDeposit` **AND** `LoanStatus ≠ PastDue`. Active membership, branch participation in the election and "has not voted yet" are separate checks made by the voting endpoints themselves; they were not touched. |
| Default values | 3000 / 4000 — unchanged. Until an administrator saves a value, these configuration values apply (the screen says so: source `configuration` vs `database`). |

Because the flag is stored, "changing a threshold" really means *recomputing a flag for every member*. That drives the design below.

## 2. Design

* **Storage**: table `SystemSettings` (`SettingKey`, `SettingValue`, `Description`, `Version`, `UpdatedAt`, `UpdatedByUserId`). Keys: `Eligibility.MinShareCapital`,
  `Eligibility.MinSavingsDeposit`, and `Eligibility.AppliedStamp` ("share|savings" the member flags were last fully recomputed with). Values are stored as exact decimal text
  (`0.00`); the thresholds are `decimal` end-to-end (C# `decimal`, SQL `DECIMAL(18,2)`), never floating point.
* **One service**: `IEligibilityService` — `GetThresholdsAsync` (cached), `EvaluateAsync`, `PreviewImpactAsync`, `SaveAsync`, `RecomputeAsync`, `FlagsInSyncAsync`, `InvalidateCacheAsync`.
  Member create/update/import and the live preview in the member editor call it; the controllers no longer read `EligibilityOptions`.
* **Validation (server side)**: each amount must be 0 … 1,000,000,000,000 with at most two decimal places (`MoneyRules`). Negative, non-numeric and over-precise values are refused with 400.
* **Remarks text** is unchanged for whole-peso thresholds ("Share Capital is below ₱3,000 (Current: ₱2,999.99)"); a threshold with centavos shows them ("₱3,000.25") so a remark can
  never say "below ₱3,000" about a balance of ₱3,000.24.
* **Recompute**: set-based SQL in id chunks of 20,000; only rows whose flag or remarks actually change are written; the remarks text is produced by the same wording as the C# policy
  (checked member-by-member in the tests). It runs after the thresholds are saved and is not cancelled if the client gives up (`CancellationToken.None`), because stopping halfway would
  leave members evaluated under two different rules.
* **Applied stamp**: written when a recompute completes. If the stamp differs from the thresholds (an interrupted recompute) the settings screen shows "recompute pending" and
  **an election cannot be opened** (`EVENT_NOT_READY`) until the recompute is run again.
* **Concurrency**: `ExpectedVersion` on save; a stale version is a 409 `CONCURRENCY` and changes nothing. The save takes row locks on the settings rows (`FOR UPDATE`) so two administrators cannot interleave.
* **Cache**: the thresholds are cached in the distributed cache (key `eligibility_thresholds_v1`, 5-minute TTL). If the cache cannot be read or written the service logs a warning and reads
  the database instead. A save evicts the key before the recompute runs, so the very next request uses the new values (test EL-16 — no stale window). If the eviction itself fails, other API
  instances may use the previous thresholds until the TTL expires (this is logged as an error).

## 3. The OPEN-election policy (decision required by Part 9)

**Question:** should an event snapshot the thresholds when it opens, so changing the global setting during an election cannot change who may vote?

**Examined:** the stored-flag architecture above. Kiosks download a member snapshot (`IsEligible` per member) at start-up and use it offline; voters are verified against the flag;
the registration station reads it. A per-event snapshot would mean every one of those readers evaluating eligibility *per event against the event's thresholds*, i.e. a different
snapshot format on the kiosk, a different verification query, and per-event recomputation — a rewrite of consumers the task explicitly asks not to rewrite, with a regression risk on the
General Assembly path.

**Decision (safest interpretation that satisfies the requirement):**

1. **While any election is open, the thresholds cannot be changed** (and a recompute is refused): `409 EVENT_OPEN_LOCK` naming the open election. Nobody becomes eligible or ineligible
   mid-election; the kiosks' cached snapshots stay valid for the whole election. (EL-18c/d/e/f)
2. **When an election opens, the thresholds in force are recorded on the event** (`Events.EligibilityMinShareCapital`, `EligibilityMinSavingsDeposit`) as evidence. They are shown in the event
   detail and survive the election; changing the thresholds after it closed does not rewrite them. (EL-18b, EL-19b)
3. **An election cannot open while the flags are out of step with the thresholds** (interrupted recompute), for either election type, manual or scheduled.
4. After the election is closed the thresholds may be changed again (EL-19).

The consequence an operator should know: *a late correction to a threshold during a live election requires closing it first.* That is deliberate.

Not done: a mid-election "grandfathering" mode, and a per-event threshold override. Both would need the consumer changes described above.

## 4. API

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/v1/admin/settings/eligibility` | `SETTINGS_ELIGIBILITY_VIEW` or `…_EDIT` | thresholds, source (`database`/`configuration`), version, last change (who/when), member counts, `locked` (an election is open), `recomputePending`, the non-configurable rules listed for the screen |
| `POST …/impact` | VIEW or EDIT | exact number who would become eligible/ineligible for proposed values; **writes nothing** |
| `PUT …` | `SETTINGS_ELIGIBILITY_EDIT` | `{minShareCapital, minSavingsDeposit, expectedVersion?, reason?}`; no-op when unchanged (`unchanged = true`, no audit row, no version bump) |
| `POST …/recompute` | `SETTINGS_ELIGIBILITY_EDIT` | explicit repair of an interrupted recompute |
| `POST /api/v1/admin/members/eligibility-preview` | existing member permissions | now answers with the thresholds in force |

The two new permission codes are seeded by the migration and granted to **RoleId 1 only**. Grant them to the operational role on the Roles screen. The WPF screen hides the page
without VIEW and disables Save without EDIT, **but the API decides** (test EL-04a/b/c: a role with VIEW only is refused with 403 on save even when the call is made directly).

## 5. Audit

Every successful change writes an audit row: action `UPDATE_ELIGIBILITY`, entity `Settings`, the acting administrator and timestamp (from the audit service), and the details
`MinShareCapital 3000.00 → 3500.00; MinSavingsDeposit 4000.00 → 4000.00; previous source=database, version 1 → 2; reason: …`. A recompute writes `RECOMPUTE_ELIGIBILITY` with the number of
members evaluated and flipped. Refused attempts do not change data. (EL-13…13d)

## 6. Verified (executed) and not verified

Executed over HTTP against a real MySQL copy (`t03_eligibility.py`, ids above in this document; totals in `09-testing.md`), and through the real WPF screen logic against the real API
(`Member eligibility` suite: ELA-01…11 and ELV-01…08, 19 passed; seven deliberate defects in the screen were each caught by the intended test).

Not verified: Redis itself (the cache was exercised through the in-process distributed-cache implementation; the eviction call is the same `IDistributedCache.RemoveAsync`, but a real Redis was not available);
behaviour with a very large member table under write load; WPF rendering of the screen.

## 7. Known limitations

* The recompute SQL walks the members table by id range and has **no `IsActive` filter**, so inactive members' flags are recalculated as well. The member counts shown on the settings screen (`ActiveMembers`, `EligibleMembers`) count active members only.
* The unit of "open" is the event status `Opened`, not the schedule window; a Closed event inside its window is not "open" for this lock (the scheduler may auto-open it within seconds).
* Eligibility of a *candidate* (Representative profile) is not governed by these thresholds; a candidate need not be a member.
