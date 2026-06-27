using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    public partial class ParsekFlight
    {
        private struct TerminalOrbitMetadataSnapshot
        {
            public string body;
            public double inclination;
            public double eccentricity;
            public double semiMajorAxis;
            public double lan;
            public double argumentOfPeriapsis;
            public double meanAnomalyAtEpoch;
            public double epoch;

            public bool HasMetadata => !string.IsNullOrEmpty(body);
        }

        private static TerminalOrbitMetadataSnapshot CaptureTerminalOrbitMetadataSnapshot(Recording rec)
        {
            if (rec == null)
                return default(TerminalOrbitMetadataSnapshot);

            return new TerminalOrbitMetadataSnapshot
            {
                body = rec.TerminalOrbitBody,
                inclination = rec.TerminalOrbitInclination,
                eccentricity = rec.TerminalOrbitEccentricity,
                semiMajorAxis = rec.TerminalOrbitSemiMajorAxis,
                lan = rec.TerminalOrbitLAN,
                argumentOfPeriapsis = rec.TerminalOrbitArgumentOfPeriapsis,
                meanAnomalyAtEpoch = rec.TerminalOrbitMeanAnomalyAtEpoch,
                epoch = rec.TerminalOrbitEpoch
            };
        }

        /// <summary>
        /// Removes zero-point leaf recordings from the tree (#173). These empty leaves have no
        /// trajectory data, no orbit segments, and no surface position. They can be same-frame
        /// debris fragments or transient placeholders left behind by split/finalize edge cases.
        /// </summary>
        internal static void PruneZeroPointLeaves(RecordingTree tree)
        {
            var toPrune = CollectZeroPointLeafIds(tree);
            if (toPrune == null || toPrune.Count == 0)
                return;

            PruneLeafRecordings(
                tree,
                toPrune,
                "PruneZeroPointLeaves",
                "zero-point leaf recording(s)");
        }

        internal static void PruneSinglePointDebrisLeaves(RecordingTree tree)
        {
            var toPrune = CollectSinglePointDebrisLeafIds(tree);
            if (toPrune == null || toPrune.Count == 0)
                return;

            // Same-frame breakup debris can die inside the coalescing window and only retain
            // the split seed point. These stubs are non-playable, fail the data-health
            // contract, and never contribute a meaningful ghost or spawn path. #447 widened
            // this beyond Destroyed, but only for debris that has already fully ended
            // (Landed / Recovered / Splashed / Destroyed). Scene-exit commits can still
            // produce one-point SubOrbital / Orbiting debris leaves; keep those for
            // diagnosis instead of silently deleting non-terminal recordings.

            // Terminal-state breakdown for the summary log (#447 follow-up): aggregate before
            // PruneLeafRecordings removes the recordings from the tree.
            var breakdown = new Dictionary<TerminalState, int>();
            for (int i = 0; i < toPrune.Count; i++)
            {
                if (!tree.Recordings.TryGetValue(toPrune[i], out var rec) || rec == null) continue;
                var state = rec.TerminalStateValue.Value;
                breakdown[state] = (breakdown.TryGetValue(state, out var n) ? n : 0) + 1;
            }
            string summarySuffix = FormatTerminalStateBreakdown(breakdown);

            PruneLeafRecordings(
                tree,
                toPrune,
                "PruneSinglePointDebrisLeaves",
                "single-point debris stub(s)",
                summarySuffix);
        }

        // #447 follow-up: render a "(Landed=2, Destroyed=1)" suffix for the prune summary
        // log. Returns empty string when nothing to break out.
        internal static string FormatTerminalStateBreakdown(
            Dictionary<TerminalState, int> breakdown)
        {
            if (breakdown == null || breakdown.Count == 0)
                return string.Empty;
            var parts = new List<string>();
            // Iterate enum values in their declared order so the suffix is deterministic
            // regardless of dictionary insertion order.
            foreach (TerminalState state in Enum.GetValues(typeof(TerminalState)))
            {
                if (breakdown.TryGetValue(state, out var count) && count > 0)
                    parts.Add($"{state}={count}");
            }
            return parts.Count == 0 ? string.Empty : " (" + string.Join(", ", parts) + ")";
        }

        private static void PruneLeafRecordings(
            RecordingTree tree,
            List<string> toPrune,
            string logTag,
            string description,
            string summarySuffix = null)
        {
            for (int i = 0; i < toPrune.Count; i++)
            {
                string id = toPrune[i];
                tree.Recordings.Remove(id);

                // Remove from parent branch point's children list
                for (int b = 0; b < tree.BranchPoints.Count; b++)
                {
                    tree.BranchPoints[b].ChildRecordingIds.Remove(id);
                }
            }

            // Clean up branch points with no remaining children
            int prunedBPs = 0;
            for (int b = tree.BranchPoints.Count - 1; b >= 0; b--)
            {
                if (tree.BranchPoints[b].ChildRecordingIds.Count == 0)
                {
                    // Clear the parent recording's ChildBranchPointId reference
                    string bpId = tree.BranchPoints[b].Id;
                    foreach (var kvp in tree.Recordings)
                    {
                        if (kvp.Value.ChildBranchPointId == bpId)
                            kvp.Value.ChildBranchPointId = null;
                    }
                    tree.BranchPoints.RemoveAt(b);
                    prunedBPs++;
                }
            }

            ParsekLog.Info("Flight",
                $"{logTag}: removed {toPrune.Count} {description}" +
                (string.IsNullOrEmpty(summarySuffix) ? "" : summarySuffix) +
                (prunedBPs > 0 ? $" and {prunedBPs} empty branch point(s)" : "") +
                $" from tree '{tree.TreeName}'");
        }

        /// <summary>
        /// Pure static helper: collects IDs of empty leaf recordings with no playback data.
        /// A leaf has no ChildBranchPointId and zero points + zero orbit segments + no surface pos.
        /// </summary>
        internal static List<string> CollectZeroPointLeafIds(RecordingTree tree)
        {
            List<string> result = null;
            foreach (var kvp in tree.Recordings)
            {
                var rec = kvp.Value;
                if (IsZeroPointLeaf(rec))
                {
                    if (result == null) result = new List<string>();
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        internal static List<string> CollectSinglePointDebrisLeafIds(RecordingTree tree)
        {
            List<string> result = null;
            foreach (var kvp in tree.Recordings)
            {
                if (kvp.Key == tree.RootRecordingId || kvp.Key == tree.ActiveRecordingId)
                    continue;

                var rec = kvp.Value;
                if (IsSinglePointDebrisLeaf(rec))
                {
                    if (result == null) result = new List<string>();
                    result.Add(kvp.Key);
                }
            }
            return result;
        }

        /// <summary>
        /// Returns true if a recording is a leaf with no playback data (zero points,
        /// no orbit segments, no surface position).
        /// </summary>
        internal static bool IsZeroPointLeaf(Recording rec)
        {
            if (rec.ChildBranchPointId != null) return false; // not a leaf
            if (rec.SidecarLoadFailed) return false; // keep explicit hydration failures for later recovery/inspection
            return rec.Points.Count == 0
                && rec.OrbitSegments.Count == 0
                && !rec.SurfacePos.HasValue;
        }

        internal static bool IsSinglePointDebrisLeaf(Recording rec)
        {
            return IsSinglePointDebrisLeafForState(rec, IsTerminalSinglePointDebrisStubState);
        }

        internal static bool IsStopMetricsExemptSinglePointDebrisLeaf(Recording rec)
        {
            return IsSinglePointDebrisLeafForState(rec, IsPreservedSinglePointInFlightDebrisState);
        }

        private static bool IsSinglePointDebrisLeafForState(
            Recording rec, Func<TerminalState, bool> statePredicate)
        {
            if (rec == null || rec.ChildBranchPointId != null) return false;
            if (rec.SidecarLoadFailed) return false;
            if (!rec.IsDebris) return false;
            if (rec.Points.Count != 1) return false;
            if (!rec.TerminalStateValue.HasValue
                || !statePredicate(rec.TerminalStateValue.Value))
                return false;
            if (rec.OrbitSegments.Count > 0 || rec.SurfacePos.HasValue) return false;
            return HasOnlyMirroredSinglePointTrackSection(rec);
        }

        private static bool IsTerminalSinglePointDebrisStubState(TerminalState state)
        {
            switch (state)
            {
                case TerminalState.Landed:
                case TerminalState.Splashed:
                case TerminalState.Destroyed:
                case TerminalState.Recovered:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsPreservedSinglePointInFlightDebrisState(TerminalState state)
        {
            switch (state)
            {
                case TerminalState.SubOrbital:
                case TerminalState.Orbiting:
                    return true;
                default:
                    return false;
            }
        }

        private static bool HasOnlyMirroredSinglePointTrackSection(Recording rec)
        {
            // #447: accept section-backed single-point debris only when the section is just
            // the mirrored seed point with no checkpoints. Anything richer than that is real
            // data and must stay visible to either pruning or REC-002.
            if (rec.TrackSections == null || rec.TrackSections.Count == 0) return true;
            if (rec.TrackSections.Count > 1) return false;
            var section = rec.TrackSections[0];
            int frames = section.frames?.Count ?? 0;
            int checkpoints = section.checkpoints?.Count ?? 0;
            return checkpoints == 0
                && frames == 1
                && TrajectoryPointEquals(section.frames[0], rec.Points[0]);
        }

        private static bool TrajectoryPointEquals(TrajectoryPoint a, TrajectoryPoint b)
        {
            return a.ut == b.ut
                && a.latitude == b.latitude
                && a.longitude == b.longitude
                && a.altitude == b.altitude
                && a.rotation.x == b.rotation.x
                && a.rotation.y == b.rotation.y
                && a.rotation.z == b.rotation.z
                && a.rotation.w == b.rotation.w
                && a.velocity.x == b.velocity.x
                && a.velocity.y == b.velocity.y
                && a.velocity.z == b.velocity.z
                && a.bodyName == b.bodyName
                && a.funds == b.funds
                && a.science == b.science
                && a.reputation == b.reputation;
        }

        /// <summary>
        /// Captures terminal orbit parameters from a vessel's current orbit.
        /// </summary>
        internal static void CaptureTerminalOrbit(Recording rec, Vessel vessel)
        {
            if (vessel == null || vessel.orbit == null) return;

            var sit = vessel.situation;
            if (sit == Vessel.Situations.ORBITING || sit == Vessel.Situations.SUB_ORBITAL
                || sit == Vessel.Situations.FLYING || sit == Vessel.Situations.ESCAPING)
            {
                var orb = vessel.orbit;
                string bodyName = orb.referenceBody?.name;
                if (string.IsNullOrEmpty(bodyName))
                {
                    ParsekLog.Warn("Flight",
                        $"CaptureTerminalOrbit: skipped '{rec?.RecordingId ?? "(null)"}' " +
                        $"because orbit reference body was null (vessel='{vessel.vesselName}')");
                    return;
                }

                rec.TerminalOrbitInclination = orb.inclination;
                rec.TerminalOrbitEccentricity = orb.eccentricity;
                rec.TerminalOrbitSemiMajorAxis = orb.semiMajorAxis;
                rec.TerminalOrbitLAN = orb.LAN;
                rec.TerminalOrbitArgumentOfPeriapsis = orb.argumentOfPeriapsis;
                rec.TerminalOrbitMeanAnomalyAtEpoch = orb.meanAnomalyAtEpoch;
                rec.TerminalOrbitEpoch = orb.epoch;
                rec.TerminalOrbitBody = bodyName;
            }
        }

        private const double TerminalOrbitNumericTolerance = 1e-6;

        private static bool TerminalOrbitScalarMatches(double cachedValue, double segmentValue)
            => Math.Abs(cachedValue - segmentValue) <= TerminalOrbitNumericTolerance;

        private static bool CachedTerminalOrbitMatchesSegment(Recording rec, OrbitSegment seg)
        {
            if (rec == null || string.IsNullOrEmpty(rec.TerminalOrbitBody))
                return false;

            return string.Equals(rec.TerminalOrbitBody, seg.bodyName, StringComparison.Ordinal)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitInclination, seg.inclination)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitEccentricity, seg.eccentricity)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitSemiMajorAxis, seg.semiMajorAxis)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitLAN, seg.longitudeOfAscendingNode)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitArgumentOfPeriapsis, seg.argumentOfPeriapsis)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitMeanAnomalyAtEpoch, seg.meanAnomalyAtEpoch)
                && TerminalOrbitScalarMatches(rec.TerminalOrbitEpoch, seg.epoch);
        }

        private static void CopyTerminalOrbitFromSegment(Recording rec, OrbitSegment seg)
        {
            rec.TerminalOrbitInclination = seg.inclination;
            rec.TerminalOrbitEccentricity = seg.eccentricity;
            rec.TerminalOrbitSemiMajorAxis = seg.semiMajorAxis;
            rec.TerminalOrbitLAN = seg.longitudeOfAscendingNode;
            rec.TerminalOrbitArgumentOfPeriapsis = seg.argumentOfPeriapsis;
            rec.TerminalOrbitMeanAnomalyAtEpoch = seg.meanAnomalyAtEpoch;
            rec.TerminalOrbitEpoch = seg.epoch;
            rec.TerminalOrbitBody = seg.bodyName;
        }

        private static bool TryGetSameUtPointAnchoredTerminalOrbitBody(
            Recording rec,
            OrbitSegment seg,
            out string pointBody,
            out double pointUT)
        {
            pointBody = null;
            pointUT = 0.0;

            if (rec?.Points == null || rec.Points.Count == 0
                || string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                return false;
            }

            // Only the terminal point is authoritative for this finalize-only
            // same-UT check; earlier points may legitimately belong to a prior SOI.
            TrajectoryPoint lastPoint = rec.Points[rec.Points.Count - 1];
            pointBody = lastPoint.bodyName;
            pointUT = lastPoint.ut;
            if (string.IsNullOrEmpty(pointBody)
                || !string.Equals(pointBody, rec.TerminalOrbitBody, StringComparison.Ordinal)
                || string.Equals(pointBody, seg.bodyName, StringComparison.Ordinal))
            {
                return false;
            }

            return Math.Abs(seg.startUT - pointUT) <= TerminalOrbitNumericTolerance;
        }

        private static bool TryGetLastMatchingBodyOrbitSegment(
            Recording rec,
            string bodyName,
            out OrbitSegment matchingSeg)
        {
            matchingSeg = default;
            if (rec?.OrbitSegments == null || string.IsNullOrEmpty(bodyName))
                return false;

            // Skip the conflicting last segment and walk backward through earlier
            // orbit evidence on the point-anchored body.
            for (int i = rec.OrbitSegments.Count - 2; i >= 0; i--)
            {
                OrbitSegment candidate = rec.OrbitSegments[i];
                if (candidate.semiMajorAxis > 0.0
                    && string.Equals(candidate.bodyName, bodyName, StringComparison.Ordinal))
                {
                    matchingSeg = candidate;
                    return true;
                }
            }

            return false;
        }

        private static void LogPreservedSameUtPointAnchor(
            Recording rec,
            OrbitSegment conflictingSeg,
            string pointBody,
            double pointUT)
        {
            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "FinalizeIndividualRecording: preserved same-UT point-anchored terminal orbit for '{0}' pointBody={1} conflictingSegmentBody={2} conflictingSegmentStartUT={3:F3} pointUT={4:F3}",
                    rec?.RecordingId ?? "(null)",
                    pointBody,
                    conflictingSeg.bodyName,
                    conflictingSeg.startUT,
                    pointUT));
        }

        private static void LogSuspiciousSameUtPointAnchorPreserve(
            Recording rec,
            OrbitSegment conflictingSeg,
            string pointBody,
            double pointUT)
        {
            ParsekLog.Warn("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "FinalizeIndividualRecording: preserved same-UT point-anchored terminal orbit for '{0}' without matching-body heal evidence despite cachedSma={1:F1} pointBody={2} conflictingSegmentBody={3} conflictingSegmentStartUT={4:F3} pointUT={5:F3}",
                    rec?.RecordingId ?? "(null)",
                    rec?.TerminalOrbitSemiMajorAxis ?? double.NaN,
                    pointBody,
                    conflictingSeg.bodyName,
                    conflictingSeg.startUT,
                    pointUT));
        }

        private static bool TryHandleFinalizeSameUtPointAnchoredTerminalOrbit(Recording rec)
        {
            if (rec?.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return false;

            OrbitSegment conflictingSeg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            if (!TryGetSameUtPointAnchoredTerminalOrbitBody(
                rec,
                conflictingSeg,
                out string pointBody,
                out double pointUT))
            {
                return false;
            }

            if (TryGetLastMatchingBodyOrbitSegment(rec, pointBody, out OrbitSegment matchingSeg))
            {
                if (CachedTerminalOrbitMatchesSegment(rec, matchingSeg))
                {
                    LogPreservedSameUtPointAnchor(rec, conflictingSeg, pointBody, pointUT);
                }
                else
                {
                    string previousBody = rec.TerminalOrbitBody;
                    double previousSemiMajorAxis = rec.TerminalOrbitSemiMajorAxis;
                    CopyTerminalOrbitFromSegment(rec, matchingSeg);
                    ParsekLog.Warn("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "FinalizeIndividualRecording: healed same-UT point-anchored terminal orbit for '{0}' previousBody={1} previousSma={2:F1} healedBody={3} healedSma={4:F1} matchingSegmentEndUT={5:F3} conflictingSegmentBody={6} conflictingSegmentStartUT={7:F3} pointUT={8:F3}",
                            rec.RecordingId ?? "(null)",
                            previousBody ?? "(empty)",
                            previousSemiMajorAxis,
                            matchingSeg.bodyName,
                            matchingSeg.semiMajorAxis,
                            matchingSeg.endUT,
                            conflictingSeg.bodyName,
                            conflictingSeg.startUT,
                            pointUT));
                }

                return true;
            }

            if (rec.TerminalOrbitSemiMajorAxis <= 0.0)
                LogSuspiciousSameUtPointAnchorPreserve(rec, conflictingSeg, pointBody, pointUT);
            else
                LogPreservedSameUtPointAnchor(rec, conflictingSeg, pointBody, pointUT);
            return true;
        }

        /// <summary>
        /// Returns whether the last endpoint-aligned OrbitSegment should repopulate
        /// terminal orbit fields, either for unloaded/destroyed vessels or to heal
        /// a stale cached terminal-orbit tuple on finalize/load. Explicit point/surface
        /// endpoint data keeps already-populated terminal orbit fields authoritative;
        /// otherwise, already-populated values are preserved only when the full cached
        /// tuple already matches the endpoint-aligned segment.
        /// (#219/#475/#484)
        /// </summary>
        internal static bool ShouldPopulateTerminalOrbitFromLastSegment(Recording rec)
        {
            if (rec?.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return false;

            OrbitSegment seg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            if (string.IsNullOrEmpty(seg.bodyName))
                return false;

            bool hasEndpointBody = RecordingEndpointResolver.TryGetPreferredEndpointBodyName(rec, out string endpointBody);
            bool endpointAligned = hasEndpointBody
                && string.Equals(seg.bodyName, endpointBody, StringComparison.Ordinal);
            if (string.IsNullOrEmpty(rec.TerminalOrbitBody))
            {
                return !hasEndpointBody || endpointAligned;
            }

            bool hasExplicitEndpointBody = RecordingEndpointResolver.TryGetExplicitEndpointBodyName(
                rec,
                out string explicitEndpointBody);
            if (hasExplicitEndpointBody
                && string.Equals(rec.TerminalOrbitBody, explicitEndpointBody, StringComparison.Ordinal))
            {
                ParsekLog.Info("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "ShouldPopulateTerminalOrbitFromLastSegment: preserved cached terminal orbit for '{0}' because explicit endpoint body={1} keeps cached orbit authoritative over later segment body={2} sma={3:F1}",
                        rec.RecordingId ?? "(null)",
                        explicitEndpointBody,
                        seg.bodyName,
                        seg.semiMajorAxis));
                return false;
            }

            bool cachedTupleMatchesLastSegment = CachedTerminalOrbitMatchesSegment(rec, seg);
            if (cachedTupleMatchesLastSegment)
            {
                if (endpointAligned)
                {
                    ParsekLog.Info("Flight",
                        string.Format(CultureInfo.InvariantCulture,
                            "ShouldPopulateTerminalOrbitFromLastSegment: preserved cached terminal orbit for '{0}' because cached tuple already matches endpoint-aligned segment body={1} sma={2:F1}",
                            rec.RecordingId ?? "(null)",
                            seg.bodyName,
                            seg.semiMajorAxis));
                }

                return false;
            }

            if (hasExplicitEndpointBody)
            {
                return string.Equals(seg.bodyName, explicitEndpointBody, StringComparison.Ordinal)
                    && !string.Equals(rec.TerminalOrbitBody, explicitEndpointBody, StringComparison.Ordinal);
            }

            if (!hasEndpointBody)
                return false;

            return endpointAligned;
        }

        internal static void PopulateTerminalOrbitFromLastSegment(Recording rec)
        {
            if (!ShouldPopulateTerminalOrbitFromLastSegment(rec)) return;

            var seg = rec.OrbitSegments[rec.OrbitSegments.Count - 1];
            string previousBody = rec.TerminalOrbitBody;
            double previousSemiMajorAxis = rec.TerminalOrbitSemiMajorAxis;
            bool healingStaleCachedTuple = !string.IsNullOrEmpty(previousBody)
                && !CachedTerminalOrbitMatchesSegment(rec, seg);
            CopyTerminalOrbitFromSegment(rec, seg);

            if (healingStaleCachedTuple)
            {
                ParsekLog.Warn("Flight",
                    string.Format(CultureInfo.InvariantCulture,
                        "PopulateTerminalOrbitFromLastSegment: healed stale cached terminal orbit for '{0}' previousBody={1} previousSma={2:F1} newBody={3} newSma={4:F1}",
                        rec.RecordingId ?? "(null)",
                        previousBody ?? "(empty)",
                        previousSemiMajorAxis,
                        seg.bodyName,
                        seg.semiMajorAxis));
                return;
            }

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "PopulateTerminalOrbitFromLastSegment: recovered orbit for '{0}' from segment body={1} sma={2:F1}",
                    rec.RecordingId ?? "(null)",
                    seg.bodyName,
                    seg.semiMajorAxis));
        }

        /// <summary>
        /// Captures terminal surface position from a vessel's current state.
        /// </summary>
        static void CaptureTerminalPosition(Recording rec, Vessel vessel)
        {
            if (vessel == null) return;

            var sit = vessel.situation;
            if (sit == Vessel.Situations.LANDED || sit == Vessel.Situations.SPLASHED
                || sit == Vessel.Situations.PRELAUNCH)
            {
                rec.TerminalPosition = new SurfacePosition
                {
                    body = vessel.mainBody?.name ?? "Kerbin",
                    latitude = vessel.latitude,
                    longitude = vessel.longitude,
                    altitude = vessel.altitude,
                    rotation = vessel.srfRelRotation,
                    rotationRecorded = true,
                    situation = sit == Vessel.Situations.SPLASHED
                        ? SurfaceSituation.Splashed
                        : SurfaceSituation.Landed
                };

                // Capture TRUE surface height (including buildings/mesh objects like
                // the Island Airfield, launchpad, KSC facilities). vessel.terrainAltitude
                // is computed by KSP from Physics.Raycast in CheckGroundCollision, so it
                // accounts for placed colliders — unlike body.TerrainAltitude() which is
                // PQS-only and returns the raw planetary surface UNDER any mesh object.
                //
                // CAVEAT (review finding): KSP's Vessel.UpdatePosVel sets
                //   terrainAltitude = altitude - heightFromTerrain   // raycast path, includes buildings
                // only when vessel.heightFromTerrain != -1f, which requires the vessel to
                // be loaded AND unpacked. For a packed vessel it falls through to
                //   terrainAltitude = pqsAltitude                     // PQS-only path
                // which is exactly the value we're trying to escape. So if the vessel is
                // packed at capture time (commit-tree-later flow with a backgrounded
                // airfield vessel), fall back to firing our own raycast against the same
                // layer mask KSP uses in GetHeightFromSurface. Covers the mesh-object
                // case without requiring the vessel to be active.
                if (vessel.mainBody != null)
                {
                    double captured;
                    string source;
                    if (!vessel.packed)
                    {
                        captured = vessel.terrainAltitude;
                        source = "vessel.terrainAltitude";
                    }
                    else
                    {
                        double raycastSurface = VesselSpawner.TryFindSurfaceAltitudeViaRaycast(
                            vessel.mainBody, vessel.latitude, vessel.longitude, vessel.altitude);
                        if (!double.IsNaN(raycastSurface))
                        {
                            captured = raycastSurface;
                            source = "packed-vessel raycast";
                        }
                        else
                        {
                            // Last-resort fallback: PQS terrain. Logs a warning so it's visible
                            // if the mesh-object fix silently degrades on a future recording.
                            captured = vessel.mainBody.TerrainAltitude(vessel.latitude, vessel.longitude);
                            source = "PQS fallback";
                            ParsekLog.Warn("TerrainCorrect",
                                $"CaptureTerminalPosition: vessel '{vessel.vesselName}' is packed and " +
                                $"raycast missed — falling back to PQS terrain (may bury mesh-object spawns)");
                        }
                    }

                    rec.TerrainHeightAtEnd = captured;
                    double pqs = vessel.mainBody.TerrainAltitude(vessel.latitude, vessel.longitude);
                    ParsekLog.Verbose("TerrainCorrect",
                        $"Captured surface height at recording end: {rec.TerrainHeightAtEnd:F1}m " +
                        $"(source={source}, vessel alt={vessel.altitude:F1}m, " +
                        $"clearance={vessel.altitude - rec.TerrainHeightAtEnd:F1}m, " +
                        $"pqsTerrain={pqs:F1}m, meshOffset={rec.TerrainHeightAtEnd - pqs:F1}m)");
                }
            }
        }
    }
}
