using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Closes ROUTE-ENDPOINT-TRANSFER-DOCKED-DOMINANT-PARTNER: while a visitor was docked to a
    /// delivery destination AND dominated the merged vessel, the destination's stop resolved
    /// pid -> proximity (the root-part step was unreachable, because a delivery stop's endpoint
    /// carried no <see cref="RouteEndpoint.RootPartUId"/> at all), the proximity step landed on
    /// the composite, and PR #1627's transfer REBOUND the route to the visitor - which then
    /// undocked and flew away with it.
    ///
    /// <para>TWO HALVES, and both are needed. (1) A delivery endpoint is STAMPED with its root
    /// part flightID at capture, so the walk starts at identity. (2) The root-part step matches
    /// a vessel whose PART SET carries that flightID, not only one whose OWN root is it -
    /// because <c>Part.Couple</c> merges into the DOMINANT half's <c>Vessel</c> and the
    /// absorbed half's root part becomes an ordinary part, re-parented and never renumbered.
    /// Without (2), (1) still loses the base for as long as a dominant visitor is docked.</para>
    ///
    /// <para>THE MIRROR DIRECTION IS PINNED CELL BY CELL: destination-dominant resolves through
    /// pass 1 and visitor-dominant through pass 2, and both land on the SAME composite - which
    /// is the physically correct answer either way, since the base is part of that craft.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointDockedCompositeTests : System.IDisposable
    {
        private const uint BaseRootFlightId = 5000u;
        private const uint BasePartFlightId = 5001u;
        private const uint VisitorRootFlightId = 9000u;
        private const uint VisitorPartFlightId = 9001u;

        private const uint BasePid = 111u;
        private const uint VisitorPid = 222u;

        private readonly List<string> logLines = new List<string>();

        public RouteEndpointDockedCompositeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            // The resolver's deep-scan miss memo is process-wide static state (CLAUDE.md's
            // shared-static rule), so it is dropped on both sides of every cell here.
            RouteEndpointResolver.ResetForTesting();
        }

        public void Dispose()
        {
            RouteEndpointResolver.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RouteEndpointResolver.RootIdVesselSnapshot Snap(
            uint pid, uint rootFlightId, params uint[] partFlightIds)
        {
            return new RouteEndpointResolver.RootIdVesselSnapshot
            {
                PersistentId = pid,
                RootPartFlightId = rootFlightId,
                PartFlightIds = new List<uint>(partFlightIds),
            };
        }

        // ==================================================================
        // 1. THE TWO DOMINANCE DIRECTIONS
        // ==================================================================

        // catches: the defect itself. The visitor dominates, so the merged vessel wears the
        // VISITOR's pid and the VISITOR's root - the base is neither - and before the composite
        // pass this resolution missed entirely and fell through to proximity, which is where
        // the rebind lives.
        [Fact]
        public void VisitorDominant_TheBaseResolvesThroughItsPartOnTheComposite()
        {
            // ONE live vessel: the merged craft. The base's own Vessel was destroyed by
            // Part.Couple, so its pid is not in FlightGlobals at all.
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(VisitorPid, VisitorRootFlightId,
                     VisitorRootFlightId, VisitorPartFlightId,
                     BaseRootFlightId, BasePartFlightId),
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, snapshots, null,
                out _, out uint pickedPid, out uint collidingPid, out string reason));

            Assert.Equal(VisitorPid, pickedPid);
            Assert.Equal(0u, collidingPid);
            Assert.Equal("docked-composite-match", reason);
        }

        // catches: a fix written only for the visitor-dominant direction. When the DESTINATION
        // dominates, the composite's own root IS the base's root, so pass 1 must resolve it and
        // the reason must NOT read as a composite match - otherwise every ordinary docked
        // delivery would start announcing a composite.
        [Fact]
        public void DestinationDominant_ResolvesThroughTheOrdinaryRootPass()
        {
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(BasePid, BaseRootFlightId,
                     BaseRootFlightId, BasePartFlightId,
                     VisitorRootFlightId, VisitorPartFlightId),
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, snapshots, null,
                out _, out uint pickedPid, out _, out string reason));

            Assert.Equal(BasePid, pickedPid);
            Assert.Equal(string.Empty, reason);
        }

        // catches: the composite pass overtaking an own-root match. An own-root match must win
        // everywhere, or a base standing on its own could be resolved to some other craft that
        // happens to carry a part with the same id (impossible in a healthy save, but the pass
        // ORDER is the contract, not the impossibility).
        [Fact]
        public void AnOwnRootMatchBeatsAContainsMatch()
        {
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                // The composite listed FIRST, so a single-pass scan would take it.
                Snap(VisitorPid, VisitorRootFlightId, VisitorRootFlightId, BaseRootFlightId),
                Snap(BasePid, BaseRootFlightId, BaseRootFlightId, BasePartFlightId),
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, snapshots, null,
                out _, out uint pickedPid, out _, out string reason));

            Assert.Equal(BasePid, pickedPid);
            Assert.Equal(string.Empty, reason);
        }

        // catches: the undock half of the round trip. Once the pair separates, the base is its
        // own vessel again with its own root - the SAME key resolves it, which is what makes
        // the stamp durable rather than a docked-only patch.
        [Fact]
        public void AfterTheUndock_TheSameKeyResolvesTheBaseOnItsOwn()
        {
            var docked = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(VisitorPid, VisitorRootFlightId, VisitorRootFlightId, BaseRootFlightId),
            };
            var undocked = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(VisitorPid, VisitorRootFlightId, VisitorRootFlightId, VisitorPartFlightId),
                Snap(BasePid, BaseRootFlightId, BaseRootFlightId, BasePartFlightId),
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, docked, null, out _, out uint dockedPid, out _, out _));
            Assert.Equal(VisitorPid, dockedPid);

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, undocked, null, out _, out uint undockedPid, out _, out string reason));
            Assert.Equal(BasePid, undockedPid);
            Assert.Equal(string.Empty, reason);
        }

        // ==================================================================
        // 2. THE PASS'S OWN EDGES
        // ==================================================================

        // catches: an unknown root id pairing with every part set that could not be read. Zero
        // is the unknown sentinel on BOTH sides, so it must never match anything.
        [Fact]
        public void AZeroRootIdNeverMatches_OnEitherPass()
        {
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(VisitorPid, 0u, 0u, BaseRootFlightId),
            };

            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                0u, snapshots, null, out _, out _, out _, out string reason));
            Assert.Equal("root-id-unknown", reason);
        }

        // catches: the ghost exclusion being applied on pass 1 only. A ghost map vessel carries
        // the recorded craft's part ids by construction, so an unexcluded composite pass would
        // deliver a route's cargo into a ghost.
        [Fact]
        public void GhostVesselsAreExcludedFromTheCompositePassToo()
        {
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(VisitorPid, VisitorRootFlightId, VisitorRootFlightId, BaseRootFlightId),
            };
            var ghosts = new HashSet<uint> { VisitorPid };

            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, snapshots, ghosts,
                out _, out _, out _, out string reason));
            Assert.Equal("no-root-match", reason);
        }

        // catches: a snapshot with no readable part set throwing or matching. Pure-test
        // snapshots and vessels mid-teardown both land here.
        [Fact]
        public void ASnapshotWithNoPartSet_IsSimplyNotACompositeCandidate()
        {
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                new RouteEndpointResolver.RootIdVesselSnapshot
                {
                    PersistentId = VisitorPid,
                    RootPartFlightId = VisitorRootFlightId,
                    PartFlightIds = null,
                },
            };

            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, snapshots, null, out _, out _, out _, out string reason));
            Assert.Equal("no-root-match", reason);
        }

        // catches: an ambiguous composite match taken silently. Two live craft carrying one
        // part flightID cannot happen in a healthy save; if it does, the log has to name both.
        [Fact]
        public void TwoCompositesCarryingOnePartId_AreReportedAsAmbiguous()
        {
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(VisitorPid, VisitorRootFlightId, VisitorRootFlightId, BaseRootFlightId),
                Snap(333u, 4444u, 4444u, BaseRootFlightId),
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                BaseRootFlightId, snapshots, null,
                out _, out uint pickedPid, out uint collidingPid, out string reason));

            Assert.Equal(VisitorPid, pickedPid);
            Assert.Equal(333u, collidingPid);
            Assert.Equal("docked-composite-match-ambiguous", reason);
        }

        // ==================================================================
        // 3. THE STEP ORDER IS UNCHANGED BY THE STAMP
        // ==================================================================

        // catches: the stamp being read as a change to the walk. A destination endpoint that
        // now carries a root id simply makes the FIRST step reachable; the order itself is
        // untouched, and an endpoint without one still walks pid -> proximity.
        [Fact]
        public void AStampedDestination_StartsAtRootPart_AnUnstampedOneStillDoesNot()
        {
            Assert.Equal(
                RouteEndpointResolver.EndpointResolutionStep.RootPart,
                RouteEndpointResolver.NextEndpointStep(
                    RouteEndpointResolver.EndpointResolutionStep.None,
                    rootIdKnown: true, pidKnown: true, proximityEligible: true));

            Assert.Equal(
                RouteEndpointResolver.EndpointResolutionStep.Pid,
                RouteEndpointResolver.NextEndpointStep(
                    RouteEndpointResolver.EndpointResolutionStep.None,
                    rootIdKnown: false, pidKnown: true, proximityEligible: true));

            // And the whole walk: with the root known AND matching, proximity is never
            // reached - which is what keeps RouteEndpointTransfer from rebinding the stop.
            Assert.Equal(
                RouteEndpointResolver.EndpointResolutionStep.RootPart,
                RouteEndpointResolver.ResolveEndpointStepPure(
                    rootIdKnown: true, rootMatches: true,
                    pidKnown: true, pidMatches: false,
                    proximityEligible: true, proximityMatches: true));
        }

        // ==================================================================
        // 4. THE STAMP ITSELF - capture, persistence, and the stop
        // ==================================================================

        // catches: the endpoint root being stamped at capture and then dropped by the PROOF
        // codec, which is the surface a delivery stop's endpoint is written to. This reader was
        // genuinely missing the key before 2026-09-06: RouteCodec read it and RouteProofCodec
        // did not, so a stamped window endpoint survived only until the first save.
        [Fact]
        public void AStampedWindowEndpoint_SurvivesTheProofCodecRoundTrip()
        {
            var recording = new Recording
            {
                RecordingId = "rec-stamped-endpoint",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-1",
                        DockUT = 100.0,
                        UndockUT = 200.0,
                        TransferTargetVesselPid = BasePid,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { 11u },
                        EndpointPartPersistentIds = new List<uint> { 33u },
                        EndpointAtDock = new RouteEndpoint
                        {
                            VesselPersistentId = BasePid,
                            RootPartUId = BaseRootFlightId,
                            BodyName = "Mun",
                            Latitude = 1.0,
                            Longitude = 2.0,
                            Altitude = 3.0,
                            IsSurface = true,
                        },
                    },
                },
            };

            var node = new ConfigNode("RECORDING");
            RouteProofCodec.SerializeRouteProofMetadata(node, recording);

            var reloaded = new Recording { RecordingId = "rec-stamped-endpoint" };
            RouteProofCodec.DeserializeRouteProofMetadata(node, reloaded);

            Assert.NotNull(reloaded.RouteConnectionWindows);
            Assert.Single(reloaded.RouteConnectionWindows);
            Assert.True(reloaded.RouteConnectionWindows[0].EndpointAtDock.HasValue);
            Assert.Equal(
                BaseRootFlightId,
                reloaded.RouteConnectionWindows[0].EndpointAtDock.Value.RootPartUId);
        }

        // catches: the capture-side stamp not reaching the persisted STOP. A delivery stop
        // takes window.EndpointAtDock verbatim, so this is a guard on that verbatim copy
        // surviving the ROUTE codec as well.
        [Fact]
        public void TheStampReachesTheStopAndSurvivesTheRouteCodec()
        {
            var endpoint = new RouteEndpoint
            {
                VesselPersistentId = BasePid,
                RootPartUId = BaseRootFlightId,
                BodyName = "Mun",
                Latitude = 1.0,
                Longitude = 2.0,
                Altitude = 3.0,
                IsSurface = true,
            };

            var node = new ConfigNode("ENDPOINT");
            RouteNodeCodec.SerializeEndpoint(node, endpoint, CultureInfo.InvariantCulture);
            Assert.Equal(
                BaseRootFlightId.ToString(CultureInfo.InvariantCulture),
                node.GetValue("rootPartUId"));

            RouteEndpoint restored = RouteCodec.DeserializeEndpointForTesting(node);
            Assert.Equal(BaseRootFlightId, restored.RootPartUId);
        }

        // catches: the hash moving when a newly captured window starts carrying the stamp.
        // The endpoint root names the same vessel the endpoint pid already names, so hashing it
        // would flip every route whose recording is re-saved to SourceChanged.
        [Fact]
        public void TheEndpointStamp_DoesNotMoveTheRouteProofHash()
        {
            string unstamped = RouteProofHasher.ComputeRouteProofHashFromRecording(
                HashFixture(endpointRoot: 0u));
            string stamped = RouteProofHasher.ComputeRouteProofHashFromRecording(
                HashFixture(endpointRoot: BaseRootFlightId));

            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, unstamped);
            Assert.Equal(unstamped, stamped);
        }

        // ==================================================================
        // THE DEEP SCAN'S COST GATE
        // ==================================================================

        // catches: the part-set rebuild running on every frame for an endpoint that will never
        // be found, and the memo suppressing a scan that could now succeed.
        //
        // The pass-2 rebuild walks every part of every vessel and the step runs every frame
        // the Logistics window draws a route. "It already pays for the proximity build" is
        // true only for a SURFACE endpoint - proximityEligible gates on IsSurface - so an
        // ORBITAL endpoint whose depot is gone would walk every part, forever, with no
        // proximity step to reach. ShouldRunDeepRootScan bounds that. It is a MISS memo, so
        // every direction that could hide a findable root must run: a different endpoint, a
        // changed vessel count (what a dock or an undock IS), a rewound / unknown frame
        // counter, and the expiry.
        [Fact]
        public void ShouldRunDeepRootScan_SuppressesOnlyTheRepeatedIdenticalMiss()
        {
            var memo = new RouteEndpointResolver.DeepRootScanMemo
            {
                RootPartUId = BaseRootFlightId,
                VesselCount = 4,
                AtFrame = 1000,
            };
            const int budget = RouteEndpointResolver.DeepRootScanNegativeCacheFrames;

            // The one suppressed case: same endpoint, same roster, inside the budget.
            Assert.False(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 4, 1000 + budget - 1, memo, budget));
            Assert.False(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 4, 1000, memo, budget));

            // A DIFFERENT endpoint is not this miss.
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                VisitorRootFlightId, 4, 1001, memo, budget));
            // The roster changed - a dock (2 -> 1) or an undock (1 -> 2), which is exactly
            // when a root that was nobody's own becomes findable again.
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 3, 1001, memo, budget));
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 5, 1001, memo, budget));
            // Budget expired.
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 4, 1000 + budget, memo, budget));
            // Frame counter went backwards, or is the unknown-clock reading.
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 4, 999, memo, budget));
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 4, 0, memo, budget));
            // An empty memo never suppresses (root id 0 is "nothing recorded", and 0 is also
            // the id an endpoint with no stamped root carries - it must not memoize itself).
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 4, 1001,
                default(RouteEndpointResolver.DeepRootScanMemo), budget));
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                0u, 4, 1001, default(RouteEndpointResolver.DeepRootScanMemo), budget));
            // A memo whose own frame is the unknown reading is not trusted either.
            Assert.True(RouteEndpointResolver.ShouldRunDeepRootScan(
                BaseRootFlightId, 4, 10,
                new RouteEndpointResolver.DeepRootScanMemo
                {
                    RootPartUId = BaseRootFlightId,
                    VesselCount = 4,
                    AtFrame = 0,
                },
                budget));
        }

        private static Recording HashFixture(uint endpointRoot)
        {
            return new Recording
            {
                RecordingId = "rec-hash-endpoint-root",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-1",
                        DockUT = 100.0,
                        UndockUT = 200.0,
                        TransferTargetVesselPid = BasePid,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { 11u },
                        EndpointPartPersistentIds = new List<uint> { 33u },
                        EndpointAtDock = new RouteEndpoint
                        {
                            VesselPersistentId = BasePid,
                            RootPartUId = endpointRoot,
                            BodyName = "Mun",
                            IsSurface = true,
                        },
                    },
                },
            };
        }
    }
}
