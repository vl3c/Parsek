using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The live KSC building patch reads the ledger's building state AT LIVE UT, never the
    /// walk's (which can include future rows when it runs with no cutoff), acts only where a
    /// row at or before now contradicts the live building, and never while a flight owns
    /// collapses the ledger has not received. Each scenario below is one the PR #1784 review
    /// named; the pure folds and decisions are what PatchLiveDestructionState composes.
    /// </summary>
    public class KscBuildingLivePatchTests
    {
        private const string X = "SpaceCenter/LaunchPad/Facility/mainBuilding";
        private const string Y = "SpaceCenter/LaunchPad/Facility/Flag";

        private static GameAction Destroy(string id, double ut, int seq = 0, string rec = null)
            => new GameAction { Type = GameActionType.FacilityDestruction, FacilityId = id, UT = ut, Sequence = seq, RecordingId = rec };

        private static GameAction Repair(string id, double ut, int seq = 0)
            => new GameAction { Type = GameActionType.FacilityRepair, FacilityId = id, UT = ut, Sequence = seq, FacilityCost = 100f };

        private static FacilityStatePatcher.DestructionPatchAction Decide(
            IReadOnlyList<GameAction> ledger, double liveUt, string id, bool isIntact, bool isDestroyed)
        {
            var atUt = FacilityStatePatcher.ComputeBuildingDestroyedAtUt(ledger, liveUt);
            bool destroyed;
            bool? target = atUt.TryGetValue(id, out destroyed) ? destroyed : (bool?)null;
            return FacilityStatePatcher.ResolveLiveDestructionPatch(target, isIntact, isDestroyed);
        }

        // (a) Revert + merge: the recorded flight knocked X down at UT 1500, the player
        // reverted to UT 1000 (stock restored X) and merged. The committed destruction is in
        // the future: the live patch must not demolish X at UT 1000, and a later
        // full-walk recalc must not either.
        [Fact]
        public void RevertThenMerge_FutureDestruction_DoesNotDemolishTheRestoredBuilding()
        {
            var ledger = new List<GameAction> { Destroy(X, 1500.0, rec: "rec-1") };

            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.None,
                Decide(ledger, 1000.0, X, isIntact: true, isDestroyed: false));

            // The walk with no cutoff says destroyed - exactly the state that must not reach
            // the live building.
            var module = new FacilitiesModule();
            foreach (var a in ledger) module.ProcessAction(a);
            Assert.True(module.IsFacilityDestroyed(X));
        }

        // (a, continued) A repair at UT 1000 of some OTHER collapse, then a future
        // destruction: the building stays as the player left it.
        [Fact]
        public void RevertThenMerge_RepairNowThenFutureDestruction_LeavesTheBuildingIntact()
        {
            var ledger = new List<GameAction> { Destroy(X, 900.0), Repair(X, 1000.0), Destroy(X, 1500.0, rec: "rec-1") };
            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.None,
                Decide(ledger, 1000.0, X, isIntact: true, isDestroyed: false));
        }

        // Once the clock passes the recorded destruction, the timeline's collapse does apply.
        [Fact]
        public void PastTheRecordedDestruction_TheIntactBuildingIsDemolished()
        {
            var ledger = new List<GameAction> { Destroy(X, 1500.0, rec: "rec-1") };
            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.Demolish,
                Decide(ledger, 1600.0, X, isIntact: true, isDestroyed: false));
        }

        // (b) Mirror direction: X's last ledger row is a repair, and a live recorded flight
        // has just knocked X down again (tagged, not in the ledger). The warp-start patch
        // must not repair it: the gate refuses while a recorder is live, an uncommitted tree
        // is active, or a tree is pending.
        [Fact]
        public void LiveFlightCollapse_OverAnEarlierRepair_IsNotRepairedWhileTheFlightOwnsIt()
        {
            var ledger = new List<GameAction> { Destroy(X, 900.0), Repair(X, 1000.0) };
            // Without the gate the fold alone would repair it - which is why the gate exists.
            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.Repair,
                Decide(ledger, 1200.0, X, isIntact: false, isDestroyed: true));

            Assert.Equal("live recorder active",
                FacilityStatePatcher.ResolveDestructionPatchSkipReason(true, true, false, 1200.0));
            Assert.Equal("active uncommitted flight tree",
                FacilityStatePatcher.ResolveDestructionPatchSkipReason(false, true, false, 1200.0));
            Assert.Equal("pending tree",
                FacilityStatePatcher.ResolveDestructionPatchSkipReason(false, false, true, 1200.0));
            Assert.Null(FacilityStatePatcher.ResolveDestructionPatchSkipReason(false, false, false, 1200.0));
        }

        // (c) After a rewind to between the destruction and the KSC repair, the repair is a
        // future row: the building (destroyed in the quicksave) is not repaired early.
        [Fact]
        public void AfterRewind_FutureRepair_DoesNotRepairEarly()
        {
            var ledger = new List<GameAction> { Destroy(X, 1000.0, rec: "rec-1"), Repair(X, 2000.0) };
            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.None,
                Decide(ledger, 1500.0, X, isIntact: false, isDestroyed: true));
            // ... and once the clock passes the repair it is applied.
            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.Repair,
                Decide(ledger, 2100.0, X, isIntact: false, isDestroyed: true));
        }

        // (c) A cold load reads UT 0 before the clock is ready: nothing is patched.
        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.NaN)]
        public void ColdLoad_ClockNotReady_SkipsTheBuildingPatch(double ut)
        {
            Assert.Equal("universe clock not ready",
                FacilityStatePatcher.ResolveDestructionPatchSkipReason(false, false, false, ut));
        }

        // Never restore from the absence of a row: a building the ledger knows nothing about
        // (or only knows about in the future) is left in whatever state stock has it.
        [Fact]
        public void NoRowAtOrBeforeNow_IsNeverActedOn()
        {
            var ledger = new List<GameAction> { Destroy(Y, 500.0) };
            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.None,
                Decide(ledger, 1000.0, X, isIntact: false, isDestroyed: true));
            Assert.Equal(FacilityStatePatcher.DestructionPatchAction.None,
                FacilityStatePatcher.ResolveLiveDestructionPatch(null, true, false));
        }

        // Agreement and animation are no-ops in both directions.
        [Theory]
        [InlineData(true, false, true)]   // ledger destroyed, live destroyed
        [InlineData(false, true, false)]  // ledger intact, live intact
        [InlineData(true, false, false)]  // ledger destroyed, live collapsing
        [InlineData(false, false, false)] // ledger intact, live repairing
        public void AgreementOrAnimation_IsNotPatched(bool ledgerDestroyed, bool isIntact, bool isDestroyed)
        {
            var action = FacilityStatePatcher.ResolveLiveDestructionPatch(ledgerDestroyed, isIntact, isDestroyed);
            Assert.NotEqual(FacilityStatePatcher.DestructionPatchAction.Demolish, action);
            Assert.NotEqual(FacilityStatePatcher.DestructionPatchAction.Repair, action);
        }

        [Fact]
        public void Fold_OrdersByUtThenSequence_AndIgnoresOtherTypesAndFutureRows()
        {
            var ledger = new List<GameAction>
            {
                Repair(X, 100.0, seq: 5),
                Destroy(X, 100.0, seq: 2),                  // same UT, earlier sequence: repair wins
                Destroy(Y, 50.0),
                Repair(Y, 150.0),                           // future at UT 120
                new GameAction { Type = GameActionType.FacilityUpgrade, FacilityId = X, UT = 110.0, ToLevel = 2 },
                null,
            };
            var atUt = FacilityStatePatcher.ComputeBuildingDestroyedAtUt(ledger, 120.0);
            Assert.False(atUt[X]);
            Assert.True(atUt[Y]);
            Assert.Equal(2, atUt.Count);
            Assert.Empty(FacilityStatePatcher.ComputeBuildingDestroyedAtUt(null, 120.0));
        }

        // A scene load's recalc runs before ScenarioDestructibles has loaded the save into
        // the buildings (KB-1 2026-09-23_2018): an unregistered building is not read.
        [Fact]
        public void UnregisteredBuilding_StateIsNotRead()
        {
            Assert.False(FacilityStatePatcher.IsBuildingStateLoaded(false));
            Assert.True(FacilityStatePatcher.IsBuildingStateLoaded(true));
        }

        // M1: one intact test for seed, poll and events; a building mid-animation has none.
        [Theory]
        [InlineData(true, false, true, true)]
        [InlineData(false, true, true, false)]
        [InlineData(false, false, false, false)]
        public void TryReadSettledIntact_MidAnimationHasNoValue(
            bool isIntact, bool isDestroyed, bool settled, bool intact)
        {
            bool read;
            Assert.Equal(settled, GameStateFacilityRecorder.TryReadSettledIntact(isIntact, isDestroyed, out read));
            if (settled) Assert.Equal(intact, read);
        }
    }
}
