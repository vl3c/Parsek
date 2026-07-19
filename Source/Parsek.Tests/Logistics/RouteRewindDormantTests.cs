using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Dormant-routes rewind-visibility extension
    /// (plan <c>docs/dev/plans/plan-route-rewind-dormant-visibility.md</c>):
    /// pure classifier + cycle-state reconcile + store materialization +
    /// bundle rewind seam. [Collection("Sequential")] + full static reset per
    /// the shared-static rule (mirrors <see cref="RouteRewindRedeliveryTests"/>).
    /// </summary>
    [Collection("Sequential")]
    public class RouteRewindDormantTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteRewindDormantTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        /// <summary>Codec-valid route (one stop, KSC origin) for the save/load
        /// tests, built via the shared fixture builder. Optional treeId adds a
        /// SOURCE ref (the codec accepts refs-less routes but rejects stop-less
        /// ones).</summary>
        private static Route MakeCodecValidRoute(string id, double createdUT, string treeId = null)
        {
            var builder = new Parsek.Tests.Generators.RouteFixtureBuilder()
                .WithId(id)
                .WithName("route-" + id)
                .WithOrigin(new RouteEndpoint
                {
                    BodyName = "Kerbin",
                    Latitude = -0.09,
                    Longitude = -74.55,
                    Altitude = 75.0,
                    VesselPersistentId = 0,
                    IsSurface = true
                })
                .WithStop(new RouteStop
                {
                    Endpoint = new RouteEndpoint
                    {
                        BodyName = "Mun",
                        Latitude = 3.2,
                        Longitude = -45.1,
                        Altitude = 612.0,
                        VesselPersistentId = 67890,
                        IsSurface = true
                    },
                    ConnectionKind = RouteConnectionKind.DockingPort,
                    SegmentIndexBefore = 0,
                    DeliveryOffsetSeconds = 0.0,
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } }
                });
            Route route = builder.Build();
            route.CreatedUT = createdUT;
            if (!string.IsNullOrEmpty(treeId))
            {
                route.SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-" + id, TreeId = treeId },
                };
                route.BackingMissionTreeId = treeId;
            }
            return route;
        }

        /// <summary>Minimal in-memory route; empty SourceRefs so RevalidateSources
        /// skips it (status untouched) unless a test opts into tree ids. NOT
        /// codec-valid (no stops) - use <see cref="MakeCodecValidRoute"/> for
        /// save/load tests.</summary>
        private static Route MakeRoute(string id, double createdUT, string treeId = null)
        {
            var route = new Route
            {
                Id = id,
                Name = "route-" + id,
                Status = RouteStatus.Active,
                CreatedUT = createdUT,
                Stops = new List<RouteStop>(),
            };
            if (!string.IsNullOrEmpty(treeId))
            {
                route.SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-" + id, TreeId = treeId },
                };
                route.BackingMissionTreeId = treeId;
            }
            return route;
        }

        // ------------------------------------------------------------------
        // Classify
        // ------------------------------------------------------------------

        [Fact]
        public void Classify_SplitsAtCutoff_InclusiveKeep()
        {
            var before = MakeRoute("before", 100.0);
            var at = MakeRoute("at", 500.0);
            var after = MakeRoute("after", 500.0000001);
            var legacy = MakeRoute("legacy", -1.0);

            RouteRewindClassifier.Classify(
                new[] { before, at, after, legacy }, null, 500.0,
                out List<Route> committed, out List<Route> dormant);

            Assert.Equal(new[] { "before", "at", "legacy" }, committed.Select(r => r.Id));
            Assert.Equal(new[] { "after" }, dormant.Select(r => r.Id));
        }

        [Fact]
        public void Classify_CarriesEarlierDormantsAndDedupes()
        {
            var kept = MakeRoute("kept", 100.0);
            var newDormant = MakeRoute("newd", 900.0);
            var oldDormant = MakeRoute("oldd", 950.0);
            var dupOfKept = MakeRoute("kept", 950.0); // stale duplicate id in dormant

            RouteRewindClassifier.Classify(
                new[] { kept, newDormant },
                new[] { oldDormant, dupOfKept, oldDormant },
                500.0,
                out List<Route> committed, out List<Route> dormant);

            Assert.Equal(new[] { "kept" }, committed.Select(r => r.Id));
            Assert.Equal(new[] { "newd", "oldd" }, dormant.Select(r => r.Id));
        }

        // ------------------------------------------------------------------
        // ResetCycleStateForRewind
        // ------------------------------------------------------------------

        [Fact]
        public void ResetCycleState_ClearsFutureStateKeepsPast()
        {
            var route = MakeRoute("r", 100.0);
            route.LastObservedLoopCycleIndex = 7;
            route.WindowAnchorCycleIndex = 3;
            route.Status = RouteStatus.InTransit;
            route.CurrentCycleStartUT = 800.0;          // beyond cutoff
            route.PendingDeliveryUT = 850.0;
            route.PendingStopIndex = 0;
            route.NextEligibilityCheckUT = 900.0;
            route.RecordHold(RouteDispatchEvaluator.EligibilityFailureKind.FundsShort, "funds-short", 5.0, 700.0);
            route.LastPartialDeliverySummary = "Ore 1/2";
            route.LastPartialDeliveryUT = 750.0;
            route.LastPartialDeliveryCycleId = "cycle-6";
            route.PendingRecoveryCreditCycleId = "cycle-6";
            route.PendingRecoveryCreditDispatchUT = 760.0;
            route.CompletedCycles = 7;
            route.SkippedCycles = 1;
            route.NextDispatchUT = 4400.0; // abandoned-future legacy schedule

            bool changed = RouteRewindClassifier.ResetCycleStateForRewind(route, 500.0);

            Assert.True(changed);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Equal(-1, route.WindowAnchorCycleIndex);
            Assert.Equal(RouteStatus.Active, route.Status); // InTransit past cutoff returns Active
            Assert.Null(route.CurrentCycleStartUT);
            Assert.Null(route.PendingDeliveryUT);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            Assert.Null(route.LastPartialDeliverySummary);
            Assert.Null(route.PendingRecoveryCreditCycleId);
            // Counters deliberately untouched (documented cosmetic residual).
            Assert.Equal(7, route.CompletedCycles);
            Assert.Equal(1, route.SkippedCycles);
            // Legacy schedule pulled back to the cutoff (due promptly).
            Assert.Equal(500.0, route.NextDispatchUT);
        }

        [Fact]
        public void ResetCycleState_PreservesPreCutoffPendingState()
        {
            var route = MakeRoute("r", 100.0);
            route.Status = RouteStatus.InTransit;
            route.CurrentCycleStartUT = 400.0;          // at/before cutoff: cycle is real
            route.PendingDeliveryUT = 450.0;
            route.PendingStopIndex = 0;
            route.NextEligibilityCheckUT = 480.0;
            route.RecordHold(RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, "Ore", 0.0, 490.0);
            route.PendingRecoveryCreditCycleId = "cycle-2";
            route.PendingRecoveryCreditDispatchUT = 300.0;

            RouteRewindClassifier.ResetCycleStateForRewind(route, 500.0);

            Assert.Equal(RouteStatus.InTransit, route.Status);
            Assert.Equal(400.0, route.CurrentCycleStartUT);
            Assert.Equal(450.0, route.PendingDeliveryUT);
            Assert.Equal(480.0, route.NextEligibilityCheckUT);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, route.LastHoldKind);
            Assert.Equal("cycle-2", route.PendingRecoveryCreditCycleId);
        }

        // ------------------------------------------------------------------
        // Occupancy + due predicates
        // ------------------------------------------------------------------

        [Fact]
        public void IsTreeOccupied_IntersectionSemantics_EmptyIdsNeverMatch()
        {
            var dormant = MakeRoute("d", 900.0, "tree-1");
            var occupier = MakeRoute("o", 100.0, "tree-1");
            var other = MakeRoute("x", 100.0, "tree-2");
            var treeless1 = MakeRoute("t1", 900.0);
            var treeless2 = MakeRoute("t2", 100.0);

            Assert.True(RouteRewindClassifier.IsTreeOccupied(dormant, new[] { other, occupier }));
            Assert.False(RouteRewindClassifier.IsTreeOccupied(dormant, new[] { other }));
            // Two tree-less routes must not "share" a vacuous tree.
            Assert.False(RouteRewindClassifier.IsTreeOccupied(treeless1, new[] { treeless2 }));
        }

        [Fact]
        public void IsDormantRouteDue_InclusiveAtCreatedUT()
        {
            var route = MakeRoute("d", 900.0);
            Assert.False(RouteRewindClassifier.IsDormantRouteDue(route, 899.999));
            Assert.True(RouteRewindClassifier.IsDormantRouteDue(route, 900.0));
            Assert.True(RouteRewindClassifier.IsDormantRouteDue(route, 900.001));
            // Unknown stamp: defensively due (can never strand invisible).
            Assert.True(RouteRewindClassifier.IsDormantRouteDue(MakeRoute("u", -1.0), 0.0));
        }

        // ------------------------------------------------------------------
        // Reset-to-fresh
        // ------------------------------------------------------------------

        [Fact]
        public void ResetToFresh_PreservesDefinitionResetsRuntime()
        {
            var route = MakeRoute("m", 900.0, "tree-9");
            route.Name = "My Fuel Run";
            route.CadenceMultiplier = 3;
            route.DispatchPriority = 2;
            route.Status = RouteStatus.Active;
            route.CompletedCycles = 5;
            route.SkippedCycles = 2;
            route.LastObservedLoopCycleIndex = 11;
            route.LoopAnchorUT = 1234.0;
            route.SendOnceArmed = true;
            route.PauseAfterCurrentCycle = true;
            route.LastConsumedPartnerCycle = 4;
            route.PendingRecoveryCreditCycleId = "cycle-4";
            route.PendingRecoveryCreditDispatchUT = 1000.0;
            route.NextDispatchUT = 99999.0; // abandoned-future legacy schedule

            RouteRewindClassifier.ResetToFreshForMaterialize(route);

            // Definition preserved.
            Assert.Equal("m", route.Id);
            Assert.Equal("My Fuel Run", route.Name);
            Assert.Equal(900.0, route.CreatedUT);
            Assert.Equal("tree-9", route.BackingMissionTreeId);
            Assert.Equal(3, route.CadenceMultiplier);
            Assert.Equal(2, route.DispatchPriority);
            // Runtime reset.
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles);
            Assert.Equal(-1, route.LastObservedLoopCycleIndex);
            Assert.Equal(-1.0, route.LoopAnchorUT);
            Assert.False(route.SendOnceArmed);
            Assert.False(route.PauseAfterCurrentCycle);
            Assert.Equal(0, route.LastConsumedPartnerCycle);
            Assert.Null(route.PendingRecoveryCreditCycleId);
            // Re-anchored to the creation point; activation pulls it up to now.
            Assert.Equal(900.0, route.NextDispatchUT);
        }

        // ------------------------------------------------------------------
        // Store: materialization
        // ------------------------------------------------------------------

        [Fact]
        public void Materialize_DueRoute_MaterializesPaused_NotDueStaysDormant()
        {
            RouteStore.InstallRoutesAtRewind(
                new List<Route>(),
                new List<Route> { MakeRoute("due", 900.0), MakeRoute("later", 2000.0) });

            int n = RouteStore.MaterializeDueDormantRoutes(950.0);

            Assert.Equal(1, n);
            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Equal("due", RouteStore.CommittedRoutes[0].Id);
            Assert.Equal(RouteStatus.Paused, RouteStore.CommittedRoutes[0].Status);
            Assert.Single(RouteStore.DormantRoutes);
            Assert.Equal("later", RouteStore.DormantRoutes[0].Id);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("Materialize: 1 dormant route(s) materialized"));
        }

        [Fact]
        public void Materialize_OccupiedTree_DropsDormantTwin()
        {
            var live = MakeRoute("live", 940.0, "tree-1");
            RouteStore.InstallRoutesAtRewind(
                new List<Route> { live },
                new List<Route> { MakeRoute("twin", 900.0, "tree-1") });

            int n = RouteStore.MaterializeDueDormantRoutes(950.0);

            Assert.Equal(0, n);
            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Empty(RouteStore.DormantRoutes);
            Assert.Contains(logLines, l => l.Contains("DROPPED") && l.Contains("occupies its source tree"));
        }

        [Fact]
        public void Materialize_CoMaterializingPartners_RelinkViaLinkRoutes()
        {
            var a = MakeRoute("pair-a", 900.0);
            var b = MakeRoute("pair-b", 910.0);
            a.LinkedRouteId = "pair-b"; // former-partner hints survive dormanting
            b.LinkedRouteId = "pair-a";
            RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route> { a, b });

            RouteStore.MaterializeDueDormantRoutes(950.0);

            Assert.Equal(2, RouteStore.CommittedRoutes.Count);
            Assert.Equal("pair-b", a.LinkedRouteId);
            Assert.Equal("pair-a", b.LinkedRouteId);
            Assert.Equal(0, a.LastConsumedPartnerCycle);
            Assert.Equal(0, b.LastConsumedPartnerCycle);
        }

        [Fact]
        public void Materialize_PartnerGone_HintSevered()
        {
            var a = MakeRoute("solo", 900.0);
            a.LinkedRouteId = "vanished-partner";
            RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route> { a });

            RouteStore.MaterializeDueDormantRoutes(950.0);

            Assert.Null(a.LinkedRouteId);
        }

        [Fact]
        public void Materialize_MissingSources_SurfaceAsMissingSourceRecording()
        {
            // A route WITH SourceRefs whose recordings are absent from ERS:
            // RevalidateSources must flip it visible-broken, never silently healthy.
            var route = MakeRoute("broken", 900.0, "tree-gone");
            RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route> { route });

            RouteStore.MaterializeDueDormantRoutes(950.0);

            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Equal(RouteStatus.MissingSourceRecording, route.Status);
        }

        [Fact]
        public void Materialize_EmptyDormantList_IsSilentNoOp()
        {
            logLines.Clear();
            Assert.Equal(0, RouteStore.MaterializeDueDormantRoutes(1000.0));
            Assert.DoesNotContain(logLines, l => l.Contains("Materialize"));
        }

        /// <summary>Minimal env stub: a dormant-only tick never evaluates any
        /// committed route, so no member should ever be called.</summary>
        private sealed class ThrowingEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer => false;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
                => throw new InvalidOperationException("env must not be consulted");
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
                => throw new InvalidOperationException("env must not be consulted");
            public bool OriginHasCargo(Route route, out string lackingResource)
                => throw new InvalidOperationException("env must not be consulted");
            public bool KscFundsAvailable(Route route, out double shortfall)
                => throw new InvalidOperationException("env must not be consulted");
            public bool DestinationHasCapacity(Route route, out string fullResource)
                => throw new InvalidOperationException("env must not be consulted");
            public bool RouteHasValidSourcesInErs(Route route)
                => throw new InvalidOperationException("env must not be consulted");
        }

        // Review finding 1 (PR #1329): the materialize call must sit BEFORE
        // Tick's committed-count early return, or a save whose only routes are
        // dormant never materializes. Driven through the real Tick entry.
        [Fact]
        public void Tick_DormantOnlyStore_StillMaterializes()
        {
            RouteStore.InstallRoutesAtRewind(
                new List<Route>(),
                new List<Route> { MakeRoute("sleeper", 900.0, "tree-s") });

            RouteOrchestrator.Tick(950.0, new ThrowingEnv());

            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Empty(RouteStore.DormantRoutes);
            // The create-path guard mirror ran (manual-loop clear walked the
            // route's source trees; headless it logs its outcome either way).
            Assert.Contains(logLines, l => l.Contains("ForceClearManualLoopForRoute"));
        }

        // ------------------------------------------------------------------
        // Store: codec round-trip
        // ------------------------------------------------------------------

        [Fact]
        public void SaveLoad_DormantRoutesRoundTrip_SparseWhenEmpty()
        {
            RouteStore.InstallRoutesAtRewind(
                new List<Route> { MakeCodecValidRoute("live", 100.0) },
                new List<Route> { MakeCodecValidRoute("sleeper", 900.0) });

            var parent = new ConfigNode("PARSEK");
            RouteStore.SaveRoutesTo(parent);
            Assert.NotNull(parent.GetNode("DORMANT_ROUTES"));

            RouteStore.ResetForTesting();
            RouteStore.LoadRoutesFrom(parent);
            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Single(RouteStore.DormantRoutes);
            Assert.Equal("sleeper", RouteStore.DormantRoutes[0].Id);
            Assert.Equal(900.0, RouteStore.DormantRoutes[0].CreatedUT);

            // Empty dormant list writes NO node and strips a stale one.
            RouteStore.InstallRoutesAtRewind(new List<Route> { MakeCodecValidRoute("live", 100.0) }, new List<Route>());
            RouteStore.SaveRoutesTo(parent);
            Assert.Null(parent.GetNode("DORMANT_ROUTES"));
        }

        [Fact]
        public void SaveLoad_AllDormantSave_LoadsWithoutRoutesNode()
        {
            RouteStore.InstallRoutesAtRewind(
                new List<Route>(),
                new List<Route> { MakeCodecValidRoute("only-sleeper", 900.0) });

            var parent = new ConfigNode("PARSEK");
            RouteStore.SaveRoutesTo(parent);
            Assert.Null(parent.GetNode("ROUTES"));

            RouteStore.ResetForTesting();
            RouteStore.LoadRoutesFrom(parent);
            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Single(RouteStore.DormantRoutes);
        }

        // Review finding 2 (PR #1329): the LoadRoutesFrom preamble must clear
        // the in-memory dormant list - the forced-cold crash-reconcile path
        // reloads a save without resetting RouteStore first, and a surviving
        // in-memory dormant list would resurrect phantom sleepers.
        [Fact]
        public void Load_SaveWithoutDormantNode_ClearsInMemoryDormantList()
        {
            RouteStore.InstallRoutesAtRewind(
                new List<Route>(),
                new List<Route> { MakeRoute("phantom", 900.0) });

            var parent = new ConfigNode("PARSEK");
            RouteStore.LoadRoutesFrom(parent); // no ROUTES, no DORMANT_ROUTES

            Assert.Empty(RouteStore.DormantRoutes);
        }

        [Fact]
        public void Load_DormantCollidingWithCommitted_DroppedCommittedWins()
        {
            var parent = new ConfigNode("PARSEK");
            var routesNode = parent.AddNode("ROUTES");
            var live = MakeCodecValidRoute("dup", 100.0);
            live.SerializeInto(routesNode.AddNode("ROUTE"));
            var dormantNode = parent.AddNode("DORMANT_ROUTES");
            var sleeper = MakeCodecValidRoute("dup", 900.0);
            sleeper.SerializeInto(dormantNode.AddNode("ROUTE"));

            RouteStore.LoadRoutesFrom(parent);

            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Empty(RouteStore.DormantRoutes);
            Assert.Contains(logLines, l => l.Contains("collides with a") && l.Contains("committed"));
        }

        // ------------------------------------------------------------------
        // Bundle rewind seam
        // ------------------------------------------------------------------

        [Fact]
        public void BundleRestore_WithCutoff_DormantsPostCutoffAndReconcilesKept()
        {
            var pre = MakeRoute("pre", 100.0);
            pre.LastObservedLoopCycleIndex = 9;             // abandoned-future cursor
            pre.LinkedRouteId = "post";                     // linked to a future route
            var post = MakeRoute("post", 900.0);
            post.LinkedRouteId = "pre";
            RouteStore.AddRoute(pre);
            RouteStore.AddRoute(post);
            RouteStore.InstallRoutesAtRewind(
                new List<Route> { pre, post },
                new List<Route> { MakeRoute("older-sleeper", 950.0) });

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 500.0);

            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Equal("pre", RouteStore.CommittedRoutes[0].Id);
            Assert.Equal(-1, pre.LastObservedLoopCycleIndex);   // reconciled
            Assert.Null(pre.LinkedRouteId);                     // partner severed two-sidedly
            Assert.Equal(0, pre.LastConsumedPartnerCycle);
            Assert.Equal(2, RouteStore.DormantRoutes.Count);
            Assert.Contains(RouteStore.DormantRoutes, r => r.Id == "post");
            Assert.Contains(RouteStore.DormantRoutes, r => r.Id == "older-sleeper");
            // Dormant entry keeps its former-partner hint for the re-link.
            Assert.Equal("pre", RouteStore.DormantRoutes.First(r => r.Id == "post").LinkedRouteId);
        }

        [Fact]
        public void BundleRestore_RouteBlindOverload_LeavesRouteListsUntouched()
        {
            var pre = MakeRoute("pre", 100.0);
            var post = MakeRoute("post", 900.0);
            post.LastObservedLoopCycleIndex = 5;
            RouteStore.InstallRoutesAtRewind(new List<Route> { pre, post }, new List<Route>());

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle); // +inf: rollback contract

            Assert.Equal(2, RouteStore.CommittedRoutes.Count);
            Assert.Empty(RouteStore.DormantRoutes);
            Assert.Equal(5, post.LastObservedLoopCycleIndex); // no reconcile
        }

        [Fact]
        public void BundleRestore_ThenMaterialize_EndToEnd()
        {
            // The plan's integration scenario: A pre-cutoff, B post-cutoff,
            // C an earlier-rewind sleeper whose tree the player re-occupied.
            var a = MakeRoute("A", 100.0);
            var b = MakeRoute("B", 900.0);
            var c = MakeRoute("C", 950.0, "tree-c");
            RouteStore.InstallRoutesAtRewind(new List<Route> { a, b }, new List<Route> { c });

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 500.0);
            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Equal(2, RouteStore.DormantRoutes.Count);

            // Player re-creates a route over C's tree during the re-fly.
            RouteStore.AddRoute(MakeRoute("C2", 940.0, "tree-c"));

            int n = RouteStore.MaterializeDueDormantRoutes(960.0);

            Assert.Equal(1, n); // B materialized, C dropped occupied
            Assert.Contains(RouteStore.CommittedRoutes, r => r.Id == "B" && r.Status == RouteStatus.Paused);
            Assert.DoesNotContain(RouteStore.CommittedRoutes, r => r.Id == "C");
            Assert.Empty(RouteStore.DormantRoutes);
        }
    }
}
