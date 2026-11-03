using EVoting.Api.Services;
using Xunit;

namespace EVoting.Api.Tests
{
    public class ReceiptCodeFormatTests
    {
        [Theory]
        [InlineData("EVT03-A1B2C3D4-E5F6A7B8", 3)]
        [InlineData("EVT12-00000000-FFFFFFFF", 12)]
        [InlineData("EVT123-0A0B0C0D-0E0F1A1B", 123)]
        public void Server_and_kiosk_format_is_accepted_for_its_own_event(string code, int eventId)
        {
            Assert.True(ReceiptCodeFormat.IsValid(code, eventId));
        }

        [Fact]
        public void A_valid_code_for_another_event_is_rejected()
        {
            Assert.False(ReceiptCodeFormat.IsValid("EVT03-A1B2C3D4-E5F6A7B8", 4));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("EVT3-A1B2C3D4-E5F6A7B8")]            // event part needs at least two digits (D2)
        [InlineData("evt03-a1b2c3d4-e5f6a7b8")]            // generators emit upper-case hex only
        [InlineData("EVT03-A1B2C3D4-E5F6A7B")]             // short
        [InlineData("EVT03-A1B2C3D4-E5F6A7B8X")]           // long
        [InlineData("EVT03-G1B2C3D4-E5F6A7B8")]            // not hex
        [InlineData("EVT03-A1B2C3D4-E5F6A7B8\n")]          // trailing newline must not slip past '$'
        [InlineData(" EVT03-A1B2C3D4-E5F6A7B8")]
        [InlineData("EVT03_A1B2C3D4_E5F6A7B8")]
        [InlineData("EVT03-A1B2C3D4-E5F6A7B8-EXTRA-PADDING-TO-31")]
        public void Anything_else_is_rejected(string? code)
        {
            Assert.False(ReceiptCodeFormat.IsValid(code, 3));
        }

        [Fact]
        public void Column_width_is_30()
        {
            Assert.Equal(30, ReceiptCodeFormat.MaxLength);
        }
    }
}
