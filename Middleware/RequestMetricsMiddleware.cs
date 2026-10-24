using System.Diagnostics;
using EVoting.Api.Services;

namespace EVoting.Api.Middleware
{
    // ══════════════════════════════════════════════════════════════════════════
    //  RequestMetricsMiddleware
    //
    //  Registered BEFORE ExceptionHandlerMiddleware (see Program.cs) so it
    //  wraps the entire rest of the pipeline. Because ExceptionHandlerMiddleware
    //  always catches and never rethrows, context.Response.StatusCode is
    //  guaranteed to be final (200, or the mapped error status) by the time
    //  control returns here — no exception ever needs to be caught in this
    //  middleware itself.
    //
    //  Two path groups are skipped entirely (not just under-counted):
    //    • /api/v1/monitoring, /api/v1/health, /health — this dashboard's own
    //      polling would otherwise dominate the "top endpoints" stats.
    //    • /hubs — SignalR upgrades a request into a long-lived WebSocket
    //      connection; `await _next(context)` doesn't return until the
    //      client disconnects, so timing it would report multi-hour
    //      "response times" and wreck the average/percentile figures.
    // ══════════════════════════════════════════════════════════════════════════
    public class RequestMetricsMiddleware
    {
        private static readonly string[] ExcludedPathPrefixes =
        {
            "/api/v1/monitoring",
            "/api/v1/health",
            "/health",
            "/hubs"
        };

        private readonly RequestDelegate _next;
        private readonly IRequestMetricsService _metrics;
        private readonly int _slowRequestThresholdMs;

        public RequestMetricsMiddleware(
            RequestDelegate next, IRequestMetricsService metrics, IConfiguration config)
        {
            _next = next;
            _metrics = metrics;

            if (!int.TryParse(config["Monitoring:SlowRequestThresholdMs"], out _slowRequestThresholdMs))
                _slowRequestThresholdMs = 1000;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "/";

            if (IsExcluded(path))
            {
                await _next(context);
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                await _next(context);
            }
            finally
            {
                sw.Stop();

                var method = context.Request.Method;
                var status = context.Response.StatusCode;
                var routeKey = ResolveRouteKey(context, path);
                var elapsedMs = sw.Elapsed.TotalMilliseconds;

                _metrics.RecordRequest(method, routeKey, status, elapsedMs);

                if (elapsedMs >= _slowRequestThresholdMs)
                    _metrics.RecordSlowRequest(method, routeKey, status, elapsedMs);
            }
        }

        private static bool IsExcluded(string path) =>
            ExcludedPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Groups by route TEMPLATE ("/api/v1/admin/members/{memberId}") rather
        /// than the raw request path, so per-endpoint stats don't grow one
        /// bucket per unique id. Endpoint routing (implicitly inserted by
        /// ASP.NET Core at the very start of the pipeline, since Program.cs
        /// never calls UseRouting explicitly) has already matched the request
        /// by the time any middleware runs, so this metadata is available even
        /// this early. Falls back to the raw path for endpoints with no
        /// attribute-route template (minimal APIs, health checks).
        /// </summary>
        private static string ResolveRouteKey(HttpContext context, string fallbackPath)
        {
            var endpoint = context.GetEndpoint();
            var controllerDescriptor = endpoint?.Metadata
                .GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>();

            var template = controllerDescriptor?.AttributeRouteInfo?.Template;
            if (!string.IsNullOrEmpty(template)) return "/" + template;

            return endpoint?.DisplayName ?? fallbackPath;
        }
    }
}
