using System.Linq;
using Parsek.Tests.LogValidation;
using Xunit;

namespace Parsek.Tests
{
    public class ParsekKspLogParserTests
    {
        [Fact]
        public void ParseFile_ParsesStructuredEntriesFromNoisyFixture()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("good_latest_session.log"));

            Assert.NotEmpty(entries);
            Assert.All(entries, e => Assert.True(e.LineNumber > 0));
            Assert.Contains(entries, e =>
                e.IsStructured &&
                e.Level == "INFO" &&
                e.Subsystem == "Init" &&
                e.Message.StartsWith("SessionStart runUtc=", System.StringComparison.Ordinal));
        }

        [Fact]
        public void ParseFile_MarksMalformedParsekLines()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("bad_malformed_parsek_line.log"));

            Assert.Contains(entries, e => !e.IsStructured);
        }

        [Fact]
        public void SelectLatestSession_UsesLastSessionStartMarker()
        {
            var entries = ParsekKspLogParser.ParseFile(TestFixtureLoader.GetFixturePath("multi_session_uses_last_marker.log"));
            var latestSession = ParsekKspLogParser.SelectLatestSession(entries);

            Assert.NotEmpty(latestSession);
            Assert.True(latestSession[0].IsStructured);
            Assert.Equal("Init", latestSession[0].Subsystem);
            Assert.Equal("SessionStart runUtc=2000", latestSession[0].Message);
            Assert.DoesNotContain(latestSession, e =>
                e.IsStructured &&
                e.Level == "ERROR" &&
                (e.Message ?? string.Empty).IndexOf("Old failure", System.StringComparison.Ordinal) >= 0);
            Assert.True(latestSession.Count < entries.Count);
            Assert.Equal(2, entries.Count(e => e.IsStructured && e.Subsystem == "Init"));
        }
    }
}
