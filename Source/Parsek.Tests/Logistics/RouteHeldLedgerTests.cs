using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The RouteHeld route event (holds in the Route History): one event when a hold
    /// episode starts and one per reason not yet recorded in it, deduplicated against the
    /// route event log (never <c>Route.LastHold*</c>), retired at rewind with the other
    /// route events, and inert everywhere else. The writer is driven directly
    /// (<see cref="RouteOrchestrator.TryEmitRouteHeldRow"/>) and through the real loop
    /// crossing (<see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>).
    /// </summary>
    [Collection("Sequential")]
    public class RouteHeldLedgerTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const RouteDispatchEvaluator.EligibilityFailureKind OriginLacksCargo =
            RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo;
        private const RouteDispatchEvaluator.EligibilityFailureKind DestinationFull =
            RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull;

        private readonly List<string> logLines = new List<string>();

        public RouteHeldLedgerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Route PlainRoute(string id = "route-held")
            => new Route { Id = id, Name = "Fuel Run", Status = RouteStatus.Active };

        private static List<RouteEvent> Held(string routeId = null) => RouteEventLog.Events
            .Where(a => a.Type == RouteEventType.RouteHeld
                && (routeId == null || a.RouteId == routeId))
            .ToList();

        private static bool Hold(Route route, RouteDispatchEvaluator.EligibilityFailureKind kind,
            string detail, double shortfall, double ut)
        {
            route.RecordHold(kind, detail, shortfall, ut);
            return RouteOrchestrator.TryEmitRouteHeldRow(route, kind, detail, shortfall, ut, "cycle-0");
        }

        private static void Boundary(Route route, RouteEventType type, double ut)
        {
            RouteEventLog.Add(new RouteEvent
            {
                Type = type, UT = ut, RouteId = route.Id, RouteCycleId = "cycle-x",
                RouteStopIndex = -1,
            });
        }

        // ==================================================================
        // The writer: episode start, same reason, changed reason, amount only
        // ==================================================================

        // catches: a hold that leaves no row, a re-check of the same reason writing one
        // row per crossing (56,500 a year on the relay), and a shortfall change counted
        // as a new reason.
        [Fact]
        public void EpisodeStartWritesOne_SameReasonNone_ChangedReasonOne_AmountOnlyNone()
        {
            Route route = PlainRoute();

            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 108.8, 1000.0));
            Assert.Single(Held());

            Assert.False(Hold(route, OriginLacksCargo, "LiquidFuel", 108.8, 1300.0));
            Assert.Single(Held());

            Assert.False(Hold(route, OriginLacksCargo, "LiquidFuel", 40.0, 1600.0));
            Assert.Single(Held());

            Assert.True(Hold(route, DestinationFull, "LiquidFuel", 0.0, 1900.0));
            Assert.Equal(2, Held().Count);

            RouteEvent first = Held()[0];
            Assert.Equal(route.Id, first.RouteId);
            Assert.Equal(1000.0, first.UT);
            Assert.Equal("cycle-0", first.RouteCycleId);
            Assert.Equal(-1, first.RouteStopIndex);
            Assert.Equal(OriginLacksCargo, first.RouteHoldKind);
            Assert.Equal("LiquidFuel", first.RouteEndpointReason);
            Assert.Equal(108.8, first.RouteHoldShortfall);
            Assert.Equal(DestinationFull, Held()[1].RouteHoldKind);
        }

        // catches: a change of SUBJECT under the same kind (a different resource, a
        // different source vessel) being read as the same reason.
        [Fact]
        public void SameKindDifferentSubject_IsANewReason()
        {
            Route route = PlainRoute();
            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0));
            Assert.True(Hold(route, OriginLacksCargo, "Oxidizer", 10.0, 1300.0));
            Assert.Equal(2, Held().Count);
        }

        // The dedupe is per DISTINCT reason in the episode: a flip-flop between two
        // reasons with no run in between writes two rows, not one per crossing.
        [Fact]
        public void FlipFlopInsideOneEpisode_IsBoundedByDistinctReasons()
        {
            Route route = PlainRoute();
            for (int i = 0; i < 6; i++)
            {
                bool even = i % 2 == 0;
                Hold(route, even ? OriginLacksCargo : DestinationFull, "LiquidFuel", 5.0, 1000.0 + 300.0 * i);
            }
            Assert.Equal(2, Held().Count);
        }

        // catches: an episode that never closes (a route held, run, held again for the
        // same reason must show the second hold).
        [Theory]
        [InlineData(RouteEventType.RouteDispatched)]
        [InlineData(RouteEventType.RoutePaused)]
        [InlineData(RouteEventType.RouteResumed)]
        [InlineData(RouteEventType.RouteEndpointLost)]
        public void ABoundaryRowClosesTheEpisode(RouteEventType boundary)
        {
            Route route = PlainRoute();
            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0));
            Boundary(route, boundary, 1150.0);
            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1300.0));
            Assert.Equal(2, Held().Count);
        }

        // catches: events that merely sit beside a hold (a delivery, a debit, a pickup)
        // closing the episode and making every later crossing write an event again.
        [Theory]
        [InlineData(RouteEventType.RouteCargoDelivered)]
        [InlineData(RouteEventType.RouteCargoDebited)]
        [InlineData(RouteEventType.RouteCargoPickedUp)]
        public void NonBoundaryRouteRows_DoNotCloseTheEpisode(RouteEventType other)
        {
            Route route = PlainRoute();
            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0));
            Boundary(route, other, 1150.0);
            Assert.False(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1300.0));
            Assert.Single(Held());
        }

        // A dispatch at the SAME UT as an earlier hold (a multi-stop catch-up tick that
        // held cycle N then ran N+1) closes that hold's episode: ledger order breaks the
        // UT tie.
        [Fact]
        public void SameUtDispatchAfterAHold_ClosesItsEpisode()
        {
            Route route = PlainRoute();
            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0));
            Boundary(route, RouteEventType.RouteDispatched, 1000.0);
            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1300.0));
        }

        // Another route's rows never open, close or satisfy this route's episode.
        [Fact]
        public void EpisodesArePerRoute()
        {
            Route a = PlainRoute("route-a");
            Route b = PlainRoute("route-b");
            Assert.True(Hold(a, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0));
            Assert.True(Hold(b, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0));
            Boundary(b, RouteEventType.RouteDispatched, 1100.0);
            Assert.False(Hold(a, OriginLacksCargo, "LiquidFuel", 10.0, 1300.0));
            Assert.Single(Held("route-a"));
        }

        // catches: the dedupe reading Route.LastHold* (cleared by an eligible crossing,
        // a rewind, an activate) instead of the ledger.
        [Fact]
        public void TheCheckReadsTheLedger_NotTheRouteHoldFields()
        {
            Route route = PlainRoute();
            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0));
            route.ClearHold("test");
            Assert.False(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1300.0));
            Assert.Single(Held());
        }

        // catches: the partner hold keyed on the partner's NAME, so renaming the linked
        // route mid-hold wrote a second row for the same wait.
        [Fact]
        public void PartnerHold_IsKeyedOnThePartnerId_SoARenameWritesNoRow()
        {
            Route route = PlainRoute();
            route.LinkedRouteId = "route-partner";
            var wait = RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner;
            Assert.True(Hold(route, wait, "partner:Return Run", 0.0, 1000.0));
            Assert.False(Hold(route, wait, "partner:Return Run Renamed", 0.0, 1300.0));
            RouteEvent row = Assert.Single(Held());
            Assert.Equal("partner:route-partner", row.RouteEndpointReason);
            // Unlinked (no id to key on): the evaluator token passes through.
            Assert.Equal("partner:X", RouteOrchestrator.HeldRowDetail(PlainRoute(), wait, "partner:X"));
            Assert.Equal("LiquidFuel", RouteOrchestrator.HeldRowDetail(route, OriginLacksCargo, "LiquidFuel"));
        }

        // ==================================================================
        // Rewind
        // ==================================================================

        // catches: a hold row stamped after the rewind cutoff surviving it (it must
        // retire with the other route events).
        [Fact]
        public void RewindRetiresHeldRowsAfterTheCutoff_KeepsTheEarlierOnes()
        {
            Route route = PlainRoute();
            Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0);
            Hold(route, DestinationFull, "LiquidFuel", 0.0, 2000.0);
            Assert.Equal(2, Held().Count);

            RouteEventLog.RetireFutureRouteActionsAtRewind(1500.0, out _);

            RouteEvent kept = Assert.Single(Held());
            Assert.Equal(1000.0, kept.UT);
        }

        // catches THE duplicate the ledger-sourced check exists for: a rewind into a
        // long hold clears Route.LastHold* (its UT is past the cutoff) while the
        // episode's pre-cutoff row survives, so a field compare would write the same
        // reason again at the next crossing.
        [Fact]
        public void ReHoldAfterARewindIntoTheEpisode_WritesNoDuplicate()
        {
            Route route = PlainRoute();
            Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0);
            Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 2000.0); // deduped, LastHoldUT=2000

            RouteEventLog.RetireFutureRouteActionsAtRewind(1500.0, out _);
            RouteRewindClassifier.ResetCycleStateForRewind(route, 1500.0);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);

            Assert.False(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1600.0));
            Assert.Single(Held());
        }

        // A rewind to BEFORE the episode retires its row; the re-flown hold writes it
        // again exactly once.
        [Fact]
        public void ReHoldAfterARewindBeforeTheEpisode_WritesExactlyOne()
        {
            Route route = PlainRoute();
            Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 2000.0);
            RouteEventLog.RetireFutureRouteActionsAtRewind(1500.0, out _);
            RouteRewindClassifier.ResetCycleStateForRewind(route, 1500.0);
            Assert.Empty(Held());

            Assert.True(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1600.0));
            Assert.False(Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1900.0));
            Assert.Single(Held());
        }

        // ==================================================================
        // Guards and logging
        // ==================================================================

        [Fact]
        public void UnresolvedUt_NoKind_OrNoRoute_WriteNothing()
        {
            Route route = PlainRoute();
            Assert.False(RouteOrchestrator.TryEmitRouteHeldRow(route, OriginLacksCargo, "LiquidFuel", 1.0, 0.0, null));
            Assert.False(RouteOrchestrator.TryEmitRouteHeldRow(route,
                RouteDispatchEvaluator.EligibilityFailureKind.None, null, 0.0, 1000.0, null));
            Assert.False(RouteOrchestrator.TryEmitRouteHeldRow(null, OriginLacksCargo, "LiquidFuel", 1.0, 1000.0, null));
            Assert.Empty(RouteEventLog.Events);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("RouteHeld:")
                && l.Contains("live UT unresolved"));
        }

        // Info on a written row (route, kind, UT); Verbose rate-limited on a skip.
        [Fact]
        public void WrittenRowLogsInfo_SkippedRowLogsVerbose()
        {
            Route route = PlainRoute();
            Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0);
            Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1300.0);

            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[Route]")
                && l.Contains("RouteHeld: route") && l.Contains("kind=OriginLacksCargo")
                && l.Contains("ut=1000") && l.Contains("hold row written"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE]") && l.Contains("[Route]")
                && l.Contains("hold unchanged, skipped") && l.Contains("kind=OriginLacksCargo"));
        }

        // ==================================================================
        // Inert everywhere else
        // ==================================================================

        // catches: a hold event missing from the route event log (the Route History would
        // never see it) or leaking into the game-actions ledger.
        [Fact]
        public void HeldEventIsInTheRouteEventLog_NeverInTheLedger()
        {
            Route route = PlainRoute();
            Hold(route, OriginLacksCargo, "LiquidFuel", 10.0, 1000.0);
            Assert.Contains(RouteEventLog.Events, a => a.Type == RouteEventType.RouteHeld);
            Assert.Empty(Ledger.Actions);
            Assert.False(LedgerOrchestrator.IsResourceImpactingAction(GameActionType.RouteHeld));
        }

        // catches: the dispatch / pickup / delivery dedups or the rewind counter
        // reconstruction counting a Held row that carries a cycle id.
        [Fact]
        public void HeldRowWithACycleId_DoesNotSkewCycleCounterReconstruction()
        {
            Route route = PlainRoute();
            RouteEventLog.Add(new RouteEvent
            {
                Type = RouteEventType.RouteDispatched, UT = 900.0, RouteId = route.Id,
                RouteCycleId = "cycle-1", RouteStopIndex = -1,
            });
            RouteEventLog.Add(new RouteEvent
            {
                Type = RouteEventType.RouteCargoDelivered, UT = 950.0, RouteId = route.Id,
                RouteCycleId = "cycle-1", RouteStopIndex = 0,
            });
            RouteEventLog.Add(new RouteEvent
            {
                Type = RouteEventType.RouteHeld, UT = 1000.0, RouteId = route.Id,
                RouteCycleId = "cycle-7", RouteStopIndex = -1, RouteHoldKind = OriginLacksCargo,
            });
            RouteRewindClassifier.ReconstructCycleCounters(route, RouteEventLog.Events);
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(1, route.SkippedCycles);
        }

        // ==================================================================
        // Through the real loop crossing
        // ==================================================================

        private sealed class HoldEnv : IRouteRuntimeEnvironment
        {
            internal string OriginShort;
            internal double OriginShortfall;
            internal string DestinationFullToken;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall)
            {
                lackingResource = OriginShort ?? string.Empty;
                shortfall = OriginShort == null ? 0.0 : OriginShortfall;
                return OriginShort == null;
            }
            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                fullResource = DestinationFullToken ?? string.Empty;
                return DestinationFullToken == null;
            }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private static Route LoopRoute(string id = "route-loop-held")
        {
            return new Route
            {
                Id = id, Name = "Rover Supply", Status = RouteStatus.Active,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1150.0, DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = 1000.0, LastObservedLoopCycleIndex = -1,
                DispatchInterval = 300.0, TransitDuration = 300.0, CadenceMultiplier = 1,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        // catches: the loop blocked branch not wiring the writer (the live path every
        // v0 route takes) or wiring it per crossing.
        [Fact]
        public void LoopCrossings_HeldThreeTimesOnTwoReasons_WriteTwoRows()
        {
            Route route = LoopRoute();
            RouteStore.AddRoute(route);
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;

            var env = new HoldEnv { OriginShort = "LiquidFuel", OriginShortfall = 60.0 };
            RouteOrchestrator.Tick(1150.0, env);
            env.OriginShortfall = 20.0;
            RouteOrchestrator.Tick(1450.0, env);
            env.OriginShort = null;
            env.DestinationFullToken = "LiquidFuel";
            RouteOrchestrator.Tick(1750.0, env);

            Assert.Equal(3, route.SkippedCycles);
            List<RouteEvent> held = Held(route.Id);
            Assert.Equal(2, held.Count);
            Assert.Equal(OriginLacksCargo, held[0].RouteHoldKind);
            Assert.Equal(1150.0, held[0].UT);
            Assert.Equal(60.0, held[0].RouteHoldShortfall);
            Assert.Equal("cycle-0", held[0].RouteCycleId);
            Assert.Equal(DestinationFull, held[1].RouteHoldKind);
            Assert.Equal(1750.0, held[1].UT);
        }

        // A Send-armed run that is held: the Held row precedes the same-UT
        // "Paused after a held run" marker, so the history reads reason, then pause.
        [Fact]
        public void SendOnceHeld_HeldRowPrecedesTheBlockedThenPausedMarker()
        {
            Route route = LoopRoute();
            RouteStore.AddRoute(route);
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
            Assert.True(RouteOrchestrator.TrySendOneCycleNow(route, 1100.0));

            RouteOrchestrator.Tick(1150.0, new HoldEnv { DestinationFullToken = "stored-part:evaScienceKit" });

            List<RouteEvent> rows = RouteEventLog.Events
                .Where(a => a.RouteId == route.Id
                    && (a.Type == RouteEventType.RouteHeld || a.Type == RouteEventType.RoutePaused))
                .ToList();
            Assert.Equal(2, rows.Count);
            Assert.Equal(RouteEventType.RouteHeld, rows[0].Type);
            Assert.Equal(RouteEventType.RoutePaused, rows[1].Type);
            Assert.Equal(rows[0].UT, rows[1].UT);
            Assert.Equal("stored-part:evaScienceKit", rows[0].RouteEndpointReason);
        }
    }
}
