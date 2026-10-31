using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.SaveRoundTrip
{
    /// <summary>
    /// Field-level save/load round-trip of a supply <see cref="Route"/> through
    /// <see cref="RouteCodec"/> (the ROUTE node) and the shared endpoint writer in
    /// <see cref="RouteNodeCodec"/>. Every instance field of the route and of every type nested
    /// in it (stops, endpoints, source refs, manifests, inventory payloads, harvest caps,
    /// adopted endpoint parts) must come back equal unless <see cref="Specs"/> classifies it.
    /// Runs under a comma-decimal culture, so a culture-sensitive number format reds too.
    /// </summary>
    [Collection("Sequential")]
    [SaveRoundTripCovers(typeof(Route))]
    [SaveRoundTripCovers(typeof(RouteCodec))]
    [SaveRoundTripCovers(typeof(RouteNodeCodec))]
    public class RouteSaveRoundTripTests : IDisposable
    {
        public RouteSaveRoundTripTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private const string RuntimeCache =
            "runtime cache re-derived from persisted route data on the first build after a load "
            + "(RouteBackingMission.BuildMission / RouteOrchestrator.ResolveLoopUnit compare a "
            + "signature that is null after a load)";

        /// <summary>The logistics FieldSpecs, shared with the store-level round-trip.</summary>
        internal static FieldSpec[] Specs()
        {
            int partCounter = 0;
            return new[]
            {
                new FieldSpec(typeof(Route))
                    .NotPersisted(RuntimeCache,
                        "AutoExcludedNewIntervalKeys", "AutoExcludeTopologySignature",
                        "CachedLoopUnit", "LoopUnitBuilderSignature", "LoopUnitTopologySignature")
                    .Then(o => SortInventory(((Route)o).InventoryCostManifest)),
                new FieldSpec(typeof(RouteStop))
                    .Then(o =>
                    {
                        var s = (RouteStop)o;
                        SortInventory(s.InventoryDeliveryManifest);
                        SortInventory(s.InventoryPickupManifest);
                    }),
                // The codec normalizes a launch guid on read, so the filler writes a normalized one.
                new FieldSpec(typeof(RouteEndpoint))
                    .Fill("LaunchGuid", () => NewNormalizedGuid()),
                new FieldSpec(typeof(RouteEndpointAdoptedParts))
                    .Fill("EndpointLaunchGuid", () => NewNormalizedGuid()),
                // Load recomputes identityHash from the item's own STOREDPART snapshot (the kind
                // key), so the filler writes a real snapshot and the hash that snapshot yields.
                new FieldSpec(typeof(InventoryPayloadItem))
                    .Fill("StoredPartSnapshot", () =>
                    {
                        var n = new ConfigNode("STOREDPART");
                        n.AddValue("partName", "fillPart" + (++partCounter).ToString(CultureInfo.InvariantCulture));
                        return n;
                    })
                    .Then(o =>
                    {
                        var item = (InventoryPayloadItem)o;
                        item.IdentityHash = VesselSnapshotOps.ComputeInventoryPayloadKindKey(item.StoredPartSnapshot);
                    }),
                new FieldSpec(typeof(RouteEvent))
                    .Then(o =>
                    {
                        var e = (RouteEvent)o;
                        SortInventory(e.RouteInventoryManifest);
                        SortInventory(e.RouteRequestedInventoryManifest);
                    }),
            };
        }

        private static int guidCounter;

        private static string NewNormalizedGuid() =>
            new Guid(++guidCounter, 0x4321, 0x0765, 9, 8, 7, 6, 5, 4, 3, 2).ToString("N", CultureInfo.InvariantCulture);

        // A loaded inventory manifest is sorted by kind key (VesselSnapshotOps
        // .NormalizeLoadedInventoryPayloadItems), so the written one is too.
        private static void SortInventory(List<InventoryPayloadItem> items)
        {
            items?.Sort((a, b) => string.CompareOrdinal(a?.IdentityHash ?? "", b?.IdentityHash ?? ""));
        }

        private static Route RoundTrip(Route written)
        {
            var node = new ConfigNode("ROUTE");
            written.SerializeInto(node);
            return Route.DeserializeFrom(node);
        }

        [Fact]
        public void Route_EveryFieldRoundTrips()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(Specs(), seed: 100);
                var written = rt.Build<Route>();

                Route read = RoundTrip(written);

                Assert.NotNull(read);
                var problems = rt.Diff(written, read, "Route");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("Route", problems));
            });
        }

        [Fact]
        public void Route_FillerReachesEveryNestedType()
        {
            // The round-trip only proves what the filler populated: pin that every nested
            // collection the codec writes is non-empty in the written route.
            var rt = new FieldRoundTrip(Specs(), seed: 200);
            var r = rt.Build<Route>();
            Assert.NotEmpty(r.Stops);
            Assert.NotEmpty(r.SourceRefs);
            Assert.NotEmpty(r.HarvestCaps);
            Assert.NotEmpty(r.CostManifest);
            Assert.NotEmpty(r.InventoryCostManifest);
            Assert.NotEmpty(r.InventoryCostManifest[0].StoredResources);
            Assert.NotEmpty(r.AdoptedEndpointParts);
            Assert.NotEmpty(r.AdoptedEndpointParts[0].PartFlightIds);
            Assert.NotEmpty(r.Stops[0].DeliveryManifest);
            Assert.NotEmpty(r.Stops[0].PickupManifest);
            Assert.NotEmpty(r.Stops[0].InventoryDeliveryManifest);
            Assert.NotEmpty(r.Stops[0].InventoryPickupManifest);
            Assert.NotEmpty(r.ExcludedIntervalKeys);
            Assert.NotEmpty(r.CreationTreeRecordingIds);
            Assert.NotEqual(0u, r.Origin.VesselPersistentId);
            Assert.NotEqual(0u, r.Origin.RootPartUId);
        }

        public static IEnumerable<object[]> Statuses() =>
            Enum.GetValues(typeof(RouteStatus)).Cast<object>().Select(v => new object[] { Convert.ToInt32(v) });

        [Theory]
        [MemberData(nameof(Statuses))]
        public void Route_EveryStatus_RoundTripsAsStatusAndPreMissingStatus(int statusValue)
        {
            var status = (RouteStatus)statusValue;
            var rt = new FieldRoundTrip(Specs(), seed: 300);
            var written = rt.Build<Route>();
            written.Status = status;
            written.PreMissingStatus = status;
            Route read = RoundTrip(written);
            Assert.Equal(status, read.Status);
            Assert.Equal(status, read.PreMissingStatus);
        }

        public static IEnumerable<object[]> HoldKinds() =>
            Enum.GetValues(typeof(RouteDispatchEvaluator.EligibilityFailureKind)).Cast<object>()
                .Select(v => new object[] { Convert.ToInt32(v) });

        [Theory]
        [MemberData(nameof(HoldKinds))]
        public void Route_EveryHoldKind_RoundTrips(int kindValue)
        {
            var kind = (RouteDispatchEvaluator.EligibilityFailureKind)kindValue;
            var rt = new FieldRoundTrip(Specs(), seed: 400);
            var written = rt.Build<Route>();
            written.LastHoldKind = kind;
            Assert.Equal(kind, RoundTrip(written).LastHoldKind);
        }

        public static IEnumerable<object[]> ConnectionKinds() =>
            Enum.GetValues(typeof(RouteConnectionKind)).Cast<object>().Select(v => new object[] { Convert.ToInt32(v) });

        [Theory]
        [MemberData(nameof(ConnectionKinds))]
        public void Stop_EveryConnectionKind_RoundTrips(int kindValue)
        {
            var kind = (RouteConnectionKind)kindValue;
            var rt = new FieldRoundTrip(Specs(), seed: 500);
            var written = rt.Build<Route>();
            written.Stops[0].ConnectionKind = kind;
            Assert.Equal(kind, RoundTrip(written).Stops[0].ConnectionKind);
        }

        [Fact]
        public void Endpoint_EveryFieldRoundTripsThroughTheSharedWriter()
        {
            FieldRoundTrip.UnderCommaCulture(() =>
            {
                var rt = new FieldRoundTrip(Specs(), seed: 600);
                var written = (RouteEndpoint)rt.Build(typeof(RouteEndpoint));
                var node = new ConfigNode("ENDPOINT");
                RouteNodeCodec.SerializeEndpoint(node, written, CultureInfo.InvariantCulture);
                RouteEndpoint read = RouteCodec.DeserializeEndpointForTesting(node);
                var problems = rt.Diff(written, read, "RouteEndpoint");
                Assert.True(problems.Count == 0, FieldRoundTrip.Report("RouteEndpoint", problems));
            });
        }

        [Fact]
        public void Harness_DetectsAFieldTheCodecDrops()
        {
            // Proves the gate can fail: a ROUTE node with one value removed must be reported.
            var rt = new FieldRoundTrip(Specs(), seed: 700);
            var written = rt.Build<Route>();
            var node = new ConfigNode("ROUTE");
            written.SerializeInto(node);
            node.RemoveValue("skippedCycles");
            node.GetNodes(RouteCodec.StopNode)[0].RemoveValue("deliveryOffsetSeconds");
            Route read = Route.DeserializeFrom(node);
            var problems = rt.Diff(written, read, "Route");
            Assert.Contains(problems, p => p.StartsWith("Route.SkippedCycles:", StringComparison.Ordinal));
            Assert.Contains(problems, p => p.StartsWith("Route.Stops[0].DeliveryOffsetSeconds:", StringComparison.Ordinal));
        }
    }
}
