using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Highest-fidelity-wins merge algorithm. Takes a RecordingTree and produces
    /// one merged Recording per vessel with non-overlapping TrackSections selected
    /// by source priority: Active > Background > Checkpoint.
    /// </summary>
    internal static class SessionMerger
    {
        private const string Tag = "Merger";
        private const double OrbitUtTolerance = 1e-6;
        private const double OrbitScalarTolerance = 1e-9;
        private const double OrbitDistanceTolerance = 1e-6;
        private const double OrbitVectorTolerance = 1e-6;
        private const double BoundaryPointUtTolerance = 1e-6;
        private const double RelativeAnchorLocalDeltaMaxDtSeconds = 0.001;

        /// <summary>
        /// Stock KSP body equatorial radii in meters. Used by ComputeBoundaryDiscontinuity
        /// for lat/lon-to-meters conversion on the correct body.
        /// Modded bodies (RSS, Kopernicus) fall back to Kerbin radius — acceptable for
        /// this diagnostic-only calculation.
        /// </summary>
        private static readonly Dictionary<string, double> BodyRadii = new Dictionary<string, double>
        {
            { "Kerbol", 261600000 },
            { "Moho", 250000 },
            { "Eve", 700000 },
            { "Gilly", 13000 },
            { "Kerbin", 600000 },
            { "Mun", 200000 },
            { "Minmus", 60000 },
            { "Duna", 320000 },
            { "Ike", 130000 },
            { "Dres", 138000 },
            { "Jool", 6000000 },
            { "Laythe", 500000 },
            { "Vall", 300000 },
            { "Tylo", 600000 },
            { "Bop", 65000 },
            { "Pol", 44000 },
            { "Eeloo", 210000 }
        };

        /// <summary>
        /// Returns the equatorial radius for a stock KSP body.
        /// Falls back to Kerbin radius (600,000m) for null or unknown body names.
        /// </summary>
        private static double GetBodyRadius(string bodyName)
        {
            if (bodyName == null)
                return 600000.0;
            if (BodyRadii.TryGetValue(bodyName, out double radius))
                return radius;
            return 600000.0;
        }

        /// <summary>
        /// Merges all recordings in the tree. For each recording, resolves overlapping
        /// TrackSections by source priority and merges PartEvents with deduplication.
        /// Returns a dictionary mapping recordingId to a clean merged Recording.
        /// </summary>
        internal static Dictionary<string, Recording> MergeTree(RecordingTree tree)
        {
            var ic = CultureInfo.InvariantCulture;
            var result = new Dictionary<string, Recording>();

            if (tree == null)
            {
                ParsekLog.Warn(Tag, "MergeTree: null tree, returning empty result");
                return result;
            }

            if (tree.Recordings.Count == 0)
            {
                ParsekLog.Info(Tag, "MergeTree: tree has no recordings, returning empty result");
                return result;
            }

            ParsekLog.Info(Tag,
                $"MergeTree: starting merge for tree='{tree.TreeName}' recordings={tree.Recordings.Count}");

            foreach (var kvp in tree.Recordings)
            {
                string recId = kvp.Key;
                Recording srcRec = kvp.Value;

                RecordingStore.EnsureCheckpointSectionsForTopLevelOrbitSegments(
                    srcRec,
                    markDirty: true,
                    context: "SessionMerger.MergeTree");
                int inputSectionCount = srcRec.TrackSections != null ? srcRec.TrackSections.Count : 0;

                // Resolve overlapping TrackSections
                int recordingFormatVersion = srcRec.RecordingFormatVersion;
                List<BoundaryDiscontinuityMeasurement> boundaryMeasurements;
                List<TrackSection> mergedSections = ResolveOverlaps(
                    srcRec.TrackSections ?? new List<TrackSection>(),
                    recordingFormatVersion,
                    out boundaryMeasurements);
                int healedUnrecordedGaps = HealBackgroundActiveUnrecordedGapBoundaries(
                    recId, srcRec.VesselName, mergedSections, recordingFormatVersion,
                    out List<TrackSection> preHealMergedSections);
                if (healedUnrecordedGaps > 0)
                    boundaryMeasurements =
                        RecomputeBoundaryDiscontinuities(mergedSections, recordingFormatVersion);

                // Merge PartEvents (source recording may only have one list, just deduplicate+sort)
                List<PartEvent> mergedEvents = MergePartEvents(
                    srcRec.PartEvents ?? new List<PartEvent>(),
                    new List<PartEvent>());

                // Build merged Recording
                var merged = new Recording();
                merged.RecordingId = srcRec.RecordingId;
                merged.VesselName = srcRec.VesselName;
                merged.TreeId = srcRec.TreeId;
                merged.VesselPersistentId = srcRec.VesselPersistentId;
                merged.RecordingFormatVersion = srcRec.RecordingFormatVersion;
                merged.TrackSections = mergedSections;
                merged.PartEvents = mergedEvents;

                SyncMergedFlatTrajectory(
                    srcRec,
                    merged,
                    preHealMergedSections,
                    healedUnrecordedGaps,
                    out string flatSyncMode);

                // Copy SegmentEvents
                if (srcRec.SegmentEvents != null)
                    merged.SegmentEvents = new List<SegmentEvent>(srcRec.SegmentEvents);

                // Copy terminal/linkage state
                merged.TerminalStateValue = srcRec.TerminalStateValue;
                merged.ParentBranchPointId = srcRec.ParentBranchPointId;
                merged.ChildBranchPointId = srcRec.ChildBranchPointId;
                merged.ExplicitStartUT = srcRec.ExplicitStartUT;
                merged.ExplicitEndUT = srcRec.ExplicitEndUT;
                if (srcRec.Controllers != null)
                    merged.Controllers = new List<ControllerInfo>(srcRec.Controllers);
                merged.IsDebris = srcRec.IsDebris;
                // PR 3b: propagate the v13 debris parent-anchor contract through merges.
                merged.DebrisParentRecordingId = srcRec.DebrisParentRecordingId;

                // Location context (Phase 10)
                merged.StartBodyName = srcRec.StartBodyName;
                merged.StartBiome = srcRec.StartBiome;
                merged.StartSituation = srcRec.StartSituation;
                merged.EndBiome = srcRec.EndBiome;
                merged.LaunchSiteName = srcRec.LaunchSiteName;

                result[recId] = merged;

                LogMergeDiagnostics(recId, srcRec.VesselName, inputSectionCount,
                    srcRec.TrackSections ?? new List<TrackSection>(), mergedSections,
                    recordingFormatVersion, boundaryMeasurements);
                ParsekLog.Verbose(Tag,
                    $"MergeTree: recording='{recId}' flatSync={flatSyncMode}");
            }

            ParsekLog.Info(Tag,
                $"MergeTree: completed merge for tree='{tree.TreeName}' merged={result.Count} recordings");

            return result;
        }

        private static bool SyncMergedFlatTrajectory(
            Recording source,
            Recording target,
            List<TrackSection> preHealMergedSections,
            int healedUnrecordedGaps,
            out string flatSyncMode)
        {
            const bool allowRelativeSections = true;
            flatSyncMode = "track-sections-fallback";

            // Preserve newer flat tail data when the resolved TrackSections are only a
            // prefix of the source flat trajectory (e.g. board/merge appended points after
            // the last flushed sparse section). Otherwise prefer the resolved sections and
            // rebuild the flat lists from them.
            bool flatTailExtendsResolvedSections =
                RecordingStore.FlatTrajectoryExtendsTrackSectionPayload(
                    source, target.TrackSections, allowRelativeSections);

            if (!flatTailExtendsResolvedSections)
            {
                if (healedUnrecordedGaps > 0
                    && preHealMergedSections != null
                    && RecordingStore.TrySyncFlatTrajectoryFromTrackSectionsPreservingFlatTail(
                        target, source, preHealMergedSections, allowRelativeSections))
                {
                    flatSyncMode = "healed-track-sections-preserved-flat-tail";
                    return true;
                }

                if (TrySyncFlatTrajectoryPreservingPredictedOrbitTail(
                        source,
                        target,
                        allowRelativeSections,
                        out int preservedOrbitTailCount))
                {
                    flatSyncMode = "track-sections-preserved-predicted-orbit-tail:" + preservedOrbitTailCount;
                    return true;
                }

                if (RecordingStore.TrySyncFlatTrajectoryFromTrackSections(
                        target, allowRelativeSections))
                {
                    flatSyncMode = "track-sections";
                    return true;
                }
            }

            bool copiedFlatTrajectory = false;
            if (source.Points != null && source.Points.Count > 0)
            {
                target.Points = new List<TrajectoryPoint>(source.Points);
                copiedFlatTrajectory = true;
            }

            if (source.OrbitSegments != null && source.OrbitSegments.Count > 0)
            {
                target.OrbitSegments = new List<OrbitSegment>(source.OrbitSegments);
                copiedFlatTrajectory = true;
            }

            if (copiedFlatTrajectory)
            {
                flatSyncMode = flatTailExtendsResolvedSections
                    ? "preserved-flat-copy"
                    : "track-sections-fallback";
                return false;
            }

            bool rebuiltFlatTrajectory =
                RecordingStore.TrySyncFlatTrajectoryFromTrackSections(
                    target, allowRelativeSections);
            flatSyncMode = rebuiltFlatTrajectory
                ? "track-sections"
                : "track-sections-fallback";
            return rebuiltFlatTrajectory;
        }

        /// <summary>
        /// Rebuilds section-owned flat points while preserving a finalizer-authored
        /// predicted orbit suffix that lives only in the source flat orbit list.
        /// </summary>
        private static bool TrySyncFlatTrajectoryPreservingPredictedOrbitTail(
            Recording source,
            Recording target,
            bool allowRelativeSections,
            out int preservedOrbitTailCount)
        {
            preservedOrbitTailCount = 0;
            if (source == null
                || target == null
                || source.OrbitSegments == null
                || source.OrbitSegments.Count == 0
                || !RecordingStore.HasCompleteTrackSectionPayloadForFlatSync(
                    target.TrackSections, allowRelativeSections))
            {
                return false;
            }

            if (!TryGetMaxTrackSectionEndUT(target.TrackSections, out double sectionEndUT))
                return false;

            // Align the predicted-tail floor with the playback hand-off bound. Playback
            // (GhostPlaybackEngine.TryFindOrbitTailPlaybackSegment) treats the orbit tail
            // as starting after the last recorded source point, not after the last
            // TrackSection.endUT. The recorder commonly extends the trailing section
            // endUT past the UT of its last frame (post-recording settle tail), and the
            // finalizer's TryReseedFirstPredictedTailSegmentFromRecordedAnchor moves the
            // first predicted segment's startUT back to that last-frame UT. Using just
            // maxTrackSectionEndUT as the floor would drop that reseeded segment because
            // its startUT < endUT-of-last-section. We take min(lastRecordedPointUT,
            // maxTrackSectionEndUT) so the floor never sits past the playback bound, while
            // still rejecting segments that overlap the resolved TrackSection payload
            // (those are excluded by the resolved-prefix walk inside
            // FindPredictedOrbitTailStart).
            double predictedTailFloorUT = sectionEndUT;
            if (TryGetLastSourcePointUT(source.Points, out double lastSourcePointUT)
                && lastSourcePointUT < predictedTailFloorUT)
            {
                predictedTailFloorUT = lastSourcePointUT;
            }

            var rebuiltPoints = new List<TrajectoryPoint>();
            RecordingStore.RebuildPointsFromTrackSections(target.TrackSections, rebuiltPoints);

            var rebuiltOrbitSegments = new List<OrbitSegment>();
            RecordingStore.RebuildOrbitSegmentsFromTrackSections(
                target.TrackSections, rebuiltOrbitSegments);

            int suffixStart = FindPredictedOrbitTailStart(
                source.OrbitSegments, rebuiltOrbitSegments, predictedTailFloorUT);
            if (suffixStart < 0)
                return false;

            var mergedOrbitSegments = new List<OrbitSegment>(rebuiltOrbitSegments);
            for (int i = suffixStart; i < source.OrbitSegments.Count; i++)
            {
                OrbitSegment segment = source.OrbitSegments[i];
                if (mergedOrbitSegments.Count > 0
                    && OrbitSegmentNearlyEquals(
                        mergedOrbitSegments[mergedOrbitSegments.Count - 1],
                        segment))
                {
                    continue;
                }

                mergedOrbitSegments.Add(segment);
                preservedOrbitTailCount++;
            }

            if (preservedOrbitTailCount == 0)
                return false;

            target.Points = rebuiltPoints;
            target.OrbitSegments = mergedOrbitSegments;
            target.CachedStats = null;
            target.CachedStatsPointCount = 0;
            return true;
        }

        /// <summary>
        /// Returns the latest section end UT. The caller uses the result as the
        /// earliest safe start for any preserved predicted orbit suffix.
        /// </summary>
        private static bool TryGetMaxTrackSectionEndUT(
            List<TrackSection> sections,
            out double maxEndUT)
        {
            maxEndUT = double.NegativeInfinity;
            if (sections == null || sections.Count == 0)
                return false;

            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i].endUT > maxEndUT)
                    maxEndUT = sections[i].endUT;
            }

            return true;
        }

        /// <summary>
        /// Returns the UT of the last entry in the source <see cref="Recording.Points"/>
        /// list (the playback orbit-tail hand-off bound). Returns false when the source
        /// has no recorded points; callers then fall back to the TrackSection.endUT bound.
        /// </summary>
        private static bool TryGetLastSourcePointUT(
            List<TrajectoryPoint> points,
            out double lastPointUT)
        {
            lastPointUT = double.NegativeInfinity;
            if (points == null || points.Count == 0)
                return false;

            lastPointUT = points[points.Count - 1].ut;
            return true;
        }

        /// <summary>
        /// Locates the first source orbit segment that can be appended as a
        /// predicted finalizer tail after the resolved track-section payload.
        /// </summary>
        private static int FindPredictedOrbitTailStart(
            List<OrbitSegment> sourceOrbitSegments,
            List<OrbitSegment> rebuiltOrbitSegments,
            double minStartUT)
        {
            if (sourceOrbitSegments == null
                || rebuiltOrbitSegments == null
                || sourceOrbitSegments.Count <= rebuiltOrbitSegments.Count)
            {
                return -1;
            }

            int start;
            if (rebuiltOrbitSegments.Count > 0)
            {
                int sourceIndex = 0;
                int lastMatchedSourceIndex = -1;
                for (int i = 0; i < rebuiltOrbitSegments.Count; i++)
                {
                    OrbitSegment rebuilt = rebuiltOrbitSegments[i];
                    bool matched = false;
                    while (sourceIndex < sourceOrbitSegments.Count)
                    {
                        OrbitSegment source = sourceOrbitSegments[sourceIndex];
                        if (OrbitSegmentCoversEquivalentResolvedSegment(source, rebuilt))
                        {
                            matched = true;
                            lastMatchedSourceIndex = sourceIndex;
                            if (source.endUT <= rebuilt.endUT + OrbitUtTolerance)
                                sourceIndex++;
                            break;
                        }

                        if (source.endUT <= rebuilt.startUT + OrbitUtTolerance)
                        {
                            sourceIndex++;
                            continue;
                        }

                        return -1;
                    }

                    if (!matched)
                        return -1;
                }

                start = lastMatchedSourceIndex + 1;
                while (start < sourceOrbitSegments.Count
                    && !sourceOrbitSegments[start].isPredicted
                    && sourceOrbitSegments[start].endUT <= minStartUT + OrbitUtTolerance)
                {
                    start++;
                }
            }
            else
            {
                start = -1;
                for (int i = 0; i < sourceOrbitSegments.Count; i++)
                {
                    if (StartsAtOrAfter(sourceOrbitSegments[i].startUT, minStartUT))
                    {
                        start = i;
                        break;
                    }
                }
            }

            if (start < 0 || start >= sourceOrbitSegments.Count)
                return -1;
            if (!StartsAtOrAfter(sourceOrbitSegments[start].startUT, minStartUT))
                return -1;

            double previousStartUT = start > 0
                ? sourceOrbitSegments[start - 1].startUT
                : double.NegativeInfinity;
            for (int i = start; i < sourceOrbitSegments.Count; i++)
            {
                OrbitSegment segment = sourceOrbitSegments[i];
                if (!segment.isPredicted)
                    return -1;
                if (StartsBefore(segment.startUT, previousStartUT))
                    return -1;
                previousStartUT = segment.startUT;
            }

            return start;
        }

        private static bool OrbitSegmentCoversEquivalentResolvedSegment(
            OrbitSegment source,
            OrbitSegment rebuilt)
        {
            return source.startUT <= rebuilt.startUT + OrbitUtTolerance
                && source.endUT + OrbitUtTolerance >= rebuilt.endUT
                && NearlyEqual(source.inclination, rebuilt.inclination, OrbitScalarTolerance)
                && NearlyEqual(source.eccentricity, rebuilt.eccentricity, OrbitScalarTolerance)
                && NearlyEqual(source.semiMajorAxis, rebuilt.semiMajorAxis, OrbitDistanceTolerance)
                && NearlyEqual(source.longitudeOfAscendingNode, rebuilt.longitudeOfAscendingNode, OrbitScalarTolerance)
                && NearlyEqual(source.argumentOfPeriapsis, rebuilt.argumentOfPeriapsis, OrbitScalarTolerance)
                && NearlyEqual(source.meanAnomalyAtEpoch, rebuilt.meanAnomalyAtEpoch, OrbitScalarTolerance)
                && NearlyEqual(source.epoch, rebuilt.epoch, OrbitUtTolerance)
                && source.bodyName == rebuilt.bodyName
                && source.isPredicted == rebuilt.isPredicted
                && NearlyEqual(source.orbitalFrameRotation.x, rebuilt.orbitalFrameRotation.x, OrbitVectorTolerance)
                && NearlyEqual(source.orbitalFrameRotation.y, rebuilt.orbitalFrameRotation.y, OrbitVectorTolerance)
                && NearlyEqual(source.orbitalFrameRotation.z, rebuilt.orbitalFrameRotation.z, OrbitVectorTolerance)
                && NearlyEqual(source.orbitalFrameRotation.w, rebuilt.orbitalFrameRotation.w, OrbitVectorTolerance)
                && NearlyEqual(source.angularVelocity.x, rebuilt.angularVelocity.x, OrbitVectorTolerance)
                && NearlyEqual(source.angularVelocity.y, rebuilt.angularVelocity.y, OrbitVectorTolerance)
                && NearlyEqual(source.angularVelocity.z, rebuilt.angularVelocity.z, OrbitVectorTolerance);
        }

        private static bool StartsAtOrAfter(double candidateStartUT, double minStartUT)
        {
            return candidateStartUT + OrbitUtTolerance >= minStartUT;
        }

        private static bool StartsBefore(double candidateStartUT, double previousStartUT)
        {
            return candidateStartUT + OrbitUtTolerance < previousStartUT;
        }

        private static bool OrbitSegmentNearlyEquals(OrbitSegment a, OrbitSegment b)
        {
            return NearlyEqual(a.startUT, b.startUT, OrbitUtTolerance)
                && NearlyEqual(a.endUT, b.endUT, OrbitUtTolerance)
                && NearlyEqual(a.inclination, b.inclination, OrbitScalarTolerance)
                && NearlyEqual(a.eccentricity, b.eccentricity, OrbitScalarTolerance)
                && NearlyEqual(a.semiMajorAxis, b.semiMajorAxis, OrbitDistanceTolerance)
                && NearlyEqual(a.longitudeOfAscendingNode, b.longitudeOfAscendingNode, OrbitScalarTolerance)
                && NearlyEqual(a.argumentOfPeriapsis, b.argumentOfPeriapsis, OrbitScalarTolerance)
                && NearlyEqual(a.meanAnomalyAtEpoch, b.meanAnomalyAtEpoch, OrbitScalarTolerance)
                && NearlyEqual(a.epoch, b.epoch, OrbitUtTolerance)
                && a.bodyName == b.bodyName
                && a.isPredicted == b.isPredicted
                && NearlyEqual(a.orbitalFrameRotation.x, b.orbitalFrameRotation.x, OrbitVectorTolerance)
                && NearlyEqual(a.orbitalFrameRotation.y, b.orbitalFrameRotation.y, OrbitVectorTolerance)
                && NearlyEqual(a.orbitalFrameRotation.z, b.orbitalFrameRotation.z, OrbitVectorTolerance)
                && NearlyEqual(a.orbitalFrameRotation.w, b.orbitalFrameRotation.w, OrbitVectorTolerance)
                && NearlyEqual(a.angularVelocity.x, b.angularVelocity.x, OrbitVectorTolerance)
                && NearlyEqual(a.angularVelocity.y, b.angularVelocity.y, OrbitVectorTolerance)
                && NearlyEqual(a.angularVelocity.z, b.angularVelocity.z, OrbitVectorTolerance);
        }

        private static bool NearlyEqual(double a, double b, double tolerance)
        {
            return Math.Abs(a - b) <= tolerance;
        }

        /// <summary>
        /// Logs diagnostics for a single recording's merge result: section counts by source,
        /// overlaps resolved, and boundary discontinuity warnings.
        /// </summary>
        private static void LogMergeDiagnostics(
            string recId, string vesselName, int inputSectionCount,
            List<TrackSection> inputSections, List<TrackSection> mergedSections,
            int recordingFormatVersion,
            List<BoundaryDiscontinuityMeasurement> boundaryMeasurements)
        {
            var ic = CultureInfo.InvariantCulture;

            int outputSectionCount = mergedSections.Count;
            int overlapsResolved = inputSectionCount > outputSectionCount
                ? inputSectionCount - outputSectionCount : 0;
            int overlapCount = CountOverlapsResolved(inputSections, mergedSections);

            int activeCount = 0, bgCount = 0, cpCount = 0;
            for (int i = 0; i < mergedSections.Count; i++)
            {
                switch (mergedSections[i].source)
                {
                    case TrackSectionSource.Active: activeCount++; break;
                    case TrackSectionSource.Background: bgCount++; break;
                    case TrackSectionSource.Checkpoint: cpCount++; break;
                }
            }

            ParsekLog.Info(Tag,
                $"MergeTree: vessel='{vesselName}' id={recId} " +
                $"inputSections={inputSectionCount} outputSections={outputSectionCount} " +
                $"overlapsResolved={overlapCount} " +
                $"active={activeCount} background={bgCount} checkpoint={cpCount}");

            // Log discontinuity warnings (#449: include time-gap classification so the
            // root cause is visible at-a-glance instead of needing log-archaeology).
            for (int i = 0; i < mergedSections.Count; i++)
            {
                BoundaryDiscontinuityMeasurement measurement =
                    GetBoundaryMeasurement(
                        mergedSections, boundaryMeasurements, i, recordingFormatVersion);
                if (i > 0 && !string.IsNullOrEmpty(measurement.SkipReason))
                {
                    string prevRef = mergedSections[i - 1].referenceFrame.ToString();
                    string prevSrc = mergedSections[i - 1].source.ToString();
                    ParsekLog.Verbose(Tag,
                        $"MergeTree: boundary discontinuity skipped at section[{i}] " +
                        $"ut={mergedSections[i].startUT.ToString("F2", ic)} " +
                        $"vessel='{vesselName}' recId={recId} " +
                        $"prevRef={prevRef} nextRef={mergedSections[i].referenceFrame} " +
                        $"prevSrc={prevSrc} nextSrc={mergedSections[i].source} " +
                        $"branch={measurement.Branch} reason={measurement.SkipReason} " +
                        $"prevPoint={measurement.PrevPointSource} nextPoint={measurement.NextPointSource}");
                }

                float disc = measurement.Meters;
                if (disc > 1.0f)
                {
                    string prevRef = i > 0 ? mergedSections[i - 1].referenceFrame.ToString() : "?";
                    string prevSrc = i > 0 ? mergedSections[i - 1].source.ToString() : "?";
                    bool saveLoadTeleport = i > 0
                        && LooksLikeSaveLoadTeleportBoundary(
                            inputSections,
                            mergedSections[i - 1],
                            mergedSections[i]);
                    ClassifyBoundaryDiscontinuity(
                        i > 0 ? mergedSections[i - 1] : default(TrackSection),
                        mergedSections[i],
                        i > 0,
                        disc,
                        saveLoadTeleport,
                        out double dt, out double expectedM, out string cause);
                    double logDt = SanitizeMeasurementDt(dt);
                    string ratio = FormatBoundaryDiscontinuityRatio(disc, expectedM);
                    ParsekLog.Warn(Tag,
                        $"MergeTree: boundary discontinuity={disc.ToString("F2", ic)}m " +
                        $"at section[{i}] ut={mergedSections[i].startUT.ToString("F2", ic)} " +
                        $"vessel='{vesselName}' " +
                        $"prevRef={prevRef} nextRef={mergedSections[i].referenceFrame} " +
                        $"prevSrc={prevSrc} nextSrc={mergedSections[i].source} " +
                        $"dt={logDt.ToString("F2", ic)}s " +
                        $"expectedFromVel={expectedM.ToString("F2", ic)}m " +
                        $"ratio={ratio} " +
                        $"cause={cause}");
                }
            }
        }

        /// <summary>
        /// Classifies a boundary discontinuity using the time gap and the previous section's
        /// last-frame velocity (#449). Output buckets:
        /// <list type="bullet">
        ///   <item><c>frame-mismatch</c> — gap is effectively zero (&lt;0.05s) but the
        ///   position jumps; points at an interpolation/source-frame bug.</item>
        ///   <item><c>unrecorded-gap</c> — discontinuity is consistent with
        ///   <c>|prev.velocity| × dt</c> (with a tolerance margin); points at a legitimate
        ///   pause-and-resume span (quickload-resume, scene-reload, frame-budget spike) that
        ///   the recorder couldn't sample through.</item>
        ///   <item><c>save-load-teleport</c> — overlap resolution exposed a boundary where
        ///   active data from the pre-load future was glued to resumed post-load data.</item>
        ///   <item><c>sample-skip</c> — discontinuity exceeds the velocity-implied gap;
        ///   points at a dropped-sample / drift bug or a source-tag mismatch.</item>
        ///   <item><c>invalid-data</c> — boundary UTs, measured discontinuity, or the
        ///   previous tail velocity are non-finite, so the classifier refuses to
        ///   downgrade the warning to a benign gap.</item>
        /// </list>
        /// Pure helper so unit tests can verify the classification independently of logging.
        /// </summary>
        internal static void ClassifyBoundaryDiscontinuity(
            TrackSection prev, TrackSection next, bool hasPrev, float discMeters,
            out double dtSeconds, out double expectedMeters, out string cause)
        {
            ClassifyBoundaryDiscontinuity(
                prev, next, hasPrev, discMeters,
                looksLikeSaveLoadTeleport: false,
                out dtSeconds, out expectedMeters, out cause);
        }

        internal static void ClassifyBoundaryDiscontinuity(
            TrackSection prev, TrackSection next, bool hasPrev, float discMeters,
            bool looksLikeSaveLoadTeleport,
            out double dtSeconds, out double expectedMeters, out string cause)
        {
            dtSeconds = 0.0;
            expectedMeters = 0.0;
            cause = "no-prev";

            if (!hasPrev) return;
            if (prev.frames == null || prev.frames.Count == 0)
            {
                cause = "prev-no-frames";
                return;
            }
            if (next.frames == null || next.frames.Count == 0)
            {
                cause = "next-no-frames";
                return;
            }

            TrajectoryPoint lastPrev = prev.frames[prev.frames.Count - 1];
            TrajectoryPoint firstNext = next.frames[0];

            dtSeconds = firstNext.ut - lastPrev.ut;
            if (!IsFinite(dtSeconds)
                || !IsFinite(discMeters)
                || !IsFiniteVector3(lastPrev.velocity))
            {
                expectedMeters = double.NaN;
                cause = "invalid-data";
                return;
            }

            // Magnitude of the recorded velocity at the prior section's tail. The
            // velocity field stores playback velocity captured from KSP — surface or
            // orbital depending on situation, but its magnitude is a reasonable
            // upper-bound on how far the vessel could have moved during dt.
            double vMag = Math.Sqrt(
                (double)lastPrev.velocity.x * lastPrev.velocity.x +
                (double)lastPrev.velocity.y * lastPrev.velocity.y +
                (double)lastPrev.velocity.z * lastPrev.velocity.z);
            double dtAbs = Math.Abs(dtSeconds);
            expectedMeters = vMag * dtAbs;

            // 5m floor + 2x margin: the velocity sample is one tick stale, the next
            // section's first sample is one tick ahead, and KSP physics can change
            // velocity between them. The floor swallows pure quantization noise.
            double tolerance = expectedMeters * 2.0 + 5.0;

            if (dtAbs < 0.05)
                cause = "frame-mismatch";
            else if ((double)discMeters <= tolerance)
                cause = "unrecorded-gap";
            else if (looksLikeSaveLoadTeleport)
                cause = "save-load-teleport";
            else
                cause = "sample-skip";
        }

        internal static bool LooksLikeSaveLoadTeleportBoundary(
            List<TrackSection> inputSections, TrackSection prev, TrackSection next)
        {
            if (inputSections == null || inputSections.Count < 2)
                return false;
            if (prev.source != TrackSectionSource.Active || next.source != TrackSectionSource.Active)
                return false;
            if (prev.referenceFrame != next.referenceFrame)
                return false;

            const double boundaryTolerance = 0.05;
            const double overlapEpsilon = 1e-3;
            double boundaryUT = next.startUT;
            bool foundTrimmedFutureTail = false;
            bool foundResumedOverlap = false;

            for (int i = 0; i < inputSections.Count; i++)
            {
                TrackSection section = inputSections[i];
                if (section.source != TrackSectionSource.Active
                    || section.referenceFrame != prev.referenceFrame)
                    continue;

                if (section.startUT < boundaryUT - overlapEpsilon
                    && Math.Abs(section.endUT - boundaryUT) <= boundaryTolerance)
                {
                    foundTrimmedFutureTail = true;
                }

                if (section.startUT < boundaryUT - overlapEpsilon
                    && section.endUT > boundaryUT + overlapEpsilon)
                {
                    foundResumedOverlap = true;
                }
            }

            return foundTrimmedFutureTail && foundResumedOverlap;
        }

        /// <summary>
        /// Resolves overlapping TrackSections by highest-fidelity-wins priority.
        /// Active(0) > Background(1) > Checkpoint(2) — lower enum value wins.
        /// Returns non-overlapping sections sorted by startUT with boundary
        /// discontinuity computed at each junction.
        /// </summary>
        internal static List<TrackSection> ResolveOverlaps(List<TrackSection> sections)
        {
            ParsekLog.Verbose(Tag, "ResolveOverlaps: current-format wrapper used");
            return ResolveOverlaps(sections, RecordingStore.CurrentRecordingFormatVersion);
        }

        internal static List<TrackSection> ResolveOverlaps(
            List<TrackSection> sections, int recordingFormatVersion)
        {
            return ResolveOverlaps(
                sections, recordingFormatVersion,
                out List<BoundaryDiscontinuityMeasurement> _);
        }

        private static List<TrackSection> ResolveOverlaps(
            List<TrackSection> sections, int recordingFormatVersion,
            out List<BoundaryDiscontinuityMeasurement> boundaryMeasurements)
        {
            var ic = CultureInfo.InvariantCulture;
            boundaryMeasurements = new List<BoundaryDiscontinuityMeasurement>();

            if (sections == null || sections.Count == 0)
            {
                ParsekLog.Verbose(Tag, "ResolveOverlaps: empty input, returning empty list");
                return new List<TrackSection>();
            }

            // Sort by startUT, then by priority (lower source value = higher priority = first)
            var sorted = new List<TrackSection>(sections);
            sorted.Sort((a, b) =>
            {
                int cmp = a.startUT.CompareTo(b.startUT);
                if (cmp != 0) return cmp;
                return ((int)a.source).CompareTo((int)b.source);
            });

            var output = new List<TrackSection>();

            for (int i = 0; i < sorted.Count; i++)
            {
                TrackSection current = sorted[i];

                // Skip sections with zero or negative duration
                if (current.endUT <= current.startUT)
                {
                    ParsekLog.Verbose(Tag,
                        $"ResolveOverlaps: skipping zero-duration section at ut={current.startUT.ToString("F2", ic)} " +
                        $"source={current.source}");
                    continue;
                }

                if (output.Count == 0)
                {
                    output.Add(current);
                    continue;
                }

                // Check against all existing output sections for overlaps
                var newOutput = new List<TrackSection>();

                for (int j = 0; j < output.Count; j++)
                {
                    TrackSection existing = output[j];

                    // No overlap: current is entirely after existing
                    if (current.startUT >= existing.endUT)
                    {
                        newOutput.Add(existing);
                        continue;
                    }

                    // No overlap: current is entirely before existing
                    if (current.endUT <= existing.startUT)
                    {
                        newOutput.Add(existing);
                        continue;
                    }

                    // Overlap detected — resolve by priority
                    bool currentWins = (int)current.source < (int)existing.source;

                    if (currentWins)
                    {
                        // Current has higher priority — trim existing around current
                        ParsekLog.Verbose(Tag,
                            $"ResolveOverlaps: {current.source} overrides {existing.source} " +
                            $"at [{current.startUT.ToString("F2", ic)},{current.endUT.ToString("F2", ic)}]");

                        // Part of existing before current
                        if (existing.startUT < current.startUT)
                        {
                            TrackSection before = existing;
                            before.endUT = current.startUT;
                            before.frames = TrimFrames(existing.frames, existing.startUT, current.startUT);
                            before.bodyFixedFrames = TrimFrames(existing.bodyFixedFrames, existing.startUT, current.startUT);
                            before.checkpoints = TrimCheckpoints(existing.checkpoints, existing.startUT, current.startUT);
                            newOutput.Add(before);
                        }

                        // Part of existing after current
                        if (existing.endUT > current.endUT)
                        {
                            TrackSection after = existing;
                            after.startUT = current.endUT;
                            after.frames = TrimFrames(existing.frames, current.endUT, existing.endUT);
                            after.bodyFixedFrames = TrimFrames(existing.bodyFixedFrames, current.endUT, existing.endUT);
                            after.checkpoints = TrimCheckpoints(existing.checkpoints, current.endUT, existing.endUT);
                            newOutput.Add(after);
                        }
                    }
                    else
                    {
                        // Existing has higher or equal priority — trim current around existing
                        newOutput.Add(existing);

                        ParsekLog.Verbose(Tag,
                            $"ResolveOverlaps: {existing.source} retains over {current.source} " +
                            $"at [{existing.startUT.ToString("F2", ic)},{existing.endUT.ToString("F2", ic)}]");

                        // Emit the portion of current BEFORE existing (if any) —
                        // this prevents losing gap portions when current spans multiple
                        // disjoint higher-priority sections.
                        if (current.startUT < existing.startUT)
                        {
                            TrackSection before = current;
                            before.endUT = existing.startUT;
                            before.frames = TrimFrames(current.frames, current.startUT, existing.startUT);
                            before.bodyFixedFrames = TrimFrames(current.bodyFixedFrames, current.startUT, existing.startUT);
                            before.checkpoints = TrimCheckpoints(current.checkpoints, current.startUT, existing.startUT);
                            if (before.endUT > before.startUT)
                                newOutput.Add(before);
                        }

                        // Trim current to only the non-overlapping portion after existing
                        if (current.endUT > existing.endUT)
                        {
                            TrackSection remainder = current;
                            remainder.startUT = existing.endUT;
                            remainder.frames = TrimFrames(current.frames, existing.endUT, current.endUT);
                            remainder.bodyFixedFrames = TrimFrames(current.bodyFixedFrames, existing.endUT, current.endUT);
                            remainder.checkpoints = TrimCheckpoints(current.checkpoints, existing.endUT, current.endUT);
                            current = remainder;
                        }
                        else
                        {
                            // Current is entirely covered by higher-priority existing
                            current.startUT = current.endUT; // Mark as consumed
                        }
                    }
                }

                output = newOutput;

                // Insert current if it still has valid duration
                if (current.endUT > current.startUT)
                {
                    // Find insertion position to maintain startUT order
                    int insertIdx = output.Count;
                    for (int k = 0; k < output.Count; k++)
                    {
                        if (output[k].startUT > current.startUT)
                        {
                            insertIdx = k;
                            break;
                        }
                    }
                    output.Insert(insertIdx, current);
                }
            }

            // Sort final output by startUT
            output.Sort((a, b) => a.startUT.CompareTo(b.startUT));

            boundaryMeasurements =
                RecomputeBoundaryDiscontinuities(output, recordingFormatVersion);

            ParsekLog.Verbose(Tag,
                $"ResolveOverlaps: input={sections.Count} output={output.Count}");

            return output;
        }

        internal static float ComputeBoundaryDiscontinuity(TrackSection prev, TrackSection next)
        {
            ParsekLog.Verbose(Tag, "ComputeBoundaryDiscontinuity: current-format wrapper used");
            return ComputeBoundaryDiscontinuity(
                prev, next, RecordingStore.CurrentRecordingFormatVersion);
        }

        internal static float ComputeBoundaryDiscontinuity(
            TrackSection prev, TrackSection next, int recordingFormatVersion)
        {
            return MeasureBoundaryDiscontinuity(
                prev, next, recordingFormatVersion).Meters;
        }

        private static BoundaryDiscontinuityMeasurement GetBoundaryMeasurement(
            List<TrackSection> sections,
            List<BoundaryDiscontinuityMeasurement> measurements,
            int index,
            int recordingFormatVersion)
        {
            if (measurements != null
                && index >= 0
                && index < measurements.Count
                && measurements.Count == (sections?.Count ?? 0))
            {
                return measurements[index];
            }

            if (index <= 0)
                return BoundaryDiscontinuityMeasurement.Zero("first-section", "no-prev");

            return MeasureBoundaryDiscontinuity(
                sections[index - 1], sections[index], recordingFormatVersion);
        }

        private struct BoundaryDiscontinuityMeasurement
        {
            public float Meters;
            public string Branch;
            public string SkipReason;
            public double DtSeconds;
            public string AnchorKey;
            public string PrevPointSource;
            public string NextPointSource;

            public static BoundaryDiscontinuityMeasurement Zero(string branch, string reason)
            {
                return new BoundaryDiscontinuityMeasurement
                {
                    Meters = 0f,
                    Branch = branch ?? "not-measurable",
                    SkipReason = reason,
                    DtSeconds = 0.0,
                    AnchorKey = string.Empty,
                    PrevPointSource = string.Empty,
                    NextPointSource = string.Empty
                };
            }
        }

        private static BoundaryDiscontinuityMeasurement MeasureBoundaryDiscontinuity(
            TrackSection prev, TrackSection next, int recordingFormatVersion)
        {
            if (prev.frames == null || prev.frames.Count == 0)
                return BoundaryDiscontinuityMeasurement.Zero("not-measurable", "prev-no-frames");
            if (next.frames == null || next.frames.Count == 0)
                return BoundaryDiscontinuityMeasurement.Zero("not-measurable", "next-no-frames");

            TrajectoryPoint lastPrev = prev.frames[prev.frames.Count - 1];
            TrajectoryPoint firstNext = next.frames[0];
            double dtSeconds = firstNext.ut - lastPrev.ut;

            if (prev.referenceFrame == ReferenceFrame.Relative
                || next.referenceFrame == ReferenceFrame.Relative)
            {
                return MeasureRelativeAwareBoundary(
                    prev, next, recordingFormatVersion, lastPrev, firstNext, dtSeconds);
            }

            if (prev.referenceFrame != ReferenceFrame.Absolute
                || next.referenceFrame != ReferenceFrame.Absolute)
            {
                BoundaryDiscontinuityMeasurement skipped =
                    BoundaryDiscontinuityMeasurement.Zero(
                        "not-measurable", "orbital-checkpoint-boundary");
                skipped.DtSeconds = SanitizeMeasurementDt(dtSeconds);
                return skipped;
            }

            return MeasureBodyFixedBoundary(
                lastPrev, firstNext, dtSeconds, "absolute", string.Empty,
                "prev.frames.last", "next.frames.first");
        }

        private static BoundaryDiscontinuityMeasurement MeasureRelativeAwareBoundary(
            TrackSection prev, TrackSection next, int recordingFormatVersion,
            TrajectoryPoint lastPrev, TrajectoryPoint firstNext, double dtSeconds)
        {
            bool sameAnchorLargeDtRequiresShadow = false;
            if (prev.referenceFrame == ReferenceFrame.Relative
                && next.referenceFrame == ReferenceFrame.Relative)
            {
                string anchorKey;
                bool sameAnchor = HasSameRelativeAnchorIdentity(prev, next, out anchorKey);
                if (sameAnchor
                    && recordingFormatVersion >= RecordingStore.RelativeLocalFrameFormatVersion
                    && IsFinite(dtSeconds)
                    && Math.Abs(dtSeconds) <= RelativeAnchorLocalDeltaMaxDtSeconds)
                {
                    double dx = firstNext.latitude - lastPrev.latitude;
                    double dy = firstNext.longitude - lastPrev.longitude;
                    double dz = firstNext.altitude - lastPrev.altitude;
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (!IsFinite(dist))
                        return BoundaryDiscontinuityMeasurement.Zero(
                            "not-measurable", "relative-anchor-local-nonfinite");

                    return new BoundaryDiscontinuityMeasurement
                    {
                        Meters = (float)dist,
                        Branch = "anchor-local",
                        SkipReason = null,
                        DtSeconds = SanitizeMeasurementDt(dtSeconds),
                        AnchorKey = anchorKey,
                        PrevPointSource = "prev.frames.last",
                        NextPointSource = "next.frames.first"
                    };
                }

                sameAnchorLargeDtRequiresShadow = sameAnchor
                    && recordingFormatVersion >= RecordingStore.RelativeLocalFrameFormatVersion;
            }

            string prevSource;
            string nextSource;
            string skipReason;
            TrajectoryPoint prevBodyFixed;
            TrajectoryPoint nextBodyFixed;
            if (!TryGetBodyFixedBoundaryPoint(
                    prev, useLast: true, recordingFormatVersion: recordingFormatVersion,
                    out prevBodyFixed, out prevSource, out skipReason))
            {
                return SkippedRelativeAwareMeasurement(
                    prev, next, dtSeconds,
                    sameAnchorLargeDtRequiresShadow
                        ? "same-anchor-shadow-required"
                        : "prev-" + skipReason);
            }
            if (!TryGetBodyFixedBoundaryPoint(
                    next, useLast: false, recordingFormatVersion: recordingFormatVersion,
                    out nextBodyFixed, out nextSource, out skipReason))
            {
                return SkippedRelativeAwareMeasurement(
                    prev, next, dtSeconds,
                    sameAnchorLargeDtRequiresShadow
                        ? "same-anchor-shadow-required"
                        : "next-" + skipReason);
            }

            string branch = prev.referenceFrame == ReferenceFrame.Relative
                || next.referenceFrame == ReferenceFrame.Relative
                    ? "body-fixed-primary"
                    : "absolute";
            return MeasureBodyFixedBoundary(
                prevBodyFixed, nextBodyFixed, dtSeconds, branch, string.Empty,
                prevSource, nextSource);
        }

        private static BoundaryDiscontinuityMeasurement SkippedRelativeAwareMeasurement(
            TrackSection prev, TrackSection next, double dtSeconds, string reason)
        {
            string branch = prev.referenceFrame == ReferenceFrame.Relative
                || next.referenceFrame == ReferenceFrame.Relative
                    ? "not-measurable"
                    : "absolute";
            return new BoundaryDiscontinuityMeasurement
            {
                Meters = 0f,
                Branch = branch,
                SkipReason = reason,
                DtSeconds = SanitizeMeasurementDt(dtSeconds),
                AnchorKey = string.Empty,
                PrevPointSource = string.Empty,
                NextPointSource = string.Empty
            };
        }

        private static BoundaryDiscontinuityMeasurement MeasureBodyFixedBoundary(
            TrajectoryPoint lastPrev, TrajectoryPoint firstNext, double dtSeconds,
            string branch, string anchorKey, string prevSource, string nextSource)
        {
            string prevBody = lastPrev.bodyName;
            string nextBody = firstNext.bodyName;
            if (!string.IsNullOrEmpty(prevBody)
                && !string.IsNullOrEmpty(nextBody)
                && !string.Equals(prevBody, nextBody, StringComparison.Ordinal))
            {
                return new BoundaryDiscontinuityMeasurement
                {
                    Meters = 0f,
                    Branch = branch,
                    SkipReason = "body-mismatch",
                    DtSeconds = SanitizeMeasurementDt(dtSeconds),
                    AnchorKey = anchorKey ?? string.Empty,
                    PrevPointSource = prevSource ?? string.Empty,
                    NextPointSource = nextSource ?? string.Empty
                };
            }

            if (!IsFinite(lastPrev.latitude) || !IsFinite(lastPrev.longitude)
                || !IsFinite(lastPrev.altitude) || !IsFinite(firstNext.latitude)
                || !IsFinite(firstNext.longitude) || !IsFinite(firstNext.altitude))
            {
                return new BoundaryDiscontinuityMeasurement
                {
                    Meters = 0f,
                    Branch = branch,
                    SkipReason = "point-data-nonfinite",
                    DtSeconds = SanitizeMeasurementDt(dtSeconds),
                    AnchorKey = anchorKey ?? string.Empty,
                    PrevPointSource = prevSource ?? string.Empty,
                    NextPointSource = nextSource ?? string.Empty
                };
            }

            double dAlt = firstNext.altitude - lastPrev.altitude;
            double bodyRadius = GetBodyRadius(lastPrev.bodyName);
            double dLat = (firstNext.latitude - lastPrev.latitude) * Math.PI / 180.0;
            double dLon = (firstNext.longitude - lastPrev.longitude) * Math.PI / 180.0;
            double avgLat = (firstNext.latitude + lastPrev.latitude) * 0.5 * Math.PI / 180.0;
            double r = bodyRadius + (firstNext.altitude + lastPrev.altitude) * 0.5;
            double dNorth = dLat * r;
            double dEast = dLon * r * Math.Cos(avgLat);
            double dist = Math.Sqrt(dNorth * dNorth + dEast * dEast + dAlt * dAlt);

            if (!IsFinite(dist))
            {
                return new BoundaryDiscontinuityMeasurement
                {
                    Meters = 0f,
                    Branch = branch,
                    SkipReason = "distance-nonfinite",
                    DtSeconds = SanitizeMeasurementDt(dtSeconds),
                    AnchorKey = anchorKey ?? string.Empty,
                    PrevPointSource = prevSource ?? string.Empty,
                    NextPointSource = nextSource ?? string.Empty
                };
            }

            return new BoundaryDiscontinuityMeasurement
            {
                Meters = (float)dist,
                Branch = branch,
                SkipReason = null,
                DtSeconds = SanitizeMeasurementDt(dtSeconds),
                AnchorKey = anchorKey ?? string.Empty,
                PrevPointSource = prevSource ?? string.Empty,
                NextPointSource = nextSource ?? string.Empty
            };
        }

        private static bool TryGetBodyFixedBoundaryPoint(
            TrackSection section, bool useLast, int recordingFormatVersion,
            out TrajectoryPoint point, out string source, out string reason)
        {
            point = default(TrajectoryPoint);
            source = string.Empty;
            reason = null;

            if (section.referenceFrame == ReferenceFrame.Absolute)
            {
                if (section.frames == null || section.frames.Count == 0)
                {
                    reason = "absolute-no-frames";
                    return false;
                }

                int index = useLast ? section.frames.Count - 1 : 0;
                point = section.frames[index];
                source = useLast ? "frames.last" : "frames.first";
                return true;
            }

            if (section.referenceFrame != ReferenceFrame.Relative)
            {
                reason = "orbital-checkpoint-no-body-fixed-frame";
                return false;
            }

            if (recordingFormatVersion < RecordingStore.RelativeLocalFrameFormatVersion)
            {
                reason = "legacy-relative-not-measurable";
                return false;
            }

            if (recordingFormatVersion < RecordingStore.RelativeBodyFixedPrimaryFormatVersion)
            {
                reason = "relative-shadow-format-unavailable";
                return false;
            }

            if (section.frames == null || section.frames.Count == 0)
            {
                reason = "relative-no-frames";
                return false;
            }
            if (section.bodyFixedFrames == null || section.bodyFixedFrames.Count == 0)
            {
                reason = "relative-shadow-missing";
                return false;
            }

            TrajectoryPoint ordinary = useLast
                ? section.frames[section.frames.Count - 1]
                : section.frames[0];
            if (!IsFinite(ordinary.ut))
            {
                reason = "relative-frame-ut-nonfinite";
                return false;
            }

            int start = useLast ? section.bodyFixedFrames.Count - 1 : 0;
            int step = useLast ? -1 : 1;
            for (int i = start; i >= 0 && i < section.bodyFixedFrames.Count; i += step)
            {
                TrajectoryPoint candidate = section.bodyFixedFrames[i];
                if (IsFinite(candidate.ut)
                    && Math.Abs(candidate.ut - ordinary.ut) <= BoundaryPointUtTolerance)
                {
                    point = candidate;
                    source = useLast ? "bodyFixedFrames.match-last" : "bodyFixedFrames.match-first";
                    return true;
                }
            }

            reason = "relative-shadow-ut-mismatch";
            return false;
        }

        private static bool HasSameRelativeAnchorIdentity(
            TrackSection prev, TrackSection next, out string anchorKey)
        {
            anchorKey = string.Empty;
            if (prev.referenceFrame != ReferenceFrame.Relative
                || next.referenceFrame != ReferenceFrame.Relative)
                return false;

            if (!string.IsNullOrWhiteSpace(prev.anchorRecordingId)
                || !string.IsNullOrWhiteSpace(next.anchorRecordingId))
            {
                if (string.IsNullOrWhiteSpace(prev.anchorRecordingId)
                    || string.IsNullOrWhiteSpace(next.anchorRecordingId)
                    || !StringComparer.Ordinal.Equals(
                        prev.anchorRecordingId, next.anchorRecordingId))
                    return false;

                anchorKey = "recording:" + prev.anchorRecordingId;
                return true;
            }

            if (prev.anchorVesselId == 0u || next.anchorVesselId == 0u)
                return false;

            if (prev.anchorVesselId != next.anchorVesselId)
                return false;

            anchorKey = "pid:" + prev.anchorVesselId.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        private static double SanitizeMeasurementDt(double dtSeconds)
        {
            return IsFinite(dtSeconds) ? dtSeconds : 0.0;
        }

        private static string FormatBoundaryDiscontinuityRatio(
            float discMeters, double expectedMeters)
        {
            if (!IsFinite(discMeters) || !IsFinite(expectedMeters))
                return "NaN";

            if (expectedMeters == 0.0)
                return "inf";

            double ratio = (double)discMeters / expectedMeters;
            if (!IsFinite(ratio))
                return ratio > 0.0 ? "inf" : "NaN";

            return ratio.ToString("F2", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Returns an anchor identity for Relative sections, or empty when the section
        /// has no usable anchor identity. Missing anchors are intentionally not reported
        /// as pid:0 because two malformed sections must not compare as same-anchor.
        /// </summary>
        internal static string AnchorIdentityKey(TrackSection section)
        {
            if (section.referenceFrame != ReferenceFrame.Relative)
                return string.Empty;

            if (!string.IsNullOrEmpty(section.anchorRecordingId))
                return "recording:" + section.anchorRecordingId;

            if (section.anchorVesselId == 0u)
                return string.Empty;

            return "pid:" + section.anchorVesselId.ToString(CultureInfo.InvariantCulture);
        }

        private static int HealBackgroundActiveUnrecordedGapBoundaries(
            string recId, string vesselName, List<TrackSection> sections,
            int recordingFormatVersion, out List<TrackSection> preHealSections)
        {
            preHealSections = null;
            if (sections == null || sections.Count < 2)
                return 0;

            var ic = CultureInfo.InvariantCulture;
            int healed = 0;

            for (int i = 1; i < sections.Count; i++)
            {
                TrackSection prev = sections[i - 1];
                TrackSection next = sections[i];
                // #580 is specifically a resume-into-active seam: Background holds
                // the tail and Active owns the next authoritative head. Leave the
                // reverse direction diagnostic-only until its sampling semantics are
                // proven equivalent.
                if (prev.source != TrackSectionSource.Background
                    || next.source != TrackSectionSource.Active
                    || prev.referenceFrame != next.referenceFrame
                    || prev.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    continue;
                }

                BoundaryDiscontinuityMeasurement measurement =
                    MeasureBoundaryDiscontinuity(prev, next, recordingFormatVersion);
                float disc = measurement.Meters;
                if (disc <= 1.0f)
                    continue;

                ClassifyBoundaryDiscontinuity(
                    prev,
                    next,
                    hasPrev: true,
                    discMeters: disc,
                    out double dt,
                    out double expectedM,
                    out string cause);
                if (cause != "unrecorded-gap")
                    continue;

                if (prev.referenceFrame == ReferenceFrame.Relative)
                {
                    ParsekLog.Verbose(Tag,
                        $"MergeTree: skipped unrecorded-gap heal at section[{i}] " +
                        $"ut={next.startUT.ToString("F2", ic)} vessel='{vesselName}' " +
                        $"recId={recId} prevSrc={prev.source} nextSrc={next.source} " +
                        $"dt={dt.ToString("F2", ic)}s expectedFromVel={expectedM.ToString("F2", ic)}m " +
                        $"reason=relative-healer-deferred branch={measurement.Branch}");
                    continue;
                }

                if (!TryBuildBoundarySeamPoint(
                        prev, next, next.startUT, out TrajectoryPoint seamPoint, out string reason))
                {
                    ParsekLog.Warn(Tag,
                        $"MergeTree: unable to heal unrecorded-gap at section[{i}] " +
                        $"ut={next.startUT.ToString("F2", ic)} vessel='{vesselName}' " +
                        $"recId={recId} prevSrc={prev.source} nextSrc={next.source} " +
                        $"dt={dt.ToString("F2", ic)}s expectedFromVel={expectedM.ToString("F2", ic)}m " +
                        $"reason={reason}");
                    continue;
                }

                bool appended = TryAppendSeamPoint(ref prev, seamPoint);
                bool prepended = TryPrependSeamPoint(ref next, seamPoint);
                if (!appended || !prepended)
                {
                    ParsekLog.Warn(Tag,
                        $"MergeTree: unable to heal unrecorded-gap at section[{i}] " +
                        $"ut={next.startUT.ToString("F2", ic)} vessel='{vesselName}' " +
                        $"recId={recId} prevSrc={prev.source} nextSrc={next.source} " +
                        $"dt={dt.ToString("F2", ic)}s expectedFromVel={expectedM.ToString("F2", ic)}m " +
                        $"reason=seam-insert-failed");
                    continue;
                }

                if (preHealSections == null)
                    preHealSections = CloneTrackSections(sections);
                sections[i - 1] = prev;
                sections[i] = next;
                healed++;

                float residual = ComputeBoundaryDiscontinuity(
                    prev, next, recordingFormatVersion);
                ParsekLog.Info(Tag,
                    $"MergeTree: healed unrecorded-gap at section[{i}] " +
                    $"ut={next.startUT.ToString("F2", ic)} vessel='{vesselName}' " +
                    $"recId={recId} prevRef={prev.referenceFrame} nextRef={next.referenceFrame} " +
                    $"prevSrc={prev.source} nextSrc={next.source} " +
                    $"dt={dt.ToString("F2", ic)}s expectedFromVel={expectedM.ToString("F2", ic)}m " +
                    $"insertedBoundaryPointUT={seamPoint.ut.ToString("F2", ic)} " +
                    $"residual={residual.ToString("F2", ic)}m cause=unrecorded-gap #580");
            }

            return healed;
        }

        private static List<BoundaryDiscontinuityMeasurement> RecomputeBoundaryDiscontinuities(
            List<TrackSection> sections, int recordingFormatVersion)
        {
            var measurements = new List<BoundaryDiscontinuityMeasurement>();
            if (sections == null || sections.Count == 0)
                return measurements;

            TrackSection first = sections[0];
            first.boundaryDiscontinuityMeters = 0f;
            sections[0] = first;
            measurements.Add(BoundaryDiscontinuityMeasurement.Zero("first-section", "no-prev"));

            for (int i = 1; i < sections.Count; i++)
            {
                TrackSection section = sections[i];
                BoundaryDiscontinuityMeasurement measurement =
                    MeasureBoundaryDiscontinuity(
                        sections[i - 1], section, recordingFormatVersion);
                section.boundaryDiscontinuityMeters = measurement.Meters;
                sections[i] = section;
                measurements.Add(measurement);
            }

            return measurements;
        }

        private static List<TrackSection> CloneTrackSections(List<TrackSection> sections)
        {
            var clones = new List<TrackSection>();
            if (sections == null)
                return clones;

            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection clone = sections[i];
                if (clone.frames != null)
                    clone.frames = new List<TrajectoryPoint>(clone.frames);
                if (clone.bodyFixedFrames != null)
                    clone.bodyFixedFrames = new List<TrajectoryPoint>(clone.bodyFixedFrames);
                if (clone.checkpoints != null)
                    clone.checkpoints = new List<OrbitSegment>(clone.checkpoints);
                clones.Add(clone);
            }

            return clones;
        }

        private static bool TryBuildBoundarySeamPoint(
            TrackSection prev, TrackSection next, double boundaryUT,
            out TrajectoryPoint seamPoint, out string reason)
        {
            seamPoint = default(TrajectoryPoint);
            reason = null;

            if (prev.frames == null || prev.frames.Count == 0)
            {
                reason = "prev-no-frames";
                return false;
            }
            if (next.frames == null || next.frames.Count == 0)
            {
                reason = "next-no-frames";
                return false;
            }
            if (!IsFinite(boundaryUT))
            {
                reason = "boundary-ut-nonfinite";
                return false;
            }

            TrajectoryPoint prevPoint = prev.frames[prev.frames.Count - 1];
            TrajectoryPoint nextPoint = next.frames[0];
            if (!IsFinite(prevPoint.ut) || !IsFinite(nextPoint.ut))
            {
                reason = "point-ut-nonfinite";
                return false;
            }
            if (!string.Equals(prevPoint.bodyName, nextPoint.bodyName, StringComparison.Ordinal))
            {
                reason = "body-mismatch";
                return false;
            }
            if (!IsFinite(prevPoint.latitude) || !IsFinite(prevPoint.longitude)
                || !IsFinite(prevPoint.altitude) || !IsFinite(nextPoint.latitude)
                || !IsFinite(nextPoint.longitude) || !IsFinite(nextPoint.altitude)
                || !IsFiniteVector3(prevPoint.velocity) || !IsFiniteVector3(nextPoint.velocity)
                || !IsFiniteQuaternion(prevPoint.rotation) || !IsFiniteQuaternion(nextPoint.rotation))
            {
                reason = "point-data-nonfinite";
                return false;
            }

            const double epsilon = 1e-6;
            double span = nextPoint.ut - prevPoint.ut;
            if (Math.Abs(boundaryUT - prevPoint.ut) <= epsilon)
            {
                seamPoint = prevPoint;
                seamPoint.ut = boundaryUT;
                return true;
            }
            if (Math.Abs(boundaryUT - nextPoint.ut) <= epsilon)
            {
                seamPoint = nextPoint;
                seamPoint.ut = boundaryUT;
                return true;
            }
            if (span <= epsilon)
            {
                reason = "non-positive-span";
                return false;
            }

            double t = (boundaryUT - prevPoint.ut) / span;
            if (t < -epsilon || t > 1.0 + epsilon)
            {
                reason = "boundary-outside-point-span";
                return false;
            }

            float tf = Mathf.Clamp01((float)t);
            seamPoint = new TrajectoryPoint
            {
                ut = boundaryUT,
                latitude = Lerp(prevPoint.latitude, nextPoint.latitude, t),
                longitude = Lerp(prevPoint.longitude, nextPoint.longitude, t),
                altitude = Lerp(prevPoint.altitude, nextPoint.altitude, t),
                rotation = LerpRotation(prevPoint.rotation, nextPoint.rotation, tf),
                velocity = Vector3.Lerp(prevPoint.velocity, nextPoint.velocity, tf),
                bodyName = prevPoint.bodyName,
                funds = prevPoint.funds,
                science = prevPoint.science,
                reputation = prevPoint.reputation,
                // Phase 7: lerp clearance across the seam (NaN propagates).
                recordedGroundClearance = Lerp(
                    prevPoint.recordedGroundClearance, nextPoint.recordedGroundClearance, t)
            };
            return true;
        }

        private static bool TryAppendSeamPoint(ref TrackSection section, TrajectoryPoint seamPoint)
        {
            if (section.frames == null)
            {
                section.frames = new List<TrajectoryPoint>();
            }
            else
            {
                if (section.frames.Count > 0)
                {
                    TrajectoryPoint last = section.frames[section.frames.Count - 1];
                    if (SameBoundaryPoint(last, seamPoint))
                        return true;
                    if (last.ut > seamPoint.ut)
                        return false;
                }

                section.frames = new List<TrajectoryPoint>(section.frames);
            }

            section.frames.Add(seamPoint);
            ExpandAltitudeRange(ref section, seamPoint.altitude);
            return true;
        }

        private static bool TryPrependSeamPoint(ref TrackSection section, TrajectoryPoint seamPoint)
        {
            if (section.frames == null)
            {
                section.frames = new List<TrajectoryPoint>();
            }
            else
            {
                if (section.frames.Count > 0)
                {
                    TrajectoryPoint first = section.frames[0];
                    if (SameBoundaryPoint(first, seamPoint))
                        return true;
                    if (first.ut < seamPoint.ut)
                        return false;
                }

                section.frames = new List<TrajectoryPoint>(section.frames);
            }

            section.frames.Insert(0, seamPoint);
            ExpandAltitudeRange(ref section, seamPoint.altitude);
            return true;
        }

        private static void ExpandAltitudeRange(ref TrackSection section, double altitude)
        {
            // The healer only inserts after TryBuildBoundarySeamPoint has verified
            // non-empty frame lists; NaN guards keep synthetic tests and legacy
            // sections from narrowing an uninitialized range.
            float value = (float)altitude;
            if (float.IsNaN(section.minAltitude) || value < section.minAltitude)
                section.minAltitude = value;
            if (float.IsNaN(section.maxAltitude) || value > section.maxAltitude)
                section.maxAltitude = value;
        }

        private static bool SameBoundaryPoint(TrajectoryPoint a, TrajectoryPoint b)
        {
            const double epsilon = 1e-6;
            return Math.Abs(a.ut - b.ut) <= epsilon
                && Math.Abs(a.latitude - b.latitude) <= epsilon
                && Math.Abs(a.longitude - b.longitude) <= epsilon
                && Math.Abs(a.altitude - b.altitude) <= epsilon
                && string.Equals(a.bodyName, b.bodyName, StringComparison.Ordinal);
        }

        private static double Lerp(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        private static Quaternion LerpRotation(Quaternion a, Quaternion b, float t)
        {
            // UnityEngine.Quaternion.Slerp is an engine internal call and throws under
            // the net472 headless test runner, so use hemisphere-corrected NLERP for
            // this visual seam glue instead.
            float dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
            if (dot < 0f)
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);

            return NormalizeQuaternion(new Quaternion(
                a.x + (b.x - a.x) * t,
                a.y + (b.y - a.y) * t,
                a.z + (b.z - a.z) * t,
                a.w + (b.w - a.w) * t));
        }

        private static Quaternion NormalizeQuaternion(Quaternion q)
        {
            double magnitude = Math.Sqrt(
                (double)q.x * q.x
                + (double)q.y * q.y
                + (double)q.z * q.z
                + (double)q.w * q.w);
            if (!IsFinite(magnitude) || magnitude < 0.001)
                return new Quaternion(0f, 0f, 0f, 1f);

            float inv = (float)(1.0 / magnitude);
            return new Quaternion(q.x * inv, q.y * inv, q.z * inv, q.w * inv);
        }

        /// <summary>
        /// Merges two PartEvent lists, deduplicating by (ut, partPersistentId, eventType)
        /// and sorting by UT with stable semantics so same-UT events keep their insertion
        /// order — critical for terminal Shutdowns at chain boundaries to stay before
        /// continuation seed EngineIgnited events (#287).
        /// </summary>
        internal static List<PartEvent> MergePartEvents(List<PartEvent> eventsA, List<PartEvent> eventsB)
        {
            var merged = new List<PartEvent>();
            var seen = new HashSet<string>();

            AddEventsWithDedup(merged, seen, eventsA);
            AddEventsWithDedup(merged, seen, eventsB);

            merged = FlightRecorder.StableSortPartEventsByUT(merged);

            ParsekLog.Verbose(Tag,
                $"MergePartEvents: inputA={eventsA?.Count ?? 0} inputB={eventsB?.Count ?? 0} " +
                $"output={merged.Count} deduped={((eventsA?.Count ?? 0) + (eventsB?.Count ?? 0)) - merged.Count}");

            return merged;
        }

        // --- Private helpers ---

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool IsFiniteVector3(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static bool IsFiniteQuaternion(Quaternion value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z)
                && !float.IsNaN(value.w) && !float.IsInfinity(value.w);
        }

        private static void AddEventsWithDedup(
            List<PartEvent> merged, HashSet<string> seen, List<PartEvent> events)
        {
            if (events == null) return;

            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < events.Count; i++)
            {
                PartEvent e = events[i];
                string key = string.Concat(
                    e.ut.ToString("R", ic), "|",
                    e.partPersistentId.ToString(ic), "|",
                    ((int)e.eventType).ToString(ic), "|",
                    e.moduleIndex.ToString(ic), "|",
                    e.value.ToString("R", ic));

                if (seen.Add(key))
                    merged.Add(e);
            }
        }

        /// <summary>
        /// Trims a trajectory frame list to only include frames within [startUT, endUT].
        /// Returns null if input is null or result is empty.
        /// </summary>
        private static List<TrajectoryPoint> TrimFrames(
            List<TrajectoryPoint> frames, double startUT, double endUT)
        {
            if (frames == null || frames.Count == 0)
                return null;

            var trimmed = new List<TrajectoryPoint>();
            for (int i = 0; i < frames.Count; i++)
            {
                if (frames[i].ut >= startUT && frames[i].ut <= endUT)
                    trimmed.Add(frames[i]);
            }

            return trimmed.Count > 0 ? trimmed : null;
        }

        /// <summary>
        /// Trims a checkpoint list to only include checkpoints within [startUT, endUT].
        /// Returns null if input is null or result is empty.
        /// </summary>
        private static List<OrbitSegment> TrimCheckpoints(
            List<OrbitSegment> checkpoints, double startUT, double endUT)
        {
            if (checkpoints == null || checkpoints.Count == 0)
                return null;

            var trimmed = new List<OrbitSegment>();
            for (int i = 0; i < checkpoints.Count; i++)
            {
                OrbitSegment clipped;
                if (OrbitSegmentCheckpointBridge.TryTrimOrbitSegmentToRange(
                        checkpoints[i], startUT, endUT, out clipped))
                {
                    trimmed.Add(clipped);
                }
            }

            return trimmed.Count > 0 ? trimmed : null;
        }

        /// <summary>
        /// Counts how many overlaps were resolved by comparing input and output sections.
        /// An overlap is resolved when an output section has different UT bounds than
        /// any corresponding input section, or when input sections were dropped entirely.
        /// </summary>
        private static int CountOverlapsResolved(
            List<TrackSection> input, List<TrackSection> output)
        {
            if (input == null || input.Count <= 1)
                return 0;

            // Simple heuristic: count input sections that were trimmed or split
            int count = 0;
            var outputBounds = new HashSet<string>();
            var ic = CultureInfo.InvariantCulture;

            for (int i = 0; i < output.Count; i++)
            {
                outputBounds.Add(string.Concat(
                    output[i].startUT.ToString("R", ic), "|",
                    output[i].endUT.ToString("R", ic), "|",
                    ((int)output[i].source).ToString(ic)));
            }

            for (int i = 0; i < input.Count; i++)
            {
                string key = string.Concat(
                    input[i].startUT.ToString("R", ic), "|",
                    input[i].endUT.ToString("R", ic), "|",
                    ((int)input[i].source).ToString(ic));

                if (!outputBounds.Contains(key))
                    count++;
            }

            return count;
        }
    }
}
