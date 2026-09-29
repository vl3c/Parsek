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
        /// <summary>
        /// Walkback candidate: at the candidate's recorded UT the sibling was not yet standing at
        /// its spot (not placed yet, still driving there) or no longer existed.
        /// </summary>
        NotAtSpotAtCandidateUT = 4,
        /// <summary>
        /// Other-tree blocker whose committed tree was not committed before the spawning
        /// recording's tree: it was not there when the spawning recording was made.
        /// </summary>
        NotCommittedBefore = 5,
        /// <summary>
        /// Other-tree blocker whose recorded history had not ended standing at its spot by the
        /// spawning member's reference UT (its end, or the walkback candidate's UT): it arrived
        /// later, or it was still a replay (a ghost) then, or its history does not end standing.
        /// </summary>
        NotStandingByThen = 6,
        /// <summary>
        /// Other-tree blocker, but the spawning member is neither an EVA kerbal nor a placed
        /// ground part: the other-tree exemption does not apply, the de-overlap still pushes.
        /// </summary>
        SpawningNotKerbalOrPlacedPart = 7,
    }

    /// <summary>How a live blocker was identified as the vessel of a committed recording.</summary>
    internal enum OverlapBlockerKind
    {
        None = 0,
        /// <summary>
        /// The vessel the recording's spawn stamp names: a Parsek spawn, or the recorded vessel
        /// itself adopted as the spawn.
        /// </summary>
        Spawn = 1,
        /// <summary>The recording's own recorded vessel, still live, with no spawn stamp.</summary>
        Original = 2,
    }

    /// <summary>Which rule exempted a blocker, if any.</summary>
    internal enum OverlapExemptionScope
    {
        None = 0,
        SameTree = 1,
        OtherTree = 2,
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
    /// vessel - its SPAWN through <see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/>
    /// (launch Guid gated, so a relaunch of the same craft that reuses the baked pid never
    /// matches), or the sibling's own recorded vessel still live (a positive pid + launch Guid
    /// match) - never a segment of the spawning member's own launch, and it must still stand within
    /// <see cref="PlacementToleranceMeters"/> (horizontal) of the spot that vessel's latest
    /// recorded segment ends at. The player's own unrecorded craft and a sibling the player has
    /// since driven elsewhere keep blocking.</para>
    ///
    /// <para>A vessel of ANOTHER committed tree (the capsule an EVA kerbal left, recorded in an
    /// earlier flight) is exempted only for an EVA kerbal or a placed ground part and only on
    /// evidence from its own committed history that it was already standing there when the
    /// spawning member was recorded and has not moved since (<see cref="ClassifyOtherTree"/>;
    /// operator ruling 2026-09-29). A vessel that arrived later still pushes.</para>
    ///
    /// <para>The time gate differs by site. At an end-of-recording spawn (no candidate UT) it asks
    /// whether the two recordings' existence overlapped; spawnable leaves persist past their end,
    /// so for two spawnable leaves that always holds and the gate is defensive only. At a walkback
    /// candidate (a point earlier on the spawning vessel's own trajectory, with its recorded UT) it
    /// asks whether the sibling was already standing at its spot AT THAT UT: a rover that drove
    /// through a spot where a part was placed later must not be walked back into that part.</para>
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
        /// Grep-stable literal of the Info line logged when a vessel of an EARLIER committed tree,
        /// already standing there when the spawning member was recorded, is exempted.
        /// </summary>
        internal const string OtherTreeExemptLogLiteral = "Overlap exempt: other-tree vessel standing there first";

        /// <summary>Grep-stable literal of the Info line logged when an other-tree vessel still blocks.</summary>
        internal const string OtherTreeNotExemptLogLiteral = "Overlap not exempt: other-tree vessel";

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
        /// True when <paramref name="sibling"/> stood at its spawn spot at <paramref name="ut"/>:
        /// it had arrived there (<paramref name="siblingArrivalUT"/>, see
        /// <see cref="SiblingArrivalAtSpotUT"/>) and had not yet ended, unless its terminal
        /// persists past its recorded end.
        /// </summary>
        internal static bool SiblingAtSpotAtUT(Recording sibling, double ut, double siblingArrivalUT)
        {
            if (sibling == null || double.IsNaN(ut) || double.IsNaN(siblingArrivalUT)) return false;
            if (ut < siblingArrivalUT) return false;
            return PersistsPastEnd(sibling) || ut <= sibling.EndUT;
        }

        /// <summary>
        /// Earliest recorded UT from which <paramref name="sibling"/> stayed within
        /// <paramref name="toleranceMeters"/> (horizontal) of (<paramref name="spotLat"/>,
        /// <paramref name="spotLon"/>) on <paramref name="spotBody"/> to the end of its trajectory:
        /// the placement UT for a placed part, the parking UT for a vehicle. Walks the flat points
        /// back from the last one and stops at the first point that is elsewhere, on another body,
        /// or inside a non-Absolute track section (Relative points carry anchor-local metres, not
        /// lat/lon). PositiveInfinity when even the last point is not at the spot.
        /// </summary>
        internal static double SiblingArrivalAtSpotUT(
            Recording sibling, string spotBody, double spotLat, double spotLon,
            double bodyRadius, double toleranceMeters)
        {
            if (sibling == null || sibling.Points == null || sibling.Points.Count == 0
                || string.IsNullOrEmpty(spotBody))
                return double.PositiveInfinity;
            double arrival = double.PositiveInfinity;
            for (int i = sibling.Points.Count - 1; i >= 0; i--)
            {
                TrajectoryPoint pt = sibling.Points[i];
                if (!string.Equals(pt.bodyName, spotBody, StringComparison.Ordinal)) break;
                int sectionIdx = TrajectoryMath.FindTrackSectionForUT(sibling.TrackSections, pt.ut);
                if (sectionIdx >= 0
                    && sibling.TrackSections[sectionIdx].referenceFrame != ReferenceFrame.Absolute)
                    break;
                double d = SpawnCollisionDetector.SurfaceDistance(
                    pt.latitude, pt.longitude, spotLat, spotLon, bodyRadius);
                if (d > toleranceMeters) break;
                arrival = pt.ut;
            }
            return arrival;
        }

        /// <summary>
        /// Pure verdict for an overlap between <paramref name="spawning"/>'s spawn and the live
        /// spawn of <paramref name="sibling"/>, which stands
        /// <paramref name="siblingDisplacementMeters"/> (horizontal) from its recorded spawn spot.
        /// <paramref name="candidateUT"/> is NaN at the end-of-recording spawn position (the time
        /// gate is recorded-interval overlap) and the recorded UT of the candidate during a
        /// walkback (the time gate is "the sibling stood at its spot at that UT", from
        /// <paramref name="siblingArrivalUT"/>).
        /// </summary>
        internal static TreeSiblingOverlapVerdict Classify(
            Recording spawning, Recording sibling, double siblingDisplacementMeters, double toleranceMeters,
            double candidateUT = double.NaN, double siblingArrivalUT = double.NaN)
        {
            if (spawning == null || sibling == null) return TreeSiblingOverlapVerdict.NotTreeSibling;
            if (string.IsNullOrEmpty(spawning.TreeId)
                || !string.Equals(spawning.TreeId, sibling.TreeId, StringComparison.Ordinal))
                return TreeSiblingOverlapVerdict.NotTreeSibling;
            if (ReferenceEquals(spawning, sibling)
                || string.Equals(spawning.RecordingId, sibling.RecordingId, StringComparison.Ordinal))
                return TreeSiblingOverlapVerdict.NotTreeSibling;
            if (double.IsNaN(candidateUT))
            {
                if (!RecordedIntervalsCoexist(spawning, sibling))
                    return TreeSiblingOverlapVerdict.NotCoexisting;
            }
            else if (!SiblingAtSpotAtUT(sibling, candidateUT, siblingArrivalUT))
            {
                return TreeSiblingOverlapVerdict.NotAtSpotAtCandidateUT;
            }
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
            if (!TryGetSiblingSpawnSpot(sibling, out string bodyName, out double lat, out double lon))
                return double.PositiveInfinity;
            if (string.IsNullOrEmpty(candidateBodyName)
                || !string.Equals(bodyName, candidateBodyName, StringComparison.Ordinal))
                return double.PositiveInfinity;
            return SpawnCollisionDetector.SurfaceDistance(lat, lon, candidateLat, candidateLon, bodyRadius);
        }

        private static bool TryGetSiblingSpawnSpot(
            Recording sibling, out string bodyName, out double lat, out double lon)
        {
            bodyName = null; lat = 0; lon = 0;
            if (sibling == null) return false;
            TrajectoryPoint? lastPt = sibling.Points != null && sibling.Points.Count > 0
                ? (TrajectoryPoint?)sibling.Points[sibling.Points.Count - 1]
                : null;
            VesselSpawner.SpawnCoordinateSource source = VesselSpawner.SelectSpawnCoordinates(
                sibling, lastPt, out bodyName, out lat, out lon, out _);
            return source != VesselSpawner.SpawnCoordinateSource.None;
        }

        /// <summary>
        /// Pure core of the same-tree exemption (kept for its callers and tests): the blocker is
        /// looked up among <paramref name="treeRecordings"/> only. See <see cref="EvaluateBlocker"/>.
        /// </summary>
        internal static TreeSiblingOverlapVerdict Evaluate(
            IEnumerable<Recording> treeRecordings, Recording spawning,
            uint candidatePid, string candidateGuid,
            string candidateBodyName, double candidateLat, double candidateLon, double bodyRadius,
            out Recording sibling, out double displacementMeters, double candidateUT = double.NaN)
        {
            return EvaluateBlocker(
                treeRecordings, null, null, spawning, false,
                candidatePid, candidateGuid, candidateBodyName, candidateLat, candidateLon, bodyRadius,
                out sibling, out _, out displacementMeters, candidateUT);
        }

        /// <summary>
        /// True only when both recordings carry the same vessel pid AND both launch Guids are
        /// known and equal: the chain segments of one physical launch. Never degrades to a bare
        /// pid match (the pid is craft-baked, so two launches of one craft share it).
        /// </summary>
        internal static bool PositivelySameLaunch(Recording a, Recording b)
        {
            if (a == null || b == null) return false;
            if (a.VesselPersistentId == 0 || a.VesselPersistentId != b.VesselPersistentId) return false;
            string ga = VesselLaunchIdentity.NormalizeGuid(a.RecordedVesselGuid);
            string gb = VesselLaunchIdentity.NormalizeGuid(b.RecordedVesselGuid);
            return ga != null && gb != null && string.Equals(ga, gb, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the live candidate is the vessel <paramref name="rec"/>'s spawn stamp names
        /// (<see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/>: a Parsek spawn, or the
        /// recorded vessel itself adopted as the spawn). <paramref name="strict"/> (other trees)
        /// accepts an adoption stamp only on a POSITIVE launch match (pid and both Guids known and
        /// equal); the same tree keeps the predicate's behaviour (a Guid unknown on one side is not
        /// conclusive).
        /// </summary>
        internal static bool MatchesSpawnStamp(
            Recording rec, uint candidatePid, string candidateGuid, bool strict)
        {
            if (rec == null || candidatePid == 0) return false;
            if (!VesselLaunchIdentity.LiveVesselIsRecordedSpawn(rec, candidatePid, candidateGuid)) return false;
            bool adoptionStamp = rec.VesselPersistentId != 0
                && rec.SpawnedVesselPersistentId == rec.VesselPersistentId;
            return !strict || !adoptionStamp
                || VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(rec, candidatePid, candidateGuid);
        }

        private static bool IsSpawningOwnLaunch(Recording spawning, Recording r)
        {
            if (ReferenceEquals(r, spawning)) return true;
            if (string.Equals(r.RecordingId, spawning.RecordingId, StringComparison.Ordinal)) return true;
            return PositivelySameLaunch(r, spawning);
        }

        private static bool IsLaterSegment(Recording candidate, Recording current)
        {
            if (candidate.EndUT > current.EndUT) return true;
            return candidate.EndUT == current.EndUT && PersistsPastEnd(candidate) && !PersistsPastEnd(current);
        }

        /// <summary>
        /// The UT by which an other-tree blocker must already have been standing: the walkback
        /// candidate's recorded UT, else the spawning member's recorded end.
        /// </summary>
        internal static double OtherTreeReferenceUT(Recording spawning, double candidateUT)
        {
            if (!double.IsNaN(candidateUT)) return candidateUT;
            return spawning != null ? spawning.EndUT : double.NaN;
        }

        /// <summary>
        /// Pure verdict for an overlap between <paramref name="spawning"/>'s spawn and the live
        /// vessel of <paramref name="blockerLatest"/> (that vessel's LATEST recorded segment),
        /// which belongs to a different committed tree. Operator ruling 2026-09-29: an EVA kerbal
        /// or a placed ground part is not pushed off its recorded spot by a vessel that was
        /// already standing there when its recording was made and has not moved since; a vessel
        /// that arrived later still pushes it. Parsek records no foreign vessel's position during
        /// a recording, so "already standing there" is proven from the blocker's OWN committed
        /// history, all of which is required:
        /// <list type="bullet">
        /// <item>the spawning member is an EVA kerbal or a placed ground part (the ruling's scope;
        /// a vehicle keeps the #duplicate-stack de-overlap);</item>
        /// <item>the blocker's tree was committed BEFORE the spawning member's tree, so it existed
        /// when this recording was made rather than in a timeline recorded afterwards;</item>
        /// <item>its latest segment is a leaf that persists standing (<see cref="PersistsPastEnd"/>)
        /// and ended at or before the reference UT (<see cref="OtherTreeReferenceUT"/>): a history
        /// still running then was a replay (a ghost, not a vessel), a later end is a later arrival;</item>
        /// <item>it still stands within <paramref name="toleranceMeters"/> (horizontal) of that
        /// leaf's spawn spot: it has not moved since.</item>
        /// </list>
        /// Tree commit indices are positions in the committed-tree list (-1 unknown).
        /// </summary>
        internal static TreeSiblingOverlapVerdict ClassifyOtherTree(
            Recording spawning, bool spawningIsKerbalOrPlacedPart,
            Recording blockerLatest, int spawningTreeIndex, int blockerTreeIndex,
            double displacementMeters, double toleranceMeters, double candidateUT = double.NaN)
        {
            if (spawning == null || blockerLatest == null) return TreeSiblingOverlapVerdict.NotTreeSibling;
            if (!spawningIsKerbalOrPlacedPart) return TreeSiblingOverlapVerdict.SpawningNotKerbalOrPlacedPart;
            if (spawningTreeIndex < 0 || blockerTreeIndex < 0 || blockerTreeIndex >= spawningTreeIndex)
                return TreeSiblingOverlapVerdict.NotCommittedBefore;
            double referenceUT = OtherTreeReferenceUT(spawning, candidateUT);
            if (double.IsNaN(referenceUT) || !PersistsPastEnd(blockerLatest) || blockerLatest.EndUT > referenceUT)
                return TreeSiblingOverlapVerdict.NotStandingByThen;
            if (double.IsNaN(displacementMeters) || displacementMeters > toleranceMeters)
                return TreeSiblingOverlapVerdict.Displaced;
            return TreeSiblingOverlapVerdict.Exempt;
        }

        /// <summary>
        /// Pure core of both exemptions. Identifies the live candidate as the vessel of a
        /// committed recording (the spawning member's own tree first, then
        /// <paramref name="otherTreeRecordings"/>; never a segment of the spawning member's own
        /// launch, which would be a duplicate of itself), moves to that vessel's LATEST
        /// positively-same-launch segment (a chain's adoption stamp can sit on its head segment,
        /// whose end is mid-flight), measures the candidate's displacement from that segment's
        /// spawn spot, and classifies with <see cref="Classify"/> (same tree) or
        /// <see cref="ClassifyOtherTree"/> (other tree, ordered by <paramref name="treeCommitIndex"/>).
        /// <paramref name="blocker"/> is null when the candidate is no committed recording's vessel.
        /// </summary>
        internal static TreeSiblingOverlapVerdict EvaluateBlocker(
            IEnumerable<Recording> sameTreeRecordings, IEnumerable<Recording> otherTreeRecordings,
            Func<string, int> treeCommitIndex, Recording spawning, bool spawningIsKerbalOrPlacedPart,
            uint candidatePid, string candidateGuid,
            string candidateBodyName, double candidateLat, double candidateLon, double bodyRadius,
            out Recording blocker, out OverlapBlockerKind kind, out double displacementMeters,
            double candidateUT = double.NaN)
        {
            blocker = null;
            kind = OverlapBlockerKind.None;
            displacementMeters = double.PositiveInfinity;
            if (spawning == null || candidatePid == 0) return TreeSiblingOverlapVerdict.NotTreeSibling;

            var pool = new List<Recording>();
            if (sameTreeRecordings != null)
            {
                foreach (Recording r in sameTreeRecordings)
                {
                    if (r == null || IsSpawningOwnLaunch(spawning, r)) continue;
                    if (string.IsNullOrEmpty(spawning.TreeId)
                        || !string.Equals(r.TreeId, spawning.TreeId, StringComparison.Ordinal))
                        continue;
                    pool.Add(r);
                }
            }
            int sameTreeCount = pool.Count;
            if (otherTreeRecordings != null)
            {
                foreach (Recording r in otherTreeRecordings)
                {
                    if (r == null || IsSpawningOwnLaunch(spawning, r)) continue;
                    if (string.IsNullOrEmpty(r.TreeId)
                        || string.Equals(r.TreeId, spawning.TreeId, StringComparison.Ordinal))
                        continue;
                    pool.Add(r);
                }
            }

            // Spawn stamps first (the recording's own record of which live vessel is its vessel),
            // then the recorded vessel itself still live with no stamp (positive pid + Guid).
            Recording matched = null;
            for (int i = 0; i < pool.Count && matched == null; i++)
            {
                if (MatchesSpawnStamp(pool[i], candidatePid, candidateGuid, strict: i >= sameTreeCount))
                {
                    matched = pool[i];
                    kind = OverlapBlockerKind.Spawn;
                }
            }
            for (int i = 0; i < pool.Count && matched == null; i++)
            {
                if (VesselLaunchIdentity.LiveVesselIsPositivelyRecordedLaunch(pool[i], candidatePid, candidateGuid))
                {
                    matched = pool[i];
                    kind = OverlapBlockerKind.Original;
                }
            }
            if (matched == null) return TreeSiblingOverlapVerdict.NotTreeSibling;

            Recording latest = matched;
            for (int i = 0; i < pool.Count; i++)
            {
                Recording r = pool[i];
                if (ReferenceEquals(r, latest) || !PositivelySameLaunch(r, matched)) continue;
                if (IsLaterSegment(r, latest)) latest = r;
            }
            blocker = latest;
            displacementMeters = SiblingDisplacementMeters(
                latest, candidateBodyName, candidateLat, candidateLon, bodyRadius);

            bool sameTree = !string.IsNullOrEmpty(spawning.TreeId)
                && string.Equals(latest.TreeId, spawning.TreeId, StringComparison.Ordinal);
            if (sameTree)
            {
                double arrivalUT = double.NaN;
                if (!double.IsNaN(candidateUT)
                    && TryGetSiblingSpawnSpot(latest, out string spotBody, out double spotLat, out double spotLon))
                {
                    arrivalUT = SiblingArrivalAtSpotUT(
                        latest, spotBody, spotLat, spotLon, bodyRadius, PlacementToleranceMeters);
                }
                return Classify(spawning, latest, displacementMeters, PlacementToleranceMeters,
                    candidateUT, arrivalUT);
            }

            int spawningIdx = treeCommitIndex != null && !string.IsNullOrEmpty(spawning.TreeId)
                ? treeCommitIndex(spawning.TreeId) : -1;
            int blockerIdx = treeCommitIndex != null && !string.IsNullOrEmpty(latest.TreeId)
                ? treeCommitIndex(latest.TreeId) : -1;
            return ClassifyOtherTree(spawning, spawningIsKerbalOrPlacedPart, latest,
                spawningIdx, blockerIdx, displacementMeters, PlacementToleranceMeters, candidateUT);
        }

        /// <summary>
        /// True when <paramref name="rec"/> is an EVA kerbal (an EVA branch's
        /// <see cref="Recording.EvaCrewName"/>, or a recording whose own vessel is a kerbal on EVA:
        /// start situation EVA or snapshot <c>type = EVA</c>, as when recording started after the
        /// kerbal left) or a placed ground part (<see cref="GroundPartPlacement.IsPlacedPartMember"/>).
        /// The scope of the other-tree exemption.
        /// </summary>
        internal static bool IsKerbalOrPlacedPartMember(RecordingTree tree, Recording rec)
        {
            if (rec == null) return false;
            if (!string.IsNullOrEmpty(rec.EvaCrewName)) return true;
            if (string.Equals(rec.StartSituation, "EVA", StringComparison.OrdinalIgnoreCase)) return true;
            string snapshotType = rec.VesselSnapshot != null ? rec.VesselSnapshot.GetValue("type") : null;
            if (string.Equals(snapshotType, "EVA", StringComparison.Ordinal)) return true;
            return GroundPartPlacement.IsPlacedPartMember(tree, rec);
        }

        /// <summary>Live wrapper, bool form of <see cref="ResolveExemption"/>.</summary>
        internal static bool IsExemptBlocker(
            Recording spawning, uint candidatePid, string candidateGuid, string candidateName,
            string candidateBodyName, double candidateLat, double candidateLon, double bodyRadius,
            string site, double candidateUT = double.NaN)
        {
            return ResolveExemption(spawning, candidatePid, candidateGuid, candidateName,
                candidateBodyName, candidateLat, candidateLon, bodyRadius, site, candidateUT)
                != OverlapExemptionScope.None;
        }

        /// <summary>
        /// Live wrapper: resolves <paramref name="spawning"/>'s committed tree, the effective
        /// recordings of the other committed trees (<see cref="EffectiveState.ComputeERS"/>) and
        /// the committed-tree order, and decides whether the candidate vessel is an exempt blocker
        /// (<paramref name="candidateUT"/> NaN at the end-of-recording position, the candidate's
        /// recorded UT during a walkback). Logs one Info line per (spawning recording, candidate)
        /// pair and verdict, rate-limited so walkback sub-steps and per-frame blocked rechecks do
        /// not spam; logs nothing for a candidate that is no committed recording's vessel.
        /// </summary>
        internal static OverlapExemptionScope ResolveExemption(
            Recording spawning, uint candidatePid, string candidateGuid, string candidateName,
            string candidateBodyName, double candidateLat, double candidateLon, double bodyRadius,
            string site, double candidateUT = double.NaN)
        {
            if (spawning == null || string.IsNullOrEmpty(spawning.TreeId) || candidatePid == 0)
                return OverlapExemptionScope.None;

            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            RecordingTree tree = FindCommittedTree(trees, spawning.TreeId, out _);
            if (tree == null || tree.Recordings == null) return OverlapExemptionScope.None;

            TreeSiblingOverlapVerdict verdict = EvaluateBlocker(
                tree.Recordings.Values, EffectiveState.ComputeERS(),
                treeId => { FindCommittedTree(trees, treeId, out int idx); return idx; },
                spawning, IsKerbalOrPlacedPartMember(tree, spawning),
                candidatePid, candidateGuid, candidateBodyName, candidateLat, candidateLon, bodyRadius,
                out Recording blocker, out OverlapBlockerKind kind, out double displacement, candidateUT);
            if (verdict == TreeSiblingOverlapVerdict.NotTreeSibling || blocker == null)
                return OverlapExemptionScope.None;

            bool exempt = verdict == TreeSiblingOverlapVerdict.Exempt;
            bool sameTree = string.Equals(blocker.TreeId, spawning.TreeId, StringComparison.Ordinal);
            string kindWord = kind == OverlapBlockerKind.Original ? "original" : "spawn";
            string displacementText = double.IsInfinity(displacement) || double.IsNaN(displacement)
                ? "unknown"
                : displacement.ToString("F2", IC) + "m";
            string candidateText = double.IsNaN(candidateUT) ? "end" : candidateUT.ToString("F2", IC);
            string message;
            if (sameTree)
            {
                message = string.Format(IC,
                    "{0} {1}: site={2} tree={3} spawning='{4}' rec={5} blocker='{6}' pid={7} siblingRec={8} " +
                    "verdict={9} displacement={10} tolerance={11}m candidateUT={12}",
                    exempt ? ExemptLogLiteral : NotExemptLogLiteral,
                    kindWord,
                    site ?? "?",
                    spawning.TreeId,
                    Recording.ResolveLocalizedName(spawning.VesselName),
                    spawning.RecordingId,
                    candidateName ?? "?",
                    candidatePid,
                    blocker.RecordingId,
                    verdict,
                    displacementText,
                    PlacementToleranceMeters.ToString("F1", IC),
                    candidateText);
            }
            else
            {
                double referenceUT = OtherTreeReferenceUT(spawning, candidateUT);
                message = string.Format(IC,
                    "{0}: site={1} tree={2} spawning='{3}' rec={4} blocker='{5}' pid={6} blockerKind={7} " +
                    "blockerTree={8} blockerRec={9} blockerEndUT={10} referenceUT={11} " +
                    "verdict={12} displacement={13} tolerance={14}m candidateUT={15}",
                    exempt ? OtherTreeExemptLogLiteral : OtherTreeNotExemptLogLiteral,
                    site ?? "?",
                    spawning.TreeId,
                    Recording.ResolveLocalizedName(spawning.VesselName),
                    spawning.RecordingId,
                    candidateName ?? "?",
                    candidatePid,
                    kindWord,
                    blocker.TreeId ?? "?",
                    blocker.RecordingId,
                    blocker.EndUT.ToString("F2", IC),
                    double.IsNaN(referenceUT) ? "?" : referenceUT.ToString("F2", IC),
                    verdict,
                    displacementText,
                    PlacementToleranceMeters.ToString("F1", IC),
                    candidateText);
            }
            ParsekLog.InfoRateLimited(Tag,
                (sameTree ? "tree-sibling-" : "other-tree-") + verdict + "-" + spawning.RecordingId + "-"
                    + candidatePid.ToString(IC),
                message);
            if (!exempt) return OverlapExemptionScope.None;
            return sameTree ? OverlapExemptionScope.SameTree : OverlapExemptionScope.OtherTree;
        }

        private static RecordingTree FindCommittedTree(List<RecordingTree> trees, string treeId, out int index)
        {
            index = -1;
            if (trees == null || string.IsNullOrEmpty(treeId)) return null;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] != null && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                {
                    index = i;
                    return trees[i];
                }
            }
            return null;
        }
    }
}
