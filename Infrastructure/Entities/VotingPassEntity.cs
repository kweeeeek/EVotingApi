using System.ComponentModel.DataAnnotations;

namespace EVoting.Api.Infrastructure.Entities
{
    /// <summary>
    /// A one-election voting pass printed in the Registration QR code (see <see cref="Services.VotingPass"/>).
    /// <para>
    /// The account code stays the member's IDENTITY (eligibility, branch and "already voted" are still decided on the member row).
    /// The pass is the PROOF that the person holding the slip went through the Registration desk: it is random (100 bits), so it
    /// cannot be guessed the way a sequential account code can.
    /// </para>
    /// <para>
    /// Only a SHA-256 digest of the pass is stored, never the pass itself: a copy of this table cannot be turned back into QR codes.
    /// Re-issuing a pass to the same member for the same election revokes the earlier one, so a lost slip stops working.
    /// </para>
    /// </summary>
    public class VotingPassEntity
    {
        [Key] public long VotingPassId { get; set; }

        public int EventId { get; set; }
        public int MemberId { get; set; }

        /// <summary>Lower-case hex SHA-256 of the normalised pass text (64 characters). Unique.</summary>
        [MaxLength(64)] public string PassHash { get; set; } = "";

        public DateTime IssuedAt { get; set; }

        /// <summary>Who issued it: "station:{id}" or the Registration user's username (for the audit trail).</summary>
        [MaxLength(60)] public string IssuedBy { get; set; } = "";

        /// <summary>Set when a newer pass was issued to the same member for the same election. A revoked pass is refused.</summary>
        public DateTime? RevokedAt { get; set; }
    }
}
