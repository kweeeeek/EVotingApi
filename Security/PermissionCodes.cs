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
    }
}
