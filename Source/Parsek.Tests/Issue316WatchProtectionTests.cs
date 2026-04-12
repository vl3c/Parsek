using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class Issue316WatchProtectionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Issue316WatchProtectionTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void Issue316_FinalizeAutomaticExitForTesting_RetainsLineageProtectionWithoutSpam()
        {
            var committed = SeedIssue316Recordings();
            var controller = MakeFlightAndController(out _);

            SetWatchedState(controller, 2, committed[2].RecordingId, -1);

            controller.FinalizeAutomaticExitForTesting();

            Assert.False(controller.IsWatchingGhost);
            Assert.Equal(2, controller.WatchProtectionRecordingIndex);
            Assert.Equal(2, controller.WatchProtectionRecordingIndex);
            Assert.Equal(1, CountRetainLogs());
        }

        [Fact]
        public void Issue316_ResolveZoneWatchState_RetainedProtectionKeepsLateDebrisFullFidelity()
        {
            var committed = SeedIssue316Recordings();
            var controller = MakeFlightAndController(out var flight);

            SetWatchedState(controller, 2, committed[2].RecordingId, -1);
            controller.FinalizeAutomaticExitForTesting();

            var lateDebrisState = new GhostPlaybackState
            {
                currentZone = RenderingZone.Physics
            };
            var (_, watchProtectionIndex, isWatchProtectedRecording) =
                flight.ResolveZoneWatchState(4, lateDebrisState, -1);

            var zone = RenderingZoneManager.ClassifyDistance(400000.0);
            var (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                GhostPlaybackLogic.GetZoneRenderingPolicy(zone);
            (shouldHideMesh, shouldSkipPartEvents, shouldSkipPositioning) =
                GhostPlaybackLogic.ApplyWatchedFullFidelityOverride(
                    shouldHideMesh,
                    shouldSkipPartEvents,
                    shouldSkipPositioning,
                    forceFullFidelity: isWatchProtectedRecording);
            var (distanceHideMesh, distanceSkipPartEvents, distanceSkipPositioning,
                distanceSuppressVisualFx, distanceReduceFidelity) =
                GhostPlaybackLogic.ApplyDistanceLodPolicy(
                    shouldHideMesh,
                    shouldSkipPartEvents,
                    shouldSkipPositioning,
                    400000.0,
                    forceFullFidelity: isWatchProtectedRecording);

            Assert.Equal(2, watchProtectionIndex);
            Assert.True(isWatchProtectedRecording);
            Assert.False(distanceHideMesh);
            Assert.False(distanceSkipPartEvents);
            Assert.False(distanceSkipPositioning);
            Assert.False(distanceSuppressVisualFx);
            Assert.False(distanceReduceFidelity);
            Assert.Equal(1, CountRetainLogs());
        }

        [Fact]
        public void Issue316_TryCommitWatchSessionStart_NullLoadedState_PreservesExistingLineageProtection()
        {
            var committed = SeedIssue316Recordings(includeFailedWatchTarget: true);
            var controller = MakeFlightAndController(out _);

            SetWatchedState(controller, 2, committed[2].RecordingId, -1);
            controller.FinalizeAutomaticExitForTesting();
            Assert.Equal(2, controller.WatchProtectionRecordingIndex);

            bool started = controller.TryCommitWatchSessionStart(5, committed[5], loadedState: null);

            Assert.False(started);
            Assert.False(controller.IsWatchingGhost);
            Assert.Equal(2, controller.WatchProtectionRecordingIndex);
            Assert.Equal(1, CountRetainLogs());
        }

        private int CountRetainLogs() =>
            logLines.Count(line => line.Contains("Retaining watched-lineage debris protection"));

        private static List<Recording> SeedIssue316Recordings(bool includeFailedWatchTarget = false)
        {
            RecordingStore.ResetForTesting();

            var root = MakeRecording(
                recordingId: "8582d3de9ee74681856352edc49563c3",
                vesselName: "root",
                vesselPid: 100,
                treeId: "tree-316",
                startUT: 0,
                endUT: 60,
                chainId: "chain-316",
                chainIndex: 0);
            var middle = MakeRecording(
                recordingId: "06efb0cf37ac493ca3e3fa72c8c2d0d0",
                vesselName: "middle",
                vesselPid: 100,
                treeId: "tree-316",
                startUT: 60,
                endUT: 90,
                chainId: "chain-316",
                chainIndex: 1);
            var watched = MakeRecording(
                recordingId: "707490bbcabe495895eecadabed34c2b",
                vesselName: "watched",
                vesselPid: 100,
                treeId: "tree-316",
                startUT: 90,
                endUT: 120,
                chainId: "chain-316",
                chainIndex: 2);
            var debrisDuringHold = MakeRecording(
                recordingId: "aea21e7f06914584be43bebc00ce52f2",
                vesselName: "debris-during-hold",
                vesselPid: 200,
                treeId: "tree-316",
                startUT: 118,
                endUT: 150,
                isDebris: true,
                parentBranchPointId: "bp-10");
            debrisDuringHold.LoopSyncParentIdx = -1;
            var debrisAfterHold = MakeRecording(
                recordingId: "62e4c90147454cc8aec091d2950e6056",
                vesselName: "debris-after-hold",
                vesselPid: 201,
                treeId: "tree-316",
                startUT: 140,
                endUT: 220,
                isDebris: true,
                parentBranchPointId: "bp-11");
            debrisAfterHold.LoopSyncParentIdx = -1;

            var committed = new List<Recording>
            {
                root,
                middle,
                watched,
                debrisDuringHold,
                debrisAfterHold
            };

            if (includeFailedWatchTarget)
            {
                committed.Add(MakeRecording(
                    recordingId: "failed-watch-target",
                    vesselName: "failed-watch-target",
                    vesselPid: 300,
                    treeId: null,
                    startUT: 10,
                    endUT: 20,
                    isDebris: true));
            }

            var tree = new RecordingTree
            {
                Id = "tree-316",
                RootRecordingId = root.RecordingId,
                Recordings = committed
                    .Where(rec => rec.TreeId == "tree-316")
                    .ToDictionary(rec => rec.RecordingId, rec => rec),
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp-10",
                        ParentRecordingIds = new List<string> { root.RecordingId },
                        ChildRecordingIds = new List<string> { debrisDuringHold.RecordingId }
                    },
                    new BranchPoint
                    {
                        Id = "bp-11",
                        ParentRecordingIds = new List<string> { root.RecordingId },
                        ChildRecordingIds = new List<string> { debrisAfterHold.RecordingId }
                    }
                }
            };

            foreach (var rec in committed)
                RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return committed;
        }

        private static Recording MakeRecording(string recordingId, string vesselName, uint vesselPid,
            string treeId, double startUT, double endUT, string chainId = null, int chainIndex = 0,
            bool isDebris = false, string parentBranchPointId = null)
        {
            return new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                VesselPersistentId = vesselPid,
                TreeId = treeId,
                ChainId = chainId,
                ChainIndex = chainIndex,
                IsDebris = isDebris,
                ParentBranchPointId = parentBranchPointId,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT
            };
        }

        private static WatchModeController MakeFlightAndController(out ParsekFlight flight)
        {
            flight = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            var engine = new GhostPlaybackEngine(null);
            SetPrivateField(flight, "engine", engine);

            var controller = new WatchModeController(flight);
            SetPrivateField(flight, "watchMode", controller);
            return controller;
        }

        private static void SetWatchedState(WatchModeController controller, int watchedRecordingIndex,
            string watchedRecordingId, long watchedOverlapCycleIndex)
        {
            SetPrivateField(controller, "watchedRecordingIndex", watchedRecordingIndex);
            SetPrivateField(controller, "watchedRecordingId", watchedRecordingId);
            SetPrivateField(controller, "watchedOverlapCycleIndex", watchedOverlapCycleIndex);
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field.SetValue(target, value);
        }
    }
}
