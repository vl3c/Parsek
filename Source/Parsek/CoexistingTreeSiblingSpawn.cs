using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>Outcome of <see cref="CoexistingTreeSiblingSpawn.Classify"/>.</summary>
    internal enum TreeSiblingOverlapVerdict
    {
        /// <summary>The blocker is not the spawn of another recording in the spawning recording's committed tree.</summary>
        NotTreeSibling = 0,
        /// <summary>Same-tree sibling spawn that co-existed with the spawning recording at its recorded spot: not a collision.</summary>
        Exempt = 1,
        /// <summary>Same-tree sibling spawn whose recorded interval never overlapped the spawning recording's.</summary>
        NotCoexisting = 2,
        /// <summary>Same-tree sibling spawn that no longer sits where its recording left it (moved, nudged, or walked back).</summary>
        Displaced = 3,
    }

    /// <summary>
    /// Spawn-collision exemption for vessels one committed tree put side by side.
    ///
    /// <para>A tree records vessels that stood a few metres apart at the same time: the parts of
    /// a Breaking Ground cluster an EVA kerbal placed (each its own GroundPartPlaced member), the
    /// rover and the kerbal that end among them. Stock physics already resolved those positions
    /// when the tree was recorded, so when Parsek materializes the members one after another, an
    /// overlap between a member's spawn and a live vessel Parsek itself spawned from ANOTHER member
    /// of the SAME committed tree is not a collision. The spawn-collision box (default 2.5 m part
    /// cube plus 5 m padding per side, a 12.6 m cube) otherwise refuses every member after the
    /// first, and the trajectory walkback exhausts because a stationary member's whole trajectory
    /// sits inside the box.</para>
    ///
    /// <para>The exemption is narrow: the blocker must be positively identified as a sibling's
    /// SPAWN through <see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/> (launch Guid
    /// gated, so a relaunch of the same craft that reuses the baked pid never matches), the two
    /// recordings' recorded existence must overlap in time, and the blocker must still stand
    /// within <see cref="PlacementToleranceMeters"/> (horizontal) of the spot its recording
    /// spawned it at. The player's own craft, another tree's spawn, and a sibling the player has
    /// since driven elsewhere all keep blocking.</para>
    /// </summary>
    internal static class CoexistingTreeSiblingSpawn
    {
        private const string Tag = "SpawnCollision";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>
        /// Horizontal distance a sibling spawn may stand from its recorded spawn spot and still
        /// count as "where the tree put it". Horizontal only: the landed spawn path lifts a vessel
        /// recorded below the PQS floor by a few metres (observed +2.4 m on a placed RTG), which is
        /// not a move. A vessel's latitude/longitude also track its centre of mass, which differs
        /// from the root placement by up to about a metre on a rover.
        /// </summary>
        internal const double PlacementToleranceMeters = 3.0;

        /// <summary>Grep-stable literal of the Info line logged when an overlap is exempted.</summary>
        internal const string ExemptLogLiteral = "Overlap exempt: co-existing tree sibling";

        /// <summary>Grep-stable literal of the Info line logged when a same-tree sibling still blocks.</summary>
        internal const string NotExemptLogLiteral = "Overlap not exempt: tree sibling";

        /// <summary>
        /// True when a recording's vessel stays in the world after its recorded end: a leaf whose
        /// terminal state leaves the vessel intact (landed, splashed, orbiting, suborbital). Tail
        /// trimming shortens such a recording without ending the vessel.
        /// </summary>
        internal static bool PersistsPastEnd(Recording rec)
        {
            if (rec == null || !string.IsNullOrEmpty(rec.ChildBranchPointId)
                || !rec.TerminalStateValue.HasValue)
                return false;
            switch (rec.TerminalStateValue.Value)
            {
                case TerminalState.Landed:
                case TerminalState.Splashed:
                case TerminalState.Orbiting:
                case TerminalState.SubOrbital:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when the two recordings' vessels existed at the same time in the recording:
        /// [start, end] intervals overlap, with a persisting terminal extending its end to infinity.
        /// </summary>
        internal static bool RecordedIntervalsCoexist(Recording a, Recording b)
        {
            if (a == null || b == null) return false;
            double aEnd = PersistsPastEnd(a) ? double.PositiveInfinity : a.EndUT;
            double bEnd = PersistsPastEnd(b) ? double.PositiveInfinity : b.EndUT;
            return Math.Max(a.StartUT, b.StartUT) <= Math.Min(aEnd, bEnd);
        }

        /// <summary>
        /// Returns the recording among <paramref name="treeRecordings"/> (other than
        /// <paramref name="spawning"/>) whose spawned vessel IS the candidate
        /// (<see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/>), or null.
        /// </summary>
        internal static Recording FindSiblingSpawnSource(
            IEnumerable<Recording> treeRecordings, Recording spawning, uint candidatePid, string candidateGuid)
        {
            if (treeRecordings == null || spawning == null || candidatePid == 0) return null;
            foreach (Recording r in treeRecordings)
            {
                if (r == null || ReferenceEquals(r, spawning)) continue;
                if (string.Equals(r.RecordingId, spawning.RecordingId, StringComparison.Ordinal)) continue;
                if (VesselLaunchIdentity.LiveVesselIsRecordedSpawn(r, candidatePid, candidateGuid))
                    return r;
            }
            return null;
        }

        /// <summary>
        /// Pure verdict for an overlap between <paramref name="spawning"/>'s spawn and the live
        /// spawn of <paramref name="sibling"/>, which stands
        /// <paramref name="siblingDisplacementMeters"/> (horizontal) from its recorded spawn spot.
        /// </summary>
        internal static TreeSiblingOverlapVerdict Classify(
            Recording spawning, Recording sibling, double siblingDisplacementMeters, double toleranceMeters)
        {
            if (spawning == null || sibling == null) return TreeSiblingOverlapVerdict.NotTreeSibling;
            if (string.IsNullOrEmpty(spawning.TreeId)
                || !string.Equals(spawning.TreeId, sibling.TreeId, StringComparison.Ordinal))
                return TreeSiblingOverlapVerdict.NotTreeSibling;
            if (ReferenceEquals(spawning, sibling)
                || string.Equals(spawning.RecordingId, sibling.RecordingId, StringComparison.Ordinal))
                return TreeSiblingOverlapVerdict.NotTreeSibling;
            if (!RecordedIntervalsCoexist(spawning, sibling))
                return TreeSiblingOverlapVerdict.NotCoexisting;
            if (double.IsNaN(siblingDisplacementMeters) || siblingDisplacementMeters > toleranceMeters)
                return TreeSiblingOverlapVerdict.Displaced;
            return TreeSiblingOverlapVerdict.Exempt;
        }

        /// <summary>
        /// Horizontal distance from a candidate's live (lat, lon) to the spot
        /// <paramref name="sibling"/>'s spawn uses (<see cref="VesselSpawner.SelectSpawnCoordinates(Recording, TrajectoryPoint?, out string, out double, out double, out double)"/>).
        /// PositiveInfinity when the sibling has no spawn coordinates or they are on another body.
        /// </summary>
        internal static double SiblingDisplacementMeters(
            Recording sibling, string candidateBodyName, double candidateLat, double candidateLon, double bodyRadius)
        {
            if (sibling == null) return double.PositiveInfinity;
            TrajectoryPoint? lastPt = sibling.Points != null && sibling.Points.Count > 0
                ? (TrajectoryPoint?)sibling.Points[sibling.Points.Count - 1]
                : null;
            VesselSpawner.SpawnCoordinateSource source = VesselSpawner.SelectSpawnCoordinates(
                sibling, lastPt, out string bodyName, out double lat, out double lon, out _);
            if (source == VesselSpawner.SpawnCoordinateSource.None) return double.PositiveInfinity;
            if (string.IsNullOrEmpty(candidateBodyName)
                || !string.Equals(bodyName, candidateBodyName, StringComparison.Ordinal))
                return double.PositiveInfinity;
            return SpawnCollisionDetector.SurfaceDistance(lat, lon, candidateLat, candidateLon, bodyRadius);
        }

        /// <summary>
        /// Pure core of the exemption: finds the sibling in <paramref name="treeRecordings"/>,
        /// measures its displacement, classifies. <paramref name="sibling"/> is null when the
        /// candidate is not a sibling spawn at all.
        /// </summary>
        internal static TreeSiblingOverlapVerdict Evaluate(
            IEnumerable<Recording> treeRecordings, Recording spawning,
            uint candidatePid, string candidateGuid,
            string candidateBodyName, double candidateLat, double candidateLon, double bodyRadius,
            out Recording sibling, out double displacementMeters)
        {
            displacementMeters = double.PositiveInfinity;
            sibling = FindSiblingSpawnSource(treeRecordings, spawning, candidatePid, candidateGuid);
            if (sibling == null) return TreeSiblingOverlapVerdict.NotTreeSibling;
            displacementMeters = SiblingDisplacementMeters(
                sibling, candidateBodyName, candidateLat, candidateLon, bodyRadius);
            return Classify(spawning, sibling, displacementMeters, PlacementToleranceMeters);
        }

        /// <summary>
        /// Live wrapper: resolves <paramref name="spawning"/>'s committed tree and decides whether
        /// the candidate vessel is an exempt co-existing sibling spawn. Logs one Info line per
        /// (spawning recording, candidate) pair and verdict, rate-limited so walkback sub-steps and
        /// per-frame blocked rechecks do not spam; logs nothing for a candidate that is not a
        /// sibling spawn.
        /// </summary>
        internal static bool IsExemptBlocker(
            Recording spawning, uint candidatePid, string candidateGuid, string candidateName,
            string candidateBodyName, double candidateLat, double candidateLon, double bodyRadius,
            string site)
        {
            if (spawning == null || string.IsNullOrEmpty(spawning.TreeId) || candidatePid == 0)
                return false;

            RecordingTree tree = FindCommittedTree(spawning.TreeId);
            if (tree == null || tree.Recordings == null) return false;

            TreeSiblingOverlapVerdict verdict = Evaluate(
                tree.Recordings.Values, spawning, candidatePid, candidateGuid,
                candidateBodyName, candidateLat, candidateLon, bodyRadius,
                out Recording sibling, out double displacement);
            if (verdict == TreeSiblingOverlapVerdict.NotTreeSibling)
                return false;

            bool exempt = verdict == TreeSiblingOverlapVerdict.Exempt;
            string displacementText = double.IsInfinity(displacement)
                ? "unknown"
                : displacement.ToString("F2", IC) + "m";
            string message = string.Format(IC,
                "{0} spawn: site={1} tree={2} spawning='{3}' rec={4} blocker='{5}' pid={6} siblingRec={7} " +
                "verdict={8} displacement={9} tolerance={10}m",
                exempt ? ExemptLogLiteral : NotExemptLogLiteral,
                site ?? "?",
                spawning.TreeId,
                Recording.ResolveLocalizedName(spawning.VesselName),
                spawning.RecordingId,
                candidateName ?? "?",
                candidatePid,
                sibling != null ? sibling.RecordingId : "?",
                verdict,
                displacementText,
                PlacementToleranceMeters.ToString("F1", IC));
            ParsekLog.InfoRateLimited(Tag,
                "tree-sibling-" + verdict + "-" + spawning.RecordingId + "-" + candidatePid.ToString(IC),
                message);
            return exempt;
        }

        private static RecordingTree FindCommittedTree(string treeId)
        {
            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            if (trees == null) return null;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] != null && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                    return trees[i];
            }
            return null;
        }
    }
}
