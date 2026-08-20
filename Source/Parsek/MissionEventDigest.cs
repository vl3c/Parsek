using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Design authority: docs/dev/design-dock-event-graph.md sections 5.1 (row shape), 7.1 (the
    // digest), 6.4 (degradation), 8 (the issue-1 seam), 15.2 (logging), 16.1 (test plan).
    //
    // The MISSION EVENT DIGEST (#4, which IS issue-1's T2.3 - one feature, not two): the
    // chronological story of one mission's tree, with both sides of every dock named and GoTo
    // ids for the cross-navigation. Rows come from three sources that the single-writer recorder
    // keeps apart: the tree's own topology (launches, undocks, terminal states), the dock-event
    // graph (docks this tree recorded AND docks another tree recorded ON this tree's vessel),
    // and the mission's own selection (the R5 loiter-gap statement on an included foreign link).
    //
    // PURITY CONTRACT (design 2.4, mirroring DockEventGraph.cs): everything is a PARAMETER -
    // the graph, the tree, the mission, and two resolver delegates the UI supplies so this file
    // never reaches for the mission store itself. It must never reference RecordingStore,
    // ParsekScenario, EffectiveState, MissionStore or any Unity type; DockEventGraphTests'
    // source-text gate pins that - and the gate is a LITERAL scan, so those four names must
    // never be written with a trailing dot even in a comment (including at the end of a
    // sentence), exactly like the raw committed-list token the ERS grep gate scans for.

    /// <summary>
    /// One chronological entry of a mission's story (design 5.1). Value carrier; the renderer
    /// owns layout, this owns the words. Never persisted.
    /// </summary>
    internal struct MissionEventRow
    {
        public double UT;
        /// <summary>"Launched", "Undocked", "Docked with", "Docked by", "Boarded",
        /// "Boarded by", "Gap", or a terminal-state name ("Landed", ...).</summary>
        public string Verb;
        /// <summary>The vessel on THIS mission's side of the event.</summary>
        public string SubjectName;
        /// <summary>"CD (mission 'CD Freighter')", the generic Q4 text, or "" when there is
        /// nothing honest to say (design 6.4: verb-only rows).</summary>
        public string PartnerText;
        /// <summary>&gt; 0 only on R5 gap rows.</summary>
        public double GapSeconds;
        /// <summary>Reveal target on the OTHER side of the event; null when there is none or
        /// when it would navigate inside the mission the row is already drawn under.</summary>
        public string GoToRecordingId;
        /// <summary>Mission owning <see cref="GoToRecordingId"/>, via the caller's resolver;
        /// null when unresolved.</summary>
        public string GoToMissionId;
        /// <summary>Provenance for tests and logs; null on rows derived from the tree alone.</summary>
        public string SourceBranchPointId;
    }

    internal static class MissionEventDigest
    {
        private const string Tag = "Mission";

        // Verb vocabulary. Constants (not literals at the use sites) because tests, the renderer
        // and the log all key on the exact strings.
        internal const string VerbLaunched = "Launched";
        internal const string VerbUndocked = "Undocked";
        internal const string VerbDockedWith = "Docked with";
        internal const string VerbDockedBy = "Docked by";
        internal const string VerbBoarded = "Boarded";
        internal const string VerbBoardedBy = "Boarded by";
        internal const string VerbGap = "Gap";

        /// <summary>
        /// Design Q4: a stamped dock whose partner resolves to no recording anywhere is named
        /// generically IN THE DIGEST ONLY - honest and cheap. The tables stay silent
        /// (<see cref="DockEventGraph.TryDescribePartner"/> declines on NoMatch).
        /// </summary>
        internal const string UnrecordedPartnerText = "an unrecorded vessel";

        /// <summary>
        /// R5 threshold (design 7.1 step 3): a claimed line that ends more than this before the
        /// dock it feeds was loitering unrecorded, and the digest STATES the gap rather than
        /// interpolating over it.
        /// </summary>
        internal const double GapThresholdSeconds = 60.0;

        /// <summary>
        /// Set true by per-frame callers so the per-build Verbose summary does not flood -
        /// mirrors <see cref="MissionCrossTreeDock.SuppressLogging"/>. The UI leaves it false:
        /// the digest is cached per (graph instance, mission), so a build is a real event.
        /// </summary>
        internal static bool SuppressLogging;

        /// <summary>
        /// Builds one mission's digest rows, ordered by UT (ties broken by source branch point,
        /// then verb, then subject, so the order is total and deterministic). Pure.
        /// <para><paramref name="graph"/> may be null (no dock rows); <paramref name="mission"/>
        /// may be null (no R5 gap rows - they are driven by the mission's own included-link
        /// selection). <paramref name="missionNameResolver"/> is
        /// <c>(treeId, recordingId) -&gt; mission name</c> (the resolver
        /// <see cref="DockEventGraph.TryDescribePartner"/> takes);
        /// <paramref name="missionIdResolver"/> is <c>treeId -&gt; mission id</c>, used only to
        /// fill <see cref="MissionEventRow.GoToMissionId"/>. Both may be null.</para>
        /// </summary>
        internal static List<MissionEventRow> Build(
            DockEventGraph graph,
            RecordingTree tree,
            Mission mission,
            Func<string, string, string> missionNameResolver,
            Func<string, string> missionIdResolver)
        {
            var rows = new List<MissionEventRow>();
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(tree.Id))
                return rows;

            int docks = 0;
            int undocks = 0;
            int gaps = 0;
            int skippedFlagged = 0;

            AddLaunchRows(tree, rows);

            if (graph != null)
            {
                List<DockEventNode> nodes = graph.NodesForTree(tree.Id);
                for (int i = 0; i < nodes.Count; i++)
                {
                    DockEventNode node = nodes[i];
                    if (node == null)
                        continue;
                    // A re-fly-superseded subtree keeps its branch points; the digest must not
                    // tell the story of a flight that was superseded away (design 6.3 step 3).
                    if (node.VisibilityFlagged)
                    {
                        skippedFlagged++;
                        continue;
                    }

                    if (node.IsMerge)
                    {
                        if (TryBuildMergeRow(graph, tree, node, missionNameResolver,
                                missionIdResolver, out MissionEventRow mergeRow))
                        {
                            rows.Add(mergeRow);
                            docks++;
                        }
                        if (TryBuildGapRow(tree, mission, node, out MissionEventRow gapRow))
                        {
                            rows.Add(gapRow);
                            gaps++;
                        }
                    }
                    else if (TryBuildUndockRow(tree, node, out MissionEventRow undockRow))
                    {
                        rows.Add(undockRow);
                        undocks++;
                    }
                }
            }

            AddTerminalRows(tree, rows);
            rows.Sort(CompareRows);

            if (!SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose(Tag,
                    "EventDigest: tree=" + tree.Id
                    + " rows=" + rows.Count.ToString(ic)
                    + " docks=" + docks.ToString(ic)
                    + " undocks=" + undocks.ToString(ic)
                    + " gaps=" + gaps.ToString(ic)
                    + " skippedFlagged=" + skippedFlagged.ToString(ic));
            }
            return rows;
        }

        /// <summary>
        /// The renderer's one-line phrasing of a row: "B0 docked with CD (mission 'CD
        /// Freighter')", "Vessel B1 undocked", "(loiter, 2d - not recorded)". Pure and
        /// InvariantCulture (the duration goes through the house formatter), so the words are
        /// unit-testable without a GUI.
        /// </summary>
        internal static string FormatRowText(MissionEventRow row)
        {
            if (string.Equals(row.Verb, VerbGap, StringComparison.Ordinal))
                return "(loiter, " + ParsekTimeFormat.FormatDuration(row.GapSeconds)
                    + " - not recorded)";

            string verb = LowerFirst(row.Verb);
            string subject = string.IsNullOrEmpty(row.SubjectName) ? "?" : row.SubjectName;
            string text = subject + " " + verb;
            if (!string.IsNullOrEmpty(row.PartnerText))
                text += " " + row.PartnerText;
            return text;
        }

        // ---- launch rows (design 7.1 step 1 + edge case 13) ----

        // One row per ROOT: a recording with no incoming branch-point edge. Membership is decided
        // by the CHILD lists rather than Recording.ParentBranchPointId so a stale/dangling parent
        // field cannot mint a phantom launch, and so a genuinely disconnected root (the post-switch
        // shape of edge case 13, and the A->D fixture's D line) gets its own row.
        private static void AddLaunchRows(RecordingTree tree, List<MissionEventRow> rows)
        {
            var attached = new HashSet<string>(StringComparer.Ordinal);
            if (tree.BranchPoints != null)
            {
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp?.ChildRecordingIds == null)
                        continue;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                        if (!string.IsNullOrEmpty(bp.ChildRecordingIds[c]))
                            attached.Add(bp.ChildRecordingIds[c]);
                }
            }

            foreach (KeyValuePair<string, Recording> entry in tree.Recordings)
            {
                Recording rec = entry.Value;
                if (rec == null || rec.IsDebris || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (attached.Contains(rec.RecordingId))
                    continue;
                rows.Add(new MissionEventRow
                {
                    UT = rec.StartUT,
                    Verb = VerbLaunched,
                    SubjectName = rec.VesselName,
                });
            }
        }

        // ---- merge rows (design 7.1 step 2, both columns of the 1.2 worked example) ----

        private static bool TryBuildMergeRow(
            DockEventGraph graph,
            RecordingTree tree,
            DockEventNode node,
            Func<string, string, string> missionNameResolver,
            Func<string, string> missionIdResolver,
            out MissionEventRow row)
        {
            row = default(MissionEventRow);
            bool owned = string.Equals(node.TreeId, tree.Id, StringComparison.Ordinal);
            bool board = node.Kind == BranchPointType.Board;

            // The viewer decides the direction TryDescribePartner answers in. Owning tree: our
            // own incoming line (a parent). Partner tree: the claimed recording - the node is
            // registered under this tree precisely BECAUSE the claim landed here.
            string viewer;
            string subject;
            if (owned)
            {
                viewer = node.ParentRecordingIds.Count > 0
                    ? node.ParentRecordingIds[0]
                    : node.MergedChildRecordingId;
                subject = node.ParentVesselNames.Count > 0 && !string.IsNullOrEmpty(node.ParentVesselNames[0])
                    ? node.ParentVesselNames[0]
                    : node.MergedChildVesselName;
            }
            else
            {
                viewer = node.Partner.PartnerRecordingId;
                subject = node.Partner.PartnerVesselName;
                // Not resolved INTO this tree in any nameable way: nothing to say from here.
                if (string.IsNullOrEmpty(viewer))
                    return false;
            }

            row.UT = node.UT;
            row.SubjectName = subject;
            row.SourceBranchPointId = node.BranchPointId;
            row.Verb = owned
                ? (board ? VerbBoarded : VerbDockedWith)
                : (board ? VerbBoardedBy : VerbDockedBy);

            if (DockEventGraph.TryDescribePartner(
                    graph, node.BranchPointId, viewer, missionNameResolver,
                    out DockPartnerDescription d))
            {
                row.PartnerText = FormatPartner(d, tree.Id);
                // GoTo crosses a mission boundary or it is not an affordance: a same-tree
                // recovered link (design Q8) and a two-parent same-tree merge both name the
                // partner without offering navigation to the mission the player is already in.
                if (!string.IsNullOrEmpty(d.PartnerRecordingId)
                    && !string.IsNullOrEmpty(d.PartnerTreeId)
                    && !string.Equals(d.PartnerTreeId, tree.Id, StringComparison.Ordinal))
                {
                    row.GoToRecordingId = d.PartnerRecordingId;
                    row.GoToMissionId = missionIdResolver != null
                        ? missionIdResolver(d.PartnerTreeId)
                        : null;
                }
            }
            else if (node.Partner.Status == DockPartnerStatus.NoMatch)
            {
                // Q4: the generic text lives HERE and nowhere else.
                row.PartnerText = UnrecordedPartnerText;
            }
            else
            {
                // UnstampedZero / GuidRejected / an undescribable two-parent viewer: verb only,
                // exactly as silent as today (design 6.4).
                row.PartnerText = "";
            }
            return true;
        }

        // ---- undock rows (design 7.1 step 2, "debris-only undock emits no row") ----

        private static bool TryBuildUndockRow(
            RecordingTree tree, DockEventNode node, out MissionEventRow row)
        {
            row = default(MissionEventRow);

            // The story of an undock is the piece that LEFT: child [0] is the continuing stack by
            // recorder convention. A split that only shed debris (a fairing, a spent stage) is not
            // a story beat, and emitting one per jettison would bury the beats that are.
            string departing = null;
            for (int c = 1; c < node.ChildRecordingIds.Count; c++)
            {
                string id = node.ChildRecordingIds[c];
                if (string.IsNullOrEmpty(id))
                    continue;
                Recording rec = Lookup(tree, id);
                if (rec != null && rec.IsDebris)
                    continue;
                departing = rec != null && !string.IsNullOrEmpty(rec.VesselName) ? rec.VesselName : null;
                if (departing != null)
                    break;
                // A non-debris child the tree cannot name still counts as a real departure; keep
                // scanning for a nameable one and fall back to the parent's name below.
                departing = "";
            }
            if (departing == null)
                return false;

            if (departing.Length == 0)
            {
                departing = node.ParentRecordingIds.Count > 0
                    && !string.IsNullOrEmpty(node.ParentVesselNames[0])
                    ? node.ParentVesselNames[0]
                    : null;
            }

            row.UT = node.UT;
            row.Verb = VerbUndocked;
            row.SubjectName = departing;
            row.SourceBranchPointId = node.BranchPointId;
            row.PartnerText = "";
            return true;
        }

        // ---- R5 gap rows (design 7.1 step 3 / 7.4) ----

        // The link-driven case only: the mission included a foreign dock link whose claimed
        // recording (OUR vessel) stops well before the dock it feeds, so the partner journey the
        // player just switched on begins with unrecorded loiter. The general member-window gap
        // needs the loop unit and is deliberately out of this scope.
        private static bool TryBuildGapRow(
            RecordingTree tree, Mission mission, DockEventNode node, out MissionEventRow row)
        {
            row = default(MissionEventRow);
            if (mission == null || mission.IncludedForeignDockLinkIds.Count == 0
                || string.IsNullOrEmpty(node.BranchPointId)
                || !mission.IncludedForeignDockLinkIds.Contains(node.BranchPointId))
                return false;

            Recording claimed = Lookup(tree, node.Partner.PartnerRecordingId);
            if (claimed == null)
                return false;

            double claimedEnd = claimed.EndUT;
            double gap = node.UT - claimedEnd;
            if (double.IsNaN(gap) || gap <= GapThresholdSeconds)
                return false;

            row.UT = claimedEnd;
            row.Verb = VerbGap;
            row.SubjectName = claimed.VesselName;
            row.PartnerText = "";
            row.GapSeconds = gap;
            row.SourceBranchPointId = node.BranchPointId;
            return true;
        }

        // ---- terminal rows (design 7.1 step 4) ----

        // One row per line end that the tree already states: a leaf recording carrying a terminal
        // verdict. Docked / Boarded terminals are DELIBERATELY skipped - that line ended by being
        // absorbed into a merge, and the merge already has its own row; emitting both would say
        // the same event twice under two different verbs.
        private static void AddTerminalRows(RecordingTree tree, List<MissionEventRow> rows)
        {
            foreach (KeyValuePair<string, Recording> entry in tree.Recordings)
            {
                Recording rec = entry.Value;
                if (rec == null || rec.IsDebris || !string.IsNullOrEmpty(rec.ChildBranchPointId)
                    || !rec.TerminalStateValue.HasValue)
                    continue;
                TerminalState state = rec.TerminalStateValue.Value;
                if (state == TerminalState.Docked || state == TerminalState.Boarded)
                    continue;
                string verb = MissionCompositionBuilder.TerminalName(rec.TerminalStateValue);
                if (string.IsNullOrEmpty(verb))
                    continue;
                rows.Add(new MissionEventRow
                {
                    UT = rec.EndUT,
                    Verb = verb,
                    SubjectName = rec.VesselName,
                });
            }
        }

        // ---- helpers ----

        /// <summary>
        /// "CD (mission 'CD Freighter')" across a mission boundary, bare "CD" inside the viewer's
        /// own tree (naming the mission the player is already reading would be noise).
        /// </summary>
        private static string FormatPartner(DockPartnerDescription d, string viewerTreeId)
        {
            if (string.IsNullOrEmpty(d.PartnerVesselName))
                return "";
            bool foreign = !string.IsNullOrEmpty(d.PartnerTreeId)
                && !string.Equals(d.PartnerTreeId, viewerTreeId, StringComparison.Ordinal);
            return foreign && !string.IsNullOrEmpty(d.PartnerMissionName)
                ? d.PartnerVesselName + " (mission '" + d.PartnerMissionName + "')"
                : d.PartnerVesselName;
        }

        private static Recording Lookup(RecordingTree tree, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;
            return tree.Recordings.TryGetValue(recordingId, out Recording rec) ? rec : null;
        }

        private static string LowerFirst(string verb)
        {
            if (string.IsNullOrEmpty(verb))
                return "";
            if (!char.IsUpper(verb[0]))
                return verb;
            return char.ToLowerInvariant(verb[0]) + verb.Substring(1);
        }

        // Total, deterministic order. UT first (the story is chronological); then the source
        // branch point, verb and subject so two rows at the same UT - a dock and the gap that
        // precedes it, two undocks of one stack - never swap between builds.
        private static int CompareRows(MissionEventRow a, MissionEventRow b)
        {
            int cmp = a.UT.CompareTo(b.UT);
            if (cmp != 0)
                return cmp;
            cmp = string.CompareOrdinal(a.SourceBranchPointId ?? "", b.SourceBranchPointId ?? "");
            if (cmp != 0)
                return cmp;
            cmp = string.CompareOrdinal(a.Verb ?? "", b.Verb ?? "");
            return cmp != 0
                ? cmp
                : string.CompareOrdinal(a.SubjectName ?? "", b.SubjectName ?? "");
        }
    }
}
