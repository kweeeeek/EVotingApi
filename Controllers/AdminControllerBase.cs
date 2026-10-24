using EVoting.Api.Models.Admin;
using EVoting.Api.Models.Responses;
using EVoting.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Base for the Admin-only management controllers. Authentication = JWT with role "Admin";
    /// fine-grained authorization = [RequirePermission] on each action (checked against the DATABASE, not the token).
    /// Invalid models are answered with the standard ApiError body (see <see cref="ApiErrorModelStateAttribute"/>).
    /// </summary>
    [ApiController]
    [Authorize(Roles = "Admin")]
    [ApiErrorModelState]
    public abstract class AdminControllerBase : ControllerBase
    {
        protected const int MaxPageSize = 200;
        protected const int MaxPhotoBytes = 4 * 1024 * 1024;

        // ── Error helpers (all use the standard ApiError body) ───────────────
        protected ObjectResult Fail(int status, string error, string? code = null, string? detail = null,
                                    Dictionary<string, string>? fields = null) =>
            new(new ApiError { Error = error, Code = code, Detail = detail, Fields = fields }) { StatusCode = status };

        protected ObjectResult Invalid(string field, string message) =>
            Fail(StatusCodes.Status400BadRequest, message, "VALIDATION", null, new() { [field] = message });

        protected ObjectResult Invalid(Dictionary<string, string> fields) =>
            Fail(StatusCodes.Status400BadRequest, fields.Values.First(), "VALIDATION", null, fields);

        protected ObjectResult NotFoundError(string what) =>
            Fail(StatusCodes.Status404NotFound, $"{what} not found. It may have been deleted.", "NOT_FOUND");

        protected ObjectResult ConcurrencyError(string what) =>
            Fail(StatusCodes.Status409Conflict,
                 $"This {what} was changed by someone else after you opened it.",
                 "CONCURRENCY", "Reload the record and re-apply your changes.");

        protected ObjectResult Duplicate(string message, string? field = null) =>
            Fail(StatusCodes.Status409Conflict, message, "DUPLICATE", null,
                 field == null ? null : new() { [field] = message });

        // ── Paging ───────────────────────────────────────────────────────────
        protected static (int Page, int PageSize) ClampPaging(int page, int pageSize, int max = MaxPageSize) =>
            (Math.Max(1, page), Math.Clamp(pageSize, 1, max));

        protected static PagedResponse<T> Paged<T>(List<T> items, int total, int page, int pageSize) => new()
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))
        };

        // ── Concurrency ──────────────────────────────────────────────────────
        /// <summary>True when the caller supplied the version it loaded and the row has since changed.</summary>
        protected static bool IsStale(DateTime? expected, DateTime actual) => Services.Concurrency.IsStale(expected, actual);

        // ── Caller identity ──────────────────────────────────────────────────
        protected int? CurrentUserId => RequirePermissionAttribute.UserIdOf(User);

        // ── Photos ───────────────────────────────────────────────────────────
        protected bool TryDecodePhoto(string? base64, out byte[]? bytes, out string? error)
        {
            bytes = null;
            error = null;
            if (string.IsNullOrWhiteSpace(base64)) return true;
            try
            {
                var raw = base64.Contains(',') ? base64[(base64.IndexOf(',') + 1)..] : base64;   // tolerate data: URIs
                bytes = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                error = "The photo is not valid Base64 image data.";
                return false;
            }
            if (bytes.Length > MaxPhotoBytes)
            {
                error = $"The photo is too large (max {MaxPhotoBytes / (1024 * 1024)} MB).";
                bytes = null;
                return false;
            }
            return true;
        }
    }
}
