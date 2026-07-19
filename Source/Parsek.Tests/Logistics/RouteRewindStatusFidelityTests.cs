using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Kept-route status fidelity at rewind (follow-up to the dormant-routes
    /// extension, plan <c>docs/dev/plans/plan-route-rewind-dormant-visibility.md</c>):
    /// timeline pause-state derivation from kept RoutePaused/RouteResumed rows
    /// (<see cref="RouteRewindClassifier.DeriveTimelineStatus"/> +
    /// <see cref="RouteRewindClassifier.ApplyDerivedTimelineStatus"/>),
    /// unconditional armed one-shot flag clearing
    /// (<see cref="RouteRewindClassifier.ClearArmedOneShotFlags"/>), and
    /// best-effort cycle-counter reconstruction
    /// (<see cref="RouteRewindClassifier.ReconstructCycleCounters"/>).
    /// [Collection("Sequential")] + full static reset per the shared-static
    /// rule (mirrors <see cref="RouteRewindDormantTests"/>).
    /// </summary>
    [Collection("Sequential")]
    public class RouteRewindStatusFidelityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteRewindStatusFidelityTests()
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

        private static Route MakeRoute(string id, RouteStatus status = RouteStatus.Active)
        {
            return new Route
            {
                Id = id,
                Name = "route-" + id,
                Status = status,
                CreatedUT = 100.0,
                Stops = new List<RouteStop>(),
            };
        }

        private static GameAction Marker(
            string routeId, GameActionType type, double ut, int sequence = 0)
        {
            return new GameAction
            {
                Type = type,
                UT = ut,
                RouteId = routeId,
                RouteStopIndex = -1,
                Sequence = sequence,
                RouteEndpointReason = "test",
            };
        }

        private static GameAction Dispatched(string routeId, string cycleId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 0,
            };
        }

        private static GameAction Delivered(
            string routeId, string cycleId, double ut, int stopIndex = 0)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = stopIndex,
                Sequence = stopIndex * 8 + 3,
            };
        }

        // ------------------------------------------------------------------
        // DeriveTimelineStatus
        // ------------------------------------------------------------------

        [Fact]
        public void Derive_NoRowsOrForeignRows_NoMarker()
        {
            Assert.Equal(RouteTimelineStatus.NoMarker,
                RouteRewindClassifier.DeriveTimelineStatus("r", null));
            Assert.Equal(RouteTimelineStatus.NoMarker,
                RouteRewindClassifier.DeriveTimelineStatus("r", new List<GameAction>()));
            // Another route's markers must not bleed over.
            Assert.Equal(RouteTimelineStatus.NoMarker,
                RouteRewindClassifier.DeriveTimelineStatus("r", new List<GameAction>
                {
                    Marker("other", GameActionType.RoutePaused, 100.0),
                    null, // null entries tolerated
                }));
        }

        [Fact]
        public void Derive_LatestMarkerByUTWins()
        {
            var pausedThenResumed = new List<GameAction>
            {
                Marker("r", GameActionType.RoutePaused, 100.0),
                Marker("r", GameActionType.RouteResumed, 200.0),
            };
            Assert.Equal(RouteTimelineStatus.Active,
                RouteRewindClassifier.DeriveTimelineStatus("r", pausedThenResumed));

            var resumedThenPaused = new List<GameAction>
            {
                Marker("r", GameActionType.RouteResumed, 100.0),
                Marker("r", GameActionType.RoutePaused, 200.0),
            };
            Assert.Equal(RouteTimelineStatus.Paused,
                RouteRewindClassifier.DeriveTimelineStatus("r", resumedThenPaused));
        }

        [Fact]
        public void Derive_SameUT_HigherSequenceWins()
        {
            // The armed-pause tail stamps its RoutePaused at the delivery UT with
            // a stride-offset Sequence; a RouteResumed cannot share that exact
            // (UT, Sequence). Higher Sequence must win regardless of list order.
            var rows = new List<GameAction>
            {
                Marker("r", GameActionType.RoutePaused, 200.0, sequence: 4),
                Marker("r", GameActionType.RouteResumed, 200.0, sequence: 0),
            };
            Assert.Equal(RouteTimelineStatus.Paused,
                RouteRewindClassifier.DeriveTimelineStatus("r", rows));

            rows.Reverse();
            Assert.Equal(RouteTimelineStatus.Paused,
                RouteRewindClassifier.DeriveTimelineStatus("r", rows));
        }

        [Fact]
        public void Derive_IgnoresNonMarkerRouteRows()
        {
            // A later dispatch row is NOT a resume (the durable-resume lesson):
            // only RoutePaused/RouteResumed rows participate.
            var rows = new List<GameAction>
            {
                Marker("r", GameActionType.RoutePaused, 100.0),
                Dispatched("r", "cycle-0", 300.0),
                Delivered("r", "cycle-0", 350.0),
            };
            Assert.Equal(RouteTimelineStatus.Paused,
                RouteRewindClassifier.DeriveTimelineStatus("r", rows));
        }

        // A resume must resume something recorded: a kept RouteResumed with NO
        // kept RoutePaused row proves the route was paused at a marker-less
        // point (pre-feature, or a skipped bogus-UT emission), so un-pausing
        // on its evidence alone could undo an unrecorded pause. Downgrade to
        // NoMarker; a resume with any kept pause row still derives Active.
        [Fact]
        public void Derive_ResumeWithoutAnyKeptPausedRow_DowngradesToNoMarker()
        {
            var resumeOnly = new List<GameAction>
            {
                Marker("r", GameActionType.RouteResumed, 200.0),
            };
            Assert.Equal(RouteTimelineStatus.NoMarker,
                RouteRewindClassifier.DeriveTimelineStatus("r", resumeOnly));

            // Another route's pause row does not satisfy the gate.
            var foreignPause = new List<GameAction>
            {
                Marker("other", GameActionType.RoutePaused, 100.0),
                Marker("r", GameActionType.RouteResumed, 200.0),
            };
            Assert.Equal(RouteTimelineStatus.NoMarker,
                RouteRewindClassifier.DeriveTimelineStatus("r", foreignPause));

            // With a kept pause row (even an older one) the resume stands.
            var pausedThenResumed = new List<GameAction>
            {
                Marker("r", GameActionType.RoutePaused, 100.0),
                Marker("r", GameActionType.RouteResumed, 200.0),
            };
            Assert.Equal(RouteTimelineStatus.Active,
                RouteRewindClassifier.DeriveTimelineStatus("r", pausedThenResumed));
        }

        // ------------------------------------------------------------------
        // ApplyDerivedTimelineStatus
        // ------------------------------------------------------------------

        [Fact]
        public void ApplyDerived_Paused_FlipsGhostDrivingAndWaitStatuses()
        {
            // RouteStatus is internal, so the matrix runs inside one Fact
            // instead of an InlineData theory.
            foreach (RouteStatus status in new[]
            {
                RouteStatus.Active,
                RouteStatus.WaitingForResources,
                RouteStatus.WaitingForFunds,
                RouteStatus.DestinationFull,
            })
            {
                var route = MakeRoute("r-" + status, status);

                bool changed = RouteRewindClassifier.ApplyDerivedTimelineStatus(
                    route, RouteTimelineStatus.Paused);

                Assert.True(changed);
                Assert.Equal(RouteStatus.Paused, route.Status);
                Assert.Contains(logLines, l =>
                    l.Contains("[Route]") && l.Contains("rewind-status-derivation")
                    && l.Contains($"{status}→Paused"));
            }
        }

        [Fact]
        public void ApplyDerived_Paused_NeverTouchesInTransit()
        {
            var route = MakeRoute("r-intransit", RouteStatus.InTransit);

            bool changed = RouteRewindClassifier.ApplyDerivedTimelineStatus(
                route, RouteTimelineStatus.Paused);

            Assert.False(changed);
            Assert.Equal(RouteStatus.InTransit, route.Status);
        }

        // A-2 (PR #1330 review): a validity status keeps its LIVE status but
        // the derived verdict lands on PreMissingStatus, so a later recovery
        // (RevalidateSources restores the pre-missing baseline) lands on the
        // timeline-correct state instead of the pre-rewind captured one.
        [Fact]
        public void ApplyDerived_ValidityStatuses_WriteDerivedVerdictToPreMissingBaseline()
        {
            foreach (RouteStatus status in new[]
            {
                RouteStatus.MissingSourceRecording,
                RouteStatus.SourceChanged,
                RouteStatus.EndpointLost,
            })
            {
                // Derived Paused: live status kept, baseline flips to Paused.
                var route = MakeRoute("r-" + status, status);
                Assert.Equal(RouteStatus.Active, route.PreMissingStatus); // sentinel default

                bool changed = RouteRewindClassifier.ApplyDerivedTimelineStatus(
                    route, RouteTimelineStatus.Paused);

                Assert.True(changed);
                Assert.Equal(status, route.Status);
                Assert.Equal(RouteStatus.Paused, route.PreMissingStatus);
                Assert.Contains(logLines, l =>
                    l.Contains("[Route]") && l.Contains("preMissingStatus")
                    && l.Contains("rewind-status-derivation"));

                // Derived Active: baseline flips back; a matching baseline is
                // a no-change.
                Assert.True(RouteRewindClassifier.ApplyDerivedTimelineStatus(
                    route, RouteTimelineStatus.Active));
                Assert.Equal(RouteStatus.Active, route.PreMissingStatus);
                Assert.Equal(status, route.Status);
                Assert.False(RouteRewindClassifier.ApplyDerivedTimelineStatus(
                    route, RouteTimelineStatus.Active));
            }
        }

        [Fact]
        public void ApplyDerived_Active_OnlyUnpausesAPausedRoute()
        {
            var paused = MakeRoute("p", RouteStatus.Paused);
            Assert.True(RouteRewindClassifier.ApplyDerivedTimelineStatus(
                paused, RouteTimelineStatus.Active));
            Assert.Equal(RouteStatus.Active, paused.Status);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("rewind-status-derivation")
                && l.Contains("Paused→Active"));

            // Every non-Paused status is left alone by a derived Active.
            var endpointLost = MakeRoute("e", RouteStatus.EndpointLost);
            Assert.False(RouteRewindClassifier.ApplyDerivedTimelineStatus(
                endpointLost, RouteTimelineStatus.Active));
            Assert.Equal(RouteStatus.EndpointLost, endpointLost.Status);

            var active = MakeRoute("a", RouteStatus.Active);
            Assert.False(RouteRewindClassifier.ApplyDerivedTimelineStatus(
                active, RouteTimelineStatus.Active));
            Assert.Equal(RouteStatus.Active, active.Status);
        }

        [Fact]
        public void ApplyDerived_NoMarker_LeavesStatusAlone()
        {
            var route = MakeRoute("r", RouteStatus.Paused);
            Assert.False(RouteRewindClassifier.ApplyDerivedTimelineStatus(
                route, RouteTimelineStatus.NoMarker));
            Assert.Equal(RouteStatus.Paused, route.Status);
        }

        // ------------------------------------------------------------------
        // ClearArmedOneShotFlags
        // ------------------------------------------------------------------

        [Fact]
        public void ClearArmedOneShotFlags_ClearsBothAndReportsChange()
        {
            var armed = MakeRoute("armed");
            armed.PauseAfterCurrentCycle = true;
            armed.SendOnceArmed = true;

            Assert.True(RouteRewindClassifier.ClearArmedOneShotFlags(armed));
            Assert.False(armed.PauseAfterCurrentCycle);
            Assert.False(armed.SendOnceArmed);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("armed one-shot")
                && l.Contains("do not survive time travel"));

            // Idempotent: nothing armed, no change reported.
            Assert.False(RouteRewindClassifier.ClearArmedOneShotFlags(armed));

            var pauseOnly = MakeRoute("pause-only");
            pauseOnly.PauseAfterCurrentCycle = true;
            Assert.True(RouteRewindClassifier.ClearArmedOneShotFlags(pauseOnly));
            Assert.False(pauseOnly.PauseAfterCurrentCycle);
        }

        // ------------------------------------------------------------------
        // TryParseCycleOrdinal
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("cycle-0", true, 0)]
        [InlineData("cycle-17", true, 17)]
        [InlineData("a-b-12", true, 12)] // suffix after the LAST dash
        [InlineData("cycle-", false, -1)]
        [InlineData("cycle-x", false, -1)]
        [InlineData("nodash", false, -1)]
        [InlineData("", false, -1)]
        [InlineData(null, false, -1)]
        public void TryParseCycleOrdinal_ParsesNumericSuffixAfterLastDash(
            string cycleId, bool expectOk, int expectOrdinal)
        {
            bool ok = RouteRewindClassifier.TryParseCycleOrdinal(cycleId, out int ordinal);
            Assert.Equal(expectOk, ok);
            if (expectOk)
                Assert.Equal(expectOrdinal, ordinal);
        }

        // ------------------------------------------------------------------
        // ReconstructCycleCounters
        // ------------------------------------------------------------------

        [Fact]
        public void Reconstruct_NoKeptDispatchRows_ResetsBothToZero()
        {
            var route = MakeRoute("r");
            route.CompletedCycles = 7;
            route.SkippedCycles = 3;
            // Only foreign / non-dispatch rows: counts as "no kept dispatch rows".
            var rows = new List<GameAction>
            {
                Dispatched("other", "cycle-5", 100.0),
                Marker("r", GameActionType.RoutePaused, 100.0),
            };

            Assert.True(RouteRewindClassifier.ReconstructCycleCounters(route, rows));
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("counters reconstructed")
                && l.Contains("completed 7->0") && l.Contains("skipped 3->0"));
        }

        [Fact]
        public void Reconstruct_DispatchedAndDelivered_CompletedPlusSkippedCoversMaxOrdinal()
        {
            var route = MakeRoute("r");
            route.CompletedCycles = 9;
            route.SkippedCycles = 9;
            // cycles 0..2 dispatched; 0 and 1 delivered; 2 dispatched-not-delivered
            // at the cutoff counts as skipped by the reconstruction.
            var rows = new List<GameAction>
            {
                Dispatched("r", "cycle-0", 100.0),
                Delivered("r", "cycle-0", 110.0),
                Dispatched("r", "cycle-1", 200.0),
                Delivered("r", "cycle-1", 210.0),
                Dispatched("r", "cycle-2", 300.0),
            };

            Assert.True(RouteRewindClassifier.ReconstructCycleCounters(route, rows));
            Assert.Equal(2, route.CompletedCycles);
            Assert.Equal(1, route.SkippedCycles);
            // Uniqueness invariant: the next live cycle id must exceed every kept one.
            Assert.True(route.CompletedCycles + route.SkippedCycles > 2);
        }

        [Fact]
        public void Reconstruct_MultiWindowDeliveredRows_CountDistinctCycleIds()
        {
            var route = MakeRoute("r");
            route.CompletedCycles = 4;
            // One cycle delivered across two stop windows: two delivered rows,
            // ONE distinct cycle id.
            var rows = new List<GameAction>
            {
                Dispatched("r", "cycle-0", 100.0),
                Delivered("r", "cycle-0", 110.0, stopIndex: 0),
                Delivered("r", "cycle-0", 120.0, stopIndex: 1),
            };

            Assert.True(RouteRewindClassifier.ReconstructCycleCounters(route, rows));
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles);
        }

        [Fact]
        public void Reconstruct_UnparseableDispatchIds_IgnoredForMaxOrdinal()
        {
            var route = MakeRoute("r");
            route.SkippedCycles = 5;
            var rows = new List<GameAction>
            {
                Dispatched("r", "weird-id-x", 100.0), // unparseable: no ordinal
                Delivered("r", "weird-id-x", 110.0),
            };

            Assert.True(RouteRewindClassifier.ReconstructCycleCounters(route, rows));
            // Dispatch rows exist, so counters derive: 1 delivered cycle id,
            // maxOrdinal -1 -> skipped clamps to 0.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles);
        }

        [Fact]
        public void Reconstruct_DeliveredExceedingDispatchOrdinals_SkippedClampsAtZero()
        {
            var route = MakeRoute("r");
            var rows = new List<GameAction>
            {
                Dispatched("r", "cycle-0", 100.0),
                Delivered("r", "cycle-0", 110.0),
                Delivered("r", "cycle-1", 120.0), // delivered row without a kept dispatch
                Delivered("r", "cycle-2", 130.0),
            };

            RouteRewindClassifier.ReconstructCycleCounters(route, rows);
            Assert.Equal(3, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles); // (0+1)-3 clamps to 0
            Assert.True(route.CompletedCycles + route.SkippedCycles > 0);
        }

        [Fact]
        public void Reconstruct_AlreadyCorrect_ReportsNoChange()
        {
            var route = MakeRoute("r");
            route.CompletedCycles = 1;
            route.SkippedCycles = 0;
            var rows = new List<GameAction>
            {
                Dispatched("r", "cycle-0", 100.0),
                Delivered("r", "cycle-0", 110.0),
            };

            Assert.False(RouteRewindClassifier.ReconstructCycleCounters(route, rows));
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles);
        }

        // A-1 (PR #1330 review, Major): a KEPT in-flight cycle (pre-cutoff
        // CurrentCycleStartUT survives ResetCycleStateForRewind) keeps its
        // identity. The delivery-time recompute is cycle-{Completed+Skipped},
        // so the sum must land ON the in-flight dispatch's ordinal - counting
        // it as skipped would re-fire its already-delivered window under a
        // NEW id, miss the kept row in the per-window dedup, and
        // double-deliver.
        [Fact]
        public void Reconstruct_KeptInFlightCycle_SumLandsOnInFlightOrdinal()
        {
            var route = MakeRoute("r");
            route.CompletedCycles = 9;
            route.SkippedCycles = 9;
            route.CurrentCycleStartUT = 80.0; // kept in-flight cycle (<= cutoff)
            var rows = new List<GameAction>
            {
                Dispatched("r", "cycle-4", 10.0),
                Delivered("r", "cycle-4", 20.0),
                Dispatched("r", "cycle-5", 80.0),      // the in-flight cycle
                Delivered("r", "cycle-5", 90.0, 0),    // window 0 already delivered
            };

            Assert.True(RouteRewindClassifier.ReconstructCycleCounters(route, rows));

            // cycle-5 is EXCLUDED from completed (incomplete straddling cycle);
            // the sum lands on its ordinal so the recompute reproduces its id.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(4, route.SkippedCycles);
            Assert.Equal("cycle-5",
                "cycle-" + (route.CompletedCycles + route.SkippedCycles));
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("counters reconstructed")
                && l.Contains("keptInFlightCycle=1"));
        }

        [Fact]
        public void Reconstruct_KeptInFlightCycle_NoDeliveredWindowYet()
        {
            var route = MakeRoute("r");
            route.CurrentCycleStartUT = 80.0;
            var rows = new List<GameAction>
            {
                Dispatched("r", "cycle-0", 10.0),
                Delivered("r", "cycle-0", 20.0),
                Dispatched("r", "cycle-1", 80.0), // in-flight, nothing delivered yet
            };

            RouteRewindClassifier.ReconstructCycleCounters(route, rows);

            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.SkippedCycles);
            Assert.Equal("cycle-1",
                "cycle-" + (route.CompletedCycles + route.SkippedCycles));
        }

        /// <summary>Permissive env for the straddle Tick: endpoint decisions
        /// succeed headlessly; vessel resolution is never reached because the
        /// replay guard short-circuits first (mirrors EndpointLostFakeEnv in
        /// RouteOrchestratorDeliveryTests).</summary>
        private sealed class ReplayFakeEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer => false;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                reason = string.Empty;
                return true;
            }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                vessel = null;
                reason = "fake-null-vessel";
                return false;
            }
            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                lackingResource = string.Empty;
                return true;
            }
            public bool KscFundsAvailable(Route route, out double shortfall)
            {
                shortfall = 0.0;
                return true;
            }
            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                fullResource = string.Empty;
                return true;
            }
            public bool RouteHasValidSourcesInErs(Route route)
                => true;
        }

        // A-1 integration: the multi-stop partial straddle. A 2-stop route
        // dispatched cycle-5 pre-cutoff, window 0 delivered pre-cutoff (kept
        // row), later windows pending at the cutoff. After the rewind seam the
        // re-presented window 0 must recompute the SAME cycleId (cycle-5) and
        // dedup against the kept delivered row instead of re-delivering under
        // cycle-6 on a world that already contains the first delivery.
        [Fact]
        public void BundleRestore_MultiStopStraddle_KeptDeliveredRowDedupsWindowZero()
        {
            var route = new Route
            {
                Id = "straddle",
                Name = "route-straddle",
                Status = RouteStatus.InTransit,
                CreatedUT = 1.0,
                CompletedCycles = 9,               // inflated pre-rewind counters
                SkippedCycles = 9,
                CurrentCycleStartUT = 80.0,        // kept in-flight cycle
                PendingDeliveryUT = 90.0,          // window 0 boundary (kept)
                PendingStopIndex = 0,
                NextDispatchUT = 1_000_000.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 43u },
                        DeliveryManifest = new Dictionary<string, double> { { "Oxidizer", 5.0 } },
                    },
                },
            };
            RouteStore.InstallRoutesAtRewind(new List<Route> { route }, new List<Route>());

            Ledger.AddAction(Dispatched("straddle", "cycle-4", 10.0));
            Ledger.AddAction(Delivered("straddle", "cycle-4", 20.0));
            Ledger.AddAction(Dispatched("straddle", "cycle-5", 80.0));
            Ledger.AddAction(Delivered("straddle", "cycle-5", 90.0, stopIndex: 0));

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 100.0);

            // Kept in-flight cycle preserved; counters land ON its ordinal.
            Assert.Equal(RouteStatus.InTransit, route.Status);
            Assert.Equal(80.0, route.CurrentCycleStartUT);
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(4, route.SkippedCycles);

            // The seam pulled NextDispatchUT back to the cutoff; park it far
            // out so this Tick isolates the delivery-replay path.
            route.NextDispatchUT = 1_000_000.0;

            int actionsBefore = Ledger.Actions.Count;
            RouteOrchestrator.Tick(200.0, new ReplayFakeEnv());

            // Window 0 re-presented under the PRESERVED id and deduped by the
            // kept row: no new delivered/endpoint-lost/funds rows at all.
            Assert.Equal(actionsBefore, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("replay detected")
                && l.Contains("cycle=cycle-5"));
            Assert.DoesNotContain(Ledger.Actions,
                a => a.Type == GameActionType.RouteEndpointLost);
        }

        // ------------------------------------------------------------------
        // Rewind seam integration (ReconciliationBundle.Restore(cutoff))
        // ------------------------------------------------------------------

        [Fact]
        public void BundleRestore_WithCutoff_DerivesStatusClearsFlagsRebuildsCounters()
        {
            // Route "p": Active at capture with armed one-shot flags and inflated
            // counters; its latest KEPT marker is a RoutePaused at UT 400.
            var p = MakeRoute("p", RouteStatus.Active);
            p.PauseAfterCurrentCycle = true;
            p.SendOnceArmed = true;
            p.CompletedCycles = 9;
            p.SkippedCycles = 9;

            // Route "q": Paused at capture; its kept markers end in RouteResumed.
            var q = MakeRoute("q", RouteStatus.Paused);

            RouteStore.InstallRoutesAtRewind(new List<Route> { p, q }, new List<Route>());

            // Kept rows (UT <= 500) + abandoned-future rows (UT > 500, retired).
            Ledger.AddAction(Dispatched("p", "cycle-0", 200.0));
            Ledger.AddAction(Delivered("p", "cycle-0", 210.0));
            Ledger.AddAction(Dispatched("p", "cycle-1", 300.0));
            Ledger.AddAction(Marker("p", GameActionType.RoutePaused, 400.0));
            Ledger.AddAction(Marker("p", GameActionType.RouteResumed, 700.0)); // retired
            Ledger.AddAction(Marker("q", GameActionType.RoutePaused, 200.0));
            Ledger.AddAction(Marker("q", GameActionType.RouteResumed, 300.0));
            Ledger.AddAction(Marker("q", GameActionType.RoutePaused, 800.0));  // retired

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 500.0);

            // p: derived Paused (the post-cutoff resume was retired), flags gone,
            // counters rebuilt from the kept rows (1 delivered, maxOrdinal 1 -> 1 skipped).
            Assert.Equal(RouteStatus.Paused, p.Status);
            Assert.False(p.PauseAfterCurrentCycle);
            Assert.False(p.SendOnceArmed);
            Assert.Equal(1, p.CompletedCycles);
            Assert.Equal(1, p.SkippedCycles);

            // q: derived Active (kept resume at 300 outlives the kept pause at 200;
            // the post-cutoff pause was retired), no kept dispatches -> counters 0.
            Assert.Equal(RouteStatus.Active, q.Status);
            Assert.Equal(0, q.CompletedCycles);
            Assert.Equal(0, q.SkippedCycles);

            Assert.Contains(logLines, l =>
                l.Contains("[ReconciliationBundle]") && l.Contains("kept-route status fidelity")
                && l.Contains("derivedPaused=1") && l.Contains("derivedActive=1")
                && l.Contains("oneShotFlagsCleared=1") && l.Contains("countersReconstructed="));
        }

        [Fact]
        public void BundleRestore_NoMarkerRoute_KeepsStatus()
        {
            // A pre-timeline-events route (no kept markers) keeps whatever
            // status it carried; only the armed flags are dropped.
            var legacy = MakeRoute("legacy", RouteStatus.Paused);
            legacy.PauseAfterCurrentCycle = true;
            RouteStore.InstallRoutesAtRewind(new List<Route> { legacy }, new List<Route>());

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 500.0);

            Assert.Equal(RouteStatus.Paused, legacy.Status);
            Assert.False(legacy.PauseAfterCurrentCycle);
        }

        [Fact]
        public void BundleRestore_RouteBlindOverload_LeavesStatusAndFlagsAlone()
        {
            var route = MakeRoute("r", RouteStatus.Active);
            route.PauseAfterCurrentCycle = true;
            route.SendOnceArmed = true;
            route.CompletedCycles = 5;
            RouteStore.InstallRoutesAtRewind(new List<Route> { route }, new List<Route>());
            Ledger.AddAction(Marker("r", GameActionType.RoutePaused, 100.0));

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle); // +inf: rollback contract, route-blind

            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.True(route.PauseAfterCurrentCycle);
            Assert.True(route.SendOnceArmed);
            Assert.Equal(5, route.CompletedCycles);
        }
    }
}
