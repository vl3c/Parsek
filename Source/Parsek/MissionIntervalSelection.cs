using System.Collections.Generic;

namespace Parsek
{
    // Interval-level Missions selection: turns "which composition intervals are included" into a
    // per-vessel RENDER WINDOW. This is the data behind start-trimming a mission - keeping only a
    // late segment of a vessel (e.g. the post-decouple coast, not the launch) or keeping only a
    // peeled branch (the booster) while dropping its trunk.
    //
    // The unit of selection is the composition interval (MissionCompositionNode with
    // IsSelectable == true): every structural segment of a vessel (launch stack, post-decouple
    // survivor, ...) and every peeled branch (probe, EVA kerbal). Each carries an OwnerHeadId =
    // the through-line head of the physical vessel it belongs to. An interval is INCLUDED unless
    // its HeadLegId is in the excluded set (no cascade - intervals toggle independently, which is
    // what lets you keep a downstream segment while dropping the upstream one).
    //
    // The render window for a vessel = the [min start, max end] over its INCLUDED intervals. A
    // vessel whose intervals are all excluded is absent (fully dropped from the render). This is
    // a contiguous window per vessel: dropping the leading interval(s) start-trims the vessel
    // (the span clock then starts later); dropping the trailing interval(s) end-trims it. Pure:
    // no Unity calls, no shared mutable state.
    internal static class MissionIntervalSelection
    {
        internal struct RenderWindow
        {
            public double StartUT;
            public double EndUT;
        }

        /// <summary>
        /// Computes the render window for each INCLUDED through-line vessel (keyed by OwnerHeadId)
        /// from the composition roots and the set of excluded interval keys (HeadLegIds). A vessel
        /// with no included interval is omitted (fully dropped). Pure.
        /// </summary>
        internal static Dictionary<string, RenderWindow> ComputeRenderWindows(
            List<MissionCompositionNode> roots, ICollection<string> excludedIntervalKeys)
        {
            var windows = new Dictionary<string, RenderWindow>();
            if (roots == null)
                return windows;
            for (int i = 0; i < roots.Count; i++)
                Accumulate(roots[i], excludedIntervalKeys, windows);
            return windows;
        }

        /// <summary>
        /// True when the given through-line head's vessel renders at all under this selection
        /// (i.e. at least one of its intervals is included).
        /// </summary>
        internal static bool IsVesselIncluded(
            List<MissionCompositionNode> roots, ICollection<string> excludedIntervalKeys, string ownerHeadId)
        {
            return ComputeRenderWindows(roots, excludedIntervalKeys).ContainsKey(ownerHeadId);
        }

        private static void Accumulate(
            MissionCompositionNode node, ICollection<string> excluded,
            Dictionary<string, RenderWindow> windows)
        {
            if (node == null)
                return;

            // Only real intervals / branches count toward a window; roster atoms are not selectable
            // and carry no span. An interval is included unless its key is in the excluded set.
            if (node.IsSelectable && !string.IsNullOrEmpty(node.OwnerHeadId)
                && (excluded == null || !excluded.Contains(node.HeadLegId)))
            {
                if (!windows.TryGetValue(node.OwnerHeadId, out RenderWindow w))
                {
                    windows[node.OwnerHeadId] = new RenderWindow { StartUT = node.StartUT, EndUT = node.EndUT };
                }
                else
                {
                    if (node.StartUT < w.StartUT) w.StartUT = node.StartUT;
                    if (node.EndUT > w.EndUT) w.EndUT = node.EndUT;
                    windows[node.OwnerHeadId] = w;
                }
            }

            for (int i = 0; i < node.Children.Count; i++)
                Accumulate(node.Children[i], excluded, windows);
        }
    }
}
