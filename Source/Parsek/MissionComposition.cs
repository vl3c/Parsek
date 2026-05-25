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
            for (int i = 0; i < s.RootLegIds.Count; i++)
            {
                MissionCompositionNode node = BuildNode(s, s.RootLegIds[i], "Launch", visited);
                if (node != null)
                    roots.Add(node);
            }
            return roots;
        }

        // Builds the composition node for one physical vessel's through-line, split into
        // structural intervals. A structural peel (a controller separating) ends an interval and
        // starts the survivor's; a crew peel (an EVA kerbal) hangs off the interval it left
        // during without ending it. A through-line member reached a second time (a merge) is
        // guarded by the shared visited set (it terminates the line that reaches it second).
        private static MissionCompositionNode BuildNode(
            MissionStructure s, string headLegId, string startEvent, HashSet<string> visited)
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

            // 3. Interval edges: the run start, each distinct structural-peel UT (a controller
            //    separation, clamped into the run), and the run end.
            var edges = new List<double> { runStart };
            for (int i = 0; i < structuralPeels.Count; i++)
            {
                double ut = PeelUT(s, structuralPeels[i], runStart);
                if (ut < runStart) ut = runStart;
                if (ut > runEnd) ut = runEnd;
                if (!edges.Contains(ut)) edges.Add(ut);
            }
            if (!edges.Contains(runEnd)) edges.Add(runEnd);
            edges.Sort();
            int segCount = edges.Count - 1; // at least one interval

            // 4. One node per interval: controllers = start composition minus the structural peels
            //    removed at or before its start (correct even when a continuing recording's
            //    start-captured Controllers are stale); crew = the roster surviving at its END.
            var segNodes = new List<MissionCompositionNode>();
            MissionLeg lastSegLeg = null;
            for (int i = 0; i < segCount; i++)
            {
                double segStart = edges[i];
                double segEnd = edges[i + 1];

                int pod = headLeg.PodCount, probe = headLeg.ProbeCount, seat = headLeg.SeatCount;
                for (int p = 0; p < structuralPeels.Count; p++)
                    if (s.LegsById.TryGetValue(structuralPeels[p], out MissionLeg pl)
                        && PeelUT(s, structuralPeels[p], runStart) <= segStart)
                    { pod -= pl.PodCount; probe -= pl.ProbeCount; seat -= pl.SeatCount; }
                if (pod < 0) pod = 0;
                if (probe < 0) probe = 0;
                if (seat < 0) seat = 0;

                var survivingNames = new List<string>(headLeg.CrewNames);
                int leftCount = 0;
                for (int p = 0; p < crewPeels.Count; p++)
                {
                    if (!s.LegsById.TryGetValue(crewPeels[p], out MissionLeg cp)) continue;
                    if (cp.StartUT > segEnd) continue; // still aboard at this interval's end
                    leftCount++;
                    if (!string.IsNullOrEmpty(cp.EvaCrewName)) survivingNames.Remove(cp.EvaCrewName);
                }
                int crew = headLeg.CrewNames.Count > 0
                    ? survivingNames.Count
                    : System.Math.Max(0, headLeg.CrewCount - leftCount);

                var segLeg = new MissionLeg
                {
                    RecordingId = (i == 0)
                        ? headLegId
                        : headLegId + "/seg" + i.ToString(CultureInfo.InvariantCulture),
                    VesselName = headLeg.VesselName,
                    EvaCrewName = (i == 0) ? headLeg.EvaCrewName : null,
                    PodCount = pod,
                    ProbeCount = probe,
                    SeatCount = seat,
                    CrewCount = crew,
                    StartUT = segStart,
                    EndUT = segEnd,
                    TerminalStateValue = (i == segCount - 1) ? lastLeg.TerminalStateValue : null,
                };
                segLeg.CrewNames.AddRange(survivingNames);

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
                    StartEvent = (i == 0) ? startEvent : StructuralPeelEventAt(s, structuralPeels, segStart),
                    EndEvent = (i == segCount - 1)
                        ? TerminalName(lastLeg.TerminalStateValue)
                        : StructuralPeelEventAt(s, structuralPeels, segEnd),
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
                MissionCompositionNode child = BuildNode(s, structuralPeels[p], OriginEventName(pl), visited);
                if (child != null)
                    segNodes[segIdx].Children.Add(child);
            }

            // 7. Crew peels: attach the kerbal to the interval that is live when it left.
            for (int p = 0; p < crewPeels.Count; p++)
            {
                s.LegsById.TryGetValue(crewPeels[p], out MissionLeg cp);
                int segIdx = SegmentContaining(edges, cp != null ? cp.StartUT : runStart);
                MissionCompositionNode child = BuildNode(s, crewPeels[p], OriginEventName(cp), visited);
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
