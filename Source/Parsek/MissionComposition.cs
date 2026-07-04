using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    // The "vessel composition over time" read model for the Missions window. Each node is a
    // physical vessel during one STRUCTURAL interval, labeled with counts like
    // "pod x1, probe x1, crew x3". A structural interval ends only when a CONTROLLER separates
    // (a stage / probe / lander decouples): that peels the separated piece off as a sibling and
    // starts a new interval for the continuing survivor. A crew change (a kerbal going EVA) does
    // NOT end the interval - the vessel continues with the same controllers, so the kerbal hangs
    // off the interval it left during as a child leaf and the interval label shows the SURVIVING
    // crew. So a launch stack that decouples a probe and later sheds a kerbal on EVA reads:
    //   Kerbal X (pod x1, probe x1, crew x3)   Launch -> Decoupled
    //   |- Kerbal X (pod x1, crew x2)          Decoupled -> <terminal>   (the survivor, spans the EVA)
    //   |  \- Bob Kerman                        EVA -> <terminal>          (the kerbal that left it)
    //   \- Kerbal X Probe (probe x1)           Decoupled -> <terminal>    (the peeled probe)
    //
    // M-MIS-5 (D1): a Dock / Board MERGE on the continuing line is ALSO an interval boundary.
    // A run member that began at a Dock/Board branch point contributes an interval edge at its
    // StartUT, SUBDIVIDING the structural interval it falls inside. Sub-intervals after the
    // first are keyed "<parentIntervalKey>@dockM" (M = 1-based ordinal of the merge edge inside
    // that structural interval) so the structural "/segN" ordinals - computed over structural
    // edges ONLY - never renumber when a dock edge appears (D3). The docked interval's label
    // REBASES to the merge leg's own start-captured composition (the combined vessel), fixing
    // the docked-label undercount, and a structural peel applied on a rebased base subtracts
    // the departing leg's CREW as well as its controllers (D2). A merge UT coincident with a
    // structural edge or a run endpoint dedups away (structural identity wins, no @dock key),
    // but its label rebase still applies from that boundary on.
    //
    // Derivation: each controlled MissionLeg already carries its own composition (pod/probe/
    // seat/crew counts, populated from Recording.Controllers + StartCrew). The full continuation
    // through-line of one physical vessel (env-split continuations + the vessel-continuation fork
    // child) is walked into one run; structural peels split that run's timeline into intervals,
    // and the survivor's controllers are the start composition minus the peels removed so far
    // (correct even when the continuing recording's start-captured Controllers are stale).
    // Pure: no Unity calls, no shared mutable state.

    internal sealed class MissionCompositionNode
    {
        public string HeadLegId;          // the leg that begins this interval (keying / selection)
        public string VesselName;         // vessel name, or kerbal name for an EVA-kerbal atom
        public string CompositionLabel;   // "pod x1, probe x1, crew x3", or the atom name
        public double StartUT;
        public double EndUT;
        public string StartEvent;         // what created this composition (Launch / Decouple / EVA / Dock / Board)
        public string EndEvent;           // what ended it (a split/merge event, or a terminal)
        public bool IsLeaf;               // single atom: nothing to expand
        public bool IsAtom;               // a roster atom (one pod/probe/crew) under a terminal: no own interval

        // The through-line head this interval belongs to: the recording id of the physical vessel
        // whose timeline this interval is a slice of. Every structural interval of one vessel
        // (e.g. the launch stack and the post-decouple survivor) shares the same OwnerHeadId, so
        // including a subset of them defines that vessel's render window. A peeled branch (probe,
        // EVA kerbal) is its own through-line, so its OwnerHeadId == its HeadLegId. Null on roster
        // atoms (they are not independently selectable). Used by MissionIntervalSelection.
        public string OwnerHeadId;

        // True when this node is an independently selectable interval / branch (every node except
        // a roster atom). The interval-level Missions selection (start-trim, branch keep/drop)
        // toggles these by HeadLegId; atoms carry no checkbox.
        public bool IsSelectable;

        public readonly List<MissionCompositionNode> Children = new List<MissionCompositionNode>();
    }

    internal static class MissionCompositionBuilder
    {
        // Set true to silence the single per-build Verbose batch summary. Set by the per-frame /
        // per-tick callers that rebuild the composition as a pure derivation on every call
        // (MissionsWindowUI per-frame display caches, RouteOrchestrator.ResolveLoopUnit) so the
        // diagnostic line does not flood - mirrors MissionStructureBuilder.SuppressLogging.
        internal static bool SuppressLogging;

        // Per-Build batch counters (house batch-counting convention): accumulated across every
        // BuildNode of one Build call, logged once as a single Verbose summary.
        private sealed class BuildTally
        {
            public int Intervals;
            public int StructuralEdges;
            public int MergeEdges;
            public int Rebases;
            public int AdditiveFallbacks;
        }

        /// <summary>
        /// Builds the composition trees for a mission structure: one root node per top-level
        /// vessel (launch root / disconnected root), each decomposing over time into its
        /// composition-change sub-tree. Pure.
        /// </summary>
        internal static List<MissionCompositionNode> Build(MissionStructure s)
        {
            var roots = new List<MissionCompositionNode>();
            if (s == null || s.LegsById.Count == 0)
                return roots;

            var visited = new HashSet<string>();
            var tally = new BuildTally();
            for (int i = 0; i < s.RootLegIds.Count; i++)
            {
                MissionCompositionNode node = BuildNode(s, s.RootLegIds[i], "Launch", visited, tally);
                if (node != null)
                    roots.Add(node);
            }
            if (!SuppressLogging)
                ParsekLog.Verbose("Mission",
                    $"BuildComposition: tree={s.TreeId ?? "<null>"} intervals={tally.Intervals} " +
                    $"structuralEdges={tally.StructuralEdges} mergeEdges={tally.MergeEdges} " +
                    $"rebases={tally.Rebases} additiveFallbacks={tally.AdditiveFallbacks}");
            return roots;
        }

        // One flat (post-subdivision) interval of a run: a structural interval, or a dock/board
        // sub-interval of one. MergeLegAtStart is the merge leg whose edge begins this interval
        // (null for the first sub-interval of a structural interval).
        private struct IntervalSpec
        {
            public double StartUT;
            public double EndUT;
            public string Key;
            public MissionLeg MergeLegAtStart;
        }

        // Builds the composition node for one physical vessel's through-line, split into
        // structural intervals (and dock/board sub-intervals, M-MIS-5). A structural peel (a
        // controller separating) ends an interval and starts the survivor's; a crew peel (an EVA
        // kerbal) hangs off the interval it left during without ending it. A through-line member
        // reached a second time (a merge) is guarded by the shared visited set (it terminates the
        // line that reaches it second).
        private static MissionCompositionNode BuildNode(
            MissionStructure s, string headLegId, string startEvent, HashSet<string> visited,
            BuildTally tally)
        {
            if (headLegId == null || !s.LegsById.TryGetValue(headLegId, out MissionLeg headLeg))
                return null;
            if (visited.Contains(headLegId))
                return null;

            // 1. Walk the FULL continuation through-line of this physical vessel (env-split
            //    continuations + the vessel-continuation fork child). Structural peels split this
            //    run into intervals afterward; crew peels attach without splitting.
            var run = new List<MissionLeg>();
            string cur = headLegId;
            while (cur != null && !visited.Contains(cur) && s.LegsById.TryGetValue(cur, out MissionLeg leg))
            {
                visited.Add(cur);
                run.Add(leg);
                cur = MissionThroughLineBuilder.ContinuationSuccessor(s, leg);
            }
            MissionLeg lastLeg = run[run.Count - 1];
            double runStart = headLeg.StartUT;
            double runEnd = lastLeg.EndUT;

            // 2. Peels across the run, classified: an EVA kerbal child is a CREW peel (it does not
            //    change the vessel's controllers, so it never starts a new interval - it hangs off
            //    the interval it left during); any other branch child is a STRUCTURAL peel (a
            //    controller separated, which DOES start a new interval for the survivor).
            var structuralPeels = new List<string>();
            var crewPeels = new List<string>();
            var runSet = new HashSet<string>();
            for (int r = 0; r < run.Count; r++) runSet.Add(run[r].RecordingId);
            for (int r = 0; r < run.Count; r++)
            {
                MissionLeg leg = run[r];
                string legSucc = MissionThroughLineBuilder.ContinuationSuccessor(s, leg);
                for (int c = 0; c < leg.BranchChildIds.Count; c++)
                {
                    string childId = leg.BranchChildIds[c];
                    if (childId == legSucc) continue;          // the vessel continuation, in the run
                    if (runSet.Contains(childId)) continue;    // a later run member (merge)
                    if (!s.LegsById.TryGetValue(childId, out MissionLeg childLeg)) continue;
                    var bucket = !string.IsNullOrEmpty(childLeg.EvaCrewName) ? crewPeels : structuralPeels;
                    if (!bucket.Contains(childId)) bucket.Add(childId);
                }
            }
            structuralPeels.Sort((a, b) => CompareLegStart(s, a, b));
            crewPeels.Sort((a, b) => CompareLegStart(s, a, b));

            // 2b. M-MIS-5 (D1): merge legs - run members (index >= 1) that BEGAN at a Dock/Board
            //     branch point. Each contributes an interval edge at its StartUT (clamped into the
            //     run) and a label rebase (D2). A run HEAD that begins via Dock produces no extra
            //     edge (its edge would coincide with the run start), and env-split continuations of
            //     a merged child carry OriginBranchPointType == null so they never re-trigger.
            var mergeLegs = new List<MissionLeg>();
            for (int r = 1; r < run.Count; r++)
            {
                MissionLeg m = run[r];
                if (m.OriginBranchPointType != BranchPointType.Dock
                    && m.OriginBranchPointType != BranchPointType.Board)
                    continue;
                if (double.IsNaN(m.StartUT))
                {
                    // D6 fail-closed: unusable merge UT -> no edge, no rebase.
                    if (!SuppressLogging)
                        ParsekLog.Verbose("Mission",
                            $"BuildComposition: merge leg {m.RecordingId} has unusable StartUT (NaN); no edge");
                    continue;
                }
                mergeLegs.Add(m);
            }
            mergeLegs.Sort((a, b) =>
            {
                int cmp = a.StartUT.CompareTo(b.StartUT);
                return cmp != 0 ? cmp : string.CompareOrdinal(a.RecordingId, b.RecordingId);
            });
            // Classify each merge leg once for the batch tally + the D2 additive-fallback log.
            for (int m = 0; m < mergeLegs.Count; m++)
            {
                if (HasOwnComposition(mergeLegs[m]))
                {
                    tally.Rebases++;
                }
                else
                {
                    tally.AdditiveFallbacks++;
                    if (!SuppressLogging)
                        ParsekLog.Verbose("Mission",
                            $"BuildComposition: merge leg {mergeLegs[m].RecordingId} carries no own " +
                            "composition; additive fallback engaged (partner start compositions added " +
                            "to the running base instead of a rebase)");
                }
            }

            // 3. STRUCTURAL interval edges: the run start, each distinct structural-peel UT (a
            //    controller separation, clamped into the run), and the run end. The "/segN"
            //    ordinals are computed over THESE edges only (D3), so a dock edge never renumbers
            //    an existing key.
            var sEdges = new List<double> { runStart };
            for (int i = 0; i < structuralPeels.Count; i++)
            {
                double ut = PeelUT(s, structuralPeels[i], runStart);
                if (ut < runStart) ut = runStart;
                if (ut > runEnd) ut = runEnd;
                if (!sEdges.Contains(ut)) sEdges.Add(ut);
            }
            if (!sEdges.Contains(runEnd)) sEdges.Add(runEnd);
            sEdges.Sort();
            int structuralSegCount = sEdges.Count - 1; // at least one structural interval

            // 3b. M-MIS-5 (D1/D3): merge edges, clamped into the run and dedup'd against the
            //     structural edges and each other. A merge UT coincident with a structural edge or
            //     a run endpoint mints NO @dock key (structural identity wins; the rebase below
            //     still applies from that boundary).
            var mergeEdges = new List<KeyValuePair<double, MissionLeg>>();
            for (int m = 0; m < mergeLegs.Count; m++)
            {
                double ut = mergeLegs[m].StartUT;
                if (ut < runStart) ut = runStart;
                if (ut > runEnd) ut = runEnd;
                if (sEdges.Contains(ut)) continue;
                bool duplicate = false;
                for (int e = 0; e < mergeEdges.Count; e++)
                    if (mergeEdges[e].Key == ut) { duplicate = true; break; }
                if (duplicate) continue;
                mergeEdges.Add(new KeyValuePair<double, MissionLeg>(ut, mergeLegs[m]));
            }
            tally.StructuralEdges += structuralSegCount - 1;
            tally.MergeEdges += mergeEdges.Count;

            // 3c. Flat interval list: each structural interval, subdivided at the merge edges
            //     strictly inside it. Sub-interval keys after the first are
            //     "<parentIntervalKey>@dockM" (M = 1-based ordinal inside the structural interval).
            var intervals = new List<IntervalSpec>();
            for (int i = 0; i < structuralSegCount; i++)
            {
                string parentKey = (i == 0)
                    ? headLegId
                    : headLegId + "/seg" + i.ToString(CultureInfo.InvariantCulture);
                var inner = new List<KeyValuePair<double, MissionLeg>>();
                for (int e = 0; e < mergeEdges.Count; e++)
                    if (mergeEdges[e].Key > sEdges[i] && mergeEdges[e].Key < sEdges[i + 1])
                        inner.Add(mergeEdges[e]);
                inner.Sort((a, b) => a.Key.CompareTo(b.Key));

                double subStart = sEdges[i];
                for (int j = 0; j <= inner.Count; j++)
                {
                    double subEnd = (j == inner.Count) ? sEdges[i + 1] : inner[j].Key;
                    intervals.Add(new IntervalSpec
                    {
                        StartUT = subStart,
                        EndUT = subEnd,
                        Key = (j == 0)
                            ? parentKey
                            : parentKey + "@dock" + j.ToString(CultureInfo.InvariantCulture),
                        MergeLegAtStart = (j == 0) ? null : inner[j - 1].Value,
                    });
                    subStart = subEnd;
                }
            }
            int segCount = intervals.Count;
            tally.Intervals += segCount;

            // Flat boundary list (structural + merge edges) for the peel-attachment lookups.
            var edges = new List<double>(segCount + 1) { intervals[0].StartUT };
            for (int i = 0; i < segCount; i++)
                edges.Add(intervals[i].EndUT);

            // 4. One node per interval: controllers/crew per D2 - base = the head-leg composition
            //    minus the structural peels removed at or before the interval start (byte-identical
            //    to the pre-M-MIS-5 model when no merge leg precedes it), REBASED to the latest
            //    preceding merge leg's own start-captured composition when one exists (the combined
            //    vessel), with structural peels applied on a rebased base subtracting the departing
            //    leg's crew too. Crew = the roster surviving at the interval's END.
            var segNodes = new List<MissionCompositionNode>();
            MissionLeg lastSegLeg = null;
            for (int i = 0; i < segCount; i++)
            {
                double segStart = intervals[i].StartUT;
                double segEnd = intervals[i].EndUT;

                MissionLeg segLeg = BuildIntervalLeg(
                    s, headLeg, lastLeg, intervals[i].Key, segStart, segEnd,
                    structuralPeels, crewPeels, mergeLegs, runSet,
                    isFirst: i == 0, isLast: i == segCount - 1);

                var node = new MissionCompositionNode
                {
                    HeadLegId = segLeg.RecordingId,
                    // All structural intervals of this physical vessel share the through-line head
                    // so the selection can derive ONE render window per vessel from them.
                    OwnerHeadId = headLegId,
                    IsSelectable = true,
                    VesselName = VesselLabel(segLeg),
                    CompositionLabel = FormatComposition(segLeg),
                    StartUT = segStart,
                    EndUT = segEnd,
                    StartEvent = (i == 0)
                        ? startEvent
                        : (intervals[i].MergeLegAtStart != null
                            ? MergeEventName(intervals[i].MergeLegAtStart)
                            : StructuralPeelEventAt(s, structuralPeels, segStart)),
                    EndEvent = (i == segCount - 1)
                        ? TerminalName(lastLeg.TerminalStateValue)
                        : (intervals[i + 1].MergeLegAtStart != null
                            ? MergeEventName(intervals[i + 1].MergeLegAtStart)
                            : StructuralPeelEventAt(s, structuralPeels, segEnd)),
                };
                segNodes.Add(node);
                if (i == segCount - 1) lastSegLeg = segLeg;
            }

            // 5. Chain the intervals: each interval's survivor (the next interval) is its first
            //    child, so the continuing vessel always reads above the pieces that left it.
            for (int i = 0; i + 1 < segCount; i++)
                segNodes[i].Children.Add(segNodes[i + 1]);

            // 6. Structural peels: attach the peeled piece (its own subtree) to the interval it
            //    separated from (the interval ending at the peel's UT).
            for (int p = 0; p < structuralPeels.Count; p++)
            {
                double ut = PeelUT(s, structuralPeels[p], runStart);
                if (ut < runStart) ut = runStart;
                if (ut > runEnd) ut = runEnd;
                int segIdx = SegmentEndingAt(edges, ut);
                s.LegsById.TryGetValue(structuralPeels[p], out MissionLeg pl);
                MissionCompositionNode child = BuildNode(s, structuralPeels[p], OriginEventName(pl), visited, tally);
                if (child != null)
                    segNodes[segIdx].Children.Add(child);
            }

            // 7. Crew peels: attach the kerbal to the interval that is live when it left.
            for (int p = 0; p < crewPeels.Count; p++)
            {
                s.LegsById.TryGetValue(crewPeels[p], out MissionLeg cp);
                int segIdx = SegmentContaining(edges, cp != null ? cp.StartUT : runStart);
                MissionCompositionNode child = BuildNode(s, crewPeels[p], OriginEventName(cp), visited, tally);
                if (child != null)
                    segNodes[segIdx].Children.Add(child);
            }

            // 8. The last interval, when it ends as a whole with nothing peeling at the end, is a
            //    terminal: a single atom is a leaf, a multi-atom terminal expands into its atoms.
            MissionCompositionNode last = segNodes[segCount - 1];
            if (last.Children.Count == 0)
            {
                if (IsSingleAtom(lastSegLeg))
                    last.IsLeaf = true;
                else
                    AddAtomChildren(last, lastSegLeg);
            }

            return segNodes[0];
        }

        // Synthesizes the display leg for one (possibly dock-subdivided) interval: the D2 label
        // math. Base = the head-leg composition with structural peels at/before segStart removed
        // (controllers only - byte-identical to the pre-M-MIS-5 model), REBASED to the latest
        // preceding merge leg's own composition when one exists; structural peels applied on a
        // rebased/merged base subtract the departing leg's CrewNames/CrewCount too (verdict C2). A
        // merge leg with no composition of its own falls back to ADDITIVE (the other parent legs'
        // start compositions are added to the running base; logged once per build in BuildNode).
        // Peels are applied CHUNKED between merge boundaries: everything in
        // (previousBoundary, mergeUT] belongs to the PRE-merge base, so a full rebase replaces an
        // already-peeled base wholesale (crew gone before the dock is baked into the merged
        // roster - no double subtraction) and the additive fallback adds the partner back onto an
        // already-peeled base (undock-then-redock and EVA-then-board stay net-correct). The final
        // chunk applies structural peels to segStart and crew peels to segEnd (roster = the crew
        // surviving at the interval's END, exactly the pre-M-MIS-5 contract).
        private static MissionLeg BuildIntervalLeg(
            MissionStructure s, MissionLeg headLeg, MissionLeg lastLeg, string key,
            double segStart, double segEnd, List<string> structuralPeels, List<string> crewPeels,
            List<MissionLeg> mergeLegs, HashSet<string> runSet, bool isFirst, bool isLast)
        {
            int pod = headLeg.PodCount, probe = headLeg.ProbeCount, seat = headLeg.SeatCount;
            var roster = new List<string>(headLeg.CrewNames);
            int crewFallback = headLeg.CrewCount;
            bool namedBase = headLeg.CrewNames.Count > 0; // which crew-count path the base uses
            bool rebased = false;                         // any merge edge folded in so far
            double prevBound = double.NegativeInfinity;   // exclusive lower bound of the next peel chunk

            for (int m = 0; m < mergeLegs.Count; m++)
            {
                MissionLeg merge = mergeLegs[m];
                if (merge.StartUT > segStart)
                    break; // sorted: no later merge applies to this interval
                // Peels between the previous boundary and this merge belong to the PRE-merge
                // base: apply them BEFORE folding the merge (see the chunking contract above).
                ApplyStructuralPeels(s, structuralPeels, prevBound, merge.StartUT,
                    ref pod, ref probe, ref seat, roster, ref crewFallback, rebased);
                ApplyCrewPeels(s, crewPeels, prevBound, merge.StartUT, roster, ref crewFallback);
                if (HasOwnComposition(merge))
                {
                    pod = merge.PodCount;
                    probe = merge.ProbeCount;
                    seat = merge.SeatCount;
                    roster.Clear();
                    roster.AddRange(merge.CrewNames);
                    crewFallback = merge.CrewCount;
                    namedBase = merge.CrewNames.Count > 0;
                }
                else
                {
                    // D2 additive fallback: add the OTHER parent legs' start compositions.
                    // Acknowledged residual: this adds the partner LEG's start composition, not
                    // its at-merge composition (a partner that shed a controller mid-leg
                    // overcounts by it) - accepted for a logged fallback path. namedBase is
                    // deliberately NOT flipped by partner names: a nameless head base keeps
                    // counting through crewFallback so the head's unnamed crew are not lost.
                    for (int p = 0; p < merge.BranchParentIds.Count; p++)
                    {
                        string parentId = merge.BranchParentIds[p];
                        if (parentId == null || runSet.Contains(parentId))
                            continue; // the run predecessor (this vessel's own line)
                        if (!s.LegsById.TryGetValue(parentId, out MissionLeg partner))
                            continue;
                        pod += partner.PodCount;
                        probe += partner.ProbeCount;
                        seat += partner.SeatCount;
                        for (int n = 0; n < partner.CrewNames.Count; n++)
                            if (!roster.Contains(partner.CrewNames[n]))
                                roster.Add(partner.CrewNames[n]);
                        crewFallback += partner.CrewCount;
                    }
                }
                rebased = true;
                prevBound = merge.StartUT;
            }
            ApplyStructuralPeels(s, structuralPeels, prevBound, segStart,
                ref pod, ref probe, ref seat, roster, ref crewFallback, rebased);

            // Defensive floor (pre-M-MIS-5 this clamp silently hid the departing-partner
            // subtraction against a base that never contained the partner; with the D2 rebase the
            // base DOES contain it, so the clamp is no longer load-bearing - it stays as a floor).
            if (pod < 0) pod = 0;
            if (probe < 0) probe = 0;
            if (seat < 0) seat = 0;

            // Crew = the roster surviving at this interval's END: the remaining crew peels in
            // (lastMergeUT, segEnd]. With no merge the chunk is (-infinity, segEnd] and this is
            // byte-identical to the pre-M-MIS-5 model; peels at/before a FULL rebase were applied
            // to the replaced pre-merge base, so the merged roster is never double-subtracted.
            ApplyCrewPeels(s, crewPeels, prevBound, segEnd, roster, ref crewFallback);
            int crew = namedBase ? roster.Count : System.Math.Max(0, crewFallback);

            var segLeg = new MissionLeg
            {
                RecordingId = key,
                VesselName = headLeg.VesselName,
                EvaCrewName = isFirst ? headLeg.EvaCrewName : null,
                PodCount = pod,
                ProbeCount = probe,
                SeatCount = seat,
                CrewCount = crew,
                StartUT = segStart,
                EndUT = segEnd,
                TerminalStateValue = isLast ? lastLeg.TerminalStateValue : null,
            };
            segLeg.CrewNames.AddRange(roster);
            return segLeg;
        }

        // Subtracts every structural peel with raw StartUT in (lowerExclusive, upperInclusive]
        // from the running base. Controllers always; crew ONLY on a rebased/merged base (verdict
        // C2: before any merge the head roster never contains a partner's crew, and subtracting
        // crew there would change the pre-M-MIS-5 labels).
        private static void ApplyStructuralPeels(
            MissionStructure s, List<string> structuralPeels,
            double lowerExclusive, double upperInclusive,
            ref int pod, ref int probe, ref int seat,
            List<string> roster, ref int crewFallback, bool rebased)
        {
            for (int p = 0; p < structuralPeels.Count; p++)
            {
                if (!s.LegsById.TryGetValue(structuralPeels[p], out MissionLeg pl))
                    continue;
                double ut = pl.StartUT;
                if (ut <= lowerExclusive || ut > upperInclusive)
                    continue;
                pod -= pl.PodCount;
                probe -= pl.ProbeCount;
                seat -= pl.SeatCount;
                if (rebased)
                {
                    for (int n = 0; n < pl.CrewNames.Count; n++)
                        roster.Remove(pl.CrewNames[n]);
                    crewFallback -= pl.CrewCount;
                }
            }
        }

        // Subtracts every crew peel (EVA kerbal) with StartUT in (lowerExclusive, upperInclusive]
        // from the running roster: the named kerbal leaves the roster, and the nameless
        // crew-count fallback decrements per peel (clamped at 0 by the caller, matching the
        // pre-M-MIS-5 Math.Max(0, CrewCount - leftCount)).
        private static void ApplyCrewPeels(
            MissionStructure s, List<string> crewPeels,
            double lowerExclusive, double upperInclusive,
            List<string> roster, ref int crewFallback)
        {
            for (int p = 0; p < crewPeels.Count; p++)
            {
                if (!s.LegsById.TryGetValue(crewPeels[p], out MissionLeg cp))
                    continue;
                double ut = cp.StartUT;
                if (ut <= lowerExclusive || ut > upperInclusive)
                    continue;
                crewFallback -= 1;
                if (!string.IsNullOrEmpty(cp.EvaCrewName))
                    roster.Remove(cp.EvaCrewName);
            }
        }

        // True when a merge leg carries a usable own composition (the recorder's fresh combined
        // capture); false engages the D2 additive fallback.
        private static bool HasOwnComposition(MissionLeg leg)
        {
            return leg.PodCount + leg.ProbeCount + leg.SeatCount > 0
                || leg.CrewCount > 0
                || leg.CrewNames.Count > 0;
        }

        // The boundary-event label of a merge edge ("Docked" / "Boarded").
        private static string MergeEventName(MissionLeg mergeLeg)
        {
            return mergeLeg.OriginBranchPointType.HasValue
                ? BranchEventName(mergeLeg.OriginBranchPointType.Value, mergeLeg.OriginCause)
                : "";
        }

        // The UT a peel separated at: its origin branch point UT, surfaced as the leg start.
        private static double PeelUT(MissionStructure s, string peelId, double fallback)
            => s.LegsById.TryGetValue(peelId, out MissionLeg pl) ? pl.StartUT : fallback;

        // The origin event of the structural peel(s) at a given interval boundary UT.
        private static string StructuralPeelEventAt(MissionStructure s, List<string> structuralPeels, double ut)
        {
            for (int i = 0; i < structuralPeels.Count; i++)
                if (s.LegsById.TryGetValue(structuralPeels[i], out MissionLeg pl) && pl.StartUT == ut)
                    return OriginEventName(pl);
            return "";
        }

        // The interval whose END boundary is this UT (the one a structural peel separated from).
        private static int SegmentEndingAt(List<double> edges, double ut)
        {
            for (int i = 0; i + 1 < edges.Count; i++)
                if (edges[i + 1] == ut)
                    return i;
            return 0; // a peel clamped to the run start attaches to the first interval
        }

        // The interval that contains this UT (the one live when a crew peel left).
        private static int SegmentContaining(List<double> edges, double ut)
        {
            for (int i = 0; i + 1 < edges.Count; i++)
                if (ut >= edges[i] && ut < edges[i + 1])
                    return i;
            return edges.Count - 2 >= 0 ? edges.Count - 2 : 0; // at/after the run end -> last interval
        }

        // --- Composition helpers (pure, individually testable) ---

        // A leg is a single atom (a leaf with nothing to expand) when it is one EVA kerbal, or
        // exactly one controller with no crew.
        internal static bool IsSingleAtom(MissionLeg leg)
        {
            if (!string.IsNullOrEmpty(leg.EvaCrewName))
                return true;
            int controllers = leg.PodCount + leg.ProbeCount + leg.SeatCount;
            return controllers == 1 && leg.CrewCount == 0;
        }

        // The composition label: "pod x1, probe x1, crew x3" (zero categories omitted). An EVA
        // kerbal leg shows the kerbal's name (it is an atom, not a count bag).
        internal static string FormatComposition(MissionLeg leg)
        {
            if (!string.IsNullOrEmpty(leg.EvaCrewName))
                return leg.EvaCrewName;

            var sb = new StringBuilder();
            AppendCount(sb, "pod", leg.PodCount);
            AppendCount(sb, "probe", leg.ProbeCount);
            AppendCount(sb, "seat", leg.SeatCount);
            AppendCount(sb, "crew", leg.CrewCount);
            return sb.Length > 0 ? sb.ToString() : "(no controllers)";
        }

        private static void AppendCount(StringBuilder sb, string label, int n)
        {
            if (n <= 0) return;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(label).Append(" x").Append(n.ToString(CultureInfo.InvariantCulture));
        }

        private static string VesselLabel(MissionLeg leg)
        {
            if (!string.IsNullOrEmpty(leg.EvaCrewName))
                return leg.EvaCrewName;
            return string.IsNullOrEmpty(leg.VesselName) ? "(vessel)" : leg.VesselName;
        }

        // Optional final expansion of a stable multi-atom terminal: one node per controller
        // (by type) plus crew. Crew shows as one named leaf per kerbal when names are recorded
        // (Recording.CrewEndStates), otherwise falls back to a single "crew xN" count atom.
        private static void AddAtomChildren(MissionCompositionNode node, MissionLeg leg)
        {
            AddAtoms(node, "Pod", leg.PodCount);
            AddAtoms(node, "Probe", leg.ProbeCount);
            AddAtoms(node, "Seat", leg.SeatCount);

            if (leg.CrewNames != null && leg.CrewNames.Count > 0)
            {
                for (int i = 0; i < leg.CrewNames.Count; i++)
                    AddCrewAtom(node, leg, leg.CrewNames[i]);
            }
            else if (leg.CrewCount > 0)
            {
                AddCrewAtom(node, leg, "crew x" + leg.CrewCount.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void AddCrewAtom(MissionCompositionNode node, MissionLeg leg, string label)
        {
            node.Children.Add(new MissionCompositionNode
            {
                HeadLegId = leg.RecordingId,
                VesselName = label,
                CompositionLabel = label,
                IsLeaf = true,
                IsAtom = true,
            });
        }

        private static void AddAtoms(MissionCompositionNode node, string label, int n)
        {
            for (int i = 0; i < n; i++)
                node.Children.Add(new MissionCompositionNode
                {
                    HeadLegId = node.HeadLegId,
                    VesselName = label,
                    CompositionLabel = label,
                    IsLeaf = true,
                    IsAtom = true,
                });
        }

        private static int CompareLegStart(MissionStructure s, string a, string b)
        {
            double sa = s.LegsById.TryGetValue(a, out MissionLeg la) ? la.StartUT : 0.0;
            double sb = s.LegsById.TryGetValue(b, out MissionLeg lb) ? lb.StartUT : 0.0;
            int cmp = sa.CompareTo(sb);
            return cmp != 0 ? cmp : string.CompareOrdinal(a, b);
        }

        // --- Event-name helpers (BranchPointType / TerminalState -> short labels) ---

        internal static string OriginEventName(MissionLeg leg)
        {
            if (leg == null) return "";
            if (!string.IsNullOrEmpty(leg.EvaCrewName)) return "EVA";
            return leg.OriginBranchPointType.HasValue
                ? BranchEventName(leg.OriginBranchPointType.Value, leg.OriginCause)
                : (leg.IsRoot ? "Launch" : "");
        }

        // Maps a branch point to a short label. The CAUSE wins over the bare type, because a
        // deliberate decoupler firing and a structural joint break are BOTH BranchPointType.
        // JointBreak (and a separator can also surface as Undock): only the cause distinguishes
        // a clean "Decoupled" from an accidental "Broke off".
        internal static string BranchEventName(BranchPointType t, string cause)
        {
            switch (cause)
            {
                case "DECOUPLE": return "Decoupled";
                case "UNDOCK": return "Undocked";
                case "CRASH": return "Crashed";
                case "OVERHEAT": return "Overheated";
                case "STRUCTURAL_FAILURE": return "Broke up";
            }
            switch (t)
            {
                case BranchPointType.Undock: return "Undocked";
                case BranchPointType.EVA: return "EVA";
                case BranchPointType.Dock: return "Docked";
                case BranchPointType.Board: return "Boarded";
                case BranchPointType.JointBreak: return "Broke off";
                case BranchPointType.Breakup: return "Broke up";
                case BranchPointType.Launch: return "Launch";
                case BranchPointType.Terminal: return "End";
                case BranchPointType.VesselSwitchContinuation: return "Switch";
                default: return t.ToString();
            }
        }

        internal static string TerminalName(TerminalState? t)
        {
            if (!t.HasValue) return "";
            switch (t.Value)
            {
                case TerminalState.Orbiting: return "Orbiting";
                case TerminalState.Landed: return "Landed";
                case TerminalState.Splashed: return "Splashed";
                case TerminalState.SubOrbital: return "Suborbital";
                case TerminalState.Destroyed: return "Destroyed";
                case TerminalState.Recovered: return "Recovered";
                case TerminalState.Docked: return "Docked";
                case TerminalState.Boarded: return "Boarded";
                default: return t.Value.ToString();
            }
        }
    }
}
