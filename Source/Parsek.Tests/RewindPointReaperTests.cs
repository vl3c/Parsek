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
    /// Open/closed is read solely from each slot's effective tip MergeState
    /// (the single source of truth after collapse-seal-into-mergestate):
    /// Immutable = closed, CommittedProvisional / NotCommitted = open. Covers
    /// the eligibility matrix (session-provisional stay, all-slots-Immutable
    /// reaped, any-slot-CommittedProvisional retained, any-slot-NotCommitted
    /// retained), the seal transition (a kept-alive CP tip flipped to Immutable
    /// reaps on the next pass), BranchPoint back-ref clearing, quicksave file
    /// delete (with a test seam), idempotence, and the count/log contract.
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

        private static Recording Rec(
            string id,
            MergeState state,
            TerminalState? terminal = null,
            string evaCrewName = null,
            string parentBranchPointId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = "tree_1",
                MergeState = state,
                TerminalStateValue = terminal,
                EvaCrewName = evaCrewName,
                ParentBranchPointId = parentBranchPointId,
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

        private static ChildSlot Slot(
            int index,
            string originRecordingId,
            bool stashedSlot = false)
        {
            return new ChildSlot
            {
                SlotIndex = index,
                OriginChildRecordingId = originRecordingId,
                Controllable = true,
                Stashed = stashedSlot,
                StashedRealTime = stashedSlot ? "2026-04-29T12:00:00.0000000Z" : null,
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
        public void Reap_StashedOpenSlot_RetainedUntilSealed()
        {
            // A stashed stable leaf is OPEN: stash demoted its tip to
            // CommittedProvisional. The RP stays alive until the tip is sealed
            // back to Immutable.
            var bp = Bp("bp_1", "rp_1");
            var stashedTip = Rec("rec_b", MergeState.CommittedProvisional,
                TerminalState.Landed, parentBranchPointId: "bp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    stashedTip,
                },
                new List<BranchPoint> { bp });
            var stashedSlot = Slot(1, "rec_b", stashedSlot: true);
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"), stashedSlot);
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int first = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, first);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);

            // Seal closes the slot by flipping its effective tip to Immutable.
            stashedTip.MergeState = MergeState.Immutable;

            int second = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, second);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Equal(new[] { "rp_1" }, deletedRpIds);
        }

        [Fact]
        public void Reap_StashedBoardedEvaImmutableSlot_CountsAsClosed()
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    Rec("rec_eva", MergeState.Immutable, TerminalState.Boarded,
                        "Jebediah Kerman", parentBranchPointId: "bp_1"),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"),
                Slot(1, "rec_eva", stashedSlot: true));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Contains("rp_1", deletedRpIds);
        }

        [Theory]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void Reap_StashedWorldInteractingImmutableSlot_CountsAsClosed(
            TerminalState terminal)
        {
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    Rec("rec_unsafe", MergeState.Immutable, terminal,
                        parentBranchPointId: "bp_1"),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"),
                Slot(1, "rec_unsafe", stashedSlot: true));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Contains("rp_1", deletedRpIds);
        }

        [Fact]
        public void Reap_StashedOpenSlot_IgnoresTerminalShape_ClosesOnSeal()
        {
            // Open/closed is read from the tip MergeState, not the terminal
            // shape. A stashed-open (CP tip) slot stays open even if its
            // terminal flips to Boarded; only sealing the tip to Immutable
            // closes it.
            var bp = Bp("bp_1", "rp_1");
            var eva = Rec("rec_eva", MergeState.CommittedProvisional, TerminalState.Landed,
                "Jebediah Kerman", parentBranchPointId: "bp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable),
                    eva,
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"),
                Slot(1, "rec_eva", stashedSlot: true));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int first = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, first);
            Assert.Single(scenario.RewindPoints);
            Assert.Empty(deletedRpIds);

            // Terminal shape change alone does NOT close the slot anymore.
            eva.TerminalStateValue = TerminalState.Boarded;
            int stillOpen = RewindPointReaper.ReapOrphanedRPs();
            Assert.Equal(0, stillOpen);
            Assert.Single(scenario.RewindPoints);

            // Sealing the tip to Immutable closes it.
            eva.MergeState = MergeState.Immutable;
            int second = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, second);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Equal(new[] { "rp_1" }, deletedRpIds);
        }

        // ---------- Open keep-alive (collapse-seal-into-mergestate) ----------
        //
        // After collapse-seal-into-mergestate, open/closed is read SOLELY from
        // the slot's effective tip MergeState. An open Unfinished Flight tip
        // (crashed terminal, stranded EVA, non-focused stable leaf) is
        // CommittedProvisional after promotion (ApplyRewindProvisionalMergeStates
        // demotes its first-commit tip to CP), so the reaper keeps its RP alive
        // because the tip is CP, not because the classifier re-qualifies it.
        // These tests pin that CP tips keep the RP and Immutable tips close it.

        [Fact]
        public void Reap_OpenCrashedSlot_KeepsRpAlive()
        {
            // Slot's effective tip ended Destroyed and was promoted to
            // CommittedProvisional (open). Reaper keeps the RP because the tip
            // is CP.
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable, TerminalState.Landed,
                        parentBranchPointId: "bp_1"),
                    Rec("rec_destroyed", MergeState.CommittedProvisional, TerminalState.Destroyed,
                        parentBranchPointId: "bp_1"),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"),
                Slot(1, "rec_destroyed"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);
        }

        [Fact]
        public void Reap_OpenDestroyedChainTipSlot_KeepsRpAlive()
        {
            // Regression for the kerbalx-probe-stash-refly playtest, re-modeled
            // for collapse-seal-into-mergestate: the slot's effective walker
            // resolves the chain TIP, and open/closed is read from that tip's
            // MergeState. The slot's OriginChildRecordingId = "rec_head"
            // (chainIndex=0). slot.EffectiveRecordingId hops via
            // ResolveChainTerminalRecording: same ChainId, higher ChainIndex
            // wins, so rec_head (0) -> rec_tip (1). Promotion demotes the
            // first-commit tip to CommittedProvisional (the gap the old reaper
            // workaround compensated for), so the reaper reads rec_tip = CP =
            // open and keeps the RP alive.
            var bp = Bp("bp_1", "rp_1");
            var chainHead = new Recording
            {
                RecordingId = "rec_head",
                VesselName = "Probe",
                TreeId = "tree_1",
                MergeState = MergeState.CommittedProvisional,
                ParentBranchPointId = "bp_1",
                ChainId = "chain_1",
                ChainIndex = 0,
            };
            var chainTip = new Recording
            {
                RecordingId = "rec_tip",
                VesselName = "Probe",
                TreeId = "tree_1",
                MergeState = MergeState.CommittedProvisional,
                TerminalStateValue = TerminalState.Destroyed,
                ChainId = "chain_1",
                ChainIndex = 1,
            };
            var siblingA = Rec("rec_a", MergeState.Immutable, TerminalState.Landed,
                parentBranchPointId: "bp_1");
            InstallTree("tree_1",
                new List<Recording> { siblingA, chainHead, chainTip },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"),
                Slot(1, "rec_head"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);

            // Sealing the chain tip to Immutable closes the slot.
            chainTip.MergeState = MergeState.Immutable;
            int afterSeal = RewindPointReaper.ReapOrphanedRPs();
            Assert.Equal(1, afterSeal);
            Assert.Empty(scenario.RewindPoints);
        }

        [Fact]
        public void Reap_ImmutableChainTipSharedByTwoRps_BothReaped()
        {
            // After collapse-seal-into-mergestate the reaper no longer consults
            // the classifier's `downstreamBp` branch: open/closed is read
            // purely from the slot's effective tip MergeState. Two RPs whose
            // slots both resolve to the same Immutable chain tip are both
            // closed and both reaped (a concluded chain tip closes every RP
            // that depends on it). A genuinely re-flyable downstream slot would
            // carry a CommittedProvisional tip, which keeps both RPs alive.
            var bpUpstream = Bp("bp_1", "rp_1");
            var bpDownstream = Bp("bp_2", "rp_2");
            var chainHead = new Recording
            {
                RecordingId = "rec_head",
                VesselName = "Probe",
                TreeId = "tree_1",
                MergeState = MergeState.Immutable,
                ParentBranchPointId = "bp_1",
                ChainId = "chain_1",
                ChainIndex = 0,
            };
            var chainTip = new Recording
            {
                RecordingId = "rec_tip",
                VesselName = "Probe",
                TreeId = "tree_1",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ChildBranchPointId = "bp_2",
                ChainId = "chain_1",
                ChainIndex = 1,
            };
            InstallTree("tree_1",
                new List<Recording> { chainHead, chainTip },
                new List<BranchPoint> { bpUpstream, bpDownstream });
            // Upstream slot: origin walks chain rec_head -> rec_tip (Immutable).
            var rpUpstream = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_head"));
            // Downstream slot: origin resolves directly to rec_tip (Immutable).
            var rpDownstream = Rp("rp_2", "bp_2", sessionProvisional: false,
                Slot(0, "rec_tip"));
            var scenario = InstallScenario(
                new List<RewindPoint> { rpUpstream, rpDownstream });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(2, reaped);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bpUpstream.RewindPointId);
            Assert.Null(bpDownstream.RewindPointId);
            Assert.Contains("rp_1", deletedRpIds);
            Assert.Contains("rp_2", deletedRpIds);
        }

        [Fact]
        public void Reap_OpenStrandedEvaSlot_KeepsRpAlive()
        {
            // A stranded-EVA tip promoted to CommittedProvisional is open. The
            // companion focus slot's tip is Immutable (concluded), so only the
            // open EVA slot keeps the RP alive.
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable, TerminalState.Landed,
                        parentBranchPointId: "bp_1"),
                    Rec("rec_eva", MergeState.CommittedProvisional, TerminalState.Landed,
                        evaCrewName: "Jebediah Kerman",
                        parentBranchPointId: "bp_1"),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"),
                Slot(1, "rec_eva"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);
        }

        [Fact]
        public void Reap_OpenNonFocusStableLeafSlot_KeepsRpAlive()
        {
            // A non-focused stable leaf promoted to CommittedProvisional is
            // open. The focus slot's tip is Immutable (concluded on its own
            // terminal path); the non-focus CP slot keeps the RP alive until
            // the player Seals it.
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_focus", MergeState.Immutable, TerminalState.Landed,
                        parentBranchPointId: "bp_1"),
                    Rec("rec_other", MergeState.CommittedProvisional, TerminalState.Orbiting,
                        parentBranchPointId: "bp_1"),
                },
                new List<BranchPoint> { bp });
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_focus"),
                Slot(1, "rec_other"));
            rp.FocusSlotIndex = 0;
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int reaped = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, reaped);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);
        }

        [Fact]
        public void Reap_OpenCrashedSlot_SealedClosesIt()
        {
            // An open crashed slot (CP tip) keeps the RP alive. Sealing flips
            // its tip to Immutable, and the RP reaps on the next pass.
            var bp = Bp("bp_1", "rp_1");
            var crashedTip = Rec("rec_destroyed", MergeState.CommittedProvisional,
                TerminalState.Destroyed, parentBranchPointId: "bp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.Immutable, TerminalState.Landed,
                        parentBranchPointId: "bp_1"),
                    crashedTip,
                },
                new List<BranchPoint> { bp });
            var crashedSlot = Slot(1, "rec_destroyed");
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_a"),
                crashedSlot);
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int first = RewindPointReaper.ReapOrphanedRPs();
            Assert.Equal(0, first);
            Assert.Single(scenario.RewindPoints);

            crashedTip.MergeState = MergeState.Immutable;

            int second = RewindPointReaper.ReapOrphanedRPs();
            Assert.Equal(1, second);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Equal(new[] { "rp_1" }, deletedRpIds);
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
        public void Reap_AllSlotTipsImmutable_Reaped()
        {
            // A slot whose effective tip is Immutable is closed; with every
            // slot's tip Immutable the RP reaps.
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
        }

        [Fact]
        public void Reap_AnySlotNotCommittedTip_StillRetained()
        {
            // A NotCommitted tip is open (recorder still running) and keeps the
            // RP alive even when every other slot's tip is Immutable.
            var bp = Bp("bp_1", "rp_1");
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_a", MergeState.NotCommitted),
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
        public void Reap_MixedClosedAndOpenSlots_ReapsAfterLastOpenTipSealed()
        {
            // Open/closed is read from each slot's tip MergeState. The RP stays
            // alive while any tip is CommittedProvisional, and reaps once the
            // last open tip is sealed to Immutable.
            var bp = Bp("bp_1", "rp_1");
            var openTip = Rec("rec_open", MergeState.CommittedProvisional);
            InstallTree("tree_1",
                new List<Recording>
                {
                    Rec("rec_immutable", MergeState.Immutable),
                    openTip,
                    Rec("rec_sealed", MergeState.Immutable),
                },
                new List<BranchPoint> { bp });
            var openSlot = Slot(1, "rec_open");
            var rp = Rp("rp_1", "bp_1", sessionProvisional: false,
                Slot(0, "rec_immutable"),
                openSlot,
                Slot(2, "rec_sealed"));
            var scenario = InstallScenario(new List<RewindPoint> { rp });

            int first = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(0, first);
            Assert.Single(scenario.RewindPoints);
            Assert.Equal("rp_1", bp.RewindPointId);
            Assert.Empty(deletedRpIds);

            openTip.MergeState = MergeState.Immutable;

            int second = RewindPointReaper.ReapOrphanedRPs();

            Assert.Equal(1, second);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(bp.RewindPointId);
            Assert.Equal(new[] { "rp_1" }, deletedRpIds);
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
