namespace EVoting.Api.Models.Admin
{
    /// <summary>The whole text of a kiosk vote file (.evvotes) as read from the USB stick.</summary>
    public class OfflineVoteFileRequest
    {
        public string FileContent { get; set; } = "";
    }

    /// <summary>What the file holds and what importing it WOULD do. Nothing is written.</summary>
    public class OfflineVoteImportPreviewResponse
    {
        public string KioskId { get; set; } = "";
        public string ExportId { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public string FileSha256 { get; set; } = "";
        public int VoteCount { get; set; }
        public DateTime? EarliestCastAtUtc { get; set; }
        public DateTime? LatestCastAtUtc { get; set; }
        public List<OfflineVoteImportEventCount> Events { get; set; } = new();

        /// <summary>Not recorded yet: these will be counted (unless a rule refuses them on import).</summary>
        public int NewVotes { get; set; }
        /// <summary>Already recorded with this same receipt (the kiosk synced them, or this file was imported before).</summary>
        public int AlreadyRecorded { get; set; }
        /// <summary>The member already voted another way (mobile or another kiosk): these will be refused - the earlier recorded vote stands.</summary>
        public int MemberAlreadyVoted { get; set; }
        /// <summary>True when an import of this same export id is already in the audit trail.</summary>
        public bool ImportedBefore { get; set; }
    }

    public class OfflineVoteImportEventCount
    {
        public int EventId { get; set; }
        public string Title { get; set; } = "";
        public int Votes { get; set; }
    }

    public class OfflineVoteImportResultResponse
    {
        public string KioskId { get; set; } = "";
        public string ExportId { get; set; } = "";
        public string FileSha256 { get; set; } = "";
        public int VoteCount { get; set; }
        public int Accepted { get; set; }
        public int AlreadyRecorded { get; set; }
        public int Rejected { get; set; }
        /// <summary>Not recorded because of a temporary problem (e.g. the election is not active). Importing the same file again is safe.</summary>
        public int Failed { get; set; }
        /// <summary>One line per vote that was NOT counted (rejected or failed). Never contains ballot content.</summary>
        public List<OfflineVoteImportProblem> Problems { get; set; } = new();
    }

    public class OfflineVoteImportProblem
    {
        public int MemberId { get; set; }
        public string AccountCode { get; set; } = "";
        public string MemberName { get; set; } = "";
        public int EventId { get; set; }
        public string ReceiptCode { get; set; } = "";
        public DateTime CastAtUtc { get; set; }
        /// <summary>"Rejected" (final - the rule refused it) or "Failed" (temporary - import the file again later).</summary>
        public string Outcome { get; set; } = "";
        public string Reason { get; set; } = "";
    }
}
