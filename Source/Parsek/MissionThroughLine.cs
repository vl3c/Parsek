using System.Collections.Generic;

namespace Parsek
{
    // Phase 2b: the mission-abstraction collapse. The Missions window does not show
    // individual recording legs (those are the Recordings window's concern); it shows
    // continuous-vessel THROUGH-LINES. A through-line merges all the legs of one
    // continuous controlled vessel (env-split continuations, and the vessel-continuation
    // child at each branch point where something else split off) into a single entry.
    // The things that LEFT the vessel (EVA kerbals, decoupled/broken-off offshoots, or
    // a different vessel a fork leads to) hang off it as child through-lines.
    // See docs/dev/design-mission-abstractions.md ("main line").

    internal sealed class MissionThroughLine
    {
        public string HeadLegId;                 // first leg (start of the vessel's line)
        public string TailLegId;                 // last leg (end of the vessel's line)
        public string VesselName;
        public double StartUT;                   // head leg start
        public double EndUT;                     // tail leg end
        public readonly List<string> MemberLegIds = new List<string>();
        public readonly List<string> OffshootHeadIds = new List<string>(); // child through-lines, chronological
    }

    internal sealed class MissionThroughLineView
    {
        public string TreeId;
        public readonly Dictionary<string, MissionThroughLine> ByHeadId =
            new Dictionary<string, MissionThroughLine>();
        public readonly List<string> RootHeadIds = new List<string>();
    }

    internal static class MissionThroughLineBuilder
    {
        /// <summary>
        /// Collapses a leg-level MissionStructure into vessel through-lines. Pure.
        /// </summary>
        internal static MissionThroughLineView Build(MissionStructure s)
        {
            var view = new MissionThroughLineView { TreeId = s?.TreeId };
            if (s == null || s.LegsById.Count == 0)
                return view;

            // 1. Continuation successor per leg: the env-split next, else the single
            //    vessel-continuation branch child (not crew, not an anchored offshoot).
            var successor = new Dictionary<string, string>();
            var continuedInto = new HashSet<string>();
            foreach (var leg in s.LegsById.Values)
            {
                string next = ContinuationSuccessor(s, leg);
                if (next != null)
                {
                    successor[leg.RecordingId] = next;
                    continuedInto.Add(next);
                }
            }

            // 2. Heads = legs nobody continues into. Walk each head's continuation chain.
            foreach (var leg in s.LegsById.Values)
            {
                if (continuedInto.Contains(leg.RecordingId))
                    continue;

                var tl = new MissionThroughLine
                {
                    HeadLegId = leg.RecordingId,
                    VesselName = leg.VesselName,
                    StartUT = leg.StartUT
                };
                string cur = leg.RecordingId;
                var walked = new HashSet<string>();
                MissionLeg tail = leg;
                while (cur != null && s.LegsById.TryGetValue(cur, out MissionLeg curLeg) && walked.Add(cur))
                {
                    tl.MemberLegIds.Add(cur);
                    tail = curLeg;
                    successor.TryGetValue(cur, out cur);
                }
                tl.TailLegId = tail.RecordingId;
                tl.EndUT = tail.EndUT;
                view.ByHeadId[tl.HeadLegId] = tl;
            }

            // 3. Offshoots per through-line: branch children of any member that are NOT
            //    that member's continuation successor (the vessel continuation is already
            //    merged in). Each offshoot is itself a head.
            var offshootHeads = new HashSet<string>();
            foreach (var tl in view.ByHeadId.Values)
            {
                for (int m = 0; m < tl.MemberLegIds.Count; m++)
                {
                    string memberId = tl.MemberLegIds[m];
                    if (!s.LegsById.TryGetValue(memberId, out MissionLeg member))
                        continue;
                    successor.TryGetValue(memberId, out string contSucc);
                    for (int i = 0; i < member.BranchChildIds.Count; i++)
                    {
                        string childId = member.BranchChildIds[i];
                        if (childId == contSucc)
                            continue;
                        if (!view.ByHeadId.ContainsKey(childId))
                            continue;
                        if (!tl.OffshootHeadIds.Contains(childId))
                        {
                            tl.OffshootHeadIds.Add(childId);
                            offshootHeads.Add(childId);
                        }
                    }
                }
                tl.OffshootHeadIds.Sort((a, b) => CompareHead(view, a, b));
            }

            // 4. Roots = heads that are not an offshoot of any through-line.
            foreach (var headId in view.ByHeadId.Keys)
            {
                if (!offshootHeads.Contains(headId))
                    view.RootHeadIds.Add(headId);
            }
            view.RootHeadIds.Sort((a, b) => CompareHead(view, a, b));

            return view;
        }

        internal static string ContinuationSuccessor(MissionStructure s, MissionLeg leg)
        {
            if (leg.SequenceNextId != null)
                return leg.SequenceNextId;
            // Among the controlled (non-anchored, non-EVA) branch children, prefer the one the
            // recorder marked as the continuing vessel (IsBranchContinuation). This makes the
            // continuation deterministic at a split where several children survive - notably an
            // Undock fork, whose two non-anchored, non-EVA children share the branch UT, so the
            // old "first by StartUT/RecordingId" pick was an arbitrary GUID tiebreak. Falls back
            // to the first controlled child by the structure's deterministic sort when none is
            // marked (legacy data, or a producer that does not designate a continuation).
            string firstControlled = null;
            for (int i = 0; i < leg.BranchChildIds.Count; i++)
            {
                if (s.LegsById.TryGetValue(leg.BranchChildIds[i], out MissionLeg child)
                    && !child.IsAnchoredOffshoot
                    && string.IsNullOrEmpty(child.EvaCrewName))
                {
                    if (child.IsBranchContinuation)
                        return child.RecordingId;
                    if (firstControlled == null)
                        firstControlled = child.RecordingId;
                }
            }
            return firstControlled;
        }

        private static int CompareHead(MissionThroughLineView v, string a, string b)
        {
            double sa = v.ByHeadId.TryGetValue(a, out MissionThroughLine ta) ? ta.StartUT : 0.0;
            double sb = v.ByHeadId.TryGetValue(b, out MissionThroughLine tb) ? tb.StartUT : 0.0;
            int cmp = sa.CompareTo(sb);
            if (cmp != 0)
                return cmp;
            return string.CompareOrdinal(a, b);
        }
    }
}
