using System.Collections.Generic;

namespace Parsek
{
    // The single definition of the Mission include/exclude cascade. A Mission stores its
    // selection as the set of EXCLUDED through-line head ids (see Mission.cs). Excluding a
    // head drops that through-line AND every offshoot downstream of it: exclusion cascades
    // down the OffshootHeadIds graph. This rule is shared by the Missions window (greying /
    // checkbox state) and the Mission-to-LoopUnitSet adapter so there is exactly one copy
    // of the cascade. Pure: no Unity calls, no shared mutable state.
    internal static class MissionSelection
    {
        /// <summary>
        /// Returns the set of through-line head ids that are INCLUDED for a Mission whose
        /// excluded-head set is <paramref name="excludedHeadIds"/>. Walks from
        /// <c>view.RootHeadIds</c> down each through-line's <c>OffshootHeadIds</c>; a head is
        /// included iff it is NOT in <paramref name="excludedHeadIds"/> AND its parent is
        /// included. Once a head is excluded, none of its descendants are included. A
        /// <c>visited</c> set guards merges/cycles so no head is traversed twice. A null view
        /// yields an empty set; a null excluded set means nothing is excluded (everything is
        /// included).
        /// </summary>
        internal static HashSet<string> ComputeIncludedHeadIds(
            MissionThroughLineView view, ICollection<string> excludedHeadIds)
        {
            var included = new HashSet<string>();
            if (view == null)
                return included;

            var visited = new HashSet<string>();
            var roots = view.RootHeadIds;
            for (int r = 0; r < roots.Count; r++)
                Walk(view, excludedHeadIds, roots[r], true, included, visited);

            return included;
        }

        // Depth-first walk down OffshootHeadIds. parentIncluded is the cascaded inclusion of
        // the chain above this head; thisIncluded = parentIncluded AND head-not-excluded.
        // A head reached a second time (merge) or a cycle is not re-traversed.
        private static void Walk(
            MissionThroughLineView view, ICollection<string> excludedHeadIds, string headId,
            bool parentIncluded, HashSet<string> included, HashSet<string> visited)
        {
            if (headId == null || !visited.Add(headId))
                return;
            if (!view.ByHeadId.TryGetValue(headId, out MissionThroughLine tl))
                return;

            bool selfExcluded = excludedHeadIds != null && excludedHeadIds.Contains(headId);
            bool thisIncluded = parentIncluded && !selfExcluded;
            if (thisIncluded)
                included.Add(headId);

            var offshoots = tl.OffshootHeadIds;
            for (int i = 0; i < offshoots.Count; i++)
                Walk(view, excludedHeadIds, offshoots[i], thisIncluded, included, visited);
        }
    }
}
