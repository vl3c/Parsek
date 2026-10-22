using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner rulings O2 and O3 (2026-10-21, todo LEDGER-RETIREMENT-PATHS-OUTSIDE-REFLY,
    /// game-actions design 3.13): a stock revert removes only the rows the reverted flight
    /// wrote, and a committed recording that fails to load keeps its ledger rows.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerRetirementPathsTests : IDisposable
    {
        private const string TreeId = "o3-tree";
        private const string RootId = "o3-root";
        private const string LeafId = "o3-leaf";

        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;

        public LedgerRetirementPathsTests()
        {
            originalScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MissionStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            Ledger.ResetForTesting();
            MissionStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            HighLogic.LoadedScene = originalScene;
        }

        private static GameAction Science(double ut, string recordingId, float amount)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceEarning,
                RecordingId = recordingId,
                SubjectId = "crewReport@KerbinSrfLandedLaunchPad" + ut.ToString(CultureInfo.InvariantCulture),
                ScienceAwarded = amount,
            };
        }

        private static GameAction FundsEarning(double ut, string recordingId, float amount)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsEarning,
                RecordingId = recordingId,
                FundsAwarded = amount,
                FundsSource = FundsEarningSource.Recovery,
            };
        }

        private static GameAction TechUnlock(double ut, string nodeId, float cost)
        {
            return new GameAction { UT = ut, Type = GameActionType.ScienceSpending, NodeId = nodeId, Cost = cost };
        }

        private static GameAction Rollout(double ut, float cost)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpent = cost,
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
            };
        }

        // ================================================================ O3

        private static ConfigNode BuildTreeNode(params string[] recordingIds)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "O3 tree",
                RootRecordingId = recordingIds[0],
            };
            for (int i = 0; i < recordingIds.Length; i++)
            {
                tree.AddOrReplaceRecording(new Recording
                {
                    RecordingId = recordingIds[i],
                    VesselName = "Vessel " + recordingIds[i],
                    TreeId = TreeId,
                    TreeOrder = i,
                });
            }
            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            return node;
        }

        [Fact]
        public void CollectNamedCommittedRecordingIds_SkipsNotCommittedAndBlankIds()
        {
            var node = new ConfigNode("RECORDING_TREE");
            node.AddNode("RECORDING").AddValue("recordingId", "committed-a");
            ConfigNode provisional = node.AddNode("RECORDING");
            provisional.AddValue("recordingId", "provisional-b");
            provisional.AddValue("mergeState", MergeState.NotCommitted.ToString());
            ConfigNode committedProvisional = node.AddNode("RECORDING");
            committedProvisional.AddValue("recordingId", "cp-c");
            committedProvisional.AddValue("mergeState", MergeState.CommittedProvisional.ToString());
            node.AddNode("RECORDING").AddValue("recordingId", "");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            ParsekScenario.CollectNamedCommittedRecordingIds(node, ids);

            Assert.Equal(new[] { "committed-a", "cp-c" }, ids.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void ComputeLoadDroppedRecordingIds_IsNamedMinusLoaded()
        {
            var named = new HashSet<string>(StringComparer.Ordinal) { RootId, LeafId, "gone" };
            var tree = new RecordingTree { Id = TreeId, RootRecordingId = RootId };
            tree.AddOrReplaceRecording(new Recording { RecordingId = RootId, TreeId = TreeId });
            tree.AddOrReplaceRecording(new Recording { RecordingId = LeafId, TreeId = TreeId });

            HashSet<string> dropped = ParsekScenario.ComputeLoadDroppedRecordingIds(
                named, new List<RecordingTree> { tree });

            Assert.Equal(new[] { "gone" }, dropped.ToArray());
            Assert.Empty(ParsekScenario.ComputeLoadDroppedRecordingIds(null, null));
        }

        // catches: LoadRecordingTrees dropping a leaf whose schema stamp it rejects without
        // telling the ledger load which recording the save named, so its rows are pruned.
        [Fact]
        public void LoadRecordingTrees_PartialReject_ReportsTheDroppedLeaf()
        {
            ConfigNode treeNode = BuildTreeNode(RootId, LeafId);
            treeNode.GetNodes("RECORDING")[1].SetValue("recordingSchemaGeneration",
                (RecordingStore.CurrentRecordingSchemaGeneration - 1).ToString(CultureInfo.InvariantCulture));
            var scenario = new ConfigNode("SCENARIO");
            scenario.AddNode(treeNode);

            ParsekScenario.LoadRecordingTrees(scenario, RecordingStore.CommittedRecordings);

            HashSet<string> dropped = ParsekScenario.TakeLoadDroppedRecordingIds();
            Assert.NotNull(dropped);
            Assert.Equal(new[] { LeafId }, dropped.ToArray());
            Assert.Null(ParsekScenario.TakeLoadDroppedRecordingIds());
        }

        // catches: Ledger.Reconcile pruning the earnings of a committed recording the save
        // named but that failed to load (the recording is gone, its rows went with it).
        [Fact]
        public void KeepRowsOfRecordingsThatFailedToLoad_UntagsThem_AndReconcileKeepsThem()
        {
            Ledger.AddAction(new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 10000f });
            Ledger.AddAction(FundsEarning(500.0, LeafId, 2500f));
            Ledger.AddAction(Science(510.0, LeafId, 12f));
            Ledger.AddAction(FundsEarning(520.0, RootId, 100f));
            Ledger.AddAction(FundsEarning(530.0, "never-named", 50f));

            var valid = new HashSet<string>(StringComparer.Ordinal) { RootId };
            int kept = LedgerOrchestrator.KeepRowsOfRecordingsThatFailedToLoad(
                new[] { LeafId, RootId }, valid);
            Ledger.Reconcile(valid, 99999.0);

            Assert.Equal(2, kept);
            Assert.Equal(4, Ledger.Actions.Count);
            Assert.Equal(2, Ledger.Actions.Count(a => a.RecordingId == null && a.UT >= 500.0 && a.UT <= 510.0));
            // A recording the loaded set still knows keeps its tag.
            Assert.Contains(Ledger.Actions, a => a.RecordingId == RootId);
            // A recording the save never named is pruned as before.
            Assert.DoesNotContain(Ledger.Actions, a => a.UT == 530.0);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("kept 2 ledger row(s) of 1 committed recording(s) that failed to load")
                && l.Contains(LeafId));
        }

        [Fact]
        public void KeepRowsOfRecordingsThatFailedToLoad_NoIds_IsANoOp()
        {
            Ledger.AddAction(FundsEarning(500.0, LeafId, 2500f));
            Assert.Equal(0, LedgerOrchestrator.KeepRowsOfRecordingsThatFailedToLoad(null, null));
            Assert.Equal(0, LedgerOrchestrator.KeepRowsOfRecordingsThatFailedToLoad(
                new[] { LeafId }, new HashSet<string> { LeafId }));
            Assert.Equal(LeafId, Ledger.Actions[0].RecordingId);
        }

        // catches: OnKspLoad never handing the dropped ids to the keep step before Reconcile.
        [Fact]
        public void OnKspLoad_KeepsTheRowsOfADroppedRecording()
        {
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            try
            {
                Ledger.AddAction(new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 10000f });
                Ledger.AddAction(FundsEarning(500.0, LeafId, 2500f));

                LedgerOrchestrator.OnKspLoad(
                    new HashSet<string>(StringComparer.Ordinal), 1000.0,
                    loadDroppedRecordingIds: new[] { LeafId });

                Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FundsEarning
                    && a.RecordingId == null && a.FundsAwarded == 2500f);
            }
            finally
            {
                LedgerOrchestrator.ResetForTesting();
                GameStateStore.ResetForTesting();
                GameStateRecorder.ResetForTesting();
                KspStatePatcher.ResetForTesting();
            }
        }

        // ================================================================ O2

        // catches: the revert prune removing a committed future KSC row (later than the
        // launch, untagged) that the ledger held before the reverted flight started.
        [Fact]
        public void PruneOrphanActionsAfterUT_KeepsRowsTheFlightEntryHeld()
        {
            GameAction futureTech = TechUnlock(900.0, "basicRocketry", 5f);
            Ledger.AddAction(futureTech);
            var keep = ParsekScenario.CollectLedgerActionIds(Ledger.Actions);
            Ledger.AddAction(Science(150.0, null, 3f));

            int removed = Ledger.PruneOrphanActionsAfterUT(100.0, inclusive: false, keepActionIds: keep);

            Assert.Equal(1, removed);
            Assert.Single(Ledger.Actions);
            Assert.Same(futureTech, Ledger.Actions[0]);
            Assert.Contains(logLines, l => l.Contains("[Ledger]") && l.Contains("keptFromFlightEntry=1"));
        }

        // catches: the flight-entry snapshot taken after the rollout, so Revert-to-editor
        // no longer refunds it; and a revert re-snapshot letting a second revert keep rows.
        [Fact]
        public void PruneRevertedFlightLedgerRows_RemovesOnlyRowsAddedSinceTheFlightEntry()
        {
            GameAction committedFutureCredit = FundsEarning(800.0, null, 4000f);
            GameAction committedFutureTech = TechUnlock(850.0, "survivability", 15f);
            GameAction earlierKsc = TechUnlock(50.0, "start", 0f);
            Ledger.AddAction(earlierKsc);
            Ledger.AddAction(committedFutureCredit);
            Ledger.AddAction(committedFutureTech);
            ParsekScenario.RememberFlightEntryLedgerRows("test");

            GameAction rollout = Rollout(100.0, 3000f);
            GameAction padScience = Science(101.0, null, 2f);
            Ledger.AddAction(rollout);
            Ledger.AddAction(padScience);

            int removed = ParsekScenario.PruneRevertedFlightLedgerRows(100.0, inclusive: true);

            Assert.Equal(2, removed);
            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Contains(committedFutureCredit, Ledger.Actions);
            Assert.Contains(committedFutureTech, Ledger.Actions);
            Assert.Contains(earlierKsc, Ledger.Actions);
        }

        [Fact]
        public void PruneRevertedFlightLedgerRows_RevertToLaunch_KeepsTheRolloutAtTheLaunchUT()
        {
            GameAction committedFutureTech = TechUnlock(850.0, "survivability", 15f);
            Ledger.AddAction(committedFutureTech);
            ParsekScenario.RememberFlightEntryLedgerRows("test");
            GameAction rollout = Rollout(100.0, 3000f);
            Ledger.AddAction(rollout);
            Ledger.AddAction(Science(140.0, null, 2f));

            int removed = ParsekScenario.PruneRevertedFlightLedgerRows(100.0, inclusive: false);

            Assert.Equal(1, removed);
            Assert.Contains(rollout, Ledger.Actions);
            Assert.Contains(committedFutureTech, Ledger.Actions);
        }

        [Fact]
        public void PruneRevertedFlightLedgerRows_NoFlightEntry_FallsBackWithAWarn()
        {
            ParsekScenario.SetFlightEntryLedgerActionIdsForTesting(null);
            Ledger.AddAction(TechUnlock(850.0, "survivability", 15f));

            int removed = ParsekScenario.PruneRevertedFlightLedgerRows(100.0, inclusive: false);

            Assert.Equal(1, removed);
            Assert.Empty(Ledger.Actions);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("[Scenario]")
                && l.Contains("no flight-entry ledger rows remembered"));
        }

        [Fact]
        public void CollectLedgerActionIds_SkipsNullRowsAndBlankIds()
        {
            var a = new GameAction();
            var b = new GameAction { ActionId = "" };
            HashSet<string> ids = ParsekScenario.CollectLedgerActionIds(new List<GameAction> { a, null, b });
            Assert.Equal(new[] { a.ActionId }, ids.ToArray());
            Assert.Empty(ParsekScenario.CollectLedgerActionIds(null));
        }
    }
}
