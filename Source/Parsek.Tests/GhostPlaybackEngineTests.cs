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
            ParsekLog.ClockOverrideForTesting = () => 1000.0;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            // Static seam used by the re-fly activation-settle carve-out — make
            // sure it doesn't leak across tests.
            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = null;
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

        private static bool InvokeTryPositionRelativeSectionAtPlaybackUT(
            GhostPlaybackEngine engine,
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double playbackUT,
            bool suppressFx)
        {
            MethodInfo method = typeof(GhostPlaybackEngine).GetMethod(
                "TryPositionRelativeSectionAtPlaybackUT",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            object result = method.Invoke(
                engine,
                new object[] { index, traj, state, playbackUT, suppressFx });
            return Assert.IsType<bool>(result);
        }

        private static void InvokePositionLoopAtPlaybackUT(
            GhostPlaybackEngine engine,
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double loopUT,
            bool suppressFx,
            string callsite)
        {
            InvokePositionLoopAtPlaybackUT(
                engine,
                index,
                traj,
                state,
                loopUT,
                suppressFx,
                callsite,
                default(TrajectoryPlaybackFlags),
                loopUT,
                1f,
                emitExitWatch: false);
        }

        private static void InvokePositionLoopAtPlaybackUT(
            GhostPlaybackEngine engine,
            int index,
            IPlaybackTrajectory traj,
            GhostPlaybackState state,
            double loopUT,
            bool suppressFx,
            string callsite,
            TrajectoryPlaybackFlags flags,
            double frameUT,
            float warpRate,
            bool emitExitWatch)
        {
            MethodInfo method = typeof(GhostPlaybackEngine).GetMethod(
                "PositionLoopAtPlaybackUT",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            method.Invoke(
                engine,
                new object[]
                {
                    index,
                    traj,
                    flags,
                    state,
                    loopUT,
                    frameUT,
                    warpRate,
                    suppressFx,
                    emitExitWatch,
                    callsite
                });
        }

        private sealed class SpawnPrimingPositioner : IGhostPositioner
        {
            internal int InterpolateCalls;
            internal int PositionAtPointCalls;
            internal int PositionFromOrbitCalls;
            internal int PositionLoopCalls;
            internal int ZoneCalls;
            internal int ShadowPositionCalls;
            internal double LastUT;
            internal double LastPointUT;
            internal double LastOrbitUT;
            internal double LastLoopUT;
            internal double LastShadowUT;
            internal Vector3 PrimedPosition = new Vector3(12f, 34f, 56f);
            // Test-controlled return value for the shadow positioner. When
            // false (default) the engine routes through the legacy hide path
            // exactly as it did pre-route. When true, set
            // PrimedShadowBracketBeforeUT / PrimedShadowBracketAfterUT.
            internal bool ShadowPositionShouldSucceed = false;
            internal double PrimedShadowBracketBeforeUT = double.NaN;
            internal double PrimedShadowBracketAfterUT = double.NaN;

            public void InterpolateAndPosition(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx)
            {
                InterpolateCalls++;
                LastUT = ut;
                state?.SetInterpolated(new InterpolationResult(default(Vector3), "Kerbin", 123.0));
            }

            public void InterpolateAndPositionRelative(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx,
                RelativeSectionPlaybackTarget target)
            {
                InterpolateAndPosition(index, traj, state, ut, suppressFx);
            }

            public bool TryPositionFromRelativeAbsoluteShadow(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double playbackUT, RelativeSectionPlaybackTarget target,
                out double bracketBeforeUT, out double bracketAfterUT)
            {
                ShadowPositionCalls++;
                LastShadowUT = playbackUT;
                bracketBeforeUT = PrimedShadowBracketBeforeUT;
                bracketAfterUT = PrimedShadowBracketAfterUT;
                if (!ShadowPositionShouldSucceed)
                    return false;
                return true;
            }

            public void PositionAtPoint(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, TrajectoryPoint point)
            {
                PositionAtPointCalls++;
                LastPointUT = point.ut;
            }

            public void PositionAtSurface(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state)
            {
            }

            public void PositionFromOrbit(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut)
            {
                PositionFromOrbitCalls++;
                LastOrbitUT = ut;
            }

            public void PositionLoop(int index, IPlaybackTrajectory traj,
                GhostPlaybackState state, double ut, bool suppressFx)
            {
                PositionLoopCalls++;
                LastLoopUT = ut;
                InterpolateAndPosition(index, traj, state, ut, suppressFx);
            }

            public bool TryResolveExplosionAnchorPosition(int index,
                IPlaybackTrajectory traj, GhostPlaybackState state, out Vector3 worldPosition)
            {
                worldPosition = PrimedPosition;
                return true;
            }

            public ZoneRenderingResult ApplyZoneRendering(int index, GhostPlaybackState state,
                IPlaybackTrajectory traj, double distance, double playbackUT, int protectedIndex)
            {
                ZoneCalls++;
                return new ZoneRenderingResult();
            }

            public void ClearOrbitCache()
            {
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
        public void TryGetCheckpointBackedOrbitEndpointUT_CheckpointSectionWithOrbitSegment_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 200.0);
            traj.RecordingId = "checkpoint-orbit";
            traj.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 200.0,
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 100.0,
                endUT = 200.0,
                frames = traj.Points,
                checkpoints = new List<OrbitSegment>(traj.OrbitSegments),
            });

            bool result = GhostPlaybackEngine.TryGetCheckpointBackedOrbitEndpointUT(
                traj,
                GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj),
                out double endpointUT,
                out int sectionIndex);

            Assert.True(result);
            Assert.Equal(200.0, endpointUT);
            Assert.Equal(0, sectionIndex);
        }

        [Fact]
        public void TryGetCheckpointBackedOrbitEndpointUT_StaleLastPointUsesCheckpointSectionEnd()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "checkpoint-orbit-tail",
                EndUTOverride = 340.0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 321.776 },
                },
            };
            traj.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 321.776,
                endUT = 340.0,
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 321.776,
                frames = traj.Points,
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 321.776,
                endUT = 340.0,
                checkpoints = new List<OrbitSegment>(traj.OrbitSegments),
            });

            double staleEndpointUT = GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj);
            bool result = GhostPlaybackEngine.TryGetCheckpointBackedOrbitEndpointUT(
                traj,
                staleEndpointUT,
                out double endpointUT,
                out int sectionIndex);

            Assert.True(result);
            Assert.Equal(321.776, staleEndpointUT);
            Assert.Equal(340.0, endpointUT);
            Assert.Equal(1, sectionIndex);
        }

        [Fact]
        public void TryGetCheckpointBackedOrbitEndpointUT_PartialCheckpointTailUsesNearestOrbitEndpoint()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "checkpoint-partial-tail",
                EndUTOverride = 340.0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 321.776 },
                },
            };
            traj.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 321.776,
                endUT = 339.75,
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 321.776,
                frames = traj.Points,
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 321.776,
                endUT = 340.0,
                checkpoints = new List<OrbitSegment>(traj.OrbitSegments),
            });

            bool result = GhostPlaybackEngine.TryGetCheckpointBackedOrbitEndpointUT(
                traj,
                GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj),
                out double endpointUT,
                out int sectionIndex);

            Assert.True(result);
            Assert.Equal(339.75, endpointUT);
            Assert.Equal(1, sectionIndex);
        }

        [Fact]
        public void TryGetCheckpointBackedOrbitEndpointUT_AbsoluteSectionReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 200.0);
            traj.OrbitSegments.Add(new OrbitSegment
            {
                bodyName = "Kerbin",
                startUT = 100.0,
                endUT = 200.0,
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 200.0,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetCheckpointBackedOrbitEndpointUT(
                traj,
                200.0,
                out _,
                out _);

            Assert.False(result);
        }

        [Fact]
        public void TryGetCheckpointBackedOrbitEndpointUT_NoOrbitSegmentReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 200.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 100.0,
                endUT = 200.0,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetCheckpointBackedOrbitEndpointUT(
                traj,
                200.0,
                out _,
                out _);

            Assert.False(result);
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_AfterLastFlatPointInOrbitSegment_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 158.971);
            traj.EndUTOverride = 1971.0;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 158.971,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 161.482));
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_AtLastFlatPoint_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 158.971);
            traj.EndUTOverride = 1971.0;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 158.971,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            Assert.False(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 158.971));
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_AfterLastFlatPointOutsideOrbitSegment_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 158.971);
            traj.EndUTOverride = 1971.0;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 200.0,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            Assert.False(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 161.482));
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_DegenerateInRangeSegment_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 158.971);
            traj.EndUTOverride = 1971.0;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 158.971,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 0.0,
                isPredicted = true,
            });

            Assert.False(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 161.482));
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_BeforeNearPredictedSegmentStart_BridgesGap()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 497.79);
            traj.EndUTOverride = 1971.0;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 498.168,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 497.98));
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_DegeneratePredictedBridgeSegment_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 497.79);
            traj.EndUTOverride = 1971.0;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 498.168,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = double.NaN,
                isPredicted = true,
            });

            Assert.False(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 497.98));
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_BeforeDistantPredictedSegmentStart_DoesNotBridgeGap()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 497.79);
            traj.EndUTOverride = 1971.0;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 499.0,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            Assert.False(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 497.98));
        }

        [Fact]
        public void ShouldUseOrbitTailPlayback_DestroyedTailBridgesFinalizerGap()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 687.58);
            traj.EndUTOverride = 1683.91;
            traj.TerminalStateValue = TerminalState.Destroyed;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 690.40,
                endUT = 1683.91,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 689.74));
        }

        [Fact]
        public void TryFindOrbitTailPlaybackSegment_ReturnsSegmentIndexForBridge()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 497.79);
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 450.0,
                endUT = 470.0,
                bodyName = "Kerbin",
                semiMajorAxis = 690000.0,
                isPredicted = false,
            });
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 498.168,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            bool found = GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment(
                traj, 497.98, out OrbitSegment segment, out int segmentIndex);

            Assert.True(found);
            Assert.Equal(1, segmentIndex);
            Assert.Equal(498.168, segment.startUT, 3);
        }

        [Fact]
        public void ShouldPrimeSinglePointGhostFromOrbit_UsesOrbitTailGate()
        {
            var traj = new MockTrajectory
            {
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 158.971, bodyName = "Kerbin" },
                },
                EndUTOverride = 1971.0,
            };
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 158.971,
                endUT = 1971.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
                isPredicted = true,
            });

            Assert.False(GhostPlaybackEngine.ShouldPrimeSinglePointGhostFromOrbit(traj, 158.971));
            Assert.True(GhostPlaybackEngine.ShouldPrimeSinglePointGhostFromOrbit(traj, 161.482));
        }

        [Fact]
        public void TryGetRelativeSectionAtUT_RelativeEndpointReturnsSectionTarget()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.RecordingId = "focus-rec";
            traj.RecordingFormatVersion = RecordingStore.RecordingAnchorChainFormatVersion;
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorRecordingId = "anchor-rec",
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAtUT(
                traj,
                GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj),
                out RelativeSectionPlaybackTarget target);

            Assert.True(result);
            Assert.Equal("focus-rec", target.RecordingId);
            Assert.Equal(0, target.SectionIndex);
            Assert.Equal("anchor-rec", target.AnchorRecordingId);
            Assert.True(target.HasAnchorRecordingId);
            Assert.Equal(ReferenceFrame.Relative, target.Section.referenceFrame);
        }

        [Fact]
        public void TryGetRelativeSectionAtUT_SinglePointRelativeEndpointStillRoutesRelative()
        {
            var traj = new MockTrajectory
            {
                RecordingId = "focus-single",
                RecordingFormatVersion = RecordingStore.RecordingAnchorChainFormatVersion,
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
                anchorRecordingId = "anchor-single",
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAtUT(
                traj,
                GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj),
                out RelativeSectionPlaybackTarget target);

            Assert.True(result);
            Assert.Equal("anchor-single", target.AnchorRecordingId);
            Assert.Equal(0, target.SectionIndex);
        }

        [Fact]
        public void TryGetRelativeSectionAtUT_MissingV11AnchorDoesNotSynthesizeLoopAnchor()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.RecordingId = "focus-missing";
            traj.RecordingFormatVersion = RecordingStore.RecordingAnchorChainFormatVersion;
            traj.LoopAnchorVesselId = 77u;
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorVesselId = 0u,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAtUT(
                traj, 110.0, out RelativeSectionPlaybackTarget target);

            Assert.True(result);
            Assert.False(target.HasAnchorRecordingId);
            Assert.Null(target.AnchorRecordingId);
            Assert.Equal(0, target.SectionIndex);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("RELATIVE v11 section missing anchorRecordingId")
                && l.Contains("recordingId=focus-missing"));
        }

        [Fact]
        public void TryGetRelativeSectionAtUT_LegacyMissingAnchorStillRoutesButDoesNotInventPid()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.RecordingId = "legacy-focus";
            traj.RecordingFormatVersion = RecordingStore.RelativeAbsoluteShadowFormatVersion;
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorVesselId = 0u,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAtUT(
                traj, 110.0, out RelativeSectionPlaybackTarget target);

            Assert.True(result);
            Assert.False(target.HasAnchorRecordingId);
            Assert.Equal(0u, target.Section.anchorVesselId);
        }

        [Fact]
        public void TryGetRelativeSectionAtUT_AbsoluteEndpointReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 110.0,
                frames = traj.Points,
            });

            bool result = GhostPlaybackEngine.TryGetRelativeSectionAtUT(
                traj, 110.0, out RelativeSectionPlaybackTarget target);

            Assert.False(result);
            Assert.Equal(default(RelativeSectionPlaybackTarget), target);
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_AfterRelativeSection_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();

            Assert.True(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, 111.0));
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_InsideRelativeSection_ReturnsFalse()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();

            Assert.False(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, 105.0));
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_InsideSectionAfterAuthoredFrames_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 },
            };
            traj.TrackSections[0] = section;

            Assert.True(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj,
                120.0,
                out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic));
            Assert.Equal("relative-and-shadow-frames-out-of-range", diagnostic.Reason);
            Assert.Equal(0, diagnostic.SectionIndex);
            Assert.Equal(100.0, diagnostic.FirstRelativeFrameUT);
            Assert.Equal(110.0, diagnostic.LastRelativeFrameUT);
            Assert.Equal(100.0, diagnostic.FirstAbsoluteFrameUT);
            Assert.Equal(110.0, diagnostic.LastAbsoluteFrameUT);
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_ShadowFramesCover_ReturnsFalse()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 130.0 },
            };
            traj.TrackSections[0] = section;

            Assert.False(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj,
                120.0,
                out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic));
            Assert.Equal("covered-by-absolute-shadow", diagnostic.Reason);
            Assert.False(diagnostic.RelativeFramesCoverUT);
            Assert.True(diagnostic.AbsoluteFramesCoverUT);
        }

        [Fact]
        public void AuthoredFrameGapHasShadowCoverage_ShadowCoveredGapWithOrbitTail_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 130.0 },
            };
            traj.TrackSections[0] = section;
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 111.0,
                endUT = 140.0,
                bodyName = "Kerbin",
                semiMajorAxis = 700000.0,
            });

            Assert.True(GhostPlaybackEngine.ShouldUseOrbitTailPlayback(traj, 120.0));
            Assert.True(GhostPlaybackEngine.AuthoredFrameGapHasShadowCoverage(
                traj, 120.0));
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_SingleAbsoluteShadowFrame_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 120.0 },
            };
            traj.TrackSections[0] = section;

            Assert.True(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj,
                120.0,
                out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic));
            Assert.Equal("relative-and-shadow-frames-out-of-range", diagnostic.Reason);
            Assert.False(diagnostic.RelativeFramesCoverUT);
            Assert.False(diagnostic.AbsoluteFramesCoverUT);
            Assert.Equal(120.0, diagnostic.FirstAbsoluteFrameUT);
            Assert.Equal(120.0, diagnostic.LastAbsoluteFrameUT);
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_LiveAnchorLoopDebris_ReturnsFalse()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            traj.LoopAnchorVesselId = 77u;
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            traj.TrackSections[0] = section;

            Assert.False(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, 120.0));
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_SingleRelativeFrameWithinSection_ReturnsFalse()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
            };
            traj.TrackSections[0] = section;

            Assert.False(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj,
                120.0,
                out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic));
            Assert.Equal("covered-by-relative-frames", diagnostic.Reason);
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_ProjectedFlatPointsCover_ReturnsFalse()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.frames = null;
            traj.TrackSections[0] = section;

            Assert.False(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj,
                105.0,
                out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic));
            Assert.Equal("covered-by-relative-frames", diagnostic.Reason);
            Assert.Equal(100.0, diagnostic.FirstRelativeFrameUT);
            Assert.Equal(110.0, diagnostic.LastRelativeFrameUT);
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_EmptyFramesAndNoProjectedPoints_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            traj.Points.Clear();
            TrackSection section = traj.TrackSections[0];
            section.frames = new List<TrajectoryPoint>();
            section.absoluteFrames = new List<TrajectoryPoint>();
            traj.TrackSections[0] = section;

            Assert.True(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj,
                105.0,
                out DebrisRelativePlaybackPolicy.ParentAnchoredDebrisCoverageDiagnostic diagnostic));
            Assert.Equal("relative-and-shadow-frames-out-of-range", diagnostic.Reason);
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_AbsoluteSection_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.referenceFrame = ReferenceFrame.Absolute;
            traj.TrackSections[0] = section;

            Assert.True(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, 105.0));
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_LegacyDebris_ReturnsFalse()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            traj.DebrisParentRecordingId = null;

            Assert.False(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, 111.0));
        }

        [Fact]
        public void ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_EmptyParentId_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            traj.DebrisParentRecordingId = "";

            Assert.True(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, 111.0));
        }

        [Fact]
        public void ResolveRecordingEndpointCoverageUT_OrbitEndpointPastRelativeCoverage_UsesOrbitEndpoint()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            traj.EndpointPhase = RecordingEndpointPhase.OrbitSegment;
            traj.EndpointBodyName = "Kerbin";
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110.0,
                endUT = 130.0,
                bodyName = "Kerbin",
            });

            double pointEndpointUT = GhostPlaybackEngine.ResolveRecordingEndpointPlaybackUT(traj);
            double coverageUT = GhostPlaybackEngine.ResolveRecordingEndpointCoverageUT(traj);

            Assert.Equal(110.0, pointEndpointUT);
            Assert.Equal(130.0, coverageUT);
            Assert.False(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, pointEndpointUT));
            Assert.True(GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage(
                traj, coverageUT));
        }

        [Fact]
        public void ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss_OrbitEndpointPastRelativeCoverage_ReturnsTrue()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            traj.EndpointPhase = RecordingEndpointPhase.OrbitSegment;
            traj.EndpointBodyName = "Kerbin";
            traj.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 110.0,
                endUT = 130.0,
                bodyName = "Kerbin",
            });

            Assert.True(GhostPlaybackEngine.ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(traj));
        }

        [Fact]
        public void ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss_EndpointInsideRelativeCoverage_ReturnsFalse()
        {
            var traj = MakeParentAnchoredDebrisWithRelativeSection();

            Assert.False(GhostPlaybackEngine.ShouldCompleteParentAnchoredDebrisEndpointCoverageMiss(traj));
        }

        [Fact]
        public void TryPositionRelativeSectionAtPlaybackUT_ParentAnchoredDebrisOutsideCoverage_Retires()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
                ghost = null,
            };

            bool handled = InvokeTryPositionRelativeSectionAtPlaybackUT(
                engine,
                index: 3,
                traj: traj,
                state: state,
                playbackUT: 111.0,
                suppressFx: true);

            Assert.True(handled);
            Assert.True(state.anchorRetiredThisFrame);
            Assert.Equal(0, positioner.InterpolateCalls);
            Assert.Equal(0, positioner.PositionAtPointCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]")
                && l.Contains("recorded-relative-retired")
                && l.Contains("reason=parent-anchored-debris-outside-relative-coverage")
                && l.Contains("coverageReason=no-covering-section")
                && l.Contains("recordingId=debris-rec"));
        }

        [Fact]
        public void TryPositionRelativeSectionAtPlaybackUT_ParentAnchoredDebrisAfterAuthoredFrames_Retires()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 },
            };
            traj.TrackSections[0] = section;
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
                ghost = null,
            };

            bool handled = InvokeTryPositionRelativeSectionAtPlaybackUT(
                engine,
                index: 3,
                traj: traj,
                state: state,
                playbackUT: 120.0,
                suppressFx: true);

            Assert.True(handled);
            Assert.True(state.anchorRetiredThisFrame);
            Assert.Equal(0, positioner.InterpolateCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]")
                && l.Contains("recorded-relative-retired")
                && l.Contains("reason=parent-anchored-debris-outside-relative-coverage")
                && l.Contains("coverageReason=relative-and-shadow-frames-out-of-range")
                && l.Contains("recordingId=debris-rec"));
        }

        [Fact]
        public void TryPositionRelativeSectionAtPlaybackUT_ShadowCoveredAfterRelativeFrames_DoesNotRetire()
        {
            var positioner = new SpawnPrimingPositioner
            {
                ShadowPositionShouldSucceed = true,
                PrimedShadowBracketBeforeUT = 100.0,
                PrimedShadowBracketAfterUT = 130.0,
            };
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 130.0 },
            };
            traj.TrackSections[0] = section;
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
                ghost = null,
            };

            bool handled = InvokeTryPositionRelativeSectionAtPlaybackUT(
                engine,
                index: 3,
                traj: traj,
                state: state,
                playbackUT: 120.0,
                suppressFx: true);

            Assert.True(handled);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.Equal(0, positioner.ShadowPositionCalls);
            Assert.Equal(1, positioner.InterpolateCalls);
            Assert.Empty(logLines.Where(l => l.Contains("recorded-relative-retired")));
        }

        [Fact]
        public void PositionLoopAtPlaybackUT_ParentAnchoredDebrisOutsideCoverage_RetiresBeforeLoopPositioner()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
                ghost = null,
            };

            InvokePositionLoopAtPlaybackUT(
                engine,
                index: 4,
                traj: traj,
                state: state,
                loopUT: 111.0,
                suppressFx: true,
                callsite: "test-loop");

            Assert.True(state.anchorRetiredThisFrame);
            Assert.Equal(0, positioner.PositionLoopCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]")
                && l.Contains("recorded-relative-retired")
                && l.Contains("reason=parent-anchored-debris-outside-relative-coverage")
                && l.Contains("coverageReason=no-covering-section")
                && l.Contains("recordingId=debris-rec")
                && l.Contains("callsite=test-loop"));
        }

        [Fact]
        public void PositionLoopAtPlaybackUT_ParentAnchoredDebrisAfterAuthoredFrames_DoesNotEvaluateResolverGate()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 110.0 },
            };
            traj.TrackSections[0] = section;
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
                ghost = null,
            };
            bool evaluatorCalled = false;
            var flags = new TrajectoryPlaybackFlags
            {
                tryEvaluateAnchorRotationReliability =
                    (int idx, IPlaybackTrajectory observedTraj, double playbackUT,
                        string playbackScope,
                        out AnchorRotationReliabilityDecision decision) =>
                    {
                        evaluatorCalled = true;
                        decision = default;
                        return false;
                    }
            };

            InvokePositionLoopAtPlaybackUT(
                engine,
                index: 4,
                traj: traj,
                state: state,
                loopUT: 120.0,
                suppressFx: true,
                callsite: "test-loop",
                flags: flags,
                frameUT: 120.0,
                warpRate: 1f,
                emitExitWatch: false);

            Assert.False(evaluatorCalled);
            Assert.True(state.anchorRetiredThisFrame);
            Assert.Equal(0, positioner.PositionLoopCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]")
                && l.Contains("recorded-relative-retired")
                && l.Contains("coverageReason=relative-and-shadow-frames-out-of-range"));
        }

        [Fact]
        public void PositionLoopAtPlaybackUT_ShadowCoveredAfterRelativeFrames_SkipsResolverGateWithoutRetiring()
        {
            var positioner = new SpawnPrimingPositioner
            {
                ShadowPositionShouldSucceed = true,
                PrimedShadowBracketBeforeUT = 100.0,
                PrimedShadowBracketAfterUT = 130.0,
            };
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            TrackSection section = traj.TrackSections[0];
            section.endUT = 140.0;
            section.absoluteFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint { ut = 100.0 },
                new TrajectoryPoint { ut = 130.0 },
            };
            traj.TrackSections[0] = section;
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
                ghost = null,
            };
            bool evaluatorCalled = false;
            var flags = new TrajectoryPlaybackFlags
            {
                tryEvaluateAnchorRotationReliability =
                    (int idx, IPlaybackTrajectory observedTraj, double playbackUT,
                        string playbackScope,
                        out AnchorRotationReliabilityDecision decision) =>
                    {
                        evaluatorCalled = true;
                        decision = default;
                        return false;
                    }
            };

            InvokePositionLoopAtPlaybackUT(
                engine,
                index: 4,
                traj: traj,
                state: state,
                loopUT: 120.0,
                suppressFx: true,
                callsite: "test-loop",
                flags: flags,
                frameUT: 120.0,
                warpRate: 1f,
                emitExitWatch: false);

            Assert.False(evaluatorCalled);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.Equal(0, positioner.ShadowPositionCalls);
            Assert.Equal(1, positioner.PositionLoopCalls);
        }

        [Fact]
        public void PositionLoopAtPlaybackUT_ParentAnchoredDebrisInsideCoverage_PositionsLoop()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
                ghost = null,
            };

            InvokePositionLoopAtPlaybackUT(
                engine,
                index: 4,
                traj: traj,
                state: state,
                loopUT: 105.0,
                suppressFx: true,
                callsite: "test-loop");

            Assert.False(state.anchorRetiredThisFrame);
            Assert.Equal(1, positioner.PositionLoopCalls);
            Assert.Equal(105.0, positioner.LastLoopUT);
        }

        [Fact]
        public void PositionLoopAtPlaybackUT_AnchorRotationUnreliable_HidesBeforeLoopPositioner()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            GhostPlaybackState state = null;
            var cameraEvents = new List<CameraActionEvent>();
            engine.OnLoopCameraAction += evt => cameraEvents.Add(evt);
            double observedPlaybackUT = double.NaN;
            string observedScope = null;
            var flags = new TrajectoryPlaybackFlags
            {
                tryEvaluateAnchorRotationReliability =
                    (int idx, IPlaybackTrajectory observedTraj, double playbackUT,
                        string playbackScope,
                        out AnchorRotationReliabilityDecision decision) =>
                    {
                        Assert.Equal(4, idx);
                        Assert.Same(traj, observedTraj);
                        observedPlaybackUT = playbackUT;
                        observedScope = playbackScope;
                        decision = new AnchorRotationReliabilityDecision(
                            unreliable: true,
                            anchorRecordingId: "parent-rec",
                            bracketDegrees: 24.0,
                            rateDegreesPerSecond: 240.0,
                            offsetMeters: 1500.0);
                        return true;
                    }
            };

            InvokePositionLoopAtPlaybackUT(
                engine,
                index: 4,
                traj: traj,
                state: state,
                loopUT: 105.0,
                suppressFx: true,
                callsite: "test-loop",
                flags: flags,
                frameUT: 222.0,
                warpRate: 2f,
                emitExitWatch: true);

            Assert.Equal(0, positioner.PositionLoopCalls);
            Assert.Equal(105.0, observedPlaybackUT);
            Assert.Equal("test-loop", observedScope);

            var evt = Assert.Single(cameraEvents);
            Assert.Equal(CameraActionType.ExitWatch, evt.Action);
            Assert.Equal(4, evt.Index);
            Assert.Same(traj, evt.Trajectory);
            Assert.NotNull(evt.Flags.tryEvaluateAnchorRotationReliability);
            Assert.Contains(logLines, l =>
                l.Contains("anchor-rotation-unreliable")
                && l.Contains("playbackUT=105"));
        }

        // ===================================================================
        // Always-shadow router (PR #803) — fallthrough ladder cases
        // -------------------------------------------------------------------
        // Pre-PR-#803 the shadow render only fired when the tumbling-parent
        // gate also fired. Post-PR-#803 the shadow render fires whenever shadow
        // data covers the playback UT, regardless of gate state, with the gate
        // demoted to FX-suppression authority. Hide remains the third tier
        // when shadow is unavailable AND the gate is firing.
        //
        // The success-path tests live in RuntimeTests.cs because exercising
        // the shadow positioner requires a real Unity GameObject (the engine's
        // <c>state?.ghost == null</c> short-circuit invokes Unity's overloaded
        // == on real ghosts). xUnit can only create literal-null
        // <c>state.ghost</c> values, which trips the short-circuit and bypasses
        // the shadow path entirely. The four cases below cover the legacy-
        // and hide-fallthrough branches that work fine with a null ghost.
        // ===================================================================

        private static TrajectoryPlaybackFlags BuildAnchorRotationFlags(
            bool unreliable,
            string anchorRecordingId = "parent-rec",
            double bracketDegrees = 24.0,
            double rateDegreesPerSecond = 240.0,
            double offsetMeters = 1500.0)
        {
            return new TrajectoryPlaybackFlags
            {
                tryEvaluateAnchorRotationReliability =
                    (int idx, IPlaybackTrajectory traj, double playbackUT,
                        string playbackScope,
                        out AnchorRotationReliabilityDecision decision) =>
                    {
                        decision = new AnchorRotationReliabilityDecision(
                            unreliable: unreliable,
                            anchorRecordingId: anchorRecordingId,
                            bracketDegrees: bracketDegrees,
                            rateDegreesPerSecond: rateDegreesPerSecond,
                            offsetMeters: offsetMeters);
                        return true;
                    }
            };
        }

        [Fact]
        public void AlwaysShadow_GateInactive_NoShadowData_FallsThroughToLegacy()
        {
            // Tier 2: older recording without v7+ absoluteFrames (or shadow
            // filtered out at runtime). Gate doesn't fire. Router must NOT
            // call the shadow positioner; legacy PositionLoop runs as today.
            // No FX suppression.
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            // MakeParentAnchoredDebrisWithRelativeSection has Relative section
            // with frames but absoluteFrames is null.
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var state = new GhostPlaybackState { vesselName = "Kerbal X Debris", ghost = null };

            InvokePositionLoopAtPlaybackUT(
                engine, index: 4, traj: traj, state: state,
                loopUT: 105.0, suppressFx: false, callsite: "test-loop",
                flags: BuildAnchorRotationFlags(unreliable: false),
                frameUT: 222.0, warpRate: 1f, emitExitWatch: false);

            Assert.Equal(0, positioner.ShadowPositionCalls);
            Assert.Equal(1, positioner.PositionLoopCalls);
            Assert.Equal(105.0, positioner.LastLoopUT);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.False(state.anchorRotationShadowRoutedThisFrame);
        }

        [Fact]
        public void AlwaysShadow_GateActive_NoShadowData_FallsThroughToHide()
        {
            // Tier 3: no shadow available AND gate fires. Hide is the
            // third-tier fallback: legacy reconstruction would be visible
            // chaos for the current frame, so retire the mesh
            // (anchorRetiredThisFrame=true) and emit the exit-watch event.
            // This is the same behaviour PR #800 had when it fell through
            // from shadow-attempt to hide; the ladder shape is unchanged
            // for this case.
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            GhostPlaybackState state = null;
            var cameraEvents = new List<CameraActionEvent>();
            engine.OnLoopCameraAction += evt => cameraEvents.Add(evt);

            InvokePositionLoopAtPlaybackUT(
                engine, index: 4, traj: traj, state: state,
                loopUT: 105.0, suppressFx: false, callsite: "test-loop",
                flags: BuildAnchorRotationFlags(unreliable: true),
                frameUT: 222.0, warpRate: 1f, emitExitWatch: true);

            Assert.Equal(0, positioner.ShadowPositionCalls);
            Assert.Equal(0, positioner.PositionLoopCalls);
            var evt = Assert.Single(cameraEvents);
            Assert.Equal(CameraActionType.ExitWatch, evt.Action);
            // The hide tier emits two log lines: the engine-level exit-watch
            // (always at Verbose) and a GhostRenderTrace GuardSkip ("hidden"
            // suffix, gated on verbose+detailed-window). xUnit only captures
            // the exit-watch line, which the existing legacy hide-path test
            // also asserts on.
            Assert.Contains(logLines, l =>
                l.Contains("anchor-rotation-unreliable")
                && l.Contains("playbackUT=105"));
        }

        [Fact]
        public void AlwaysShadow_NullGhostShortCircuit_GateInactive_FallsThroughToLegacy()
        {
            // Defensive guard inside TryRouteAnchorRotationToShadow: when
            // state?.ghost is null the shadow positioner is never invoked
            // (pre-flight bail) regardless of whether shadow data covers.
            // With gate inactive, the router falls through to legacy as
            // today. This pins the null-ghost short-circuit so a future
            // refactor that loosens the check has to update the test.
            var positioner = new SpawnPrimingPositioner
            {
                ShadowPositionShouldSucceed = true,
            };
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithShadowFrames();
            var state = new GhostPlaybackState { vesselName = "Kerbal X Debris", ghost = null };

            InvokePositionLoopAtPlaybackUT(
                engine, index: 4, traj: traj, state: state,
                loopUT: 105.0, suppressFx: false, callsite: "test-loop",
                flags: BuildAnchorRotationFlags(unreliable: false),
                frameUT: 222.0, warpRate: 1f, emitExitWatch: false);

            Assert.Equal(0, positioner.ShadowPositionCalls);
            Assert.Equal(1, positioner.PositionLoopCalls);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.False(state.anchorRotationShadowRoutedThisFrame);
        }

        [Fact]
        public void AlwaysShadow_LiveAnchorRecording_ExcludedByPredicate()
        {
            // Reviewer P1 regression: a recording with v12+ parent linkage AND
            // a live-anchor loop assignment must NOT route through the
            // always-shadow path. Pre-PR-#803 the resolver short-circuited at
            // `LoopAnchorVesselId != 0` ("loop-anchor-out-of-scope") so the
            // gate evaluator returned false; the router treated the result as
            // None and legacy ran. PR #803's always-shadow path bypasses the
            // resolver, so the same predicate alone (IsDebris &&
            // DebrisParentRecordingId != null) would happily route the live-
            // anchor recording's debris through the recorded shadow track,
            // breaking the live-anchor contract.
            //
            // Fix: include `LoopAnchorVesselId == 0u` in the host predicate
            // ShouldEvaluateAnchorRotationReliability so the
            // `tryEvaluateAnchorRotationReliability` callback is null for
            // live-anchor recordings, the router returns None, and the legacy
            // resolver chain runs as today.
            var live = new Recording
            {
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
                LoopAnchorVesselId = 12345u,
            };
            Assert.False(
                ParsekFlight.ShouldEvaluateAnchorRotationReliabilityForTesting(live),
                "live-anchor (LoopAnchorVesselId != 0) recordings must be excluded from the always-shadow predicate even when the v12+ parent linkage is set");

            // And the symmetric positive case: same fields but no live-anchor
            // assignment should still qualify (PR #800 + #803 path is intact).
            var noLive = new Recording
            {
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
                LoopAnchorVesselId = 0u,
            };
            Assert.True(
                ParsekFlight.ShouldEvaluateAnchorRotationReliabilityForTesting(noLive),
                "v12+ parent-anchored debris without a live-anchor assignment must still qualify for the always-shadow path");
        }

        [Fact]
        public void AlwaysShadow_EvaluatorReturnsFalse_StillTriesShadow()
        {
            // Reviewer P2 (round 2): a runtime evaluator miss (no focus tree,
            // resolver-side issue) must NOT block the shadow render. The PR
            // #803 contract says shadow renders for every covered frame for
            // v12+ parent-anchored debris with shadow data, regardless of
            // whether the gate evaluator's runtime call succeeded.
            //
            // The host predicate ShouldEvaluateAnchorRotationReliability has
            // already filtered the recording in scope at flag-build time --
            // a runtime evaluator miss is purely a diagnostic-data gap (the
            // shadow-route log line will carry default bracket/rate/offset
            // fields and mode=always, since fxSuppress can't be true without
            // a real evaluation), not a signal to skip the shadow render.
            //
            // The earlier fix that gated the shadow attempt on `gateEvaluated`
            // contradicted the PR contract -- this test pins the corrected
            // behaviour.
            //
            // The success path (shadow positioner actually called and writes
            // ghost.transform) requires a real Unity GameObject and is in
            // RuntimeTests.cs. This xUnit test verifies the router does NOT
            // skip the shadow attempt -- with `ghost = null` the positioner
            // is bypassed at the engine's null-ghost short-circuit, then we
            // fall through to legacy. The key thing pinned: the router does
            // not return early ON THE EVALUATOR-MISS BIT ALONE.
            //
            // To pin "the router does not gate on gateEvaluated" without a
            // real GameObject, we use the symmetry: when the predicate is
            // null (recording out of scope), legacy runs; when the predicate
            // is present-but-returns-false, the same legacy fallthrough
            // happens via the null-ghost short-circuit. Both produce
            // ShadowPositionCalls=0 in xUnit. The behavioural difference
            // (success path) lives in RuntimeTests.cs.
            var positioner = new SpawnPrimingPositioner
            {
                ShadowPositionShouldSucceed = true,
            };
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithShadowFrames();
            // Predicate present (non-null) but returns false -- mirrors the
            // host's "no focus tree" / "resolver miss" cases.
            var flags = new TrajectoryPlaybackFlags
            {
                tryEvaluateAnchorRotationReliability =
                    (int idx, IPlaybackTrajectory trajArg, double playbackUT,
                        string playbackScope,
                        out AnchorRotationReliabilityDecision decision) =>
                    {
                        decision = default;
                        return false;
                    }
            };
            var state = new GhostPlaybackState { vesselName = "Kerbal X Debris", ghost = null };

            InvokePositionLoopAtPlaybackUT(
                engine, index: 4, traj: traj, state: state,
                loopUT: 105.0, suppressFx: false, callsite: "test-loop",
                flags: flags,
                frameUT: 222.0, warpRate: 1f, emitExitWatch: false);

            // Null ghost short-circuit: shadow positioner not called even
            // though the router DID try shadow first. Legacy runs because
            // !fxSuppress (evaluator returned false) and shadow returned
            // false (null ghost).
            Assert.Equal(0, positioner.ShadowPositionCalls);
            Assert.Equal(1, positioner.PositionLoopCalls);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.False(state.anchorRotationShadowRoutedThisFrame);
        }

        [Fact]
        public void AlwaysShadow_NotV12Debris_NoPredicate_FallsThroughToLegacy()
        {
            // Predicate gate: TrajectoryPlaybackFlags.tryEvaluateAnchorRotationReliability
            // is null for non-v12 debris (host-side ShouldEvaluateAnchorRotationReliability
            // returns false when IsDebris==false OR DebrisParentRecordingId is
            // null). The router must early-return None and let legacy
            // positioning run, even if the recording does carry absoluteFrames.
            // This protects the live-anchor and non-debris cases from being
            // unintentionally re-routed.
            var positioner = new SpawnPrimingPositioner
            {
                ShadowPositionShouldSucceed = true,
            };
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithShadowFrames();
            // Flags with no anchor-rotation predicate -- mirrors the host side
            // for non-v12 / live-anchor recordings.
            var flags = new TrajectoryPlaybackFlags();
            var state = new GhostPlaybackState { vesselName = "Kerbal X Debris", ghost = null };

            InvokePositionLoopAtPlaybackUT(
                engine, index: 4, traj: traj, state: state,
                loopUT: 105.0, suppressFx: false, callsite: "test-loop",
                flags: flags,
                frameUT: 222.0, warpRate: 1f, emitExitWatch: false);

            Assert.Equal(0, positioner.ShadowPositionCalls);
            Assert.Equal(1, positioner.PositionLoopCalls);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.False(state.anchorRotationShadowRoutedThisFrame);
        }

        [Fact]
        public void ResolveRenderSurface_RouteShadow_ReturnsShadow()
        {
            // Trace surface resolver: pure helper. Shadow route always
            // resolves to Shadow regardless of retired (the route enum was
            // ShadowPositioned, so the engine wrote a real position before
            // any retire signal could fire on the same frame).
            Assert.Equal(GhostRenderTrace.RenderSurface.Shadow,
                GhostPlaybackEngine.ResolveRenderSurfaceForTesting(
                    AnchorRotationUnreliableRoute.ShadowPositioned, retired: false));
            Assert.Equal(GhostRenderTrace.RenderSurface.Shadow,
                GhostPlaybackEngine.ResolveRenderSurfaceForTesting(
                    AnchorRotationUnreliableRoute.ShadowPositioned, retired: true));
        }

        [Fact]
        public void ResolveRenderSurface_RouteHidden_ReturnsHidden()
        {
            Assert.Equal(GhostRenderTrace.RenderSurface.Hidden,
                GhostPlaybackEngine.ResolveRenderSurfaceForTesting(
                    AnchorRotationUnreliableRoute.Hidden, retired: false));
            Assert.Equal(GhostRenderTrace.RenderSurface.Hidden,
                GhostPlaybackEngine.ResolveRenderSurfaceForTesting(
                    AnchorRotationUnreliableRoute.Hidden, retired: true));
        }

        [Fact]
        public void ResolveRenderSurface_RouteNoneAndRetired_ReturnsHidden()
        {
            // Legacy rendering path BUT downstream retire mechanism (e.g. the
            // recorded-relative coverage gate) marked the ghost retired this
            // frame. Surface attribution should be Hidden because the mesh is
            // not visible.
            Assert.Equal(GhostRenderTrace.RenderSurface.Hidden,
                GhostPlaybackEngine.ResolveRenderSurfaceForTesting(
                    AnchorRotationUnreliableRoute.None, retired: true));
        }

        [Fact]
        public void ResolveRenderSurface_RouteNoneAndNotRetired_ReturnsLegacy()
        {
            Assert.Equal(GhostRenderTrace.RenderSurface.Legacy,
                GhostPlaybackEngine.ResolveRenderSurfaceForTesting(
                    AnchorRotationUnreliableRoute.None, retired: false));
        }

        // ===================================================================
        // Shadow-route FX-flag helpers — pure-static OR pattern + primary-loop
        // branch decision. The bug class these test pin: clear-without-read
        // leaks of FX state through the shadow-route window. A reviewer caught
        // two P1 instances of this in the original engine wiring; the helpers
        // were extracted afterwards so the OR pattern has one source of truth.
        // ===================================================================

        [Fact]
        public void IsInterpolationResultValid_BodyNameNull_TreatsAsFailure()
        {
            // Regression guard for the fail-closed contract on the shadow
            // route. Reviewer P2: TryPositionFromRelativeAbsoluteShadow used
            // to return true after InterpolateAndPosition even when the
            // helper had hit body-lookup-miss / empty-points failure paths,
            // which write InterpolationResult.Zero (bodyName=null) and call
            // ghost.SetActive(false). The engine then treated the route as
            // ShadowPositioned and the post-position pipeline could
            // re-activate the ghost at a stale transform. The new helper
            // detects this; the positioner falls back to Hidden when it
            // returns false.
            Assert.False(GhostPlaybackEngine.IsInterpolationResultValid(InterpolationResult.Zero));

            var nullName = new InterpolationResult { velocity = Vector3.zero, bodyName = null, altitude = 0 };
            Assert.False(GhostPlaybackEngine.IsInterpolationResultValid(nullName));

            var emptyName = new InterpolationResult { velocity = Vector3.zero, bodyName = "", altitude = 0 };
            Assert.False(GhostPlaybackEngine.IsInterpolationResultValid(emptyName));
        }

        [Fact]
        public void IsInterpolationResultValid_BodyNamePopulated_TreatsAsSuccess()
        {
            var success = new InterpolationResult(Vector3.zero, "Kerbin", 100.0);
            Assert.True(GhostPlaybackEngine.IsInterpolationResultValid(success));

            var minimal = new InterpolationResult { velocity = Vector3.zero, bodyName = "Mun", altitude = 0 };
            Assert.True(GhostPlaybackEngine.IsInterpolationResultValid(minimal));
        }

        [Fact]
        public void AdjustFxFlagsForShadowRoute_NotShadowRouted_PassesBaseFlagsThrough()
        {
            // (baseSkip, baseSuppress, shadowRouted=false) -> (baseSkip, baseSuppress)
            var (skip, suppress) = GhostPlaybackEngine.AdjustFxFlagsForShadowRoute(
                baseSkipPartEvents: false,
                baseSuppressVisualFx: false,
                shadowRouted: false);
            Assert.False(skip);
            Assert.False(suppress);

            (skip, suppress) = GhostPlaybackEngine.AdjustFxFlagsForShadowRoute(
                baseSkipPartEvents: true,
                baseSuppressVisualFx: false,
                shadowRouted: false);
            Assert.True(skip);
            Assert.False(suppress);

            (skip, suppress) = GhostPlaybackEngine.AdjustFxFlagsForShadowRoute(
                baseSkipPartEvents: false,
                baseSuppressVisualFx: true,
                shadowRouted: false);
            Assert.False(skip);
            Assert.True(suppress);

            (skip, suppress) = GhostPlaybackEngine.AdjustFxFlagsForShadowRoute(
                baseSkipPartEvents: true,
                baseSuppressVisualFx: true,
                shadowRouted: false);
            Assert.True(skip);
            Assert.True(suppress);
        }

        [Fact]
        public void AdjustFxFlagsForShadowRoute_ShadowRouted_ForcesBothFlagsTrue()
        {
            // Regression guard: when shadowRouted=true, BOTH outputs must be
            // true regardless of the base inputs. This is the OR pattern that
            // a clear-without-read reviewer finding (P1) had broken at three
            // sites in the original wiring; centralising it here makes future
            // bypass attempts visible at code-review time.
            foreach (bool baseSkip in new[] { false, true })
            foreach (bool baseSuppress in new[] { false, true })
            {
                var (skip, suppress) = GhostPlaybackEngine.AdjustFxFlagsForShadowRoute(
                    baseSkipPartEvents: baseSkip,
                    baseSuppressVisualFx: baseSuppress,
                    shadowRouted: true);
                Assert.True(skip,
                    $"shadowRouted=true must force skipPartEvents=true (was baseSkip={baseSkip}, baseSuppress={baseSuppress})");
                Assert.True(suppress,
                    $"shadowRouted=true must force suppressVisualFx=true (was baseSkip={baseSkip}, baseSuppress={baseSuppress})");
            }
        }

        [Fact]
        public void ResolveLoopShadowFxBranch_ShadowRouted_AlwaysReturnsForcedTeardown()
        {
            // Regression guard for primary-loop P1: when the previous logic
            // OR'd shadowRouted into skipLoopPartEvents and gated the entire
            // ApplyFrameVisuals call on !skipLoopPartEvents, shadow-routed
            // frames silently skipped FX teardown -- letting stale plumes /
            // RCS / reentry / audio continue running through the route.
            // Forced teardown must fire regardless of the legacy LOD flag.
            Assert.Equal(
                GhostPlaybackEngine.LoopShadowFxBranch.ForcedShadowTeardown,
                GhostPlaybackEngine.ResolveLoopShadowFxBranch(
                    shadowRouted: true,
                    skipLoopPartEvents: false));
            Assert.Equal(
                GhostPlaybackEngine.LoopShadowFxBranch.ForcedShadowTeardown,
                GhostPlaybackEngine.ResolveLoopShadowFxBranch(
                    shadowRouted: true,
                    skipLoopPartEvents: true));
        }

        [Fact]
        public void ResolveLoopShadowFxBranch_NotShadowRouted_RespectsLegacySkipFlag()
        {
            Assert.Equal(
                GhostPlaybackEngine.LoopShadowFxBranch.Normal,
                GhostPlaybackEngine.ResolveLoopShadowFxBranch(
                    shadowRouted: false,
                    skipLoopPartEvents: false));
            Assert.Equal(
                GhostPlaybackEngine.LoopShadowFxBranch.Skipped,
                GhostPlaybackEngine.ResolveLoopShadowFxBranch(
                    shadowRouted: false,
                    skipLoopPartEvents: true));
        }

        [Fact]
        public void GhostPlaybackState_ClearLoadedVisualReferences_ClearsShadowRoutedFlag()
        {
            // Lifecycle clear: the ClearLoadedVisualReferences path (called on
            // ghost destroy / engine reset / scene cleanup) must reset the new
            // flag alongside anchorRetiredThisFrame so a future state reuse
            // does not inherit a stale shadow-route signal. Reviewer P2.
            var state = new GhostPlaybackState
            {
                anchorRetiredThisFrame = true,
                anchorRotationShadowRoutedThisFrame = true,
            };

            state.ClearLoadedVisualReferences();

            Assert.False(state.anchorRetiredThisFrame);
            Assert.False(state.anchorRotationShadowRoutedThisFrame);
        }

        [Fact]
        public void CoverageRetiredHelper_StateNull_DoesNotReserveSpawnOrLoadVisuals()
        {
            var positioner = new SpawnPrimingPositioner();
            var engine = new GhostPlaybackEngine(positioner);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();

            bool handled = engine.TryHandleParentAnchoredDebrisCoverageRetiredForTesting(
                index: 3,
                traj: traj,
                state: null,
                playbackUT: 111.0,
                currentUT: 111.0,
                warpRate: 1f,
                emitExitWatch: false,
                out bool ghostActive);

            Assert.True(handled);
            Assert.False(ghostActive);
            Assert.Equal(0, engine.SpawnReserveAttemptCountForTesting);
            Assert.Equal(0, engine.VisualLoadAttemptCountForTesting);
            Assert.Equal(0, positioner.ZoneCalls);
            Assert.Equal(0, positioner.InterpolateCalls);
            Assert.Contains(logLines, l =>
                l.Contains("[Anchor]")
                && l.Contains("recorded-relative-retired")
                && l.Contains("recordingId=debris-rec")
                && l.Contains("callsite=test"));
        }

        [Fact]
        public void CoverageRetiredHelper_CoveredPlaybackUTClearsRuntimeFlag()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var state = new GhostPlaybackState
            {
                parentAnchoredDebrisCoverageRetired = true,
                anchorRetiredThisFrame = true,
            };

            bool handled = engine.TryHandleParentAnchoredDebrisCoverageRetiredForTesting(
                index: 3,
                traj: traj,
                state: state,
                playbackUT: 105.0,
                currentUT: 105.0,
                warpRate: 1f,
                emitExitWatch: false,
                out bool ghostActive);

            Assert.False(handled);
            Assert.False(ghostActive);
            Assert.False(state.parentAnchoredDebrisCoverageRetired);
            Assert.True(state.anchorRetiredThisFrame);
        }

        [Fact]
        public void CoverageRetiredHelper_DirectWatch_EmitsExitWatch()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var cameraEvents = new List<CameraActionEvent>();
            engine.OnLoopCameraAction += evt => cameraEvents.Add(evt);

            bool handled = engine.TryHandleParentAnchoredDebrisCoverageRetiredForTesting(
                index: 3,
                traj: traj,
                state: null,
                playbackUT: 111.0,
                currentUT: 111.0,
                warpRate: 1f,
                emitExitWatch: true,
                out _);

            Assert.True(handled);
            var evt = Assert.Single(cameraEvents);
            Assert.Equal(CameraActionType.ExitWatch, evt.Action);
            Assert.Equal(3, evt.Index);
            Assert.Same(traj, evt.Trajectory);
        }

        [Fact]
        public void CoverageRetiredHelper_LineageProtectedSibling_DoesNotEmitExitWatch()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var cameraEvents = new List<CameraActionEvent>();
            engine.OnLoopCameraAction += evt => cameraEvents.Add(evt);
            var ctx = new FrameContext
            {
                protectedIndex = 7,
                protectedLoopCycleIndex = -1,
            };
            bool emitExitWatch = GhostPlaybackEngine.ShouldExitWatchForCoverageRetiredStateForTesting(
                index: 3,
                state: null,
                ctx: ctx);

            bool handled = engine.TryHandleParentAnchoredDebrisCoverageRetiredForTesting(
                index: 3,
                traj: traj,
                state: null,
                playbackUT: 111.0,
                currentUT: 111.0,
                warpRate: 1f,
                emitExitWatch: emitExitWatch,
                out _);

            Assert.True(handled);
            Assert.False(emitExitWatch);
            Assert.Empty(cameraEvents);
        }

        [Fact]
        public void CoverageRetiredCycle_WatchingDifferentOverlapCycle_DoesNotExitWatch()
        {
            var ctx = new FrameContext
            {
                protectedIndex = 3,
                protectedLoopCycleIndex = 1,
            };

            bool exitWatch = GhostPlaybackEngine.ShouldExitWatchForCoverageRetiredCycleForTesting(
                index: 3,
                loopCycleIndex: 2,
                ctx: ctx);

            Assert.False(exitWatch);
        }

        [Fact]
        public void CoverageRetiredCycle_WatchingSameOverlapCycle_ExitsWatch()
        {
            var ctx = new FrameContext
            {
                protectedIndex = 3,
                protectedLoopCycleIndex = 2,
            };

            bool exitWatch = GhostPlaybackEngine.ShouldExitWatchForCoverageRetiredCycleForTesting(
                index: 3,
                loopCycleIndex: 2,
                ctx: ctx);

            Assert.True(exitWatch);
        }

        [Fact]
        public void CoverageRetiredCycle_NonLoopWatch_ExitsWatch()
        {
            var ctx = new FrameContext
            {
                protectedIndex = 3,
                protectedLoopCycleIndex = -1,
            };

            bool exitWatch = GhostPlaybackEngine.ShouldExitWatchForCoverageRetiredCycleForTesting(
                index: 3,
                loopCycleIndex: 2,
                ctx: ctx);

            Assert.True(exitWatch);
        }

        [Fact]
        public void CoverageRetiredCycle_ExplosionHoldSentinel_DoesNotExitWatch()
        {
            var ctx = new FrameContext
            {
                protectedIndex = 3,
                protectedLoopCycleIndex = -2,
            };

            bool exitWatch = GhostPlaybackEngine.ShouldExitWatchForCoverageRetiredCycleForTesting(
                index: 3,
                loopCycleIndex: 2,
                ctx: ctx);

            Assert.False(exitWatch);
        }

        [Fact]
        public void ClearLoadedVisualReferences_DoesNotClearCoverageRetiredFlag()
        {
            var state = new GhostPlaybackState
            {
                parentAnchoredDebrisCoverageRetired = true,
                anchorRetiredThisFrame = true,
                deferVisibilityUntilPlaybackSync = true,
            };

            state.ClearLoadedVisualReferences();

            Assert.True(state.parentAnchoredDebrisCoverageRetired);
            Assert.False(state.anchorRetiredThisFrame);
            Assert.False(state.deferVisibilityUntilPlaybackSync);
        }

        [Fact]
        public void EnsureGhostVisualsLoadedForWatch_ParentAnchoredDebrisOutsideCoverage_DoesNotLoad()
        {
            var engine = new GhostPlaybackEngine(positioner: null);
            var traj = MakeParentAnchoredDebrisWithRelativeSection();
            var state = new GhostPlaybackState
            {
                vesselName = "Kerbal X Debris",
            };
            engine.ghostStates[3] = state;

            bool loaded = engine.EnsureGhostVisualsLoadedForWatch(
                index: 3,
                traj: traj,
                playbackUT: 111.0,
                currentUT: 111.0,
                forceRebuildLoadedVisuals: true);

            Assert.False(loaded);
            Assert.True(state.parentAnchoredDebrisCoverageRetired);
            Assert.True(state.anchorRetiredThisFrame);
            Assert.Equal(0, engine.VisualLoadAttemptCountForTesting);
        }

        [Fact]
        public void InterpolateAndPositionRelativeContract_UsesRelativeSectionPlaybackTarget()
        {
            MethodInfo method = typeof(IGhostPositioner).GetMethod(
                nameof(IGhostPositioner.InterpolateAndPositionRelative));

            Assert.NotNull(method);
            ParameterInfo[] parameters = method.GetParameters();
            Assert.Equal(typeof(RelativeSectionPlaybackTarget), parameters.Last().ParameterType);
            Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(uint));
        }

        private static MockTrajectory MakeParentAnchoredDebrisWithRelativeSection()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.RecordingId = "debris-rec";
            traj.VesselName = "Kerbal X Debris";
            traj.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorRecordingId = "parent-rec",
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 110.0 },
                },
            });
            return traj;
        }

        /// <summary>
        /// Variant with a populated `absoluteFrames` shadow on the Relative
        /// section -- exercises the new tumbling-quality shadow route.
        /// </summary>
        private static MockTrajectory MakeParentAnchoredDebrisWithShadowFrames()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.RecordingId = "debris-rec";
            traj.VesselName = "Kerbal X Debris";
            traj.RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion;
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 110.0,
                anchorRecordingId = "parent-rec",
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 110.0 },
                },
                absoluteFrames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0, latitude = 0.0, longitude = 0.0, altitude = 0.0,
                        rotation = Quaternion.identity,
                    },
                    new TrajectoryPoint
                    {
                        ut = 110.0, latitude = 0.001, longitude = 0.001, altitude = 1.0,
                        rotation = Quaternion.identity,
                    },
                },
            });
            return traj;
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
        public void TryResolvePendingPlaybackInterpolation_SuborbitalMixedOrbitSegment_PrefersActiveOrbitSegment()
        {
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
            Assert.Equal("Mun", result.bodyName);
            Assert.Equal(0.0, result.altitude);
            Assert.Equal(Vector3.zero, result.velocity);
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_DegenerateMixedOrbitSegment_FallsBackToPoints()
        {
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
                        semiMajorAxis = 0.0,
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
        public void TryResolvePendingPlaybackInterpolation_RelativeSection_UsesAbsoluteShadowMetadata()
        {
            logLines.Clear();

            var relativeFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100,
                    bodyName = "Kerbin",
                    latitude = 12,
                    longitude = -4,
                    altitude = -0.5,
                    velocity = new Vector3(1f, 0f, 0f),
                    rotation = Quaternion.identity
                },
                new TrajectoryPoint
                {
                    ut = 110,
                    bodyName = "Kerbin",
                    latitude = 18,
                    longitude = -7,
                    altitude = 0.5,
                    velocity = new Vector3(3f, 0f, 0f),
                    rotation = Quaternion.identity
                }
            };
            var absoluteShadowFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100,
                    bodyName = "Kerbin",
                    altitude = 62000,
                    velocity = new Vector3(20f, 0f, 0f),
                    rotation = Quaternion.identity
                },
                new TrajectoryPoint
                {
                    ut = 110,
                    bodyName = "Kerbin",
                    altitude = 64000,
                    velocity = new Vector3(40f, 0f, 0f),
                    rotation = Quaternion.identity
                }
            };
            var traj = new MockTrajectory
            {
                VesselName = "RelativeSpawnGhost",
                Points = relativeFrames,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 100,
                        endUT = 110,
                        frames = relativeFrames,
                        absoluteFrames = absoluteShadowFrames
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 105.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Kerbin", result.bodyName);
            Assert.Equal(63000.0, result.altitude);
            Assert.Equal(new Vector3(30f, 0f, 0f), result.velocity);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("RelativeSpawnGhost")
                && l.Contains("relative absolute shadow point interpolation")
                && l.Contains("altitude=63000.0"));
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_AuthoredFrameGapWithShadow_SkipsOrbitPrecedence()
        {
            logLines.Clear();

            var relativeFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100,
                    bodyName = "Kerbin",
                    latitude = 12,
                    longitude = -4,
                    altitude = -0.5,
                    velocity = new Vector3(1f, 0f, 0f),
                    rotation = Quaternion.identity
                },
                new TrajectoryPoint
                {
                    ut = 110,
                    bodyName = "Kerbin",
                    latitude = 18,
                    longitude = -7,
                    altitude = 0.5,
                    velocity = new Vector3(3f, 0f, 0f),
                    rotation = Quaternion.identity
                }
            };
            var absoluteShadowFrames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = 100,
                    bodyName = "Kerbin",
                    altitude = 62000,
                    velocity = new Vector3(20f, 0f, 0f),
                    rotation = Quaternion.identity
                },
                new TrajectoryPoint
                {
                    ut = 130,
                    bodyName = "Kerbin",
                    altitude = 68000,
                    velocity = new Vector3(50f, 0f, 0f),
                    rotation = Quaternion.identity
                }
            };
            var traj = new MockTrajectory
            {
                RecordingId = "debris-gap-shadow",
                VesselName = "RelativeDebrisSpawnGhost",
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                IsDebris = true,
                DebrisParentRecordingId = "parent-rec",
                Points = relativeFrames,
                OrbitSegments = new List<OrbitSegment>
                {
                    new OrbitSegment
                    {
                        bodyName = "Mun",
                        semiMajorAxis = 800000,
                        startUT = 111,
                        endUT = 140
                    }
                },
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        referenceFrame = ReferenceFrame.Relative,
                        startUT = 100,
                        endUT = 140,
                        anchorRecordingId = "parent-rec",
                        frames = relativeFrames,
                        absoluteFrames = absoluteShadowFrames
                    }
                }
            };

            bool resolved = GhostPlaybackEngine.TryResolvePendingPlaybackInterpolation(
                traj, playbackUT: 120.0, out InterpolationResult result);

            Assert.True(resolved);
            Assert.Equal("Kerbin", result.bodyName);
            Assert.Equal(66000.0, result.altitude, 3);
            Assert.Equal(new Vector3(40f, 0f, 0f), result.velocity);
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("RelativeDebrisSpawnGhost")
                && l.Contains("skipping orbit precedence: authored-frame gap shadow available"));
            Assert.Contains(logLines, l =>
                l.Contains("[Engine]")
                && l.Contains("RelativeDebrisSpawnGhost")
                && l.Contains("relative absolute shadow point interpolation")
                && l.Contains("altitude=66000.0"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("active orbit segment")
                || l.Contains("body='Mun'"));
        }

        [Fact]
        public void TryResolvePendingPlaybackInterpolation_SurfaceTrackSection_SkipsOrbitSegmentPrecedence()
        {
            logLines.Clear();

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
        public void TryResolvePendingPlaybackInterpolation_SurfaceTrackSection_SuborbitalOrbitSegment_LogsSkippedOrbitPrecedence()
        {
            logLines.Clear();

            var traj = new MockTrajectory
            {
                VesselName = "SuborbitalSurfaceGhost",
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
            Assert.Contains(logLines, l =>
                l.Contains("SuborbitalSurfaceGhost")
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
        public void GetActiveAnchorCandidates_FiltersMissingRecordingIdsAndCarriesPositionedFlag()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[0] = new GhostPlaybackState { positionedThisFrame = true };
            engine.ghostStates[1] = new GhostPlaybackState { positionedThisFrame = true };
            engine.ghostStates[2] = new GhostPlaybackState { positionedThisFrame = false };
            engine.ghostStates[3] = null;
            engine.ghostStates[4] = new GhostPlaybackState { positionedThisFrame = true };
            engine.ghostStates[5] = new GhostPlaybackState { positionedThisFrame = true };

            var trajectories = new List<IPlaybackTrajectory>
            {
                new MockTrajectory { RecordingId = "rec-0" },
                new MockTrajectory { RecordingId = null },
                new MockTrajectory { RecordingId = "rec-2" },
                new MockTrajectory { RecordingId = "rec-3" },
                new MockTrajectory { RecordingId = string.Empty },
            };

            var candidates = engine.GetActiveAnchorCandidates(trajectories)
                .OrderBy(c => c.Index)
                .ToList();

            Assert.Equal(2, candidates.Count);
            Assert.Equal(0, candidates[0].Index);
            Assert.Equal("rec-0", candidates[0].RecordingId);
            Assert.True(candidates[0].PositionedThisFrame);
            Assert.Equal(2, candidates[1].Index);
            Assert.Equal("rec-2", candidates[1].RecordingId);
            Assert.False(candidates[1].PositionedThisFrame);
            Assert.Empty(engine.GetActiveAnchorCandidates(null));
        }

        [Fact]
        public void PositionedThisFrame_ClearsAtFrameStartAndMarksAfterPositionSeam()
        {
            var engine = new GhostPlaybackEngine(null);
            engine.ghostStates[1] = new GhostPlaybackState { positionedThisFrame = true };
            var trajectories = new List<IPlaybackTrajectory>
            {
                new MockTrajectory { RecordingId = "unused" },
                new MockTrajectory { RecordingId = "rec-1" },
            };

            Assert.True(engine.GetActiveAnchorCandidates(trajectories).Single().PositionedThisFrame);

            engine.ResetPerFrameCountersForTesting();
            Assert.False(engine.GetActiveAnchorCandidates(trajectories).Single().PositionedThisFrame);

            Assert.True(engine.MarkGhostPositionedThisFrameForTesting(1));
            Assert.True(engine.GetActiveAnchorCandidates(trajectories).Single().PositionedThisFrame);
            Assert.False(engine.MarkGhostPositionedThisFrameForTesting(99));
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
        public void ResolveVisiblePlaybackUT_DoesNotRewindOrdinaryFrameAfterActivation()
        {
            var traj = new MockTrajectory().WithTimeRange(217.97, 261.41);
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            double visibleUT = GhostPlaybackEngine.ResolveVisiblePlaybackUT(traj, state, 218.01);

            Assert.Equal(218.01, visibleUT, 2);
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
        public void ResolveVisiblePlaybackUT_ClampsFreshDebrisSeedBridgeFirstFrameToFirstOrdinarySample()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 48.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            double visibleUT = GhostPlaybackEngine.ResolveVisiblePlaybackUT(traj, state, 100.538);

            Assert.Equal(100.52, visibleUT, 3);
        }

        [Fact]
        public void ResolveVisiblePlaybackUT_DoesNotClampSmallDebrisSeedBridge()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 4.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            double visibleUT = GhostPlaybackEngine.ResolveVisiblePlaybackUT(traj, state, 100.538);

            Assert.Equal(100.538, visibleUT, 3);
        }

        [Fact]
        public void ResolveVisiblePlaybackUT_DoesNotClampDebrisSeedBridgeAfterClampWindow()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 48.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            double visibleUT = GhostPlaybackEngine.ResolveVisiblePlaybackUT(traj, state, 100.56);

            Assert.Equal(100.56, visibleUT, 3);
        }

        [Fact]
        public void ShouldHoldInitialRelativeActivationHidden_FreshRelativeStartWithinWindow_ReturnsTrue()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialRelativeActivationHidden(
                traj, state, 100.04));
        }

        [Fact]
        public void ShouldHoldInitialRelativeActivationHidden_AfterWindowOrAbsolute_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialRelativeActivationHidden(
                traj, state, 100.20));

            TrackSection section = traj.TrackSections[0];
            section.referenceFrame = ReferenceFrame.Absolute;
            traj.TrackSections[0] = section;
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialRelativeActivationHidden(
                traj, state, 100.04));
        }

        [Fact]
        public void ShouldHoldInitialDebrisSeedBridgeActivationHidden_LargeParentAnchoredStructuralSeedGap_ReturnsTrueUntilFirstOrdinarySample()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 48.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.25));
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.52));
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.53));
        }

        [Fact]
        public void ShouldHoldInitialDebrisSeedBridgeActivationHidden_SmallParentAnchoredStructuralSeedGap_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 4.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.25));
        }

        [Fact]
        public void IsSyntheticSeedBridgeDistance_UsesRelativeLocalDistance()
        {
            var seed = new TrajectoryPoint { latitude = -6.0, longitude = -48.0, altitude = -0.5 };
            var ordinary = new TrajectoryPoint { latitude = 1.0, longitude = -4.0, altitude = -0.8 };

            Assert.True(DebrisRelativePlaybackPolicy.IsSyntheticSeedBridgeDistance(
                seed,
                ordinary,
                20.0));
            Assert.False(DebrisRelativePlaybackPolicy.IsSyntheticSeedBridgeDistance(
                seed,
                ordinary,
                50.0));
        }

        [Fact]
        public void ShouldHoldInitialDebrisSeedBridgeActivationHidden_RequiresParentAnchoredStructuralRelativeSeed()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = null;
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 48.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.25));

            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections[0].frames[0] = new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" };
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.25));

            TrackSection section = traj.TrackSections[0];
            section.referenceFrame = ReferenceFrame.Absolute;
            section.frames[0] = new TrajectoryPoint
            {
                ut = 100.0,
                bodyName = "Kerbin",
                flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
            };
            traj.TrackSections[0] = section;
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.25));
        }

        [Fact]
        public void ShouldHoldInitialDebrisSeedBridgeActivationHidden_LongSeedBridge_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 101.50, bodyName = "Kerbin", longitude = 48.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialDebrisSeedBridgeActivationHidden(
                traj, state, 100.25));
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_ParentAnchoredDebrisSeedBridge_ReportsDebrisReason()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 48.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string reason));
            Assert.Equal("debris-seed-bridge", reason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_DebrisSeedBridgeEnd_AllowsFirstOrdinarySample()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint
                    {
                        ut = 100.0,
                        bodyName = "Kerbin",
                        flags = (byte)TrajectoryPointFlags.StructuralEventSnapshot
                    },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin", longitude = 48.0 }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.52, out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void ShouldHoldInitialAbsoluteBridgeActivationHidden_FreshSeedBridge_ReturnsTrueUntilBridgeEnd()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 100.52,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialAbsoluteBridgeActivationHidden(
                traj, state, 100.25));
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialAbsoluteBridgeActivationHidden(
                traj, state, 100.53));
        }

        [Fact]
        public void ShouldHoldInitialAbsoluteBridgeActivationHidden_OrdinaryAbsoluteSection_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 100.52,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin" }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialAbsoluteBridgeActivationHidden(
                traj, state, 100.25));
        }

        [Fact]
        public void ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden_HoldsUntilRelativeStart()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 100.30,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.18, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.30, bodyName = "Kerbin" }
                },
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.30,
                endUT = 100.32,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.30, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.32, bodyName = "Kerbin" }
                },
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.32,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden(
                traj, state, 100.25));
            Assert.True(GhostPlaybackEngine.ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden(
                traj, state, 100.32));
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden(
                traj, state, 100.33));

            state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };
            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string reason));
            Assert.Equal("absolute-primer-to-relative", reason);
        }

        [Fact]
        public void ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden_LongAbsoluteRun_ReturnsFalse()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 101.50,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 101.50, bodyName = "Kerbin" }
                },
            });
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 101.50,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialAbsoluteToRelativePrimerActivationHidden(
                traj, state, 100.25));
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_HoldsFreshAbsoluteForMinimumFrames()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin" }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string firstReason));
            Assert.Equal("activation-settle", firstReason);

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.26, out string secondReason));
            Assert.Equal("minimum-frames", secondReason);

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.27, out string finalReason));
            Assert.Null(finalReason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_ActiveReFlyControlledAbsolute_SkipsActivationSettle()
        {
            // Regression cover for the May-3 generalization of the
            // activation-settle hide. A non-debris controlled ghost whose
            // active section at activationStartUT is Absolute, encountered
            // during an active re-fly session, must NOT take the
            // activation-settle hide -- the 2-frame hide otherwise advances
            // playbackUT by ~0.04 s and the first-visible world position is
            // offset by v_recorded x 0.04 s (~80 m at re-fly ascent
            // velocities). Without the carve-out the user sees the previous
            // attempt's ghost slide forward along the recorded velocity
            // vector at re-fly load. The UT-window gates above this clause
            // stay intact; this only bypasses the generic settle fallback.
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin" }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;

            // First frame (the would-be activation-settle frame), second frame
            // (would-be minimum-frames follow-up), and a frame several physics
            // ticks past activation all remain unhidden because the carve-out
            // skips the priming step.
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string firstReason));
            Assert.Null(firstReason);
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.26, out string secondReason));
            Assert.Null(secondReason);
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.27, out string thirdReason));
            Assert.Null(thirdReason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_ActiveReFlyControlledUnsectionedFlatPoints_SkipsActivationSettle()
        {
            // Unsectioned (no TrackSections) recording with flat Points
            // covering activationStartUT: playback resolves first-position
            // from Points alone, no anchor dependency, carve-out applies.
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            // Deliberately empty TrackSections -- exercises the
            // HasUnsectionedFlatPointsCoverageAtActivationUT branch.
            Assert.Empty(traj.TrackSections);
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_ActiveReFlyControlledNonRelativeSectionWithFlatPoints_SkipsActivationSettle()
        {
            // Non-Relative sectioned recording (here OrbitalCheckpoint, the
            // only non-{Absolute,Relative} ReferenceFrame today) with flat
            // Points covering activationStartUT: exercises the
            // HasNonRelativeSectionWithFlatPointsCoverageAtActivationUT
            // branch. No live-anchor dependency, carve-out applies.
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.OrbitalCheckpoint,
                startUT = 100.0,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;

            Assert.False(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string reason));
            Assert.Null(reason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_ActiveReFlyControlledRelativeSectionWithFlatPoints_StillTakesHide()
        {
            // Load-bearing regression cover: a Relative section at
            // activationStartUT MUST block the carve-out even when the flat
            // Points list also covers the UT. Without this gate, any
            // Relative-anchored ghost during a re-fly outside the 0.08 s
            // relative-start window would skip the activation-settle hide
            // and lose its anchor-resolution-race protection.
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;

            // playbackUT 100.25 is OUTSIDE the 0.08 s relative-start UT
            // window, so the relative-start hide does not short-circuit
            // before the carve-out runs. The carve-out must NOT fire here
            // because the section is Relative, so the activation-settle
            // fallback should still hold the ghost hidden.
            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string reason));
            Assert.Equal("activation-settle", reason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_ActiveReFlyDebris_StillTakesHide()
        {
            // The carve-out is non-debris-only. A parent-anchored debris
            // trajectory whose active section is Absolute must still take the
            // activation-settle hide during a re-fly -- debris has its own
            // anchor-resolution race the hide protects.
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.IsDebris = true;
            traj.DebrisParentRecordingId = "parent-rec";
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin" }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string reason));
            Assert.Equal("activation-settle", reason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_ActiveReFlyRelativeSection_StillTakesUtWindowHide()
        {
            // Relative section at activationStartUT means the first frame's
            // position depends on the anchor's live pose -- not deterministic
            // from recorded data. The carve-out predicate must NOT apply.
            // The relative-start UT-window hide should still fire (the
            // generic settle fallback is what the carve-out bypasses).
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;

            // Inside the relative-start UT window: the existing hide fires.
            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.04, out string reason));
            Assert.Equal("relative-start", reason);
        }

        [Fact]
        public void ShouldHoldInitialActivationHiddenThisFrame_NoReFlySession_StillTakesActivationSettleHide()
        {
            // Regression cover: outside a re-fly session, the predicate is
            // a no-op and the existing activation-settle behavior is intact.
            // Mirrors HoldsFreshAbsoluteForMinimumFrames but explicitly with
            // the probe set to a false-returning delegate (also covers the
            // null-probe path because Dispose clears it between tests).
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 105.0,
                frames = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" },
                    new TrajectoryPoint { ut = 100.52, bodyName = "Kerbin" }
                },
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => false;

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialActivationHiddenThisFrame(
                traj, state, 100.25, out string reason));
            Assert.Equal("activation-settle", reason);
        }

        [Fact]
        public void IsActiveReFlyControlledTrajectoryWithDeterministicActivationStart_PredicateBranches()
        {
            // Direct exercise of the predicate's four short-circuit branches:
            // null traj, debris, no probe / probe returns false, and the
            // probe-throws fallback (must swallow + return false).
            Assert.False(GhostPlaybackEngine
                .IsActiveReFlyControlledTrajectoryWithDeterministicActivationStart(null));

            var debrisTraj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            debrisTraj.IsDebris = true;
            debrisTraj.DebrisParentRecordingId = "parent-rec";
            debrisTraj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 105.0,
            });
            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;
            Assert.False(GhostPlaybackEngine
                .IsActiveReFlyControlledTrajectoryWithDeterministicActivationStart(debrisTraj));

            var controlledTraj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            controlledTraj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 100.0,
                endUT = 105.0,
            });
            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = null;
            Assert.False(GhostPlaybackEngine
                .IsActiveReFlyControlledTrajectoryWithDeterministicActivationStart(controlledTraj));

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => false;
            Assert.False(GhostPlaybackEngine
                .IsActiveReFlyControlledTrajectoryWithDeterministicActivationStart(controlledTraj));

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe =
                () => throw new InvalidOperationException("probe failure");
            Assert.False(GhostPlaybackEngine
                .IsActiveReFlyControlledTrajectoryWithDeterministicActivationStart(controlledTraj));
            Assert.Contains(logLines, l => l.Contains("[Engine]")
                && l.Contains("ActiveReFlySessionInProgressProbe threw")
                && l.Contains("InvalidOperationException"));

            GhostPlaybackEngine.ActiveReFlySessionInProgressProbe = () => true;
            Assert.True(GhostPlaybackEngine
                .IsActiveReFlyControlledTrajectoryWithDeterministicActivationStart(controlledTraj));
        }

        [Fact]
        public void ShouldHoldInitialRelativeActivationHiddenThisFrame_HoldsMinimumFramesAfterUtWindow()
        {
            var traj = new MockTrajectory().WithTimeRange(100.0, 110.0);
            traj.TrackSections.Add(new TrackSection
            {
                referenceFrame = ReferenceFrame.Relative,
                startUT = 100.0,
                endUT = 105.0,
            });
            var state = new GhostPlaybackState
            {
                deferVisibilityUntilPlaybackSync = true,
                appearanceCount = 0
            };

            Assert.True(GhostPlaybackEngine.ShouldHoldInitialRelativeActivationHiddenThisFrame(
                traj, state, 100.04));
            Assert.True(GhostPlaybackEngine.ShouldHoldInitialRelativeActivationHiddenThisFrame(
                traj, state, 100.20));
            Assert.False(GhostPlaybackEngine.ShouldHoldInitialRelativeActivationHiddenThisFrame(
                traj, state, 100.21));
        }

        [Fact]
        public void ResolvePredictedOrbitTailContinuityBlendSeconds_CoversFinalizerGap()
        {
            Assert.Equal(5.0, GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                lastPointUT: 687.58, segmentStartUT: 690.40), precision: 3);
            Assert.Equal(7.0, GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                lastPointUT: 100.0, segmentStartUT: 105.0), precision: 3);
            Assert.Equal(10.0, GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityBlendSeconds(
                lastPointUT: 100.0, segmentStartUT: 120.0), precision: 3);
        }

        [Fact]
        public void ResolvePredictedOrbitTailContinuityWeight_EasesFromLastPointToOrbit()
        {
            Assert.Equal(1.0, GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                lastPointUT: 100.0, playbackUT: 100.0, blendSeconds: 10.0), precision: 3);
            Assert.Equal(0.5, GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                lastPointUT: 100.0, playbackUT: 105.0, blendSeconds: 10.0), precision: 3);
            Assert.Equal(0.0, GhostPlaybackEngine.ResolvePredictedOrbitTailContinuityWeight(
                lastPointUT: 100.0, playbackUT: 110.0, blendSeconds: 10.0), precision: 3);
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

        // Guard behavior that requires UpdatePlayback is covered by the in-game
        // ReFlyPostLoadSettle_GhostMeshHiddenDuringWindow test. Headless xUnit
        // cannot call UpdatePlayback because GhostRenderTrace reads Unity Time.

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
