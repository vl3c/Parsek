using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for two bugs exposed by the 2026-04-09 playtest after
    /// PR #163 fixed the OnFlightReady ordering bug:
    ///
    ///   1. FilesDirty was not set in PromoteToTreeForBreakup / CreateSplitBranch
    ///      / AppendCapturedDataToRecording / FlushRecorderToTreeRecording /
    ///      ChainSegmentManager continuation sampling. Trajectory data sat in
    ///      memory and never reached the .prec sidecar file because SaveRecordingFiles
    ///      guards on FilesDirty. On scene reload, TryRestoreActiveTreeNode reads
    ///      the empty .prec and produces a 0-point recording. The user lost an 88+
    ///      point Kerbal X launch recording this way.
    ///
    ///   2. vesselSwitchPending flag leaked across unrelated scene changes. EVA
    ///      fires onVesselSwitching without triggering a scene reload, so the
    ///      raw flag stayed true for thousands of frames until the next quickload
    ///      consumed it — at which point the stale flag mis-identified the
    ///      quickload as a tracking-station vessel switch and routed the pending
    ///      tree into FinalizeLimboForRevert instead of the restore-and-resume
    ///      path. Fix adds a frame-count staleness check.
    /// </summary>
    [Collection("Sequential")]
    public class PlaytestFollowupTests : IDisposable
    {
        public PlaytestFollowupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ----- FilesDirty: AppendCapturedDataToRecording -----

        [Fact]
        public void AppendCapturedDataToRecording_SourceNonNull_MarksTargetDirty()
        {
            var target = new Recording
            {
                RecordingId = "target",
                VesselName = "V",
                FilesDirty = false,
            };
            var source = new Recording { RecordingId = "src" };
            source.Points.Add(new TrajectoryPoint { ut = 10.0 });
            source.Points.Add(new TrajectoryPoint { ut = 11.0 });

            ParsekFlight.AppendCapturedDataToRecording(target, source, 11.5);

            Assert.True(target.FilesDirty,
                "AppendCapturedDataToRecording must mark the target dirty so the " +
                "appended points reach disk on the next OnSave. Without this, in-flight " +
                "tree recordings that receive data via this path are never persisted, " +
                "and their in-memory state is lost on scene reload " +
                "(2026-04-09 playtest data-loss bug — see CHANGELOG).");
            Assert.Equal(2, target.Points.Count);
            Assert.Equal(11.5, target.ExplicitEndUT);
        }

        [Fact]
        public void AppendCapturedDataToRecording_NullSource_DoesNotMarkDirty()
        {
            var target = new Recording
            {
                RecordingId = "target",
                VesselName = "V",
                FilesDirty = false,
            };

            ParsekFlight.AppendCapturedDataToRecording(target, source: null, endUT: 20.0);

            // Null source: no data changed, so no need to mark dirty.
            // (The `ExplicitEndUT = endUT;` assignment still fires, but that's
            // metadata that lives on the RECORDING ConfigNode in the .sfs, not
            // the .prec sidecar — no dirty tracking needed.)
            Assert.False(target.FilesDirty);
            Assert.Equal(20.0, target.ExplicitEndUT);
        }

        [Fact]
        public void AppendCapturedDataToRecording_PreservesExistingPoints()
        {
            var target = new Recording { RecordingId = "target" };
            target.Points.Add(new TrajectoryPoint { ut = 1.0 });
            target.Points.Add(new TrajectoryPoint { ut = 2.0 });
            target.FilesDirty = false;

            var source = new Recording { RecordingId = "src" };
            source.Points.Add(new TrajectoryPoint { ut = 3.0 });

            ParsekFlight.AppendCapturedDataToRecording(target, source, 3.5);

            Assert.Equal(3, target.Points.Count);
            Assert.Equal(1.0, target.Points[0].ut);
            Assert.Equal(2.0, target.Points[1].ut);
            Assert.Equal(3.0, target.Points[2].ut);
            Assert.True(target.FilesDirty);
        }

        // ----- vesselSwitchPending: IsVesselSwitchFlagFresh -----

        [Fact]
        public void IsVesselSwitchFlagFresh_FlagNotSet_ReturnsFalse()
        {
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: false, pendingFrame: 100, currentFrame: 105, maxAgeFrames: 300);
            Assert.False(result);
        }

        [Fact]
        public void IsVesselSwitchFlagFresh_FlagSetButNeverStamped_ReturnsFalse()
        {
            // pendingFrame == -1 is the sentinel for "not yet stamped"
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true, pendingFrame: -1, currentFrame: 100, maxAgeFrames: 300);
            Assert.False(result);
        }

        [Fact]
        public void IsVesselSwitchFlagFresh_SameFrame_ReturnsTrue()
        {
            // onVesselSwitching fired in frame 500, OnLoad reads on frame 500
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true, pendingFrame: 500, currentFrame: 500, maxAgeFrames: 300);
            Assert.True(result);
        }

        [Fact]
        public void IsVesselSwitchFlagFresh_WithinMaxAge_ReturnsTrue()
        {
            // Typical tracking-station switch: 1-2 frames between event and OnLoad
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true, pendingFrame: 500, currentFrame: 502, maxAgeFrames: 300);
            Assert.True(result);

            // Upper bound of the fresh window
            bool atLimit = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true, pendingFrame: 500, currentFrame: 800, maxAgeFrames: 300);
            Assert.True(atLimit);
        }

        [Fact]
        public void IsVesselSwitchFlagFresh_JustPastMaxAge_ReturnsFalse()
        {
            // One frame past the limit — stale
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true, pendingFrame: 500, currentFrame: 801, maxAgeFrames: 300);
            Assert.False(result);
        }

        [Fact]
        public void IsVesselSwitchFlagFresh_EvaLeakageScenario_ReturnsFalse()
        {
            // The actual 2026-04-09 playtest sequence:
            //   - EVA fires onVesselSwitching at frame 5000
            //   - Player records for minutes (thousands of frames) without a scene reload
            //   - F9 at frame 15000 triggers OnLoad
            //   - Flag is ~10000 frames old — clearly stale
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true, pendingFrame: 5000, currentFrame: 15000, maxAgeFrames: 300);
            Assert.False(result);
        }

        [Fact]
        public void IsVesselSwitchFlagFresh_NegativeAge_ReturnsFalse()
        {
            // Defensive: frame count went backwards (shouldn't happen in practice
            // since Time.frameCount is monotonic, but guard against impossible states)
            bool result = ParsekScenario.IsVesselSwitchFlagFresh(
                pending: true, pendingFrame: 1000, currentFrame: 500, maxAgeFrames: 300);
            Assert.False(result);
        }

        [Fact]
        public void IsVesselSwitchFlagFresh_MaxAgeConstantValue()
        {
            // Lock in the max-age cap so a reviewer changing it notices the test
            // that documents the rationale (tracking-station reload completes well
            // under 6 seconds at 50 FPS, EVA leakage is typically minutes = 10000+ frames)
            Assert.Equal(300, ParsekScenario.VesselSwitchPendingMaxAgeFrames);
        }
    }
}
