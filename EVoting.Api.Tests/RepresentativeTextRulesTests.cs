using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// Candidate codes and the text clean-up used by the Representative import and the roster screens
    /// (docs/representative-election/02-import-and-column-mapping.md, section "Cleaning and checking each cell").
    /// </summary>
    public class CandidateCodeRulesTests
    {
        [Theory]
        [InlineData("  ab-12 ", "AB-12")]
        [InlineData("rep/001", "REP/001")]
        [InlineData("a", "A")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        [InlineData(null, "")]
        public void Normalize_trims_and_upper_cases(string? raw, string expected)
        {
            Assert.Equal(expected, CandidateCodeRules.Normalize(raw));
        }

        [Fact]
        public void Normalize_never_removes_inner_characters()
        {
            // an inner space must stay visible to IsValid; silently closing it up would turn two different codes into one
            Assert.Equal("A B", CandidateCodeRules.Normalize("a b"));
        }

        [Theory]
        [InlineData("A")]
        [InlineData("7")]
        [InlineData("REP-001")]
        [InlineData("R/12.3_X")]
        [InlineData("A.B_C/D-E")]
        public void A_letter_or_digit_followed_by_letters_digits_and_dot_underscore_slash_dash_is_valid(string code)
        {
            Assert.True(CandidateCodeRules.IsValid(code));
        }

        [Fact]
        public void The_longest_valid_code_has_30_characters()
        {
            Assert.True(CandidateCodeRules.IsValid(new string('A', 30)));
            Assert.False(CandidateCodeRules.IsValid(new string('A', 31)));
            Assert.Equal(30, RepresentativeLimits.CandidateCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("-A")]            // must start with a letter or digit
        [InlineData(".A")]
        [InlineData("_A")]
        [InlineData("/A")]
        [InlineData("A B")]           // no inner space
        [InlineData("A#1")]
        [InlineData("A,B")]
        [InlineData("A;B")]
        [InlineData("a1")]            // IsValid expects the NORMALIZED (upper-case) form
        [InlineData("É1")]            // only plain A-Z / 0-9
        [InlineData("A\tB")]
        public void Anything_else_is_invalid(string code)
        {
            Assert.False(CandidateCodeRules.IsValid(code));
        }

        [Theory]
        [InlineData("A\n")]
        [InlineData("REP-001\n")]
        public void A_trailing_line_break_does_not_slip_past_the_pattern(string code)
        {
            // In a .NET regular expression '$' also matches just before a final line break. Normalize() trims such text away, so the import never
            // sees it, but IsValid is public and must give the same answer for any text it is handed.
            Assert.False(CandidateCodeRules.IsValid(code));
        }
    }

    public class RepTextTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void OneLine_turns_blank_text_into_null(string? raw)
        {
            Assert.Null(RepText.OneLine(raw));
        }

        [Fact]
        public void OneLine_collapses_runs_of_white_space_and_trims()
        {
            Assert.Equal("Juan dela Cruz", RepText.OneLine("  Juan   dela\tCruz\r\n"));
        }

        [Fact]
        public void OneLine_treats_a_non_breaking_space_like_a_space()
        {
            Assert.Equal("Juan dela Cruz", RepText.OneLine("Juan  dela Cruz"));
        }

        [Fact]
        public void OneLine_replaces_line_breaks_and_other_control_characters_with_a_space()
        {
            Assert.Equal("a b c d", RepText.OneLine("a\nb\rc\u0007d"));
        }

        [Fact]
        public void OneLine_composes_accents_to_one_character_so_the_same_name_compares_equal()
        {
            var decomposed = "José";          // e + combining acute accent
            var composed = "José";            // precomposed é
            Assert.Equal(composed, RepText.OneLine(decomposed));
            Assert.Equal(RepText.OneLine(composed), RepText.OneLine(decomposed));
        }

        [Fact]
        public void OneLine_keeps_letters_digits_and_punctuation_untouched()
        {
            Assert.Equal("Dela Cruz, Juan Jr. (No. 5) - 'A/B'", RepText.OneLine("Dela Cruz, Juan Jr. (No. 5) - 'A/B'"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  \r\n ")]
        public void MultiLine_turns_blank_text_into_null(string? raw)
        {
            Assert.Null(RepText.MultiLine(raw));
        }

        [Fact]
        public void MultiLine_keeps_line_breaks_as_LF_and_removes_other_control_characters()
        {
            Assert.Equal("one\ntwo\nthree", RepText.MultiLine("  one\r\ntwo\rthree\u0007  "));
        }

        [Fact]
        public void MultiLine_keeps_blank_lines_between_paragraphs()
        {
            Assert.Equal("a\n\nb", RepText.MultiLine("a\r\n\r\nb"));
        }

        [Theory]
        [InlineData("09171234567", true)]
        [InlineData("+63 (917) 123-4567", true)]
        [InlineData("123", true)]
        [InlineData("02.8123/4567", true)]
        [InlineData("12", false)]                                  // too short
        [InlineData("123456789012345678901", false)]               // 21 characters
        [InlineData("abc", false)]
        [InlineData("0917 123 4567 ext", false)]
        [InlineData("0917#123", false)]
        public void IsPhone_accepts_only_digits_spaces_and_plus_minus_brackets_dot_slash_of_3_to_20_characters(string value, bool expected)
        {
            Assert.Equal(expected, RepText.IsPhone(value));
        }

        [Fact]
        public void IsPhone_accepts_exactly_20_characters()
        {
            Assert.True(RepText.IsPhone(new string('7', 20)));
            Assert.False(RepText.IsPhone(new string('7', 21)));
        }

        [Fact]
        public void IsPhone_rejects_a_trailing_line_break()
        {
            // same '$' behaviour as in CandidateCodeRules
            Assert.False(RepText.IsPhone("0917123\n"));
        }

        [Theory]
        [InlineData("married", "Married")]
        [InlineData("WIDOWED", "Widowed")]
        [InlineData("Single", "Single")]
        public void Canonical_returns_the_spelling_of_the_allowed_list(string typed, string expected)
        {
            Assert.Equal(expected, RepText.Canonical(typed, CandidacyLists.CivilStatuses));
        }

        [Theory]
        [InlineData("Engaged")]
        [InlineData("")]
        [InlineData("Marrie")]
        [InlineData("Married ")]       // callers clean the text first; Canonical itself does not trim
        public void Canonical_returns_null_for_text_that_is_not_in_the_list(string typed)
        {
            Assert.Null(RepText.Canonical(typed, CandidacyLists.CivilStatuses));
        }

        [Theory]
        [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
        [InlineData("+1", "'+1")]
        [InlineData("-1", "'-1")]
        [InlineData("@cmd", "'@cmd")]
        [InlineData("\tx", "'\tx")]
        [InlineData("\rx", "'\rx")]
        [InlineData("Juan", "Juan")]
        [InlineData("A=B", "A=B")]            // only a LEADING character can start a formula
        [InlineData("", "")]
        public void Safe_prefixes_text_a_spreadsheet_would_read_as_a_formula(string value, string expected)
        {
            Assert.Equal(expected, RepText.Safe(value));
        }

        [Fact]
        public void Safe_turns_null_into_an_empty_string()
        {
            Assert.Equal("", RepText.Safe(null));
        }
    }

    public class EventTypesAndResolutionsTests
    {
        [Theory]
        [InlineData(null, "GENERAL_ASSEMBLY")]
        [InlineData("", "GENERAL_ASSEMBLY")]
        [InlineData("   ", "GENERAL_ASSEMBLY")]
        [InlineData("GENERAL_ASSEMBLY", "GENERAL_ASSEMBLY")]
        [InlineData("general assembly", "GENERAL_ASSEMBLY")]
        [InlineData("general-assembly", "GENERAL_ASSEMBLY")]
        [InlineData("representative", "REPRESENTATIVE")]
        [InlineData(" Representative ", "REPRESENTATIVE")]
        public void Event_type_text_is_normalised_and_a_missing_one_means_General_Assembly(string? raw, string expected)
        {
            Assert.Equal(expected, EventTypes.Normalize(raw));
        }

        [Theory]
        [InlineData("RepresentativeX")]
        [InlineData("ASSEMBLY")]
        [InlineData("1")]
        public void An_unknown_event_type_is_refused_not_defaulted(string raw)
        {
            Assert.Null(EventTypes.Normalize(raw));
        }

        [Fact]
        public void Only_the_two_known_event_types_are_valid()
        {
            Assert.True(EventTypes.IsValid(EventTypes.GeneralAssembly));
            Assert.True(EventTypes.IsValid(EventTypes.Representative));
            Assert.False(EventTypes.IsValid("representative"));       // the stored form is exact
            Assert.False(EventTypes.IsValid(null));
            Assert.False(EventTypes.IsValid(""));
        }

        [Theory]
        [InlineData("use existing", "USE_EXISTING")]
        [InlineData("USE_EXISTING", "USE_EXISTING")]
        [InlineData("Update-Profile", "UPDATE_PROFILE")]
        [InlineData(" skip ", "SKIP")]
        public void Import_resolution_text_is_normalised(string raw, string expected)
        {
            Assert.Equal(expected, ImportResolutions.Normalize(raw));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("merge")]
        [InlineData("overwrite")]
        public void An_unknown_or_blank_resolution_is_null(string? raw)
        {
            Assert.Null(ImportResolutions.Normalize(raw));
        }

        [Fact]
        public void The_import_batch_statuses_are_the_seven_the_database_allows()
        {
            Assert.Equal(7, RepresentativeImportStatuses.All.Length);
            Assert.Equal(7, RepresentativeImportStatuses.All.Distinct().Count());
            foreach (var s in new[] { "UPLOADED", "VALIDATING", "VALIDATED", "INVALID", "COMMITTED", "FAILED", "CANCELLED" })
                Assert.Contains(s, RepresentativeImportStatuses.All);
        }
    }
}
