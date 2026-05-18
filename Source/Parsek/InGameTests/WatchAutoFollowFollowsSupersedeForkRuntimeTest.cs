using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live-wiring verification for the watch-camera supersede-walk fix
    /// (logs/2026-05-18_1953_watch-cam-stuck-upper-stage/). Builds the
    /// playtest topology directly in the live <see cref="RecordingStore"/>
    /// + <see cref="ParsekScenario.RecordingSupersedes"/>, registers a fake
    /// <see cref="GhostPlaybackState"/> on the fork only (the chain slot
    /// stays inactive, as it is in production when skipped from playback
    /// by <c>skip=superseded-by-relation</c>), and calls the live
    /// <see cref="ParsekFlight.FindNextWatchTargetFromPolicy"/> entry
    /// point so the real wrapper grabs <c>ParsekScenario.Instance.RecordingSupersedes</c>
    /// and the real <c>WatchModeController.HasActiveGhost</c> queries the
    /// real engine. The unit-test coverage in
    /// <c>BugFixTests.FindNextWatchTargetTests</c> proves the pure logic;
    /// this test pins that the live wiring forwards supersedes correctly
    /// and that <c>HasActiveGhost</c> reads ghostStates at the resolved
    /// index.
    /// </summary>
    public class WatchAutoFollowFollowsSupersedeForkRuntimeTest
    {
        [InGameTest(Category = "Watch", Scene = GameScenes.FLIGHT,
            Description = "FindNextWatchTarget resolves a superseded chain-next to its tree-attached fork through ParsekScenario.RecordingSupersedes")]
        public void WatchAutoFollowFollowsSupersedeFork()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var flight = ParsekFlight.Instance;
            InGameAssert.IsNotNull(flight, "ParsekFlight.Instance is null");
            InGameAssert.IsNotNull(flight.Engine, "ParsekFlight.Instance.Engine is null");

            // Preserve live state so the synthetic fixture doesn't corrupt
            // an in-flight session or the player's save.
            var priorSupersedes = scenario.RecordingSupersedes;

            string treeId = "wat_sf_tree_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string chainId = "wat_sf_chain_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string originId = "wat_sf_origin_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string chainNextId = "wat_sf_chainnext_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string forkId = "wat_sf_fork_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            uint vesselPid = 0x57415453u; // distinctive PID

            var originRec = new Recording
            {
                RecordingId = originId,
                VesselName = "WatchSupersedeFork_Origin",
                TreeId = treeId,
                VesselPersistentId = vesselPid,
                ChainId = chainId,
                ChainIndex = 0,
                ChainBranch = 0,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0 },
                    new TrajectoryPoint { ut = 200.0 },
                },
            };
            var chainNextRec = new Recording
            {
                RecordingId = chainNextId,
                VesselName = "WatchSupersedeFork_ChainNext",
                TreeId = treeId,
                VesselPersistentId = vesselPid,
                ChainId = chainId,
                ChainIndex = 1,
                ChainBranch = 0,
            };
            var forkRec = new Recording
            {
                RecordingId = forkId,
                VesselName = "WatchSupersedeFork_Fork",
                TreeId = treeId,
                VesselPersistentId = vesselPid,
                MergeState = MergeState.Immutable,
            };

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "WatchSupersedeFork_" + treeId,
                BranchPoints = new List<BranchPoint>(),
                Recordings = new Dictionary<string, Recording>
                {
                    [originId] = originRec,
                    [chainNextId] = chainNextRec,
                    [forkId] = forkRec,
                },
            };

            var supersedeRow = new RecordingSupersedeRelation
            {
                OldRecordingId = chainNextId,
                NewRecordingId = forkId,
            };

            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(originRec);
            RecordingStore.AddCommittedInternal(chainNextRec);
            RecordingStore.AddCommittedInternal(forkRec);

            scenario.RecordingSupersedes = new List<RecordingSupersedeRelation> { supersedeRow };

            // Locate committed indices so the assertion is exact.
            int originIdx = -1, chainNextIdx = -1, forkIdx = -1;
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                if (committed[i] == null) continue;
                if (committed[i].RecordingId == originId) originIdx = i;
                else if (committed[i].RecordingId == chainNextId) chainNextIdx = i;
                else if (committed[i].RecordingId == forkId) forkIdx = i;
            }
            InGameAssert.IsTrue(originIdx >= 0, "Origin recording missing from CommittedRecordings");
            InGameAssert.IsTrue(chainNextIdx >= 0, "Chain-next recording missing from CommittedRecordings");
            InGameAssert.IsTrue(forkIdx >= 0, "Fork recording missing from CommittedRecordings");

            // Register a fake ghost on the fork only. In production the chain-
            // next slot is skipped from playback (skip=superseded-by-relation)
            // so its ghostStates entry is absent; we mirror that here.
            var fakeForkGhost = new GhostPlaybackState
            {
                vesselName = forkRec.VesselName,
                recordingId = forkId,
            };
            bool forkSlotHadPriorGhost = flight.Engine.ghostStates.TryGetValue(forkIdx, out var priorForkGhost);
            bool chainNextSlotHadPriorGhost = flight.Engine.ghostStates.TryGetValue(chainNextIdx, out var priorChainNextGhost);
            flight.Engine.ghostStates[forkIdx] = fakeForkGhost;
            flight.Engine.ghostStates.Remove(chainNextIdx);

            try
            {
                ParsekLog.Info("WatchTest",
                    $"WatchAutoFollowFollowsSupersedeFork: synthesized tree={treeId} " +
                    $"origin=#{originIdx} chainNext=#{chainNextIdx} fork=#{forkIdx} " +
                    $"supersedeRows={scenario.RecordingSupersedes.Count} — invoking live FindNextWatchTarget");

                int result = flight.FindNextWatchTargetFromPolicy(originIdx, originRec);

                InGameAssert.AreEqual(forkIdx, result,
                    $"Live FindNextWatchTarget should resolve chain-next #{chainNextIdx} " +
                    $"through supersede to fork #{forkIdx}; got {result}");

                ParsekLog.Info("WatchTest",
                    "WatchAutoFollowFollowsSupersedeFork: live wiring resolved supersede correctly");
            }
            finally
            {
                // Restore ghostStates entries we touched.
                if (forkSlotHadPriorGhost)
                    flight.Engine.ghostStates[forkIdx] = priorForkGhost;
                else
                    flight.Engine.ghostStates.Remove(forkIdx);
                if (chainNextSlotHadPriorGhost)
                    flight.Engine.ghostStates[chainNextIdx] = priorChainNextGhost;

                // Drop the synthetic supersede row + recordings + tree.
                scenario.RecordingSupersedes = priorSupersedes;

                RecordingStore.RemoveCommittedInternal(forkRec);
                RecordingStore.RemoveCommittedInternal(chainNextRec);
                RecordingStore.RemoveCommittedInternal(originRec);

                var trees = RecordingStore.CommittedTrees;
                for (int i = trees.Count - 1; i >= 0; i--)
                    if (trees[i] == tree) trees.RemoveAt(i);
            }
        }
    }
}
