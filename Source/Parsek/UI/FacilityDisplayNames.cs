using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The one place a ledger facility id becomes the name the player reads: the Timeline's
    /// facility rows, and the facility id the Career State window reads slot-limit levels
    /// by.
    ///
    /// A ledger facility id comes in three shapes: an upgrade carries the facility's own id
    /// (<c>SpaceCenter/LaunchPad</c>), a destruction or repair the id of ONE of its
    /// destructible buildings (<c>SpaceCenter/LaunchPad/Facility/&lt;part&gt;</c>), and some
    /// producers write the bare id (<c>LaunchPad</c>). All three reduce to the facility id
    /// (<see cref="FacilityIdForBuilding"/>), which is named by stock's own localized name
    /// when KSP can answer, else by the humanized id.
    /// </summary>
    internal static class FacilityDisplayNames
    {
        /// <summary>Row text when a facility action carries no id at all.</summary>
        internal const string UnknownFacilityText = "Unknown facility";

        /// <summary>
        /// Test seam for the stock facility display-name lookup. When non-null it replaces
        /// <c>ScenarioUpgradeableFacilities.GetFacilityName</c>.
        /// </summary>
        internal static Func<string, string> FacilityNameLookupForTesting;

        // Stock names resolved once per facility id. Only successful lookups are cached,
        // so a lookup attempted before KSP's Localizer is up is retried on the next build.
        private static readonly Dictionary<string, string> facilityNameCache =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// The facility a ledger facility id belongs to: the segment after
        /// <c>SpaceCenter/</c> (or the first segment of an unprefixed id).
        /// </summary>
        internal static string FacilityIdForBuilding(string buildingId)
        {
            if (string.IsNullOrEmpty(buildingId)) return "";
            const string prefix = "SpaceCenter/";
            string rest = buildingId.StartsWith(prefix, StringComparison.Ordinal)
                ? buildingId.Substring(prefix.Length)
                : buildingId;
            int slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        /// <summary>
        /// The readable name of the facility any ledger facility id (facility-level or
        /// building-level) belongs to, e.g. <c>SpaceCenter/LaunchPad/Facility/...</c> ->
        /// <c>Launchpad</c>. Never returns an empty string.
        /// </summary>
        internal static string ResolveBuildingDisplayName(string buildingId)
        {
            string facilityId = FacilityIdForBuilding(buildingId);
            if (string.IsNullOrEmpty(facilityId)) return UnknownFacilityText;
            return ResolveFacilityDisplayName(facilityId);
        }

        /// <summary>
        /// The facility's display name: stock's own localized name
        /// (<c>ScenarioUpgradeableFacilities.GetFacilityName</c>, e.g. "Research and
        /// Development", "Launchpad") when KSP can answer, else the humanized id.
        /// </summary>
        internal static string ResolveFacilityDisplayName(string facilityId)
        {
            if (string.IsNullOrEmpty(facilityId)) return facilityId;
            var lookup = FacilityNameLookupForTesting;
            string stock;
            if (lookup != null)
            {
                stock = lookup(facilityId);
            }
            else
            {
                if (facilityNameCache.TryGetValue(facilityId, out string cached))
                    return cached;
                stock = LookupStockFacilityName(facilityId);
                if (IsUsableStockName(stock))
                    facilityNameCache[facilityId] = stock;
            }
            return IsUsableStockName(stock) ? stock : HumanizeFacilityId(facilityId);
        }

        /// <summary>
        /// Fallback facility name when stock cannot answer (headless tests, an unknown
        /// id): PascalCase split, with the conjunction lowercased the way stock writes it
        /// ("ResearchAndDevelopment" -> "Research and Development").
        /// </summary>
        internal static string HumanizeFacilityId(string facilityId)
        {
            string spaced = CareerStateWindowUI.SpaceBeforeCapitals(facilityId);
            if (string.IsNullOrEmpty(spaced)) return spaced;
            return spaced.Replace(" And ", " and ");
        }

        private static bool IsUsableStockName(string name)
        {
            // An unresolved localization tag comes back verbatim ("#autoLOC_6001646").
            return !string.IsNullOrEmpty(name) && name[0] != '#';
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string LookupStockFacilityName(string facilityId)
        {
            try
            {
                return LookupStockFacilityNameCore(facilityId);
            }
            catch (Exception ex)
            {
                ParsekLog.VerboseRateLimited("UI",
                    "FacilityDisplayNames.lookupThrew",
                    $"FacilityDisplayNames: facility name lookup threw id={facilityId} ex={ex.GetType().Name}");
                return null;
            }
        }

        // Separate NoInlining core: mono resolves a KSP type's failing initializer when it
        // JITs the CALLING method, so the call that can throw headlessly sits one frame
        // below the try/catch that handles it.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string LookupStockFacilityNameCore(string facilityId)
        {
            SpaceCenterFacility facility;
            if (!Enum.TryParse(facilityId, false, out facility)) return null;
            return ScenarioUpgradeableFacilities.GetFacilityName(facility);
        }
    }
}
