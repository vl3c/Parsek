using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Pure core of the <c>FlightGhostPosition</c> in-game category
    /// (<see cref="FlightGhostPositionInGameTests"/>): is every live flight-scene ghost drawn
    /// where its recording says the vessel was at the UT the engine plays it at?
    ///
    /// <para>The comparison itself is <see cref="RouteGhostPlacement"/>'s, reused unchanged:
    /// the cell re-derives the recorded place on its own (section lookup, bracketing samples,
    /// latitude / longitude / altitude interpolation, then stock
    /// <c>CelestialBody.GetLatitude / GetLongitude / GetAltitude</c> of the rendered root) and
    /// compares horizontally within <see cref="RouteGhostPlacement.HorizontalToleranceMeters"/>
    /// and vertically inside <see cref="RouteGhostPlacement.VerticalWindow"/>. This file adds
    /// only what a flight ghost needs beyond a route ghost: which of the three recorded
    /// surfaces a sample sits on, and whether the tolerance at that sample still resolves a
    /// wrong placement.</para>
    ///
    /// <para>THE THREE SURFACES, one cell each, so a lane's tally says which ones its flight
    /// exercised:
    /// <list type="bullet">
    /// <item><see cref="SurfaceKind.Absolute"/>: an Absolute section's body-fixed frames (or
    /// the flat point list of a recording with no sections). The positioner interpolates the
    /// same samples, so this half checks world placement (body rotation, floating origin,
    /// terrain handling, the loop unit's body-fixed shift) rather than the interpolation.</item>
    /// <item><see cref="SurfaceKind.ParentAnchored"/>: a Relative section of a parent-anchored
    /// recording (debris, controlled-decoupled children), whose primary playback surface is the
    /// recorded body-fixed shadow (<c>TrackSection.bodyFixedFrames</c>).</item>
    /// <item><see cref="SurfaceKind.RelativeAnchor"/>: a Relative section of a recording that
    /// is NOT parent-anchored (a docking approach, a rendezvous). The positioner places it from
    /// the anchor-local offsets (<c>TrackSection.frames</c>) and the anchor's resolved pose;
    /// the cell compares it with the body-fixed shadow the recorder wrote beside them, so the
    /// check is NOT circular: an anchor resolved at the wrong place, an offset applied in the
    /// wrong frame, or an offset read as latitude / longitude lands kilometres off the
    /// shadow. In LOOP playback a non-parent-anchored Relative section resolves against the
    /// LIVE anchor vessel by contract, so the shadow is not its expected place and the sample
    /// is counted unsupported, never compared.</item>
    /// </list></para>
    ///
    /// <para>Orbital-checkpoint sections, a Relative section without a covering shadow, a UT
    /// outside the samples, an overlap / re-aim loop and a sample whose float grid swallows the
    /// signal are counted, never failed; a cell that compared nothing skips, so a lane that
    /// pins <c>passed=</c> of at least one reds when nothing was compared.</para>
    /// </summary>
    internal static class FlightGhostPosition
    {
        internal const string Category = "FlightGhostPosition";

        /// <summary>The recorded surface a sample is compared against.</summary>
        internal enum SurfaceKind
        {
            Absolute = 0,
            ParentAnchored = 1,
            RelativeAnchor = 2,
        }

        /// <summary>
        /// The smallest wrong placement the cell must still be able to see, in metres: a Relative
        /// offset read as latitude / longitude, a body rotated by a loop shift the positioner
        /// forgot, or an anchor resolved at the wrong instant are all far above it.
        /// </summary>
        internal const double MinDiscriminatedErrorMeters = 50.0;

        /// <summary>A ghost drawn one second of its own travel off is also a wrong placement
        /// (a clock that slipped a second); the signal grows with the ghost's speed.</summary>
        internal const double DiscriminatedClockSlipSeconds = 1.0;

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal static string KindLabel(SurfaceKind kind)
        {
            switch (kind)
            {
                case SurfaceKind.Absolute: return "absolute";
                case SurfaceKind.ParentAnchored: return "parent-anchored";
                case SurfaceKind.RelativeAnchor: return "relative-anchor";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Names which of the three surfaces the sample at <paramref name="ut"/> sits on and picks
        /// its recorded body-fixed samples. <paramref name="kind"/> is set even when the method
        /// returns false (an orbital-checkpoint section reads <see cref="SurfaceKind.Absolute"/>,
        /// a Relative section without a covering shadow reads its Relative kind), so the caller can
        /// count an uncomparable sample against the right cell; <paramref name="source"/> carries
        /// the reason. The sample list itself is chosen by
        /// <see cref="RouteGhostPlacement.TrySelectSurface"/>, so the two cells read a recording
        /// the same way.
        /// </summary>
        internal static bool TryClassify(IList<TrackSection> sections,
            IList<TrajectoryPoint> flatPoints, bool parentAnchored, bool loopPlayback, double ut,
            out SurfaceKind kind, out IList<TrajectoryPoint> frames, out string source)
        {
            kind = SurfaceKind.Absolute;
            if (sections != null && sections.Count > 0)
            {
                int index = RouteGhostPlacement.FindSectionIndex(sections, ut);
                if (index >= 0 && sections[index].referenceFrame == ReferenceFrame.Relative)
                    kind = parentAnchored ? SurfaceKind.ParentAnchored : SurfaceKind.RelativeAnchor;
            }

            if (kind == SurfaceKind.RelativeAnchor && loopPlayback)
            {
                frames = null;
                source = "relative-loop-live-anchor";
                return false;
            }

            if (!RouteGhostPlacement.TrySelectSurface(sections, flatPoints, ut, out frames, out source))
                return false;

            if (kind == SurfaceKind.RelativeAnchor)
                source = "relative-anchor-shadow";
            return true;
        }

        /// <summary>The displacement a wrong placement would show at this sample.</summary>
        internal static double DiscriminationSignalMeters(double speedMps)
        {
            double speed = IsFinite(speedMps) && speedMps > 0.0 ? speedMps : 0.0;
            return Math.Max(MinDiscriminatedErrorMeters, speed * DiscriminatedClockSlipSeconds);
        }

        /// <summary>
        /// True when the horizontal tolerance still resolves <see cref="DiscriminationSignalMeters"/>
        /// (<see cref="InGameFixtureMath.ToleranceResolvesSignal"/>). A sample far enough from the
        /// floating origin that the float grid swallows the signal is not compared.
        /// </summary>
        internal static bool ToleranceResolves(double horizontalToleranceMeters, double speedMps)
        {
            return InGameFixtureMath.ToleranceResolvesSignal(
                horizontalToleranceMeters, DiscriminationSignalMeters(speedMps));
        }

        /// <summary>
        /// One frame of the ghost's motion, the same allowance
        /// <see cref="RouteGhostPlacement.HorizontalToleranceMeters"/> grants horizontally, applied
        /// to the vertical window too: a flight ghost climbs or descends at hundreds of metres a
        /// second, where a surface route ghost does not.
        /// </summary>
        internal static double FrameMotionMeters(double speedMps, double frameSeconds)
        {
            double speed = IsFinite(speedMps) && speedMps > 0.0 ? speedMps : 0.0;
            double dt = IsFinite(frameSeconds) && frameSeconds > 0.0
                ? Math.Min(frameSeconds, RouteGhostPlacement.MaxFrameSeconds) : 0.0;
            return speed * dt;
        }

        /// <summary>Whether a playback UT lies inside the recording's own window.</summary>
        internal static bool InsideRecording(double playbackUT, double startUT, double endUT)
        {
            if (double.IsNaN(playbackUT)) return false;
            return playbackUT >= startUT && playbackUT <= endUT;
        }

        internal enum CellOutcome
        {
            Pass = 0,
            Fail = 1,
            Skip = 2,
        }

        /// <summary>A cell fails on any bad sample, skips when it compared nothing, passes otherwise.</summary>
        internal static CellOutcome DecideOutcome(int compared, int bad)
        {
            if (bad > 0) return CellOutcome.Fail;
            if (compared <= 0) return CellOutcome.Skip;
            return CellOutcome.Pass;
        }

        internal static string FormatSampleLine(SurfaceKind kind, int ghostIndex, string recordingId,
            string vesselName, string body, string source, string clock, double ut, double playbackUT,
            RouteGhostPlacement.Verdict v)
        {
            return string.Format(IC,
                "FlightGhostPosition: kind={0} ghost={1} rec={2} vessel='{3}' body={4} source={5} "
                + "clock={6} ut={7:F2} playbackUT={8:F2} horizM={9:F2} tolM={10:F2} dAltM={11:F2} "
                + "altWindow=[{12:F1},{13:F1}] verdict={14}",
                KindLabel(kind), ghostIndex, recordingId, vesselName, body, source, clock, ut,
                playbackUT, v.HorizontalMeters, v.HorizontalToleranceMeters, v.VerticalDeltaMeters,
                v.VerticalMinMeters, v.VerticalMaxMeters, v.Reason);
        }

        /// <summary>The one summary line per cell the lanes pin.</summary>
        internal static string FormatSummary(SurfaceKind kind, string scene, int frames, int ghosts,
            int compared, int ok, int bad, int otherKind, int outsideWindow, int hidden,
            int unsupported, int unresolved, int notLive, double worstHorizontalMeters, string bodies)
        {
            return string.Format(IC,
                "FlightGhostPosition summary: kind={0} scene={1} frames={2} ghosts={3} compared={4} "
                + "ok={5} bad={6} otherKind={7} outsideWindow={8} hidden={9} unsupported={10} "
                + "unresolved={11} notLive={12} worstHorizM={13:F2} bodies=[{14}]",
                KindLabel(kind), scene, frames, ghosts, compared, ok, bad, otherKind, outsideWindow,
                hidden, unsupported, unresolved, notLive, worstHorizontalMeters, bodies ?? string.Empty);
        }

        private static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
    }
}
