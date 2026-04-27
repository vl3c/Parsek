using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class WatchModeControllerTests
    {
        private static Vector3 OrbitDirectionFromAngles(float pitch, float heading)
        {
            float pitchRad = pitch * Mathf.Deg2Rad;
            float headingRad = heading * Mathf.Deg2Rad;
            return new Vector3(
                Mathf.Sin(headingRad) * Mathf.Cos(pitchRad),
                Mathf.Sin(pitchRad),
                Mathf.Cos(headingRad) * Mathf.Cos(pitchRad));
        }

        private static Recording MakeRecording(
            string id,
            uint vesselPid,
            string treeId = null,
            string childBranchPointId = null,
            bool isDebris = false)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                VesselPersistentId = vesselPid,
                TreeId = treeId,
                ChildBranchPointId = childBranchPointId,
                IsDebris = isDebris,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100 },
                    new TrajectoryPoint { ut = 101 }
                }
            };
        }

        [Fact]
        public void WatchRangeHysteresis_AllowsNearCutoffEntryAndExitsAfterBand()
        {
            Assert.True(WatchModeController.IsWithinWatchEntryRange(299_700.0));
            Assert.False(WatchModeController.IsWithinWatchEntryRange(300_000.0));

            Assert.True(WatchModeController.IsWithinWatchExitRange(299_700.0));
            Assert.True(WatchModeController.IsWithinWatchExitRange(304_999.0));
            Assert.False(WatchModeController.ShouldExitWatchForDistance(304_999.0));
            Assert.True(WatchModeController.ShouldExitWatchForDistance(305_000.0));
        }

        [Fact]
        public void PrimeLoopWatchResetState_NullGhost_DoesNotThrow_AndResetsState()
        {
            var state = new GhostPlaybackState
            {
                ghost = null,
                currentZone = RenderingZone.Beyond,
                playbackIndex = 42,
                partEventIndex = 7,
                pauseHidden = true,
                explosionFired = true
            };

            WatchModeController.PrimeLoopWatchResetState(state);

            Assert.Equal(RenderingZone.Physics, state.currentZone);
            Assert.Equal(0, state.playbackIndex);
            Assert.Equal(0, state.partEventIndex);
            Assert.False(state.pauseHidden);
            Assert.False(state.explosionFired);
        }

        [Fact]
        public void ResolveOverlapBridgeRetargetState_PendingWithoutPrimary_KeepsBridge()
        {
            OverlapBridgeRetargetState state = WatchModeController.ResolveOverlapBridgeRetargetState(
                hasPendingBridge: true,
                primaryReady: false,
                bridgeWaitFrames: 1,
                maxBridgeWaitFrames: WatchMode.MaxPendingOverlapBridgeFrames);

            Assert.Equal(OverlapBridgeRetargetState.KeepBridge, state);
        }

        [Fact]
        public void ResolveOverlapBridgeRetargetState_PendingWithPrimary_RetargetsToPrimary()
        {
            OverlapBridgeRetargetState state = WatchModeController.ResolveOverlapBridgeRetargetState(
                hasPendingBridge: true,
                primaryReady: true,
                bridgeWaitFrames: WatchMode.MaxPendingOverlapBridgeFrames,
                maxBridgeWaitFrames: WatchMode.MaxPendingOverlapBridgeFrames);

            Assert.Equal(OverlapBridgeRetargetState.RetargetToPrimary, state);
        }

        [Fact]
        public void ResolveOverlapBridgeRetargetState_NoPendingBridge_DoesNothing()
        {
            OverlapBridgeRetargetState state = WatchModeController.ResolveOverlapBridgeRetargetState(
                hasPendingBridge: false,
                primaryReady: true,
                bridgeWaitFrames: WatchMode.MaxPendingOverlapBridgeFrames,
                maxBridgeWaitFrames: WatchMode.MaxPendingOverlapBridgeFrames);

            Assert.Equal(OverlapBridgeRetargetState.None, state);
        }

        [Fact]
        public void ResolveOverlapBridgeRetargetState_PendingWithoutPrimaryPastBudget_ExitsWatch()
        {
            OverlapBridgeRetargetState state = WatchModeController.ResolveOverlapBridgeRetargetState(
                hasPendingBridge: true,
                primaryReady: false,
                bridgeWaitFrames: WatchMode.MaxPendingOverlapBridgeFrames,
                maxBridgeWaitFrames: WatchMode.MaxPendingOverlapBridgeFrames);

            Assert.Equal(OverlapBridgeRetargetState.ExitWatch, state);
        }

        [Fact]
        public void AdvanceOverlapBridgeWaitFrames_SameFrame_DoesNotIncrementTwice()
        {
            int waitFrames = WatchModeController.AdvanceOverlapBridgeWaitFrames(
                currentWaitFrames: 1,
                currentFrame: 42,
                lastRetryFrame: 42);

            Assert.Equal(1, waitFrames);
        }

        [Fact]
        public void AdvanceOverlapBridgeWaitFrames_NewFrame_IncrementsBudget()
        {
            int waitFrames = WatchModeController.AdvanceOverlapBridgeWaitFrames(
                currentWaitFrames: 1,
                currentFrame: 43,
                lastRetryFrame: 42);

            Assert.Equal(2, waitFrames);
        }

        [Fact]
        public void ProcessWatchEndHoldTimer_DebrisOnlyChild_DoesNotAutoFollowDuringHold()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
            var previousRealtimeNow = WatchModeController.RealtimeNow;
            var previousCurrentUTNow = WatchModeController.CurrentUTNow;
            var previousCurrentWarpRateNow = WatchModeController.CurrentWarpRateNow;
            WatchModeController.RealtimeNow = () => 0f;
            WatchModeController.CurrentUTNow = () => 0.0;
            WatchModeController.CurrentWarpRateNow = () => 1f;

            try
            {
                var branchPoint = new BranchPoint
                {
                    Id = "bp1",
                    Type = BranchPointType.Breakup,
                    ChildRecordingIds = new List<string> { "child-debris" }
                };
                var tree = new RecordingTree
                {
                    Id = "tree1",
                    TreeName = "TestTree",
                    BranchPoints = new List<BranchPoint> { branchPoint }
                };

                var root = MakeRecording("root", 100, treeId: tree.Id, childBranchPointId: branchPoint.Id);
                var debris = MakeRecording("child-debris", 200, treeId: tree.Id, isDebris: true);
                tree.Recordings[root.RecordingId] = root;
                tree.Recordings[debris.RecordingId] = debris;

                RecordingStore.AddCommittedTreeForTesting(tree);
                RecordingStore.AddCommittedInternal(root);
                RecordingStore.AddCommittedInternal(debris);

                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                engine.ghostStates[0] = new GhostPlaybackState();
                engine.ghostStates[1] = new GhostPlaybackState();

                var engineField = typeof(ParsekFlight).GetField("engine",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                engineField.SetValue(host, engine);

                var controller = new WatchModeController(host);

                var watchedRecordingIndexField = typeof(WatchModeController).GetField("watchedRecordingIndex",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIndexField.SetValue(controller, 0);

                var watchedRecordingIdField = typeof(WatchModeController).GetField("watchedRecordingId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIdField.SetValue(controller, root.RecordingId);

                var holdUntilField = typeof(WatchModeController).GetField("watchEndHoldUntilRealTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                holdUntilField.SetValue(controller, float.MaxValue);

                var processMethod = typeof(WatchModeController).GetMethod("ProcessWatchEndHoldTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                bool handled = (bool)processMethod.Invoke(controller, null);

                Assert.True(handled);
                Assert.Equal(0, watchedRecordingIndexField.GetValue(controller));
                Assert.Equal(float.MaxValue, (float)holdUntilField.GetValue(controller));
                Assert.True(engine.ghostStates.ContainsKey(0));
                Assert.True(engine.ghostStates.ContainsKey(1));
            }
            finally
            {
                WatchModeController.RealtimeNow = previousRealtimeNow;
                WatchModeController.CurrentUTNow = previousCurrentUTNow;
                WatchModeController.CurrentWarpRateNow = previousCurrentWarpRateNow;
                RecordingStore.ResetForTesting();
                ParsekLog.SuppressLogging = false;
            }
        }

        [Fact]
        public void ProcessWatchEndHoldTimer_PendingActivationUT_RecomputesDeadlineBeforeExpiry()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
            var previousRealtimeNow = WatchModeController.RealtimeNow;
            var previousCurrentUTNow = WatchModeController.CurrentUTNow;
            var previousCurrentWarpRateNow = WatchModeController.CurrentWarpRateNow;
            WatchModeController.RealtimeNow = () => 5f;
            WatchModeController.CurrentUTNow = () => 120.0;
            WatchModeController.CurrentWarpRateNow = () => 1f;

            try
            {
                var root = MakeRecording("root", 100);
                RecordingStore.AddCommittedInternal(root);

                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                engine.ghostStates[0] = new GhostPlaybackState();

                var engineField = typeof(ParsekFlight).GetField("engine",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                engineField.SetValue(host, engine);

                var controller = new WatchModeController(host);

                var watchedRecordingIndexField = typeof(WatchModeController).GetField("watchedRecordingIndex",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIndexField.SetValue(controller, 0);

                var watchedRecordingIdField = typeof(WatchModeController).GetField("watchedRecordingId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIdField.SetValue(controller, root.RecordingId);

                var holdUntilField = typeof(WatchModeController).GetField("watchEndHoldUntilRealTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                holdUntilField.SetValue(controller, 1f);

                var holdMaxField = typeof(WatchModeController).GetField("watchEndHoldMaxRealTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                holdMaxField.SetValue(controller, 45f);

                var pendingActivationField = typeof(WatchModeController).GetField("watchEndHoldPendingActivationUT",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                pendingActivationField.SetValue(controller, 150.0);

                var processMethod = typeof(WatchModeController).GetMethod("ProcessWatchEndHoldTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                bool handled = (bool)processMethod.Invoke(controller, null);

                Assert.True(handled);
                Assert.Equal(0, watchedRecordingIndexField.GetValue(controller));
                Assert.Equal(37f, (float)holdUntilField.GetValue(controller));
                Assert.Equal(150.0, (double)pendingActivationField.GetValue(controller));
                Assert.True(engine.ghostStates.ContainsKey(0));
            }
            finally
            {
                WatchModeController.RealtimeNow = previousRealtimeNow;
                WatchModeController.CurrentUTNow = previousCurrentUTNow;
                WatchModeController.CurrentWarpRateNow = previousCurrentWarpRateNow;
                RecordingStore.ResetForTesting();
                ParsekLog.SuppressLogging = false;
            }
        }

        [Fact]
        public void ProcessWatchEndHoldTimer_PendingActivationUTReached_AddsPostActivationGrace()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
            var previousRealtimeNow = WatchModeController.RealtimeNow;
            var previousCurrentUTNow = WatchModeController.CurrentUTNow;
            var previousCurrentWarpRateNow = WatchModeController.CurrentWarpRateNow;
            WatchModeController.RealtimeNow = () => 5f;
            WatchModeController.CurrentUTNow = () => 150.0;
            WatchModeController.CurrentWarpRateNow = () => 1f;

            try
            {
                var root = MakeRecording("root", 100);
                RecordingStore.AddCommittedInternal(root);

                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                engine.ghostStates[0] = new GhostPlaybackState();

                var engineField = typeof(ParsekFlight).GetField("engine",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                engineField.SetValue(host, engine);

                var controller = new WatchModeController(host);

                var watchedRecordingIndexField = typeof(WatchModeController).GetField("watchedRecordingIndex",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIndexField.SetValue(controller, 0);

                var watchedRecordingIdField = typeof(WatchModeController).GetField("watchedRecordingId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIdField.SetValue(controller, root.RecordingId);

                var holdUntilField = typeof(WatchModeController).GetField("watchEndHoldUntilRealTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                holdUntilField.SetValue(controller, 1f);

                var holdMaxField = typeof(WatchModeController).GetField("watchEndHoldMaxRealTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                holdMaxField.SetValue(controller, 10f);

                var pendingActivationField = typeof(WatchModeController).GetField("watchEndHoldPendingActivationUT",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                pendingActivationField.SetValue(controller, 150.0);

                var processMethod = typeof(WatchModeController).GetMethod("ProcessWatchEndHoldTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                bool handled = (bool)processMethod.Invoke(controller, null);

                Assert.True(handled);
                Assert.Equal(7f, (float)holdUntilField.GetValue(controller));
                Assert.True(double.IsNaN((double)pendingActivationField.GetValue(controller)));
                Assert.Equal(0, watchedRecordingIndexField.GetValue(controller));
                Assert.True(engine.ghostStates.ContainsKey(0));
            }
            finally
            {
                WatchModeController.RealtimeNow = previousRealtimeNow;
                WatchModeController.CurrentUTNow = previousCurrentUTNow;
                WatchModeController.CurrentWarpRateNow = previousCurrentWarpRateNow;
                RecordingStore.ResetForTesting();
                ParsekLog.SuppressLogging = false;
            }
        }

        [Fact]
        public void ProcessWatchEndHoldTimer_PendingActivationUT_RecomputedDeadlineRespectsMaxCap()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.SuppressLogging = true;
            var previousRealtimeNow = WatchModeController.RealtimeNow;
            var previousCurrentUTNow = WatchModeController.CurrentUTNow;
            var previousCurrentWarpRateNow = WatchModeController.CurrentWarpRateNow;
            WatchModeController.RealtimeNow = () => 44f;
            WatchModeController.CurrentUTNow = () => 120.0;
            WatchModeController.CurrentWarpRateNow = () => 0.1f;

            try
            {
                var root = MakeRecording("root", 100);
                RecordingStore.AddCommittedInternal(root);

                var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
                var engine = new GhostPlaybackEngine(null);
                engine.ghostStates[0] = new GhostPlaybackState();

                var engineField = typeof(ParsekFlight).GetField("engine",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                engineField.SetValue(host, engine);

                var controller = new WatchModeController(host);

                var watchedRecordingIndexField = typeof(WatchModeController).GetField("watchedRecordingIndex",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIndexField.SetValue(controller, 0);

                var watchedRecordingIdField = typeof(WatchModeController).GetField("watchedRecordingId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                watchedRecordingIdField.SetValue(controller, root.RecordingId);

                var holdUntilField = typeof(WatchModeController).GetField("watchEndHoldUntilRealTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                holdUntilField.SetValue(controller, 1f);

                var holdMaxField = typeof(WatchModeController).GetField("watchEndHoldMaxRealTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                holdMaxField.SetValue(controller, 45f);

                var pendingActivationField = typeof(WatchModeController).GetField("watchEndHoldPendingActivationUT",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                pendingActivationField.SetValue(controller, 150.0);

                var processMethod = typeof(WatchModeController).GetMethod("ProcessWatchEndHoldTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                bool handled = (bool)processMethod.Invoke(controller, null);

                Assert.True(handled);
                Assert.Equal(45f, (float)holdUntilField.GetValue(controller));
                Assert.Equal(150.0, (double)pendingActivationField.GetValue(controller));
                Assert.Equal(0, watchedRecordingIndexField.GetValue(controller));
                Assert.True(engine.ghostStates.ContainsKey(0));
            }
            finally
            {
                WatchModeController.RealtimeNow = previousRealtimeNow;
                WatchModeController.CurrentUTNow = previousCurrentUTNow;
                WatchModeController.CurrentWarpRateNow = previousCurrentWarpRateNow;
                RecordingStore.ResetForTesting();
                ParsekLog.SuppressLogging = false;
            }
        }

        [Fact]
        public void ResolveSwitchCameraMode_AutoMode_ChoosesHorizonLock()
        {
            WatchCameraMode result = WatchModeController.ResolveSwitchCameraMode(
                WatchCameraMode.Free,
                userModeOverride: false,
                hasAtmosphere: true,
                atmosphereDepth: 70000,
                altitude: 289);

            Assert.Equal(WatchCameraMode.HorizonLocked, result);
        }

        [Fact]
        public void ResolveSwitchCameraMode_AutoMode_ChoosesFreeAboveAtmosphere()
        {
            WatchCameraMode result = WatchModeController.ResolveSwitchCameraMode(
                WatchCameraMode.HorizonLocked,
                userModeOverride: false,
                hasAtmosphere: true,
                atmosphereDepth: 70000,
                altitude: 71000);

            Assert.Equal(WatchCameraMode.Free, result);
        }

        [Fact]
        public void ResolveSwitchCameraMode_UserOverride_KeepsExplicitMode()
        {
            WatchCameraMode result = WatchModeController.ResolveSwitchCameraMode(
                WatchCameraMode.Free,
                userModeOverride: true,
                hasAtmosphere: true,
                atmosphereDepth: 70000,
                altitude: 150);

            Assert.Equal(WatchCameraMode.Free, result);
        }

        [Fact]
        public void PrepareFreshWatchCameraState_AtmosphericGhost_UsesHorizonModeAndCanonicalFraming()
        {
            var currentState = new WatchCameraTransitionState
            {
                Distance = 123f,
                Pitch = -37f,
                Heading = 210f,
                Mode = WatchCameraMode.Free,
                UserModeOverride = true,
                HasTargetRotation = true,
                TargetRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f),
                HasWorldOrbitDirection = true,
                WorldOrbitDirection = new Vector3(1f, 0f, 0f)
            };

            WatchCameraTransitionState result = WatchModeController.PrepareFreshWatchCameraState(
                currentState,
                hasAtmosphere: true,
                atmosphereDepth: 70000,
                altitude: 289);

            Assert.Equal(WatchCameraMode.HorizonLocked, result.Mode);
            Assert.Equal(WatchMode.EntryDistance, result.Distance);
            Assert.Equal(WatchMode.EntryPitchDegrees, result.Pitch);
            Assert.Equal(WatchMode.EntryHeadingDegrees, result.Heading);
            Assert.False(result.UserModeOverride);
            Assert.False(result.HasTargetRotation);
            Assert.False(result.HasWorldOrbitDirection);
        }

        [Fact]
        public void PrepareFreshWatchCameraState_OrbitalGhost_UsesFreeModeAndCanonicalFraming()
        {
            var currentState = new WatchCameraTransitionState
            {
                Distance = 88f,
                Pitch = -5f,
                Heading = 170f,
                Mode = WatchCameraMode.Free,
                UserModeOverride = false
            };

            WatchCameraTransitionState result = WatchModeController.PrepareFreshWatchCameraState(
                currentState,
                hasAtmosphere: true,
                atmosphereDepth: 70000,
                altitude: 71000);

            Assert.Equal(WatchCameraMode.Free, result.Mode);
            Assert.Equal(WatchMode.EntryDistance, result.Distance);
            Assert.Equal(WatchMode.EntryPitchDegrees, result.Pitch);
            Assert.Equal(WatchMode.EntryHeadingDegrees, result.Heading);
            Assert.False(result.HasTargetRotation);
            Assert.False(result.HasWorldOrbitDirection);
        }

        [Fact]
        public void DefaultWatchEntryConstants_AreInDegreeConvention_ProduceExpectedOrbitDirection()
        {
            // Pins the canonical fresh-entry constants to the degree convention that
            // Parsek's OrbitDirectionFromAngles / CompensateCameraAngles expect.
            // KSP's FlightCamera.camPitch / camHdg are in radians; all boundary
            // accesses in WatchModeController.cs convert on read/write. If a future
            // regression drops a Deg2Rad conversion or reinterprets these constants
            // as radians, sin(12 rad) ≈ -0.5366 would break this assertion.
            Vector3 orbit = WatchModeController.OrbitDirectionFromAngles(
                WatchMode.EntryPitchDegrees,
                WatchMode.EntryHeadingDegrees);

            // 12° pitch, 0° heading → (sin(0°)*cos(12°), sin(12°), cos(0°)*cos(12°))
            Assert.InRange(orbit.x, -0.0001f, 0.0001f);
            Assert.InRange(orbit.y, 0.2079f - 0.0001f, 0.2079f + 0.0001f);   // sin(12°)
            Assert.InRange(orbit.z, 0.9781f - 0.0001f, 0.9781f + 0.0001f);   // cos(12°)

            // Extra belt-and-suspenders: the constants must be in degree range,
            // not radian range. A 12-radian pitch constant slipping through would
            // be ~687° — way outside any sane camera angle.
            Assert.InRange(WatchMode.EntryPitchDegrees, -90f, 90f);
            Assert.InRange(WatchMode.EntryHeadingDegrees, -180f, 180f);
        }

        [Fact]
        public void CompensateTransferredWatchAngles_NoRotationOrWorldDirection_ReturnsRawAngles()
        {
            // Canonical fresh-entry state: the remembered V-toggle state should also
            // have no rotation/world-orbit data, so the restore path must fall through
            // to raw (pitch, hdg) without trying to re-project a stale world direction.
            var state = new WatchCameraTransitionState
            {
                Pitch = 12f,
                Heading = 0f,
                HasTargetRotation = false,
                HasWorldOrbitDirection = false
            };

            var (newPitch, newHeading) = WatchModeController.CompensateTransferredWatchAngles(
                state,
                new Quaternion(0f, 0.7071068f, 0f, 0.7071068f));

            Assert.Equal(12f, newPitch);
            Assert.Equal(0f, newHeading);
        }

        [Fact]
        public void CompensateTransferredWatchAngles_WorldOrbitDirection_PreservesDirection()
        {
            Vector3 worldOrbitDirection = new Vector3(0.31f, 0.22f, 0.92f).normalized;
            var state = new WatchCameraTransitionState
            {
                Pitch = -11f,
                Heading = 147f,
                HasTargetRotation = true,
                TargetRotation = new Quaternion(0f, 1f, 0f, 0f),
                HasWorldOrbitDirection = true,
                WorldOrbitDirection = worldOrbitDirection
            };

            Quaternion newTargetRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f);

            var (newPitch, newHeading) = WatchModeController.CompensateTransferredWatchAngles(
                state,
                newTargetRotation);

            Vector3 resolvedWorldDirection = WatchModeController.RotateVectorByQuaternion(
                newTargetRotation,
                OrbitDirectionFromAngles(newPitch, newHeading));

            Assert.True(
                Vector3.Dot(worldOrbitDirection, resolvedWorldDirection) > 0.999f,
                $"Expected preserved orbit direction, got {resolvedWorldDirection} from ({newPitch}, {newHeading})");
        }

        [Fact]
        public void TryResolveRetargetedWatchAngles_WithCapturedState_ReappliesCompensatedAngles()
        {
            Quaternion newTargetRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f);
            Vector3 worldOrbitDirection = new Vector3(0.31f, 0.22f, 0.92f).normalized;
            var state = new WatchCameraTransitionState
            {
                Pitch = -11f,
                Heading = 147f,
                HasTargetRotation = true,
                TargetRotation = new Quaternion(0f, 1f, 0f, 0f),
                HasWorldOrbitDirection = true,
                WorldOrbitDirection = worldOrbitDirection
            };

            bool result = WatchModeController.TryResolveRetargetedWatchAngles(
                hasCapturedState: true,
                cameraState: state,
                newTargetRotation: newTargetRotation,
                out float appliedPitch,
                out float appliedHeading);

            Assert.True(result);

            Vector3 resolvedWorldDirection = WatchModeController.RotateVectorByQuaternion(
                newTargetRotation,
                OrbitDirectionFromAngles(appliedPitch, appliedHeading));

            Assert.True(
                Vector3.Dot(worldOrbitDirection, resolvedWorldDirection) > 0.999f,
                $"Expected preserved orbit direction, got {resolvedWorldDirection} from ({appliedPitch}, {appliedHeading})");
        }

        [Fact]
        public void TryResolveRetargetedWatchAngles_WithoutCapturedState_LeavesRawAngles()
        {
            var state = new WatchCameraTransitionState
            {
                Pitch = 12f,
                Heading = -33f
            };

            bool result = WatchModeController.TryResolveRetargetedWatchAngles(
                hasCapturedState: false,
                cameraState: state,
                newTargetRotation: new Quaternion(0f, 0.7071068f, 0f, 0.7071068f),
                out float appliedPitch,
                out float appliedHeading);

            Assert.False(result);
            Assert.Equal(12f, appliedPitch);
            Assert.Equal(-33f, appliedHeading);
        }

        [Fact]
        public void TryResolveWorldOrbitDirection_PivotFrameRotatesForwardIntoWorldSpace()
        {
            Quaternion pivotRotation = new Quaternion(0f, 0.7071068f, 0f, 0.7071068f);

            bool result = WatchModeController.TryResolveWorldOrbitDirection(
                pivotRotation,
                pitch: 0f,
                heading: 0f,
                out var worldOrbitDirection);

            Assert.True(result);
            Assert.True(
                Vector3.Dot(Vector3.right, worldOrbitDirection) > 0.999f,
                $"Expected +X world orbit, got {worldOrbitDirection}");
        }

        [Fact]
        public void TryResolveWorldOrbitDirection_PivotFramePreservesPitchComponent()
        {
            Quaternion pivotRotation = new Quaternion(0f, 0f, 0f, 1f);

            bool result = WatchModeController.TryResolveWorldOrbitDirection(
                pivotRotation,
                pitch: 30f,
                heading: 0f,
                out var worldOrbitDirection);

            Assert.True(result);
            Assert.True(
                Vector3.Dot(OrbitDirectionFromAngles(30f, 0f), worldOrbitDirection) > 0.999f,
                $"Expected identity-frame orbit, got {worldOrbitDirection}");
        }

        [Fact]
        public void InitializeMapFocusRestoreState_MapAlreadyOpen_PrimesOneShotRestore()
        {
            var (lastMapViewEnabled, pendingMapFocusRestore) =
                WatchModeController.InitializeMapFocusRestoreState(mapViewEnabled: true);

            Assert.True(lastMapViewEnabled);
            Assert.True(pendingMapFocusRestore);
        }

        [Fact]
        public void AdvanceMapFocusRestoreState_ReopeningMap_RearmsRestoreAttempt()
        {
            var (lastMapViewEnabled, pendingMapFocusRestore, shouldAttemptRestore) =
                WatchModeController.AdvanceMapFocusRestoreState(
                    lastMapViewEnabled: false,
                    pendingMapFocusRestore: false,
                    mapViewEnabled: true);

            Assert.True(lastMapViewEnabled);
            Assert.True(pendingMapFocusRestore);
            Assert.True(shouldAttemptRestore);
        }

        [Fact]
        public void AdvanceMapFocusRestoreState_MapStillOpenWithoutPendingRestore_DoesNotRefocusAgain()
        {
            var (lastMapViewEnabled, pendingMapFocusRestore, shouldAttemptRestore) =
                WatchModeController.AdvanceMapFocusRestoreState(
                    lastMapViewEnabled: true,
                    pendingMapFocusRestore: false,
                    mapViewEnabled: true);

            Assert.True(lastMapViewEnabled);
            Assert.False(pendingMapFocusRestore);
            Assert.False(shouldAttemptRestore);
        }

        [Fact]
        public void CanRestoreMapFocus_MissingMapObject_StaysDeferred()
        {
            bool canRestore = WatchModeController.CanRestoreMapFocus(
                ghostPid: 123u,
                hasGhostVessel: true,
                hasMapObject: false,
                hasPlanetariumCamera: true);

            Assert.False(canRestore);
        }

        [Fact]
        public void ClassifyMapFocusRestore_ReturnsSpecificMissingReason()
        {
            Assert.Equal("no-ghost-pid",
                WatchModeController.ClassifyMapFocusRestore(
                    ghostPid: 0u,
                    hasGhostVessel: false,
                    hasMapObject: false,
                    hasPlanetariumCamera: true));
            Assert.Equal("ghost-vessel-missing",
                WatchModeController.ClassifyMapFocusRestore(
                    ghostPid: 123u,
                    hasGhostVessel: false,
                    hasMapObject: false,
                    hasPlanetariumCamera: true));
            Assert.Equal("map-object-missing",
                WatchModeController.ClassifyMapFocusRestore(
                    ghostPid: 123u,
                    hasGhostVessel: true,
                    hasMapObject: false,
                    hasPlanetariumCamera: true));
            Assert.Equal("planetarium-camera-missing",
                WatchModeController.ClassifyMapFocusRestore(
                    ghostPid: 123u,
                    hasGhostVessel: true,
                    hasMapObject: true,
                    hasPlanetariumCamera: false));
            Assert.Equal("ready",
                WatchModeController.ClassifyMapFocusRestore(
                    ghostPid: 123u,
                    hasGhostVessel: true,
                    hasMapObject: true,
                    hasPlanetariumCamera: true));
        }

        [Fact]
        public void BuildMapFocusRestoreDecisionMessage_IncludesMapIconState()
        {
            string message = WatchModeController.BuildMapFocusRestoreDecisionMessage(
                recordingIndex: 9,
                ghostPid: 123u,
                hasGhostVessel: true,
                hasMapObject: false,
                hasOrbitRenderer: true,
                hasPlanetariumCamera: true,
                reason: "map-object-missing");

            Assert.Contains("rec=#9", message);
            Assert.Contains("ghostPid=123", message);
            Assert.Contains("hasGhostVessel=True", message);
            Assert.Contains("mapObj=False", message);
            Assert.Contains("orbitRenderer=True", message);
            Assert.Contains("planetariumCamera=True", message);
            Assert.Contains("reason=map-object-missing", message);
        }

        [Fact]
        public void ClassifyWatchCameraInfrastructure_ReturnsSpecificMissingReason()
        {
            Assert.Equal("flight-camera-missing",
                WatchModeController.ClassifyWatchCameraInfrastructure(
                    hasFlightCamera: false,
                    hasTransform: false,
                    hasParent: false));
            Assert.Equal("camera-transform-missing",
                WatchModeController.ClassifyWatchCameraInfrastructure(
                    hasFlightCamera: true,
                    hasTransform: false,
                    hasParent: false));
            Assert.Equal("camera-parent-missing",
                WatchModeController.ClassifyWatchCameraInfrastructure(
                    hasFlightCamera: true,
                    hasTransform: true,
                    hasParent: false));
            Assert.Equal("ready",
                WatchModeController.ClassifyWatchCameraInfrastructure(
                    hasFlightCamera: true,
                    hasTransform: true,
                    hasParent: true));
        }

        [Fact]
        public void BuildWatchCameraInfrastructureMessage_IncludesTargetState()
        {
            string message = WatchModeController.BuildWatchCameraInfrastructureMessage(
                recordingIndex: 2,
                recordingId: "rec-watch",
                reason: "camera-parent-missing",
                vesselName: "Watch Vessel",
                cycleIndex: 5,
                scene: "FLIGHT",
                hasState: true,
                hasGhost: true,
                hasCameraPivot: false);

            Assert.Contains("rec=#2", message);
            Assert.Contains("id=rec-watch", message);
            Assert.Contains("vessel=\"Watch Vessel\"", message);
            Assert.Contains("cycle=5", message);
            Assert.Contains("scene=FLIGHT", message);
            Assert.Contains("targetState[state=True ghost=True pivot=False]", message);
            Assert.Contains("reason=camera-parent-missing", message);
        }

        [Fact]
        public void FinalizeAutomaticExit_ClearsEnsureGhostOrbitRenderersLatch()
        {
            // Regression for #377 follow-up review finding: the one-shot
            // EnsureGhostOrbitRenderers latch must clear at watch-session
            // end. Otherwise a fresh watch session that enters while map view
            // is already open inherits a stale `true`, skipping renderer
            // creation and leaving map-focus restore stuck until the player
            // toggles map view.
            var controller = MakeUninitializedWatchModeController();
            controller.ensureGhostOrbitRenderersAttempted = true;

            controller.FinalizeAutomaticExitForTesting();

            Assert.False(controller.ensureGhostOrbitRenderersAttempted);
        }

        [Fact]
        public void TryCommitWatchSessionStart_AfterPriorSessionLeftLatchSet_ClearsLatch()
        {
            // Regression for #377 follow-up review finding: the exit-side
            // reset is not sufficient on its own — a prior session that
            // landed via an alternate teardown path (or a controller state
            // manipulated by some future refactor) could still leave the
            // latch set. TryCommitWatchSessionStart must clear it too so a
            // fresh watch entered while map view is already open still runs
            // one EnsureGhostOrbitRenderers pass.
            var originalRealtimeNow = WatchModeController.RealtimeNow;
            WatchModeController.RealtimeNow = () => 0f;
            try
            {
                var controller = MakeUninitializedWatchModeController();
                controller.ensureGhostOrbitRenderersAttempted = true;

                var rec = new Recording
                {
                    RecordingId = "watchtarget",
                    VesselName = "watchtarget"
                };
                var state = new GhostPlaybackState();

                controller.TryCommitWatchSessionStart(index: 7, rec: rec, loadedState: state);

                Assert.False(controller.ensureGhostOrbitRenderersAttempted);
            }
            finally
            {
                WatchModeController.RealtimeNow = originalRealtimeNow;
            }
        }

        [Fact]
        public void ShouldIgnoreOverlapCycleEvent_NonWatchedCycle_Ignored()
        {
            Assert.True(WatchModeController.ShouldIgnoreOverlapCycleEvent(
                eventCycleIndex: 3, watchedCycleIndex: 5));
        }

        [Fact]
        public void ShouldIgnoreOverlapCycleEvent_WatchedCycle_NotIgnored()
        {
            Assert.False(WatchModeController.ShouldIgnoreOverlapCycleEvent(
                eventCycleIndex: 5, watchedCycleIndex: 5));
        }

        [Fact]
        public void ShouldIgnoreOverlapCycleEvent_ReadyForNextSentinel_IgnoresRealEvent()
        {
            // After a prior ExplosionHoldEnd for the watched cycle, the field sits at
            // -1 (ready-for-next). A subsequent event for a newer real cycle must not
            // re-enter the handler and stomp the bridge/retarget state machine.
            Assert.True(WatchModeController.ShouldIgnoreOverlapCycleEvent(
                eventCycleIndex: 7, watchedCycleIndex: -1));
        }

        [Fact]
        public void ShouldIgnoreOverlapCycleEvent_HoldingSentinel_IgnoresRealEvent()
        {
            // While holding after an explosion on the watched cycle, field is -2.
            // Another overlap cycle expiring must not interrupt the hold.
            Assert.True(WatchModeController.ShouldIgnoreOverlapCycleEvent(
                eventCycleIndex: 4, watchedCycleIndex: -2));
        }

        private static WatchModeController MakeUninitializedWatchModeController()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = new GhostPlaybackEngine(null);
            var engineField = typeof(ParsekFlight).GetField("engine",
                BindingFlags.Instance | BindingFlags.NonPublic);
            engineField.SetValue(host, engine);
            return new WatchModeController(host);
        }
    }
}
