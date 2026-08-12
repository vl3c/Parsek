using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Design authority: docs/dev/design-dock-event-graph.md sections 5.1 (ChapterRoot shape),
    // 7.2 (the chapter spec), 6.4 (degradation), 10 edge cases 22 / 23 / 25, 15.2 (logging),
    // 16.1 (test plan).
    //
    // CHAPTER GROUPING (#5): an accreted mission tree silently inflates with sub-stories the
    // player never asked this mission to be about - the vessel they switched to and kept flying,
    // and the piece that departed after a rendezvous with someone else's station. Each of those
    // is a CHAPTER: a named group header above the rows it owns, with ONE include toggle that
    // expands to the explicit composition-interval keys underneath it.
    //
    // THE NON-CASCADING CONTRACT IS RESPECTED, NOT CHANGED (design 7.2 / 10 edge case 25). A
    // chapter toggle is a bulk EDIT of Mission.ExcludedIntervalKeys through the existing
    // mechanism: it writes the same explicit keys an individual checkbox writes, and every
    // individual checkbox keeps exactly its current semantics afterwards. There is no chapter
    // namespace, no persisted chapter state, and nothing new for ReconcileSelections to validate
    // (a chapter key IS an interval key).
    //
    // PURITY CONTRACT (design 2.4, mirroring DockEventGraph.cs / MissionEventDigest.cs):
    // everything is a PARAMETER - the graph, the tree, the structure, the composition roots, and
    // a mission-name resolver delegate. This file must never reference RecordingStore,
    // ParsekScenario, EffectiveState, MissionStore or any Unity type; the source-text gate in
    // DockEventGraphTests pins that, and the gate is a LITERAL scan, so none of those four names
    // may ever be written with a trailing dot even in a comment.

    /// <summary>
    /// What kind of sub-story a chapter is (design 5.1). Explicit values: they appear in logs and
    /// test assertions, so they are part of the diagnostic contract.
    /// </summary>
    internal enum ChapterKind
    {
        /// <summary>The player switched to another vessel mid-mission and kept flying it: the
        /// child of a <see cref="BranchPointType.VesselSwitchContinuation"/> branch point.
        /// STRUCTURAL - derivable from tree topology alone, so it needs no partner pid and works
        /// unchanged on pre-stamping recordings (design 6.4 row 1).</summary>
        SwitchContinuation = 0,
        /// <summary>A piece that left after a rendezvous with a FOREIGN partner: a non-continuing
        /// child of an Undock whose parent is the merged child of a resolved cross-tree /
        /// same-tree-recovered dock. HEURISTIC - see
        /// <see cref="MissionChapters.CollectChapterRoots"/>.</summary>
        ForeignDockDeparture = 1,
    }

    /// <summary>
    /// One chapter: the recording its sub-story starts at, plus the display title (design 5.1).
    /// Value carrier; runtime-only, never persisted.
    /// </summary>
    internal struct ChapterRoot
    {
        public ChapterKind Kind;
        /// <summary>The chapter's first recording (R(A') for a switch, R(D) for a departure).</summary>
        public string RootRecordingId;
        /// <summary>The branch point that starts the chapter - provenance for tests and logs.</summary>
        public string SourceBranchPointId;
        /// <summary>"Continuation: &lt;vessel&gt;" / "After docking with &lt;partner&gt;:
        /// &lt;vessel&gt; departed".</summary>
        public string Title;
    }

    /// <summary>
    /// The tri-state a chapter's include checkbox renders (design 7.2).
    /// </summary>
    internal enum ChapterSelectionState
    {
        AllIncluded = 0,
        Mixed = 1,
        AllExcluded = 2,
    }

    internal static class MissionChapters
    {
        private const string Tag = "Mission";

        /// <summary>
        /// A composition interval belongs to a chapter only when it starts at or after the
        /// chapter root. Interval edges are cut FROM the leg UTs this comparison uses (the same
        /// doubles), so this tolerance only absorbs representation noise - it is not a window.
        /// </summary>
        private const double IntervalStartEpsilonSeconds = 1e-6;

        /// <summary>
        /// Set true by per-frame / per-tick callers so the per-derivation Verbose summary does
        /// not flood - mirrors <see cref="MissionCrossTreeDock.SuppressLogging"/>. The UI leaves
        /// it false: chapters are memoized per (mission, graph instance), so a derivation is a
        /// real event.
        /// </summary>
        internal static bool SuppressLogging;

        // ------------------------------------------------------------------
        // Roots
        // ------------------------------------------------------------------

        /// <summary>
        /// Collects the chapter roots of one tree (design 7.2), in a deterministic order: by the
        /// root recording's <c>StartUT</c>, ties broken by RootRecordingId.
        /// <para><b>Kind 1, SwitchContinuation:</b> every non-debris child recording of a
        /// <see cref="BranchPointType.VesselSwitchContinuation"/> branch point. Pure topology -
        /// no partner pid is consulted, so this kind derives unchanged on recordings written
        /// before the unconditional dock stamp (design 6.4 row 1).</para>
        /// <para><b>Kind 2, ForeignDockDeparture:</b> every non-continuation (index &gt;= 1 in
        /// <c>ChildRecordingIds</c>), non-debris child of an Undock node whose PARENT recording
        /// is the merged child of a Dock / Board node that resolved a foreign partner
        /// (<see cref="DockPartnerStatus.CrossTree"/> or
        /// <see cref="DockPartnerStatus.SameTreeRecovered"/>), with neither node
        /// visibility-flagged. This is a LABELLED HEURISTIC: it says "this piece departed after a
        /// foreign dock", NOT "this piece is made of the partner's matter" - that second question
        /// is the #8 D-provenance spike's (design 9), and nothing here pretends to answer it. A
        /// dock the graph could not resolve (UnstampedZero / NoMatch / GuidRejected) mints no
        /// chapter, which is exactly the design 6.4 degradation row.</para>
        /// <para>A recording qualifying under BOTH kinds gets ONE root, and SwitchContinuation
        /// wins: it is the structural fact (the player really did switch here), while the
        /// departure classification is the heuristic.</para>
        /// <paramref name="graph"/> may be null - then only SwitchContinuation roots derive.
        /// <paramref name="missionNameResolver"/> is <c>(treeId, recordingId) -&gt; mission name
        /// or null</c> (the resolver <see cref="DockEventGraph.TryDescribePartner"/> takes), used
        /// only to suffix a partner that lives in ANOTHER tree. May be null. Pure.
        /// </summary>
        internal static List<ChapterRoot> CollectChapterRoots(
            DockEventGraph graph,
            RecordingTree tree,
            Func<string, string, string> missionNameResolver)
        {
            var roots = new List<ChapterRoot>();
            if (tree?.Recordings == null || string.IsNullOrEmpty(tree.Id))
            {
                EmitSummary(tree, roots, 0, 0);
                return roots;
            }

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            int switchRoots = 0;
            int departureRoots = 0;

            // (1) SwitchContinuation - topology only.
            if (tree.BranchPoints != null)
            {
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    BranchPoint bp = tree.BranchPoints[b];
                    if (bp == null || bp.Type != BranchPointType.VesselSwitchContinuation
                        || bp.ChildRecordingIds == null)
                        continue;
                    for (int c = 0; c < bp.ChildRecordingIds.Count; c++)
                    {
                        string childId = bp.ChildRecordingIds[c];
                        if (!TryResolveControlledRecording(tree, childId, out Recording child)
                            || !claimed.Add(childId))
                            continue;
                        roots.Add(new ChapterRoot
                        {
                            Kind = ChapterKind.SwitchContinuation,
                            RootRecordingId = childId,
                            SourceBranchPointId = bp.Id,
                            Title = "Continuation: " + VesselLabel(child),
                        });
                        switchRoots++;
                    }
                }
            }

            // (2) ForeignDockDeparture - needs the graph's partner resolution.
            if (graph != null)
            {
                Dictionary<string, DockEventNode> foreignDockByMergedChild =
                    IndexResolvedForeignDocks(graph, tree.Id);
                if (foreignDockByMergedChild.Count > 0)
                {
                    List<DockEventNode> nodes = graph.NodesForTree(tree.Id);
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        DockEventNode undock = nodes[i];
                        if (undock == null || undock.IsMerge || undock.VisibilityFlagged
                            || !string.Equals(undock.TreeId, tree.Id, StringComparison.Ordinal)
                            || undock.ParentRecordingIds.Count == 0
                            || undock.ChildRecordingIds.Count < 2)
                            continue;
                        // The undock's own line must BE the merged stack of a resolved foreign
                        // dock. Any parent qualifies (an Undock has one in practice); first wins.
                        DockEventNode dock = null;
                        for (int p = 0; p < undock.ParentRecordingIds.Count && dock == null; p++)
                            foreignDockByMergedChild.TryGetValue(
                                undock.ParentRecordingIds[p] ?? "", out dock);
                        if (dock == null)
                            continue;

                        string partnerText = DescribeDockPartner(dock, tree.Id, missionNameResolver);
                        if (string.IsNullOrEmpty(partnerText))
                            continue;   // nothing nameable: stay silent, as design 6.4 requires

                        // index 0 is the CONTINUING child (recorder convention) - the mission's
                        // own vessel carrying on, never a chapter.
                        for (int c = 1; c < undock.ChildRecordingIds.Count; c++)
                        {
                            string childId = undock.ChildRecordingIds[c];
                            if (!TryResolveControlledRecording(tree, childId, out Recording child)
                                || !claimed.Add(childId))
                                continue;
                            roots.Add(new ChapterRoot
                            {
                                Kind = ChapterKind.ForeignDockDeparture,
                                RootRecordingId = childId,
                                SourceBranchPointId = undock.BranchPointId,
                                Title = "After docking with " + partnerText + ": "
                                        + VesselLabel(child) + " departed",
                            });
                            departureRoots++;
                        }
                    }
                }
            }

            roots.Sort((a, b) => CompareRoots(tree, a, b));
            EmitSummary(tree, roots, switchRoots, departureRoots);
            return roots;
        }

        // Merge nodes OWNED by this tree whose partner actually resolved to a foreign line,
        // keyed by their merged child (the recording an Undock of the stack hangs off).
        private static Dictionary<string, DockEventNode> IndexResolvedForeignDocks(
            DockEventGraph graph, string treeId)
        {
            var byMergedChild = new Dictionary<string, DockEventNode>(StringComparer.Ordinal);
            List<DockEventNode> nodes = graph.NodesForTree(treeId);
            for (int i = 0; i < nodes.Count; i++)
            {
                DockEventNode n = nodes[i];
                if (n == null || !n.IsMerge || n.VisibilityFlagged
                    || !string.Equals(n.TreeId, treeId, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(n.MergedChildRecordingId))
                    continue;
                if (n.Partner.Status != DockPartnerStatus.CrossTree
                    && n.Partner.Status != DockPartnerStatus.SameTreeRecovered)
                    continue;
                // Earliest dock wins on the (pathological) repeat: the graph's node list is
                // already UT-sorted, so the first write is the earliest.
                if (!byMergedChild.ContainsKey(n.MergedChildRecordingId))
                    byMergedChild[n.MergedChildRecordingId] = n;
            }
            return byMergedChild;
        }

        // "CD" / "CD (mission 'CD Freighter')" - the mission suffix only when the partner lives
        // in ANOTHER tree (a same-tree-recovered partner is already inside this same mission, so
        // naming its mission would just repeat the header the row is drawn under).
        private static string DescribeDockPartner(
            DockEventNode dock, string treeId, Func<string, string, string> missionNameResolver)
        {
            string name = dock.Partner.PartnerVesselName;
            if (string.IsNullOrEmpty(name))
                return null;
            if (missionNameResolver == null
                || string.IsNullOrEmpty(dock.Partner.PartnerTreeId)
                || string.Equals(dock.Partner.PartnerTreeId, treeId, StringComparison.Ordinal))
                return name;
            string mission = missionNameResolver(
                dock.Partner.PartnerTreeId, dock.Partner.PartnerRecordingId);
            return string.IsNullOrEmpty(mission) ? name : name + " (mission '" + mission + "')";
        }

        // A chapter root must be a CONTROLLED recording of this tree: debris is never a leg (it
        // rides its parent at playback and owns no selectable interval), so a debris child could
        // only ever mint an empty chapter (design 10 edge case 25's sibling case).
        private static bool TryResolveControlledRecording(
            RecordingTree tree, string recordingId, out Recording rec)
        {
            rec = null;
            return !string.IsNullOrEmpty(recordingId)
                && tree.Recordings.TryGetValue(recordingId, out rec)
                && rec != null
                && !rec.IsDebris;
        }

        private static int CompareRoots(RecordingTree tree, ChapterRoot a, ChapterRoot b)
        {
            int cmp = RootStartUT(tree, a).CompareTo(RootStartUT(tree, b));
            return cmp != 0 ? cmp : string.CompareOrdinal(a.RootRecordingId, b.RootRecordingId);
        }

        private static double RootStartUT(RecordingTree tree, ChapterRoot root)
        {
            return tree.Recordings != null
                   && tree.Recordings.TryGetValue(root.RootRecordingId ?? "", out Recording rec)
                   && rec != null
                ? rec.StartUT
                : 0.0;
        }

        private static string VesselLabel(Recording rec)
            => string.IsNullOrEmpty(rec?.VesselName) ? "(vessel)" : rec.VesselName;

        // ------------------------------------------------------------------
        // Expansion
        // ------------------------------------------------------------------

        /// <summary>
        /// The explicit composition interval keys a chapter's one-click toggle writes (design
        /// 7.2): every SELECTABLE key (head keys, <c>/segN</c>, <c>@dockM</c>) owned by the
        /// through-lines reachable DOWNSTREAM from <see cref="ChapterRoot.RootRecordingId"/>,
        /// including the root's own line. Debris is skipped for free -
        /// <see cref="MissionStructureBuilder"/> never makes a leg of it.
        /// <para><b>Why a UT floor rather than "the whole owning line".</b> A
        /// ForeignDockDeparture root is a through-line HEAD (a non-continuing branch child), so
        /// its line begins at the chapter. A SwitchContinuation root is NOT: the recorder lists
        /// the switch segment as <c>ChildRecordingIds[0]</c>, so
        /// <see cref="MissionThroughLineBuilder.ContinuationSuccessor"/> folds it into the SAME
        /// physical vessel's line as the pre-switch legs, and taking that whole line would put
        /// the mission's launch inside the chapter. So a key of the root's own line joins the
        /// chapter only when its interval starts at or after the root. ACCEPTED CONSEQUENCE: the
        /// interval that STRADDLES the switch (a switch mints no interval edge of its own - it is
        /// not a controller separation and not a merge) stays OUT of the chapter. That is the
        /// honest answer, not a gap: the pre-switch and post-switch halves of that interval share
        /// ONE key, so no selection can separate them, and including it would make "exclude this
        /// chapter" silently drop the launch.</para>
        /// <para><b>Stability across optimizer churn (design 10 edge case 23).</b> The keys are
        /// rooted at through-line HEAD ids, and a head keeps the EARLIEST segment's recording id
        /// under an optimizer split/merge (design-mission-abstractions.md open question 3b), so a
        /// split inside a chapter re-cuts the segments underneath a key without renaming it.
        /// Chapter roots are branch children, i.e. heads or head-line members by construction, so
        /// the same holds for the root id this expansion is anchored on.</para>
        /// Pure. Returns an empty set for an unresolvable root (never null).
        /// </summary>
        internal static HashSet<string> ExpandChapterToIntervalKeys(
            ChapterRoot root,
            MissionStructure structure,
            IReadOnlyList<MissionCompositionNode> compRoots)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (structure == null || compRoots == null
                || string.IsNullOrEmpty(root.RootRecordingId)
                || !structure.LegsById.TryGetValue(root.RootRecordingId, out MissionLeg rootLeg))
                return keys;

            // The through-lines the chapter owns: every line holding a leg reachable downstream
            // of the root. Reuses the existing through-line collapse rather than re-deriving the
            // "which leg belongs to which vessel" convention here.
            //
            // The map is one-leg-to-MANY-heads on purpose. A two-parent same-tree Dock is walked
            // by BOTH parents' continuation chains, so its merged child is a member of two
            // through-lines, while MissionCompositionBuilder awards that child's intervals to
            // exactly ONE owner (whichever run its shared visited set reaches first, in root
            // order). Collapsing to a single head here would therefore disagree with
            // OwnerHeadId roughly half the time, and the chapter would silently lose every key
            // from the dock onward - up to and including its whole key set, which drops the
            // header and its toggle with no trace. Recording every containing head makes the
            // expansion independent of both traversal orders.
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
            var headsByLeg = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (MissionThroughLine line in view.ByHeadId.Values)
            {
                for (int m = 0; m < line.MemberLegIds.Count; m++)
                {
                    string legId = line.MemberLegIds[m];
                    if (!headsByLeg.TryGetValue(legId, out List<string> heads))
                    {
                        heads = new List<string>(1);
                        headsByLeg[legId] = heads;
                    }
                    heads.Add(line.HeadLegId);
                }
            }

            // Per reachable line, the UT the chapter ENTERS it at. For the root's own line that
            // is the root itself; for a line the chapter only reaches THROUGH a merge (the
            // chapter's vessel docks someone, so the merged stack belongs to the partner's line)
            // it is the merge. Without this the chapter would swallow the partner's whole
            // pre-dock solo flight - one click dropping a vessel the chapter never contained.
            Dictionary<string, double> entryUTByHead = CollectDownstreamLineEntries(
                structure, headsByLeg, root.RootRecordingId);
            if (entryUTByHead.Count == 0)
                return keys;

            double rootStartUT = rootLeg.StartUT;
            for (int i = 0; i < compRoots.Count; i++)
                CollectChapterKeys(compRoots[i], entryUTByHead, rootStartUT, keys);
            return keys;
        }

        // Downstream leg closure from the root (env-split continuations + branch children,
        // transitively), folded onto EVERY through-line containing each reached leg as that
        // line's earliest reached start. Cycle-safe via the visited set - a same-tree Dock merge
        // makes the leg graph a DAG, not a tree.
        private static Dictionary<string, double> CollectDownstreamLineEntries(
            MissionStructure structure, Dictionary<string, List<string>> headsByLeg,
            string rootLegId)
        {
            var entries = new Dictionary<string, double>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(rootLegId);
            while (stack.Count > 0)
            {
                string legId = stack.Pop();
                if (!visited.Add(legId)
                    || !structure.LegsById.TryGetValue(legId, out MissionLeg leg))
                    continue;
                if (headsByLeg.TryGetValue(legId, out List<string> heads))
                {
                    for (int h = 0; h < heads.Count; h++)
                        if (!entries.TryGetValue(heads[h], out double known)
                            || leg.StartUT < known)
                            entries[heads[h]] = leg.StartUT;
                }
                if (!string.IsNullOrEmpty(leg.SequenceNextId))
                    stack.Push(leg.SequenceNextId);
                for (int c = 0; c < leg.BranchChildIds.Count; c++)
                    stack.Push(leg.BranchChildIds[c]);
            }
            return entries;
        }

        private static void CollectChapterKeys(
            MissionCompositionNode node, Dictionary<string, double> entryUTByHead,
            double rootStartUT, HashSet<string> into)
        {
            if (node == null)
                return;
            // Roster atoms are not independently selectable and carry no span, so they never
            // become chapter keys (they grey with the interval that owns them, as today).
            if (node.IsSelectable && !string.IsNullOrEmpty(node.HeadLegId)
                && !string.IsNullOrEmpty(node.OwnerHeadId)
                && entryUTByHead.TryGetValue(node.OwnerHeadId, out double entryUT))
            {
                double floorUT = (entryUT > rootStartUT ? entryUT : rootStartUT)
                                 - IntervalStartEpsilonSeconds;
                if (node.StartUT >= floorUT)
                    into.Add(node.HeadLegId);
            }
            for (int i = 0; i < node.Children.Count; i++)
                CollectChapterKeys(node.Children[i], entryUTByHead, rootStartUT, into);
        }

        /// <summary>
        /// The chapter's first row in composition order - the key whose interval starts earliest
        /// (ties broken ordinally), i.e. where the chapter's group header belongs. Null when the
        /// chapter owns no selectable key (nothing to head, nothing to toggle). Pure.
        /// </summary>
        internal static string ResolveChapterAnchorKey(
            ICollection<string> chapterKeys, IReadOnlyList<MissionCompositionNode> compRoots)
        {
            if (chapterKeys == null || chapterKeys.Count == 0 || compRoots == null)
                return null;
            string bestKey = null;
            double bestUT = 0.0;
            for (int i = 0; i < compRoots.Count; i++)
                FindAnchor(compRoots[i], chapterKeys, ref bestKey, ref bestUT);
            return bestKey;
        }

        private static void FindAnchor(
            MissionCompositionNode node, ICollection<string> chapterKeys,
            ref string bestKey, ref double bestUT)
        {
            if (node == null)
                return;
            if (node.IsSelectable && !string.IsNullOrEmpty(node.HeadLegId)
                && chapterKeys.Contains(node.HeadLegId)
                && (bestKey == null
                    || node.StartUT < bestUT
                    || (node.StartUT == bestUT
                        && string.CompareOrdinal(node.HeadLegId, bestKey) < 0)))
            {
                bestKey = node.HeadLegId;
                bestUT = node.StartUT;
            }
            for (int i = 0; i < node.Children.Count; i++)
                FindAnchor(node.Children[i], chapterKeys, ref bestKey, ref bestUT);
        }

        // ------------------------------------------------------------------
        // Tri-state
        // ------------------------------------------------------------------

        /// <summary>
        /// The tri-state of a chapter's include checkbox (design 7.2): all of its keys included,
        /// none of them, or a mix (the player toggled individual intervals inside it, or new
        /// topology appeared - design Q6 / edge case 22). An EMPTY chapter reads AllIncluded:
        /// nothing is excluded, and reporting "all excluded" for a chapter with no keys would
        /// render a header claiming the player dropped something they never had. Pure.
        /// </summary>
        internal static ChapterSelectionState ClassifyChapterState(
            HashSet<string> chapterKeys, ISet<string> excludedKeys)
        {
            if (chapterKeys == null || chapterKeys.Count == 0)
                return ChapterSelectionState.AllIncluded;
            int excluded = 0;
            foreach (string key in chapterKeys)
                if (excludedKeys != null && excludedKeys.Contains(key))
                    excluded++;
            if (excluded == 0)
                return ChapterSelectionState.AllIncluded;
            return excluded == chapterKeys.Count
                ? ChapterSelectionState.AllExcluded
                : ChapterSelectionState.Mixed;
        }

        /// <summary>
        /// Design Q6 / edge case 22, the RECONCILE observation: a branch recorded LATER inside an
        /// excluded chapter defaults to INCLUDED (the standing open-question-3a contract), so the
        /// chapter reappears in the loop with a piece the player thought they had dropped. Auto-
        /// extending the exclusion would be a silent write to player selection state, so this
        /// only reports.
        /// <para><b>The v1 approximation, stated plainly.</b> The precise statement ("the excluded
        /// set still contains everything the chapter held WHEN IT WAS EXCLUDED, and the current
        /// expansion has grown past it") needs a persisted per-chapter exclusion snapshot, and
        /// this design adds NO persistence (design 5.2). So the raise predicate is the observable
        /// shadow of that statement: the chapter is <see cref="ChapterSelectionState.Mixed"/> -
        /// i.e. it holds at least one excluded key AND at least one included one. New topology
        /// inside a fully excluded chapter always produces exactly that shape, so the rule never
        /// MISSES the case it exists for. It over-reports: a player who deliberately excluded one
        /// interval inside an otherwise-included chapter raises it too, and the two are
        /// indistinguishable without the snapshot. It is a Warn on a reporting path with no
        /// control-flow effect, so over-reporting costs a log line and nothing else.</para>
        /// Returns true when the warn should be raised, and the count of still-included keys.
        /// Pure.
        /// </summary>
        internal static bool ShouldWarnNewTopologyInsideExcludedChapter(
            HashSet<string> chapterKeys, ISet<string> excludedKeys, out int includedKeyCount)
        {
            includedKeyCount = 0;
            if (chapterKeys == null || chapterKeys.Count == 0)
                return false;
            foreach (string key in chapterKeys)
                if (excludedKeys == null || !excludedKeys.Contains(key))
                    includedKeyCount++;
            return ClassifyChapterState(chapterKeys, excludedKeys) == ChapterSelectionState.Mixed;
        }

        // ------------------------------------------------------------------
        // Logging (design 15.2)
        // ------------------------------------------------------------------

        private static void EmitSummary(
            RecordingTree tree, List<ChapterRoot> roots, int switchRoots, int departureRoots)
        {
            if (SuppressLogging)
                return;
            var ic = CultureInfo.InvariantCulture;
            ParsekLog.Verbose(Tag,
                "Chapters: tree=" + (tree?.Id ?? "<null>")
                + " roots=" + roots.Count.ToString(ic)
                + " switch=" + switchRoots.ToString(ic)
                + " foreignDeparture=" + departureRoots.ToString(ic));
        }
    }
}
