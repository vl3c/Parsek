using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pin the pure all-or-nothing origin-cargo gate (M1 origin debit,
    /// design D3) and the D6 inventory-deferral predicate. Pure-method
    /// tests; no KSP statics, no shared state.
    /// </summary>
    public class RouteOriginCargoCheckTests
    {
        private static System.Func<string, double> Reader(Dictionary<string, double> stored)
        {
            return name => stored.TryGetValue(name, out double v) ? v : 0.0;
        }

        // catches: a fully-stocked origin being held (false negative at the gate).
        [Fact]
        public void HasRequired_AllCovered_True()
        {
            var manifest = new Dictionary<string, double>
            {
                { "LiquidFuel", 100.0 },
                { "Oxidizer", 120.0 },
            };
            var stored = new Dictionary<string, double>
            {
                { "LiquidFuel", 100.0 }, // exact-equal boundary counts as covered
                { "Oxidizer", 500.0 },
            };

            bool ok = RouteOriginCargoCheck.HasRequired(
                manifest, Reader(stored), out string lacking, out double shortfall);

            Assert.True(ok);
            Assert.Equal(string.Empty, lacking);
            Assert.Equal(0.0, shortfall);
        }

        // catches: the first-failure pick depending on dictionary insertion
        // order instead of ordinal name order (the hold reason must be stable
        // across ticks). All three resources are short; "LiquidFuel" sorts
        // first ordinally even though it was inserted last.
        [Fact]
        public void HasRequired_NamesFirstShortResourceOrdinal()
        {
            var manifest = new Dictionary<string, double>
            {
                { "Oxidizer", 120.0 },
                { "MonoPropellant", 40.0 },
                { "LiquidFuel", 100.0 },
            };
            var stored = new Dictionary<string, double>
            {
                { "Oxidizer", 10.0 },
                { "MonoPropellant", 5.0 },
                { "LiquidFuel", 25.0 },
            };

            bool ok = RouteOriginCargoCheck.HasRequired(
                manifest, Reader(stored), out string lacking, out double shortfall);

            Assert.False(ok);
            Assert.Equal("LiquidFuel", lacking);
            Assert.Equal(75.0, shortfall); // 100 required - 25 stored
        }

        // catches: zero / negative manifest entries failing the gate when the
        // origin holds none of that resource (non-positive entries carry no
        // debit and must be skipped).
        [Fact]
        public void HasRequired_SkipsNonPositiveEntries()
        {
            var manifest = new Dictionary<string, double>
            {
                { "Ablator", 0.0 },
                { "XenonGas", -5.0 },
                { "LiquidFuel", 50.0 },
            };
            var stored = new Dictionary<string, double>
            {
                { "LiquidFuel", 60.0 },
                // Ablator / XenonGas absent: reader returns 0.
            };

            bool ok = RouteOriginCargoCheck.HasRequired(
                manifest, Reader(stored), out string lacking, out _);

            Assert.True(ok);
            Assert.Equal(string.Empty, lacking);
        }

        // catches: an empty or null manifest holding the route (nothing to
        // verify must pass).
        [Fact]
        public void HasRequired_EmptyManifest_True()
        {
            Assert.True(RouteOriginCargoCheck.HasRequired(
                null, Reader(new Dictionary<string, double>()), out _, out _));
            Assert.True(RouteOriginCargoCheck.HasRequired(
                new Dictionary<string, double>(), Reader(new Dictionary<string, double>()), out _, out _));
        }

        // catches: a null stored-reader silently passing the gate (must fail
        // closed; the all-or-nothing contract cannot be verified without a
        // reader).
        [Fact]
        public void HasRequired_NullReader_FailsClosed()
        {
            var manifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } };

            bool ok = RouteOriginCargoCheck.HasRequired(
                manifest, null, out string lacking, out _);

            Assert.False(ok);
            Assert.Equal("null-stored-reader", lacking);
        }

        // catches (D6): a non-KSC route with inventory payload NOT holding
        // (delivering items without debiting them would duplicate matter),
        // or a KSC / item-free route being held by the deferral.
        [Fact]
        public void RequiresInventoryDebit_NonKscWithInventory_True()
        {
            var nonKscWithItems = new Route
            {
                Id = "route-inv",
                IsKscOrigin = false,
                InventoryCostManifest = new List<InventoryPayloadItem>
                {
                    new InventoryPayloadItem { IdentityHash = "h1", PartName = "science.module" },
                },
            };
            Assert.True(RouteOriginCargoCheck.RequiresInventoryDebit(nonKscWithItems));

            // KSC origin: funds carry the cost, no physical debit at all.
            var kscWithItems = new Route
            {
                Id = "route-ksc",
                IsKscOrigin = true,
                InventoryCostManifest = new List<InventoryPayloadItem>
                {
                    new InventoryPayloadItem { IdentityHash = "h1", PartName = "science.module" },
                },
            };
            Assert.False(RouteOriginCargoCheck.RequiresInventoryDebit(kscWithItems));

            // Non-KSC with no items: resources-only debit is supported in M1.
            var nonKscEmpty = new Route { Id = "route-res", IsKscOrigin = false };
            Assert.False(RouteOriginCargoCheck.RequiresInventoryDebit(nonKscEmpty));
            nonKscEmpty.InventoryCostManifest = new List<InventoryPayloadItem>();
            Assert.False(RouteOriginCargoCheck.RequiresInventoryDebit(nonKscEmpty));

            Assert.False(RouteOriginCargoCheck.RequiresInventoryDebit(null));
        }
    }
}
