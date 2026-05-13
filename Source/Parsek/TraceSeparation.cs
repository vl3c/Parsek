using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Opt-in diagnostic utility for tracing a separation (joint-break)
    /// event and the playback that follows, gated by the
    /// <c>ghostRenderTracing</c> setting. Maintains:
    ///   1. A short ring buffer of pre-event log lines (~2 s history) so we
    ///      can see the per-tick samples leading up to the structural event.
    ///   2. An active-window UT after a joint-break fires (default 2 s)
    ///      during which every instrumented site logs unconditionally.
    ///   3. A separate playback window opened the first time a debris ghost
    ///      is rendered after a structural event.
    ///
    /// All log lines go to <c>ParsekLog.Info("Trace-Sep", ...)</c>. The
    /// component is OFF entirely unless <c>ghostRenderTracing</c> is on AND
    /// a joint-break / ghost-activation occurs, so idle-path cost is a
    /// settings null-check plus a bool check.
    /// </summary>
    internal static class TraceSeparation
    {
        private const int PreEventRingBufferSize = 400; // ~2 s at 200 events/s
        private const double RecordingWindowSeconds = 2.0;
        private const double PlaybackWindowSeconds = 2.0;

        private static readonly List<string> ringBuffer = new List<string>();
        private static double recordingWindowEndUT = double.NegativeInfinity;
        private static double playbackWindowEndUT = double.NegativeInfinity;
        private static double lastRecordingTriggerUT = double.NaN;
        private static readonly object gate = new object();

        /// <summary>
        /// UT of the most recent OpenRecordingWindow trigger (typically a
        /// joint-break). Per-tick recorder traces can use this to compute
        /// `tickSinceBreak` so a log reader can identify the first
        /// post-PhysX per-tick sample after the structural event without
        /// having to grep for the JointBreak line and count subsequent
        /// per-tick samples manually. NaN when no recording window has been
        /// opened in this session.
        /// </summary>
        internal static double LastRecordingTriggerUT
        {
            get { lock (gate) { return lastRecordingTriggerUT; } }
        }

        /// <summary>
        /// Add a line to the pre-event ring buffer. Cheap unless the buffer
        /// is full (then it pops the oldest entry). Use at instrumented
        /// capture sites that should retroactively flush when an event
        /// fires.
        /// </summary>
        internal static void Prelog(string subsystem, string msg)
        {
            string line = FormatLine(subsystem, msg);
            lock (gate)
            {
                if (ringBuffer.Count >= PreEventRingBufferSize)
                    ringBuffer.RemoveAt(0);
                ringBuffer.Add(line);
            }
        }

        /// <summary>
        /// Flush the pre-event ring buffer to the log AND open a recording
        /// window for the next <see cref="RecordingWindowSeconds"/> seconds.
        /// Call from the structural-event entry point (joint-break).
        /// </summary>
        internal static void OpenRecordingWindow(double eventUT, string trigger)
        {
            List<string> toFlush;
            lock (gate)
            {
                toFlush = new List<string>(ringBuffer);
                ringBuffer.Clear();
                recordingWindowEndUT = eventUT + RecordingWindowSeconds;
                lastRecordingTriggerUT = eventUT;
            }
            ParsekLog.Info("Trace-Sep",
                "=============== RECORDING WINDOW OPEN ===============");
            ParsekLog.Info("Trace-Sep",
                string.Format(CultureInfo.InvariantCulture,
                    "Trigger: {0} | eventUT={1:R} | windowEndUT={2:R} | preEventBufferCount={3}",
                    trigger, eventUT, eventUT + RecordingWindowSeconds, toFlush.Count));
            ParsekLog.Info("Trace-Sep", "------- PRE-EVENT BUFFER (oldest first) -------");
            for (int i = 0; i < toFlush.Count; i++)
                ParsekLog.Info("Trace-Sep", "PRE  " + toFlush[i]);
            ParsekLog.Info("Trace-Sep", "------- LIVE WINDOW (next " + RecordingWindowSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s) -------");
        }

        /// <summary>
        /// Open a playback-side window when a debris ghost first activates.
        /// Idempotent: calling repeatedly just refreshes the window.
        /// </summary>
        internal static void OpenPlaybackWindow(string trigger)
        {
            double startUT;
            try { startUT = Planetarium.GetUniversalTime(); }
            catch { startUT = 0.0; }
            lock (gate)
            {
                playbackWindowEndUT = startUT + PlaybackWindowSeconds;
            }
            ParsekLog.Info("Trace-Sep",
                "=============== PLAYBACK WINDOW OPEN ===============");
            ParsekLog.Info("Trace-Sep",
                string.Format(CultureInfo.InvariantCulture,
                    "Trigger: {0} | nowUT={1:R} | windowEndUT={2:R}",
                    trigger, startUT, startUT + PlaybackWindowSeconds));
        }

        internal static bool RecordingWindowActive
        {
            get
            {
                double now;
                try { now = Planetarium.GetUniversalTime(); }
                catch { return false; }
                return now <= recordingWindowEndUT;
            }
        }

        internal static bool PlaybackWindowActive
        {
            get
            {
                double now;
                try { now = Planetarium.GetUniversalTime(); }
                catch { return false; }
                return now <= playbackWindowEndUT;
            }
        }

        /// <summary>
        /// Log a line immediately if a recording window is active; otherwise
        /// push it to the pre-event ring buffer for retroactive flush.
        /// </summary>
        internal static void RecordLog(string subsystem, string msg)
        {
            if (RecordingWindowActive)
                ParsekLog.Info("Trace-Sep", "LIVE " + FormatLine(subsystem, msg));
            else
                Prelog(subsystem, msg);
        }

        /// <summary>
        /// Log a line only if a playback window is active. Pre-event
        /// buffering does not make sense for playback events.
        /// </summary>
        internal static void PlaybackLog(string subsystem, string msg)
        {
            if (PlaybackWindowActive)
                ParsekLog.Info("Trace-Sep", "PLAY " + FormatLine(subsystem, msg));
        }

        private static string FormatLine(string subsystem, string msg)
        {
            double ut;
            float fdt, fixedT;
            int fc;
            string inFixed;
            try { ut = Planetarium.GetUniversalTime(); } catch { ut = double.NaN; }
            try { fdt = Time.fixedDeltaTime; } catch { fdt = float.NaN; }
            try { fixedT = Time.fixedTime; } catch { fixedT = float.NaN; }
            try { fc = Time.frameCount; } catch { fc = -1; }
            // Time.inFixedTimeStep distinguishes the FixedUpdate phase (where
            // VesselPrecalculate.CalculatePhysicsStats and our per-tick recorder
            // postfix run, pre-PhysX) from the post-physics callback phase
            // (where OnJointBreak / OnCollision fire after PhysX has advanced
            // transform.position by one tick). Pinning this on every trace
            // line lets log readers see at a glance whether a capture site
            // saw start-of-tick or end-of-tick vessel state.
            try { inFixed = Time.inFixedTimeStep ? "T" : "F"; } catch { inFixed = "?"; }
            return string.Format(CultureInfo.InvariantCulture,
                "[{0}] ut={1:R} fixedT={2:R} dt={3:R} frame={4} inFixed={5} | {6}",
                subsystem, ut, fixedT, fdt, fc, inFixed, msg);
        }

        /// <summary>
        /// Manual lerp-alpha helper used by playback traces to log the
        /// interpolation factor InterpolateAndPosition computed implicitly.
        /// Returns NaN for degenerate brackets so the log row stays parseable.
        /// </summary>
        internal static double ComputeLerpAlpha(double beforeUT, double afterUT, double playbackUT)
        {
            double span = afterUT - beforeUT;
            if (double.IsNaN(span) || double.IsInfinity(span) || span <= 0.0)
                return double.NaN;
            return (playbackUT - beforeUT) / span;
        }

        /// <summary>
        /// Pure helper: returns the magnitude of the anchor-local offset vector
        /// at <paramref name="playbackUT"/> linearly interpolated between the
        /// bracketing entries of <paramref name="frames"/>. In v13 Relative
        /// debris sections, <c>frames[i].latitude/longitude/altitude</c> are
        /// anchor-local Cartesian metres (NOT body-fixed lat/lon/alt), so the
        /// magnitude of the lerped vector is the parent-vs-debris distance the
        /// recorder authored for the current playback UT.
        ///
        /// Unlike the existing <c>recordedAnchorLocalDist</c> trace field (which
        /// reads only the bracketing-before entry's magnitude), this helper
        /// follows the recorded geometry across the bracket so a reader can
        /// tell whether the rendered distance is tracking the recorded
        /// relative motion or actually diverging from it.
        ///
        /// Returns NaN when the input is null/empty, when playbackUT is before
        /// the first sample, or when a one-sample list is provided (we cannot
        /// interpolate without two real samples; clamping to a single sample
        /// would lie about coverage). When playbackUT is at or after the last
        /// sample we return the last sample's magnitude (clamp-on-overrun is
        /// safe — the caller already passes the section's `frames` and knows
        /// the bracket ends).
        /// </summary>
        internal static double InterpolateAnchorLocalOffsetMagnitude(
            IReadOnlyList<TrajectoryPoint> frames, double playbackUT)
        {
            if (frames == null || frames.Count < 2)
                return double.NaN;
            if (double.IsNaN(playbackUT) || double.IsInfinity(playbackUT))
                return double.NaN;

            int beforeIdx = -1;
            int afterIdx = -1;
            for (int j = 0; j < frames.Count; j++)
            {
                if (frames[j].ut <= playbackUT)
                    beforeIdx = j;
                if (frames[j].ut >= playbackUT && afterIdx < 0)
                    afterIdx = j;
            }

            if (beforeIdx < 0)
                return double.NaN; // playbackUT before first sample
            if (afterIdx < 0)
            {
                // playbackUT past the last sample: return last-sample magnitude
                // (caller's bracket has ended; this matches the existing
                // "use the bracketing-before only" behavior at the right edge).
                TrajectoryPoint last = frames[frames.Count - 1];
                return new Vector3d(last.latitude, last.longitude, last.altitude).magnitude;
            }
            if (afterIdx == beforeIdx)
            {
                // Exact-hit on a sample, or single-sample-bracket edge.
                TrajectoryPoint p = frames[beforeIdx];
                return new Vector3d(p.latitude, p.longitude, p.altitude).magnitude;
            }

            TrajectoryPoint b = frames[beforeIdx];
            TrajectoryPoint a = frames[afterIdx];
            double alpha = ComputeLerpAlpha(b.ut, a.ut, playbackUT);
            if (double.IsNaN(alpha))
            {
                // Degenerate bracket (zero/negative span). Fall back to the
                // bracketing-before magnitude rather than producing a NaN log
                // value that hides the real number.
                return new Vector3d(b.latitude, b.longitude, b.altitude).magnitude;
            }

            Vector3d offB = new Vector3d(b.latitude, b.longitude, b.altitude);
            Vector3d offA = new Vector3d(a.latitude, a.longitude, a.altitude);
            Vector3d interp = offB + (offA - offB) * alpha;
            return interp.magnitude;
        }

        internal static string FormatVector3d(Vector3d v)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "({0:R}, {1:R}, {2:R})", v.x, v.y, v.z);
        }

        internal static string FormatVector3(Vector3 v)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "({0:R}, {1:R}, {2:R})", v.x, v.y, v.z);
        }
    }
}
