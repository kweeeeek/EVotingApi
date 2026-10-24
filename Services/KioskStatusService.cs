using System.Collections.Concurrent;

namespace EVoting.Api.Services
{
    // ══════════════════════════════════════════════════════════════════════════
    //  KioskStatusService  (WebSocket upgrade)
    //
    //  Changes from the original polling-based version:
    //  ─────────────────────────────────────────────────
    //  • RecordHeartbeat() now also stores the SignalR ConnectionId so the
    //    hub can look up which kiosk belongs to a disconnecting connection.
    //  • RemoveByConnectionId() is new — called from ElectionHub.OnDisconnectedAsync
    //    to mark a kiosk offline and return its KioskId for the admin broadcast.
    //  • Everything else (ConcurrentDictionary, IsOnline timeout) is unchanged.
    //
    //  Heartbeat flow (old vs new):
    //  OLD: Kiosk  →  POST /api/v1/health/heartbeat  (HTTP, every 30 s)
    //  NEW: Kiosk  →  hub.KioskHeartbeat(kioskId)    (WebSocket frame, every 30 s)
    //
    //  Offline detection:
    //  OLD: Admin polls GET /api/v1/health/kiosks every N seconds
    //  NEW: OnDisconnectedAsync fires instantly → push to "admins" group
    // ══════════════════════════════════════════════════════════════════════════

    public interface IKioskStatusService
    {
        void RecordHeartbeat(string kioskId, string? ipAddress = null, string? connectionId = null);
        string? RemoveByConnectionId(string connectionId);
        IReadOnlyList<KioskStatus> GetAllStatuses(int timeoutSeconds = 90);
        KioskStatus? GetStatus(string kioskId, int timeoutSeconds = 90);
    }

    public class KioskStatus
    {
        public string   KioskId      { get; init; } = "";
        public string?  IpAddress    { get; init; }
        public string?  ConnectionId { get; init; }
        public DateTime LastSeen     { get; init; }
        public bool     IsOnline     { get; init; }
        public int      SecondsAgo   { get; init; }
    }

    public class KioskStatusService : IKioskStatusService
    {
        private record HeartbeatEntry(DateTime LastSeen, string? IpAddress, string? ConnectionId);

        private readonly ConcurrentDictionary<string, HeartbeatEntry> _heartbeats       = new();
        // Reverse lookup: ConnectionId → KioskId (for fast disconnect handling)
        private readonly ConcurrentDictionary<string, string>         _connToKiosk      = new();

        public void RecordHeartbeat(
            string kioskId, string? ipAddress = null, string? connectionId = null)
        {
            // Update (or remove old) reverse-lookup entry
            if (connectionId != null)
            {
                // Remove the old connectionId if this kiosk reconnected
                if (_heartbeats.TryGetValue(kioskId, out var old) &&
                    old.ConnectionId != null &&
                    old.ConnectionId != connectionId)
                {
                    _connToKiosk.TryRemove(old.ConnectionId, out _);
                }

                _connToKiosk[connectionId] = kioskId;
            }

            _heartbeats[kioskId] = new HeartbeatEntry(DateTime.UtcNow, ipAddress, connectionId);
        }

        /// <summary>
        /// Called from ElectionHub.OnDisconnectedAsync.
        /// Removes the kiosk entry that matches the given SignalR ConnectionId.
        /// Returns the KioskId so the hub can notify admins, or null if not found.
        /// </summary>
        public string? RemoveByConnectionId(string connectionId)
        {
            if (!_connToKiosk.TryRemove(connectionId, out var kioskId))
                return null;

            _heartbeats.TryRemove(kioskId, out _);
            return kioskId;
        }

        public IReadOnlyList<KioskStatus> GetAllStatuses(int timeoutSeconds = 90)
        {
            var now = DateTime.UtcNow;
            return _heartbeats
                .Select(kvp => BuildStatus(kvp.Key, kvp.Value, now, timeoutSeconds))
                .OrderBy(s => s.KioskId)
                .ToList();
        }

        public KioskStatus? GetStatus(string kioskId, int timeoutSeconds = 90)
        {
            if (!_heartbeats.TryGetValue(kioskId, out var entry)) return null;
            return BuildStatus(kioskId, entry, DateTime.UtcNow, timeoutSeconds);
        }

        private static KioskStatus BuildStatus(
            string kioskId, HeartbeatEntry entry, DateTime now, int timeoutSeconds)
        {
            int secondsAgo = (int)(now - entry.LastSeen).TotalSeconds;
            return new KioskStatus
            {
                KioskId      = kioskId,
                IpAddress    = entry.IpAddress,
                ConnectionId = entry.ConnectionId,
                LastSeen     = entry.LastSeen,
                IsOnline     = secondsAgo <= timeoutSeconds,
                SecondsAgo   = secondsAgo
            };
        }
    }
}
