using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 2 guard tests for <see cref="EffectiveState"/> — the shared
    /// ERS/ELS helper (design doc sections 3.1 and 3.2).
    ///
    /// <para>
    /// Each test name states the regression it guards. Log-assertion tests
    /// verify the expected <c>[ERS]</c> / <c>[ELS]</c> / <c>[Supersede]</c>
    /// tagged lines from design §10.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class EffectiveStateTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool priorVerbose;

        public EffectiveStateTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            // Stash the verbose toggle so the outer environment's setting is
            // restored after the test; we force verbose ON here so ERS/ELS
            // rebuild Verbose lines reach the test sink.
            priorVerbose = ParsekLog.IsVerboseEnabled;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
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
        }

        // --- Helpers ----------------------------------------------------------

        private static RecordingSupersedeRelation Rel(string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = "rsr_" + oldId + "_" + newId,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 0.0
            };
        }

        private static Recording Rec(string id, MergeState state = MergeState.Immutable,
            TerminalState? terminal = null,
            string parentBranchPointId = null,
            string treeId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                TreeId = treeId
            };
        }

        private static ParsekScenario MakeScenario(
            List<RecordingSupersedeRelation> supersedes = null,
            List<LedgerTombstone> tombstones = null,
            List<RewindPoint> rps = null,
            ReFlySessionMarker marker = null)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = supersedes ?? new List<RecordingSupersedeRelation>(),
                LedgerTombstones = tombstones ?? new List<LedgerTombstone>(),
                RewindPoints = rps ?? new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            // Bump so EffectiveState sees a non-zero version on first read; the
            // default-0 start could otherwise mask a regression where the
            // initial-zero state matches a stale cache.
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static RewindPoint Rp(string rpId, string bpId, params string[] slotRecordingIds)
        {
            var slots = new List<ChildSlot>();
            if (slotRecordingIds != null)
            {
                for (int i = 0; i < slotRecordingIds.Length; i++)
                {
                    slots.Add(new ChildSlot
                    {
                        SlotIndex = i,
                        OriginChildRecordingId = slotRecordingIds[i],
                        Controllable = true
                    });
                }
            }

            return new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                UT = 0.0,
                SessionProvisional = false,
                ChildSlots = slots
            };
        }

        // =====================================================================
        // EffectiveRecordingId: forward walk
        // =====================================================================

        [Fact]
        public void EffectiveRecordingId_ChainLen0_NoSupersede_ReturnsOrigin()
        {
            string eff = EffectiveState.EffectiveRecordingId("rec_A", new List<RecordingSupersedeRelation>());
            Assert.Equal("rec_A", eff);
        }

        [Fact]
        public void EffectiveRecordingId_ChainLen1_ReturnsSuperseder()
        {
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            Assert.Equal("rec_B", EffectiveState.EffectiveRecordingId("rec_A", list));
        }

        [Fact]
        public void EffectiveRecordingId_ChainLen2_ReturnsLastInChain()
        {
            var list = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_C")
            };
            Assert.Equal("rec_C", EffectiveState.EffectiveRecordingId("rec_A", list));
        }

        [Fact]
        public void EffectiveRecordingId_ChainLen3_ReturnsLastInChain()
        {
            var list = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_C"),
                Rel("rec_C", "rec_D")
            };
            Assert.Equal("rec_D", EffectiveState.EffectiveRecordingId("rec_A", list));
        }

        [Fact]
        public void EffectiveRecordingId_Cycle_LogsWarnReturnsLastVisited()
        {
            var list = new List<RecordingSupersedeRelation>
            {
                Rel("rec_A", "rec_B"),
                Rel("rec_B", "rec_A")
            };
            string eff = EffectiveState.EffectiveRecordingId("rec_A", list);

            // Walk: rec_A -> rec_B; at rec_B finds relation pointing back to rec_A
            // (already visited). Method logs Warn and returns the last-visited id
            // reached before closing the cycle (rec_B).
            Assert.Equal("rec_B", eff);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("cycle detected") && l.Contains("rec_A"));
        }

        [Fact]
        public void EffectiveRecordingId_OrphanEndpoint_ReturnsLastNonSuperseded()
        {
            // rec_B is the new id for rec_A, and no supersede has rec_B as Old.
            // Per design §5.2, B IS the effective id — the walk returns B.
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            Assert.Equal("rec_B", EffectiveState.EffectiveRecordingId("rec_A", list));
        }

        [Fact]
        public void EffectiveRecordingId_NullOrigin_ReturnsNull()
        {
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            Assert.Null(EffectiveState.EffectiveRecordingId(null, list));
            Assert.Null(EffectiveState.EffectiveRecordingId("", list));
        }

        // =====================================================================
        // IsVisible
        // =====================================================================

        [Fact]
        public void IsVisible_Immutable_NoSupersede_True()
        {
            var rec = Rec("rec_A", MergeState.Immutable);
            Assert.True(EffectiveState.IsVisible(rec, new List<RecordingSupersedeRelation>()));
        }

        [Fact]
        public void IsVisible_NotCommitted_False()
        {
            var rec = Rec("rec_A", MergeState.NotCommitted);
            Assert.False(EffectiveState.IsVisible(rec, new List<RecordingSupersedeRelation>()));
        }

        [Fact]
        public void IsVisible_CommittedProvisional_NoSupersede_True()
        {
            var rec = Rec("rec_A", MergeState.CommittedProvisional);
            Assert.True(EffectiveState.IsVisible(rec, new List<RecordingSupersedeRelation>()));
        }

        [Fact]
        public void IsVisible_ImmutableSuperseded_False()
        {
            var rec = Rec("rec_A", MergeState.Immutable);
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            Assert.False(EffectiveState.IsVisible(rec, list));
        }

        [Fact]
        public void IsSupersededByRelation_NotCommittedSuperseded_True()
        {
            var rec = Rec("rec_A", MergeState.NotCommitted);
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            Assert.True(EffectiveState.IsSupersededByRelation(rec, list));
        }

        [Fact]
        public void ComputeSupersededRecordingIdsByRelation_EmptyNewId_DoesNotSuppress()
        {
            var rec = Rec("rec_A", MergeState.Immutable);
            var list = new List<RecordingSupersedeRelation> { Rel("rec_A", null) };

            var result = EffectiveState.ComputeSupersededRecordingIdsByRelation(
                new List<Recording> { rec },
                list);

            Assert.Empty(result);
            Assert.False(EffectiveState.IsSupersededByRelation(rec, list));
        }

        // =====================================================================
        // IsUnfinishedFlight
        // =====================================================================

        [Fact]
        public void IsUnfinishedFlight_ImmutableCrashedUnderRP_True()
        {
            var bp = new BranchPoint { Id = "bp_1", Type = BranchPointType.Launch };
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test",
                BranchPoints = new List<BranchPoint> { bp }
            };
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_1", treeId: "tree_1");
            tree.AddOrReplaceRecording(rec);
            RecordingStore.CommittedTrees.Add(tree);

            var rp = Rp("rp_1", "bp_1", "rec_A");
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.True(EffectiveState.IsUnfinishedFlight(rec));
            // Log-assertion: the decision emits an UnfinishedFlights line per
            // design §10.5 so the per-row logic can be audited post-hoc.
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("IsUnfinishedFlight=true") && l.Contains("rec_A"));
        }

        [Fact]
        public void IsUnfinishedFlight_ImmutableLandedUnderRP_False()
        {
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Landed,
                parentBranchPointId: "bp_1");
            var rp = Rp("rp_1", "bp_1", "rec_A");
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.False(EffectiveState.IsUnfinishedFlight(rec));
            // Log-assertion: stable terminal rejects must emit a distinguishing
            // Verbose line so the rejection is visible post-hoc (design §10.5).
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("stableTerminal"));
        }

        [Fact]
        public void IsUnfinishedFlight_ImmutableCrashedNotUnderRP_False()
        {
            // Crash terminal, but the parent BP has no RP written.
            var rec = Rec("rec_A", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_1");
            MakeScenario();

            Assert.False(EffectiveState.IsUnfinishedFlight(rec));
        }

        [Fact]
        public void IsUnfinishedFlight_MultipleMatchingRpsWithoutSlot_LogsAllMisses()
        {
            var rec = Rec("rec_debris", MergeState.Immutable, TerminalState.Destroyed,
                parentBranchPointId: "bp_1");
            MakeScenario(rps: new List<RewindPoint>
            {
                Rp("rp_1", "bp_1", "rec_parent"),
                Rp("rp_2", "bp_1", "rec_controlled_child")
            });

            Assert.False(EffectiveState.IsUnfinishedFlight(rec));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("reason=noMatchingRpSlot")
                && l.Contains("matches=2")
                && l.Contains("rp_1@bp_1")
                && l.Contains("rp_2@bp_1"));
        }

        [Fact]
        public void IsUnfinishedFlight_NotCommitted_False()
        {
            var rec = Rec("rec_A", MergeState.NotCommitted, TerminalState.Destroyed,
                parentBranchPointId: "bp_1");
            var rp = new RewindPoint { RewindPointId = "rp_1", BranchPointId = "bp_1" };
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.False(EffectiveState.IsUnfinishedFlight(rec));
        }

        [Fact]
        public void IsUnfinishedFlight_ChainHeadWithCrashedTip_True()
        {
            // Regression for the post-v0.8.3 booster case: Optimizer.SplitAtSection
            // splits the live recording at the atmo→exo env boundary, leaving the
            // chain HEAD (parentBranchPointId + RP link) with terminal=null and
            // the chain TIP with terminal=Destroyed. The predicate must walk the
            // chain to find the tip's terminal, otherwise a destroyed booster's
            // recording is silently excluded from Unfinished Flights.
            var bp = new BranchPoint { Id = "bp_stage", Type = BranchPointType.JointBreak };
            var tree = new RecordingTree
            {
                Id = "tree_boost",
                TreeName = "Booster",
                BranchPoints = new List<BranchPoint> { bp }
            };
            var head = new Recording
            {
                RecordingId = "rec_atmo",
                VesselName = "Booster (atmo)",
                MergeState = MergeState.Immutable,
                TerminalStateValue = null, // mid-chain, no own terminal
                ParentBranchPointId = "bp_stage",
                TreeId = "tree_boost",
                ChainId = "chain_boost",
                ChainIndex = 0
            };
            var tip = new Recording
            {
                RecordingId = "rec_exo",
                VesselName = "Booster (exo)",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = null, // chain continuation, no RP link
                TreeId = "tree_boost",
                ChainId = "chain_boost",
                ChainIndex = 1
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.CommittedTrees.Add(tree);

            var rp = Rp("rp_stage", "bp_stage", "rec_atmo");
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.True(EffectiveState.IsUnfinishedFlight(head));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("IsUnfinishedFlight=true") && l.Contains("rec_atmo"));
            // The chain-tip classification is also reflected in the ResolveChainTerminalRecording helper.
            Assert.Same(tip, EffectiveState.ResolveChainTerminalRecording(head));
        }

        [Fact]
        public void IsUnfinishedFlight_ChainHeadWithOrbitingTip_False()
        {
            // Non-regression: a post-staging booster that actually reached a
            // stable orbit (tip terminal=Orbiting) must NOT enter Unfinished
            // Flights. The predicate stays narrow: destruction / loss only.
            var bp = new BranchPoint { Id = "bp_stage", Type = BranchPointType.JointBreak };
            var tree = new RecordingTree
            {
                Id = "tree_orbit",
                TreeName = "SafeBoost",
                BranchPoints = new List<BranchPoint> { bp }
            };
            var head = new Recording
            {
                RecordingId = "rec_atmo",
                MergeState = MergeState.Immutable,
                TerminalStateValue = null,
                ParentBranchPointId = "bp_stage",
                TreeId = "tree_orbit",
                ChainId = "chain_orbit",
                ChainIndex = 0
            };
            var tip = new Recording
            {
                RecordingId = "rec_exo",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Orbiting,
                ParentBranchPointId = null,
                TreeId = "tree_orbit",
                ChainId = "chain_orbit",
                ChainIndex = 1
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.CommittedTrees.Add(tree);

            var rp = Rp("rp_stage", "bp_stage", "rec_atmo");
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.False(EffectiveState.IsUnfinishedFlight(head));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("noFocusSignalOrbiting"));
        }

        [Fact]
        public void IsUnfinishedFlight_RepeatedCallsSameRec_RateLimitedToOneLine()
        {
            // Regression: IsUnfinishedFlight is invoked from per-frame UI hot
            // paths (RecordingsTableUI rows, UnfinishedFlightsGroup membership,
            // TimelineBuilder) once per recording per frame. Before the fix it
            // emitted plain Verbose lines on every return path, producing
            // hundreds of thousands of identical lines per session — captured
            // KSP.log dated 2026-04-25 had ~511k UnfinishedFlights lines, ~94%
            // of total file. The branches now route through VerboseRateLimited
            // keyed by reason+recId so each (rec, reason) pair logs at most
            // once per rate-limit window.
            var rec = Rec("rec_spam", MergeState.Immutable, TerminalState.Orbiting,
                parentBranchPointId: "bp_1");
            var rp = Rp("rp_1", "bp_1", "rec_spam");
            MakeScenario(rps: new List<RewindPoint> { rp });

            for (int i = 0; i < 100; i++)
                Assert.False(EffectiveState.IsUnfinishedFlight(rec));

            int noFocusSignalLines = logLines.Count(l =>
                l.Contains("[UnfinishedFlights]") && l.Contains("rec_spam") && l.Contains("noFocusSignalOrbiting"));
            Assert.Equal(1, noFocusSignalLines);
        }

        [Fact]
        public void ResolveChainTerminalRecording_NoChain_ReturnsSelf()
        {
            var rec = new Recording
            {
                RecordingId = "rec_solo",
                ChainId = null,
                TerminalStateValue = TerminalState.Destroyed
            };
            Assert.Same(rec, EffectiveState.ResolveChainTerminalRecording(rec));
        }

        [Fact]
        public void ResolveChainTerminalRecording_ChainLen3_ReturnsMaxIndexSegment()
        {
            var tree = new RecordingTree { Id = "tree_chain", TreeName = "C" };
            var seg0 = new Recording { RecordingId = "s0", TreeId = "tree_chain", ChainId = "ch", ChainIndex = 0, MergeState = MergeState.Immutable };
            var seg1 = new Recording { RecordingId = "s1", TreeId = "tree_chain", ChainId = "ch", ChainIndex = 1, MergeState = MergeState.Immutable };
            var seg2 = new Recording { RecordingId = "s2", TreeId = "tree_chain", ChainId = "ch", ChainIndex = 2, MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Destroyed };
            tree.AddOrReplaceRecording(seg0);
            tree.AddOrReplaceRecording(seg1);
            tree.AddOrReplaceRecording(seg2);
            RecordingStore.CommittedTrees.Add(tree);
            MakeScenario();

            Assert.Same(seg2, EffectiveState.ResolveChainTerminalRecording(seg0));
            Assert.Same(seg2, EffectiveState.ResolveChainTerminalRecording(seg1));
            Assert.Same(seg2, EffectiveState.ResolveChainTerminalRecording(seg2));
        }

        [Fact]
        public void IsChainMemberOfUnfinishedFlight_ChainHeadUnfinishedFlight_ContinuationTripsGate()
        {
            // The chain continuation has no parentBranchPointId of its own, so
            // its IsUnfinishedFlight is false. But its chain head qualifies —
            // the recordings-table row must suppress the legacy rewind-to-
            // launch R button on the continuation via this helper.
            var bp = new BranchPoint { Id = "bp_stage", Type = BranchPointType.JointBreak };
            var tree = new RecordingTree
            {
                Id = "tree_boost",
                TreeName = "Booster",
                BranchPoints = new List<BranchPoint> { bp }
            };
            var head = new Recording
            {
                RecordingId = "rec_atmo",
                MergeState = MergeState.Immutable,
                TerminalStateValue = null,
                ParentBranchPointId = "bp_stage",
                TreeId = "tree_boost",
                ChainId = "chain_boost",
                ChainIndex = 0
            };
            var tip = new Recording
            {
                RecordingId = "rec_exo",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = null,
                TreeId = "tree_boost",
                ChainId = "chain_boost",
                ChainIndex = 1
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.CommittedTrees.Add(tree);
            RecordingStore.AddRecordingWithTreeForTesting(head);
            RecordingStore.AddRecordingWithTreeForTesting(tip);

            var rp = Rp("rp_stage", "bp_stage", "rec_atmo");
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.True(EffectiveState.IsChainMemberOfUnfinishedFlight(head));
            Assert.True(EffectiveState.IsChainMemberOfUnfinishedFlight(tip));
        }

        [Fact]
        public void IsUnfinishedFlight_ActiveParentOfBreakup_WithChildBranchPointId_True()
        {
            // Review item: breakup RP creation includes the surviving active
            // parent as a controllable output (ParsekFlight.TryAuthorRewindPointForBreakup).
            // The active parent references the branch via ChildBranchPointId
            // (not ParentBranchPointId). Without accepting ChildBranchPointId,
            // if the active-parent side later crashes, its row cannot resolve
            // to the RP and never appears as an Unfinished Flight.
            var bp = new BranchPoint { Id = "bp_breakup", Type = BranchPointType.JointBreak };
            var tree = new RecordingTree
            {
                Id = "tree_break",
                TreeName = "Kerbal X",
                BranchPoints = new List<BranchPoint> { bp }
            };
            var activeParent = new Recording
            {
                RecordingId = "rec_active_parent",
                VesselName = "Kerbal X",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = null,          // no parent link
                ChildBranchPointId = "bp_breakup",   // links as the branch parent
                TreeId = "tree_break"
            };
            tree.AddOrReplaceRecording(activeParent);
            RecordingStore.CommittedTrees.Add(tree);

            var rp = Rp("rp_breakup", "bp_breakup", "rec_active_parent");
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.True(EffectiveState.IsUnfinishedFlight(activeParent));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("IsUnfinishedFlight=true")
                && l.Contains("rec_active_parent")
                && l.Contains("side=active-parent-child"));
        }

        [Fact]
        public void IsChainMemberOfUnfinishedFlight_ChainHeadStable_DoesNotTrip()
        {
            // Chain head with safe-terminal tip: NOT an unfinished flight,
            // so the legacy R button should stay available on the continuation.
            var bp = new BranchPoint { Id = "bp_stage", Type = BranchPointType.JointBreak };
            var tree = new RecordingTree
            {
                Id = "tree_safe",
                TreeName = "Safe",
                BranchPoints = new List<BranchPoint> { bp }
            };
            var head = new Recording
            {
                RecordingId = "rec_head",
                MergeState = MergeState.Immutable,
                TerminalStateValue = null,
                ParentBranchPointId = "bp_stage",
                TreeId = "tree_safe",
                ChainId = "chain_safe",
                ChainIndex = 0
            };
            var tip = new Recording
            {
                RecordingId = "rec_tip",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed,
                TreeId = "tree_safe",
                ChainId = "chain_safe",
                ChainIndex = 1
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.CommittedTrees.Add(tree);
            RecordingStore.AddRecordingWithTreeForTesting(head);
            RecordingStore.AddRecordingWithTreeForTesting(tip);

            var rp = new RewindPoint { RewindPointId = "rp_stage", BranchPointId = "bp_stage" };
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.False(EffectiveState.IsChainMemberOfUnfinishedFlight(head));
            Assert.False(EffectiveState.IsChainMemberOfUnfinishedFlight(tip));
        }

        [Fact]
        public void IsChainMemberOfUnfinishedFlight_NoChain_DoesNotTrip()
        {
            var rec = new Recording
            {
                RecordingId = "rec_solo",
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Destroyed,
                ParentBranchPointId = "bp",
                ChainId = null
            };
            MakeScenario();

            Assert.False(EffectiveState.IsChainMemberOfUnfinishedFlight(rec));
        }

        [Fact]
        public void ResolveChainTerminalRecording_DifferentChainBranch_DoesNotCrossBranches()
        {
            // Two siblings on different ChainBranch values must not be treated
            // as the same chain. Each resolves to its own branch's tip.
            var tree = new RecordingTree { Id = "tree_br", TreeName = "Br" };
            var seg0a = new Recording { RecordingId = "s0a", TreeId = "tree_br", ChainId = "ch", ChainBranch = 0, ChainIndex = 0, MergeState = MergeState.Immutable };
            var seg1a = new Recording { RecordingId = "s1a", TreeId = "tree_br", ChainId = "ch", ChainBranch = 0, ChainIndex = 1, MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Landed };
            var seg0b = new Recording { RecordingId = "s0b", TreeId = "tree_br", ChainId = "ch", ChainBranch = 1, ChainIndex = 0, MergeState = MergeState.Immutable };
            var seg1b = new Recording { RecordingId = "s1b", TreeId = "tree_br", ChainId = "ch", ChainBranch = 1, ChainIndex = 1, MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Destroyed };
            tree.AddOrReplaceRecording(seg0a);
            tree.AddOrReplaceRecording(seg1a);
            tree.AddOrReplaceRecording(seg0b);
            tree.AddOrReplaceRecording(seg1b);
            RecordingStore.CommittedTrees.Add(tree);
            MakeScenario();

            Assert.Same(seg1a, EffectiveState.ResolveChainTerminalRecording(seg0a));
            Assert.Same(seg1b, EffectiveState.ResolveChainTerminalRecording(seg0b));
        }

        // =====================================================================
        // ComputeERS
        // =====================================================================

        [Fact]
        public void ComputeERS_FiltersSupersededAndNotCommitted()
        {
            var a = Rec("rec_A", MergeState.Immutable);
            var b = Rec("rec_B", MergeState.Immutable);
            var c = Rec("rec_C", MergeState.Immutable);
            var d = Rec("rec_D", MergeState.NotCommitted);

            RecordingStore.AddRecordingWithTreeForTesting(a);
            RecordingStore.AddRecordingWithTreeForTesting(b);
            RecordingStore.AddRecordingWithTreeForTesting(c);
            RecordingStore.AddRecordingWithTreeForTesting(d);

            // rec_A superseded by rec_B -> A out, B stays (as long as B not also superseded)
            var supersedes = new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") };
            MakeScenario(supersedes: supersedes);

            var ers = EffectiveState.ComputeERS();
            var ids = ers.Select(r => r.RecordingId).ToList();

            Assert.DoesNotContain("rec_A", ids); // superseded
            Assert.DoesNotContain("rec_D", ids); // NotCommitted
            Assert.Contains("rec_B", ids);
            Assert.Contains("rec_C", ids);
            Assert.Equal(2, ers.Count);

            // Design §10 ERS rebuild log.
            Assert.Contains(logLines, l => l.Contains("[ERS]") && l.Contains("Rebuilt"));
        }

        [Fact]
        public void IsCurrentTimelineRecordingId_UsesERSVisibility()
        {
            var a = Rec("rec_A", MergeState.Immutable);
            var b = Rec("rec_B", MergeState.Immutable);
            RecordingStore.AddRecordingWithTreeForTesting(a);
            RecordingStore.AddRecordingWithTreeForTesting(b);
            MakeScenario(supersedes: new List<RecordingSupersedeRelation> { Rel("rec_A", "rec_B") });

            Assert.False(RecordingStore.IsCurrentTimelineRecordingId("rec_A"));
            Assert.True(RecordingStore.IsCurrentTimelineRecordingId("rec_B"));
        }

        [Fact]
        public void ComputeERS_CacheHit_DoesNotRebuild()
        {
            var a = Rec("rec_A", MergeState.Immutable);
            RecordingStore.AddRecordingWithTreeForTesting(a);
            MakeScenario();

            // First call primes cache.
            var firstCall = EffectiveState.ComputeERS();
            logLines.Clear();

            // Second call: no mutation between, so must be a cache hit.
            var secondCall = EffectiveState.ComputeERS();

            Assert.DoesNotContain(logLines, l => l.Contains("[ERS]") && l.Contains("Rebuilt"));
            // Reference-equality: cache-hit must return the SAME instance, not a
            // fresh list with identical contents. If this trips, the cache is
            // re-wrapping and the "does not rebuild" assertion above is hollow.
            Assert.Same(firstCall, secondCall);
        }

        [Fact]
        public void ComputeERS_CacheInvalidatedOnBump()
        {
            var a = Rec("rec_A", MergeState.Immutable);
            RecordingStore.AddRecordingWithTreeForTesting(a);
            MakeScenario();

            EffectiveState.ComputeERS();
            logLines.Clear();

            // Mutate the store: this bumps StateVersion via AddRecordingWithTreeForTesting.
            var b = Rec("rec_B", MergeState.Immutable);
            RecordingStore.AddRecordingWithTreeForTesting(b);

            EffectiveState.ComputeERS();
            Assert.Contains(logLines, l => l.Contains("[ERS]") && l.Contains("Rebuilt"));
        }

        // =====================================================================
        // ComputeELS
        // =====================================================================

        [Fact]
        public void ComputeELS_FiltersByTombstoneOnly_NonDeathActionsPassThrough()
        {
            // Design §3.2: ELS filters ONLY by tombstones. A ContractComplete
            // action tagged with a superseded recording id MUST still appear in
            // ELS when no tombstone targets its ActionId.
            var contract = new GameAction
            {
                ActionId = "act_contract_1",
                Type = GameActionType.ContractComplete,
                UT = 10.0,
                RecordingId = "rec_superseded"
            };
            // Rep penalty bundled with a kerbal death (tombstone-eligible in
            // design §5.6). The action type is ReputationPenalty with a
            // KerbalDeath source; for this test it only needs to carry a stable
            // ActionId the tombstone can target.
            var deathPenalty = new GameAction
            {
                ActionId = "act_death_1",
                Type = GameActionType.ReputationPenalty,
                UT = 11.0,
                RecordingId = "rec_superseded"
            };
            Ledger.AddAction(contract);
            Ledger.AddAction(deathPenalty);

            // Tombstone retires the death-bundled rep penalty but NOT the contract.
            var tomb = new LedgerTombstone
            {
                TombstoneId = "tomb_1",
                ActionId = "act_death_1",
                RetiringRecordingId = "rec_new",
                UT = 12.0
            };
            MakeScenario(tombstones: new List<LedgerTombstone> { tomb });

            var els = EffectiveState.ComputeELS();
            var ids = els.Select(a => a.ActionId).ToList();

            Assert.Contains("act_contract_1", ids); // survives supersede (no tombstone)
            Assert.DoesNotContain("act_death_1", ids); // tombstoned
        }

        [Fact]
        public void ComputeELS_TombstonedActionExcluded()
        {
            var a = new GameAction
            {
                ActionId = "act_1",
                Type = GameActionType.FundsEarning,
                UT = 1.0
            };
            Ledger.AddAction(a);
            var tomb = new LedgerTombstone { TombstoneId = "t1", ActionId = "act_1" };
            MakeScenario(tombstones: new List<LedgerTombstone> { tomb });

            var els = EffectiveState.ComputeELS();
            Assert.Empty(els);

            // Design §10 ELS rebuild log.
            Assert.Contains(logLines, l => l.Contains("[ELS]") && l.Contains("Rebuilt"));
        }

        [Fact]
        public void ComputeSessionSuppressedSubtree_CrossChainSamePidPostRewindPeer_Included()
        {
            // Repro for KSP.log 2026-04-26 23:53:33 a0d14b08/29f1d9a8: an
            // in-place Re-Fly that crossed staging produced two NEW recordings
            // sharing the same VesselPersistentId as the origin (the live
            // probe being re-flown), in DIFFERENT ChainIds. Without the
            // cross-chain same-PID walk, EnqueueChainSiblings only picks up
            // members of the origin's own chain, the destroyed sibling stays
            // out of the closure, AppendRelations writes 0 supersede rows,
            // and the destroyed run remains visible in the mission list +
            // ghost playback.
            const uint sharedPid = 2450432355u;
            var tree = new RecordingTree { Id = "tree_kx", TreeName = "Kerbal X" };

            var origin = new Recording
            {
                RecordingId = "f3f1f2e6", VesselName = "Kerbal X Probe (atmo)",
                MergeState = MergeState.Immutable, TerminalStateValue = null,
                TreeId = "tree_kx", ChainId = "chain_new", ChainIndex = 0,
                VesselPersistentId = sharedPid, ExplicitStartUT = 140.79,
            };
            var newChainTip = new Recording
            {
                RecordingId = "0a83aee0", VesselName = "Kerbal X Probe (exo)",
                MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Orbiting,
                TreeId = "tree_kx", ChainId = "chain_new", ChainIndex = 1,
                VesselPersistentId = sharedPid, ExplicitStartUT = 163.27,
            };
            // Cross-chain destroyed sibling — same PID, started AFTER rewind UT
            // (140.79 -> InvokedUT 141.36 -> sibling start 162.10).
            var crossChainDestroyed = new Recording
            {
                RecordingId = "29f1d9a8", VesselName = "Kerbal X Probe (destroyed)",
                MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Destroyed,
                TreeId = "tree_kx", ChainId = "chain_old", ChainIndex = 1,
                VesselPersistentId = sharedPid, ExplicitStartUT = 162.10,
            };
            // Pre-rewind history for the same PID — must NOT enter the closure.
            var preRewindOrigin = new Recording
            {
                RecordingId = "rec_pre_rewind", VesselName = "Kerbal X Probe (pre-rewind)",
                MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Destroyed,
                TreeId = "tree_kx", ChainId = "chain_pre", ChainIndex = 0,
                VesselPersistentId = sharedPid, ExplicitStartUT = 100.0,
            };

            tree.AddOrReplaceRecording(origin);
            tree.AddOrReplaceRecording(newChainTip);
            tree.AddOrReplaceRecording(crossChainDestroyed);
            tree.AddOrReplaceRecording(preRewindOrigin);
            RecordingStore.CommittedTrees.Add(tree);
            // Closure walk reads RecordingStore.CommittedRecordings (the flat
            // list) to build its recId->Recording map; tree-only registration
            // would bypass it.
            RecordingStore.AddCommittedInternal(origin);
            RecordingStore.AddCommittedInternal(newChainTip);
            RecordingStore.AddCommittedInternal(crossChainDestroyed);
            RecordingStore.AddCommittedInternal(preRewindOrigin);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_test",
                TreeId = "tree_kx",
                ActiveReFlyRecordingId = "f3f1f2e6",
                OriginChildRecordingId = "f3f1f2e6",
                RewindPointId = "rp_test",
                InvokedUT = 141.36,
            };
            MakeScenario(marker: marker);

            var closure = EffectiveState.ComputeSessionSuppressedSubtree(marker);

            Assert.Contains("f3f1f2e6", closure);              // origin always
            Assert.Contains("0a83aee0", closure);              // same-chain sibling
            Assert.Contains("29f1d9a8", closure);              // <-- the regression: cross-chain same-PID post-rewind sibling
            Assert.DoesNotContain("rec_pre_rewind", closure);  // pre-rewind history must stay out

            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]")
                && l.Contains("SessionSuppressedSubtree")
                && l.Contains("pidPeersAdded="));
        }

        [Fact]
        public void ComputeELS_CacheInvalidatedOnTombstoneBump()
        {
            var a = new GameAction
            {
                ActionId = "act_1",
                Type = GameActionType.FundsEarning,
                UT = 1.0
            };
            Ledger.AddAction(a);

            var scenario = MakeScenario();

            EffectiveState.ComputeELS();
            logLines.Clear();

            // Add a tombstone and bump manually (simulating what merge code will do in Phase 6).
            scenario.LedgerTombstones.Add(new LedgerTombstone { TombstoneId = "t1", ActionId = "act_1" });
            scenario.BumpTombstoneStateVersion();

            EffectiveState.ComputeELS();
            Assert.Contains(logLines, l => l.Contains("[ELS]") && l.Contains("Rebuilt"));
        }
    }
}
