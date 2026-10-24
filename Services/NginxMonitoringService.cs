using EVoting.Api.Models.Responses;

namespace EVoting.Api.Services
{
    public interface INginxMonitoringService
    {
        Task<NginxStatusSnapshot> GetSnapshotAsync(CancellationToken ct);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  NginxMonitoringService
    //
    //  Reads Nginx's built-in stub_status module (see the loopback-only
    //  "/nginx_status" location added to nginx.conf) and parses its
    //  plain-text output into structured fields for the monitoring page.
    //
    //  This is entirely optional. If stub_status isn't enabled, or Nginx
    //  isn't reachable at the configured URL, IsAvailable is false and every
    //  other field is null — the rest of the monitoring page is unaffected.
    // ══════════════════════════════════════════════════════════════════════════
    public class NginxMonitoringService : INginxMonitoringService
    {
        private readonly IHttpClientFactory _httpFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<NginxMonitoringService> _logger;

        public NginxMonitoringService(
            IHttpClientFactory httpFactory, IConfiguration config, ILogger<NginxMonitoringService> logger)
        {
            _httpFactory = httpFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<NginxStatusSnapshot> GetSnapshotAsync(CancellationToken ct)
        {
            var snapshot = new NginxStatusSnapshot { GeneratedAtUtc = DateTime.UtcNow };
            var url = _config["Monitoring:NginxStatusUrl"] ?? "http://127.0.0.1/nginx_status";

            try
            {
                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(3);

                var body = await client.GetStringAsync(url, ct);
                ParseStubStatus(body, snapshot);
                snapshot.IsAvailable = true;
            }
            catch (Exception ex)
            {
                // Expected if stub_status isn't configured yet — this is a
                // bonus panel, not a required one, so it degrades quietly.
                snapshot.IsAvailable = false;
                snapshot.Error = ex.Message;
                _logger.LogDebug(ex, "Monitoring: nginx stub_status unavailable at {Url}.", url);
            }

            return snapshot;
        }

        /// <summary>
        /// stub_status output looks like:
        ///   Active connections: 3
        ///   server accepts handled requests
        ///    481 481 632
        ///   Reading: 0 Writing: 1 Waiting: 2
        /// </summary>
        private static void ParseStubStatus(string body, NginxStatusSnapshot snapshot)
        {
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var line in lines)
            {
                if (line.StartsWith("Active connections:", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.ActiveConnections = ExtractFirstLong(line);
                }
                else if (line.StartsWith("Reading:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    snapshot.Reading = ExtractLabelledValue(parts, "Reading:");
                    snapshot.Writing = ExtractLabelledValue(parts, "Writing:");
                    snapshot.Waiting = ExtractLabelledValue(parts, "Waiting:");
                }
                else
                {
                    // The "server accepts handled requests" header line is
                    // followed by a data line with exactly those 3 numbers —
                    // rather than depend on line ordering, just look for any
                    // line that's purely 3 whitespace-separated integers.
                    var numbers = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Where(part => long.TryParse(part, out _))
                        .Select(long.Parse)
                        .ToArray();

                    if (numbers.Length == 3)
                    {
                        snapshot.Accepts = numbers[0];
                        snapshot.Handled = numbers[1];
                        snapshot.Requests = numbers[2];
                    }
                }
            }
        }

        private static long? ExtractFirstLong(string line)
        {
            var digits = new string(line.Where(char.IsDigit).ToArray());
            return long.TryParse(digits, out var value) ? value : null;
        }

        private static long? ExtractLabelledValue(string[] parts, string label)
        {
            var index = Array.IndexOf(parts, label);
            if (index < 0 || index + 1 >= parts.Length) return null;
            return long.TryParse(parts[index + 1], out var value) ? value : null;
        }
    }
}
