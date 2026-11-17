using EVoting.Api.Infrastructure;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Models.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text;

namespace EVoting.Api.Services
{
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════
    //  Representative candidate import — batch lifecycle and database work.
    //
    //   create batch ─► stage rows (chunked, bulk) ─► validate ─► [resolve conflicts ─► validate again] ─► commit
    //
    //  * The client (Admin) reads the workbook; this service never receives a file. It receives rows, so one batch can be sent in several
    //    requests of up to RepresentativeImport:MaxRowsPerRequest rows and the Admin can show real progress.
    //  * Staging and result write-back are multi-row parameterised statements (never one SaveChanges per candidate).
    //  * What happens to each row is decided by RepresentativeImportPlanner (pure). Commit runs that planner AGAIN inside its transaction,
    //    under the event's row lock, and refuses (VALIDATION_STALE) if the answer differs from what the administrator previewed.
    //  * Commit is one transaction made of set-based INSERT…SELECT / UPDATE statements. Nothing is half-applied.
    //  * Nothing is overwritten silently: a file that disagrees with a stored profile becomes a CONFLICT that needs an explicit decision.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════

    public interface IRepresentativeImportService
    {
        Task<ServiceResult<RepImportBatchRow>> CreateBatchAsync(RepImportBatchCreateRequest req, CancellationToken ct);
        Task<ServiceResult<RepImportStageResponse>> StageRowsAsync(int batchId, RepImportStageRequest req, CancellationToken ct);
        Task<ServiceResult<RepImportBatchRow>> ValidateAsync(int batchId, CancellationToken ct);
        Task<ServiceResult<RepImportBatchRow>> ResolveAsync(int batchId, RepImportResolveRequest req, CancellationToken ct);
        Task<ServiceResult<RepImportBatchRow>> CommitAsync(int batchId, RepImportCommitRequest req, CancellationToken ct);
        Task<ServiceResult<RepImportBatchRow>> CancelAsync(int batchId, CancellationToken ct);
        Task<ServiceResult<RepImportBatchRow>> GetAsync(int batchId, CancellationToken ct);
        Task<RepImportHistoryResponse> HistoryAsync(int eventId, int take, CancellationToken ct);
        Task<ServiceResult<PagedResponse<RepImportPreviewRow>>> PreviewAsync(int batchId, string? status, string? search, int page, int pageSize, CancellationToken ct);
        Task<ServiceResult<(string FileName, byte[] Content)>> ErrorReportAsync(int batchId, bool allRows, CancellationToken ct);
    }

    public sealed class RepresentativeImportService : IRepresentativeImportService
    {
        private static readonly TimeSpan StaleValidation = TimeSpan.FromMinutes(10);
        private const int PersistChunk = 500;

        private readonly ApiDbContext _db;
        private readonly IAuditService _audit;
        private readonly RepresentativeImportOptions _opt;
        private readonly ILogger<RepresentativeImportService> _log;

        public RepresentativeImportService(ApiDbContext db, IAuditService audit, IOptions<RepresentativeImportOptions> options, ILogger<RepresentativeImportService> log)
        {
            _db = db;
            _audit = audit;
            _opt = options.Value;
            _log = log;
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Create
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        public async Task<ServiceResult<RepImportBatchRow>> CreateBatchAsync(RepImportBatchCreateRequest req, CancellationToken ct)
        {
            var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == req.EventId && e.IsActive, ct);
            if (ev == null) return ServiceResult<RepImportBatchRow>.NotFound("Event");
            if (ev.EventType != EventTypes.Representative)
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                    "Candidate import is only available for Representative elections.", RepCodes.WrongEventType);

            var gate = await RosterQueries.LockAsync(_db, ev, ct);
            if (gate.Locked) return ServiceResult<RepImportBatchRow>.Locked(gate);

            if (!await _db.EventPositionRules.AsNoTracking().AnyAsync(r => r.EventId == ev.EventId, ct))
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                    "This election has no position rules yet.", RepCodes.NoPositionRules, "Configure the positions (and their limits) first; every imported candidate is assigned to one of them.");

            if (req.ExpectedRows > _opt.MaxRowsPerBatch)
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status400BadRequest,
                    $"A file may hold at most {_opt.MaxRowsPerBatch:N0} candidate rows; this one has {req.ExpectedRows:N0}. Split it into several files.",
                    RepCodes.TooManyRows, null, new() { ["expectedRows"] = $"At most {_opt.MaxRowsPerBatch:N0} rows." });

            // A List (not an array): inside an EF query an array's Contains can bind to MemoryExtensions.Contains(ReadOnlySpan) under C# 14.
            var openStates = new List<string> { RepresentativeImportStatuses.Uploaded, RepresentativeImportStatuses.Validating, RepresentativeImportStatuses.Validated,
                                                RepresentativeImportStatuses.Invalid, RepresentativeImportStatuses.Failed };
            var openCount = await _db.RepresentativeImportBatches.AsNoTracking().CountAsync(b => b.EventId == ev.EventId && openStates.Contains(b.Status), ct);
            if (openCount >= _opt.MaxOpenBatchesPerEvent)
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                    $"This election already has {openCount} unfinished import batches.", RepCodes.TooManyOpenBatches, "Commit or cancel some of them first.");

            var actor = _audit.Actor;
            var now = DateTime.Now;
            var batch = new RepresentativeImportBatchEntity
            {
                EventId = ev.EventId,
                FileName = Clip(req.FileName.Trim(), 255)!,
                FileSha256 = string.IsNullOrWhiteSpace(req.FileSha256) ? null : Clip(req.FileSha256.Trim().ToLowerInvariant(), 64),
                Status = RepresentativeImportStatuses.Uploaded,
                ExpectedRows = req.ExpectedRows,
                CreatedByUserId = actor.UserId,
                CreatedByUsername = Clip(actor.Username, 50),
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.RepresentativeImportBatches.Add(batch);
            await _db.SaveChangesAsync(ct);                       // need the generated id for the audit row

            _audit.Stage(AuditActions.RepImportUploaded, AuditEntities.RepresentativeImport, batch.ImportBatchId,
                $"Import batch {batch.ImportBatchId} created for '{ev.Title}': file '{batch.FileName}', {batch.ExpectedRows} row(s) announced.");
            await _db.SaveChangesAsync(ct);

            return ServiceResult<RepImportBatchRow>.Success(ToRow(batch, ev.Title), StatusCodes.Status201Created);
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Stage rows
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        private static readonly string[] StageColumns =
        {
            "ImportBatchId", "RowNumber", "CandidateCode", "CandidateName", "AccountCode", "Position", "CellNo", "TelNo", "CivilStatus", "Gender",
            "DateOfBirth", "HighestEducation", "PreviousPositions", "SpecialSkills", "Reason", "BallotNumber", "RowStatus", "ErrorCodes", "ErrorMessage"
        };

        public async Task<ServiceResult<RepImportStageResponse>> StageRowsAsync(int batchId, RepImportStageRequest req, CancellationToken ct)
        {
            var batch = await _db.RepresentativeImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
            if (batch == null) return ServiceResult<RepImportStageResponse>.NotFound("Import batch");
            if (batch.Status != RepresentativeImportStatuses.Uploaded)
                return ServiceResult<RepImportStageResponse>.Failure(StatusCodes.Status409Conflict,
                    $"Rows can only be added to a batch that has not been validated yet (this one is {batch.Status}).", RepCodes.BatchState,
                    "Create a new batch to upload a different file.");

            if (req.Rows.Count > _opt.MaxRowsPerRequest)
                return ServiceResult<RepImportStageResponse>.Failure(StatusCodes.Status400BadRequest,
                    $"At most {_opt.MaxRowsPerRequest:N0} rows can be sent in one request; send the file in several parts.", RepCodes.TooManyRows);

            var numbers = req.Rows.Select(r => r.RowNumber).ToList();
            if (numbers.Any(n => n < 1 || n > 1_048_576))
                return ServiceResult<RepImportStageResponse>.Invalid("rows", "Row numbers must be between 1 and 1,048,576.");
            if (numbers.Distinct().Count() != numbers.Count)
                return ServiceResult<RepImportStageResponse>.Invalid("rows", "The same row number appears twice in this request.");

            var ev = await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.EventId == batch.EventId && e.IsActive, ct);
            if (ev == null) return ServiceResult<RepImportStageResponse>.NotFound("Event");
            var gate = await RosterQueries.LockAsync(_db, ev, ct);
            if (gate.Locked) return ServiceResult<RepImportStageResponse>.Locked(gate);

            // A retried request re-sends row numbers that may already be staged: those overwrite (idempotent) and do not count twice.
            var already = await _db.RepresentativeImportRows.AsNoTracking()
                .Where(r => r.ImportBatchId == batchId && numbers.Contains(r.RowNumber)).CountAsync(ct);
            var stagedNow = await _db.RepresentativeImportRows.AsNoTracking().CountAsync(r => r.ImportBatchId == batchId, ct);
            if (stagedNow - already + req.Rows.Count > _opt.MaxRowsPerBatch)
                return ServiceResult<RepImportStageResponse>.Failure(StatusCodes.Status400BadRequest,
                    $"A batch may hold at most {_opt.MaxRowsPerBatch:N0} rows.", RepCodes.TooManyRows);

            var today = DateTime.Now.Date;
            var staged = new List<object?[]>(req.Rows.Count);
            foreach (var row in req.Rows)
            {
                var chk = RepresentativeImportRowRules.Check(row, today);
                var rawCode = CandidateCodeRules.Normalize(RepText.OneLine(row.CandidateCode));
                staged.Add(new object?[]
                {
                    batchId, row.RowNumber,
                    Clip(chk.CandidateCode ?? (rawCode.Length == 0 ? null : rawCode), 30),
                    Clip(chk.CandidateName, 200), Clip(chk.AccountCode, 20), Clip(chk.Position, 100),
                    Clip(chk.CellNo, 20), Clip(chk.TelNo, 20), Clip(chk.CivilStatus, 20), Clip(chk.Gender, 20),
                    chk.DateOfBirth,
                    Clip(chk.HighestEducation, 200), Clip(chk.PreviousPositions, 500), Clip(chk.SpecialSkills, 500), Clip(chk.Reason, 2000),
                    chk.BallotNumber,
                    chk.HasErrors ? RepresentativeImportRowStatuses.Invalid : RepresentativeImportRowStatuses.Pending,
                    chk.HasErrors ? Clip(chk.ErrorCodes, 500) : null,
                    chk.HasErrors ? Clip(chk.ErrorMessage, 1000) : null
                });
            }

            var sqlHead = "INSERT INTO RepresentativeCandidateImportRows (" + string.Join(", ", StageColumns) + ") VALUES ";
            var sqlTail =
                " ON DUPLICATE KEY UPDATE CandidateCode = VALUES(CandidateCode), CandidateName = VALUES(CandidateName), AccountCode = VALUES(AccountCode)," +
                " Position = VALUES(Position), CellNo = VALUES(CellNo), TelNo = VALUES(TelNo), CivilStatus = VALUES(CivilStatus), Gender = VALUES(Gender)," +
                " DateOfBirth = VALUES(DateOfBirth), HighestEducation = VALUES(HighestEducation), PreviousPositions = VALUES(PreviousPositions)," +
                " SpecialSkills = VALUES(SpecialSkills), Reason = VALUES(Reason), BallotNumber = VALUES(BallotNumber), RowStatus = VALUES(RowStatus)," +
                " ErrorCodes = VALUES(ErrorCodes), ErrorMessage = VALUES(ErrorMessage), PlannedAction = NULL, MatchedProfileId = NULL, Resolution = NULL";

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                foreach (var chunk in staged.Chunk(Math.Max(1, _opt.InsertChunkRows)))
                {
                    var parameters = new List<object?>(chunk.Length * StageColumns.Length);
                    var tuples = new StringBuilder();
                    foreach (var values in chunk)
                    {
                        if (tuples.Length > 0) tuples.Append(", ");
                        tuples.Append('(');
                        for (int i = 0; i < values.Length; i++)
                        {
                            if (i > 0) tuples.Append(", ");
                            tuples.Append('{').Append(parameters.Count).Append('}');
                            parameters.Add(values[i]);
                        }
                        tuples.Append(')');
                    }
                    await _db.Database.ExecuteSqlRawAsync(sqlHead + tuples + sqlTail, parameters.Cast<object>().ToArray()!, ct);
                }

                var total = await _db.RepresentativeImportRows.CountAsync(r => r.ImportBatchId == batchId, ct);
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE RepresentativeCandidateImportBatches SET TotalRows = {0}, UpdatedAt = {1} WHERE ImportBatchId = {2}",
                    new object[] { total, DateTime.Now, batchId }, ct);
                await tx.CommitAsync(ct);
                return ServiceResult<RepImportStageResponse>.Success(new RepImportStageResponse { Accepted = req.Rows.Count, TotalStaged = total });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Staging rows of import batch {Batch} failed.", batchId);
                return ServiceResult<RepImportStageResponse>.Failure(StatusCodes.Status500InternalServerError,
                    "The rows could not be stored. Nothing from this request was kept; send it again.", RepCodes.ImportFailed);
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Validate / resolve
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        public async Task<ServiceResult<RepImportBatchRow>> ValidateAsync(int batchId, CancellationToken ct)
        {
            var claim = await ClaimForValidationAsync(batchId, ct);
            if (!claim.Ok) return claim.As<RepImportBatchRow>();
            return await RunValidationAsync(batchId, ct);
        }

        private async Task<ServiceResult<RepresentativeImportBatchEntity>> ClaimForValidationAsync(int batchId, CancellationToken ct)
        {
            var exists = await _db.RepresentativeImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
            if (exists == null) return ServiceResult<RepresentativeImportBatchEntity>.NotFound("Import batch");

            var stale = DateTime.Now - StaleValidation;
            var claimed = await _db.Database.ExecuteSqlRawAsync(
                "UPDATE RepresentativeCandidateImportBatches SET Status = {0}, UpdatedAt = {1} WHERE ImportBatchId = {2} AND " +
                "(Status IN ({3}, {4}, {5}, {6}) OR (Status = {0} AND UpdatedAt < {7}))",
                new object[] { RepresentativeImportStatuses.Validating, DateTime.Now, batchId,
                               RepresentativeImportStatuses.Uploaded, RepresentativeImportStatuses.Validated, RepresentativeImportStatuses.Invalid,
                               RepresentativeImportStatuses.Failed, stale }, ct);
            if (claimed == 0)
                return ServiceResult<RepresentativeImportBatchEntity>.Failure(StatusCodes.Status409Conflict,
                    $"This batch is {exists.Status}; it cannot be validated now.", RepCodes.BatchState,
                    exists.Status == RepresentativeImportStatuses.Validating ? "Validation is already running." : null);
            return ServiceResult<RepresentativeImportBatchEntity>.Success(exists);
        }

        /// <summary>Plans the staged rows, stores the outcome on every row and settles the batch (VALIDATED / INVALID / FAILED). The caller has claimed the batch (VALIDATING).</summary>
        private async Task<ServiceResult<RepImportBatchRow>> RunValidationAsync(int batchId, CancellationToken ct)
        {
            _db.ChangeTracker.Clear();
            var batch = await _db.RepresentativeImportBatches.FirstAsync(b => b.ImportBatchId == batchId, ct);
            var ev = await _db.Events.AsNoTracking().FirstAsync(e => e.EventId == batch.EventId, ct);
            try
            {
                var stagedCount = await _db.RepresentativeImportRows.AsNoTracking().CountAsync(r => r.ImportBatchId == batchId, ct);
                if (stagedCount == 0 || stagedCount != batch.ExpectedRows)
                {
                    batch.Status = RepresentativeImportStatuses.Uploaded;
                    batch.UpdatedAt = DateTime.Now;
                    batch.Message = stagedCount == 0
                        ? "No rows have been uploaded to this batch yet."
                        : $"The file announced {batch.ExpectedRows:N0} row(s) but {stagedCount:N0} arrived. Upload the file again so no candidate is missed.";
                    await _db.SaveChangesAsync(ct);
                    return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict, batch.Message, "ROWS_MISSING");
                }

                var gate = await RosterQueries.LockAsync(_db, ev, ct);
                if (gate.Locked)
                {
                    batch.Status = RepresentativeImportStatuses.Uploaded;
                    batch.UpdatedAt = DateTime.Now;
                    await _db.SaveChangesAsync(ct);
                    return ServiceResult<RepImportBatchRow>.Locked(gate);
                }

                var (results, summary) = await PlanAsync(batch, ct);
                await PersistPlanAsync(batchId, results, ct);

                batch.TotalRows = summary.Total;
                batch.ValidRows = summary.Valid;
                batch.InvalidRows = summary.Invalid;
                batch.ConflictRows = summary.Conflict;
                batch.NewProfiles = summary.NewProfiles;
                batch.ReusedProfiles = summary.ReusedProfiles;
                batch.NewAssignments = summary.NewAssignments;
                batch.Status = summary.Valid > 0 || summary.Conflict > 0 ? RepresentativeImportStatuses.Validated : RepresentativeImportStatuses.Invalid;
                batch.ValidatedAt = DateTime.Now;
                batch.UpdatedAt = batch.ValidatedAt.Value;
                batch.Message = Describe(summary);
                _audit.Stage(AuditActions.RepImportValidated, AuditEntities.RepresentativeImport, batchId, $"Import batch {batchId} validated. {batch.Message}");
                await _db.SaveChangesAsync(ct);
                return ServiceResult<RepImportBatchRow>.Success(ToRow(batch, ev.Title));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Validation of import batch {Batch} failed.", batchId);
                await MarkFailedAsync(batchId, "Validation could not be completed. Try again; if it keeps failing the server log has the reason.", ct);
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status500InternalServerError, "The file could not be validated.", RepCodes.ImportFailed);
            }
        }

        public async Task<ServiceResult<RepImportBatchRow>> ResolveAsync(int batchId, RepImportResolveRequest req, CancellationToken ct)
        {
            var batch = await _db.RepresentativeImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
            if (batch == null) return ServiceResult<RepImportBatchRow>.NotFound("Import batch");
            if (batch.Status != RepresentativeImportStatuses.Validated)
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                    $"Conflicts can only be resolved on a validated batch (this one is {batch.Status}).", RepCodes.BatchState);
            if (Concurrency.IsStale(req.ExpectedUpdatedAt, batch.UpdatedAt)) return ServiceResult<RepImportBatchRow>.Concurrency("import batch");

            var defaultDecision = string.IsNullOrWhiteSpace(req.DefaultResolution) ? null : ImportResolutions.Normalize(req.DefaultResolution);
            if (!string.IsNullOrWhiteSpace(req.DefaultResolution) && defaultDecision == null)
                return ServiceResult<RepImportBatchRow>.Invalid("defaultResolution", $"Resolution must be one of: {string.Join(", ", ImportResolutions.All)}.");

            var perRow = new Dictionary<int, string>();
            for (int i = 0; i < req.Items.Count; i++)
            {
                var d = ImportResolutions.Normalize(req.Items[i].Resolution);
                if (d == null) return ServiceResult<RepImportBatchRow>.Invalid($"items[{i}].resolution", $"Resolution must be one of: {string.Join(", ", ImportResolutions.All)}.");
                perRow[req.Items[i].RowNumber] = d;
            }
            if (perRow.Count == 0 && defaultDecision == null)
                return ServiceResult<RepImportBatchRow>.Invalid("items", "Choose a decision for at least one row.");

            // Only rows that actually are (or were) conflicts can carry a decision.
            if (perRow.Count > 0)
            {
                var numbers = perRow.Keys.ToList();
                var decidable = await _db.RepresentativeImportRows.AsNoTracking()
                    .Where(r => r.ImportBatchId == batchId && numbers.Contains(r.RowNumber) &&
                                (r.RowStatus == RepresentativeImportRowStatuses.Conflict || r.Resolution != null))
                    .Select(r => r.RowNumber).ToListAsync(ct);
                var bad = numbers.Except(decidable).OrderBy(n => n).Take(5).ToList();
                if (bad.Count > 0)
                    return ServiceResult<RepImportBatchRow>.Invalid("items", $"Row(s) {string.Join(", ", bad)} have no conflict to resolve.");
            }

            var claim = await ClaimForValidationAsync(batchId, ct);
            if (!claim.Ok) return claim.As<RepImportBatchRow>();

            try
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                foreach (var group in perRow.GroupBy(kv => kv.Value))
                {
                    var rowNumbers = group.Select(kv => kv.Key).ToList();
                    await _db.RepresentativeImportRows.Where(r => r.ImportBatchId == batchId && rowNumbers.Contains(r.RowNumber))
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.Resolution, group.Key), ct);
                }
                if (defaultDecision != null)
                {
                    var explicitRows = perRow.Keys.ToList();
                    await _db.RepresentativeImportRows
                        .Where(r => r.ImportBatchId == batchId && r.RowStatus == RepresentativeImportRowStatuses.Conflict && !explicitRows.Contains(r.RowNumber))
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.Resolution, defaultDecision), ct);
                }
                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Saving conflict decisions of import batch {Batch} failed.", batchId);
                await MarkStatusAsync(batchId, RepresentativeImportStatuses.Validated, ct);
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status500InternalServerError, "The decisions could not be saved.", RepCodes.ImportFailed);
            }

            // The decisions change what happens to rows (and the position capacity), so the whole plan is computed again.
            return await RunValidationAsync(batchId, ct);
        }

        // ── planning ─────────────────────────────────────────────────────────────────────────────────────────
        private async Task<(List<PlanResult> Results, PlanSummary Summary)> PlanAsync(RepresentativeImportBatchEntity batch, CancellationToken ct)
        {
            var rows = await _db.RepresentativeImportRows.AsNoTracking()
                .Where(r => r.ImportBatchId == batch.ImportBatchId && r.RowStatus != RepresentativeImportRowStatuses.Committed)
                .OrderBy(r => r.RowNumber)
                .Select(r => new
                {
                    r.ImportRowId, r.RowNumber, r.CandidateCode, r.CandidateName, r.AccountCode, r.Position, r.CellNo, r.TelNo, r.CivilStatus, r.Gender,
                    r.DateOfBirth, r.HighestEducation, r.PreviousPositions, r.SpecialSkills, r.Reason, r.BallotNumber, r.RowStatus, r.ErrorCodes, r.ErrorMessage, r.Resolution
                })
                .ToListAsync(ct);

            var planRows = rows.Select(r =>
            {
                // A row that failed the field checks when it was staged keeps those errors; anything else is judged afresh.
                bool fieldLevel = r.RowStatus == RepresentativeImportRowStatuses.Invalid && RepresentativeImportRowRules.IsFieldLevel(r.ErrorCodes);
                return new PlanRow(r.ImportRowId, r.RowNumber, r.CandidateCode, r.CandidateName, r.AccountCode, r.Position, r.CellNo, r.TelNo,
                    r.CivilStatus, r.Gender, r.DateOfBirth, r.HighestEducation, r.PreviousPositions, r.SpecialSkills, r.Reason, r.BallotNumber,
                    fieldLevel ? r.ErrorCodes : null, fieldLevel ? r.ErrorMessage : null, r.Resolution);
            }).ToList();

            var ctx = await LoadContextAsync(batch, ct);
            return RepresentativeImportPlanner.Plan(planRows, ctx);
        }

        private async Task<PlanContext> LoadContextAsync(RepresentativeImportBatchEntity batch, CancellationToken ct)
        {
            int batchId = batch.ImportBatchId, eventId = batch.EventId;

            var rules = await _db.EventPositionRules.AsNoTracking().Where(r => r.EventId == eventId)
                .Select(r => new PlanRule(r.Position, r.MaxCandidates)).ToListAsync(ct);

            // Profiles whose code appears in this file (semi-join against the staging table; no code list is sent to the server).
            var profiles = await _db.RepresentativeCandidateProfiles.AsNoTracking()
                .Where(p => _db.RepresentativeImportRows.Any(r => r.ImportBatchId == batchId && r.CandidateCode == p.CandidateCode))
                .Select(p => new PlanProfile(
                    p.RepresentativeCandidateProfileId, p.CandidateCode, p.CandidateName, p.MemberId,
                    p.CellNo, p.TelNo, p.CivilStatus, p.Gender, p.DateOfBirth, p.HighestEducation, p.PreviousPositions, p.SpecialSkills,
                    p.IsActive,
                    _db.RepresentativeEventCandidates.Any(c => c.RepresentativeCandidateProfileId == p.RepresentativeCandidateProfileId && c.Event!.Status == "Opened"),
                    _db.RepresentativeVotes.Any(v => v.EventCandidate!.RepresentativeCandidateProfileId == p.RepresentativeCandidateProfileId)))
                .ToListAsync(ct);

            var members = await _db.Members.AsNoTracking()
                .Where(m => m.IsActive && _db.RepresentativeImportRows.Any(r => r.ImportBatchId == batchId && r.AccountCode == m.AccountCode))
                .Select(m => new
                {
                    m.MemberId, m.AccountCode, m.MemberName,
                    Linked = _db.RepresentativeCandidateProfiles.Where(p => p.MemberId == m.MemberId)
                        .Select(p => new { p.RepresentativeCandidateProfileId, p.CandidateCode }).FirstOrDefault()
                })
                .ToListAsync(ct);

            var roster = await _db.RepresentativeEventCandidates.AsNoTracking().Where(c => c.EventId == eventId)
                .Select(c => new { c.RepresentativeCandidateProfileId, c.Position, c.BallotNumber, c.Status }).ToListAsync(ct);

            var approved = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in roster.Where(c => c.Status == RepresentativeCandidateStatuses.Approved).GroupBy(c => c.Position.Trim(), StringComparer.OrdinalIgnoreCase))
                approved[g.Key] = g.Count();

            return new PlanContext
            {
                Rules = rules,
                ProfilesByCode = profiles.ToDictionary(p => p.Code.ToUpperInvariant()),
                MembersByAccount = members.GroupBy(m => m.AccountCode, StringComparer.OrdinalIgnoreCase).ToDictionary(
                    g => g.Key,
                    g => new PlanMember(g.First().MemberId, g.First().AccountCode, g.First().MemberName,
                                        g.First().Linked?.RepresentativeCandidateProfileId, g.First().Linked?.CandidateCode),
                    StringComparer.OrdinalIgnoreCase),
                ExistingAssignments = roster.Select(c => (c.RepresentativeCandidateProfileId, c.Position.Trim().ToUpperInvariant())).ToHashSet(),
                ApprovedByPosition = approved,
                ExistingBallotNumbers = roster.Where(c => c.BallotNumber.HasValue)
                    .Select(c => (c.Position.Trim().ToUpperInvariant(), c.BallotNumber!.Value)).ToHashSet()
            };
        }

        /// <summary>Writes the planner's verdict onto the staged rows, 500 rows per statement.</summary>
        private async Task PersistPlanAsync(int batchId, IReadOnlyList<PlanResult> results, CancellationToken ct)
        {
            foreach (var chunk in results.Chunk(PersistChunk))
            {
                var parameters = new List<object?>(chunk.Length * 9);
                var tuples = new StringBuilder();
                foreach (var r in chunk)
                {
                    if (tuples.Length > 0) tuples.Append(", ");
                    tuples.Append('(');
                    int start = parameters.Count;
                    // Only the OUTCOME is written back (plus the position in the rule's own spelling). The staged cells stay exactly as the file
                    // gave them — they are what a later validation, a conflict decision and the commit itself work from.
                    object?[] values =
                    {
                        r.ImportRowId, batchId, r.RowNumber, r.RowStatus, r.PlannedAction, r.MatchedProfileId,
                        Clip(r.CanonicalPosition, 100), Clip(r.ErrorCodes, 500), Clip(r.ErrorMessage, 1000)
                    };
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (i > 0) tuples.Append(", ");
                        tuples.Append('{').Append(start + i).Append('}');
                        parameters.Add(values[i]);
                    }
                    tuples.Append(')');
                }
                await _db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO RepresentativeCandidateImportRows (ImportRowId, ImportBatchId, RowNumber, RowStatus, PlannedAction, MatchedProfileId, Position, ErrorCodes, ErrorMessage) VALUES " +
                    tuples +
                    " ON DUPLICATE KEY UPDATE RowStatus = VALUES(RowStatus), PlannedAction = VALUES(PlannedAction), MatchedProfileId = VALUES(MatchedProfileId)," +
                    " Position = VALUES(Position), ErrorCodes = VALUES(ErrorCodes), ErrorMessage = VALUES(ErrorMessage)",
                    parameters.Cast<object>().ToArray()!, ct);
            }
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Commit
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        public async Task<ServiceResult<RepImportBatchRow>> CommitAsync(int batchId, RepImportCommitRequest req, CancellationToken ct)
        {
            if (!req.Confirm)
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status400BadRequest,
                    "Committing needs the administrator's explicit confirmation after reviewing the preview.", RepCodes.NotConfirmed);

            _db.ChangeTracker.Clear();
            var head = await _db.RepresentativeImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
            if (head == null) return ServiceResult<RepImportBatchRow>.NotFound("Import batch");

            string eventTitle = head.EventId.ToString(CultureInfo.InvariantCulture);
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                // Same lock order as every roster change (event row first): assignments, rule edits, opening and this commit are serialised.
                var lockedEvent = (await _db.Events
                    .FromSqlRaw("SELECT * FROM Events WHERE EventId = {0} AND IsActive = 1 FOR UPDATE", head.EventId).ToListAsync(ct)).FirstOrDefault();
                if (lockedEvent == null) return ServiceResult<RepImportBatchRow>.NotFound("Event");
                eventTitle = lockedEvent.Title;

                var batch = (await _db.RepresentativeImportBatches
                    .FromSqlRaw("SELECT * FROM RepresentativeCandidateImportBatches WHERE ImportBatchId = {0} FOR UPDATE", batchId).ToListAsync(ct)).First();

                if (batch.Status != RepresentativeImportStatuses.Validated)
                    return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                        $"This batch is {batch.Status}; only a validated batch can be committed.", RepCodes.BatchState);
                if (Concurrency.IsStale(req.ExpectedUpdatedAt, batch.UpdatedAt)) return ServiceResult<RepImportBatchRow>.Concurrency("import batch");
                if (lockedEvent.EventType != EventTypes.Representative)
                    return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict, "Not a Representative election.", RepCodes.WrongEventType);

                var gate = await RosterQueries.LockAsync(_db, lockedEvent, ct);
                if (gate.Locked) return ServiceResult<RepImportBatchRow>.Locked(gate);

                // Judge the file again against the data as it is NOW. The previewed outcome must still hold.
                var stored = await _db.RepresentativeImportRows.AsNoTracking()
                    .Where(r => r.ImportBatchId == batchId)
                    .Select(r => new { r.ImportRowId, r.RowStatus, r.PlannedAction, r.MatchedProfileId }).ToListAsync(ct);
                var (results, summary) = await PlanAsync(batch, ct);

                var storedById = stored.ToDictionary(s => s.ImportRowId);
                bool drifted = results.Any(r => !storedById.TryGetValue(r.ImportRowId, out var s) ||
                                                s.RowStatus != r.RowStatus || s.PlannedAction != r.PlannedAction || s.MatchedProfileId != r.MatchedProfileId);
                if (drifted)
                {
                    await PersistPlanAsync(batchId, results, ct);
                    batch.ValidRows = summary.Valid; batch.InvalidRows = summary.Invalid; batch.ConflictRows = summary.Conflict;
                    batch.NewProfiles = summary.NewProfiles; batch.ReusedProfiles = summary.ReusedProfiles; batch.NewAssignments = summary.NewAssignments;
                    batch.Status = summary.Valid > 0 || summary.Conflict > 0 ? RepresentativeImportStatuses.Validated : RepresentativeImportStatuses.Invalid;
                    batch.Message = "The roster or the candidate profiles changed after the preview. " + Describe(summary);
                    batch.UpdatedAt = DateTime.Now;
                    await _db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);                         // keeps the refreshed preview, changes no roster data
                    return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                        "The roster changed after you previewed this file, so nothing was imported. The preview has been refreshed — review it and confirm again.",
                        RepCodes.ValidationStale, batch.Message);
                }

                if (summary.Conflict > 0)
                    return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                        $"{summary.Conflict:N0} row(s) conflict with stored candidate profiles and still need a decision.", RepCodes.ConflictsUnresolved,
                        "Choose for each: use the stored profile, update it with the file's values, or skip the row.");
                if (req.RequireAllValid && summary.Invalid > 0)
                    return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                        $"{summary.Invalid:N0} row(s) are invalid and the import was requested to be all-or-nothing. Fix the file and upload it again, or commit the valid rows only.", "NOT_ALL_VALID");
                if (summary.Valid == 0)
                    return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict, "There is no valid row to import.", "NOTHING_TO_COMMIT");

                var now = DateTime.Now;
                await ApplyAsync(batch, now, ct);

                batch.Status = RepresentativeImportStatuses.Committed;
                batch.CommittedAt = now;
                batch.UpdatedAt = now;
                batch.ValidRows = summary.Valid; batch.InvalidRows = summary.Invalid; batch.ConflictRows = 0;
                batch.NewProfiles = summary.NewProfiles; batch.ReusedProfiles = summary.ReusedProfiles; batch.NewAssignments = summary.NewAssignments;
                batch.Message = $"Imported {summary.Valid:N0} candidate(s) ({summary.NewProfiles:N0} new profile(s), {summary.ReusedProfiles:N0} existing profile(s) reused" +
                                (summary.UpdatedProfiles > 0 ? $", {summary.UpdatedProfiles:N0} updated" : "") + ")" +
                                (summary.Invalid > 0 ? $"; {summary.Invalid:N0} invalid row(s) were not imported" : "") +
                                (summary.Skipped > 0 ? $"; {summary.Skipped:N0} skipped" : "") + ".";
                // The event row itself is not modified (its UpdatedAt is the Event editor's concurrency token; a roster import must not make
                // an open event editor "stale"). The roster has its own fingerprint — see RosterQueries.VersionAsync.
                _audit.Stage(AuditActions.RepImportCommitted, AuditEntities.RepresentativeImport, batchId,
                    $"Import batch {batchId} ('{batch.FileName}') committed to '{lockedEvent.Title}'. {batch.Message}");
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return ServiceResult<RepImportBatchRow>.Success(ToRow(batch, lockedEvent.Title));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "Committing import batch {Batch} failed; the transaction was rolled back.", batchId);
                try { await tx.RollbackAsync(CancellationToken.None); } catch { /* already rolled back */ }
                await MarkFailedAsync(batchId, "The import failed and was rolled back; no candidate was added. " + Clip(ex.GetBaseException().Message, 300), ct);
                await _audit.WriteAsync(AuditActions.RepImportFailed, AuditEntities.RepresentativeImport, batchId,
                    $"Import batch {batchId} failed and was rolled back: {Clip(ex.GetBaseException().Message, 300)}", ct: CancellationToken.None);
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status500InternalServerError,
                    "The import failed and was rolled back. No candidate was added.", RepCodes.ImportFailed);
            }
        }

        /// <summary>The writes of a commit. Set-based; runs inside the caller's transaction.</summary>
        private async Task ApplyAsync(RepresentativeImportBatchEntity batch, DateTime now, CancellationToken ct)
        {
            int b = batch.ImportBatchId, e = batch.EventId;

            // 1. New profiles — one per distinct code (the planner guarantees rows sharing a code agree, so MAX() just merges the blanks).
            await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO RepresentativeCandidateProfiles
                    (CandidateCode, MemberId, CandidateName, CellNo, TelNo, CivilStatus, Gender, DateOfBirth, HighestEducation, PreviousPositions, SpecialSkills, PhotoUpdatedAt, IsActive, CreatedAt, UpdatedAt)
                SELECT x.CandidateCode,
                       (SELECT m.MemberId FROM Members m WHERE m.AccountCode = x.AccountCode AND m.IsActive = 1 ORDER BY m.MemberId LIMIT 1),
                       LEFT(COALESCE(x.CandidateName, (SELECT m.MemberName FROM Members m WHERE m.AccountCode = x.AccountCode AND m.IsActive = 1 ORDER BY m.MemberId LIMIT 1)), 200),
                       x.CellNo, x.TelNo, x.CivilStatus, x.Gender, x.DateOfBirth, x.HighestEducation, x.PreviousPositions, x.SpecialSkills,
                       NULL, 1, {1}, {1}
                FROM (SELECT r.CandidateCode, MAX(r.AccountCode) AS AccountCode, MAX(r.CandidateName) AS CandidateName, MAX(r.CellNo) AS CellNo, MAX(r.TelNo) AS TelNo,
                             MAX(r.CivilStatus) AS CivilStatus, MAX(r.Gender) AS Gender, MAX(r.DateOfBirth) AS DateOfBirth, MAX(r.HighestEducation) AS HighestEducation,
                             MAX(r.PreviousPositions) AS PreviousPositions, MAX(r.SpecialSkills) AS SpecialSkills
                      FROM RepresentativeCandidateImportRows r
                      WHERE r.ImportBatchId = {0} AND r.RowStatus = 'VALID' AND r.PlannedAction = 'CREATE_PROFILE'
                      GROUP BY r.CandidateCode) x
                """, new object[] { b, now }, ct);

            // 2. Explicitly approved profile updates: only the values the file provides; the member link is added, never replaced.
            await _db.Database.ExecuteSqlRawAsync(
                """
                UPDATE RepresentativeCandidateProfiles p
                JOIN (SELECT r.MatchedProfileId AS pid, MAX(r.AccountCode) AS AccountCode, MAX(r.CandidateName) AS CandidateName, MAX(r.CellNo) AS CellNo, MAX(r.TelNo) AS TelNo,
                             MAX(r.CivilStatus) AS CivilStatus, MAX(r.Gender) AS Gender, MAX(r.DateOfBirth) AS DateOfBirth, MAX(r.HighestEducation) AS HighestEducation,
                             MAX(r.PreviousPositions) AS PreviousPositions, MAX(r.SpecialSkills) AS SpecialSkills
                      FROM RepresentativeCandidateImportRows r
                      WHERE r.ImportBatchId = {0} AND r.RowStatus = 'VALID' AND r.PlannedAction = 'UPDATE_PROFILE'
                      GROUP BY r.MatchedProfileId) u ON u.pid = p.RepresentativeCandidateProfileId
                SET p.CandidateName = COALESCE(u.CandidateName, p.CandidateName),
                    p.CellNo = COALESCE(u.CellNo, p.CellNo), p.TelNo = COALESCE(u.TelNo, p.TelNo),
                    p.CivilStatus = COALESCE(u.CivilStatus, p.CivilStatus), p.Gender = COALESCE(u.Gender, p.Gender),
                    p.DateOfBirth = COALESCE(u.DateOfBirth, p.DateOfBirth), p.HighestEducation = COALESCE(u.HighestEducation, p.HighestEducation),
                    p.PreviousPositions = COALESCE(u.PreviousPositions, p.PreviousPositions), p.SpecialSkills = COALESCE(u.SpecialSkills, p.SpecialSkills),
                    p.MemberId = COALESCE(p.MemberId, (SELECT m.MemberId FROM Members m WHERE m.AccountCode = u.AccountCode AND m.IsActive = 1 ORDER BY m.MemberId LIMIT 1)),
                    p.UpdatedAt = {1}
                """, new object[] { b, now }, ct);

            // 3. Roster entries. The unique keys (event+position+profile, event+position+ballot number) are the last line of defence.
            await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO RepresentativeEventCandidates (EventId, RepresentativeCandidateProfileId, Position, Reason, BallotNumber, Status, ImportBatchId, CreatedAt, UpdatedAt)
                SELECT {1}, p.RepresentativeCandidateProfileId, r.Position, r.Reason, r.BallotNumber, 'APPROVED', {0}, {2}, {2}
                FROM RepresentativeCandidateImportRows r
                JOIN RepresentativeCandidateProfiles p ON p.CandidateCode = r.CandidateCode
                WHERE r.ImportBatchId = {0} AND r.RowStatus = 'VALID'
                ORDER BY r.RowNumber
                """, new object[] { b, e, now }, ct);

            // 4. The rows that were imported.
            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE RepresentativeCandidateImportRows SET RowStatus = 'COMMITTED' WHERE ImportBatchId = {0} AND RowStatus = 'VALID'", new object[] { b }, ct);
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  Cancel / read
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        public async Task<ServiceResult<RepImportBatchRow>> CancelAsync(int batchId, CancellationToken ct)
        {
            var batch = await _db.RepresentativeImportBatches.FirstOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
            if (batch == null) return ServiceResult<RepImportBatchRow>.NotFound("Import batch");
            var cancellable = new[] { RepresentativeImportStatuses.Uploaded, RepresentativeImportStatuses.Validated, RepresentativeImportStatuses.Invalid, RepresentativeImportStatuses.Failed };
            bool staleValidating = batch.Status == RepresentativeImportStatuses.Validating && DateTime.Now - batch.UpdatedAt > StaleValidation;
            if (!cancellable.Contains(batch.Status) && !staleValidating)
                return ServiceResult<RepImportBatchRow>.Failure(StatusCodes.Status409Conflict,
                    $"A batch that is {batch.Status} cannot be cancelled.", RepCodes.BatchState);

            batch.Status = RepresentativeImportStatuses.Cancelled;
            batch.UpdatedAt = DateTime.Now;
            batch.Message = "Cancelled by the administrator. Nothing was imported.";
            _audit.Stage(AuditActions.RepImportCancelled, AuditEntities.RepresentativeImport, batchId, $"Import batch {batchId} ('{batch.FileName}') cancelled.");
            await _db.SaveChangesAsync(ct);
            var title = await _db.Events.AsNoTracking().Where(e => e.EventId == batch.EventId).Select(e => e.Title).FirstAsync(ct);
            return ServiceResult<RepImportBatchRow>.Success(ToRow(batch, title));
        }

        public async Task<ServiceResult<RepImportBatchRow>> GetAsync(int batchId, CancellationToken ct)
        {
            var row = await _db.RepresentativeImportBatches.AsNoTracking().Where(b => b.ImportBatchId == batchId)
                .Select(b => new { Batch = b, Title = b.Event!.Title }).FirstOrDefaultAsync(ct);
            return row == null ? ServiceResult<RepImportBatchRow>.NotFound("Import batch") : ServiceResult<RepImportBatchRow>.Success(ToRow(row.Batch, row.Title));
        }

        public async Task<RepImportHistoryResponse> HistoryAsync(int eventId, int take, CancellationToken ct)
        {
            take = Math.Clamp(take, 1, 200);
            var rows = await _db.RepresentativeImportBatches.AsNoTracking().Where(b => b.EventId == eventId)
                .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.ImportBatchId).Take(take)
                .Select(b => new { Batch = b, Title = b.Event!.Title }).ToListAsync(ct);
            return new RepImportHistoryResponse { Batches = rows.Select(r => ToRow(r.Batch, r.Title)).ToList() };
        }

        public async Task<ServiceResult<PagedResponse<RepImportPreviewRow>>> PreviewAsync(
            int batchId, string? status, string? search, int page, int pageSize, CancellationToken ct)
        {
            if (!await _db.RepresentativeImportBatches.AsNoTracking().AnyAsync(b => b.ImportBatchId == batchId, ct))
                return ServiceResult<PagedResponse<RepImportPreviewRow>>.NotFound("Import batch");

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 500);
            var q = _db.RepresentativeImportRows.AsNoTracking().Where(r => r.ImportBatchId == batchId);
            if (!string.IsNullOrWhiteSpace(status))
            {
                var s = status.Trim().ToUpperInvariant();
                q = q.Where(r => r.RowStatus == s);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var t = search.Trim();
                q = q.Where(r => (r.CandidateCode != null && r.CandidateCode.StartsWith(t)) || (r.CandidateName != null && r.CandidateName.Contains(t)));
            }
            var total = await q.CountAsync(ct);
            var items = await q.OrderBy(r => r.RowNumber).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(r => new RepImportPreviewRow
                {
                    RowNumber = r.RowNumber, CandidateCode = r.CandidateCode, CandidateName = r.CandidateName, AccountCode = r.AccountCode,
                    Position = r.Position, RowStatus = r.RowStatus, PlannedAction = r.PlannedAction, ErrorCodes = r.ErrorCodes, ErrorMessage = r.ErrorMessage,
                    Resolution = r.Resolution, MatchedProfileId = r.MatchedProfileId
                }).ToListAsync(ct);

            var ids = items.Where(i => i.MatchedProfileId.HasValue).Select(i => i.MatchedProfileId!.Value).Distinct().ToList();
            if (ids.Count > 0)
            {
                var names = await _db.RepresentativeCandidateProfiles.AsNoTracking().Where(p => ids.Contains(p.RepresentativeCandidateProfileId))
                    .Select(p => new { p.RepresentativeCandidateProfileId, p.CandidateName }).ToDictionaryAsync(p => p.RepresentativeCandidateProfileId, p => p.CandidateName, ct);
                foreach (var i in items) if (i.MatchedProfileId.HasValue && names.TryGetValue(i.MatchedProfileId.Value, out var n)) i.MatchedProfileName = n;
            }

            // A row without a name (it names a member instead, or an existing candidate) shows the name it will end up with.
            var unnamed = items.Where(i => string.IsNullOrEmpty(i.CandidateName)).ToList();
            if (unnamed.Count > 0)
            {
                var accounts = unnamed.Where(i => !string.IsNullOrEmpty(i.AccountCode)).Select(i => i.AccountCode!).Distinct().ToList();
                var memberNames = accounts.Count == 0
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : (await _db.Members.AsNoTracking().Where(m => m.IsActive && accounts.Contains(m.AccountCode)).Select(m => new { m.AccountCode, m.MemberName }).ToListAsync(ct))
                        .GroupBy(m => m.AccountCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().MemberName, StringComparer.OrdinalIgnoreCase);
                foreach (var i in unnamed)
                    i.CandidateName = i.MatchedProfileName ?? (i.AccountCode != null && memberNames.TryGetValue(i.AccountCode, out var mn) ? mn : null);
            }

            return ServiceResult<PagedResponse<RepImportPreviewRow>>.Success(new PagedResponse<RepImportPreviewRow>
            {
                Items = items, Total = total, Page = page, PageSize = pageSize, TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))
            });
        }

        public async Task<ServiceResult<(string FileName, byte[] Content)>> ErrorReportAsync(int batchId, bool allRows, CancellationToken ct)
        {
            var batch = await _db.RepresentativeImportBatches.AsNoTracking().FirstOrDefaultAsync(b => b.ImportBatchId == batchId, ct);
            if (batch == null) return ServiceResult<(string, byte[])>.NotFound("Import batch");

            var q = _db.RepresentativeImportRows.AsNoTracking().Where(r => r.ImportBatchId == batchId);
            if (!allRows)
                q = q.Where(r => r.RowStatus == RepresentativeImportRowStatuses.Invalid || r.RowStatus == RepresentativeImportRowStatuses.Conflict ||
                                 r.RowStatus == RepresentativeImportRowStatuses.Skipped);
            var rows = await q.OrderBy(r => r.RowNumber).Take(200_000)
                .Select(r => new { r.RowNumber, r.CandidateCode, r.CandidateName, r.AccountCode, r.Position, r.RowStatus, r.PlannedAction, r.Resolution, r.ErrorCodes, r.ErrorMessage })
                .ToListAsync(ct);

            var sb = new StringBuilder();
            sb.Append('﻿');                                         // UTF-8 BOM so Excel reads the peso sign and accents correctly
            sb.Append("Row,CandidateCode,CandidateName,AccountCode,Position,Status,Action,Decision,ErrorCodes,Message\r\n");
            foreach (var r in rows)
                sb.Append(string.Join(",", new[]
                {
                    r.RowNumber.ToString(CultureInfo.InvariantCulture), Csv(r.CandidateCode), Csv(r.CandidateName), Csv(r.AccountCode), Csv(r.Position),
                    Csv(r.RowStatus), Csv(r.PlannedAction), Csv(r.Resolution), Csv(r.ErrorCodes), Csv(r.ErrorMessage)
                })).Append("\r\n");

            await _audit.WriteAsync(AuditActions.ExportReport, AuditEntities.RepresentativeImport, batchId,
                $"Error report of import batch {batchId} downloaded ({rows.Count} row(s)).", ct: ct);
            return ServiceResult<(string, byte[])>.Success(($"representative-import-{batchId}-{(allRows ? "rows" : "errors")}.csv", Encoding.UTF8.GetBytes(sb.ToString())));
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        //  helpers
        // ═════════════════════════════════════════════════════════════════════════════════════════════════════
        private async Task MarkFailedAsync(int batchId, string message, CancellationToken ct)
        {
            try
            {
                _db.ChangeTracker.Clear();
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE RepresentativeCandidateImportBatches SET Status = {0}, Message = {1}, UpdatedAt = {2} WHERE ImportBatchId = {3} AND Status <> {4}",
                    new object[] { RepresentativeImportStatuses.Failed, Clip(message, 1000)!, DateTime.Now, batchId, RepresentativeImportStatuses.Committed }, CancellationToken.None);
            }
            catch (Exception ex) { _log.LogError(ex, "Could not mark import batch {Batch} as failed.", batchId); }
        }

        private async Task MarkStatusAsync(int batchId, string status, CancellationToken ct)
        {
            try
            {
                _db.ChangeTracker.Clear();
                await _db.Database.ExecuteSqlRawAsync(
                    "UPDATE RepresentativeCandidateImportBatches SET Status = {0}, UpdatedAt = {1} WHERE ImportBatchId = {2} AND Status = {3}",
                    new object[] { status, DateTime.Now, batchId, RepresentativeImportStatuses.Validating }, CancellationToken.None);
            }
            catch (Exception ex) { _log.LogError(ex, "Could not reset import batch {Batch} to {Status}.", batchId, status); }
        }

        private static string Describe(PlanSummary s) =>
            $"Checked {s.Total:N0} row(s): {s.Valid:N0} ready ({s.NewProfiles:N0} new candidate profile(s), {s.ReusedProfiles:N0} existing profile(s) reused)" +
            (s.Invalid > 0 ? $", {s.Invalid:N0} invalid" : "") +
            (s.Conflict > 0 ? $", {s.Conflict:N0} need a decision" : "") +
            (s.Skipped > 0 ? $", {s.Skipped:N0} skipped" : "") + ".";

        private static RepImportBatchRow ToRow(RepresentativeImportBatchEntity b, string eventTitle)
        {
            bool idle = b.Status is RepresentativeImportStatuses.Uploaded or RepresentativeImportStatuses.Validated or RepresentativeImportStatuses.Invalid or RepresentativeImportStatuses.Failed;
            return new RepImportBatchRow
            {
                BatchId = b.ImportBatchId, EventId = b.EventId, EventTitle = eventTitle, FileName = b.FileName, Status = b.Status,
                ExpectedRows = b.ExpectedRows, TotalRows = b.TotalRows, ValidRows = b.ValidRows, InvalidRows = b.InvalidRows, ConflictRows = b.ConflictRows,
                NewProfiles = b.NewProfiles, ReusedProfiles = b.ReusedProfiles, NewAssignments = b.NewAssignments, Message = b.Message,
                CreatedBy = b.CreatedByUsername, CreatedAt = b.CreatedAt, ValidatedAt = b.ValidatedAt, CommittedAt = b.CommittedAt, UpdatedAt = b.UpdatedAt,
                CanValidate = idle && b.TotalRows > 0,
                CanCommit = b.Status == RepresentativeImportStatuses.Validated && b.ValidRows > 0 && b.ConflictRows == 0,
                CanCancel = idle
            };
        }

        private static string? Clip(string? s, int max) => s == null ? null : (s.Length <= max ? s : s.Substring(0, max));

        private static string Csv(string? v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            var safe = RepText.Safe(v);
            return safe.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + safe.Replace("\"", "\"\"") + "\"" : safe;
        }
    }
}
