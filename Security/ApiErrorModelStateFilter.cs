using EVoting.Api.Models.Responses;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EVoting.Api.Security
{
    /// <summary>
    /// Applied to the Admin controllers ONLY (not globally, so the kiosk-facing endpoints keep their current 400 shape).
    /// [ApiController] answers an invalid model with RFC 7807 ProblemDetails; this filter runs first (lower Order than the
    /// framework's ModelStateInvalidFilter) and answers with the same ApiError body every other Admin error uses, including
    /// per-field messages keyed by camelCase field name, so the client has ONE error shape to parse.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class ApiErrorModelStateAttribute : ActionFilterAttribute
    {
        public ApiErrorModelStateAttribute() => Order = -3000;   // ModelStateInvalidFilter uses -2000

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.ModelState.IsValid) return;

            var fields = new Dictionary<string, string>();
            foreach (var (key, entry) in context.ModelState)
            {
                var first = entry.Errors.FirstOrDefault();
                if (first == null) continue;
                var name = Normalise(key);
                if (!fields.ContainsKey(name))
                    fields[name] = Friendly(first.ErrorMessage);
            }

            var headline = fields.Count > 0 ? fields.Values.First() : "The request is not valid.";
            context.Result = new ObjectResult(new ApiError
            {
                Error = headline,
                Code = "VALIDATION",
                Fields = fields.Count > 0 ? fields : null
            })
            { StatusCode = StatusCodes.Status400BadRequest };
        }

        /// <summary>"FirstName" → "firstName", "$.rows[2].Username" → "rows[2].username", "" → "request".</summary>
        public static string Normalise(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "request";
            var k = key.StartsWith("$.") ? key[2..] : key;
            var parts = k.Split('.');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0)
                    parts[i] = char.ToLowerInvariant(parts[i][0]) + parts[i][1..];
            return string.Join('.', parts);
        }

        private static string Friendly(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "Invalid value.";
            // System.Text.Json / binder messages are developer-oriented; do not leak type names or line numbers.
            if (message.Contains("could not be converted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("JSON", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("is invalid", StringComparison.OrdinalIgnoreCase) && message.Contains("LineNumber", StringComparison.OrdinalIgnoreCase))
                return "Invalid value.";
            return message;
        }
    }
}
