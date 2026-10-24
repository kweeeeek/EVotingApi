using EVoting.Api.Infrastructure.Entities;
using System.Globalization;

namespace EVoting.Api.Services
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Pure, dependency-free rules (no EF, no HTTP). Kept separate so they can be
    //  unit-tested in isolation and shared by controllers AND background services.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Column limits enforced by the API so a too-long value is a clear 400, never a DB truncation error.</summary>
    public static class FieldLimits
    {
        public const int Name = 50;                 // first/middle/last, username, email, role/branch name
        public const int UserAddress = 200;
        public const int ContactNo = 20;
        public const int BranchAddress = 50;        // DB/API limit (the legacy editor composed longer strings!)
        public const int BranchContact = 15;
        public const int RoleDescription = 50;      // DB column limit (API entity said 200 — the DB is authoritative)
        public const int AccountCode = 20;
        public const int MemberName = 100;
        public const int MemberAddress = 200;
        public const int MemberContact = 15;
        public const int EventTitle = 100;
        public const int EventVenue = 150;
        public const int CandidatePosition = 100;
        public const int WalkInName = 200;
        public const int CellNo = 20;
        public const int Education = 200;
        public const int LongText = 500;
        public const int PasswordMin = 8;
        public const int PasswordMax = 128;

        /// <summary>Largest share capital / savings deposit accepted (the <c>[Range]</c> of the member request; the decimal(18,2) columns could hold far more).</summary>
        public const decimal MaxMoney = 1_000_000_000_000m;
    }

    /// <summary>
    /// What a money amount (share capital, savings deposit) may be: not negative, no more than two decimal places (the database would round
    /// the rest away silently) and not above <see cref="FieldLimits.MaxMoney"/>. The single save is bounded by the request's <c>[Range]</c>;
    /// the import had no upper bound, so one absurd amount (1e20 parses as a decimal) failed in the database and took its whole batch with it.
    /// </summary>
    public static class MoneyRules
    {
        public static bool IsValid(decimal value) => value >= 0m && value <= FieldLimits.MaxMoney && decimal.Round(value, 2) == value;

        public static string Message(string label) =>
            $"{label} must be an amount from 0 to {FieldLimits.MaxMoney.ToString("N2", CultureInfo.InvariantCulture)} with at most two decimal places.";
    }

    public sealed class EligibilityOptions
    {
        public const string Section = "Eligibility";

        /// <summary>Minimum share capital to vote. Default mirrors the legacy AUTHORITATIVE persisted rule.</summary>
        public decimal MinShareCapital { get; set; } = 3000m;

        /// <summary>Minimum savings deposit to vote. Default mirrors the legacy AUTHORITATIVE persisted rule.</summary>
        public decimal MinSavingsDeposit { get; set; } = 4000m;
    }

    /// <summary>
    /// Single server-side source of truth for voting eligibility. The legacy Admin had THREE divergent copies
    /// (entity: 3000/4000; editor preview and import preview: 100/3000). The persisted value always came from the
    /// entity rule, so that is the rule preserved here; thresholds are configuration (Eligibility:*).
    /// </summary>
    public static class MemberEligibilityPolicy
    {
        public static (bool IsEligible, string Remarks) Evaluate(
            decimal shareCapital, decimal savingsDeposit, LoanStatus loanStatus, EligibilityOptions? options = null)
        {
            var o = options ?? new EligibilityOptions();
            var remarks = new List<string>();
            var ph = CultureInfo.InvariantCulture;

            if (shareCapital < o.MinShareCapital)
                remarks.Add($"Share Capital is below ₱{o.MinShareCapital.ToString("N0", ph)} (Current: ₱{shareCapital.ToString("N2", ph)})");

            if (savingsDeposit < o.MinSavingsDeposit)
                remarks.Add($"Savings Deposit is below ₱{o.MinSavingsDeposit.ToString("N0", ph)} (Current: ₱{savingsDeposit.ToString("N2", ph)})");

            if (loanStatus == LoanStatus.PastDue)
                remarks.Add("Loan Status is Past Due");

            return remarks.Count == 0
                ? (true, "Eligible")
                : (false, AuditWriter.Clip(string.Join("; ", remarks), FieldLimits.LongText)!);
        }
    }

    /// <summary>
    /// Password rule for NEW and RESET passwords. This is the project's own legacy <c>Validations.isStrongPassword</c>
    /// (≥ 8 chars with an upper-case letter, a lower-case letter, a digit and a special character). The legacy
    /// UserEditor never called it, so any non-empty password was accepted; the API now enforces it server-side.
    /// </summary>
    public static class PasswordPolicy
    {
        public const string Requirement =
            "Password must be 8–128 characters and include an uppercase letter, a lowercase letter, a digit and a special character.";

        public static bool IsAcceptable(string? input)
        {
            if (string.IsNullOrEmpty(input) || input.Length < FieldLimits.PasswordMin || input.Length > FieldLimits.PasswordMax)
                return false;

            bool upper = false, lower = false, digit = false, special = false;
            foreach (var c in input)
            {
                if (char.IsUpper(c)) upper = true;
                else if (char.IsLower(c)) lower = true;
                else if (char.IsDigit(c)) digit = true;
                else special = true;
            }
            return upper && lower && digit && special;
        }
    }

    /// <summary>Time helpers. See docs: columns are either LOCAL server time or UTC depending on the table (legacy convention).</summary>
    public static class TimeConv
    {
        /// <summary>Legacy semantics for local-time columns: from.Date … to.Date + 1 day − 1 tick (inclusive).</summary>
        public static (DateTime? Start, DateTime? End) LocalRange(DateTime? from, DateTime? to) =>
            (from?.Date, to?.Date.AddDays(1).AddTicks(-1));

        /// <summary>Mark a UTC-convention DB value as UTC so it serialises with a 'Z' and clients convert it correctly.</summary>
        public static DateTime AsUtc(DateTime dbValue) => DateTime.SpecifyKind(dbValue, DateTimeKind.Utc);
        public static DateTime? AsUtc(DateTime? dbValue) => dbValue.HasValue ? AsUtc(dbValue.Value) : null;
    }

    /// <summary>
    /// Optimistic-concurrency check on an UpdatedAt token. Tolerates &lt;1 ms of drift: MySQL datetime(6) truncates to
    /// microseconds while an in-memory DateTime carries 100 ns ticks, so an exact comparison could report a false conflict
    /// for a value the client legitimately echoed back.
    /// </summary>
    public static class Concurrency
    {
        public static bool IsStale(DateTime? expected, DateTime actual) =>
            expected.HasValue && Math.Abs((expected.Value - actual).Ticks) > TimeSpan.TicksPerMillisecond;
    }

    /// <summary>
    /// Stops the scheduler from re-opening an election that an administrator deliberately closed inside its
    /// scheduled window. (Legacy rule: Closed + now∈[From,To) ⇒ auto-open, so a manual close was undone within 10 s.)
    /// The decision uses the most recent OPEN_EVENT/CLOSE_EVENT audit row, so it survives API restarts without a schema change.
    /// </summary>
    public static class AutoOpenGuard
    {
        public static bool IsSuppressed(string? lastAction, string? lastUsername, DateTime lastOccurredAtUtc, DateTime scheduleFromLocal)
        {
            if (!string.Equals(lastAction, AuditActions.CloseEvent, StringComparison.Ordinal)) return false;
            if (string.Equals(lastUsername, AuditWriter.SystemUser, StringComparison.OrdinalIgnoreCase)) return false;

            var fromUtc = DateTime.SpecifyKind(scheduleFromLocal, DateTimeKind.Local).ToUniversalTime();
            return lastOccurredAtUtc >= fromUtc;   // a close BEFORE the window opened must not block the scheduled auto-open
        }
    }

    public static class ImageSniffer
    {
        /// <summary>Content type from magic bytes (the DB stores raw bytes with no type).</summary>
        public static string ContentType(byte[] b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
            if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
            return "application/octet-stream";
        }
    }

    public static class ImportParsing
    {
        /// <summary>
        /// A comma is a thousands separator only: it must be followed by exactly three digits and may not follow the decimal point.
        /// <c>NumberStyles.AllowThousands</c> alone accepts a comma anywhere between digits, so a decimal comma was read as a separator:
        /// "5,00" became 500 and "1500,50" became 150050 — and the amounts decide who is eligible to vote.
        /// </summary>
        public static bool CommasAreThousandsSeparators(string text) =>
            text.IndexOf(',') < 0
            || System.Text.RegularExpressions.Regex.IsMatch(text, @"^[+-]?[0-9]{1,3}(,[0-9]{3})+(\.[0-9]*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Parses "₱5,000.50" / "5000" using the invariant culture (the legacy importer's rules, except that a comma must be a thousands separator).</summary>
        public static bool TryParseMoney(string? raw, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrWhiteSpace(raw)) return true;      // blank == 0 (legacy behaviour)
            var cleaned = raw.Replace("₱", "").Trim();
            if (!CommasAreThousandsSeparators(cleaned)) return false;
            return decimal.TryParse(cleaned,
                NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value) && MoneyRules.IsValid(value);
        }

        /// <summary>
        /// "None", "Active", "Past Due" / "PastDue" / "past-due", "Paid" in any case; blank means None. Anything else is NOT a loan status:
        /// the legacy importer turned every unknown text into None, so a past-due member whose cell said "Past-Due" or "Pastdue." was
        /// silently imported as eligible to vote.
        /// </summary>
        public static bool TryParseLoanStatus(string? raw, out LoanStatus status)
        {
            var key = new string((raw ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '_').ToArray()).ToLowerInvariant();
            switch (key)
            {
                case "": case "none": status = LoanStatus.None; return true;
                case "active": status = LoanStatus.Active; return true;
                case "pastdue": status = LoanStatus.PastDue; return true;
                case "paid": status = LoanStatus.Paid; return true;
                default: status = LoanStatus.None; return false;
            }
        }
    }

    public static class UserCodes
    {
        /// <summary>Display code shown in the Users grid: {branch:D3}-{role:D4}-{id:D4} (the editor used D2 for role — inconsistent).</summary>
        public static string Format(int? branchId, int? roleId, int id) =>
            $"{(branchId ?? 0):D3}-{(roleId ?? 0):D4}-{id:D4}";

        /// <summary>Parses "001-0001-0005" (any digit widths) into its three numbers.</summary>
        public static bool TryParse(string? text, out int branchId, out int roleId, out int userId)
        {
            branchId = roleId = userId = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split('-');
            return parts.Length == 3
                && int.TryParse(parts[0], out branchId)
                && int.TryParse(parts[1], out roleId)
                && int.TryParse(parts[2], out userId);
        }
    }

    public static class DefaultPositionRules
    {
        /// <summary>Same four defaults the legacy Admin persisted and the API returned when an event had no rows.</summary>
        public static readonly (string Position, int MinVotes, int MaxVotes, int TotalSeats)[] All =
        {
            ("Board of Director",  1, 11, 11),
            ("Audit Committee",    1, 2,  2),
            ("Election Committee", 1, 1,  1),
            ("Secretary",          1, 2,  2),
        };
    }

    public static class CandidacyStatuses
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string Withdrawn = "Withdrawn";
        public static readonly string[] All = { Pending, Approved, Rejected, Withdrawn };
        public static bool IsValid(string? s) => All.Contains(s, StringComparer.Ordinal);
    }

    public static class CandidatePositions
    {
        /// <summary>Legacy enum order (CandidacyPosition): BoD, Election, Audit, Secretary.</summary>
        public static readonly string[] All = { "Board of Director", "Election Committee", "Audit Committee", "Secretary" };
        public static bool IsValid(string? s) => All.Contains(s, StringComparer.Ordinal);
    }

    /// <summary>Allowed values for the candidacy form dropdowns (identical to the legacy enums' display strings).</summary>
    public static class CandidacyLists
    {
        public static readonly string[] CivilStatuses = { "Single", "Married", "Widowed", "Separated", "Divorced" };
        public static readonly string[] Genders = { "Male", "Female", "Prefer not to say" };
        public static readonly string[] EducationLevels =
        {
            "Elementary", "High School", "Vocational", "Associate's Degree",
            "Bachelor's Degree", "Master's Degree", "Doctorate"
        };
    }
}
