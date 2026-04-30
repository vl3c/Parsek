using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class GhostPlaybackEngineTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GhostPlaybackEngineTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GhostPlaybackEngine.PendingOrbitBodyRadiusResolverForTesting = null;
        }

        private static EngineGhostInfo BuildEngineGhostInfo(int particleSystemCount)
        {
            var info = new EngineGhostInfo();
            SetParticleSystemCount(info, "particleSystems", particleSystemCount);
            return info;
        }

        private static RcsGhostInfo BuildRcsGhostInfo(int particleSystemCount)
        {
            var info = new RcsGhostInfo();
            SetParticleSystemCount(info, "particleSystems", particleSystemCount);
            return info;
        }

        private static void SetParticleSystemCount(object target, string fieldName, int count)
        {
            var field = target.GetType().GetField(fieldName);
            Assert.NotNull(field);

            var list = Activator.CreateInstance(field.FieldType) as IList;
            Assert.NotNull(list);

            for (int i = 0; i < count; i++)
                list.Add(null);

            field.SetValue(target, list);
        }

        private static MockTrajectory MakeAutoTrajectory(
            string recordingId, double startUT, double endUT)
        {
            return new MockTrajectory
            {
                RecordingId = recordingId,
                VesselName = recordingId,
                PlaybackEnabled = true,
            }.WithTimeRange(startUT, endUT).WithLoop(999.0, LoopTimeUnit.Auto);
        }

        private static GhostPlaybackState InvokeCreatePendingSpawnState(
            GhostPlaybackEngine engine,
            IPlaybackTrajectory traj,
            double playbackUT,
            PendingSpawnLifecycle lifecycle,
            TrajectoryPlaybackFlags flags)
        {
            MethodInfo method = typeof(GhostPlaybackEngine).GetMethod(
                "CreatePendingSpawnState",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object state = method.Invoke(engine, new object[] { traj, playbackUT, lifecycle, flags });
            return Assert.IsType<GhostPlaybackState>(state);
        }

        private sealed class SpawnPrimingPositioner : IGhostPositioner
        {
            internal int InterpolateCalls;
            internal int PositionAtPointCalls;
            internal double LastUT;
            internal double LastPointUT;
            internal Vector3 PrimedPosition = new Vector3(12f, 34f, 56f);

            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx)
            {
                InterpolateCalls++;
                LastUT = ut;
                if (state?.ghost != null)
                    state.ghost.transform.position = PrimedPosition;
                state?.SetInterpolated(new InterpolationResult(Vector3.zero, "Kerbin", 123.0));
            }

            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx, uint anchorVesselId)
            {
                InterpolateAndPosition(index, traj, state, ut, suppressFx);
            }

            public void PositionAtPoint(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, TrajectoryPoint point)
            {
                PositionAtPointCalls++;
                LastPointUT = point.ut;
                if (state?.ghost != null)
                    state.ghost.transform.position = PrimedPosition;
            }

            public void PositionAtSurface(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state)
            {
            }

            public void PositionFromOrbit(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut)
            {
            }

            public void PositionLoop(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx)
            {
                InterpolateAndPosition(index, traj, state, ut, suppressFx);
            }

            public bool TryResolveExplosionAnchorPosition(int index,
                IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
            {
                worldPosition = PrimedPosition;
                return true;
            }

            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
                IPlaybackTrajectory traj, double distance, int protectedIndex)
            {
                return new ZoneRenderingResult();
            }

            public void ClearOrbitCache()
            {
            }

            public bool TryGetLiveAnchorWorldPosition(uint anchorVesselId, out Vector3d worldPosition)
            {
                worldPosition = Vector3d.zero;
                return false;
            }
        }
        // ===================================================================
        // ShouldLoopPlayback — static, pure predicate
        // ===================================================================

        #region ShouldLoopPlayback

        [Fact]
        public void ShouldLoopPlayback_NullTrajectory_ReturnsFalse()
        {
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(null));
        }

        [Fact]
        public void ShouldLoopPlayback_LoopDisabled_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopPlayback = false;
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_NullPoints_ReturnsFalse()
        {
            var traj = new MockTrajectory();
            traj.LoopPlayback = true;
            traj.Points = null;
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_EmptyPoints_ReturnsFalse()
        {
            var traj = new MockTrajectory();
            traj.LoopPlayback = true;
            // Points is empty by default
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_SinglePoint_ReturnsFalse()
        {
            var traj = new MockTrajectory();
            traj.LoopPlayback = true;
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_TooShortDuration_ReturnsFalse()
        {
            // Duration = 0.5s, which is <= MinLoopDurationSeconds (1.0)
            var traj = new MockTrajectory().WithTimeRange(100, 100.5);
            traj.LoopPlayback = true;
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_ExactlyMinDuration_ReturnsFalse()
        {
            // Duration == MinLoopDurationSeconds (1.0) — boundary: not strictly greater
            var traj = new MockTrajectory().WithTimeRange(100, 101);
            traj.LoopPlayback = true;
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_ValidLooping_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop();
            Assert.True(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_JustOverMinDuration_ReturnsTrue()
        {
            // Duration = 1.01s, which is > MinLoopDurationSeconds (1.0)
            var traj = new MockTrajectory().WithTimeRange(100, 101.01).WithLoop();
            Assert.True(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_NoLogOutput()
        {
            // ShouldLoopPlayback is a pure predicate — should not produce any log output
            GhostPlaybackEngine.ShouldLoopPlayback(null);
            GhostPlaybackEngine.ShouldLoopPlayback(new MockTrajectory().WithTimeRange(100, 200).WithLoop());
            Assert.Empty(logLines);
        }

        #endregion

        #region Relative endpoint helpers

        [Fact]
        public void ResolveRecordingEndpointPlaybackUT_UsesLastPointUT()
        {
            var traj = new MockTrajectory
            {
                EndUTOverride = 200.0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 150.0 },
                },
            };

            Assert.Equal(150.0, GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj));
        }

        [Fact]
        public void ResolveRecordingEndpointPlaybackUT_NoPointsUsesOrbitEndUT()
        {
            var traj = new MockTrajectory
            {
                EndUTOverride = 300.0,
                Points = new List<TrajectoryPoint>(),
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment { startUT = 100.0, endUT = 250.0 },
                },
            };

            Assert.Equal(250.0, GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj));
        }

        [Fact]
        public void TryGetRelativeSectionAnchorAtUT_RelativeEndpointReturnsSectionAnchor()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorVesselId = 42u,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT(
                traj,
                GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj),
                out uint anchorPid);

            Assert.True(result);
            Assert.Equal(42u, anchorPid);
        }

        [Fact]
        public void TryGetRelativeSectionAnchorAtUT_SinglePointRelativeEndpointStillRoutesRelative()
        {
            var traj = new MockTrajectory
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 110.0 },
                },
            };
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorVesselId = 3151978247u,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT(
                traj,
                GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj),
                out uint anchorPid);

            Assert.True(result);
            Assert.Equal(3151978247u, anchorPid);
        }

        [Fact]
        public void TryGetRelativeSectionAnchorAtUT_RelativeSectionFallsBackToLoopAnchor()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.LoopAnchorVesselId = 77u;
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorVesselId = 0u,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT(
                traj, 110.0, out uint anchorPid);

            Assert.True(result);
            Assert.Equal(77u, anchorPid);
        }

        [Fact]
        public void TryGetRelativeSectionAnchorAtUT_RelativeSectionWithNoAnchorStillRoutesRelative()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorVesselId = 0u,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT(
                traj, 110.0, out uint anchorPid);

            Assert.True(result);
            Assert.Equal(0u, anchorPid);
        }

        [Fact]
        public void TryGetRelativeSectionAnchorAtUT_AbsoluteEndpointReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 110.0,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAnchorAtUT(
                traj, 110.0, out uint anchorPid);

            Assert.False(result);
            Assert.Equal(0u, anchorPid);
        }

        [Theory]
        [InlineData(true, false, false, false, false, true)]
        [InlineData(false, true, false, false, false, true)]
        [InlineData(false, false, false, false, false, false)]
        [InlineData(true, false, true, false, false, false)]
        [InlineData(true, false, false, true, false, false)]
        [InlineData(true, false, false, false, true, false)]
        public void ShouldEnterPastEndCompletion_GatesAllDedupReasons(
            bool pastEnd,
            bool pastEffectiveEnd,
            bool completionFired,
            bool earlyCompletionFired,
            bool endpointRetiredSuppressed,
            bool expected)
        {
            Assert.Equal(expected, GhostPlaybackEngine.ShouldEnterPastEndCompletion(
                pastEnd,
                pastEffectiveEnd,
                completionFired,
                earlyCompletionFired,
                endpointRetiredSuppressed));
        }

        #endregion

        // ===================================================================
        // EffectiveLoopStartUT / EffectiveLoopEndUT — static helpers
        // ===================================================================

        #region EffectiveLoopStartUT

        [Fact]
        public void EffectiveLoopStartUT_NaN_ReturnsStartUT()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = double.NaN;
            Assert.Equal(100, GhostPlaybackEngine.EffectiveLoopStartUT(traj));
        }

        [Fact]
        public void EffectiveLoopStartUT_ValidValue_ReturnsLoopStartUT()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = 130;
            Assert.Equal(130, GhostPlaybackEngine.EffectiveLoopStartUT(traj));
        }

        [Fact]
        public void EffectiveLoopStartUT_BelowStartUT_ReturnsStartUT()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = 50;
            Assert.Equal(100, GhostPlaybackEngine.EffectiveLoopStartUT(traj));
        }

        [Fact]
        public void EffectiveLoopStartUT_AtEndUT_ReturnsStartUT()
        {
            // LoopStartUT must be < EndUT
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = 200;
            Assert.Equal(100, GhostPlaybackEngine.EffectiveLoopStartUT(traj));
        }

        [Fact]
        public void EffectiveLoopStartUT_InvertedRange_FallsBackToFullRange()
        {
            // start=180, end=130 → start >= end → fall back
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = 180;
            traj.LoopEndUT = 130;
            Assert.Equal(100, GhostPlaybackEngine.EffectiveLoopStartUT(traj));
        }

        #endregion

        #region EffectiveLoopEndUT

        [Fact]
        public void EffectiveLoopEndUT_NaN_ReturnsEndUT()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopEndUT = double.NaN;
            Assert.Equal(200, GhostPlaybackEngine.EffectiveLoopEndUT(traj));
        }

        [Fact]
        public void EffectiveLoopEndUT_ValidValue_ReturnsLoopEndUT()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopEndUT = 170;
            Assert.Equal(170, GhostPlaybackEngine.EffectiveLoopEndUT(traj));
        }

        [Fact]
        public void EffectiveLoopEndUT_AboveEndUT_ReturnsEndUT()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopEndUT = 250;
            Assert.Equal(200, GhostPlaybackEngine.EffectiveLoopEndUT(traj));
        }

        [Fact]
        public void EffectiveLoopEndUT_AtStartUT_ReturnsEndUT()
        {
            // LoopEndUT must be > StartUT
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopEndUT = 100;
            Assert.Equal(200, GhostPlaybackEngine.EffectiveLoopEndUT(traj));
        }

        [Fact]
        public void EffectiveLoopEndUT_InvertedRange_FallsBackToFullRange()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = 180;
            traj.LoopEndUT = 130;
            Assert.Equal(200, GhostPlaybackEngine.EffectiveLoopEndUT(traj));
        }

        #endregion

        #region EffectiveLoopDuration — #409

        [Fact]
        public void EffectiveLoopDuration_NoSubrange_EqualsFullRange()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            // LoopStartUT/LoopEndUT default to NaN → effective range = full range.
            Assert.Equal(100.0, GhostPlaybackEngine.EffectiveLoopDuration(traj));
        }

        [Fact]
        public void EffectiveLoopDuration_WithSubrange_EqualsSubrange()
        {
            // #409: a recording with a loop subrange [125, 175] inside [100, 200] must
            // report duration=50, not 100. Previously WatchModeController had two sites
            // computing "duration" two different ways — this test is the shared source of
            // truth that both sites now use.
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = 125;
            traj.LoopEndUT = 175;
            Assert.Equal(50.0, GhostPlaybackEngine.EffectiveLoopDuration(traj));
        }

        [Fact]
        public void ResolveLoopPlaybackEndpointUT_WithSubrange_UsesEffectiveLoopEnd()
        {
            var traj = new MockTrajectory().WithTimeRange(0, 300);
            traj.LoopStartUT = 100;
            traj.LoopEndUT = 200;

            Assert.Equal(200.0, GhostPlaybackEngine.ResolveLoopPlaybackEndpointUT(traj));
        }

        [Fact]
        public void EffectiveLoopDuration_SubrangeAndFullRange_OverlapDecisionDiffers()
        {
            // #409 regression guard: for a recording with a 50s loop subrange and a 80s
            // period, raw-range dispatch (100s) would say OVERLAP (80 < 100) while
            // subrange dispatch (50s) says SINGLE (80 >= 50). Before the fix, the two
            // watch-mode sites could disagree on which path to take.
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.LoopStartUT = 125;
            traj.LoopEndUT = 175;
            double effectiveDuration = GhostPlaybackEngine.EffectiveLoopDuration(traj);
            double rawDuration = traj.EndUT - traj.StartUT;

            const double period = 80.0;
            Assert.True(GhostPlaybackLogic.IsOverlapLoop(period, rawDuration));
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(period, effectiveDuration));
        }

        [Fact]
        public void EffectiveLoopDuration_SubrangeAndHybridRange_OverlapDecisionDiffers()
        {
            // #411 regression guard: UpdateLoopingPlayback used `traj.EndUT - effStart`,
            // which turns a [100, 200] loop subrange inside [0, 300] into a fake 200s
            // duration. A 150s period should therefore be SINGLE under the effective
            // subrange, not OVERLAP under the hybrid parent range.
            var traj = new MockTrajectory().WithTimeRange(0, 300);
            traj.LoopStartUT = 100;
            traj.LoopEndUT = 200;

            double effectiveDuration = GhostPlaybackEngine.EffectiveLoopDuration(traj);
            double hybridDuration = traj.EndUT - GhostPlaybackEngine.EffectiveLoopStartUT(traj);

            const double period = 150.0;
            Assert.True(GhostPlaybackLogic.IsOverlapLoop(period, hybridDuration));
            Assert.False(GhostPlaybackLogic.IsOverlapLoop(period, effectiveDuration));
        }

        [Fact]
        public void EffectiveLoopDuration_SubrangeChangesOverlapCycleBoundsComparedToHybridRange()
        {
            // #411 regression guard: overlap cycle selection must stop at EffectiveLoopEndUT,
            // not raw EndUT, otherwise an expired older cycle lingers one extra period.
            var traj = new MockTrajectory().WithTimeRange(0, 300).WithLoop(80);
            traj.LoopStartUT = 100;
            traj.LoopEndUT = 200;

            long effectiveFirstCycle;
            long effectiveLastCycle;
            long hybridFirstCycle;
            long hybridLastCycle;

            double loopStartUT = GhostPlaybackEngine.EffectiveLoopStartUT(traj);
            GhostPlaybackLogic.GetActiveCycles(260, loopStartUT, GhostPlaybackEngine.EffectiveLoopEndUT(traj),
                traj.LoopIntervalSeconds, 10, out effectiveFirstCycle, out effectiveLastCycle);
            GhostPlaybackLogic.GetActiveCycles(260, loopStartUT, traj.EndUT,
                traj.LoopIntervalSeconds, 10, out hybridFirstCycle, out hybridLastCycle);

            Assert.Equal(1, effectiveFirstCycle);
            Assert.Equal(2, effectiveLastCycle);
            Assert.Equal(0, hybridFirstCycle);
            Assert.Equal(2, hybridLastCycle);
        }

        #endregion

        #region ShouldLoopPlayback with loop range

        [Fact]
        public void ShouldLoopPlayback_LoopRangeTooShort_ReturnsFalse()
        {
            // Full range is 100s but loop range is only 0.5s
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop();
            traj.LoopStartUT = 150;
            traj.LoopEndUT = 150.5;
            Assert.False(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        [Fact]
        public void ShouldLoopPlayback_ValidLoopRange_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop();
            traj.LoopStartUT = 120;
            traj.LoopEndUT = 180;
            Assert.True(GhostPlaybackEngine.ShouldLoopPlayback(traj));
        }

        #endregion

        // ===================================================================
        // TryComputeLoopPlaybackUT — instance, pure math
        // ===================================================================

        #region TryComputeLoopPlaybackUT

        [Fact]
        public void TryComputeLoopPlaybackUT_NullTrajectory_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            double loopUT;
            long cycleIndex;
            bool inPause;
            Assert.False(engine.TryComputeLoopPlaybackUT(null, 150, 10,
                out loopUT, out cycleIndex, out inPause));
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_BeforeStartUT_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop();
            double loopUT;
            long cycleIndex;
            bool inPause;
            Assert.False(engine.TryComputeLoopPlaybackUT(traj, 50, 10,
                out loopUT, out cycleIndex, out inPause));
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_TooShortDuration_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            // 0.5s duration — at or below MinLoopDurationSeconds
            var traj = new MockTrajectory().WithTimeRange(100, 100.5).WithLoop();
            double loopUT;
            long cycleIndex;
            bool inPause;
            Assert.False(engine.TryComputeLoopPlaybackUT(traj, 101, 10,
                out loopUT, out cycleIndex, out inPause));
        }

        // #381: duration=100, period=110 → cycleDuration=110, pause tail 10s.
        // This is the classic "period > duration" shape used in single-ghost loop tests.

        [Fact]
        public void TryComputeLoopPlaybackUT_PeriodLongerThanDuration_HasPauseWindow()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 205 => elapsed = 105, cycle 0, cycleTime = 105 > duration(100) → pause.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 205, 110,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(0, cycleIndex);
            Assert.True(inPause);
            Assert.Equal(200, loopUT, 6);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_JustPastBoundaryWithinEpsilon_StaysInPlayback()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            double loopUT;
            long cycleIndex;
            bool inPause;
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 200.0000005, 110,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(0, cycleIndex);
            Assert.False(inPause);
            Assert.Equal(200, loopUT, 6);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_PeriodEqualsDuration_NoPauseWindow()
        {
            var engine = new GhostPlaybackEngine(null);
            // Duration=100, period=100, cycleDuration=100, back-to-back cycles.
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(100);

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 250, elapsed = 150, cycle = 1, cycleTime = 50.
            // period > duration is false → never in pause window.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 250, 100,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(1, cycleIndex);
            Assert.False(inPause);
            Assert.Equal(150, loopUT, 6);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_PeriodShorterThanDuration_OverlapCycles()
        {
            var engine = new GhostPlaybackEngine(null);
            // Duration=100, period=30 → cycleDuration=30 (overlap via single-ghost math).
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(30);

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 160, elapsed = 60 → cycle=2, cycleTime=0. No pause.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 160, 30,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(2, cycleIndex);
            Assert.False(inPause);
            Assert.Equal(100, loopUT, 6);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_NegativeInterval_DefensivelyClamped_NoThrow()
        {
            var engine = new GhostPlaybackEngine(null);
            // #443: negative interval clamps to MinCycleDuration=5 via ResolveLoopInterval.
            // Engine must not throw.
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(-30);

            double loopUT;
            long cycleIndex;
            bool inPause;
            // The caller passes autoLoopIntervalSeconds=10 but traj.LoopTimeUnit=Sec so the
            // recording's own (negative) value is used, then clamped to MinCycleDuration=5.
            // At currentUT=205, resolved interval=5, cycleDuration=5, elapsed=105,
            // cycle=floor(105/5)=21, phase=0.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 205, 10,
                out loopUT, out cycleIndex, out inPause));
            Assert.False(inPause);
            Assert.Equal(100, loopUT, 6);
            Assert.Equal(21, cycleIndex);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_FirstCycle_MidTrajectory()
        {
            var engine = new GhostPlaybackEngine(null);
            // #381: period=110 > duration=100 (single-ghost loop with 10s pause tail).
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 150 => elapsed = 50, cycle 0, cycleTime = 50.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 150, 110,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(0, cycleIndex);
            Assert.False(inPause);
            Assert.Equal(150, loopUT, 6);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_SecondCycle_Start()
        {
            var engine = new GhostPlaybackEngine(null);
            // #381: period=110, cycleDuration=110.
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 210 => elapsed = 110 => cycle 1, cycleTime = 0.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 210, 110,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(1, cycleIndex);
            Assert.False(inPause);
            Assert.Equal(100, loopUT, 6); // starts at StartUT
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_PhaseOffset_ShiftsElapsed()
        {
            var engine = new GhostPlaybackEngine(null);
            // #381: period=110, cycleDuration=110.
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            // Add a phase offset of 55s for recording index 0
            engine.loopPhaseOffsets[0] = 55;

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 150, elapsed = 50 + offset 55 = 105. cycle 0, cycleTime = 105 > 100 → pause.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 150, 110,
                out loopUT, out cycleIndex, out inPause, 0));
            Assert.Equal(0, cycleIndex);
            Assert.True(inPause);

            // Verify phase offset was logged
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains("phase offset"));
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_NoPhaseOffset_NoLogOutput()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            logLines.Clear(); // clear engine creation log

            double loopUT;
            long cycleIndex;
            bool inPause;
            engine.TryComputeLoopPlaybackUT(traj, 150, 110,
                out loopUT, out cycleIndex, out inPause, 0);

            // No phase offset => no phase offset log line (only verbose rate-limited may appear)
            Assert.DoesNotContain(logLines, l => l.Contains("phase offset"));
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_CycleIndexNeverNegative()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            // Negative phase offset that could make elapsed negative
            engine.loopPhaseOffsets[0] = -1000;

            double loopUT;
            long cycleIndex;
            bool inPause;
            engine.TryComputeLoopPlaybackUT(traj, 150, 110,
                out loopUT, out cycleIndex, out inPause, 0);
            Assert.True(cycleIndex >= 0);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_RecIdxMinus1_SkipsPhaseOffset()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(110);

            // Even if phase offset exists for index 0, passing recIdx=-1 skips it
            engine.loopPhaseOffsets[0] = 1000;

            logLines.Clear();

            double loopUT;
            long cycleIndex;
            bool inPause;
            // Default recIdx = -1
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 150, 110,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(0, cycleIndex);
            Assert.False(inPause);
            // No phase offset log
            Assert.DoesNotContain(logLines, l => l.Contains("phase offset"));
        }

        #endregion

        #region TryComputeLoopPlaybackUT with loop range

        [Fact]
        public void TryComputeLoopPlaybackUT_WithLoopRange_StaysWithinRange()
        {
            var engine = new GhostPlaybackEngine(null);
            // Full range 100-200, loop range 130-170 (40s duration). #381: period=50 > duration.
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(50);
            traj.LoopStartUT = 130;
            traj.LoopEndUT = 170;

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 150 => elapsed from loopStart(130) = 20 => cycle 0, loopUT = 150
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 150, 50,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(0, cycleIndex);
            Assert.False(inPause);
            Assert.Equal(150, loopUT, 6);
            Assert.True(loopUT >= 130 && loopUT <= 170);
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_WithLoopRange_SecondCycle()
        {
            var engine = new GhostPlaybackEngine(null);
            // Loop range 130-170 (40s), #381 period=50 → cycleDuration=50.
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(50);
            traj.LoopStartUT = 130;
            traj.LoopEndUT = 170;

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 180 => elapsed from 130 = 50 => cycle 1, cycleTime = 0.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 180, 50,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(1, cycleIndex);
            Assert.False(inPause);
            Assert.Equal(130, loopUT, 6); // Back to loopStart
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_WithLoopRange_PauseWindow()
        {
            var engine = new GhostPlaybackEngine(null);
            // Loop range 130-170 (40s duration), #381 period=50 > duration, pause tail 10s.
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(50);
            traj.LoopStartUT = 130;
            traj.LoopEndUT = 170;

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 175 => elapsed from 130 = 45 => cycle 0, cycleTime = 45.
            // cycleTime (45) > duration (40) AND period (50) > duration → pause window.
            Assert.True(engine.TryComputeLoopPlaybackUT(traj, 175, 50,
                out loopUT, out cycleIndex, out inPause));
            Assert.Equal(0, cycleIndex);
            Assert.True(inPause);
            Assert.Equal(170, loopUT, 6); // Pause at loopEnd, NOT traj.EndUT (200)
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_WithLoopRange_BeforeLoopStart_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(50);
            traj.LoopStartUT = 130;
            traj.LoopEndUT = 170;

            double loopUT;
            long cycleIndex;
            bool inPause;
            // currentUT = 120 is before loopStart (130)
            Assert.False(engine.TryComputeLoopPlaybackUT(traj, 120, 50,
                out loopUT, out cycleIndex, out inPause));
        }

        [Fact]
        public void TryComputeLoopPlaybackUT_AutoQueueUsesCachedLaunchSchedule()
        {
            var engine = new GhostPlaybackEngine(null);
            var first = MakeAutoTrajectory("first", 100.0, 150.0);
            var second = MakeAutoTrajectory("second", 110.0, 160.0);
            var third = MakeAutoTrajectory("third", 120.0, 170.0);
            var trajectories = new List<IPlaybackTrajectory> { first, second, third };
            var rebuildScheduleCache = typeof(GhostPlaybackEngine).GetMethod(
                "RebuildAutoLoopLaunchScheduleCache",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(rebuildScheduleCache);
            rebuildScheduleCache.Invoke(engine, new object[] { trajectories, 30.0 });

            bool result = engine.TryComputeLoopPlaybackUT(
                second, 145.0, 30.0,
                out double loopUT,
                out long cycleIndex,
                out bool inPause,
                recIdx: 1);

            Assert.True(result);
            Assert.Equal(125.0, loopUT, 6);
            Assert.Equal(0, cycleIndex);
            Assert.False(inPause);
        }

        #endregion

        // ===================================================================
        // GetLoopIntervalSeconds — delegates to GhostPlaybackLogic
        // ===================================================================

        #region GetLoopIntervalSeconds

        [Fact]
        public void GetLoopIntervalSeconds_AutoMode_ReturnsGlobalValue()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(999, LoopTimeUnit.Auto);
            double result = engine.GetLoopIntervalSeconds(traj, 42.0);
            Assert.Equal(42.0, result);
        }

        [Fact]
        public void GetLoopIntervalSeconds_ManualMode_ReturnsRecordingValue()
        {
            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory().WithTimeRange(100, 200).WithLoop(25.0);
            double result = engine.GetLoopIntervalSeconds(traj, 42.0);
            Assert.Equal(25.0, result);
        }

        [Fact]
        public void GetLoopIntervalSeconds_AutoQueueUsesCachedCadence()
        {
            var engine = new GhostPlaybackEngine(null);
            var first = MakeAutoTrajectory("first", 100.0, 150.0);
            var second = MakeAutoTrajectory("second", 110.0, 160.0);
            var third = MakeAutoTrajectory("third", 120.0, 170.0);
            var trajectories = new List<IPlaybackTrajectory> { first, second, third };
            var rebuildScheduleCache = typeof(GhostPlaybackEngine).GetMethod(
                "RebuildAutoLoopLaunchScheduleCache",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(rebuildScheduleCache);
            rebuildScheduleCache.Invoke(engine, new object[] { trajectories, 30.0 });

            double result = engine.GetLoopIntervalSeconds(second, 30.0, recIdx: 1);

            Assert.Equal(90.0, result, 6);
        }

        [Fact]
        public void GetLoopIntervalSeconds_NullTrajectory_ReturnsDefault()
        {
            var engine = new GhostPlaybackEngine(null);
            double result = engine.GetLoopIntervalSeconds(null, 42.0);
            Assert.Equal(LoopTiming.DefaultLoopIntervalSeconds, result);
        }

        #endregion

        // ===================================================================
        // ReindexAfterDelete — verifies dictionary/set key shifting
        // ===================================================================

        #region ReindexAfterDelete

        [Fact]
        public void ReindexAfterDelete_ShiftsGhostStatesDown()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState();
            engine.ghostStates[2] = new GhostPlaybackState();
            engine.ghostStates[3] = new GhostPlaybackState();

            engine.ReindexAfterDelete(1);

            Assert.True(engine.ghostStates.ContainsKey(0));
            Assert.True(engine.ghostStates.ContainsKey(1));  // was 2, shifted
            Assert.True(engine.ghostStates.ContainsKey(2));  // was 3, shifted
            Assert.False(engine.ghostStates.ContainsKey(3)); // gone
        }

        [Fact]
        public void ReindexAfterDelete_ShiftsOverlapGhostsDown()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.overlapGhosts[1] = new List<GhostPlaybackState>();
            engine.overlapGhosts[3] = new List<GhostPlaybackState>();

            engine.ReindexAfterDelete(0);

            Assert.True(engine.overlapGhosts.ContainsKey(0));  // was 1
            Assert.True(engine.overlapGhosts.ContainsKey(2));  // was 3
            Assert.False(engine.overlapGhosts.ContainsKey(1)); // shifted
            Assert.False(engine.overlapGhosts.ContainsKey(3)); // shifted
        }

        [Fact]
        public void ReindexAfterDelete_ShiftsLoopPhaseOffsetsDown()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.loopPhaseOffsets[0] = 10.0;
            engine.loopPhaseOffsets[2] = 20.0;
            engine.loopPhaseOffsets[4] = 40.0;

            engine.ReindexAfterDelete(1);

            Assert.True(engine.loopPhaseOffsets.ContainsKey(0));
            Assert.Equal(10.0, engine.loopPhaseOffsets[0]);
            Assert.True(engine.loopPhaseOffsets.ContainsKey(1));  // was 2
            Assert.Equal(20.0, engine.loopPhaseOffsets[1]);
            Assert.True(engine.loopPhaseOffsets.ContainsKey(3));  // was 4
            Assert.Equal(40.0, engine.loopPhaseOffsets[3]);
        }

        [Fact]
        public void ReindexAfterDelete_ShiftsSetsDown()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.loggedGhostEnter.Add(0);
            engine.loggedGhostEnter.Add(2);
            engine.loggedGhostEnter.Add(3);

            engine.ReindexAfterDelete(1);

            Assert.Contains(0, engine.loggedGhostEnter);
            Assert.Contains(1, engine.loggedGhostEnter);  // was 2
            Assert.Contains(2, engine.loggedGhostEnter);  // was 3
            Assert.DoesNotContain(3, engine.loggedGhostEnter);
        }

        [Fact]
        public void ReindexAfterDelete_DropsRemovedIndexFromSets()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.loggedGhostEnter.Add(1);
            engine.loggedGhostEnter.Add(2);
            engine.loggedReshow.Add(1);

            engine.ReindexAfterDelete(1);

            // Index 1 was the removed index — should be dropped
            // Index 2 shifts to 1
            Assert.Contains(1, engine.loggedGhostEnter);  // was 2
            Assert.Single(engine.loggedGhostEnter);
            Assert.Empty(engine.loggedReshow); // only had 1, which was removed
        }

        [Fact]
        public void ReindexAfterDelete_AllSetsShifted()
        {
            var engine = new GhostPlaybackEngine(null);
            // Populate all sets that get reindexed
            engine.loggedGhostEnter.Add(5);
            engine.loggedReshow.Add(5);

            engine.ReindexAfterDelete(2);

            Assert.Contains(4, engine.loggedGhostEnter);
            Assert.Contains(4, engine.loggedReshow);
            Assert.DoesNotContain(5, engine.loggedGhostEnter);
            Assert.DoesNotContain(5, engine.loggedReshow);
        }

        [Fact]
        public void ReindexAfterDelete_LowerKeysUnaffected()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState();
            engine.ghostStates[1] = new GhostPlaybackState();
            engine.loggedGhostEnter.Add(0);
            engine.loggedGhostEnter.Add(1);

            engine.ReindexAfterDelete(5); // remove an index above all existing

            // Nothing should change
            Assert.True(engine.ghostStates.ContainsKey(0));
            Assert.True(engine.ghostStates.ContainsKey(1));
            Assert.Contains(0, engine.loggedGhostEnter);
            Assert.Contains(1, engine.loggedGhostEnter);
        }

        [Fact]
        public void ReindexAfterDelete_EmptyCollections_NoError()
        {
            var engine = new GhostPlaybackEngine(null);
            // All collections are empty — should not throw
            engine.ReindexAfterDelete(0);
            Assert.Empty(engine.ghostStates);
            Assert.Empty(engine.overlapGhosts);
            Assert.Empty(engine.loggedGhostEnter);
        }

        [Fact]
        public void ReindexAfterDelete_PreservesGhostStateIdentity()
        {
            var engine = new GhostPlaybackEngine(null);
            var state2 = new GhostPlaybackState { loopCycleIndex = 42 };
            var state3 = new GhostPlaybackState { loopCycleIndex = 99 };
            engine.ghostStates[2] = state2;
            engine.ghostStates[3] = state3;

            engine.ReindexAfterDelete(1);

            // Same object references, just moved
            Assert.Same(state2, engine.ghostStates[1]);
            Assert.Same(state3, engine.ghostStates[2]);
        }

        #endregion

        // ===================================================================
        // CaptureGhostObservability — pure aggregation over engine state
        // ===================================================================

        #region CaptureGhostObservability

        [Fact]
        public void CaptureGhostObservability_CountsPrimaryOverlapAndFxInstances()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                currentZone = RenderingZone.Physics,
                fidelityReduced = true,
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    [1] = BuildEngineGhostInfo(2),
                    [2] = BuildEngineGhostInfo(1)
                },
                rcsInfos = new Dictionary<ulong, RcsGhostInfo>
                {
                    [3] = BuildRcsGhostInfo(3)
                }
            };
            engine.ghostStates[1] = new GhostPlaybackState
            {
                currentZone = RenderingZone.Visual,
                simplified = true,
                rcsInfos = new Dictionary<ulong, RcsGhostInfo>
                {
                    [4] = BuildRcsGhostInfo(0),
                    [5] = BuildRcsGhostInfo(1)
                }
            };
            engine.overlapGhosts[0] = new List<GhostPlaybackState>
            {
                new GhostPlaybackState
                {
                    engineInfos = new Dictionary<ulong, EngineGhostInfo>
                    {
                        [6] = BuildEngineGhostInfo(4)
                    }
                }
            };

            GhostObservability result = engine.CaptureGhostObservability();

            Assert.Equal(0, result.activePrimaryGhostCount);
            Assert.Equal(0, result.activeOverlapGhostCount);
            Assert.Equal(0, result.zone1GhostCount);
            Assert.Equal(0, result.zone2GhostCount);
            Assert.Equal(0, result.softCapReducedCount);
            Assert.Equal(0, result.softCapSimplifiedCount);
            Assert.Equal(0, result.ghostsWithEngineFx);
            Assert.Equal(0, result.engineModuleCount);
            Assert.Equal(0, result.engineParticleSystemCount);
            Assert.Equal(0, result.ghostsWithRcsFx);
            Assert.Equal(0, result.rcsModuleCount);
            Assert.Equal(0, result.rcsParticleSystemCount);
        }

        [Fact]
        public void CaptureGhostObservability_IgnoresNullStatesAndEmptyFxMaps()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = null;
            engine.ghostStates[1] = new GhostPlaybackState
            {
                currentZone = RenderingZone.Beyond,
                engineInfos = new Dictionary<ulong, EngineGhostInfo>(),
                rcsInfos = null
            };
            engine.overlapGhosts[2] = new List<GhostPlaybackState> { null };

            GhostObservability result = engine.CaptureGhostObservability();

            Assert.Equal(0, result.activePrimaryGhostCount);
            Assert.Equal(0, result.activeOverlapGhostCount);
            Assert.Equal(0, result.zone1GhostCount);
            Assert.Equal(0, result.zone2GhostCount);
            Assert.Equal(0, result.ghostsWithEngineFx);
            Assert.Equal(0, result.engineModuleCount);
            Assert.Equal(0, result.engineParticleSystemCount);
            Assert.Equal(0, result.ghostsWithRcsFx);
            Assert.Equal(0, result.rcsModuleCount);
            Assert.Equal(0, result.rcsParticleSystemCount);
        }

        #endregion

        // ===================================================================
        // Ghost shell lifecycle helpers
        // ===================================================================

        #region GhostShellLifecycle

        [Fact]
        public void HasLoopCycleChanged_UnloadedShellSameCycle_ReturnsFalse()
        {
            var state = new GhostPlaybackState
            {
                loopCycleIndex = 12,
                ghost = null
            };

            Assert.False(GhostPlaybackEngine.HasLoopCycleChanged(state, 12));
        }

        [Fact]
        public void ClearLoadedVisualReferences_PreservesLogicalPlaybackState()
        {
            var state = new GhostPlaybackState
            {
                vesselName = "Test",
                playbackIndex = 17,
                partEventIndex = 9,
                loopCycleIndex = 4,
                flagEventIndex = 3,
                currentZone = RenderingZone.Beyond,
                lastDistance = 67890,
                lastRenderDistance = 2345,
                explosionFired = true,
                pauseHidden = true,
                fidelityReduced = true,
                distanceLodReduced = true,
                simplified = true,
                materials = new List<Material>(),
                partTree = new Dictionary<uint, List<uint>> { [1] = new List<uint> { 2, 3 } },
                logicalPartIds = new HashSet<uint> { 1, 2, 3 },
                engineInfos = new Dictionary<ulong, EngineGhostInfo> { [1] = new EngineGhostInfo() },
                rcsInfos = new Dictionary<ulong, RcsGhostInfo> { [2] = new RcsGhostInfo() },
                audioInfos = new Dictionary<ulong, AudioGhostInfo> { [3] = new AudioGhostInfo() },
                compoundPartInfos = new List<CompoundPartGhostInfo> { new CompoundPartGhostInfo() },
                fakeCanopies = new Dictionary<uint, GameObject>(),
                reentryFxInfo = new ReentryFxInfo()
            };

            state.ClearLoadedVisualReferences();

            Assert.Equal("Test", state.vesselName);
            Assert.Equal(17, state.playbackIndex);
            Assert.Equal(9, state.partEventIndex);
            Assert.Equal(4, state.loopCycleIndex);
            Assert.Equal(3, state.flagEventIndex);
            Assert.Equal(RenderingZone.Beyond, state.currentZone);
            Assert.Equal(67890, state.lastDistance);
            Assert.Equal(2345, state.lastRenderDistance);
            Assert.True(state.explosionFired);
            Assert.NotNull(state.partTree);
            Assert.NotNull(state.logicalPartIds);
            Assert.Contains(2u, state.logicalPartIds);
            Assert.Null(state.materials);
            Assert.Null(state.engineInfos);
            Assert.Null(state.rcsInfos);
            Assert.Null(state.audioInfos);
            Assert.Null(state.compoundPartInfos);
            Assert.Null(state.fakeCanopies);
            Assert.Null(state.reentryFxInfo);
            Assert.False(state.pauseHidden);
            Assert.False(state.fidelityReduced);
            Assert.False(state.distanceLodReduced);
            Assert.False(state.simplified);
        }

        [Fact]
        public void ClearLoadedVisualReferences_ResetsPendingSplitBuildState()
        {
            // Bug #450 B2: unloading / rebuild paths must clear any partial snapshot-build
            // state and deferred lifecycle payload. Otherwise a later rehydrate can inherit
            // a stale pending root or emit the wrong OnGhostCreated / camera-retarget event.
            var state = new GhostPlaybackState
            {
                pendingVisualBuild = new PendingGhostVisualBuild
                {
                    rootName = "Parsek_Timeline_7",
                    nextPartIndex = 3,
                    buildType = HeaviestSpawnBuildType.VesselSnapshot,
                    hasLoggedSplitYield = true,
                },
                pendingSpawnLifecycle = PendingSpawnLifecycle.OverlapPrimaryEnter,
                pendingSpawnFlags = new TrajectoryPlaybackFlags
                {
                    needsSpawn = true,
                    chainEndUT = 123.45,
                    recordingId = "rec-7",
                },
            };

            state.ClearLoadedVisualReferences();

            Assert.Null(state.pendingVisualBuild);
            Assert.Equal(PendingSpawnLifecycle.None, state.pendingSpawnLifecycle);
            Assert.False(state.pendingSpawnFlags.needsSpawn);
            Assert.Equal(0.0, state.pendingSpawnFlags.chainEndUT);
            Assert.Null(state.pendingSpawnFlags.recordingId);
        }

        [Fact]
        public void ReusePrimaryGhostAcrossCycle_NullGhost_AdvancesCycleWithoutEvents()
        {
            // Bug #450 B2 (pure-logic counterpart to the Unity-gated pending
            // loop-cycle boundary regression): when the
            // reuse path is entered on a state whose ghost has never materialised
            // (pending split-build), it must advance loopCycleIndex and emit
            // zero restart / camera events for a ghost that never spawned.
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory
            {
                IsDebris = true,
                VesselName = "PendingLoop",
                RecordingId = "rec-loop",
            }.WithTimeRange(100, 200).WithLoop(150);
            var state = new GhostPlaybackState
            {
                vesselName = "PendingLoop",
                ghost = null,
                loopCycleIndex = 0,
                pendingSpawnLifecycle = PendingSpawnLifecycle.LoopEnter,
                pendingSpawnFlags = new TrajectoryPlaybackFlags { recordingId = "rec-loop" },
            };

            var cameraEvents = new List<CameraActionEvent>();
            var restartedEvents = new List<LoopRestartedEvent>();
            engine.OnLoopCameraAction += evt => cameraEvents.Add(evt);
            engine.OnLoopRestarted += evt => restartedEvents.Add(evt);

            engine.ReusePrimaryGhostAcrossCycle(
                index: 4, traj, flags: default, state,
                playbackUT: 160, newCycleIndex: 1);

            Assert.Empty(cameraEvents);
            Assert.Empty(restartedEvents);
            Assert.Equal(1L, state.loopCycleIndex);
            Assert.Equal(PendingSpawnLifecycle.LoopEnter, state.pendingSpawnLifecycle);
        }

        [Fact]
        public void UpdateOverlapPlayback_PendingPrimaryDemotion_ClearsLifecycleBeforeOverlapList()
        {
            // Bug #450 B2: when an overlap primary is demoted before its split build
            // completes, it must move into the overlap list as a quiet shell rather than
            // later finalizing as a fresh OverlapPrimaryEnter.
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = new MockTrajectory
            {
                IsDebris = true, // keep the replacement primary on the early-fail path in xUnit
                VesselName = "PendingOverlap",
                RecordingId = "rec-overlap",
            }.WithTimeRange(100, 200).WithLoop(30);
            var primaryState = new GhostPlaybackState
            {
                vesselName = "PendingOverlap",
                loopCycleIndex = 1,
                pendingSpawnLifecycle = PendingSpawnLifecycle.OverlapPrimaryEnter,
                pendingSpawnFlags = new TrajectoryPlaybackFlags
                {
                    needsSpawn = true,
                    chainEndUT = 321.0,
                    recordingId = "rec-overlap",
                },
            };
            engine.ghostStates[5] = primaryState;

            var overlapCameraEvents = new List<CameraActionEvent>();
            engine.OnOverlapCameraAction += evt => overlapCameraEvents.Add(evt);

            engine.UpdateOverlapPlaybackForTesting(
                index: 5,
                traj,
                flags: default,
                ctx: new FrameContext
                {
                    currentUT = 160,
                    warpRate = 1f,
                    activeVesselPos = new Vector3d(0, 0, 0),
                    protectedIndex = -1,
                    protectedLoopCycleIndex = -1,
                    autoLoopIntervalSeconds = 30,
                },
                primaryState,
                suppressVisualFx: false);

            Assert.Empty(overlapCameraEvents);
            Assert.False(engine.ghostStates.ContainsKey(5));
            Assert.True(engine.TryGetOverlapGhosts(5, out var overlaps));
            Assert.Single(overlaps);
            Assert.Same(primaryState, overlaps[0]);
            Assert.Equal(PendingSpawnLifecycle.None, primaryState.pendingSpawnLifecycle);
            Assert.False(primaryState.pendingSpawnFlags.needsSpawn);
            Assert.Equal(0.0, primaryState.pendingSpawnFlags.chainEndUT);
            Assert.Null(primaryState.pendingSpawnFlags.recordingId);
        }

        [Fact]
        public void UpdateOverlapPlayback_HighWarpStationaryOverlap_KeepsPrimaryAndClearsOverlaps()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = new MockTrajectory
            {
                IsDebris = true, // keep xUnit on the no-Unity-GameObject path
                VesselName = "StationaryOverlap",
                RecordingId = "rec-stationary-overlap",
            }.WithTimeRange(100, 200).WithLoop(30);
            traj.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.SurfaceStationary,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100,
                endUT = 200,
                frames = traj.Points,
                source = TrackSectionSource.Active,
            });

            var primaryState = new GhostPlaybackState
            {
                vesselName = "StationaryOverlap",
                loopCycleIndex = 2,
            };
            engine.ghostStates[5] = primaryState;
            engine.overlapGhosts[5] = new List<GhostPlaybackState>
            {
                null
            };

            engine.UpdateOverlapPlaybackForTesting(
                index: 5,
                traj,
                flags: default,
                ctx: new FrameContext
                {
                    currentUT = 160,
                    warpRate = 100f,
                    activeVesselPos = new Vector3d(0, 0, 0),
                    protectedIndex = -1,
                    protectedLoopCycleIndex = -1,
                    autoLoopIntervalSeconds = 30,
                },
                primaryState,
                suppressVisualFx: true,
                suppressOverlapGhosts: true,
                stopAfterSuppressOverlapGhosts: true);

            Assert.True(engine.TryGetGhostState(5, out var keptPrimary));
            Assert.Same(primaryState, keptPrimary);
            Assert.True(engine.TryGetOverlapGhosts(5, out var overlaps));
            Assert.Empty(overlaps);
            Assert.Equal(0, engine.FrameOverlapGhostIterationCountForTesting);
        }

        [Fact]
        public void ShouldPrewarmHiddenGhost_NearVisibleTierBoundary_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            var state = new GhostPlaybackState { partEventIndex = 0 };

            bool result = GhostPlaybackEngine.ShouldPrewarmHiddenGhost(
                traj, state,
                DistanceThresholds.GhostFlight.LoopSimplifiedMeters + 1000,
                currentUT: 120);

            Assert.True(result);
        }

        [Fact]
        public void ShouldPrewarmHiddenGhost_UpcomingDecoupleEvent_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.PartEvents.Add(new PartEvent
            {
                ut = 121.5,
                eventType = PartEventType.Decoupled
            });
            var state = new GhostPlaybackState { partEventIndex = 0 };

            bool result = GhostPlaybackEngine.ShouldPrewarmHiddenGhost(
                traj, state,
                DistanceThresholds.GhostFlight.LoopSimplifiedMeters + 20000,
                currentUT: 120);

            Assert.True(result);
        }

        [Fact]
        public void ShouldPrewarmHiddenGhost_UpcomingThrottleOnlyEvent_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100, 200);
            traj.PartEvents.Add(new PartEvent
            {
                ut = 121.5,
                eventType = PartEventType.EngineThrottle
            });
            var state = new GhostPlaybackState { partEventIndex = 0 };

            bool result = GhostPlaybackEngine.ShouldPrewarmHiddenGhost(
                traj, state,
                DistanceThresholds.GhostFlight.LoopSimplifiedMeters + 20000,
                currentUT: 120);

            Assert.False(result);
        }

        #endregion

        // ===================================================================
        // Query API — HasGhost, HasActiveGhost, IsGhostOnBody, etc.
        // ===================================================================

        #region PendingPlaybackMetadata

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_PointInterpolation_UsesInterpolatedState()
        {
            logLines.Clear();

            var traj = new MockTrajectory
            {
                VesselName = "CrossBodyGhost",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1000,
                        velocity = new Vector3(10f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Mun",
                        altitude = 3000,
                        velocity = new Vector3(30f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 105.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Mun", result.bodyName);
            Assert.Equal(2000.0, result.altitude);
            Assert.Equal(new Vector3(20f, 0f, 0f), result.velocity);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("CrossBodyGhost")
                && l.Contains("cross-body point transition Kerbin->Mun")
                && l.Contains("body='Mun'"));
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_BeforeStart_UsesFirstPointState()
        {
            var traj = new MockTrajectory
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1500,
                        velocity = new Vector3(7f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Kerbin",
                        altitude = 1700,
                        velocity = new Vector3(9f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 90.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Kerbin", result.bodyName);
            Assert.Equal(1500.0, result.altitude);
            Assert.Equal(new Vector3(7f, 0f, 0f), result.velocity);
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_AfterEnd_UsesLastPointStateAndLogsClamp()
        {
            logLines.Clear();

            var traj = new MockTrajectory
            {
                VesselName = "AfterEndGhost",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1500,
                        velocity = new Vector3(7f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Mun",
                        altitude = 1700,
                        velocity = new Vector3(9f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 120.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Mun", result.bodyName);
            Assert.Equal(1700.0, result.altitude);
            Assert.Equal(new Vector3(9f, 0f, 0f), result.velocity);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("AfterEndGhost")
                && l.Contains("resolved from point after-end clamp")
                && l.Contains("body='Mun'"));
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_OrbitOnlyTrajectory_UsesSegmentBody()
        {
            var traj = new MockTrajectory
            {
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Minmus",
                        semiMajorAxis = 800000,
                        startUT = 100,
                        endUT = 200
                    }
                },
                EndUTOverride = 200
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 150.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Minmus", result.bodyName);
            Assert.Equal(0.0, result.altitude);
            Assert.Equal(Vector3.zero, result.velocity);
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_MixedPointAndOrbitData_PrefersActiveOrbitSegment()
        {
            GhostPlaybackEngine.PendingOrbitBodyRadiusResolverForTesting = _ => 600000;

            var traj = new MockTrajectory
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1000,
                        velocity = new Vector3(5f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Kerbin",
                        altitude = 2000,
                        velocity = new Vector3(15f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        semiMajorAxis = 800000,
                        startUT = 100,
                        endUT = 200
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 105.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Mun", result.bodyName);
            Assert.Equal(0.0, result.altitude);
            Assert.Equal(Vector3.zero, result.velocity);
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_SubSurfaceMixedOrbitSegment_FallsBackToPoints()
        {
            GhostPlaybackEngine.PendingOrbitBodyRadiusResolverForTesting = _ => 600000;

            var traj = new MockTrajectory
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1000,
                        velocity = new Vector3(5f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Kerbin",
                        altitude = 2000,
                        velocity = new Vector3(15f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        semiMajorAxis = 100000,
                        startUT = 100,
                        endUT = 200
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 105.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Kerbin", result.bodyName);
            Assert.Equal(1500.0, result.altitude);
            Assert.Equal(new Vector3(10f, 0f, 0f), result.velocity);
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_SurfaceTrackSection_SkipsOrbitSegmentPrecedence()
        {
            logLines.Clear();
            GhostPlaybackEngine.PendingOrbitBodyRadiusResolverForTesting = _ => 600000;

            var traj = new MockTrajectory
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1000,
                        velocity = new Vector3(5f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Kerbin",
                        altitude = 2000,
                        velocity = new Vector3(15f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        semiMajorAxis = 800000,
                        startUT = 100,
                        endUT = 200
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.SurfaceMobile,
                        startUT = 100,
                        endUT = 110
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 105.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Kerbin", result.bodyName);
            Assert.Equal(1500.0, result.altitude);
            Assert.Equal(new Vector3(10f, 0f, 0f), result.velocity);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("surface track section active, skipping orbit precedence"));
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_SurfaceTrackSection_OffUtOrbitSegment_DoesNotLogSkippedOrbitPrecedence()
        {
            logLines.Clear();

            var traj = new MockTrajectory
            {
                VesselName = "SurfaceOnlyGhost",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1000,
                        velocity = new Vector3(5f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Kerbin",
                        altitude = 2000,
                        velocity = new Vector3(15f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        semiMajorAxis = 800000,
                        startUT = 200,
                        endUT = 300
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.SurfaceMobile,
                        startUT = 100,
                        endUT = 110
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 105.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Kerbin", result.bodyName);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("SurfaceOnlyGhost")
                && l.Contains("surface track section active, skipping orbit precedence"));
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_SurfaceTrackSection_SubSurfaceOrbitSegment_DoesNotLogSkippedOrbitPrecedence()
        {
            logLines.Clear();
            GhostPlaybackEngine.PendingOrbitBodyRadiusResolverForTesting = _ => 600000;

            var traj = new MockTrajectory
            {
                VesselName = "SubSurfaceSurfaceGhost",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1000,
                        velocity = new Vector3(5f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Kerbin",
                        altitude = 2000,
                        velocity = new Vector3(15f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                },
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        semiMajorAxis = 100000,
                        startUT = 100,
                        endUT = 200
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        environment = SegmentEnvironment.SurfaceMobile,
                        startUT = 100,
                        endUT = 110
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 105.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Kerbin", result.bodyName);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("SubSurfaceSurfaceGhost")
                && l.Contains("surface track section active, skipping orbit precedence"));
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_SurfaceOnlyTrajectory_UsesSurfaceBody()
        {
            var traj = new MockTrajectory
            {
                SurfacePos = new SurfacePosition
                {
                    body = "Duna",
                    altitude = 42
                },
                EndUTOverride = 100
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 100.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Duna", result.bodyName);
            Assert.Equal(42.0, result.altitude);
            Assert.Equal(Vector3.zero, result.velocity);
        }

        [Fact]
        public void CreatePendingSpawnState_ResolvedInterpolation_SeedsStateAndLogs()
        {
            logLines.Clear();

            var engine = new GhostPlaybackEngine(null);
            var traj = new MockTrajectory
            {
                VesselName = "SeededGhost",
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100,
                        bodyName = "Kerbin",
                        altitude = 1000,
                        velocity = new Vector3(5f, 0f, 0f),
                        rotation = Quaternion.identity
                    },
                    new TrajectoryPoint
                    {
                        ut = 110,
                        bodyName = "Kerbin",
                        altitude = 2000,
                        velocity = new Vector3(15f, 0f, 0f),
                        rotation = Quaternion.identity
                    }
                }
            };

            GhostPlaybackState state = InvokeCreatePendingSpawnState(
                engine,
                traj,
                playbackUT: 105.0,
                PendingSpawnLifecycle.StandardEnter,
                default(TrajectoryPlaybackFlags));

            Assert.Equal("Kerbin", state.lastInterpolatedBodyName);
            Assert.Equal(1500.0, state.lastInterpolatedAltitude);
            Assert.Equal(new Vector3(10f, 0f, 0f), state.lastInterpolatedVelocity);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("SeededGhost")
                && l.Contains("resolved from point interpolation")
                && l.Contains("body='Kerbin'"));
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("Pending spawn interpolation seed")
                && l.Contains("SeededGhost")
                && l.Contains("lifecycle=StandardEnter")
                && l.Contains("body='Kerbin'"));
        }

        #endregion

        #region QueryAPI

        [Fact]
        public void HasGhost_NoState_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            Assert.False(engine.HasGhost(0));
        }

        [Fact]
        public void HasGhost_WithState_ReturnsTrue()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState();
            Assert.True(engine.HasGhost(0));
        }

        [Fact]
        public void HasActiveGhost_NullGhostGameObject_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState { ghost = null };
            Assert.False(engine.HasActiveGhost(0));
        }

        [Fact]
        public void TryGetGhostState_ExistingIndex_ReturnsState()
        {
            var engine = new GhostPlaybackEngine(null);
            var state = new GhostPlaybackState { loopCycleIndex = 7 };
            engine.ghostStates[3] = state;

            GhostPlaybackState result;
            Assert.True(engine.TryGetGhostState(3, out result));
            Assert.Same(state, result);
        }

        [Fact]
        public void TryGetGhostState_MissingIndex_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            GhostPlaybackState result;
            Assert.False(engine.TryGetGhostState(99, out result));
        }

        [Fact]
        public void IsGhostOnBody_MatchingBody_ReturnsTrue()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                lastInterpolatedBodyName = "Kerbin"
            };
            Assert.True(engine.IsGhostOnBody(0, "Kerbin"));
        }

        [Fact]
        public void IsGhostOnBody_DifferentBody_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                lastInterpolatedBodyName = "Mun"
            };
            Assert.False(engine.IsGhostOnBody(0, "Kerbin"));
        }

        [Fact]
        public void IsGhostOnBody_NullBodyName_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState();
            Assert.False(engine.IsGhostOnBody(0, null));
        }

        [Fact]
        public void IsGhostOnBody_NoState_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            Assert.False(engine.IsGhostOnBody(0, "Kerbin"));
        }

        [Fact]
        public void GetGhostBodyName_ExistingState_ReturnsBody()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                lastInterpolatedBodyName = "Duna"
            };
            Assert.Equal("Duna", engine.GetGhostBodyName(0));
        }

        [Fact]
        public void GetGhostBodyName_NoState_ReturnsNull()
        {
            var engine = new GhostPlaybackEngine(null);
            Assert.Null(engine.GetGhostBodyName(0));
        }

        [Fact]
        public void IsGhostWithinVisualRange_BeyondZone_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                currentZone = RenderingZone.Beyond
            };
            Assert.False(engine.IsGhostWithinVisualRange(0));
        }

        [Fact]
        public void IsGhostWithinVisualRange_PhysicsZone_ReturnsTrue()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                currentZone = RenderingZone.Physics
            };
            Assert.True(engine.IsGhostWithinVisualRange(0));
        }

        [Fact]
        public void IsGhostWithinVisualRange_VisualZone_ReturnsTrue()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState
            {
                currentZone = RenderingZone.Visual
            };
            Assert.True(engine.IsGhostWithinVisualRange(0));
        }

        [Fact]
        public void IsGhostWithinVisualRange_NoState_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            Assert.False(engine.IsGhostWithinVisualRange(99));
        }

        [Fact]
        public void GhostCount_ReflectsLoadedGhostVisualCount()
        {
            var engine = new GhostPlaybackEngine(null);
            Assert.Equal(0, engine.GhostCount);
            engine.ghostStates[0] = new GhostPlaybackState();
            engine.ghostStates[3] = new GhostPlaybackState();
            Assert.Equal(0, engine.GhostCount);
        }

        [Fact]
        public void CollectDeferredEnginePowerRestores_ReturnsOnlyActiveEngineModules()
        {
            ulong activeKey = FlightRecorder.EncodeEngineKey(42u, 0);
            ulong inactiveKey = FlightRecorder.EncodeEngineKey(99u, 1);
            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    [activeKey] = new EngineGhostInfo
                    {
                        partPersistentId = 42u,
                        moduleIndex = 0,
                        currentPower = 0.75f
                    },
                    [inactiveKey] = new EngineGhostInfo
                    {
                        partPersistentId = 99u,
                        moduleIndex = 1,
                        currentPower = 0f
                    }
                }
            };

            var restores = GhostPlaybackLogic.CollectDeferredEnginePowerRestores(state);

            Assert.Single(restores);
            Assert.Equal(activeKey, restores[0].key);
            Assert.Equal(0.75f, restores[0].power);
        }

        [Fact]
        public void CollectDeferredRuntimePowerRestores_SeparatesEngineRcsAndAudioTrackedPower()
        {
            ulong engineKey = FlightRecorder.EncodeEngineKey(42u, 0);
            ulong rcsKey = FlightRecorder.EncodeEngineKey(77u, 1);
            var engineInfo = new EngineGhostInfo
            {
                partPersistentId = 42u,
                moduleIndex = 0,
                currentPower = 0.75f
            };
            var rcsInfo = new RcsGhostInfo
            {
                partPersistentId = 77u,
                moduleIndex = 1,
                currentPower = 0.35f
            };
            var audioInfo = new AudioGhostInfo
            {
                partPersistentId = 42u,
                moduleIndex = 0,
                currentPower = 0.75f
            };
            var state = new GhostPlaybackState
            {
                atmosphereFactor = 1f,
                engineInfos = new Dictionary<ulong, EngineGhostInfo> { [engineKey] = engineInfo },
                rcsInfos = new Dictionary<ulong, RcsGhostInfo> { [rcsKey] = rcsInfo },
                audioInfos = new Dictionary<ulong, AudioGhostInfo> { [engineKey] = audioInfo }
            };

            var engineRestores = GhostPlaybackLogic.CollectDeferredEnginePowerRestores(state);
            var rcsRestores = GhostPlaybackLogic.CollectDeferredRcsPowerRestores(state);
            var audioRestores = GhostPlaybackLogic.CollectDeferredAudioPowerRestores(state);

            Assert.Single(engineRestores);
            Assert.Equal((engineKey, 0.75f), engineRestores[0]);
            Assert.Single(rcsRestores);
            Assert.Equal((rcsKey, 0.35f), rcsRestores[0]);
            Assert.Single(audioRestores);
            Assert.Equal((engineKey, 0.75f), audioRestores[0]);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        public void ShouldRestoreDeferredRuntimeFxState_RequiresFirstActivationAndUnsuppressedFx(
            bool activatedDeferredState, bool suppressVisualFx, bool expected)
        {
            bool shouldRestore = GhostPlaybackEngine.ShouldRestoreDeferredRuntimeFxState(
                activatedDeferredState, suppressVisualFx);

            Assert.Equal(expected, shouldRestore);
        }

        [Fact]
        public void HiddenPrimeVisualPolicy_SuppressesFxAndTransientEvents()
        {
            var policy = GhostPlaybackEngine.HiddenPrimeVisualPolicy();

            Assert.False(policy.skipPartEvents);
            Assert.True(policy.suppressVisualFx);
            Assert.False(policy.allowTransientEffects);
        }

        [Fact]
        public void ShouldSuppressLazyReentryUntilPlaybackSync_BlocksPendingBuildBeforeFirstSyncedFrame()
        {
            var state = new GhostPlaybackState
            {
                reentryFxPendingBuild = true,
                reentryFxInfo = null,
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldSuppressLazyReentryUntilPlaybackSync(state));
        }

        [Theory]
        [InlineData(false, true, 0, false)]
        [InlineData(true, false, 0, false)]
        [InlineData(true, true, 1, false)]
        public void ShouldSuppressLazyReentryUntilPlaybackSync_AllowsAfterSyncOrWhenNotPending(
            bool pendingBuild,
            bool deferredVisibility,
            int appearanceCount,
            bool expected)
        {
            var state = new GhostPlaybackState
            {
                reentryFxPendingBuild = pendingBuild,
                reentryFxInfo = null,
                deferVisibilityUntilPlaybackSync = deferredVisibility,
                appearanceCount = appearanceCount
            };

            Assert.Equal(expected,
                GhostPlaybackEngine.ShouldSuppressLazyReentryUntilPlaybackSync(state));
        }

        [Theory]
        [InlineData(842.4, "842m")]
        [InlineData(double.MaxValue, "unresolved")]
        [InlineData(double.NaN, "unresolved")]
        [InlineData(double.PositiveInfinity, "unresolved")]
        [InlineData(-1.0, "unresolved")]
        public void FormatPlaybackDistanceForLog_FormatsFiniteAndUnresolved(
            double distanceMeters, string expected)
        {
            Assert.Equal(expected,
                GhostPlaybackEngine.FormatPlaybackDistanceForLog(distanceMeters));
        }

        [Fact]
        public void ClearTrackedEnginePowerForPart_ClearsTrackedCurrentPower()
        {
            var info = new EngineGhostInfo
            {
                partPersistentId = 42u,
                moduleIndex = 0,
                currentPower = 0.75f
            };
            var state = new GhostPlaybackState
            {
                engineInfos = new Dictionary<ulong, EngineGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(42u, 0)] = info
                }
            };

            GhostPlaybackLogic.ClearTrackedEnginePowerForPart(state, 42u);

            Assert.Equal(0f, info.currentPower);
        }

        [Fact]
        public void ClearTrackedRcsPowerForPart_ClearsTrackedCurrentPower()
        {
            var info = new RcsGhostInfo
            {
                partPersistentId = 77u,
                moduleIndex = 1,
                currentPower = 0.35f
            };
            var state = new GhostPlaybackState
            {
                rcsInfos = new Dictionary<ulong, RcsGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(77u, 1)] = info
                }
            };

            GhostPlaybackLogic.ClearTrackedRcsPowerForPart(state, 77u);

            Assert.Equal(0f, info.currentPower);
        }

        [Fact]
        public void ClearTrackedAudioPowerForPart_ClearsTrackedCurrentPower()
        {
            var info = new AudioGhostInfo
            {
                partPersistentId = 42u,
                moduleIndex = 0,
                currentPower = 0.75f
            };
            var state = new GhostPlaybackState
            {
                audioInfos = new Dictionary<ulong, AudioGhostInfo>
                {
                    [FlightRecorder.EncodeEngineKey(42u, 0)] = info
                }
            };

            GhostPlaybackLogic.ClearTrackedAudioPowerForPart(state, 42u);

            Assert.Equal(0f, info.currentPower);
        }

        [Fact]
        public void ResolveVisiblePlaybackUT_ClampsFreshFirstFrameBackToActivationStart()
        {
            var traj = new MockTrajectory().WithTimeRange(217.97, 261.41);
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            double visibleUT = GhostPlaybackEngine.ResolveVisiblePlaybackUT(traj, state, 217.98);

            Assert.Equal(217.97, visibleUT, 2);
        }

        [Fact]
        public void ResolveVisiblePlaybackUT_DoesNotRewindLargeLateFirstAppearance()
        {
            var traj = new MockTrajectory().WithTimeRange(217.97, 261.41);
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            double visibleUT = GhostPlaybackEngine.ResolveVisiblePlaybackUT(traj, state, 218.30);

            Assert.Equal(218.30, visibleUT, 2);
        }

        [Fact]
        public void ResolveVisiblePlaybackUT_DoesNotRewindReshownGhost()
        {
            var traj = new MockTrajectory().WithTimeRange(217.97, 261.41);
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 1
            };

            double visibleUT = GhostPlaybackEngine.ResolveVisiblePlaybackUT(traj, state, 217.98);

            Assert.Equal(217.98, visibleUT, 2);
        }

        [Fact]
        public void HasRenderableGhostData_SinglePoint_ReturnsTrue()
        {
            var traj = new MockTrajectory();
            traj.Points.Add(new TrajectoryPoint
            {
                ut = 100,
                latitude = 0,
                longitude = 0,
                altitude = 0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero
            });

            Assert.True(GhostPlaybackEngine.HasRenderableGhostData(traj));
        }

        [Fact]
        public void CachePlaybackDistances_CachesSeparateActiveVesselAndRenderDistances()
        {
            var state = new GhostPlaybackState
            {
                lastDistance = 1.0,
                lastRenderDistance = 2.0
            };

            GhostPlaybackEngine.CachePlaybackDistances(state, 18600.0, 50.0);

            Assert.Equal(18600.0, state.lastDistance);
            Assert.Equal(50.0, state.lastRenderDistance);
        }

        #endregion

        // ===================================================================
        // Anchor vessel lifecycle
        // ===================================================================

        #region AnchorVesselLifecycle

        [Fact]
        public void OnAnchorVesselLoaded_AddsToSet()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.OnAnchorVesselLoaded(12345);
            Assert.Contains(12345u, engine.loadedAnchorVessels);
        }

        [Fact]
        public void OnAnchorVesselUnloaded_RemovesFromSet()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.OnAnchorVesselLoaded(12345);
            engine.OnAnchorVesselUnloaded(12345);
            Assert.DoesNotContain(12345u, engine.loadedAnchorVessels);
        }

        [Fact]
        public void OnAnchorVesselUnloaded_NonexistentId_NoError()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.OnAnchorVesselUnloaded(99999); // should not throw
            Assert.Empty(engine.loadedAnchorVessels);
        }

        #endregion

        // ===================================================================
        // DestroyAllGhosts — cannot be tested outside Unity
        // (DestroyGhostResources calls UnityEngine.Object.Destroy which
        // is an ECall and unavailable in the test runner). Tested via
        // in-game KSP.log validation instead.
        // ===================================================================

        #region DestroyAllGhosts_StateOnly

        // These tests verify pre-conditions of DestroyAllGhosts without
        // actually calling it (which would trigger Unity ECall).

        #endregion

        // ===================================================================
        // Engine creation logging
        // ===================================================================

        #region EngineCreation

        [Fact]
        public void Constructor_LogsCreation()
        {
            logLines.Clear();
            var engine = new GhostPlaybackEngine(null);
            Assert.Contains(logLines, l => l.Contains("[Engine]") && l.Contains("GhostPlaybackEngine created"));
        }

        #endregion

        // ===================================================================
        // Interface isolation — engine methods accept MockTrajectory
        // ===================================================================

        #region InterfaceIsolation

        #endregion

        // ===================================================================
        // UpdatePlayback early-exit guards
        // NOTE: UpdatePlayback internally references GhostPlaybackLogic methods
        // that use FrameContext with Vector3d (KSP struct). The FrameContext
        // default constructor triggers ECall in the test runner, so we cannot
        // call UpdatePlayback directly. The guard logic is tested via the
        // pure static methods it delegates to (ShouldLoopPlayback, etc.).
        // ===================================================================

        #region UpdatePlaybackGuards

        // Guard logic tests removed — they tested tautologies about test setup,
        // not production code. Guard behavior verified via in-game testing.

        #endregion

        // ===================================================================
        // Constants sanity checks
        // ===================================================================

        #region Constants

        [Fact]
        public void MaxOverlapGhostsPerRecording_IsReasonable()
        {
            Assert.True(GhostPlayback.MaxOverlapGhostsPerRecording > 0);
            Assert.True(GhostPlayback.MaxOverlapGhostsPerRecording <= 20);
        }

        [Fact]
        public void OverlapExplosionHoldSeconds_IsPositive()
        {
            Assert.True(GhostPlayback.OverlapExplosionHoldSeconds > 0);
        }

        #endregion

        // ===================================================================
        // TryGetOverlapGhosts
        // ===================================================================

        #region TryGetOverlapGhosts

        [Fact]
        public void TryGetOverlapGhosts_NoOverlaps_ReturnsFalse()
        {
            var engine = new GhostPlaybackEngine(null);
            List<GhostPlaybackState> overlaps;
            Assert.False(engine.TryGetOverlapGhosts(0, out overlaps));
        }

        [Fact]
        public void TryGetOverlapGhosts_WithOverlaps_ReturnsList()
        {
            var engine = new GhostPlaybackEngine(null);
            var list = new List<GhostPlaybackState> { new GhostPlaybackState() };
            engine.overlapGhosts[0] = list;

            List<GhostPlaybackState> overlaps;
            Assert.True(engine.TryGetOverlapGhosts(0, out overlaps));
            Assert.Same(list, overlaps);
        }

        #endregion

        // ===================================================================
        // Dispose — calls DestroyAllGhosts which uses Unity ECall.
        // Cannot be tested outside Unity. Tested via in-game validation.
        // ===================================================================

        #region Dispose

        [Fact]
        public void Dispose_EmptyEngine_NoError()
        {
            // Dispose on an engine with no ghosts should work even outside Unity
            // because DestroyGhostResources is never called when ghostStates is empty
            var engine = new GhostPlaybackEngine(null);
            logLines.Clear();
            engine.Dispose();
            Assert.Contains(logLines, l => l.Contains("GhostPlaybackEngine disposed"));
        }

        #endregion
    }
}
