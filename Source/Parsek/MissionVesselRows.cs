using System.Collections.Generic;
using System.Text;

namespace Parsek
{
    // T2.2 (mission-presentation analysis §3 Tier 2): the FLATTENED per-vessel row model the
    // Missions tab renders instead of the interval staircase. One row per physical vessel (or
    // EVA kerbal); depth encodes ONLY separation lineage (the booster under the ship it left),
    // never time; the vessel's own structural intervals become an expandable detail rather than
    // the default reading surface. Derived purely from the composition trees
    // (MissionCompositionBuilder output): a run of nodes sharing one OwnerHeadId is one vessel,
    // a child with a different OwnerHeadId is a vessel that separated from it.
    //
    // Selection stays interval-keyed and NON-CASCADING: the per-vessel include affordance
    // expands to the vessel's OWN explicit interval keys (never a child vessel's), so
    // Mission.ExcludedIntervalKeys semantics are untouched - see IntervalKeys /
    // ApplyVesselInclusion.
    //
    // Pure: no Unity calls, no shared mutable state.

    internal sealed class MissionVesselRow
    {
        public string OwnerHeadId;        // the through-line head recording id (row identity)
        public string VesselName;         // vessel name, or the kerbal's name for an EVA row
        public bool IsPerson;             // an EVA kerbal (a person, not a vessel)
        public double StartUT;            // first interval start
        public double EndUT;              // last interval end
        public string StartEvent;         // what created this vessel ("Launch", "Decoupled", "EVA")
        public string EndEvent;           // how it ended (terminal word of the last interval)
        public string EventPhrase;        // "Launch → Decoupled (Booster) → Landed"

        // This vessel's own composition intervals, in time order (the expandable detail).
        public readonly List<MissionCompositionNode> Intervals = new List<MissionCompositionNode>();

        // Vessels / kerbals that separated FROM this one, by separation UT (the lineage depth).
        public readonly List<MissionVesselRow> Children = new List<MissionVesselRow>();
    }

    // How much of a vessel's own interval set the mission's excluded-key set keeps.
    internal enum MissionVesselInclusion
    {
        All,
        Partial,
        None,
    }

    internal static class MissionVesselRowBuilder
    {
        /// <summary>
        /// Builds the flattened vessel rows from the composition roots: one row per physical
        /// vessel / EVA kerbal, children = the pieces that separated from it.
        /// <para><paramref name="dockPartnerResolver"/> (optional) maps
        /// (ownerHeadId, boundaryStartUT) to the same-tree dock partner's vessel name so a
        /// Dock / Board boundary in the event phrase reads <c>"Docked (Munport Station)"</c>
        /// instead of the bare word - without it (T1.4) the partner would be unreachable on
        /// the default collapsed surface. Pure apart from whatever the resolver captures.</para>
        /// </summary>
        internal static List<MissionVesselRow> Build(
            List<MissionCompositionNode> roots,
            System.Func<string, double, string> dockPartnerResolver = null)
        {
            var rows = new List<MissionVesselRow>();
            if (roots == null)
                return rows;
            for (int i = 0; i < roots.Count; i++)
            {
                MissionVesselRow row = BuildRow(roots[i], dockPartnerResolver);
                if (row != null)
                    rows.Add(row);
            }
            return rows;
        }

        // One vessel's row: walk the same-owner survivor chain (the builder chains interval
        // i+1 as a child of interval i), collecting different-owner selectable children as
        // separated child vessels. Roster atoms (not selectable vessels) are skipped - the
        // interval rows already carry the composition label, and the crew are named on the
        // header's narrative line.
        private static MissionVesselRow BuildRow(
            MissionCompositionNode head, System.Func<string, double, string> dockPartnerResolver)
        {
            if (head == null || head.IsAtom || !head.IsSelectable
                || string.IsNullOrEmpty(head.OwnerHeadId))
                return null;

            var row = new MissionVesselRow
            {
                OwnerHeadId = head.OwnerHeadId,
                VesselName = head.VesselName,
                IsPerson = head.IsPerson,
            };

            MissionCompositionNode cur = head;
            while (cur != null)
            {
                row.Intervals.Add(cur);
                MissionCompositionNode next = null;
                for (int i = 0; i < cur.Children.Count; i++)
                {
                    MissionCompositionNode c = cur.Children[i];
                    if (c == null || c.IsAtom || !c.IsSelectable)
                        continue;
                    if (string.Equals(c.OwnerHeadId, row.OwnerHeadId, System.StringComparison.Ordinal))
                    {
                        // The survivor chain: the builder creates exactly one same-owner child
                        // (the next interval). Keep the first, but a SECOND one means the
                        // builder's chaining contract moved under us and intervals are being
                        // dropped from the row - the per-vessel include would then write a
                        // partial key set - so say it loudly instead of silently flattening.
                        if (next == null)
                            next = c;
                        else
                            ParsekLog.Warn("Mission",
                                $"VesselRow: interval '{cur.HeadLegId}' carries a second " +
                                $"same-owner child '{c.HeadLegId}' (owner={row.OwnerHeadId}); " +
                                "keeping the first - flattened row may be missing intervals");
                    }
                    else
                    {
                        MissionVesselRow child = BuildRow(c, dockPartnerResolver);
                        if (child != null)
                            row.Children.Add(child);
                    }
                }
                cur = next;
            }

            MissionCompositionNode first = row.Intervals[0];
            MissionCompositionNode last = row.Intervals[row.Intervals.Count - 1];
            row.StartUT = first.StartUT;
            row.EndUT = last.EndUT;
            row.StartEvent = first.StartEvent ?? "";
            row.EndEvent = last.EndEvent ?? "";

            // Lineage order = separation time (deterministic tiebreak on the head id).
            row.Children.Sort((a, b) =>
            {
                int cmp = a.StartUT.CompareTo(b.StartUT);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.OwnerHeadId, b.OwnerHeadId);
            });

            row.EventPhrase = BuildEventPhrase(row, dockPartnerResolver);
            return row;
        }

        /// <summary>
        /// The inline event chain of one vessel row:
        /// <c>"Launch → Decoupled (Kerbal X Booster) → Docked (Munport Station) → Landed"</c>.
        /// One piece per interval boundary; a separation boundary names the piece that left (the
        /// child row starting at that UT - the first match when several peel at once, same
        /// limitation as the T1.3 labels), and a Dock / Board boundary (or start event) names
        /// the same-tree partner via <paramref name="dockPartnerResolver"/> when one resolves.
        /// A crew (EVA) departure is not a boundary, so it never appears here - the kerbal has
        /// their own child row.
        /// </summary>
        internal static string BuildEventPhrase(
            MissionVesselRow row, System.Func<string, double, string> dockPartnerResolver = null)
        {
            if (row == null || row.Intervals.Count == 0)
                return "";
            var sb = new StringBuilder();
            AppendPhrasePiece(sb, NameEventPiece(row, row.StartEvent, row.StartUT, dockPartnerResolver));
            for (int i = 0; i < row.Intervals.Count; i++)
            {
                MissionCompositionNode interval = row.Intervals[i];
                bool isLast = i == row.Intervals.Count - 1;
                string boundaryEvent = interval.EndEvent ?? "";
                if (boundaryEvent.Length == 0)
                    continue;
                if (!isLast)
                {
                    string peeled = ResolveChildAtBoundary(row, interval.EndUT);
                    AppendPhrasePiece(sb, peeled != null
                        ? boundaryEvent + " (" + peeled + ")"
                        : NameEventPiece(row, boundaryEvent, interval.EndUT, dockPartnerResolver));
                }
                else
                {
                    AppendPhrasePiece(sb, boundaryEvent);
                }
            }
            return sb.ToString();
        }

        // A Dock / Board piece gains the partner's name when the resolver knows it; every other
        // event word passes through unchanged. The boundary UT is the merged interval's start,
        // which is what the T1.4 resolver matches merge legs against.
        private static string NameEventPiece(
            MissionVesselRow row, string eventWord, double boundaryUT,
            System.Func<string, double, string> dockPartnerResolver)
        {
            if (dockPartnerResolver == null || string.IsNullOrEmpty(eventWord)
                || !MissionPresentation.IsDockEventWord(eventWord))
                return eventWord;
            string partner = dockPartnerResolver(row.OwnerHeadId, boundaryUT);
            return string.IsNullOrEmpty(partner) ? eventWord : eventWord + " (" + partner + ")";
        }

        private static void AppendPhrasePiece(StringBuilder sb, string piece)
        {
            if (string.IsNullOrEmpty(piece))
                return;
            if (sb.Length > 0)
                sb.Append(MissionPresentation.SummarySpanArrow);
            sb.Append(piece);
        }

        // The (non-person) child vessel that separated at this boundary UT, if any.
        private static string ResolveChildAtBoundary(MissionVesselRow row, double boundaryUT)
        {
            for (int i = 0; i < row.Children.Count; i++)
            {
                MissionVesselRow c = row.Children[i];
                if (c.IsPerson)
                    continue;
                if (System.Math.Abs(c.StartUT - boundaryUT) <= MissionPresentation.PeelUtEpsilon
                    && !string.IsNullOrEmpty(c.VesselName))
                    return c.VesselName;
            }
            return null;
        }

        /// <summary>
        /// How much of this vessel's OWN interval set the excluded-key set keeps. Children are
        /// deliberately not consulted (no cascade). Pure.
        /// </summary>
        internal static MissionVesselInclusion ClassifyInclusion(
            MissionVesselRow row, ICollection<string> excludedIntervalKeys)
        {
            if (row == null || row.Intervals.Count == 0)
                return MissionVesselInclusion.All;
            int excluded = 0;
            for (int i = 0; i < row.Intervals.Count; i++)
            {
                if (excludedIntervalKeys != null
                    && excludedIntervalKeys.Contains(row.Intervals[i].HeadLegId))
                    excluded++;
            }
            if (excluded == 0)
                return MissionVesselInclusion.All;
            return excluded == row.Intervals.Count
                ? MissionVesselInclusion.None
                : MissionVesselInclusion.Partial;
        }

        /// <summary>
        /// The vessel's OWN interval keys (never a child vessel's) - what the per-vessel include
        /// affordance expands to. Pure.
        /// </summary>
        internal static List<string> IntervalKeys(MissionVesselRow row)
        {
            var keys = new List<string>();
            if (row == null)
                return keys;
            for (int i = 0; i < row.Intervals.Count; i++)
                if (!string.IsNullOrEmpty(row.Intervals[i].HeadLegId))
                    keys.Add(row.Intervals[i].HeadLegId);
            return keys;
        }

        /// <summary>
        /// Applies a per-vessel include / exclude by expanding to the vessel's own EXPLICIT
        /// interval keys: include removes them from the excluded set, exclude adds them. The
        /// non-cascading interval-key contract is untouched - child vessels' keys are never
        /// written. Returns the number of keys whose membership changed. Pure over the passed
        /// set.
        /// </summary>
        internal static int ApplyVesselInclusion(
            MissionVesselRow row, bool include, ICollection<string> excludedIntervalKeys)
        {
            if (row == null || excludedIntervalKeys == null)
                return 0;
            int changed = 0;
            for (int i = 0; i < row.Intervals.Count; i++)
            {
                string key = row.Intervals[i].HeadLegId;
                if (string.IsNullOrEmpty(key))
                    continue;
                if (include)
                {
                    if (excludedIntervalKeys.Remove(key))
                        changed++;
                }
                else if (!excludedIntervalKeys.Contains(key))
                {
                    excludedIntervalKeys.Add(key);
                    changed++;
                }
            }
            return changed;
        }
    }
}
