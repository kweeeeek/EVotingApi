namespace EVoting.Api.Security
{
    /// <summary>
    /// Permission codes. Values are identical to the legacy Admin's PermissionConstants and to the
    /// rows seeded into the Permissions table, so existing role assignments keep working unchanged.
    /// </summary>
    public static class PermissionCodes
    {
        public const string RoleManager = "ROLE_MANAGER";

        public const string BranchView = "BRANCH_VIEW";
        public const string BranchAdd = "BRANCH_ADD";
        public const string BranchEdit = "BRANCH_EDIT";
        public const string BranchDelete = "BRANCH_DELETE";

        public const string UserView = "USER_VIEW";
        public const string UserAdd = "USER_ADD";
        public const string UserEdit = "USER_EDIT";
        public const string UserDelete = "USER_DELETE";

        public const string RoleView = "ROLE_VIEW";
        public const string RoleAdd = "ROLE_ADD";
        public const string RoleEdit = "ROLE_EDIT";
        public const string RoleDelete = "ROLE_DELETE";

        public const string MemberView = "MEMBER_VIEW";
        public const string MemberAdd = "MEMBER_ADD";
        public const string MemberEdit = "MEMBER_EDIT";
        public const string MemberDelete = "MEMBER_DELETE";

        public const string EventView = "EVENT_VIEW";
        public const string EventAdd = "EVENT_ADD";
        public const string EventEdit = "EVENT_EDIT";
        public const string EventDelete = "EVENT_DELETE";

        public const string ReportView = "REPORT_VIEW";
        public const string ReportExport = "REPORT_EXPORT";

        public const string CandidateView = "CANDIDATE_VIEW";
        public const string CandidateAdd = "CANDIDATE_ADD";
        public const string CandidateEdit = "CANDIDATE_EDIT";
        public const string CandidateDelete = "CANDIDATE_DELETE";

        public const string AuditView = "AUDIT_VIEW";

        // Settings > Member Eligibility (system-wide voting-eligibility thresholds). Seeded by migration 20261104140000_AddRepresentativeElection
        // and granted to RoleId 1 (the built-in super administrator); every other role must be granted them explicitly on the Roles screen.
        public const string SettingsEligibilityView = "SETTINGS_ELIGIBILITY_VIEW";
        public const string SettingsEligibilityEdit = "SETTINGS_ELIGIBILITY_EDIT";

        /// <summary>Screens that used to ride on EVENT_VIEW (2026-11-17): each now has its own permission.</summary>
        public const string DashboardView = "DASHBOARD_VIEW";
        public const string ResultsView = "RESULTS_VIEW";
        public const string VoterLookupView = "VOTER_LOOKUP_VIEW";

        /// <summary>Import a kiosk vote file carried on USB (2026-11-18, deploy/sql/009). RoleId 1 only until granted on the Roles screen.</summary>
        public const string OfflineVoteImport = "OFFLINE_VOTE_IMPORT";

        /// <summary>Make the kiosk package (.evpkg: roster, candidates, rules) for a venue with no signal (2026-11-18, deploy/sql/010). RoleId 1 only until granted.</summary>
        public const string OfflinePackageExport = "OFFLINE_PACKAGE_EXPORT";
    }
}
