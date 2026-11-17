using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using EVoting.Api.Security;
using EVoting.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EVoting.Api.Controllers
{
    /// <summary>
    /// Representative candidate import. The Admin reads the Excel file and sends rows; the server stages, validates (set-based), lets the
    /// administrator resolve conflicts, and commits valid rows in ONE transaction after an explicit confirmation.
    ///   upload → rows → validate → [resolve → …] → commit      (or cancel at any point before commit)
    /// Permissions: CANDIDATE_ADD runs the import; CANDIDATE_VIEW / EVENT_VIEW may read batches, previews and error reports.
    /// </summary>
    [Route("api/v1/admin/representative-imports")]
    public class RepresentativeImportAdminController : AdminControllerBase
    {
        private readonly IRepresentativeImportService _svc;
        private readonly RepresentativeImportOptions _options;

        public RepresentativeImportAdminController(IRepresentativeImportService svc, IOptions<RepresentativeImportOptions> options)
        {
            _svc = svc;
            _options = options.Value;
        }

        /// <summary>
        /// The file format and the configured limits. The Admin builds the downloadable template and reads uploads from this. REPORT_EXPORT may read it too:
        /// the roster export (REPORT_EXPORT or CANDIDATE_VIEW) uses the same column list, so a role that may export the roster must be able to read it.
        /// </summary>
        [HttpGet("template")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateAdd, PermissionCodes.ReportExport)]
        public IActionResult Template()
        {
            RepImportTemplateColumn C(string header, string field, bool required, int max, string description, string example) =>
                new() { Header = header, Field = field, Required = required, MaxLength = max, Description = description, Example = example };

            return Ok(new RepImportTemplateResponse
            {
                MaxRowsPerBatch = _options.MaxRowsPerBatch,
                MaxRowsPerRequest = _options.MaxRowsPerRequest,
                MaxOpenBatchesPerEvent = _options.MaxOpenBatchesPerEvent,
                Columns = new List<RepImportTemplateColumn>
                {
                    C("CandidateCode", "candidateCode", true, RepresentativeLimits.CandidateCode,
                      "Stable code of the person (letters, digits and . _ / -). It recognises the same candidate in other elections.", "REP-0001"),
                    C("CandidateName", "candidateName", false, RepresentativeLimits.CandidateName,
                      "Full name. Required for a new candidate unless AccountCode names an existing member.", "Juan Dela Cruz"),
                    C("AccountCode", "accountCode", false, FieldLimits.AccountCode,
                      "Account code of an existing member, when the candidate is a member. Leave blank for non-members.", "0001234"),
                    C("Position", "position", true, FieldLimits.CandidatePosition,
                      "A position configured for this election (see its position rules).", "Delegate"),
                    C("CellNo", "cellNo", false, RepresentativeLimits.PhoneNumber, "Mobile number.", "09171234567"),
                    C("TelNo", "telNo", false, RepresentativeLimits.PhoneNumber, "Telephone number.", ""),
                    C("CivilStatus", "civilStatus", false, RepresentativeLimits.ShortText, $"One of: {string.Join(", ", CandidacyLists.CivilStatuses)}.", "Married"),
                    C("Gender", "gender", false, RepresentativeLimits.ShortText, $"One of: {string.Join(", ", CandidacyLists.Genders)}.", "Female"),
                    C("DateOfBirth", "dateOfBirth", false, 0, "Calendar date (yyyy-mm-dd or an Excel date).", "1980-05-17"),
                    C("HighestEducation", "highestEducation", false, RepresentativeLimits.Education, "Highest education attained.", "College Graduate"),
                    C("PreviousPositions", "previousPositions", false, RepresentativeLimits.PreviousPositions, "Previous positions held.", ""),
                    C("SpecialSkills", "specialSkills", false, RepresentativeLimits.SpecialSkills, "Special skills.", ""),
                    C("Reason", "reason", false, RepresentativeLimits.Reason, "Reason for candidacy (stored on the election assignment, not on the profile).", ""),
                    C("BallotNumber", "ballotNumber", false, 0, $"Printed ballot number, 1–{RepresentativeLimits.MaxBallotNumber}; unique within the position.", "12")
                },
                Notes = new List<string>
                {
                    "One row per candidate per position. The same candidate may appear on several rows with different positions.",
                    "A candidate whose CandidateCode already exists is REUSED; if the file disagrees with the stored profile you must decide per row (use the stored profile, update it, or skip).",
                    "Profile fields (name, phones, …) belong to the person; Position, Reason and BallotNumber belong to this election.",
                    $"A file may hold up to {_options.MaxRowsPerBatch:N0} rows.",
                    "Nothing is imported until you review the preview and confirm."
                }
            });
        }

        [HttpPost]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Create([FromBody] RepImportBatchCreateRequest req, CancellationToken ct) =>
            FromResult(await _svc.CreateBatchAsync(req, ct));

        [HttpPost("{batchId:int}/rows")]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> StageRows(int batchId, [FromBody] RepImportStageRequest req, CancellationToken ct) =>
            FromResult(await _svc.StageRowsAsync(batchId, req, ct));

        [HttpPost("{batchId:int}/validate")]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Validate(int batchId, CancellationToken ct) => FromResult(await _svc.ValidateAsync(batchId, ct));

        [HttpPost("{batchId:int}/resolve")]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Resolve(int batchId, [FromBody] RepImportResolveRequest req, CancellationToken ct) =>
            FromResult(await _svc.ResolveAsync(batchId, req, ct));

        [HttpPost("{batchId:int}/commit")]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Commit(int batchId, [FromBody] RepImportCommitRequest req, CancellationToken ct) =>
            FromResult(await _svc.CommitAsync(batchId, req, ct));

        [HttpPost("{batchId:int}/cancel")]
        [RequirePermission(PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Cancel(int batchId, CancellationToken ct) => FromResult(await _svc.CancelAsync(batchId, ct));

        [HttpGet("{batchId:int}")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Get(int batchId, CancellationToken ct) => FromResult(await _svc.GetAsync(batchId, ct));

        /// <summary>The staged rows with their outcome, filterable by status (VALID / INVALID / CONFLICT / SKIPPED / COMMITTED) and searchable.</summary>
        [HttpGet("{batchId:int}/rows")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> Rows(
            int batchId, [FromQuery] string? status, [FromQuery] string? search,
            [FromQuery] int page = 1, [FromQuery] int pageSize = 100, CancellationToken ct = default) =>
            FromResult(await _svc.PreviewAsync(batchId, status, search, page, pageSize, ct));

        /// <summary>CSV of the rows that need attention (all=false, default) or of every row (all=true).</summary>
        [HttpGet("{batchId:int}/error-report")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.CandidateAdd)]
        public async Task<IActionResult> ErrorReport(int batchId, [FromQuery] bool all = false, CancellationToken ct = default)
        {
            var r = await _svc.ErrorReportAsync(batchId, all, ct);
            return r.Ok ? File(r.Value.Content, "text/csv; charset=utf-8", r.Value.FileName) : FromResult(r);
        }

        /// <summary>Import history of one election (newest first): who uploaded which file, when, and what came of it.</summary>
        [HttpGet("~/api/v1/admin/events/{eventId:int}/representative-imports")]
        [RequirePermission(PermissionCodes.CandidateView, PermissionCodes.EventView)]
        public async Task<IActionResult> History(int eventId, [FromQuery] int take = 50, CancellationToken ct = default) =>
            Ok(await _svc.HistoryAsync(eventId, take, ct));
    }
}
