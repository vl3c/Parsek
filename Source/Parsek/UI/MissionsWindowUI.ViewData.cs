using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // The tab's derived data: mission views and composition roots, the dock event graph and partner text, vessel rows and names, summary facts, the loop unit set, event digests, the tree index map, mission spans and the sorted mission rows.
    internal partial class MissionsWindowUI
    {
        private static RecordingTree FindTree(List<RecordingTree> trees, string treeId)
        {
            if (trees == null || string.IsNullOrEmpty(treeId))
                return null;
            for (int i = 0; i < trees.Count; i++)
                if (trees[i] != null && trees[i].Id == treeId)
                    return trees[i];
            return null;
        }

        // Returns the derived (structure, view) for a tree, building it at most once per
        // frame per tree id. The first lookup in a new frame clears the cache so the data
        // stays fresh; later lookups in the same frame (other OnGUI passes, or other
        // missions targeting the same tree) hit the cache instead of rebuilding.
        private (MissionStructure structure, MissionThroughLineView view) GetMissionView(RecordingTree tree)
        {
            int frame = Time.frameCount;
            if (frame != missionViewCacheFrame)
            {
                missionViewCache.Clear();
                compositionCache.Clear();
                vesselRowsCache.Clear();
                foreignLinksCache.Clear();
                journeyLegsCache.Clear();
                dockPartnerTextCache.Clear();

                summaryFactsCache.Clear();
                missionViewCacheFrame = frame;
            }

            if (!missionViewCache.TryGetValue(tree.Id, out var cached))
            {
                // Per-frame display rebuild: suppress the per-build "BuildMissionStructure:"
                // Verbose summary so the Missions window does not flood the log (a 2026-06-06
                // orbital-route playtest logged ~5.2k of these lines from this path). The
                // structure built is byte-identical; only the diagnostic line is gated. Mirrors
                // the route-pipeline suppress wrap (RouteOrchestrator.ResolveLoopUnit) and
                // GetLoopUnitSet below; MissionPeriodicity is gated too for parity (defensive -
                // MissionStructureBuilder.Build emits no periodicity lines today). Restored in
                // finally inside BuildForDisplay.
                MissionStructure structure = MissionStructureBuilder.BuildForDisplay(tree);
                MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);
                cached = (structure, view);
                missionViewCache[tree.Id] = cached;
            }

            return cached;
        }

        // Returns the cached composition-over-time root nodes for a tree (built once per frame
        // from the same MissionStructure that GetMissionView caches).
        private List<MissionCompositionNode> GetCompositionRoots(RecordingTree tree)
        {
            if (!compositionCache.TryGetValue(tree.Id, out var roots))
            {
                var (structure, _) = GetMissionView(tree);
                // Per-frame display rebuild (M-MIS-5): suppress the per-build
                // "BuildComposition:" Verbose batch summary exactly like the structure
                // summary above - the composition built is byte-identical, only the
                // diagnostic line is gated. Restored in finally.
                bool prevCompSuppress = MissionCompositionBuilder.SuppressLogging;
                MissionCompositionBuilder.SuppressLogging = true;
                try
                {
                    roots = MissionCompositionBuilder.Build(structure);
                }
                finally
                {
                    MissionCompositionBuilder.SuppressLogging = prevCompSuppress;
                }
                compositionCache[tree.Id] = roots;
            }
            return roots;
        }

        // R6 double-clock advisory (design-dock-event-graph.md 7.7, gated on the section-7.8
        // verification, which CONFIRMED the collision - see
        // docs/dev/done/research/double-clock-verification-2026-08-13.md). Called on loop-ENABLE only:
        // when the freshly-looping mission's tree is dock-connected (transitively) to a tree whose
        // own mission is ALSO looping, the two loops run independent clocks over recordings that
        // share physical matter, so one vessel can be on screen twice at two recorded times. ONE
        // ScreenMessage, no enforcement.
        //
        // The link-include toggle does NOT need this: including a partner-journey link calls
        // MissionStore.ClearLoopsConflictingWith, which turns the foreign tree's own loop OFF, so
        // the two-concurrent-loops state the advisory describes cannot survive that path.
        private void PostDoubleClockAdvisoryIfAny(Mission mission)
        {
            string advisory = MissionStore.TryDescribeDoubleClockAdvisory(
                mission, MissionStore.Missions, GetDockEventGraph());
            if (string.IsNullOrEmpty(advisory))
                return;
            try
            {
                ScreenMessages.PostScreenMessage(
                    "[Parsek] " + advisory, 8f, ScreenMessageStyle.UPPER_LEFT);
            }
            catch (System.Exception ex)
            {
                // A ScreenMessages failure must never take the Missions-window draw pass with it.
                ParsekLog.Warn("Mission",
                    $"double-clock advisory PostScreenMessage threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- dock-partner naming (design-dock-event-graph.md 6.5 / 7.6) ----

        // The global dock-event graph, fetched at most once per frame. NOT log-suppressed: the
        // host cache only rebuilds on a committed-topology signature change, and that rebuild is
        // exactly the event the one [DockGraph] summary line exists for.
        private DockEventGraph GetDockEventGraph()
        {
            int frame = Time.frameCount;
            if (frame != dockEventGraphCacheFrame || dockEventGraphCache == null)
            {
                dockEventGraphCache = DockEventGraphCache.GetOrBuild();
                dockEventGraphCacheFrame = frame;
            }
            return dockEventGraphCache;
        }

        // (treeId, recordingId) -> mission name, the resolver DockEventGraph.TryDescribePartner
        // takes so the pure core stays free of Mission / MissionStore references. The recording
        // id is unused today: a tree's narrative custody is its original mission (clones are
        // player-made views of the same tree, and naming one of those as "the partner's mission"
        // would be arbitrary).
        internal static string ResolvePartnerMissionName(string treeId, string recordingId)
        {
            Mission m = MissionStore.FindOriginalMission(treeId);
            return m != null ? m.Name : null;
        }

        /// <summary>
        /// The dock partner named by a composition interval whose START is a Dock / Board merge:
        /// "with CD (mission 'CD Freighter')", or null when there is nothing to name (design 6.4
        /// keeps the tables silent exactly where they are silent today). Cached per frame.
        /// </summary>
        private string GetIntervalDockPartnerText(Mission mission, MissionCompositionNode node)
        {
            if (mission == null || node == null || node.IsAtom
                || string.IsNullOrEmpty(mission.TreeId)
                || !IsMergeStartEvent(node.StartEvent))
                return null;

            string key = mission.TreeId + KeySeparator + (node.HeadLegId ?? "");
            if (dockPartnerTextCache.TryGetValue(key, out string cached))
                return cached;

            string text = BuildIntervalDockPartnerText(mission, node);
            dockPartnerTextCache[key] = text;
            return text;
        }

        private static bool IsMergeStartEvent(string startEvent)
            => string.Equals(startEvent, DockedEventLabel, System.StringComparison.Ordinal)
               || string.Equals(startEvent, BoardedEventLabel, System.StringComparison.Ordinal);

        private string BuildIntervalDockPartnerText(Mission mission, MissionCompositionNode node)
        {
            DockEventGraph graph = GetDockEventGraph();
            if (graph == null)
                return null;

            // Map interval -> branch point by UT, restricted to branch points this tree OWNS.
            // Composition interval edges are cut FROM the merge legs' UTs (the same doubles the
            // branch points carry), so the epsilon only absorbs representation noise. Restricting
            // to owned nodes removes the only ambiguity the UT match could have: a foreign node
            // resolved INTO this tree shares no interval edge with it.
            DockEventNode merge = null;
            List<DockEventNode> candidates = graph.NodesForTree(mission.TreeId);
            for (int i = 0; i < candidates.Count; i++)
            {
                DockEventNode n = candidates[i];
                if (n.IsMerge && !n.VisibilityFlagged
                    && string.Equals(n.TreeId, mission.TreeId, System.StringComparison.Ordinal)
                    && System.Math.Abs(n.UT - node.StartUT) <= DockPartnerUTEpsilon)
                {
                    merge = n;
                    break;
                }
            }
            if (merge == null)
                return null;

            // Viewer = this row's own line, so the description names the OTHER vessel. The
            // through-line head is the mission's side of the merge in the ordinary shape; when it
            // is not one of the branch point's parents, a single-parent merge can still answer
            // from the parent (the partner is the claimed recording either way), while a
            // TWO-parent merge must stay silent rather than guess which parent the row is on -
            // guessing is exactly how a row ends up naming its own vessel as its partner.
            if (!DockEventGraph.TryDescribePartner(
                    graph, merge.BranchPointId, node.OwnerHeadId,
                    ResolvePartnerMissionName, out DockPartnerDescription d))
            {
                if (merge.Partner.Status == DockPartnerStatus.TwoParentSameTree
                    || merge.ParentRecordingIds.Count == 0
                    || !DockEventGraph.TryDescribePartner(
                        graph, merge.BranchPointId, merge.ParentRecordingIds[0],
                        ResolvePartnerMissionName, out d))
                    return null;
            }
            return FormatDockPartner(d);
        }

        // (The Mission-parameter DrawStartEventCell overload this branch carried merged into the
        // RowDeriveContext cell above: the T1.4 two-parent walk answers first, the dock-event
        // graph answers what it cannot - cross-tree and recovered same-tree partners, with the
        // partner's mission name.)

        /// <summary>"with CD (mission 'CD Freighter')", or "with CD" when no mission names it.</summary>
        internal static string FormatDockPartner(DockPartnerDescription d)
        {
            if (string.IsNullOrEmpty(d.PartnerVesselName))
                return null;
            return string.IsNullOrEmpty(d.PartnerMissionName)
                ? "with " + d.PartnerVesselName
                : "with " + d.PartnerVesselName + " (mission '" + d.PartnerMissionName + "')";
        }

        // Returns the cached T2.2 flattened per-vessel rows for a tree (built once per frame
        // from the same composition roots that GetCompositionRoots caches). The dock-partner
        // resolver names same-tree Dock / Board boundaries in the inline event phrase
        // ("Docked (Munport Station)") - resolved here at row-build time, once per frame,
        // rather than per row per pass.
        private List<MissionVesselRow> GetVesselRows(RecordingTree tree)
        {
            if (!vesselRowsCache.TryGetValue(tree.Id, out var rows))
            {
                var (structure, view) = GetMissionView(tree);
                Dictionary<string, string> vesselNames = GetVesselNames(tree);
                // The naming pass's partner legs: another mission's vessel that joined this
                // flight, or its half after an undock, draws beside the vessel it joined or
                // left, not under it; and a ship that undocked from a pair carrying the
                // partner's identity keeps its own row through the undock.
                HashSet<string> partnerLegIds = vesselNamingCache.PartnerLegIds(tree.Id);
                rows = MissionVesselRowBuilder.Build(
                    GetCompositionRoots(tree),
                    (ownerHeadId, boundaryUT) =>
                        MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                            structure, view, ownerHeadId, boundaryUT, vesselNames),
                    vesselNames,
                    partnerLegIds,
                    (ownerHeadId, endUT) =>
                        MissionPresentation.ResolveTerminalDockPartnerVesselName(
                            structure, ownerHeadId, endUT, vesselNames),
                    MissionVesselRowBuilder.ResolveUndockSides(structure, partnerLegIds));
                vesselRowsCache[tree.Id] = rows;
            }
            return rows;
        }

        // The vessel names the mission Log uses too (MissionVesselNaming): numbered same-named
        // vessels, another mission's vessel as its partner phrase. Cached ACROSS frames
        // (MissionVesselNaming.Cache, keyed on StateVersion + the missions' names): a steady
        // frame is one dictionary read per tree.
        private Dictionary<string, string> GetVesselNames(RecordingTree tree)
        {
            if (vesselNamingStructure == null)
                vesselNamingStructure = t => GetMissionView(t).structure;
            int frame = Time.frameCount;
            if (frame != vesselNamingSignatureFrame)
            {
                vesselNamingSignature = MissionVesselNaming.Cache.MissionSignature(MissionStore.Missions);
                vesselNamingSignatureFrame = frame;
            }
            Dictionary<string, string> names = vesselNamingCache.GetOrBuild(
                tree, vesselNamingStructure, RecordingStore.CommittedTrees,
                RecordingStore.StateVersion, vesselNamingSignature, VesselNamingMissionName);
            int rebuilds = vesselNamingCache.Rebuilds;
            if (rebuilds != vesselNamingRebuildsLogged)
            {
                vesselNamingRebuildsLogged = rebuilds;
                MissionVesselNaming.Tally t = vesselNamingCache.LastTally;
                int hits = vesselNamingCache.Hits;
                ParsekLog.VerboseRateLimited("Mission", "missions-vessel-names",
                    () => $"Missions vessel names rebuilt: tree={tree.Id} rebuilds={rebuilds} " +
                    $"hits={hits} legs={t.Legs} partners={t.Partners} numbered={t.Numbered} " +
                    $"continuations={t.Continuations}", 5.0);
            }
            return names;
        }

        // Returns the header summary-line facts for a tree (T1.1), derived at most once per frame
        // per tree from the same per-frame structure / through-line / composition caches. Facts are
        // tree-level (span, vessel count, crew union, terminal word), so two Missions over one tree
        // reuse one derivation.
        private MissionPresentation.MissionSummaryFacts GetSummaryFacts(RecordingTree tree)
        {
            if (tree == null || string.IsNullOrEmpty(tree.Id))
                return default;
            if (!summaryFactsCache.TryGetValue(tree.Id, out MissionPresentation.MissionSummaryFacts facts))
            {
                var (structure, view) = GetMissionView(tree);
                var compRoots = GetCompositionRoots(tree);
                GetVesselNames(tree); // primes the partner legs the vessel count leaves out
                facts = MissionPresentation.ComputeSummaryFacts(structure, view, compRoots,
                    vesselNamingCache.PartnerLegIds(tree.Id));
                // T2.1: the narrative body path, from the SAME primary vessel the terminal word
                // came from (composition roots[0]) - deriving the two halves of one sentence
                // from two independently-sorted root lists could narrate one vessel's journey
                // next to another vessel's outcome on a multi-root tree. Bodies live on
                // Recordings, not on the read models, so this is stamped here where the tree
                // is in hand.
                string primaryHeadId = compRoots.Count > 0 && compRoots[0] != null
                    ? compRoots[0].OwnerHeadId : null;
                facts.BodyPath = MissionPresentation.BuildBodyPathText(
                    CollectMainLineBodySequences(tree, view, primaryHeadId));
                // Frame-invariant display strings, built once per rebuild rather than per pass.
                facts.StartDateText = facts.HasSpan ? KSPUtil.PrintDateCompact(facts.StartUT, true) : "";
                facts.EndDateText = facts.HasSpan ? KSPUtil.PrintDateCompact(facts.EndUT, true) : "";
                facts.DetailTooltip = MissionPresentation.BuildSummaryDetailTooltip(
                    facts.StartDateText, facts.EndDateText, facts.VesselCount, facts.CrewNames);
                summaryFactsCache[tree.Id] = facts;
                var loggedFacts = facts;
                // Per-tree rate key: the line carries the tree id, so a shared key would let
                // only the first-drawn tree ever log its facts.
                ParsekLog.VerboseRateLimited("Mission", "missions-summary-facts|" + tree.Id,
                    () => $"Missions summary facts: tree={tree.Id} span={(loggedFacts.HasSpan ? "yes" : "no")} " +
                    $"vessels={loggedFacts.VesselCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"crew={loggedFacts.CrewCount.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"terminal='{loggedFacts.TerminalWord ?? ""}'", 5.0);
            }
            return facts;
        }

        // The per-leg body-transition sequences of the tree's PRIMARY through-line, in leg
        // order, for the T2.1 narrative body path. primaryHeadId names the vessel (the same
        // composition roots[0] owner the terminal word is derived from; null falls back to the
        // through-line view's first root). Each leg's sequence comes from its committed
        // Recording via RecordingSegmentLabels.GetBodyTransitionSequence; a leg with no recording
        // contributes nothing. Returns null when no through-line resolves (the body-path
        // builder then yields null and the summary line falls back to span dates). Internal
        // static for direct testability (Testing Requirements).
        internal static List<List<string>> CollectMainLineBodySequences(
            RecordingTree tree, MissionThroughLineView view, string primaryHeadId)
        {
            if (tree == null || tree.Recordings == null || view == null)
                return null;
            if (string.IsNullOrEmpty(primaryHeadId) && view.RootHeadIds.Count > 0)
                primaryHeadId = view.RootHeadIds[0];
            if (string.IsNullOrEmpty(primaryHeadId)
                || !view.ByHeadId.TryGetValue(primaryHeadId, out MissionThroughLine tl)
                || tl == null)
                return null;

            var sequences = new List<List<string>>(tl.MemberLegIds.Count);
            for (int i = 0; i < tl.MemberLegIds.Count; i++)
            {
                if (tl.MemberLegIds[i] != null
                    && tree.Recordings.TryGetValue(tl.MemberLegIds[i], out Recording rec)
                    && rec != null)
                {
                    sequences.Add(RecordingSegmentLabels.GetBodyTransitionSequence(rec));
                }
            }
            return sequences;
        }

        // Returns the REAL Mission LoopUnitSet, built at most once per frame, EXACTLY as the scene
        // drivers build it (MissionLoopUnitBuilder.Build over MissionStore.Missions +
        // RecordingStore.CommittedTrees/CommittedRecordings, with the global auto-loop interval and
        // FlightGlobalsBodyInfo.Instance). The unit the engine actually relaunches on is read off
        // this set so the T- countdown is drift-free against playback. Display-only: never pushed to
        // the engine. The build's per-call Verbose summary plus the inner structure / periodicity
        // diagnostics are suppressed (the UI runs every frame).
        // [ERS-exempt] reason: this reads the RAW committed list to match MissionLoopUnitBuilder
        // (which keys loop members by committed RecordingId); display-only, file allowlisted.
        private GhostPlaybackLogic.LoopUnitSet GetLoopUnitSet()
        {
            int frame = Time.frameCount;
            if (frame != loopUnitSetCacheFrame || loopUnitSetCache == null)
            {
                var settings = ParsekSettings.Current;
                double autoLoopIntervalSeconds = settings != null
                    ? settings.autoLoopIntervalSeconds
                    : LoopTiming.DefaultLoopIntervalSeconds;
                // Same pinned transited-body rotation mode the scene drivers use, so the display
                // mirror matches the engine's schedule.
                TransitedBodyRotationMode tbrMode = ParsekSettings.LandingBodyAlignmentMode;
                // Same force-faithful knob the scene drivers read (harness-only since the
                // 2026-08-27 settings simplification - for players ParsekScenario.OnLoad clamps
                // it false), so the display mirror shows the faithful schedule when a harness
                // run forces it.
                bool forceFaithful = settings?.forceFaithfulLoopPlayback ?? false;
                // MissionLoopUnitBuilder.Build internally rebuilds the mission structure, the
                // composition (M-MIS-5), and runs the periodicity solver, so gate ALL FOUR
                // pipeline diagnostic flags - not just the loop builder's own - or the inner
                // "BuildMissionStructure:" / "BuildComposition:" / MissionPeriodicity
                // ExtractConstraints/Solve lines still flood once per frame per looping mission.
                // Mirrors RouteOrchestrator.ResolveLoopUnit. Restored in finally.
                bool prevStructSuppress = MissionStructureBuilder.SuppressLogging;
                bool prevCompSuppress = MissionCompositionBuilder.SuppressLogging;
                bool prevPeriodicitySuppress = MissionPeriodicity.SuppressLogging;
                bool prevLoopSuppress = MissionLoopUnitBuilder.SuppressLogging;
                bool prevCrossTreeSuppress = MissionCrossTreeDock.SuppressLogging;
                MissionStructureBuilder.SuppressLogging = true;
                MissionCompositionBuilder.SuppressLogging = true;
                MissionPeriodicity.SuppressLogging = true;
                MissionLoopUnitBuilder.SuppressLogging = true;
                // M-MIS-8: Build resolves cross-tree dock links for linked looping missions,
                // so the fifth pipeline flag must be gated too or the FindLinks / journey /
                // member-merge Verbose lines flood once per frame.
                MissionCrossTreeDock.SuppressLogging = true;
                try
                {
                    loopUnitSetCache = MissionLoopUnitBuilder.Build(
                        MissionStore.Missions, RecordingStore.CommittedTrees,
                        RecordingStore.CommittedRecordings, autoLoopIntervalSeconds,
                        FlightGlobalsBodyInfo.Instance, tbrMode, forceFaithful);
                }
                finally
                {
                    MissionStructureBuilder.SuppressLogging = prevStructSuppress;
                    MissionCompositionBuilder.SuppressLogging = prevCompSuppress;
                    MissionPeriodicity.SuppressLogging = prevPeriodicitySuppress;
                    MissionLoopUnitBuilder.SuppressLogging = prevLoopSuppress;
                    MissionCrossTreeDock.SuppressLogging = prevCrossTreeSuppress;
                }
                loopUnitSetCacheFrame = frame;
            }
            return loopUnitSetCache ?? GhostPlaybackLogic.LoopUnitSet.Empty;
        }

        // Digest rows for a mission, rebuilt only when the dock-event graph instance changes (a
        // committed-topology move) or the mission's included-link set does (the R5 gap rows'
        // only selection input). Everything else about the digest derives from the tree, whose
        // changes move the graph signature too.
        private List<MissionEventRow> GetEventDigest(Mission mission, RecordingTree tree)
        {
            if (mission == null || string.IsNullOrEmpty(mission.Id) || tree == null)
                return null;

            DockEventGraph graph = GetDockEventGraph();
            int fingerprint = ForeignLinkFingerprint(mission);
            if (digestCache.TryGetValue(mission.Id, out DigestCacheEntry entry)
                && ReferenceEquals(entry.Graph, graph)
                && entry.LinkFingerprint == fingerprint)
                return entry.Rows;

            List<MissionEventRow> rows = MissionEventDigest.Build(
                graph, tree, mission, ResolvePartnerMissionName, ResolvePartnerMissionId);
            digestCache[mission.Id] = new DigestCacheEntry
            {
                Graph = graph,
                LinkFingerprint = fingerprint,
                Rows = rows,
            };
            return rows;
        }

        // Order-independent, allocation-free fold over the mission's included foreign dock links
        // (typically zero of them). Only used to decide whether a cached digest is stale, so an
        // XOR fold is enough: a change that collides would have to swap one link id for another
        // with an identical hash in the same frame.
        private static int ForeignLinkFingerprint(Mission mission)
        {
            int hash = mission.IncludedForeignDockLinkIds.Count;
            foreach (string id in mission.IncludedForeignDockLinkIds)
                hash ^= id != null ? id.GetHashCode() : 0;
            return hash;
        }

        // treeId -> mission id, the GoTo half of the digest's resolvers (the NAME half is
        // ResolvePartnerMissionName above). Same pick as the name resolver and as
        // RevealMissionForRecording: a tree's narrative custody is its ORIGINAL mission.
        internal static string ResolvePartnerMissionId(string treeId)
        {
            Mission m = MissionStore.FindOriginalMission(treeId);
            return m != null ? m.Id : null;
        }

        // Linear lookup of a committed recording by id (the Missions tab maps a composition row to
        // its recording for the Re-Fly cell). Returns false when the id is empty or not committed.
        // [ERS-exempt] reason: Re-Fly is keyed on the RAW committed index, same as the watch /
        // rewind buttons (which shift under an ERS supersede); MissionsWindowUI.cs is allowlisted.
        private static bool TryResolveCommittedRecording(
            string recordingId, out int index, out Recording rec)
        {
            index = -1;
            rec = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording r = committed[i];
                if (r != null && r.RecordingId == recordingId)
                {
                    index = i;
                    rec = r;
                    return true;
                }
            }
            return false;
        }

        // Per-tree index numbers, 1-based, assigned in committed-tree order. Every Mission of a
        // tree (the default plus any clones) shares the tree's number, so a clone reads as a copy
        // of the same indexed mission and renaming never renumbers anything.
        private static Dictionary<string, int> BuildTreeIndexMap(List<RecordingTree> trees)
        {
            var map = new Dictionary<string, int>(System.StringComparer.Ordinal);
            if (trees == null)
                return map;
            int n = 0;
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t == null || string.IsNullOrEmpty(t.Id) || map.ContainsKey(t.Id))
                    continue;
                map[t.Id] = ++n;
            }
            return map;
        }

        // The mission span start UT = earliest root through-line start (offshoots leave later).
        private static double MissionSpanStartUT(MissionThroughLineView view)
        {
            double min = double.PositiveInfinity;
            if (view != null)
                for (int r = 0; r < view.RootHeadIds.Count; r++)
                    if (view.ByHeadId.TryGetValue(view.RootHeadIds[r], out MissionThroughLine tl)
                        && tl.StartUT < min)
                        min = tl.StartUT;
            return double.IsInfinity(min) ? 0.0 : min;
        }

        // The mission span in seconds = (max trimmed end - min trimmed start) over the COMMITTED
        // loop members, computed via the SAME MissionLoopUnitBuilder.ComputeTrimmedMemberWindows the
        // loop builder derives its span from - so the period cell's effective-cadence display matches
        // the cadence actually running, including interval-level start/end trims.
        // Returns 0 when no committed member is included (no overlap; cell shows the raw period).
        private double MissionSpanSeconds(
            MissionThroughLineView view, Mission mission, IReadOnlyList<Recording> committed)
        {
            if (view == null || committed == null)
                return 0.0;
            RecordingTree tree = FindTree(RecordingStore.CommittedTrees, mission.TreeId);
            if (tree == null)
                return 0.0;

            var windows = MissionLoopUnitBuilder.ComputeTrimmedMemberWindows(
                view, GetCompositionRoots(tree), committed, mission.ExcludedIntervalKeys,
                null, out _, out _);

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;
            foreach (var w in windows.Values)
            {
                if (w.StartUT < min) min = w.StartUT;
                if (w.EndUT > max) max = w.EndUT;
            }
            if (double.IsInfinity(min) || double.IsInfinity(max))
                return 0.0;
            return System.Math.Max(0.0, max - min);
        }

        // Builds the display-ordered (mission, index) list for the current sort column/direction.
        // Tiebreakers fall back to the tree index then the original list position so a tree's
        // clones stay grouped together (the clone was inserted right after its source).
        private List<(Mission mission, int index)> BuildSortedMissionRows(
            IReadOnlyList<Mission> missions, List<RecordingTree> trees, Dictionary<string, int> treeIndex)
        {
            var rows = new List<(Mission mission, int index, double startUT, int origPos)>();
            for (int i = 0; i < missions.Count; i++)
            {
                Mission m = missions[i];
                if (m == null)
                    continue;
                RecordingTree tree = FindTree(trees, m.TreeId);
                if (tree == null)
                    continue;
                int idx = treeIndex.TryGetValue(m.TreeId ?? "", out int ti) ? ti : 0;
                var (_, view) = GetMissionView(tree);
                rows.Add((m, idx, MissionSpanStartUT(view), i));
            }

            rows.Sort((a, b) => CompareMissionRows(
                a.mission.Name, a.index, a.startUT, a.origPos,
                b.mission.Name, b.index, b.startUT, b.origPos,
                sortColumn, sortAscending));

            var result = new List<(Mission, int)>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
                result.Add((rows[i].mission, rows[i].index));
            return result;
        }

        // Pure mission-row sort comparison (extracted for unit testing). Primary key per column;
        // tiebreak by tree index then original list position so a tree's clones stay grouped
        // (the clone is inserted right after its source). Descending negates the whole result.
        internal static int CompareMissionRows(
            string aName, int aIndex, double aStartUT, int aOrigPos,
            string bName, int bIndex, double bStartUT, int bOrigPos,
            MissionSortColumn column, bool ascending)
        {
            int cmp;
            switch (column)
            {
                case MissionSortColumn.Name:
                    cmp = string.Compare(aName ?? "", bName ?? "",
                        System.StringComparison.OrdinalIgnoreCase);
                    if (cmp == 0) cmp = aIndex.CompareTo(bIndex);
                    break;
                case MissionSortColumn.StartTime:
                    cmp = aStartUT.CompareTo(bStartUT);
                    if (cmp == 0) cmp = aIndex.CompareTo(bIndex);
                    break;
                default: // Index
                    cmp = aIndex.CompareTo(bIndex);
                    break;
            }
            if (cmp == 0) cmp = aOrigPos.CompareTo(bOrigPos);
            return ascending ? cmp : -cmp;
        }
    }
}
