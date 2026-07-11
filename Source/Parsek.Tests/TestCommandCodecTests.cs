using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the ParsekTestCommands percent codec and token splitter
    /// (<see cref="TestCommandProtocol"/>). Guards the wire-value contract: a
    /// MissionMark label with spaces, a path with an '=', or a literal '%' must
    /// survive a single encode/decode round-trip on one token. A regression here
    /// splits a spaced value across tokens (breaks parsing) or corrupts a '%'.
    /// </summary>
    public class TestCommandCodecTests
    {
        [Theory]
        [InlineData("mun landing start", "mun%20landing%20start")]
        [InlineData("a=b", "a%3Db")]
        [InlineData("50%", "50%25")]
        [InlineData("plain", "plain")]
        [InlineData("", "")]
        public void Encode_SpecialChars_ProducesPercentForm(string literal, string encoded)
        {
            Assert.Equal(encoded, TestCommandProtocol.Encode(literal));
        }

        [Theory]
        [InlineData("mun%20landing", "mun landing")]
        [InlineData("a%3Db", "a=b")]
        [InlineData("50%25", "50%")]
        [InlineData("plain", "plain")]
        public void Decode_PercentForm_ProducesLiteral(string encoded, string literal)
        {
            Assert.True(TestCommandProtocol.TryDecode(encoded, out string decoded));
            Assert.Equal(literal, decoded);
        }

        [Theory]
        [InlineData("mun landing start")]
        [InlineData("a=b=c")]
        [InlineData("100%")]
        [InlineData("tab\tsep")]
        [InlineData("percent%and=equals space")]
        public void EncodeDecode_RoundTrips(string literal)
        {
            string encoded = TestCommandProtocol.Encode(literal);
            Assert.True(TestCommandProtocol.TryDecode(encoded, out string back));
            Assert.Equal(literal, back);
        }

        [Fact]
        public void Encode_ControlChars_AreEscaped()
        {
            // Newline (0x0A) and DEL (0x7F) must not appear raw on a token.
            string encoded = TestCommandProtocol.Encode("line\nend");
            Assert.DoesNotContain("\n", encoded);
            Assert.Contains("%0A", encoded);
        }

        [Theory]
        [InlineData("%2")]     // truncated escape
        [InlineData("%")]      // bare percent at end
        [InlineData("ab%zz")]  // non-hex digits
        [InlineData("x%g0")]   // one bad hex digit
        public void Decode_MalformedEscape_Fails(string encoded)
        {
            Assert.False(TestCommandProtocol.TryDecode(encoded, out _));
        }

        [Theory]
        // Non-ASCII strings are built with \u escapes to keep the test SOURCE pure ASCII:
        // \u00fc = u-umlaut, \u4e2d\u6587 = CJK, \u00e9 = e-acute, \u2192 = a right-arrow.
        [InlineData("M\u00fcn")]
        [InlineData("\u4e2d\u6587")]
        [InlineData("caf\u00e9")]
        [InlineData("Mun \u2192 orbit")] // mixed ASCII + a non-ASCII arrow + a space
        public void Encode_NonAscii_ProducesPureAsciiAndRoundTrips(string literal)
        {
            string encoded = TestCommandProtocol.Encode(literal);
            // Every non-ASCII byte is percent-encoded, so the wire token is pure ASCII.
            foreach (char c in encoded)
                Assert.True(c < 0x80, $"encoded token must be pure ASCII, saw U+{(int)c:X4}");
            Assert.True(TestCommandProtocol.TryDecode(encoded, out string back));
            Assert.Equal(literal, back);
        }

        [Theory]
        [InlineData("id=0001", "id", "0001")]
        [InlineData("name=mun%20landing", "name", "mun%20landing")]
        [InlineData("k=a=b", "k", "a=b")] // split at FIRST '=', value keeps the rest
        public void TrySplitToken_SplitsAtFirstEquals(string token, string key, string rawValue)
        {
            Assert.True(TestCommandProtocol.TrySplitToken(token, out string k, out string v));
            Assert.Equal(key, k);
            Assert.Equal(rawValue, v);
        }

        [Theory]
        [InlineData("bareTokenNoEquals")]
        [InlineData("=leadingEqualsEmptyKey")]
        [InlineData("")]
        public void TrySplitToken_Malformed_Fails(string token)
        {
            Assert.False(TestCommandProtocol.TrySplitToken(token, out _, out _));
        }
    }
}
