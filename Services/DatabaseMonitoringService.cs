using System.Data.Common;
using System.Diagnostics;
using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Responses;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EVoting.Api.Services
{
    public interface IDatabaseMonitoringService
    {
        Task<DatabaseMonitoringSnapshot> GetSnapshotAsync(CancellationToken ct);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  DatabaseMonitoringService
    //
    //  MySQL connection / performance / storage metrics for the monitoring
    //  page. EF Core has no LINQ surface for "SHOW STATUS" or
    //  information_schema, so this runs plain ADO.NET commands on the SAME
    //  connection EF Core already manages for the request
    //  (Database.OpenConnectionAsync / GetDbConnection / CloseConnectionAsync
    //  — the documented pattern for mixing raw SQL into an EF Core DbContext).
    //
    //  Registered Scoped, matching ApiDbContext's lifetime.
    //
    //  Every metric is fetched defensively: a missing privilege or an
    //  unexpected server response degrades that ONE field to null/empty
    //  rather than failing the whole snapshot. The only thing that produces
    //  IsConnected = false is a failure to open the connection at all.
    // ══════════════════════════════════════════════════════════════════════════
    public class DatabaseMonitoringService : IDatabaseMonitoringService
    {
        private const string PreviousSampleCacheKey = "monitoring:mysql:previous-status";
        private static readonly TimeSpan MaxDeltaWindow = TimeSpan.FromMinutes(5);

        private readonly ApiDbContext _db;
        private readonly IMemoryCache _cache;
        private readonly ILogger<DatabaseMonitoringService> _logger;

        public DatabaseMonitoringService(
            ApiDbContext db, IMemoryCache cache, ILogger<DatabaseMonitoringService> logger)
        {
            _db = db;
            _cache = cache;
            _logger = logger;
        }

        // IMemoryCache (process-local), deliberately NOT the app's Redis-backed
        // IDistributedCache — this "previous sample" is only ever meaningful
        // to whichever process is about to compute the next delta, so there's
        // nothing to gain (and latency to lose) from a distributed cache here.
        private sealed record StatusSample(DateTime AtUtc, Dictionary<string, string> Values);

        public async Task<DatabaseMonitoringSnapshot> GetSnapshotAsync(CancellationToken ct)
        {
            var snapshot = new DatabaseMonitoringSnapshot { GeneratedAtUtc = DateTime.UtcNow };
            var sw = Stopwatch.StartNew();

            try
            {
                await _db.Database.OpenConnectionAsync(ct);
            }
            catch (Exception ex)
            {
                sw.Stop();
                snapshot.IsConnected = false;
                snapshot.PingMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                snapshot.ConnectionError = ex.Message;
                _logger.LogWarning(ex, "Monitoring: could not connect to MySQL.");
                return snapshot;
            }

            sw.Stop();
            snapshot.IsConnected = true;
            snapshot.PingMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1);

            try
            {
                var connection = _db.Database.GetDbConnection();
                snapshot.DatabaseName = connection.Database;

                var status = await ReadKeyValueAsync(connection, "SHOW GLOBAL STATUS", ct);
                var variables = await ReadKeyValueAsync(
                    connection,
                    "SHOW VARIABLES WHERE Variable_name IN ('max_connections','version')",
                    ct);

                ApplyStatusAndVariables(snapshot, status, variables);
                ApplyRateMetrics(snapshot, status);

                await PopulateTableSizesAsync(connection, snapshot, ct);
                await PopulateActiveQueriesAsync(connection, snapshot, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monitoring: partial failure reading MySQL metrics.");
                snapshot.ConnectionError = $"Connected, but some metrics could not be read: {ex.Message}";
            }
            finally
            {
                await _db.Database.CloseConnectionAsync();
            }

            return snapshot;
        }

        private static async Task<Dictionary<string, string>> ReadKeyValueAsync(
            DbConnection connection, string sql, CancellationToken ct)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = reader.GetString(0);
                var value = reader.IsDBNull(1) ? "" : reader.GetString(1);
                result[key] = value;
            }

            return result;
        }

        private static long? ParseLong(Dictionary<string, string> map, string key) =>
            map.TryGetValue(key, out var raw) && long.TryParse(raw, out var value) ? value : null;

        private static void ApplyStatusAndVariables(
            DatabaseMonitoringSnapshot snapshot,
            Dictionary<string, string> status,
            Dictionary<string, string> variables)
        {
            snapshot.ServerVersion = variables.TryGetValue("version", out var version) ? version : null;
            snapshot.MaxConnections = ParseLong(variables, "max_connections");

            var uptime = ParseLong(status, "Uptime");
            snapshot.UptimeSeconds = uptime;

            snapshot.ThreadsConnected = ParseLong(status, "Threads_connected");
            snapshot.ThreadsRunning = ParseLong(status, "Threads_running");

            if (snapshot.ThreadsConnected.HasValue && snapshot.MaxConnections is > 0)
            {
                snapshot.ConnectionUsagePercent = Math.Round(
                    snapshot.ThreadsConnected.Value * 100.0 / snapshot.MaxConnections.Value, 1);
            }

            snapshot.SlowQueriesTotal = ParseLong(status, "Slow_queries");
            snapshot.ComSelectTotal = ParseLong(status, "Com_select");
            snapshot.ComInsertTotal = ParseLong(status, "Com_insert");
            snapshot.ComUpdateTotal = ParseLong(status, "Com_update");
            snapshot.ComDeleteTotal = ParseLong(status, "Com_delete");

            var bufferReads = ParseLong(status, "Innodb_buffer_pool_reads");
            var bufferReadRequests = ParseLong(status, "Innodb_buffer_pool_read_requests");
            if (bufferReads.HasValue && bufferReadRequests is > 0)
            {
                var hitRatio = 1.0 - (double)bufferReads.Value / bufferReadRequests.Value;
                snapshot.InnodbBufferPoolHitRatioPercent =
                    Math.Round(Math.Clamp(hitRatio, 0, 1) * 100.0, 2);
            }
        }

        /// <summary>
        /// MySQL's status counters (Queries, Slow_queries, ...) are cumulative
        /// since mysqld started, so a single sample only gives a total, not a
        /// rate. This stashes the previous sample in IMemoryCache and, if it's
        /// recent enough (between 1 second and MaxDeltaWindow old), computes a
        /// per-second delta — the same technique tools like
        /// `mysqladmin extended-status -i` use.
        /// </summary>
        private void ApplyRateMetrics(DatabaseMonitoringSnapshot snapshot, Dictionary<string, string> status)
        {
            var now = DateTime.UtcNow;
            var previous = _cache.Get<StatusSample>(PreviousSampleCacheKey);
            _cache.Set(PreviousSampleCacheKey, new StatusSample(now, status), MaxDeltaWindow);

            if (previous == null) return;

            var elapsedSeconds = (now - previous.AtUtc).TotalSeconds;
            if (elapsedSeconds < 1 || elapsedSeconds > MaxDeltaWindow.TotalSeconds) return;

            var currentQueries = ParseLong(status, "Queries");
            var previousQueries = ParseLong(previous.Values, "Queries");
            if (currentQueries.HasValue && previousQueries.HasValue && currentQueries >= previousQueries)
            {
                snapshot.QueriesPerSecond =
                    Math.Round((currentQueries.Value - previousQueries.Value) / elapsedSeconds, 1);
            }

            var currentSlow = ParseLong(status, "Slow_queries");
            var previousSlow = ParseLong(previous.Values, "Slow_queries");
            if (currentSlow.HasValue && previousSlow.HasValue && currentSlow >= previousSlow)
            {
                snapshot.SlowQueriesDelta = currentSlow.Value - previousSlow.Value;
            }
        }

        private static async Task PopulateTableSizesAsync(
            DbConnection connection, DatabaseMonitoringSnapshot snapshot, CancellationToken ct)
        {
            const string sql = @"
                SELECT table_name,
                       IFNULL(table_rows, 0) AS approx_rows,
                       IFNULL(data_length, 0) + IFNULL(index_length, 0) AS size_bytes
                FROM information_schema.tables
                WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE'
                ORDER BY size_bytes DESC";

            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            long totalBytes = 0;
            var tables = new List<TableSizeInfo>();
            while (await reader.ReadAsync(ct))
            {
                var name = reader.GetString(0);
                var rows = Convert.ToInt64(reader.GetValue(1));
                var bytes = Convert.ToInt64(reader.GetValue(2));

                totalBytes += bytes;
                if (tables.Count < 8)
                    tables.Add(new TableSizeInfo { Name = name, ApproxRows = rows, SizeBytes = bytes });
            }

            snapshot.Tables = tables;
            snapshot.TotalSizeBytes = totalBytes;
        }

        private static async Task PopulateActiveQueriesAsync(
            DbConnection connection, DatabaseMonitoringSnapshot snapshot, CancellationToken ct)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SHOW FULL PROCESSLIST";
                await using var reader = await cmd.ExecuteReaderAsync(ct);

                // Columns: Id, User, Host, db, Command, Time, State, Info
                var queries = new List<ActiveQueryInfo>();
                while (await reader.ReadAsync(ct))
                {
                    queries.Add(new ActiveQueryInfo
                    {
                        Id = Convert.ToInt64(reader.GetValue(0)),
                        User = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Host = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Database = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Command = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        TimeSeconds = reader.IsDBNull(5) ? 0 : Convert.ToInt64(reader.GetValue(5)),
                        State = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        InfoSnippet = Truncate(reader.IsDBNull(7) ? null : reader.GetValue(7)?.ToString(), 160)
                    });
                }

                snapshot.ActiveQueries = queries.OrderByDescending(q => q.TimeSeconds).Take(20).ToList();
            }
            catch (Exception ex)
            {
                // Most likely cause: the app's DB user lacks the PROCESS
                // privilege, in which case MySQL just returns fewer rows
                // rather than erroring — this catch is a backstop for the
                // rarer case (stricter server config, a timeout, etc.) where
                // it doesn't. Either way, this panel degrades gracefully
                // instead of failing the whole monitoring response.
                snapshot.ProcessListNote =
                    $"Active queries unavailable ({ex.Message}). " +
                    "Grant the PROCESS privilege to the app's DB user to see all sessions here.";
            }
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value[..maxLength] + "…";
        }
    }
}
