using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using System.Security.Cryptography;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Representative candidate PROFILES (reusable people) and the per-event ROSTER (a profile at a position).
    //
    //  Rules enforced here, whatever client calls:
    //   * Roster changes happen inside a transaction that first locks the event row, so assignments, removals, rule edits, the import
    //     commit and Open/Close are serialised per event.
    //   * The roster is editable only while the election is being prepared (EventLockPolicy). Removing a candidate from a roster NEVER
    //     deletes the profile; a profile is never physically deleted at all (it can be deactivated).
    //   * MaxCandidates is a hard limit: the extra assignment is refused with a message that says the position's limit has been reached.
    //   * A profile's data cannot change while the profile is on the roster of an election that is open now, and a candidate who has
    //     received votes keeps the name those votes were cast for.
    //   * Candidates are matched by CandidateCode (stable, unique) — never by name.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    public interface IRepresentativeRosterService
    {
        // profiles
        Task<PagedResponse<RepProfileRow>> ListProfilesAsync(string? search, bool? active, int page, int pageSize, CancellationToken ct);
        Task<ServiceResult<RepProfileDetail>> GetProfileAsync(int id, CancellationToken ct);
        Task<ServiceResult<RepProfileDetail>> CreateProfileAsync(RepProfileUpsertRequest req, CancellationToken ct);
        Task<ServiceResult<RepProfileDetail>> UpdateProfileAsync(int id, RepProfileUpsertRequest req, CancellationToken ct);
        Task<ServiceResult<RepProfileDetail>> SetActiveAsync(int id, bool active, DateTime? expectedUpdatedAt, CancellationToken ct);
        Task<ServiceResult<RepProfileDetail>> SetPhotoAsync(int id, byte[]? image, byte[]? thumbnail, CancellationToken ct);
        Task<RepresentativePhoto?> GetPhotoAsync(int profileId, bool thumbnail, CancellationToken ct);

        // roster
        Task<ServiceResult<RepRosterSummaryResponse>> SummaryAsync(int eventId, CancellationToken ct);
        Task<ServiceResult<PagedResponse<RepRosterRow>>> ListRosterAsync(int eventId, string? search, string? position, string? status, int page, int pageSize, CancellationToken ct);
        Task<ServiceResult<List<RepRosterExportRow>>> ExportRosterAsync(int eventId, CancellationToken ct);
        Task<ServiceResult<RepRosterRow>> AssignAsync(int eventId, RepAssignRequest req, CancellationToken ct);
        Task<ServiceResult<RepRosterRow>> RemoveAsync(int eventId, int assignmentId, CancellationToken ct);
        Task<ServiceResult<RepRosterSummaryResponse>> UnlockAsync(int eventId, RepRosterUnlockRequest req, CancellationToken ct);
    }

    /// <summary>A stored candidate photo ready to be served.</summary>
    public sealed record RepresentativePhoto(byte[] Bytes, string ContentType, DateTime Version, bool IsThumbnail);

    public sealed class RepresentativeRosterService : IRepresentativeRosterService
    {
        private const int ExportCap = 50_000;

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly IEventOpenGate _gate;

        public RepresentativeRosterService(ApiDbContext db, IAuditService audit, IEventOpenGate gate)
        {
            _db = db;
            _audit = audit;
            _gate = gate;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Profiles
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        private IQueryable<RepProfileDetail> ProjectDetail(IQueryable<RepresentativeCandidateProfileEntity> q) =>
            q.Select(p => new RepProfileDetail
            {
                ProfileId = p.RepresentativeCandidateProfileId,
                CandidateCode = p.CandidateCode,
                CandidateName = p.CandidateName,
                MemberId = p.MemberId,
                AccountCode = p.Member != null ? p.Member.AccountCode : null,
                HasPhoto = p.PhotoUpdatedAt != null,
                PhotoUpdatedAt = p.PhotoUpdatedAt,
                IsActive = p.IsActive,
                EventAssignments = _db.RepresentativeEventCandidates.Count(c => c.RepresentativeCandidateProfileId == p.RepresentativeCandidateProfileId),
                UpdatedAt = p.UpdatedAt,
                CellNo = p.CellNo, TelNo = p.TelNo, CivilStatus = p.CivilStatus, Gender = p.Gender, DateOfBirth = p.DateOfBirth,
                HighestEducation = p.HighestEducation, PreviousPositions = p.PreviousPositions, SpecialSkills = p.SpecialSkills
            });

        public async Task<PagedResponse<RepProfileRow>> ListProfilesAsync(string? search, bool? active, int page, int pageSize, CancellationToken ct)
        {
            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, RepresentativeLimits.MaxPageSize);

            var q = _db.RepresentativeCandidateProfiles.AsNoTracking().AsQueryable();
            if (active.HasValue) q = q.Where(p => p.IsActive == active.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(p => p.CandidateCode.StartsWith(s) || p.CandidateName.Contains(s) || (p.Member != null && p.Member.AccountCode.StartsWith(s)));
            }

            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(p => p.CandidateName).ThenBy(p => p.RepresentativeCandidateProfileId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new RepProfileRow
                {
                    ProfileId = p.RepresentativeCandidateProfileId,
                    CandidateCode = p.CandidateCode,
                    CandidateName = p.CandidateName,
                    MemberId = p.MemberId,
                    AccountCode = p.Member != null ? p.Member.AccountCode : null,
                    HasPhoto = p.PhotoUpdatedAt != null,
                    PhotoUpdatedAt = p.PhotoUpdatedAt,
                    IsActive = p.IsActive,
                    EventAssignments = _db.RepresentativeEventCandidates.Count(c => c.RepresentativeCandidateProfileId == p.RepresentativeCandidateProfileId),
                    UpdatedAt = p.UpdatedAt
                }).ToListAsync(ct);

            return new PagedResponse<RepProfileRow>
            {
                Items = items, Total = total, Page = page, PageSize = pageSize,
                TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))
            };
        }

        public async Task<ServiceResult<RepProfileDetail>> GetProfileAsync(int id, CancellationToken ct)
        {
            var row = await ProjectDetail(_db.RepresentativeCandidateProfiles.AsNoTracking().Where(p => p.RepresentativeCandidateProfileId == id)).FirstOrDefaultAsync(ct);
            return row == null ? ServiceResult<RepProfileDetail>.NotFound("Candidate profile") : ServiceResult<RepProfileDetail>.Success(row);
        }

        public async Task<ServiceResult<RepProfileDetail>> CreateProfileAsync(RepProfileUpsertRequest req, CancellationToken ct)
        {
            var built = await BuildNewProfileAsync(req, DateTime.Now, ct);
            if (!built.Ok) return built.As<RepProfileDetail>();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            _db.RepresentativeCandidateProfiles.Add(built.Value!);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status409Conflict,
                    $"A candidate profile with code {built.Value!.CandidateCode} (or the same member) already exists.", RepCodes.CodeExists);
            }
            _audit.Stage(AuditActions.Create, AuditEntities.RepresentativeCandidate, built.Value!.RepresentativeCandidateProfileId,
                $"Candidate profile {built.Value.CandidateCode} '{built.Value.CandidateName}' created{(built.Value.MemberId.HasValue ? $" (member {built.Value.MemberId})" : "")}.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await GetProfileAsync(built.Value.RepresentativeCandidateProfileId, ct) is { } r
                ? ServiceResult<RepProfileDetail>.Success(r.Value!, StatusCodes.Status201Created)
                : ServiceResult<RepProfileDetail>.NotFound("Candidate profile");
        }

        /// <summary>Validates a profile request and, when it is acceptable, returns the (not yet saved) entity. Shared by "create profile" and "assign a new profile".</summary>
        private async Task<ServiceResult<RepresentativeCandidateProfileEntity>> BuildNewProfileAsync(RepProfileUpsertRequest np, DateTime now, CancellationToken ct)
        {
            var chk = CheckProfileFields(np);
            if (!chk.Ok) return chk.As<RepresentativeCandidateProfileEntity>();
            var f = chk.Value!;

            var code = f.CandidateCode!;
            if (await _db.RepresentativeCandidateProfiles.AsNoTracking().AnyAsync(p => p.CandidateCode == code, ct))
                return ServiceResult<RepresentativeCandidateProfileEntity>.Failure(StatusCodes.Status409Conflict,
                    $"A candidate profile with code {code} already exists. Select the existing profile instead of creating another.", RepCodes.CodeExists, null,
                    new() { ["candidateCode"] = "This code is already in use." });

            MemberEntity? member = null;
            if (f.AccountCode != null)
            {
                var account = f.AccountCode;
                member = await _db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.AccountCode == account && m.IsActive, ct);
                if (member == null)
                    return ServiceResult<RepresentativeCandidateProfileEntity>.Failure(StatusCodes.Status400BadRequest,
                        $"No active member has account code '{account}'.", RepresentativeImportRowRules.MemberNotFound, null,
                        new() { ["accountCode"] = "No active member has this account code." });
                var linked = await _db.RepresentativeCandidateProfiles.AsNoTracking().Where(p => p.MemberId == member.MemberId).Select(p => p.CandidateCode).FirstOrDefaultAsync(ct);
                if (linked != null)
                    return ServiceResult<RepresentativeCandidateProfileEntity>.Failure(StatusCodes.Status409Conflict,
                        $"Member '{account}' already has the candidate profile {linked}. A member can have only one profile.", RepCodes.MemberLinked, null,
                        new() { ["accountCode"] = "This member already has a candidate profile." });
            }

            var name = f.CandidateName ?? member?.MemberName;
            if (name == null)
                return ServiceResult<RepresentativeCandidateProfileEntity>.Invalid("candidateName", "Candidate name is required (or give the account code of an existing member).");
            if (name.Length > RepresentativeLimits.CandidateName)
                name = name.Substring(0, RepresentativeLimits.CandidateName);

            return ServiceResult<RepresentativeCandidateProfileEntity>.Success(new RepresentativeCandidateProfileEntity
            {
                CandidateCode = code, MemberId = member?.MemberId, CandidateName = name,
                CellNo = f.CellNo, TelNo = f.TelNo, CivilStatus = f.CivilStatus, Gender = f.Gender, DateOfBirth = f.DateOfBirth,
                HighestEducation = f.HighestEducation, PreviousPositions = f.PreviousPositions, SpecialSkills = f.SpecialSkills,
                PhotoUpdatedAt = null, IsActive = true, CreatedAt = now, UpdatedAt = now
            });
        }

        /// <summary>The same field rules the import applies per row, so a profile typed in by hand and one imported are held to one standard.</summary>
        private static ServiceResult<ImportRowCheck> CheckProfileFields(RepProfileUpsertRequest r)
        {
            var chk = RepresentativeImportRowRules.Check(new RepImportRowIn
            {
                RowNumber = 1, CandidateCode = r.CandidateCode, CandidateName = r.CandidateName, AccountCode = r.AccountCode,
                Position = "-",                                           // the position is not part of a profile
                CellNo = r.CellNo, TelNo = r.TelNo, CivilStatus = r.CivilStatus, Gender = r.Gender, DateOfBirth = r.DateOfBirth,
                HighestEducation = r.HighestEducation, PreviousPositions = r.PreviousPositions, SpecialSkills = r.SpecialSkills
            }, DateTime.Now.Date);
            if (!chk.HasErrors) return ServiceResult<ImportRowCheck>.Success(chk);

            var fields = new Dictionary<string, string>();
            foreach (var (code, message) in chk.Errors)
            {
                var key = code switch
                {
                    RepresentativeImportRowRules.CodeInvalid => "candidateCode",
                    RepresentativeImportRowRules.PhoneInvalid => "cellNo",
                    RepresentativeImportRowRules.CivilStatusInvalid => "civilStatus",
                    RepresentativeImportRowRules.GenderInvalid => "gender",
                    RepresentativeImportRowRules.DobInvalid => "dateOfBirth",
                    _ => "profile"
                };
                fields[key] = fields.TryGetValue(key, out var old) ? old + " " + message : message;
            }
            return ServiceResult<ImportRowCheck>.Failure(StatusCodes.Status400BadRequest, chk.Errors[0].Message, "VALIDATION", null, fields);
        }

        public async Task<ServiceResult<RepProfileDetail>> UpdateProfileAsync(int id, RepProfileUpsertRequest req, CancellationToken ct)
        {
            var chk = CheckProfileFields(req);
            if (!chk.Ok) return chk.As<RepProfileDetail>();
            var f = chk.Value!;

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var p = (await _db.RepresentativeCandidateProfiles
                .FromSqlRaw("SELECT * FROM RepresentativeCandidateProfiles WHERE RepresentativeCandidateProfileId = {0} FOR UPDATE", id).ToListAsync(ct)).FirstOrDefault();
            if (p == null) return ServiceResult<RepProfileDetail>.NotFound("Candidate profile");
            if (Concurrency.IsStale(req.ExpectedUpdatedAt, p.UpdatedAt)) return ServiceResult<RepProfileDetail>.Concurrency("candidate profile");

            if (!string.Equals(f.CandidateCode, p.CandidateCode, StringComparison.OrdinalIgnoreCase))
                return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status409Conflict,
                    "The candidate code identifies the person for good and cannot be changed. Create a new profile if this is a different person.", RepCodes.CodeImmutable);

            var blocked = await ProfileEditBlockAsync(p, ct);
            if (blocked != null) return blocked.As<RepProfileDetail>();

            var newName = f.CandidateName ?? p.CandidateName;
            if (!string.Equals(newName, p.CandidateName, StringComparison.Ordinal) && await HasVotesAsync(p.RepresentativeCandidateProfileId, ct))
                return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status409Conflict,
                    "This candidate has received votes in a past election, so the name those votes were cast for cannot be changed.", RepCodes.ProfileHasVotes);

            // Member link: may be set once; never silently changed or cleared.
            if (f.AccountCode != null)
            {
                var account = f.AccountCode;
                var member = await _db.Members.AsNoTracking().FirstOrDefaultAsync(m => m.AccountCode == account && m.IsActive, ct);
                if (member == null)
                    return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status400BadRequest, $"No active member has account code '{account}'.",
                        RepresentativeImportRowRules.MemberNotFound, null, new() { ["accountCode"] = "No active member has this account code." });
                if (p.MemberId.HasValue && p.MemberId.Value != member.MemberId)
                    return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status409Conflict,
                        "This profile is already linked to a different member; the link cannot be changed.", RepCodes.MemberLinked, null,
                        new() { ["accountCode"] = "The member link cannot be changed." });
                if (!p.MemberId.HasValue)
                {
                    var other = await _db.RepresentativeCandidateProfiles.AsNoTracking()
                        .Where(x => x.MemberId == member.MemberId && x.RepresentativeCandidateProfileId != id).Select(x => x.CandidateCode).FirstOrDefaultAsync(ct);
                    if (other != null)
                        return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status409Conflict,
                            $"Member '{account}' already has the candidate profile {other}.", RepCodes.MemberLinked, null,
                            new() { ["accountCode"] = "This member already has a candidate profile." });
                    p.MemberId = member.MemberId;
                }
            }

            var before = $"{p.CandidateName}|{p.CellNo}|{p.TelNo}|{p.CivilStatus}|{p.Gender}|{p.DateOfBirth:yyyy-MM-dd}|{p.HighestEducation}|{p.PreviousPositions}|{p.SpecialSkills}";
            p.CandidateName = newName.Length > RepresentativeLimits.CandidateName ? newName[..RepresentativeLimits.CandidateName] : newName;
            p.CellNo = f.CellNo; p.TelNo = f.TelNo; p.CivilStatus = f.CivilStatus; p.Gender = f.Gender; p.DateOfBirth = f.DateOfBirth;
            p.HighestEducation = f.HighestEducation; p.PreviousPositions = f.PreviousPositions; p.SpecialSkills = f.SpecialSkills;
            var after = $"{p.CandidateName}|{p.CellNo}|{p.TelNo}|{p.CivilStatus}|{p.Gender}|{p.DateOfBirth:yyyy-MM-dd}|{p.HighestEducation}|{p.PreviousPositions}|{p.SpecialSkills}";

            if (before != after || _db.ChangeTracker.HasChanges())
            {
                p.UpdatedAt = DateTime.Now;
                _audit.Stage(AuditActions.UpdateRepCandidate, AuditEntities.RepresentativeCandidate, id, $"Candidate profile {p.CandidateCode} '{p.CandidateName}' updated.");
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return await GetProfileAsync(id, ct);
        }

        public async Task<ServiceResult<RepProfileDetail>> SetActiveAsync(int id, bool active, DateTime? expectedUpdatedAt, CancellationToken ct)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var p = (await _db.RepresentativeCandidateProfiles
                .FromSqlRaw("SELECT * FROM RepresentativeCandidateProfiles WHERE RepresentativeCandidateProfileId = {0} FOR UPDATE", id).ToListAsync(ct)).FirstOrDefault();
            if (p == null) return ServiceResult<RepProfileDetail>.NotFound("Candidate profile");
            if (Concurrency.IsStale(expectedUpdatedAt, p.UpdatedAt)) return ServiceResult<RepProfileDetail>.Concurrency("candidate profile");

            if (p.IsActive != active)
            {
                if (!active)
                {
                    // Deactivating a person who is still on a live ballot would silently change that ballot, so it is refused.
                    var live = await _db.RepresentativeEventCandidates.AsNoTracking()
                        .Where(c => c.RepresentativeCandidateProfileId == id && (c.Event!.Status == "Opened" || c.Event.RosterLockedAt == null))
                        .OrderBy(c => c.EventId)          // the same election is named on every call when there are several
                        .Select(c => new { c.Event!.Title, c.Event.Status }).FirstOrDefaultAsync(ct);
                    if (live != null)
                        return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status409Conflict,
                            live.Status == "Opened"
                                ? $"{p.CandidateName} is a candidate in the election '{live.Title}', which is open now. The profile cannot be deactivated."
                                : $"{p.CandidateName} is still on the candidate roster of '{live.Title}'. Remove the candidate from that roster before deactivating the profile.",
                            live.Status == "Opened" ? RepCodes.ProfileInOpenEvent : RepCodes.ProfileOnRoster);
                }
                p.IsActive = active;
                p.UpdatedAt = DateTime.Now;
                _audit.Stage(AuditActions.UpdateRepCandidate, AuditEntities.RepresentativeCandidate, id,
                    $"Candidate profile {p.CandidateCode} '{p.CandidateName}' {(active ? "reactivated" : "deactivated")}.");
                await _db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            return await GetProfileAsync(id, ct);
        }

        public async Task<ServiceResult<RepProfileDetail>> SetPhotoAsync(int id, byte[]? image, byte[]? thumbnail, CancellationToken ct)
        {
            if (image != null && image.Length > 0)
            {
                if (ImageSniffer.ContentType(image) == "application/octet-stream")
                    return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status400BadRequest, "The photo must be a PNG, JPEG, GIF or BMP image.", RepCodes.PhotoInvalid);
                if (thumbnail != null && thumbnail.Length > 0)
                {
                    if (thumbnail.Length > RepresentativeLimits.MaxThumbnailBytes)
                        return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status400BadRequest,
                            $"The thumbnail is too large (max {RepresentativeLimits.MaxThumbnailBytes / 1024} KB).", RepCodes.PhotoInvalid);
                    if (ImageSniffer.ContentType(thumbnail) == "application/octet-stream")
                        return ServiceResult<RepProfileDetail>.Failure(StatusCodes.Status400BadRequest, "The thumbnail must be a PNG, JPEG, GIF or BMP image.", RepCodes.PhotoInvalid);
                }
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var p = (await _db.RepresentativeCandidateProfiles
                .FromSqlRaw("SELECT * FROM RepresentativeCandidateProfiles WHERE RepresentativeCandidateProfileId = {0} FOR UPDATE", id).ToListAsync(ct)).FirstOrDefault();
            if (p == null) return ServiceResult<RepProfileDetail>.NotFound("Candidate profile");

            var blocked = await ProfileEditBlockAsync(p, ct);
            if (blocked != null) return blocked.As<RepProfileDetail>();

            var now = DateTime.Now;
            var existing = await _db.RepresentativeCandidatePhotos.FirstOrDefaultAsync(x => x.RepresentativeCandidateProfileId == id, ct);
            if (image == null || image.Length == 0)
            {
                if (existing != null) _db.RepresentativeCandidatePhotos.Remove(existing);
                p.PhotoUpdatedAt = null;
            }
            else
            {
                var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
                var thumb = thumbnail is { Length: > 0 } ? thumbnail : null;
                if (existing == null)
                    _db.RepresentativeCandidatePhotos.Add(new RepresentativeCandidatePhotoEntity
                    {
                        RepresentativeCandidateProfileId = id, Image = image, Thumbnail = thumb, ContentType = ImageSniffer.ContentType(image), Sha256 = hash, UpdatedAt = now
                    });
                else
                {
                    existing.Image = image; existing.Thumbnail = thumb; existing.ContentType = ImageSniffer.ContentType(image); existing.Sha256 = hash; existing.UpdatedAt = now;
                }
                p.PhotoUpdatedAt = now;
            }
            p.UpdatedAt = now;
            _audit.Stage(AuditActions.RepProfilePhoto, AuditEntities.RepresentativeCandidate, id,
                $"Photo of candidate profile {p.CandidateCode} {(image == null || image.Length == 0 ? "removed" : $"replaced ({image.Length:N0} bytes)")}.");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return await GetProfileAsync(id, ct);
        }

        public async Task<RepresentativePhoto?> GetPhotoAsync(int profileId, bool thumbnail, CancellationToken ct)
        {
            var row = await _db.RepresentativeCandidatePhotos.AsNoTracking()
                .Where(x => x.RepresentativeCandidateProfileId == profileId && x.Profile!.PhotoUpdatedAt != null)
                .Select(x => new { x.Image, x.Thumbnail, x.ContentType, Version = x.Profile!.PhotoUpdatedAt!.Value })
                .FirstOrDefaultAsync(ct);
            if (row == null) return null;
            bool useThumb = thumbnail && row.Thumbnail is { Length: > 0 };
            return new RepresentativePhoto(useThumb ? row.Thumbnail! : row.Image, useThumb ? ImageSniffer.ContentType(row.Thumbnail!) : row.ContentType, row.Version, useThumb);
        }

        /// <summary>Edits to a profile are refused while it is on the ballot of an election that is open right now.</summary>
        private async Task<ServiceResult<object>?> ProfileEditBlockAsync(RepresentativeCandidateProfileEntity p, CancellationToken ct)
        {
            var open = await _db.RepresentativeEventCandidates.AsNoTracking()
                .Where(c => c.RepresentativeCandidateProfileId == p.RepresentativeCandidateProfileId && c.Event!.Status == "Opened")
                .OrderBy(c => c.EventId)
                .Select(c => c.Event!.Title).FirstOrDefaultAsync(ct);
            return open == null ? null
                : ServiceResult<object>.Failure(StatusCodes.Status409Conflict,
                    $"{p.CandidateName} is a candidate in the election '{open}', which is open now. The profile cannot be changed while voting is open.", RepCodes.ProfileInOpenEvent);
        }

        private Task<bool> HasVotesAsync(int profileId, CancellationToken ct) =>
            _db.RepresentativeVotes.AsNoTracking().AnyAsync(v => v.EventCandidate!.RepresentativeCandidateProfileId == profileId, ct);

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Roster
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        private async Task<(EventEntity? Event, ServiceResult<T>? Error)> RepresentativeEventAsync<T>(int eventId, bool forUpdate, CancellationToken ct)
        {
            var ev = forUpdate
                ? await RosterQueries.LockEventRowAsync(_db, eventId, ct)
                : await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == eventId && e.IsActive, ct);
            if (ev == null) return (null, ServiceResult<T>.NotFound("Event"));
            if (ev.EventType != EventTypes.Representative)
                return (null, ServiceResult<T>.Failure(StatusCodes.Status409Conflict,
                    "This is a General Assembly election. Its candidates are the approved candidacies; the Representative roster does not apply.", RepCodes.WrongEventType));
            return (ev, null);
        }

        public async Task<ServiceResult<RepRosterSummaryResponse>> SummaryAsync(int eventId, CancellationToken ct)
        {
            var (ev, err) = await RepresentativeEventAsync<RepRosterSummaryResponse>(eventId, false, ct);
            if (ev == null) return err!;

            var rules = await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == eventId).OrderBy(r => r.Id).ToListAsync(ct);
            var assigned = await RosterQueries.AssignedByPositionAsync(_db, ev, ct);
            var gate = await RosterQueries.LockAsync(_db, ev, ct);
            var readiness = await _gate.EvaluateAsync(ev, ct);

            return ServiceResult<RepRosterSummaryResponse>.Success(new RepRosterSummaryResponse
            {
                EventId = ev.EventId,
                EventTitle = ev.Title,
                EventType = ev.EventType,
                EventStatus = ev.Status,
                TotalAssigned = assigned.Values.Sum(),
                CanEdit = !gate.Locked,
                LockCode = gate.Code,
                LockReason = gate.Reason,
                RosterLockedAt = ev.RosterLockedAt,
                RosterVersion = await RosterQueries.VersionAsync(_db, ev, ct),
                Positions = rules.Select(r =>
                {
                    assigned.TryGetValue(r.Position.Trim(), out var n);
                    return new RepRosterPositionSummary
                    {
                        Position = r.Position, MinVotes = r.MinVotes, MaxVotes = r.MaxVotes, TotalSeats = r.TotalSeats, MaxCandidates = r.MaxCandidates,
                        Assigned = n, FreeSlots = r.MaxCandidates.HasValue ? Math.Max(0, r.MaxCandidates.Value - n) : null
                    };
                }).ToList(),
                ReadinessIssues = readiness.Issues.ToList(),
                ReadinessWarnings = readiness.Warnings.ToList()
            });
        }

        public async Task<ServiceResult<PagedResponse<RepRosterRow>>> ListRosterAsync(
            int eventId, string? search, string? position, string? status, int page, int pageSize, CancellationToken ct)
        {
            var (ev, err) = await RepresentativeEventAsync<PagedResponse<RepRosterRow>>(eventId, false, ct);
            if (ev == null) return err!;

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, RepresentativeLimits.MaxPageSize);

            // The roster is the SAME for every voter and every branch: there is deliberately no branch filter here or anywhere on the ballot.
            var q = _db.RepresentativeEventCandidates.AsNoTracking().Where(c => c.EventId == eventId);
            if (!string.IsNullOrWhiteSpace(position)) { var pos = position.Trim(); q = q.Where(c => c.Position == pos); }
            if (!string.IsNullOrWhiteSpace(status)) { var st = status.Trim().ToUpperInvariant(); q = q.Where(c => c.Status == st); }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                q = q.Where(c => c.Profile!.CandidateCode.StartsWith(s) || c.Profile.CandidateName.Contains(s));
            }

            var total = await q.CountAsync(ct);
            var items = await q
                .OrderBy(c => c.Position).ThenBy(c => c.BallotNumber == null).ThenBy(c => c.BallotNumber)
                .ThenBy(c => c.Profile!.CandidateName).ThenBy(c => c.RepresentativeEventCandidateId)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(c => new RepRosterRow
                {
                    AssignmentId = c.RepresentativeEventCandidateId,
                    ProfileId = c.RepresentativeCandidateProfileId,
                    CandidateCode = c.Profile!.CandidateCode,
                    CandidateName = c.Profile.CandidateName,
                    AccountCode = c.Profile.Member != null ? c.Profile.Member.AccountCode : null,
                    Position = c.Position,
                    BallotNumber = c.BallotNumber,
                    Status = c.Status,
                    Reason = c.Reason,
                    HasPhoto = c.Profile.PhotoUpdatedAt != null,
                    PhotoUpdatedAt = c.Profile.PhotoUpdatedAt,
                    ImportBatchId = c.ImportBatchId,
                    CreatedAt = c.CreatedAt
                }).ToListAsync(ct);

            return ServiceResult<PagedResponse<RepRosterRow>>.Success(new PagedResponse<RepRosterRow>
            {
                Items = items, Total = total, Page = page, PageSize = pageSize,
                TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))
            });
        }

        public async Task<ServiceResult<List<RepRosterExportRow>>> ExportRosterAsync(int eventId, CancellationToken ct)
        {
            var (ev, err) = await RepresentativeEventAsync<List<RepRosterExportRow>>(eventId, false, ct);
            if (ev == null) return err!;

            var rows = await _db.RepresentativeEventCandidates.AsNoTracking().Where(c => c.EventId == eventId)
                .OrderBy(c => c.Position).ThenBy(c => c.BallotNumber == null).ThenBy(c => c.BallotNumber)
                .ThenBy(c => c.Profile!.CandidateName).ThenBy(c => c.RepresentativeEventCandidateId)
                .Take(ExportCap)
                .Select(c => new RepRosterExportRow
                {
                    AssignmentId = c.RepresentativeEventCandidateId,
                    ProfileId = c.RepresentativeCandidateProfileId,
                    CandidateCode = c.Profile!.CandidateCode,
                    CandidateName = c.Profile.CandidateName,
                    AccountCode = c.Profile.Member != null ? c.Profile.Member.AccountCode : null,
                    Position = c.Position,
                    BallotNumber = c.BallotNumber,
                    Status = c.Status,
                    Reason = c.Reason,
                    HasPhoto = c.Profile.PhotoUpdatedAt != null,
                    PhotoUpdatedAt = c.Profile.PhotoUpdatedAt,
                    ImportBatchId = c.ImportBatchId,
                    CreatedAt = c.CreatedAt,
                    CellNo = c.Profile.CellNo, TelNo = c.Profile.TelNo, CivilStatus = c.Profile.CivilStatus, Gender = c.Profile.Gender,
                    DateOfBirth = c.Profile.DateOfBirth, HighestEducation = c.Profile.HighestEducation,
                    PreviousPositions = c.Profile.PreviousPositions, SpecialSkills = c.Profile.SpecialSkills
                }).ToListAsync(ct);

            await _audit.WriteAsync(AuditActions.ExportReport, AuditEntities.RepresentativeRoster, eventId,
                $"Candidate roster of '{ev.Title}' exported ({rows.Count} row(s)).", ct: ct);
            return ServiceResult<List<RepRosterExportRow>>.Success(rows);
        }

        public async Task<ServiceResult<RepRosterRow>> AssignAsync(int eventId, RepAssignRequest req, CancellationToken ct)
        {
            // ── cheap request checks first ──
            var positionText = RepText.OneLine(req.Position);
            if (positionText == null) return ServiceResult<RepRosterRow>.Invalid("position", "Position is required.");
            var reason = RepText.MultiLine(req.Reason);
            if (reason is { Length: > RepresentativeLimits.Reason })
                return ServiceResult<RepRosterRow>.Invalid("reason", $"Reason is longer than {RepresentativeLimits.Reason} characters.");
            if (req.BallotNumber is { } bn && (bn < 1 || bn > RepresentativeLimits.MaxBallotNumber))
                return ServiceResult<RepRosterRow>.Invalid("ballotNumber", $"Ballot number must be a whole number from 1 to {RepresentativeLimits.MaxBallotNumber}.");
            int sources = (req.ProfileId.HasValue ? 1 : 0) + (!string.IsNullOrWhiteSpace(req.CandidateCode) ? 1 : 0) + (req.NewProfile != null ? 1 : 0);
            if (sources == 0)
                return ServiceResult<RepRosterRow>.Invalid("profileId", "Choose an existing candidate profile, or enter the details of a new candidate.");
            if (sources > 1)
                return ServiceResult<RepRosterRow>.Invalid("profileId", "Give either an existing profile (id or code) or the details of a new candidate, not both.");

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var (ev, err) = await RepresentativeEventAsync<RepRosterRow>(eventId, true, ct);
            if (ev == null) return err!;

            var gate = await RosterQueries.LockAsync(_db, ev, ct);
            if (gate.Locked) return ServiceResult<RepRosterRow>.Locked(gate);

            var rule = await _db.EventPositionRules.AsNoTracking().FirstOrDefaultAsync(r => r.EventId == eventId && r.Position == positionText, ct);
            if (rule == null)
                return ServiceResult<RepRosterRow>.Failure(StatusCodes.Status400BadRequest,
                    $"'{positionText}' is not a position of this election. Add it to the position rules first.", RepCodes.PositionNotConfigured, null,
                    new() { ["position"] = "This position is not configured for the election." });
            var position = rule.Position.Trim();

            // ── which profile? ──
            RepresentativeCandidateProfileEntity profile;
            bool created = false;
            if (req.NewProfile != null)
            {
                var built = await BuildNewProfileAsync(req.NewProfile, DateTime.Now, ct);
                if (!built.Ok) return built.As<RepRosterRow>();
                profile = built.Value!;
                created = true;
            }
            else
            {
                RepresentativeCandidateProfileEntity? found;
                if (req.ProfileId.HasValue)
                    found = await _db.RepresentativeCandidateProfiles.FirstOrDefaultAsync(p => p.RepresentativeCandidateProfileId == req.ProfileId.Value, ct);
                else
                {
                    var code = CandidateCodeRules.Normalize(req.CandidateCode);
                    found = await _db.RepresentativeCandidateProfiles.FirstOrDefaultAsync(p => p.CandidateCode == code, ct);
                }
                if (found == null) return ServiceResult<RepRosterRow>.NotFound("Candidate profile");
                if (!found.IsActive)
                    return ServiceResult<RepRosterRow>.Failure(StatusCodes.Status409Conflict,
                        $"{found.CandidateName} ({found.CandidateCode}) is deactivated. Reactivate the profile before assigning it.", RepCodes.ProfileInactive);
                profile = found;
            }

            // ── already there? ballot number free? room left? (all under the event lock) ──
            if (!created)
            {
                var pid = profile.RepresentativeCandidateProfileId;
                if (await _db.RepresentativeEventCandidates.AsNoTracking().AnyAsync(c => c.EventId == eventId && c.Position == position && c.RepresentativeCandidateProfileId == pid, ct))
                    return ServiceResult<RepRosterRow>.Failure(StatusCodes.Status409Conflict,
                        $"{profile.CandidateName} ({profile.CandidateCode}) is already on this election's roster for '{position}'.", RepCodes.AlreadyAssigned);
            }
            if (req.BallotNumber.HasValue)
            {
                var number = req.BallotNumber.Value;
                if (await _db.RepresentativeEventCandidates.AsNoTracking().AnyAsync(c => c.EventId == eventId && c.Position == position && c.BallotNumber == number, ct))
                    return ServiceResult<RepRosterRow>.Failure(StatusCodes.Status409Conflict,
                        $"Ballot number {number} is already used at '{position}'.", RepCodes.BallotNumberTaken, null,
                        new() { ["ballotNumber"] = "This ballot number is already used at this position." });
            }
            if (rule.MaxCandidates.HasValue)
            {
                int approved = await _db.RepresentativeEventCandidates.AsNoTracking()
                    .CountAsync(c => c.EventId == eventId && c.Position == position && c.Status == RepresentativeCandidateStatuses.Approved, ct);
                if (approved >= rule.MaxCandidates.Value)
                    return ServiceResult<RepRosterRow>.Failure(StatusCodes.Status409Conflict,
                        $"The candidate limit for '{position}' has been reached ({approved} of {rule.MaxCandidates.Value}); this candidate was not added.",
                        RepCodes.PositionFull, "Raise the limit in the election's position rules, or remove a candidate from this position first.");
            }

            // ── write ──
            var now = DateTime.Now;
            try
            {
                if (created) { _db.RepresentativeCandidateProfiles.Add(profile); await _db.SaveChangesAsync(ct); }
                var row = new RepresentativeEventCandidateEntity
                {
                    EventId = eventId, RepresentativeCandidateProfileId = profile.RepresentativeCandidateProfileId, Position = position, Reason = reason,
                    BallotNumber = req.BallotNumber, Status = RepresentativeCandidateStatuses.Approved, ImportBatchId = null, CreatedAt = now, UpdatedAt = now
                };
                _db.RepresentativeEventCandidates.Add(row);
                await _db.SaveChangesAsync(ct);
                _audit.Stage(AuditActions.AssignRepCandidate, AuditEntities.RepresentativeRoster, row.RepresentativeEventCandidateId,
                    $"{profile.CandidateCode} '{profile.CandidateName}' assigned to '{position}' in '{ev.Title}'" +
                    (created ? " (new profile)" : "") + (req.BallotNumber.HasValue ? $", ballot no. {req.BallotNumber}" : "") + ".");
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return await GetRowAsync(eventId, row.RepresentativeEventCandidateId, StatusCodes.Status201Created, ct);
            }
            catch (DbUpdateException ex) when (IsDuplicateKey(ex))
            {
                // The checks above ran under the event lock, so this is only a last line of defence (e.g. a second API instance, or a manual SQL insert).
                return ServiceResult<RepRosterRow>.Failure(StatusCodes.Status409Conflict,
                    "That candidate, code or ballot number is already in use on this roster.", RepCodes.AlreadyAssigned);
            }
        }

        public async Task<ServiceResult<RepRosterRow>> RemoveAsync(int eventId, int assignmentId, CancellationToken ct)
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var (ev, err) = await RepresentativeEventAsync<RepRosterRow>(eventId, true, ct);
            if (ev == null) return err!;

            var gate = await RosterQueries.LockAsync(_db, ev, ct);
            if (gate.Locked) return ServiceResult<RepRosterRow>.Locked(gate);

            var shown = await GetRowAsync(eventId, assignmentId, StatusCodes.Status200OK, ct);
            if (!shown.Ok) return shown;

            if (await _db.RepresentativeVotes.AsNoTracking().AnyAsync(v => v.RepresentativeEventCandidateId == assignmentId, ct))
                return ServiceResult<RepRosterRow>.Failure(StatusCodes.Status409Conflict,
                    "Votes have been recorded for this candidate, so the roster entry cannot be removed.", RepCodes.HasBallots);

            // Only the assignment goes. The profile stays, ready to be assigned to another election.
            await _db.Database.ExecuteSqlRawAsync("DELETE FROM RepresentativeEventCandidates WHERE RepresentativeEventCandidateId = {0} AND EventId = {1}",
                new object[] { assignmentId, eventId }, ct);
            var v = shown.Value!;
            _audit.Stage(AuditActions.RemoveRepCandidate, AuditEntities.RepresentativeRoster, assignmentId,
                $"{v.CandidateCode} '{v.CandidateName}' removed from '{v.Position}' in '{ev.Title}' (profile kept).");
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return shown;
        }

        private async Task<ServiceResult<RepRosterRow>> GetRowAsync(int eventId, int assignmentId, int status, CancellationToken ct)
        {
            var row = await _db.RepresentativeEventCandidates.AsNoTracking()
                .Where(c => c.EventId == eventId && c.RepresentativeEventCandidateId == assignmentId)
                .Select(c => new RepRosterRow
                {
                    AssignmentId = c.RepresentativeEventCandidateId,
                    ProfileId = c.RepresentativeCandidateProfileId,
                    CandidateCode = c.Profile!.CandidateCode,
                    CandidateName = c.Profile.CandidateName,
                    AccountCode = c.Profile.Member != null ? c.Profile.Member.AccountCode : null,
                    Position = c.Position,
                    BallotNumber = c.BallotNumber,
                    Status = c.Status,
                    Reason = c.Reason,
                    HasPhoto = c.Profile.PhotoUpdatedAt != null,
                    PhotoUpdatedAt = c.Profile.PhotoUpdatedAt,
                    ImportBatchId = c.ImportBatchId,
                    CreatedAt = c.CreatedAt
                }).FirstOrDefaultAsync(ct);
            return row == null ? ServiceResult<RepRosterRow>.NotFound("Roster entry") : ServiceResult<RepRosterRow>.Success(row, status);
        }

        public async Task<ServiceResult<RepRosterSummaryResponse>> UnlockAsync(int eventId, RepRosterUnlockRequest req, CancellationToken ct)
        {
            if (!req.Confirm)
                return ServiceResult<RepRosterSummaryResponse>.Failure(StatusCodes.Status400BadRequest,
                    "Unlocking the roster needs the administrator's explicit confirmation.", RepCodes.NotConfirmed);
            var reason = RepText.OneLine(req.Reason);
            if (reason == null)
                return ServiceResult<RepRosterSummaryResponse>.Invalid("reason", "Give the reason for unlocking the roster; it is written to the audit log.");

            await using (var tx = await _db.Database.BeginTransactionAsync(ct))
            {
                var (ev, err) = await RepresentativeEventAsync<RepRosterSummaryResponse>(eventId, true, ct);
                if (ev == null) return err!;

                if (ev.Status.Equals("Opened", StringComparison.OrdinalIgnoreCase))
                    return ServiceResult<RepRosterSummaryResponse>.Failure(StatusCodes.Status409Conflict,
                        "The election is open. Close it first.", RepCodes.EventOpenLock);
                if (!ev.RosterLockedAt.HasValue)
                    return ServiceResult<RepRosterSummaryResponse>.Failure(StatusCodes.Status409Conflict,
                        "The roster is not locked; it can already be edited.", RepCodes.NotLocked);
                var ballots = await RosterQueries.BallotCountAsync(_db, ev, ct);
                if (ballots > 0)
                    return ServiceResult<RepRosterSummaryResponse>.Failure(StatusCodes.Status409Conflict,
                        $"{ballots} ballot(s) have been cast; the roster of an election with votes can never be unlocked.", RepCodes.HasBallots);

                var lockedAt = ev.RosterLockedAt.Value;
                ev.RosterLockedAt = null;
                ev.UpdatedAt = DateTime.Now;
                _audit.Stage(AuditActions.RosterUnlocked, AuditEntities.Event, eventId,
                    $"Candidate roster of '{ev.Title}' (locked {lockedAt:yyyy-MM-dd HH:mm}) unlocked; no ballots had been cast. Reason: {reason}");
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            return await SummaryAsync(eventId, ct);
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────
        private static bool IsDuplicateKey(DbUpdateException ex) =>
            ex.GetBaseException() is MySqlException { ErrorCode: MySqlErrorCode.DuplicateKeyEntry };
    }
}
