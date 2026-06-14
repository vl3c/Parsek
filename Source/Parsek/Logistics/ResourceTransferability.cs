using System;

namespace Parsek.Logistics
{
    /// <summary>
    /// The single authority for the logistics resource-transferability rule
    /// (design 19.4 M2 item 1, plan D1). The rule, stated once:
    ///
    /// Any resource with a <see cref="PartResourceDefinition"/> in
    /// <see cref="PartResourceLibrary"/> is routable, modded resources
    /// included; zero-cost defined resources route normally (they simply
    /// contribute 0 to the funds charge). ElectricCharge and IntakeAir stay
    /// excluded as environmental noise. An undefined resource name in a
    /// recorded snapshot (its defining mod was uninstalled) is excluded
    /// from ADMISSION-direction outputs (the delivery manifest, and through
    /// it the route's CostManifest and funds charge) and logged - but it
    /// stays VISIBLE to REJECTION-direction checks
    /// (<see cref="RouteAnalysisEngine"/>'s pickup gate keeps rejecting an
    /// undefined-name pickup), so a mod uninstall can never flip a
    /// rejection into Eligible (plan D2, direction-sensitive exclusion).
    ///
    /// Capture stays permissive: <see cref="VesselSpawner"/>'s manifest
    /// extraction records whatever names the snapshot carries (recordings
    /// are immutable witnesses; a definition check at capture would bake
    /// mod-install state into recorded data), so reinstalling the mod
    /// restores routability.
    /// </summary>
    internal static class ResourceTransferability
    {
        internal const string ReasonAlwaysIgnored = "always-ignored";
        internal const string ReasonUndefined = "undefined";
        internal const string ReasonEmptyName = "empty-name";

        /// <summary>
        /// Test seam replacing the production <see cref="PartResourceLibrary"/>
        /// probe: <c>name -> has a PartResourceDefinition</c>. Null (the
        /// default) routes through the production lookup.
        /// </summary>
        internal static Func<string, bool> DefinitionLookupOverrideForTesting;

        // One-shot guard for the null-library fallback diagnostic: the
        // condition is process-wide (library missing entirely), so it logs
        // once, not once per probed name.
        private static bool nullLibraryFallbackLogged;

        internal static void ResetForTesting()
        {
            DefinitionLookupOverrideForTesting = null;
            nullLibraryFallbackLogged = false;
        }

        /// <summary>
        /// ElectricCharge and IntakeAir are environmental noise, never
        /// meaningful supply cargo: a docked transport recharges its batteries
        /// from the depot and its IntakeAir reading drifts between the dock
        /// and undock snapshots, so either can show a spurious per-resource
        /// delta on an otherwise clean delivery-only run. Design section 5.3
        /// rule 7 ("after EC/IntakeAir filtering") and section 6 ("EC-only
        /// delivery ... remains excluded") filter them out of BOTH the pickup
        /// gate and the delivery manifest, matching
        /// <c>VesselSpawner.ExtractResourceManifest</c>. These two are the
        /// only always-ignored resources the design names.
        /// </summary>
        internal static bool IsAlwaysIgnored(string name)
        {
            return name == "ElectricCharge" || name == "IntakeAir";
        }

        /// <summary>
        /// The transferability rule decided for one name: routable iff the
        /// name is non-empty, not always-ignored, and defined. On false,
        /// <paramref name="excludeReason"/> carries
        /// <see cref="ReasonEmptyName"/> / <see cref="ReasonAlwaysIgnored"/> /
        /// <see cref="ReasonUndefined"/>; on true it is null. Callers own
        /// the logging of admission-side skips (M2 logging plan row 1).
        /// </summary>
        internal static bool IsRoutableResource(string name, out string excludeReason)
        {
            if (string.IsNullOrEmpty(name))
            {
                excludeReason = ReasonEmptyName;
                return false;
            }

            if (IsAlwaysIgnored(name))
            {
                excludeReason = ReasonAlwaysIgnored;
                return false;
            }

            if (!IsDefinedResource(name))
            {
                excludeReason = ReasonUndefined;
                return false;
            }

            excludeReason = null;
            return true;
        }

        /// <summary>
        /// True when <see cref="PartResourceLibrary"/> carries a definition
        /// for the name. Defensive contract (plan D1): a null library (xUnit,
        /// early load) treats every name as defined with a one-shot Verbose -
        /// the exclusion must fail OPEN, never reject every resource
        /// headlessly. The try/catch mirrors
        /// <c>LiveRouteRuntimeEnvironment.LookupResourceUnitCost</c>'s
        /// defensive shape, also failing open.
        /// </summary>
        private static bool IsDefinedResource(string name)
        {
            Func<string, bool> overrideLookup = DefinitionLookupOverrideForTesting;
            if (overrideLookup != null)
                return overrideLookup(name);

            try
            {
                if (PartResourceLibrary.Instance == null)
                {
                    LogNullLibraryFallbackOnce();
                    return true;
                }

                return PartResourceLibrary.Instance.GetDefinition(name) != null;
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(RouteOrchestrator.Tag,
                    $"IsDefinedResource({name}) threw {ex.GetType().Name}: {ex.Message}; treating as defined");
                return true;
            }
        }

        // M2 logging plan row 2: one-shot, Verbose - a missing library is a
        // process-wide environmental condition, not a per-name event.
        private static void LogNullLibraryFallbackOnce()
        {
            if (nullLibraryFallbackLogged)
                return;

            nullLibraryFallbackLogged = true;
            ParsekLog.Verbose(RouteOrchestrator.Tag,
                "PartResourceLibrary unavailable; treating resource names as defined");
        }
    }
}
