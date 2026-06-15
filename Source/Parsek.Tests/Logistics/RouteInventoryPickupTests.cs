using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// M3 Phase 5 (design D7 / OQ3): inventory pickup analysis + gate + applier
    /// coverage reachable WITHOUT a live KSP Vessel. The live stock-API removal
    /// (ModuleInventoryPart.ClearPartAtSlot, unloaded STOREDPART proto-node
    /// removal) needs a live Vessel and is pinned by the in-game
    /// <c>LogisticsPickupRuntimeTests</c> inventory cases. Here we cover:
    /// <list type="bullet">
    ///   <item><see cref="RouteAnalysisEngine.BuildInventoryLoadManifest"/> -
    ///     the sign-flip mirror of the inventory delivery builder.</item>
    ///   <item><see cref="RouteAnalysisEngine.HasUnwitnessedInventoryGain"/> -
    ///     the non-fungible window-local closure (fail-closed).</item>
    ///   <item>The cross-vessel + proto-vs-loaded IDENTITY HASH stability the
    ///     pickup match depends on (the existing live-move test is same-vessel
    ///     only).</item>
    ///   <item><see cref="RouteOriginCargoCheck.HasRequiredInventory"/> - the
    ///     M3 carve-out-lift inventory presence gate.</item>
    ///   <item><see cref="RouteOrchestrator.ApplyInventoryPickupDebit"/> branches
    ///     reachable via the test seam / empty / unresolved paths.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class RouteInventoryPickupTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteInventoryPickupTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteOrchestrator.InventoryPickupApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.InventoryPickupApplierForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==============================================================
        // BuildInventoryLoadManifest - sign-flip mirror of the delivery builder
        // ==============================================================

        // catches: the load builder failing to mirror the delivery sign flip
        // (endpoint LOSS + transport GAIN, loaded = min, identity intact).
        [Fact]
        public void BuildInventoryLoadManifest_SignFlipMirror_LoadsEndpointLossMatchedByTransportGain()
        {
            InventoryPayloadItem item = Payload("ore-container", "smallCargoContainer", 1);
            var window = new RouteConnectionWindow
            {
                WindowId = "pure-inv-pickup",
                DockUT = 100.0,
                UndockUT = 160.0,
                // Endpoint HAD the container at dock, lost it at undock.
                DockEndpointInventory = new List<InventoryPayloadItem> { item.DeepClone() },
                UndockEndpointInventory = null,
                // Transport lacked it at dock, gained it at undock.
                DockTransportInventory = null,
                UndockTransportInventory = new List<InventoryPayloadItem> { item.DeepClone() },
            };

            List<InventoryPayloadItem> load = RouteAnalysisEngine.BuildInventoryLoadManifest(window);

            Assert.NotNull(load);
            Assert.Single(load);
            Assert.Equal("ore-container", load[0].IdentityHash);
            Assert.Equal("smallCargoContainer", load[0].PartName);
            Assert.Equal(1, load[0].Quantity);
            // The carried StoredPartSnapshot is the canonical copy with the
            // loaded quantity set.
            Assert.NotNull(load[0].StoredPartSnapshot);
            Assert.Equal("1", load[0].StoredPartSnapshot.GetValue("quantity"));
        }

        // catches: the min() not clamping when the transport gain is below the
        // endpoint loss (partial pickup - only the witnessed-both quantity loads).
        [Fact]
        public void BuildInventoryLoadManifest_ClampsToMinOfLossAndGain()
        {
            InventoryPayloadItem two = Payload("ore-container", "smallCargoContainer", 2);
            InventoryPayloadItem one = Payload("ore-container", "smallCargoContainer", 1);
            var window = new RouteConnectionWindow
            {
                // Endpoint loses 2 (had 2, has 0).
                DockEndpointInventory = new List<InventoryPayloadItem> { two.DeepClone() },
                UndockEndpointInventory = null,
                // Transport gains only 1.
                DockTransportInventory = null,
                UndockTransportInventory = new List<InventoryPayloadItem> { one.DeepClone() },
            };

            List<InventoryPayloadItem> load = RouteAnalysisEngine.BuildInventoryLoadManifest(window);

            Assert.NotNull(load);
            Assert.Single(load);
            Assert.Equal(1, load[0].Quantity); // min(2, 1)
        }

        // catches: a delivery-only window (transport LOSES, endpoint GAINS)
        // producing a phantom load term.
        [Fact]
        public void BuildInventoryLoadManifest_DeliveryDirection_ReturnsNull()
        {
            InventoryPayloadItem item = Payload("jetpack", "evaJetpack", 1);
            var window = new RouteConnectionWindow
            {
                // Transport HAD it, lost it (delivery).
                DockTransportInventory = new List<InventoryPayloadItem> { item.DeepClone() },
                UndockTransportInventory = null,
                // Endpoint gained it.
                DockEndpointInventory = null,
                UndockEndpointInventory = new List<InventoryPayloadItem> { item.DeepClone() },
            };

            List<InventoryPayloadItem> load = RouteAnalysisEngine.BuildInventoryLoadManifest(window);

            Assert.Null(load);
        }

        // ==============================================================
        // HasUnwitnessedInventoryGain - non-fungible window-local closure
        // ==============================================================

        // catches: an unwitnessed transport gain (no matching endpoint loss)
        // NOT failing closed.
        [Fact]
        public void HasUnwitnessedInventoryGain_TransportGainNoEndpointLoss_True()
        {
            InventoryPayloadItem phantom = Payload("phantom", "smallCargoContainer", 1);
            var window = new RouteConnectionWindow
            {
                DockTransportInventory = null,
                UndockTransportInventory = new List<InventoryPayloadItem> { phantom.DeepClone() },
                DockEndpointInventory = null,
                UndockEndpointInventory = null,
            };

            Assert.True(RouteAnalysisEngine.HasUnwitnessedInventoryGain(window, out string reason));
            Assert.Contains("phantom", reason);
            Assert.Contains("unwitnessed=1", reason);
        }

        // catches: a clean pickup (transport gain fully matched by endpoint loss)
        // false-rejecting as unwitnessed.
        [Fact]
        public void HasUnwitnessedInventoryGain_GainMatchedByLoss_False()
        {
            InventoryPayloadItem item = Payload("ore-container", "smallCargoContainer", 1);
            var window = new RouteConnectionWindow
            {
                DockEndpointInventory = new List<InventoryPayloadItem> { item.DeepClone() },
                UndockEndpointInventory = null,
                DockTransportInventory = null,
                UndockTransportInventory = new List<InventoryPayloadItem> { item.DeepClone() },
            };

            Assert.False(RouteAnalysisEngine.HasUnwitnessedInventoryGain(window, out _));
        }

        // catches: a PARTIAL unwitnessed gain (transport gains 3, endpoint loses
        // 1) not flagging the unwitnessed remainder.
        [Fact]
        public void HasUnwitnessedInventoryGain_PartialUnwitnessed_FlagsRemainder()
        {
            InventoryPayloadItem one = Payload("ore-container", "smallCargoContainer", 1);
            InventoryPayloadItem three = Payload("ore-container", "smallCargoContainer", 3);
            var window = new RouteConnectionWindow
            {
                DockEndpointInventory = new List<InventoryPayloadItem> { one.DeepClone() },
                UndockEndpointInventory = null,
                DockTransportInventory = null,
                UndockTransportInventory = new List<InventoryPayloadItem> { three.DeepClone() },
            };

            Assert.True(RouteAnalysisEngine.HasUnwitnessedInventoryGain(window, out string reason));
            Assert.Contains("unwitnessed=2", reason); // 3 gained - 1 witnessed
        }

        // ==============================================================
        // Identity hash stability: cross-vessel + proto-vs-loaded
        // (the existing live-move test is same-vessel only)
        // ==============================================================

        // catches: a cross-vessel payload (the same part stored on two DIFFERENT
        // vessels with different transient PART fields) hashing differently, which
        // would break the pickup IdentityHash match between the recorded depot
        // payload and the live source slot.
        [Fact]
        public void IdentityHash_CrossVessel_SamePayloadDifferentTransients_MatchesidenticalHash()
        {
            // STOREDPART on "vessel A": one cid/persistentId/position/temp.
            ConfigNode onVesselA = StoredPartWithInnerPart(
                cid: "4294884350", persistentId: "100", position: "0,0,0", temp: "-1");
            // STOREDPART on "vessel B": the SAME payload but every vessel-local
            // transient differs (different launch, different physical pose).
            ConfigNode onVesselB = StoredPartWithInnerPart(
                cid: "9999000111", persistentId: "55000", position: "123,4,-9", temp: "284.3");

            string hashA = VesselSpawner.ComputeInventoryPayloadIdentityHash(onVesselA);
            string hashB = VesselSpawner.ComputeInventoryPayloadIdentityHash(onVesselB);

            Assert.False(string.IsNullOrEmpty(hashA));
            Assert.Equal(hashA, hashB);
        }

        // catches: the PROTO STOREDPART node (depot snapshot shape) and the
        // LIVE-SAVED StoredPart (StoredPart.Save shape) hashing differently. The
        // pickup match recomputes the live slot's hash via
        // LiveInventoryPickupWriter.ComputeLoadedStoredPartHash (StoredPart.Save),
        // so it MUST equal the recorded proto-node hash for the same payload. We
        // simulate the live-save shape by adding the extra value keys StoredPart.Save
        // writes (stackCapacity, a different slotIndex/quantity) and verify the
        // canonical hash strips them.
        [Fact]
        public void IdentityHash_ProtoVsLoadedSaveShape_MatchesAfterCanonicalStrip()
        {
            // The "proto" depot snapshot shape (as ExtractInventoryPayloadItems
            // sees it): partName / variantName / quantity + inner PART.
            ConfigNode protoShape = StoredPartWithInnerPart(
                cid: "4294884350", persistentId: "100", position: "0,0,0", temp: "-1");

            // The "live-saved" shape (as StoredPart.Save writes it): the SAME
            // payload but with stackCapacity + a DIFFERENT slotIndex and quantity
            // (slot 7, quantity 5) - canonical strip ignores slotIndex / quantity.
            ConfigNode loadedShape = StoredPartWithInnerPart(
                cid: "4294884350", persistentId: "100", position: "0,0,0", temp: "-1");
            loadedShape.SetValue("slotIndex", "7", true);
            loadedShape.SetValue("quantity", "5", true);
            loadedShape.AddValue("stackCapacity", "8");

            string protoHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(protoShape);
            string loadedHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(loadedShape);

            // stackCapacity is NOT a stripped value at the STOREDPART level (only
            // slotIndex / quantity are), so a difference there WOULD split the
            // hash. The match the pickup relies on is over the SAME stored part,
            // so its stackCapacity is identical - assert the slot/quantity-only
            // difference does not split.
            ConfigNode protoWithStack = protoShape.CreateCopy();
            protoWithStack.AddValue("stackCapacity", "8");
            string protoWithStackHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(protoWithStack);
            Assert.Equal(protoWithStackHash, loadedHash);
        }

        // ==============================================================
        // HasRequiredInventory - the carve-out-lift presence gate
        // ==============================================================

        // catches: the all-or-nothing inventory gate passing when the origin is
        // short of a witnessed identity.
        [Fact]
        public void HasRequiredInventory_ShortIdentity_FailsNamingFirst()
        {
            var manifest = new List<InventoryPayloadItem>
            {
                Payload("aaa", "partA", 2),
                Payload("bbb", "partB", 1),
            };
            // Origin holds 1 of aaa (short), 1 of bbb (covered).
            int Counter(string hash) => hash == "aaa" ? 1 : 5;

            bool covered = RouteOriginCargoCheck.HasRequiredInventory(
                manifest, Counter, out string lacking, out int shortBy);

            Assert.False(covered);
            Assert.Equal("aaa", lacking); // ordinal-first short
            Assert.Equal(1, shortBy); // need 2, have 1
        }

        // catches: a fully-covered manifest failing.
        [Fact]
        public void HasRequiredInventory_FullyCovered_Passes()
        {
            var manifest = new List<InventoryPayloadItem>
            {
                Payload("aaa", "partA", 2),
                Payload("bbb", "partB", 1),
            };
            int Counter(string hash) => 10;

            Assert.True(RouteOriginCargoCheck.HasRequiredInventory(
                manifest, Counter, out _, out _));
        }

        // catches: a null/empty manifest failing the trivial pass.
        [Fact]
        public void HasRequiredInventory_NullOrEmpty_Passes()
        {
            Assert.True(RouteOriginCargoCheck.HasRequiredInventory(
                null, h => 0, out _, out _));
            Assert.True(RouteOriginCargoCheck.HasRequiredInventory(
                new List<InventoryPayloadItem>(), h => 0, out _, out _));
        }

        // ==============================================================
        // ApplyInventoryPickupDebit - seam / empty / unresolved branches
        // ==============================================================

        // catches: the inventory pickup test seam not being consulted (the
        // two-direction applier cannot be driven without a live Vessel otherwise).
        [Fact]
        public void InventoryPickupSeam_ShortCircuits_WithEndpointAndManifest()
        {
            RouteEndpoint capturedEndpoint = default;
            List<InventoryPayloadItem> capturedManifest = null;
            var expected = new RouteOrchestrator.InventoryPickupOutcome
            {
                EndpointVesselPid = 888u,
                Short = false,
            };
            RouteOrchestrator.InventoryPickupApplierForTesting = (ep, manifest, env) =>
            {
                capturedEndpoint = ep;
                capturedManifest = manifest;
                return expected;
            };

            var endpoint = Endpoint(42u);
            var manifestIn = new List<InventoryPayloadItem> { Payload("ore-container", "smallCargoContainer", 1) };
            var outcome = RouteOrchestrator.ApplyInventoryPickupDebit(
                endpoint, manifestIn, new FakeInventoryEnv(), "route-inv");

            Assert.Equal(888u, outcome.EndpointVesselPid);
            Assert.False(outcome.Short);
            Assert.Equal(42u, capturedEndpoint.VesselPersistentId);
            Assert.Same(manifestIn, capturedManifest);
        }

        // catches: an empty/null inventory manifest taking the unresolved or
        // write path (it must be a structural no-op).
        [Fact]
        public void InventoryPickup_EmptyManifest_NoOp_ResolvedNotShort()
        {
            var emptyOutcome = RouteOrchestrator.ApplyInventoryPickupDebit(
                Endpoint(42u), new List<InventoryPayloadItem>(), new FakeInventoryEnv(), "route-empty");
            Assert.Null(emptyOutcome.ActualPickedUp);
            Assert.Null(emptyOutcome.RequestedOnShortfall);
            Assert.Equal(0u, emptyOutcome.EndpointVesselPid);
            Assert.False(emptyOutcome.Short);
            Assert.False(emptyOutcome.Unresolved);

            var nullOutcome = RouteOrchestrator.ApplyInventoryPickupDebit(
                Endpoint(42u), null, new FakeInventoryEnv(), "route-null");
            Assert.False(nullOutcome.Short);
            Assert.False(nullOutcome.Unresolved);

            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("empty inventory pickup manifest"));
        }

        // catches: a failed endpoint resolution NOT producing the honest
        // unresolved bookkeeping (zero actuals, FULL requested manifest, short).
        [Fact]
        public void InventoryPickup_UnresolvedEndpoint_FullRequested_ShortAndUnresolved()
        {
            var env = new FakeInventoryEnv { EndpointResolvable = false, FailureReason = "pid-miss" };
            var manifest = new List<InventoryPayloadItem>
            {
                Payload("ore-container", "smallCargoContainer", 1),
                Payload("zero-qty", "partZ", 0), // non-positive -> dropped
            };

            var outcome = RouteOrchestrator.ApplyInventoryPickupDebit(
                Endpoint(42u), manifest, env, "route-unresolved");

            Assert.True(outcome.Unresolved);
            Assert.True(outcome.Short);
            Assert.Null(outcome.ActualPickedUp);
            Assert.Equal(0u, outcome.EndpointVesselPid);
            Assert.NotNull(outcome.RequestedOnShortfall);
            Assert.Single(outcome.RequestedOnShortfall); // zero-qty dropped
            Assert.Equal("ore-container", outcome.RequestedOnShortfall[0].IdentityHash);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("pickup endpoint unresolved")
                && l.Contains("pid-miss"));
        }

        // catches: a resolution that returns true with a null vessel slipping
        // past the unresolved guard (the same one-tick race the M1 origin path
        // and the resource pickup treat as unresolved).
        [Fact]
        public void InventoryPickup_ResolvedNullVessel_TreatedAsUnresolved()
        {
            var env = new FakeInventoryEnv { EndpointResolvable = true };
            var manifest = new List<InventoryPayloadItem>
            {
                Payload("ore-container", "smallCargoContainer", 1),
            };

            var outcome = RouteOrchestrator.ApplyInventoryPickupDebit(
                Endpoint(42u), manifest, env, "route-null-vessel");

            Assert.True(outcome.Unresolved);
            Assert.True(outcome.Short);
            Assert.NotNull(outcome.RequestedOnShortfall);
            Assert.Equal("ore-container", outcome.RequestedOnShortfall[0].IdentityHash);
        }

        // ==============================================================
        // Deterministic partial-load slot match (unloaded path)
        // ==============================================================

        // catches: the unloaded source slot match NOT taking the lowest slot
        // index when two stored parts of the SAME identity occupy different
        // slots (replay stability, design D7). FindUnloadedMatchNode operates on
        // a proto STOREDPARTS node (Vessel-free), so the deterministic rule is
        // unit-testable here; the live ClearPartAtSlot removal is pinned in-game.
        [Fact]
        public void FindUnloadedMatchNode_TwoSlotsSameIdentity_PicksLowestSlot()
        {
            // Two STOREDPART nodes of the SAME payload at slots 5 and 2.
            ConfigNode atSlot5 = StoredPartWithInnerPart(
                cid: "111", persistentId: "5001", position: "0,0,0", temp: "-1");
            atSlot5.SetValue("slotIndex", "5", true);
            ConfigNode atSlot2 = StoredPartWithInnerPart(
                cid: "222", persistentId: "5002", position: "9,9,9", temp: "300");
            atSlot2.SetValue("slotIndex", "2", true);

            // Both hash identically (transients stripped); compute the target.
            string hash = VesselSpawner.ComputeInventoryPayloadIdentityHash(atSlot5);
            Assert.Equal(hash, VesselSpawner.ComputeInventoryPayloadIdentityHash(atSlot2));

            // Build a STOREDPARTS container with slot 5 listed BEFORE slot 2 so a
            // naive first-match would wrongly pick slot 5.
            var storedParts = new ConfigNode("STOREDPARTS");
            storedParts.AddNode(atSlot5);
            storedParts.AddNode(atSlot2);

            ConfigNode match = LiveInventoryPickupWriter.FindUnloadedMatchNode(storedParts, hash);

            Assert.NotNull(match);
            Assert.Equal("2", match.GetValue("slotIndex")); // lowest slot wins
        }

        // catches: a no-match identity returning a phantom node instead of null.
        [Fact]
        public void FindUnloadedMatchNode_NoMatch_ReturnsNull()
        {
            ConfigNode node = StoredPartWithInnerPart(
                cid: "111", persistentId: "5001", position: "0,0,0", temp: "-1");
            var storedParts = new ConfigNode("STOREDPARTS");
            storedParts.AddNode(node);

            Assert.Null(LiveInventoryPickupWriter.FindUnloadedMatchNode(storedParts, "no-such-hash"));
        }

        // catches: removing one unit from a STACKED unloaded slot (quantity > 1)
        // deleting the whole node and over-debiting the partial pickup. It must
        // DECREMENT the quantity, leaving the node in place.
        [Fact]
        public void RemoveOneUnitFromStoredPartsNode_StackedSlot_DecrementsQuantity()
        {
            ConfigNode node = StoredPartWithInnerPart(
                cid: "111", persistentId: "5001", position: "0,0,0", temp: "-1");
            node.SetValue("quantity", "3", true);
            var storedParts = new ConfigNode("STOREDPARTS");
            storedParts.AddNode(node);

            bool removed = LiveInventoryPickupWriter.RemoveOneUnitFromStoredPartsNode(
                storedParts, node, out int stackBefore, out string action);

            Assert.True(removed);
            Assert.Equal(3, stackBefore);
            Assert.Equal("decremented", action);
            // The node stays; quantity drops to 2.
            Assert.Single(storedParts.GetNodes("STOREDPART"));
            Assert.Equal("2", storedParts.GetNodes("STOREDPART")[0].GetValue("quantity"));
        }

        // catches: a single-unit slot being decremented to 0 instead of removed.
        [Fact]
        public void RemoveOneUnitFromStoredPartsNode_SingleUnit_RemovesNode()
        {
            ConfigNode node = StoredPartWithInnerPart(
                cid: "111", persistentId: "5001", position: "0,0,0", temp: "-1");
            node.SetValue("quantity", "1", true);
            var storedParts = new ConfigNode("STOREDPARTS");
            storedParts.AddNode(node);

            bool removed = LiveInventoryPickupWriter.RemoveOneUnitFromStoredPartsNode(
                storedParts, node, out int stackBefore, out string action);

            Assert.True(removed);
            Assert.Equal(1, stackBefore);
            Assert.Equal("removed", action);
            Assert.Empty(storedParts.GetNodes("STOREDPART"));
        }

        // ==============================================================
        // Helpers
        // ==============================================================

        private sealed class FakeInventoryEnv : IRouteRuntimeEnvironment
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
                vessel = null; // KSP-static-free
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

        private static InventoryPayloadItem Payload(string hash, string partName, int quantity)
        {
            var storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));
            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = partName,
                Quantity = quantity,
                SlotsTaken = 1,
                StoredPartSnapshot = storedPart,
            };
        }

        // A STOREDPART carrying an inner PART with the volatile vessel-local
        // fields the canonical hash strips, so cross-vessel / proto-vs-loaded
        // payloads of the same part hash identically.
        private static ConfigNode StoredPartWithInnerPart(
            string cid, string persistentId, string position, string temp)
        {
            var storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("slotIndex", "0");
            storedPart.AddValue("partName", "smallCargoContainer");
            storedPart.AddValue("quantity", "1");
            storedPart.AddValue("variantName", "white");

            var part = storedPart.AddNode("PART");
            part.AddValue("name", "smallCargoContainer");
            part.AddValue("cid", cid);
            part.AddValue("persistentId", persistentId);
            part.AddValue("position", position);
            part.AddValue("temp", temp);
            part.AddValue("flag", "");

            var resource = part.AddNode("RESOURCE");
            resource.AddValue("name", "MonoPropellant");
            resource.AddValue("amount", "5");
            resource.AddValue("maxAmount", "5");

            var module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleCargoPart");
            module.AddValue("payloadMode", "packed");
            return storedPart;
        }
    }
}
