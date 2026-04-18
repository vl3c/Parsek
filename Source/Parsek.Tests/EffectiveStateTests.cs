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

            var rp = new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = "bp_1",
                UT = 0.0,
                SessionProvisional = false
            };
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
            var rp = new RewindPoint { RewindPointId = "rp_1", BranchPointId = "bp_1" };
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.False(EffectiveState.IsUnfinishedFlight(rec));
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
        public void IsUnfinishedFlight_NotCommitted_False()
        {
            var rec = Rec("rec_A", MergeState.NotCommitted, TerminalState.Destroyed,
                parentBranchPointId: "bp_1");
            var rp = new RewindPoint { RewindPointId = "rp_1", BranchPointId = "bp_1" };
            MakeScenario(rps: new List<RewindPoint> { rp });

            Assert.False(EffectiveState.IsUnfinishedFlight(rec));
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
        public void ComputeERS_CacheHit_DoesNotRebuild()
        {
            var a = Rec("rec_A", MergeState.Immutable);
            RecordingStore.AddRecordingWithTreeForTesting(a);
            MakeScenario();

            // First call primes cache.
            EffectiveState.ComputeERS();
            logLines.Clear();

            // Second call: no mutation between, so must be a cache hit.
            EffectiveState.ComputeERS();

            Assert.DoesNotContain(logLines, l => l.Contains("[ERS]") && l.Contains("Rebuilt"));
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
