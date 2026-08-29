using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// UNAFFORDABLE-SCIENCE-SPENDING-SILENTLY-RE-LOCKS-A-TECH-NODE.
    ///
    /// <para>The defect: <c>ScienceModule.ProcessSpending</c> marks a spend the reconstructed
    /// pool cannot cover <c>Affordable=false</c> (no deduct, WARN only);
    /// <c>KspStatePatcher.BuildTargetTechIdsForPatch</c> then drops the row from the
    /// authoritative researched-tech set; and <c>PatchTechTree</c>'s not-in-target branch
    /// re-locks the node and strips its <c>ProtoTechNode</c> — taking the node's purchased
    /// parts with it, unrecoverable in a default career. Reachable from ORDINARY recalcs, not
    /// only from time travel.</para>
    ///
    /// <para>The fix is a GUARD, not an announcement: when the ONLY reason a node left the
    /// target set is a refused-unaffordable <c>ScienceSpending</c> row, the re-lock is
    /// refused, the live researched state is preserved, and the patcher WARNs. A re-lock for
    /// any LEGITIMATE reason (rewound past the research, tombstoned row, never researched)
    /// must be completely untouched.</para>
    ///
    /// <para><b>How much of the real path these cells drive.</b>
    /// <c>PatchTechTree</c> itself cannot run headlessly — it early-returns on
    /// <c>ResearchAndDevelopment.Instance == null</c> before its loop. So the cells drive the
    /// real <c>BuildTargetTechIdsForPatch</c> (over real <c>GameAction</c> rows that a real
    /// <c>ScienceModule.ProcessSpending</c> classified) and then the real
    /// <c>ResolveTechRelockOutcome</c> — which IS the production branch selector inside that
    /// loop, not a re-implementation of it. <c>Relock</c> is the arm that removes the proto
    /// node and flips the state; <c>RefusedUnaffordable</c> is the arm that mutates nothing.</para>
    ///
    /// <para>The FAILS-ON-MAIN cells are
    /// <see cref="Fixed_UnaffordableSpend_RefusesTheRelockAndPreservesLiveState"/> and
    /// <see cref="RealWalk_UnderfundedLedger_ArmsTheGuardForTheUnpayableNode"/>: neutering
    /// <c>ShouldRefuseUnaffordableRelock</c> inside <c>ResolveTechRelockOutcome</c> (which is
    /// exactly main's behaviour) reds those two and leaves every legitimate-re-lock cell
    /// green. <see cref="Repro_UnaffordableSpend_WouldRelockAndStripProto_WithoutTheGuard"/>
    /// is NOT one of them — it is a characterization pin that documents the defect by
    /// passing no drop map, and it passes on main and here alike.</para>
    /// </summary>
    [Collection("Sequential")]
    public class UnaffordableRelockGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public UnaffordableRelockGuardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecalculationEngine.ClearModules();
        }

        public void Dispose()
        {
            RecalculationEngine.ClearModules();
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================
        // The producer half: ScienceModule stamps the positive refusal marker
        // ================================================================

        [Fact]
        public void ProcessSpending_Refused_StampsRunningScienceMarkerAndDoesNotDeduct()
        {
            var module = new ScienceModule();
            module.ProcessAction(MakeScienceInitial(10f));

            var spend = MakeSpending("advRocketry", cost: 45f, ut: 100.0);
            module.ProcessSpending(spend);

            Assert.False(spend.Affordable);
            Assert.True(spend.UnaffordableRunningScience.HasValue);
            Assert.Equal(10.0, spend.UnaffordableRunningScience.Value, 3);
            Assert.Contains(logLines, l =>
                l.Contains("[ScienceModule]") && l.Contains("Spending NOT affordable"));
        }

        [Fact]
        public void ProcessSpending_Affordable_LeavesMarkerNull()
        {
            var module = new ScienceModule();
            module.ProcessAction(MakeScienceInitial(90f));

            var spend = MakeSpending("advRocketry", cost: 45f, ut: 100.0);
            module.ProcessSpending(spend);

            Assert.True(spend.Affordable);
            Assert.False(spend.UnaffordableRunningScience.HasValue);
        }

        [Fact]
        public void ProcessSpending_AffordableAfterAPriorRefusal_ClearsTheStaleMarker()
        {
            // Idempotency: a second walk that CAN pay must not leave the previous walk's
            // refusal marker standing, or the guard would fire on a healthy reconstruction.
            var spend = MakeSpending("advRocketry", cost: 45f, ut: 100.0);

            var poor = new ScienceModule();
            poor.ProcessAction(MakeScienceInitial(10f));
            poor.ProcessSpending(spend);
            Assert.True(spend.UnaffordableRunningScience.HasValue);

            var rich = new ScienceModule();
            rich.ProcessAction(MakeScienceInitial(90f));
            rich.ProcessSpending(spend);

            Assert.True(spend.Affordable);
            Assert.False(spend.UnaffordableRunningScience.HasValue);
        }

        [Fact]
        public void RecalculationEngine_ResetDerivedFields_ClearsTheRefusalMarker()
        {
            // The engine's per-walk reset must clear the marker alongside Affordable;
            // a stale non-null value would make a legitimate re-lock look like a shortfall.
            var spend = MakeSpending("advRocketry", cost: 45f, ut: 100.0);
            spend.Affordable = false;
            spend.UnaffordableRunningScience = 3.0;

            var actions = new List<GameAction> { MakeScienceInitial(1000f), spend };
            RecalculationEngine.RegisterModule(
                new ScienceModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.Recalculate(actions);

            // The walk now affords it, so the marker must be gone (and the row affordable).
            Assert.True(spend.Affordable);
            Assert.False(spend.UnaffordableRunningScience.HasValue);
        }

        // ================================================================
        // The discriminator: IsRefusedUnaffordableUnlockRow
        // ================================================================

        [Fact]
        public void IsRefusedUnaffordableUnlockRow_RefusedRowInsideCutoff_IsTrue()
        {
            var row = MakeRefusedRow("advRocketry", cost: 45f, ut: 100.0, running: 10.0);
            Assert.True(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: 200.0));
            Assert.True(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: null));
        }

        [Fact]
        public void IsRefusedUnaffordableUnlockRow_RowPastCutoff_IsFalse()
        {
            // The ordinary "rewound past the research" case: the row is not a claim at this
            // cutoff at all, so it never reaches the affordability question.
            var row = MakeRefusedRow("advRocketry", cost: 45f, ut: 300.0, running: 10.0);
            Assert.False(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: 200.0));
        }

        [Fact]
        public void IsRefusedUnaffordableUnlockRow_UnprocessedRow_IsFalse()
        {
            // THE over-reach trap. ResetDerivedFields seeds Affordable=false on every action,
            // so a row the science walk never dispatched looks "unaffordable" on that field
            // alone. Only the positive marker separates the two, and this cell is what reds
            // if the predicate is ever relaxed to a bare !Affordable.
            var row = MakeSpending("advRocketry", cost: 45f, ut: 100.0);
            row.Affordable = false;
            row.UnaffordableRunningScience = null;

            Assert.False(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: 200.0));
        }

        [Fact]
        public void IsRefusedUnaffordableUnlockRow_AffordableRow_IsFalse()
        {
            var row = MakeSpending("advRocketry", cost: 45f, ut: 100.0);
            row.Affordable = true;
            Assert.False(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: 200.0));
        }

        [Theory]
        [InlineData(GameActionType.ScienceEarning)]
        [InlineData(GameActionType.FundsSpending)]
        [InlineData(GameActionType.ContractComplete)]
        public void IsRefusedUnaffordableUnlockRow_NonScienceSpendingType_IsFalse(GameActionType type)
        {
            var row = MakeRefusedRow("advRocketry", cost: 45f, ut: 100.0, running: 10.0);
            row.Type = type;
            Assert.False(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: 200.0));
        }

        [Fact]
        public void IsRefusedUnaffordableUnlockRow_NullOrEmptyNodeId_IsFalse()
        {
            Assert.False(KspStatePatcher.IsRefusedUnaffordableUnlockRow(null, utCutoff: 200.0));

            var row = MakeRefusedRow("advRocketry", cost: 45f, ut: 100.0, running: 10.0);
            row.NodeId = null;
            Assert.False(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: 200.0));
            row.NodeId = string.Empty;
            Assert.False(KspStatePatcher.IsRefusedUnaffordableUnlockRow(row, utCutoff: 200.0));
        }

        // ================================================================
        // BuildTargetTechIdsForPatch: the drop map
        // ================================================================

        [Fact]
        public void BuildTarget_RefusedRow_IsExcludedFromTargetAndReportedAsADrop()
        {
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 100.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.NotNull(target);
            Assert.DoesNotContain("advRocketry", target);

            Assert.NotNull(drops);
            var drop = Assert.Single(drops).Value;
            Assert.Equal("advRocketry", drop.NodeId);
            Assert.Equal(45f, drop.Cost);
            Assert.Equal(10.0, drop.RunningScience, 3);
            Assert.Equal(100.0, drop.Ut, 3);
        }

        [Fact]
        public void BuildTarget_RowRewoundPastTheCutoff_IsNotADrop()
        {
            // The legitimate re-lock: the research is in the future of this cutoff. It must
            // NOT arm the guard, or rewinding past a research would stop un-researching.
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 300.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.DoesNotContain("advRocketry", target);
            Assert.NotNull(drops);
            Assert.Empty(drops);
        }

        [Fact]
        public void BuildTarget_UnprocessedRow_IsNotADrop()
        {
            var row = MakeSpending("advRocketry", cost: 45f, ut: 100.0);
            row.Affordable = false;
            row.UnaffordableRunningScience = null;

            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"), new List<GameAction> { row },
                utCutoff: 200.0, baselineTechExclusions: null, out drops);

            Assert.NotNull(drops);
            Assert.Empty(drops);
        }

        [Fact]
        public void BuildTarget_NodeAlsoInBaseline_IsNotADrop()
        {
            // "Solely" half 1: the node reached the target set via the baseline, so it was
            // never dropped and there is nothing for the guard to protect.
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start", "advRocketry"),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 100.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.Contains("advRocketry", target);
            Assert.Empty(drops);
        }

        [Fact]
        public void BuildTarget_SecondAffordableRowForTheSameNode_IsNotADrop()
        {
            // "Solely" half 2, order-independent: the refused row comes FIRST in the list,
            // so the map must be pruned after the walk, not merely guarded during it.
            var refused = MakeRefusedRow("advRocketry", 45f, 100.0, 10.0);
            var paid = MakeSpending("advRocketry", cost: 45f, ut: 150.0);
            paid.Affordable = true;

            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"),
                new List<GameAction> { refused, paid },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.Contains("advRocketry", target);
            Assert.Empty(drops);
        }

        [Fact]
        public void BuildTarget_TwoRefusalsForOneNode_KeepsTheLatest()
        {
            var early = MakeRefusedRow("advRocketry", 45f, 100.0, running: 10.0);
            var late = MakeRefusedRow("advRocketry", 45f, 180.0, running: 2.0);

            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"), new List<GameAction> { late, early },
                utCutoff: 200.0, baselineTechExclusions: null, out drops);

            var drop = Assert.Single(drops).Value;
            Assert.Equal(180.0, drop.Ut, 3);
            Assert.Equal(2.0, drop.RunningScience, 3);
        }

        [Fact]
        public void BuildTarget_NoUsableBaseline_ReportsNullTargetAndNullDrops()
        {
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                new List<GameStateBaseline>(),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 100.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.Null(target);
            Assert.Null(drops);
        }

        [Fact]
        public void BuildTarget_FourArgOverload_StillBehavesIdentically()
        {
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 100.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null);

            Assert.NotNull(target);
            Assert.Single(target);
            Assert.Contains("start", target);
        }

        // ================================================================
        // The consumed decision: PatchTechTree's not-in-target branch
        // ================================================================

        [Fact]
        public void Repro_UnaffordableSpend_WouldRelockAndStripProto_WithoutTheGuard()
        {
            // FAILS-ON-MAIN cell. Same career situation as the guarded cell below: a node
            // the surviving ledger says was researched, live-available, dropped from the
            // target set by an unaffordable row. Passing no drop map is exactly main's
            // behaviour — and the decision is Relock, the arm that removes the ProtoTechNode
            // (purchased parts and all) and flips the static state to Unavailable.
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 100.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.DoesNotContain("advRocketry", target);

            var unguarded = KspStatePatcher.ResolveTechRelockOutcome(
                currentlyAvailable: true,
                protoBackedAvailable: true,
                techId: "advRocketry",
                unaffordableDrops: null);

            Assert.Equal(KspStatePatcher.TechRelockOutcome.Relock, unguarded);
        }

        [Fact]
        public void Fixed_UnaffordableSpend_RefusesTheRelockAndPreservesLiveState()
        {
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 100.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.DoesNotContain("advRocketry", target);

            var guarded = KspStatePatcher.ResolveTechRelockOutcome(
                currentlyAvailable: true,
                protoBackedAvailable: true,
                techId: "advRocketry",
                unaffordableDrops: drops);

            Assert.Equal(KspStatePatcher.TechRelockOutcome.RefusedUnaffordable, guarded);
        }

        [Fact]
        public void Legitimate_RewoundPastResearch_StillRelocks()
        {
            // The over-reach cell: rewinding past a research must still un-research it. The
            // row exists and is even refused, but it lives past the cutoff, so it is not a
            // claim at this UT and arms nothing.
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"),
                new List<GameAction> { MakeRefusedRow("advRocketry", 45f, 300.0, 10.0) },
                utCutoff: 200.0,
                baselineTechExclusions: null,
                out drops);

            Assert.DoesNotContain("advRocketry", target);
            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.Relock,
                KspStatePatcher.ResolveTechRelockOutcome(true, true, "advRocketry", drops));
        }

        [Fact]
        public void Legitimate_TombstonedRow_StillRelocks()
        {
            // A merge tombstone retires the unlock row entirely: it is gone from the ELS and
            // excluded from the baseline. Nothing claims the node, so the re-lock stands.
            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start", "advRocketry"),
                new List<GameAction>(),
                utCutoff: 200.0,
                baselineTechExclusions: new[] { "advRocketry" },
                out drops);

            Assert.DoesNotContain("advRocketry", target);
            Assert.Empty(drops);
            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.Relock,
                KspStatePatcher.ResolveTechRelockOutcome(true, true, "advRocketry", drops));
        }

        [Fact]
        public void Legitimate_NeverResearchedNode_IsAlreadyUnavailable()
        {
            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.AlreadyUnavailable,
                KspStatePatcher.ResolveTechRelockOutcome(false, false, "advRocketry", null));
        }

        [Fact]
        public void Guard_DoesNotFireOnANodeThatIsNotLiveAvailable()
        {
            // Nothing to preserve: the node is not researched live, so the guard must stand
            // down and let the ordinary already-unavailable accounting run.
            var drops = DropMap("advRocketry");
            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.AlreadyUnavailable,
                KspStatePatcher.ResolveTechRelockOutcome(false, false, "advRocketry", drops));
        }

        [Fact]
        public void Guard_StandsDownOnSplitStateWithNoProtoNode()
        {
            // KSP's availability is split across the proto dictionary and the static tree
            // node. In the "static says Available, no proto" shape there is nothing to
            // preserve — no proto means no purchased parts — so the guard must stand down
            // and let the pre-existing normalization drop the stale static Available.
            var drops = DropMap("advRocketry");

            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.Relock,
                KspStatePatcher.ResolveTechRelockOutcome(
                    currentlyAvailable: true,
                    protoBackedAvailable: false,
                    techId: "advRocketry",
                    unaffordableDrops: drops));

            // The mirror shape (proto present) is the one worth protecting.
            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.RefusedUnaffordable,
                KspStatePatcher.ResolveTechRelockOutcome(
                    currentlyAvailable: true,
                    protoBackedAvailable: true,
                    techId: "advRocketry",
                    unaffordableDrops: drops));
        }

        [Fact]
        public void Guard_DoesNotFireOnADifferentNode()
        {
            var drops = DropMap("advRocketry");
            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.Relock,
                KspStatePatcher.ResolveTechRelockOutcome(true, true, "generalRocketry", drops));
        }

        // ================================================================
        // ShouldRefuseUnaffordableRelock: the raw predicate matrix
        // ================================================================

        // The classification is passed as its enum ORDINAL because TechNodePatchAction is
        // internal and this test class (as xUnit requires) is public.
        [Theory]
        // MakeAvailable=0, AlreadyAvailable=1, MakeUnavailable=2, AlreadyUnavailable=3
        [InlineData(2, true, true)]
        [InlineData(2, false, false)]
        [InlineData(3, true, false)]
        [InlineData(1, true, false)]
        [InlineData(0, true, false)]
        public void ShouldRefuseUnaffordableRelock_Matrix(
            int classificationOrdinal, bool inDropMap, bool expected)
        {
            var classification =
                (KspStatePatcher.TechNodePatchAction)classificationOrdinal;
            var drops = inDropMap ? DropMap("advRocketry") : DropMap();
            Assert.Equal(expected, KspStatePatcher.ShouldRefuseUnaffordableRelock(
                classification, "advRocketry", drops));
        }

        [Fact]
        public void ShouldRefuseUnaffordableRelock_MatrixOrdinalsStillNameTheRightMembers()
        {
            // Pins the ordinals the matrix above encodes, so a reordering of the enum reds
            // here instead of silently re-labelling the matrix rows.
            Assert.Equal(0, (int)KspStatePatcher.TechNodePatchAction.MakeAvailable);
            Assert.Equal(1, (int)KspStatePatcher.TechNodePatchAction.AlreadyAvailable);
            Assert.Equal(2, (int)KspStatePatcher.TechNodePatchAction.MakeUnavailable);
            Assert.Equal(3, (int)KspStatePatcher.TechNodePatchAction.AlreadyUnavailable);
        }

        [Fact]
        public void ShouldRefuseUnaffordableRelock_NullOrEmptyInputs_AreFalse()
        {
            Assert.False(KspStatePatcher.ShouldRefuseUnaffordableRelock(
                KspStatePatcher.TechNodePatchAction.MakeUnavailable, "advRocketry", null));
            Assert.False(KspStatePatcher.ShouldRefuseUnaffordableRelock(
                KspStatePatcher.TechNodePatchAction.MakeUnavailable, null, DropMap("advRocketry")));
            Assert.False(KspStatePatcher.ShouldRefuseUnaffordableRelock(
                KspStatePatcher.TechNodePatchAction.MakeUnavailable, string.Empty, DropMap("advRocketry")));
        }

        // ================================================================
        // End-to-end through a real recalculation walk
        // ================================================================

        [Fact]
        public void RealWalk_UnderfundedLedger_ArmsTheGuardForTheUnpayableNode()
        {
            // No hand-set Affordable anywhere: the real engine walks a real ScienceModule
            // over a ledger whose earnings do not cover the second unlock, and the resulting
            // drop map is what the guard consumes.
            var seed = MakeScienceInitial(0f);
            var earn = new GameAction
            {
                UT = 10.0, Type = GameActionType.ScienceEarning,
                ScienceAwarded = 50f, SubjectMaxValue = 100f, SubjectId = "srf@Kerbin"
            };
            var paid = MakeSpending("basicRocketry", cost: 45f, ut: 50.0);
            var unpayable = MakeSpending("advRocketry", cost: 90f, ut: 100.0);

            var actions = new List<GameAction> { seed, earn, paid, unpayable };
            RecalculationEngine.RegisterModule(
                new ScienceModule(), RecalculationEngine.ModuleTier.FirstTier);
            RecalculationEngine.Recalculate(actions);

            Assert.True(paid.Affordable);
            Assert.False(unpayable.Affordable);
            Assert.True(unpayable.UnaffordableRunningScience.HasValue);

            Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> drops;
            var target = KspStatePatcher.BuildTargetTechIdsForPatch(
                Baselines("start"), actions,
                utCutoff: 200.0, baselineTechExclusions: null, out drops);

            Assert.Contains("basicRocketry", target);
            Assert.DoesNotContain("advRocketry", target);

            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.RefusedUnaffordable,
                KspStatePatcher.ResolveTechRelockOutcome(true, true, "advRocketry", drops));
            // The paid node is in the target set, so it never reaches the re-lock branch.
            Assert.Equal(
                KspStatePatcher.TechRelockOutcome.Relock,
                KspStatePatcher.ResolveTechRelockOutcome(true, true, "someOtherNode", drops));
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static GameAction MakeScienceInitial(float amount)
        {
            return new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = amount
            };
        }

        private static GameAction MakeSpending(string nodeId, float cost, double ut)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceSpending,
                NodeId = nodeId,
                Cost = cost
            };
        }

        private static GameAction MakeRefusedRow(string nodeId, float cost, double ut, double running)
        {
            var action = MakeSpending(nodeId, cost, ut);
            action.Affordable = false;
            action.UnaffordableRunningScience = running;
            return action;
        }

        private static List<GameStateBaseline> Baselines(params string[] techIds)
        {
            var baseline = new GameStateBaseline { ut = 0.0 };
            baseline.researchedTechIds.AddRange(techIds);
            return new List<GameStateBaseline> { baseline };
        }

        private static Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop> DropMap(
            params string[] nodeIds)
        {
            var map = new Dictionary<string, KspStatePatcher.UnaffordableUnlockDrop>(
                StringComparer.Ordinal);
            for (int i = 0; i < nodeIds.Length; i++)
            {
                map[nodeIds[i]] = new KspStatePatcher.UnaffordableUnlockDrop(
                    nodeIds[i], 45f, 10.0, 100.0);
            }
            return map;
        }
    }
}
