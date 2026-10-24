using System.Collections.Concurrent;
using EVoting.Api.Models.Responses;

namespace EVoting.Api.Services
{
    public interface IRequestMetricsService
    {
        void RecordRequest(string method, string routeKey, int statusCode, double elapsedMs);
        void RecordError(string method, string path, int statusCode, string message);
        void RecordSlowRequest(string method, string path, int statusCode, double elapsedMs);
        RequestMetricsSnapshot GetSnapshot();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  RequestMetricsService
    //
    //  In-memory request/error counters that power the "API" section of the
    //  monitoring page. Deliberately lightweight — no external metrics
    //  pipeline, no unbounded growth:
    //    • per-endpoint stats are keyed by ROUTE TEMPLATE, not raw path, and
    //      capped at MaxTrackedEndpoints so an id-in-the-path route can't
    //      explode the dictionary (see RequestMetricsMiddleware.ResolveRouteKey)
    //    • recent-errors / slow-requests / latency-sample lists are capped
    //      ring buffers (oldest entry drops off when full)
    //    • the "requests in the last 5 minutes" queue is trimmed on every
    //      write, so it never grows past ~5 minutes of traffic
    //
    //  Registered as a Singleton (one instance for the process lifetime) —
    //  see RequestMetricsMiddleware, which is the only writer, and
    //  ExceptionHandlerMiddleware, which additionally calls RecordError with
    //  the real (unsanitized) exception detail for admins to see.
    //
    //  Note: paths under /api/v1/monitoring, /api/v1/health, /health and
    //  /hubs are excluded upstream in RequestMetricsMiddleware — the first
    //  three so the dashboard's own polling doesn't dominate the stats, the
    //  last because SignalR connections are long-lived and would otherwise
    //  report multi-hour "response times".
    // ══════════════════════════════════════════════════════════════════════════
    public class RequestMetricsService : IRequestMetricsService
    {
        private const int MaxTrackedEndpoints = 300;
        private const int RecentLatencySampleCap = 500;
        private const int RecentErrorCap = 25;
        private const int SlowRequestCap = 25;
        private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(5);

        private long _totalRequests;
        private readonly ConcurrentDictionary<string, long> _statusClassCounts = new();
        private readonly ConcurrentDictionary<string, EndpointBucket> _endpoints = new();
        private readonly ConcurrentQueue<double> _recentLatenciesMs = new();
        private readonly ConcurrentQueue<DateTime> _recentTimestamps = new();
        private readonly ConcurrentQueue<RecentErrorInfo> _recentErrors = new();
        private readonly ConcurrentQueue<SlowRequestInfo> _slowRequests = new();

        private sealed class EndpointBucket
        {
            public long Hits;
            public double TotalMs;
            public double MaxMs;
        }

        public void RecordRequest(string method, string routeKey, int statusCode, double elapsedMs)
        {
            Interlocked.Increment(ref _totalRequests);

            var statusClass = $"{statusCode / 100}xx";
            _statusClassCounts.AddOrUpdate(statusClass, 1, (_, current) => current + 1);

            var key = $"{method} {routeKey}";
            if (_endpoints.Count >= MaxTrackedEndpoints && !_endpoints.ContainsKey(key))
                key = "OTHER (endpoint limit reached)";

            var bucket = _endpoints.GetOrAdd(key, _ => new EndpointBucket());
            lock (bucket)
            {
                bucket.Hits++;
                bucket.TotalMs += elapsedMs;
                if (elapsedMs > bucket.MaxMs) bucket.MaxMs = elapsedMs;
            }

            EnqueueCapped(_recentLatenciesMs, elapsedMs, RecentLatencySampleCap);

            _recentTimestamps.Enqueue(DateTime.UtcNow);
            TrimOlderThan(_recentTimestamps, RateWindow);
        }

        public void RecordError(string method, string path, int statusCode, string message)
        {
            EnqueueCapped(_recentErrors, new RecentErrorInfo
            {
                AtUtc = DateTime.UtcNow,
                Method = method,
                Path = path,
                StatusCode = statusCode,
                Message = Truncate(message, 300)
            }, RecentErrorCap);
        }

        public void RecordSlowRequest(string method, string path, int statusCode, double elapsedMs)
        {
            EnqueueCapped(_slowRequests, new SlowRequestInfo
            {
                AtUtc = DateTime.UtcNow,
                Method = method,
                Path = path,
                StatusCode = statusCode,
                ElapsedMs = Math.Round(elapsedMs, 1)
            }, SlowRequestCap);
        }

        public RequestMetricsSnapshot GetSnapshot()
        {
            var now = DateTime.UtcNow;
            TrimOlderThan(_recentTimestamps, RateWindow);

            var total = Interlocked.Read(ref _totalRequests);
            var statusCounts = new Dictionary<string, long>(_statusClassCounts);

            long errorCount = statusCounts
                .Where(kv => kv.Key is "4xx" or "5xx")
                .Sum(kv => kv.Value);
            double errorRate = total == 0 ? 0 : Math.Round(errorCount * 100.0 / total, 2);

            double requestsPerMinute = _recentTimestamps.Count(t => (now - t) <= TimeSpan.FromMinutes(1));

            var latencies = _recentLatenciesMs.ToArray();
            double? avgMs = latencies.Length > 0 ? Math.Round(latencies.Average(), 1) : null;
            double? p95Ms = latencies.Length > 0 ? Math.Round(Percentile(latencies, 95), 1) : null;

            var topEndpoints = _endpoints
                .Select(ProjectEndpointStat)
                .OrderByDescending(e => e.Hits)
                .Take(10)
                .ToList();

            return new RequestMetricsSnapshot
            {
                GeneratedAtUtc = now,
                TotalRequests = total,
                RequestsPerMinute = requestsPerMinute,
                StatusClassCounts = statusCounts,
                ErrorRatePercent = errorRate,
                AverageResponseMs = avgMs,
                P95ResponseMs = p95Ms,
                TopEndpoints = topEndpoints,
                RecentErrors = _recentErrors.Reverse().ToList(),
                SlowRequests = _slowRequests.Reverse().ToList()
            };
        }

        private static EndpointStat ProjectEndpointStat(KeyValuePair<string, EndpointBucket> kv)
        {
            var parts = kv.Key.Split(' ', 2);
            long hits;
            double totalMs, maxMs;

            lock (kv.Value)
            {
                hits = kv.Value.Hits;
                totalMs = kv.Value.TotalMs;
                maxMs = kv.Value.MaxMs;
            }

            return new EndpointStat
            {
                Method = parts.Length > 0 ? parts[0] : "",
                Path = parts.Length > 1 ? parts[1] : kv.Key,
                Hits = hits,
                AvgMs = hits == 0 ? 0 : Math.Round(totalMs / hits, 1),
                MaxMs = Math.Round(maxMs, 1)
            };
        }

        /// <summary>Linear-interpolated percentile over an unsorted sample array (does not mutate the input).</summary>
        private static double Percentile(double[] samples, int percentile)
        {
            if (samples.Length == 0) return 0;
            var data = (double[])samples.Clone();
            Array.Sort(data);

            var rank = (percentile / 100.0) * (data.Length - 1);
            var low = (int)Math.Floor(rank);
            var high = (int)Math.Ceiling(rank);
            if (low == high) return data[low];

            var fraction = rank - low;
            return data[low] + (data[high] - data[low]) * fraction;
        }

        private static void EnqueueCapped<T>(ConcurrentQueue<T> queue, T item, int cap)
        {
            queue.Enqueue(item);
            while (queue.Count > cap) queue.TryDequeue(out _);
        }

        private static void TrimOlderThan(ConcurrentQueue<DateTime> queue, TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            while (queue.TryPeek(out var oldest) && oldest < cutoff)
                queue.TryDequeue(out _);
        }

        private static string Truncate(string value, int maxLength) =>
            value.Length <= maxLength ? value : value[..maxLength] + "…";
    }
}
