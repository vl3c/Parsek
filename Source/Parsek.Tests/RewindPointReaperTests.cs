using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 11 of Rewind-to-Staging (design §5.1 / §6.6 step 9 / §6.8 /
    /// §10.1): guards <see cref="RewindPointReaper.ReapOrphanedRPs"/>.
    ///
    /// <para>
    /// Covers the eligibility matrix (session-provisional stay, all-slots-
    /// Immutable reaped, any-slot-CommittedProvisional retained, any-slot-
    /// NotCommitted retained), BranchPoint back-ref clearing, quicksave file
    /// delete (with a test seam), idempotence (second call reaps nothing),
    /// and the count/log contract.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class RewindPointReaperTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly List<string> deletedRpIds = new List<string>();

        public RewindPointReaperTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            RewindPointReaper.ResetTestOverrides();

            // File delete is a test-only stub that records the rp id and
            // always reports success. Tests that want the failure path
            // override this in the body.
            RewindPointReaper.DeleteQuicksaveForTesting = rpId =>
            {
                deletedRpIds.Add(rpId);
                return true;
            };
        }

        public void Dispose()
        {
            RewindPointReaper.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        // ---------- Helpers -----------------------------------------------

        private static Recording Rec(string id, MergeState state)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = "tree_1",
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

        private static ChildSlot Slot(int index, string originRecordingId, bool sealedSlot = false)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = originRecordingId,
                Controllable = true,
                Sealed = sealedSlot,
                SealedRealTime = sealedSlot ? "2026-04-28T12:00:00.0000000Z" : null,
            };
        }

        private static RewindPoint Rp(string id, string bpId, bool sessionProvisional,
            params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = id,
                BranchPointId = bpId,
                UT = 0.0,
                QuicksaveFilename = id + ".sfs",
                SessionProvisional = sessionProvisional,
                CreatingSessionId = sessionProvisional ? "sess_1" : null,
                ChildSlots = new List<ChildSlot>(slots ?? Array.Empty<ChildSlot>()),
            };
        }

        private static ParsekScenario InstallScenario(List<RewindPoint> rps)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = rps ?? new List<RewindPoint>(),
                ActiveReFlySessionMarker = null,
                ActiveMergeJournal = null,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            EffectiveState.ResetCachesForTesting();
            return scenario;
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

        // ---------- Eligibility matrix ------------------------------------

        [Fact]
        public void Reap_SessionProvisional_NotReaped()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    Rec("rec_b", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: true,
                Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId); // back-ref intact
            Assert.Empty(deletedRpIds);
        }

        [Fact]
        public void Reap_AllSlotsImmutable_Reaped()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    Rec("rec_b", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Contains("rp_1", deletedRpIds);
        }

        [Fact]
        public void Reap_AnySlotNotCommitted_Retained()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    Rec("rec_b", MergeState.NotCommitted),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);
        }

        [Fact]
        public void Reap_AnySlotCommittedProvisional_Retained()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.CommittedProvisional),
                    Rec("rec_b", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"), Slot(1, "rec_b"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);
        }

        [Fact]
        public void Reap_CommittedProvisionalSealedSlot_CountsAsClosed()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.CommittedProvisional),
                    Rec("rec_b", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a", sealedSlot: true), Slot(1, "rec_b"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("ReapOrphanedRPs:")
                && l.Contains("sealedSlotsContributing=1"));
        }

        [Fact]
        public void Reap_NotCommittedSealedSlot_StillRetained()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.NotCommitted),
                    Rec("rec_b", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a", sealedSlot: true), Slot(1, "rec_b"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);
        }

        // ---------- File delete -------------------------------------------

        [Fact]
        public void Reap_DeletesQuicksaveFile_IfExists()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", MergeState.Immutable) },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"));
            InstallScenario(new List<RewindPoint> { rp });

            bool hookCalled = false;
            string seenRpId = null;
            RewindPointReaper.DeleteQuicksaveForTesting = id =>
            {
                hookCalled = true;
                seenRpId = id;
                return true;
            };

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.True(hookCalled);
            Assert.Equal("rp_1", seenRpId);
        }

        [Fact]
        public void Reap_FileDeleteFails_LogsWarn_StillReapsEntry()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", MergeState.Immutable) },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            // Simulate a locked file / IO failure.
            RewindPointReaper.DeleteQuicksaveForTesting = id =>
            {
                throw new System.IO.IOException("simulated locked file");
            };

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            // Scenario entry + back-ref still cleared so state is bounded.
            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("quicksave delete hook threw")
                && l.Contains("rp=rp_1"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("ReapOrphanedRPs:")
                && l.Contains("fileDeleteFail=1"));
        }

        // ---------- Idempotence + count ------------------------------------

        [Fact]
        public void Reap_Idempotent_SecondCallReapsNothing()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording> { Rec("rec_a", MergeState.Immutable) },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"));
            InstallScenario(new List<RewindPoint> { rp });

            int first = RewindPointReaper.ReapOrphanedRPs();
            int second = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, first);
            Assert.Equal(0, second);
        }

        [Fact]
        public void Reap_LogsCount()
        {
            var bp1 = Bp("bp_1", "rp_1");
            var bp2 = Bp("bp_2", "rp_2");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    Rec("rec_b", MergeState.Immutable),
                },
                new List<BranchPoint> { bp1, bp2 });
            var rp1 = Rp("rp_1", "bp_1", sessionProvisional: false, Slot(0, "rec_a"));
            var rp2 = Rp("rp_2", "bp_2", sessionProvisional: false, Slot(0, "rec_b"));
            InstallScenario(new List<RewindPoint> { rp1, rp2 });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(2, reaped);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("ReapOrphanedRPs:")
                && l.Contains("reaped=2"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Reaped rp=rp_1"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Reaped rp=rp_2"));
        }

        // ---------- Supersede walk ----------------------------------------

        [Fact]
        public void Reap_WalksSupersedesToImmutableEndpoint_Reaped()
        {
            // Slot's origin recording is superseded by an Immutable provisional.
            // The forward walk should see the end of chain as Immutable and
            // reap.
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_origin", MergeState.Immutable), // superseded origin
                    Rec("rec_provisional", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_origin"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_1",
                OldRecordingId = "rec_origin",
                NewRecordingId = "rec_provisional",
                UT = 0.0,
            });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
        }

        [Fact]
        public void Reap_OrphanSlotRecording_TreatedAsEligible()
        {
            // Slot points at a recording that isn't in the committed list
            // (tree discarded mid-session). Per spec, missing recording =
            // no re-fly target = eligible.
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>(),
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "ghost_rec_id"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
        }

        [Fact]
        public void Reap_EmptyRewindPointsList_NoOp_LogsZero()
        {
            var scenario = InstallScenario(new List<RewindPoint>());

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("reaped=0"));
        }

        [Fact]
        public void Reap_NoScenarioInstance_NoOp_LogsVerbose()
        {
            ParsekScenario.ResetInstanceForTesting();

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("no ParsekScenario instance"));
        }

        [Fact]
        public void Reap_RpWithEmptySlotList_Reaped()
        {
            // Defensive: an RP with no slots has nothing that could still be
            // re-flown. The feature doesn't create these in production, but
            // the policy treats them as eligible.
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1", new List<Recording>(), new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false);
            rp.ChildSlots = new List<ChildSlot>();
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
        }
    }
}
