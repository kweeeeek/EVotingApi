namespace EVoting.Api.Models.Responses
{
    // ══════════════════════════════════════════════════════════════════════════
    //  Monitoring DTOs
    //
    //  Wire-format response shapes for GET /api/v1/monitoring/*.
    //  All properties serialize camelCase (see the JsonSerializerOptions
    //  configured in Program.cs), so these C# PascalCase names become e.g.
    //  "generatedAtUtc", "cpuUsagePercent", etc. on the wire.
    //
    //  Nullable fields (`double?`, `long?`) mean "not available right now" —
    //  e.g. CPU% needs a second sample before it can report a rate, and MySQL
    //  QPS needs two polls to compute a delta. The dashboard renders these as
    //  "—" until a real value shows up.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>API process / runtime / OS metrics — GET /api/v1/monitoring/system</summary>
    public class SystemMonitoringSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public double UptimeSeconds { get; set; }
        public int ProcessId { get; set; }
        public string MachineName { get; set; } = "";
        public string OsDescription { get; set; } = "";
        public string DotnetVersion { get; set; } = "";
        public int ProcessorCount { get; set; }

        /// <summary>Null on the first sample after process start — needs a second sample to compute a delta.</summary>
        public double? CpuUsagePercent { get; set; }

        public long WorkingSetBytes { get; set; }
        public long ManagedMemoryBytes { get; set; }
        public int GcGen0Collections { get; set; }
        public int GcGen1Collections { get; set; }
        public int GcGen2Collections { get; set; }
        public int ThreadCount { get; set; }

        public long? DiskTotalBytes { get; set; }
        public long? DiskFreeBytes { get; set; }
        public string? DiskPath { get; set; }
    }

    public class TableSizeInfo
    {
        public string Name { get; set; } = "";
        public long ApproxRows { get; set; }
        public long SizeBytes { get; set; }
    }

    public class ActiveQueryInfo
    {
        public long Id { get; set; }
        public string User { get; set; } = "";
        public string Host { get; set; } = "";
        public string? Database { get; set; }
        public string Command { get; set; } = "";
        public long TimeSeconds { get; set; }
        public string State { get; set; } = "";

        /// <summary>Truncated query text (SHOW FULL PROCESSLIST → Info column). May contain parameter values — Admin-only.</summary>
        public string? InfoSnippet { get; set; }
    }

    /// <summary>MySQL connection / performance / storage metrics — GET /api/v1/monitoring/database</summary>
    public class DatabaseMonitoringSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public bool IsConnected { get; set; }
        public double PingMs { get; set; }
        public string? ConnectionError { get; set; }
        public string? ServerVersion { get; set; }
        public string? DatabaseName { get; set; }

        public double? UptimeSeconds { get; set; }
        public long? ThreadsConnected { get; set; }
        public long? ThreadsRunning { get; set; }
        public long? MaxConnections { get; set; }
        public double? ConnectionUsagePercent { get; set; }

        /// <summary>Null until two polls have happened — this is a delta, not a lifetime average.</summary>
        public double? QueriesPerSecond { get; set; }

        public long? SlowQueriesTotal { get; set; }
        public long? SlowQueriesDelta { get; set; }

        public long? ComSelectTotal { get; set; }
        public long? ComInsertTotal { get; set; }
        public long? ComUpdateTotal { get; set; }
        public long? ComDeleteTotal { get; set; }

        public double? InnodbBufferPoolHitRatioPercent { get; set; }

        public long? TotalSizeBytes { get; set; }
        public List<TableSizeInfo> Tables { get; set; } = new();

        public List<ActiveQueryInfo> ActiveQueries { get; set; } = new();

        /// <summary>Set when the process list couldn't be read (usually a missing PROCESS privilege) — informational, not an error.</summary>
        public string? ProcessListNote { get; set; }
    }

    public class EndpointStat
    {
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public long Hits { get; set; }
        public double AvgMs { get; set; }
        public double MaxMs { get; set; }
    }

    public class RecentErrorInfo
    {
        public DateTime AtUtc { get; set; }
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public int StatusCode { get; set; }
        public string Message { get; set; } = "";
    }

    public class SlowRequestInfo
    {
        public DateTime AtUtc { get; set; }
        public string Method { get; set; } = "";
        public string Path { get; set; } = "";
        public int StatusCode { get; set; }
        public double ElapsedMs { get; set; }
    }

    /// <summary>API traffic / error / latency metrics — GET /api/v1/monitoring/requests</summary>
    public class RequestMetricsSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public long TotalRequests { get; set; }
        public double RequestsPerMinute { get; set; }

        /// <summary>Keys are "2xx" / "3xx" / "4xx" / "5xx".</summary>
        public Dictionary<string, long> StatusClassCounts { get; set; } = new();

        public double ErrorRatePercent { get; set; }
        public double? AverageResponseMs { get; set; }
        public double? P95ResponseMs { get; set; }

        public List<EndpointStat> TopEndpoints { get; set; } = new();
        public List<RecentErrorInfo> RecentErrors { get; set; } = new();
        public List<SlowRequestInfo> SlowRequests { get; set; } = new();
    }

    /// <summary>Nginx stub_status metrics — GET /api/v1/monitoring/nginx (optional; see nginx.conf)</summary>
    public class NginxStatusSnapshot
    {
        public DateTime GeneratedAtUtc { get; set; }
        public bool IsAvailable { get; set; }
        public string? Error { get; set; }

        public long? ActiveConnections { get; set; }
        public long? Accepts { get; set; }
        public long? Handled { get; set; }
        public long? Requests { get; set; }
        public long? Reading { get; set; }
        public long? Writing { get; set; }
        public long? Waiting { get; set; }
    }

    /// <summary>Combined snapshot — GET /api/v1/monitoring/overview</summary>
    public class MonitoringOverviewResponse
    {
        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>"healthy" | "degraded" | "unhealthy" — a simple heuristic, see MonitoringController.GetOverview.</summary>
        public string Status { get; set; } = "healthy";

        public SystemMonitoringSnapshot System { get; set; } = new();
        public DatabaseMonitoringSnapshot Database { get; set; } = new();
        public RequestMetricsSnapshot Requests { get; set; } = new();
        public NginxStatusSnapshot Nginx { get; set; } = new();
    }
}
