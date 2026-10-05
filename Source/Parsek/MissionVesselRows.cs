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
    // mission's vessel that joined the flight or undocked from it, which sits beside it -
    // MissionVesselRow.IsPartner).
    //
    // KSP keeps ONE identity for a docked pair, and the recorder lists the half that kept it
    // first at the undock, so the composition run (MissionThroughLineBuilder.ContinuationSuccessor)
    // continues into that half. When the pair carried another mission's identity, that half is
    // the PARTNER's, and this mission's ship left as a separate (re-pidded) child. The rows
    // follow the physical vessel instead (MissionUndockSides, the Log's own-side rule): the
    // ship's row continues into its own post-undock leg, and the partner's half becomes a
    // partner row beside it. The composition, and so every interval key, is untouched: a row
    // only regroups the composition's intervals, it never renames one.
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

        // The run head whose recording carries the row's Interact cell (Fly / Stash + Seal):
        // the run the row ENDS on. The row's own head, except on a row that continues through
        // an own-side undock, where it is the ship's own post-undock run (an Undock is a Re-Fly
        // split, so that run is a real slot - e.g. a lander that undocked and then crashed).
        public string InteractHeadId;
        public string VesselName;         // vessel name, or the kerbal's name for an EVA row
        public bool IsPerson;             // an EVA kerbal (a person, not a vessel)
        public double StartUT;            // first interval start
        public double EndUT;              // last interval end
        public string StartEvent;         // what created this vessel ("Launch", "Decoupled", "EVA")
        public string EndEvent;           // how it ended (terminal word of the last interval)
        public string EventPhrase;        // "Launch → Decoupled (Booster) → Landed"

        // Another mission's vessel recorded into this flight: a joined run
        // (MissionCompositionNode.IsJoinedVessel) the naming pass names as a partner, or a
        // partner's half after an undock (MissionUndockSides.PartnerHalves, and the split rows
        // below). Drawn as a SIBLING of the vessel it joined or left, never under it, and not
        // one of this mission's own vessels; its own intervals keep their include affordance.
        public bool IsPartner;

        // Set on a partner's post-undock half that the builder SPLIT off this mission's ship
        // row (the docked pair carried the partner's identity, so the composition run went on
        // along the partner's half): the name of the ship it undocked from, read by the start
        // piece of the event phrase ("Undocked (Duna Supply 1)"). Null on every other row.
        public string UndockedFromName;

        // The own-side undocks this row continues through: index into Intervals of the own
        // post-undock leg's first interval -> the name of the partner's half that left there
        // (the split partner row). The boundary piece reads "Undocked (Depot ...)" and the
        // interval's detail label "after undock: Depot ... left". Empty on most rows.
        public readonly Dictionary<int, string> OwnSideUndockPartners = new Dictionary<int, string>();

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

    /// <summary>
    /// One undock where this mission's ship left a docked pair that carried ANOTHER mission's
    /// identity: the composition run continues into <see cref="PartnerChildId"/> (the half that
    /// kept the identity, the partner's), and the ship's own post-undock leg
    /// <see cref="OwnChildId"/> is a separate run.
    /// </summary>
    internal sealed class MissionOwnSideUndock
    {
        public string UndockLegId;      // the docked pair's leg that undocked (a partner leg)
        public string OwnChildId;       // this mission's ship after the undock
        public string PartnerChildId;   // the partner's half (the run's continuation)
        public string PartnerVesselName; // the half's recorded vessel name (no naming pass)
    }

    /// <summary>
    /// The undock sides of one mission tree, from the naming pass's partner legs
    /// (<see cref="MissionVesselRowBuilder.ResolveUndockSides"/>). Pure data.
    /// </summary>
    internal sealed class MissionUndockSides
    {
        // Keyed by the own child (the run head the rows stitch onto the ship's row).
        public readonly Dictionary<string, MissionOwnSideUndock> OwnSideByOwnChild =
            new Dictionary<string, MissionOwnSideUndock>(System.StringComparer.Ordinal);

        // Every partner leg born at an undock: when it heads a run (the passive side - this
        // mission's ship kept the identity and the partner left), its row is a partner row
        // beside the ship, not a piece that separated from it.
        public readonly HashSet<string> PartnerHalves =
            new HashSet<string>(System.StringComparer.Ordinal);

        public bool IsEmpty => OwnSideByOwnChild.Count == 0 && PartnerHalves.Count == 0;
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
            public MissionUndockSides UndockSides;
            public int PartnerRows;
            public int OwnSideUndocks;
            public int SkippedOwnSideUndocks;
        }

        // A partner half split off a ship's row: the row identity (the half's own leg) and
        // the ship it undocked from.
        private sealed class SplitHalf
        {
            public string RowId;
            public string VesselName;
            public string UndockedFromName;
        }

        /// <summary>
        /// The undock sides of a mission tree, from the naming pass's partner legs
        /// (<paramref name="partnerLegIds"/>). Every partner leg born at an undock is a
        /// <see cref="MissionUndockSides.PartnerHalves"/> entry. An undock whose undocking leg is
        /// a partner leg, where the run continues into a partner child
        /// (<see cref="MissionThroughLineBuilder.ContinuationSuccessor"/>) while an own child
        /// exists (<see cref="MissionStructureListBuilder.ResolveUndockOwnChild"/>, the Log's
        /// own-side rule), is an own-side undock the rows follow. Empty without a partner set
        /// (the structural reading stays). Pure.
        /// </summary>
        internal static MissionUndockSides ResolveUndockSides(
            MissionStructure structure, ICollection<string> partnerLegIds)
        {
            var sides = new MissionUndockSides();
            if (structure == null || partnerLegIds == null || partnerLegIds.Count == 0)
                return sides;
            var undockKids = new List<string>();
            foreach (MissionLeg leg in structure.LegsById.Values)
            {
                if (leg == null) continue;
                undockKids.Clear();
                for (int i = 0; i < leg.BranchChildIds.Count; i++)
                {
                    string cid = leg.BranchChildIds[i];
                    if (cid != null && structure.LegsById.TryGetValue(cid, out MissionLeg child)
                        && child != null && child.OriginBranchPointType == BranchPointType.Undock)
                        undockKids.Add(cid);
                }
                if (undockKids.Count == 0) continue;
                for (int i = 0; i < undockKids.Count; i++)
                    if (partnerLegIds.Contains(undockKids[i]))
                        sides.PartnerHalves.Add(undockKids[i]);

                string own = MissionStructureListBuilder.ResolveUndockOwnChild(
                    structure, leg.RecordingId, undockKids, partnerLegIds);
                if (own == null) continue;
                string succ = MissionThroughLineBuilder.ContinuationSuccessor(structure, leg);
                if (succ == null || string.Equals(succ, own, System.StringComparison.Ordinal)
                    || !undockKids.Contains(succ) || !partnerLegIds.Contains(succ))
                    continue;
                sides.OwnSideByOwnChild[own] = new MissionOwnSideUndock
                {
                    UndockLegId = leg.RecordingId,
                    OwnChildId = own,
                    PartnerChildId = succ,
                    PartnerVesselName = structure.LegsById[succ].VesselName,
                };
            }
            return sides;
        }

        /// <summary>
        /// True when <paramref name="node"/> heads a run whose row belongs to another mission:
        /// a partner join (<see cref="IsPartnerJoin"/>) or a partner's half born at an undock
        /// (<see cref="MissionUndockSides.PartnerHalves"/>). Pure.
        /// </summary>
        internal static bool IsPartnerRowHead(MissionCompositionNode node,
            ICollection<string> partnerLegIds, MissionUndockSides undockSides)
        {
            if (IsPartnerJoin(node, partnerLegIds))
                return true;
            return node != null && undockSides != null && !string.IsNullOrEmpty(node.OwnerHeadId)
                && string.Equals(node.HeadLegId, node.OwnerHeadId, System.StringComparison.Ordinal)
                && undockSides.PartnerHalves.Contains(node.OwnerHeadId);
        }

        /// <summary>
        /// True when <paramref name="node"/> heads a run that is this mission's own ship after an
        /// own-side undock: the rows continue the ship's row into it, so it is not a vessel of
        /// its own when the row walk follows it. Pure.
        /// </summary>
        internal static bool IsOwnSideUndockHead(MissionCompositionNode node,
            MissionUndockSides undockSides)
            => node != null && undockSides != null && !string.IsNullOrEmpty(node.OwnerHeadId)
               && string.Equals(node.HeadLegId, node.OwnerHeadId, System.StringComparison.Ordinal)
               && undockSides.OwnSideByOwnChild.ContainsKey(node.OwnerHeadId);

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
        /// <para><paramref name="undockSides"/> (optional, <see cref="ResolveUndockSides"/> over
        /// the same partner legs): at an own-side undock the ship's row continues into its own
        /// post-undock leg and the partner's half is split into a partner row beside it; a
        /// partner's half heading its own run is a partner row too. The resolvers are asked
        /// with the composition owner of the interval they name, which is the row's own head
        /// on every row that crosses no own-side undock.</para>
        /// </summary>
        internal static List<MissionVesselRow> Build(
            List<MissionCompositionNode> roots,
            System.Func<string, double, string> dockPartnerResolver = null,
            IReadOnlyDictionary<string, string> vesselNames = null,
            ICollection<string> partnerLegIds = null,
            System.Func<string, double, string> terminalDockPartnerResolver = null,
            MissionUndockSides undockSides = null)
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
                UndockSides = undockSides != null && !undockSides.IsEmpty ? undockSides : null,
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
                          $"joined or left, not under it (another mission's vessel recorded " +
                          $"after a switch, or its half after an undock; first row head={firstHead})");
            }
            if (ctx.OwnSideUndocks > 0 || ctx.SkippedOwnSideUndocks > 0)
            {
                string firstHead = rows.Count > 0 ? rows[0].OwnerHeadId : "<none>";
                int followed = ctx.OwnSideUndocks;
                int skipped = ctx.SkippedOwnSideUndocks;
                ParsekLog.VerboseRateLimited("Mission", "vesselrow-own-side-undock-" + firstHead,
                    () => $"VesselRow: ownSideUndocks={followed} skipped={skipped}: the ship's " +
                          $"row follows its own post-undock leg and the partner's half is a " +
                          $"partner row (the docked pair carried the partner's identity; " +
                          $"first row head={firstHead})");
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
        // header's narrative line. A partner row (IsPartnerRowHead) never separated from this
        // vessel, so its row goes to siblingsOut - the caller places it at THIS row's level -
        // with its own children under it as usual. At an own-side undock (an own row whose
        // interval carries the own post-undock head as a child) the walk swaps: the row goes
        // on along the own head's chain, and the survivor chain (the partner's half) is built
        // as a split partner row into siblingsOut. split != null builds such a half: its row
        // identity is the half's own leg, and it never stitches.
        private static MissionVesselRow BuildRow(
            MissionCompositionNode head, BuildContext ctx, List<MissionVesselRow> siblingsOut,
            SplitHalf split = null)
        {
            if (!IsRowHead(head))
                return null;

            string rowId = split != null ? split.RowId : head.OwnerHeadId;
            // A split half's intervals carry the run head's name (the ship's), so it reads
            // its own leg's name when the naming pass gives none.
            string vesselName = split != null && !string.IsNullOrEmpty(split.VesselName)
                ? split.VesselName : head.VesselName;
            if (!head.IsPerson && ctx.VesselNames != null
                && ctx.VesselNames.TryGetValue(rowId, out string named)
                && !string.IsNullOrEmpty(named))
                vesselName = named;
            var row = new MissionVesselRow
            {
                OwnerHeadId = rowId,
                VesselName = vesselName,
                IsPerson = head.IsPerson,
                IsPartner = split != null
                    || IsPartnerRowHead(head, ctx.PartnerLegIds, ctx.UndockSides),
                UndockedFromName = split?.UndockedFromName,
            };
            // Only this mission's own ship follows its own leg: a row headed by another
            // mission's leg (a partner row, or a partner leg heading a run of its own) keeps
            // the structural reading, its own child staying under it.
            bool mayStitch = !row.IsPartner && !row.IsPerson && ctx.UndockSides != null
                && (ctx.PartnerLegIds == null || !ctx.PartnerLegIds.Contains(head.OwnerHeadId));

            string chainOwner = head.OwnerHeadId;
            MissionCompositionNode cur = head;
            while (cur != null)
            {
                row.Intervals.Add(cur);
                MissionCompositionNode next = null;
                MissionCompositionNode ownHead = mayStitch
                    ? FindOwnSideUndockChild(cur, chainOwner, ctx) : null;
                for (int i = 0; i < cur.Children.Count; i++)
                {
                    MissionCompositionNode c = cur.Children[i];
                    if (c == null || c.IsAtom || !c.IsSelectable)
                        continue;
                    if (string.Equals(c.OwnerHeadId, chainOwner, System.StringComparison.Ordinal))
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
                                $"same-owner child '{c.HeadLegId}' (owner={chainOwner}); " +
                                "keeping the first - flattened row may be missing intervals");
                    }
                    else if (!ReferenceEquals(c, ownHead))
                    {
                        AddChildRow(c, ctx, row, siblingsOut);
                    }
                }

                if (ownHead != null)
                {
                    if (next != null && System.Math.Abs(next.StartUT - ownHead.StartUT)
                        <= MissionPresentation.PeelUtEpsilon)
                    {
                        MissionOwnSideUndock undock =
                            ctx.UndockSides.OwnSideByOwnChild[ownHead.OwnerHeadId];
                        var halfSiblings = new List<MissionVesselRow>();
                        MissionVesselRow half = BuildRow(next, ctx, halfSiblings, new SplitHalf
                        {
                            RowId = undock.PartnerChildId,
                            VesselName = undock.PartnerVesselName,
                            UndockedFromName = row.VesselName,
                        });
                        if (half != null)
                        {
                            siblingsOut.Add(half);
                            ctx.PartnerRows++;
                            row.OwnSideUndockPartners[row.Intervals.Count] = half.VesselName;
                        }
                        siblingsOut.AddRange(halfSiblings);
                        ctx.OwnSideUndocks++;
                        chainOwner = ownHead.OwnerHeadId;
                        next = ownHead;
                    }
                    else
                    {
                        // The run does not go on along the partner's half from this undock (it
                        // ended there): nothing to swap, so the own leg keeps its child row.
                        ctx.SkippedOwnSideUndocks++;
                        AddChildRow(ownHead, ctx, row, siblingsOut);
                    }
                }
                cur = next;
            }

            // A split half's chain is the ship's run, but its slot is its own leg.
            row.InteractHeadId = split != null ? rowId : chainOwner;

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

        // A different-owner child of an interval: a child row under it, or - for a partner
        // row, and the partners joined under the child - a row beside it.
        private static void AddChildRow(MissionCompositionNode c, BuildContext ctx,
            MissionVesselRow row, List<MissionVesselRow> siblingsOut)
        {
            var childSiblings = new List<MissionVesselRow>();
            MissionVesselRow child = BuildRow(c, ctx, childSiblings);
            if (child == null)
                return;
            List<MissionVesselRow> level = child.IsPartner ? siblingsOut : row.Children;
            if (child.IsPartner)
                ctx.PartnerRows++;
            level.Add(child);
            level.AddRange(childSiblings);
        }

        // The own post-undock head hanging off this interval at an own-side undock, if any.
        private static MissionCompositionNode FindOwnSideUndockChild(
            MissionCompositionNode cur, string chainOwner, BuildContext ctx)
        {
            for (int i = 0; i < cur.Children.Count; i++)
            {
                MissionCompositionNode c = cur.Children[i];
                if (c == null || c.IsAtom || !c.IsSelectable || c.IsPerson)
                    continue;
                if (string.Equals(c.OwnerHeadId, chainOwner, System.StringComparison.Ordinal))
                    continue;
                if (IsOwnSideUndockHead(c, ctx.UndockSides))
                    return c;
            }
            return null;
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
            string startEvent = row.StartEvent ?? "";
            AppendPhrasePiece(sb, !string.IsNullOrEmpty(row.UndockedFromName) && startEvent.Length > 0
                ? startEvent + " (" + row.UndockedFromName + ")"
                : NameEventPiece(OwnerOf(row, 0), startEvent, row.StartUT, dockPartnerResolver));
            for (int i = 0; i < row.Intervals.Count; i++)
            {
                MissionCompositionNode interval = row.Intervals[i];
                bool isLast = i == row.Intervals.Count - 1;
                string boundaryEvent = interval.EndEvent ?? "";
                if (boundaryEvent.Length == 0)
                    continue;
                if (!isLast)
                {
                    string peeled = row.OwnSideUndockPartners.TryGetValue(i + 1, out string half)
                        ? half
                        : ResolveChildAtBoundary(row, interval.EndUT);
                    AppendPhrasePiece(sb, peeled != null
                        ? boundaryEvent + " (" + peeled + ")"
                        : NameEventPiece(OwnerOf(row, i + 1), boundaryEvent, interval.EndUT,
                            dockPartnerResolver));
                }
                else
                {
                    AppendPhrasePiece(sb, NameEventPiece(OwnerOf(row, i), boundaryEvent,
                        interval.EndUT, terminalDockPartnerResolver));
                }
            }
            return sb.ToString();
        }

        // The composition owner (through-line head) of a row's interval: what the resolvers
        // walk. The row's own head unless the row crossed an own-side undock (or is a split
        // partner half, whose intervals belong to the run it was split from).
        private static string OwnerOf(MissionVesselRow row, int index)
        {
            if (index >= 0 && index < row.Intervals.Count
                && !string.IsNullOrEmpty(row.Intervals[index].OwnerHeadId))
                return row.Intervals[index].OwnerHeadId;
            return row.OwnerHeadId;
        }

        // A Dock / Board piece gains the partner's name when the resolver knows it; every other
        // event word passes through unchanged. A boundary UT is the merged interval's start,
        // which is what the T1.4 resolver matches merge legs against; the terminal resolver
        // takes the line's end.
        private static string NameEventPiece(
            string ownerHeadId, string eventWord, double boundaryUT,
            System.Func<string, double, string> dockPartnerResolver)
        {
            if (dockPartnerResolver == null || string.IsNullOrEmpty(eventWord)
                || !MissionPresentation.IsDockEventWord(eventWord))
                return eventWord;
            string partner = dockPartnerResolver(ownerHeadId, boundaryUT);
            return string.IsNullOrEmpty(partner) ? eventWord : eventWord + " (" + partner + ")";
        }

        /// <summary>
        /// The label of one interval in a vessel row's expanded detail. Interval 0 and every
        /// interval that does not start at a separation keep <c>"Vessel (composition)"</c>; a
        /// later interval that starts at a separation leads with the piece that left
        /// (<see cref="MissionPresentation.BuildIntervalRowLabel"/>): the peeled sibling in the
        /// composition, or, where the row continues through an own-side undock, the partner's
        /// half that left (<c>"after undock: Depot (mission 'Kerbal X #3') left - (pod x1)"</c>).
        /// A split partner half's first interval reads the row's name, not the composition
        /// run's. Pure.
        /// </summary>
        internal static string IntervalDetailLabel(MissionVesselRow row, int index,
            IReadOnlyDictionary<string, string> vesselNames)
        {
            if (row == null || index < 0 || index >= row.Intervals.Count)
                return "";
            MissionCompositionNode node = row.Intervals[index];
            if (index > 0 && row.OwnSideUndockPartners.TryGetValue(index, out string half))
                return MissionPresentation.BuildIntervalRowLabel(node.VesselName,
                    node.CompositionLabel, false, node.StartEvent, half);
            // A split half's intervals carry the ship's run name; the half reads its own.
            bool splitHalf = !string.IsNullOrEmpty(row.UndockedFromName);
            string vesselName = splitHalf ? row.VesselName : node.VesselName;
            bool isFirstInterval = node.IsAtom || index == 0 && splitHalf
                || string.Equals(node.HeadLegId, node.OwnerHeadId, System.StringComparison.Ordinal);
            string peeled = isFirstInterval || index == 0
                ? null
                : MissionPresentation.ResolvePeeledSiblingVesselName(
                    row.Intervals[index - 1], node, vesselNames);
            return MissionPresentation.BuildIntervalRowLabel(vesselName,
                node.CompositionLabel, isFirstInterval, node.StartEvent, peeled);
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
