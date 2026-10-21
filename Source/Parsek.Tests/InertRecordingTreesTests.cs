using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// GC-3 (todo GC3-SCHEMA-REJECTED-TREES-LOSE-EARNED-VALUE, owner ruling 2026-10-21 "Keep
    /// them inert, untouched"): a COMMITTED tree the schema gate rejects (another recording
    /// schema generation, older or newer) is kept inert. Its node is written back verbatim,
    /// its recording ids count as known (ledger reconcile, sweeps, the file collector, the
    /// rewind-point reaper), its mission and groups are not pruned, and the load names how
    /// many trees could not load in one screen message. A pass-through, not a migration.
    /// Partial rejection inside a current-generation tree stays as pinned by
    /// <see cref="SchemaRejectPruneChainTests"/>; an active / pending node stays dropped.
    /// </summary>
    [Collection("Sequential")]
    public class InertRecordingTreesTests : IDisposable
    {
        private const string TreeA = "inert-tree-a";
        private const string TreeB = "inert-tree-b";
        private const string RecA1 = "inert-rec-a1";
        private const string RecA2 = "inert-rec-a2";
        private const string RecB1 = "inert-rec-b1";
        private const string GroupA = "Inert Mission A";

        private static readonly int OlderGeneration = RecordingStore.CurrentRecordingSchemaGeneration - 1;
        private static readonly int NewerGeneration = RecordingStore.CurrentRecordingSchemaGeneration + 1;

        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;

        public InertRecordingTreesTests()
        {
            originalScene = HighLogic.LoadedScene;
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            MissionStore.ResetForTesting();
            GroupHierarchyStore.ResetForTesting();
            ParsekFileCollector.ResetForTesting();
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
            ParsekFileCollector.ResetForTesting();
            RecalculationEngine.ClearModules();
            ParsekScenario.ResetInstanceForTesting();
            EffectiveState.ResetCachesForTesting();
            HighLogic.LoadedScene = originalScene;
        }

        // ---------------------------------------------------------------- fixtures

        /// <summary>
        /// A committed RECORDING_TREE node as the ParsekScenario save writes it, with the
        /// tree's and every recording's <c>recordingSchemaGeneration</c> stamped to
        /// <paramref name="treeGeneration"/> / <paramref name="recordingGeneration"/>.
        /// </summary>
        private static ConfigNode BuildTreeNode(string treeId, int treeGeneration, int recordingGeneration,
            params string[] recordingIds)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Inert " + treeId,
                RootRecordingId = recordingIds[0],
            };
            for (int i = 0; i < recordingIds.Length; i++)
            {
                var rec = new Recording
                {
                    RecordingId = recordingIds[i],
                    VesselName = "Vessel " + recordingIds[i],
                    TreeId = treeId,
                    TreeOrder = i,
                    RecordingGroups = new List<string> { GroupA },
                };
                tree.AddOrReplaceRecording(rec);
            }
            var node = new ConfigNode("RECORDING_TREE");
            tree.Save(node);
            node.SetValue("recordingSchemaGeneration", treeGeneration.ToString(CultureInfo.InvariantCulture));
            foreach (ConfigNode recNode in node.GetNodes("RECORDING"))
                recNode.SetValue("recordingSchemaGeneration",
                    recordingGeneration.ToString(CultureInfo.InvariantCulture));
            return node;
        }

        private static ConfigNode Scenario(params ConfigNode[] treeNodes)
        {
            var scenario = new ConfigNode("SCENARIO");
            for (int i = 0; i < treeNodes.Length; i++)
                scenario.AddNode(treeNodes[i].CreateCopy());
            return scenario;
        }

        private static void Load(ConfigNode scenario)
        {
            ParsekScenario.LoadRecordingTrees(scenario, RecordingStore.CommittedRecordings);
        }

        private static ConfigNode Save()
        {
            var saved = new ConfigNode("SCENARIO");
            ParsekScenario.SaveTreeRecordings(saved);
            return saved;
        }

        // ---------------------------------------------------------------- 1. verbatim save

        // catches: SaveTreeRecordings' RemoveNodes("RECORDING_TREE") + committed-only write
        // dropping a tree the schema gate rejected, so the next save loses it for good.
        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        public void RejectedCommittedTree_SurvivesLoadAndSave_ByteEqual(int generationOffset)
        {
            int generation = RecordingStore.CurrentRecordingSchemaGeneration + generationOffset;
            ConfigNode original = BuildTreeNode(TreeA, generation, generation, RecA1, RecA2);

            Load(Scenario(original));
            Assert.Empty(RecordingStore.CommittedTrees);

            ConfigNode saved = Save();
            ConfigNode[] trees = saved.GetNodes("RECORDING_TREE");
            Assert.Single(trees);
            Assert.Equal(original.ToString(), trees[0].ToString());
            Assert.Equal(1, InertRecordingTrees.Count);
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("re-emitted 1 inert RECORDING_TREE node(s) verbatim"));

            // A second load + save of what was written is a fixed point.
            Load(saved);
            ConfigNode savedAgain = Save();
            Assert.Equal(original.ToString(), savedAgain.GetNodes("RECORDING_TREE")[0].ToString());
        }

        // catches: only the tree-level gate counted as "rejected", so a current-generation
        // tree whose every recording carries another generation is still dropped on save.
        [Fact]
        public void TreeWhoseEveryRecordingIsRejected_IsKeptInert()
        {
            ConfigNode original = BuildTreeNode(TreeA,
                RecordingStore.CurrentRecordingSchemaGeneration, OlderGeneration, RecA1, RecA2);

            Load(Scenario(original));

            Assert.Empty(RecordingStore.CommittedTrees);
            ConfigNode[] trees = Save().GetNodes("RECORDING_TREE");
            Assert.Single(trees);
            Assert.Equal(original.ToString(), trees[0].ToString());
            Assert.True(InertRecordingTrees.ContainsTreeId(TreeA));
            Assert.Equal("recordings-generation-older", InertRecordingTrees.Entries[0].Reason);
        }

        [Fact]
        public void Classify_TreeGate_And_PartialRejection()
        {
            ConfigNode older = BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1);
            Assert.Equal("tree-generation-older",
                InertRecordingTrees.ClassifySchemaRejection(older, RecordingTree.Load(older)));

            ConfigNode newer = BuildTreeNode(TreeA, NewerGeneration, NewerGeneration, RecA1);
            Assert.Equal("tree-generation-newer",
                InertRecordingTrees.ClassifySchemaRejection(newer, RecordingTree.Load(newer)));

            ConfigNode missing = BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1);
            missing.RemoveValue("recordingSchemaGeneration");
            Assert.Equal("tree-generation-missing",
                InertRecordingTrees.ClassifySchemaRejection(missing, RecordingTree.Load(missing)));

            // Partial rejection (one leaf of another generation) keeps today's prune: not inert.
            ConfigNode partial = BuildTreeNode(TreeA,
                RecordingStore.CurrentRecordingSchemaGeneration,
                RecordingStore.CurrentRecordingSchemaGeneration, RecA1, RecA2);
            partial.GetNodes("RECORDING")[1].SetValue("recordingSchemaGeneration",
                OlderGeneration.ToString(CultureInfo.InvariantCulture));
            RecordingTree partialLoaded = RecordingTree.Load(partial);
            Assert.Single(partialLoaded.Recordings);
            Assert.Null(InertRecordingTrees.ClassifySchemaRejection(partial, partialLoaded));

            // A current tree with no recordings at all was not rejected by anything.
            var empty = new ConfigNode("RECORDING_TREE");
            empty.AddValue("id", "empty-tree");
            empty.AddValue("recordingSchemaGeneration",
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            Assert.Null(InertRecordingTrees.ClassifySchemaRejection(empty, RecordingTree.Load(empty)));
        }

        // catches: an inert node written next to a committed tree that now carries the same
        // id (two RECORDING_TREE nodes with one id in one save).
        [Fact]
        public void InertNode_IsSkippedWhenACommittedTreeWithTheSameIdExists()
        {
            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1)));
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            var committed = new RecordingTree { Id = TreeA, TreeName = "Current A", RootRecordingId = "cur-1" };
            committed.AddOrReplaceRecording(new Recording { RecordingId = "cur-1", TreeId = TreeA, VesselName = "Cur" });
            RecordingStore.AddCommittedTreeForTesting(committed);

            ConfigNode[] trees = Save().GetNodes("RECORDING_TREE");

            Assert.Single(trees);
            Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture),
                trees[0].GetValue("recordingSchemaGeneration"));
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("skipped inert tree id=" + TreeA)
                && l.Contains("committed tree with the same id"));
        }

        // ---------------------------------------------------------------- 2. ledger

        // catches: Ledger.Reconcile pruning every earning / milestone row tagged to the
        // rejected recordings (BuildKnownRecordingIds did not name them), so the recalculated
        // funds and the achieved milestone drop.
        [Fact]
        public void LedgerRowsTaggedToInertRecordings_SurviveReconcile_AndAreCredited()
        {
            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1, RecA2)));

            Ledger.AddAction(new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 10000f });
            Ledger.AddAction(new GameAction
            {
                UT = 500.0, Type = GameActionType.FundsEarning, FundsAwarded = 2500f,
                FundsSource = FundsEarningSource.Recovery, RecordingId = RecA1,
            });
            Ledger.AddAction(new GameAction
            {
                UT = 600.0, Type = GameActionType.MilestoneAchievement, MilestoneId = "FirstLaunch",
                MilestoneFundsAwarded = 800f, RecordingId = RecA2,
            });

            HashSet<string> valid = ParsekScenario.BuildValidRecordingIdsForKspLoad();
            Ledger.Reconcile(valid, 99999.0);

            Assert.Equal(3, Ledger.Actions.Count);
            Assert.Contains(RecA1, valid);
            Assert.Contains(RecA2, valid);

            var milestones = new MilestonesModule();
            var funds = new FundsModule();
            RecalculationEngine.RegisterModule(milestones, RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(funds, RecalculationEngine.ModuleTier.SecondTier);
            RecalculationEngine.Recalculate(new List<GameAction>(EffectiveState.ComputeELS()));

            Assert.True(milestones.IsMilestoneCredited("FirstLaunch"));
            Assert.Equal(13300.0, funds.GetRunningBalance(), 1);
        }

        // ---------------------------------------------------------------- 3. rewind points

        // catches: RewindPointReaper treating a slot whose recording is missing as closed,
        // so a rewind point of an inert tree is reaped (unlisted, then its file collected).
        [Fact]
        public void RewindPointNamingAnInertRecording_IsNotReapEligible()
        {
            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1, RecA2)));
            var none = new List<RecordingSupersedeRelation>();

            var inertRp = new RewindPoint
            {
                RewindPointId = "rp-inert", BranchPointId = "bp-inert", SessionProvisional = false,
                ChildSlots = new List<ChildSlot> { new ChildSlot { SlotIndex = 0, OriginChildRecordingId = RecA2 } },
            };
            Assert.False(RewindPointReaper.IsReapEligible(inertRp, none));

            // Control: a slot naming a recording nobody knows is still closed (unchanged).
            var orphanRp = new RewindPoint
            {
                RewindPointId = "rp-orphan", BranchPointId = "bp-orphan", SessionProvisional = false,
                ChildSlots = new List<ChildSlot> { new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "gone" } },
            };
            Assert.True(RewindPointReaper.IsReapEligible(orphanRp, none));

            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint> { inertRp, orphanRp },
                RecordingSupersedes = none,
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            RewindPointReaper.ReleaseQuicksaveForTesting = _ => true;
            try
            {
                RewindPointReaper.ReapOrphanedRPs();
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp-inert", scenario.RewindPoints[0].RewindPointId);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("kept 1 rewind point(s) naming inert recordings"));
        }

        // ---------------------------------------------------------------- 4. missions, groups, sweeps, files

        // catches: MissionStore.PruneOrphans dropping the mission of an inert tree because its
        // id is not among the committed trees.
        [Fact]
        public void MissionNamingTheInertTree_IsNotPruned()
        {
            ConfigNode scenarioNode = Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1));
            Load(scenarioNode);
            MissionStore.EnsureDefaultsForTrees(new[] { new RecordingTree { Id = TreeA, TreeName = "Inert A" } });
            Assert.Equal(1, MissionStore.CountForTree(TreeA));

            MissionStore.PruneOrphans(RecordingStore.CommittedTrees,
                ParsekScenario.CollectParkedTreeIdsForMissionPrune(scenarioNode));

            Assert.Equal(1, MissionStore.CountForTree(TreeA));
        }

        // catches: the save-time group hierarchy prune dropping the nesting of an inert tree's
        // groups (no committed recording names them).
        [Fact]
        public void GroupHierarchyOfTheInertTree_IsNotPruned()
        {
            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1)));
            Assert.True(GroupHierarchyStore.SetGroupParent(GroupA, "Program X"));

            GroupHierarchyStore.PruneUnusedHierarchyEntriesFromCommittedRecordings("test");

            Assert.True(GroupHierarchyStore.TryGetGroupParent(GroupA, out string parent));
            Assert.Equal("Program X", parent);
        }

        // catches: the load-time sweep removing supersede / retirement rows that name inert
        // recordings as orphans (they would be gone when the matching build loads the save).
        [Fact]
        public void LoadTimeSweep_KeepsRowsNamingInertRecordings()
        {
            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1, RecA2)));
            var supersede = new RecordingSupersedeRelation
            {
                RelationId = "rsr-inert", OldRecordingId = RecA1, NewRecordingId = RecA2, UT = 100.0,
            };
            var oldSide = new RecordingRewindRetirement
            {
                RetirementId = "rrt-inert", RecordingId = RecA1,
                Reason = RecordingRewindRetirement.RewoundOutOldSideReason,
            };
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(),
                RecordingSupersedes = new List<RecordingSupersedeRelation> { supersede },
                RecordingRewindRetirements = new List<RecordingRewindRetirement> { oldSide },
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();

            LoadTimeSweep.Run();

            Assert.Single(scenario.RecordingSupersedes);
            Assert.Single(scenario.RecordingRewindRetirements);
        }

        // catches: the file collector's memory reference set and the cleanup id sets not
        // naming inert recordings (their sidecars would be quarantined once no save on disk
        // still lists the tree).
        [Fact]
        public void InertRecordingIds_AreKnownToCleanupAndToTheCollector()
        {
            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1, RecA2)));

            Assert.Contains(RecA1, RecordingStore.BuildKnownRecordingIds());
            Assert.Contains(RecA2, RecordingStore.BuildKnownRecordingIdsForCleanup());
            SaveReferences memory = ParsekFileCollector.BuildMemoryReferences(null);
            Assert.Contains(RecA1, memory.RecordingIds);
            Assert.Contains(RecA2, memory.RecordingIds);
        }

        // ---------------------------------------------------------------- 5. lifetime

        // catches: the holder surviving a cold reset (a later save would write another save's
        // inert trees) or LoadRecordingTrees appending instead of replacing.
        [Fact]
        public void ColdReset_ClearsTheHolder_AndTheNextLoadReplacesIt()
        {
            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1)));
            Assert.True(InertRecordingTrees.ContainsTreeId(TreeA));

            ParsekScenario.ResetSaveScopedSessionState("test-cold");
            Assert.Equal(0, InertRecordingTrees.Count);
            Assert.False(InertRecordingTrees.ContainsRecordingId(RecA1));

            Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1)));
            Load(Scenario(BuildTreeNode(TreeB, NewerGeneration, NewerGeneration, RecB1)));
            Assert.Equal(1, InertRecordingTrees.Count);
            Assert.False(InertRecordingTrees.ContainsTreeId(TreeA));
            Assert.True(InertRecordingTrees.ContainsTreeId(TreeB));
            Assert.True(InertRecordingTrees.ContainsRecordingId(RecB1));
            Assert.False(InertRecordingTrees.ContainsRecordingId(RecA1));
        }

        // ---------------------------------------------------------------- 6. notice

        [Fact]
        public void LoadNotice_IsNullForNone_AndNamesTheCount()
        {
            Assert.Null(InertRecordingTrees.BuildLoadNotice(0));
            Assert.Equal(
                "1 recorded mission was made by a different Parsek version. "
                + "It is kept unchanged in this save but not loaded.",
                InertRecordingTrees.BuildLoadNotice(1));
            Assert.Equal(
                "3 recorded missions were made by a different Parsek version. "
                + "They are kept unchanged in this save but not loaded.",
                InertRecordingTrees.BuildLoadNotice(3));
        }

        // catches: no notice for a load that set trees aside, or one per call (per frame).
        [Fact]
        public void TakeLoadNotice_OncePerLoad_WithAnInfoLineNamingTreesAndGenerations()
        {
            Load(Scenario(
                BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1),
                BuildTreeNode(TreeB, NewerGeneration, NewerGeneration, RecB1)));

            Assert.Equal(InertRecordingTrees.BuildLoadNotice(2), InertRecordingTrees.TakeLoadNotice());
            Assert.Null(InertRecordingTrees.TakeLoadNotice());
            Assert.Contains(logLines, l => l.Contains("[INFO][InertTrees]")
                && l.Contains("2 committed tree(s) from a different recording schema generation kept inert")
                && l.Contains(TreeA + "(generation=" + OlderGeneration.ToString(CultureInfo.InvariantCulture))
                && l.Contains(TreeB + "(generation=" + NewerGeneration.ToString(CultureInfo.InvariantCulture)));

            Load(Scenario());
            Assert.Null(InertRecordingTrees.TakeLoadNotice());
        }

        // ---------------------------------------------------------------- 7. active / pending

        // catches: the inert recording ids counting as "committed recordings exist" in
        // LedgerOrchestrator.OnKspLoad, so a save whose only trees are inert and whose
        // ledger is empty runs the old-save migration (an interpretation of the inert trees).
        [Fact]
        public void SaveOfOnlyInertTrees_DoesNotRunTheOldSaveMigration()
        {
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            try
            {
                Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1, RecA2)));
                var accept = new GameStateEvent
                {
                    ut = 500.0,
                    eventType = GameStateEventType.ContractAccepted,
                    key = "contract-inert",
                    detail = "title=Inert Contract;deadline=NaN;type=PartTest;funds=5000;failFunds=1000;failRep=1"
                };
                GameStateStore.AddEvent(ref accept);
                LedgerOrchestrator.SetMigrateOldSaveEventsRanThisLoadForTesting(false);

                HashSet<string> valid = ParsekScenario.BuildValidRecordingIdsForKspLoad();
                Assert.Contains(RecA1, valid);
                LedgerOrchestrator.OnKspLoad(valid, 1000.0);

                Assert.False(LedgerOrchestrator.GetMigrateOldSaveEventsRanThisLoadForTesting());
                Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                    && l.Contains("old-save migration scope excludes 2 recording id(s) of inert trees"));
            }
            finally
            {
                LedgerOrchestrator.ResetForTesting();
                GameStateStore.ResetForTesting();
                GameStateRecorder.ResetForTesting();
                KspStatePatcher.ResetForTesting();
            }
        }

        // catches: RouteStore's load-time candidate sweeps reading only the committed trees,
        // so a dismissed or already-prompted supply-run candidate naming an inert tree is
        // dropped and the matching build re-offers it.
        [Fact]
        public void RouteCandidateSetsNamingAnInertTree_SurviveTheLoadSweeps()
        {
            RouteStore.ResetForTesting();
            try
            {
                Load(Scenario(BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1)));
                RouteStore.DismissCandidateTree(TreeA, "Inert A");
                RouteStore.MarkCandidatePrompted(TreeA, "Inert A");
                RouteStore.DismissCandidateTree("gone-tree", "Gone");

                Assert.Equal(1, RouteStore.SweepStaleDismissedCandidates(RecordingStore.CommittedTrees));
                Assert.Equal(0, RouteStore.SweepStalePromptedCandidates(RecordingStore.CommittedTrees));

                Assert.True(RouteStore.IsCandidateDismissed(TreeA));
                Assert.False(RouteStore.IsCandidateDismissed("gone-tree"));
                Assert.True(RouteStore.IsCandidatePrompted(TreeA));
            }
            finally
            {
                RouteStore.ResetForTesting();
            }
        }

        // Pins today's behavior: an active or pending node of another generation is an
        // uncommitted flight; it is not kept inert, so the next save does not carry it.
        [Fact]
        public void RejectedActiveAndPendingNodes_AreStillDropped_WithALogLine()
        {
            ConfigNode active = BuildTreeNode(TreeA, OlderGeneration, OlderGeneration, RecA1);
            active.AddValue("isActive", "True");
            ConfigNode pending = BuildTreeNode(TreeB, NewerGeneration, NewerGeneration, RecB1);
            pending.AddValue("isPending", "True");

            Load(Scenario(active, pending));

            Assert.Equal(0, InertRecordingTrees.Count);
            Assert.Empty(Save().GetNodes("RECORDING_TREE"));
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("active tree node id=" + TreeA)
                && l.Contains("not kept"));
            Assert.Contains(logLines, l => l.Contains("[InertTrees]")
                && l.Contains("pending tree node id=" + TreeB)
                && l.Contains("not kept"));
        }
    }
}
