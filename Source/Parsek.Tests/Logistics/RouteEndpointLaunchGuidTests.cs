using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Closes RESOLVER-PID-STEP-NOT-GUID-GATED: <see cref="RouteEndpointResolver"/>'s PID step
    /// matched a bare <c>persistentId</c>, which is craft-baked and reused verbatim on every
    /// launch of the same <c>.craft</c>, so it could accept a DIFFERENT launch standing where
    /// the depot was. The endpoint now carries the launch guid the bind read when it stamped
    /// that pid (<see cref="RouteEndpoint.LaunchGuid"/>), and the step is gated with
    /// <see cref="VesselLaunchIdentity.GuidsConclusivelyDiffer"/>.
    ///
    /// <para>THREE DIRECTIONS, and all three are cells here because the middle one is the
    /// whole risk: matching guids must ACCEPT, conclusively different guids must REFUSE, and
    /// an UNKNOWN guid on either side must accept - unknown is "no evidence", never "differs"
    /// (CLAUDE.md's identity rule). A gate that refused on unknown would break every route
    /// built before the key existed.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointLaunchGuidTests : System.IDisposable
    {
        private const string GuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string GuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        public RouteEndpointLaunchGuidTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // 1. THE GATE ITSELF
        // ==================================================================

        // catches: the gate accepting a same-craft sibling from another launch, which is the
        // defect - a pid match alone is not an identity match.
        [Fact]
        public void PidGuidGate_RefusesOnlyWhenBothGuidsAreKnownAndDiffer()
        {
            Assert.Equal("different-launch",
                RouteEndpointResolver.ClassifyPidGuidGate(GuidA, GuidB));

            Assert.Equal("same-launch",
                RouteEndpointResolver.ClassifyPidGuidGate(GuidA, GuidA));
        }

        // catches: the MIRROR direction - a gate that refuses on unknown evidence. Every route
        // built before the key existed carries no recorded guid, and a live vessel mid-teardown
        // can fail to yield one; refusing either would strand those endpoints on proximity (or
        // on nothing at all, for an endpoint that is not surface-eligible).
        [Theory]
        [InlineData(null, GuidB, "unknown-recorded")]
        [InlineData("", GuidB, "unknown-recorded")]
        [InlineData(GuidA, null, "unknown-live")]
        [InlineData(GuidA, "", "unknown-live")]
        [InlineData(null, null, "unknown-recorded")]
        public void PidGuidGate_AcceptsWheneverEitherSideIsUnknown(
            string recorded, string live, string expected)
        {
            Assert.Equal(expected, RouteEndpointResolver.ClassifyPidGuidGate(recorded, live));
            Assert.False(VesselLaunchIdentity.GuidsConclusivelyDiffer(recorded, live));
        }

        // catches: an unnormalized comparison. The persisted guid is normalized on read and the
        // live one is read as "N"-format, so a formatting difference must not read as a
        // different launch.
        [Fact]
        public void PidGuidGate_IsNormalizationInsensitive()
        {
            Assert.Equal("same-launch", RouteEndpointResolver.ClassifyPidGuidGate(
                "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", GuidA));
        }

        // ==================================================================
        // 2. THE PERSISTED KEY
        // ==================================================================

        // catches: the guid being decided and then dropped by the codec - the gate would be
        // live in-session and dead on every reload, which is exactly when a route needs it.
        [Fact]
        public void EndpointLaunchGuid_RoundTripsThroughTheRouteCodec()
        {
            var endpoint = new RouteEndpoint
            {
                VesselPersistentId = 4242u,
                LaunchGuid = GuidA,
                BodyName = "Mun",
                Latitude = 1.0,
                Longitude = 2.0,
                Altitude = 3.0,
                IsSurface = true,
            };

            var node = new ConfigNode("ORIGIN");
            RouteNodeCodec.SerializeEndpoint(node, endpoint, CultureInfo.InvariantCulture);
            Assert.Equal(GuidA, node.GetValue("launchGuid"));

            RouteEndpoint restored = RouteCodec.DeserializeEndpointForTesting(node);
            Assert.Equal(GuidA, restored.LaunchGuid);
        }

        // catches: a non-sparse write. An endpoint with no guid must omit the key entirely, or
        // every pre-existing route's ROUTE node changes bytes on the next save.
        [Fact]
        public void EndpointWithoutAGuid_OmitsTheKey_AndReadsBackUnknown()
        {
            var endpoint = new RouteEndpoint
            {
                VesselPersistentId = 99u,
                BodyName = "Kerbin",
                IsSurface = true,
            };

            var node = new ConfigNode("ORIGIN");
            RouteNodeCodec.SerializeEndpoint(node, endpoint, CultureInfo.InvariantCulture);
            Assert.False(node.HasValue("launchGuid"));

            RouteEndpoint restored = RouteCodec.DeserializeEndpointForTesting(node);
            Assert.True(string.IsNullOrEmpty(restored.LaunchGuid));
        }

        // catches: the proof-side key being written but not read - the origin endpoint's guid
        // comes off the proof, so a one-way codec would silently un-gate every reloaded route.
        [Fact]
        public void OriginProofLaunchGuid_RoundTripsAndIsSparseWhenAbsent()
        {
            var recording = new Recording
            {
                RecordingId = "rec-guid",
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 8888u,
                    StartDockedOriginVesselGuid = GuidA,
                    StartDockedOriginRootPartUId = 777u,
                },
            };
            var node = new ConfigNode("RECORDING");
            RouteProofCodec.SerializeRouteProofMetadata(node, recording);

            var reloaded = new Recording { RecordingId = "rec-guid" };
            RouteProofCodec.DeserializeRouteProofMetadata(node, reloaded);
            Assert.Equal(GuidA, reloaded.RouteOriginProof.StartDockedOriginVesselGuid);
            Assert.Equal(GuidA, reloaded.RouteOriginProof.DeepClone().StartDockedOriginVesselGuid);

            // SPARSE: a proof whose bind had no guid evidence writes no key at all. Zero-length
            // is the unknown reading and an emitted empty value would be a second spelling of
            // it for every future reader.
            var noGuid = new Recording
            {
                RecordingId = "rec-no-guid",
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 8888u,
                    StartDockedOriginRootPartUId = 777u,
                },
            };
            var sparseNode = new ConfigNode("RECORDING");
            RouteProofCodec.SerializeRouteProofMetadata(sparseNode, noGuid);
            Assert.False(sparseNode.GetNode("ROUTE_ORIGIN_PROOF").HasValue("startDockedOriginVesselGuid"));

            var sparseReloaded = new Recording { RecordingId = "rec-no-guid" };
            RouteProofCodec.DeserializeRouteProofMetadata(sparseNode, sparseReloaded);
            Assert.True(string.IsNullOrEmpty(
                sparseReloaded.RouteOriginProof.StartDockedOriginVesselGuid));
        }

        // ==================================================================
        // 3. THE HASH MUST NOT MOVE
        // ==================================================================

        // catches: the guid leaking into the proof fingerprint. It names the SAME vessel the
        // pid already names and is written by a site younger than the proofs it lands on, so
        // hashing it would flip every existing route to SourceChanged the first time its
        // recording is re-saved with the field populated.
        [Fact]
        public void RouteProofHash_IsUnchangedByTheLaunchGuidOnEitherSurface()
        {
            Recording without = BuildHashFixture(originGuid: null, endpointGuid: null);
            Recording withProofGuid = BuildHashFixture(originGuid: GuidA, endpointGuid: null);
            Recording withEndpointGuid = BuildHashFixture(originGuid: null, endpointGuid: GuidA);
            Recording withBoth = BuildHashFixture(originGuid: GuidA, endpointGuid: GuidB);

            string baseline = RouteProofHasher.ComputeRouteProofHashFromRecording(without);
            Assert.NotEqual(RouteProofHasher.NoRouteProofSentinel, baseline);
            Assert.Equal(baseline, RouteProofHasher.ComputeRouteProofHashFromRecording(withProofGuid));
            Assert.Equal(baseline, RouteProofHasher.ComputeRouteProofHashFromRecording(withEndpointGuid));
            Assert.Equal(baseline, RouteProofHasher.ComputeRouteProofHashFromRecording(withBoth));
        }

        private static Recording BuildHashFixture(string originGuid, string endpointGuid)
        {
            return new Recording
            {
                RecordingId = "rec-hash",
                RouteConnectionWindows = new List<RouteConnectionWindow>
                {
                    new RouteConnectionWindow
                    {
                        WindowId = "win-1",
                        DockUT = 100.0,
                        UndockUT = 200.0,
                        TransferTargetVesselPid = 4242u,
                        TransferKind = RouteConnectionKind.DockingPort,
                        TransportPartPersistentIds = new List<uint> { 11u },
                        EndpointPartPersistentIds = new List<uint> { 33u },
                        EndpointAtDock = new RouteEndpoint
                        {
                            VesselPersistentId = 4242u,
                            RootPartUId = 555u,
                            LaunchGuid = endpointGuid,
                            BodyName = "Mun",
                            IsSurface = true,
                        },
                    },
                },
                RouteOriginProof = new RouteOriginProof
                {
                    StartDockedOriginVesselPid = 8888u,
                    StartDockedOriginVesselGuid = originGuid,
                    StartDockedOriginRootPartUId = 777u,
                },
            };
        }

        // ==================================================================
        // 4. THE TRANSFER SIDE - the mirror the fix has to be walked in
        // ==================================================================

        // catches: a rebound endpoint keeping the OLD vessel's guid beside the NEW pid, which
        // would make every future pid match on that endpoint read different-launch and refuse
        // the very vessel the transfer just chose.
        [Fact]
        public void ATransferStampsTheResolvedVesselsGuidOntoTheReboundEndpoint()
        {
            var recorded = new RouteEndpoint
            {
                VesselPersistentId = 100u,
                RootPartUId = 10u,
                LaunchGuid = GuidA,
                BodyName = "Mun",
                IsSurface = true,
            };
            var route = new Route
            {
                Id = "route-1",
                Name = "Depot run",
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = recorded,
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                    },
                },
            };

            List<RouteEndpointTransfer.EndpointOwner> owners =
                RouteEndpointTransfer.FindOwners(new List<Route> { route }, recorded);
            Assert.Single(owners);

            int rebound = RouteEndpointTransfer.ApplyTransfers(
                owners, recorded,
                resolvedPid: 200u,
                resolvedLaunchGuid: GuidB,
                resolvedName: "Depot Mk2",
                resolvedRootPartFlightId: 20u,
                step: RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                distanceMeters: 12.5,
                distanceKnown: true,
                ut: 500.0);

            Assert.Equal(1, rebound);
            Assert.Equal(200u, route.Stops[0].Endpoint.VesselPersistentId);
            Assert.Equal(20u, route.Stops[0].Endpoint.RootPartUId);
            Assert.Equal(GuidB, route.Stops[0].Endpoint.LaunchGuid);

            // The rebound value must no longer match the pre-transfer copy, or a caller
            // re-resolving a stale struct would find the owner again and rebind twice.
            Assert.False(RouteEndpointTransfer.SameEndpoint(recorded, route.Stops[0].Endpoint));
        }

        // catches: an unreadable resolved guid leaving the STALE one in place. Clearing it is
        // the fail-open reading (unknown = no evidence); keeping the old one would be evidence
        // about a vessel that is gone.
        [Fact]
        public void ATransferWithAnUnreadableGuid_ClearsTheStaleOneRatherThanKeepingIt()
        {
            var recorded = new RouteEndpoint
            {
                VesselPersistentId = 100u,
                RootPartUId = 10u,
                LaunchGuid = GuidA,
                BodyName = "Mun",
                IsSurface = true,
            };
            var route = new Route
            {
                Id = "route-2",
                Stops = new List<RouteStop> { new RouteStop { Endpoint = recorded } },
            };

            int rebound = RouteEndpointTransfer.ApplyTransfers(
                RouteEndpointTransfer.FindOwners(new List<Route> { route }, recorded),
                recorded,
                resolvedPid: 200u,
                resolvedLaunchGuid: null,
                resolvedName: "Depot Mk2",
                resolvedRootPartFlightId: 0u,
                step: RouteEndpointResolver.EndpointResolutionStep.SurfaceProximity,
                distanceMeters: 4.0,
                distanceKnown: true,
                ut: 600.0);

            Assert.Equal(1, rebound);
            Assert.True(string.IsNullOrEmpty(route.Stops[0].Endpoint.LaunchGuid));
        }

        // catches: SameEndpoint ignoring the new field, which would let an owner lookup match
        // two endpoints that name different launches of the same craft at the same coordinates.
        [Fact]
        public void SameEndpoint_DistinguishesOnTheLaunchGuid()
        {
            var a = new RouteEndpoint
            {
                VesselPersistentId = 100u, RootPartUId = 10u, LaunchGuid = GuidA,
                BodyName = "Mun", Latitude = 1.0, Longitude = 2.0, Altitude = 3.0, IsSurface = true,
            };
            RouteEndpoint b = a;
            b.LaunchGuid = GuidB;
            RouteEndpoint unknown = a;
            unknown.LaunchGuid = null;

            Assert.True(RouteEndpointTransfer.SameEndpoint(a, a));
            Assert.False(RouteEndpointTransfer.SameEndpoint(a, b));
            Assert.False(RouteEndpointTransfer.SameEndpoint(a, unknown));
        }

        // catches: the transfer decision still being fed a hardcoded null recorded guid. With
        // the endpoint's own guid threaded, Evaluate's pid arm can now separate a
        // different-launch pid match from a same-launch one.
        [Fact]
        public void EvaluateSeesTheEndpointsOwnGuid_SoThePidArmSeparatesTheTwoLaunches()
        {
            Assert.Equal(
                RouteEndpointTransfer.TransferDecision.Transfer,
                RouteEndpointTransfer.Evaluate(
                    100u, GuidA, 100u, GuidB,
                    RouteEndpointResolver.EndpointResolutionStep.Pid, out string differing));
            Assert.Equal("pid-different-launch", differing);

            Assert.Equal(
                RouteEndpointTransfer.TransferDecision.Keep,
                RouteEndpointTransfer.Evaluate(
                    100u, GuidA, 100u, GuidA,
                    RouteEndpointResolver.EndpointResolutionStep.Pid, out string same));
            Assert.Equal("pid-same-launch", same);

            // Unknown recorded guid: the pre-fix reading, and still a Keep.
            Assert.Equal(
                RouteEndpointTransfer.TransferDecision.Keep,
                RouteEndpointTransfer.Evaluate(
                    100u, null, 100u, GuidB,
                    RouteEndpointResolver.EndpointResolutionStep.Pid, out string unknown));
            Assert.Equal("pid-same-launch", unknown);
        }

        // ==================================================================
        // 5. THE STEP THE GATE ACTUALLY SITS IN
        // ==================================================================

        // catches: the refusal being decided somewhere other than the classifier, and the
        // refusal arm being deleted.
        //
        // WHY THIS CELL EXISTS. The gate's four readings were pinned as a pure function
        // (section 1) and the production step then re-tested GuidsConclusivelyDiffer inline
        // beside it, so ClassifyPidGuidGate was load-bearing only for a LOG TOKEN: deleting
        // the inline refusal left the whole suite green. DecidePidStep is now the step's whole
        // decision and the caller only dispatches on it, so the refusal is reachable headlessly
        // - mutate the classifier and this reds.
        [Fact]
        public void DecidePidStep_RefusesADifferentLaunch_AndAcceptsEveryOtherReading()
        {
            Assert.Equal(
                RouteEndpointResolver.PidStepOutcome.RefuseDifferentLaunch,
                RouteEndpointResolver.DecidePidStep(true, GuidA, GuidB));

            Assert.Equal(
                RouteEndpointResolver.PidStepOutcome.Accept,
                RouteEndpointResolver.DecidePidStep(true, GuidA, GuidA));
            // Unknown on either side is "no evidence", never "differs" - the reading that
            // carries every route built before the key existed.
            Assert.Equal(
                RouteEndpointResolver.PidStepOutcome.Accept,
                RouteEndpointResolver.DecidePidStep(true, null, GuidB));
            Assert.Equal(
                RouteEndpointResolver.PidStepOutcome.Accept,
                RouteEndpointResolver.DecidePidStep(true, GuidA, null));

            // No live vessel at all is its own outcome, not a refusal: the log must not
            // announce a different launch when nothing was compared.
            Assert.Equal(
                RouteEndpointResolver.PidStepOutcome.NoCandidate,
                RouteEndpointResolver.DecidePidStep(false, GuidA, GuidB));
            Assert.Equal(
                RouteEndpointResolver.PidStepOutcome.NoCandidate,
                RouteEndpointResolver.DecidePidStep(false, null, null));

            // The refusal token and the refusing reading are the same fact.
            Assert.Equal(
                RouteEndpointResolver.PidGuidGateDifferentLaunch,
                RouteEndpointResolver.ClassifyPidGuidGate(GuidA, GuidB));
        }

        // catches: a refusal being treated as a dead end instead of a fall-through. Drives the
        // SAME pure walk production drives (ResolveEndpointStepPure over NextEndpointStep) with
        // pidMatches taken from DecidePidStep, so the guid decision and the step order are
        // composed here exactly as they are in TryResolveEndpoint.
        //
        // The expected step travels as its NAME rather than the enum value: the enum is
        // internal, and an internal parameter type on a public xUnit Theory does not compile.
        [Theory]
        // root known but gone, pid matches a DIFFERENT launch, surface endpoint -> proximity
        [InlineData(true, false, GuidA, GuidB, true, "SurfaceProximity")]
        // same pair, ORBITAL endpoint (no proximity step at all) -> nothing resolves
        [InlineData(true, false, GuidA, GuidB, false, "None")]
        // same launch -> the pid step wins and proximity is never reached
        [InlineData(true, false, GuidA, GuidA, true, "Pid")]
        // unknown live guid -> accepts, same as before the gate existed
        [InlineData(true, false, GuidA, null, true, "Pid")]
        // the root still resolves -> identity wins and the gate is never consulted
        [InlineData(true, true, GuidA, GuidB, true, "RootPart")]
        public void RefusedPidStep_FallsThroughToTheNextStep(
            bool rootIdKnown, bool rootMatches, string recordedGuid, string liveGuid,
            bool proximityEligible, string expectedStep)
        {
            bool pidMatches =
                RouteEndpointResolver.DecidePidStep(true, recordedGuid, liveGuid)
                    == RouteEndpointResolver.PidStepOutcome.Accept;

            RouteEndpointResolver.EndpointResolutionStep resolved =
                RouteEndpointResolver.ResolveEndpointStepPure(
                    rootIdKnown, rootMatches,
                    pidKnown: true, pidMatches: pidMatches,
                    proximityEligible: proximityEligible, proximityMatches: true);

            Assert.Equal(expectedStep, resolved.ToString());
        }
    }
}
