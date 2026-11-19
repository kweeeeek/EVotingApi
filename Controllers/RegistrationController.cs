using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Services;
using EVoting.Api.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
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
            [FromQuery] int? eventId,
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

            var perEvent = eventId.HasValue ? await QrIssuedPerEventAsync(_db, eventId.Value, members.Select(m => m.MemberId).ToList(), ct) : null;
            return Ok(members.Select(m => MapMember(m, perEvent)));
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
            [FromQuery] int? eventId,
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

            var perEvent = eventId.HasValue ? await QrIssuedPerEventAsync(_db, eventId.Value, null, ct) : null;
            return Ok(members.Select(m => MapMember(m, perEvent)));
        }

        // ── POST /api/v1/registration/members/{id}/qr-issued ──────────────────
        /// <summary>
        /// Records that the Registration desk handed this member a QR slip FOR THE ELECTION THAT IS OPEN NOW: one row in QrIssuances
        /// (election, desk number, user, time) and, for older readers, Members.QrIssuedAt = now.
        /// Optional body { eventId, deskNo }: eventId must be the open election (409 EVENT_CHANGED otherwise, so a page that still
        /// shows an earlier election does not record against the wrong one); deskNo is the desk 1-30 chosen at sign-in.
        /// </summary>
        [HttpPost("members/{id:int}/qr-issued")]
        public async Task<IActionResult> MarkQrIssued(
            int id,
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] QrIssueRequest? req,
            CancellationToken ct)
        {
            if (req?.DeskNo is int d && !ValidDesk(d))
                return BadRequest(new ApiError { Error = $"Desk must be a number from 1 to {MaxDeskNo}.", Code = "INVALID_DESK" });

            var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Status == "Opened" && e.IsActive, ct);
            if (ev == null)
                return Conflict(new ApiError { Error = "No election event is currently open.", Code = "NO_OPEN_EVENT" });
            if (req?.EventId is int asked && asked != ev.EventId)
                return Conflict(new ApiError
                {
                    Error = "The open election has changed. Reload the page before issuing.",
                    Detail = $"Asked for event {asked}; the open event is {ev.EventId}.",
                    Code = "EVENT_CHANGED"
                });

            var member = await _db.Members.FindAsync(new object[] { id }, ct);
            if (member == null)
                return NotFound(new ApiError { Error = "Member not found." });

            var now = DateTime.UtcNow;
            member.QrIssuedAt = now;
            _db.QrIssuances.Add(new QrIssuanceEntity
            {
                EventId = ev.EventId,
                MemberId = id,
                DeskNo = req?.DeskNo,
                IssuedBy = Issuer(),
                IssuedAtUtc = now,
                RecordedAtUtc = now,
                Offline = false,
            });
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("QR issued: MemberId={MemberId} EventId={EventId} Desk={Desk}", id, ev.EventId, req?.DeskNo);

            return Ok(new { message = "QR issuance recorded.", memberId = id, qrIssuedAt = TimeConv.AsUtc(now), eventId = ev.EventId, deskNo = req?.DeskNo });
        }

        // ── GET /api/v1/registration/qr-issuances ──────────────────────────────
        /// <summary>
        /// The QR slips ONE DESK issued in ONE ELECTION, newest first (what the desk's "Issued at this desk" list shows). Read from the
        /// server, so it survives a reload and a change of PC. Every Registration user may read any desk number: the desk is chosen by
        /// the user at sign-in, it is not a security boundary.
        /// </summary>
        [HttpGet("qr-issuances")]
        public async Task<IActionResult> DeskIssuances(
            [FromQuery] int eventId, [FromQuery] int deskNo, [FromQuery] int take = 1000, CancellationToken ct = default)
        {
            if (!ValidDesk(deskNo))
                return BadRequest(new ApiError { Error = $"Desk must be a number from 1 to {MaxDeskNo}.", Code = "INVALID_DESK" });
            take = Math.Clamp(take, 1, 5000);

            var q = from i in _db.QrIssuances.AsNoTracking()
                    where i.EventId == eventId && i.DeskNo == deskNo
                    join m in _db.Members.AsNoTracking() on i.MemberId equals m.MemberId
                    select new { i, m.AccountCode, m.MemberName, Branch = m.Branch != null ? m.Branch.BranchName : "" };

            int total = await q.CountAsync(ct);
            int memberCount = await q.Select(x => x.i.MemberId).Distinct().CountAsync(ct);
            var rows = await q.OrderByDescending(x => x.i.IssuedAtUtc).ThenByDescending(x => x.i.QrIssuanceId).Take(take).ToListAsync(ct);

            return Ok(new DeskIssuancesResponse
            {
                EventId = eventId,
                DeskNo = deskNo,
                Total = total,
                MemberCount = memberCount,
                Items = rows.Select(x => new DeskIssuanceRow
                {
                    MemberId = x.i.MemberId,
                    AccountCode = x.AccountCode,
                    MemberName = x.MemberName,
                    Branch = x.Branch,
                    IssuedAtUtc = TimeConv.AsUtc(x.i.IssuedAtUtc),
                    IssuedBy = x.i.IssuedBy,
                    Offline = x.i.Offline,
                }).ToList()
            });
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
            var now = DateTime.UtcNow;
            var issuer = Issuer();

            // Elections the items may belong to: the ones they name, plus the fallbacks for an item queued by an older page (no eventId).
            var namedIds = req.Items.Where(i => i.EventId.HasValue).Select(i => i.EventId!.Value).Distinct().ToList();
            var events = await _db.Events.AsNoTracking()
                .Where(e => namedIds.Contains(e.EventId) || e.IsActive)
                .Select(e => new { e.EventId, e.Status, e.IsActive, e.ScheduleFrom, e.ScheduleTo })
                .ToListAsync(ct);
            var known = events.Select(e => e.EventId).ToHashSet();
            var open = events.FirstOrDefault(e => e.Status == "Opened" && e.IsActive);

            int? EventFor(QrIssuanceBulkItemRequest item)
            {
                if (item.EventId is int id) return known.Contains(id) ? id : null; // null: names an election that was deleted
                var at = DateTime.SpecifyKind(item.IssuedAtUtc, DateTimeKind.Utc);
                var inWindow = events.Where(e => e.IsActive && ElectionClock.IsWithinWindow(e.ScheduleFrom, e.ScheduleTo, at))
                                     .OrderByDescending(e => e.ScheduleFrom).FirstOrDefault();
                return inWindow?.EventId ?? open?.EventId;
            }

            foreach (var item in req.Items)
            {
                var member = await _db.Members.FindAsync(new object[] { item.MemberId }, ct);
                if (member == null)
                {
                    failed.Add(item.MemberId);
                    continue;
                }
                var issuedAt = DateTime.SpecifyKind(item.IssuedAtUtc, DateTimeKind.Utc);
                if (member.QrIssuedAt == null || member.QrIssuedAt < issuedAt) member.QrIssuedAt = issuedAt;

                var eventId = EventFor(item);
                if (eventId.HasValue)
                {
                    // A desk re-sends a batch whose answer it never got: the same member, election and time is stored once.
                    var lo = issuedAt.AddMilliseconds(-1);
                    var hi = issuedAt.AddMilliseconds(1);
                    int ev = eventId.Value;
                    bool already = _db.QrIssuances.Local.Any(q => q.EventId == ev && q.MemberId == item.MemberId
                                       && q.IssuedAtUtc >= lo && q.IssuedAtUtc <= hi)
                                   || await _db.QrIssuances.AnyAsync(q => q.EventId == ev && q.MemberId == item.MemberId
                                       && q.IssuedAtUtc >= lo && q.IssuedAtUtc <= hi, ct);
                    if (!already)
                        _db.QrIssuances.Add(new QrIssuanceEntity
                        {
                            EventId = ev,
                            MemberId = item.MemberId,
                            DeskNo = item.DeskNo is int d && ValidDesk(d) ? d : null,
                            IssuedBy = BulkIssuer(item.IssuedBy, issuer),
                            IssuedAtUtc = issuedAt,
                            RecordedAtUtc = now,
                            Offline = true,
                        });
                }
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

        // ── POST /api/v1/registration/members/{id}/voting-pass ─────────────────
        /// <summary>
        /// Issues the member's VOTING PASS for the election that is open now and returns it once, to be printed in the QR code of
        /// the Registration slip. The account code stays the member's identity; the pass proves the slip came from this desk.
        /// <list type="bullet">
        /// <item>Same checks the kiosk makes: an open election, the member active, eligible, in a participating branch, not yet voted.</item>
        /// <item>A pass issued earlier to the same member for the same election is revoked (a lost or reprinted slip: only the newest works).</item>
        /// <item>Only the SHA-256 of the pass is stored; the pass itself is never logged or audited.</item>
        /// <item>Also stamps QrIssuedAt, so this call replaces <c>qr-issued</c> for a station that uses passes.</item>
        /// </list>
        /// </summary>
        [HttpPost("members/{id:int}/voting-pass")]
        public async Task<IActionResult> IssueVotingPass(
            int id, [FromServices] IAuditService audit,
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] QrIssueRequest? req,
            CancellationToken ct)
        {
            if (req?.DeskNo is int d && !ValidDesk(d))
                return BadRequest(new ApiError { Error = $"Desk must be a number from 1 to {MaxDeskNo}.", Code = "INVALID_DESK" });
            var ev = await _db.Events.AsNoTracking().Include(e => e.EventBranches)
                .FirstOrDefaultAsync(e => e.Status == "Opened" && e.IsActive, ct);
            if (ev == null)
                return Conflict(new ApiError { Error = "No election event is currently open.", Code = "NO_OPEN_EVENT" });

            var member = await _db.Members.Include(m => m.Branch).FirstOrDefaultAsync(m => m.MemberId == id && m.IsActive, ct);
            if (member == null)
                return NotFound(new ApiError { Error = "Member not found.", Code = "MEMBER_NOT_FOUND" });
            if (!member.IsEligibleToVote)
                return UnprocessableEntity(new ApiError { Error = "Member is not eligible to vote.", Detail = member.VotingRemarks, Code = "NOT_ELIGIBLE" });
            if (!ev.EventBranches.Any(eb => eb.BranchId == member.BranchId))
                return StatusCode(403, new ApiError
                {
                    Error = "Branch not assigned to this election.",
                    Detail = $"Branch '{member.Branch?.BranchName}' is not participating in this event.",
                    Code = "BRANCH_NOT_PARTICIPATING"
                });
            if (await _db.VoterLogs.AnyAsync(v => v.MemberId == id && v.EventId == ev.EventId && v.HasVoted, ct))
                return Conflict(new ApiError { Error = "Member has already voted in this election.", Code = "ALREADY_VOTED" });

            var now = DateTime.UtcNow;
            var issuer = Issuer();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Revoke what this member already holds for this election: only the newest slip works.
            int revoked = await _db.VotingPasses
                .Where(p => p.EventId == ev.EventId && p.MemberId == id && p.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), ct);

            var pass = VotingPass.Generate();
            _db.VotingPasses.Add(new VotingPassEntity
            {
                EventId = ev.EventId,
                MemberId = id,
                PassHash = VotingPass.Hash(pass),
                IssuedAt = now,
                IssuedBy = issuer,
            });
            member.QrIssuedAt = now;
            _db.QrIssuances.Add(new QrIssuanceEntity
            {
                EventId = ev.EventId, MemberId = id, DeskNo = req?.DeskNo, IssuedBy = issuer,
                IssuedAtUtc = now, RecordedAtUtc = now, Offline = false,
            });

            audit.Stage(AuditActions.IssueVotingPass, AuditEntities.Member, id,
                $"EventId={ev.EventId}, IssuedBy='{issuer}'" + (revoked > 0 ? $", revoked {revoked} earlier pass(es)." : "."));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation("Voting pass issued: MemberId={MemberId} EventId={EventId} By={Issuer} Revoked={Revoked}",
                id, ev.EventId, issuer, revoked);

            return Ok(new VotingPassResponse
            {
                MemberId = id,
                EventId = ev.EventId,
                VotingPass = pass,
                QrIssuedAt = now,
                RevokedEarlier = revoked
            });
        }

        // ── Private helpers ─────────────────────────────────────────────────────

        /// <summary>Registration desks are numbered 1..MaxDeskNo (chosen by the user at sign-in).</summary>
        internal const int MaxDeskNo = 30;
        internal static bool ValidDesk(int deskNo) => deskNo >= 1 && deskNo <= MaxDeskNo;

        private string Issuer()
        {
            var issuer = User.FindFirst("stationId")?.Value is { Length: > 0 } st ? $"station:{st}" : (User.Identity?.Name ?? "registration");
            return issuer.Length > 60 ? issuer.Substring(0, 60) : issuer;
        }

        /// <summary>
        /// Who issued an offline slip: the user the desk PC says (any user of that PC may be the one who sends the outbox), marked
        /// with the sender when it is someone else. Only letters, digits and . _ - @ are kept from the text the PC sent.
        /// </summary>
        internal static string BulkIssuer(string? claimed, string sender)
        {
            var who = new string((claimed ?? "").Trim().Where(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or '@').Take(30).ToArray());
            if (who.Length == 0 || string.Equals(who, sender, StringComparison.OrdinalIgnoreCase)) return sender;
            var s = $"{who} (sent by {sender})";
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        /// <summary>
        /// Latest QR time per member IN ONE ELECTION (from QrIssuances). <paramref name="memberIds"/> null = every member of the election.
        /// </summary>
        internal static async Task<Dictionary<int, DateTime>> QrIssuedPerEventAsync(
            ApiDbContext db, int eventId, IReadOnlyCollection<int>? memberIds, CancellationToken ct)
        {
            var q = db.QrIssuances.AsNoTracking().Where(i => i.EventId == eventId);
            if (memberIds != null) q = q.Where(i => memberIds.Contains(i.MemberId));
            var rows = await q.GroupBy(i => i.MemberId).Select(g => new { MemberId = g.Key, At = g.Max(i => i.IssuedAtUtc) }).ToListAsync(ct);
            return rows.ToDictionary(r => r.MemberId, r => r.At);
        }

        /// <summary>
        /// Like <see cref="MapMember(Infrastructure.Entities.MemberEntity)"/>, but QrIssuedAt is the member's QR IN ONE ELECTION
        /// (null when not issued in it). <paramref name="perEvent"/> null = the legacy Members.QrIssuedAt.
        /// </summary>
        internal static RegistrationMemberResponse MapMember(Infrastructure.Entities.MemberEntity m, IReadOnlyDictionary<int, DateTime>? perEvent)
        {
            var r = MapMember(m);
            if (perEvent != null) r.QrIssuedAt = perEvent.TryGetValue(m.MemberId, out var at) ? TimeConv.AsUtc(at) : null;
            return r;
        }

        internal static RegistrationMemberResponse MapMember(
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
