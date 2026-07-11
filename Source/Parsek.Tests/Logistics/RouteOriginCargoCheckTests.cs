using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
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

        // catches (M2 resource generality): the origin-cargo gate is
        // name-agnostic - CRP-style mod resources gate exactly like stock
        // names, covered passes and the first short CRP resource is named
        // ordinally with its shortfall.
        [Fact]
        public void HasRequired_CrpNames_CoveredAndShort()
        {
            var manifest = new Dictionary<string, double>
            {
                { CrpFixtures.Supplies, 200.0 },
                { CrpFixtures.Karbonite, 80.0 },
            };
            var covered = new Dictionary<string, double>
            {
                { CrpFixtures.Supplies, 200.0 },
                { CrpFixtures.Karbonite, 100.0 },
            };
            Assert.True(RouteOriginCargoCheck.HasRequired(
                manifest, Reader(covered), out string lackingCovered, out _));
            Assert.Equal(string.Empty, lackingCovered);

            var shortStored = new Dictionary<string, double>
            {
                { CrpFixtures.Supplies, 50.0 },
                { CrpFixtures.Karbonite, 10.0 },
            };
            Assert.False(RouteOriginCargoCheck.HasRequired(
                manifest, Reader(shortStored), out string lacking, out double shortfall));
            // "Karbonite" sorts before "Supplies" ordinally.
            Assert.Equal(CrpFixtures.Karbonite, lacking);
            Assert.Equal(70.0, shortfall); // 80 required - 10 stored
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

        // ------------------------------------------------------------------
        // BuildInventoryShortToken (hold-token legibility + near-miss)
        // ------------------------------------------------------------------

        private static List<InventoryPayloadItem> ManifestWith(string hash, string partName)
        {
            return new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem { IdentityHash = hash, PartName = partName, Quantity = 1 },
            };
        }

        // catches: the token carrying the opaque hash when the manifest can
        // name the part (the player-legibility contract).
        [Fact]
        public void BuildInventoryShortToken_AbsentPart_NamesPart()
        {
            string token = RouteOriginCargoCheck.BuildInventoryShortToken(
                ManifestWith("hash-1", "evaJetpack"), "hash-1", shortQuantity: 1,
                countByPartName: _ => 0, out int stateMismatch);

            Assert.Equal("inventory:evaJetpack", token);
            Assert.Equal(0, stateMismatch);
        }

        // catches: a physically-present part with drifted state (different
        // identity hash) reading as "missing" instead of the near-miss token.
        [Fact]
        public void BuildInventoryShortToken_NearMiss_StateToken()
        {
            // Required 1, stored-by-hash 0 (shortQuantity 1); 2 same-name parts
            // exist -> both are state-drifted extras.
            string token = RouteOriginCargoCheck.BuildInventoryShortToken(
                ManifestWith("hash-1", "evaJetpack"), "hash-1", shortQuantity: 1,
                countByPartName: name => name == "evaJetpack" ? 2 : 0, out int stateMismatch);

            Assert.Equal("inventory-state:evaJetpack", token);
            Assert.Equal(2, stateMismatch);
        }

        // catches: a plain QUANTITY shortfall misclassified as state drift when
        // the by-name count includes the exact-identity matches the gate DID
        // find (required 2, one PERFECT copy stored: the depot is one unit
        // short, nothing drifted - the token must say "missing", not "state
        // differs").
        [Fact]
        public void BuildInventoryShortToken_QuantityShortWithExactMatches_NotStateToken()
        {
            var manifest = new List<InventoryPayloadItem>
            {
                new InventoryPayloadItem { IdentityHash = "hash-1", PartName = "battery", Quantity = 2 },
            };
            // Stored 1 exact match (shortQuantity = 2 - 1 = 1); by-name count 1
            // (only the exact match itself).
            string token = RouteOriginCargoCheck.BuildInventoryShortToken(
                manifest, "hash-1", shortQuantity: 1,
                countByPartName: name => name == "battery" ? 1 : 0, out int stateMismatch);

            Assert.Equal("inventory:battery", token);
            Assert.Equal(0, stateMismatch);

            // With one EXTRA drifted same-name copy (by-name 2 > exact 1) the
            // near-miss fires.
            string driftToken = RouteOriginCargoCheck.BuildInventoryShortToken(
                manifest, "hash-1", shortQuantity: 1,
                countByPartName: name => name == "battery" ? 2 : 0, out int driftCount);
            Assert.Equal("inventory-state:battery", driftToken);
            Assert.Equal(1, driftCount);
        }

        // catches: an unresolvable identity (a special marker like
        // "null-stored-counter", or a manifest mismatch) losing its raw token,
        // and a null by-name probe throwing instead of skipping the near-miss.
        [Fact]
        public void BuildInventoryShortToken_Fallbacks()
        {
            // Marker identity not present in the manifest: raw token survives.
            Assert.Equal("inventory:null-stored-counter",
                RouteOriginCargoCheck.BuildInventoryShortToken(
                    ManifestWith("hash-1", "evaJetpack"), "null-stored-counter", 1,
                    countByPartName: _ => 5, out _));

            // Null manifest: raw token.
            Assert.Equal("inventory:hash-1",
                RouteOriginCargoCheck.BuildInventoryShortToken(
                    null, "hash-1", 1, countByPartName: _ => 5, out _));

            // Manifest item without a PartName: raw token (never a blank name).
            Assert.Equal("inventory:hash-1",
                RouteOriginCargoCheck.BuildInventoryShortToken(
                    ManifestWith("hash-1", null), "hash-1", 1,
                    countByPartName: _ => 5, out _));

            // Null probe: absent-part token, no near-miss classification.
            Assert.Equal("inventory:evaJetpack",
                RouteOriginCargoCheck.BuildInventoryShortToken(
                    ManifestWith("hash-1", "evaJetpack"), "hash-1", 1,
                    countByPartName: null, out int stateMismatch));
            Assert.Equal(0, stateMismatch);
        }
    }
}
