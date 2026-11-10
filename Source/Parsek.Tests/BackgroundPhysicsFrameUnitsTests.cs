using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the units extracted from
    /// <see cref="BackgroundRecorder.OnBackgroundPhysicsFrame(Vessel)"/>: the proximity interval
    /// bookkeeping, the fixed-step skip note, the effective sampling floor / ceiling and the two
    /// sample triggers.
    /// </summary>
    [Collection("Sequential")]
    public class BackgroundPhysicsFrameUnitsTests : IDisposable
    {
        private const uint Pid = 5201u;
        private const string RecId = "bg-units-rec";
        private readonly List<string> logLines = new List<string>();

        public BackgroundPhysicsFrameUnitsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.ClockOverrideForTesting = () => 100.0;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static BackgroundRecorder Recorder(Recording rec = null)
        {
            var tree = new RecordingTree { Id = "bg-units-tree" };
            tree.Recordings[RecId] = rec ?? new Recording { RecordingId = RecId, VesselPersistentId = Pid };
            var bg = new BackgroundRecorder(tree);
            bg.InjectLoadedStateForTesting(Pid, RecId);
            return bg;
        }

        // ---------------- UpdateBackgroundSampleInterval

        [Fact]
        public void SampleInterval_Change_LogsAndStores()
        {
            BackgroundRecorder bg = Recorder();
            bg.UpdateBackgroundSampleIntervalForTesting(Pid, 100.4, 0.2, 0.2);

            Assert.Equal(0.2, bg.GetCurrentSampleIntervalForTesting(Pid));
            string line = Assert.Single(logLines);
            Assert.Equal("[Parsek][INFO][BgRecorder] Sample rate changed: pid=5201 dist=100m interval=0.2s (5.0 Hz)", line);
        }

        [Fact]
        public void SampleInterval_Unchanged_NoLine()
        {
            BackgroundRecorder bg = Recorder();
            bg.UpdateBackgroundSampleIntervalForTesting(Pid, 100.0, 0.2, 0.2);
            logLines.Clear();

            bg.UpdateBackgroundSampleIntervalForTesting(Pid, 120.0, 0.2, 0.2);

            Assert.Empty(logLines);
            Assert.Equal(0.2, bg.GetCurrentSampleIntervalForTesting(Pid));
        }

        [Fact]
        public void SampleInterval_DebrisCapMovedTheTier_NamesTheRawTier()
        {
            BackgroundRecorder bg = Recorder();
            bg.UpdateBackgroundSampleIntervalForTesting(Pid, 1500.0, 2.0, 0.5);

            Assert.Equal(0.5, bg.GetCurrentSampleIntervalForTesting(Pid));
            Assert.Equal(
                "[Parsek][INFO][BgRecorder] Sample rate changed: pid=5201 dist=1500m interval=0.5s (2.0 Hz) " +
                "debris-cap=applied (rawTier=2.0s)",
                Assert.Single(logLines));
        }

        [Fact]
        public void SampleInterval_OutOfRange_ZeroHz()
        {
            BackgroundRecorder bg = Recorder();
            bg.UpdateBackgroundSampleIntervalForTesting(Pid, 100.0, 0.2, 0.2);
            logLines.Clear();

            bg.UpdateBackgroundSampleIntervalForTesting(Pid, 1.0e7, ProximityRateSelector.OutOfRangeInterval,
                ProximityRateSelector.OutOfRangeInterval);

            Assert.Equal(ProximityRateSelector.OutOfRangeInterval, bg.GetCurrentSampleIntervalForTesting(Pid));
            Assert.Equal(
                "[Parsek][INFO][BgRecorder] Sample rate changed: pid=5201 dist=10000000m interval=none (0.0 Hz)",
                Assert.Single(logLines));
        }

        // ---------------- NoteBackgroundFixedStepSampleSkip

        [Fact]
        public void FixedStepSkip_CountsEveryCall_LogIsRateLimited()
        {
            BackgroundRecorder bg = Recorder();
            bg.NoteBackgroundFixedStepSampleSkip(Pid, 12.34567);
            bg.NoteBackgroundFixedStepSampleSkip(Pid, 12.5);

            Assert.Equal(2, bg.FixedStepSampleSkipCountForTesting);
            Assert.Equal(
                "[Parsek][VERBOSE][BgRecorder] Fixed-step physics callback: background sample deferred to the " +
                "Update step pid=5201 ut=12.3457 skippedCallbacks=1",
                Assert.Single(logLines));
        }

        // ---------------- ResolveBackgroundEffectiveSampleIntervals

        [Fact]
        public void EffectiveIntervals_NoDebrisNoHighFidelity_ProximityIsTheFloor()
        {
            BackgroundRecorder.ResolveBackgroundEffectiveSampleIntervals(
                ProximitySamplingTier.None, false, 2.0, 0.2f, 3.0f, new Recording(),
                out float min, out float max);

            Assert.Equal(2.0f, min);
            Assert.Equal(
                FlightRecorder.ResolveEffectiveMaxSampleInterval(ProximitySamplingTier.None,
                    ProximitySamplingTier.None, false, 3.0f, 0.2f),
                max);
        }

        [Fact]
        public void EffectiveIntervals_HighFidelity_ConfiguredForegroundFloor()
        {
            BackgroundRecorder.ResolveBackgroundEffectiveSampleIntervals(
                ProximitySamplingTier.None, true, 2.0, 0.2f, 3.0f, new Recording(),
                out float min, out float max);

            Assert.Equal(FlightRecorder.ResolveEffectiveMinSampleInterval(true, 0.2f), min);
            Assert.Equal(
                FlightRecorder.ResolveEffectiveMaxSampleInterval(ProximitySamplingTier.None,
                    ProximitySamplingTier.None, true, 3.0f, 0.2f),
                max);
        }

        [Fact]
        public void EffectiveIntervals_DebrisTier_DebrisCadenceAndNoDebrisAwareCap()
        {
            var debris = new Recording { IsDebris = true, ParentAnchorRecordingId = "parent" };
            BackgroundRecorder.ResolveBackgroundEffectiveSampleIntervals(
                ProximitySamplingTier.Half, false, 2.0, 0.2f, 3.0f, debris,
                out float min, out float max);

            Assert.Equal(FlightRecorder.ResolveEffectiveMinSampleInterval(ProximitySamplingTier.None,
                ProximitySamplingTier.Half, false, 0.2f, 3.0f), min);
            Assert.Equal(FlightRecorder.ResolveEffectiveMaxSampleInterval(ProximitySamplingTier.None,
                ProximitySamplingTier.Half, false, 3.0f, 0.2f), max);
        }

        [Fact]
        public void EffectiveIntervals_NoTier_TakesTheDebrisAwareCeilingCap()
        {
            var debris = new Recording { IsDebris = true, ParentAnchorRecordingId = "parent" };
            BackgroundRecorder.ResolveBackgroundEffectiveSampleIntervals(
                ProximitySamplingTier.None, false, 2.0, 0.2f, 30.0f, debris,
                out float min, out float max);

            float uncapped = FlightRecorder.ResolveEffectiveMaxSampleInterval(ProximitySamplingTier.None,
                ProximitySamplingTier.None, false, 30.0f, 0.2f);
            Assert.Equal(2.0f, min);
            Assert.Equal(BackgroundRecorder.ResolveDebrisAwareMaxSampleInterval(uncapped, debris), max);
        }

        // ---------------- ResolveBackgroundSampleTriggers

        [Fact]
        public void Triggers_FirstSample_MotionFires()
        {
            BackgroundRecorder bg = Recorder();
            bg.ResolveBackgroundSampleTriggersForTesting(Pid, 10.0, new Vector3(0, 0, 100), Quaternion.identity,
                false, 0.2f, 3.0f, 0.2f, 2.0f, 0.05f, out bool motion, out bool attitude);

            Assert.True(motion);
        }

        [Fact]
        public void Triggers_InsideFloorStillVelocity_NeitherFires()
        {
            BackgroundRecorder bg = Recorder();
            bg.SetLoadedSampleBaselineForTesting(Pid, 10.0, new Vector3(0, 0, 100), Quaternion.identity);
            bg.ResolveBackgroundSampleTriggersForTesting(Pid, 10.1, new Vector3(0, 0, 100), Quaternion.identity,
                false, 0.2f, 3.0f, 0.2f, 2.0f, 0.05f, out bool motion, out bool attitude);

            Assert.False(motion);
            Assert.False(attitude);
        }

        [Fact]
        public void Triggers_PastCeiling_MotionFires()
        {
            BackgroundRecorder bg = Recorder();
            bg.SetLoadedSampleBaselineForTesting(Pid, 10.0, new Vector3(0, 0, 100), Quaternion.identity);
            bg.ResolveBackgroundSampleTriggersForTesting(Pid, 13.5, new Vector3(0, 0, 100), Quaternion.identity,
                false, 0.2f, 3.0f, 0.2f, 2.0f, 0.05f, out bool motion, out bool attitude);

            Assert.True(motion);
        }

        [Fact]
        public void Triggers_RotationPastThresholdOutsideFloor_AttitudeFires()
        {
            BackgroundRecorder bg = Recorder();
            bg.SetLoadedSampleBaselineForTesting(Pid, 10.0, new Vector3(0, 0, 100), Quaternion.identity);
            Quaternion turned = new Quaternion(0f, 0.0871557f, 0f, 0.9961947f); // 10 deg about y
            bg.ResolveBackgroundSampleTriggersForTesting(Pid, 10.5, new Vector3(0, 0, 100), turned,
                false, 0.2f, 3.0f, 0.2f, 2.0f, 0.05f, out bool motion, out bool attitude);

            Assert.False(motion);
            Assert.True(attitude);
        }

        [Fact]
        public void Triggers_RotationInsideAttitudeFloor_AttitudeHolds()
        {
            BackgroundRecorder bg = Recorder();
            bg.SetLoadedSampleBaselineForTesting(Pid, 10.0, new Vector3(0, 0, 100), Quaternion.identity);
            Quaternion turned = new Quaternion(0f, 0.0871557f, 0f, 0.9961947f);
            float floor = BackgroundRecorder.ResolveBackgroundAttitudeMinSampleInterval(false, 2.0f, 0.2f);
            bg.ResolveBackgroundSampleTriggersForTesting(Pid, 10.0 + floor * 0.5, new Vector3(0, 0, 100), turned,
                false, 2.0f, 3.0f, 0.2f, 2.0f, 0.05f, out bool motion, out bool attitude);

            Assert.False(motion);
            Assert.False(attitude);
        }

        [Fact]
        public void Triggers_RotationBaselineFlagClear_AttitudeHolds()
        {
            // The same 10 degree turn outside the floor that fires in
            // Triggers_RotationPastThresholdOutsideFloor_AttitudeFires, but the state has no
            // rotation baseline: the unit must pass the state's flag, so attitude holds.
            BackgroundRecorder bg = Recorder();
            bg.SetLoadedSampleBaselineForTesting(Pid, 10.0, new Vector3(0, 0, 100), Quaternion.identity,
                hasWorldRotation: false);
            Quaternion turned = new Quaternion(0f, 0.0871557f, 0f, 0.9961947f);
            bg.ResolveBackgroundSampleTriggersForTesting(Pid, 10.5, new Vector3(0, 0, 100), turned,
                false, 0.2f, 3.0f, 0.2f, 2.0f, 0.05f, out bool motion, out bool attitude);

            Assert.False(motion);
            Assert.False(attitude);
        }
    }
}
