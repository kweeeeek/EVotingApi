using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Controllers
{
  
    [ApiController]
    [Route("api/v1/registration")]
    [Authorize(Roles = "Registration,Admin")]
    public class RegistrationController : ControllerBase
    {
        private readonly ApiDbContext _db;
        private readonly ILogger<RegistrationController> _logger;

        public RegistrationController(ApiDbContext db, ILogger<RegistrationController> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ── GET /api/v1/registration/members/search ───────────────────────────
        /// <summary>
        /// Search active members by name or account code, restricted to a set
        /// of branch IDs (the branches participating in the station's active
        /// event). Mirrors the filter that used to run directly against MySQL
        /// from EVoting.Registration exactly: IsActive members only, name or
        /// account-code substring match, capped at 200 rows, ordered by name.
        /// </summary>
        [HttpGet("members/search")]
        public async Task<IActionResult> SearchMembers(
            [FromQuery] string? query,
            [FromQuery] List<int>? branchIds,
            CancellationToken ct)
        {
            // Same 2-character floor the station's own search box already
            // enforces client-side — kept here too as defense in depth for
            // any other caller of this endpoint.
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                return Ok(Array.Empty<RegistrationMemberResponse>());

            if (branchIds == null || branchIds.Count == 0)
                return Ok(Array.Empty<RegistrationMemberResponse>());

            string q = query.Trim();

            var members = await _db.Members
                .AsNoTracking()
                .Include(m => m.Branch)
                .Where(m => m.IsActive
                    && branchIds.Contains(m.BranchId)
                    && (m.MemberName.Contains(q) || m.AccountCode.Contains(q)))
                .OrderBy(m => m.MemberName)
                .Take(200)
                .ToListAsync(ct);

            return Ok(members.Select(MapMember));
        }

        // ── GET /api/v1/registration/members/roster ────────────────────────────
        /// <summary>
        /// Full active-member roster for a set of branches — every member, not
        /// a name/code search match. Called once per sync cycle to populate the
        /// Registration Station's local offline cache (unlike SearchMembers,
        /// which the UI calls on every keystroke and deliberately caps results
        /// at 200). No query text, no row cap — the caller already knows which
        /// branches it wants (from the event it just fetched via
        /// GET /api/v1/events/current, whose response carries each branch's
        /// BranchId) and needs everyone in them, for offline search to work.
        /// </summary>
        [HttpGet("members/roster")]
        public async Task<IActionResult> GetMembersRoster(
            [FromQuery] List<int>? branchIds,
            CancellationToken ct)
        {
            if (branchIds == null || branchIds.Count == 0)
                return Ok(Array.Empty<RegistrationMemberResponse>());

            var members = await _db.Members
                .AsNoTracking()
                .Include(m => m.Branch)
                .Where(m => m.IsActive && branchIds.Contains(m.BranchId))
                .OrderBy(m => m.MemberName)
                .ToListAsync(ct);

            _logger.LogInformation(
                "Registration roster served: BranchCount={BranchCount} MemberCount={Count}",
                branchIds.Count, members.Count);

            return Ok(members.Select(MapMember));
        }

        // ── POST /api/v1/registration/members/{id}/qr-issued ──────────────────
        /// <summary>
        /// Stamps QrIssuedAt = now for a member, called once the Registration
        /// Station has generated their voting QR code. Read by the Admin app's
        /// "QR Code Issued" tab. Mirrors the old direct-DB write exactly —
        /// always overwrites with the latest timestamp (last-issued, not
        /// first-issued semantics).
        /// </summary>
        [HttpPost("members/{id:int}/qr-issued")]
        public async Task<IActionResult> MarkQrIssued(int id, CancellationToken ct)
        {
            var member = await _db.Members.FindAsync(new object[] { id }, ct);
            if (member == null)
                return NotFound(new ApiError { Error = "Member not found." });

            member.QrIssuedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("QR issued stamped: MemberId={MemberId}", id);

            return Ok(new { message = "QR issuance recorded.", memberId = id, member.QrIssuedAt });
        }

        // ── POST /api/v1/registration/members/qr-issued-bulk ────────────────────
        /// <summary>
        /// Bulk version of the single-item endpoint above, for a Registration
        /// Station catching up after a period offline. Unlike vote-sync, there
        /// is no meaningful "conflict" to detect here: QrIssuedAt is a plain
        /// "most recently issued at" timestamp (see MarkQrIssued's own doc
        /// comment above — it already just overwrites, no history kept), so
        /// this does the same thing per item, using each item's own
        /// IssuedAtUtc rather than "now" — that's when the QR was actually
        /// generated on the station, which could be hours before this sync
        /// request lands. A memberId this API doesn't recognize (deactivated
        /// or removed since the station's roster was last refreshed) is
        /// reported back as failed and is NOT retried by the station — see
        /// EVoting.Registration's RegistrationSyncBackgroundService.
        /// </summary>
        [HttpPost("members/qr-issued-bulk")]
        public async Task<IActionResult> MarkQrIssuedBulk(
            [FromBody] QrIssuanceBulkRequest req, CancellationToken ct)
        {
            var succeeded = new List<int>();
            var failed = new List<int>();

            foreach (var item in req.Items)
            {
                var member = await _db.Members.FindAsync(new object[] { item.MemberId }, ct);
                if (member == null)
                {
                    failed.Add(item.MemberId);
                    continue;
                }
                member.QrIssuedAt = item.IssuedAtUtc;
                succeeded.Add(item.MemberId);
            }

            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Registration bulk QR-issuance sync: Succeeded={Succeeded} Failed={Failed}",
                succeeded.Count, failed.Count);

            return Ok(new QrIssuanceBulkResponse
            {
                SucceededMemberIds = succeeded,
                FailedMemberIds = failed
            });
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        private static RegistrationMemberResponse MapMember(
            Infrastructure.Entities.MemberEntity m) => new()
        {
            MemberId            = m.MemberId,
            AccountCode         = m.AccountCode,
            MemberName          = m.MemberName,
            MemberContactNumber = m.MemberContactNumber,
            MemberAddress       = m.MemberAddress,
            BranchId            = m.BranchId,
            Branch              = m.Branch?.BranchName ?? "",
            ShareCapital        = m.ShareCapital,
            SavingsDeposit      = m.SavingsDeposit,
            LoanStatus          = (int)m.LoanStatus,
            IsEligibleToVote    = m.IsEligibleToVote,
            VotingRemarks       = m.VotingRemarks,
            QrIssuedAt          = m.QrIssuedAt,
            IsActive            = m.IsActive
        };
    }
}
