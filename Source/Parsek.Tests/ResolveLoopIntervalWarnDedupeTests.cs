using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for the per-frame log-spam bug in
    /// GhostPlaybackLogic.ResolveLoopInterval. Before the dedupe fix, degenerate
    /// loop periods (period &lt; MinCycleDuration) emitted an unrate-limited
    /// ParsekLog.Warn per frame per recording, producing ~1.3M lines in a
    /// 6-minute session with ~20 offending recordings. Each offender must warn
    /// at most once per session.
    /// </summary>
    [Collection("Sequential")]
    public class ResolveLoopIntervalWarnDedupeTests : IDisposable
    {
        private const double DefaultInterval = 10.0;
        private const double MinCycleDuration = LoopTiming.MinCycleDuration;

        private readonly List<string> logLines = new List<string>();

        public ResolveLoopIntervalWarnDedupeTests()
        {
            GhostPlaybackLogic.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetForTesting();
        }

        private static int CountClampWarnings(List<string> lines, string vesselName)
        {
            return lines.Count(l =>
                l.Contains("[WARN]") &&
                l.Contains("[Loop]") &&
                l.Contains("ResolveLoopInterval:") &&
                l.Contains($"'{vesselName}'"));
        }

        [Fact]
        public void DegenerateInterval_HundredCalls_SameRecording_EmitsExactlyOneWarning()
        {
            var rec = new MockTrajectory
            {
                RecordingId = "rec-legacy-zero",
                VesselName = "LegacyZero",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 0.0,
            };

            for (int i = 0; i < 100; i++)
            {
                double result = GhostPlaybackLogic.ResolveLoopInterval(
                    rec, globalAutoInterval: DefaultInterval,
                    defaultInterval: DefaultInterval, minCycleDuration: MinCycleDuration);

                // Defensive clamp behavior is unchanged: every call still returns the floor.
                Assert.Equal(MinCycleDuration, result);
            }

            Assert.Equal(1, CountClampWarnings(logLines, "LegacyZero"));
        }

        [Fact]
        public void DegenerateInterval_HundredCallsEach_TwoRecordings_EmitsExactlyTwoWarnings()
        {
            var recA = new MockTrajectory
            {
                RecordingId = "rec-alpha",
                VesselName = "Alpha",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 0.0,
            };
            var recB = new MockTrajectory
            {
                RecordingId = "rec-beta",
                VesselName = "Beta",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 0.0,
            };

            for (int i = 0; i < 100; i++)
            {
                double a = GhostPlaybackLogic.ResolveLoopInterval(
                    recA, DefaultInterval, DefaultInterval, MinCycleDuration);
                double b = GhostPlaybackLogic.ResolveLoopInterval(
                    recB, DefaultInterval, DefaultInterval, MinCycleDuration);
                Assert.Equal(MinCycleDuration, a);
                Assert.Equal(MinCycleDuration, b);
            }

            // Each recording logs its clamp exactly once — 200 calls produce 2 lines.
            Assert.Equal(1, CountClampWarnings(logLines, "Alpha"));
            Assert.Equal(1, CountClampWarnings(logLines, "Beta"));
        }

        [Fact]
        public void DegenerateInterval_DedupesOnRecordingId_EvenWhenVesselNamesCollide()
        {
            // Two recordings of the same vessel (e.g. two reverts of "Pad Walk") must each
            // get their own warning: the stable key is RecordingId, not VesselName.
            var recA = new MockTrajectory
            {
                RecordingId = "rec-a",
                VesselName = "PadWalk",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 0.0,
            };
            var recB = new MockTrajectory
            {
                RecordingId = "rec-b",
                VesselName = "PadWalk",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 0.0,
            };

            GhostPlaybackLogic.ResolveLoopInterval(recA, DefaultInterval, DefaultInterval, MinCycleDuration);
            GhostPlaybackLogic.ResolveLoopInterval(recA, DefaultInterval, DefaultInterval, MinCycleDuration);
            GhostPlaybackLogic.ResolveLoopInterval(recB, DefaultInterval, DefaultInterval, MinCycleDuration);
            GhostPlaybackLogic.ResolveLoopInterval(recB, DefaultInterval, DefaultInterval, MinCycleDuration);

            // Two distinct RecordingIds → two warnings, both tagged with the shared VesselName.
            Assert.Equal(2, CountClampWarnings(logLines, "PadWalk"));
        }

        [Fact]
        public void DegenerateInterval_FallsBackToVesselName_WhenRecordingIdMissing()
        {
            // Fixtures without an id (e.g. transient non-Recording fixtures) must still dedupe.
            var rec = new MockTrajectory
            {
                RecordingId = null,
                VesselName = "Nameless",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 0.0,
            };

            for (int i = 0; i < 50; i++)
            {
                GhostPlaybackLogic.ResolveLoopInterval(
                    rec, DefaultInterval, DefaultInterval, MinCycleDuration);
            }

            Assert.Equal(1, CountClampWarnings(logLines, "Nameless"));
        }

        [Fact]
        public void HealthyInterval_DoesNotWarn_AndDoesNotConsumeDedupeSlot()
        {
            var rec = new MockTrajectory
            {
                VesselName = "Healthy",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 30.0,
            };

            double resolved = GhostPlaybackLogic.ResolveLoopInterval(
                rec, DefaultInterval, DefaultInterval, MinCycleDuration);

            Assert.Equal(30.0, resolved);
            Assert.Equal(0, CountClampWarnings(logLines, "Healthy"));
        }

        [Fact]
        public void ResetForTesting_AllowsWarningToFireAgain()
        {
            var rec = new MockTrajectory
            {
                RecordingId = "rec-legacy-zero",
                VesselName = "LegacyZero",
                LoopTimeUnit = LoopTimeUnit.Sec,
                LoopIntervalSeconds = 0.0,
            };

            GhostPlaybackLogic.ResolveLoopInterval(
                rec, DefaultInterval, DefaultInterval, MinCycleDuration);
            Assert.Equal(1, CountClampWarnings(logLines, "LegacyZero"));

            // Simulating a new session — reset flushes the dedupe set.
            GhostPlaybackLogic.ResetForTesting();
            GhostPlaybackLogic.ResolveLoopInterval(
                rec, DefaultInterval, DefaultInterval, MinCycleDuration);

            // Now two warnings total — one from before reset, one after.
            Assert.Equal(2, CountClampWarnings(logLines, "LegacyZero"));
        }
    }
}
