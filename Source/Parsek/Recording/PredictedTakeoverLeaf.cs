using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Owner ruling Q1b: which recording of a tree a takeover of a live vessel continues from.
    /// Kept apart from <see cref="PredictedEndingTakeover"/>, which stays tree-free so the tree
    /// commit can call it without pulling it into the type knot (the knot first-cut canary).
    /// </summary>
    internal static class PredictedTakeoverLeaf
    {
        /// <summary>
        /// Pure: the leaf of <paramref name="tree"/> a takeover of the live vessel
        /// (<paramref name="livePid"/>, launch <paramref name="liveGuid"/>, "N" form or null)
        /// continues from: the one PID-coherent terminal leaf of that launch whose ending is a
        /// prediction (<see cref="PredictedEndingTakeover.HasPredictedEnding"/>). Null when there
        /// is none or more than one, so the caller keeps its old route. Read-only.
        /// </summary>
        internal static string ResolveInTree(RecordingTree tree, uint livePid, string liveGuid)
        {
            if (tree?.Recordings == null || livePid == 0)
                return null;
            string found = null;
            foreach (KeyValuePair<string, Recording> kvp in tree.Recordings)
            {
                Recording rec = kvp.Value;
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, livePid, liveGuid))
                    continue;
                if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                    && !RecordingTree.IsVesselLineEnd(rec, tree.Recordings, tree.BranchPoints))
                {
                    continue;
                }
                if (!PredictedEndingTakeover.HasPredictedEnding(rec))
                    continue;
                if (found != null)
                {
                    ParsekLog.Info("PredictedTakeover",
                        string.Format(CultureInfo.InvariantCulture,
                            "Takeover leaf ambiguous: tree={0} pid={1} candidates={2},{3}",
                            tree.Id ?? "(null)", livePid, found, rec.RecordingId));
                    return null;
                }
                found = rec.RecordingId;
            }
            return found;
        }
    }
}
