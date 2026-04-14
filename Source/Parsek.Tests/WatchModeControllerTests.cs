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
        public void PrepareFreshWatchCameraState_AtmosphericGhost_UsesHorizonModeAndDefaultEntryDistance()
        {
            var currentState = new WatchCameraTransitionState
            {
                Distance = 123f,
                Pitch = 12f,
                Heading = 34f,
                Mode = WatchCameraMode.Free,
                UserModeOverride = false
            };

            WatchCameraTransitionState result = WatchModeController.PrepareFreshWatchCameraState(
                currentState,
                hasAtmosphere: true,
                atmosphereDepth: 70000,
                altitude: 289);

            Assert.Equal(WatchCameraMode.HorizonLocked, result.Mode);
            Assert.Equal(WatchModeController.DefaultWatchEntryDistance, result.Distance);
            Assert.Equal(12f, result.Pitch);
            Assert.Equal(34f, result.Heading);
        }

        [Fact]
        public void PrepareFreshWatchCameraState_OrbitalGhost_UsesFreeModeAndDefaultEntryDistance()
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
            Assert.Equal(WatchModeController.DefaultWatchEntryDistance, result.Distance);
            Assert.Equal(-5f, result.Pitch);
            Assert.Equal(170f, result.Heading);
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

    }
}
