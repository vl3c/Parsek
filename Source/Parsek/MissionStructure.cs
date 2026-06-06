using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    // Phase 1 read model for the Missions feature (the controlled-leg fork-tree).
    // See docs/dev/design-mission-abstractions.md. This derives, from a
    // RecordingTree, the directed graph of controlled legs the Missions window
    // renders and a Mission selection operates over. It is a pure projection:
    // no Unity calls, no shared mutable state, no recording mutation.

    /// <summary>
    /// One controlled leg: a single post-optimizer recording with
    /// <c>IsDebris == false</c>. Debris never becomes a leg; it rides along its
    /// parent at playback and is not represented here.
    /// </summary>
    internal sealed class MissionLeg
    {
        public string RecordingId;
        public string VesselName;
        public string ChainId;
        public int ChainBranch;
        public int ChainIndex;
        public double StartUT;
        public double EndUT;
        public TerminalState? TerminalStateValue;

        // Crew name if this leg is an EVA kerbal (its own controllable craft); null
        // for vessels. IsAnchoredOffshoot = this leg separated and is anchored to a
        // parent (a probe / lander / decoupled or broken-off child) rather than being
        // the continuing primary vessel. ContinuesAsVessel = the same vessel keeps
        // going past this leg (an env-split continuation, or a branch child that is
        // the vessel itself rather than a crew/offshoot).
        public string EvaCrewName;
        public bool IsAnchoredOffshoot;
        public bool ContinuesAsVessel;

        // Within-run (env-split) sequence links: same (ChainId, ChainBranch),
        // adjacent ChainIndex, no branch point between them. Null at run head/tail.
        // The UI stacks a run's legs (no indent) by walking SequenceNextId.
        public string SequencePrevId;
        public string SequenceNextId;

        // Cross-run links via a BranchPoint (fork / merge / continuation). The UI
        // indents at these. BranchParentIds.Count > 1 means a same-tree merge
        // (Dock/Board) converges on this leg.
        public readonly List<string> BranchParentIds = new List<string>();
        public readonly List<string> BranchChildIds = new List<string>();

        // The branch point this leg's run ENDS at (set on the run-tail leg) and the
        // branch point this leg's run STARTED from (set on the run-head leg). Used
        // for labeling rows by their end / origin event.
        public BranchPointType? EndBranchPointType;
        public BranchPointType? OriginBranchPointType;

        // The cause string of the origin / end branch point (BranchPoint.SplitCause, e.g.
        // "DECOUPLE" / "UNDOCK", or BranchPoint.BreakupCause, e.g. "CRASH"). Lets the event
        // label distinguish a deliberate decouple from a structural joint break (both are
        // BranchPointType.JointBreak). Null when the branch carries no cause.
        public string OriginCause;
        public string EndCause;

        // A leg with no run-predecessor and no branch-parent: a launch root or a
        // disconnected continuation root (ParentBranchPointId == null).
        public bool IsRoot;

        // True when this leg is the CONTINUING vessel of its origin branch point: the
        // recorder lists the continuing vessel first in BranchPoint.ChildRecordingIds (the
        // active vessel a split follows, or the single merged child of a Dock/Board). Set
        // on ChildRecordingIds[0]. Lets ContinuationSuccessor follow the controlled vessel
        // deterministically at a split where several children survive (an Undock fork has
        // two non-anchored, non-EVA children at the same UT, so without this the "main line"
        // vs offshoot assignment was an arbitrary GUID/StartUT tiebreak). Derived, not
        // serialized: recomputed from the (serialized) child order on every Build.
        public bool IsBranchContinuation;

        // Composition at this leg's start, derived from the recording's Controllers list
        // (classified by ControllerInfo.type) and StartCrew manifest. Drives the Missions
        // window "vessel composition over time" view: a controlled leg is rendered with
        // these counts (pod x1, probe x1, crew x3), and the composition tree branches
        // wherever the counts change between continuation legs. An EVA-kerbal leg
        // (EvaCrewName != null) carries no pod/probe/seat and is labeled by its kerbal name.
        public int PodCount;     // CrewedPod controllers (crewed command parts)
        public int ProbeCount;   // ProbeCore controllers (uncrewed command parts)
        public int SeatCount;    // ExternalSeat controllers
        public int CrewCount;    // total crew aboard at start (sum of StartCrew counts)

        // Crew kerbal NAMES aboard this leg (from Recording.CrewEndStates keys, sorted), used
        // to render named crew leaves in the composition roster. Empty when names are not
        // recorded (legacy / uncrewed) - the renderer then falls back to a "crew xN" count.
        public readonly List<string> CrewNames = new List<string>();
    }

    /// <summary>
    /// The derived controlled-leg fork-tree for one mission tree. A DAG: forks
    /// split lines, same-tree Dock/Board merges join them.
    /// </summary>
    internal sealed class MissionStructure
    {
        public string TreeId;
        public readonly Dictionary<string, MissionLeg> LegsById =
            new Dictionary<string, MissionLeg>();
        public readonly List<string> RootLegIds = new List<string>();
    }

    internal static class MissionStructureBuilder
    {
        // Set true to silence the per-build Verbose summary. Set by the per-tick /
        // per-frame callers that rebuild the structure as a pure derivation on every
        // call (the route delivery clock via RouteOrchestrator.ResolveLoopUnit) so the
        // diagnostic line does not flood - mirrors MissionLoopUnitBuilder.SuppressLogging
        // / MissionPeriodicity.SuppressLogging.
        internal static bool SuppressLogging;

        /// <summary>
        /// Derives the controlled-leg fork-tree from a recording tree. Pure.
        /// Nodes = controlled recordings (debris excluded). Within-run sequence
        /// edges group env-split legs by (ChainId, ChainBranch) ordered by
        /// ChainIndex; cross-run fork/merge/continuation edges come from
        /// RecordingTree.BranchPoints. Roots are legs with no predecessor.
        /// </summary>
        internal static MissionStructure Build(RecordingTree tree)
        {
            var structure = new MissionStructure { TreeId = tree?.Id };
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
            {
                if (!SuppressLogging)
                    ParsekLog.Verbose("Mission",
                        $"BuildMissionStructure: empty tree={tree?.Id ?? "<null>"}");
                return structure;
            }

            // 1. Controlled legs only. Debris rides along its parent at playback.
            int debrisExcluded = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (rec.IsDebris)
                {
                    debrisExcluded++;
                    continue;
                }
                var leg = new MissionLeg
                {
                    RecordingId = rec.RecordingId,
                    VesselName = rec.VesselName,
                    ChainId = rec.ChainId,
                    ChainBranch = rec.ChainBranch,
                    ChainIndex = rec.ChainIndex,
                    StartUT = rec.StartUT,
                    EndUT = rec.EndUT,
                    TerminalStateValue = rec.TerminalStateValue,
                    EvaCrewName = rec.EvaCrewName,
                    IsAnchoredOffshoot = !string.IsNullOrEmpty(rec.ParentAnchorRecordingId)
                };
                PopulateComposition(leg, rec);
                structure.LegsById[rec.RecordingId] = leg;
            }

            // 2. Within-run sequence links.
            int sequenceEdges = BuildSequenceLinks(structure);

            // 3. Cross-run branch links (forks / merges / continuations).
            int branchEdges = BuildBranchLinks(tree, structure);

            // 3b. ContinuesAsVessel: the same vessel keeps going past this leg, via an
            // env-split continuation or a branch child that is the vessel itself (not a
            // crew EVA or an anchored offshoot). Used to label "Continues" vs a terminal.
            foreach (var leg in structure.LegsById.Values)
            {
                bool continues = leg.SequenceNextId != null;
                if (!continues)
                {
                    for (int i = 0; i < leg.BranchChildIds.Count; i++)
                    {
                        if (structure.LegsById.TryGetValue(leg.BranchChildIds[i], out MissionLeg child)
                            && !child.IsAnchoredOffshoot
                            && string.IsNullOrEmpty(child.EvaCrewName))
                        {
                            continues = true;
                            break;
                        }
                    }
                }
                leg.ContinuesAsVessel = continues;
            }

            // 4. Roots: no run-predecessor and no branch-parent.
            int merges = 0;
            foreach (var leg in structure.LegsById.Values)
            {
                if (leg.BranchParentIds.Count > 1)
                    merges++;
                if (leg.SequencePrevId == null && leg.BranchParentIds.Count == 0)
                {
                    leg.IsRoot = true;
                    structure.RootLegIds.Add(leg.RecordingId);
                }
            }

            // Deterministic output ordering.
            SortLegIds(structure.RootLegIds, structure);
            foreach (var leg in structure.LegsById.Values)
            {
                SortLegIds(leg.BranchChildIds, structure);
                SortLegIds(leg.BranchParentIds, structure);
            }

            if (!SuppressLogging)
                ParsekLog.Verbose("Mission",
                    $"BuildMissionStructure: tree={structure.TreeId ?? "<null>"} " +
                    $"legs={structure.LegsById.Count} debrisExcluded={debrisExcluded} " +
                    $"sequenceEdges={sequenceEdges} branchEdges={branchEdges} " +
                    $"merges={merges} roots={structure.RootLegIds.Count}");
            return structure;
        }

        // Fills a leg's controller/crew composition from the recording. Controllers are
        // classified by ControllerInfo.type (KerbalEVA contributes nothing here - an EVA
        // kerbal leg is labeled by EvaCrewName, not a controller count). Crew is the sum of
        // the per-trait StartCrew manifest. Both are null-safe (legacy / uncrewed recordings).
        private static void PopulateComposition(MissionLeg leg, Recording rec)
        {
            if (rec.Controllers != null)
            {
                for (int i = 0; i < rec.Controllers.Count; i++)
                {
                    string t = rec.Controllers[i].type;
                    if (t == "CrewedPod") leg.PodCount++;
                    else if (t == "ProbeCore") leg.ProbeCount++;
                    else if (t == "ExternalSeat") leg.SeatCount++;
                    // "KerbalEVA": the leg IS an EVA kerbal (EvaCrewName set); no command count.
                }
            }
            if (rec.StartCrew != null)
            {
                foreach (var kv in rec.StartCrew)
                    leg.CrewCount += kv.Value;
            }
            // Crew NAMES: CrewEndStates is keyed by kerbal name (the roster aboard this leg);
            // the per-name value is its end state, which we do not need here. Sorted for a
            // deterministic roster order.
            if (rec.CrewEndStates != null && rec.CrewEndStates.Count > 0)
            {
                foreach (var name in rec.CrewEndStates.Keys)
                    if (!string.IsNullOrEmpty(name))
                        leg.CrewNames.Add(name);
                leg.CrewNames.Sort(System.StringComparer.Ordinal);
            }
            // Many recordings carry no per-trait StartCrew manifest but DO carry the named
            // CrewEndStates roster, so fall back to the name count for the crew total (otherwise
            // a crewed pod would show no crew at all). The names remain the authoritative roster.
            if (leg.CrewCount == 0 && leg.CrewNames.Count > 0)
                leg.CrewCount = leg.CrewNames.Count;
        }

        private static int BuildSequenceLinks(MissionStructure structure)
        {
            // Group controlled legs by (ChainId, ChainBranch). A null/empty ChainId
            // is a singleton run with no intra-run links.
            var runs = new Dictionary<string, List<MissionLeg>>();
            foreach (var leg in structure.LegsById.Values)
            {
                if (string.IsNullOrEmpty(leg.ChainId))
                    continue;
                string key = leg.ChainId + "|"
                    + leg.ChainBranch.ToString(CultureInfo.InvariantCulture);
                if (!runs.TryGetValue(key, out var list))
                {
                    list = new List<MissionLeg>();
                    runs[key] = list;
                }
                list.Add(leg);
            }

            int edges = 0;
            foreach (var list in runs.Values)
            {
                if (list.Count < 2)
                    continue;
                list.Sort(CompareLegSequence);
                for (int i = 0; i + 1 < list.Count; i++)
                {
                    list[i].SequenceNextId = list[i + 1].RecordingId;
                    list[i + 1].SequencePrevId = list[i].RecordingId;
                    edges++;
                }
            }
            return edges;
        }

        private static int CompareLegSequence(MissionLeg a, MissionLeg b)
        {
            // Single transitive key: (ChainIndex, StartUT, RecordingId). An unset
            // ChainIndex (-1) sorts after valid ones. Mixing index- and UT-based
            // comparisons in one comparer can violate transitivity and make
            // List.Sort throw, so this collapses to one lexicographic key.
            int ai = a.ChainIndex >= 0 ? a.ChainIndex : int.MaxValue;
            int bi = b.ChainIndex >= 0 ? b.ChainIndex : int.MaxValue;
            int cmp = ai.CompareTo(bi);
            if (cmp != 0)
                return cmp;
            cmp = a.StartUT.CompareTo(b.StartUT);
            if (cmp != 0)
                return cmp;
            return string.CompareOrdinal(a.RecordingId, b.RecordingId);
        }

        // Stable ordering for output collections whose build order follows
        // non-deterministic Dictionary / BranchPoint enumeration. Orders by
        // StartUT then RecordingId so the UI outline and positional assertions
        // are reproducible across loads.
        private static void SortLegIds(List<string> ids, MissionStructure structure)
        {
            if (ids.Count < 2)
                return;
            ids.Sort((x, y) =>
            {
                structure.LegsById.TryGetValue(x, out MissionLeg lx);
                structure.LegsById.TryGetValue(y, out MissionLeg ly);
                double sx = lx != null ? lx.StartUT : 0.0;
                double sy = ly != null ? ly.StartUT : 0.0;
                int cmp = sx.CompareTo(sy);
                if (cmp != 0)
                    return cmp;
                return string.CompareOrdinal(x, y);
            });
        }

        private static int BuildBranchLinks(RecordingTree tree, MissionStructure structure)
        {
            if (tree.BranchPoints == null)
                return 0;

            int edges = 0;
            var controlledParents = new List<MissionLeg>();
            foreach (var bp in tree.BranchPoints)
            {
                if (bp == null)
                    continue;

                controlledParents.Clear();
                if (bp.ParentRecordingIds != null)
                {
                    foreach (var pid in bp.ParentRecordingIds)
                    {
                        if (pid != null && structure.LegsById.TryGetValue(pid, out var pleg))
                        {
                            pleg.EndBranchPointType = bp.Type;
                            pleg.EndCause = bp.SplitCause ?? bp.BreakupCause;
                            controlledParents.Add(pleg);
                        }
                    }
                }

                if (bp.ChildRecordingIds == null)
                    continue;

                // The recorder lists the CONTINUING vessel first in ChildRecordingIds (the
                // active vessel a split follows, or the single merged child of a Dock/Board),
                // so mark it as the branch continuation. ContinuationSuccessor consults this
                // only among children that already pass its non-anchored / non-EVA filter, so
                // flagging an anchored peel that happens to be child[0] (a decouple BP with a
                // single anchored child) is harmless.
                if (bp.ChildRecordingIds.Count > 0
                    && bp.ChildRecordingIds[0] != null
                    && structure.LegsById.TryGetValue(bp.ChildRecordingIds[0], out var contLeg))
                {
                    contLeg.IsBranchContinuation = true;
                }

                foreach (var cid in bp.ChildRecordingIds)
                {
                    // Debris children (twigs) and foreign/missing ids are not legs.
                    if (cid == null || !structure.LegsById.TryGetValue(cid, out var cleg))
                        continue;
                    cleg.OriginBranchPointType = bp.Type;
                    cleg.OriginCause = bp.SplitCause ?? bp.BreakupCause;

                    foreach (var pleg in controlledParents)
                    {
                        if (!pleg.BranchChildIds.Contains(cid))
                            pleg.BranchChildIds.Add(cid);
                        if (!cleg.BranchParentIds.Contains(pleg.RecordingId))
                            cleg.BranchParentIds.Add(pleg.RecordingId);
                        edges++;
                    }
                }
            }
            return edges;
        }
    }
}
