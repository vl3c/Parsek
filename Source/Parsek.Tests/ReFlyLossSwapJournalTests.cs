using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Design section 2.12 Rule 2, the journal side: the merge journal persists the
    /// loss-swap variant (<c>lossSwapVariant</c>, absent = A), Variant B tombstones the
    /// re-fly's own ReputationPenalty loss rows and keeps the replaced flight's, and a
    /// finisher that resumes a crashed merge replays the same variant.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyLossSwapJournalTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        private const string Sess = "sess_swap";

        public ReFlyLossSwapJournalTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            MergeJournalOrchestrator.ResetTestOverrides();
            MergeJournalOrchestrator.DurableSaveForTesting = label => { };
        }

        public void Dispose()
        {
            MergeJournalOrchestrator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        [Fact]
        public void MergeJournal_LossSwapVariantB_RoundTrips()
        {
            var j = new MergeJournal
            {
                JournalId = "mj_1", SessionId = Sess, TreeId = "tree_1",
                Phase = MergeJournal.Phases.Supersede, StartedUT = 12.5,
                LossSwapVariant = LossSwapVariant.B,
            };
            var parent = new ConfigNode("PARSEK");
            j.SaveInto(parent);

            var node = parent.GetNode(MergeJournal.NodeName);
            Assert.Equal("B", node.GetValue("lossSwapVariant"));
            Assert.Equal(LossSwapVariant.B, MergeJournal.LoadFrom(node).LossSwapVariant);
        }

        [Fact]
        public void MergeJournal_LossSwapVariantAbsent_ReadsA()
        {
            var node = new ConfigNode(MergeJournal.NodeName);
            node.AddValue("journalId", "mj_old");
            node.AddValue("sessionId", Sess);
            node.AddValue("phase", MergeJournal.Phases.Tombstone);

            Assert.Equal(LossSwapVariant.A, MergeJournal.LoadFrom(node).LossSwapVariant);
        }

        private static Recording Rec(string id, string treeId, MergeState state,
            string parentBp = null, string childBp = null, TerminalState? terminal = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                ParentBranchPointId = parentBp,
                ChildBranchPointId = childBp,
                TerminalStateValue = terminal,
            };
        }

        private static GameAction RepLoss(string id, string rec, double ut, float penalty)
        {
            return new GameAction
            {
                ActionId = id,
                Type = GameActionType.ReputationPenalty,
                RecordingId = rec,
                UT = ut,
                NominalPenalty = penalty,
                RepPenaltySource = ReputationPenaltySource.Other,
            };
        }

        /// <summary>Origin with one inside child (the closure), a committed provisional
        /// stamped with the session, a reputation loss on each side.</summary>
        private (ParsekScenario scenario, Recording provisional) InstallFixture()
        {
            var origin = Rec("rec_origin", "tree_1", MergeState.Immutable, childBp: "bp_c");
            var inside = Rec("rec_inside", "tree_1", MergeState.Immutable, parentBp: "bp_c");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Swap",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = "bp_c", Type = BranchPointType.Undock, UT = 0.0,
                        ParentRecordingIds = new List<string> { "rec_origin" },
                        ChildRecordingIds = new List<string> { "rec_inside" },
                    },
                },
            };
            foreach (var r in new[] { origin, inside })
            {
                tree.AddOrReplaceRecording(r);
                RecordingStore.AddRecordingWithTreeForTesting(r, "tree_1");
            }
            RecordingStore.CommittedTrees.Add(tree);

            var provisional = Rec("rec_provisional", "tree_1", MergeState.NotCommitted,
                terminal: TerminalState.Landed);
            provisional.SupersedeTargetId = "rec_origin";
            provisional.CreatingSessionId = Sess;
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");

            Ledger.AddAction(RepLoss("rep_old", "rec_inside", 12.0, 10f));
            Ledger.AddAction(RepLoss("rep_new", "rec_provisional", 15.0, 20f));

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = Sess,
                    TreeId = "tree_1",
                    ActiveReFlyRecordingId = "rec_provisional",
                    OriginChildRecordingId = "rec_origin",
                    RewindPointId = "rp_1",
                    InvokedUT = 0.0,
                },
                ActiveMergeJournal = null,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return (scenario, provisional);
        }

        [Fact]
        public void RunMerge_VariantA_TombstonesTheReplacedFlightsReputationLoss()
        {
            var (scenario, provisional) = InstallFixture();

            Assert.True(MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional, LossSwapVariant.A));

            Assert.Contains(scenario.LedgerTombstones, t => t.ActionId == "rep_old");
            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.ActionId == "rep_new");
        }

        [Fact]
        public void RunMerge_VariantB_KeepsTheReplacedLossAndTombstonesTheReFlysOwn()
        {
            var (scenario, provisional) = InstallFixture();

            Assert.True(MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional, LossSwapVariant.B));

            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.ActionId == "rep_old");
            var tomb = Assert.Single(scenario.LedgerTombstones, t => t.ActionId == "rep_new");
            Assert.Equal("rec_provisional", tomb.RetiringRecordingId);
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("phase=Begin") && l.Contains("lossSwapVariant=B"));
            Assert.Contains(logLines, l => l.Contains("[LedgerSwap]")
                && l.Contains("Loss swap variant B applied"));
        }

        // catches: an empty merge subtree (an unflown attempt AppendRelations refuses)
        // returning before Variant B, so the re-fly's own reputation loss counted although
        // the dominance check modeled it dropped.
        [Fact]
        public void RunMerge_VariantB_MergeRetiresNothing_StillTombstonesTheReFlysOwnLoss()
        {
            var (scenario, provisional) = InstallFixture();
            provisional.Points.Clear();

            Assert.True(MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional, LossSwapVariant.B));

            Assert.Contains(logLines, l => l.Contains("outcome=refused-unflown-provisional"));
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.ActionId == "rep_old");
            var tomb = Assert.Single(scenario.LedgerTombstones, t => t.ActionId == "rep_new");
            Assert.Equal("rec_provisional", tomb.RetiringRecordingId);
            Assert.Contains(logLines, l => l.Contains("[LedgerSwap]")
                && l.Contains("Loss swap variant B applied")
                && l.Contains("keptReplacedRepLoss=0 tombstonedAttemptRepLoss=1"));
        }

        [Fact]
        public void RunMerge_VariantA_MergeRetiresNothing_TombstonesNothing()
        {
            var (scenario, provisional) = InstallFixture();
            provisional.Points.Clear();

            Assert.True(MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional, LossSwapVariant.A));

            Assert.Empty(scenario.LedgerTombstones);
        }

        [Fact]
        public void CrashBeforeTombstones_JournalReloaded_FinisherReplaysVariantB()
        {
            var (scenario, provisional) = InstallFixture();
            MergeJournalOrchestrator.FaultInjectionPoint = MergeJournalOrchestrator.Phase.Supersede;
            try
            {
                MergeJournalOrchestrator.RunMerge(
                    scenario.ActiveReFlySessionMarker, provisional, LossSwapVariant.B);
                Assert.True(false, "expected the injected fault");
            }
            catch (MergeJournalOrchestrator.FaultInjectionException)
            {
            }
            finally
            {
                MergeJournalOrchestrator.FaultInjectionPoint = null;
            }
            Assert.Empty(scenario.LedgerTombstones);

            var parent = new ConfigNode("PARSEK");
            scenario.ActiveMergeJournal.SaveInto(parent);
            scenario.ActiveMergeJournal = MergeJournal.LoadFrom(parent.GetNode(MergeJournal.NodeName));
            Assert.Equal(LossSwapVariant.B, scenario.ActiveMergeJournal.LossSwapVariant);

            MergeJournalOrchestrator.RunFinisher();

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.ActionId == "rep_old");
            var tomb = Assert.Single(scenario.LedgerTombstones, t => t.ActionId == "rep_new");
            Assert.Equal("rec_provisional", tomb.RetiringRecordingId);
        }
    }
}
