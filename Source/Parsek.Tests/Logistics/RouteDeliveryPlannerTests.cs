using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pin the pure delivery planner's clamp + partial/zero/deterministic-order
    /// invariants. Uses a hand-rolled <see cref="IDeliveryCapacityProbe"/> fake
    /// so the planner never touches KSP statics, vessels, or parts.
    /// </summary>
    [Collection("Sequential")]
    public class RouteDeliveryPlannerTests
    {
        /// <summary>
        /// Hand-rolled probe fake. Resource capacity is read from a settable
        /// dictionary; inventory slots are pulled from a scripted queue and the
        /// planner is expected to call <see cref="ConsumeInventorySlot"/> for
        /// each accepted slot so the next probe call returns a fresh one.
        /// </summary>
        private sealed class FakeDeliveryCapacityProbe : IDeliveryCapacityProbe
        {
            public Dictionary<string, double> ResourceCapacity = new Dictionary<string, double>();

            /// <summary>
            /// Scripted slot queue. Each <see cref="ProbeFirstEmptyInventorySlot"/>
            /// returns the next entry without dequeuing — the planner's
            /// <see cref="ConsumeInventorySlot"/> call dequeues the head. An
            /// empty queue means "no empty slot" (probe returns None and
            /// doesn't dequeue).
            /// </summary>
            public List<InventorySlotAddress> SlotQueue = new List<InventorySlotAddress>();

            /// <summary>Slot addresses the planner consumed, in call order.</summary>
            public List<InventorySlotAddress> ConsumedSlots = new List<InventorySlotAddress>();

            public double ProbeResourceFreeCapacity(string resourceName)
            {
                if (resourceName == null) return 0.0;
                double cap;
                return ResourceCapacity.TryGetValue(resourceName, out cap) ? cap : 0.0;
            }

            public InventorySlotAddress ProbeFirstEmptyInventorySlot()
            {
                if (SlotQueue.Count == 0) return InventorySlotAddress.None;
                return SlotQueue[0];
            }

            public void ConsumeInventorySlot(InventorySlotAddress address)
            {
                ConsumedSlots.Add(address);
                if (SlotQueue.Count > 0 && SlotQueue[0].Equals(address))
                {
                    SlotQueue.RemoveAt(0);
                }
            }

            /// <summary>Prefab stackableQuantity fallback per part name; parts absent here report 1.</summary>
            public Dictionary<string, int> StackableQuantityByPart = new Dictionary<string, int>();

            /// <summary>
            /// Optional volume/mass admission script: (item, requestedUnits) →
            /// admitted units. Null = everything fits (the default so the
            /// pre-existing slot-focused tests run unchanged).
            /// </summary>
            public Func<InventoryPayloadItem, int, int> UnitsThatFit;

            /// <summary>Capacity consumptions the planner claimed, in call order.</summary>
            public List<(string PartName, int Units)> ConsumedCapacity = new List<(string, int)>();

            public int ProbeInventoryStackableQuantity(InventoryPayloadItem item)
            {
                if (item?.PartName == null) return 1;
                int cap;
                return StackableQuantityByPart.TryGetValue(item.PartName, out cap) && cap > 0 ? cap : 1;
            }

            public int ProbeInventoryUnitsThatFit(InventoryPayloadItem item, int requestedUnits)
            {
                return UnitsThatFit != null ? UnitsThatFit(item, requestedUnits) : requestedUnits;
            }

            public void ConsumeInventoryCapacity(InventoryPayloadItem item, int units)
            {
                ConsumedCapacity.Add((item?.PartName, units));
            }
        }

        /// <summary>Shorthand for a single-module slot address (part 0, module 0).</summary>
        private static InventorySlotAddress Slot(int slotIndex) => new InventorySlotAddress(0, 0, slotIndex);

        private static Route MakeRouteWithStop(RouteStop stop)
        {
            return new Route
            {
                Id = "route-1",
                Stops = new List<RouteStop> { stop },
            };
        }

        private static InventoryPayloadItem MakeItem(
            string hash, string partName, int quantity = 1, ConfigNode snapshot = null)
        {
            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = partName,
                Quantity = quantity,
                SlotsTaken = 1,
                StoredPartSnapshot = snapshot,
            };
        }

        /// <summary>Minimal STOREDPART wrapper carrying an explicit stackCapacity value.</summary>
        private static ConfigNode MakeStoredPartSnapshot(string partName, int? stackCapacity = null)
        {
            var snapshot = new ConfigNode("STOREDPART");
            snapshot.AddValue("partName", partName);
            if (stackCapacity.HasValue)
                snapshot.AddValue("stackCapacity", stackCapacity.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            var partNode = snapshot.AddNode("PART");
            partNode.AddValue("name", partName);
            return snapshot;
        }

        // catches: clamp logic returning Available < Requested when there's headroom.
        [Fact]
        public void FullFill_AllResourcesUnderCapacity()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 50.0 },
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "LiquidFuel", 500.0 },
                    { "Oxidizer", 200.0 },
                },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(2, plan.Resources.Count);
            foreach (var line in plan.Resources)
            {
                Assert.Equal(line.Requested, line.Available);
            }
            Assert.False(plan.IsPartial);
            Assert.False(plan.IsZero);
        }

        // catches: clamp logic flipping the wrong resource or losing the partial marker.
        [Fact]
        public void PartialFill_OneResourceCapped()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 80.0 },
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "LiquidFuel", 500.0 }, // fits fully
                    { "Oxidizer", 40.0 },    // capped at half
                },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.True(plan.IsPartial);
            Assert.False(plan.IsZero);
            Assert.Equal(2, plan.Resources.Count);
            foreach (var line in plan.Resources)
            {
                if (line.Name == "LiquidFuel")
                {
                    Assert.Equal(100.0, line.Available);
                    Assert.Equal(100.0, line.Requested);
                }
                else if (line.Name == "Oxidizer")
                {
                    Assert.Equal(40.0, line.Available);
                    Assert.Equal(80.0, line.Requested);
                    Assert.True(line.Available < line.Requested);
                }
                else
                {
                    Assert.True(false, "unexpected resource " + line.Name);
                }
            }
        }

        // catches: IsZero not being set when all resources clamp to 0; downstream would emit an incorrect "real delivery" ledger row.
        [Fact]
        public void ZeroFill_DestinationTanksFull()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 80.0 },
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "LiquidFuel", 0.0 },
                    { "Oxidizer", 0.0 },
                },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(2, plan.Resources.Count);
            foreach (var line in plan.Resources)
            {
                Assert.Equal(0.0, line.Available);
            }
            Assert.True(plan.IsPartial);
            Assert.True(plan.IsZero);
        }

        // catches: planner not calling ConsumeInventorySlot, leading to double-assigned slots.
        [Fact]
        public void Inventory_FirstSlotUsed_SecondSlotFromConsume()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA"),
                    MakeItem("h-b", "partB"),
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(0), Slot(1) },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(2, plan.Inventory.Count);
            Assert.Equal(Slot(0), plan.Inventory[0].AssignedSlot);
            Assert.Equal(Slot(1), plan.Inventory[1].AssignedSlot);
            Assert.Equal(new List<InventorySlotAddress> { Slot(0), Slot(1) }, probe.ConsumedSlots);
            Assert.False(plan.IsPartial);
            Assert.False(plan.IsZero);
        }

        // catches: the planner or the line struct collapsing the module-qualified
        // address back to a bare slot index — two modules re-using slot index 0
        // must stay distinct addresses end-to-end.
        [Fact]
        public void Inventory_MultiModule_AddressesFromLaterModulesPropagate()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA"),
                    MakeItem("h-b", "partB"),
                    MakeItem("h-c", "partC"),
                },
            };
            var route = MakeRouteWithStop(stop);
            // First module (part 0, module 0) has one free slot; the next free
            // slots live on a second module of the same part and on another part.
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress>
                {
                    new InventorySlotAddress(0, 0, 2),
                    new InventorySlotAddress(0, 1, 0),
                    new InventorySlotAddress(3, 0, 0),
                },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(3, plan.Inventory.Count);
            Assert.Equal(new InventorySlotAddress(0, 0, 2), plan.Inventory[0].AssignedSlot);
            Assert.Equal(new InventorySlotAddress(0, 1, 0), plan.Inventory[1].AssignedSlot);
            Assert.Equal(new InventorySlotAddress(3, 0, 0), plan.Inventory[2].AssignedSlot);
            Assert.Equal(3, probe.ConsumedSlots.Count);
            Assert.False(plan.IsPartial);
            Assert.False(plan.IsZero);
        }

        // catches: AssignedSlot=-1 not propagating; downstream apply would crash on -1.
        [Fact]
        public void Inventory_NoEmptySlot_SkipsItem()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA"),
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe(); // empty SlotQueue → None

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Single(plan.Inventory);
            Assert.False(plan.Inventory[0].AssignedSlot.IsValid);
            Assert.True(plan.IsPartial);
            Assert.True(plan.IsZero);
        }

        // catches: partial inventory delivery not correctly marked.
        [Fact]
        public void Inventory_PartialSlot_FirstAssignedSecondSkipped()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA"),
                    MakeItem("h-b", "partB"),
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(0) }, // only slot 0 available; second item gets None
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(2, plan.Inventory.Count);
            Assert.Equal(Slot(0), plan.Inventory[0].AssignedSlot);
            Assert.False(plan.Inventory[1].AssignedSlot.IsValid);
            Assert.True(plan.IsPartial);
            Assert.False(plan.IsZero);
        }

        // catches: planner only checking one manifest for IsPartial.
        [Fact]
        public void MixedManifest_ResourcesAndInventory_TogetherPartial()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA"),
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "LiquidFuel", 40.0 }, // partial
                },
                SlotQueue = new List<InventorySlotAddress> { Slot(0) }, // inventory fits
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.True(plan.IsPartial);
            Assert.False(plan.IsZero);
            Assert.Single(plan.Resources);
            Assert.Equal(40.0, plan.Resources[0].Available);
            Assert.Equal(100.0, plan.Resources[0].Requested);
            Assert.Single(plan.Inventory);
            Assert.Equal(Slot(0), plan.Inventory[0].AssignedSlot);
        }

        // catches: planner crashing on empty manifest.
        [Fact]
        public void EmptyManifest_ReturnsEmptyPlan()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>(),
                InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe();

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Empty(plan.Resources);
            Assert.Empty(plan.Inventory);
            Assert.True(plan.IsZero);
            Assert.False(plan.IsPartial);
        }

        // catches: NRE on null route.
        [Fact]
        public void NullRoute_ReturnsEmpty()
        {
            var probe = new FakeDeliveryCapacityProbe();

            var plan = RouteDeliveryPlanner.PrepareDelivery(null, 0, probe);

            Assert.Empty(plan.Resources);
            Assert.Empty(plan.Inventory);
            Assert.True(plan.IsZero);
            Assert.False(plan.IsPartial);
        }

        // catches: NRE on null probe.
        [Fact]
        public void NullProbe_ReturnsEmpty()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
            };
            var route = MakeRouteWithStop(stop);

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, null);

            Assert.Empty(plan.Resources);
            Assert.Empty(plan.Inventory);
            Assert.True(plan.IsZero);
            Assert.False(plan.IsPartial);
        }

        // catches: planner indexing past Stops list.
        [Fact]
        public void InvalidStopIndex_OutOfRange_ReturnsEmpty()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe();

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 99, probe);

            Assert.Empty(plan.Resources);
            Assert.Empty(plan.Inventory);
            Assert.True(plan.IsZero);
            Assert.False(plan.IsPartial);
        }

        // catches: planner not guarding against negative index.
        [Fact]
        public void InvalidStopIndex_Negative_ReturnsEmpty()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe();

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, -1, probe);

            Assert.Empty(plan.Resources);
            Assert.Empty(plan.Inventory);
            Assert.True(plan.IsZero);
            Assert.False(plan.IsPartial);
        }

        // catches: nondeterministic dictionary enumeration leaking into ledger rows.
        [Fact]
        public void DeterministicOrdering_AcrossRuns()
        {
            // Build two stops with the same resources but inserted in opposite order.
            // Dictionary<string, double> iteration order is normally insertion-order
            // for small sets but not guaranteed; the planner's sort guarantees the
            // resulting Resources list is in the same (Ordinal) order regardless.
            var stopA = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "Zeta", 1.0 },
                    { "Alpha", 2.0 },
                    { "Mu", 3.0 },
                },
            };
            var stopB = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "Mu", 3.0 },
                    { "Alpha", 2.0 },
                    { "Zeta", 1.0 },
                },
            };
            var routeA = MakeRouteWithStop(stopA);
            var routeB = MakeRouteWithStop(stopB);
            var probeA = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "Zeta", 100.0 }, { "Alpha", 100.0 }, { "Mu", 100.0 },
                },
            };
            var probeB = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "Zeta", 100.0 }, { "Alpha", 100.0 }, { "Mu", 100.0 },
                },
            };

            var planA = RouteDeliveryPlanner.PrepareDelivery(routeA, 0, probeA);
            var planB = RouteDeliveryPlanner.PrepareDelivery(routeB, 0, probeB);

            Assert.Equal(3, planA.Resources.Count);
            Assert.Equal(3, planB.Resources.Count);
            // Ordinal sort: Alpha < Mu < Zeta
            Assert.Equal("Alpha", planA.Resources[0].Name);
            Assert.Equal("Mu", planA.Resources[1].Name);
            Assert.Equal("Zeta", planA.Resources[2].Name);
            for (int i = 0; i < planA.Resources.Count; i++)
            {
                Assert.Equal(planA.Resources[i].Name, planB.Resources[i].Name);
            }
        }

        // catches: garbage manifest entries (0 or negative) leaking through.
        [Fact]
        public void ZeroOrNegativeRequested_Skipped()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "JunkZero", 0.0 },
                    { "JunkNeg", -1.0 },
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "LiquidFuel", 500.0 },
                    { "JunkZero", 500.0 },
                    { "JunkNeg", 500.0 },
                },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Single(plan.Resources);
            Assert.Equal("LiquidFuel", plan.Resources[0].Name);
        }

        // catches: over-compressed manifest (Quantity > stackCapacity) planned into
        // ONE slot — the pre-fix behavior that persisted an invalid stock state
        // (quantity=10 in a single non-stackable slot on the unloaded path) and
        // silently dropped 9 of 10 units on the loaded path.
        [Fact]
        public void Inventory_QuantityAboveStackCapacity_SplitsAcrossSlots()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA", quantity: 10,
                        snapshot: MakeStoredPartSnapshot("partA", stackCapacity: 4)),
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(0), Slot(1), Slot(2), Slot(3) },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            // ceil(10 / 4) = 3 slots: 4 + 4 + 2 units.
            Assert.Equal(3, plan.Inventory.Count);
            Assert.Equal(Slot(0), plan.Inventory[0].AssignedSlot);
            Assert.Equal(4, plan.Inventory[0].Units);
            Assert.Equal(Slot(1), plan.Inventory[1].AssignedSlot);
            Assert.Equal(4, plan.Inventory[1].Units);
            Assert.Equal(Slot(2), plan.Inventory[2].AssignedSlot);
            Assert.Equal(2, plan.Inventory[2].Units);
            Assert.Equal(new List<InventorySlotAddress> { Slot(0), Slot(1), Slot(2) }, probe.ConsumedSlots);
            // Every placed stack claimed its volume/mass budget.
            Assert.Equal(3, probe.ConsumedCapacity.Count);
            Assert.False(plan.IsPartial);
            Assert.False(plan.IsZero);
        }

        // catches: non-stackable items (stackCapacity=1, e.g. resource-bearing cargo)
        // packed multiple-per-slot instead of one slot per unit.
        [Fact]
        public void Inventory_NonStackable_OneSlotPerUnit()
        {
            var item = MakeItem("h-a", "partA", quantity: 3,
                snapshot: MakeStoredPartSnapshot("partA", stackCapacity: 1));
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem> { item },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(2), Slot(5), Slot(7) },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(3, plan.Inventory.Count);
            foreach (var line in plan.Inventory)
                Assert.Equal(1, line.Units);
            Assert.Equal(new List<InventorySlotAddress> { Slot(2), Slot(5), Slot(7) }, probe.ConsumedSlots);
            Assert.False(plan.IsPartial);
        }

        // catches: slots running out mid-item not surfacing as a partial plan with
        // the unplaced unit count.
        [Fact]
        public void Inventory_SlotsRunOutMidItem_RemainderSkippedAsPartial()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA", quantity: 10,
                        snapshot: MakeStoredPartSnapshot("partA", stackCapacity: 4)),
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(0), Slot(1) }, // room for 8 of 10 units
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(3, plan.Inventory.Count);
            Assert.Equal(4, plan.Inventory[0].Units);
            Assert.Equal(4, plan.Inventory[1].Units);
            Assert.False(plan.Inventory[2].AssignedSlot.IsValid);
            Assert.Equal(2, plan.Inventory[2].Units); // 2 units did not fit
            Assert.True(plan.IsPartial);
            Assert.False(plan.IsZero);
        }

        // catches: the unloaded-path volume/mass hole — an item the probe refuses
        // (oversized for the container) must be SKIPPED by the planner, not handed
        // to the writer to append anyway.
        [Fact]
        public void Inventory_VolumeRejected_ItemSkipped()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "hugePart", quantity: 1,
                        snapshot: MakeStoredPartSnapshot("hugePart", stackCapacity: 1)),
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(0) },       // an empty slot EXISTS
                UnitsThatFit = (item, want) => 0,      // but the item does not fit
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Single(plan.Inventory);
            Assert.False(plan.Inventory[0].AssignedSlot.IsValid);
            Assert.Equal(1, plan.Inventory[0].Units);
            Assert.Empty(probe.ConsumedSlots); // no slot burned on a rejected item
            Assert.True(plan.IsPartial);
            Assert.True(plan.IsZero);
        }

        // catches: partial volume admission (fewer units fit than a full stack)
        // not clamping the stack or not marking the remainder skipped.
        [Fact]
        public void Inventory_VolumeAdmitsPartialStack_RestSkipped()
        {
            var stop = new RouteStop
            {
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeItem("h-a", "partA", quantity: 10,
                        snapshot: MakeStoredPartSnapshot("partA", stackCapacity: 4)),
                },
            };
            var route = MakeRouteWithStop(stop);
            int admissionCalls = 0;
            var probe = new FakeDeliveryCapacityProbe
            {
                SlotQueue = new List<InventorySlotAddress> { Slot(0), Slot(1) },
                // First stack: only 2 of 4 fit. After that the budget is gone.
                UnitsThatFit = (item, want) => (++admissionCalls == 1) ? 2 : 0,
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Equal(2, plan.Inventory.Count);
            Assert.Equal(Slot(0), plan.Inventory[0].AssignedSlot);
            Assert.Equal(2, plan.Inventory[0].Units);
            Assert.False(plan.Inventory[1].AssignedSlot.IsValid);
            Assert.Equal(8, plan.Inventory[1].Units);
            Assert.True(plan.IsPartial);
            Assert.False(plan.IsZero);
        }

        // ==================================================================
        // ResolveStackCapacity
        // ==================================================================

        // catches: wrapper stackCapacity (the recorded stock value) not winning.
        [Fact]
        public void ResolveStackCapacity_WrapperValue_Wins()
        {
            var snapshot = MakeStoredPartSnapshot("partA", stackCapacity: 6);
            snapshot.GetNode("PART").AddValue("moduleCargoStackableQuantity", "9");
            var item = MakeItem("h-a", "partA", quantity: 1, snapshot: snapshot);
            var probe = new FakeDeliveryCapacityProbe
            {
                StackableQuantityByPart = { { "partA", 12 } },
            };

            Assert.Equal(6, RouteDeliveryPlanner.ResolveStackCapacity(item, probe));
        }

        // catches: the inner PART node's moduleCargoStackableQuantity fallback
        // (stock ProtoPartSnapshot.Save writes it) being ignored.
        [Fact]
        public void ResolveStackCapacity_InnerPartValue_UsedWhenWrapperMissing()
        {
            var snapshot = MakeStoredPartSnapshot("partA");
            snapshot.GetNode("PART").AddValue("moduleCargoStackableQuantity", "9");
            var item = MakeItem("h-a", "partA", quantity: 1, snapshot: snapshot);
            var probe = new FakeDeliveryCapacityProbe();

            Assert.Equal(9, RouteDeliveryPlanner.ResolveStackCapacity(item, probe));
        }

        // catches: the live prefab fallback (ModuleCargoPart.stackableQuantity via
        // the probe) being skipped when the payload has no snapshot at all.
        [Fact]
        public void ResolveStackCapacity_ProbeFallback_UsedOnlyWithoutSnapshot()
        {
            var probe = new FakeDeliveryCapacityProbe
            {
                StackableQuantityByPart = { { "partA", 8 } },
            };

            // No snapshot: prefab fallback applies.
            var noSnapshot = MakeItem("h-a", "partA", quantity: 1);
            Assert.Equal(8, RouteDeliveryPlanner.ResolveStackCapacity(noSnapshot, probe));

            // Snapshot present but silent on stack values: the writers can
            // only persist stack capacity 1 (loaded UpdateStackAmountAtSlot
            // clamps to the snapshot-derived capacity, unloaded
            // StoredPart.Load defaults to 1), so the prefab value must NOT
            // widen the plan beyond that.
            var silentSnapshot = MakeItem("h-a", "partA", quantity: 1,
                snapshot: MakeStoredPartSnapshot("partA"));
            Assert.Equal(1, RouteDeliveryPlanner.ResolveStackCapacity(silentSnapshot, probe));
        }

        // catches: resource-bearing cargo stacked above 1 — stock ModuleCargoPart
        // forces stackableQuantity=1 for parts containing resources, so a bigger
        // recorded value must not leak into the plan.
        [Fact]
        public void ResolveStackCapacity_ResourceBearing_ForcedToOne()
        {
            var item = MakeItem("h-a", "partA", quantity: 1,
                snapshot: MakeStoredPartSnapshot("partA", stackCapacity: 4));
            item.StoredResources = new Dictionary<string, ResourceAmount>
            {
                { "MonoPropellant", new ResourceAmount { amount = 5.0, maxAmount = 5.0 } },
            };
            var probe = new FakeDeliveryCapacityProbe();

            Assert.Equal(1, RouteDeliveryPlanner.ResolveStackCapacity(item, probe));
        }

        // catches: null snapshot / null probe crashing the resolver instead of
        // defaulting to 1.
        [Fact]
        public void ResolveStackCapacity_NoData_DefaultsToOne()
        {
            var item = MakeItem("h-a", "partA");
            Assert.Equal(1, RouteDeliveryPlanner.ResolveStackCapacity(item, new FakeDeliveryCapacityProbe()));
            Assert.Equal(1, RouteDeliveryPlanner.ResolveStackCapacity(item, null));
            Assert.Equal(1, RouteDeliveryPlanner.ResolveStackCapacity(null, null));
        }

        // catches: negative capacity propagating into apply path (would cause negative resource writes).
        [Fact]
        public void NegativeCapacity_TreatedAsZero()
        {
            var stop = new RouteStop
            {
                DeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                },
            };
            var route = MakeRouteWithStop(stop);
            var probe = new FakeDeliveryCapacityProbe
            {
                ResourceCapacity = new Dictionary<string, double>
                {
                    { "LiquidFuel", -5.0 },
                },
            };

            var plan = RouteDeliveryPlanner.PrepareDelivery(route, 0, probe);

            Assert.Single(plan.Resources);
            Assert.Equal(0.0, plan.Resources[0].Available);
            Assert.Equal(100.0, plan.Resources[0].Requested);
            Assert.True(plan.IsPartial);
            Assert.True(plan.IsZero);
        }
    }
}
