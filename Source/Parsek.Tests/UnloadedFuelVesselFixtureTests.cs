using System.Collections.Generic;
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

        // ==================================================================
        // capStoredLf: bound a shared source so it covers one pickup but not two
        // (the escrow competing-route hold). The cap forces EXACT stored + a tank
        // just large enough for it + the requested free capacity (donor's large
        // capacity NOT preserved).
        // ==================================================================

        [Fact]
        public void AdjustSnapshotLiquidFuel_Cap_OnLargeTank_ClampsExactAndDoesNotPreserveCapacity()
        {
            // A large donor tank (1000 max). Capping to 5 stored with free=1 must
            // shrink the tank to exactly 6 max - NOT keep the 1000 capacity - so the
            // spawned source holds exactly 5 LF (one pickup of 4, not two).
            var lf = Resource(LiquidFuelName, 800.0, 1000.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, minStoredLf: 5.0, minFreeCapacity: 1.0, out string reason, capStoredLf: 5.0);

            Assert.True(ok);
            Assert.Null(reason);
            Assert.Equal(5.0, Amount(lf), 6);             // exact cap, overrides the donor's 800 stored
            Assert.Equal(6.0, MaxAmount(lf), 6);          // cap + free, donor's 1000 NOT preserved
            Assert.True(MaxAmount(lf) - Amount(lf) >= 1.0 - 1e-9);
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_Cap_OverridesMinStoredFloor()
        {
            // capStoredLf wins over minStoredLf: the cap is the exact stored amount
            // even when minStoredLf is larger.
            var lf = Resource(LiquidFuelName, 0.0, 0.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, minStoredLf: 100.0, minFreeCapacity: 2.0, out _, capStoredLf: 5.0);

            Assert.True(ok);
            Assert.Equal(5.0, Amount(lf), 6);     // cap, NOT the 100 floor
            Assert.Equal(7.0, MaxAmount(lf), 6);  // cap + free
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_Cap_NegativeTreatedAsZero()
        {
            var lf = Resource(LiquidFuelName, 50.0, 100.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, minStoredLf: 10.0, minFreeCapacity: 3.0, out _, capStoredLf: -7.0);

            Assert.True(ok);
            Assert.Equal(0.0, Amount(lf), 6);     // negative cap clamps to 0
            Assert.Equal(3.0, MaxAmount(lf), 6);  // 0 + free
        }

        [Fact]
        public void AdjustSnapshotLiquidFuel_Cap_BoundsBelowTwicePickup()
        {
            // The escrow contract: stored covers one pickup but not two.
            const double pickup = 4.0;
            var lf = Resource(LiquidFuelName, 9999.0, 99999.0);
            var vessel = Vessel(Part(lf));

            bool ok = UnloadedFuelVesselFixture.AdjustSnapshotLiquidFuel(
                vessel, minStoredLf: pickup + 1.0, minFreeCapacity: 1.0, out _, capStoredLf: pickup + 1.0);

            Assert.True(ok);
            double stored = Amount(lf);
            Assert.True(stored >= pickup, $"source must cover one pickup (stored={stored} pickup={pickup})");
            Assert.True(stored < 2.0 * pickup, $"source must NOT cover two pickups (stored={stored} 2x={2.0 * pickup})");
        }

        // ==================================================================
        // IsReuseExcluded: a second multi-source provisioning call must NOT reuse
        // an already-provisioned depot's pid (distinct-depot fix).
        // ==================================================================

        [Fact]
        public void IsReuseExcluded_NullSet_NeverExcludes()
        {
            Assert.False(UnloadedFuelVesselFixture.IsReuseExcluded(1600022941u, null));
        }

        [Fact]
        public void IsReuseExcluded_EmptySet_NeverExcludes()
        {
            Assert.False(UnloadedFuelVesselFixture.IsReuseExcluded(1600022941u, new HashSet<uint>()));
        }

        [Fact]
        public void IsReuseExcluded_PidInSet_Excludes()
        {
            // The KSP.log repro: depot A spawned pid=1600022941, then the depot B call
            // reused that SAME pid. Excluding it forces a fresh distinct-pid spawn.
            var set = new HashSet<uint> { 1600022941u };
            Assert.True(UnloadedFuelVesselFixture.IsReuseExcluded(1600022941u, set));
        }

        [Fact]
        public void IsReuseExcluded_OtherPidNotInSet_NotExcluded()
        {
            // A freshly-spawned depot B (distinct pid) is NOT excluded, so it resolves
            // as the second source.
            var set = new HashSet<uint> { 1600022941u };
            Assert.False(UnloadedFuelVesselFixture.IsReuseExcluded(1600099999u, set));
        }
    }
}
