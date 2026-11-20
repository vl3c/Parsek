using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// One stretch of a recording where the vessel did not move continuously: a time jump
    /// (UT skipped from <see cref="StartUT"/> to <see cref="EndUT"/>) or a teleport (the
    /// last sample before a boundary seam to the seam's own frame). A cut strictly inside it
    /// would interpolate between two unrelated states.
    /// </summary>
    internal struct DiscontinuityWindow
    {
        internal double StartUT;
        internal double EndUT;
        internal string Kind;

        internal bool StrictlyContains(double ut)
        {
            return ut > StartUT && ut < EndUT;
        }

        public override string ToString()
        {
            var ic = CultureInfo.InvariantCulture;
            return Kind + "[" + StartUT.ToString("F2", ic) + "," + EndUT.ToString("F2", ic) + "]";
        }
    }

    /// <summary>
    /// Owner ruling Q18 (2026-11-18): optimizer splits and Re-Fly cuts never land inside a
    /// cheat teleport or a time jump; the cut moves to the nearest point outside it. Pure
    /// over the recordings handed in.
    /// </summary>
    internal static class RecordingDiscontinuities
    {
        internal const string TimeJumpKind = "time-jump";
        internal const string SeamKind = "teleport-seam";

        /// <summary>
        /// The discontinuity windows of <paramref name="piecesInChainOrder"/> (the segment
        /// events and track sections of one recording, or of the consecutive chain pieces of
        /// one vessel, earliest first, so a seam that starts a later piece still finds the
        /// sample before it in the earlier one): every <see cref="SegmentEventType.TimeJump"/>
        /// event (its <c>preUT</c> / <c>postUT</c> details) and every
        /// <see cref="TrackSection.isBoundarySeam"/> section, from the last sample of the
        /// section before it to the seam's start. Windows of zero or negative length are
        /// dropped. Takes the two lists rather than the recording type so this helper stays a
        /// leaf of the type graph.
        /// </summary>
        internal static List<DiscontinuityWindow> Collect(
            IEnumerable<(List<SegmentEvent> events, List<TrackSection> sections)> piecesInChainOrder)
        {
            var windows = new List<DiscontinuityWindow>();
            if (piecesInChainOrder == null) return windows;
            bool havePrev = false;
            double prevLastSampleUT = double.NaN;
            foreach (var piece in piecesInChainOrder)
            {
                var events = piece.events;
                var sections = piece.sections;
                if (events != null)
                {
                    for (int i = 0; i < events.Count; i++)
                    {
                        var e = events[i];
                        if (e.type != SegmentEventType.TimeJump) continue;
                        double pre, post;
                        if (!TryParseTimeJumpDetails(e.details, out pre, out post))
                            continue;
                        if (post > pre)
                            windows.Add(new DiscontinuityWindow { StartUT = pre, EndUT = post, Kind = TimeJumpKind });
                    }
                }
                if (sections == null) continue;
                for (int s = 0; s < sections.Count; s++)
                {
                    var section = sections[s];
                    if (section.isBoundarySeam && havePrev && section.startUT > prevLastSampleUT)
                    {
                        windows.Add(new DiscontinuityWindow
                        {
                            StartUT = prevLastSampleUT,
                            EndUT = section.startUT,
                            Kind = SeamKind,
                        });
                    }
                    double last = LastSampleUT(section);
                    if (!double.IsNaN(last))
                    {
                        prevLastSampleUT = last;
                        havePrev = true;
                    }
                }
            }
            return windows;
        }

        /// <summary>
        /// Moves <paramref name="cutUT"/> out of every window that strictly contains it:
        /// to the window's start when <paramref name="earlierEdgeOnly"/> (a Re-Fly cut,
        /// whose kept part must end at or before the rewind), else to the nearer edge (a
        /// tie goes to the start). Returns the cut unchanged when no window contains it.
        /// Overlapping windows are resolved by repeating, bounded by the window count.
        /// </summary>
        internal static double MoveCutOutside(
            IReadOnlyList<DiscontinuityWindow> windows, double cutUT, bool earlierEdgeOnly,
            out string movedOutOf)
        {
            movedOutOf = null;
            if (windows == null || windows.Count == 0 || double.IsNaN(cutUT)) return cutUT;
            double cut = cutUT;
            for (int pass = 0; pass <= windows.Count; pass++)
            {
                bool moved = false;
                for (int i = 0; i < windows.Count; i++)
                {
                    var w = windows[i];
                    if (!w.StrictlyContains(cut)) continue;
                    double next = earlierEdgeOnly || (cut - w.StartUT) <= (w.EndUT - cut)
                        ? w.StartUT
                        : w.EndUT;
                    movedOutOf = movedOutOf == null ? w.ToString() : movedOutOf + "+" + w;
                    cut = next;
                    moved = true;
                }
                if (!moved) break;
            }
            return cut;
        }

        /// <summary>Parses <c>preUT=..;postUT=..</c> from a time-jump event's details.</summary>
        internal static bool TryParseTimeJumpDetails(string details, out double preUT, out double postUT)
        {
            preUT = double.NaN;
            postUT = double.NaN;
            if (string.IsNullOrEmpty(details)) return false;
            string[] parts = details.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = parts[i].Substring(0, eq).Trim();
                string value = parts[i].Substring(eq + 1).Trim();
                double v;
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    continue;
                if (key == "preUT") preUT = v;
                else if (key == "postUT") postUT = v;
            }
            return !double.IsNaN(preUT) && !double.IsNaN(postUT);
        }

        private static double LastSampleUT(TrackSection section)
        {
            double last = double.NaN;
            if (section.frames != null && section.frames.Count > 0)
                last = Max(last, section.frames[section.frames.Count - 1].ut);
            if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                last = Max(last, section.bodyFixedFrames[section.bodyFixedFrames.Count - 1].ut);
            if (section.checkpoints != null && section.checkpoints.Count > 0)
                last = Max(last, section.checkpoints[section.checkpoints.Count - 1].endUT);
            return last;
        }

        private static double Max(double a, double b)
        {
            if (double.IsNaN(a)) return b;
            if (double.IsNaN(b)) return a;
            return Math.Max(a, b);
        }
    }
}
