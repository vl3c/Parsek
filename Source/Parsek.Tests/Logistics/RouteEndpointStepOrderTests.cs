using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// THE RESOLUTION STEP ORDER, pinned against the same function production drives.
    ///
    /// <para>Before this, the order lived as the sequence of <c>if</c> blocks inside
    /// <c>TryResolveEndpoint</c>, which no headless cell could reach: forcing proximity ahead
    /// of the root-part step left the whole suite green while a start-docked route silently
    /// went back to resolving its origin by "nearest vessel to the recorded coordinates".
    /// <c>NextEndpointStep</c> is now the ONLY place the order exists and
    /// <c>TryResolveEndpoint</c> dispatches from it, so a reordering changes production
    /// behaviour and reds these cells together.</para>
    /// </summary>
    public class RouteEndpointStepOrderTests
    {
        private const RouteEndpointResolver.EndpointResolutionStep None =
            RouteEndpointResolver.EndpointResolutionStep.None;
        private const RouteEndpointResolver.EndpointResolutionStep Root =
            RouteEndpointResolver.EndpointResolutionStep.RootPart;
        private const RouteEndpointResolver.EndpointResolutionStep Pid =
            RouteEndpointResolver.EndpointResolutionStep.Pid;
        private const RouteEndpointResolver.EndpointResolutionStep Proximity =
            RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity;

        [Fact]
        public void RootPartIsTried_First()
        {
            // FAILS IF: the order is swapped. This is the cell the reviewer's mutation
            // (proximity first for surface endpoints) has to red.
            Assert.Equal(Root, RouteEndpointResolver.NextEndpointStep(
                None, rootIdKnown: true, pidKnown: true, proximityEligible: true));
        }

        [Fact]
        public void TheWholeSequenceIsRootThenPidThenProximity()
        {
            Assert.Equal(Root, RouteEndpointResolver.NextEndpointStep(None, true, true, true));
            Assert.Equal(Pid, RouteEndpointResolver.NextEndpointStep(Root, true, true, true));
            Assert.Equal(Proximity, RouteEndpointResolver.NextEndpointStep(Pid, true, true, true));
            Assert.Equal(None, RouteEndpointResolver.NextEndpointStep(Proximity, true, true, true));
        }

        [Fact]
        public void UnknownInputsAreSkipped_NotStalledOn()
        {
            // A KSC origin has neither a root id nor a pid, and a pre-2026-09-02 route has no
            // root id: the walk must fall straight to the step that CAN answer rather than
            // returning None because the first step's input is missing.
            Assert.Equal(Pid, RouteEndpointResolver.NextEndpointStep(
                None, rootIdKnown: false, pidKnown: true, proximityEligible: true));
            Assert.Equal(Proximity, RouteEndpointResolver.NextEndpointStep(
                None, rootIdKnown: false, pidKnown: false, proximityEligible: true));
            Assert.Equal(None, RouteEndpointResolver.NextEndpointStep(
                None, rootIdKnown: false, pidKnown: false, proximityEligible: false));
            Assert.Equal(Proximity, RouteEndpointResolver.NextEndpointStep(
                Root, rootIdKnown: true, pidKnown: false, proximityEligible: true));
        }

        [Fact]
        public void RootMatch_WinsOverANearerProximityCandidate()
        {
            // THE FAILURE THIS ORDER EXISTS TO PREVENT: the transport parked back at the
            // depot is nearer the recorded coordinates than anything else, so with proximity
            // first a supply route resolves its own transport as its origin and pays itself.
            Assert.Equal(Proximity, RouteEndpointResolver.ResolveEndpointStepPure(
                rootIdKnown: false, rootMatches: false,
                pidKnown: false, pidMatches: false,
                proximityEligible: true, proximityMatches: true));

            Assert.Equal(Root, RouteEndpointResolver.ResolveEndpointStepPure(
                rootIdKnown: true, rootMatches: true,
                pidKnown: false, pidMatches: false,
                proximityEligible: true, proximityMatches: true));
        }

        [Fact]
        public void RootMatch_WinsOverAPidMatchToo()
        {
            // A persistentId is craft-baked and can name a DIFFERENT launch of the same craft
            // file; a part flightID cannot. When both would answer, identity wins.
            Assert.Equal(Root, RouteEndpointResolver.ResolveEndpointStepPure(
                rootIdKnown: true, rootMatches: true,
                pidKnown: true, pidMatches: true,
                proximityEligible: true, proximityMatches: true));
        }

        [Fact]
        public void RootMiss_FallsThroughToPid_ThenToProximity()
        {
            // A rebuilt or recovered depot has a new root part, so the identity step must
            // MISS cleanly and let the later steps answer.
            Assert.Equal(Pid, RouteEndpointResolver.ResolveEndpointStepPure(
                rootIdKnown: true, rootMatches: false,
                pidKnown: true, pidMatches: true,
                proximityEligible: true, proximityMatches: true));

            Assert.Equal(Proximity, RouteEndpointResolver.ResolveEndpointStepPure(
                rootIdKnown: true, rootMatches: false,
                pidKnown: true, pidMatches: false,
                proximityEligible: true, proximityMatches: true));
        }

        [Fact]
        public void NothingMatches_ResolvesToNone()
        {
            Assert.Equal(None, RouteEndpointResolver.ResolveEndpointStepPure(
                rootIdKnown: true, rootMatches: false,
                pidKnown: true, pidMatches: false,
                proximityEligible: true, proximityMatches: false));
        }

        [Fact]
        public void AnOrbitalDepot_ResolvesByRootAlthoughProximityCanNeverRun()
        {
            // The shape the root step buys outright: a station origin is not surface-typed,
            // so the proximity step is ineligible and the pid is 0 at capture. Without the
            // identity step this endpoint resolves to nothing at all.
            Assert.Equal(Root, RouteEndpointResolver.ResolveEndpointStepPure(
                rootIdKnown: true, rootMatches: true,
                pidKnown: false, pidMatches: false,
                proximityEligible: false, proximityMatches: false));
        }
    }
}
