using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ParsekLogTests
    {
        public ParsekLogTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
        }

        [Fact]
        public void Info_UsesStructuredFormat()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);

            ParsekLog.Info("UnitTest", "hello");

            Assert.Single(lines);
            Assert.Equal("[Parsek][INFO][UnitTest] hello", lines[0]);
        }

        [Fact]
        public void Verbose_SuppressedWhenVerboseDisabled()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = false;

            ParsekLog.Verbose("UnitTest", "detail");
            ParsekLog.VerboseRateLimited("UnitTest", "key", "detail", 2.0);
            ParsekLog.Info("UnitTest", "always");

            Assert.Single(lines);
            Assert.Equal("[Parsek][INFO][UnitTest] always", lines[0]);
        }

        [Fact]
        public void VerboseRateLimited_EmitsSuppressedCountAfterInterval()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            ParsekLog.VerboseRateLimited("UnitTest", "sample", "tick", 2.0);
            now = 1000.5;
            ParsekLog.VerboseRateLimited("UnitTest", "sample", "tick", 2.0);
            now = 1001.0;
            ParsekLog.VerboseRateLimited("UnitTest", "sample", "tick", 2.0);
            now = 1002.1;
            ParsekLog.VerboseRateLimited("UnitTest", "sample", "tick", 2.0);

            Assert.Equal(2, lines.Count);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] tick", lines[0]);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] tick | suppressed=2", lines[1]);
        }
    }
}
