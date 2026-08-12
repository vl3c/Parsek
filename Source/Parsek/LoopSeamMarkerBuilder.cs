using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Design authority: docs/dev/design-dock-event-graph.md sections 5.1 (LoopSeamMarker shape),
    // 7.3 (R2 merge seam / R3 line-ends-at-dock), 6.4 (degradation), 10 edge cases 16-21,
    // 14 (performance budget), 15.2 (logging), 16.1 (SeamMarkerTests).
    //
    // LOOP SEAM MARKERS (#6). A looping mission whose story crosses a dock has two silent moments:
    //
    //   R2 (MergeAppear)  the merged stack this mission replays DOUBLES in size at the dock UT,
    //                     because the other vessel's half materialised out of a snapshot this
    //                     mission never flew. Today: unexplained.
    //   R3 (DockedVanish) a member's line ENDS at a dock this mission does not replay past, so the
    //                     ghost simply vanishes. Today: silent. The vessel did not blow up - it
    //                     docked, and the story continues in another mission.
    //
    // Both are computed HERE, once per loop-unit build, from the dock-event graph plus the unit's
    // FINAL member windows; the runtime cost is one comparison per member per frame
    // (GhostPlaybackLogic.TryResolveSeamMarkerToEmit). Nothing here runs per frame.
    //
    // PURITY CONTRACT (design 2.4, mirroring DockEventGraph.cs / MissionEventDigest.cs /
    // MissionChapters.cs): the graph, the member windows, the committed-index map and the
    // mission-name resolver all arrive as PARAMETERS. This file must never reference
    // RecordingStore, ParsekScenario, EffectiveState, MissionStore or any Unity type; the
    // source-text gate in DockEventGraphTests pins that, and the gate is a LITERAL scan, so none of
    // those four names may ever be written with a trailing dot even in a comment.
    internal static class LoopSeamMarkerBuilder
    {
        private const string Tag = "SeamMarker";

        /// <summary>
        /// Badge/message window (design Q5): a marker is live while the shared span clock sits in
        /// [SeamUT, SeamUT + this] of LOOP time, so the explanation is on screen for the moment it
        /// explains and not a second longer.
        /// </summary>
        internal const double SeamWindowSeconds = 10.0;

        /// <summary>Set true by per-frame / per-tick callers so the per-build Verbose summary does
        /// not flood - mirrors <see cref="MissionCrossTreeDock.SuppressLogging"/>. The loop-unit
        /// builder forwards its own <c>SuppressLogging</c> into this.</summary>
        internal static bool SuppressLogging;

        /// <summary>
        /// Computes the seam markers for ONE loop unit (design 7.3). Returns an EMPTY list - never
        /// null - when there is nothing to explain; the caller stores null in that case so
        /// <see cref="GhostPlaybackLogic.LoopUnit.SeamMarkers"/> stays at its default.
        ///
        /// <para><paramref name="graph"/> may be null (no graph supplied by the host - KSC, the
        /// Tracking Station, the UI mirror, every existing test): the result is empty and the unit
        /// behaves exactly as it does today. <paramref name="memberWindows"/> is the unit's FINAL
        /// trimmed windows keyed by committed index; <paramref name="committedIndexById"/> maps a
        /// recording id to that same index space; <paramref name="missionNameByTreeId"/> resolves a
        /// tree id to a mission display name (null / null-returning is fine - the text then names
        /// the vessel without a mission clause). Pure.</para>
        /// </summary>
        internal static List<GhostPlaybackLogic.LoopSeamMarker> Build(
            DockEventGraph graph,
            string treeId,
            IReadOnlyDictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindows,
            IReadOnlyDictionary<string, int> committedIndexById,
            Func<string, string> missionNameByTreeId,
            out int r2Count,
            out int r3Count)
        {
            r2Count = 0;
            r3Count = 0;
            var markers = new List<GhostPlaybackLogic.LoopSeamMarker>();
            if (graph == null || string.IsNullOrEmpty(treeId)
                || memberWindows == null || memberWindows.Count == 0
                || committedIndexById == null)
                return markers;

            // TryDescribePartner takes (treeId, recordingId) -> mission name; this builder only ever
            // needs the tree half (a mission is per tree), so adapt rather than widen the resolver.
            Func<string, string, string> nameResolver =
                missionNameByTreeId == null ? (Func<string, string, string>)null
                                            : (t, _) => missionNameByTreeId(t);

            List<DockEventNode> nodes = graph.NodesForTree(treeId);
            for (int n = 0; n < nodes.Count; n++)
            {
                DockEventNode node = nodes[n];
                // Undock nodes have no seam to explain (nothing appears; the departing child is its
                // own member or it is not in this mission at all). A visibility-flagged node is a
                // re-fly-superseded dock: skipping it is the same rule every other graph consumer
                // applies (design 6.3 step 3).
                if (node == null || !node.IsMerge || node.VisibilityFlagged)
                    continue;

                int mergedIndex = ResolveIndex(committedIndexById, node.MergedChildRecordingId);
                GhostPlaybackLogic.LoopUnit.MemberWindow mergedWindow = default(GhostPlaybackLogic.LoopUnit.MemberWindow);
                bool mergedIsMember = mergedIndex >= 0
                    && memberWindows.TryGetValue(mergedIndex, out mergedWindow);
                if (mergedIsMember)
                {
                    if (TryBuildMergeAppear(
                            graph, node, mergedIndex, mergedWindow, memberWindows,
                            committedIndexById, nameResolver,
                            out GhostPlaybackLogic.LoopSeamMarker r2))
                    {
                        markers.Add(r2);
                        r2Count++;
                    }
                    // R3 asks for the merged child NOT to be a member (design 7.3): this mission
                    // replays right through the dock, so no line ends here.
                    continue;
                }

                r3Count += AddDockedVanish(
                    graph, node, memberWindows, committedIndexById, nameResolver, markers);
            }

            // Sorted by SeamUT: the runtime cursor (GhostPlaybackLogic.TryResolveSeamMarkerToEmit)
            // depends on it, and a total order keeps the list deterministic for tests.
            markers.Sort(CompareMarkers);

            if (!SuppressLogging)
            {
                var ic = CultureInfo.InvariantCulture;
                ParsekLog.Verbose(Tag,
                    "unit tree=" + treeId
                    + " markers=" + markers.Count.ToString(ic)
                    + " r2=" + r2Count.ToString(ic)
                    + " r3=" + r3Count.ToString(ic));
            }
            return markers;
        }

        // ---- R2: the merged stack grew because a partner this mission does not replay joined ----

        private static bool TryBuildMergeAppear(
            DockEventGraph graph,
            DockEventNode node,
            int mergedIndex,
            GhostPlaybackLogic.LoopUnit.MemberWindow mergedWindow,
            IReadOnlyDictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindows,
            IReadOnlyDictionary<string, int> committedIndexById,
            Func<string, string, string> nameResolver,
            out GhostPlaybackLogic.LoopSeamMarker marker)
        {
            marker = default(GhostPlaybackLogic.LoopSeamMarker);

            // The seam must fall inside the window this member actually renders. An interval-level
            // trim that starts the merged child AFTER the dock means the player never sees the
            // growth, and an explanation for an off-screen moment is noise.
            if (!GhostPlaybackLogic.IsLoopUTInMemberWindow(
                    node.UT, mergedWindow.StartUT, mergedWindow.EndUT))
                return false;

            string partnerRecordingId = null;
            string partnerName = null;
            string partnerMission = null;
            if (DockEventGraph.TryDescribePartner(
                    graph, node.BranchPointId, node.MergedChildRecordingId, nameResolver,
                    out DockPartnerDescription described))
            {
                partnerRecordingId = described.PartnerRecordingId;
                partnerName = described.PartnerVesselName;
                partnerMission = described.PartnerMissionName;
            }
            else if (node.Partner.Status == DockPartnerStatus.TwoParentSameTree)
            {
                // Both parents are equally "the partner" seen from the merged child, so
                // TryDescribePartner declines by contract (naming one would be arbitrary). Here the
                // MEMBERSHIP breaks the tie: the parent this mission does not replay is exactly the
                // half that materialises.
                if (!TryFindNonMemberParent(
                        node, memberWindows, committedIndexById,
                        out partnerRecordingId, out partnerName))
                    return false;                 // every parent is a member: the growth is explained
                partnerMission = nameResolver != null
                    ? nameResolver(node.TreeId, partnerRecordingId)
                    : null;
            }

            // The partner IS a member: this mission replays both halves, the stack growing is the
            // two ghosts meeting, and there is nothing to explain (design 7.3).
            if (!string.IsNullOrEmpty(partnerRecordingId)
                && IsMember(memberWindows, committedIndexById, partnerRecordingId))
                return false;

            marker = new GhostPlaybackLogic.LoopSeamMarker(
                node.UT, node.UT + SeamWindowSeconds,
                GhostPlaybackLogic.SeamMarkerKind.MergeAppear, mergedIndex,
                FormatMergeAppear(partnerName, partnerMission));
            return true;
        }

        // ---- R3: a member's line ends AT a dock this mission does not replay past ----

        private static int AddDockedVanish(
            DockEventGraph graph,
            DockEventNode node,
            IReadOnlyDictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindows,
            IReadOnlyDictionary<string, int> committedIndexById,
            Func<string, string, string> nameResolver,
            List<GhostPlaybackLogic.LoopSeamMarker> markers)
        {
            int emitted = 0;
            // Only a PARTICIPANT of this merge can have been absorbed by it: one of its parents (the
            // controller's own pre-dock line - edge case 18's excluded `@dock`), or the claimed
            // partner recording (the partner mission replaying its own line with the link off). A
            // member that merely happens to end at the same UT is a coincidence, not a dock, and
            // labelling it "docked" would be a lie.
            for (int p = 0; p < node.ParentRecordingIds.Count; p++)
                emitted += TryAddDockedVanishFor(
                    graph, node, node.ParentRecordingIds[p], memberWindows, committedIndexById,
                    nameResolver, markers);
            emitted += TryAddDockedVanishFor(
                graph, node, node.Partner.PartnerRecordingId, memberWindows, committedIndexById,
                nameResolver, markers);
            return emitted;
        }

        private static int TryAddDockedVanishFor(
            DockEventGraph graph,
            DockEventNode node,
            string viewerRecordingId,
            IReadOnlyDictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindows,
            IReadOnlyDictionary<string, int> committedIndexById,
            Func<string, string, string> nameResolver,
            List<GhostPlaybackLogic.LoopSeamMarker> markers)
        {
            int index = ResolveIndex(committedIndexById, viewerRecordingId);
            if (index < 0
                || !memberWindows.TryGetValue(index, out GhostPlaybackLogic.LoopUnit.MemberWindow w))
                return 0;
            // "Coincides" is the loop math's own boundary tolerance, so the seam agrees with the
            // window check that hides the member on the very next frame.
            if (Math.Abs(w.EndUT - node.UT) > LoopTiming.BoundaryEpsilon)
                return 0;

            string partnerName = null;
            if (DockEventGraph.TryDescribePartner(
                    graph, node.BranchPointId, viewerRecordingId, nameResolver,
                    out DockPartnerDescription described))
            {
                partnerName = described.PartnerVesselName;
            }
            // "Continues in" names the mission that owns the MERGE, not the partner's mission: the
            // combined flight from the dock onward is always recorded by the tree the branch point
            // lives in (design 2.7, narrative custody). That is right in both directions - the
            // partner mission is told where its vessel's story went, and the controller's own
            // mission with the `@dock` interval excluded (edge case 18) is told the stretch is its
            // own, just not included.
            string partnerMission = nameResolver != null ? nameResolver(node.TreeId, null) : null;

            markers.Add(new GhostPlaybackLogic.LoopSeamMarker(
                node.UT, node.UT + SeamWindowSeconds,
                GhostPlaybackLogic.SeamMarkerKind.DockedVanish, index,
                FormatDockedVanish(partnerName, partnerMission)));
            return 1;
        }

        // ---- text (design 7.3; generic wording on every unresolved status, design 6.4) ----

        internal static string FormatMergeAppear(string partnerName, string partnerMissionName)
        {
            if (string.IsNullOrEmpty(partnerName))
                return "joined by another vessel";
            if (string.IsNullOrEmpty(partnerMissionName))
                return "joined by " + partnerName;
            return "joined by " + partnerName + " - see mission '" + partnerMissionName + "'";
        }

        internal static string FormatDockedVanish(string partnerName, string partnerMissionName)
        {
            if (string.IsNullOrEmpty(partnerName))
                return "docked to another vessel";
            if (string.IsNullOrEmpty(partnerMissionName))
                return "docked to " + partnerName;
            return "docked to " + partnerName + " - continues in mission '" + partnerMissionName + "'";
        }

        // ---- helpers ----

        private static bool TryFindNonMemberParent(
            DockEventNode node,
            IReadOnlyDictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindows,
            IReadOnlyDictionary<string, int> committedIndexById,
            out string parentRecordingId,
            out string parentVesselName)
        {
            for (int i = 0; i < node.ParentRecordingIds.Count; i++)
            {
                string id = node.ParentRecordingIds[i];
                if (string.IsNullOrEmpty(id) || IsMember(memberWindows, committedIndexById, id))
                    continue;
                parentRecordingId = id;
                parentVesselName = i < node.ParentVesselNames.Count ? node.ParentVesselNames[i] : null;
                return true;
            }
            parentRecordingId = null;
            parentVesselName = null;
            return false;
        }

        private static bool IsMember(
            IReadOnlyDictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> memberWindows,
            IReadOnlyDictionary<string, int> committedIndexById,
            string recordingId)
        {
            int index = ResolveIndex(committedIndexById, recordingId);
            return index >= 0 && memberWindows.ContainsKey(index);
        }

        private static int ResolveIndex(
            IReadOnlyDictionary<string, int> committedIndexById, string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId) || committedIndexById == null)
                return -1;
            return committedIndexById.TryGetValue(recordingId, out int index) ? index : -1;
        }

        private static int CompareMarkers(
            GhostPlaybackLogic.LoopSeamMarker a, GhostPlaybackLogic.LoopSeamMarker b)
        {
            int cmp = a.SeamUT.CompareTo(b.SeamUT);
            if (cmp != 0)
                return cmp;
            cmp = a.MemberIndex.CompareTo(b.MemberIndex);
            if (cmp != 0)
                return cmp;
            cmp = ((int)a.Kind).CompareTo((int)b.Kind);
            return cmp != 0 ? cmp : string.CompareOrdinal(a.Text, b.Text);
        }
    }
}
