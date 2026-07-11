using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the command-line envelope parser
    /// (<see cref="TestCommandParser.ParseLine"/>). Guards the grammar: id first,
    /// cmd second, args any order, unknown keys ignored, blank/# skipped, and each
    /// malformed shape returning the correct reason. A regression here would accept
    /// a malformed line and later execute it as a real command, or drop args.
    /// </summary>
    public class TestCommandParserTests
    {
        [Fact]
        public void Valid_Line_PopulatesEnvelopeAndArgs()
        {
            var p = TestCommandParser.ParseLine("id=0001 cmd=SetSetting name=x value=false", 1);
            Assert.True(p.ParseOk);
            Assert.False(p.Ignored);
            Assert.Equal("0001", p.Id);
            Assert.Equal("SetSetting", p.Verb);
            Assert.Equal("x", p.Args["name"]);
            Assert.Equal("false", p.Args["value"]);
        }

        [Fact]
        public void Valid_Line_DecodesPercentEncodedArg()
        {
            var p = TestCommandParser.ParseLine("id=7 cmd=MissionMark label=mun%20landing%20start", 1);
            Assert.True(p.ParseOk);
            Assert.Equal("mun landing start", p.Args["label"]);
        }

        [Fact]
        public void Args_AnyOrder_AfterIdAndCmd()
        {
            var p = TestCommandParser.ParseLine("id=2 cmd=LoadGame name=persistent save=DefaultCareer", 1);
            Assert.True(p.ParseOk);
            Assert.Equal("DefaultCareer", p.Args["save"]);
            Assert.Equal("persistent", p.Args["name"]);
        }

        [Fact]
        public void UnknownKeys_AreKeptNotRejected()
        {
            // v= and any future phase-3 key are tolerated (forward compatibility).
            var p = TestCommandParser.ParseLine("id=3 cmd=RecordingState v=1 futureKey=abc", 1);
            Assert.True(p.ParseOk);
            Assert.Equal("1", p.Args["v"]);
            Assert.Equal("abc", p.Args["futureKey"]);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void BlankLine_IsIgnored(string line)
        {
            var p = TestCommandParser.ParseLine(line, 5);
            Assert.True(p.Ignored);
            Assert.False(p.ParseOk);
        }

        [Fact]
        public void CommentLine_IsIgnored()
        {
            var p = TestCommandParser.ParseLine("# this is a comment", 5);
            Assert.True(p.Ignored);
            Assert.False(p.ParseOk);
        }

        [Fact]
        public void MissingId_FirstTokenNotId_ReturnsMissingId()
        {
            var p = TestCommandParser.ParseLine("cmd=StartRecording name=x", 4);
            Assert.False(p.ParseOk);
            Assert.False(p.Ignored);
            Assert.Equal("missing-id", p.ParseError);
            Assert.Null(p.Id);
            Assert.Equal("line#4", TestCommandParser.FallbackId(p.LineNumber));
        }

        [Fact]
        public void MissingCmd_SecondTokenNotCmd_ReturnsMissingCmdUnderRealId()
        {
            var p = TestCommandParser.ParseLine("id=0009 name=x", 1);
            Assert.False(p.ParseOk);
            Assert.Equal("missing-cmd", p.ParseError);
            Assert.Equal("0009", p.Id); // id salvaged for correlation
        }

        [Fact]
        public void BareToken_NoEquals_IsMalformed()
        {
            var p = TestCommandParser.ParseLine("id=1 cmd=MissionMark garbage", 1);
            Assert.False(p.ParseOk);
            Assert.Equal("malformed", p.ParseError);
            Assert.Equal("1", p.Id); // id still salvaged
        }

        [Fact]
        public void ValueWithRawSpace_SplitsIntoBareToken_IsMalformed()
        {
            // An un-encoded space splits "mun landing" into a second bare token.
            var p = TestCommandParser.ParseLine("id=1 cmd=MissionMark label=mun landing", 1);
            Assert.False(p.ParseOk);
            Assert.Equal("malformed", p.ParseError);
        }

        [Fact]
        public void GarbageFirstToken_NoUsableId_FallsBackToLineNumber()
        {
            var p = TestCommandParser.ParseLine("garbage", 12);
            Assert.False(p.ParseOk);
            Assert.Equal("malformed", p.ParseError);
            Assert.Null(p.Id);
            Assert.Equal("line#12", TestCommandParser.FallbackId(p.LineNumber));
        }

        [Fact]
        public void TrailingNewline_IsStripped()
        {
            var p = TestCommandParser.ParseLine("id=1 cmd=StopRecording\n", 1);
            Assert.True(p.ParseOk);
            Assert.Equal("StopRecording", p.Verb);
        }
    }
}
