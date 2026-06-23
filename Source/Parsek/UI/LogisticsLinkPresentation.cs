using System.Collections.Generic;
using Parsek.Logistics;

namespace Parsek
{
    /// <summary>
    /// Pure presentation helper for the Logistics window round-trip link control
    /// (M4c). Builds the eligible-partner candidate list for the link picker and
    /// formats the detail-panel pairing note. Keeping the eligibility filter pure
    /// (mirrors <see cref="LogisticsRenamePresentation"/>) makes it unit-testable off
    /// the IMGUI path; the window owns the popup draw + the
    /// <see cref="RouteStore.LinkRoutes"/> write + the log line. Unity-free and
    /// side-effect-free.
    /// </summary>
    internal static class LogisticsLinkPresentation
    {
        /// <summary>One selectable partner-route option in the link picker.</summary>
        internal struct LinkCandidate
        {
            public string Id;
            public string Name;
        }

        /// <summary>
        /// Builds the eligible round-trip partner list for <paramref name="sourceId"/>:
        /// every OTHER route (<c>Id != sourceId</c>) that is currently UNLINKED
        /// (<see cref="Route.LinkedRouteId"/> == null). An already-linked route is
        /// excluded so the player cannot create a 3-way tangle (the engine's partner
        /// gate has no concept of more than one partner); a null route or a route with
        /// an empty Id is skipped. Input order is preserved. The window renders the
        /// result as a single-select list and commits the chosen id through
        /// <see cref="RouteStore.LinkRoutes"/>. Returns an empty list (never null) when
        /// <paramref name="routes"/> is null, <paramref name="sourceId"/> is empty, or
        /// no eligible partner exists.
        /// </summary>
        internal static List<LinkCandidate> BuildLinkCandidates(IReadOnlyList<Route> routes, string sourceId)
        {
            var result = new List<LinkCandidate>();
            if (routes == null || string.IsNullOrEmpty(sourceId))
                return result;

            for (int i = 0; i < routes.Count; i++)
            {
                Route r = routes[i];
                if (r == null || string.IsNullOrEmpty(r.Id))
                    continue;
                if (string.Equals(r.Id, sourceId, System.StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(r.LinkedRouteId))
                    continue;
                result.Add(new LinkCandidate
                {
                    Id = r.Id,
                    Name = string.IsNullOrEmpty(r.Name) ? r.Id : r.Name
                });
            }
            return result;
        }

        /// <summary>
        /// The detail-panel pairing note shown when a route is round-trip linked,
        /// naming the partner. A null/empty partner name renders as
        /// <c>&lt;unnamed&gt;</c> so the note is never blank.
        /// </summary>
        internal static string FormatLinkedNote(string partnerName)
        {
            string name = string.IsNullOrEmpty(partnerName) ? "<unnamed>" : partnerName;
            return $"Round-trip linked to '{name}' (alternates: dispatches only after its partner completes a run).";
        }
    }
}
