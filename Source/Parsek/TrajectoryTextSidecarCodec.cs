using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static class TrajectoryTextSidecarCodec
    {
        #region Trajectory Serialization

        private static void SerializePoint(ConfigNode parent, TrajectoryPoint pt, CultureInfo ic)
        {
            ConfigNode ptNode = parent.AddNode("POINT");
            SerializePointValues(ptNode, pt, ic);
        }

        private static void SerializePointValues(ConfigNode ptNode, TrajectoryPoint pt, CultureInfo ic)
        {
            ptNode.AddValue("ut", pt.ut.ToString("R", ic));
            ptNode.AddValue("lat", pt.latitude.ToString("R", ic));
            ptNode.AddValue("lon", pt.longitude.ToString("R", ic));
            ptNode.AddValue("alt", pt.altitude.ToString("R", ic));
            ptNode.AddValue("rotX", pt.rotation.x.ToString("R", ic));
            ptNode.AddValue("rotY", pt.rotation.y.ToString("R", ic));
            ptNode.AddValue("rotZ", pt.rotation.z.ToString("R", ic));
            ptNode.AddValue("rotW", pt.rotation.w.ToString("R", ic));
            ptNode.AddValue("body", pt.bodyName);
            ptNode.AddValue("velX", pt.velocity.x.ToString("R", ic));
            ptNode.AddValue("velY", pt.velocity.y.ToString("R", ic));
            ptNode.AddValue("velZ", pt.velocity.z.ToString("R", ic));
            ptNode.AddValue("funds", pt.funds.ToString("R", ic));
            ptNode.AddValue("science", pt.science.ToString("R", ic));
            ptNode.AddValue("rep", pt.reputation.ToString("R", ic));
        }

        private static TrajectoryPoint DeserializePoint(ConfigNode ptNode, NumberStyles ns, CultureInfo ic)
        {
            // Phase 7: text codec is the debug mirror only (post-refactor-4 the
            // canonical sidecar is binary). Default clearance to NaN sentinel —
            // anything reaching this path is at most a legacy fallback and must
            // play back via the legacy altitude path.
            var pt = new TrajectoryPoint
            {
                recordedGroundClearance = double.NaN
            };

            double.TryParse(ptNode.GetValue("ut"), ns, ic, out pt.ut);
            double.TryParse(ptNode.GetValue("lat"), ns, ic, out pt.latitude);
            double.TryParse(ptNode.GetValue("lon"), ns, ic, out pt.longitude);
            double.TryParse(ptNode.GetValue("alt"), ns, ic, out pt.altitude);

            float rx, ry, rz, rw;
            float.TryParse(ptNode.GetValue("rotX"), ns, ic, out rx);
            float.TryParse(ptNode.GetValue("rotY"), ns, ic, out ry);
            float.TryParse(ptNode.GetValue("rotZ"), ns, ic, out rz);
            float.TryParse(ptNode.GetValue("rotW"), ns, ic, out rw);
            pt.rotation = new Quaternion(rx, ry, rz, rw);

            pt.bodyName = ptNode.GetValue("body") ?? "Kerbin";

            float velX, velY, velZ;
            float.TryParse(ptNode.GetValue("velX"), ns, ic, out velX);
            float.TryParse(ptNode.GetValue("velY"), ns, ic, out velY);
            float.TryParse(ptNode.GetValue("velZ"), ns, ic, out velZ);
            pt.velocity = new Vector3(velX, velY, velZ);

            double funds;
            double.TryParse(ptNode.GetValue("funds"), ns, ic, out funds);
            pt.funds = funds;

            float science, rep;
            float.TryParse(ptNode.GetValue("science"), ns, ic, out science);
            float.TryParse(ptNode.GetValue("rep"), ns, ic, out rep);
            pt.science = science;
            pt.reputation = rep;

            return pt;
        }

        private static void SerializeOrbitSegment(
            ConfigNode parent,
            OrbitSegment seg,
            CultureInfo ic,
            int recordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
            bool writeLegacyPredictedFlag = false)
        {
            ConfigNode segNode = parent.AddNode("ORBIT_SEGMENT");
            segNode.AddValue("startUT", seg.startUT.ToString("R", ic));
            segNode.AddValue("endUT", seg.endUT.ToString("R", ic));
            segNode.AddValue("inc", seg.inclination.ToString("R", ic));
            segNode.AddValue("ecc", seg.eccentricity.ToString("R", ic));
            segNode.AddValue("sma", seg.semiMajorAxis.ToString("R", ic));
            segNode.AddValue("lan", seg.longitudeOfAscendingNode.ToString("R", ic));
            segNode.AddValue("argPe", seg.argumentOfPeriapsis.ToString("R", ic));
            segNode.AddValue("mna", seg.meanAnomalyAtEpoch.ToString("R", ic));
            segNode.AddValue("epoch", seg.epoch.ToString("R", ic));
            segNode.AddValue("body", seg.bodyName);
            if (recordingFormatVersion >= RecordingStore.PredictedOrbitSegmentFormatVersion
                || (writeLegacyPredictedFlag && seg.isPredicted))
            {
                segNode.AddValue("isPredicted", seg.isPredicted ? "True" : "False");
            }
            if (TrajectoryMath.HasOrbitalFrameRotation(seg))
            {
                segNode.AddValue("ofrX", seg.orbitalFrameRotation.x.ToString("R", ic));
                segNode.AddValue("ofrY", seg.orbitalFrameRotation.y.ToString("R", ic));
                segNode.AddValue("ofrZ", seg.orbitalFrameRotation.z.ToString("R", ic));
                segNode.AddValue("ofrW", seg.orbitalFrameRotation.w.ToString("R", ic));
            }
            if (TrajectoryMath.IsSpinning(seg))
            {
                segNode.AddValue("avX", seg.angularVelocity.x.ToString("R", ic));
                segNode.AddValue("avY", seg.angularVelocity.y.ToString("R", ic));
                segNode.AddValue("avZ", seg.angularVelocity.z.ToString("R", ic));
            }
        }

        private static OrbitSegment DeserializeOrbitSegment(ConfigNode segNode, NumberStyles ns, CultureInfo ic)
        {
            var seg = new OrbitSegment();

            double.TryParse(segNode.GetValue("startUT"), ns, ic, out seg.startUT);
            double.TryParse(segNode.GetValue("endUT"), ns, ic, out seg.endUT);
            double.TryParse(segNode.GetValue("inc"), ns, ic, out seg.inclination);
            double.TryParse(segNode.GetValue("ecc"), ns, ic, out seg.eccentricity);
            double.TryParse(segNode.GetValue("sma"), ns, ic, out seg.semiMajorAxis);
            double.TryParse(segNode.GetValue("lan"), ns, ic, out seg.longitudeOfAscendingNode);
            double.TryParse(segNode.GetValue("argPe"), ns, ic, out seg.argumentOfPeriapsis);
            double.TryParse(segNode.GetValue("mna"), ns, ic, out seg.meanAnomalyAtEpoch);
            double.TryParse(segNode.GetValue("epoch"), ns, ic, out seg.epoch);
            seg.bodyName = segNode.GetValue("body") ?? "Kerbin";
            bool.TryParse(segNode.GetValue("isPredicted"), out seg.isPredicted);

            float ofrX, ofrY, ofrZ, ofrW;
            if (float.TryParse(segNode.GetValue("ofrX"), ns, ic, out ofrX) &&
                float.TryParse(segNode.GetValue("ofrY"), ns, ic, out ofrY) &&
                float.TryParse(segNode.GetValue("ofrZ"), ns, ic, out ofrZ) &&
                float.TryParse(segNode.GetValue("ofrW"), ns, ic, out ofrW))
            {
                seg.orbitalFrameRotation = new Quaternion(ofrX, ofrY, ofrZ, ofrW);
            }

            float avX, avY, avZ;
            if (float.TryParse(segNode.GetValue("avX"), ns, ic, out avX) &&
                float.TryParse(segNode.GetValue("avY"), ns, ic, out avY) &&
                float.TryParse(segNode.GetValue("avZ"), ns, ic, out avZ))
            {
                seg.angularVelocity = new Vector3(avX, avY, avZ);
            }

            return seg;
        }

        internal static int GetTrajectoryFormatVersion(ConfigNode sourceNode)
        {
            if (sourceNode == null)
                return 0;

            string versionStr = sourceNode.GetValue("version");
            if (string.IsNullOrEmpty(versionStr))
                return 0;

            int version;
            if (!int.TryParse(versionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out version))
            {
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrajectoryFrom: invalid trajectory version '{versionStr}', treating as v0");
                }
                return 0;
            }

            return version;
        }

        private const string SectionAuthoritativeHeaderKey = "sectionAuthoritative";

        private static void EnsureTrajectoryHeader(ConfigNode targetNode, Recording rec)
        {
            if (targetNode == null || rec == null)
                return;

            if (string.IsNullOrEmpty(targetNode.GetValue("version")))
            {
                targetNode.AddValue("version",
                    rec.RecordingFormatVersion.ToString(CultureInfo.InvariantCulture));
            }

            if (string.IsNullOrEmpty(targetNode.GetValue("recordingId")) &&
                !string.IsNullOrEmpty(rec.RecordingId))
            {
                targetNode.AddValue("recordingId", rec.RecordingId);
            }
        }

        private static void SetSectionAuthoritativeHeader(ConfigNode targetNode, bool useSectionAuthoritative)
        {
            if (targetNode == null)
                return;

            string value = useSectionAuthoritative ? "True" : "False";
            if (targetNode.HasValue(SectionAuthoritativeHeaderKey))
                targetNode.SetValue(SectionAuthoritativeHeaderKey, value, true);
            else
                targetNode.AddValue(SectionAuthoritativeHeaderKey, value);
        }

        internal static bool HasTrackSectionPayloadMatchingFlatTrajectory(
            Recording rec,
            bool allowRelativeSections = false)
        {
            return rec != null
                && rec.TrackSections != null
                && rec.TrackSections.Count > 0
                && HasCompleteTrackSectionPayloadForFlatSync(rec.TrackSections, allowRelativeSections)
                && FlatTrajectoryExactlyMatchesTrackSectionPayload(rec);
        }

        internal static bool FlatTrajectoryExtendsTrackSectionPayload(
            Recording rec,
            List<TrackSection> tracks,
            bool allowRelativeSections = false)
        {
            if (rec == null
                || tracks == null
                || tracks.Count == 0
                || !HasCompleteTrackSectionPayloadForFlatSync(tracks, allowRelativeSections))
            {
                return false;
            }

            var rebuiltPoints = new List<TrajectoryPoint>();
            RebuildPointsFromTrackSections(tracks, rebuiltPoints);
            var flatPoints = rec.Points ?? new List<TrajectoryPoint>();
            if (flatPoints.Count < rebuiltPoints.Count)
                return false;
            for (int i = 0; i < rebuiltPoints.Count; i++)
            {
                if (!TrajectoryPointEquals(rebuiltPoints[i], flatPoints[i]))
                    return false;
            }

            var rebuiltOrbitSegments = new List<OrbitSegment>();
            RebuildOrbitSegmentsFromTrackSections(tracks, rebuiltOrbitSegments);
            var flatOrbitSegments = rec.OrbitSegments ?? new List<OrbitSegment>();
            if (flatOrbitSegments.Count < rebuiltOrbitSegments.Count)
                return false;
            for (int i = 0; i < rebuiltOrbitSegments.Count; i++)
            {
                if (!OrbitSegmentEquals(rebuiltOrbitSegments[i], flatOrbitSegments[i]))
                    return false;
            }

            bool pointsExtend = false;
            if (flatPoints.Count > rebuiltPoints.Count)
            {
                int suffixStart = FindSafeTrajectoryPointSuffixStart(flatPoints, rebuiltPoints);
                if (suffixStart < 0)
                    return false;

                var extendedPoints = new List<TrajectoryPoint>(rebuiltPoints);
                AppendTrajectoryPointSuffix(extendedPoints, flatPoints, suffixStart);
                if (!TrajectoryPointListIsMonotonicNonDecreasing(extendedPoints))
                    return false;

                pointsExtend = extendedPoints.Count > rebuiltPoints.Count;
                if (!pointsExtend)
                    return false;
            }

            bool orbitSegmentsExtend = false;
            if (flatOrbitSegments.Count > rebuiltOrbitSegments.Count)
            {
                int suffixStart = FindSafeOrbitSegmentSuffixStart(flatOrbitSegments, rebuiltOrbitSegments);
                if (suffixStart < 0)
                    return false;

                var extendedOrbitSegments = new List<OrbitSegment>(rebuiltOrbitSegments);
                AppendOrbitSegmentSuffix(extendedOrbitSegments, flatOrbitSegments, suffixStart);
                if (!OrbitSegmentListIsMonotonicNonDecreasing(extendedOrbitSegments))
                    return false;

                orbitSegmentsExtend = extendedOrbitSegments.Count > rebuiltOrbitSegments.Count;
                if (!orbitSegmentsExtend)
                    return false;
            }

            return pointsExtend || orbitSegmentsExtend;
        }

        internal static bool TrySyncFlatTrajectoryFromTrackSectionsPreservingFlatTail(
            Recording target,
            Recording source,
            List<TrackSection> tailReferenceTracks,
            bool allowRelativeSections = false)
        {
            if (target == null
                || source == null
                || tailReferenceTracks == null
                || !HasCompleteTrackSectionPayloadForFlatSync(target.TrackSections, allowRelativeSections)
                || !HasCompleteTrackSectionPayloadForFlatSync(tailReferenceTracks, allowRelativeSections))
            {
                return false;
            }

            var referencePoints = new List<TrajectoryPoint>();
            RebuildPointsFromTrackSections(tailReferenceTracks, referencePoints);
            var sourcePoints = source.Points ?? new List<TrajectoryPoint>();
            if (sourcePoints.Count < referencePoints.Count)
                return false;
            for (int i = 0; i < referencePoints.Count; i++)
            {
                if (!TrajectoryPointEquals(referencePoints[i], sourcePoints[i]))
                    return false;
            }

            int pointSuffixStart = -1;
            if (sourcePoints.Count > referencePoints.Count)
            {
                pointSuffixStart = FindSafeTrajectoryPointSuffixStart(sourcePoints, referencePoints);
                if (pointSuffixStart < 0)
                    return false;
            }

            var referenceOrbitSegments = new List<OrbitSegment>();
            RebuildOrbitSegmentsFromTrackSections(tailReferenceTracks, referenceOrbitSegments);
            var sourceOrbitSegments = source.OrbitSegments ?? new List<OrbitSegment>();
            if (sourceOrbitSegments.Count < referenceOrbitSegments.Count)
                return false;
            for (int i = 0; i < referenceOrbitSegments.Count; i++)
            {
                if (!OrbitSegmentEquals(referenceOrbitSegments[i], sourceOrbitSegments[i]))
                    return false;
            }

            int orbitSuffixStart = -1;
            if (sourceOrbitSegments.Count > referenceOrbitSegments.Count)
            {
                orbitSuffixStart = FindSafeOrbitSegmentSuffixStart(
                    sourceOrbitSegments, referenceOrbitSegments);
                if (orbitSuffixStart < 0)
                    return false;
            }

            if (pointSuffixStart < 0 && orbitSuffixStart < 0)
                return false;

            var healedPoints = new List<TrajectoryPoint>();
            RebuildPointsFromTrackSections(target.TrackSections, healedPoints);
            if (pointSuffixStart >= 0)
            {
                AppendTrajectoryPointSuffix(healedPoints, sourcePoints, pointSuffixStart);
                if (!TrajectoryPointListIsMonotonicNonDecreasing(healedPoints))
                    return false;
            }

            var healedOrbitSegments = new List<OrbitSegment>();
            RebuildOrbitSegmentsFromTrackSections(target.TrackSections, healedOrbitSegments);
            if (orbitSuffixStart >= 0)
            {
                AppendOrbitSegmentSuffix(healedOrbitSegments, sourceOrbitSegments, orbitSuffixStart);
                if (!OrbitSegmentListIsMonotonicNonDecreasing(healedOrbitSegments))
                    return false;
            }

            target.Points = healedPoints;
            target.OrbitSegments = healedOrbitSegments;
            target.CachedStats = null;
            target.CachedStatsPointCount = 0;
            return true;
        }

        internal static bool ShouldWriteSectionAuthoritativeTrajectory(Recording rec)
        {
            return rec != null
                && rec.RecordingFormatVersion >= 1
                && HasTrackSectionPayloadMatchingFlatTrajectory(rec, allowRelativeSections: true);
        }

        private static bool ShouldReadSectionAuthoritativeTrajectory(ConfigNode sourceNode, int formatVersion)
        {
            if (formatVersion < 1
                || sourceNode == null
                || sourceNode.GetNodes("TRACK_SECTION").Length == 0)
            {
                return false;
            }

            string explicitHeader = sourceNode.GetValue(SectionAuthoritativeHeaderKey);
            bool useSectionAuthoritative;
            if (!string.IsNullOrEmpty(explicitHeader)
                && bool.TryParse(explicitHeader, out useSectionAuthoritative))
            {
                return useSectionAuthoritative;
            }

            return sourceNode.GetNodes("POINT").Length == 0
                && sourceNode.GetNodes("ORBIT_SEGMENT").Length == 0;
        }

        internal static int RebuildPointsFromTrackSections(List<TrackSection> tracks, List<TrajectoryPoint> points)
        {
            points.Clear();
            if (tracks == null || tracks.Count == 0)
                return 0;

            int dedupedBoundaryCopies = 0;
            for (int t = 0; t < tracks.Count; t++)
            {
                // OrbitalCheckpoint frames are derived samples used for dense playback;
                // they intentionally participate in the flat Points compatibility view.
                if (tracks[t].frames == null)
                    continue;

                for (int i = 0; i < tracks[t].frames.Count; i++)
                {
                    var pt = tracks[t].frames[i];
                    if (points.Count > 0 && TrajectoryPointEquals(points[points.Count - 1], pt))
                    {
                        dedupedBoundaryCopies++;
                        continue;
                    }

                    points.Add(pt);
                }
            }

            return dedupedBoundaryCopies;
        }

        internal static int AppendPointsFromTrackSections(List<TrackSection> tracks, List<TrajectoryPoint> points)
        {
            if (tracks == null || tracks.Count == 0 || points == null)
                return 0;

            var rebuiltPoints = new List<TrajectoryPoint>();
            int dedupedBoundaryCopies = RebuildPointsFromTrackSections(tracks, rebuiltPoints);
            int overlapCopies = FindTrajectoryPointSuffixPrefixOverlap(points, rebuiltPoints);

            // Bug #419 defense-in-depth: monotonicity guard at the flush/stitch boundary.
            // The sampler-level guard in BackgroundRecorder.ApplyTrajectoryPointToRecording
            // is the primary defense, but track-section frames can also reach this flush
            // via other paths (in-memory state rebuilt from disk, legacy sections, test
            // injection). If any rebuilt point's UT regresses below the last existing
            // point's UT, skip it here so save-load never re-materializes a #419 corruption.
            int nonMonotonicSkipped = 0;
            for (int i = overlapCopies; i < rebuiltPoints.Count; i++)
            {
                var incoming = rebuiltPoints[i];
                if (points.Count > 0 && incoming.ut < points[points.Count - 1].ut)
                {
                    nonMonotonicSkipped++;
                    continue;
                }
                points.Add(incoming);
            }
            if (nonMonotonicSkipped > 0)
            {
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                double lastUt = points.Count > 0 ? points[points.Count - 1].ut : double.NaN;
                ParsekLog.Warn("RecordingStore",
                    $"AppendPointsFromTrackSections: skipped {nonMonotonicSkipped} non-monotonic frame(s) " +
                    $"at flush stitch (lastPointUT={lastUt.ToString("R", ic)}, " +
                    $"rebuiltCount={rebuiltPoints.Count}, overlapCopies={overlapCopies}) — #419");
            }

            return dedupedBoundaryCopies + overlapCopies;
        }

        internal static bool ContainsRelativeTrackSections(List<TrackSection> tracks)
        {
            if (tracks == null)
                return false;

            for (int i = 0; i < tracks.Count; i++)
            {
                if (tracks[i].referenceFrame == ReferenceFrame.Relative)
                    return true;
            }

            return false;
        }

        internal static bool HasCompleteTrackSectionPayloadForFlatSync(
            List<TrackSection> tracks,
            bool allowRelativeSections = false)
        {
            if (tracks == null || tracks.Count == 0)
                return false;

            bool sawPayload = false;
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                switch (track.referenceFrame)
                {
                    case ReferenceFrame.Absolute:
                        if (track.frames == null || track.frames.Count == 0)
                            return false;
                        sawPayload = true;
                        break;

                    case ReferenceFrame.Relative:
                        if (!allowRelativeSections)
                            return false;
                        if (track.frames == null || track.frames.Count == 0)
                            return false;
                        sawPayload = true;
                        break;

                    case ReferenceFrame.OrbitalCheckpoint:
                        if (track.checkpoints == null || track.checkpoints.Count == 0)
                            return false;
                        sawPayload = true;
                        break;
                }
            }

            return sawPayload;
        }

        internal static bool TrySyncFlatTrajectoryFromTrackSections(
            Recording rec,
            bool allowRelativeSections = false)
        {
            if (rec == null
                || !HasCompleteTrackSectionPayloadForFlatSync(rec.TrackSections, allowRelativeSections))
            {
                return false;
            }

            RebuildPointsFromTrackSections(rec.TrackSections, rec.Points);
            RebuildOrbitSegmentsFromTrackSections(rec.TrackSections, rec.OrbitSegments);
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
            return true;
        }

        internal static bool TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
            Recording rec,
            bool allowRelativeSections = false)
        {
            if (rec == null
                || !HasCompleteTrackSectionPayloadForFlatSync(rec.TrackSections, allowRelativeSections))
            {
                return false;
            }

            bool healedPoints = TryHealMalformedFlatFallbackPointsFromTrackSections(rec);
            bool healedOrbitSegments = TryHealMalformedFlatFallbackOrbitSegmentsFromTrackSections(rec);
            if (!healedPoints && !healedOrbitSegments)
                return false;

            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
            rec.MarkFilesDirty();
            return true;
        }

        // #378: warn-threshold for the exact-match rebuild-and-compare check.
        // 5ms is conservative given the check runs once per recording per save.
        private const double FlatTrajectoryExactMatchWarnMs = 5.0;

        private static bool FlatTrajectoryExactlyMatchesTrackSectionPayload(Recording rec)
        {
            if (rec == null)
                return false;

            var sw = Stopwatch.StartNew();
            var rebuiltPoints = new List<TrajectoryPoint>();
            RebuildPointsFromTrackSections(rec.TrackSections, rebuiltPoints);
            bool pointsEqual = TrajectoryPointListsEqual(rebuiltPoints, rec.Points);

            bool orbitSegmentsEqual = false;
            List<OrbitSegment> rebuiltOrbitSegments = null;
            if (pointsEqual)
            {
                rebuiltOrbitSegments = new List<OrbitSegment>();
                RebuildOrbitSegmentsFromTrackSections(rec.TrackSections, rebuiltOrbitSegments);
                orbitSegmentsEqual = OrbitSegmentListsEqual(rebuiltOrbitSegments, rec.OrbitSegments);
            }

            sw.Stop();
            double elapsedMs = sw.Elapsed.TotalMilliseconds;
            if (elapsedMs >= FlatTrajectoryExactMatchWarnMs && !RecordingStore.SuppressLogging)
            {
                int pointCount = rec.Points != null ? rec.Points.Count : 0;
                int orbitCount = rec.OrbitSegments != null ? rec.OrbitSegments.Count : 0;
                int sectionCount = rec.TrackSections != null ? rec.TrackSections.Count : 0;
                ParsekLog.WarnRateLimited(
                    "RecordingStore",
                    "flat-section-exact-match-slow:" + rec.RecordingId,
                    $"FlatTrajectoryExactlyMatchesTrackSectionPayload slow: " +
                    $"recId={rec.RecordingId} points={pointCount} orbits={orbitCount} " +
                    $"sections={sectionCount} elapsedMs={elapsedMs.ToString("F2", CultureInfo.InvariantCulture)}");
            }

            return pointsEqual && orbitSegmentsEqual;
        }

        private static bool TryHealMalformedFlatFallbackPointsFromTrackSections(Recording rec)
        {
            if (rec.Points == null || rec.Points.Count == 0)
                return false;

            var rebuiltPoints = new List<TrajectoryPoint>();
            RebuildPointsFromTrackSections(rec.TrackSections, rebuiltPoints);
            if (rebuiltPoints.Count == 0 || rec.Points.Count < rebuiltPoints.Count)
                return false;

            for (int i = 0; i < rebuiltPoints.Count; i++)
            {
                if (!TrajectoryPointEquals(rebuiltPoints[i], rec.Points[i]))
                    return false;
            }

            int suffixStart = FindSafeTrajectoryPointSuffixStart(rec.Points, rebuiltPoints);
            if (suffixStart < 0)
                return false;

            var healedPoints = new List<TrajectoryPoint>(rebuiltPoints);
            AppendTrajectoryPointSuffix(healedPoints, rec.Points, suffixStart);
            if (!TrajectoryPointListIsMonotonicNonDecreasing(healedPoints)
                || TrajectoryPointListsEqual(healedPoints, rec.Points))
            {
                return false;
            }

            rec.Points = healedPoints;
            return true;
        }

        private static bool TryHealMalformedFlatFallbackOrbitSegmentsFromTrackSections(Recording rec)
        {
            if (rec.OrbitSegments == null || rec.OrbitSegments.Count == 0)
                return false;

            var rebuiltOrbitSegments = new List<OrbitSegment>();
            RebuildOrbitSegmentsFromTrackSections(rec.TrackSections, rebuiltOrbitSegments);
            if (rebuiltOrbitSegments.Count == 0 || rec.OrbitSegments.Count < rebuiltOrbitSegments.Count)
                return false;

            for (int i = 0; i < rebuiltOrbitSegments.Count; i++)
            {
                if (!OrbitSegmentEquals(rebuiltOrbitSegments[i], rec.OrbitSegments[i]))
                    return false;
            }

            int suffixStart = FindSafeOrbitSegmentSuffixStart(rec.OrbitSegments, rebuiltOrbitSegments);
            if (suffixStart < 0)
                return false;

            var healedOrbitSegments = new List<OrbitSegment>(rebuiltOrbitSegments);
            AppendOrbitSegmentSuffix(healedOrbitSegments, rec.OrbitSegments, suffixStart);
            if (!OrbitSegmentListIsMonotonicNonDecreasing(healedOrbitSegments)
                || OrbitSegmentListsEqual(healedOrbitSegments, rec.OrbitSegments))
            {
                return false;
            }

            rec.OrbitSegments = healedOrbitSegments;
            return true;
        }

        private static bool TrajectoryPointListsEqual(List<TrajectoryPoint> a, List<TrajectoryPoint> b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (!TrajectoryPointEquals(a[i], b[i]))
                    return false;
            }

            return true;
        }

        private static bool TrajectoryPointListIsMonotonicNonDecreasing(
            List<TrajectoryPoint> points,
            int startIndex = 1)
        {
            if (points == null)
                return true;

            int firstIndexToCheck = Math.Max(1, startIndex);
            for (int i = firstIndexToCheck; i < points.Count; i++)
            {
                if (points[i].ut < points[i - 1].ut)
                    return false;
            }

            return true;
        }

        private static bool OrbitSegmentListsEqual(List<OrbitSegment> a, List<OrbitSegment> b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null || a.Count != b.Count)
                return false;

            for (int i = 0; i < a.Count; i++)
            {
                if (!OrbitSegmentEquals(a[i], b[i]))
                    return false;
            }

            return true;
        }

        private static bool OrbitSegmentListIsMonotonicNonDecreasing(
            List<OrbitSegment> orbitSegments,
            int startIndex = 1)
        {
            if (orbitSegments == null)
                return true;

            int firstIndexToCheck = Math.Max(1, startIndex);
            for (int i = firstIndexToCheck; i < orbitSegments.Count; i++)
            {
                if (orbitSegments[i].startUT < orbitSegments[i - 1].startUT)
                    return false;
            }

            return true;
        }

        internal static int RebuildOrbitSegmentsFromTrackSections(List<TrackSection> tracks, List<OrbitSegment> orbitSegments)
        {
            orbitSegments.Clear();
            if (tracks == null || tracks.Count == 0)
                return 0;

            int dedupedCopies = 0;
            for (int t = 0; t < tracks.Count; t++)
            {
                if (tracks[t].referenceFrame != ReferenceFrame.OrbitalCheckpoint || tracks[t].checkpoints == null)
                    continue;

                for (int i = 0; i < tracks[t].checkpoints.Count; i++)
                {
                    var seg = tracks[t].checkpoints[i];
                    if (orbitSegments.Count > 0 && OrbitSegmentEquals(orbitSegments[orbitSegments.Count - 1], seg))
                    {
                        dedupedCopies++;
                        continue;
                    }

                    orbitSegments.Add(seg);
                }
            }

            return dedupedCopies;
        }

        internal static int AppendOrbitSegmentsFromTrackSections(List<TrackSection> tracks, List<OrbitSegment> orbitSegments)
        {
            if (tracks == null || tracks.Count == 0 || orbitSegments == null)
                return 0;

            var rebuiltOrbitSegments = new List<OrbitSegment>();
            int dedupedCopies = RebuildOrbitSegmentsFromTrackSections(tracks, rebuiltOrbitSegments);
            int overlapCopies = FindOrbitSegmentSuffixPrefixOverlap(orbitSegments, rebuiltOrbitSegments);
            for (int i = overlapCopies; i < rebuiltOrbitSegments.Count; i++)
                orbitSegments.Add(rebuiltOrbitSegments[i]);

            return dedupedCopies + overlapCopies;
        }

        private static int FindTrajectoryPointSuffixPrefixOverlap(
            List<TrajectoryPoint> existing,
            List<TrajectoryPoint> incoming)
        {
            if (existing == null || incoming == null || existing.Count == 0 || incoming.Count == 0)
                return 0;

            int maxOverlap = Math.Min(existing.Count, incoming.Count);
            for (int overlap = maxOverlap; overlap > 0; overlap--)
            {
                bool matches = true;
                int existingStart = existing.Count - overlap;
                for (int i = 0; i < overlap; i++)
                {
                    if (!TrajectoryPointEquals(existing[existingStart + i], incoming[i]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return overlap;
            }

            return 0;
        }

        private static int FindSafeTrajectoryPointSuffixStart(
            List<TrajectoryPoint> flatPoints,
            List<TrajectoryPoint> rebuiltPoints)
        {
            if (flatPoints == null
                || rebuiltPoints == null
                || rebuiltPoints.Count == 0
                || flatPoints.Count < rebuiltPoints.Count)
            {
                return -1;
            }

            double minUt = rebuiltPoints[rebuiltPoints.Count - 1].ut;
            for (int start = rebuiltPoints.Count; start < flatPoints.Count; start++)
            {
                if (flatPoints[start].ut < minUt)
                    continue;

                if (flatPoints[start].ut == minUt
                    && !TrajectoryPointEquals(flatPoints[start], rebuiltPoints[rebuiltPoints.Count - 1]))
                {
                    continue;
                }

                if (!TrajectoryPointSuffixIsMonotonicNonDecreasing(flatPoints, start))
                    continue;

                return start;
            }

            return -1;
        }

        private static int FindOrbitSegmentSuffixPrefixOverlap(
            List<OrbitSegment> existing,
            List<OrbitSegment> incoming)
        {
            if (existing == null || incoming == null || existing.Count == 0 || incoming.Count == 0)
                return 0;

            int maxOverlap = Math.Min(existing.Count, incoming.Count);
            for (int overlap = maxOverlap; overlap > 0; overlap--)
            {
                bool matches = true;
                int existingStart = existing.Count - overlap;
                for (int i = 0; i < overlap; i++)
                {
                    if (!OrbitSegmentEquals(existing[existingStart + i], incoming[i]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return overlap;
            }

            return 0;
        }

        private static int FindSafeOrbitSegmentSuffixStart(
            List<OrbitSegment> flatOrbitSegments,
            List<OrbitSegment> rebuiltOrbitSegments)
        {
            if (flatOrbitSegments == null
                || rebuiltOrbitSegments == null
                || rebuiltOrbitSegments.Count == 0
                || flatOrbitSegments.Count < rebuiltOrbitSegments.Count)
            {
                return -1;
            }

            double minStartUT = rebuiltOrbitSegments[rebuiltOrbitSegments.Count - 1].startUT;
            for (int start = rebuiltOrbitSegments.Count; start < flatOrbitSegments.Count; start++)
            {
                if (flatOrbitSegments[start].startUT < minStartUT)
                    continue;

                if (start == rebuiltOrbitSegments.Count)
                {
                    if (!OrbitSegmentSuffixIsMonotonicNonDecreasing(flatOrbitSegments, start))
                        continue;
                    return start;
                }

                if (flatOrbitSegments[start].startUT == minStartUT
                    && !OrbitSegmentEquals(flatOrbitSegments[start], rebuiltOrbitSegments[rebuiltOrbitSegments.Count - 1]))
                {
                    continue;
                }

                if (!OrbitSegmentSuffixIsMonotonicNonDecreasing(flatOrbitSegments, start))
                    continue;

                return start;
            }

            return -1;
        }

        private static bool TrajectoryPointSuffixIsMonotonicNonDecreasing(
            List<TrajectoryPoint> points,
            int startIndex)
        {
            if (points == null || startIndex >= points.Count)
                return true;

            double previousUt = points[startIndex].ut;
            for (int i = startIndex + 1; i < points.Count; i++)
            {
                if (points[i].ut < previousUt)
                    return false;
                previousUt = points[i].ut;
            }

            return true;
        }

        private static bool OrbitSegmentSuffixIsMonotonicNonDecreasing(
            List<OrbitSegment> orbitSegments,
            int startIndex)
        {
            if (orbitSegments == null || startIndex >= orbitSegments.Count)
                return true;

            double previousStartUT = orbitSegments[startIndex].startUT;
            for (int i = startIndex + 1; i < orbitSegments.Count; i++)
            {
                if (orbitSegments[i].startUT < previousStartUT)
                    return false;
                previousStartUT = orbitSegments[i].startUT;
            }

            return true;
        }

        private static void AppendTrajectoryPointSuffix(
            List<TrajectoryPoint> target,
            List<TrajectoryPoint> source,
            int startIndex)
        {
            for (int i = startIndex; i < source.Count; i++)
            {
                if (target.Count > 0 && TrajectoryPointEquals(target[target.Count - 1], source[i]))
                    continue;
                target.Add(source[i]);
            }
        }

        private static void AppendOrbitSegmentSuffix(
            List<OrbitSegment> target,
            List<OrbitSegment> source,
            int startIndex)
        {
            for (int i = startIndex; i < source.Count; i++)
            {
                if (target.Count > 0 && OrbitSegmentEquals(target[target.Count - 1], source[i]))
                    continue;
                target.Add(source[i]);
            }
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

        private static bool OrbitSegmentEquals(OrbitSegment a, OrbitSegment b)
        {
            return a.startUT == b.startUT
                && a.endUT == b.endUT
                && a.inclination == b.inclination
                && a.eccentricity == b.eccentricity
                && a.semiMajorAxis == b.semiMajorAxis
                && a.longitudeOfAscendingNode == b.longitudeOfAscendingNode
                && a.argumentOfPeriapsis == b.argumentOfPeriapsis
                && a.meanAnomalyAtEpoch == b.meanAnomalyAtEpoch
                && a.epoch == b.epoch
                && a.bodyName == b.bodyName
                && a.isPredicted == b.isPredicted
                && a.orbitalFrameRotation.x == b.orbitalFrameRotation.x
                && a.orbitalFrameRotation.y == b.orbitalFrameRotation.y
                && a.orbitalFrameRotation.z == b.orbitalFrameRotation.z
                && a.orbitalFrameRotation.w == b.orbitalFrameRotation.w
                && a.angularVelocity.x == b.angularVelocity.x
                && a.angularVelocity.y == b.angularVelocity.y
                && a.angularVelocity.z == b.angularVelocity.z;
        }

        internal static void SerializeTrajectoryInto(ConfigNode targetNode, Recording rec)
        {
            var ic = CultureInfo.InvariantCulture;
            EnsureTrajectoryHeader(targetNode, rec);
            bool useSectionAuthoritative = ShouldWriteSectionAuthoritativeTrajectory(rec);
            if (rec != null && rec.RecordingFormatVersion >= 1)
                SetSectionAuthoritativeHeader(targetNode, useSectionAuthoritative);

            if (!useSectionAuthoritative)
            {
                for (int i = 0; i < rec.Points.Count; i++)
                    SerializePoint(targetNode, rec.Points[i], ic);

                for (int s = 0; s < rec.OrbitSegments.Count; s++)
                    SerializeOrbitSegment(
                        targetNode,
                        rec.OrbitSegments[s],
                        ic,
                        rec.RecordingFormatVersion,
                        writeLegacyPredictedFlag: true);
            }
            else
            {
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Verbose("RecordingStore",
                        $"SerializeTrajectoryInto: recording={rec.RecordingId} version={rec.RecordingFormatVersion} " +
                        $"using section-authoritative path sections={rec.TrackSections.Count} " +
                        $"skippedTopLevelPoints={rec.Points.Count} skippedTopLevelOrbitSegments={rec.OrbitSegments.Count}");
                }
            }

            for (int pe = 0; pe < rec.PartEvents.Count; pe++)
            {
                var evt = rec.PartEvents[pe];
                ConfigNode evtNode = targetNode.AddNode("PART_EVENT");
                evtNode.AddValue("ut", evt.ut.ToString("R", ic));
                evtNode.AddValue("pid", evt.partPersistentId.ToString(ic));
                evtNode.AddValue("type", ((int)evt.eventType).ToString(ic));
                evtNode.AddValue("part", evt.partName ?? "");
                evtNode.AddValue("value", evt.value.ToString("R", ic));
                evtNode.AddValue("midx", evt.moduleIndex.ToString(ic));
            }

            for (int fe = 0; fe < rec.FlagEvents.Count; fe++)
            {
                var evt = rec.FlagEvents[fe];
                ConfigNode feNode = targetNode.AddNode("FLAG_EVENT");
                feNode.AddValue("ut", evt.ut.ToString("R", ic));
                feNode.AddValue("name", evt.flagSiteName ?? "");
                feNode.AddValue("placedBy", evt.placedBy ?? "");
                feNode.AddValue("plaqueText", evt.plaqueText ?? "");
                feNode.AddValue("flagURL", evt.flagURL ?? "");
                feNode.AddValue("lat", evt.latitude.ToString("R", ic));
                feNode.AddValue("lon", evt.longitude.ToString("R", ic));
                feNode.AddValue("alt", evt.altitude.ToString("R", ic));
                feNode.AddValue("rotX", evt.rotX.ToString("R", ic));
                feNode.AddValue("rotY", evt.rotY.ToString("R", ic));
                feNode.AddValue("rotZ", evt.rotZ.ToString("R", ic));
                feNode.AddValue("rotW", evt.rotW.ToString("R", ic));
                feNode.AddValue("body", evt.bodyName ?? "Kerbin");
            }

            SerializeSegmentEvents(targetNode, rec.SegmentEvents);

            // Serialize track sections (new recording system)
            if (rec.TrackSections != null && rec.TrackSections.Count > 0)
                SerializeTrackSections(targetNode, rec.TrackSections, rec.RecordingFormatVersion);

            if (!useSectionAuthoritative && rec.RecordingFormatVersion >= 1)
            {
                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Verbose("RecordingStore",
                        $"SerializeTrajectoryInto: recording={rec.RecordingId} version={rec.RecordingFormatVersion} " +
                        $"used flat fallback path points={rec.Points.Count} orbitSegments={rec.OrbitSegments.Count} " +
                        $"trackSections={rec.TrackSections?.Count ?? 0}");
                }
            }
        }

        internal static void DeserializeTrajectoryFrom(ConfigNode sourceNode, Recording rec)
        {
            int formatVersion = GetTrajectoryFormatVersion(sourceNode);
            bool useSectionAuthoritative = ShouldReadSectionAuthoritativeTrajectory(sourceNode, formatVersion);

            if (useSectionAuthoritative)
            {
                rec.TrackSections.Clear();
                DeserializeTrackSections(sourceNode, rec.TrackSections);

                int dedupedPointCopies = RebuildPointsFromTrackSections(rec.TrackSections, rec.Points);
                int dedupedOrbitCopies = RebuildOrbitSegmentsFromTrackSections(rec.TrackSections, rec.OrbitSegments);

                if (!RecordingStore.SuppressLogging)
                {
                    ParsekLog.Verbose("RecordingStore",
                        $"DeserializeTrajectoryFrom: recording={rec.RecordingId} version={formatVersion} " +
                        $"using section-authoritative path sections={rec.TrackSections.Count} rebuiltPoints={rec.Points.Count} " +
                        $"dedupedPointCopies={dedupedPointCopies} rebuiltOrbitSegments={rec.OrbitSegments.Count} " +
                        $"dedupedOrbitCopies={dedupedOrbitCopies}");
                }
            }
            else
            {
                DeserializePoints(sourceNode, rec);
                DeserializeOrbitSegments(sourceNode, rec);
                DeserializeTrackSections(sourceNode, rec.TrackSections);

                bool healedMalformedFlatFallback = false;
                if (formatVersion >= 1 && rec.TrackSections.Count > 0)
                {
                    healedMalformedFlatFallback = TryHealMalformedFlatFallbackTrajectoryFromTrackSections(
                        rec, allowRelativeSections: true);
                    if (healedMalformedFlatFallback && !RecordingStore.SuppressLogging)
                    {
                        ParsekLog.Verbose("RecordingStore",
                            $"DeserializeTrajectoryFrom: recording={rec.RecordingId} version={formatVersion} " +
                            $"healed malformed flat fallback using track-section prefix " +
                            $"points={rec.Points.Count} orbitSegments={rec.OrbitSegments.Count} " +
                            $"trackSections={rec.TrackSections.Count}");
                    }
                }

                if (formatVersion >= 1)
                {
                    if (!RecordingStore.SuppressLogging)
                    {
                        ParsekLog.Verbose("RecordingStore",
                            $"DeserializeTrajectoryFrom: recording={rec.RecordingId} version={formatVersion} " +
                            $"used flat fallback path points={rec.Points.Count} orbitSegments={rec.OrbitSegments.Count} " +
                            $"trackSections={rec.TrackSections.Count}");
                    }
                }
            }

            DeserializePartEvents(sourceNode, rec);
            DeserializeFlagEvents(sourceNode, rec);
            DeserializeSegmentEvents(sourceNode, rec.SegmentEvents);
        }

        /// <summary>
        /// Deserializes POINT nodes from a trajectory ConfigNode into the recording's Points list.
        /// </summary>
        internal static void DeserializePoints(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] ptNodes = sourceNode.GetNodes("POINT");
            int parseFailCount = 0;
            for (int i = 0; i < ptNodes.Length; i++)
            {
                var ptNode = ptNodes[i];
                bool utOk = double.TryParse(ptNode.GetValue("ut"), ns, ic, out _);
                if (!utOk)
                    parseFailCount++;

                rec.Points.Add(DeserializePoint(ptNode, ns, ic));
            }
            if (parseFailCount > 0)
                RecordingStore.Log($"[Parsek] WARNING: {parseFailCount}/{ptNodes.Length} trajectory points had unparseable UT in recording {rec.RecordingId}");
        }

        /// <summary>
        /// Deserializes ORBIT_SEGMENT nodes from a trajectory ConfigNode into the recording's OrbitSegments list.
        /// </summary>
        internal static void DeserializeOrbitSegments(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] segNodes = sourceNode.GetNodes("ORBIT_SEGMENT");
            for (int s = 0; s < segNodes.Length; s++)
                rec.OrbitSegments.Add(DeserializeOrbitSegment(segNodes[s], ns, ic));

        }

        /// <summary>
        /// Deserializes PART_EVENT nodes from a trajectory ConfigNode into the recording's PartEvents list.
        /// </summary>
        internal static void DeserializePartEvents(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] peNodes = sourceNode.GetNodes("PART_EVENT");
            for (int pe = 0; pe < peNodes.Length; pe++)
            {
                var peNode = peNodes[pe];
                var evt = new PartEvent();

                double.TryParse(peNode.GetValue("ut"), ns, ic, out evt.ut);
                uint pid;
                if (uint.TryParse(peNode.GetValue("pid"), NumberStyles.Integer, ic, out pid))
                    evt.partPersistentId = pid;
                int typeInt;
                if (int.TryParse(peNode.GetValue("type"), NumberStyles.Integer, ic, out typeInt))
                {
                    if (Enum.IsDefined(typeof(PartEventType), typeInt))
                        evt.eventType = (PartEventType)typeInt;
                    else
                    {
                        RecordingStore.Log($"[Recording] Skipping unknown PartEvent type={typeInt} in recording {rec.RecordingId}");
                        continue;
                    }
                }
                evt.partName = peNode.GetValue("part") ?? "";

                float val;
                if (float.TryParse(peNode.GetValue("value"), ns, ic, out val))
                    evt.value = val;
                int midx;
                if (int.TryParse(peNode.GetValue("midx"), NumberStyles.Integer, ic, out midx))
                    evt.moduleIndex = midx;

                rec.PartEvents.Add(evt);
            }
        }

        /// <summary>
        /// Deserializes FLAG_EVENT nodes from a trajectory ConfigNode into the recording's FlagEvents list.
        /// </summary>
        internal static void DeserializeFlagEvents(ConfigNode sourceNode, Recording rec)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] feNodes = sourceNode.GetNodes("FLAG_EVENT");
            for (int fe = 0; fe < feNodes.Length; fe++)
            {
                var feNode = feNodes[fe];
                var evt = new FlagEvent();

                double.TryParse(feNode.GetValue("ut"), ns, ic, out evt.ut);
                evt.flagSiteName = feNode.GetValue("name") ?? "";
                evt.placedBy = feNode.GetValue("placedBy") ?? "";
                evt.plaqueText = feNode.GetValue("plaqueText") ?? "";
                evt.flagURL = feNode.GetValue("flagURL") ?? "";
                double.TryParse(feNode.GetValue("lat"), ns, ic, out evt.latitude);
                double.TryParse(feNode.GetValue("lon"), ns, ic, out evt.longitude);
                double.TryParse(feNode.GetValue("alt"), ns, ic, out evt.altitude);
                float.TryParse(feNode.GetValue("rotX"), ns, ic, out evt.rotX);
                float.TryParse(feNode.GetValue("rotY"), ns, ic, out evt.rotY);
                float.TryParse(feNode.GetValue("rotZ"), ns, ic, out evt.rotZ);
                float.TryParse(feNode.GetValue("rotW"), ns, ic, out evt.rotW);
                evt.bodyName = feNode.GetValue("body") ?? "Kerbin";

                rec.FlagEvents.Add(evt);
            }

        }

        /// <summary>
        /// Serializes SegmentEvent entries as SEGMENT_EVENT child nodes.
        /// </summary>
        internal static void SerializeSegmentEvents(ConfigNode parent, List<SegmentEvent> events)
        {
            if (events == null || events.Count == 0)
            {
                return;
            }

            var ic = CultureInfo.InvariantCulture;
            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                ConfigNode evtNode = parent.AddNode("SEGMENT_EVENT");
                evtNode.AddValue("ut", evt.ut.ToString("R", ic));
                evtNode.AddValue("type", ((int)evt.type).ToString(ic));
                if (!string.IsNullOrEmpty(evt.details))
                    evtNode.AddValue("details", evt.details);
            }

        }

        /// <summary>
        /// Deserializes SEGMENT_EVENT child nodes into the given list.
        /// Unknown type values are skipped with a warning log.
        /// Missing ut values cause the event to be skipped.
        /// </summary>
        internal static void DeserializeSegmentEvents(ConfigNode parent, List<SegmentEvent> events)
        {
            ConfigNode[] seNodes = parent.GetNodes("SEGMENT_EVENT");
            if (seNodes.Length == 0)
                return;

            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;
            int skipped = 0;

            for (int i = 0; i < seNodes.Length; i++)
            {
                var seNode = seNodes[i];

                double ut;
                if (!double.TryParse(seNode.GetValue("ut"), ns, ic, out ut))
                {
                    RecordingStore.Log("[Recording] WARNING: Skipping SEGMENT_EVENT with missing or unparseable ut");
                    skipped++;
                    continue;
                }

                int typeInt;
                if (!int.TryParse(seNode.GetValue("type"), NumberStyles.Integer, ic, out typeInt))
                {
                    RecordingStore.Log("[Recording] WARNING: Skipping SEGMENT_EVENT with unparseable type");
                    skipped++;
                    continue;
                }
                if (!Enum.IsDefined(typeof(SegmentEventType), typeInt))
                {
                    RecordingStore.Log($"[Recording] WARNING: Skipping SEGMENT_EVENT with unknown type={typeInt}");
                    skipped++;
                    continue;
                }

                var evt = new SegmentEvent
                {
                    ut = ut,
                    type = (SegmentEventType)typeInt,
                    details = seNode.GetValue("details")
                };
                events.Add(evt);
            }

            if (skipped > 0)
                ParsekLog.Warn("RecordingStore", $"DeserializeSegmentEvents: {skipped}/{seNodes.Length} events skipped");
        }

        /// <summary>
        /// Serializes TrackSection list into TRACK_SECTION ConfigNodes under the given parent.
        /// Each section carries its own environment classification, reference frame, and nested
        /// trajectory data (POINT nodes for Absolute/Relative and densified OrbitalCheckpoint
        /// frames, ORBIT_SEGMENT nodes for OrbitalCheckpoint source elements).
        /// </summary>
        internal static void SerializeTrackSections(
            ConfigNode parent,
            List<TrackSection> tracks,
            int recordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return;
            }

            var ic = CultureInfo.InvariantCulture;

            for (int t = 0; t < tracks.Count; t++)
            {
                var track = tracks[t];
                ConfigNode tsNode = parent.AddNode("TRACK_SECTION");

                tsNode.AddValue("env", ((int)track.environment).ToString(ic));
                tsNode.AddValue("ref", ((int)track.referenceFrame).ToString(ic));
                tsNode.AddValue("startUT", track.startUT.ToString("R", ic));
                tsNode.AddValue("endUT", track.endUT.ToString("R", ic));
                tsNode.AddValue("sampleRate", track.sampleRateHz.ToString("R", ic));

                // Source: sparse — only write when not Active (default)
                if (track.source != TrackSectionSource.Active)
                {
                    tsNode.AddValue("src", ((int)track.source).ToString(ic));
                    ParsekLog.Verbose("RecordingStore",
                        $"SerializeTrackSections: [{t}] writing source={track.source} (non-default)");
                }

                // Boundary discontinuity: sparse — only write when > 0
                if (track.boundaryDiscontinuityMeters > 0f)
                    tsNode.AddValue("bdisc", track.boundaryDiscontinuityMeters.ToString("R", ic));

                // Altitude range: sparse — only write when tracked (non-NaN)
                if (!float.IsNaN(track.minAltitude))
                    tsNode.AddValue("minAlt", track.minAltitude.ToString("R", ic));
                if (!float.IsNaN(track.maxAltitude))
                    tsNode.AddValue("maxAlt", track.maxAltitude.ToString("R", ic));

                if (track.anchorVesselId != 0)
                    tsNode.AddValue("anchorPid", track.anchorVesselId.ToString(ic));

                // Producer-C boundary seam flag: sparse — only write when set. Forward-tolerant
                // for legacy text loaders (unknown key is silently ignored). See
                // docs/dev/plans/optimizer-persistence-split.md §5.3.
                if (track.isBoundarySeam)
                    tsNode.AddValue("seam", "1");

                // Nested trajectory data depends on reference frame
                if (track.referenceFrame == ReferenceFrame.Absolute ||
                    track.referenceFrame == ReferenceFrame.Relative)
                {
                    var frames = track.frames;
                    if (frames != null)
                    {
                        for (int i = 0; i < frames.Count; i++)
                            SerializePoint(tsNode, frames[i], ic);
                    }

                    if (track.referenceFrame == ReferenceFrame.Relative
                        && recordingFormatVersion >= RecordingStore.RelativeAbsoluteShadowFormatVersion)
                    {
                        var absoluteFrames = track.absoluteFrames;
                        if (absoluteFrames != null)
                        {
                            for (int i = 0; i < absoluteFrames.Count; i++)
                            {
                                ConfigNode ptNode = tsNode.AddNode("ABSOLUTE_POINT");
                                SerializePointValues(ptNode, absoluteFrames[i], ic);
                            }
                        }
                    }
                }
                else if (track.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    var frames = track.frames;
                    if (frames != null)
                    {
                        for (int i = 0; i < frames.Count; i++)
                            SerializePoint(tsNode, frames[i], ic);
                    }

                    var checkpoints = track.checkpoints;
                    if (checkpoints != null)
                    {
                        for (int s = 0; s < checkpoints.Count; s++)
                            SerializeOrbitSegment(
                                tsNode,
                                checkpoints[s],
                                ic,
                                recordingFormatVersion,
                                writeLegacyPredictedFlag: false);
                    }
                }

            }
        }

        /// <summary>
        /// Deserializes TRACK_SECTION ConfigNodes from the given parent into the tracks list.
        /// Unknown environment or reference frame values cause the entire section to be skipped
        /// with a warning (forward tolerance for future enum additions).
        /// </summary>
        internal static void DeserializeTrackSections(ConfigNode parent, List<TrackSection> tracks)
        {
            var ns = NumberStyles.Float;
            var ic = CultureInfo.InvariantCulture;

            ConfigNode[] tsNodes = parent.GetNodes("TRACK_SECTION");
            if (tsNodes.Length == 0)
            {
                return;
            }

            for (int t = 0; t < tsNodes.Length; t++)
            {
                var tsNode = tsNodes[t];
                var section = new TrackSection();

                // Parse environment enum (skip section if unknown)
                int envInt;
                if (!int.TryParse(tsNode.GetValue("env"), NumberStyles.Integer, ic, out envInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unparseable env — skipping");
                    continue;
                }
                if (!Enum.IsDefined(typeof(SegmentEnvironment), envInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unknown env={envInt} — skipping");
                    continue;
                }
                section.environment = (SegmentEnvironment)envInt;

                // Parse reference frame enum (skip section if unknown)
                int refInt;
                if (!int.TryParse(tsNode.GetValue("ref"), NumberStyles.Integer, ic, out refInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unparseable ref — skipping");
                    continue;
                }
                if (!Enum.IsDefined(typeof(ReferenceFrame), refInt))
                {
                    ParsekLog.Warn("RecordingStore",
                        $"DeserializeTrackSections: section [{t}] has unknown ref={refInt} — skipping");
                    continue;
                }
                section.referenceFrame = (ReferenceFrame)refInt;

                // Parse scalar fields
                double.TryParse(tsNode.GetValue("startUT"), ns, ic, out section.startUT);
                double.TryParse(tsNode.GetValue("endUT"), ns, ic, out section.endUT);
                float.TryParse(tsNode.GetValue("sampleRate"), ns, ic, out section.sampleRateHz);

                // Source: defaults to Active (0) when absent — backward compatible
                string srcStr = tsNode.GetValue("src");
                if (srcStr != null)
                {
                    int srcInt;
                    if (int.TryParse(srcStr, NumberStyles.Integer, ic, out srcInt))
                    {
                        if (Enum.IsDefined(typeof(TrackSectionSource), srcInt))
                        {
                            section.source = (TrackSectionSource)srcInt;
                            ParsekLog.Verbose("RecordingStore",
                                $"DeserializeTrackSections: [{t}] loaded source={section.source}");
                        }
                        else
                        {
                            ParsekLog.Warn("RecordingStore",
                                $"DeserializeTrackSections: [{t}] unknown TrackSectionSource value={srcInt}, defaulting to Active");
                        }
                    }
                }
                // else: absent key — defaults to Active (struct default = 0)

                // Boundary discontinuity: defaults to 0 when absent — backward compatible
                float bdisc;
                if (float.TryParse(tsNode.GetValue("bdisc"), ns, ic, out bdisc))
                    section.boundaryDiscontinuityMeters = bdisc;

                // Altitude range: defaults to NaN when absent (legacy recordings)
                float minAlt, maxAlt;
                section.minAltitude = float.TryParse(tsNode.GetValue("minAlt"), ns, ic, out minAlt)
                    ? minAlt : float.NaN;
                section.maxAltitude = float.TryParse(tsNode.GetValue("maxAlt"), ns, ic, out maxAlt)
                    ? maxAlt : float.NaN;

                uint anchorPid;
                if (uint.TryParse(tsNode.GetValue("anchorPid"), NumberStyles.Integer, ic, out anchorPid))
                    section.anchorVesselId = anchorPid;

                // Producer-C boundary seam flag: defaults to false when absent — forward-tolerant
                // for legacy text recordings that were written before v8.
                string seamStr = tsNode.GetValue("seam");
                if (seamStr == "1")
                    section.isBoundarySeam = true;

                // Parse nested trajectory data based on reference frame
                if (section.referenceFrame == ReferenceFrame.Absolute ||
                    section.referenceFrame == ReferenceFrame.Relative)
                {
                    section.frames = new List<TrajectoryPoint>();
                    ConfigNode[] ptNodes = tsNode.GetNodes("POINT");
                    for (int i = 0; i < ptNodes.Length; i++)
                        section.frames.Add(DeserializePoint(ptNodes[i], ns, ic));

                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        section.absoluteFrames = new List<TrajectoryPoint>();
                        ConfigNode[] absPtNodes = tsNode.GetNodes("ABSOLUTE_POINT");
                        for (int i = 0; i < absPtNodes.Length; i++)
                            section.absoluteFrames.Add(DeserializePoint(absPtNodes[i], ns, ic));
                    }
                }
                else if (section.referenceFrame == ReferenceFrame.OrbitalCheckpoint)
                {
                    section.frames = new List<TrajectoryPoint>();
                    ConfigNode[] ptNodes = tsNode.GetNodes("POINT");
                    for (int i = 0; i < ptNodes.Length; i++)
                        section.frames.Add(DeserializePoint(ptNodes[i], ns, ic));

                    section.checkpoints = new List<OrbitSegment>();
                    ConfigNode[] segNodes = tsNode.GetNodes("ORBIT_SEGMENT");
                    for (int s = 0; s < segNodes.Length; s++)
                        section.checkpoints.Add(DeserializeOrbitSegment(segNodes[s], ns, ic));
                }

                // Initialize null lists to empty for frames that don't have nested data
                if (section.frames == null)
                    section.frames = new List<TrajectoryPoint>();
                if (section.checkpoints == null)
                    section.checkpoints = new List<OrbitSegment>();
                if (section.referenceFrame == ReferenceFrame.Relative && section.absoluteFrames == null)
                    section.absoluteFrames = new List<TrajectoryPoint>();

                tracks.Add(section);
            }
        }

        #endregion
    }
}
