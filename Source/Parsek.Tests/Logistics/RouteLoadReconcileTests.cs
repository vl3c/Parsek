using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The in-session load route reconcile (todo ROUTE-STATE-NOT-RECONCILED-ON-F9-REVERT-DISCARD,
    /// owner ruling 2026-10-07): an F9 quickload, a stock revert and the Esc-menu Discard Re-fly
    /// load run the go-back rewind's reconcile (<see cref="Ledger.RetireFutureRouteActionsAtRewind"/>
    /// + <see cref="RouteRewindClassifier.ReconcileStoreAtRewind"/>) keyed to the loaded save's UT,
    /// then take each kept route's loop position back from the loaded save's own ROUTES copy.
    /// Covers the per-kind decision, the cutoff, the back-in-time evidence that gates the other
    /// in-session loads, the loop-position restore, the store-level run and the OnLoad wiring
    /// (OnLoad is not xUnit-drivable, so a source gate pins the hook, as
    /// <see cref="RouteGoBackRewindReconcileTests"/> does for the go-back exit).
    /// </summary>
    [Collection("Sequential")]
    public class RouteLoadReconcileTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteLoadReconcileTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekScenario.ResetInstanceForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static Route MakeLoopRoute(string id = "r", double createdUT = 900.0)
        {
            return new Route
            {
                Id = id,
                Name = "route-" + id,
                Status = RouteStatus.Active,
                CreatedUT = createdUT,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-" + id,
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-" + id,
                LoopAnchorUT = 1000.0,
                DispatchInterval = 300.0,
                TransitDuration = 300.0,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-" + id, TreeId = "tree-" + id, RouteProofHash = "deadbeef" },
                },
            };
        }

        private static Route Clone(Route route)
        {
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            Route copy = Route.DeserializeFrom(node);
            Assert.NotNull(copy);
            return copy;
        }

        private static GameAction Dispatched(string routeId, string cycleId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 0,
            };
        }

        private static GameAction Delivered(string routeId, string cycleId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 3,
            };
        }

        private static readonly LoadKind[] BackInTimeKinds =
        {
            LoadKind.QuickloadFlight, LoadKind.StockRevert, LoadKind.DiscardReFly,
        };

        private static IEnumerable<LoadKind> AllKinds()
            => Enum.GetValues(typeof(LoadKind)).Cast<LoadKind>();

        // ------------------------------------------------------------------
        // Decide: which loads run the reconcile
        // ------------------------------------------------------------------

        [Theory]
        [InlineData((int)LoadKind.QuickloadFlight)]
        [InlineData((int)LoadKind.StockRevert)]
        [InlineData((int)LoadKind.DiscardReFly)]
        public void Decide_BackInTimeKinds_RunEvenWithNoRouteEvidence(int kindValue)
        {
            var kind = (LoadKind)kindValue;
            // The load itself went back in time (classifier): a cursor that advanced after the
            // save leaves no UT stamp, so these kinds cannot wait for evidence.
            Assert.Equal(RouteLoadReconcileOutcome.Reconciled,
                RouteLoadReconcile.Decide(kind, 1200.0, routeStateAfterCutoff: false));
            Assert.Equal(RouteLoadReconcileOutcome.Reconciled,
                RouteLoadReconcile.Decide(kind, 1200.0, routeStateAfterCutoff: true));
        }

        [Fact]
        public void Decide_InSessionOther_RunsOnlyWhenRouteStateLiesAfterTheLoadedSave()
        {
            // F9 at the Space Center / Tracking Station, or into a flight quicksave from the
            // Space Center: classified InSessionOther, back in time only by evidence.
            Assert.Equal(RouteLoadReconcileOutcome.Reconciled,
                RouteLoadReconcile.Decide(LoadKind.InSessionOther, 1200.0, routeStateAfterCutoff: true));
            // A forward or same-instant scene change: nothing after the save, nothing to do.
            Assert.Equal(RouteLoadReconcileOutcome.SkippedNothingAfterCutoff,
                RouteLoadReconcile.Decide(LoadKind.InSessionOther, 1200.0, routeStateAfterCutoff: false));
        }

        [Theory]
        [InlineData((int)LoadKind.Cold)]
        [InlineData((int)LoadKind.PlainRewind)]
        [InlineData((int)LoadKind.ReFlyStart)]
        public void Decide_KindsOwnedElsewhere_NeverRunHere(int kindValue)
        {
            var kind = (LoadKind)kindValue;
            Assert.Equal(RouteLoadReconcileOutcome.SkippedOwnedElsewhere,
                RouteLoadReconcile.Decide(kind, 1200.0, routeStateAfterCutoff: true));
            Assert.Equal(RouteLoadReconcileOutcome.SkippedOwnedElsewhere,
                RouteLoadReconcile.Decide(kind, 1200.0, routeStateAfterCutoff: false));
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(-1.0)]
        [InlineData(0.0)]
        [InlineData(double.PositiveInfinity)]
        public void Decide_NoUsableCutoff_Skips(double cutoff)
        {
            foreach (LoadKind kind in BackInTimeKinds.Concat(new[] { LoadKind.InSessionOther }))
            {
                Assert.Equal(RouteLoadReconcileOutcome.SkippedNoUsableCutoff,
                    RouteLoadReconcile.Decide(kind, cutoff, routeStateAfterCutoff: true));
            }
        }

        [Fact]
        public void Decide_RunsForExactlyTheInSessionKindsThePolicyTableReconcilesAtTheCutoff()
        {
            // PlainRewind's cell is ReconcileAtCutoff too, but HandleRewindOnLoad owns it and
            // returns before this hook.
            var tableKinds = AllKinds()
                .Where(k => k != LoadKind.PlainRewind)
                .Where(k => LoadReconcilePolicy.Decide(k, LoadStateCategory.Routes).Action
                    == LoadReconcileAction.ReconcileAtCutoff)
                .OrderBy(k => k)
                .ToList();
            var hookKinds = AllKinds()
                .Where(k => RouteLoadReconcile.Decide(k, 1200.0, routeStateAfterCutoff: true)
                    == RouteLoadReconcileOutcome.Reconciled)
                .OrderBy(k => k)
                .ToList();
            Assert.Equal(tableKinds, hookKinds);
            Assert.Equal(
                new[] { LoadKind.DiscardReFly, LoadKind.StockRevert, LoadKind.QuickloadFlight, LoadKind.InSessionOther }
                    .OrderBy(k => k),
                hookKinds);
        }

        // ------------------------------------------------------------------
        // ResolveCutoffUT
        // ------------------------------------------------------------------

        [Fact]
        public void ResolveCutoff_IsTheLoadedSaveUT_ExceptARevertTakesTheEarlierLaunchBoundary()
        {
            Assert.Equal(1200.0, RouteLoadReconcile.ResolveCutoffUT(LoadKind.QuickloadFlight, 1200.0, 900.0));
            Assert.Equal(1200.0, RouteLoadReconcile.ResolveCutoffUT(LoadKind.DiscardReFly, 1200.0, 900.0));
            Assert.Equal(1200.0, RouteLoadReconcile.ResolveCutoffUT(LoadKind.InSessionOther, 1200.0, double.NaN));

            // A revert to the editor: the loaded game can carry the revert-moment clock, the
            // revert prune's launch boundary is the instant the world went back to.
            Assert.Equal(900.0, RouteLoadReconcile.ResolveCutoffUT(LoadKind.StockRevert, 1500.0, 900.0));
            Assert.Equal(900.0, RouteLoadReconcile.ResolveCutoffUT(LoadKind.StockRevert, 900.0, 1500.0));
            Assert.Equal(1500.0, RouteLoadReconcile.ResolveCutoffUT(LoadKind.StockRevert, 1500.0, double.NaN));
            Assert.Equal(900.0, RouteLoadReconcile.ResolveCutoffUT(LoadKind.StockRevert, double.NaN, 900.0));
            Assert.True(double.IsNaN(RouteLoadReconcile.ResolveCutoffUT(LoadKind.StockRevert, double.NaN, double.NaN)));
            Assert.True(double.IsNaN(RouteLoadReconcile.ResolveCutoffUT(LoadKind.QuickloadFlight, double.NaN, 900.0)));
        }

        // ------------------------------------------------------------------
        // HasRouteStateAfterUT: the back-in-time evidence
        // ------------------------------------------------------------------

        [Fact]
        public void Evidence_NothingAfterTheCutoff_IsFalse()
        {
            var route = MakeLoopRoute();
            route.LastHoldUT = 1200.0;          // exactly at the cutoff: strict >
            route.PendingDeliveryUT = 5000.0;   // scheduled, not happened
            route.NextEligibilityCheckUT = 5000.0;
            route.NextDispatchUT = 5000.0;

            Assert.False(RouteLoadReconcile.HasRouteStateAfterUT(
                1200.0, new[] { route }, ledgerHasFutureRouteRows: false, out string evidence));
            Assert.Equal("none", evidence);
        }

        [Fact]
        public void Evidence_EachHappenedStampAfterTheCutoff_IsTrue()
        {
            Assert.True(RouteLoadReconcile.HasRouteStateAfterUT(
                1200.0, new List<Route>(), ledgerHasFutureRouteRows: true, out string rows));
            Assert.Equal("ledger-rows", rows);

            var created = MakeLoopRoute(createdUT: 1300.0);
            Assert.True(RouteLoadReconcile.HasRouteStateAfterUT(1200.0, new[] { created }, false, out string e1));
            Assert.Contains("created", e1);

            var held = MakeLoopRoute();
            held.LastHoldUT = 1300.0;
            Assert.True(RouteLoadReconcile.HasRouteStateAfterUT(1200.0, new[] { held }, false, out _));

            var cycle = MakeLoopRoute();
            cycle.CurrentCycleStartUT = 1300.0;
            Assert.True(RouteLoadReconcile.HasRouteStateAfterUT(1200.0, new[] { cycle }, false, out _));

            var partial = MakeLoopRoute();
            partial.LastPartialDeliveryUT = 1300.0;
            Assert.True(RouteLoadReconcile.HasRouteStateAfterUT(1200.0, new[] { partial }, false, out _));

            var credit = MakeLoopRoute();
            credit.PendingRecoveryCreditCycleId = "cycle-4";
            credit.PendingRecoveryCreditDispatchUT = 1300.0;
            Assert.True(RouteLoadReconcile.HasRouteStateAfterUT(1200.0, new[] { credit }, false, out _));
        }

        [Fact]
        public void LedgerEvidence_ReadsOnlyFreeStandingRouteRowsAfterTheCutoff()
        {
            Ledger.AddAction(Dispatched("r", "cycle-0", 1200.0));             // at the cutoff
            Ledger.AddAction(new GameAction { Type = GameActionType.FundsEarning, UT = 1500.0 });
            Assert.False(Ledger.HasFreeStandingRouteActionsAfterUT(1200.0));

            Ledger.AddAction(Dispatched("r", "cycle-1", 1450.0));
            Assert.True(Ledger.HasFreeStandingRouteActionsAfterUT(1200.0));
        }

        // ------------------------------------------------------------------
        // RestoreLoopPositionFromSave
        // ------------------------------------------------------------------

        [Fact]
        public void Restore_TakesTheSavedLoopPosition_WhenTheClockDefinitionMatches()
        {
            var saved = MakeLoopRoute();
            saved.LastObservedLoopCycleIndex = 0;
            saved.WindowAnchorCycleIndex = 0;
            saved.Stops[0].LastFiredCycleIndex = 0;
            var kept = Clone(saved);
            kept.LastObservedLoopCycleIndex = -1;  // the reconcile's reset
            kept.WindowAnchorCycleIndex = -1;
            kept.Stops[0].LastFiredCycleIndex = -1;

            int restored = RouteLoadReconcile.RestoreLoopPositionFromSave(
                new[] { kept }, new[] { saved }, 1200.0, 1200.0,
                out int credits, out int changed, out int missing);

            Assert.Equal(1, restored);
            Assert.Equal(0, credits);
            Assert.Equal(0, changed);
            Assert.Equal(0, missing);
            Assert.Equal(0, kept.LastObservedLoopCycleIndex);
            Assert.Equal(0, kept.WindowAnchorCycleIndex);
            Assert.Equal(0, kept.Stops[0].LastFiredCycleIndex);
        }

        [Fact]
        public void Restore_ClockDefinitionChangedAfterTheSave_KeepsTheReset()
        {
            var saved = MakeLoopRoute();
            saved.LastObservedLoopCycleIndex = 4;
            var kept = Clone(saved);
            kept.LastObservedLoopCycleIndex = -1;
            kept.CadenceMultiplier = 2;           // cadence changed in the abandoned future
            kept.DispatchInterval = 600.0;        // the saved index is in another index space

            int restored = RouteLoadReconcile.RestoreLoopPositionFromSave(
                new[] { kept }, new[] { saved }, 1200.0, 1200.0,
                out _, out int changed, out _);

            Assert.Equal(0, restored);
            Assert.Equal(1, changed);
            Assert.Equal(-1, kept.LastObservedLoopCycleIndex);
            Assert.False(RouteLoadReconcile.ClockDefinitionMatches(kept, saved));
            Assert.True(RouteLoadReconcile.ClockDefinitionMatches(saved, Clone(saved)));
        }

        [Fact]
        public void Restore_SaveNewerThanTheCutoff_RestoresNothing()
        {
            // A revert to the editor can hand OnLoad a game carrying the revert-moment state:
            // its loop position is not the position at the cutoff.
            var saved = MakeLoopRoute();
            saved.LastObservedLoopCycleIndex = 7;
            var kept = Clone(saved);
            kept.LastObservedLoopCycleIndex = -1;

            int restored = RouteLoadReconcile.RestoreLoopPositionFromSave(
                new[] { kept }, new[] { saved }, cutoffUT: 900.0, loadedSaveUT: 1500.0,
                out _, out _, out _);

            Assert.Equal(0, restored);
            Assert.Equal(-1, kept.LastObservedLoopCycleIndex);
        }

        [Fact]
        public void Restore_RouteMissingFromTheSave_KeepsTheReset()
        {
            var kept = MakeLoopRoute("only-live");
            kept.LastObservedLoopCycleIndex = -1;

            int restored = RouteLoadReconcile.RestoreLoopPositionFromSave(
                new[] { kept }, new[] { MakeLoopRoute("other") }, 1200.0, 1200.0,
                out _, out _, out int missing);

            Assert.Equal(0, restored);
            Assert.Equal(1, missing);
            Assert.Equal(-1, kept.LastObservedLoopCycleIndex);
        }

        [Fact]
        public void Restore_OwedRecoveryCredit_ComesBackWhenItsDispatchIsAtOrBeforeTheCutoff()
        {
            var saved = MakeLoopRoute();
            saved.PendingRecoveryCreditCycleId = "cycle-0";
            saved.PendingRecoveryCreditDispatchUT = 1150.0;
            var kept = Clone(saved);
            kept.PendingRecoveryCreditCycleId = null;     // flushed after the save, then reset
            kept.PendingRecoveryCreditDispatchUT = -1.0;

            RouteLoadReconcile.RestoreLoopPositionFromSave(
                new[] { kept }, new[] { saved }, 1200.0, 1200.0,
                out int credits, out _, out _);

            Assert.Equal(1, credits);
            Assert.Equal("cycle-0", kept.PendingRecoveryCreditCycleId);
            Assert.Equal(1150.0, kept.PendingRecoveryCreditDispatchUT);

            // A saved credit owed by a dispatch after the cutoff is not the cutoff's.
            var late = MakeLoopRoute("late");
            late.PendingRecoveryCreditCycleId = "cycle-2";
            late.PendingRecoveryCreditDispatchUT = 1300.0;
            var keptLate = Clone(late);
            keptLate.PendingRecoveryCreditCycleId = null;
            keptLate.PendingRecoveryCreditDispatchUT = -1.0;
            RouteLoadReconcile.RestoreLoopPositionFromSave(
                new[] { keptLate }, new[] { late }, 1200.0, 1200.0,
                out int lateCredits, out _, out _);
            Assert.Equal(0, lateCredits);
            Assert.Null(keptLate.PendingRecoveryCreditCycleId);

            // A credit the live route still owes is left alone.
            var owing = MakeLoopRoute("owing");
            owing.PendingRecoveryCreditCycleId = "cycle-1";
            owing.PendingRecoveryCreditDispatchUT = 1100.0;
            var keptOwing = Clone(owing);
            keptOwing.PendingRecoveryCreditCycleId = "cycle-1";
            RouteLoadReconcile.RestoreLoopPositionFromSave(
                new[] { keptOwing }, new[] { saved }, 1200.0, 1200.0, out _, out _, out _);
            Assert.Equal("cycle-1", keptOwing.PendingRecoveryCreditCycleId);
        }

        // ------------------------------------------------------------------
        // ReconcileAtInSessionLoad: store-level runs
        // ------------------------------------------------------------------

        [Fact]
        public void Run_QuickloadBackPastADispatch_RetiresTheRowsAndRestoresTheSavedPosition()
        {
            var live = MakeLoopRoute();
            var futureRoute = MakeLoopRoute("built-later", createdUT: 1300.0);
            // Saved at 1200: cycle-0 dispatched at 1150, cursor 0.
            live.LastObservedLoopCycleIndex = 0;
            live.CompletedCycles = 1;
            var savedAt1200 = Clone(live);
            // The abandoned future: cycle-1 at 1450, cursor 1, a route built at 1300.
            live.LastObservedLoopCycleIndex = 1;
            live.CompletedCycles = 2;
            live.SendOnceArmed = true;
            RouteStore.AddRoute(live);
            RouteStore.AddRoute(futureRoute);
            Ledger.AddAction(Dispatched("r", "cycle-0", 1150.0));
            Ledger.AddAction(Delivered("r", "cycle-0", 1150.0));
            Ledger.AddAction(Dispatched("r", "cycle-1", 1450.0));
            Ledger.AddAction(Delivered("r", "cycle-1", 1450.0));

            var outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.QuickloadFlight, 1200.0, 1200.0, new[] { savedAt1200 });

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            // Route rows after the save retired, the rest kept.
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.DoesNotContain(Ledger.Actions, a => a.UT > 1200.0);
            // The loop position is the save's, the counters come from the kept rows.
            Assert.Equal(0, live.LastObservedLoopCycleIndex);
            Assert.Equal(1, live.CompletedCycles + live.SkippedCycles);
            // Armed one-shots do not survive time travel (the shared reconcile's rule).
            Assert.False(live.SendOnceArmed);
            // The route built after the save is dormant until the timeline reaches it again.
            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Contains(RouteStore.DormantRoutes, r => r.Id == "built-later");
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("Route reconcile at in-session load: kind=QuickloadFlight")
                && l.Contains("cutoff=1200") && l.Contains("retiredRouteRows=2")
                && l.Contains("loopPositionsRestored=1"));
        }

        [Fact]
        public void Run_ForwardSceneChange_TouchesNothing()
        {
            var live = MakeLoopRoute();
            live.LastObservedLoopCycleIndex = 1;
            live.CompletedCycles = 2;
            live.PauseAfterCurrentCycle = true;
            RouteStore.AddRoute(live);
            Ledger.AddAction(Dispatched("r", "cycle-0", 1150.0));
            Ledger.AddAction(Dispatched("r", "cycle-1", 1450.0));
            int version = Ledger.StateVersion;

            // The save was written at the scene change, at the current instant (1500).
            var outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.InSessionOther, 1500.0, 1500.0, new[] { Clone(live) });

            Assert.Equal(RouteLoadReconcileOutcome.SkippedNothingAfterCutoff, outcome);
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Equal(version, Ledger.StateVersion);
            Assert.Equal(1, live.LastObservedLoopCycleIndex);
            Assert.Equal(2, live.CompletedCycles);
            Assert.True(live.PauseAfterCurrentCycle);
            Assert.Contains(logLines, l => l.Contains("[LoadPolicy]")
                && l.Contains("Route reconcile skipped at in-session load: kind=InSessionOther")
                && l.Contains("outcome=SkippedNothingAfterCutoff"));
        }

        [Fact]
        public void Run_SpaceCenterQuickloadBackPastADispatch_IsReconciledByEvidence()
        {
            var live = MakeLoopRoute();
            live.LastObservedLoopCycleIndex = 0;
            var savedAt1200 = Clone(live);
            live.LastObservedLoopCycleIndex = 1;
            RouteStore.AddRoute(live);
            Ledger.AddAction(Dispatched("r", "cycle-0", 1150.0));
            Ledger.AddAction(Dispatched("r", "cycle-1", 1450.0));

            var outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.InSessionOther, 1200.0, 1200.0, new[] { savedAt1200 });

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            Assert.Single(Ledger.Actions);
            Assert.Equal(0, live.LastObservedLoopCycleIndex);
        }

        [Theory]
        [InlineData((int)LoadKind.Cold)]
        [InlineData((int)LoadKind.PlainRewind)]
        [InlineData((int)LoadKind.ReFlyStart)]
        public void Run_KindsOwnedElsewhere_TouchNothing(int kindValue)
        {
            var live = MakeLoopRoute();
            live.LastObservedLoopCycleIndex = 1;
            RouteStore.AddRoute(live);
            Ledger.AddAction(Dispatched("r", "cycle-1", 1450.0));

            var outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                (LoadKind)kindValue, 1200.0, 1200.0, new List<Route>());

            Assert.Equal(RouteLoadReconcileOutcome.SkippedOwnedElsewhere, outcome);
            Assert.Single(Ledger.Actions);
            Assert.Equal(1, live.LastObservedLoopCycleIndex);
        }

        [Fact]
        public void Run_NodeOverload_ReadsTheLoadedSavesRoutes()
        {
            var live = MakeLoopRoute();
            live.LastObservedLoopCycleIndex = 0;
            RouteStore.AddRoute(live);
            var scenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(scenarioNode);   // the save at 1200
            live.LastObservedLoopCycleIndex = 1;      // then the abandoned future
            Ledger.AddAction(Dispatched("r", "cycle-1", 1450.0));

            Assert.Single(RouteStore.ReadSavedCommittedRoutes(scenarioNode));
            Assert.Empty(RouteStore.ReadSavedCommittedRoutes(new ConfigNode("SCENARIO")));
            Assert.Empty(RouteStore.ReadSavedCommittedRoutes(null));

            var outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.StockRevert, 1200.0, 1200.0, scenarioNode);

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            Assert.Equal(0, live.LastObservedLoopCycleIndex);
            Assert.Empty(Ledger.Actions);
        }

        // ------------------------------------------------------------------
        // OnLoad wiring gate
        // ------------------------------------------------------------------

        [Fact]
        public void OnLoad_RunsTheRouteReconcileAfterTheRevertPruneAndBeforeTheRecalculation()
        {
            string repo = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(repo, "Source", "Parsek", "ParsekScenario.cs");
            Assert.True(File.Exists(path), "ParsekScenario.cs not found at " + path);
            string prepared = SourceScanText.StripCommentsAndMaskLiterals(File.ReadAllText(path));

            const string sig = "public override void OnLoad(ConfigNode node)";
            int sigIdx = prepared.IndexOf(sig, StringComparison.Ordinal);
            Assert.True(sigIdx >= 0, "OnLoad signature not found");
            string body = SourceScanText.BraceMatchedBlock(prepared, prepared.IndexOf('{', sigIdx));
            string flat = System.Text.RegularExpressions.Regex.Replace(body, @"\s+", " ");

            const string hook = "Logistics.RouteLoadReconcile.ReconcileAtInSessionLoad(";
            int first = body.IndexOf(hook, StringComparison.Ordinal);
            Assert.True(first >= 0, "OnLoad must call " + hook);
            Assert.True(body.IndexOf(hook, first + 1, StringComparison.Ordinal) < 0,
                "OnLoad calls the in-session route reconcile exactly once (the in-session branch; the cold "
                + "path reads routes from the save and the rewind branch runs HandleRewindOnLoad's own reconcile)");

            int refined = body.IndexOf("LoadReconcilePolicy.ClassifyRefined(", StringComparison.Ordinal);
            int prune = body.IndexOf("Ledger.PruneOrphanActionsAfterUT(", StringComparison.Ordinal);
            int future = body.IndexOf("LedgerOrchestrator.HasActionsAfterUT(loadedUT)", StringComparison.Ordinal);
            int recalc = body.IndexOf("LedgerOrchestrator.RecalculateAndPatch();", StringComparison.Ordinal);
            int coldRoutes = body.IndexOf("RouteStore.LoadRoutesFrom(node);", StringComparison.Ordinal);
            Assert.True(refined >= 0 && prune >= 0 && future >= 0 && recalc >= 0 && coldRoutes >= 0);
            Assert.True(refined < first && prune < first,
                "the route reconcile reads the refined load kind and runs after the revert prune");
            Assert.True(first < future && first < recalc,
                "the route reconcile retires rows before the future-actions check and the recalculation");
            Assert.True(first < coldRoutes, "the hook sits in the in-session branch, not on the cold path");

            Assert.Contains("Logistics.RouteLoadReconcile.ReconcileAtInSessionLoad( refinedLoadKind, "
                + "Logistics.RouteLoadReconcile.ResolveCutoffUT( refinedLoadKind, loadedSaveUTForRoutes, "
                + "revertPruneCutoffUTForRoutes), loadedSaveUTForRoutes, node);", flat);
            Assert.Contains("double loadedSaveUTForRoutes = KerbalsModule.ReadLoadedSaveUT();", flat);
            Assert.Contains("revertPruneCutoffUTForRoutes = pruneCutoffUT;", flat);
        }
    }
}
