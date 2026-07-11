using System.Collections.Generic;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P5.3 coverage for the MissionMark stable log line. The orchestrator greps
    /// <c>MISSIONMARK label=&lt;label&gt; ut=&lt;ut&gt;</c> to correlate its own timeline
    /// against the KSP session, so the message shape is a load-bearing contract. Both
    /// the pure format and the actual ParsekLog.Info emission (tag + level) are asserted;
    /// fails if the marker format drifts or the tag/level changes.
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandMissionMarkTests
    {
        public TestCommandMissionMarkTests() => ParsekLog.ResetTestOverrides();

        [Fact]
        public void FormatMarkMessage_WithUt_IsStable()
        {
            Assert.Equal("MISSIONMARK label=mun landing start ut=1234.5",
                TestCommandMissionMark.FormatMarkMessage("mun landing start", 1234.5));
        }

        [Fact]
        public void FormatMarkMessage_NoGame_RendersUtNone()
        {
            Assert.Equal("MISSIONMARK label=x ut=none",
                TestCommandMissionMark.FormatMarkMessage("x", null));
        }

        [Fact]
        public void FormatMarkMessage_NullLabel_EmptyLabel()
        {
            Assert.Equal("MISSIONMARK label= ut=none",
                TestCommandMissionMark.FormatMarkMessage(null, null));
        }

        [Fact]
        public void EmitMark_LogsTaggedInfoLine()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = l => lines.Add(l);
            try
            {
                TestCommandMissionMark.EmitMark("mun landing start", 1234.5);
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }

            Assert.Contains(lines, l =>
                l.Contains("[INFO]") && l.Contains("[TestCommands]")
                && l.Contains("MISSIONMARK label=mun landing start ut=1234.5"));
        }
    }
}
