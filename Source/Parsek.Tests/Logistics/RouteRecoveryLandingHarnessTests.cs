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
    /// M6 "precise per-run recovery landing" DIVERGENCE HARNESS (recovery-credit
    /// plan OQ1; design doc 19.4 M6). This file changes NO behavior: it PINS,
    /// with numbers, the timing gap between
    ///
    ///   (a) the SHIPPED constant-deferral model: cycle K's recovery credit is a
    ///       constant amount (SumRecoveredCredits over the source tree) flushed at
    ///       the NEXT dock crossing, i.e. at dispatchUT(K) + one dispatch
    ///       interval (EmitPendingRecoveryCredit, RouteOrchestrator.cs; plan
    ///       section 5.1/5.5), and
    ///
    ///   (b) the UT-MAPPED IDEAL the OQ1 second clock would implement: cycle K's
    ///       credit lands when the RECORDED recovery physically lands in run K's
    ///       replayed timeline, i.e. at
    ///       dispatchUT(K) + (recordedRecoveryUT - recordedDockUT).
    ///
    /// CONSTRUCTIBILITY (why the divergence is real in v0, not hypothetical):
    /// RouteBuilder clamps DispatchInterval >= (recordedDockUT - rootLaunchUT)
    /// ONLY (the outbound launch-to-dock leg; RouteBuilder.cs "must-fix #2"
    /// clamp). The post-undock fly-home-and-recover leg is OUTSIDE the rendered
    /// [launch .. dock] loop span (RouteBackingMission renders to the dock) and
    /// OUTSIDE the clamp, so its recorded duration is unbounded relative to the
    /// dispatch interval. Both divergence lobes are therefore constructible:
    ///
    ///   EARLY lobe (fly-home longer than the interval): the shipped model pays
    ///   each run's recovery one interval after dispatch even though that run's
    ///   transport is still flying home for another N-1 intervals. The player
    ///   sees funds BEFORE the recording says the recovery happened.
    ///
    ///   LATE lobe (interval longer than the fly-home, e.g. a monthly resupply
    ///   whose recorded round trip takes hours): the shipped model holds the
    ///   credit until the next crossing, up to a full interval AFTER the
    ///   recorded recovery landed. The player fronts gross and waits.
    ///
    /// The recorded recovery UT is captured on the FundsEarning(Recovery) row
    /// (GameAction.UT, written at recovery time by
    /// LedgerOrchestrator.OnVesselRecoveryFunds) but is NEVER read by the credit
    /// timing: SumRecoveredCredits sums FundsAwarded and ignores UT.
    ///
    /// The active tests below PASS: they pin TODAY's model (they are not
    /// required-RED). The deliberately-SKIPPED test at the bottom is the ideal-
    /// model assertion; if un-skipped it fails RED with the delta in its
    /// failure message. Decision memo:
    /// docs/dev/research/logistics-recovery-clock-memo.md. Do not build the
    /// second clock without a separate green-light (it is L-sized: persisted
    /// per-run queue, second idempotency surface, rewind/tombstone
    /// reversibility, codec).
    /// </summary>
    [Collection("Sequential")]
    public class RouteRecoveryLandingHarnessTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();
        private readonly List<double> liveCredits = new List<double>();

        private const string TreeId = "tree-landing";
        private const string RootRecId = "rec-root";
        private const string RecoveryRecId = "rec-recovery"; // post-undock fly-home leg

        // ------------------------------------------------------------------
        // EARLY-lobe fixture numbers (the OQ1 cycle-overlap case).
        //
        //   loop span            [1000 .. 1300]  (launch 1000, dock 1150)
        //   dispatch interval    300 s (cadence == interval; crossings at the
        //                        dock phase: 1150, 1450, 1750, 2050, 2350, ...)
        //   recorded dock UT     1150
        //   recorded recovery UT 2350  (the transport flies home for 1200 s
        //                        after the dock = FOUR dispatch intervals)
        //   per-run recovery     7300 funds
        //
        // Run K dispatches at UT_K = 1150 + 300*K.
        //   shipped credit for run K:  UT_K + 300   (next crossing)
        //   UT-mapped ideal for run K: UT_K + 1200  (recorded landing, mapped)
        //   divergence per run:        900 s EARLY = 3 dispatch intervals.
        // ------------------------------------------------------------------
        private const double Interval = 300.0;
        private const double SpanStart = 1000.0;
        private const double SpanEnd = 1300.0;
        private const double RecordedDockUT = 1150.0;
        private const double RecordedRecoveryUT = 2350.0;
        private const double RecoveryOffset = RecordedRecoveryUT - RecordedDockUT; // 1200 s
        private const double Recovered = 7300.0;

        public RouteRecoveryLandingHarnessTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            ParsekScenario.ResetInstanceForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.RecoveryCreditFunderForTesting = amount => liveCredits.Add(amount);
            liveCredits.Clear();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.RecoveryCreditFunderForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Fixtures / seams (mirrors RouteRecoveryCreditTests idioms)
        // ==================================================================

        private void InstallUnitResolver(double cadenceSeconds)
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: SpanStart, spanEndUT: SpanEnd,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: SpanStart);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

        // Mirrors ApplyDelivery's observable contract WITHOUT a live Vessel
        // (same fake as RouteRecoveryCreditTests): emit RouteCargoDelivered +
        // bump CompletedCycles. The dispatch-debit half and the recovery-credit
        // flush run for real.
        private void InstallFakeDeliveryApplier()
        {
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered,
                    UT = currentUT,
                    RouteId = route.Id,
                    RouteCycleId = cycleId,
                    RouteStopIndex = 0,
                    Sequence = 0,
                    RouteKscFundsCost = 0f,
                });
                route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.TransitionTo(RouteStatus.Active, "delivered-loop-fake");
            };
        }

        private static Route BuildLoopRoute(double dispatchInterval)
        {
            return new Route
            {
                Id = "route-landing",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                BackingMissionTreeId = TreeId,
                RecordedDockUT = RecordedDockUT,
                DockMemberRecordingId = RootRecId,
                LoopAnchorUT = SpanStart,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = dispatchInterval,
                TransitDuration = RecordedDockUT - SpanStart,
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
                    new RouteSourceRef { RecordingId = RootRecId, TreeId = TreeId, RouteProofHash = "deadbeef" },
                },
            };
        }

        // Source tree with the root ([launch..dock] member) and the post-undock
        // fly-home-and-recover leg (in the TREE, outside the route member set,
        // gotcha G1).
        private static void InstallSourceTree()
        {
            var root = new Recording { RecordingId = RootRecId, TreeId = TreeId, VesselName = "transport" };
            var recovery = new Recording { RecordingId = RecoveryRecId, TreeId = TreeId, VesselName = "transport-home" };
            var tree = new RecordingTree { Id = TreeId, RootRecordingId = RootRecId };
            tree.Recordings[RootRecId] = root;
            tree.Recordings[RecoveryRecId] = recovery;
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // The recovery FundsEarning row carries the RECORDED recovery landing UT
        // (production: LedgerOrchestrator.OnVesselRecoveryFunds stamps the
        // recovery-time UT). The shipped credit path never reads it; the ideal
        // model is keyed on it.
        private static void SeedRecoveryRow(double recoveryUt)
        {
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                RecordingId = RecoveryRecId,
                FundsAwarded = (float)Recovered,
                UT = recoveryUt,
            });
        }

        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; } = true;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private static List<GameAction> Credits()
        {
            return Ledger.Actions.Where(a => a.Type == GameActionType.RouteRecoveryCredited).ToList();
        }

        // ==================================================================
        // EARLY lobe: fly-home = 4 dispatch intervals. The shipped credit for a
        // run lands 3 intervals (900 s) BEFORE the recorded recovery landing.
        // ==================================================================

        // PASSES: pins TODAY's model, and pins that the fixture genuinely
        // diverges from the UT-mapped ideal (delta 900 s = 3 intervals). If the
        // second recovery clock ever ships, this test goes RED here and the
        // skipped ideal test below goes green: swap them.
        [Fact]
        public void ShippedModel_CreditLandsOneIntervalAfterDispatch_ThreeIntervalsBeforeRecordedLanding()
        {
            InstallSourceTree();
            SeedRecoveryRow(RecordedRecoveryUT);
            var route = BuildLoopRoute(Interval);
            RouteStore.AddRoute(route);
            InstallUnitResolver(Interval);
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            double dispatchUt = 1150.0;          // crossing 0: cycle-0 dispatches
            double nextCrossingUt = 1450.0;      // crossing 1: cycle-0's credit flushes

            RouteOrchestrator.Tick(dispatchUt, env);
            Assert.Empty(Credits());             // deferral: nothing on the own tick
            RouteOrchestrator.Tick(nextCrossingUt, env);

            // (a) SHIPPED: constant deferral. The player sees the 7300 funds at
            // UT 1450, exactly one dispatch interval after the dispatch.
            var credit = Assert.Single(Credits());
            Assert.Equal("cycle-0", credit.RouteCycleId);
            Assert.Equal(nextCrossingUt, credit.UT);
            Assert.Equal((float)Recovered, credit.RouteKscFundsCost);

            // (b) UT-MAPPED IDEAL (not implemented; documented here): run 0's
            // recorded recovery physically lands at
            //   dispatchUT + (recordedRecoveryUT - recordedDockUT)
            //   = 1150 + 1200 = 2350.
            double idealCreditUt = dispatchUt + RecoveryOffset;
            Assert.Equal(2350.0, idealCreditUt);

            // The pinned divergence: the shipped credit is 900 s = 3.0 dispatch
            // intervals EARLY relative to the recording. This assert keeps the
            // fixture honest (if it ever stops diverging the harness is dead).
            double earlyBySeconds = idealCreditUt - credit.UT;
            Assert.Equal(900.0, earlyBySeconds);
            Assert.Equal(3.0, earlyBySeconds / Interval);
        }

        // PASSES: the cumulative-lead statement of the same divergence. By the
        // 4th crossing (UT 2050) the shipped model has paid THREE full per-run
        // recoveries (21900 funds) while ZERO recorded recoveries have physically
        // landed yet (the first lands at 2350). The player's balance runs a
        // permanent ~3-credit (21900 funds) lead over the recorded physical
        // timeline while the route overlaps.
        [Fact]
        public void ShippedModel_ThreeCreditsPaidBeforeAnyRecordedRecoveryHasLanded()
        {
            InstallSourceTree();
            SeedRecoveryRow(RecordedRecoveryUT);
            var route = BuildLoopRoute(Interval);
            RouteStore.AddRoute(route);
            InstallUnitResolver(Interval);
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // cycle-0 dispatch
            RouteOrchestrator.Tick(1450.0, env); // cycle-1 dispatch + cycle-0 credit
            RouteOrchestrator.Tick(1750.0, env); // cycle-2 dispatch + cycle-1 credit
            RouteOrchestrator.Tick(2050.0, env); // cycle-3 dispatch + cycle-2 credit

            // SHIPPED: three credits flushed (cycles 0, 1, 2), 21900 funds live.
            Assert.Equal(3, Credits().Count);
            Assert.Equal(3, liveCredits.Count);
            Assert.Equal(3 * Recovered, liveCredits.Sum());

            // UT-MAPPED IDEAL at UT 2050: run 0's recovery lands at 2350, run
            // 1's at 2650, run 2's at 2950 - NONE have landed. Ideal credited
            // total at this instant: 0 funds. Divergence: 21900 funds paid
            // ahead of the recorded physical timeline.
            double probeUt = 2050.0;
            int ideallyLanded = 0;
            for (int k = 0; k <= 2; k++)
            {
                double landingUt = (1150.0 + k * Interval) + RecoveryOffset;
                if (landingUt <= probeUt) ideallyLanded++;
            }
            Assert.Equal(0, ideallyLanded);
            double fundsLead = liveCredits.Sum() - ideallyLanded * Recovered;
            Assert.Equal(21900.0, fundsLead);
        }

        // ==================================================================
        // LATE lobe: interval (900 s) longer than the fly-home (600 s). The
        // shipped credit lands 300 s AFTER the recorded recovery landing (in the
        // realistic version - a monthly resupply whose recorded round trip takes
        // hours - the lag approaches a full dispatch interval).
        // ==================================================================

        // PASSES: pins the opposite divergence lobe. Run 0 dispatches at 1150,
        // its recorded recovery lands at 1150 + 600 = 1750, but the shipped
        // model holds the credit until the next crossing at 2050.
        [Fact]
        public void ShippedModel_LongInterval_CreditLandsAfterRecordedLanding()
        {
            const double longInterval = 900.0;   // crossings at 1150, 2050, ...
            const double lateRecoveryUt = 1750.0; // fly-home 600 s < interval
            InstallSourceTree();
            SeedRecoveryRow(lateRecoveryUt);
            var route = BuildLoopRoute(longInterval);
            RouteStore.AddRoute(route);
            InstallUnitResolver(longInterval);
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // cycle-0 dispatch
            Assert.Empty(Credits());
            RouteOrchestrator.Tick(2050.0, env); // next crossing: cycle-0 credit

            var credit = Assert.Single(Credits());
            Assert.Equal("cycle-0", credit.RouteCycleId);
            Assert.Equal(2050.0, credit.UT);

            // UT-mapped ideal: 1150 + (1750 - 1150) = 1750. Shipped is 300 s LATE.
            double idealCreditUt = 1150.0 + (lateRecoveryUt - RecordedDockUT);
            Assert.Equal(1750.0, idealCreditUt);
            Assert.Equal(300.0, credit.UT - idealCreditUt);
        }

        // ==================================================================
        // THE IDEAL-MODEL ASSERTION (deliberately skipped). This is what the
        // OQ1 second recovery clock would have to make pass. Un-skipping it
        // today fails RED with the divergence in the failure message:
        //
        //   "UT-mapped ideal DIVERGENCE: cycle-0's credit landed at UT 1450
        //    (next crossing, constant deferral) but the recorded recovery
        //    physically lands at UT 2350 (dispatch 1150 + fly-home offset 1200);
        //    the shipped credit is 900 s = 3.0 dispatch intervals EARLY."
        //
        // Keep it skipped unless the maintainer green-lights the clock (see
        // docs/dev/research/logistics-recovery-clock-memo.md).
        // ==================================================================

        [Fact(Skip = "OQ1 ideal-model assertion: the shipped constant-deferral " +
            "credit intentionally diverges from the recorded recovery landing UT " +
            "(here by 900 s = 3 dispatch intervals). Un-skip only if the second " +
            "recovery clock is green-lit; decision memo at " +
            "docs/dev/research/logistics-recovery-clock-memo.md")]
        public void UtMappedIdeal_CreditForCycle0_WouldLandAtRecordedRecoveryUT()
        {
            InstallSourceTree();
            SeedRecoveryRow(RecordedRecoveryUT);
            var route = BuildLoopRoute(Interval);
            RouteStore.AddRoute(route);
            InstallUnitResolver(Interval);
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            double dispatchUt = 1150.0;
            RouteOrchestrator.Tick(dispatchUt, env);
            RouteOrchestrator.Tick(1450.0, env);

            var credit = Assert.Single(Credits());
            double idealCreditUt = dispatchUt + RecoveryOffset; // 2350
            double deltaSeconds = idealCreditUt - credit.UT;
            Assert.True(
                Math.Abs(credit.UT - idealCreditUt) < 0.5,
                "UT-mapped ideal DIVERGENCE: cycle-0's credit landed at UT " +
                credit.UT.ToString("R", IC) + " (next crossing, constant deferral) " +
                "but the recorded recovery physically lands at UT " +
                idealCreditUt.ToString("R", IC) + " (dispatch " +
                dispatchUt.ToString("R", IC) + " + fly-home offset " +
                RecoveryOffset.ToString("R", IC) + "); the shipped credit is " +
                deltaSeconds.ToString("R", IC) + " s = " +
                (deltaSeconds / Interval).ToString("R", IC) +
                " dispatch intervals EARLY.");
        }
    }
}
