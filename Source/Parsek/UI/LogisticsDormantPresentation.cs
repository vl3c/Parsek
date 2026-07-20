using System.Globalization;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helpers for the Logistics window's Dormant Routes
    /// section (rewind-visibility extension follow-up: the collapsed disclosure
    /// listing routes that went dormant at a rewind and have not re-materialized
    /// yet). All methods are Unity-free and side-effect-free so they are unit
    /// tested directly off the IMGUI path (mirrors
    /// <see cref="LogisticsDeliveryPresentation"/>). IMGUI drawing and the live
    /// store reads stay in <see cref="LogisticsWindowUI"/>; this class only
    /// formats already-resolved inputs. InvariantCulture for every numeric piece.
    /// </summary>
    internal static class LogisticsDormantPresentation
    {
        /// <summary>
        /// Visibility predicate for the whole section: shown ONLY while at
        /// least one route is dormant. An empty dormant list renders nothing
        /// (no header, no empty bubble) - the common no-rewind save stays
        /// visually unchanged.
        /// </summary>
        internal static bool ShouldShowDormantSection(int dormantCount)
        {
            return dormantCount > 0;
        }

        /// <summary>
        /// Disclosure-header title carrying the count, e.g. "Dormant Routes (2)".
        /// </summary>
        internal static string DormantSectionTitle(int dormantCount)
        {
            return $"Dormant Routes ({dormantCount.ToString(CultureInfo.InvariantCulture)})";
        }

        /// <summary>
        /// Display label for a dormant route: the player-visible Name, falling
        /// back to the short route id and then "&lt;unnamed&gt;" so a row is
        /// never blank (mirrors the window's NearMissTreeLabel fallback chain).
        /// </summary>
        internal static string DormantRouteDisplayName(string name, string id)
        {
            if (!string.IsNullOrEmpty(name))
                return name;
            if (!string.IsNullOrEmpty(id))
                return RouteIds.Short(id);
            return "<unnamed>";
        }

        /// <summary>
        /// The "appears at ..." cell: <paramref name="formattedDate"/> is the
        /// already-formatted creation date (the window formats
        /// <see cref="Route.CreatedUT"/> via <c>KSPUtil.PrintDateCompact</c>,
        /// the house date formatter, guarded off-Unity). A negative
        /// <paramref name="createdUT"/> or a null/empty formatted date renders
        /// the honest "&lt;unknown&gt;" instead of a bogus epoch date -
        /// defensive only, since a route without a CreatedUT stamp never goes
        /// dormant.
        /// </summary>
        internal static string DormantAppearsLabel(double createdUT, string formattedDate)
        {
            if (createdUT < 0.0 || string.IsNullOrEmpty(formattedDate))
                return "appears at <unknown>";
            return $"appears at {formattedDate}";
        }

        /// <summary>
        /// Body text for the dormant-route delete confirm dialog (mirrors
        /// <see cref="LogisticsWindowUI.BuildDeleteConfirmBody"/> but names the
        /// dormant state so the player knows the route never re-materializes).
        /// </summary>
        internal static string BuildDeleteDormantConfirmBody(string displayName)
        {
            string name = string.IsNullOrEmpty(displayName) ? "<unnamed>" : displayName;
            return $"Delete dormant route '{name}'?\n\n" +
                "It will never re-materialize when the timeline reaches its creation point. " +
                "This cannot be undone.";
        }
    }
}
