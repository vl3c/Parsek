using System;

namespace Parsek.Tests.Generators
{
    /// <summary>
    /// Community-Resource-Pack-style mod-resource constants shared by the M2
    /// resource-generality suites (plan D11), so every suite exercises the
    /// same names: four defined mod resources and one deliberately UNDEFINED
    /// name standing in for an uninstalled mod's resource. No live
    /// <see cref="PartResourceLibrary"/> in xUnit - tests inject
    /// <see cref="DefinedLookup"/> via
    /// <c>ResourceTransferability.DefinitionLookupOverrideForTesting</c>.
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

        /// <summary>
        /// Definition-lookup seam value: every name is defined EXCEPT
        /// <see cref="UninstalledModResource"/>. Stock names (LiquidFuel,
        /// Ore, ...) used alongside the CRP names in mixed fixtures stay
        /// defined, so installing this seam never flips an unrelated case.
        /// </summary>
        internal static readonly Func<string, bool> DefinedLookup =
            name => name != UninstalledModResource;
    }
}
