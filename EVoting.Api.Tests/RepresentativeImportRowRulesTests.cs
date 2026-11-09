using System.ComponentModel.DataAnnotations;
using EVoting.Api.Models.Admin;
using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// The field-level checks of one spreadsheet row of a candidate import (Part 15 Representative tests 8 and 10: reject invalid candidate
    /// profiles, preview invalid rows). The column-by-column rules are listed in docs/representative-election/02-import-and-column-mapping.md.
    /// </summary>
    public class RepresentativeImportRowRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 4, 10, 0, 0);

        private static RepImportRowIn Valid() => new() { RowNumber = 2, CandidateCode = "REP-001", CandidateName = "Ana Reyes", Position = "Delegate" };

        private static ImportRowCheck Check(RepImportRowIn row) => RepresentativeImportRowRules.Check(row, Today);

        private static void AssertOnlyError(ImportRowCheck c, string code)
        {
            Assert.True(c.HasErrors, "an error was expected");
            Assert.Equal(code, c.ErrorCodes);
        }

        // ── a good row ────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_row_with_only_the_three_core_cells_is_accepted()
        {
            var c = Check(Valid());
            Assert.False(c.HasErrors);
            Assert.Equal("REP-001", c.CandidateCode);
            Assert.Equal("Ana Reyes", c.CandidateName);
            Assert.Equal("Delegate", c.Position);
            Assert.Null(c.AccountCode);
            Assert.Null(c.BallotNumber);
            Assert.Equal("", c.ErrorCodes);
            Assert.Equal("", c.ErrorMessage);
        }

        [Fact]
        public void Every_cell_of_a_complete_row_is_cleaned_and_kept()
        {
            var c = Check(new RepImportRowIn
            {
                RowNumber = 7,
                CandidateCode = " rep-001 ",
                CandidateName = "  Ana   Reyes ",
                AccountCode = "  0001234 ",
                Position = " Delegate ",
                CellNo = "0917 123 4567",
                TelNo = "(02) 8123-4567",
                CivilStatus = "married",
                Gender = "FEMALE",
                DateOfBirth = new DateTime(1985, 3, 9, 14, 30, 0),
                HighestEducation = "Bachelor's Degree",
                PreviousPositions = "Treasurer 2019\r\nSecretary 2021",
                SpecialSkills = "Accounting",
                Reason = "Wants to serve",
                BallotNumber = " 12 ",
            });

            Assert.False(c.HasErrors);
            Assert.Equal("REP-001", c.CandidateCode);
            Assert.Equal("Ana Reyes", c.CandidateName);
            Assert.Equal("0001234", c.AccountCode);
            Assert.Equal("Delegate", c.Position);
            Assert.Equal("0917 123 4567", c.CellNo);
            Assert.Equal("(02) 8123-4567", c.TelNo);
            Assert.Equal("Married", c.CivilStatus);
            Assert.Equal("Female", c.Gender);
            Assert.Equal(new DateTime(1985, 3, 9), c.DateOfBirth);
            Assert.Equal("Bachelor's Degree", c.HighestEducation);
            Assert.Equal("Treasurer 2019\nSecretary 2021", c.PreviousPositions);
            Assert.Equal("Accounting", c.SpecialSkills);
            Assert.Equal("Wants to serve", c.Reason);
            Assert.Equal(12, c.BallotNumber);
        }

        // ── candidate code ────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_candidate_code_is_refused(string? code)
        {
            var row = Valid(); row.CandidateCode = code;
            var c = Check(row);
            AssertOnlyError(c, RepresentativeImportRowRules.CodeInvalid);
            Assert.Null(c.CandidateCode);
            Assert.Equal("Candidate code is required.", c.ErrorMessage);
        }

        [Theory]
        [InlineData("A B")]
        [InlineData("-A")]
        [InlineData("A#1")]
        [InlineData("É1")]
        public void A_candidate_code_with_other_characters_is_refused_with_the_rule_in_words(string code)
        {
            var row = Valid(); row.CandidateCode = code;
            var c = Check(row);
            AssertOnlyError(c, RepresentativeImportRowRules.CodeInvalid);
            Assert.Equal(CandidateCodeRules.Requirement, c.ErrorMessage);
        }

        [Fact]
        public void A_candidate_code_is_30_characters_at_most()
        {
            var ok = Valid(); ok.CandidateCode = new string('a', 30);
            Assert.False(Check(ok).HasErrors);
            Assert.Equal(new string('A', 30), Check(ok).CandidateCode);

            var tooLong = Valid(); tooLong.CandidateCode = new string('a', 31);
            AssertOnlyError(Check(tooLong), RepresentativeImportRowRules.CodeInvalid);
        }

        [Fact]
        public void A_line_break_typed_into_a_code_cell_does_not_make_the_code_invalid_once_cleaned()
        {
            var row = Valid(); row.CandidateCode = "rep-01\n";
            var c = Check(row);
            Assert.False(c.HasErrors);
            Assert.Equal("REP-01", c.CandidateCode);
        }

        // ── name, account code, position ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_missing_name_is_not_a_field_error_because_the_member_may_supply_it()
        {
            var row = Valid(); row.CandidateName = null; row.AccountCode = "0001234";
            var c = Check(row);
            Assert.False(c.HasErrors);
            Assert.Null(c.CandidateName);
        }

        [Fact]
        public void A_name_is_200_characters_at_most()
        {
            var ok = Valid(); ok.CandidateName = new string('n', 200);
            Assert.False(Check(ok).HasErrors);

            var tooLong = Valid(); tooLong.CandidateName = new string('n', 201);
            var c = Check(tooLong);
            AssertOnlyError(c, RepresentativeImportRowRules.TooLong);
            Assert.Contains("200", c.ErrorMessage);
        }

        [Fact]
        public void An_account_code_is_20_characters_at_most()
        {
            var ok = Valid(); ok.AccountCode = new string('9', 20);
            Assert.False(Check(ok).HasErrors);

            var tooLong = Valid(); tooLong.AccountCode = new string('9', 21);
            AssertOnlyError(Check(tooLong), RepresentativeImportRowRules.TooLong);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_missing_position_is_refused(string? position)
        {
            var row = Valid(); row.Position = position;
            var c = Check(row);
            AssertOnlyError(c, RepresentativeImportRowRules.PositionRequired);
            Assert.Equal("Position is required.", c.ErrorMessage);
        }

        [Fact]
        public void A_position_is_100_characters_at_most()
        {
            var ok = Valid(); ok.Position = new string('p', 100);
            Assert.False(Check(ok).HasErrors);

            var tooLong = Valid(); tooLong.Position = new string('p', 101);
            AssertOnlyError(Check(tooLong), RepresentativeImportRowRules.TooLong);
        }

        // ── phone numbers ─────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("abc")]
        [InlineData("12")]
        [InlineData("0917#123")]
        public void An_invalid_cell_number_or_telephone_is_refused(string phone)
        {
            var cell = Valid(); cell.CellNo = phone;
            AssertOnlyError(Check(cell), RepresentativeImportRowRules.PhoneInvalid);
            Assert.Contains("Cell number", Check(cell).ErrorMessage);

            var tel = Valid(); tel.TelNo = phone;
            AssertOnlyError(Check(tel), RepresentativeImportRowRules.PhoneInvalid);
            Assert.Contains("Telephone number", Check(tel).ErrorMessage);
        }

        [Fact]
        public void Two_bad_phone_numbers_give_two_messages_but_the_code_is_listed_once()
        {
            var row = Valid(); row.CellNo = "abc"; row.TelNo = "xyz";
            var c = Check(row);
            Assert.Equal(2, c.Errors.Count);
            Assert.Equal("PHONE_INVALID", c.ErrorCodes);
        }

        // ── civil status, gender ──────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("single", "Single")]
        [InlineData("MARRIED", "Married")]
        [InlineData("Widowed", "Widowed")]
        [InlineData("separated", "Separated")]
        [InlineData("divorced", "Divorced")]
        public void Civil_status_takes_the_spelling_of_the_candidacy_form(string typed, string expected)
        {
            var row = Valid(); row.CivilStatus = typed;
            var c = Check(row);
            Assert.False(c.HasErrors);
            Assert.Equal(expected, c.CivilStatus);
        }

        [Fact]
        public void An_unknown_civil_status_is_refused_and_the_message_lists_the_allowed_values()
        {
            var row = Valid(); row.CivilStatus = "Engaged";
            var c = Check(row);
            AssertOnlyError(c, RepresentativeImportRowRules.CivilStatusInvalid);
            Assert.Contains("Single, Married, Widowed, Separated, Divorced", c.ErrorMessage);
            Assert.Null(c.CivilStatus);
        }

        [Theory]
        [InlineData("male", "Male")]
        [InlineData("FEMALE", "Female")]
        [InlineData("prefer not to say", "Prefer not to say")]
        public void Gender_takes_the_spelling_of_the_candidacy_form(string typed, string expected)
        {
            var row = Valid(); row.Gender = typed;
            var c = Check(row);
            Assert.False(c.HasErrors);
            Assert.Equal(expected, c.Gender);
        }

        [Fact]
        public void An_unknown_gender_is_refused_and_the_message_lists_the_allowed_values()
        {
            var row = Valid(); row.Gender = "X";
            var c = Check(row);
            AssertOnlyError(c, RepresentativeImportRowRules.GenderInvalid);
            Assert.Contains("Male, Female, Prefer not to say", c.ErrorMessage);
        }

        // ── date of birth ─────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void A_date_of_birth_keeps_only_the_date()
        {
            var row = Valid(); row.DateOfBirth = new DateTime(1990, 5, 6, 13, 45, 0);
            Assert.Equal(new DateTime(1990, 5, 6), Check(row).DateOfBirth);
        }

        [Fact]
        public void A_cell_that_could_not_be_read_as_a_date_is_reported_with_its_text()
        {
            var row = Valid(); row.DateOfBirthRaw = " 31/02/1990 "; row.DateOfBirth = null;
            var c = Check(row);
            AssertOnlyError(c, RepresentativeImportRowRules.DobInvalid);
            Assert.Equal("Date of birth '31/02/1990' is not a valid date.", c.ErrorMessage);
            Assert.Null(c.DateOfBirth);
        }

        [Fact]
        public void A_blank_raw_date_cell_is_not_an_error()
        {
            var row = Valid(); row.DateOfBirthRaw = "   "; row.DateOfBirth = null;
            Assert.False(Check(row).HasErrors);
        }

        [Fact]
        public void A_date_of_birth_today_is_allowed_and_tomorrow_is_refused()
        {
            var today = Valid(); today.DateOfBirth = Today.Date.AddHours(15);
            Assert.False(Check(today).HasErrors);

            var tomorrow = Valid(); tomorrow.DateOfBirth = Today.Date.AddDays(1);
            var c = Check(tomorrow);
            AssertOnlyError(c, RepresentativeImportRowRules.DobInvalid);
            Assert.Equal("Date of birth cannot be in the future.", c.ErrorMessage);
        }

        [Fact]
        public void A_date_of_birth_before_1900_is_refused_and_the_first_day_of_1900_is_accepted()
        {
            var old = Valid(); old.DateOfBirth = new DateTime(1899, 12, 31);
            var c = Check(old);
            AssertOnlyError(c, RepresentativeImportRowRules.DobInvalid);
            Assert.Equal("Date of birth is before 1900.", c.ErrorMessage);

            var edge = Valid(); edge.DateOfBirth = new DateTime(1900, 1, 1);
            Assert.False(Check(edge).HasErrors);
        }

        // ── free text ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Highest_education_is_200_characters_at_most()
        {
            var ok = Valid(); ok.HighestEducation = new string('e', 200);
            Assert.False(Check(ok).HasErrors);
            var tooLong = Valid(); tooLong.HighestEducation = new string('e', 201);
            AssertOnlyError(Check(tooLong), RepresentativeImportRowRules.TooLong);
        }

        [Fact]
        public void Previous_positions_and_special_skills_are_500_characters_at_most()
        {
            var ok = Valid(); ok.PreviousPositions = new string('p', 500); ok.SpecialSkills = new string('s', 500);
            Assert.False(Check(ok).HasErrors);

            var tooLong = Valid(); tooLong.PreviousPositions = new string('p', 501); tooLong.SpecialSkills = new string('s', 501);
            var c = Check(tooLong);
            Assert.Equal(2, c.Errors.Count);
            Assert.Equal("TOO_LONG", c.ErrorCodes);
        }

        [Fact]
        public void The_reason_is_2000_characters_at_most()
        {
            var ok = Valid(); ok.Reason = new string('r', 2000);
            Assert.False(Check(ok).HasErrors);
            var tooLong = Valid(); tooLong.Reason = new string('r', 2001);
            AssertOnlyError(Check(tooLong), RepresentativeImportRowRules.TooLong);
        }

        [Fact]
        public void The_length_is_counted_after_cleaning_so_extra_blanks_do_not_count()
        {
            var row = Valid(); row.CandidateName = new string('n', 200) + "      ";
            Assert.False(Check(row).HasErrors);
        }

        // ── ballot number ─────────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData("1", 1)]
        [InlineData("12", 12)]
        [InlineData(" 12 ", 12)]
        [InlineData("007", 7)]
        [InlineData("1000000", 1_000_000)]
        public void A_printed_ballot_number_is_a_whole_number_from_1_to_1000000(string typed, int expected)
        {
            var row = Valid(); row.BallotNumber = typed;
            var c = Check(row);
            Assert.False(c.HasErrors);
            Assert.Equal(expected, c.BallotNumber);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("+5")]
        [InlineData("1000001")]
        [InlineData("12.5")]
        [InlineData("1,000")]
        [InlineData("abc")]
        [InlineData("99999999999999999999")]
        public void Anything_else_as_a_ballot_number_is_refused(string typed)
        {
            var row = Valid(); row.BallotNumber = typed;
            var c = Check(row);
            AssertOnlyError(c, RepresentativeImportRowRules.BallotNumberInvalid);
            Assert.Null(c.BallotNumber);
        }

        [Fact]
        public void A_blank_ballot_number_means_none_was_given()
        {
            var row = Valid(); row.BallotNumber = "   ";
            var c = Check(row);
            Assert.False(c.HasErrors);
            Assert.Null(c.BallotNumber);
        }

        // ── all the problems of a row at once ─────────────────────────────────────────────────────────────

        [Fact]
        public void Every_problem_of_a_row_is_reported_together_with_each_code_listed_once()
        {
            var c = Check(new RepImportRowIn
            {
                RowNumber = 9,
                CandidateCode = "",
                CandidateName = new string('n', 201),
                Position = null,
                CellNo = "abc",
                CivilStatus = "Engaged",
                Gender = "X",
                DateOfBirthRaw = "someday",
                BallotNumber = "0",
            });

            Assert.True(c.HasErrors);
            Assert.Equal(8, c.Errors.Count);
            Assert.Equal("CODE_INVALID,TOO_LONG,POSITION_REQUIRED,PHONE_INVALID,CIVIL_STATUS_INVALID,GENDER_INVALID,DOB_INVALID,BALLOT_NUMBER_INVALID", c.ErrorCodes);
            Assert.Contains("Candidate code is required.", c.ErrorMessage);
            Assert.Contains("Position is required.", c.ErrorMessage);
        }

        // ── which stored error lists describe a row that is wrong by itself ───────────────────────────────

        [Theory]
        [InlineData("CODE_INVALID", true)]
        [InlineData("CODE_INVALID,TOO_LONG", true)]
        [InlineData("POSITION_REQUIRED, PHONE_INVALID", true)]          // a space after the comma is allowed
        [InlineData("CIVIL_STATUS_INVALID,GENDER_INVALID,DOB_INVALID,BALLOT_NUMBER_INVALID", true)]
        [InlineData("DUPLICATE_IN_FILE", false)]
        [InlineData("CODE_INVALID,DUPLICATE_IN_FILE", false)]
        [InlineData("NAME_REQUIRED", false)]                            // decided later, when the member is known
        [InlineData("POSITION_NOT_CONFIGURED", false)]
        [InlineData("SOMETHING_NEW", false)]
        [InlineData("", false)]
        [InlineData("   ", false)]
        [InlineData(null, false)]
        public void A_row_whose_only_problems_are_field_level_stays_invalid_without_being_examined_again(string? errorCodes, bool expected)
        {
            Assert.Equal(expected, RepresentativeImportRowRules.IsFieldLevel(errorCodes));
        }

        [Fact]
        public void Every_code_Check_itself_can_produce_counts_as_field_level()
        {
            // Run Check on a row built to trigger every one of its codes and confirm IsFieldLevel accepts what it returned.
            var c = Check(new RepImportRowIn
            {
                CandidateCode = "", Position = null, CellNo = "abc", CivilStatus = "Engaged", Gender = "X", DateOfBirthRaw = "x", BallotNumber = "0",
                CandidateName = new string('n', 201),
            });
            Assert.True(RepresentativeImportRowRules.IsFieldLevel(c.ErrorCodes));
        }
    }

    /// <summary>
    /// The request model accepts a little more than the stored limits, so a value that is too long for the database reaches the row check and is
    /// reported on its own row, instead of the whole request being refused by model validation.
    /// </summary>
    public class RepImportRequestCapsTests
    {
        private static int Cap(string property)
        {
            var p = typeof(RepImportRowIn).GetProperty(property)!;
            var attribute = (MaxLengthAttribute?)Attribute.GetCustomAttribute(p, typeof(MaxLengthAttribute));
            Assert.NotNull(attribute);
            return attribute!.Length;
        }

        [Theory]
        [InlineData("CandidateCode", RepresentativeLimits.CandidateCode)]
        [InlineData("CandidateName", RepresentativeLimits.CandidateName)]
        [InlineData("AccountCode", FieldLimits.AccountCode)]
        [InlineData("Position", FieldLimits.CandidatePosition)]
        [InlineData("CellNo", RepresentativeLimits.PhoneNumber)]
        [InlineData("TelNo", RepresentativeLimits.PhoneNumber)]
        [InlineData("CivilStatus", RepresentativeLimits.ShortText)]
        [InlineData("Gender", RepresentativeLimits.ShortText)]
        [InlineData("HighestEducation", RepresentativeLimits.Education)]
        [InlineData("PreviousPositions", RepresentativeLimits.PreviousPositions)]
        [InlineData("SpecialSkills", RepresentativeLimits.SpecialSkills)]
        [InlineData("Reason", RepresentativeLimits.Reason)]
        public void The_request_cap_of_a_cell_is_not_below_the_stored_limit(string property, int storedLimit)
        {
            Assert.True(Cap(property) >= storedLimit, property + ": the request cap " + Cap(property) + " is below the stored limit " + storedLimit);
        }

        [Fact]
        public void The_ballot_number_text_can_hold_the_largest_allowed_number()
        {
            Assert.True(Cap("BallotNumber") >= RepresentativeLimits.MaxBallotNumber.ToString().Length);
        }

        [Fact]
        public void A_valid_MaxVotes_always_fits_into_one_ballot_request()
        {
            Assert.True(RepresentativeLimits.MaxVotesPerPosition <= RepresentativeLimits.MaxSelectionsPerRequest);
        }

        [Fact]
        public void The_default_page_size_is_within_the_maximum_and_the_import_options_are_consistent()
        {
            Assert.True(RepresentativeLimits.DefaultPageSize <= RepresentativeLimits.MaxPageSize);

            var o = new RepresentativeImportOptions();
            Assert.True(o.MaxRowsPerRequest <= o.MaxRowsPerBatch);
            Assert.True(o.InsertChunkRows >= 1);
            Assert.True(o.MaxOpenBatchesPerEvent >= 1);
            Assert.True(o.MaxRowsPerBatch >= 2_000, "the import must handle far more than 1,000 candidates");
        }
    }
}
