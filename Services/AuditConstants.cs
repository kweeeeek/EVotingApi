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

        // ── Representative Election / settings (additive; appended after the legacy codes) ──
        public const string UpdatePositionRules = "UPDATE_POSITION_RULES";
        public const string AssignRepCandidate = "ASSIGN_REP_CANDIDATE";
        public const string RemoveRepCandidate = "REMOVE_REP_CANDIDATE";
        public const string UpdateRepCandidate = "UPDATE_REP_CANDIDATE";
        public const string RepImportUploaded = "REP_IMPORT_UPLOADED";
        public const string RepImportValidated = "REP_IMPORT_VALIDATED";
        public const string RepImportCommitted = "REP_IMPORT_COMMITTED";
        public const string RepImportCancelled = "REP_IMPORT_CANCELLED";
        public const string RepImportFailed = "REP_IMPORT_FAILED";
        public const string UpdateEligibilitySettings = "UPDATE_ELIGIBILITY";
        public const string RecomputeEligibility = "RECOMPUTE_ELIGIBILITY";
        public const string OpenEventBlocked = "OPEN_EVENT_BLOCKED";
        public const string RosterLocked = "ROSTER_LOCKED";
        public const string RosterUnlocked = "ROSTER_UNLOCKED";
        public const string RepOfflineRejected = "REP_OFFLINE_REJECTED";
        public const string RepProfilePhoto = "REP_PROFILE_PHOTO";
        public const string RepOfflineSynced = "REP_OFFLINE_SYNCED";

        public static readonly string[] All =
        {
            Login, LoginFailed, Logout, Create, Update, SoftDelete, Delete, BulkDelete, Restore,
            ApproveCandidacy, RejectCandidacy, OpenEvent, CloseEvent, ExportReport, Import, UpdatePermissions,
            UpdatePositionRules, AssignRepCandidate, RemoveRepCandidate, UpdateRepCandidate,
            RepImportUploaded, RepImportValidated, RepImportCommitted, RepImportCancelled, RepImportFailed,
            UpdateEligibilitySettings, RecomputeEligibility, OpenEventBlocked, RosterLocked, RosterUnlocked,
            RepOfflineRejected, RepProfilePhoto, RepOfflineSynced
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
        /// <summary>A non-interactive Registration Station (sign-in audit rows). Additive.</summary>
        public const string Station = "Station";
        public const string RepresentativeCandidate = "RepresentativeCandidate";
        public const string RepresentativeRoster = "RepresentativeRoster";
        public const string RepresentativeImport = "RepresentativeImport";
        public const string Settings = "Settings";
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
