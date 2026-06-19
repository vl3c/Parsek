using System.Globalization;
using Parsek.InGameTests.Helpers;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the ONE pure piece of the in-game auto-spawn fixture
    /// (<see cref="UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel"/>): rewriting
    /// the donor pad-rocket VESSEL snapshot so the spawned copy carries the requested
    /// stored LiquidFuel + free capacity. The live spawn / unloaded-wait / cleanup
    /// path is in-game only (validated via Ctrl+Shift+T); this is the extractable
    /// ConfigNode-only decision.
    /// </summary>
    public class UnloadedFuelVesselFixtureTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private const string LiquidFuelName = "LiquidFuel";

        private static ConfigNode Resource(string name, double amount, double maxAmount)
        {
            var res = new ConfigNode("RESOURCE");
            res.AddValue("name", name);
            res.AddValue("amount", amount.ToString("R", IC));
            res.AddValue("maxAmount", maxAmount.ToString("R", IC));
            return res;
        }

        private static ConfigNode Part(params ConfigNode[] resources)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "testTank");
            foreach (var r in resources)
                part.AddNode(r);
            return part;
        }

        private static ConfigNode Vessel(params ConfigNode[] parts)
        {
            var vessel = new ConfigNode("VESSEL");
            foreach (var p in parts)
                vessel.AddNode(p);
            return vessel;
        }

        private static double Amount(ConfigNode resourceNode) =>
            double.Parse(resourceNode.GetValue("amount"), NumberStyles.Float, IC);

        private static double MaxAmount(ConfigNode resourceNode) =>
            double.Parse(resourceNode.GetValue("maxAmount"), NumberStyles.Float, IC);

        [Fact]
        public void AdjustSnapshotLiquidFuel_NullNode_ReturnsFalseWithReason()
        {
            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                null, 10.0, 5.0, out string reason);
            Assert.False(ok);
            Assert.Equal("null-vessel-node", reason);
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_NoParts_ReturnsFalse()
        {
            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                Vessel(), 10.0, 5.0, out string reason);
            Assert.False(ok);
            Assert.Equal("no-parts", reason);
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_NoLiquidFuelResource_ReturnsFalse()
        {
            var vessel = Vessel(Part(Resource("Oxidizer", 100.0, 100.0)));
            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, 10.0, 5.0, out string reason);
            Assert.False(ok);
            Assert.Equal("no-liquidfuel-resource", reason);
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_SmallTank_BumpsAmountAndMaxToMeetFloors()
        {
            // A tiny near-empty tank: must be raised to stored>=10, free>=5 => max>=15.
            var lf = Resource(LiquidFuelName, 0.5, 1.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, 10.0, 5.0, out string reason);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.Equal(10.0, Amount(lf), 6);
            Assert.True(MaxAmount(lf) - Amount(lf) >= 5.0 - 1e-9,
                $"free capacity must be >= 5 (max={MaxAmount(lf)} amount={Amount(lf)})");
            Assert.Equal(15.0, MaxAmount(lf), 6);
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_LargeExistingTank_PreservesLargerCapacity()
        {
            // A large tank already exceeding the requested totals keeps its capacity;
            // only the stored amount is set to the floor (free capacity stays larger).
            var lf = Resource(LiquidFuelName, 2.0, 1000.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, 10.0, 5.0, out _);

            Assert.True(ok);
            Assert.Equal(10.0, Amount(lf), 6);
            Assert.Equal(1000.0, MaxAmount(lf), 6); // larger existing capacity preserved
            Assert.True(MaxAmount(lf) - Amount(lf) >= 5.0);
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_SetsFlowStateTrue()
        {
            // A flow-locked tank would be skipped by the production probe/writer, so
            // the freshly-shaped tank is forced flowing.
            var lf = Resource(LiquidFuelName, 0.0, 0.0);
            lf.AddValue("flowState", "False");
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, 10.0, 5.0, out _);

            Assert.True(ok);
            Assert.Equal("True", lf.GetValue("flowState"));
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_OnlyFirstLiquidFuelTankShaped()
        {
            // Two LF tanks: the first is shaped to the floor, the second is untouched.
            var lf1 = Resource(LiquidFuelName, 0.0, 1.0);
            var lf2 = Resource(LiquidFuelName, 3.0, 7.0);
            var vessel = Vessel(Part(lf1), Part(lf2));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, 10.0, 5.0, out _);

            Assert.True(ok);
            Assert.Equal(10.0, Amount(lf1), 6);
            // Second tank left exactly as authored.
            Assert.Equal(3.0, Amount(lf2), 6);
            Assert.Equal(7.0, MaxAmount(lf2), 6);
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_ZeroFloors_StillSetsAmountAndFlow()
        {
            // Degenerate floors: amount clamps to 0, max>=amount, flow forced true.
            var lf = Resource(LiquidFuelName, 42.0, 100.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, 0.0, 0.0, out _);

            Assert.True(ok);
            Assert.Equal(0.0, Amount(lf), 6);
            Assert.Equal(100.0, MaxAmount(lf), 6); // larger existing capacity preserved
            Assert.Equal("True", lf.GetValue("flowState"));
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_NegativeFloors_TreatedAsZero()
        {
            var lf = Resource(LiquidFuelName, 5.0, 5.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, -10.0, -3.0, out _);

            Assert.True(ok);
            Assert.Equal(0.0, Amount(lf), 6);
            Assert.True(MaxAmount(lf) >= Amount(lf));
        }
    }
}
