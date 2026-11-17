using Serilog.Events;

namespace EVoting.Api.Middleware
{
    /// <summary>
    /// The level of the one "request finished" line that Serilog writes for every request (<c>UseSerilogRequestLogging</c>).
    /// By default an exception that passes through it is an ERROR answered "500" - including the OperationCanceledException of a caller that closed the
    /// connection before the answer was ready (a search box that sends the next request while the last one is still running, a window that was closed).
    /// <see cref="ExceptionHandlerMiddleware"/> already treats that case as "nothing failed on the server" (no error log of its own, no error metric, status 499);
    /// this makes the request line agree, so an ERROR in the log means something really went wrong. Everything else keeps the library's default:
    /// an exception or a 5xx answer is an Error, anything else Information.
    /// </summary>
    public static class RequestLogLevel
    {
        public static LogEventLevel For(HttpContext context, double elapsedMs, Exception? exception)
        {
            // The same condition as ExceptionHandlerMiddleware: the caller is gone. A cancellation while the caller is still waiting
            // (for instance a server-side timeout) is NOT abandoned and stays an Error.
            if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
                return LogEventLevel.Debug;

            return exception != null || context.Response.StatusCode > 499 ? LogEventLevel.Error : LogEventLevel.Information;
        }
    }
}
