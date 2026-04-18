using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 9 of Rewind-to-Staging (design §6.6 step 4 / §7.13-§7.17 /
    /// §7.41 / §10.4): guards the merge-time narrow-scope tombstone step in
    /// <see cref="SupersedeCommit.CommitSupersede"/>.
    ///
    /// <para>
    /// Covers the v1 eligibility matrix from the subtree-walk perspective
    /// (kerbal-death actions inside the subtree get tombstoned; contract /
    /// milestone / facility / strategy / tech / science / funds actions do
    /// not), parent-subtree exclusion, idempotence, null-scoped pass-through,
    /// log counters, and the <see cref="ParsekScenario.TombstoneStateVersion"/>
    /// bump that invalidates the ELS cache.
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
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
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
        public void CommitTombstones_UnbundledRepPenalty_NotTombstoned()
        {
            // §7.44: vessel-destruction rep (no paired death) stays in ELS.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var vesselRep = RepPenalty("rec_origin", 100.0, ReputationPenaltySource.Other);
            Ledger.AddAction(vesselRep);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.DoesNotContain(scenario.LedgerTombstones,
                t => t.ActionId == vesselRep.ActionId);
        }

        // ---------- Type-ineligible scope -----------------------------------

        [Fact]
        public void CommitTombstones_SupersededSubtreeContract_NotTombstoned()
        {
            // §7.13: ContractComplete inside the subtree stays in ELS.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var complete = ContractComplete("rec_origin", 100.0);
            Ledger.AddAction(complete);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.DoesNotContain(scenario.LedgerTombstones,
                t => t.ActionId == complete.ActionId);
        }

        [Fact]
        public void CommitTombstones_ParentSubtreeMilestone_NotTombstoned()
        {
            // §7.15: milestone earned by superseded recording stays sticky.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var milestone = Milestone("rec_origin", 80.0);
            Ledger.AddAction(milestone);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.DoesNotContain(scenario.LedgerTombstones,
                t => t.ActionId == milestone.ActionId);
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

            // Inside subtree: 1 death + 1 bundled rep + 1 contract + 1 milestone.
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

            // §10.4 Info advisory line.
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") &&
                l.Contains("Tombstoned 2 (KerbalDeath=1, repBundled=1)") &&
                l.Contains("Contract=1") &&
                l.Contains("Milestone=1"));

            // §10.4 Narrow-v1 advisory line.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") &&
                l.Contains("Narrow v1 effects: tombstoned 2 actions") &&
                l.Contains("career state"));
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

        // ---------- Cache invalidation -------------------------------------

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
    }
}
