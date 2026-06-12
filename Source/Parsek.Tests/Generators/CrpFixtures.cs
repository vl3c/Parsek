using System;
using System.Collections.Generic;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// Community-Resource-Pack-style mod-resource constants shared by the M2
    /// resource-generality suites (plan D11), so every suite exercises the
    /// same names and unit costs: three priced resources, one zero-cost
    /// defined resource (routes normally, contributes 0 funds), and one
    /// deliberately UNDEFINED name standing in for an uninstalled mod's
    /// resource. No live <see cref="PartResourceLibrary"/> in xUnit - tests
    /// inject <see cref="DefinedLookup"/> via
    /// <c>ResourceTransferability.DefinitionLookupOverrideForTesting</c> and
    /// <see cref="UnitCostLookup"/> via the funds calculator's delegate.
    /// </summary>
    internal static class CrpFixtures
    {
        internal const string Karbonite = "Karbonite";
        internal const string MetallicOre = "MetallicOre";
        internal const string Uraninite = "Uraninite";
        internal const string Supplies = "Supplies";

        /// <summary>
        /// The one name <see cref="DefinedLookup"/> reports as undefined -
        /// a resource recorded while its mod was installed, analyzed after
        /// the mod was removed.
        /// </summary>
        internal const string UninstalledModResource = "UninstalledModResource";

        // Representative unit costs (funds per unit). MetallicOre is the
        // zero-cost defined resource the transferability rule must still
        // route; UninstalledModResource has no entry on purpose.
        internal const float KarboniteUnitCost = 0.04f;
        internal const float MetallicOreUnitCost = 0f;
        internal const float UraniniteUnitCost = 8f;
        internal const float SuppliesUnitCost = 0.5f;

        private static readonly Dictionary<string, float> UnitCosts =
            new Dictionary<string, float>
            {
                { Karbonite, KarboniteUnitCost },
                { MetallicOre, MetallicOreUnitCost },
                { Uraninite, UraniniteUnitCost },
                { Supplies, SuppliesUnitCost },
            };

        /// <summary>
        /// Definition-lookup seam value: every name is defined EXCEPT
        /// <see cref="UninstalledModResource"/>. Stock names (LiquidFuel,
        /// Ore, ...) used alongside the CRP names in mixed fixtures stay
        /// defined, so installing this seam never flips an unrelated case.
        /// </summary>
        internal static readonly Func<string, bool> DefinedLookup =
            name => name != UninstalledModResource;

        /// <summary>
        /// Unit-cost delegate for <c>RouteFundsCalculator</c> fixtures:
        /// the costs above, 0 for any unknown name (matching the production
        /// <c>LookupResourceUnitCost</c> undefined-name behavior).
        /// </summary>
        internal static readonly Func<string, float> UnitCostLookup =
            name => UnitCosts.TryGetValue(name, out float cost) ? cost : 0f;
    }
}
