using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 11 of Rewind-to-Staging (design §3.5 invariant 7 / §6.10 /
    /// §10.1): guards <see cref="TreeDiscardPurge.PurgeTree"/>.
    ///
    /// <para>
    /// Covers the invariants: RPs, supersede relations, ledger tombstones,
    /// and reservations tied to the discarded tree are purged; state tied
    /// to sibling trees is untouched; empty-tree no-op; marker + journal
    /// clear when scoped to the discarded tree; file-delete failure logs
    /// but does not abort the purge.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class TreeDiscardPurgeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly List<string> deletedRpIds = new List<string>();

        public TreeDiscardPurgeTests()
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
            TreeDiscardPurge.ResetTestOverrides();

            TreeDiscardPurge.DeleteQuicksaveForTesting = id =>
            {
                deletedRpIds.Add(id);
                return true;
            };
        }

        public void Dispose()
        {
            TreeDiscardPurge.ResetTestOverrides();
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

        // ---------- Helpers -----------------------------------------------

        private static Recording Rec(string id, string treeId,
            MergeState state = MergeState.Immutable)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
            };
        }

        private static BranchPoint Bp(string id, string rpId = null)
        {
            return new BranchPoint
            {
                Id = id,
                Type = BranchPointType.Undock,
                UT = 0.0,
                RewindPointId = rpId,
            };
        }

        private static ChildSlot Slot(int index, string originRecordingId)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = originRecordingId,
                Controllable = true,
            };
        }

        private static RewindPoint Rp(string id, string bpId, params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = bpId,
                UT = 0.0,
                QuicksaveFilename = id + ".sfs",
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>(slots ?? Array.Empty<ChildSlot>()),
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

        private static ParsekScenario InstallScenario(
            List<RewindPoint> rps = null,
            List<RecordingSupersedeRelation> supersedes = null,
            List<RecordingRewindRetirement> retirements = null,
            List<LedgerTombstone> tombstones = null,
            ReFlySessionMarker marker = null,
            MergeJournal journal = null)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = rps ?? new List<RewindPoint>(),
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = retirements ?? new List<RecordingRewindRetirement>(),
                LedgerTombstones = tombstones ?? new List<LedgerTombstone>(),
                ActiveReFlySessionMarker = marker,
                ActiveMergeJournal = journal,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static GameAction KerbalDeath(string recordingId, string actionId = null)
        {
            return new GameAction
            {
                ActionId = actionId ?? ("act_" + Guid.NewGuid().ToString("N")),
                Type = GameActionType.KerbalAssignment,
                RecordingId = recordingId,
                KerbalName = "Jeb",
                KerbalEndStateField = KerbalEndState.Dead,
                UT = 100.0,
            };
        }

        // ---------- Cases --------------------------------------------------

        [Fact]
        public void PurgeTree_RemovesAllRPsInTree_DeletesQuicksaveFiles()
        {
            var bp1 = Bp("bp_1", "rp_1");
            var bp2 = Bp("bp_2", "rp_2");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", "tree_1"),
                    Rec("rec_b", "tree_1"),
                },
                new List<BranchPoint> { bp1, bp2 });
            var rp1 = Rp("rp_1", "bp_1", Slot(0, "rec_a"));
            var rp2 = Rp("rp_2", "bp_2", Slot(0, "rec_b"));
            var scenario = InstallScenario(rps: new List<RewindPoint> { rp1, rp2 });

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp1.RewindPointId);
            Assert.Null(bp2.RewindPointId);
            Assert.Contains("rp_1", deletedRpIds);
            Assert.Contains("rp_2", deletedRpIds);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Purged rp=rp_1") && l.Contains("tree=tree_1"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("Purged rp=rp_2") && l.Contains("tree=tree_1"));
        }

        [Fact]
        public void PurgeTree_RemovesSupersedeRelationsWithEndpointInTree()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_in_tree1", "tree_1"),
                },
                new List<BranchPoint>());
            InstallTree("tree_2",
                new List<Recording>
                {
                    Rec("rec_in_tree2", "tree_2"),
                },
                new List<BranchPoint>());

            var rel_old_in = new RecordingSupersedeRelation
            {
                RelationId = "rsr_1",
                OldRecordingId = "rec_in_tree1",
                NewRecordingId = "rec_in_tree2",
            };
            var rel_new_in = new RecordingSupersedeRelation
            {
                RelationId = "rsr_2",
                OldRecordingId = "rec_in_tree2",
                NewRecordingId = "rec_in_tree1",
            };
            var rel_unrelated = new RecordingSupersedeRelation
            {
                RelationId = "rsr_3",
                OldRecordingId = "rec_x",
                NewRecordingId = "rec_y",
            };
            var scenario = InstallScenario(supersedes: new List<RecordingSupersedeRelation>
            {
                rel_old_in, rel_new_in, rel_unrelated,
            });

            TreeDiscardPurge.PurgeTree("tree_1");

            // Both relations with an endpoint in tree_1 are gone; the
            // unrelated one survives.
            Assert.Single(scenario.RecordingSupersedes);
            Assert.Equal("rsr_3", scenario.RecordingSupersedes[0].RelationId);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Purged supersede relation=rsr_1"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Purged supersede relation=rsr_2"));
        }

        [Fact]
        public void PurgeTree_RemovesRewindRetirementsWithEndpointInTree()
        {
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_retired", "tree_1"),
                },
                new List<BranchPoint>());
            InstallTree("tree_2",
                new List<Recording>
                {
                    Rec("rec_restored", "tree_2"),
                    Rec("rec_unrelated", "tree_2"),
                },
                new List<BranchPoint>());

            var retiredIn = new RecordingRewindRetirement
            {
                RetirementId = "rrt_retired_in",
                RecordingId = "rec_retired",
                RestoredRecordingId = "rec_restored",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var restoredIn = new RecordingRewindRetirement
            {
                RetirementId = "rrt_restored_in",
                RecordingId = "rec_unrelated",
                RestoredRecordingId = "rec_retired",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var unrelated = new RecordingRewindRetirement
            {
                RetirementId = "rrt_unrelated",
                RecordingId = "rec_unrelated",
                RestoredRecordingId = "rec_restored",
                Reason = RecordingRewindRetirement.DefaultReason
            };
            var scenario = InstallScenario(retirements: new List<RecordingRewindRetirement>
            {
                retiredIn, restoredIn, unrelated,
            });

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Single(scenario.RecordingRewindRetirements);
            Assert.Equal("rrt_unrelated", scenario.RecordingRewindRetirements[0].RetirementId);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Purged rewind-retirement=rrt_retired_in"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Purged rewind-retirement=rrt_restored_in"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("rewindRetirements=2"));
        }

        [Fact]
        public void PurgeTree_RemovesTombstonesForInTreeActions()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint>());
            InstallTree("tree_2",
                new List<Recording> { Rec("rec_b", "tree_2") },
                new List<BranchPoint>());

            var actInTree = KerbalDeath("rec_a", "act_in_tree");
            var actUnrelated = KerbalDeath("rec_b", "act_unrelated");
            Ledger.AddAction(actInTree);
            Ledger.AddAction(actUnrelated);

            var tInTree = new LedgerTombstone
            {
                TombstoneId = "tomb_1",
                ActionId = "act_in_tree",
                RetiringRecordingId = "rec_provisional",
                UT = 100.0,
            };
            var tUnrelated = new LedgerTombstone
            {
                TombstoneId = "tomb_2",
                ActionId = "act_unrelated",
                RetiringRecordingId = "rec_provisional",
                UT = 100.0,
            };
            var scenario = InstallScenario(tombstones: new List<LedgerTombstone>
            {
                tInTree, tUnrelated,
            });

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Single(scenario.LedgerTombstones);
            Assert.Equal("tomb_2", scenario.LedgerTombstones[0].TombstoneId);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerSwap]") && l.Contains("Purged tombstone=tomb_1"));
        }

        [Fact]
        public void DiscardPendingTree_WithCommittedOverlap_PurgesPendingOnlyTreeState()
        {
            var committedBp = Bp("bp_committed", "rp_committed");
            InstallTree("tree_overlap",
                new List<Recording>
                {
                    Rec("rec_shared", "tree_overlap"),
                    Rec("rec_committed_only", "tree_overlap"),
                },
                new List<BranchPoint> { committedBp });

            var pendingBp = Bp("bp_pending", "rp_pending");
            var pending = new RecordingTree
            {
                Id = "tree_overlap",
                TreeName = "Pending overlap",
                BranchPoints = new List<BranchPoint> { pendingBp },
                RootRecordingId = "rec_shared",
                ActiveRecordingId = "rec_shared",
            };
            pending.AddOrReplaceRecording(Rec("rec_shared", "tree_overlap", MergeState.NotCommitted));
            pending.AddOrReplaceRecording(Rec("rec_pending_only", "tree_overlap", MergeState.NotCommitted));
            RecordingStore.StashPendingTree(pending);

            Ledger.AddAction(KerbalDeath("rec_shared", "act_shared"));
            Ledger.AddAction(KerbalDeath("rec_pending_only", "act_pending"));
            Ledger.AddAction(KerbalDeath("rec_committed_only", "act_committed_only"));

            var scenario = InstallScenario(
                rps: new List<RewindPoint>
                {
                    Rp("rp_committed", "bp_committed", Slot(0, "rec_shared")),
                    Rp("rp_pending", "bp_pending", Slot(0, "rec_pending_only")),
                },
                supersedes: new List<RecordingSupersedeRelation>
                {
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rel_shared",
                        OldRecordingId = "rec_shared",
                        NewRecordingId = "rec_other",
                    },
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rel_pending",
                        OldRecordingId = "rec_pending_only",
                        NewRecordingId = "rec_other",
                    },
                    new RecordingSupersedeRelation
                    {
                        RelationId = "rel_committed_only",
                        OldRecordingId = "rec_committed_only",
                        NewRecordingId = "rec_other",
                    },
                },
                retirements: new List<RecordingRewindRetirement>
                {
                    new RecordingRewindRetirement
                    {
                        RetirementId = "ret_shared",
                        RecordingId = "rec_shared",
                        RestoredRecordingId = "rec_other",
                        Reason = RecordingRewindRetirement.DefaultReason,
                    },
                    new RecordingRewindRetirement
                    {
                        RetirementId = "ret_pending",
                        RecordingId = "rec_pending_only",
                        RestoredRecordingId = "rec_other",
                        Reason = RecordingRewindRetirement.DefaultReason,
                    },
                },
                tombstones: new List<LedgerTombstone>
                {
                    new LedgerTombstone
                    {
                        TombstoneId = "tomb_shared",
                        ActionId = "act_shared",
                        RetiringRecordingId = "rec_other",
                    },
                    new LedgerTombstone
                    {
                        TombstoneId = "tomb_pending",
                        ActionId = "act_pending",
                        RetiringRecordingId = "rec_other",
                    },
                    new LedgerTombstone
                    {
                        TombstoneId = "tomb_pending_fallback",
                        ActionId = "act_missing_pending",
                        RetiringRecordingId = "rec_pending_only",
                    },
                    new LedgerTombstone
                    {
                        TombstoneId = "tomb_shared_fallback",
                        ActionId = "act_missing_shared",
                        RetiringRecordingId = "rec_shared",
                    },
                    new LedgerTombstone
                    {
                        TombstoneId = "tomb_committed_only",
                        ActionId = "act_committed_only",
                        RetiringRecordingId = "rec_other",
                    },
                });

            RecordingStore.DiscardPendingTree();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(scenario.RewindPoints, rp => rp.RewindPointId == "rp_committed");
            Assert.DoesNotContain(scenario.RewindPoints, rp => rp.RewindPointId == "rp_pending");
            Assert.Contains("rp_pending", deletedRpIds);
            Assert.DoesNotContain("rp_committed", deletedRpIds);
            Assert.Equal("rp_committed", committedBp.RewindPointId);
            Assert.Null(pendingBp.RewindPointId);

            Assert.Contains(scenario.RecordingSupersedes, r => r.RelationId == "rel_shared");
            Assert.Contains(scenario.RecordingSupersedes, r => r.RelationId == "rel_committed_only");
            Assert.DoesNotContain(scenario.RecordingSupersedes, r => r.RelationId == "rel_pending");

            Assert.Contains(scenario.RecordingRewindRetirements, r => r.RetirementId == "ret_shared");
            Assert.DoesNotContain(scenario.RecordingRewindRetirements, r => r.RetirementId == "ret_pending");

            Assert.Contains(scenario.LedgerTombstones, t => t.TombstoneId == "tomb_shared");
            Assert.Contains(scenario.LedgerTombstones, t => t.TombstoneId == "tomb_shared_fallback");
            Assert.Contains(scenario.LedgerTombstones, t => t.TombstoneId == "tomb_committed_only");
            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.TombstoneId == "tomb_pending");
            Assert.DoesNotContain(scenario.LedgerTombstones, t => t.TombstoneId == "tomb_pending_fallback");
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("PurgeTree: tree=tree_overlap")
                && l.Contains("supersedes=1")
                && l.Contains("rewindRetirements=1")
                && l.Contains("tombstones=2"));
        }

        [Fact]
        public void PurgeTree_ClearsReservationsForTreeKerbals()
        {
            // Set up: 1 in-tree kerbal-death tombstone. After PurgeTree
            // removes it, CrewReservationManager.RecomputeAfterTombstones
            // should fire and log its recompute message.
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint>());

            var act = KerbalDeath("rec_a", "act_death");
            Ledger.AddAction(act);

            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_1",
                ActionId = "act_death",
                RetiringRecordingId = "rec_provisional",
                UT = 100.0,
            };
            InstallScenario(tombstones: new List<LedgerTombstone> { tomb });

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Contains(logLines, l =>
                l.Contains("[CrewReservations]")
                && l.Contains("recomputed reservations after 1 tombstone(s)"));
        }

        [Fact]
        public void PurgeTree_ClearsMarkerIfTreeScoped()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint>());

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_1",
                TreeId = "tree_1",
                ActiveReFlyRecordingId = "rec_provisional",
                OriginChildRecordingId = "rec_a",
            };
            var journal = new MergeJournal
            {
                JournalId = "mj_1",
                SessionId = "sess_1",
                TreeId = "tree_1",
                Phase = MergeJournal.Phases.Begin,
            };
            var scenario = InstallScenario(marker: marker, journal: journal);

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("End reason=treeDiscarded")
                && l.Contains("sess=sess_1")
                && l.Contains("tree=tree_1"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeJournal]")
                && l.Contains("Aborted journal=mj_1")
                && l.Contains("tree=tree_1"));
        }

        [Fact]
        public void PurgeTree_MarkerScopedToOtherTree_NotCleared()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint>());

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_other",
                TreeId = "tree_other",
                ActiveReFlyRecordingId = "rec_other_provisional",
            };
            var scenario = InstallScenario(marker: marker);

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Same(marker, scenario.ActiveReFlySessionMarker);
        }

        [Fact]
        public void PurgeTree_MarkerNull_JournalScopedToOtherTree_NotCleared()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint>());

            var journal = new MergeJournal
            {
                JournalId = "mj_other",
                SessionId = "sess_other",
                TreeId = "tree_other",
                Phase = MergeJournal.Phases.Durable1Done,
            };
            var scenario = InstallScenario(journal: journal);

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Same(journal, scenario.ActiveMergeJournal);
        }

        [Fact]
        public void PurgeTree_OtherTreesUnaffected()
        {
            var bp_a = Bp("bp_a", "rp_a");
            var bp_b = Bp("bp_b", "rp_b");
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint> { bp_a });
            InstallTree("tree_2",
                new List<Recording> { Rec("rec_b", "tree_2") },
                new List<BranchPoint> { bp_b });
            var rp_a = Rp("rp_a", "bp_a", Slot(0, "rec_a"));
            var rp_b = Rp("rp_b", "bp_b", Slot(0, "rec_b"));
            var scenario = InstallScenario(rps: new List<RewindPoint> { rp_a, rp_b });

            TreeDiscardPurge.PurgeTree("tree_1");

            // rp_a gone; rp_b remains with back-ref intact.
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_b", scenario.RewindPoints[0].RewindPointId);
            Assert.Equal("rp_b", bp_b.RewindPointId);
            Assert.Null(bp_a.RewindPointId);
        }

        [Fact]
        public void PurgeTree_EmptyTree_NoOp_LogsZero()
        {
            InstallTree("tree_1",
                new List<Recording>(),
                new List<BranchPoint>());
            var scenario = InstallScenario();

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.Empty(scenario.RewindPoints);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Empty(scenario.LedgerTombstones);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("PurgeTree: tree=tree_1")
                && l.Contains("rps=0")
                && l.Contains("supersedes=0")
                && l.Contains("tombstones=0"));
        }

        [Fact]
        public void PurgeTree_FileDeleteFails_LogsWarn_ContinuesOtherRemovals()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", Slot(0, "rec_a"));

            // Also add a supersede relation to prove the purge keeps going
            // after the file delete fails.
            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_1",
                OldRecordingId = "rec_a",
                NewRecordingId = "rec_x",
            };
            var scenario = InstallScenario(
                rps: new List<RewindPoint> { rp },
                supersedes: new List<RecordingSupersedeRelation> { rel });

            TreeDiscardPurge.DeleteQuicksaveForTesting = id =>
            {
                throw new System.IO.IOException("simulated failure");
            };

            TreeDiscardPurge.PurgeTree("tree_1");

            // RP still removed, BP back-ref cleared, supersede still purged.
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("PurgeTree quicksave delete hook threw")
                && l.Contains("rp=rp_1"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Purged supersede relation=rsr_1"));
        }

        [Fact]
        public void PurgeTree_UnknownTreeId_NoOp_LogsVerbose()
        {
            var scenario = InstallScenario();

            TreeDiscardPurge.PurgeTree("tree_does_not_exist");

            Assert.Empty(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("tree=tree_does_not_exist not found"));
        }

        [Fact]
        public void PurgeTree_BumpsSupersedeAndTombstoneStateVersions_WhenChanged()
        {
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", "tree_1") },
                new List<BranchPoint>());
            var rel = new RecordingSupersedeRelation
            {
                RelationId = "rsr_1",
                OldRecordingId = "rec_a",
                NewRecordingId = "rec_x",
            };
            var act = KerbalDeath("rec_a", "act_1");
            Ledger.AddAction(act);
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_1",
                ActionId = "act_1",
                RetiringRecordingId = "rec_x",
                UT = 0.0,
            };
            var scenario = InstallScenario(
                supersedes: new List<RecordingSupersedeRelation> { rel },
                tombstones: new List<LedgerTombstone> { tomb });

            int beforeS = scenario.SupersedeStateVersion;
            int beforeT = scenario.TombstoneStateVersion;

            TreeDiscardPurge.PurgeTree("tree_1");

            Assert.NotEqual(beforeS, scenario.SupersedeStateVersion);
            Assert.NotEqual(beforeT, scenario.TombstoneStateVersion);
        }
    }
}
