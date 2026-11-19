using System.ComponentModel.DataAnnotations;

namespace EVoting.Api.Infrastructure.Entities
{
    /// <summary>
    /// One Registration QR slip handed out FOR ONE ELECTION (table QrIssuances, deploy/sql/011).
    /// <para>
    /// Members.QrIssuedAt is a single "last QR at" per member and is overwritten by every election, so a member registered at the
    /// Representative Assembly looked "already issued" at the General Assembly. This table keeps every issuance with its election,
    /// the desk number (1-30) the Registration user chose at sign-in and the user, so each desk can list its own slips per election
    /// and the Admin can report per election and per desk. Members.QrIssuedAt is still stamped for older readers.
    /// </para>
    /// </summary>
    public class QrIssuanceEntity
    {
        [Key] public long QrIssuanceId { get; set; }

        public int EventId { get; set; }
        public int MemberId { get; set; }

        /// <summary>Registration desk 1-30 chosen at sign-in; null for an older client that sent none.</summary>
        public int? DeskNo { get; set; }

        /// <summary>The Registration user's username, or "station:{id}".</summary>
        [MaxLength(60)] public string IssuedBy { get; set; } = "";

        /// <summary>When the slip was issued (UTC). For an offline desk: the time on that PC, not the time it was sent.</summary>
        public DateTime IssuedAtUtc { get; set; }

        /// <summary>When the server stored it (UTC).</summary>
        public DateTime RecordedAtUtc { get; set; }

        /// <summary>True when the desk issued it without the server (sent later with qr-issued-bulk).</summary>
        public bool Offline { get; set; }
    }
}
