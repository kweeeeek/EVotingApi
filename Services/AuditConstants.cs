using EVoting.Api.Infrastructure.Entities;

namespace EVoting.Api.Services
{
    /// <summary>Audit action codes. Identical to the legacy Admin's AuditActions so old and new rows interleave cleanly.</summary>
    public static class AuditActions
    {
        public const string Login = "LOGIN";
        public const string LoginFailed = "LOGIN_FAILED";
        public const string Logout = "LOGOUT";
        public const string Create = "CREATE";
        public const string Update = "UPDATE";
        public const string SoftDelete = "SOFT_DELETE";
        public const string Delete = "DELETE";
        public const string BulkDelete = "BULK_DELETE";
        public const string Restore = "RESTORE";
        public const string ApproveCandidacy = "APPROVE_CANDIDACY";
        public const string RejectCandidacy = "REJECT_CANDIDACY";
        public const string OpenEvent = "OPEN_EVENT";
        public const string CloseEvent = "CLOSE_EVENT";
        public const string ExportReport = "EXPORT_REPORT";
        public const string Import = "IMPORT";
        public const string UpdatePermissions = "UPDATE_PERMISSIONS";

        public static readonly string[] All =
        {
            Login, LoginFailed, Logout, Create, Update, SoftDelete, Delete, BulkDelete, Restore,
            ApproveCandidacy, RejectCandidacy, OpenEvent, CloseEvent, ExportReport, Import, UpdatePermissions
        };
    }

    public static class AuditEntities
    {
        public const string Member = "Member";
        public const string Role = "Role";
        public const string Branch = "Branch";
        public const string User = "User";
        public const string Event = "Event";
        public const string Candidacy = "Candidacy";
        public const string Report = "Report";
    }

    /// <summary>Pure helpers for building AuditLogs rows (no HTTP, no EF queries) — safe to use from background services.</summary>
    public static class AuditWriter
    {
        public const string SystemUser = "SYSTEM";

        public static AuditLogEntity Create(
            string action, string entityType, int? entityId, string? details,
            int? userId, string? username, string? ip, DateTime? utcNow = null) => new()
            {
                Action = Clip(action, 50) ?? "",
                EntityType = Clip(entityType, 100) ?? "",
                EntityId = entityId,
                Details = Clip(details, 2000),
                UserId = userId,
                Username = Clip(string.IsNullOrWhiteSpace(username) ? SystemUser : username, 50),
                IpAddress = Clip(ip, 45),
                OccurredAtUtc = utcNow ?? DateTime.UtcNow
            };

        public static string? Clip(string? s, int max) =>
            s == null ? null : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");
    }
}
