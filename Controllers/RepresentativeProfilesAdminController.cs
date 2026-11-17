using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Reusable Representative candidate PROFILES (the people). A profile is identified by its stable CandidateCode and can be placed on the
    /// roster of any number of Representative elections. Profiles are deactivated, never deleted. Permissions reuse the candidate codes:
    ///   CANDIDATE_VIEW (read) · CANDIDATE_ADD (create) · CANDIDATE_EDIT (change, photo, activate/deactivate).
    /// </summary>
    [Route("api/v1/admin/representative-profiles")]
    public class RepresentativeProfilesAdminController : AdminControllerBase
    {
        private readonly IRepresentativeRosterService _svc;

        public RepresentativeProfilesAdminController(IRepresentativeRosterService svc) => _svc = svc;

        [HttpGet]
        [RequirePermission(PermissionCodes.CandidateView)]
        public async Task<IActionResult> List(
            [FromQuery] string? search, [FromQuery] bool? active,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
            Ok(await _svc.ListProfilesAsync(search, active, page, pageSize, ct));

        [HttpGet("{id:int}")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> Get(int id, CancellationToken ct) => FromResult(await _svc.GetProfileAsync(id, ct));

        [HttpPost]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Create([FromBody] RepProfileUpsertRequest req, CancellationToken ct) =>
            FromResult(await _svc.CreateProfileAsync(req, ct));

        [HttpPut("{id:int}")]
        [RequirePermission(PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> Update(int id, [FromBody] RepProfileUpsertRequest req, CancellationToken ct) =>
            FromResult(await _svc.UpdateProfileAsync(id, req, ct));

        [HttpPut("{id:int}/active")]
        [RequirePermission(PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> SetActive(int id, [FromBody] RepProfileActiveRequest req, CancellationToken ct) =>
            FromResult(await _svc.SetActiveAsync(id, req.IsActive, req.ExpectedUpdatedAt, ct));

        [HttpPut("{id:int}/photo")]
        [RequirePermission(PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> SetPhoto(int id, [FromBody] RepProfilePhotoRequest req, CancellationToken ct)
        {
            if (!TryDecodePhoto(req.ImageBase64, out var image, out var error)) return Invalid("imageBase64", error!);
            if (!TryDecodePhoto(req.ThumbnailBase64, out var thumb, out error)) return Invalid("thumbnailBase64", error!);
            return FromResult(await _svc.SetPhotoAsync(id, image, thumb, ct));
        }

        /// <summary>size=thumb (default) returns the small version when one exists; size=full always the full image. ETag = the photo version stamp.</summary>
        [HttpGet("{id:int}/photo")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateEdit)]
        public async Task<IActionResult> Photo(int id, [FromQuery] string? size, CancellationToken ct)
        {
            var photo = await _svc.GetPhotoAsync(id, !string.Equals(size, "full", StringComparison.OrdinalIgnoreCase), ct);
            if (photo == null) return NoContent();

            var etag = $"\"{photo.Version.Ticks:x}-{(photo.IsThumbnail ? "t" : "f")}\"";
            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "private, max-age=300";
            if (Request.Headers.IfNoneMatch.Any(v => v == etag)) return StatusCode(StatusCodes.Status304NotModified);
            return File(photo.Bytes, photo.ContentType);
        }
    }
}
