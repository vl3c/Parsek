using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The endpoint-TRANSFER ruling (operator, 2026-09-04): a resolution that lands on a
    /// vessel other than the recorded one REBINDS the persisted endpoint, and the route's own
    /// transport is never a candidate.
    ///
    /// <para>Every cell here is headless. The decision, the transport predicate, the owner
    /// search, the transport collection and both formatters are pure; the rebind itself is
    /// driven through <see cref="RouteEndpointTransfer.ApplyTransfers"/> with owners built by
    /// the pure search, so no live <c>Vessel</c> is needed to prove the persisted field
    /// moved.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointTransferTests : System.IDisposable
    {
        private const string Body = "Kerbin";
        private const uint RecordedPid = 2123618197u;
        private const uint SubstitutePid = 2875537755u;
        private const uint TransportPid = 313889796u;

        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();

        public RouteEndpointTransferTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting = (msg, _) => screenMessages.Add(msg);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        private static RouteEndpoint RecordedEndpoint(uint pid = RecordedPid, uint rootId = 0u)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = pid,
                RootPartUId = rootId,
                BodyName = Body,
                Latitude = -0.0972,
                Longitude = -74.5577,
                Altitude = 75.2,
                IsSurface = true,
            };
        }

        private static RouteStop DeliveryStop(RouteEndpoint endpoint)
        {
            return new RouteStop
            {
                Endpoint = endpoint,
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 97.6 } },
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 100.0,
            };
        }

        private static RouteEndpointResolver.SurfaceVesselSnapshot Snap(
            uint pid, string guid, Vector3d position)
        {
            return new RouteEndpointResolver.SurfaceVesselSnapshot
            {
                PersistentId = pid,
                LaunchGuid = guid,
                BodyName = Body,
                Situation = Vessel.Situations.LANDED,
                WorldPosition = position,
                Vessel = null,
            };
        }

        // -----------------------------------------------------------------
        // The decision
        // -----------------------------------------------------------------

        // catches: a proximity resolution onto a DIFFERENT craft left as a silent
        // substitution (the pre-ruling behaviour RVR-18 measured).
        [Fact]
        public void Evaluate_ProximityOntoAnotherVessel_Transfers()
        {
            RouteEndpointTransfer.TransferDecision decision = RouteEndpointTransfer.Evaluate(
                RecordedPid, null, SubstitutePid, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                out string reason);

            Assert.Equal(RouteEndpointTransfer.TransferDecision.Transfer, decision);
            Assert.Equal("proximity-substitute", reason);
        }

        // catches: rebinding on the ORDINARY case - the same depot that merely drifted a few
        // metres still resolves by proximity, and must NOT be treated as a transfer.
        [Fact]
        public void Evaluate_ProximityOntoTheSameVessel_Keeps()
        {
            RouteEndpointTransfer.TransferDecision decision = RouteEndpointTransfer.Evaluate(
                RecordedPid, null, RecordedPid, null,
                RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                out string reason);

            Assert.Equal(RouteEndpointTransfer.TransferDecision.Keep, decision);
            Assert.Equal("proximity-same-vessel", reason);
        }

        // catches: the craft-baked-pid trap on the positional step - same pid, provably a
        // different launch, is a different physical vessel and so a transfer.
        [Fact]
        public void Evaluate_ProximitySamePidDifferentLaunch_Transfers()
        {
            RouteEndpointTransfer.TransferDecision decision = RouteEndpointTransfer.Evaluate(
                RecordedPid, "11111111111111111111111111111111",
                RecordedPid, "22222222222222222222222222222222",
                RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                out string reason);

            Assert.Equal(RouteEndpointTransfer.TransferDecision.Transfer, decision);
            Assert.Equal("proximity-different-launch", reason);
        }

        // catches: the MIRROR direction of the pid step. Same pid + guids that conclusively
        // differ is a different launch of the same craft file; same pid with either guid
        // unknown is the pid-only fallback and must stay a Keep.
        [Fact]
        public void Evaluate_PidStep_TransfersOnlyWhenLaunchesConclusivelyDiffer()
        {
            RouteEndpointTransfer.TransferDecision differs = RouteEndpointTransfer.Evaluate(
                RecordedPid, "11111111111111111111111111111111",
                RecordedPid, "22222222222222222222222222222222",
                RouteEndpointResolver.EndpointResolutionStep.Pid,
                out string differsReason);
            Assert.Equal(RouteEndpointTransfer.TransferDecision.Transfer, differs);
            Assert.Equal("pid-different-launch", differsReason);

            // The production shape today: a RouteEndpoint carries no launch guid, so the
            // recorded side is always unknown and the arm above cannot fire
            // (RESOLVER-PID-STEP-NOT-GUID-GATED).
            RouteEndpointTransfer.TransferDecision unknown = RouteEndpointTransfer.Evaluate(
                RecordedPid, null,
                RecordedPid, "22222222222222222222222222222222",
                RouteEndpointResolver.EndpointResolutionStep.Pid,
                out string unknownReason);
            Assert.Equal(RouteEndpointTransfer.TransferDecision.Keep, unknown);
            Assert.Equal("pid-same-launch", unknownReason);
        }

        // catches: a transfer fired off the IDENTITY step. The root-part step matches a
        // launch-unique flightID, so whatever pid it lands on is the recorded vessel - a
        // rebind there would rewrite a correct binding.
        [Fact]
        public void Evaluate_RootPartStep_NeverTransfers()
        {
            RouteEndpointTransfer.TransferDecision decision = RouteEndpointTransfer.Evaluate(
                RecordedPid, null, SubstitutePid, null,
                RouteEndpointResolver.EndpointResolutionStep.RootPart,
                out string reason);

            Assert.Equal(RouteEndpointTransfer.TransferDecision.Keep, decision);
            Assert.Equal("root-identity-match", reason);
        }

        // catches: rebinding onto nothing - a resolution with no pid must never clear the
        // recorded binding.
        [Fact]
        public void Evaluate_ResolvedPidUnknown_Keeps()
        {
            RouteEndpointTransfer.TransferDecision decision = RouteEndpointTransfer.Evaluate(
                RecordedPid, null, 0u, null,
                RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                out string reason);

            Assert.Equal(RouteEndpointTransfer.TransferDecision.Keep, decision);
            Assert.Equal("resolved-pid-unknown", reason);
        }

        // -----------------------------------------------------------------
        // The transport guard
        // -----------------------------------------------------------------

        // catches: a route paying its own carrier. Pid match with agreeing (or unknown) guids
        // is the transport; a conclusively different launch of the same craft file is not.
        [Fact]
        public void IsRouteTransport_GuidFirstPidFallback()
        {
            var transports = new List<RouteEndpointTransfer.VesselIdentity>
            {
                new RouteEndpointTransfer.VesselIdentity
                {
                    Pid = TransportPid,
                    LaunchGuid = "11111111111111111111111111111111",
                },
            };

            Assert.True(RouteEndpointTransfer.IsRouteTransport(
                TransportPid, "11111111111111111111111111111111", transports, out string hit));
            Assert.Equal("route-own-transport", hit);

            // Unknown live guid falls back to pid-only (never regress a legacy recording).
            Assert.True(RouteEndpointTransfer.IsRouteTransport(
                TransportPid, null, transports, out _));

            // A different LAUNCH of the same craft file is a different physical vessel.
            Assert.False(RouteEndpointTransfer.IsRouteTransport(
                TransportPid, "22222222222222222222222222222222", transports, out string missGuid));
            Assert.Equal(string.Empty, missGuid);

            // A different craft entirely.
            Assert.False(RouteEndpointTransfer.IsRouteTransport(
                SubstitutePid, "11111111111111111111111111111111", transports, out _));
        }

        // catches: the transport surviving into the proximity candidate set. RVR-18 measured
        // the transport as the runner-up at 16.42 m, so a resolver that only excluded ghosts
        // would hand a route its own cargo back.
        [Fact]
        public void SurfaceFallback_ExcludesTheRouteOwnTransport_NextNearestWins()
        {
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(TransportPid, "11111111111111111111111111111111", new Vector3d(16.42, 0, 0)),
                Snap(SubstitutePid, "33333333333333333333333333333333", new Vector3d(120.0, 0, 0)),
            };
            var transports = new List<RouteEndpointTransfer.VesselIdentity>
            {
                new RouteEndpointTransfer.VesselIdentity
                {
                    Pid = TransportPid,
                    LaunchGuid = "11111111111111111111111111111111",
                },
            };

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                new Vector3d(0, 0, 0), Body, snapshots,
                excludePids: null,
                excludeTransports: transports,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                vessel: out Vessel _,
                pickedPid: out uint pickedPid,
                pickedDistanceMeters: out double distance,
                transportCandidatesSkipped: out int skipped,
                reason: out string reason);

            Assert.True(ok, reason);
            // The NEAREST candidate was the transport; the next-nearest wins instead.
            Assert.Equal(SubstitutePid, pickedPid);
            Assert.Equal(120.0, distance, 3);
            Assert.Equal(1, skipped);
        }

        // catches: the guard degrading into a delivery when the transport is the ONLY vessel
        // parked at the recorded dock point. That must hold the route (EndpointLost), and the
        // miss must carry its own token so the hold reason says why.
        [Fact]
        public void SurfaceFallback_TransportIsTheOnlyCandidate_MissesWithItsOwnReason()
        {
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(TransportPid, "11111111111111111111111111111111", new Vector3d(16.42, 0, 0)),
            };
            var transports = new List<RouteEndpointTransfer.VesselIdentity>
            {
                new RouteEndpointTransfer.VesselIdentity
                {
                    Pid = TransportPid,
                    LaunchGuid = "11111111111111111111111111111111",
                },
            };

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                new Vector3d(0, 0, 0), Body, snapshots,
                excludePids: null,
                excludeTransports: transports,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                vessel: out Vessel _,
                pickedPid: out uint pickedPid,
                pickedDistanceMeters: out double distance,
                transportCandidatesSkipped: out int skipped,
                reason: out string reason);

            Assert.False(ok);
            Assert.Equal(0u, pickedPid);
            Assert.Equal(0.0, distance);
            Assert.Equal(1, skipped);
            Assert.Equal("no-candidate-after-transport-exclusion", reason);
        }

        // catches: the new overload changing the behaviour of the old one. With no transports
        // the search is byte-identical to the pre-ruling contract, including its reason token.
        [Fact]
        public void SurfaceFallback_NoTransports_UnchangedMissToken()
        {
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(SubstitutePid, null, new Vector3d(0, 0, 0)),
            };
            // Wrong body: the only candidate is filtered out before any transport test runs.
            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                new Vector3d(0, 0, 0), "Mun", snapshots,
                excludePids: null,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                out Vessel _, out uint pickedPid, out string reason);

            Assert.False(ok);
            Assert.Equal(0u, pickedPid);
            Assert.Equal("no-surface-candidate", reason);
        }

        // -----------------------------------------------------------------
        // Owners, roles, transports
        // -----------------------------------------------------------------

        // catches: a rebind that cannot find the field it must write. The resolver only ever
        // sees a COPY of the endpoint struct, so the owner search is the whole way back.
        [Fact]
        public void FindOwners_MatchesStopAndOriginAndLabelsTheRole()
        {
            RouteEndpoint endpoint = RecordedEndpoint();
            Route deliveryRoute = new RouteFixtureBuilder()
                .WithId("route-delivery")
                .WithStop(DeliveryStop(endpoint))
                .Build();
            Route originRoute = new RouteFixtureBuilder()
                .WithId("route-origin")
                .WithOrigin(endpoint)
                .Build();

            List<RouteEndpointTransfer.EndpointOwner> owners = RouteEndpointTransfer.FindOwners(
                new List<Route> { deliveryRoute, originRoute }, endpoint);

            Assert.Equal(2, owners.Count);
            Assert.Equal(RouteEndpointTransfer.EndpointRole.Destination, owners[0].Role);
            Assert.Equal(0, owners[0].StopIndex);
            Assert.Equal(RouteEndpointTransfer.EndpointRole.Origin, owners[1].Role);
            // Origin rows carry no stop index; the log line prints -1 there.
            Assert.Equal(-1, owners[1].StopIndex);
        }

        // catches: a value match loose enough to rebind the WRONG stop. A neighbouring depot
        // 100 m away shares the body and is not the same endpoint.
        [Fact]
        public void FindOwners_DifferentCoordinates_NoMatch()
        {
            RouteEndpoint recorded = RecordedEndpoint();
            RouteEndpoint neighbour = recorded;
            neighbour.Latitude += 0.001;

            Route route = new RouteFixtureBuilder()
                .WithId("route-neighbour")
                .WithStop(DeliveryStop(neighbour))
                .Build();

            Assert.Empty(RouteEndpointTransfer.FindOwners(new List<Route> { route }, recorded));
        }

        // catches: a pickup-source endpoint mislabelled as a delivery in the player-facing
        // message. A stop that only loads cargo reads "pickup"; a stop that does both is
        // labelled by its delivery direction.
        [Fact]
        public void ClassifyStopRole_PickupOnlyAndMixed()
        {
            var pickupOnly = new RouteStop
            {
                Endpoint = RecordedEndpoint(),
                PickupManifest = new Dictionary<string, double> { { "Ore", 42.0 } },
            };
            Assert.Equal(RouteEndpointTransfer.EndpointRole.Pickup,
                RouteEndpointTransfer.ClassifyStopRole(pickupOnly));

            var mixed = new RouteStop
            {
                Endpoint = RecordedEndpoint(),
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                PickupManifest = new Dictionary<string, double> { { "Ore", 42.0 } },
            };
            Assert.Equal(RouteEndpointTransfer.EndpointRole.Destination,
                RouteEndpointTransfer.ClassifyStopRole(mixed));
        }

        // catches: a transport set built from the wrong side, or one that duplicates the same
        // carrier once per chain member (a route's source refs are all segments of ONE flight).
        [Fact]
        public void CollectTransportIdentities_OneEntryPerCarrier()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-transports")
                .WithRecordingId("rec-1")
                .WithRecordingId("rec-2")
                .WithRecordingId("rec-missing")
                .WithSourceRef(new RouteSourceRef { RecordingId = "rec-1" })
                .WithSourceRef(new RouteSourceRef { RecordingId = "rec-2" })
                .WithSourceRef(new RouteSourceRef { RecordingId = "rec-missing" })
                .Build();

            var recordings = new Dictionary<string, Recording>
            {
                {
                    "rec-1",
                    new Recording
                    {
                        RecordingId = "rec-1",
                        VesselPersistentId = TransportPid,
                        RecordedVesselGuid = "11111111111111111111111111111111",
                    }
                },
                {
                    // Chain continuation of the SAME launch: same pid, same guid.
                    "rec-2",
                    new Recording
                    {
                        RecordingId = "rec-2",
                        VesselPersistentId = TransportPid,
                        RecordedVesselGuid = "11111111111111111111111111111111",
                    }
                },
            };

            List<RouteEndpointTransfer.VesselIdentity> transports =
                RouteEndpointTransfer.CollectTransportIdentities(
                    route,
                    id => recordings.TryGetValue(id, out Recording rec) ? rec : null);

            Assert.Single(transports);
            Assert.Equal(TransportPid, transports[0].Pid);
            Assert.Equal("11111111111111111111111111111111", transports[0].LaunchGuid);
        }

        // -----------------------------------------------------------------
        // The rebind, the log line, the message
        // -----------------------------------------------------------------

        // catches: the whole point of the ruling - a substitution that stays invisible. The
        // persisted endpoint must move, the Info line must be grep-stable, and exactly one
        // screen message must fire.
        [Fact]
        public void ApplyTransfers_RebindsTheStopAndLogsOnce()
        {
            RouteEndpoint recorded = RecordedEndpoint();
            RouteStop stop = DeliveryStop(recorded);
            Route route = new RouteFixtureBuilder()
                .WithId("68749916-aaaa-bbbb-cccc-ddddeeeeffff")
                .WithName("Mun Fuel Run")
                .WithStop(stop)
                .Build();

            List<RouteEndpointTransfer.EndpointOwner> owners =
                RouteEndpointTransfer.FindOwners(new List<Route> { route }, recorded);

            int rebound = RouteEndpointTransfer.ApplyTransfers(
                owners, recorded,
                resolvedPid: SubstitutePid,
                resolvedLaunchGuid: "33333333333333333333333333333333",
                resolvedName: "A",
                resolvedRootPartFlightId: 987654u,
                step: RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                distanceMeters: 1.0299,
                distanceKnown: true,
                ut: 1600.0);

            Assert.Equal(1, rebound);
            // The persisted identity moved; the recorded dock ANCHOR did not.
            Assert.Equal(SubstitutePid, route.Stops[0].Endpoint.VesselPersistentId);
            Assert.Equal(987654u, route.Stops[0].Endpoint.RootPartUId);
            Assert.Equal(recorded.Latitude, route.Stops[0].Endpoint.Latitude);
            Assert.Equal(recorded.Longitude, route.Stops[0].Endpoint.Longitude);
            Assert.Equal(recorded.Altitude, route.Stops[0].Endpoint.Altitude);
            Assert.Equal(Body, route.Stops[0].Endpoint.BodyName);

            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains(
                "Route endpoint transferred: route=68749916 stop=0 role=destination "
                + "from=2123618197/'<unknown>' to=2875537755/'A' step=proximity "
                + "distance=1.03 body=Kerbin at ut=1600"));
            Assert.Single(screenMessages);
            Assert.Contains("Mun Fuel Run", screenMessages[0]);
            Assert.Contains("now delivering to A", screenMessages[0]);
        }

        // catches: a rebind fired on a Keep decision (the same depot that drifted). Nothing
        // moves and nothing is announced.
        [Fact]
        public void ApplyTransfers_SameVessel_NoRebindNoMessage()
        {
            RouteEndpoint recorded = RecordedEndpoint();
            Route route = new RouteFixtureBuilder()
                .WithId("route-keep")
                .WithStop(DeliveryStop(recorded))
                .Build();

            List<RouteEndpointTransfer.EndpointOwner> owners =
                RouteEndpointTransfer.FindOwners(new List<Route> { route }, recorded);

            int rebound = RouteEndpointTransfer.ApplyTransfers(
                owners, recorded,
                resolvedPid: RecordedPid,
                resolvedLaunchGuid: null,
                resolvedName: "rover fuel 0",
                resolvedRootPartFlightId: 42u,
                step: RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                distanceMeters: 3.5,
                distanceKnown: true,
                ut: 1600.0);

            Assert.Equal(0, rebound);
            Assert.Equal(RecordedPid, route.Stops[0].Endpoint.VesselPersistentId);
            Assert.Equal(0u, route.Stops[0].Endpoint.RootPartUId);
            Assert.Empty(screenMessages);
            Assert.Contains(logLines, l => l.Contains("Endpoint transfer skipped")
                && l.Contains("reason=proximity-same-vessel"));
        }

        // catches: the ORIGIN / pickup mirror going unimplemented or reading as a delivery.
        // The ruling is about endpoints generally, so an origin resolved onto another craft
        // rebinds the same way - and the message says "loading from", not "delivering to".
        [Fact]
        public void ApplyTransfers_OriginRole_RebindsAndSaysLoadingFrom()
        {
            RouteEndpoint recorded = RecordedEndpoint();
            Route route = new RouteFixtureBuilder()
                .WithId("route-origin-transfer")
                .WithName("Depot Run")
                .WithOrigin(recorded)
                .Build();

            List<RouteEndpointTransfer.EndpointOwner> owners =
                RouteEndpointTransfer.FindOwners(new List<Route> { route }, recorded);

            int rebound = RouteEndpointTransfer.ApplyTransfers(
                owners, recorded,
                resolvedPid: SubstitutePid,
                resolvedLaunchGuid: null,
                resolvedName: "Depot Mk2",
                resolvedRootPartFlightId: 555u,
                step: RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                distanceMeters: 12.0,
                distanceKnown: true,
                ut: 3200.0);

            Assert.Equal(1, rebound);
            Assert.Equal(SubstitutePid, route.Origin.VesselPersistentId);
            Assert.Equal(555u, route.Origin.RootPartUId);
            Assert.Contains(logLines, l => l.Contains("role=origin") && l.Contains("stop=-1"));
            Assert.Single(screenMessages);
            Assert.Contains("now loading from Depot Mk2", screenMessages[0]);
        }

        // catches: culture-dependent formatting in a line a harness regex reads. The line is
        // asserted under a comma-decimal culture; every number must still print dots.
        [Fact]
        public void FormatTransferLine_IsInvariantUnderACommaDecimalCulture()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                string line = RouteEndpointTransfer.FormatTransferLine(
                    "68749916-aaaa", RouteEndpointTransfer.EndpointRole.Destination, 0,
                    RecordedPid, null, SubstitutePid, "A",
                    RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                    1.0299, true, Body, 1600.5);

                Assert.Equal(
                    "Route endpoint transferred: route=68749916 stop=0 role=destination "
                    + "from=2123618197/'<unknown>' to=2875537755/'A' step=proximity "
                    + "distance=1.03 body=Kerbin at ut=1600.5",
                    line);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // -----------------------------------------------------------------
        // Persistence
        // -----------------------------------------------------------------

        // catches: a rebind that lives only in RAM. The produced save must name the NEW
        // vessel - that is the surface the harness lane reads (destinationVesselPids).
        [Fact]
        public void ReboundStop_RoundTripsThroughTheCodec()
        {
            RouteEndpoint recorded = RecordedEndpoint();
            Route route = new RouteFixtureBuilder()
                .WithId("route-roundtrip")
                .WithName("Mun Fuel Run")
                .WithStop(DeliveryStop(recorded))
                .Build();

            RouteEndpointTransfer.ApplyTransfers(
                RouteEndpointTransfer.FindOwners(new List<Route> { route }, recorded),
                recorded,
                resolvedPid: SubstitutePid,
                resolvedLaunchGuid: null,
                resolvedName: "A",
                resolvedRootPartFlightId: 987654u,
                step: RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                distanceMeters: 1.03,
                distanceKnown: true,
                ut: 1600.0);

            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);

            // The STOP's ENDPOINT node names the substitute, on both identity keys.
            ConfigNode stopNode = node.GetNodes(RouteCodec.StopNode)[0];
            ConfigNode endpointNode = stopNode.GetNode(RouteCodec.EndpointNode);
            Assert.Equal("2875537755", endpointNode.GetValue("vesselPersistentId"));
            Assert.Equal("987654", endpointNode.GetValue("rootPartUId"));

            Route roundTripped = Route.DeserializeFrom(node);
            Assert.NotNull(roundTripped);
            Assert.Equal(SubstitutePid, roundTripped.Stops[0].Endpoint.VesselPersistentId);
            Assert.Equal(987654u, roundTripped.Stops[0].Endpoint.RootPartUId);
            // The historical anchor survived the trip unchanged.
            Assert.Equal(recorded.Latitude, roundTripped.Stops[0].Endpoint.Latitude);
            Assert.Equal(recorded.Longitude, roundTripped.Stops[0].Endpoint.Longitude);
            Assert.Equal(recorded.Altitude, roundTripped.Stops[0].Endpoint.Altitude);
            Assert.True(roundTripped.Stops[0].Endpoint.IsSurface);
        }
    }
}
