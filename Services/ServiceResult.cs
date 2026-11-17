namespace EVoting.Api.Services
{
    /// <summary>
    /// Outcome of a service call that can fail for business reasons (not found, locked, conflict, invalid). Services return this instead of throwing,
    /// so the controller can map it to the standard ApiError body without relying on the exception middleware (which turns unknown exceptions into 500).
    /// </summary>
    public sealed class ServiceResult<T>
    {
        public bool Ok { get; init; }
        public int StatusCode { get; init; } = StatusCodes.Status200OK;
        public T? Value { get; init; }
        public string? Error { get; init; }
        public string? Code { get; init; }
        public string? Detail { get; init; }
        public Dictionary<string, string>? Fields { get; init; }

        public static ServiceResult<T> Success(T value, int status = StatusCodes.Status200OK) =>
            new() { Ok = true, Value = value, StatusCode = status };

        public static ServiceResult<T> Failure(int status, string error, string? code = null, string? detail = null, Dictionary<string, string>? fields = null) =>
            new() { Ok = false, StatusCode = status, Error = error, Code = code, Detail = detail, Fields = fields };

        public static ServiceResult<T> Invalid(string field, string message) =>
            Failure(StatusCodes.Status400BadRequest, message, "VALIDATION", null, new() { [field] = message });

        public static ServiceResult<T> NotFound(string what) =>
            Failure(StatusCodes.Status404NotFound, $"{what} not found. It may have been deleted.", "NOT_FOUND");

        public static ServiceResult<T> Concurrency(string what) =>
            Failure(StatusCodes.Status409Conflict, $"This {what} was changed by someone else after you opened it.", "CONCURRENCY", "Reload the record and re-apply your changes.");

        public static ServiceResult<T> Locked(EventLock gate) =>
            Failure(StatusCodes.Status409Conflict, gate.Reason ?? "The candidate roster is locked.", gate.Code ?? RepCodes.RosterLocked);

        /// <summary>Carries a failure over to another result type (the error part is identical).</summary>
        public ServiceResult<TOther> As<TOther>() =>
            ServiceResult<TOther>.Failure(StatusCode, Error ?? "The request failed.", Code, Detail, Fields);
    }
}
