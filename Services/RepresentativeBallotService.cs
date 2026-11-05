using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Representative;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  What a voter or a kiosk sees of a Representative election: the ballot rules, the candidates (paged), the
    //  kiosk roster sync (keyset paged) and the candidate photos.
    //
    //   * The candidate list is the SAME for every voter: there is deliberately no branch filter anywhere on this path.
    //     Which voters may vote at all is decided by the election's participating branches, at casting time.
    //   * A candidate is on the ballot when its roster entry is APPROVED and its profile is active.
    //   * Lists never carry image bytes (HasPhoto + PhotoVersion tell the client what to fetch lazily); contact data
    //     (phones, date of birth, civil status) is never included.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    public interface IRepresentativeBallotService
    {
        Task<ServiceResult<RepBallotResponse>> GetBallotAsync(int eventId, CancellationToken ct);
        Task<ServiceResult<RepCandidatePage>> ListCandidatesAsync(int eventId, string? search, string? position, int page, int pageSize, CancellationToken ct);
        Task<ServiceResult<RepRosterSyncPage>> SyncRosterAsync(int eventId, int afterId, int pageSize, CancellationToken ct);
        /// <summary>Null value = the candidate exists but has no photo.</summary>
        Task<ServiceResult<RepresentativePhoto?>> GetCandidatePhotoAsync(int eventId, int eventCandidateId, bool thumbnail, CancellationToken ct);
    }

    /// <summary>The roster fingerprint, cached briefly for a LOCKED roster (it cannot change while locked) and memoised per request.</summary>
    public interface IRosterVersionProvider
    {
        Task<string> GetAsync(EventEntity ev, CancellationToken ct);
    }

    public sealed class RosterVersionProvider : IRosterVersionProvider
    {
        private static readonly TimeSpan LockedTtl = TimeSpan.FromSeconds(30);

        private readonly ApiDbContext _db;
        private readonly IDistributedCache _cache;
        private readonly Dictionary<int, string> _perRequest = new();

        public RosterVersionProvider(ApiDbContext db, IDistributedCache cache)
        {
            _db = db;
            _cache = cache;
        }

        public async Task<string> GetAsync(EventEntity ev, CancellationToken ct)
        {
            if (_perRequest.TryGetValue(ev.EventId, out var known)) return known;

            // A roster that is still being prepared changes under the administrator's hands: always computed fresh.
            // Once locked (voting opened) it can only change through an audited unlock, which clears the lock and so bypasses this cache.
            var cacheable = ev.EventType == EventTypes.Representative && ev.RosterLockedAt != null;
            var key = $"rep_roster_version:{ev.EventId}";
            string? version = null;
            if (cacheable)
            {
                try { version = await _cache.GetStringAsync(key, ct); } catch { /* the cache is an optimisation only */ }
            }
            if (string.IsNullOrEmpty(version))
            {
                version = await RosterQueries.VersionAsync(_db, ev, ct);
                if (cacheable)
                {
                    try
                    {
                        await _cache.SetStringAsync(key, version,
                            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = LockedTtl }, ct);
                    }
                    catch { /* non-fatal */ }
                }
            }
            _perRequest[ev.EventId] = version;
            return version;
        }
    }

    public sealed class RepresentativeBallotService : IRepresentativeBallotService
    {
        private readonly ApiDbContext _db;
        private readonly IRosterVersionProvider _versions;

        public RepresentativeBallotService(ApiDbContext db, IRosterVersionProvider versions)
        {
            _db = db;
            _versions = versions;
        }

        private async Task<(EventEntity? Event, ServiceResult<T>? Error)> LoadAsync<T>(int eventId, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId && e.IsActive, ct);
            if (ev == null)
                return (null, ServiceResult<T>.Failure(StatusCodes.Status404NotFound, "Election not found.", RepCodes.EventNotFound));
            if (ev.EventType != EventTypes.Representative)
                return (null, ServiceResult<T>.Failure(StatusCodes.Status409Conflict,
                    "This is a General Assembly election; it has no Representative ballot.", RepCodes.WrongEventType,
                    $"Use GET /api/v1/events/{eventId}/candidates for a General Assembly election."));
            return (ev, null);
        }

        /// <summary>The candidates a ballot may contain.</summary>
        private IQueryable<RepresentativeEventCandidateEntity> OnBallot(int eventId) =>
            _db.RepresentativeEventCandidates.AsNoTracking()
                .Where(c => c.EventId == eventId && c.Status == RepresentativeCandidateStatuses.Approved && c.Profile!.IsActive);

        private async Task<List<RepBallotPosition>> PositionsAsync(int eventId, CancellationToken ct)
        {
            var rules = await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == eventId).OrderBy(r => r.Id).ToListAsync(ct);
            var counts = (await OnBallot(eventId).GroupBy(c => c.Position).Select(g => new { Position = g.Key, Count = g.Count() }).ToListAsync(ct))
                .ToDictionary(x => x.Position.Trim(), x => x.Count, StringComparer.OrdinalIgnoreCase);
            return rules.Select(r =>
            {
                counts.TryGetValue(r.Position.Trim(), out var n);
                return new RepBallotPosition
                {
                    Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes, TotalSeats = r.TotalSeats, CandidateCount = n
                };
            }).ToList();
        }

        public async Task<ServiceResult<RepBallotResponse>> GetBallotAsync(int eventId, CancellationToken ct)
        {
            var (ev, err) = await LoadAsync<RepBallotResponse>(eventId, ct);
            if (ev == null) return err!;

            var positions = await PositionsAsync(eventId, ct);
            var now = DateTime.UtcNow;
            return ServiceResult<RepBallotResponse>.Success(new RepBallotResponse
            {
                EventId = ev.EventId,
                EventType = ev.EventType,
                Title = ev.Title,
                ScheduleFrom = ev.ScheduleFrom,
                ScheduleTo = ev.ScheduleTo,
                Status = ev.Status,
                IsVotingOpen = RepresentativeCastTime.CheckOnline(ev.Status, ev.ScheduleFrom, ev.ScheduleTo, now).Ok,
                ServerTimeUtc = now,
                RosterVersion = await _versions.GetAsync(ev, ct),
                TotalCandidates = positions.Sum(p => p.CandidateCount),
                Positions = positions
            });
        }

        // The fields a card needs, read in one narrow projection (no photo bytes, no contact data).
        private sealed record CardRow(
            int Id, string Code, string Name, string Position, int? BallotNumber, string? Reason, string? Gender,
            string? HighestEducation, string? PreviousPositions, string? SpecialSkills, DateTime? PhotoUpdatedAt);

        private static IQueryable<CardRow> ProjectCards(IQueryable<RepresentativeEventCandidateEntity> q) =>
            q.Select(c => new CardRow(
                c.RepresentativeEventCandidateId, c.Profile!.CandidateCode, c.Profile.CandidateName, c.Position, c.BallotNumber, c.Reason,
                c.Profile.Gender, c.Profile.HighestEducation, c.Profile.PreviousPositions, c.Profile.SpecialSkills, c.Profile.PhotoUpdatedAt));

        private static RepCandidateCard ToCard(CardRow r) => new()
        {
            Id = r.Id, CandidateCode = r.Code, CandidateName = r.Name, Position = r.Position, BallotNumber = r.BallotNumber, Reason = r.Reason,
            Gender = r.Gender, HighestEducation = r.HighestEducation, PreviousPositions = r.PreviousPositions, SpecialSkills = r.SpecialSkills,
            HasPhoto = r.PhotoUpdatedAt != null, PhotoVersion = r.PhotoUpdatedAt?.Ticks ?? 0
        };

        public async Task<ServiceResult<RepCandidatePage>> ListCandidatesAsync(
            int eventId, string? search, string? position, int page, int pageSize, CancellationToken ct)
        {
            var (ev, err) = await LoadAsync<RepCandidatePage>(eventId, ct);
            if (ev == null) return err!;

            page = Math.Clamp(page, 1, 1_000_000);
            pageSize = Math.Clamp(pageSize <= 0 ? RepresentativeLimits.DefaultPageSize : pageSize, 1, RepresentativeLimits.MaxPageSize);

            var q = OnBallot(eventId);
            if (!string.IsNullOrWhiteSpace(position))
            {
                var pos = position.Trim();
                q = q.Where(c => c.Position == pos);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                if (int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    q = q.Where(c => c.Profile!.CandidateCode.StartsWith(s) || c.Profile.CandidateName.Contains(s) || c.BallotNumber == number);
                else
                    q = q.Where(c => c.Profile!.CandidateCode.StartsWith(s) || c.Profile.CandidateName.Contains(s));
            }

            var total = await q.CountAsync(ct);
            var rows = await ProjectCards(
                    q.OrderBy(c => c.Position).ThenBy(c => c.BallotNumber == null).ThenBy(c => c.BallotNumber)
                     .ThenBy(c => c.Profile!.CandidateName).ThenBy(c => c.RepresentativeEventCandidateId)
                     .Skip((page - 1) * pageSize).Take(pageSize))
                .ToListAsync(ct);

            return ServiceResult<RepCandidatePage>.Success(new RepCandidatePage
            {
                EventId = eventId,
                RosterVersion = await _versions.GetAsync(ev, ct),
                Total = total,
                Page = page,
                PageSize = pageSize,
                TotalPages = (int)Math.Ceiling(total / (double)pageSize),
                Items = rows.Select(ToCard).ToList()
            });
        }

        public async Task<ServiceResult<RepRosterSyncPage>> SyncRosterAsync(int eventId, int afterId, int pageSize, CancellationToken ct)
        {
            var (ev, err) = await LoadAsync<RepRosterSyncPage>(eventId, ct);
            if (ev == null) return err!;

            pageSize = Math.Clamp(pageSize <= 0 ? 200 : pageSize, 1, RepresentativeLimits.MaxSyncPageSize);
            afterId = Math.Max(0, afterId);

            var total = await OnBallot(eventId).CountAsync(ct);
            // Keyset paging on the primary key: every page costs the same however deep it is, and the pages stay consistent
            // even if rows are added while a kiosk is syncing (they simply show up later in the key order).
            var rows = await ProjectCards(
                    OnBallot(eventId).Where(c => c.RepresentativeEventCandidateId > afterId)
                        .OrderBy(c => c.RepresentativeEventCandidateId).Take(pageSize + 1))
                .ToListAsync(ct);
            var more = rows.Count > pageSize;
            if (more) rows.RemoveAt(rows.Count - 1);

            return ServiceResult<RepRosterSyncPage>.Success(new RepRosterSyncPage
            {
                EventId = eventId,
                EventType = ev.EventType,
                RosterVersion = await _versions.GetAsync(ev, ct),
                Total = total,
                NextAfterId = more ? rows[^1].Id : null,
                Items = rows.Select(ToCard).ToList(),
                Positions = afterId == 0 ? await PositionsAsync(eventId, ct) : null
            });
        }

        public async Task<ServiceResult<RepresentativePhoto?>> GetCandidatePhotoAsync(int eventId, int eventCandidateId, bool thumbnail, CancellationToken ct)
        {
            var (ev, err) = await LoadAsync<RepresentativePhoto?>(eventId, ct);
            if (ev == null) return err!;

            // The candidate must be on THIS election's ballot: an id from another election (or a withdrawn entry) is "not found".
            var profileId = await OnBallot(eventId)
                .Where(c => c.RepresentativeEventCandidateId == eventCandidateId)
                .Select(c => (int?)c.RepresentativeCandidateProfileId)
                .FirstOrDefaultAsync(ct);
            if (profileId == null) return ServiceResult<RepresentativePhoto?>.NotFound("Candidate");

            var row = await _db.RepresentativeCandidatePhotos.AsNoTracking()
                .Where(x => x.RepresentativeCandidateProfileId == profileId && x.Profile!.PhotoUpdatedAt != null)
                .Select(x => new { x.Image, x.Thumbnail, x.ContentType, Version = x.Profile!.PhotoUpdatedAt!.Value })
                .FirstOrDefaultAsync(ct);
            if (row == null) return ServiceResult<RepresentativePhoto?>.Success(null);

            var useThumb = thumbnail && row.Thumbnail is { Length: > 0 };
            return ServiceResult<RepresentativePhoto?>.Success(new RepresentativePhoto(
                useThumb ? row.Thumbnail! : row.Image,
                useThumb ? ImageSniffer.ContentType(row.Thumbnail!) : row.ContentType,
                row.Version, useThumb));
        }
    }
}
