using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Bug #422: the per-tree sidecar-hydration roll-up WARN duplicates the per-recording
    /// INFO lines ("Trajectory file missing for ... — recording degraded (0 points)") for
    /// injected synthetic fixtures on a fresh test save. The roll-up must be suppressed
    /// (downgraded to INFO) when every failure is the synthetic-fixture marker
    /// (trajectory-missing + zero points), and must still fire WARN when any genuine
    /// degradation is present in the batch.
    /// </summary>
    [Collection("Sequential")]
    public class Bug422SidecarHydrationRollupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug422SidecarHydrationRollupTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // --- IsSyntheticFixtureSidecarMarker ---

        [Fact]
        public void IsSyntheticFixtureSidecarMarker_TrajectoryMissingWithZeroPoints_ReturnsTrue()
        {
            var rec = new Recording();
            RecordingStore.MarkSidecarLoadFailure(rec, "trajectory-missing");

            Assert.True(ParsekScenario.IsSyntheticFixtureSidecarMarker(rec));
        }

        [Fact]
        public void IsSyntheticFixtureSidecarMarker_TrajectoryMissingWithPoints_ReturnsFalse()
        {
            // A recording that had trajectory points before the sidecar was (mysteriously)
            // deleted is a genuine degradation, not a synthetic-fixture marker.
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint());
            RecordingStore.MarkSidecarLoadFailure(rec, "trajectory-missing");

            Assert.False(ParsekScenario.IsSyntheticFixtureSidecarMarker(rec));
        }

        [Fact]
        public void IsSyntheticFixtureSidecarMarker_OtherFailureReasons_ReturnFalse()
        {
            string[] genuineReasons =
            {
                "trajectory-invalid",
                "trajectory-unsupported",
                "trajectory-id-mismatch",
                "stale-sidecar-epoch",
                "snapshot-vessel-missing",
                "exception:IOException",
            };

            foreach (var reason in genuineReasons)
            {
                var rec = new Recording();
                RecordingStore.MarkSidecarLoadFailure(rec, reason);
                Assert.False(
                    ParsekScenario.IsSyntheticFixtureSidecarMarker(rec),
                    $"reason '{reason}' must not be treated as synthetic-fixture marker");
            }
        }

        [Fact]
        public void IsSyntheticFixtureSidecarMarker_NoFailure_ReturnsFalse()
        {
            var rec = new Recording();
            Assert.False(ParsekScenario.IsSyntheticFixtureSidecarMarker(rec));
        }

        [Fact]
        public void IsSyntheticFixtureSidecarMarker_NullRecording_ReturnsFalse()
        {
            Assert.False(ParsekScenario.IsSyntheticFixtureSidecarMarker(null));
        }

        // --- EmitSidecarHydrationRollup ---

        [Fact]
        public void EmitRollup_AllSyntheticFixture_SuppressesWarnAndLogsInfo()
        {
            // Ten failures, all synthetic-fixture — clean test save. The WARN must be
            // suppressed (bug #422), the INFO replacement must mention "synthetic".
            ParsekScenario.EmitSidecarHydrationRollup("TestTree", totalFailures: 10, syntheticFixtureFailures: 10);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Scenario]") &&
                l.Contains("recording(s) with sidecar hydration failures"));

            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") && l.Contains("[Scenario]") &&
                l.Contains("TestTree") && l.Contains("synthetic-fixture") &&
                l.Contains("10"));
        }

        [Fact]
        public void EmitRollup_MixedBatch_EmitsWarnWithBreakdown()
        {
            // One genuine degradation mixed into nine synthetic-fixture markers — WARN
            // must still fire because a genuine issue is present.
            ParsekScenario.EmitSidecarHydrationRollup("MixedTree", totalFailures: 10, syntheticFixtureFailures: 9);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Scenario]") &&
                l.Contains("MixedTree") &&
                l.Contains("recording(s) with sidecar hydration failures") &&
                l.Contains("1 genuine") && l.Contains("9 synthetic-fixture"));
        }

        [Fact]
        public void EmitRollup_AllGenuine_EmitsWarnWithZeroSynthetic()
        {
            // No synthetic markers — pure genuine degradation batch. WARN must fire and
            // the breakdown must say "0 synthetic-fixture".
            ParsekScenario.EmitSidecarHydrationRollup("GenuineTree", totalFailures: 3, syntheticFixtureFailures: 0);

            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Scenario]") &&
                l.Contains("GenuineTree") &&
                l.Contains("3 genuine") && l.Contains("0 synthetic-fixture"));
        }

        [Fact]
        public void EmitRollup_NoFailures_EmitsNothing()
        {
            ParsekScenario.EmitSidecarHydrationRollup("CleanTree", totalFailures: 0, syntheticFixtureFailures: 0);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("CleanTree"));
        }

        [Fact]
        public void EmitRollup_SingleSyntheticFixture_SuppressesWarn()
        {
            // Defensive: the N==1 case (a single-recording synthetic tree) must also be
            // suppressed, not just N==10.
            ParsekScenario.EmitSidecarHydrationRollup("SoloTree", totalFailures: 1, syntheticFixtureFailures: 1);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Scenario]"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") && l.Contains("[Scenario]") &&
                l.Contains("SoloTree") && l.Contains("synthetic-fixture"));
        }
    }
}
