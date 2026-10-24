using System.Diagnostics;
using System.Runtime.InteropServices;
using EVoting.Api.Models.Responses;

namespace EVoting.Api.Services
{
    public interface ISystemMonitoringService
    {
        SystemMonitoringSnapshot GetSnapshot();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  SystemMonitoringService
    //
    //  Process / .NET runtime / OS metrics for the API's own host machine —
    //  CPU%, memory, GC counters, thread count, disk space.
    //
    //  Registered as a Singleton so CPU% sampling can keep a baseline between
    //  calls: CPU usage is a RATE (processor time consumed per wall-clock
    //  time), so a single sample can only ever report a cumulative total —
    //  two samples, a few seconds apart, are needed to compute a percentage.
    //  The first call after startup therefore returns CpuUsagePercent = null;
    //  every call after that returns the rate since the previous call.
    // ══════════════════════════════════════════════════════════════════════════
    public class SystemMonitoringService : ISystemMonitoringService
    {
        private readonly ILogger<SystemMonitoringService> _logger;
        private readonly DateTime _startedAtUtc;

        private readonly object _cpuSampleLock = new();
        private TimeSpan? _lastTotalProcessorTime;
        private DateTime _lastCpuSampleUtc;

        public SystemMonitoringService(ILogger<SystemMonitoringService> logger)
        {
            _logger = logger;

            // Prefer the real OS process start time; fall back to "when this
            // singleton was constructed" (effectively the same moment, just
            // in case Process.StartTime is ever unavailable).
            try
            {
                using var proc = Process.GetCurrentProcess();
                _startedAtUtc = proc.StartTime.ToUniversalTime();
            }
            catch (Exception ex)
            {
                _startedAtUtc = DateTime.UtcNow;
                _logger.LogDebug(ex, "Could not read process StartTime; using construction time instead.");
            }
        }

        public SystemMonitoringSnapshot GetSnapshot()
        {
            var now = DateTime.UtcNow;
            using var proc = Process.GetCurrentProcess();

            var snapshot = new SystemMonitoringSnapshot
            {
                GeneratedAtUtc = now,
                StartedAtUtc = _startedAtUtc,
                UptimeSeconds = Math.Max(0, (now - _startedAtUtc).TotalSeconds),
                ProcessId = Environment.ProcessId,
                MachineName = Environment.MachineName,
                OsDescription = RuntimeInformation.OSDescription,
                DotnetVersion = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                WorkingSetBytes = proc.WorkingSet64,
                ManagedMemoryBytes = GC.GetTotalMemory(false),
                GcGen0Collections = GC.CollectionCount(0),
                GcGen1Collections = GC.CollectionCount(1),
                GcGen2Collections = GC.CollectionCount(2),
                ThreadCount = proc.Threads.Count,
                CpuUsagePercent = SampleCpuUsagePercent(proc, now)
            };

            TryPopulateDiskInfo(snapshot);

            return snapshot;
        }

        private double? SampleCpuUsagePercent(Process proc, DateTime now)
        {
            try
            {
                var totalProcessorTime = proc.TotalProcessorTime;

                lock (_cpuSampleLock)
                {
                    double? result = null;

                    if (_lastTotalProcessorTime.HasValue)
                    {
                        var cpuDeltaMs = (totalProcessorTime - _lastTotalProcessorTime.Value).TotalMilliseconds;
                        var wallDeltaMs = (now - _lastCpuSampleUtc).TotalMilliseconds;

                        // Guard against a near-zero wall-clock delta (e.g. two
                        // requests landing within the same tick), which would
                        // otherwise produce a wildly inflated percentage.
                        if (wallDeltaMs > 200 && cpuDeltaMs >= 0)
                        {
                            var pct = cpuDeltaMs / (wallDeltaMs * Environment.ProcessorCount) * 100.0;
                            result = Math.Round(Math.Clamp(pct, 0, 100), 1);
                        }
                    }

                    _lastTotalProcessorTime = totalProcessorTime;
                    _lastCpuSampleUtc = now;

                    return result;
                }
            }
            catch (Exception ex)
            {
                // CPU sampling should never take the whole monitoring page down.
                _logger.LogDebug(ex, "CPU usage sampling failed.");
                return null;
            }
        }

        private void TryPopulateDiskInfo(SystemMonitoringSnapshot snapshot)
        {
            try
            {
                var root = Path.GetPathRoot(AppContext.BaseDirectory);
                if (string.IsNullOrEmpty(root)) return;

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;

                snapshot.DiskPath = root;
                snapshot.DiskTotalBytes = drive.TotalSize;
                snapshot.DiskFreeBytes = drive.AvailableFreeSpace;
            }
            catch (Exception ex)
            {
                // Non-fatal — e.g. an unusual hosting path or restricted
                // permissions. The rest of the snapshot is still useful.
                _logger.LogDebug(ex, "Disk usage lookup failed.");
            }
        }
    }
}
