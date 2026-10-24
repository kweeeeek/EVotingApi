using EVoting.Api.Models.Responses;
using EVoting.Api.Services;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;

namespace EVoting.Api.Middleware
{
    /// <summary>
    /// Global exception handler — catches unhandled exceptions and returns
    /// a consistent JSON error response. Prevents stack traces leaking to clients.
    /// </summary>
    public class ExceptionHandlerMiddleware
    {
        private readonly RequestDelegate _next; 
        private readonly ILogger<ExceptionHandlerMiddleware> _logger;
        private readonly IRequestMetricsService _metrics;

        public ExceptionHandlerMiddleware(
            RequestDelegate next,
            ILogger<ExceptionHandlerMiddleware> logger,
            IRequestMetricsService metrics)
        {
            _next    = next;
            _logger  = logger;
            _metrics = metrics;
        } 

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The caller went away before the answer was ready. Nothing failed on the server and nobody is left to read a
                // reply: no error log, no entry in the error metrics, no 500 - only a debug line and the status that nginx uses
                // for "client closed request" (it is written for the access log; the connection is already gone).
                _logger.LogDebug("Request abandoned by the caller: {Method} {Path}", context.Request.Method, context.Request.Path);
                if (!context.Response.HasStarted) context.Response.StatusCode = 499;
            }
            catch (Exception ex)
            {
                await HandleExceptionAsync(context, ex);
            }
        }

        private async Task HandleExceptionAsync(HttpContext context, Exception ex)
        {
            _logger.LogError(ex,
                "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);
             
            int status;
            string message;
            string? code = null;

            switch (ex)
            {
                case ArgumentException:
                    status  = (int)HttpStatusCode.BadRequest;
                    message = ex.Message;
                    break;
                case InvalidOperationException when ex.Message.Contains("already voted"):
                    status  = (int)HttpStatusCode.Conflict;
                    message = ex.Message;
                    break;
                // Admin API only (kiosk endpoints keep their existing behaviour): a unique-index violation that slipped past
                // the controller's pre-check - two administrators saving the same value at the same moment - is a conflict the
                // user can resolve, not a server fault.
                case DbUpdateException dbe when IsAdminPath(context) && IsDuplicateKey(dbe):
                    status  = (int)HttpStatusCode.Conflict;
                    message = "That value is already in use by another record. Change it and try again.";
                    code    = "DUPLICATE";
                    break;
                // MySQL 1406 "Data too long for column 'X'": a value is too long for its column (the client sent too much,
                // or an old column width cannot hold a legitimate value). Report it as a field problem instead of an opaque 500.
                case DbUpdateException dtl when IsAdminPath(context) && TryGetDataTooLong(dtl, out var tooLongColumn):
                    status  = (int)HttpStatusCode.BadRequest;
                    message = tooLongColumn != null
                        ? $"The value for '{tooLongColumn}' is too long for the database column. Shorten it, or ask your administrator to apply the latest database scripts."
                        : "A value is too long for its database column. Shorten it, or ask your administrator to apply the latest database scripts.";
                    code    = "DATA_TOO_LONG";
                    break;
                case UnauthorizedAccessException:
                    status  = (int)HttpStatusCode.Unauthorized;
                    message = "Unauthorized.";
                    break;
                default:
                    status  = (int)HttpStatusCode.InternalServerError;
                    message = "An unexpected error occurred. Please try again or contact support.";
                    break;
            }

            // Record the REAL exception detail (type + message) for the
            // Admin-only monitoring dashboard. This is deliberately separate
            // from `message` above, which is the sanitized text sent back to
            // the caller — admins get full diagnostic detail, callers never do.
            _metrics.RecordError(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                status,
                $"{ex.GetType().Name}: {ex.Message}");

            context.Response.ContentType = "application/json";
            context.Response.StatusCode  = status;

            var body = JsonSerializer.Serialize(new ApiError { Error = message, Code = code },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            await context.Response.WriteAsync(body);
        }

        private static bool IsAdminPath(HttpContext context) =>
            context.Request.Path.StartsWithSegments("/api/v1/admin");

        /// <summary>MySQL error 1062 ("Duplicate entry '...' for key '...'"). Detected without a compile-time dependency on the MySQL driver.</summary>
        private static bool IsDuplicateKey(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)) return true;
                if (e.GetType().GetProperty("Number")?.GetValue(e) is int n && n == 1062) return true;
            }
            return false;
        }

        /// <summary>MySQL error 1406 ("Data too long for column 'Gender' at row 1").</summary>
        private static bool TryGetDataTooLong(Exception ex, out string? column)
        {
            column = null;
            for (var e = ex; e != null; e = e.InnerException)
            {
                var m = System.Text.RegularExpressions.Regex.Match(e.Message, "Data too long for column '([^']+)'",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) { column = m.Groups[1].Value; return true; }
                if (e.GetType().GetProperty("Number")?.GetValue(e) is int n && n == 1406) return true;
            }
            return false;
        }
    }
}
