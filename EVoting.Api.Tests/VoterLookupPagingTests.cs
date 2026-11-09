using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    /// <summary>
    /// Server-side paging of the voter lookup (Part 15 VoterLookup tests 1-4, 7): which page and page size are accepted, where a page starts,
    /// how many pages there are, and how the search text is prepared. The database side (counts, order, speed) is measured by the integration suites.
    /// </summary>
    public class VoterLookupPagingTests
    {
        // ── page and page size ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void The_first_page_starts_at_offset_zero()
        {
            var (window, field, message) = VoterLookupPaging.Normalize(1, 50);
            Assert.Null(field);
            Assert.Null(message);
            Assert.Equal(1, window!.Value.Page);
            Assert.Equal(50, window.Value.PageSize);
            Assert.Equal(0L, window.Value.Offset);
        }

        [Theory]
        [InlineData(2, 50, 50)]
        [InlineData(3, 50, 100)]
        [InlineData(675, 50, 33_700)]
        [InlineData(1351, 50, 67_500)]          // the last page of the 67,515 voters of the production-size election
        [InlineData(2, 500, 500)]
        [InlineData(5, 25, 100)]
        [InlineData(1, 1, 0)]
        [InlineData(7, 1, 6)]
        public void A_page_starts_after_all_the_rows_of_the_pages_before_it(int page, int pageSize, int expectedOffset)
        {
            var (window, field, _) = VoterLookupPaging.Normalize(page, pageSize);
            Assert.Null(field);
            Assert.Equal((long)expectedOffset, window!.Value.Offset);
            Assert.Equal(pageSize, window.Value.PageSize);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void A_page_number_below_one_is_refused_and_the_field_is_named(int page)
        {
            var (window, field, message) = VoterLookupPaging.Normalize(page, 50);
            Assert.Null(window);
            Assert.Equal("page", field);
            Assert.Equal("Page must be 1 or greater.", message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void A_page_size_below_one_is_refused_and_the_field_is_named(int pageSize)
        {
            var (window, field, message) = VoterLookupPaging.Normalize(1, pageSize);
            Assert.Null(window);
            Assert.Equal("pageSize", field);
            Assert.Equal("Page size must be 1 or greater.", message);
        }

        [Fact]
        public void When_both_are_wrong_the_page_is_reported_first()
        {
            var (_, field, _) = VoterLookupPaging.Normalize(0, 0);
            Assert.Equal("page", field);
        }

        [Theory]
        [InlineData(500, 500)]
        [InlineData(501, 500)]
        [InlineData(1000, 500)]
        [InlineData(int.MaxValue, 500)]
        public void A_page_size_above_the_ceiling_is_cut_to_the_ceiling_not_refused(int asked, int served)
        {
            var (window, field, _) = VoterLookupPaging.Normalize(1, asked);
            Assert.Null(field);
            Assert.Equal(served, window!.Value.PageSize);
            Assert.Equal(VoterLookupLimits.MaxPageSize, window.Value.PageSize);
        }

        [Fact]
        public void The_offset_of_a_cut_page_size_uses_the_size_that_will_be_served()
        {
            // page 3 of "1000 per page" is page 3 of "500 per page": rows 1000-1499, not rows 2000-2999
            var (window, _, _) = VoterLookupPaging.Normalize(3, 1000);
            Assert.Equal(1000L, window!.Value.Offset);
        }

        [Fact]
        public void An_enormous_page_number_does_not_overflow_the_offset()
        {
            var (window, field, _) = VoterLookupPaging.Normalize(int.MaxValue, 500);
            Assert.Null(field);
            Assert.Equal((long)(int.MaxValue - 1) * 500, window!.Value.Offset);
            Assert.True(window.Value.Offset > int.MaxValue);
        }

        [Fact]
        public void The_default_page_size_and_the_ceiling_are_the_documented_ones()
        {
            Assert.Equal(50, VoterLookupLimits.DefaultPageSize);
            Assert.Equal(500, VoterLookupLimits.MaxPageSize);
            Assert.Equal(100, VoterLookupLimits.MaxSearchLength);
            Assert.True(VoterLookupLimits.DefaultPageSize <= VoterLookupLimits.MaxPageSize);
        }

        // ── number of pages ───────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(0, 50, 0)]
        [InlineData(1, 50, 1)]
        [InlineData(49, 50, 1)]
        [InlineData(50, 50, 1)]
        [InlineData(51, 50, 2)]
        [InlineData(100, 50, 2)]
        [InlineData(101, 50, 3)]
        [InlineData(67_515, 50, 1351)]          // the production-size election in docs/representative-election/06-voter-lookup-paging.md
        [InlineData(972_130, 50, 19_443)]       // the million-member stress election in the same document
        [InlineData(67_515, 500, 136)]
        [InlineData(10, 1, 10)]
        public void The_number_of_pages_is_the_row_count_divided_by_the_page_size_rounded_up(int totalCount, int pageSize, int expected)
        {
            Assert.Equal(expected, VoterLookupPaging.TotalPages(totalCount, pageSize));
        }

        [Fact]
        public void The_number_of_pages_never_overflows()
        {
            Assert.Equal(int.MaxValue, VoterLookupPaging.TotalPages(int.MaxValue, 1));
            Assert.Equal(int.MaxValue / 500 + 1, VoterLookupPaging.TotalPages(int.MaxValue, 500));
        }

        [Theory]
        [InlineData(-5, 50)]
        [InlineData(10, 0)]
        [InlineData(10, -1)]
        public void Nothing_to_page_means_zero_pages(int totalCount, int pageSize)
        {
            Assert.Equal(0, VoterLookupPaging.TotalPages(totalCount, pageSize));
        }

        [Fact]
        public void The_last_page_holds_the_remainder_and_the_page_after_it_starts_past_the_end()
        {
            const int total = 67_515;
            var pages = VoterLookupPaging.TotalPages(total, 50);
            var (last, _, _) = VoterLookupPaging.Normalize(pages, 50);
            var (after, _, _) = VoterLookupPaging.Normalize(pages + 1, 50);

            Assert.Equal(15L, total - last!.Value.Offset);            // 15 rows on the last page (doc 06)
            Assert.True(after!.Value.Offset >= total);                 // a page past the end is an empty page, not an error
        }

        // ── the search text ───────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        public void Blank_search_text_means_no_search(string? raw)
        {
            Assert.Null(VoterLookupPaging.CleanSearch(raw));
        }

        [Theory]
        [InlineData("valdez", "valdez")]
        [InlineData("  valdez  ", "valdez")]
        [InlineData("dela cruz", "dela cruz")]
        [InlineData("  de la  cruz ", "de la  cruz")]            // inner spacing is the typist's; only the ends are trimmed
        public void Search_text_is_trimmed(string raw, string expected)
        {
            Assert.Equal(expected, VoterLookupPaging.CleanSearch(raw));
        }

        [Fact]
        public void Search_text_is_cut_to_100_characters_after_trimming()
        {
            Assert.Equal(new string('a', 100), VoterLookupPaging.CleanSearch(new string('a', 100)));
            Assert.Equal(new string('a', 100), VoterLookupPaging.CleanSearch(new string('a', 101)));
            Assert.Equal(new string('a', 100), VoterLookupPaging.CleanSearch("   " + new string('a', 150) + "   "));
        }

        // ── which text could be part of a receipt code ────────────────────────────────────────────────────

        [Theory]
        [InlineData("EVT01-0000ABCD-DEADBEEF")]
        [InlineData("evt01-abcdef")]
        [InlineData("123")]
        [InlineData("dead-beef")]
        [InlineData("-")]
        [InlineData("EVT")]
        [InlineData("fade")]
        public void Text_made_only_of_receipt_characters_could_be_part_of_a_receipt(string text)
        {
            Assert.True(VoterLookupPaging.CouldBePartOfReceiptCode(text));
        }

        [Theory]
        [InlineData("valdez")]
        [InlineData("Santos")]
        [InlineData("cruz")]
        [InlineData("evt 01")]              // a space
        [InlineData("EVT01_ABC")]           // an underscore
        [InlineData("0001234x")]
        [InlineData("ÉVT")]
        [InlineData("g")]
        public void Any_other_character_means_it_cannot_be_part_of_a_receipt_so_the_receipt_look_up_is_skipped(string text)
        {
            Assert.False(VoterLookupPaging.CouldBePartOfReceiptCode(text));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void No_text_is_not_a_receipt_search(string? text)
        {
            Assert.False(VoterLookupPaging.CouldBePartOfReceiptCode(text));
        }

        [Fact]
        public void Every_character_of_a_real_receipt_code_is_inside_the_alphabet()
        {
            // the alphabet test must never reject text a real receipt contains, or a voter's own receipt could not be found
            for (int event_ = 1; event_ <= 123; event_ += 61)
            {
                var code = $"EVT{event_:00}-{0xA1B2C3D4:X8}-{0xE5F6A7B8:X8}";
                Assert.True(ReceiptCodeFormat.IsValid(code, event_));
                Assert.True(VoterLookupPaging.CouldBePartOfReceiptCode(code));
                foreach (var part in code.Split('-'))
                    Assert.True(VoterLookupPaging.CouldBePartOfReceiptCode(part));
            }
        }
    }
}
