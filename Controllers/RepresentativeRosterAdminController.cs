using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// The candidate roster of ONE Representative election: who is on the ballot, at which position, and how many slots are left.
    /// The roster is shared by every voter and branch — there is no branch filter. Changes are possible only while the election is being
    /// prepared (never opened, no ballots); the API refuses them otherwise, whatever the client shows.
    ///   CANDIDATE_VIEW / EVENT_VIEW (read) · CANDIDATE_ADD (assign) · CANDIDATE_DELETE (remove from roster) · EVENT_EDIT (unlock).
    /// </summary>
    [Route("api/v1/admin/events/{eventId:int}/representative-roster")]
    public class RepresentativeRosterAdminController : AdminControllerBase
    {
        private readonly IRepresentativeRosterService _svc;

        public RepresentativeRosterAdminController(IRepresentativeRosterService svc) => _svc = svc;

        /// <summary>Per-position counts, free slots, lock state, readiness problems and the roster fingerprint.</summary>
        [HttpGet("summary")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.EventView)]
        public async Task<IActionResult> Summary(int eventId, CancellationToken ct) => FromResult(await _svc.SummaryAsync(eventId, ct));

        [HttpGet]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.EventView)]
        public async Task<IActionResult> List(
            int eventId, [FromQuery] string? search, [FromQuery] string? position, [FromQuery] string? status,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
            FromResult(await _svc.ListRosterAsync(eventId, search, position, status, page, pageSize, ct));

        /// <summary>Every roster row with the full profile (no images) for the Admin's Excel export.</summary>
        [HttpGet("export")]
        [RequirePermission(PermissionCodes.ReportExport, PermissionCodes.CandidateView)]
        public async Task<IActionResult> Export(int eventId, CancellationToken ct) => FromResult(await _svc.ExportRosterAsync(eventId, ct));

        [HttpPost]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Assign(int eventId, [FromBody] RepAssignRequest req, CancellationToken ct) =>
            FromResult(await _svc.AssignAsync(eventId, req, ct));

        /// <summary>Removes the ASSIGNMENT only; the candidate's profile stays available for other elections.</summary>
        [HttpDelete("{assignmentId:int}")]
        [RequirePermission(PermissionCodes.CandidateDelete)]
        public async Task<IActionResult> Remove(int eventId, int assignmentId, CancellationToken ct) =>
            FromResult(await _svc.RemoveAsync(eventId, assignmentId, ct));

        /// <summary>Re-opens roster editing for an election that was opened by mistake: closed, never any ballot. Audited with the reason.</summary>
        [HttpPost("unlock")]
        [RequirePermission(PermissionCodes.EventEdit)]
        public async Task<IActionResult> Unlock(int eventId, [FromBody] RepRosterUnlockRequest req, CancellationToken ct) =>
            FromResult(await _svc.UnlockAsync(eventId, req, ct));
    }
}
