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
        public void Warn_UsesStructuredFormat()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);

            ParsekLog.Warn("UnitTest", "careful");

            Assert.Single(lines);
            Assert.Equal("[Parsek][WARN][UnitTest] careful", lines[0]);
        }

        [Fact]
        public void TestObserverForTesting_ReceivesStructuredFormat()
        {
            var lines = new List<string>();
            ParsekLog.TestObserverForTesting = line => lines.Add(line);

            ParsekLog.Info("UnitTest", "hello");

            Assert.Single(lines);
            Assert.Equal("[Parsek][INFO][UnitTest] hello", lines[0]);
        }

        [Fact]
        public void TestObserverForTesting_RunsAlongsideTestSink()
        {
            var observed = new List<string>();
            var sunk = new List<string>();
            ParsekLog.TestObserverForTesting = line => observed.Add(line);
            ParsekLog.TestSinkForTesting = line => sunk.Add(line);

            ParsekLog.Info("UnitTest", "hello");

            Assert.Single(observed);
            Assert.Single(sunk);
            Assert.Equal("[Parsek][INFO][UnitTest] hello", observed[0]);
            Assert.Equal("[Parsek][INFO][UnitTest] hello", sunk[0]);
        }

        [Fact]
        public void Info_NullInputs_UseSafeFallbacks()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);

            ParsekLog.Info(null, null);

            Assert.Single(lines);
            Assert.Equal("[Parsek][INFO][General] (empty)", lines[0]);
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

        [Fact]
        public void ResetRateLimitsForTesting_AllowsImmediateReemit()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;

            double now = 1000.0;
            ParsekLog.ClockOverrideForTesting = () => now;

            ParsekLog.VerboseRateLimited("UnitTest", "sample", "tick", 2.0);
            now = 1000.5;
            ParsekLog.VerboseRateLimited("UnitTest", "sample", "tick", 2.0);

            ParsekLog.ResetRateLimitsForTesting();
            ParsekLog.VerboseRateLimited("UnitTest", "sample", "tick", 2.0);

            Assert.Equal(2, lines.Count);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] tick", lines[0]);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] tick", lines[1]);
        }

        [Fact]
        public void ResetTestOverrides_ClearsSuppressLogging()
        {
            ParsekLog.SuppressLogging = true;
            ParsekLog.TestObserverForTesting = _ => { };

            ParsekLog.ResetTestOverrides();

            Assert.False(ParsekLog.SuppressLogging);
            Assert.Null(ParsekLog.TestObserverForTesting);
        }

        [Fact]
        public void VerboseOnChange_FirstCallEmits()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "decision A");

            Assert.Single(lines);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] decision A", lines[0]);
        }

        [Fact]
        public void VerboseOnChange_StableKey_SuppressesRepeats()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "decision A1");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "decision A2");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "decision A3");

            Assert.Single(lines);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] decision A1", lines[0]);
        }

        [Fact]
        public void VerboseOnChange_KeyFlip_ReemitsWithSuppressedCount()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "stable A");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "stable A");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "stable A");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "Segment|ok", "flipped to segment");

            Assert.Equal(2, lines.Count);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] stable A", lines[0]);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] flipped to segment | suppressed=2", lines[1]);
        }

        [Fact]
        public void VerboseOnChange_DifferentIdentities_TrackedIndependently()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "A first");
            ParsekLog.VerboseOnChange("UnitTest", "rec-B", "None|threshold", "B first");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "None|threshold", "A repeat");
            ParsekLog.VerboseOnChange("UnitTest", "rec-B", "None|threshold", "B repeat");

            Assert.Equal(2, lines.Count);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] A first", lines[0]);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] B first", lines[1]);
        }

        [Fact]
        public void VerboseOnChange_SuppressedCountResetsAfterEmission()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "K1", "first");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "K1", "first");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "K1", "first");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "K2", "second");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "K2", "second");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "K1", "back to first");

            Assert.Equal(3, lines.Count);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] first", lines[0]);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] second | suppressed=2", lines[1]);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] back to first | suppressed=1", lines[2]);
        }

        [Fact]
        public void VerboseOnChange_SuppressedWhenVerboseDisabled()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = false;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "K1", "would emit");

            Assert.Empty(lines);
        }

        [Fact]
        public void VerboseOnChange_NullStateKeyTreatedAsEmpty()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", "rec-A", null, "first");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", null, "second");
            ParsekLog.VerboseOnChange("UnitTest", "rec-A", "", "third");

            Assert.Single(lines);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] first", lines[0]);
        }

        [Fact]
        public void VerboseOnChange_EmptyIdentityFallsBackToVerbose()
        {
            var lines = new List<string>();
            ParsekLog.TestSinkForTesting = line => lines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ResetRateLimitsForTesting();

            ParsekLog.VerboseOnChange("UnitTest", null, "K1", "always");
            ParsekLog.VerboseOnChange("UnitTest", "", "K1", "always");

            Assert.Equal(2, lines.Count);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] always", lines[0]);
            Assert.Equal("[Parsek][VERBOSE][UnitTest] always", lines[1]);
        }
    }
}
