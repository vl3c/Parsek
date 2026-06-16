using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the reusable per-window endpoint pickup-debit path
    /// (<see cref="RouteOrchestrator.ApplyPickupDebit"/>, M3 Phase 3, design
    /// D5): the REVERSE-direction reuse of the M1 origin-debit machinery
    /// re-aimed at a per-window pickup ENDPOINT vessel sourcing a pickup
    /// manifest. The branches reachable WITHOUT a live KSP <c>Vessel</c> are
    /// covered here (the test seam, the empty-manifest no-op, and the
    /// unresolved / resolved-null-vessel branch); the resolved-vessel WRITER
    /// arithmetic (loaded + unloaded proto removal) needs a live Vessel and is
    /// pinned by the in-game <c>LogisticsPickupRuntimeTests</c> mirror of
    /// <c>LogisticsOriginDebitRuntimeTests</c> (Phase 7).
    ///
    /// <para>The fake <see cref="IRouteRuntimeEnvironment"/> below resolves a
    /// NULL vessel (instantiating a real Vessel drags in KSP statics, breaking
    /// the xUnit-only contract), so a "resolvable" endpoint here exercises the
    /// resolved-null-vessel UNRESOLVED branch - the same honest-bookkeeping
    /// shape the M1 origin path returns.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RoutePickupDebitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RoutePickupDebitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // Resolves a null vessel (no KSP statics) but honors a settable
        // resolvable flag so the unresolved-vs-resolved-null branches both fire.
        private sealed class FakePickupEnv : IRouteRuntimeEnvironment
        {
            public bool EndpointResolvable { get; set; } = true;
            public string FailureReason { get; set; } = "pid-miss";

            public bool IsCareer => false;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                reason = EndpointResolvable ? string.Empty : FailureReason;
                return EndpointResolvable;
            }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                vessel = null; // KSP-static-free: a real Vessel would break xUnit
                reason = EndpointResolvable ? string.Empty : FailureReason;
                return EndpointResolvable;
            }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private static RouteEndpoint Endpoint(uint pid)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = pid,
                BodyName = "Kerbin",
                IsSurface = false,
            };
        }

        private static Dictionary<string, double> Manifest(params (string, double)[] entries)
        {
            var m = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (name, amount) in entries)
                m[name] = amount;
            return m;
        }

        // catches: the test seam not being consulted (Phase 4 cannot drive the
        // two-direction applier without a live endpoint Vessel otherwise).
        [Fact]
        public void Seam_ShortCircuits_WithEndpointAndManifest()
        {
            RouteEndpoint capturedEndpoint = default;
            Dictionary<string, double> capturedManifest = null;
            var expected = new RouteOrchestrator.OriginDebitOutcome { OriginVesselPid = 777u, Short = false };
            RouteOrchestrator.PickupDebitApplierForTesting = (ep, manifest, env) =>
            {
                capturedEndpoint = ep;
                capturedManifest = manifest;
                return expected;
            };

            var endpoint = Endpoint(42u);
            var manifestIn = Manifest(("Ore", 50.0));
            var outcome = RouteOrchestrator.ApplyPickupDebit(endpoint, manifestIn, new FakePickupEnv(), "route-x");

            Assert.Equal(777u, outcome.OriginVesselPid);
            Assert.False(outcome.Short);
            // The seam received the endpoint + the per-window manifest.
            Assert.Equal(42u, capturedEndpoint.VesselPersistentId);
            Assert.Same(manifestIn, capturedManifest);
        }

        // catches: an empty/null pickup manifest taking the unresolved or write
        // path (it must be a structural no-op: zero actuals, resolved, not short).
        [Fact]
        public void EmptyManifest_NoOp_ResolvedNotShort()
        {
            var emptyOutcome = RouteOrchestrator.ApplyPickupDebit(
                Endpoint(42u), new Dictionary<string, double>(), new FakePickupEnv(), "route-empty");
            Assert.Null(emptyOutcome.ActualDebited);
            Assert.Null(emptyOutcome.RequestedOnShortfall);
            Assert.Equal(0u, emptyOutcome.OriginVesselPid);
            Assert.False(emptyOutcome.Short);
            Assert.False(emptyOutcome.Unresolved);

            var nullOutcome = RouteOrchestrator.ApplyPickupDebit(
                Endpoint(42u), null, new FakePickupEnv(), "route-null");
            Assert.False(nullOutcome.Short);
            Assert.False(nullOutcome.Unresolved);

            // No endpoint-resolution attempt is logged for a no-op manifest.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("empty pickup manifest"));
        }

        // catches: a failed endpoint resolution NOT producing the honest
        // unresolved bookkeeping (zero actuals, FULL requested manifest, short).
        [Fact]
        public void UnresolvedEndpoint_FullRequestedManifest_ShortAndUnresolved()
        {
            var env = new FakePickupEnv { EndpointResolvable = false, FailureReason = "pid-miss-no-surface-fallback" };
            var manifest = Manifest(("Ore", 50.0), ("LiquidFuel", 10.0), ("Ablator", 0.0));

            var outcome = RouteOrchestrator.ApplyPickupDebit(Endpoint(42u), manifest, env, "route-unresolved");

            Assert.True(outcome.Unresolved);
            Assert.True(outcome.Short);
            Assert.Null(outcome.ActualDebited);
            Assert.Equal(0u, outcome.OriginVesselPid);
            Assert.NotNull(outcome.RequestedOnShortfall);
            // Positive entries only - the non-positive Ablator entry is dropped
            // (matches the planner's <=0 skip).
            Assert.Equal(2, outcome.RequestedOnShortfall.Count);
            Assert.True(outcome.RequestedOnShortfall.ContainsKey("Ore"));
            Assert.True(outcome.RequestedOnShortfall.ContainsKey("LiquidFuel"));
            Assert.False(outcome.RequestedOnShortfall.ContainsKey("Ablator"));
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("pickup endpoint unresolved")
                && l.Contains("pid-miss-no-surface-fallback"));
        }

        // catches: a resolution that returns true with a null vessel slipping
        // past the unresolved guard (the fake resolves null - the same one-tick
        // race the M1 origin path treats as unresolved).
        [Fact]
        public void ResolvedNullVessel_TreatedAsUnresolved()
        {
            var env = new FakePickupEnv { EndpointResolvable = true };
            var manifest = Manifest(("Ore", 50.0));

            var outcome = RouteOrchestrator.ApplyPickupDebit(Endpoint(42u), manifest, env, "route-null-vessel");

            Assert.True(outcome.Unresolved);
            Assert.True(outcome.Short);
            Assert.NotNull(outcome.RequestedOnShortfall);
            Assert.Equal(50.0, outcome.RequestedOnShortfall["Ore"]);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("pickup endpoint unresolved")
                && l.Contains("resolved-null-vessel"));
        }

        // ==============================================================
        // Flow-gate symmetry (design D4/D5): the probe and the writer share
        // RouteOrchestrator.ShouldDeliverToResource, so a NO_FLOW / flow-locked
        // tank is neither COUNTED by the probe nor DEBITED by the writer. The
        // live PartResource.flowState read needs a Vessel (in-game), but the
        // SYMMETRY is structural: the probe (which applies the gate) reports 0
        // stored for a locked resource, so the planner clamps the pickup line
        // to 0 and the writer (sharing the gate) removes nothing.
        // ==============================================================

        // catches: a flow-locked endpoint resource being planned for removal -
        // the probe reports 0 (gate applied), so the plan clamps to 0 + short,
        // and the shared writer gate guarantees the live removal is also 0.
        [Fact]
        public void FlowLockedResource_ProbeReportsZero_PlanClampsToZero()
        {
            // Locked-tank probe: reports 0 for "MonoPropellant" (flowState=false
            // in production) and a real amount for "Ore". The production probe
            // (LiveOriginCargoProbe) applies ShouldDeliverToResource so a locked
            // tank reads as 0 stored; this fake mirrors that contract.
            var probe = new FlowGatedProbe
            {
                Stored = new Dictionary<string, double> { { "Ore", 100.0 } },
                // MonoPropellant intentionally absent -> reads 0 (locked).
            };
            var manifest = Manifest(("MonoPropellant", 20.0), ("Ore", 30.0));

            var plan = RouteOriginDebitPlanner.PrepareDebit(manifest, probe);

            Assert.Equal(2, plan.Resources.Count);
            Assert.True(plan.IsShort);
            // Ordinal order: MonoPropellant before Ore.
            Assert.Equal("MonoPropellant", plan.Resources[0].Name);
            Assert.Equal(20.0, plan.Resources[0].Required);
            Assert.Equal(0.0, plan.Resources[0].Available); // locked -> nothing removable
            Assert.Equal("Ore", plan.Resources[1].Name);
            Assert.Equal(30.0, plan.Resources[1].Available);
        }

        // Probe fake whose stored read already reflects the shared flow gate
        // (locked / NO_FLOW tanks read 0), mirroring the production
        // LiveOriginCargoProbe contract used by both the probe and the writer.
        private sealed class FlowGatedProbe : IOriginCargoProbe
        {
            public Dictionary<string, double> Stored = new Dictionary<string, double>();
            public double ProbeResourceStored(string resourceName)
            {
                if (resourceName == null) return 0.0;
                return Stored.TryGetValue(resourceName, out double v) ? v : 0.0;
            }
        }
    }
}
