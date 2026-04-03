using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class PlaybackTrajectoryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public PlaybackTrajectoryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // ===================================================================
        // IPlaybackTrajectory conformance: Recording implements interface
        // ===================================================================

        #region RecordingConformance

        [Fact]
        public void Recording_ImplementsIPlaybackTrajectory()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.NotNull(traj);
        }

        [Fact]
        public void Recording_PointsProperty_ReturnsSameList()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint { ut = 100 });
            IPlaybackTrajectory traj = rec;
            Assert.Same(rec.Points, traj.Points);
            Assert.Single(traj.Points);
        }

        [Fact]
        public void Recording_OrbitSegmentsProperty_ReturnsSameList()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.Same(rec.OrbitSegments, traj.OrbitSegments);
        }

        [Fact]
        public void Recording_TrackSectionsProperty_ReturnsSameList()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.Same(rec.TrackSections, traj.TrackSections);
        }

        [Fact]
        public void Recording_StartUT_ComputedFromPoints()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 42.5, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 99.9, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            IPlaybackTrajectory traj = rec;
            Assert.Equal(42.5, traj.StartUT, 6);
        }

        [Fact]
        public void Recording_EndUT_ComputedFromPoints()
        {
            var rec = new Recording();
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 42.5, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 99.9, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity,
                velocity = Vector3.zero
            });
            IPlaybackTrajectory traj = rec;
            Assert.Equal(99.9, traj.EndUT, 6);
        }

        [Fact]
        public void Recording_StartEndUT_NoPoints_ReturnsZero()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.Equal(0.0, traj.StartUT);
            Assert.Equal(0.0, traj.EndUT);
        }

        [Fact]
        public void Recording_RecordingFormatVersion_Matches()
        {
            var rec = new Recording();
            rec.RecordingFormatVersion = 0;
            IPlaybackTrajectory traj = rec;
            Assert.Equal(0, traj.RecordingFormatVersion);
        }

        [Fact]
        public void Recording_PartEventsProperty_ReturnsSameList()
        {
            var rec = new Recording();
            rec.PartEvents.Add(new PartEvent { ut = 100, eventType = PartEventType.Decoupled, partPersistentId = 1 });
            IPlaybackTrajectory traj = rec;
            Assert.Same(rec.PartEvents, traj.PartEvents);
            Assert.Single(traj.PartEvents);
        }

        [Fact]
        public void Recording_FlagEventsProperty_ReturnsSameList()
        {
            var rec = new Recording();
            rec.FlagEvents.Add(new FlagEvent { ut = 100, flagSiteName = "Flag1" });
            IPlaybackTrajectory traj = rec;
            Assert.Same(rec.FlagEvents, traj.FlagEvents);
            Assert.Single(traj.FlagEvents);
        }

        [Fact]
        public void Recording_GhostVisualSnapshot_Matches()
        {
            var rec = new Recording();
            var node = new ConfigNode("SNAPSHOT");
            rec.GhostVisualSnapshot = node;
            IPlaybackTrajectory traj = rec;
            Assert.Same(node, traj.GhostVisualSnapshot);
        }

        [Fact]
        public void Recording_VesselSnapshot_Matches()
        {
            var rec = new Recording();
            var node = new ConfigNode("VESSEL");
            rec.VesselSnapshot = node;
            IPlaybackTrajectory traj = rec;
            Assert.Same(node, traj.VesselSnapshot);
        }

        [Fact]
        public void Recording_VesselName_Matches()
        {
            var rec = new Recording { VesselName = "My Rocket" };
            IPlaybackTrajectory traj = rec;
            Assert.Equal("My Rocket", traj.VesselName);
        }

        [Fact]
        public void Recording_LoopPlayback_Matches()
        {
            var rec = new Recording { LoopPlayback = true };
            IPlaybackTrajectory traj = rec;
            Assert.True(traj.LoopPlayback);
        }

        [Fact]
        public void Recording_LoopIntervalSeconds_Matches()
        {
            var rec = new Recording { LoopIntervalSeconds = 42.5 };
            IPlaybackTrajectory traj = rec;
            Assert.Equal(42.5, traj.LoopIntervalSeconds, 6);
        }

        [Fact]
        public void Recording_LoopTimeUnit_Matches()
        {
            var rec = new Recording { LoopTimeUnit = LoopTimeUnit.Hour };
            IPlaybackTrajectory traj = rec;
            Assert.Equal(LoopTimeUnit.Hour, traj.LoopTimeUnit);
        }

        [Fact]
        public void Recording_LoopAnchorVesselId_Matches()
        {
            var rec = new Recording { LoopAnchorVesselId = 54321 };
            IPlaybackTrajectory traj = rec;
            Assert.Equal(54321u, traj.LoopAnchorVesselId);
        }

        [Fact]
        public void Recording_TerminalStateValue_Null_Default()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.Null(traj.TerminalStateValue);
        }

        [Fact]
        public void Recording_TerminalStateValue_Destroyed()
        {
            var rec = new Recording { TerminalStateValue = TerminalState.Destroyed };
            IPlaybackTrajectory traj = rec;
            Assert.Equal(TerminalState.Destroyed, traj.TerminalStateValue);
        }

        [Fact]
        public void Recording_SurfacePos_Null_Default()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.Null(traj.SurfacePos);
        }

        [Fact]
        public void Recording_SurfacePos_Populated()
        {
            var rec = new Recording
            {
                SurfacePos = new SurfacePosition
                {
                    body = "Kerbin", latitude = -0.1, longitude = -74.5, altitude = 70
                }
            };
            IPlaybackTrajectory traj = rec;
            Assert.NotNull(traj.SurfacePos);
            Assert.Equal("Kerbin", traj.SurfacePos.Value.body);
        }

        [Fact]
        public void Recording_PlaybackEnabled_DefaultTrue()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.True(traj.PlaybackEnabled);
        }

        [Fact]
        public void Recording_PlaybackEnabled_Disabled()
        {
            var rec = new Recording { PlaybackEnabled = false };
            IPlaybackTrajectory traj = rec;
            Assert.False(traj.PlaybackEnabled);
        }

        [Fact]
        public void Recording_IsDebris_DefaultFalse()
        {
            var rec = new Recording();
            IPlaybackTrajectory traj = rec;
            Assert.False(traj.IsDebris);
        }

        [Fact]
        public void Recording_IsDebris_True()
        {
            var rec = new Recording { IsDebris = true };
            IPlaybackTrajectory traj = rec;
            Assert.True(traj.IsDebris);
        }

        #endregion

        // ===================================================================
        // MockTrajectory conformance — same interface, no Recording
        // ===================================================================

        #region MockTrajectoryConformance

        [Fact]
        public void MockTrajectory_ImplementsIPlaybackTrajectory()
        {
            IPlaybackTrajectory traj = new MockTrajectory();
            Assert.NotNull(traj);
        }

        [Fact]
        public void MockTrajectory_StartEndUT_FromPoints()
        {
            var mock = new MockTrajectory().WithTimeRange(50, 150);
            IPlaybackTrajectory traj = mock;
            Assert.Equal(50, traj.StartUT, 6);
            Assert.Equal(150, traj.EndUT, 6);
        }

        [Fact]
        public void MockTrajectory_EmptyPoints_ZeroStartEnd()
        {
            var mock = new MockTrajectory();
            IPlaybackTrajectory traj = mock;
            Assert.Equal(0, traj.StartUT);
            Assert.Equal(0, traj.EndUT);
        }

        [Fact]
        public void MockTrajectory_AllProperties_Settable()
        {
            var mock = new MockTrajectory
            {
                RecordingFormatVersion = 0,
                VesselName = "Probe",
                LoopPlayback = true,
                LoopIntervalSeconds = -15,
                LoopTimeUnit = LoopTimeUnit.Min,
                LoopAnchorVesselId = 99,
                TerminalStateValue = TerminalState.Orbiting,
                SurfacePos = new SurfacePosition { body = "Mun" },
                PlaybackEnabled = false,
                IsDebris = true,
                GhostVisualSnapshot = new ConfigNode("GV"),
                VesselSnapshot = new ConfigNode("VS"),
            };

            IPlaybackTrajectory traj = mock;
            Assert.Equal(0, traj.RecordingFormatVersion);
            Assert.Equal("Probe", traj.VesselName);
            Assert.True(traj.LoopPlayback);
            Assert.Equal(-15, traj.LoopIntervalSeconds, 6);
            Assert.Equal(LoopTimeUnit.Min, traj.LoopTimeUnit);
            Assert.Equal(99u, traj.LoopAnchorVesselId);
            Assert.Equal(TerminalState.Orbiting, traj.TerminalStateValue);
            Assert.Equal("Mun", traj.SurfacePos.Value.body);
            Assert.False(traj.PlaybackEnabled);
            Assert.True(traj.IsDebris);
            Assert.NotNull(traj.GhostVisualSnapshot);
            Assert.NotNull(traj.VesselSnapshot);
        }

        [Fact]
        public void MockTrajectory_FluentBuilder_Chainable()
        {
            var mock = new MockTrajectory()
                .WithTimeRange(100, 300)
                .WithLoop(5, LoopTimeUnit.Hour);

            Assert.Equal(100, mock.StartUT, 6);
            Assert.Equal(300, mock.EndUT, 6);
            Assert.True(mock.LoopPlayback);
            Assert.Equal(5, mock.LoopIntervalSeconds, 6);
            Assert.Equal(LoopTimeUnit.Hour, mock.LoopTimeUnit);
        }

        #endregion

        // ===================================================================
        // TrajectoryPlaybackFlags — struct field defaults and assignment
        // ===================================================================

        #region TrajectoryPlaybackFlags

        [Fact]
        public void TrajectoryPlaybackFlags_DefaultValues()
        {
            var flags = new TrajectoryPlaybackFlags();
            Assert.False(flags.skipGhost);
            Assert.False(flags.isStandalone);
            Assert.False(flags.isMidChain);
            Assert.Equal(0.0, flags.chainEndUT);
            Assert.False(flags.needsSpawn);
            Assert.False(flags.isActiveChainMember);
            Assert.False(flags.isChainLoopingOrDisabled);
            Assert.Null(flags.segmentLabel);
            Assert.Null(flags.recordingId);
            Assert.Equal(0u, flags.vesselPersistentId);
        }

        [Fact]
        public void TrajectoryPlaybackFlags_AllFieldsSettable()
        {
            var flags = new TrajectoryPlaybackFlags
            {
                skipGhost = true,
                isStandalone = true,
                isMidChain = true,
                chainEndUT = 500.0,
                needsSpawn = true,
                isActiveChainMember = true,
                isChainLoopingOrDisabled = true,
                segmentLabel = "Ascent [Kerbin]",
                recordingId = "abc123",
                vesselPersistentId = 42
            };
            Assert.True(flags.skipGhost);
            Assert.True(flags.isStandalone);
            Assert.True(flags.isMidChain);
            Assert.Equal(500.0, flags.chainEndUT, 6);
            Assert.True(flags.needsSpawn);
            Assert.True(flags.isActiveChainMember);
            Assert.True(flags.isChainLoopingOrDisabled);
            Assert.Equal("Ascent [Kerbin]", flags.segmentLabel);
            Assert.Equal("abc123", flags.recordingId);
            Assert.Equal(42u, flags.vesselPersistentId);
        }

        #endregion

        // ===================================================================
        // FrameContext — struct field defaults and assignment
        // ===================================================================

        #region FrameContext

        [Fact]
        public void FrameContext_DefaultValues()
        {
            var ctx = new FrameContext();
            Assert.Equal(0.0, ctx.currentUT);
            Assert.Equal(0f, ctx.warpRate);
            Assert.Equal(0, ctx.protectedIndex);
            Assert.Equal(0, ctx.externalGhostCount);
            Assert.Equal(0.0, ctx.autoLoopIntervalSeconds);
        }

        [Fact]
        public void FrameContext_AllFieldsSettable()
        {
            var ctx = new FrameContext
            {
                currentUT = 1234.5,
                warpRate = 100f,
                activeVesselPos = new Vector3d(1, 2, 3),
                protectedIndex = 3,
                externalGhostCount = 7,
                autoLoopIntervalSeconds = 15.0
            };
            Assert.Equal(1234.5, ctx.currentUT, 6);
            Assert.Equal(100f, ctx.warpRate);
            Assert.Equal(3, ctx.protectedIndex);
            Assert.Equal(7, ctx.externalGhostCount);
            Assert.Equal(15.0, ctx.autoLoopIntervalSeconds, 6);
        }

        #endregion

        // ===================================================================
        // Event types — construction and field assignment
        // ===================================================================

        #region EventTypes

        [Fact]
        public void GhostLifecycleEvent_CanConstruct()
        {
            var mock = new MockTrajectory().WithTimeRange(100, 200);
            var state = new GhostPlaybackState();
            var flags = new TrajectoryPlaybackFlags { recordingId = "r1" };

            var evt = new GhostLifecycleEvent
            {
                Index = 3,
                Trajectory = mock,
                State = state,
                Flags = flags
            };

            Assert.Equal(3, evt.Index);
            Assert.Same(mock, evt.Trajectory);
            Assert.Same(state, evt.State);
            Assert.Equal("r1", evt.Flags.recordingId);
        }

        [Fact]
        public void PlaybackCompletedEvent_ExtendsGhostLifecycleEvent()
        {
            var mock = new MockTrajectory().WithTimeRange(100, 200);
            var point = new TrajectoryPoint { ut = 200 };

            var evt = new PlaybackCompletedEvent
            {
                Index = 5,
                Trajectory = mock,
                GhostWasActive = true,
                PastEffectiveEnd = false,
                LastPoint = point,
                CurrentUT = 201.5
            };

            Assert.Equal(5, evt.Index);
            Assert.True(evt.GhostWasActive);
            Assert.False(evt.PastEffectiveEnd);
            Assert.Equal(200, evt.LastPoint.ut, 6);
            Assert.Equal(201.5, evt.CurrentUT, 6);

            // Verify it IS a GhostLifecycleEvent
            GhostLifecycleEvent baseEvt = evt;
            Assert.Same(mock, baseEvt.Trajectory);
        }

        [Fact]
        public void LoopRestartedEvent_AllFields()
        {
            var evt = new LoopRestartedEvent
            {
                Index = 2,
                PreviousCycleIndex = 3,
                NewCycleIndex = 4,
                ExplosionFired = true,
                ExplosionPosition = new Vector3(10, 20, 30)
            };

            Assert.Equal(2, evt.Index);
            Assert.Equal(3, evt.PreviousCycleIndex);
            Assert.Equal(4, evt.NewCycleIndex);
            Assert.True(evt.ExplosionFired);
            Assert.Equal(new Vector3(10, 20, 30), evt.ExplosionPosition);
        }

        [Fact]
        public void OverlapExpiredEvent_AllFields()
        {
            var evt = new OverlapExpiredEvent
            {
                Index = 1,
                CycleIndex = 7,
                ExplosionFired = false,
                ExplosionPosition = Vector3.zero
            };

            Assert.Equal(1, evt.Index);
            Assert.Equal(7, evt.CycleIndex);
            Assert.False(evt.ExplosionFired);
            Assert.Equal(Vector3.zero, evt.ExplosionPosition);
        }

        [Fact]
        public void CameraActionEvent_AllFields()
        {
            var mock = new MockTrajectory();
            var evt = new CameraActionEvent
            {
                Index = 0,
                Trajectory = mock,
                Action = CameraActionType.RetargetToNewGhost,
                NewCycleIndex = 5,
                AnchorPosition = new Vector3(1, 2, 3),
                GhostPivot = null, // No Transform in tests
                HoldUntilUT = 1000.0,
                Flags = new TrajectoryPlaybackFlags { segmentLabel = "Test" }
            };

            Assert.Equal(0, evt.Index);
            Assert.Same(mock, evt.Trajectory);
            Assert.Equal(CameraActionType.RetargetToNewGhost, evt.Action);
            Assert.Equal(5, evt.NewCycleIndex);
            Assert.Equal(new Vector3(1, 2, 3), evt.AnchorPosition);
            Assert.Null(evt.GhostPivot);
            Assert.Equal(1000.0, evt.HoldUntilUT, 6);
            Assert.Equal("Test", evt.Flags.segmentLabel);
        }

        [Fact]
        public void CameraActionType_AllValues_Exist()
        {
            // Verify the enum has expected values
            Assert.Equal(0, (int)CameraActionType.ExplosionHoldStart);
            Assert.Equal(1, (int)CameraActionType.ExplosionHoldEnd);
            Assert.Equal(2, (int)CameraActionType.RetargetToNewGhost);
            Assert.Equal(3, (int)CameraActionType.ExitWatch);
        }

        #endregion

        // ===================================================================
        // Recording StartUT/EndUT with ExplicitStartUT/ExplicitEndUT
        // ===================================================================

        #region ExplicitUT

        [Fact]
        public void Recording_ExplicitStartUT_UsedWhenNoPoints()
        {
            var rec = new Recording
            {
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
            };
            IPlaybackTrajectory traj = rec;
            Assert.Equal(500.0, traj.StartUT, 6);
            Assert.Equal(600.0, traj.EndUT, 6);
        }

        [Fact]
        public void Recording_PointsOverrideExplicitUT()
        {
            var rec = new Recording
            {
                ExplicitStartUT = 500.0,
                ExplicitEndUT = 600.0,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 100, bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 200, bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            IPlaybackTrajectory traj = rec;
            // Points take priority over explicit UT
            Assert.Equal(100.0, traj.StartUT, 6);
            Assert.Equal(200.0, traj.EndUT, 6);
        }

        [Fact]
        public void Recording_NoPointsNoExplicit_ReturnsZero()
        {
            var rec = new Recording();
            // ExplicitStartUT defaults to NaN, so falls through to 0
            IPlaybackTrajectory traj = rec;
            Assert.Equal(0.0, traj.StartUT);
            Assert.Equal(0.0, traj.EndUT);
        }

        #endregion
    }
}
