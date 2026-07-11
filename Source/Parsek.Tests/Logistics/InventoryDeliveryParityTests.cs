using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the pure seams of the loaded/unloaded inventory-delivery parity
    /// fix: the volume/mass admission core the live probe dispatches through
    /// (<see cref="LiveDeliveryCapacityProbe.ComputeUnitsThatFit"/>) and the
    /// STOREDPART node the unloaded writer appends
    /// (<see cref="LiveDeliveryWriters.BuildUnloadedStoredPartNode"/>). The
    /// live halves (prefab lookups, proto walks, StoreCargoPartAtSlot +
    /// UpdateStackAmountAtSlot) are covered by the Logistics in-game tests.
    /// </summary>
    public class InventoryDeliveryParityTests
    {
        // ==================================================================
        // ComputeUnitsThatFit — the volume/mass admission core
        // ==================================================================

        // catches: an unlimited container (limit <= 0, stock HasPackedVolumeLimit /
        // HasMassLimit false) constraining admission.
        [Fact]
        public void ComputeUnitsThatFit_NoLimits_AdmitsAll()
        {
            int fit = LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                perUnitVolume: 100.0, perUnitMass: 50.0,
                freeVolume: -1000.0, freeMass: -1000.0,
                hasVolumeLimit: false, hasMassLimit: false,
                requestedUnits: 7);
            Assert.Equal(7, fit);
        }

        // catches: volume floor rounding up (admitting a unit that doesn't fit).
        [Fact]
        public void ComputeUnitsThatFit_VolumeConstrains()
        {
            int fit = LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                perUnitVolume: 30.0, perUnitMass: 0.1,
                freeVolume: 100.0, freeMass: 1000.0,
                hasVolumeLimit: true, hasMassLimit: true,
                requestedUnits: 5);
            Assert.Equal(3, fit); // floor(100 / 30)
        }

        // catches: mass limit not applied when volume passes.
        [Fact]
        public void ComputeUnitsThatFit_MassConstrains()
        {
            int fit = LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                perUnitVolume: 1.0, perUnitMass: 0.4,
                freeVolume: 1000.0, freeMass: 1.0,
                hasVolumeLimit: true, hasMassLimit: true,
                requestedUnits: 5);
            Assert.Equal(2, fit); // floor(1.0 / 0.4)
        }

        // catches: negative headroom (container already over its limit) admitting
        // units instead of clamping to zero.
        [Fact]
        public void ComputeUnitsThatFit_NegativeHeadroom_AdmitsZero()
        {
            int fit = LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                perUnitVolume: 10.0, perUnitMass: 0.1,
                freeVolume: -5.0, freeMass: 100.0,
                hasVolumeLimit: true, hasMassLimit: true,
                requestedUnits: 3);
            Assert.Equal(0, fit);
        }

        // catches: a zero-footprint item (packedVolume 0) being divided by zero or
        // refused; stock admits it on that axis.
        [Fact]
        public void ComputeUnitsThatFit_ZeroPerUnitFootprint_DoesNotConstrain()
        {
            int fit = LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                perUnitVolume: 0.0, perUnitMass: 0.0,
                freeVolume: 0.0, freeMass: 0.0,
                hasVolumeLimit: true, hasMassLimit: true,
                requestedUnits: 4);
            Assert.Equal(4, fit);
        }

        // catches: non-positive request returning garbage.
        [Fact]
        public void ComputeUnitsThatFit_NonPositiveRequest_ReturnsZero()
        {
            Assert.Equal(0, LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                1.0, 1.0, 100.0, 100.0, true, true, 0));
            Assert.Equal(0, LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                1.0, 1.0, 100.0, 100.0, true, true, -3));
        }

        // ==================================================================
        // BuildUnloadedStoredPartNode — the unloaded writer's persisted shape
        // ==================================================================

        private static ConfigNode BuildRecordedStoredPart()
        {
            // Shape a recorded payload takes: origin slot + full manifest
            // quantity + stock stack metadata + nested PART.
            var snapshot = new ConfigNode("STOREDPART");
            snapshot.AddValue("slotIndex", "5");
            snapshot.AddValue("partName", "evaRepairKit");
            snapshot.AddValue("quantity", "10");
            snapshot.AddValue("stackCapacity", "4");
            snapshot.AddValue("variantName", "");
            var part = snapshot.AddNode("PART");
            part.AddValue("name", "evaRepairKit");
            part.AddValue("moduleCargoStackableQuantity", "4");
            return snapshot;
        }

        // catches: the planner-assigned slot or the per-slot unit count not
        // overriding the recorded payload's origin slot / whole-manifest quantity —
        // the Gap C shape that persisted quantity=10 into one slot.
        [Fact]
        public void BuildUnloadedStoredPartNode_OverridesSlotAndQuantity()
        {
            ConfigNode node = LiveDeliveryWriters.BuildUnloadedStoredPartNode(
                BuildRecordedStoredPart(), slot: 2, units: 4);

            Assert.Equal("STOREDPART", node.name);
            Assert.Equal("2", node.GetValue("slotIndex"));
            Assert.Equal("4", node.GetValue("quantity"));
            // Exactly one value each — the recorded ones were removed, not shadowed.
            Assert.Single(node.GetValues("slotIndex"));
            Assert.Single(node.GetValues("quantity"));
        }

        // catches: the builder canonicalizing / dropping payload identity fields
        // (stackCapacity, variantName, nested PART) or mutating the source node.
        [Fact]
        public void BuildUnloadedStoredPartNode_PreservesPayloadAndSource()
        {
            ConfigNode source = BuildRecordedStoredPart();
            string sourceBefore = source.ToString();

            ConfigNode node = LiveDeliveryWriters.BuildUnloadedStoredPartNode(source, 0, 4);

            Assert.Equal("4", node.GetValue("stackCapacity"));
            Assert.Equal("evaRepairKit", node.GetValue("partName"));
            Assert.NotNull(node.GetNode("PART"));
            Assert.Equal("4", node.GetNode("PART").GetValue("moduleCargoStackableQuantity"));
            // Source untouched (the payload is reused across slots and cycles).
            Assert.Equal(sourceBefore, source.ToString());
        }
    }
}
