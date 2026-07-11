using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pin the pure all-or-nothing destination-capacity gate: it must reuse
    /// the delivery planner's fit verdict (a plan that would partial-fill is
    /// exactly a hold), name the first short item deterministically, skip
    /// pure-pickup stops, and fail OPEN on an unresolvable stop (null probe -
    /// the endpoint gate owns that failure).
    /// </summary>
    [Collection("Sequential")]
    public class RouteDestinationCapacityCheckTests
    {
        /// <summary>Same scripted-probe shape as RouteDeliveryPlannerTests.</summary>
        private sealed class FakeProbe : IDeliveryCapacityProbe
        {
            public Dictionary<string, double> ResourceCapacity = new Dictionary<string, double>();
            public List<InventorySlotAddress> SlotQueue = new List<InventorySlotAddress>();

            public double ProbeResourceFreeCapacity(string resourceName)
            {
                if (resourceName == null) return 0.0;
                return ResourceCapacity.TryGetValue(resourceName, out double cap) ? cap : 0.0;
            }

            public InventorySlotAddress ProbeFirstEmptyInventorySlot()
                => SlotQueue.Count == 0 ? InventorySlotAddress.None : SlotQueue[0];

            public void ConsumeInventorySlot(InventorySlotAddress address)
            {
                if (SlotQueue.Count > 0 && SlotQueue[0].Equals(address))
                    SlotQueue.RemoveAt(0);
            }

            // Parity-fix probe surface: these tests gate on SLOT availability,
            // so stack capacity is 1 (one unit per slot, matching the Quantity=1
            // fixtures) and volume/mass admission is unlimited (everything the
            // caller asks for fits) - the slot queue is the only limiter.
            public int ProbeInventoryStackableQuantity(InventoryPayloadItem item) => 1;

            public int ProbeInventoryUnitsThatFit(InventoryPayloadItem item, int requestedUnits)
                => requestedUnits;

            public void ConsumeInventoryCapacity(InventoryPayloadItem item, int units) { }
        }

        private static InventorySlotAddress Slot(int slotIndex) => new InventorySlotAddress(0, 0, slotIndex);

        private static Route MakeRoute(params RouteStop[] stops)
        {
            return new Route
            {
                Id = "route-1",
                Stops = new List<RouteStop>(stops),
            };
        }

        private static InventoryPayloadItem MakeItem(string hash, string partName)
        {
            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = partName,
                Quantity = 1,
                SlotsTaken = 1,
            };
        }

        // catches: a fitting manifest holding the route (false full).
        [Fact]
        public void FullFit_Passes()
        {
            var route = MakeRoute(new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("hashA", "evaJetpack"),
                },
            });
            var probe = new FakeProbe
            {
                ResourceCapacity = new Dictionary<string, double> { { "LiquidFuel", 200.0 } },
                SlotQueue = new List<InventorySlotAddress> { Slot(0) },
            };

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, _ => probe, out string token, out int stopIndex);

            Assert.True(ok);
            Assert.Equal(string.Empty, token);
            Assert.Equal(-1, stopIndex);
        }

        // catches: a resource shortfall not holding, or the token not naming
        // the short resource.
        [Fact]
        public void ResourceShort_Holds_NamesResource()
        {
            var route = MakeRoute(new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 50.0 },
                },
            });
            var probe = new FakeProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 10.0 }, // short
                },
            };

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, _ => probe, out string token, out int stopIndex);

            Assert.False(ok);
            Assert.Equal("Oxidizer", token);
            Assert.Equal(0, stopIndex);
        }

        // catches: an inventory-slot shortfall not holding, or the token not
        // carrying the stored-part family + part name.
        [Fact]
        public void InventorySlotShort_Holds_NamesStoredPart()
        {
            var route = MakeRoute(new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("hashA", "evaJetpack"),
                    MakeItem("hashB", "sensorThermometer"),
                },
            });
            var probe = new FakeProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(3) }, // one slot for two items
            };

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, _ => probe, out string token, out int stopIndex);

            Assert.False(ok);
            // Manifest order is identity-hash order; the second item gets no slot.
            Assert.Equal("stored-part:sensorThermometer", token);
            Assert.Equal(0, stopIndex);
        }

        // catches: pure-pickup stops (no delivery manifest) probing the
        // destination at all - the probe factory must not even be called.
        [Fact]
        public void PurePickupStop_SkippedWithoutProbing()
        {
            var route = MakeRoute(new RouteStop
            {
                PickupManifest = new Dictionary<string, double> { { "Ore", 50.0 } },
            });

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route,
                _ => throw new InvalidOperationException("probe factory must not be called"),
                out string token, out _);

            Assert.True(ok);
            Assert.Equal(string.Empty, token);
        }

        // catches: an unresolvable stop (null probe) holding the route - the
        // endpoint eligibility check owns that failure; this gate fails OPEN.
        [Fact]
        public void UnresolvedStop_NullProbe_FailsOpen()
        {
            var route = MakeRoute(new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
            });

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, _ => null, out string token, out _);

            Assert.True(ok);
            Assert.Equal(string.Empty, token);
        }

        // catches: a multi-stop route reporting the wrong stop, or stopping the
        // walk before a later short stop.
        [Fact]
        public void MultiStop_SecondStopShort_NamesStopIndex()
        {
            var stopA = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
            };
            var stopB = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "Ore", 100.0 } },
            };
            var route = MakeRoute(stopA, stopB);

            var probeA = new FakeProbe
            {
                ResourceCapacity = new Dictionary<string, double> { { "LiquidFuel", 50.0 } },
            };
            var probeB = new FakeProbe
            {
                ResourceCapacity = new Dictionary<string, double> { { "Ore", 40.0 } }, // short
            };

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, i => i == 0 ? (IDeliveryCapacityProbe)probeA : probeB,
                out string token, out int stopIndex);

            Assert.False(ok);
            Assert.Equal("Ore", token);
            Assert.Equal(1, stopIndex);
        }

        // catches: two stops delivering to the SAME destination each being
        // checked against the full free capacity (fresh-probe hole): the
        // caller returns the SAME probe instance per vessel and the gate must
        // account the combined resource manifest against it.
        [Fact]
        public void SameDestination_TwoStops_CombinedResourcesGate()
        {
            var stopA = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 60.0 } },
            };
            var stopB = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 60.0 } },
            };
            var route = MakeRoute(stopA, stopB);
            var shared = new FakeProbe
            {
                ResourceCapacity = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
            };

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, _ => shared, out string token, out int stopIndex);

            Assert.False(ok); // 60 + 60 > 100 even though each fits alone
            Assert.Equal("LiquidFuel", token);
            Assert.Equal(1, stopIndex);

            // With 120 free, the combined manifest fits.
            shared.ResourceCapacity["LiquidFuel"] = 120.0;
            Assert.True(RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, _ => shared, out _, out _));
        }

        // catches: two stops' stored parts each being offered the same single
        // inventory slot (the shared probe's consumed-slot tracking must span
        // stops).
        [Fact]
        public void SameDestination_TwoStops_SharedInventorySlots()
        {
            var stopA = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("hashA", "evaJetpack"),
                },
            };
            var stopB = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("hashB", "sensorThermometer"),
                },
            };
            var route = MakeRoute(stopA, stopB);
            var shared = new FakeProbe { SlotQueue = new List<InventorySlotAddress> { Slot(0) } }; // ONE slot

            bool ok = RouteDestinationCapacityCheck.HasCapacityForAllStops(
                route, _ => shared, out string token, out int stopIndex);

            Assert.False(ok);
            Assert.Equal("stored-part:sensorThermometer", token);
            Assert.Equal(1, stopIndex);
        }

        // catches: degenerate inputs throwing or holding (null route / stops /
        // factory all mean "nothing to gate").
        [Fact]
        public void DegenerateInputs_Pass()
        {
            Assert.True(RouteDestinationCapacityCheck.HasCapacityForAllStops(
                null, _ => null, out _, out _));
            Assert.True(RouteDestinationCapacityCheck.HasCapacityForAllStops(
                new Route { Id = "r", Stops = null }, _ => null, out _, out _));
            Assert.True(RouteDestinationCapacityCheck.HasCapacityForAllStops(
                MakeRoute(new RouteStop()), null, out _, out _));
        }

        // catches: FirstShortToken preferring inventory over the resource walk
        // order, or the defensive fallback going blank.
        [Fact]
        public void FirstShortToken_ResourceBeforeInventory_AndFallback()
        {
            var resources = new List<ResourceDeliveryLine>
            {
                new ResourceDeliveryLine("LiquidFuel", 100.0, 100.0), // full
                new ResourceDeliveryLine("Oxidizer", 50.0, 10.0),     // short
            };
            var inventory = new List<InventoryDeliveryLine>
            {
                new InventoryDeliveryLine(MakeItem("h", "evaJetpack"), InventorySlotAddress.None, 1), // also short
            };
            var partialPlan = new DeliveryPlan(resources, inventory, isPartial: true, isZero: false);
            Assert.Equal("Oxidizer", RouteDestinationCapacityCheck.FirstShortToken(partialPlan));

            var emptyPlan = DeliveryPlan.Empty();
            Assert.Equal("delivery", RouteDestinationCapacityCheck.FirstShortToken(emptyPlan));
        }
    }
}
