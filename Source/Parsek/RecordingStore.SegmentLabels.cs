using System.Collections.Generic;

namespace Parsek
{
    public static partial class RecordingStore
    {
        /// <summary>
        /// Returns a human-readable phase label like "Kerbin atmo" or "exo".
        /// Returns empty string for untagged/legacy recordings.
        /// </summary>
        internal static string GetSegmentPhaseLabel(Recording rec)
        {
            if (rec == null) return "";
            return GetSegmentPhaseLabel(rec, GetSegmentBodyDisplayLabel(rec));
        }

        internal static string GetSegmentPhaseLabel(Recording rec, string displayBody)
        {
            if (rec == null) return "";
            if (ShouldSuppressEvaBoundaryPhaseLabel(rec))
            {
                return displayBody ?? "";
            }

            if (string.IsNullOrEmpty(rec.SegmentPhase)) return "";
            if (!string.IsNullOrEmpty(displayBody))
                return displayBody + " " + rec.SegmentPhase;
            return rec.SegmentPhase;
        }

        internal static string GetSegmentBodyDisplayLabel(Recording rec)
        {
            if (rec == null) return "";

            int pointCount = rec.Points != null ? rec.Points.Count : 0;
            int trackSectionCount = rec.TrackSections != null ? rec.TrackSections.Count : 0;
            string lastPointBodyName = pointCount > 0 ? rec.Points[pointCount - 1].bodyName : null;
            if (rec.SegmentBodyDisplayLabelCacheValid
                && rec.SegmentBodyDisplayLabelCachePointCount == pointCount
                && rec.SegmentBodyDisplayLabelCacheTrackSectionCount == trackSectionCount
                && rec.SegmentBodyDisplayLabelCacheSegmentBodyName == rec.SegmentBodyName
                && rec.SegmentBodyDisplayLabelCacheStartBodyName == rec.StartBodyName
                && rec.SegmentBodyDisplayLabelCacheLastPointBodyName == lastPointBodyName)
            {
                return rec.SegmentBodyDisplayLabelCache ?? "";
            }

            string bodyPath;
            string result;
            if (TryBuildBodyPathLabel(rec.Points, out bodyPath))
            {
                result = bodyPath;
            }
            else if (TryBuildBodyPathLabel(rec.TrackSections, out bodyPath))
            {
                result = bodyPath;
            }
            else
            {
                string body = rec.SegmentBodyName;
                if (string.IsNullOrEmpty(body))
                    body = lastPointBodyName;
                if (string.IsNullOrEmpty(body))
                    body = rec.StartBodyName;
                result = body ?? "";
            }

            rec.SegmentBodyDisplayLabelCacheValid = true;
            rec.SegmentBodyDisplayLabelCache = result;
            rec.SegmentBodyDisplayLabelCachePointCount = pointCount;
            rec.SegmentBodyDisplayLabelCacheTrackSectionCount = trackSectionCount;
            rec.SegmentBodyDisplayLabelCacheSegmentBodyName = rec.SegmentBodyName;
            rec.SegmentBodyDisplayLabelCacheStartBodyName = rec.StartBodyName;
            rec.SegmentBodyDisplayLabelCacheLastPointBodyName = lastPointBodyName;
            return result;
        }

        /// <summary>
        /// The ordered body-transition sequence of one recording (consecutive duplicates
        /// collapsed): the same walk the segment body-path label uses (Points first, then
        /// TrackSection frames / bodyFixedFrames / checkpoints), falling back to the single
        /// segment / start body when no per-sample bodies exist. Used by the Missions header's
        /// narrative body path (T2.1), which concatenates these across a through-line's legs.
        /// Never null; empty when the recording carries no body at all.
        /// </summary>
        internal static List<string> GetBodyTransitionSequence(Recording rec)
        {
            var bodies = new List<string>();
            if (rec == null)
                return bodies;

            if (rec.Points != null)
                for (int i = 0; i < rec.Points.Count; i++)
                    AppendBodyTransition(bodies, rec.Points[i].bodyName);

            if (bodies.Count == 0 && rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    TrackSection section = rec.TrackSections[i];
                    if (section.frames != null && section.frames.Count > 0)
                    {
                        AppendBodyTransitions(bodies, section.frames);
                    }
                    else if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                    {
                        AppendBodyTransitions(bodies, section.bodyFixedFrames);
                    }
                    else if (section.checkpoints != null)
                    {
                        for (int j = 0; j < section.checkpoints.Count; j++)
                            AppendBodyTransition(bodies, section.checkpoints[j].bodyName);
                    }
                }
            }

            if (bodies.Count == 0)
            {
                string body = rec.SegmentBodyName;
                if (string.IsNullOrEmpty(body))
                    body = rec.StartBodyName;
                AppendBodyTransition(bodies, body);
            }

            return bodies;
        }

        private static bool TryBuildBodyPathLabel(List<TrajectoryPoint> points, out string label)
        {
            label = null;
            if (points == null || points.Count == 0)
                return false;

            var bodies = new List<string>();
            for (int i = 0; i < points.Count; i++)
                AppendBodyTransition(bodies, points[i].bodyName);

            return TryFormatBodyPathLabel(bodies, out label);
        }

        private static bool TryBuildBodyPathLabel(List<TrackSection> sections, out string label)
        {
            label = null;
            if (sections == null || sections.Count == 0)
                return false;

            var bodies = new List<string>();
            for (int i = 0; i < sections.Count; i++)
            {
                TrackSection section = sections[i];
                if (section.frames != null && section.frames.Count > 0)
                {
                    AppendBodyTransitions(bodies, section.frames);
                }
                else if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                {
                    AppendBodyTransitions(bodies, section.bodyFixedFrames);
                }
                else if (section.checkpoints != null)
                {
                    for (int j = 0; j < section.checkpoints.Count; j++)
                        AppendBodyTransition(bodies, section.checkpoints[j].bodyName);
                }
            }

            return TryFormatBodyPathLabel(bodies, out label);
        }

        private static void AppendBodyTransitions(List<string> bodies, List<TrajectoryPoint> points)
        {
            if (points == null)
                return;
            for (int i = 0; i < points.Count; i++)
                AppendBodyTransition(bodies, points[i].bodyName);
        }

        private static void AppendBodyTransition(List<string> bodies, string bodyName)
        {
            if (bodies == null || string.IsNullOrEmpty(bodyName))
                return;
            if (bodies.Count == 0 || bodies[bodies.Count - 1] != bodyName)
                bodies.Add(bodyName);
        }

        private static bool TryFormatBodyPathLabel(List<string> bodies, out string label)
        {
            label = null;
            if (bodies == null || bodies.Count < 2)
                return false;
            label = string.Join(" -> ", bodies.ToArray());
            return true;
        }

        internal static bool ShouldSuppressEvaBoundaryPhaseLabel(Recording rec)
        {
            if (rec == null
                || string.IsNullOrEmpty(rec.EvaCrewName)
                || rec.TrackSections == null
                || rec.TrackSections.Count < 2)
            {
                return false;
            }

            bool sawAtmo = false;
            bool sawSurface = false;
            for (int i = 0; i < rec.TrackSections.Count; i++)
            {
                int envClass = RecordingOptimizer.SplitEnvironmentClass(rec.TrackSections[i].environment);
                switch (envClass)
                {
                    case 0:
                        sawAtmo = true;
                        break;
                    case 2:
                        sawSurface = true;
                        break;
                    default:
                        return false;
                }
            }

            return sawAtmo && sawSurface;
        }
    }
}
