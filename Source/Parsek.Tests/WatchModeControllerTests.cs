using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class WatchModeControllerTests
    {
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
            WatchModeController.RealtimeNow = () => 0f;

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
                RecordingStore.ResetForTesting();
                ParsekLog.SuppressLogging = false;
            }
        }
    }
}
