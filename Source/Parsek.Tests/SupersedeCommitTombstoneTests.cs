using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging: guards the merge-time tombstone step in
    /// <see cref="SupersedeCommit.CommitSupersede"/>.
    ///
    /// <para>
    /// Covers the merge eligibility matrix from the subtree-walk perspective,
    /// parent-subtree exclusion, idempotence, null-scoped / seed / rollout
    /// pass-through, log counters, and the
    /// <see cref="ParsekScenario.TombstoneStateVersion"/> bump that invalidates
    /// the ELS cache.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class SupersedeCommitTombstoneTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SupersedeCommitTombstoneTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
        }

        // ---------- Fixture helpers ----------------------------------------

        private static Recording Rec(string id, string treeId,
            string parentBranchPointId = null, string childBranchPointId = null,
            MergeState state = MergeState.Immutable,
            TerminalState? terminal = null,
            string supersedeTargetId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
                SupersedeTargetId = supersedeTargetId,
            };
        }

        private static BranchPoint Bp(string id, BranchPointType type,
            List<string> parents = null, List<string> children = null)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0.0,
                ParentRecordingIds = parents ?? new List<string>(),
                ChildRecordingIds = children ?? new List<string>(),
            };
        }

        private static void InstallTree(string treeId, List<Recording> recordings,
            List<BranchPoint> branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static ReFlySessionMarker Marker(string originId, string provisionalId)
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_1",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
            };
        }

        // origin + 1 descendant + 1 unrelated-outside, Undock branch.
        private void InstallOriginClosureFixture(string originId, string insideId, string outsideId)
        {
            var origin = Rec(originId, "tree_1", childBranchPointId: "bp_c");
            var inside = Rec(insideId, "tree_1", parentBranchPointId: "bp_c");
            var outside = Rec(outsideId, "tree_1");
            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { originId },
                children: new List<string> { insideId });
            InstallTree("tree_1",
                new List<Recording> { origin, inside, outside },
                new List<BranchPoint> { bp_c });
        }

        private static Recording AddProvisional(string recordingId, string treeId,
            TerminalState? terminal, string supersedeTargetId)
        {
            var provisional = Rec(recordingId, treeId,
                state: MergeState.NotCommitted,
                terminal: terminal,
                supersedeTargetId: supersedeTargetId);
            // Satisfy SupersedeCommit.AppendRelations supersede-target
            // invariant (>=1 trajectory point + non-null terminal).
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional, treeId);
            return provisional;
        }

        private static GameAction KerbalDeath(string recordingId, double ut,
            string kerbalName = "Jeb", string actionId = null)
        {
            return new GameAction
            {
                ActionId = actionId ?? ("act_" + Guid.NewGuid().ToString("N")),
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = kerbalName,
                KerbalEndStateField = KerbalEndState.Dead,
                UT = ut,
            };
        }

        private static GameAction RepPenalty(string recordingId, double ut,
            ReputationPenaltySource source = ReputationPenaltySource.KerbalDeath,
            string actionId = null)
        {
            return new GameAction
            {
                ActionId = actionId ?? ("act_" + Guid.NewGuid().ToString("N")),
                Type = GameActionType.ReputationPenalty,
                RecordingId = recordingId,
                RepPenaltySource = source,
                NominalPenalty = 10f,
                UT = ut,
            };
        }

        private static GameAction ContractComplete(string recordingId, double ut,
            string contractId = "c_1")
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.ContractComplete,
                RecordingId = recordingId,
                ContractId = contractId,
                FundsReward = 1000f,
                RepReward = 5f,
                UT = ut,
            };
        }

        private static GameAction Milestone(string recordingId, double ut,
            string milestoneId = "FirstOrbitKerbin")
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.MilestoneAchievement,
                RecordingId = recordingId,
                MilestoneId = milestoneId,
                MilestoneFundsAwarded = 500f,
                UT = ut,
            };
        }

        private static GameAction ScienceEarning(string recordingId, double ut,
            string subjectId = "crewReport@MunSrfLandedMidlands")
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.ScienceEarning,
                RecordingId = recordingId,
                SubjectId = subjectId,
                ScienceAwarded = 10f,
                SubjectMaxValue = 10f,
                UT = ut,
            };
        }

        private static GameAction ScienceSpending(string recordingId, double ut,
            string nodeId)
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.ScienceSpending,
                RecordingId = recordingId,
                NodeId = nodeId,
                Cost = 5f,
                UT = ut,
            };
        }

        private static GameAction FacilityAction(string recordingId, double ut,
            string facilityId, GameActionType type = GameActionType.FacilityUpgrade)
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = type,
                RecordingId = recordingId,
                FacilityId = facilityId,
                ToLevel = 2,
                UT = ut,
            };
        }

        private static GameAction KerbalRescue(string recordingId, double ut,
            string kerbalName = "Rescuee")
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.KerbalRescue,
                RecordingId = recordingId,
                KerbalName = kerbalName,
                KerbalRole = "Pilot",
                UT = ut,
            };
        }

        // ---------- Positive path ------------------------------------------

        [Fact]
        public void CommitTombstones_SupersededSubtreeKerbalDeath_Tombstoned()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var deathInside = KerbalDeath("rec_inside", 100.0, kerbalName: "Bill");
            var deathOrigin = KerbalDeath("rec_origin", 50.0, kerbalName: "Jeb");
            var deathOutside = KerbalDeath("rec_outside", 200.0, kerbalName: "Bob");
            Ledger.AddAction(deathInside);
            Ledger.AddAction(deathOrigin);
            Ledger.AddAction(deathOutside);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var tombstonedActionIds = new HashSet<string>(
                scenario.LedgerTombstones.Select(t => t.ActionId));
            Assert.Contains(deathOrigin.ActionId, tombstonedActionIds);
            Assert.Contains(deathInside.ActionId, tombstonedActionIds);
            Assert.DoesNotContain(deathOutside.ActionId, tombstonedActionIds);

            // Every tombstone points at the provisional.
            foreach (var t in scenario.LedgerTombstones)
                Assert.Equal("rec_provisional", t.RetiringRecordingId);
        }

        [Fact]
        public void CommitTombstones_BundledRepPenalty_Tombstoned()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var death = KerbalDeath("rec_origin", 100.0);
            var bundled = RepPenalty("rec_origin", 100.2, ReputationPenaltySource.KerbalDeath);
            Ledger.AddAction(death);
            Ledger.AddAction(bundled);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var ids = new HashSet<string>(scenario.LedgerTombstones.Select(t => t.ActionId));
            Assert.Contains(death.ActionId, ids);
            Assert.Contains(bundled.ActionId, ids);
        }

        [Fact]
        public void CommitTombstones_UnbundledRepPenalty_Tombstoned()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var vesselRep = RepPenalty("rec_origin", 100.0, ReputationPenaltySource.Other);
            Ledger.AddAction(vesselRep);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones,
                t => t.ActionId == vesselRep.ActionId);
        }

        // ---------- Broad career scope --------------------------------------

        [Fact]
        public void CommitTombstones_SupersededSubtreeContract_Tombstoned()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var complete = ContractComplete("rec_origin", 100.0);
            Ledger.AddAction(complete);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones,
                t => t.ActionId == complete.ActionId);
        }

        [Fact]
        public void CommitTombstones_TombstonedContractScope_OnlyIncludesRetiredContractIds()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var oldBranchContractId = Guid.NewGuid();
            var unrelatedContractId = Guid.NewGuid();
            var oldBranchComplete = ContractComplete(
                "rec_origin", 100.0, oldBranchContractId.ToString());
            var unrelatedComplete = ContractComplete(
                "rec_outside", 120.0, unrelatedContractId.ToString());
            Ledger.AddAction(oldBranchComplete);
            Ledger.AddAction(unrelatedComplete);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var contractIds = LedgerOrchestrator.BuildTombstonedContractGuidsForPatch();
            Assert.NotNull(contractIds);
            Assert.Single(contractIds);
            Assert.Contains(oldBranchContractId, contractIds);
            Assert.DoesNotContain(unrelatedContractId, contractIds);
        }

        [Fact]
        public void CommitTombstones_ParentSubtreeMilestone_Tombstoned()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var milestone = Milestone("rec_origin", 80.0);
            Ledger.AddAction(milestone);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones,
                t => t.ActionId == milestone.ActionId);
        }

        [Fact]
        public void CommitTombstones_PreservesRolloutBuildCost()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var rollout = new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.FundsSpending,
                RecordingId = "rec_origin",
                FundsSpendingSource = FundsSpendingSource.VesselBuild,
                FundsSpent = 1234f,
                UT = 1.0,
            };
            Ledger.AddAction(rollout);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.DoesNotContain(scenario.LedgerTombstones,
                t => t.ActionId == rollout.ActionId);
        }

        [Fact]
        public void CommitTombstones_ScienceAndContractOldBranch_DoNotBlockRetryReplay()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var oldScience = ScienceEarning("rec_origin", 100.0);
            var retryScience = ScienceEarning("rec_provisional", 120.0);
            var oldComplete = ContractComplete("rec_origin", 101.0, contractId: "contract_1");
            var retryComplete = ContractComplete("rec_provisional", 121.0, contractId: "contract_1");
            Ledger.AddAction(oldScience);
            Ledger.AddAction(retryScience);
            Ledger.AddAction(oldComplete);
            Ledger.AddAction(retryComplete);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var els = EffectiveState.ComputeELS().ToList();
            Assert.DoesNotContain(els, a => a.ActionId == oldScience.ActionId);
            Assert.DoesNotContain(els, a => a.ActionId == oldComplete.ActionId);
            Assert.Contains(els, a => a.ActionId == retryScience.ActionId);
            Assert.Contains(els, a => a.ActionId == retryComplete.ActionId);

            RecalculationEngine.ClearModules();
            RecalculationEngine.RegisterModule(new ScienceModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.RegisterModule(new ContractsModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.Recalculate(els);

            Assert.Equal(10f, retryScience.EffectiveScience);
            Assert.True(retryComplete.Effective);
        }

        [Fact]
        public void CommitTombstones_RecalculatesModulesFromPostTombstoneELS()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var oldScience = ScienceEarning("rec_origin", 100.0, "old-subject");
            var retryScience = ScienceEarning("rec_provisional", 120.0, "retry-subject");
            Ledger.AddAction(oldScience);
            Ledger.AddAction(retryScience);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(0.0, LedgerOrchestrator.Science.GetSubjectCredited("old-subject"), 3);
            Assert.Equal(10.0, LedgerOrchestrator.Science.GetSubjectCredited("retry-subject"), 3);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("refreshing recalculated KSP state")
                && l.Contains("tombstone"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("RecalculateAndPatch complete"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("cutoffUT=null"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[CrewReservations]")
                && l.Contains("after cutoff walk"));
        }

        [Fact]
        public void CommitTombstones_NonTechTombstone_DoesNotPatchTechTree()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var contract = ContractComplete(
                "rec_origin", 100.0, Guid.NewGuid().ToString());
            Ledger.AddAction(contract);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("tech-tree patch enabled"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("no cutoff supplied")
                && l.Contains("skipping tech-tree patch"));
        }

        [Fact]
        public void CommitTombstones_NonTechTombstone_WithPriorTechTombstone_DoesNotPatchTechTree()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var priorUnlock = ScienceSpending("rec_outside", 50.0, "advConstruction");
            Ledger.AddAction(priorUnlock);
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb_prior",
                ActionId = priorUnlock.ActionId,
                RetiringRecordingId = "rec_prior_retry",
                UT = 60.0,
                CreatedRealTime = "2026-05-09T00:00:00Z",
            });

            var contract = ContractComplete(
                "rec_origin", 100.0, Guid.NewGuid().ToString());
            Ledger.AddAction(contract);

            logLines.Clear();
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones,
                t => t.ActionId == contract.ActionId);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("tech-tree patch enabled"));
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("no cutoff supplied")
                && l.Contains("skipping tech-tree patch"));
        }

        [Fact]
        public void CommitTombstones_TombstonedScienceSpending_NotSeededFromLatestTechBaseline()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            const string startingTech = "start";
            const string oldBranchTech = "advConstruction";
            var latestBaseline = new GameStateBaseline { ut = 200.0 };
            latestBaseline.researchedTechIds.Add(startingTech);
            latestBaseline.researchedTechIds.Add(oldBranchTech);
            GameStateStore.AddBaseline(latestBaseline);

            var oldUnlock = ScienceSpending("rec_origin", 100.0, oldBranchTech);
            Ledger.AddAction(oldUnlock);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones,
                t => t.ActionId == oldUnlock.ActionId);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("tech-tree patch enabled")
                && l.Contains("baselineTechExclusions=1")
                && l.Contains("targetCount=1"));
        }

        [Fact]
        public void CommitTombstones_TombstonedFacilityDefaultScope_OnlyIncludesRetiredFacilityIds()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var oldBranchUpgrade = FacilityAction(
                "rec_origin", 100.0, "SpaceCenter/LaunchPad");
            var unrelatedUpgrade = FacilityAction(
                "rec_outside", 120.0, "SpaceCenter/MissionControl");
            Ledger.AddAction(oldBranchUpgrade);
            Ledger.AddAction(unrelatedUpgrade);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var facilityIds = LedgerOrchestrator.BuildTombstonedFacilityIdsForPatch();
            Assert.NotNull(facilityIds);
            Assert.Single(facilityIds);
            Assert.Contains("SpaceCenter/LaunchPad", facilityIds);
            Assert.DoesNotContain("SpaceCenter/MissionControl", facilityIds);
            Assert.Contains(logLines, l =>
                l.Contains("[KspStatePatcher]")
                && l.Contains("scheduled default targets for 1 tombstoned facility id"));
        }

        // ---------- Null scope pass-through --------------------------------

        [Fact]
        public void CommitTombstones_NullScopedAction_NotTombstoned()
        {
            // §7.41: null-scoped actions are never tombstoned, even with a Dead
            // KerbalEndState.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var nullDeath = KerbalDeath(null, 100.0, kerbalName: "KSCKerbal");
            Ledger.AddAction(nullDeath);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.DoesNotContain(scenario.LedgerTombstones,
                t => t.ActionId == nullDeath.ActionId);
        }

        [Fact]
        public void CommitTombstones_KerbalRescueInOldBranch_RemovedFromELS()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var oldBranchRescue = KerbalRescue("rec_inside", 100.0, "Rescuee Kerman");
            var outsideRescue = KerbalRescue("rec_outside", 200.0, "Outside Kerman");
            Ledger.AddAction(oldBranchRescue);
            Ledger.AddAction(outsideRescue);

            Assert.Null(LedgerOrchestrator.Kerbals);
            var elsBefore = EffectiveState.ComputeELS();
            Assert.Contains(elsBefore, a => a.ActionId == oldBranchRescue.ActionId);
            Assert.Contains(elsBefore, a => a.ActionId == outsideRescue.ActionId);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones,
                t => t.ActionId == oldBranchRescue.ActionId);

            var elsAfter = EffectiveState.ComputeELS();
            Assert.DoesNotContain(elsAfter, a => a.ActionId == oldBranchRescue.ActionId);
            Assert.Contains(elsAfter, a => a.ActionId == outsideRescue.ActionId);

            // Headless xUnit can prove ELS retirement. Live CrewRoster cleanup for
            // a stock-rescued kerbal must be covered by runtime/KSP validation.
            Assert.Contains(logLines, l =>
                l.Contains("[KerbalsModule]")
                && l.Contains("Queued 1 tombstoned roster kerbal cleanup candidate"));
        }

        [Fact]
        public void TryEnsureKerbalsModuleForTombstoneRosterCleanup_NotInitialized_InitializesBeforeQueue()
        {
            Assert.False(LedgerOrchestrator.IsInitialized);
            Assert.Null(LedgerOrchestrator.Kerbals);

            bool available = LedgerOrchestrator.TryEnsureKerbalsModuleForTombstoneRosterCleanup();

            Assert.True(available);
            Assert.True(LedgerOrchestrator.IsInitialized);
            Assert.NotNull(LedgerOrchestrator.Kerbals);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("Tombstoned roster cleanup requested before LedgerOrchestrator.Initialize"));
        }

        [Fact]
        public void TryEnsureKerbalsModuleForTombstoneRosterCleanup_InitializedButMissing_WarnsAndSkips()
        {
            LedgerOrchestrator.Initialize();
            LedgerOrchestrator.SetKerbalsForTesting(null);
            logLines.Clear();

            bool available = LedgerOrchestrator.TryEnsureKerbalsModuleForTombstoneRosterCleanup();

            Assert.False(available);
            Assert.Null(LedgerOrchestrator.Kerbals);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]")
                && l.Contains("initialized but KerbalsModule is missing"));
        }

        // ---------- Idempotence --------------------------------------------

        [Fact]
        public void CommitTombstones_AlreadyTombstoned_Idempotent()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var death = KerbalDeath("rec_origin", 100.0);
            Ledger.AddAction(death);

            // Seed an existing tombstone for the same ActionId from a prior run.
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb_existing",
                ActionId = death.ActionId,
                RetiringRecordingId = "rec_earlier_provisional",
                UT = 50.0,
                CreatedRealTime = DateTime.UtcNow.ToString("o"),
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // Count must still be 1 — idempotence skip.
            var matching = scenario.LedgerTombstones
                .Where(t => t.ActionId == death.ActionId).ToList();
            Assert.Single(matching);
            Assert.Equal("tomb_existing", matching[0].TombstoneId);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") && l.Contains("already tombstoned"));
        }

        // ---------- Advisory log counters ----------------------------------

        [Fact]
        public void CommitTombstones_LogsCounters()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            // Inside subtree: 1 death + 1 rep + 1 contract + 1 milestone.
            var death = KerbalDeath("rec_origin", 100.0);
            var bundled = RepPenalty("rec_origin", 100.1, ReputationPenaltySource.KerbalDeath);
            var contract = ContractComplete("rec_origin", 95.0);
            var milestone = Milestone("rec_origin", 99.0);
            Ledger.AddAction(death);
            Ledger.AddAction(bundled);
            Ledger.AddAction(contract);
            Ledger.AddAction(milestone);

            // Outside — must be ignored entirely by both counters.
            Ledger.AddAction(KerbalDeath("rec_outside", 300.0, kerbalName: "Bob"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") &&
                l.Contains("Tombstoned 4 career actions") &&
                l.Contains("Contract=1") &&
                l.Contains("Reputation=1") &&
                l.Contains("Kerbal=1") &&
                l.Contains("Milestone=1"));

            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") &&
                l.Contains("Supersede tombstone effects: tombstoned 4 recording-scoped career actions"));
        }

        [Fact]
        public void CommitTombstones_EmptySubtree_LogsZeroes()
        {
            // A marker with an origin id that's not in the tree → empty subtree.
            InstallTree("tree_1", new List<Recording>(), new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: null);
            var scenario = InstallScenario(Marker("rec_not_in_store", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // Subtree closure for a standalone origin is {origin} itself — one id,
            // but the ledger has zero matching actions. Counter line must still
            // show zero tombstoned.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") && l.Contains("Tombstoned 0"));
        }

        [Fact]
        public void CommitTombstones_EmptySubtree_BumpsTombstoneStateVersion()
        {
            InstallTree("tree_1", new List<Recording>(), new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: null);
            var scenario = InstallScenario(Marker("rec_not_in_store", "rec_provisional"));
            int before = scenario.TombstoneStateVersion;

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.NotEqual(before, scenario.TombstoneStateVersion);
        }

        // ---------- Cache invalidation -------------------------------------

        [Fact]
        public void CommitTombstones_KerbalDeathInTip_TombstonedWithChainOrigin()
        {
            // Item 23 / fix-chain-sibling-supersede: a kerbal-death action
            // stamped against the chain TIP (in-atmo segment carrying the
            // Destroyed terminal) must be tombstoned when the player re-flies
            // from the chain HEAD and merges. Pre-fix the closure walker
            // ignored chain siblings, so the action stayed in ELS and the
            // kerbal stayed dead even though the re-fly recovered them.
            var head = Rec("rec_head", "tree_1", parentBranchPointId: "bp_split");
            head.ChainId = "chain_a";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            var tip = Rec("rec_tip", "tree_1");
            tip.ChainId = "chain_a";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.TerminalStateValue = TerminalState.Destroyed;

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_head" });

            InstallTree("tree_1",
                new List<Recording> { head, tip },
                new List<BranchPoint> { bp_split });

            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_head");
            var scenario = InstallScenario(Marker("rec_head", "rec_provisional"));

            // Death action stamped against the TIP (the segment that carries
            // the Destroyed terminal in production).
            var death = KerbalDeath("rec_tip", 100.0, kerbalName: "Magdo");
            Ledger.AddAction(death);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == death.ActionId);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") &&
                l.Contains("Tombstoned 1 career actions") &&
                l.Contains("Kerbal=1"));
        }

        [Fact]
        public void CommitTombstones_BumpsTombstoneStateVersion_InvalidatesELSCache()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var death = KerbalDeath("rec_origin", 100.0);
            Ledger.AddAction(death);

            // Warm the ELS cache — death must be in the pre-commit ELS.
            var elsBefore = EffectiveState.ComputeELS();
            Assert.Contains(elsBefore, a => a.ActionId == death.ActionId);

            int versionBefore = scenario.TombstoneStateVersion;

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            int versionAfter = scenario.TombstoneStateVersion;
            Assert.NotEqual(versionBefore, versionAfter);

            // ELS must now exclude the tombstoned action.
            var elsAfter = EffectiveState.ComputeELS();
            Assert.DoesNotContain(elsAfter, a => a.ActionId == death.ActionId);
        }

        // ---------- TOMBSTONE-SCOPE-HAS-NO-UT-GUARD -------------------------

        private static GameAction FundsEarning(string recordingId, double ut,
            float funds = 5000f)
        {
            return new GameAction
            {
                ActionId = "act_" + Guid.NewGuid().ToString("N"),
                Type = GameActionType.FundsEarning,
                RecordingId = recordingId,
                FundsReward = funds,
                UT = ut,
            };
        }

        [Fact]
        public void IsPreRewindAttributedAction_StrictlyBeforeCutoff_True()
        {
            var a = FundsEarning("rec_origin", 99.0);
            Assert.True(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, 100.0));
        }

        [Fact]
        public void IsPreRewindAttributedAction_AtCutoff_False_MirrorsSplitterRetag()
        {
            // RecordingTreeSplitter step 2.9 retags `a.UT >= rewindUT` to TIP. The
            // boundary sample belongs to the REPLACED half, so it must stay tombstonable.
            var a = FundsEarning("rec_origin", 100.0);
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, 100.0));
        }

        [Fact]
        public void IsPreRewindAttributedAction_NaNCutoffOrNaNActionUT_False()
        {
            var a = FundsEarning("rec_origin", 10.0);
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(a, double.NaN));

            var nanUt = FundsEarning("rec_origin", double.NaN);
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(nanUt, 100.0));
            Assert.False(TombstoneAttributionHelper.IsPreRewindAttributedAction(null, 100.0));
        }

        [Theory]
        [InlineData(120.0, 0.0, 120.0)]           // RewindPointUT preferred
        [InlineData(double.NaN, 90.0, 90.0)]      // legacy marker: InvokedUT fallback
        [InlineData(double.NaN, 0.0, double.NaN)] // neither usable: guard inert
        [InlineData(0.0, 0.0, double.NaN)]        // non-positive rewind UT: guard inert
        public void ComputeTombstoneRewindCutoffUT_FieldPreference(
            double rewindPointUT, double invokedUT, double expected)
        {
            var marker = Marker("rec_origin", "rec_provisional");
            marker.RewindPointUT = rewindPointUT;
            marker.InvokedUT = invokedUT;

            double actual = SupersedeCommit.ComputeTombstoneRewindCutoffUT(marker);
            if (double.IsNaN(expected))
                Assert.True(double.IsNaN(actual), $"expected NaN, got {actual}");
            else
                Assert.Equal(expected, actual);
        }

        [Fact]
        public void ComputeTombstoneRewindCutoffUT_CarriesNoEpsilon_UnlikePreRewindCutoff()
        {
            // The two cutoffs deliberately differ: the debris carve-out biases a sampled
            // StartUT by an epsilon, while the tombstone guard must mirror the splitter's
            // raw `a.UT >= rewindUT` exactly. Pin the difference so a future "unify these"
            // refactor reds here instead of silently tombstoning kept rows.
            var marker = Marker("rec_origin", "rec_provisional");
            marker.RewindPointUT = 500.0;

            Assert.Equal(500.0, SupersedeCommit.ComputeTombstoneRewindCutoffUT(marker));
            Assert.Equal(
                500.0 - EffectiveState.PidPeerStartUtEpsilonSeconds,
                SupersedeCommit.ComputePreRewindCutoff(marker));
        }

        [Fact]
        public void CommitTombstones_PreRewindPayoutAttributedToOriginChild_NotTombstoned()
        {
            // The filed shape: a payout earned BEFORE the rewind point brackets to the
            // origin child, the origin child lands in the supersede subtree, and (pre-fix)
            // the bare subtree-id containment test tombstoned a real payout belonging to
            // the half of the flight the merge KEEPS.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var marker = Marker("rec_origin", "rec_provisional");
            marker.RewindPointUT = 1000.0;
            var scenario = InstallScenario(marker);

            var preRewindPayout = FundsEarning("rec_origin", 900.0);
            var postRewindPayout = FundsEarning("rec_origin", 1100.0);
            var atRewindPayout = FundsEarning("rec_inside", 1000.0);
            Ledger.AddAction(preRewindPayout);
            Ledger.AddAction(postRewindPayout);
            Ledger.AddAction(atRewindPayout);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var tombstoned = new HashSet<string>(
                scenario.LedgerTombstones.Select(t => t.ActionId));
            Assert.DoesNotContain(preRewindPayout.ActionId, tombstoned);
            Assert.Contains(postRewindPayout.ActionId, tombstoned);
            Assert.Contains(atRewindPayout.ActionId, tombstoned);

            // The kept payout survives into the ELS — the property that actually matters.
            var els = EffectiveState.ComputeELS();
            Assert.Contains(els, a => a.ActionId == preRewindPayout.ActionId);
            Assert.DoesNotContain(els, a => a.ActionId == postRewindPayout.ActionId);

            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") && l.Contains("PreRewindTombstoneGuard: kept 1 "));
        }

        [Fact]
        public void CommitTombstones_LegacyMarkerWithoutRewindUT_GuardInert()
        {
            // A marker carrying neither a rewind-point UT nor an invoked UT has no cutoff,
            // so the guard must not change what a legacy merge retires.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var marker = Marker("rec_origin", "rec_provisional");
            marker.RewindPointUT = double.NaN;
            marker.InvokedUT = 0.0;
            var scenario = InstallScenario(marker);

            var earlyPayout = FundsEarning("rec_origin", 1.0);
            Ledger.AddAction(earlyPayout);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == earlyPayout.ActionId);
            Assert.Contains(logLines, l =>
                l.Contains("PreRewindTombstoneGuard: kept 0 ") && l.Contains("cutoffUT=<none>"));
        }

        [Fact]
        public void CommitTombstones_PreRewindKerbalDeath_SurvivesTheMerge()
        {
            // The death happened on the kept flight. Tombstoning it would re-animate a
            // kerbal the player lost before ever rewinding.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var marker = Marker("rec_origin", "rec_provisional");
            marker.RewindPointUT = 300.0;
            var scenario = InstallScenario(marker);

            var death = KerbalDeath("rec_origin", 250.0, kerbalName: "Jeb");
            var bundledRep = RepPenalty("rec_origin", 250.0, ReputationPenaltySource.KerbalDeath);
            Ledger.AddAction(death);
            Ledger.AddAction(bundledRep);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var tombstoned = new HashSet<string>(
                scenario.LedgerTombstones.Select(t => t.ActionId));
            Assert.DoesNotContain(death.ActionId, tombstoned);
            Assert.DoesNotContain(bundledRep.ActionId, tombstoned);
        }
    }
}
