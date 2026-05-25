using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    // The "vessel composition over time" read model for the Missions window. Each node is a
    // physical vessel during a span where its composition (controllers + crew) is STABLE,
    // labeled with counts like "pod x1, probe x1, crew x3". The tree branches at composition
    // CHANGE events: a controller separating or a kerbal going EVA (split), or a dock/board
    // (merge). A composition that is stable until it ends as a whole (Destroyed / Recovered /
    // an intact endpoint) is a terminal node; a single-atom terminal (one controller, or one
    // EVA kerbal) is a leaf, a multi-atom terminal can be expanded one level into its atoms.
    //
    // Derivation: each controlled MissionLeg already carries its own composition (pod/probe/
    // seat/crew counts, populated from Recording.Controllers + StartCrew). Consecutive
    // continuation legs with equal composition (env-splits, observation boundaries) collapse
    // into one interval; the composition only "changes" where a controller/kerbal peels off
    // or joins. Pure: no Unity calls, no shared mutable state.

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

        // Builds one composition node: a maximal run of equal-composition continuation legs,
        // then its children = the continuing vessel's next (changed) interval plus any pieces
        // that peeled off across the run. A merge child reached a second time is guarded by
        // the shared visited set (it terminates the line that reaches it second).
        private static MissionCompositionNode BuildNode(
            MissionStructure s, string headLegId, string startEvent, HashSet<string> visited)
        {
            if (headLegId == null || !s.LegsById.TryGetValue(headLegId, out MissionLeg headLeg))
                return null;
            if (visited.Contains(headLegId))
                return null;

            // 1. Walk the continuation chain, extending the run while composition is unchanged.
            var run = new List<MissionLeg>();
            string cur = headLegId;
            while (cur != null && s.LegsById.TryGetValue(cur, out MissionLeg leg) && visited.Add(cur))
            {
                run.Add(leg);
                string succ = MissionThroughLineBuilder.ContinuationSuccessor(s, leg);
                if (succ == null || !s.LegsById.TryGetValue(succ, out MissionLeg succLeg))
                    break;
                if (!SameComposition(leg, succLeg))
                    break; // composition changes at this boundary: close the run here
                cur = succ;
            }

            MissionLeg lastLeg = run[run.Count - 1];
            var node = new MissionCompositionNode
            {
                HeadLegId = headLegId,
                VesselName = VesselLabel(headLeg),
                CompositionLabel = FormatComposition(headLeg),
                StartUT = headLeg.StartUT,
                EndUT = lastLeg.EndUT,
                StartEvent = startEvent ?? OriginEventName(headLeg),
            };

            // 2. Continuing vessel after the run (changed composition), if any.
            string contSucc = MissionThroughLineBuilder.ContinuationSuccessor(s, lastLeg);
            bool hasContinuation = contSucc != null && s.LegsById.ContainsKey(contSucc)
                && !visited.Contains(contSucc);

            // 3. Pieces that peeled off across the run (non-continuation branch children).
            var peels = new List<string>();
            for (int r = 0; r < run.Count; r++)
            {
                MissionLeg leg = run[r];
                string legSucc = MissionThroughLineBuilder.ContinuationSuccessor(s, leg);
                for (int c = 0; c < leg.BranchChildIds.Count; c++)
                {
                    string childId = leg.BranchChildIds[c];
                    if (childId == legSucc) continue;        // the continuation, handled above
                    if (!s.LegsById.ContainsKey(childId)) continue;
                    if (!peels.Contains(childId)) peels.Add(childId);
                }
            }
            peels.Sort((a, b) => CompareLegStart(s, a, b));

            // 4. End event: a composition change (split/merge) vs a clean terminal.
            node.EndEvent = (hasContinuation || peels.Count > 0)
                ? EndEventName(lastLeg)
                : TerminalName(lastLeg.TerminalStateValue);

            // 5. Children: continuing vessel first, then peeled pieces (the user's nesting order).
            if (hasContinuation)
            {
                MissionCompositionNode child = BuildNode(s, contSucc, EndEventName(lastLeg), visited);
                if (child != null)
                    node.Children.Add(child);
            }
            for (int p = 0; p < peels.Count; p++)
            {
                s.LegsById.TryGetValue(peels[p], out MissionLeg peelLeg);
                MissionCompositionNode child = BuildNode(s, peels[p], OriginEventName(peelLeg), visited);
                if (child != null)
                    node.Children.Add(child);
            }

            // 6. Terminal node: a single atom is a leaf; a stable multi-atom terminal expands
            //    one optional level into its atoms (controllers by type + crew).
            if (!hasContinuation && peels.Count == 0)
            {
                if (IsSingleAtom(headLeg))
                    node.IsLeaf = true;
                else
                    AddAtomChildren(node, headLeg);
            }

            return node;
        }

        // --- Composition helpers (pure, individually testable) ---

        // Two legs share a composition iff their controller/crew counts match. Env-split and
        // observation-boundary continuations preserve composition; forks / EVA / dock change it.
        internal static bool SameComposition(MissionLeg a, MissionLeg b)
        {
            return a.PodCount == b.PodCount
                && a.ProbeCount == b.ProbeCount
                && a.SeatCount == b.SeatCount
                && a.CrewCount == b.CrewCount
                && string.IsNullOrEmpty(a.EvaCrewName) == string.IsNullOrEmpty(b.EvaCrewName);
        }

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
        // (by type) plus crew. Crew shows as a single "crew xN" atom (per-name roster is a
        // later enhancement once start-crew names are threaded through).
        private static void AddAtomChildren(MissionCompositionNode node, MissionLeg leg)
        {
            AddAtoms(node, "Pod", leg.PodCount);
            AddAtoms(node, "Probe", leg.ProbeCount);
            AddAtoms(node, "Seat", leg.SeatCount);
            if (leg.CrewCount > 0)
            {
                string crewLabel = "crew x" + leg.CrewCount.ToString(CultureInfo.InvariantCulture);
                node.Children.Add(new MissionCompositionNode
                {
                    HeadLegId = leg.RecordingId,
                    VesselName = crewLabel,
                    CompositionLabel = crewLabel,
                    IsLeaf = true,
                    IsAtom = true,
                });
            }
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
                ? BranchEventName(leg.OriginBranchPointType.Value)
                : (leg.IsRoot ? "Launch" : "");
        }

        internal static string EndEventName(MissionLeg leg)
        {
            if (leg == null) return "";
            return leg.EndBranchPointType.HasValue
                ? BranchEventName(leg.EndBranchPointType.Value)
                : TerminalName(leg.TerminalStateValue);
        }

        private static string BranchEventName(BranchPointType t)
        {
            switch (t)
            {
                case BranchPointType.Undock: return "Undock";
                case BranchPointType.EVA: return "EVA";
                case BranchPointType.Dock: return "Dock";
                case BranchPointType.Board: return "Board";
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
