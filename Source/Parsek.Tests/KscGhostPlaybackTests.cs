using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class KscGhostPlaybackTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public KscGhostPlaybackTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekSettings.CurrentOverrideForTesting = null;
            RecordingStore.ResetForTesting();
        }

        #region Helper: create recordings

        static Recording MakeKerbinRecording(
            double startUT = 100, double endUT = 200,
            bool loopPlayback = false, double loopInterval = 10.0,
            bool playbackEnabled = true,
            string chainId = null, int chainIndex = -1,
            string bodyName = "Kerbin",
            TerminalState? terminalState = null)
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                PlaybackEnabled = playbackEnabled,
                LoopPlayback = loopPlayback,
                LoopIntervalSeconds = loopInterval,
                ChainId = chainId,
                ChainIndex = chainIndex,
                TerminalStateValue = terminalState,
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 70,
                bodyName = bodyName,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 0, 0)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT,
                latitude = -0.0972,
                longitude = -74.5575,
                altitude = 5000,
                bodyName = bodyName,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 50, 0)
            });

            return rec;
        }

        /// <summary>
        /// Create a recording that starts on Kerbin then transitions to Mun.
        /// </summary>
        static Recording MakeCrossBodyRecording()
        {
            var rec = new Recording
            {
                VesselName = "MunTransfer",
                PlaybackEnabled = true,
                LoopPlayback = false,
            };

            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = -0.0972, longitude = -74.5575, altitude = 70,
                bodyName = "Kerbin", rotation = new Quaternion(0, 0, 0, 1)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = -0.0972, longitude = -74.5575, altitude = 70000,
                bodyName = "Kerbin", rotation = new Quaternion(0, 0, 0, 1)
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 300, latitude = 5.0, longitude = 30.0, altitude = 10000,
                bodyName = "Mun", rotation = new Quaternion(0, 0, 0, 1)
            });

            return rec;
        }

        static Recording MakeKscRelativeRecording()
        {
            var before = new TrajectoryPoint
            {
                ut = 100,
                latitude = 10,
                longitude = 20,
                altitude = 30,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = Vector3.zero
            };
            var after = new TrajectoryPoint
            {
                ut = 110,
                latitude = 30,
                longitude = 40,
                altitude = 50,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = Vector3.zero
            };
            var rec = new Recording
            {
                VesselName = "RelativeKsc",
                RecordingFormatVersion = RecordingStore.RelativeLocalFrameFormatVersion
            };
            rec.Points.Add(before);
            rec.Points.Add(after);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100,
                endUT = 110,
                anchorVesselId = 42u,
                frames = new List<TrajectoryPoint> { before, after }
            });
            return rec;
        }

        static Recording MakeKscAbsoluteSectionRecording()
        {
            var before = new TrajectoryPoint
            {
                ut = 100,
                latitude = 1,
                longitude = 2,
                altitude = 3,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = Vector3.zero
            };
            var after = new TrajectoryPoint
            {
                ut = 110,
                latitude = 11,
                longitude = 12,
                altitude = 13,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = Vector3.zero
            };
            var rec = new Recording
            {
                VesselName = "AbsoluteKsc",
                RecordingFormatVersion = RecordingStore.RelativeLocalFrameFormatVersion
            };
            rec.Points.Add(before);
            rec.Points.Add(after);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100,
                endUT = 110,
                frames = new List<TrajectoryPoint> { before, after }
            });
            return rec;
        }

        static Recording MakeKscOrbitalCheckpointSectionRecording()
        {
            var before = new TrajectoryPoint
            {
                ut = 100,
                latitude = 1,
                longitude = 2,
                altitude = 3,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = Vector3.zero
            };
            var after = new TrajectoryPoint
            {
                ut = 110,
                latitude = 11,
                longitude = 12,
                altitude = 13,
                bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = Vector3.zero
            };
            var rec = new Recording
            {
                VesselName = "CheckpointKsc",
                RecordingFormatVersion = RecordingStore.RelativeLocalFrameFormatVersion
            };
            rec.Points.Add(before);
            rec.Points.Add(after);
            rec.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 100,
                endUT = 110,
                frames = new List<TrajectoryPoint> { before, after }
            });
            return rec;
        }

        static ParsekKSC.KscSurfaceLookup SurfaceLookupFromLatLonAlt(Action onCall = null)
        {
            return (string bodyName, double lat, double lon, double alt,
                out Vector3d worldPos, out Quaternion bodyRot) =>
            {
                onCall?.Invoke();
                bodyRot = Quaternion.identity;
                if (bodyName != "Kerbin")
                {
                    worldPos = Vector3d.zero;
                    return false;
                }

                worldPos = new Vector3d(lat * 100.0, lon * 100.0, alt);
                return true;
            };
        }

        static ParsekKSC.KscAnchorLookup AnchorLookup(uint expectedPid, Vector3d anchorPos)
        {
            return (uint pid, out ParsekKSC.KscAnchorFrame anchor) =>
            {
                if (pid != expectedPid)
                {
                    anchor = default(ParsekKSC.KscAnchorFrame);
                    return false;
                }

                anchor = new ParsekKSC.KscAnchorFrame(anchorPos, Quaternion.identity);
                return true;
            };
        }

        static bool MissingAnchorLookup(uint pid, out ParsekKSC.KscAnchorFrame anchor)
        {
            anchor = default(ParsekKSC.KscAnchorFrame);
            return false;
        }

        #endregion

        #region KSC reference-frame dispatch

        [Fact]
        public void TryInterpolateKscPlaybackPose_RelativeSection_UsesAnchorLocalOffset()
        {
            var rec = MakeKscRelativeRecording();
            int cachedIndex = 0;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;
            bool surfaceCalled = false;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec,
                ref cachedIndex,
                ref cachedFrameSourceKey,
                105,
                SurfaceLookupFromLatLonAlt(() => surfaceCalled = true),
                AnchorLookup(42u, new Vector3d(1000, 2000, 3000)),
                out ParsekKSC.KscPoseResolution pose);

            Assert.True(resolved);
            Assert.False(surfaceCalled);
            Assert.Equal(1, cachedFrameSourceKey);
            Assert.Equal("relative", pose.Branch);
            Assert.Equal(42u, pose.AnchorPid);
            Assert.Equal(1020.0, pose.WorldPos.x, 6);
            Assert.Equal(2030.0, pose.WorldPos.y, 6);
            Assert.Equal(3040.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, line =>
                line.Contains("[KSCGhost]") &&
                line.Contains("RELATIVE KSC playback resolved") &&
                line.Contains("anchorPid=42"));
        }

        [Fact]
        public void TryInterpolateKscPlaybackPose_RelativeSection_UnresolvedAnchorSkips()
        {
            var rec = MakeKscRelativeRecording();
            int cachedIndex = 0;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec,
                ref cachedIndex,
                ref cachedFrameSourceKey,
                105,
                SurfaceLookupFromLatLonAlt(),
                MissingAnchorLookup,
                out ParsekKSC.KscPoseResolution pose);

            Assert.False(resolved);
            Assert.Equal("relative", pose.Branch);
            Assert.Equal("relative-anchor-unresolved", pose.FailureReason);
            Assert.Equal(42u, pose.AnchorPid);
            Assert.Contains(logLines, line =>
                line.Contains("[KSCGhost]") &&
                line.Contains("RELATIVE KSC playback skipped") &&
                line.Contains("anchorPid=42"));
        }

        [Fact]
        public void ShouldHideKscRelativeAnchorUnresolvedGhost_HidesOnlyBeforeFirstPose()
        {
            Assert.True(ParsekKSC.ShouldHideKscRelativeAnchorUnresolvedGhost(null));

            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true
            };
            Assert.True(ParsekKSC.ShouldHideKscRelativeAnchorUnresolvedGhost(state));

            state.deferVisibilityUntilPlaybackSync = false;
            Assert.False(ParsekKSC.ShouldHideKscRelativeAnchorUnresolvedGhost(state));
        }

        [Fact]
        public void TryInterpolateKscPlaybackPose_AbsoluteSection_UsesSurfaceLookup()
        {
            var rec = MakeKscAbsoluteSectionRecording();
            int cachedIndex = 0;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;
            int surfaceCalls = 0;

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec,
                ref cachedIndex,
                ref cachedFrameSourceKey,
                105,
                SurfaceLookupFromLatLonAlt(() => surfaceCalls++),
                AnchorLookup(42u, new Vector3d(1000, 2000, 3000)),
                out ParsekKSC.KscPoseResolution pose);

            Assert.True(resolved);
            Assert.Equal(2, surfaceCalls);
            Assert.Equal(1, cachedFrameSourceKey);
            Assert.Equal("absolute", pose.Branch);
            Assert.Equal(600.0, pose.WorldPos.x, 6);
            Assert.Equal(700.0, pose.WorldPos.y, 6);
            Assert.Equal(8.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, line =>
                line.Contains("[KSCGhost]") &&
                line.Contains("KSC SURFACE playback resolved"));
        }

        [Fact]
        public void ShouldTriggerKscExplosionAtCurrentPose_UsesFrozenPoseAfterFirstPosition()
        {
            Assert.False(ParsekKSC.ShouldTriggerKscExplosionAtCurrentPoseForTesting(
                positioned: false,
                hasValidPose: false));
            Assert.True(ParsekKSC.ShouldTriggerKscExplosionAtCurrentPoseForTesting(
                positioned: false,
                hasValidPose: true));
            Assert.True(ParsekKSC.ShouldTriggerKscExplosionAtCurrentPoseForTesting(
                positioned: true,
                hasValidPose: false));
        }

        [Fact]
        public void TryInterpolateKscPlaybackPose_OrbitalCheckpointFrames_UseSurfaceLookup()
        {
            var rec = MakeKscOrbitalCheckpointSectionRecording();
            int cachedIndex = 0;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;
            int surfaceCalls = 0;
            bool anchorCalled = false;
            ParsekKSC.KscAnchorLookup anchorLookup = (uint pid, out ParsekKSC.KscAnchorFrame anchor) =>
            {
                anchorCalled = true;
                anchor = default(ParsekKSC.KscAnchorFrame);
                return false;
            };

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec,
                ref cachedIndex,
                ref cachedFrameSourceKey,
                105,
                SurfaceLookupFromLatLonAlt(() => surfaceCalls++),
                anchorLookup,
                out ParsekKSC.KscPoseResolution pose);

            Assert.True(resolved);
            Assert.Equal(2, surfaceCalls);
            Assert.Equal(1, cachedFrameSourceKey);
            Assert.False(anchorCalled);
            Assert.Equal("orbital-checkpoint", pose.Branch);
            Assert.Equal(600.0, pose.WorldPos.x, 6);
            Assert.Equal(700.0, pose.WorldPos.y, 6);
            Assert.Equal(8.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, line =>
                line.Contains("[KSCGhost]") &&
                line.Contains("KSC SURFACE playback resolved") &&
                line.Contains("branch=orbital-checkpoint"));
        }

        [Fact]
        public void TryInterpolateKscPlaybackPose_NoSection_UsesAbsoluteSurfaceLookup()
        {
            var rec = MakeKerbinRecording();
            int cachedIndex = 0;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;
            int surfaceCalls = 0;
            bool anchorCalled = false;
            ParsekKSC.KscAnchorLookup anchorLookup = (uint pid, out ParsekKSC.KscAnchorFrame anchor) =>
            {
                anchorCalled = true;
                anchor = default(ParsekKSC.KscAnchorFrame);
                return false;
            };

            bool resolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                rec,
                ref cachedIndex,
                ref cachedFrameSourceKey,
                150,
                SurfaceLookupFromLatLonAlt(() => surfaceCalls++),
                anchorLookup,
                out ParsekKSC.KscPoseResolution pose);

            Assert.True(resolved);
            Assert.Equal(2, surfaceCalls);
            Assert.Equal(ParsekKSC.KscFlatPointFrameSourceKey, cachedFrameSourceKey);
            Assert.False(anchorCalled);
            Assert.Equal("no-section", pose.Branch);
            Assert.Equal(-9.72, pose.WorldPos.x, 6);
            Assert.Equal(-7455.75, pose.WorldPos.y, 6);
            Assert.Equal(2535.0, pose.WorldPos.z, 6);
            Assert.Contains(logLines, line =>
                line.Contains("[KSCGhost]") &&
                line.Contains("KSC SURFACE playback resolved") &&
                line.Contains("branch=no-section"));
        }

        [Fact]
        public void TryInterpolateKscPlaybackPose_ResetsCacheWhenFrameSourceChanges()
        {
            var relative = MakeKscRelativeRecording();
            int cachedIndex = 99;
            int cachedFrameSourceKey = ParsekKSC.KscFlatPointFrameSourceKey;

            bool relativeResolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                relative,
                ref cachedIndex,
                ref cachedFrameSourceKey,
                105,
                SurfaceLookupFromLatLonAlt(),
                AnchorLookup(42u, new Vector3d(1000, 2000, 3000)),
                out ParsekKSC.KscPoseResolution relativePose);

            Assert.True(relativeResolved);
            Assert.Equal("relative", relativePose.Branch);
            Assert.Equal(1, cachedFrameSourceKey);
            Assert.InRange(cachedIndex, 0, relative.TrackSections[0].frames.Count - 1);

            var noSection = MakeKerbinRecording();
            bool absoluteResolved = ParsekKSC.TryInterpolateKscPlaybackPose(
                noSection,
                ref cachedIndex,
                ref cachedFrameSourceKey,
                150,
                SurfaceLookupFromLatLonAlt(),
                MissingAnchorLookup,
                out ParsekKSC.KscPoseResolution absolutePose);

            Assert.True(absoluteResolved);
            Assert.Equal("no-section", absolutePose.Branch);
            Assert.Equal(ParsekKSC.KscFlatPointFrameSourceKey, cachedFrameSourceKey);
            Assert.InRange(cachedIndex, 0, noSection.Points.Count - 1);
        }

        #endregion

        #region ShouldShowInKSC

        [Fact]
        public void ShouldShowInKSC_ValidKerbinRecording_ReturnsTrue()
        {
            var rec = MakeKerbinRecording();
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_DisabledRecording_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(playbackEnabled: false);
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_EmptyPoints_ReturnsFalse()
        {
            var rec = new Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test"
            };
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_SinglePoint_ReturnsFalse()
        {
            var rec = new Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test"
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100, bodyName = "Kerbin" });
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_NonKerbinBody_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(bodyName: "Mun");
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_ChainMidSegment_ReturnsTrue()
        {
            // Chain segments play independently — same behavior as flight scene
            var rec = MakeKerbinRecording(chainId: "chain-abc", chainIndex: 1);
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_ChainFirstSegment_ReturnsTrue()
        {
            var rec = MakeKerbinRecording(chainId: "chain-abc", chainIndex: 0);
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_NullPoints_ReturnsFalse()
        {
            var rec = new Recording
            {
                PlaybackEnabled = true,
                VesselName = "Test",
                Points = null
            };
            Assert.False(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_DestroyedRecording_StillShows()
        {
            // Destroyed recordings are valid for KSC display — they play then explode
            var rec = MakeKerbinRecording(terminalState: TerminalState.Destroyed);
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_CrossBodyRecording_PassesFilter()
        {
            // First point is Kerbin so it passes filter. Ghost will hide
            // when trajectory reaches Mun points (handled by InterpolateAndPositionKsc).
            var rec = MakeCrossBodyRecording();
            Assert.True(ParsekKSC.ShouldShowInKSC(rec));
        }

        [Fact]
        public void ShouldShowInKSC_IsPureFunction_NoLogging()
        {
            var rec = MakeKerbinRecording();
            ParsekKSC.ShouldShowInKSC(rec);
            Assert.Empty(logLines);
        }

        #endregion

        #region TryComputeLoopUT

        [Fact]
        public void TryComputeLoopUT_BeforeStart_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(startUT: 100, endUT: 200, loopPlayback: true);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 50,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_AtExactStart_ReturnsTrue()
        {
            // #381: period=110 > duration=100 (single-ghost loop with 10s pause tail).
            var rec = MakeKerbinRecording(startUT: 100, endUT: 200, loopPlayback: true,
                loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 100,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(100, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_WithinFirstCycle_ReturnsCorrectUT()
        {
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 150,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(150, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_InPauseWindow_SetsFlag()
        {
            // #381: duration=100, period=110 → cycleDuration=110.
            // At UT 205, elapsed=105, cycle=0, cycleTime=105 > duration=100 → pause window.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 205,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.True(inPauseWindow);
            Assert.Equal(0, cycleIndex);
            Assert.Equal(200, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_JustPastBoundaryWithinEpsilon_StaysInPlayback()
        {
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 200.0000005,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(200, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_SecondCycle_ReturnsCorrectCycleIndex()
        {
            // #381: duration=100, period=110 → cycleDuration=110.
            // At UT 215, elapsed=115, cycle=1, cycleTime=5.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 215,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(1, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(105, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_NullRecording_ReturnsFalse()
        {
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(null, 100,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_TooShortRecording_ReturnsFalse()
        {
            var rec = MakeKerbinRecording(startUT: 100, endUT: 100, loopPlayback: true);
            rec.Points[1] = new TrajectoryPoint
            {
                ut = 100, bodyName = "Kerbin",
                rotation = new Quaternion(0, 0, 0, 1)
            };

            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            bool result = ParsekKSC.TryComputeLoopUT(rec, 150,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.False(result);
        }

        [Fact]
        public void TryComputeLoopUT_ZeroInterval_ClampsAndCycles()
        {
            // #443: period=0 clamps to MinCycleDuration=5. duration=100.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 0.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 250: elapsed=150, cycleDuration=5, cycle=floor(150/5)=30, cycleTime=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 250,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(30, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(100, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_LargeCycleJump_NoOverflow()
        {
            // Simulate time warp: currentUT far ahead produces large cycleIndex.
            // #381: period=110 > duration → single-ghost path.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 11100: elapsed=11000, cycleDuration=110, cycle=100, phase=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 11100,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(100, cycleIndex);
            Assert.False(inPauseWindow);
        }

        [Fact]
        public void TryComputeLoopUT_AtEndOfCycle_NotInPauseWindow()
        {
            // #381: At exactly EndUT within a cycle (cycleTime == duration), should NOT be in pause.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 110.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 200: elapsed=100, cycle=0, cycleTime=100 == duration.
            // cycleTime > duration is false → not in pause.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 200,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(200, loopUT, 3);
        }

        [Fact]
        public void TryComputeLoopUT_AutoQueueUsesSharedLaunchSchedule()
        {
            var first = MakeKerbinRecording(
                startUT: 100, endUT: 150, loopPlayback: true, loopInterval: 999.0);
            first.RecordingId = "first";
            first.VesselName = "First";
            first.LoopTimeUnit = LoopTimeUnit.Auto;

            var second = MakeKerbinRecording(
                startUT: 110, endUT: 160, loopPlayback: true, loopInterval: 999.0);
            second.RecordingId = "second";
            second.VesselName = "Second";
            second.LoopTimeUnit = LoopTimeUnit.Auto;

            var third = MakeKerbinRecording(
                startUT: 120, endUT: 170, loopPlayback: true, loopInterval: 999.0);
            third.RecordingId = "third";
            third.VesselName = "Third";
            third.LoopTimeUnit = LoopTimeUnit.Auto;

            RecordingStore.AddCommittedInternal(first);
            RecordingStore.AddCommittedInternal(second);
            RecordingStore.AddCommittedInternal(third);
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                autoLoopIntervalSeconds = 30f
            };

            bool result = ParsekKSC.TryComputeLoopUT(
                second, 145.0,
                out double loopUT,
                out long cycleIndex,
                out bool inPauseWindow,
                recIdx: 1);

            Assert.True(result);
            Assert.Equal(125.0, loopUT, 6);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
        }

        [Fact]
        public void TryComputeLoopUT_UsesProvidedAutoQueueCache()
        {
            var rec = MakeKerbinRecording(
                startUT: 110, endUT: 160, loopPlayback: true, loopInterval: 999.0);
            rec.RecordingId = "second";
            rec.VesselName = "Second";
            rec.LoopTimeUnit = LoopTimeUnit.Auto;

            var cache = new Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>
            {
                [1] = new GhostPlaybackLogic.AutoLoopLaunchSchedule(130.0, 90.0, 1, 3)
            };

            bool result = ParsekKSC.TryComputeLoopUT(
                rec, 145.0,
                out double loopUT,
                out long cycleIndex,
                out bool inPauseWindow,
                recIdx: 1,
                autoLoopScheduleCache: cache);

            Assert.True(result);
            Assert.Equal(125.0, loopUT, 6);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPauseWindow);
        }

        #endregion

        #region GetLoopIntervalSeconds

        [Fact]
        public void GetLoopIntervalSeconds_NullRecording_ReturnsDefault()
        {
            Assert.Equal(LoopTiming.DefaultLoopIntervalSeconds, ParsekKSC.GetLoopIntervalSeconds(null));
        }

        [Fact]
        public void GetLoopIntervalSeconds_NaN_ReturnsDefault()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = double.NaN;
            Assert.Equal(LoopTiming.DefaultLoopIntervalSeconds, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_Infinity_ReturnsDefault()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = double.PositiveInfinity;
            Assert.Equal(LoopTiming.DefaultLoopIntervalSeconds, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_NegativeValue_ClampedToMinCycleDuration()
        {
            // #381: negative intervals are no longer allowed — clamp defensively to MinCycleDuration.
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = -50.0;
            Assert.Equal(LoopTiming.MinCycleDuration, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_VeryNegativeValue_ClampedToMinCycleDuration()
        {
            // #381: clamp applies regardless of magnitude.
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = -200.0;
            Assert.Equal(LoopTiming.MinCycleDuration, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_PositiveValue_ReturnsAsIs()
        {
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = 15.0;
            Assert.Equal(15.0, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_Zero_ClampsToMinCycleDuration()
        {
            // #381: zero is also below MinCycleDuration.
            var rec = MakeKerbinRecording();
            rec.LoopIntervalSeconds = 0.0;
            Assert.Equal(LoopTiming.MinCycleDuration, ParsekKSC.GetLoopIntervalSeconds(rec));
        }

        [Fact]
        public void GetLoopIntervalSeconds_AutoQueueReturnsQueueCadenceWhenRecordingIndexProvided()
        {
            var first = MakeKerbinRecording(
                startUT: 100, endUT: 150, loopPlayback: true, loopInterval: 999.0);
            first.RecordingId = "first";
            first.LoopTimeUnit = LoopTimeUnit.Auto;

            var second = MakeKerbinRecording(
                startUT: 110, endUT: 160, loopPlayback: true, loopInterval: 999.0);
            second.RecordingId = "second";
            second.LoopTimeUnit = LoopTimeUnit.Auto;

            var third = MakeKerbinRecording(
                startUT: 120, endUT: 170, loopPlayback: true, loopInterval: 999.0);
            third.RecordingId = "third";
            third.LoopTimeUnit = LoopTimeUnit.Auto;

            RecordingStore.AddCommittedInternal(first);
            RecordingStore.AddCommittedInternal(second);
            RecordingStore.AddCommittedInternal(third);
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                autoLoopIntervalSeconds = 30f
            };

            Assert.Equal(30.0, ParsekKSC.GetLoopIntervalSeconds(second), 6);
            Assert.Equal(90.0, ParsekKSC.GetLoopIntervalSeconds(second, recIdx: 1), 6);
        }

        [Fact]
        public void GetLoopIntervalSeconds_UsesProvidedAutoQueueCache()
        {
            var rec = MakeKerbinRecording(
                startUT: 110, endUT: 160, loopPlayback: true, loopInterval: 999.0);
            rec.RecordingId = "second";
            rec.LoopTimeUnit = LoopTimeUnit.Auto;

            var cache = new Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>
            {
                [1] = new GhostPlaybackLogic.AutoLoopLaunchSchedule(130.0, 90.0, 1, 3)
            };

            Assert.Equal(90.0, ParsekKSC.GetLoopIntervalSeconds(rec, 1, cache), 6);
        }

        [Fact]
        public void GetLoopIntervalSeconds_ManualLoopWithRecordingIndex_ReturnsRecordingCadence()
        {
            var rec = MakeKerbinRecording(
                startUT: 110, endUT: 160, loopPlayback: true, loopInterval: 45.0);
            rec.RecordingId = "manual";
            rec.LoopTimeUnit = LoopTimeUnit.Sec;

            Assert.Equal(45.0, ParsekKSC.GetLoopIntervalSeconds(rec, recIdx: 1), 6);
        }

        [Fact]
        public void RebuildAutoLoopLaunchScheduleCache_BuildsOrderedSharedSchedule()
        {
            var host = (ParsekKSC)FormatterServices.GetUninitializedObject(typeof(ParsekKSC));
            var schedulesField = typeof(ParsekKSC).GetField(
                "autoLoopLaunchSchedules",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var scratchField = typeof(ParsekKSC).GetField(
                "autoLoopQueueScratch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var rebuildMethod = typeof(ParsekKSC).GetMethod(
                "RebuildAutoLoopLaunchScheduleCache",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(schedulesField);
            Assert.NotNull(scratchField);
            Assert.NotNull(rebuildMethod);

            schedulesField.SetValue(
                host,
                new Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>());
            scratchField.SetValue(host, Activator.CreateInstance(scratchField.FieldType));

            var third = MakeKerbinRecording(
                startUT: 120, endUT: 170, loopPlayback: true, loopInterval: 999.0);
            third.RecordingId = "third";
            third.LoopTimeUnit = LoopTimeUnit.Auto;

            var disabled = MakeKerbinRecording(
                startUT: 90, endUT: 140, loopPlayback: true, loopInterval: 999.0, playbackEnabled: false);
            disabled.RecordingId = "disabled";
            disabled.LoopTimeUnit = LoopTimeUnit.Auto;

            var first = MakeKerbinRecording(
                startUT: 100, endUT: 150, loopPlayback: true, loopInterval: 999.0);
            first.RecordingId = "first";
            first.LoopTimeUnit = LoopTimeUnit.Auto;

            var second = MakeKerbinRecording(
                startUT: 110, endUT: 160, loopPlayback: true, loopInterval: 999.0);
            second.RecordingId = "second";
            second.LoopTimeUnit = LoopTimeUnit.Auto;

            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                autoLoopIntervalSeconds = 30f
            };

            rebuildMethod.Invoke(host, new object[]
            {
                new List<Recording> { third, disabled, first, second }
            });

            var cache = (Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>)schedulesField.GetValue(host);
            Assert.NotNull(cache);
            Assert.Equal(3, cache.Count);
            Assert.False(cache.ContainsKey(1));
            Assert.Equal(100.0, cache[2].LaunchStartUT, 6);
            Assert.Equal(130.0, cache[3].LaunchStartUT, 6);
            Assert.Equal(160.0, cache[0].LaunchStartUT, 6);
            Assert.Equal(90.0, cache[2].LaunchCadenceSeconds, 6);
            Assert.Equal(90.0, cache[3].LaunchCadenceSeconds, 6);
            Assert.Equal(90.0, cache[0].LaunchCadenceSeconds, 6);
        }

        [Fact]
        public void RebuildAutoLoopLaunchScheduleCache_StableQueueLogsOnceUntilFingerprintChanges()
        {
            var host = (ParsekKSC)FormatterServices.GetUninitializedObject(typeof(ParsekKSC));
            var schedulesField = typeof(ParsekKSC).GetField(
                "autoLoopLaunchSchedules",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var scratchField = typeof(ParsekKSC).GetField(
                "autoLoopQueueScratch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var rebuildMethod = typeof(ParsekKSC).GetMethod(
                "RebuildAutoLoopLaunchScheduleCache",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(schedulesField);
            Assert.NotNull(scratchField);
            Assert.NotNull(rebuildMethod);

            schedulesField.SetValue(
                host,
                new Dictionary<int, GhostPlaybackLogic.AutoLoopLaunchSchedule>());
            scratchField.SetValue(host, Activator.CreateInstance(scratchField.FieldType));

            var first = MakeKerbinRecording(
                startUT: 100, endUT: 150, loopPlayback: true, loopInterval: 999.0);
            first.RecordingId = "first";
            first.LoopTimeUnit = LoopTimeUnit.Auto;

            var second = MakeKerbinRecording(
                startUT: 110, endUT: 160, loopPlayback: true, loopInterval: 999.0);
            second.RecordingId = "second";
            second.LoopTimeUnit = LoopTimeUnit.Auto;

            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings
            {
                autoLoopIntervalSeconds = 30f
            };

            var recordings = new List<Recording> { first, second };
            rebuildMethod.Invoke(host, new object[] { recordings });
            rebuildMethod.Invoke(host, new object[] { recordings });

            Assert.Single(logLines.FindAll(l =>
                l.Contains("[KSC]") && l.Contains("Auto loop queue rebuilt")));

            second.RecordingId = "second-changed";
            rebuildMethod.Invoke(host, new object[] { recordings });

            Assert.Equal(2, logLines.FindAll(l =>
                l.Contains("[KSC]") && l.Contains("Auto loop queue rebuilt")).Count);
            Assert.Contains(logLines, l =>
                l.Contains("Auto loop queue rebuilt")
                && l.Contains("orderedIds=0:first,1:second-changed")
                && l.Contains("suppressed=1"));
        }

        [Fact]
        public void PlaybackDisabledPastEndSpawnAttempt_LogsOncePerRecordingAndReason()
        {
            var rec = MakeKerbinRecording(playbackEnabled: false);
            rec.RecordingId = "disabled-rec";
            rec.VesselName = "Disabled Vessel";
            var logged = new HashSet<string>();

            Assert.True(ParsekKSC.LogPlaybackDisabledPastEndSpawnAttemptOnce(
                rec, 3, "playback-disabled-past-end", logged));
            Assert.False(ParsekKSC.LogPlaybackDisabledPastEndSpawnAttemptOnce(
                rec, 3, "playback-disabled-past-end", logged));
            Assert.True(ParsekKSC.LogPlaybackDisabledPastEndSpawnAttemptOnce(
                rec, 3, "playback-disabled-other", logged));

            Assert.Single(logLines.FindAll(l =>
                l.Contains("[KSCSpawn]")
                && l.Contains("Playback-disabled past-end")
                && l.Contains("reason=playback-disabled-past-end")));
            Assert.Single(logLines.FindAll(l =>
                l.Contains("[KSCSpawn]")
                && l.Contains("Playback-disabled past-end")
                && l.Contains("reason=playback-disabled-other")));
        }

        #endregion

        #region TryComputeLoopUT with #381 period semantics

        [Fact]
        public void TryComputeLoopUT_PeriodShorterThanDuration_Overlaps()
        {
            // #381: period=30 < duration=100 → cycleDuration=30 (overlap via single-ghost math).
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: 30.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // At UT 160: elapsed=60, cycleDuration=30, cycle=2, cycleTime=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 160,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.Equal(2, cycleIndex);
            Assert.False(inPauseWindow);
            Assert.Equal(100, loopUT, 3);
        }

        [Fact]
        public void EffectiveLoopDuration_SubrangeAndFullRange_OverlapDecisionDiffers()
        {
            // #411 regression guard: the KSC dispatcher must branch on the effective loop
            // subrange, not the recording's full [StartUT, EndUT] span.
            var rec = MakeKerbinRecording(
                startUT: 0, endUT: 300, loopPlayback: true, loopInterval: 150.0);
            rec.LoopStartUT = 100;
            rec.LoopEndUT = 200;

            double effectiveDuration = GhostPlaybackEngine.EffectiveLoopDuration(rec);
            double rawDuration = rec.EndUT - rec.StartUT;

            Assert.True(GhostPlaybackLogic.IsOverlapLoop(rec.LoopIntervalSeconds, rawDuration));
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(rec.LoopIntervalSeconds, effectiveDuration));
        }

        [Fact]
        public void ResolveLoopPlaybackEndpointUT_Subrange_UsesEffectiveLoopEnd()
        {
            var rec = MakeKerbinRecording(
                startUT: 0, endUT: 300, loopPlayback: true, loopInterval: 80.0);
            rec.LoopStartUT = 100;
            rec.LoopEndUT = 200;

            Assert.Equal(200.0, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(rec));
        }

        [Fact]
        public void EffectiveLoopDuration_SubrangeChangesOverlapCycleBoundsComparedToFullRange()
        {
            // #411 regression guard: UpdateOverlapKsc must anchor both active-cycle bounds
            // and phase math to the effective loop range, otherwise cycles start from the
            // recording's raw StartUT and stale overlap ghosts linger too long.
            var rec = MakeKerbinRecording(
                startUT: 0, endUT: 300, loopPlayback: true, loopInterval: 80.0);
            rec.LoopStartUT = 100;
            rec.LoopEndUT = 200;

            long effectiveFirstCycle;
            long effectiveLastCycle;
            long rawFirstCycle;
            long rawLastCycle;

            GhostPlaybackLogic.GetActiveCycles(260,
                GhostPlaybackEngine.EffectiveLoopStartUT(rec),
                GhostPlaybackEngine.EffectiveLoopEndUT(rec),
                ParsekKSC.GetLoopIntervalSeconds(rec),
                10, out effectiveFirstCycle, out effectiveLastCycle);
            GhostPlaybackLogic.GetActiveCycles(260,
                rec.StartUT, rec.EndUT,
                ParsekKSC.GetLoopIntervalSeconds(rec),
                10, out rawFirstCycle, out rawLastCycle);

            Assert.Equal(1, effectiveFirstCycle);
            Assert.Equal(2, effectiveLastCycle);
            Assert.Equal(0, rawFirstCycle);
            Assert.Equal(3, rawLastCycle);
        }

        [Fact]
        public void TryComputeLoopUT_NegativeInterval_ClampsDefensively_NoThrow()
        {
            // #443: negative intervals are rejected at UI; engine clamps defensively to
            // MinCycleDuration=5. Must not throw.
            var rec = MakeKerbinRecording(
                startUT: 100, endUT: 200, loopPlayback: true, loopInterval: -30.0);
            double loopUT;
            long cycleIndex;
            bool inPauseWindow;

            // Clamped period=5, duration=100. At UT 250, elapsed=150, cycle=floor(150/5)=30, phase=0.
            bool result = ParsekKSC.TryComputeLoopUT(rec, 250,
                out loopUT, out cycleIndex, out inPauseWindow);

            Assert.True(result);
            Assert.False(inPauseWindow);
            Assert.Equal(30, cycleIndex);
            Assert.Equal(100, loopUT, 3);
        }

        #endregion

        #region Pause Menu Audio

        [Fact]
        public void ApplyAudioActionToGhostSet_VisitsPrimaryAndOverlapGhosts()
        {
            var primary0 = new GhostPlaybackState();
            var primary1 = new GhostPlaybackState();
            var overlap0 = new GhostPlaybackState();
            var overlap1 = new GhostPlaybackState();
            var visited = new List<GhostPlaybackState>();

            var counts = ParsekKSC.ApplyAudioActionToGhostSet(
                new Dictionary<int, GhostPlaybackState>
                {
                    [1] = primary0,
                    [2] = primary1,
                },
                new Dictionary<int, List<GhostPlaybackState>>
                {
                    [1] = new List<GhostPlaybackState> { overlap0, overlap1 }
                },
                state => visited.Add(state));

            Assert.Equal((2, 2), counts);
            Assert.Equal(4, visited.Count);
            Assert.Contains(primary0, visited);
            Assert.Contains(primary1, visited);
            Assert.Contains(overlap0, visited);
            Assert.Contains(overlap1, visited);
        }

        [Theory]
        [InlineData(false, true, true)]
        [InlineData(true, true, false)]
        [InlineData(false, false, false)]
        [InlineData(true, false, false)]
        public void ShouldApplyRuntimeGhostEvents_RespectsPauseLatch(
            bool pauseMenuOpen, bool inCullRange, bool expected)
        {
            Assert.Equal(expected,
                ParsekKSC.ShouldApplyRuntimeGhostEvents(pauseMenuOpen, inCullRange));
        }

        [Fact]
        public void ApplyAudioActionToActiveGhosts_LogsVisitedCounts()
        {
            var host = (ParsekKSC)FormatterServices.GetUninitializedObject(typeof(ParsekKSC));
            var primary = new GhostPlaybackState();
            var overlap = new GhostPlaybackState();
            var visited = new List<GhostPlaybackState>();

            typeof(ParsekKSC)
                .GetField("kscGhosts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(host, new Dictionary<int, GhostPlaybackState> { [7] = primary });
            typeof(ParsekKSC)
                .GetField("kscOverlapGhosts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(host, new Dictionary<int, List<GhostPlaybackState>>
                {
                    [7] = new List<GhostPlaybackState> { overlap }
                });

            var counts = host.ApplyAudioActionToActiveGhosts(
                state => visited.Add(state),
                "pause-test");

            Assert.Equal((1, 1), counts);
            Assert.Equal(new[] { primary, overlap }, visited);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostAudio]") &&
                line.Contains("KSC pause-test: 1 primary + 1 overlap ghost(s)"));
        }

        [Fact]
        public void OnGamePause_WrapperLatchesPauseAndDispatches()
        {
            var host = (ParsekKSC)FormatterServices.GetUninitializedObject(typeof(ParsekKSC));
            var primary = new GhostPlaybackState();
            var overlap = new GhostPlaybackState();
            var visited = new List<GhostPlaybackState>();
            var pauseField = typeof(ParsekKSC)
                .GetField("pauseMenuOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var onGamePause = typeof(ParsekKSC)
                .GetMethod("OnGamePause", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var previousPauseAction = ParsekKSC.PauseGhostAudioAction;

            typeof(ParsekKSC)
                .GetField("kscGhosts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(host, new Dictionary<int, GhostPlaybackState> { [1] = primary });
            typeof(ParsekKSC)
                .GetField("kscOverlapGhosts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(host, new Dictionary<int, List<GhostPlaybackState>>
                {
                    [1] = new List<GhostPlaybackState> { overlap }
                });

            try
            {
                ParsekKSC.PauseGhostAudioAction = state => visited.Add(state);
                onGamePause.Invoke(host, null);
            }
            finally
            {
                ParsekKSC.PauseGhostAudioAction = previousPauseAction;
            }

            Assert.True((bool)pauseField.GetValue(host));
            Assert.Equal(2, visited.Count);
            Assert.Contains(primary, visited);
            Assert.Contains(overlap, visited);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostAudio]") &&
                line.Contains("KSC OnGamePause: 1 primary + 1 overlap ghost(s)"));
        }

        [Fact]
        public void OnGameUnpause_WrapperClearsPauseAndDispatches()
        {
            var host = (ParsekKSC)FormatterServices.GetUninitializedObject(typeof(ParsekKSC));
            var primary = new GhostPlaybackState();
            var overlap = new GhostPlaybackState();
            var visited = new List<GhostPlaybackState>();
            var pauseField = typeof(ParsekKSC)
                .GetField("pauseMenuOpen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var onGameUnpause = typeof(ParsekKSC)
                .GetMethod("OnGameUnpause", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var previousUnpauseAction = ParsekKSC.UnpauseGhostAudioAction;

            typeof(ParsekKSC)
                .GetField("kscGhosts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(host, new Dictionary<int, GhostPlaybackState> { [1] = primary });
            typeof(ParsekKSC)
                .GetField("kscOverlapGhosts", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(host, new Dictionary<int, List<GhostPlaybackState>>
                {
                    [1] = new List<GhostPlaybackState> { overlap }
                });
            pauseField.SetValue(host, true);

            try
            {
                ParsekKSC.UnpauseGhostAudioAction = state => visited.Add(state);
                onGameUnpause.Invoke(host, null);
            }
            finally
            {
                ParsekKSC.UnpauseGhostAudioAction = previousUnpauseAction;
            }

            Assert.False((bool)pauseField.GetValue(host));
            Assert.Equal(2, visited.Count);
            Assert.Contains(primary, visited);
            Assert.Contains(overlap, visited);
            Assert.Contains(logLines, line =>
                line.Contains("[GhostAudio]") &&
                line.Contains("KSC OnGameUnpause: 1 primary + 1 overlap ghost(s)"));
        }

        #endregion
    }
}
