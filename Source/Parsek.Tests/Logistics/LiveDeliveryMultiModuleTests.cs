using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the multi-module delivery slot contract: the module-qualified
    /// <see cref="InventorySlotAddress"/> value semantics, the pure unloaded
    /// per-module empty-slot scan
    /// (<see cref="LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule"/>),
    /// and the module-targeted unloaded store
    /// (<see cref="LiveDeliveryWriters.StoreIntoUnloadedInventoryModule"/>).
    /// The loaded-branch walk needs live <c>ModuleInventoryPart</c> instances
    /// and is pinned in-game
    /// (LogisticsDeliveryRuntimeTests.Delivery_MultiModule_FirstContainerFullSecondReceives).
    /// </summary>
    [Collection("Sequential")]
    public class LiveDeliveryMultiModuleTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LiveDeliveryMultiModuleTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            logLines.Clear();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // InventorySlotAddress value semantics
        // ==================================================================

        // catches: None accidentally reading as a valid assignable slot.
        [Fact]
        public void SlotAddress_None_IsInvalid()
        {
            Assert.False(InventorySlotAddress.None.IsValid);
            Assert.True(new InventorySlotAddress(0, 0, 0).IsValid);
            Assert.False(new InventorySlotAddress(-1, 0, 0).IsValid);
            Assert.False(new InventorySlotAddress(0, -1, 0).IsValid);
            Assert.False(new InventorySlotAddress(0, 0, -1).IsValid);
        }

        // catches: default(InventorySlotAddress) reading as the VALID address
        // (0,0,0) — a forgotten initialization would silently deliver into the
        // root part's first slot instead of being skipped.
        [Fact]
        public void SlotAddress_DefaultValue_IsInvalid()
        {
            Assert.False(default(InventorySlotAddress).IsValid);
            var uninitializedArrayElement = new InventorySlotAddress[1];
            Assert.False(uninitializedArrayElement[0].IsValid);
        }

        // catches: equality treating the invalid default and the constructed
        // (0,0,0) as interchangeable keys — two values whose IsValid differs
        // must not compare equal.
        [Fact]
        public void SlotAddress_DefaultValue_NotEqualToConstructedZeroAddress()
        {
            Assert.False(default(InventorySlotAddress).Equals(new InventorySlotAddress(0, 0, 0)));
            Assert.True(new InventorySlotAddress(0, 0, 0).Equals(new InventorySlotAddress(0, 0, 0)));
        }

        // catches: consumed-set keying collapsing back to the bare slot index —
        // slot 0 of module A and slot 0 of module B must be DISTINCT keys, or
        // consuming one blocks the other and the second container is never used.
        [Fact]
        public void SlotAddress_SameSlotIndexDifferentModule_AreDistinctKeys()
        {
            var set = new HashSet<InventorySlotAddress>
            {
                new InventorySlotAddress(0, 0, 0),
            };

            Assert.Contains(new InventorySlotAddress(0, 0, 0), set);
            Assert.DoesNotContain(new InventorySlotAddress(0, 1, 0), set);
            Assert.DoesNotContain(new InventorySlotAddress(1, 0, 0), set);

            set.Add(new InventorySlotAddress(0, 1, 0));
            set.Add(new InventorySlotAddress(1, 0, 0));
            Assert.Equal(3, set.Count);
        }

        // catches: ToString regressions breaking the log grep contract.
        [Fact]
        public void SlotAddress_ToString_IsGrepStable()
        {
            Assert.Equal("part2/mod1/slot4", new InventorySlotAddress(2, 1, 4).ToString());
            Assert.Equal("<none>", InventorySlotAddress.None.ToString());
        }

        // ==================================================================
        // FindFirstEmptySlotInUnloadedModule (pure unloaded per-module scan)
        // ==================================================================

        private static ConfigNode MakeInventoryModuleValues(int? inventorySlots, params int[] occupiedSlots)
        {
            var mv = new ConfigNode("MODULE");
            mv.AddValue("name", "ModuleInventoryPart");
            if (inventorySlots.HasValue)
                mv.AddValue("InventorySlots", inventorySlots.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (occupiedSlots != null && occupiedSlots.Length > 0)
            {
                ConfigNode storedParts = mv.AddNode("STOREDPARTS");
                for (int i = 0; i < occupiedSlots.Length; i++)
                {
                    ConfigNode sp = storedParts.AddNode("STOREDPART");
                    sp.AddValue("slotIndex", occupiedSlots[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                    sp.AddValue("partName", "dummyPart");
                }
            }
            return mv;
        }

        // catches: NRE / wrong sentinel on a null module node.
        [Fact]
        public void UnloadedScan_NullModuleValues_ReturnsMinusOne()
        {
            int slot = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                null, 9, 0, 0, new HashSet<InventorySlotAddress>(), out _, out _);
            Assert.Equal(-1, slot);
        }

        // catches: losing the stock InventorySlots=9 fallback when the proto
        // module doesn't persist the KSPField.
        [Fact]
        public void UnloadedScan_MissingInventorySlots_FallsBackToNineSlots()
        {
            ConfigNode mv = MakeInventoryModuleValues(inventorySlots: null);
            var consumed = new HashSet<InventorySlotAddress>();
            for (int s = 0; s < 9; s++)
                consumed.Add(new InventorySlotAddress(0, 0, s));

            int slot = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 9, 0, 0, consumed, out _, out int consumedCount);

            Assert.Equal(-1, slot); // all 9 fallback slots consumed → full
            Assert.Equal(9, consumedCount);
        }

        // catches: assuming the stock 9-slot default for an unpersisted
        // InventorySlots on a SMALLER container — the probe would hand out
        // phantom slot indices past the real count, which the unloaded writer
        // persists as UI-inaccessible stores.
        [Fact]
        public void UnloadedScan_SmallerPrefabFallback_NoPhantomSlots()
        {
            ConfigNode mv = MakeInventoryModuleValues(inventorySlots: null, 0, 1, 2);

            // Prefab-resolved fallback of 3 (e.g. a stock SEQ container): all
            // real slots occupied must mean FULL, not "slot 3 free".
            int slot = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 3, 0, 0, new HashSet<InventorySlotAddress>(), out int occupiedCount, out _);

            Assert.Equal(-1, slot);
            Assert.Equal(3, occupiedCount);
        }

        // catches: int.TryParse failure zeroing the slot count — a
        // present-but-unparseable InventorySlots value (mod/MM garbage like
        // "9.0") must fall back instead of reporting the module full.
        [Fact]
        public void UnloadedScan_UnparseableInventorySlots_UsesFallback()
        {
            ConfigNode mv = MakeInventoryModuleValues(inventorySlots: null, 0, 1);
            mv.AddValue("InventorySlots", "9.0");

            int slot = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 3, 0, 0, new HashSet<InventorySlotAddress>(), out _, out _);

            Assert.Equal(2, slot); // fallback of 3, slots 0-1 occupied
        }

        // catches: a negative persisted InventorySlots value being trusted.
        [Fact]
        public void UnloadedScan_NegativeInventorySlots_UsesFallback()
        {
            ConfigNode mv = MakeInventoryModuleValues(inventorySlots: null, 0);
            mv.AddValue("InventorySlots", "-2");

            int slot = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 2, 0, 0, new HashSet<InventorySlotAddress>(), out _, out _);

            Assert.Equal(1, slot);
        }

        // catches: occupied STOREDPART slots not being skipped, or the scan
        // stopping before the first genuinely free index.
        [Fact]
        public void UnloadedScan_OccupiedSlots_SkippedToFirstFree()
        {
            ConfigNode mv = MakeInventoryModuleValues(3, 0, 1);

            int slot = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 9, 0, 0, new HashSet<InventorySlotAddress>(), out int occupiedCount, out _);

            Assert.Equal(2, slot);
            Assert.Equal(2, occupiedCount);
        }

        // catches: a full module reporting a phantom slot instead of -1 (the
        // caller must then walk on to the NEXT module).
        [Fact]
        public void UnloadedScan_FullModule_ReturnsMinusOne()
        {
            ConfigNode mv = MakeInventoryModuleValues(3, 0, 1, 2);

            int slot = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 9, 0, 0, new HashSet<InventorySlotAddress>(), out int occupiedCount, out _);

            Assert.Equal(-1, slot);
            Assert.Equal(3, occupiedCount);
        }

        // catches: the consumed-set lookup ignoring the module qualifier — a
        // consumed slot 0 on ANOTHER module must not block THIS module's slot 0.
        [Fact]
        public void UnloadedScan_ConsumedSlotOnOtherModule_DoesNotBlock()
        {
            ConfigNode mv = MakeInventoryModuleValues(3);
            var consumed = new HashSet<InventorySlotAddress>
            {
                new InventorySlotAddress(0, 0, 0), // module (0,0) slot 0 consumed
                new InventorySlotAddress(2, 0, 0), // some other part's module
            };

            int slotThisModule = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 9, 0, 0, consumed, out _, out int consumedCount);
            int slotOtherModule = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                mv, 9, 0, 1, consumed, out _, out int consumedOther);

            Assert.Equal(1, slotThisModule);  // slot 0 consumed on (0,0)
            Assert.Equal(1, consumedCount);
            Assert.Equal(0, slotOtherModule); // (0,1) unaffected by (0,0)/(2,0)
            Assert.Equal(0, consumedOther);
        }

        // catches: the multi-module walk regression itself, in miniature — a
        // full first module must hand over to the second module's first free
        // slot instead of ending the probe.
        [Fact]
        public void UnloadedScan_FirstModuleFull_SecondModuleProvidesSlot()
        {
            ConfigNode moduleA = MakeInventoryModuleValues(2, 0, 1); // full
            ConfigNode moduleB = MakeInventoryModuleValues(2, 0);    // slot 1 free
            var consumed = new HashSet<InventorySlotAddress>();

            int slotA = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                moduleA, 9, 0, 0, consumed, out _, out _);
            int slotB = LiveDeliveryCapacityProbe.FindFirstEmptySlotInUnloadedModule(
                moduleB, 9, 1, 0, consumed, out _, out _);

            Assert.Equal(-1, slotA);
            Assert.Equal(1, slotB);
        }

        // ==================================================================
        // StoreIntoUnloadedInventoryModule (module-targeted unloaded store)
        // ==================================================================

        private static ConfigNode MakeStoredPartPayload(string partName, int originalSlotIndex)
        {
            var node = new ConfigNode("STOREDPART");
            node.AddValue("partName", partName);
            node.AddValue("slotIndex", originalSlotIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            node.AddValue("quantity", "1");
            node.AddNode("PART");
            return node;
        }

        // catches: missing STOREDPARTS child not being created, or the payload's
        // original slotIndex leaking through instead of the assigned one.
        [Fact]
        public void UnloadedStore_CreatesStoredPartsAndOverridesSlotIndex()
        {
            ConfigNode mv = MakeInventoryModuleValues(3);
            ConfigNode payload = MakeStoredPartPayload("smallPart", originalSlotIndex: 7);

            bool stored = LiveDeliveryWriters.StoreIntoUnloadedInventoryModule(mv, payload, 2);

            Assert.True(stored);
            ConfigNode storedParts = mv.GetNode("STOREDPARTS");
            Assert.NotNull(storedParts);
            ConfigNode[] nodes = storedParts.GetNodes("STOREDPART");
            Assert.Single(nodes);
            Assert.Equal("2", nodes[0].GetValue("slotIndex"));
            Assert.Equal("smallPart", nodes[0].GetValue("partName"));
            // Payload node itself must be untouched (the writer stores a copy).
            Assert.Equal("7", payload.GetValue("slotIndex"));
        }

        // catches: the store clobbering existing STOREDPART children on the
        // targeted module.
        [Fact]
        public void UnloadedStore_AppendsWithoutDisturbingExistingStoredParts()
        {
            ConfigNode mv = MakeInventoryModuleValues(3, 0);
            ConfigNode payload = MakeStoredPartPayload("newPart", 0);

            bool stored = LiveDeliveryWriters.StoreIntoUnloadedInventoryModule(mv, payload, 1);

            Assert.True(stored);
            ConfigNode[] nodes = mv.GetNode("STOREDPARTS").GetNodes("STOREDPART");
            Assert.Equal(2, nodes.Length);
            Assert.Equal("0", nodes[0].GetValue("slotIndex"));
            Assert.Equal("dummyPart", nodes[0].GetValue("partName"));
            Assert.Equal("1", nodes[1].GetValue("slotIndex"));
            Assert.Equal("newPart", nodes[1].GetValue("partName"));
        }

        // catches: null / negative-slot inputs silently "succeeding".
        [Fact]
        public void UnloadedStore_InvalidInputs_ReturnFalse()
        {
            ConfigNode mv = MakeInventoryModuleValues(3);
            ConfigNode payload = MakeStoredPartPayload("p", 0);

            Assert.False(LiveDeliveryWriters.StoreIntoUnloadedInventoryModule(null, payload, 0));
            Assert.False(LiveDeliveryWriters.StoreIntoUnloadedInventoryModule(mv, null, 0));
            Assert.False(LiveDeliveryWriters.StoreIntoUnloadedInventoryModule(mv, payload, -1));
            Assert.Null(mv.GetNode("STOREDPARTS"));
        }

        // ==================================================================
        // WriteInventory logging contract
        // ==================================================================

        // catches: the per-item "Inventory store" Info line losing the
        // module-qualified slot address or the stored=0/1 outcome — the
        // pickup/delivery pair must stay traceable in KSP.log. Headless, so
        // the vessel is null and the store fails; the log line must still
        // carry the full address and stored=0.
        [Fact]
        public void WriteInventory_LogsModuleQualifiedAddressAndOutcome()
        {
            var route = new Route { Id = "route-log-test" };
            var writers = new LiveDeliveryWriters(route, null, DeliveryPlan.Empty(), isLoaded: false);
            var item = new InventoryPayloadItem
            {
                PartName = "smallPart",
                Quantity = 1,
                SlotsTaken = 1,
                StoredPartSnapshot = MakeStoredPartPayload("smallPart", 0),
            };

            writers.WriteInventory(item, new InventorySlotAddress(1, 2, 3));

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("Inventory store:")
                && l.Contains("route=route-log-test")
                && l.Contains("part=smallPart")
                && l.Contains("slot=part1/mod2/slot3")
                && l.Contains("stored=0")
                && l.Contains("path=unloaded"));
            Assert.Equal(0, writers.ReadInventoryActualCount());
        }

        // catches: an invalid (planner-skipped) address reaching the writer and
        // logging / counting anyway — the writer must silently no-op on None.
        [Fact]
        public void WriteInventory_NoneAddress_IsSilentNoOp()
        {
            var writers = new LiveDeliveryWriters(new Route { Id = "r" }, null, DeliveryPlan.Empty(), isLoaded: false);
            var item = new InventoryPayloadItem
            {
                PartName = "smallPart",
                StoredPartSnapshot = MakeStoredPartPayload("smallPart", 0),
            };

            writers.WriteInventory(item, InventorySlotAddress.None);

            Assert.DoesNotContain(logLines, l => l.Contains("Inventory store:"));
            Assert.Equal(0, writers.ReadInventoryActualCount());
        }
    }
}
