using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// One dock (or its undock) another mission recorded onto a vessel of this mission
    /// (docs/dev/design-mission-profile.md section 3.2, "Docked by another mission"). It lives
    /// in the other tree, so the line builder reads it from the source the Missions tab's
    /// Docked partner rows read (<see cref="MissionCrossTreeDock.FindLinks"/>). Named, never
    /// expanded: the other mission has its own Profile.
    /// </summary>
    internal sealed class ProfileOtherMissionDock
    {
        public bool IsUndock;
        // Dock or Board: what the other mission's vessel did to this one.
        public BranchPointType ClaimType;
        public double UT;
        // The claiming branch point in the other tree (the tab's link id).
        public string LinkId;
        public string ForeignTreeId;
        // The other mission's vessel that docked (or boarded), and that mission's name.
        public string PartnerName;
        public string MissionName;
        // After the line's end: this mission's recording had stopped, so it was not recorded here.
        public bool AfterLineEnd;
    }

    /// <summary>Counts of one Docked by another mission pass, for its log line.</summary>
    internal struct MissionProfileForeignDockTally
    {
        public int Links;
        public int Docks;
        public int Undocks;
        public int AfterLineEnd;
        // A link whose claimed recording is on no drawn line (a partner row, a hidden line).
        public int Unmatched;
        // An instant inside a span the mission's include set excludes (dropped like any event there).
        public int Excluded;
    }

    internal static partial class MissionProfileLineBuilder
    {
        // A dock this far past the line's last sample still reads as at the end, not after it.
        private const double LineEndEpsilon = 1e-3;

        /// <summary>
        /// Places every dock another mission recorded onto a vessel of <paramref name="lines"/>
        /// (and the undock that ended it, when the other tree recorded one) on the drawn line
        /// whose pieces hold the claimed recording. Runs after the plans exist, so an instant
        /// past the line's end is flagged. <c>req.CommittedTrees</c> null: nothing. Pure.
        /// </summary>
        internal static MissionProfileForeignDockTally AddOtherMissionDocks(
            MissionProfileRequest req, List<MissionProfileLine> lines)
        {
            var tally = default(MissionProfileForeignDockTally);
            if (req?.Tree == null || req.CommittedTrees == null || lines == null) return tally;
            List<ForeignDockLink> links;
            bool prev = MissionCrossTreeDock.SuppressLogging;
            MissionCrossTreeDock.SuppressLogging = true;
            try
            {
                links = MissionCrossTreeDock.FindLinks(req.Tree, req.CommittedTrees);
            }
            finally
            {
                MissionCrossTreeDock.SuppressLogging = prev;
            }
            if (links.Count == 0) return tally;

            var owner = new Dictionary<string, MissionProfileLine>(StringComparer.Ordinal);
            foreach (MissionProfileLine l in All(lines))
                if (l.IsDrawn && l.Plan != null)
                    foreach (Recording r in l.Pieces) owner[r.RecordingId] = l;

            foreach (ForeignDockLink link in links)
            {
                tally.Links++;
                if (link.ClaimedRecordingId == null
                    || !owner.TryGetValue(link.ClaimedRecordingId, out MissionProfileLine line))
                {
                    tally.Unmatched++;
                    continue;
                }
                RecordingTree foreign = FindTree(req.CommittedTrees, link.ForeignTreeId);
                string partner = DockingVesselName(foreign, link);
                string mission = req.TreeMissionName?.Invoke(link.ForeignTreeId);
                if (string.IsNullOrEmpty(mission)) mission = foreign?.TreeName;
                AddOtherMissionDock(line, link, false, link.DockUT, partner, mission, ref tally);
                if (link.ClaimType != BranchPointType.Dock) continue;
                double undock = OtherMissionUndockUT(foreign, link);
                if (!double.IsNaN(undock))
                    AddOtherMissionDock(line, link, true, undock, partner, mission, ref tally);
            }
            foreach (MissionProfileLine l in All(lines))
                if (l.OtherMissionDocks.Count > 1)
                    l.OtherMissionDocks.Sort((a, b) => a.UT.CompareTo(b.UT));
            return tally;
        }

        private static void AddOtherMissionDock(MissionProfileLine line, ForeignDockLink link, bool undock,
            double ut, string partner, string mission, ref MissionProfileForeignDockTally tally)
        {
            foreach (ProfileSpan span in line.ExcludedSpans)
            {
                if (ut >= span.StartUT && ut <= span.EndUT)
                {
                    tally.Excluded++;
                    return;
                }
            }
            bool after = line.Plan != null && !double.IsNaN(line.Plan.EndUT) && ut > line.Plan.EndUT + LineEndEpsilon;
            line.OtherMissionDocks.Add(new ProfileOtherMissionDock
            {
                IsUndock = undock,
                ClaimType = link.ClaimType,
                UT = ut,
                LinkId = link.LinkId,
                ForeignTreeId = link.ForeignTreeId,
                PartnerName = partner,
                MissionName = mission,
                AfterLineEnd = after,
            });
            if (undock) tally.Undocks++;
            else tally.Docks++;
            if (after) tally.AfterLineEnd++;
        }

        // The other mission's vessel that came to this one: the claiming branch point's own
        // parent in the other tree (a cross-tree dock records one); the merged stack's name
        // (the tab's label) when that parent is missing.
        internal static string DockingVesselName(RecordingTree foreign, ForeignDockLink link)
        {
            BranchPoint bp = foreign?.BranchPoints != null && foreign.Recordings != null
                ? FindBranchPoint(foreign, link?.LinkId) : null;
            if (bp?.ParentRecordingIds != null)
                foreach (string id in bp.ParentRecordingIds)
                    if (id != null && foreign.Recordings.TryGetValue(id, out Recording r) && r != null
                        && !string.IsNullOrEmpty(r.VesselName))
                        return r.VesselName;
            return link?.ForeignVesselName;
        }

        // The undock that ends the dock: along the partner journey the other tree recorded
        // (MissionCrossTreeDock.ComputePartnerJourneyLegIds, the tab's journey walk), the first
        // recording after the merged stack that carries this vessel's pid and starts at an
        // Undock branch point. NaN when the stack never undocked there.
        internal static double OtherMissionUndockUT(RecordingTree foreign, ForeignDockLink link)
        {
            if (foreign?.Recordings == null || foreign.BranchPoints == null || link == null) return double.NaN;
            List<string> journey;
            bool prev = MissionCrossTreeDock.SuppressLogging;
            MissionCrossTreeDock.SuppressLogging = true;
            try
            {
                journey = MissionCrossTreeDock.ComputePartnerJourneyLegIds(foreign, link);
            }
            finally
            {
                MissionCrossTreeDock.SuppressLogging = prev;
            }
            for (int i = 1; i < journey.Count; i++)
            {
                if (!foreign.Recordings.TryGetValue(journey[i], out Recording r) || r == null
                    || r.VesselPersistentId != link.PartnerPid)
                    continue;
                BranchPoint bp = FindBranchPoint(foreign, r.ParentBranchPointId);
                if (bp != null && bp.Type == BranchPointType.Undock) return bp.UT;
            }
            return double.NaN;
        }

        private static RecordingTree FindTree(IReadOnlyList<RecordingTree> trees, string id)
        {
            if (trees == null || string.IsNullOrEmpty(id)) return null;
            foreach (RecordingTree t in trees)
                if (t != null && string.Equals(t.Id, id, StringComparison.Ordinal)) return t;
            return null;
        }
    }
}
