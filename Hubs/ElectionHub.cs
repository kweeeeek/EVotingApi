using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace EVoting.Api.Hubs
{


    [Authorize]
    public class ElectionHub : Hub
    {
        private readonly IKioskStatusService      _kioskStatus;
        private readonly IPermissionService       _permissions;
        private readonly ILogger<ElectionHub>     _logger;

        // Group name constants — use these everywhere to avoid typos
        public const string AdminGroup  = "admins";
        public const string KioskGroup  = "kiosks";
        public static string EventGroup(int eventId) => $"event:{eventId}";

        // ── Server → client broadcasts (sent via IHubContext, not hub methods) ──
        // Not invoked from this class directly — see VotesController for where
        // each is actually sent. Listed here so a future reader doesn't have to
        // grep for SendAsync calls to know what a client can expect to receive.
        //
        //   KioskStatusChanged  → AdminGroup           (this file, OnConnectedAsync/OnDisconnectedAsync/KioskHeartbeat)
        //   TurnoutUpdated      → EventGroup(eventId)   (VotesController.PushLiveUpdatesAsync)
        //   LiveResultsUpdated  → AdminGroup            (VotesController.PushLiveUpdatesAsync)
        //   MemberVoted         → KioskGroup            (VotesController.PushMemberVotedAsync) —
        //                         { eventId, memberId } only, never candidate selections
        //
        // Who may receive what (the push counterpart of the REST rule): an administrator is put into AdminGroup,
        // and may subscribe to an election's turnout, only while he or she holds EVENT_VIEW — the permission that
        // guards every REST endpoint returning the same figures (running tallies, turnout, kiosk and election
        // status). The role claim in the token is not enough: it is given to every active account. The permission
        // is looked up when the connection is made (and when SubscribeToEvent is called); a connection that is
        // already open is not re-evaluated until it reconnects.

        /// <summary>The permission an administrator needs to receive the live pushes (see the comment above).</summary>
        public const string LivePushPermission = PermissionCodes.EventView;

        public ElectionHub(
            IKioskStatusService kioskStatus,
            IPermissionService permissions,
            ILogger<ElectionHub> logger)
        {
            _kioskStatus = kioskStatus;
            _permissions = permissions;
            _logger      = logger;
        }

        private async Task<bool> AdministratorMayViewEventsAsync()
        {
            var userId = Context.User == null ? null : RequirePermissionAttribute.UserIdOf(Context.User);
            if (userId == null) return false;
            return await _permissions.HasAnyAsync(userId.Value, new[] { LivePushPermission }, Context.ConnectionAborted);
        }

        // ── Connection lifecycle ──────────────────────────────────────────────

        public override async Task OnConnectedAsync()
        {
            var role    = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                       ?? Context.User?.FindFirst("role")?.Value
                       ?? "";
            var connId  = Context.ConnectionId;

            _logger.LogInformation(
                "SignalR connected: ConnectionId={ConnectionId} Role={Role}",
                connId, role);

            // Auto-join the right group based on JWT role
            switch (role)
            {
                case "Admin":
                    if (await AdministratorMayViewEventsAsync())
                        await Groups.AddToGroupAsync(connId, AdminGroup);
                    else
                        _logger.LogWarning(
                            "SignalR: ConnectionId={ConnectionId} is an administrator without {Permission}; the live pushes are not delivered to it",
                            connId, LivePushPermission);
                    break;

                case "Kiosk":
                    await Groups.AddToGroupAsync(connId, KioskGroup);
                    // Kiosk connection itself counts as a heartbeat
                    var kioskId = Context.User?.FindFirst("kioskId")?.Value ?? connId;
                    var ip      = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
                    _kioskStatus.RecordHeartbeat(kioskId, ip, connId);

                    // Notify admins that a kiosk just came online
                    await Clients.Group(AdminGroup).SendAsync("KioskStatusChanged",
                        _kioskStatus.GetStatus(kioskId));
                    break;
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var role    = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                       ?? Context.User?.FindFirst("role")?.Value
                       ?? "";
            var connId  = Context.ConnectionId;

            _logger.LogInformation(
                "SignalR disconnected: ConnectionId={ConnectionId} Role={Role} Error={Error}",
                connId, role, exception?.Message);

            if (role == "Kiosk")
            {
                // Mark kiosk offline in the status service and alert admins
                var kioskId = _kioskStatus.RemoveByConnectionId(connId);
                if (kioskId != null)
                {
                    await Clients.Group(AdminGroup).SendAsync("KioskStatusChanged",
                        new { kioskId, isOnline = false, disconnectedAt = DateTime.UtcNow });
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        // ── Kiosk → Server ────────────────────────────────────────────────────

        /// <summary>
        /// Kiosk calls this periodically (every 30 s) to confirm it is still alive.
        /// Replaces POST /api/v1/health/heartbeat.
        /// </summary>
        [Authorize(Roles = "Kiosk")]
        public async Task KioskHeartbeat(string kioskId)
        {
            var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
            _kioskStatus.RecordHeartbeat(kioskId, ip, Context.ConnectionId);

            // Push updated status to admins so the dashboard refreshes without polling
            var status = _kioskStatus.GetStatus(kioskId);
            await Clients.Group(AdminGroup).SendAsync("KioskStatusChanged", status);
        }

        // ── Client → Server (subscriptions) ──────────────────────────────────

        /// <summary>
        /// Subscribe to live turnout and results updates for a specific election.
        /// Admins and Kiosks may call this (an administrator only while holding EVENT_VIEW, like the REST endpoints for the same figures).
        /// </summary>
        [Authorize(Roles = "Admin,Kiosk,Voter")]
        public async Task SubscribeToEvent(int eventId)
        {
            if (Context.User?.IsInRole("Admin") == true && !await AdministratorMayViewEventsAsync())
                throw new HubException("You do not have permission to perform this action.");

            await Groups.AddToGroupAsync(Context.ConnectionId, EventGroup(eventId));
            _logger.LogDebug(
                "ConnectionId={ConnectionId} subscribed to event:{EventId}",
                Context.ConnectionId, eventId);
        }

        /// <summary>
        /// Unsubscribe from live updates for a specific election.
        /// </summary>
        public async Task UnsubscribeFromEvent(int eventId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, EventGroup(eventId));
        }
    }
}
