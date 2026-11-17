using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Member eligibility — ONE place decides it.
    //
    //  Policy (unchanged business rule, now configurable):  eligible  ⇔  ShareCapital ≥ MinShareCapital
    //                                                                 AND SavingsDeposit ≥ MinSavingsDeposit
    //                                                                 AND LoanStatus ≠ PastDue.
    //  "Active membership", "branch participates in the event" and "has not voted yet" are checked at voting time by the voting
    //  endpoints themselves and are not touched here.
    //
    //  Thresholds: stored in SystemSettings (editable by an authorised Admin, audited); until an administrator saves them the
    //  appsettings values (default 3000 / 4000, identical to the legacy rule) apply.
    //
    //  The result of the policy is persisted per member (Members.IsEligibleToVote / VotingRemarks) because every reader — voter
    //  verification, the kiosk snapshot, the lookup screens, the registration station — reads that flag. Changing a threshold
    //  therefore recomputes the flags (set-based, in id chunks). An "applied stamp" records which thresholds the flags were computed
    //  with, so a half-finished recompute is visible and blocks opening an election.
    //
    //  OPEN-event policy (documented in docs/representative-election/06-eligibility-policy.md):
    //   * thresholds cannot be changed while an election is open (409) — nobody becomes eligible/ineligible mid-election;
    //   * the thresholds in force are recorded on the event when it opens (Events.EligibilityMin*), as evidence.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════

    public static class SettingKeys
    {
        public const string MinShareCapital = "Eligibility.MinShareCapital";
        public const string MinSavingsDeposit = "Eligibility.MinSavingsDeposit";
        /// <summary>"{share}|{savings}" the member flags were last fully recomputed with.</summary>
        public const string AppliedStamp = "Eligibility.AppliedStamp";
    }

    public sealed record EligibilityThresholds(
        decimal MinShareCapital, decimal MinSavingsDeposit, string Source, int Version, DateTime? UpdatedAt, string? UpdatedBy)
    {
        public const string FromDatabase = "database";
        public const string FromConfiguration = "configuration";

        public string Stamp => MakeStamp(MinShareCapital, MinSavingsDeposit);

        public static string MakeStamp(decimal share, decimal savings) =>
            share.ToString("0.00", CultureInfo.InvariantCulture) + "|" + savings.ToString("0.00", CultureInfo.InvariantCulture);

        public EligibilityOptions ToOptions() => new() { MinShareCapital = MinShareCapital, MinSavingsDeposit = MinSavingsDeposit };
    }

    public enum EligibilitySaveKind { Saved, Unchanged, Invalid, VersionConflict, ElectionOpen }

    public sealed class EligibilitySaveOutcome
    {
        public EligibilitySaveKind Kind { get; init; }
        public string? Message { get; init; }
        public string? Field { get; init; }
        public EligibilitySettingsResponse? Settings { get; init; }
        public EligibilityRecomputeResponse? Recompute { get; init; }
    }

    public interface IEligibilityService
    {
        Task<EligibilityThresholds> GetThresholdsAsync(CancellationToken ct);
        Task<EligibilityOptions> GetOptionsAsync(CancellationToken ct);

        /// <summary>The single evaluation used by member create / update / import / preview and by the recompute.</summary>
        Task<(bool IsEligible, string Remarks)> EvaluateAsync(decimal shareCapital, decimal savingsDeposit, LoanStatus loanStatus, CancellationToken ct);

        /// <summary>False while the stored member flags were computed with other thresholds than the current ones.</summary>
        Task<bool> FlagsInSyncAsync(CancellationToken ct);

        Task<EligibilitySettingsResponse> GetSettingsAsync(CancellationToken ct);
        Task<EligibilityImpactResponse> PreviewImpactAsync(decimal minShare, decimal minSavings, CancellationToken ct);
        Task<EligibilitySaveOutcome> SaveAsync(EligibilitySettingsUpdateRequest req, CancellationToken ct);
        Task<EligibilityRecomputeResponse> RecomputeAsync(CancellationToken ct);
        Task InvalidateCacheAsync(CancellationToken ct);
    }

    public sealed class EligibilityService : IEligibilityService
    {
        public const string CacheKey = "eligibility_thresholds_v1";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
        private const int RecomputeChunk = 20_000;

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly IDistributedCache _cache;
        private readonly IOptions<EligibilityOptions> _config;
        private readonly ILogger<EligibilityService> _logger;

        public EligibilityService(ApiDbContext db, IAuditService audit, IDistributedCache cache,
                                  IOptions<EligibilityOptions> config, ILogger<EligibilityService> logger)
        {
            _db = db;
            _audit = audit;
            _cache = cache;
            _config = config;
            _logger = logger;
        }

        // ── Reading ──────────────────────────────────────────────────────────────────────────────────────
        public async Task<EligibilityThresholds> GetThresholdsAsync(CancellationToken ct)
        {
            try
            {
                var bytes = await _cache.GetAsync(CacheKey, ct);
                if (bytes is { Length: > 0 })
                {
                    var cached = JsonSerializer.Deserialize<EligibilityThresholds>(bytes);
                    if (cached != null) return cached;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Eligibility cache read failed; reading the database.");
            }

            var fresh = await ReadFromDatabaseAsync(ct);
            try
            {
                await _cache.SetAsync(CacheKey, JsonSerializer.SerializeToUtf8Bytes(fresh),
                    new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheTtl }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Eligibility cache write failed (non-fatal).");
            }
            return fresh;
        }

        private async Task<EligibilityThresholds> ReadFromDatabaseAsync(CancellationToken ct)
        {
            var rows = await _db.SystemSettings.AsNoTracking()
                .Where(s => s.SettingKey == SettingKeys.MinShareCapital || s.SettingKey == SettingKeys.MinSavingsDeposit)
                .ToListAsync(ct);

            var cfg = _config.Value;
            var shareRow = rows.FirstOrDefault(r => r.SettingKey == SettingKeys.MinShareCapital);
            var savingsRow = rows.FirstOrDefault(r => r.SettingKey == SettingKeys.MinSavingsDeposit);

            bool haveShare = TryParse(shareRow?.SettingValue, out var share);
            bool haveSavings = TryParse(savingsRow?.SettingValue, out var savings);

            if (!haveShare && !haveSavings)
                return new EligibilityThresholds(cfg.MinShareCapital, cfg.MinSavingsDeposit, EligibilityThresholds.FromConfiguration, 0, null, null);

            // A value that is missing or unreadable falls back to the configured default for THAT value only.
            if (!haveShare) share = cfg.MinShareCapital;
            if (!haveSavings) savings = cfg.MinSavingsDeposit;

            var newest = rows.OrderByDescending(r => r.UpdatedAt).First();
            string? by = null;
            if (newest.UpdatedByUserId.HasValue)
                by = await _db.Users.AsNoTracking().Where(u => u.Id == newest.UpdatedByUserId.Value).Select(u => u.Username).FirstOrDefaultAsync(ct);

            return new EligibilityThresholds(share, savings, EligibilityThresholds.FromDatabase,
                rows.Max(r => r.Version), TimeConv.AsUtc(newest.UpdatedAt), by);
        }

        private static bool TryParse(string? text, out decimal value)
        {
            value = 0m;
            return !string.IsNullOrWhiteSpace(text)
                && decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
                && MoneyRules.IsValid(value);
        }

        public async Task<EligibilityOptions> GetOptionsAsync(CancellationToken ct) => (await GetThresholdsAsync(ct)).ToOptions();

        public async Task<(bool IsEligible, string Remarks)> EvaluateAsync(
            decimal shareCapital, decimal savingsDeposit, LoanStatus loanStatus, CancellationToken ct) =>
            MemberEligibilityPolicy.Evaluate(shareCapital, savingsDeposit, loanStatus, await GetOptionsAsync(ct));

        public async Task<bool> FlagsInSyncAsync(CancellationToken ct)
        {
            var t = await ReadFromDatabaseAsync(ct);                     // never the cache: this guards the opening of an election
            var applied = await _db.SystemSettings.AsNoTracking()
                .Where(s => s.SettingKey == SettingKeys.AppliedStamp).Select(s => s.SettingValue).FirstOrDefaultAsync(ct);
            if (applied == null)
                return t.Source == EligibilityThresholds.FromConfiguration;   // nothing was ever saved: the flags carry the configured rule
            return string.Equals(applied, t.Stamp, StringComparison.Ordinal);
        }

        // ── Settings screen ──────────────────────────────────────────────────────────────────────────────
        public async Task<EligibilitySettingsResponse> GetSettingsAsync(CancellationToken ct)
        {
            var t = await ReadFromDatabaseAsync(ct);
            var open = await _db.Events.AsNoTracking().Where(e => e.IsActive && e.Status == "Opened").OrderBy(e => e.EventId).Select(e => e.Title).Take(3).ToListAsync(ct);
            return new EligibilitySettingsResponse
            {
                MinShareCapital = t.MinShareCapital,
                MinSavingsDeposit = t.MinSavingsDeposit,
                Source = t.Source,
                Version = t.Version,
                UpdatedAt = t.UpdatedAt,
                UpdatedBy = t.UpdatedBy,
                RecomputePending = !await FlagsInSyncAsync(ct),
                Locked = open.Count > 0,
                LockReason = open.Count > 0
                    ? $"An election is open ({string.Join(", ", open.Select(o => $"\"{o}\""))}). The thresholds cannot change while voting is open."
                    : null,
                ActiveMembers = await _db.Members.AsNoTracking().CountAsync(m => m.IsActive, ct),
                EligibleMembers = await _db.Members.AsNoTracking().CountAsync(m => m.IsActive && m.IsEligibleToVote, ct),
                OtherRules = new List<string>
                {
                    "The member must be active (not deleted).",
                    "The member's loan status must not be Past Due.",
                    "The member's branch must take part in the election.",
                    "The member must not have voted in that election already."
                }
            };
        }

        public async Task<EligibilityImpactResponse> PreviewImpactAsync(decimal minShare, decimal minSavings, CancellationToken ct)
        {
            // One aggregate pass over the members; nothing is written.
            var conn = _db.Database.GetDbConnection();
            var wasClosed = conn.State != System.Data.ConnectionState.Open;
            if (wasClosed) await conn.OpenAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT COUNT(*), " +
                    "       COALESCE(SUM(IsEligibleToVote = 1), 0), " +
                    "       COALESCE(SUM(ShareCapital >= @s AND SavingsDeposit >= @d AND LoanStatus <> 2), 0), " +
                    "       COALESCE(SUM(IsEligibleToVote = 0 AND ShareCapital >= @s AND SavingsDeposit >= @d AND LoanStatus <> 2), 0), " +
                    "       COALESCE(SUM(IsEligibleToVote = 1 AND NOT (ShareCapital >= @s AND SavingsDeposit >= @d AND LoanStatus <> 2)), 0) " +
                    "FROM Members WHERE IsActive = 1";
                AddParam(cmd, "@s", minShare);
                AddParam(cmd, "@d", minSavings);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return new EligibilityImpactResponse
                {
                    ActiveMembers = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture),
                    EligibleNow = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                    EligibleAfter = Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture),
                    BecomingEligible = Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture),
                    BecomingIneligible = Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture)
                };
            }
            finally
            {
                if (wasClosed) await conn.CloseAsync();
            }
        }

        private static void AddParam(DbCommand cmd, string name, decimal value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            p.DbType = System.Data.DbType.Decimal;
            cmd.Parameters.Add(p);
        }

        // ── Saving ───────────────────────────────────────────────────────────────────────────────────────
        public async Task<EligibilitySaveOutcome> SaveAsync(EligibilitySettingsUpdateRequest req, CancellationToken ct)
        {
            if (!MoneyRules.IsValid(req.MinShareCapital))
                return new EligibilitySaveOutcome { Kind = EligibilitySaveKind.Invalid, Field = "minShareCapital", Message = MoneyRules.Message("Minimum share capital") };
            if (!MoneyRules.IsValid(req.MinSavingsDeposit))
                return new EligibilitySaveOutcome { Kind = EligibilitySaveKind.Invalid, Field = "minSavingsDeposit", Message = MoneyRules.Message("Minimum savings deposit") };

            EligibilityThresholds before;
            await using (var tx = await _db.Database.BeginTransactionAsync(ct))
            {
                // Serialise concurrent savers on the settings rows.
                await _db.SystemSettings
                    .FromSqlRaw("SELECT * FROM SystemSettings WHERE SettingKey IN ({0}, {1}, {2}) FOR UPDATE",
                        SettingKeys.MinShareCapital, SettingKeys.MinSavingsDeposit, SettingKeys.AppliedStamp)
                    .ToListAsync(ct);

                var open = await _db.Events.AsNoTracking().Where(e => e.IsActive && e.Status == "Opened").OrderBy(e => e.EventId).Select(e => e.Title).Take(3).ToListAsync(ct);
                if (open.Count > 0)
                    return new EligibilitySaveOutcome
                    {
                        Kind = EligibilitySaveKind.ElectionOpen,
                        Message = $"An election is open ({string.Join(", ", open.Select(o => $"\"{o}\""))}). Close it before changing who is eligible to vote."
                    };

                before = await ReadFromDatabaseAsync(ct);
                if (req.ExpectedVersion.HasValue && req.ExpectedVersion.Value != before.Version)
                    return new EligibilitySaveOutcome
                    {
                        Kind = EligibilitySaveKind.VersionConflict,
                        Message = "The eligibility settings were changed by someone else after you opened them. Reload and try again."
                    };

                if (before.Source == EligibilityThresholds.FromDatabase
                    && before.MinShareCapital == req.MinShareCapital && before.MinSavingsDeposit == req.MinSavingsDeposit)
                {
                    await tx.RollbackAsync(ct);
                    return new EligibilitySaveOutcome { Kind = EligibilitySaveKind.Unchanged, Message = "The thresholds are already set to these values.", Settings = await GetSettingsAsync(ct) };
                }

                var now = DateTime.UtcNow;
                var actor = _audit.Actor;
                int newVersion = before.Version + 1;
                Upsert(SettingKeys.MinShareCapital, req.MinShareCapital, "Minimum share capital a member needs to be eligible to vote.", newVersion, now, actor.UserId);
                Upsert(SettingKeys.MinSavingsDeposit, req.MinSavingsDeposit, "Minimum savings deposit a member needs to be eligible to vote.", newVersion, now, actor.UserId);

                _audit.Stage(AuditActions.UpdateEligibilitySettings, AuditEntities.Settings, null,
                    $"MinShareCapital {before.MinShareCapital:0.00} → {req.MinShareCapital:0.00}; " +
                    $"MinSavingsDeposit {before.MinSavingsDeposit:0.00} → {req.MinSavingsDeposit:0.00}; " +
                    $"previous source={before.Source}, version {before.Version} → {newVersion}" +
                    (string.IsNullOrWhiteSpace(req.Reason) ? "." : $"; reason: {AuditWriter.Clip(req.Reason.Trim(), 300)}."));

                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            await InvalidateCacheAsync(ct);

            // The flags are recomputed after the settings are safely stored. CancellationToken.None: once the thresholds changed, the
            // recompute must not be abandoned halfway because the browser or the Admin application gave up waiting.
            var recompute = await RecomputeAsync(CancellationToken.None);
            return new EligibilitySaveOutcome
            {
                Kind = EligibilitySaveKind.Saved,
                Settings = await GetSettingsAsync(CancellationToken.None),
                Recompute = recompute,
                Message = "Eligibility settings saved. " + recompute.Message
            };
        }

        private void Upsert(string key, decimal value, string description, int version, DateTime utcNow, int? userId)
        {
            var text = value.ToString("0.00", CultureInfo.InvariantCulture);
            var row = _db.SystemSettings.Local.FirstOrDefault(s => s.SettingKey == key);
            if (row == null)
            {
                _db.SystemSettings.Add(new SystemSettingEntity
                {
                    SettingKey = key, SettingValue = text, Description = description,
                    Version = version, UpdatedAt = utcNow, UpdatedByUserId = userId
                });
            }
            else
            {
                row.SettingValue = text;
                row.Version = version;
                row.UpdatedAt = utcNow;
                row.UpdatedByUserId = userId;
            }
        }

        // ── Recompute ────────────────────────────────────────────────────────────────────────────────────
        public async Task<EligibilityRecomputeResponse> RecomputeAsync(CancellationToken ct)
        {
            var t = await ReadFromDatabaseAsync(ct);
            var impact = await PreviewImpactAsync(t.MinShareCapital, t.MinSavingsDeposit, ct);

            var span = await _db.Members.AsNoTracking().GroupBy(_ => 1)
                .Select(g => new { Min = g.Min(m => m.MemberId), Max = g.Max(m => m.MemberId) }).FirstOrDefaultAsync(ct);

            int processed = 0, changed = 0;
            if (span != null)
            {
                for (long lo = span.Min; lo <= span.Max; lo += RecomputeChunk)
                {
                    ct.ThrowIfCancellationRequested();
                    long hi = lo + RecomputeChunk - 1;
                    // Remarks text mirrors MemberEligibilityPolicy.Evaluate exactly (verified by a parity test); only changed rows are written.
                    changed += await _db.Database.ExecuteSqlRawAsync(
                        """
                        UPDATE /*+ NO_MERGE(x) */ Members m
                        JOIN (
                          SELECT MemberId,
                                 (ShareCapital >= {0} AND SavingsDeposit >= {1} AND LoanStatus <> 2) AS nf,
                                 IF(ShareCapital >= {0} AND SavingsDeposit >= {1} AND LoanStatus <> 2, 'Eligible',
                                    CONCAT_WS('; ',
                                      IF(ShareCapital < {0}, CONCAT('Share Capital is below ₱', FORMAT({0}, IF({0} = TRUNCATE({0}, 0), 0, 2), 'en_US'), ' (Current: ₱', FORMAT(ShareCapital, 2, 'en_US'), ')'), NULL),
                                      IF(SavingsDeposit < {1}, CONCAT('Savings Deposit is below ₱', FORMAT({1}, IF({1} = TRUNCATE({1}, 0), 0, 2), 'en_US'), ' (Current: ₱', FORMAT(SavingsDeposit, 2, 'en_US'), ')'), NULL),
                                      IF(LoanStatus = 2, 'Loan Status is Past Due', NULL))) AS nr
                          FROM Members WHERE MemberId BETWEEN {2} AND {3}
                        ) x ON x.MemberId = m.MemberId
                        SET m.IsEligibleToVote = x.nf, m.VotingRemarks = x.nr, m.UpdatedAt = {4}
                        WHERE m.IsEligibleToVote <> x.nf OR NOT (m.VotingRemarks <=> x.nr)
                        """,
                        new object[] { t.MinShareCapital, t.MinSavingsDeposit, lo, hi, DateTime.Now }, ct);
                    processed += (int)Math.Min(RecomputeChunk, span.Max - lo + 1);
                }
            }

            // Record which thresholds the flags now carry.
            await _db.Database.ExecuteSqlRawAsync(
                "INSERT INTO SystemSettings (SettingKey, SettingValue, Description, Version, UpdatedAt, UpdatedByUserId) " +
                "VALUES ({0}, {1}, {2}, 1, {3}, NULL) " +
                "ON DUPLICATE KEY UPDATE SettingValue = VALUES(SettingValue), Version = Version + 1, UpdatedAt = VALUES(UpdatedAt)",
                new object[] { SettingKeys.AppliedStamp, t.Stamp, "Thresholds the stored member eligibility flags were last computed with.", DateTime.UtcNow }, ct);

            var message = changed == 0
                ? "Member eligibility already matched the thresholds; no member changed."
                : $"Member eligibility was recalculated: {impact.BecomingEligible} became eligible, {impact.BecomingIneligible} became ineligible ({changed} member record(s) updated).";

            await _audit.WriteAsync(AuditActions.RecomputeEligibility, AuditEntities.Settings, null,
                $"Thresholds {t.Stamp}: {processed} member id slot(s) scanned, {changed} record(s) updated, " +
                $"+{impact.BecomingEligible} eligible, -{impact.BecomingIneligible} ineligible.", ct: ct);
            await InvalidateCacheAsync(ct);

            return new EligibilityRecomputeResponse
            {
                Processed = impact.ActiveMembers,
                Changed = changed,
                BecameEligible = impact.BecomingEligible,
                BecameIneligible = impact.BecomingIneligible,
                Message = message
            };
        }

        public async Task InvalidateCacheAsync(CancellationToken ct)
        {
            try { await _cache.RemoveAsync(CacheKey, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not evict {Key}; other API instances may use the previous thresholds until the cache expires ({Ttl}).", CacheKey, CacheTtl);
            }
        }
    }
}
