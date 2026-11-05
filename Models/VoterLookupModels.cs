namespace EVoting.Api.Models.Admin
{
    /// <summary>One voter in the Voter Lookup list. No photo, address, phone or money fields: the list needs none of them.</summary>
    public class VoterLookupRow
    {
        public int MemberId { get; set; }
        public string AccountCode { get; set; } = "";
        public string MemberName { get; set; } = "";
        public string Branch { get; set; } = "";
        public int BranchId { get; set; }
        public bool IsEligible { get; set; }
        /// <summary>True only when this election's voter log says the ballot was submitted (a log row can exist without a vote).</summary>
        public bool HasVoted { get; set; }
        /// <summary>UTC (serialised with a 'Z'); null when the member has not voted.</summary>
        public DateTime? VotedAt { get; set; }
        public string ReceiptCode { get; set; } = "";
    }

    /// <summary>
    /// One page of the Voter Lookup list.
    /// Page / PageSize / TotalCount / TotalPages / Items are the paging contract. Total and Data are the OLD names for TotalCount and Items,
    /// kept so a client written before paging was added keeps working; new code should not read them.
    /// PageSize is the size actually used (a request above the maximum is cut to it): clients must show and reuse this value.
    /// </summary>
    public class VoterLookupPageResponse
    {
        public int EventId { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages { get; set; }
        public List<VoterLookupRow> Items { get; set; } = new();

        /// <summary>Deprecated alias of <see cref="TotalCount"/>.</summary>
        public int Total { get; set; }
        /// <summary>Deprecated alias of <see cref="Items"/> (the same rows).</summary>
        public List<VoterLookupRow> Data { get; set; } = new();
    }
}
