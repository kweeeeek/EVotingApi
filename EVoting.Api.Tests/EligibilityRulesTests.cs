using System.Globalization;
using EVoting.Api.Infrastructure.Entities;
using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// The voting-eligibility rule of a member, now with configurable thresholds (Part 15 Eligibility tests 5-9):
    ///   eligible  =  ShareCapital >= MinShareCapital  AND  SavingsDeposit >= MinSavingsDeposit  AND  LoanStatus is not PastDue.
    /// The defaults (3,000 and 4,000) are the rule the system always had; only the numbers became settings.
    /// The database side (save, reload, audit, cache, open elections) is covered by the integration suites.
    /// </summary>
    public class MemberEligibilityPolicyTests
    {
        private static readonly EligibilityOptions Default = new();

        private static (bool IsEligible, string Remarks) Eval(decimal share, decimal savings, LoanStatus loan = LoanStatus.None, EligibilityOptions? options = null) =>
            MemberEligibilityPolicy.Evaluate(share, savings, loan, options);

        [Fact]
        public void The_default_thresholds_are_the_legacy_ones()
        {
            Assert.Equal(3000m, Default.MinShareCapital);
            Assert.Equal(4000m, Default.MinSavingsDeposit);
            Assert.Equal("Eligibility", EligibilityOptions.Section);
        }

        // ── a member who meets both thresholds ────────────────────────────────────────────────────────────

        [Fact]
        public void A_member_who_meets_both_thresholds_exactly_is_eligible()
        {
            var (ok, remarks) = Eval(3000m, 4000m);
            Assert.True(ok);
            Assert.Equal("Eligible", remarks);
        }

        [Fact]
        public void A_member_above_both_thresholds_is_eligible()
        {
            Assert.True(Eval(1_000_000m, 2_500_000.75m).IsEligible);
        }

        [Theory]
        [InlineData(LoanStatus.None)]
        [InlineData(LoanStatus.Active)]
        [InlineData(LoanStatus.Paid)]
        public void Only_a_past_due_loan_blocks_a_member_whose_balances_are_enough(LoanStatus loan)
        {
            Assert.True(Eval(3000m, 4000m, loan).IsEligible);
        }

        // ── one threshold not met ─────────────────────────────────────────────────────────────────────────

        [Fact]
        public void One_centavo_below_the_share_capital_threshold_is_not_eligible_and_the_reason_says_why()
        {
            var (ok, remarks) = Eval(2999.99m, 4000m);
            Assert.False(ok);
            Assert.Equal("Share Capital is below ₱3,000 (Current: ₱2,999.99)", remarks);
        }

        [Fact]
        public void One_centavo_below_the_savings_threshold_is_not_eligible_and_the_reason_says_why()
        {
            var (ok, remarks) = Eval(3000m, 3999.99m);
            Assert.False(ok);
            Assert.Equal("Savings Deposit is below ₱4,000 (Current: ₱3,999.99)", remarks);
        }

        [Fact]
        public void Both_below_gives_both_reasons_share_capital_first()
        {
            var (ok, remarks) = Eval(0m, 0m);
            Assert.False(ok);
            Assert.Equal("Share Capital is below ₱3,000 (Current: ₱0.00); Savings Deposit is below ₱4,000 (Current: ₱0.00)", remarks);
        }

        [Fact]
        public void A_past_due_loan_makes_a_member_with_enough_money_not_eligible()
        {
            var (ok, remarks) = Eval(10_000m, 10_000m, LoanStatus.PastDue);
            Assert.False(ok);
            Assert.Equal("Loan Status is Past Due", remarks);
        }

        [Fact]
        public void Every_reason_is_listed_when_everything_fails()
        {
            var (ok, remarks) = Eval(100.5m, 200m, LoanStatus.PastDue);
            Assert.False(ok);
            Assert.Equal("Share Capital is below ₱3,000 (Current: ₱100.50); Savings Deposit is below ₱4,000 (Current: ₱200.00); Loan Status is Past Due", remarks);
        }

        // ── thresholds that were changed ──────────────────────────────────────────────────────────────────

        [Fact]
        public void A_changed_threshold_decides_the_outcome_and_the_reason_quotes_the_new_number()
        {
            var options = new EligibilityOptions { MinShareCapital = 5000m, MinSavingsDeposit = 1000m };

            var below = Eval(4999.99m, 1000m, options: options);
            Assert.False(below.IsEligible);
            Assert.Equal("Share Capital is below ₱5,000 (Current: ₱4,999.99)", below.Remarks);

            Assert.True(Eval(5000m, 1000m, options: options).IsEligible);
            Assert.True(Eval(5000m, 1000m, options: options).Remarks == "Eligible");
        }

        [Fact]
        public void The_same_member_can_be_eligible_under_one_setting_and_not_under_another()
        {
            var lenient = new EligibilityOptions { MinShareCapital = 100m, MinSavingsDeposit = 100m };
            Assert.True(Eval(500m, 500m, options: lenient).IsEligible);
            Assert.False(Eval(500m, 500m).IsEligible);                       // the default 3,000 / 4,000
        }

        [Fact]
        public void A_threshold_with_centavos_keeps_them_in_the_reason_so_the_text_is_never_misleading()
        {
            var options = new EligibilityOptions { MinShareCapital = 3000.25m, MinSavingsDeposit = 4000m };
            var (ok, remarks) = Eval(3000.24m, 4000m, options: options);
            Assert.False(ok);
            Assert.Equal("Share Capital is below ₱3,000.25 (Current: ₱3,000.24)", remarks);
        }

        [Fact]
        public void A_threshold_of_zero_means_nobody_is_held_back_by_that_balance()
        {
            var options = new EligibilityOptions { MinShareCapital = 0m, MinSavingsDeposit = 0m };
            Assert.True(Eval(0m, 0m, options: options).IsEligible);
            Assert.False(Eval(0m, 0m, LoanStatus.PastDue, options).IsEligible);       // the loan rule is not a threshold and is kept
        }

        [Fact]
        public void No_options_at_all_means_the_defaults()
        {
            Assert.False(MemberEligibilityPolicy.Evaluate(2999m, 4000m, LoanStatus.None, null).IsEligible);
            Assert.True(MemberEligibilityPolicy.Evaluate(3000m, 4000m, LoanStatus.None, null).IsEligible);
        }

        [Theory]
        [InlineData("3000", "3,000")]
        [InlineData("3000.00", "3,000")]
        [InlineData("3000.25", "3,000.25")]
        [InlineData("1234567", "1,234,567")]
        [InlineData("1234567.5", "1,234,567.50")]
        [InlineData("0", "0")]
        [InlineData("0.05", "0.05")]
        public void A_threshold_is_written_without_centavos_when_it_is_a_whole_peso_amount(string value, string expected)
        {
            Assert.Equal(expected, MemberEligibilityPolicy.Threshold(decimal.Parse(value, CultureInfo.InvariantCulture)));
        }

        [Fact]
        public void The_reason_text_does_not_depend_on_the_language_of_the_server()
        {
            var before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");            // a culture that writes 3.000,00
                var (_, remarks) = Eval(2999.99m, 4000m);
                Assert.Equal("Share Capital is below ₱3,000 (Current: ₱2,999.99)", remarks);
                Assert.Equal("3,000.25", MemberEligibilityPolicy.Threshold(3000.25m));
            }
            finally { CultureInfo.CurrentCulture = before; }
        }
    }

    public class MoneyRulesTests
    {
        [Theory]
        [InlineData("0")]
        [InlineData("0.01")]
        [InlineData("123.45")]
        [InlineData("100.10")]
        [InlineData("1.0000")]                        // trailing zeros are not extra decimal places
        [InlineData("3000")]
        [InlineData("1000000000000")]                 // the largest amount accepted
        [InlineData("999999999999.99")]
        public void An_amount_from_zero_to_the_maximum_with_at_most_two_decimal_places_is_valid(string text)
        {
            Assert.True(MoneyRules.IsValid(decimal.Parse(text, CultureInfo.InvariantCulture)));
        }

        [Theory]
        [InlineData("-0.01")]
        [InlineData("-1")]
        [InlineData("0.001")]                         // the database would round it away silently
        [InlineData("3000.255")]
        [InlineData("1000000000000.01")]
        public void A_negative_amount_a_third_decimal_place_or_an_amount_above_the_maximum_is_invalid(string text)
        {
            Assert.False(MoneyRules.IsValid(decimal.Parse(text, CultureInfo.InvariantCulture)));
        }

        [Fact]
        public void The_largest_decimal_is_not_a_valid_amount()
        {
            Assert.False(MoneyRules.IsValid(decimal.MaxValue));
            Assert.False(MoneyRules.IsValid(decimal.MinValue));
        }

        [Fact]
        public void The_message_names_the_field_and_the_allowed_range()
        {
            Assert.Equal("Share capital must be an amount from 0 to 1,000,000,000,000.00 with at most two decimal places.", MoneyRules.Message("Share capital"));
        }
    }

    public class EligibilityThresholdsTests
    {
        [Fact]
        public void The_applied_stamp_writes_both_numbers_with_two_decimals()
        {
            Assert.Equal("3000.00|4000.00", EligibilityThresholds.MakeStamp(3000m, 4000m));
            Assert.Equal("3000.50|0.00", EligibilityThresholds.MakeStamp(3000.5m, 0m));
            Assert.Equal("1234567.89|0.01", EligibilityThresholds.MakeStamp(1234567.89m, 0.01m));
        }

        [Fact]
        public void The_same_amount_written_with_different_scales_gives_the_same_stamp()
        {
            Assert.Equal(EligibilityThresholds.MakeStamp(3000m, 4000m), EligibilityThresholds.MakeStamp(3000.00m, 4000.0m));
            Assert.Equal(EligibilityThresholds.MakeStamp(3000m, 4000m), EligibilityThresholds.MakeStamp(3000.000m, 4000.0000m));
        }

        [Fact]
        public void A_different_amount_gives_a_different_stamp()
        {
            Assert.NotEqual(EligibilityThresholds.MakeStamp(3000m, 4000m), EligibilityThresholds.MakeStamp(3000.01m, 4000m));
            Assert.NotEqual(EligibilityThresholds.MakeStamp(3000m, 4000m), EligibilityThresholds.MakeStamp(3000m, 4000.01m));
            Assert.NotEqual(EligibilityThresholds.MakeStamp(3000m, 4000m), EligibilityThresholds.MakeStamp(4000m, 3000m));
        }

        [Fact]
        public void The_stamp_does_not_depend_on_the_language_of_the_server()
        {
            var before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("3000.50|4000.00", EligibilityThresholds.MakeStamp(3000.5m, 4000m));
            }
            finally { CultureInfo.CurrentCulture = before; }
        }

        [Fact]
        public void The_Stamp_property_is_the_stamp_of_its_own_two_numbers()
        {
            var t = new EligibilityThresholds(3000m, 4000m, EligibilityThresholds.FromDatabase, 3, null, null);
            Assert.Equal(EligibilityThresholds.MakeStamp(3000m, 4000m), t.Stamp);
        }

        [Fact]
        public void The_thresholds_convert_to_the_options_the_policy_uses()
        {
            var o = new EligibilityThresholds(2500.50m, 1500m, EligibilityThresholds.FromConfiguration, 0, null, null).ToOptions();
            Assert.Equal(2500.50m, o.MinShareCapital);
            Assert.Equal(1500m, o.MinSavingsDeposit);
        }

        [Fact]
        public void The_source_names_and_the_stored_setting_keys_are_fixed_because_stored_rows_and_documents_refer_to_them()
        {
            Assert.Equal("database", EligibilityThresholds.FromDatabase);
            Assert.Equal("configuration", EligibilityThresholds.FromConfiguration);
            Assert.Equal("Eligibility.MinShareCapital", SettingKeys.MinShareCapital);
            Assert.Equal("Eligibility.MinSavingsDeposit", SettingKeys.MinSavingsDeposit);
            Assert.Equal("Eligibility.AppliedStamp", SettingKeys.AppliedStamp);
        }
    }
}
