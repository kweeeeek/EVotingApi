using EVoting.Api.Infrastructure;
using EVoting.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Voter Lookup, paged ON THE SERVER.
    //
    //  The list used to read every voter log of the election and every matching member (with the photo column), sort them in memory,
    //  filter "voted / not voted" in memory and only then cut one page. That cost grows with the size of the membership, not with the page.
    //
    //  Now (three small queries, none of them reads a photo, an address or a phone number):
    //    1  COUNT(*)          of the members that match the filters                        -> TotalCount / TotalPages
    //    2  the MemberIds of the requested page only (ORDER BY name, id  LIMIT n OFFSET k)  -> a narrow, index-friendly query
    //    3  the page's rows:  members (+ branch name) and this election's voter logs, both by primary/unique key for those few ids
    //  Step 2 is a "deferred join": the expensive sort/skip runs on ids alone, and the wide rows are fetched only for the n ids kept.
    //
    //  Who is listed is unchanged: active, eligible members of the election's branches (optionally one branch). "Voted" means the
    //  voter log of THIS election says HasVoted. Search matches name, account code or - for this election only - the receipt code.
    //
    //  Two details keep it fast on a very large membership (measured on 1,000,000 members, see docs/representative-election):
    //    * the active / eligible / voted flags are compared with a query parameter (an equality, not a bare boolean) so MySQL can walk
    //      IX_Members_VoterLookup_Cover in name order and stop after one page, and count / search entirely inside that index (no member rows are read);
    //    * the receipt code is matched by a cheap look-up of this election's receipts FIRST, and only when the typed text could be part of
    //      a receipt code at all. A name such as "Santos" contains letters no receipt contains, so it never touches the receipts. When it
    //      could (digits, A-F, E, V, T, '-'), the few matching member ids join the filter as a short list instead of a per-member sub-query.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    public static class VoterLookupLimits
    {
        public const int DefaultPageSize = 50;
        /// <summary>
        /// Largest page served. The screen offers 25 / 50 / 100, but the previous API allowed 500 and an older Admin build still asks for 500 per page
        /// and counts its pages with that number, so the ceiling stays 500; a bigger request is cut to it (never rejected). The response echoes the size used.
        /// </summary>
        public const int MaxPageSize = 500;
        public const int MaxSearchLength = 100;
        /// <summary>If more members than this match the typed text through their receipt, the receipt test runs per member instead of from an id list (correct, just slower).</summary>
        public const int ReceiptIdListLimit = 200;
    }

    public readonly record struct PageWindow(int Page, int PageSize, long Offset);

    public static class VoterLookupPaging
    {
        /// <summary>page and pageSize must be 1 or more; a pageSize above the maximum is cut to the maximum.</summary>
        public static (PageWindow? Window, string? Field, string? Message) Normalize(int page, int pageSize)
        {
            if (page < 1) return (null, "page", "Page must be 1 or greater.");
            if (pageSize < 1) return (null, "pageSize", "Page size must be 1 or greater.");
            pageSize = Math.Min(pageSize, VoterLookupLimits.MaxPageSize);
            return (new PageWindow(page, pageSize, (long)(page - 1) * pageSize), null, null);
        }

        public static int TotalPages(int totalCount, int pageSize) =>
            totalCount <= 0 || pageSize <= 0 ? 0 : (int)(((long)totalCount + pageSize - 1) / pageSize);

        /// <summary>Trims the search text and cuts it to the longest value a name can have; blank means "no search".</summary>
        public static string? CleanSearch(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var s = raw.Trim();
            return s.Length > VoterLookupLimits.MaxSearchLength ? s.Substring(0, VoterLookupLimits.MaxSearchLength) : s;
        }

        /// <summary>
        /// True when every character of the text could occur in a receipt code (<c>EVT01-0000ABCD-DEADBEEF</c>: the letters E, V, T,
        /// the digits, A-F and '-'), ignoring case. Receipt codes only ever come from <see cref="ReceiptCodeFormat"/>, which enforces that
        /// alphabet, so any other text cannot match a receipt and the receipt look-up is skipped.
        /// </summary>
        public static bool CouldBePartOfReceiptCode(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var ch in text)
            {
                var c = char.ToUpperInvariant(ch);
                var inAlphabet = c is >= '0' and <= '9' or >= 'A' and <= 'F' or 'V' or 'T' or '-';
                if (!inAlphabet) return false;
            }
            return true;
        }
    }

    public interface IVoterLookupService
    {
        Task<ServiceResult<VoterLookupPageResponse>> QueryAsync(
            int eventId, string? search, int? branchId, bool? hasVoted, int page, int pageSize, CancellationToken ct);
    }

    public sealed class VoterLookupService : IVoterLookupService
    {
        private readonly ApiDbContext _db;

        public VoterLookupService(ApiDbContext db) => _db = db;

        public async Task<ServiceResult<VoterLookupPageResponse>> QueryAsync(
            int eventId, string? search, int? branchId, bool? hasVoted, int page, int pageSize, CancellationToken ct)
        {
            var (window, field, message) = VoterLookupPaging.Normalize(page, pageSize);
            if (window == null) return ServiceResult<VoterLookupPageResponse>.Invalid(field!, message!);
            var w = window.Value;

            var branchIds = await _db.Events.AsNoTracking().Where(e => e.EventId == eventId)
                .Select(e => e.EventBranches.Select(b => b.BranchId).ToList()).FirstOrDefaultAsync(ct);
            if (branchIds == null) return ServiceResult<VoterLookupPageResponse>.Failure(StatusCodes.Status404NotFound, $"Event {eventId} not found.");

            var text = VoterLookupPaging.CleanSearch(search);

            // Receipt matches of THIS election, looked up once and only when the text could be part of a receipt code.
            //   receiptMemberIds == null && !receiptPerMember : the text cannot match a receipt (or no receipt matched) -> name / code only
            List<int>? receiptMemberIds = null;
            var receiptPerMember = false;
            if (text != null && VoterLookupPaging.CouldBePartOfReceiptCode(text))
            {
                var found = await _db.VoterLogs.AsNoTracking()
                    .Where(v => v.EventId == eventId && v.ReceiptCode != null && v.ReceiptCode.Contains(text))
                    .OrderBy(v => v.MemberId)          // a LIMIT without ORDER BY is not deterministic (and EF warns about it)
                    .Select(v => v.MemberId).Take(VoterLookupLimits.ReceiptIdListLimit + 1).ToListAsync(ct);   // List<int>, never an array: C# 14 would pick the span overload, which EF cannot translate
                if (found.Count > VoterLookupLimits.ReceiptIdListLimit) receiptPerMember = true;
                else if (found.Count > 0) receiptMemberIds = found;
            }

            // The flags are compared with a variable on purpose. EF Core rewrites "col == true" to the bare column ("WHERE col"), which MySQL reads as "col <> 0":
            // a range, not an equality, so it can neither walk the index in name order nor use a (EventId, HasVoted, ...) index. A variable becomes a
            // query parameter ("col = @p") and stays an equality.
            var yes = true;

            // The filters, shared by the count and the page query.
            var q = _db.Members.AsNoTracking().Where(m => m.IsActive == yes && m.IsEligibleToVote == yes && branchIds.Contains(m.BranchId));
            if (branchId.HasValue) q = q.Where(m => m.BranchId == branchId.Value);
            if (text != null)
            {
                if (receiptPerMember)
                    q = q.Where(m => m.MemberName.Contains(text) || m.AccountCode.Contains(text) ||
                                     _db.VoterLogs.Any(v => v.MemberId == m.MemberId && v.EventId == eventId && v.ReceiptCode != null && v.ReceiptCode.Contains(text)));
                else if (receiptMemberIds != null)
                    q = q.Where(m => m.MemberName.Contains(text) || m.AccountCode.Contains(text) || receiptMemberIds.Contains(m.MemberId));
                else
                    q = q.Where(m => m.MemberName.Contains(text) || m.AccountCode.Contains(text));
            }
            if (hasVoted == true)
                q = q.Where(m => _db.VoterLogs.Any(v => v.MemberId == m.MemberId && v.EventId == eventId && v.HasVoted == yes));
            else if (hasVoted == false)
                q = q.Where(m => !_db.VoterLogs.Any(v => v.MemberId == m.MemberId && v.EventId == eventId && v.HasVoted == yes));

            var total = await q.CountAsync(ct);
            var response = new VoterLookupPageResponse
            {
                EventId = eventId,
                Page = w.Page,
                PageSize = w.PageSize,
                TotalCount = total,
                TotalPages = VoterLookupPaging.TotalPages(total, w.PageSize),
                Total = total
            };
            // A page past the end (the list shrank since the screen last asked) is a valid, empty answer: the totals tell the client where to go.
            if (w.Offset >= total) { response.Data = response.Items; return ServiceResult<VoterLookupPageResponse>.Success(response); }

            // Stable order: name, then id - two members with the same name never swap places between pages.
            var ids = await q.OrderBy(m => m.MemberName).ThenBy(m => m.MemberId)
                .Skip((int)w.Offset).Take(w.PageSize).Select(m => m.MemberId).ToListAsync(ct);

            var members = await _db.Members.AsNoTracking().Where(m => ids.Contains(m.MemberId))
                .Select(m => new { m.MemberId, m.AccountCode, m.MemberName, Branch = m.Branch!.BranchName, m.BranchId, m.IsEligibleToVote })
                .ToDictionaryAsync(m => m.MemberId, ct);
            var logs = await _db.VoterLogs.AsNoTracking().Where(l => l.EventId == eventId && ids.Contains(l.MemberId))
                .Select(l => new { l.MemberId, l.HasVoted, l.VotedAt, l.ReceiptCode }).ToDictionaryAsync(l => l.MemberId, ct);

            foreach (var id in ids)
            {
                if (!members.TryGetValue(id, out var m)) continue;      // deleted between the two reads: simply not shown
                logs.TryGetValue(id, out var log);
                response.Items.Add(new VoterLookupRow
                {
                    MemberId = m.MemberId,
                    AccountCode = m.AccountCode,
                    MemberName = m.MemberName,
                    Branch = m.Branch ?? "",
                    BranchId = m.BranchId,
                    IsEligible = m.IsEligibleToVote,
                    HasVoted = log?.HasVoted ?? false,
                    // VoterLogs.VotedAt is written with DateTime.UtcNow: flag it so clients show it in local time.
                    VotedAt = TimeConv.AsUtc(log?.VotedAt),
                    ReceiptCode = log?.ReceiptCode ?? ""
                });
            }
            response.Data = response.Items;
            return ServiceResult<VoterLookupPageResponse>.Success(response);
        }
    }
}
