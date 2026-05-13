using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    internal static class DebrisRelativeRecorderPolicy
    {
        private const string LogTag = "BgRecorder";

        internal struct ParentAnchoredDebrisTailNormalizationResult
        {
            internal bool Mutated;
            internal int ClampedSections;
            internal int DroppedSections;
            internal int TrimmedFlatPoints;
            internal bool ExplicitEndClamped;
            internal double OldEndUT;
            internal double NewEndUT;
            internal double RelativeTailUT;
            internal double ShadowTailUT;
        }

        internal static bool ShouldNormalizeParentAnchoredDebris(Recording rec)
        {
            return rec != null
                && rec.IsDebris
                && rec.DebrisParentRecordingId != null
                && rec.LoopAnchorVesselId == 0u;
        }

        internal static ParentAnchoredDebrisTailNormalizationResult NormalizeParentAnchoredRelativeRecording(
            Recording rec,
            string context,
            bool refreshEndpointDecision = true)
        {
            var result = CreateResult();
            if (!ShouldNormalizeParentAnchoredDebris(rec))
                return result;

            bool sawRelativeSection = false;
            NormalizeRelativeTrackSections(rec, ref result, out sawRelativeSection);
            if (!sawRelativeSection)
                return result;

            bool hasRenderableTail = TryGetLatestRenderableUT(
                rec,
                includePredictedOrbitSegments: true,
                out double latestRenderableUT);

            TrimFlatPointsPastRenderableTail(rec, hasRenderableTail, latestRenderableUT, ref result);
            ClampExplicitEndUT(rec, hasRenderableTail, latestRenderableUT, ref result);

            if (!result.Mutated)
                return result;

            rec.MarkFilesDirty();
            rec.CachedStats = null;
            rec.CachedStatsPointCount = 0;
            if (refreshEndpointDecision)
            {
                RecordingEndpointResolver.RefreshEndpointDecision(
                    rec,
                    "ParentAnchoredDebrisTailNormalize:" + (context ?? "unspecified"),
                    logDecision: false);
            }

            LogNormalization(rec, context, result);
            return result;
        }

        internal static bool TryGetLastRecorderPersistableAuthoredUT(
            Recording rec,
            out double lastAuthoredUT)
        {
            lastAuthoredUT = double.NaN;
            if (!ShouldNormalizeParentAnchoredDebris(rec))
                return false;

            return TryGetLatestRenderableUT(
                rec,
                includePredictedOrbitSegments: false,
                out lastAuthoredUT);
        }

        private static void NormalizeRelativeTrackSections(
            Recording rec,
            ref ParentAnchoredDebrisTailNormalizationResult result,
            out bool sawRelativeSection)
        {
            sawRelativeSection = false;
            if (rec.TrackSections == null || rec.TrackSections.Count == 0)
                return;

            for (int i = rec.TrackSections.Count - 1; i >= 0; i--)
            {
                TrackSection section = rec.TrackSections[i];
                if (section.referenceFrame != ReferenceFrame.Relative)
                    continue;

                sawRelativeSection = true;
                TrackSection original = section;
                double relativeTail = GetRelativeTailUT(section);
                double shadowTail = GetShadowTailUT(section);
                RememberTailUT(relativeTail, ref result.RelativeTailUT);
                RememberTailUT(shadowTail, ref result.ShadowTailUT);

                bool hasCoverage = DebrisRelativeCoveragePrimitives.TryGetRenderableCoverageEndUT(
                    section.frames,
                    section.bodyFixedFrames,
                    section.checkpoints,
                    section.startUT,
                    section.endUT,
                    DebrisRelativeCoverageMode.RecorderPersistable,
                    out double coverageEndUT,
                    out _);

                if (!hasCoverage)
                {
                    rec.TrackSections.RemoveAt(i);
                    result.DroppedSections++;
                    RememberOldEnd(section.endUT, ref result);
                    MarkMutated(ref result);
                    continue;
                }

                if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(section.endUT)
                    || section.endUT > coverageEndUT + DebrisRelativeCoveragePrimitives.UtEpsilon)
                {
                    section.endUT = coverageEndUT;
                    RecomputeSampleRate(ref section);
                    rec.TrackSections[i] = section;
                    result.ClampedSections++;
                    RememberOldEnd(original.endUT, ref result);
                    RememberNewEnd(section.endUT, ref result);
                    MarkMutated(ref result);
                }
            }
        }

        private static bool TryGetLatestRenderableUT(
            Recording rec,
            bool includePredictedOrbitSegments,
            out double latestUT)
        {
            latestUT = double.NaN;
            bool found = false;

            if (rec == null)
                return false;

            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection section = rec.TrackSections[i];
                    if (section.referenceFrame == ReferenceFrame.Relative)
                    {
                        if (DebrisRelativeCoveragePrimitives.TryGetRenderableCoverageEndUT(
                                section.frames,
                                section.bodyFixedFrames,
                                section.checkpoints,
                                section.startUT,
                                section.endUT,
                                DebrisRelativeCoverageMode.RecorderPersistable,
                                out double coverageEndUT,
                                out _))
                        {
                            ConsiderUT(coverageEndUT, ref latestUT, ref found);
                        }

                        continue;
                    }

                    bool hasSectionPayload = false;
                    if (section.frames != null)
                    {
                        for (int j = 0; j < section.frames.Count; j++)
                        {
                            ConsiderUT(section.frames[j].ut, ref latestUT, ref found);
                            hasSectionPayload = true;
                        }
                    }

                    if (section.checkpoints != null)
                    {
                        for (int j = 0; j < section.checkpoints.Count; j++)
                        {
                            OrbitSegment checkpoint = section.checkpoints[j];
                            if (checkpoint.isPredicted)
                                continue;

                            ConsiderUT(checkpoint.endUT, ref latestUT, ref found);
                            hasSectionPayload = true;
                        }
                    }

                    if (hasSectionPayload)
                        ConsiderUT(section.endUT, ref latestUT, ref found);
                }
            }

            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment segment = rec.OrbitSegments[i];
                    if (segment.isPredicted && !includePredictedOrbitSegments)
                        continue;

                    ConsiderUT(segment.endUT, ref latestUT, ref found);
                }
            }

            return found;
        }

        private static void TrimFlatPointsPastRenderableTail(
            Recording rec,
            bool hasRenderableTail,
            double latestRenderableUT,
            ref ParentAnchoredDebrisTailNormalizationResult result)
        {
            if (rec.Points == null || rec.Points.Count == 0)
                return;

            int before = rec.Points.Count;
            if (hasRenderableTail)
            {
                rec.Points.RemoveAll(point =>
                    DebrisRelativeCoveragePrimitives.IsFiniteUT(point.ut)
                    && point.ut > latestRenderableUT + DebrisRelativeCoveragePrimitives.UtEpsilon);
            }
            else
            {
                rec.Points.Clear();
            }

            int trimmed = before - rec.Points.Count;
            if (trimmed <= 0)
                return;

            result.TrimmedFlatPoints += trimmed;
            MarkMutated(ref result);
        }

        private static void ClampExplicitEndUT(
            Recording rec,
            bool hasRenderableTail,
            double latestRenderableUT,
            ref ParentAnchoredDebrisTailNormalizationResult result)
        {
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(rec.ExplicitEndUT))
                return;

            double newEndUT = hasRenderableTail ? latestRenderableUT : double.NaN;
            bool shouldClamp = !hasRenderableTail
                || rec.ExplicitEndUT > latestRenderableUT + DebrisRelativeCoveragePrimitives.UtEpsilon;
            if (!shouldClamp)
                return;

            RememberOldEnd(rec.ExplicitEndUT, ref result);
            rec.ExplicitEndUT = newEndUT;
            RememberNewEnd(rec.ExplicitEndUT, ref result);
            result.ExplicitEndClamped = true;
            MarkMutated(ref result);
        }

        private static ParentAnchoredDebrisTailNormalizationResult CreateResult()
        {
            return new ParentAnchoredDebrisTailNormalizationResult
            {
                Mutated = false,
                ClampedSections = 0,
                DroppedSections = 0,
                TrimmedFlatPoints = 0,
                ExplicitEndClamped = false,
                OldEndUT = double.NaN,
                NewEndUT = double.NaN,
                RelativeTailUT = double.NaN,
                ShadowTailUT = double.NaN
            };
        }

        private static double GetRelativeTailUT(TrackSection section)
        {
            if (DebrisRelativeCoveragePrimitives.TryGetRelativeFrameCoverageEndUT(
                    section.frames,
                    section.startUT,
                    section.endUT,
                    DebrisRelativeCoverageMode.RecorderPersistable,
                    out double relativeTailUT))
            {
                return relativeTailUT;
            }

            if (DebrisRelativeCoveragePrimitives.TryGetCheckpointCoverageEndUT(
                    section.checkpoints,
                    out double checkpointTailUT))
            {
                return checkpointTailUT;
            }

            return double.NaN;
        }

        private static double GetShadowTailUT(TrackSection section)
        {
            return DebrisRelativeCoveragePrimitives.TryGetBodyFixedPrimaryCoverageEndUT(
                section.bodyFixedFrames,
                out double shadowTailUT)
                ? shadowTailUT
                : double.NaN;
        }

        private static void RecomputeSampleRate(ref TrackSection section)
        {
            if (section.frames == null || section.frames.Count <= 1)
            {
                // A single-frame section has no measurable cadence; keep existing
                // rate metadata and let consumers treat the frame list as authoritative.
                return;
            }

            double duration = section.endUT - section.startUT;
            if (duration > 0.0)
                section.sampleRateHz = (float)(section.frames.Count / duration);
        }

        private static void ConsiderUT(double candidateUT, ref double latestUT, ref bool found)
        {
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(candidateUT))
                return;

            if (!found || candidateUT > latestUT)
                latestUT = candidateUT;

            found = true;
        }

        private static void RememberTailUT(double candidateUT, ref double storedUT)
        {
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(candidateUT))
                return;
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(storedUT) || candidateUT > storedUT)
                storedUT = candidateUT;
        }

        private static void RememberOldEnd(
            double candidateUT,
            ref ParentAnchoredDebrisTailNormalizationResult result)
        {
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(candidateUT))
                return;
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(result.OldEndUT)
                || candidateUT > result.OldEndUT)
            {
                result.OldEndUT = candidateUT;
            }
        }

        private static void RememberNewEnd(
            double candidateUT,
            ref ParentAnchoredDebrisTailNormalizationResult result)
        {
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(candidateUT))
                return;
            if (!DebrisRelativeCoveragePrimitives.IsFiniteUT(result.NewEndUT)
                || candidateUT > result.NewEndUT)
            {
                result.NewEndUT = candidateUT;
            }
        }

        private static void MarkMutated(
            ref ParentAnchoredDebrisTailNormalizationResult result)
        {
            result.Mutated = true;
        }

        private static void LogNormalization(
            Recording rec,
            string context,
            ParentAnchoredDebrisTailNormalizationResult result)
        {
            ParsekLog.Warn(LogTag,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "ParentAnchoredDebrisTailNormalize rec={0} context={1} clamped={2} dropped={3} " +
                    "oldEnd={4} newEnd={5} relTail={6} shadowTail={7} parentRec={8} flatTrimmed={9}",
                    rec.RecordingId ?? "(null)",
                    string.IsNullOrEmpty(context) ? "unspecified" : context,
                    result.ClampedSections,
                    result.DroppedSections,
                    FormatUT(result.OldEndUT),
                    FormatUT(result.NewEndUT),
                    FormatUT(result.RelativeTailUT),
                    FormatUT(result.ShadowTailUT),
                    rec.DebrisParentRecordingId ?? "(null)",
                    result.TrimmedFlatPoints));
        }

        private static string FormatUT(double value)
        {
            return DebrisRelativeCoveragePrimitives.IsFiniteUT(value)
                ? value.ToString("R", CultureInfo.InvariantCulture)
                : "NaN";
        }
    }
}
