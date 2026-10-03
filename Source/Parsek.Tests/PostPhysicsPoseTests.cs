using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The physics-callback sample-source decision (todo PHYSWARP-RATE-CHANGE-SAMPLE-SKEW /
    /// PHYSWARP-BACKSTOP-MISSED-FRAME): a callback inside FixedUpdate must not pair one
    /// physics step's UT with another step's position.
    /// </summary>
    [Collection("Sequential")]
    public class PostPhysicsPoseTests : IDisposable
    {
        private const uint Pid = 4242u;
        private const double Step4x = 0.08;
        private readonly List<string> logLines = new List<string>();

        public PostPhysicsPoseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            PostPhysicsPoseCache.ResetForTesting();
        }

        public void Dispose()
        {
            PostPhysicsPoseCache.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static PostPhysicsPose Pose(double ut, uint pid = Pid, string body = "Kerbin")
        {
            return new PostPhysicsPose
            {
                VesselPid = pid,
                BodyName = body,
                UT = ut,
                FixedTime = 10.0,
                Latitude = -0.0946,
                Longitude = -60.83,
                Altitude = 70100.0,
            };
        }

        private static PhysicsSampleSource Resolve(
            bool inFixedStep,
            PostPhysicsPose pose,
            double liveUT,
            out string reason,
            bool hasPose = true,
            bool packed = false,
            bool relative = false,
            bool statsRunInUpdate = true,
            double step = Step4x)
        {
            return PostPhysicsPoseCache.ResolveSampleSource(
                inFixedStep, statsRunInUpdate, packed, relative, hasPose, pose,
                Pid, "Kerbin", liveUT, step, out reason);
        }

        [Fact]
        public void FixedStep_PoseOneStepBehindLiveUT_UsesPose()
        {
            // PWR-3 2026-10-02_2133: the read on the 14th 4x step carried the 13th step's
            // position; the pose of that 13th step is what the sample must record.
            var source = Resolve(true, Pose(410.1444), 410.2244, out string reason);
            Assert.Equal(PhysicsSampleSource.PostPhysicsPose, source);
            Assert.Equal("fixed-step-pose", reason);
        }

        [Fact]
        public void FixedStep_PoseAtLiveUT_UsesPose()
        {
            // If UT had not advanced yet at the callback, the pose is still that step's.
            var source = Resolve(true, Pose(212.5152), 212.5152, out _);
            Assert.Equal(PhysicsSampleSource.PostPhysicsPose, source);
        }

        [Fact]
        public void UpdateStep_PoseAtLiveUT_UsesPose()
        {
            var source = Resolve(false, Pose(212.4391), 212.4391, out string reason);
            Assert.Equal(PhysicsSampleSource.PostPhysicsPose, source);
            Assert.Equal("update-pose", reason);
        }

        [Fact]
        public void FixedStep_NoPose_Skips()
        {
            var source = Resolve(true, default(PostPhysicsPose), 100.0, out string reason, hasPose: false);
            Assert.Equal(PhysicsSampleSource.Skip, source);
            Assert.Equal("fixed-step-no-pose", reason);
        }

        [Fact]
        public void UpdateStep_NoPose_ReadsLive()
        {
            var source = Resolve(false, default(PostPhysicsPose), 100.0, out string reason, hasPose: false);
            Assert.Equal(PhysicsSampleSource.Live, source);
            Assert.Equal("update-no-pose", reason);
        }

        [Fact]
        public void FixedStep_StalePose_Skips()
        {
            // Two steps behind: the capture loop missed a step, so the pose is not the
            // step the live read straddles.
            var source = Resolve(true, Pose(100.0), 100.0 + 2 * Step4x, out string reason);
            Assert.Equal(PhysicsSampleSource.Skip, source);
            Assert.Equal("fixed-step-pose-stale", reason);
        }

        [Fact]
        public void FixedStep_PoseAheadOfLiveUT_Skips()
        {
            var source = Resolve(true, Pose(100.1), 100.0, out string reason);
            Assert.Equal(PhysicsSampleSource.Skip, source);
            Assert.Equal("fixed-step-pose-ahead", reason);
        }

        [Fact]
        public void FixedStep_PoseOfAnotherVessel_Skips()
        {
            var source = Resolve(true, Pose(99.92, pid: 7u), 100.0, out string reason);
            Assert.Equal(PhysicsSampleSource.Skip, source);
            Assert.Equal("fixed-step-pose-other-vessel", reason);
        }

        [Fact]
        public void FixedStep_PoseOnAnotherBody_Skips()
        {
            var source = Resolve(true, Pose(99.92, body: "Mun"), 100.0, out string reason);
            Assert.Equal(PhysicsSampleSource.Skip, source);
            Assert.Equal("fixed-step-pose-other-body", reason);
        }

        [Fact]
        public void PackedVessel_ReadsLiveInEitherStep()
        {
            Assert.Equal(PhysicsSampleSource.Live,
                Resolve(true, Pose(99.92), 100.0, out string r1, packed: true));
            Assert.Equal("packed", r1);
            Assert.Equal(PhysicsSampleSource.Live,
                Resolve(false, Pose(100.0), 100.0, out _, packed: true));
        }

        [Fact]
        public void RelativeMode_SkipsInFixedStep_ReadsLiveInUpdate()
        {
            Assert.Equal(PhysicsSampleSource.Skip,
                Resolve(true, Pose(99.92), 100.0, out string r1, relative: true));
            Assert.Equal("fixed-step-relative", r1);
            Assert.Equal(PhysicsSampleSource.Live,
                Resolve(false, Pose(100.0), 100.0, out string r2, relative: true));
            Assert.Equal("update-relative", r2);
        }

        [Fact]
        public void StatsNeverRunInUpdate_RejectionReadsLiveInsteadOfSkipping()
        {
            // With VesselPrecalculate.disableRunInUpdate every callback is a FixedUpdate
            // one; skipping would starve the recording.
            Assert.Equal(PhysicsSampleSource.Live,
                Resolve(true, default(PostPhysicsPose), 100.0, out _, hasPose: false, statsRunInUpdate: false));
            Assert.Equal(PhysicsSampleSource.Live,
                Resolve(true, Pose(99.92), 100.0, out _, relative: true, statsRunInUpdate: false));
            Assert.Equal(PhysicsSampleSource.PostPhysicsPose,
                Resolve(true, Pose(99.92), 100.0, out _, statsRunInUpdate: false));
        }

        [Fact]
        public void ClassifyPoseRejection_StepToleranceFollowsTheWarpRate()
        {
            // 1x: one 0.02 s step behind is current, 0.04 s is stale.
            Assert.Null(PostPhysicsPoseCache.ClassifyPoseRejection(true, Pose(100.0), Pid, "Kerbin", 100.02, 0.02));
            Assert.Equal("pose-stale",
                PostPhysicsPoseCache.ClassifyPoseRejection(true, Pose(100.0), Pid, "Kerbin", 100.04, 0.02));
            // 4x: one 0.08 s step is current.
            Assert.Null(PostPhysicsPoseCache.ClassifyPoseRejection(true, Pose(100.0), Pid, "Kerbin", 100.08, 0.08));
            // A non-positive step falls back to the 1x step.
            Assert.Equal("pose-stale",
                PostPhysicsPoseCache.ClassifyPoseRejection(true, Pose(100.0), Pid, "Kerbin", 100.08, 0.0));
            Assert.Equal("pose-nan",
                PostPhysicsPoseCache.ClassifyPoseRejection(true, Pose(double.NaN), Pid, "Kerbin", 100.0, 0.02));
        }

        [Fact]
        public void BackgroundFixedStepSkip_OnlyForUnpackedVesselsWhenUpdateRunsTheStats()
        {
            Assert.True(PostPhysicsPoseCache.ShouldSkipBackgroundSampleInFixedStep(true, true, false));
            Assert.False(PostPhysicsPoseCache.ShouldSkipBackgroundSampleInFixedStep(false, true, false));
            Assert.False(PostPhysicsPoseCache.ShouldSkipBackgroundSampleInFixedStep(true, true, true));
            Assert.False(PostPhysicsPoseCache.ShouldSkipBackgroundSampleInFixedStep(true, false, false));
        }

        [Fact]
        public void BoundaryChecks_DeferOnlyInFixedStepForUnpackedVesselsWhenUpdateRunsTheStats()
        {
            Assert.True(PostPhysicsPoseCache.ShouldDeferBoundaryChecksInFixedStep(true, true, false));
            Assert.False(PostPhysicsPoseCache.ShouldDeferBoundaryChecksInFixedStep(false, true, false));
            Assert.False(PostPhysicsPoseCache.ShouldDeferBoundaryChecksInFixedStep(true, true, true));
            Assert.False(PostPhysicsPoseCache.ShouldDeferBoundaryChecksInFixedStep(true, false, false));
        }

        [Fact]
        public void Cache_SetClear_RoundTrips()
        {
            Assert.False(PostPhysicsPoseCache.TryGetLatest(out _));
            PostPhysicsPoseCache.SetForTesting(Pose(12.5));
            Assert.True(PostPhysicsPoseCache.TryGetLatest(out PostPhysicsPose got));
            Assert.Equal(12.5, got.UT);
            PostPhysicsPoseCache.Clear();
            Assert.False(PostPhysicsPoseCache.TryGetLatest(out _));
        }

        [Fact]
        public void NotePhysicsSampleSource_LogsFixedStepCallbacksWithTallies()
        {
            var recorder = new FlightRecorder();
            recorder.NotePhysicsSampleSource(
                PhysicsSampleSource.PostPhysicsPose, "fixed-step-pose", true, 410.2244, Pose(410.1444));
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Fixed-step physics callback: source=PostPhysicsPose reason=fixed-step-pose")
                && l.Contains("liveUT=410.2244")
                && l.Contains("poseUT=410.1444")
                && l.Contains("fixedStepPoseSamples=1")
                && l.Contains("skippedCallbacks=0"));
        }

        [Fact]
        public void NotePhysicsSampleSource_UpdateStepPoseIsSilent_UpdateFallbackLogs()
        {
            var recorder = new FlightRecorder();
            recorder.NotePhysicsSampleSource(
                PhysicsSampleSource.PostPhysicsPose, "update-pose", false, 100.0, Pose(100.0));
            Assert.DoesNotContain(logLines, l => l.Contains("physics callback"));

            recorder.NotePhysicsSampleSource(
                PhysicsSampleSource.Live, "update-no-pose", false, 100.0, default(PostPhysicsPose));
            Assert.Contains(logLines, l => l.Contains("[Recorder]")
                && l.Contains("Update-step physics callback read live: source=Live reason=update-no-pose")
                && l.Contains("poseUT=(none)"));
        }

        [Fact]
        public void NotePhysicsSampleSource_SkipCountsAndLogs()
        {
            var recorder = new FlightRecorder();
            recorder.NotePhysicsSampleSource(
                PhysicsSampleSource.Skip, "fixed-step-relative", true, 100.0, default(PostPhysicsPose));
            Assert.Contains(logLines, l => l.Contains("source=Skip reason=fixed-step-relative")
                && l.Contains("skippedCallbacks=1"));
        }
    }
}
