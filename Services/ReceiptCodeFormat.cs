using System.Text.RegularExpressions;

namespace EVoting.Api.Services
{
    /// <summary>
    /// The one definition of a valid receipt code: EVT{eventId:D2}-{8 upper-case hex}-{8 upper-case hex}
    /// (for example EVT03-A1B2C3D4-E5F6A7B8), at most 30 characters (the column width).
    ///
    /// The server generates codes in this exact format (VoteService.GenerateReceiptCode) and so do the kiosks (offline queue).
    /// A kiosk may also propose a code for an ONLINE vote (SubmitVoteRequest.ReceiptCode) so the call can be retried safely.
    /// That code is only a label - it carries no member id and no ballot content - but it must look like a receipt and must
    /// belong to the event of the voter's token.
    /// </summary>
    public static class ReceiptCodeFormat
    {
        public const int MaxLength = 30;

        private static readonly Regex Pattern = new(
            // \z, not $: in .NET '$' also matches in front of a trailing '\n', which would let "EVT03-...\n" through.
            "^EVT(?<event>[0-9]{2,9})-[0-9A-F]{8}-[0-9A-F]{8}\\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>
        /// A new cryptographically random code in the standard format. The Representative ballot uses this; the General Assembly path keeps
        /// its own private generator (VoteService.GenerateReceiptCode) so that path is not touched. Same format, same randomness.
        /// </summary>
        public static string Generate(int eventId)
        {
            Span<byte> bytes = stackalloc byte[8];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            var hex = Convert.ToHexString(bytes);
            return $"EVT{eventId:D2}-{hex[..8]}-{hex[8..]}";
        }

        public static bool IsValid(string? code, int eventId)
        {
            if (string.IsNullOrEmpty(code) || code.Length > MaxLength) return false;

            var m = Pattern.Match(code);
            if (!m.Success) return false;

            return int.TryParse(m.Groups["event"].Value, out var idInCode) && idInCode == eventId;
        }
    }
}
