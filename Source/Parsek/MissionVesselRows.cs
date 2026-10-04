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
    // a child with a different OwnerHeadId is a vessel that separated from it (except another
    // mission's vessel that joined the flight, which sits beside it - MissionVesselRow.IsPartner).
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

        // Another mission's vessel the player switched to and recorded into this flight (a
        // joined run, MissionCompositionNode.IsJoinedVessel, that the naming pass names as a
        // partner). Drawn as a SIBLING of the vessel it joined at, never under it, and not one
        // of this mission's own vessels; its own intervals keep their include affordance.
        public bool IsPartner;

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
        // One Build call's inputs and tally, carried down the recursion.
        private sealed class BuildContext
        {
            public System.Func<string, double, string> DockPartnerResolver;
            public System.Func<string, double, string> TerminalDockPartnerResolver;
            public IReadOnlyDictionary<string, string> VesselNames;
            public ICollection<string> PartnerLegIds;
            public int PartnerRows;
        }

        /// <summary>
        /// Builds the flattened vessel rows from the composition roots: one row per physical
        /// vessel / EVA kerbal, children = the pieces that separated from it.
        /// <para><paramref name="dockPartnerResolver"/> (optional) maps
        /// (ownerHeadId, boundaryStartUT) to the same-tree dock partner's vessel name so a
        /// Dock / Board boundary in the event phrase reads <c>"Docked (Munport Station)"</c>
        /// instead of the bare word - without it (T1.4) the partner would be unreachable on
        /// the default collapsed surface. Pure apart from whatever the resolver captures.</para>
        /// <para><paramref name="vesselNames"/> (optional) is
        /// <see cref="MissionVesselNaming.Build"/>'s map, keyed by the row's head leg: a
        /// vessel row reads that name (<c>"Kerbal X [2]"</c>, or another mission's vessel as
        /// <c>"Kerbal X (mission 'Kerbal X')"</c>), and so does every phrase piece that names
        /// a child row. A kerbal's row keeps the kerbal's name.</para>
        /// <para><paramref name="partnerLegIds"/> (optional) is the same naming pass's partner
        /// legs. A joined vessel (<see cref="MissionCompositionNode.IsJoinedVessel"/>) whose
        /// head leg is in it is another mission's ship the player flew to a dock: its row is a
        /// SIBLING of the vessel it joined at (<see cref="MissionVesselRow.IsPartner"/>), not a
        /// piece that separated from it. Any other joined vessel keeps an ordinary child row.</para>
        /// <para><paramref name="terminalDockPartnerResolver"/> (optional) maps
        /// (ownerHeadId, endUT) to the vessel a row docked into when its line ENDS at a Dock /
        /// Board, so the closing piece reads <c>"Docked (Duna Supply 1)"</c>.</para>
        /// </summary>
        internal static List<MissionVesselRow> Build(
            List<MissionCompositionNode> roots,
            System.Func<string, double, string> dockPartnerResolver = null,
            IReadOnlyDictionary<string, string> vesselNames = null,
            ICollection<string> partnerLegIds = null,
            System.Func<string, double, string> terminalDockPartnerResolver = null)
        {
            var rows = new List<MissionVesselRow>();
            if (roots == null)
                return rows;
            var ctx = new BuildContext
            {
                DockPartnerResolver = dockPartnerResolver,
                TerminalDockPartnerResolver = terminalDockPartnerResolver,
                VesselNames = vesselNames,
                PartnerLegIds = partnerLegIds,
            };
            for (int i = 0; i < roots.Count; i++)
            {
                var siblings = new List<MissionVesselRow>();
                MissionVesselRow row = BuildRow(roots[i], ctx, siblings);
                if (row != null)
                    rows.Add(row);
                SortByStart(siblings);
                rows.AddRange(siblings);
            }
            if (ctx.PartnerRows > 0)
            {
                string firstHead = rows.Count > 0 ? rows[0].OwnerHeadId : "<none>";
                int partners = ctx.PartnerRows;
                ParsekLog.VerboseRateLimited("Mission", "vesselrow-partner-sibling-" + firstHead,
                    () => $"VesselRow: {partners} partner row(s) placed beside the vessel they " +
                          $"joined, not under it (another mission's vessel recorded after a " +
                          $"switch; first row head={firstHead})");
            }
            return rows;
        }

        /// <summary>
        /// The start event of the first row <see cref="Build"/> would produce, without building
        /// any row: what a collapsed mission's bar shows. Pure.
        /// </summary>
        internal static string FirstRowStartEvent(List<MissionCompositionNode> roots)
        {
            if (roots == null) return "";
            for (int i = 0; i < roots.Count; i++)
                if (IsRowHead(roots[i]))
                    return roots[i].StartEvent ?? "";
            return "";
        }

        // A node that heads a row: a selectable, non-atom interval with a through-line head.
        private static bool IsRowHead(MissionCompositionNode node)
            => node != null && !node.IsAtom && node.IsSelectable
               && !string.IsNullOrEmpty(node.OwnerHeadId);

        /// <summary>
        /// True when <paramref name="node"/> heads a joined run whose head leg the naming pass
        /// names as another mission's vessel. Pure.
        /// </summary>
        internal static bool IsPartnerJoin(MissionCompositionNode node, ICollection<string> partnerLegIds)
            => node != null && node.IsJoinedVessel && partnerLegIds != null
               && !string.IsNullOrEmpty(node.OwnerHeadId)
               && partnerLegIds.Contains(node.OwnerHeadId);

        // One vessel's row: walk the same-owner survivor chain (the builder chains interval
        // i+1 as a child of interval i), collecting different-owner selectable children as
        // separated child vessels. Roster atoms (not selectable vessels) are skipped - the
        // interval rows already carry the composition label, and the crew are named on the
        // header's narrative line. A partner join (IsPartnerJoin) never separated from this
        // vessel, so its row goes to siblingsOut - the caller places it at THIS row's level -
        // with its own children under it as usual.
        private static MissionVesselRow BuildRow(
            MissionCompositionNode head, BuildContext ctx, List<MissionVesselRow> siblingsOut)
        {
            if (!IsRowHead(head))
                return null;

            string vesselName = head.VesselName;
            if (!head.IsPerson && ctx.VesselNames != null
                && ctx.VesselNames.TryGetValue(head.OwnerHeadId, out string named)
                && !string.IsNullOrEmpty(named))
                vesselName = named;
            var row = new MissionVesselRow
            {
                OwnerHeadId = head.OwnerHeadId,
                VesselName = vesselName,
                IsPerson = head.IsPerson,
                IsPartner = IsPartnerJoin(head, ctx.PartnerLegIds),
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
                        // Partners joined under the child sit beside the child.
                        var childSiblings = new List<MissionVesselRow>();
                        MissionVesselRow child = BuildRow(c, ctx, childSiblings);
                        if (child == null)
                            continue;
                        List<MissionVesselRow> level = child.IsPartner ? siblingsOut : row.Children;
                        if (child.IsPartner)
                            ctx.PartnerRows++;
                        level.Add(child);
                        level.AddRange(childSiblings);
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
            SortByStart(row.Children);

            row.EventPhrase = BuildEventPhrase(row, ctx.DockPartnerResolver,
                ctx.TerminalDockPartnerResolver);
            return row;
        }

        private static void SortByStart(List<MissionVesselRow> rows)
        {
            rows.Sort((a, b) =>
            {
                int cmp = a.StartUT.CompareTo(b.StartUT);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.OwnerHeadId, b.OwnerHeadId);
            });
        }

        /// <summary>
        /// The inline event chain of one vessel row:
        /// <c>"Launch → Decoupled (Kerbal X Booster) → Docked (Munport Station) → Landed"</c>.
        /// One piece per interval boundary; a separation boundary names the piece that left (the
        /// child row starting at that UT - the first match when several peel at once, same
        /// limitation as the T1.3 labels), and a Dock / Board boundary (or start event) names
        /// the same-tree partner via <paramref name="dockPartnerResolver"/> when one resolves;
        /// a line that ENDS at a Dock / Board names what it docked into via
        /// <paramref name="terminalDockPartnerResolver"/>. A crew (EVA) departure is not a
        /// boundary, so it never appears here - the kerbal has their own child row. An empty
        /// event word (a joined vessel's start, and the edge it cuts in the vessel it joined,
        /// <see cref="MissionCompositionNode.IsJoinedVessel"/>) adds no piece.
        /// </summary>
        internal static string BuildEventPhrase(
            MissionVesselRow row, System.Func<string, double, string> dockPartnerResolver = null,
            System.Func<string, double, string> terminalDockPartnerResolver = null)
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
                    AppendPhrasePiece(sb, NameEventPiece(row, boundaryEvent, interval.EndUT,
                        terminalDockPartnerResolver));
                }
            }
            return sb.ToString();
        }

        // A Dock / Board piece gains the partner's name when the resolver knows it; every other
        // event word passes through unchanged. A boundary UT is the merged interval's start,
        // which is what the T1.4 resolver matches merge legs against; the terminal resolver
        // takes the line's end.
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
                if (!MissionIntervalSelection.IsIntervalIncluded(
                        row.Intervals[i], excludedIntervalKeys))
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
