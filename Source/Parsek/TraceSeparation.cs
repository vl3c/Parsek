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
        private static readonly object gate = new object();

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
            float ft, fdt, fixedT;
            int fc;
            try { ut = Planetarium.GetUniversalTime(); } catch { ut = double.NaN; }
            try { ft = Time.time; } catch { ft = float.NaN; }
            try { fdt = Time.fixedDeltaTime; } catch { fdt = float.NaN; }
            try { fixedT = Time.fixedTime; } catch { fixedT = float.NaN; }
            try { fc = Time.frameCount; } catch { fc = -1; }
            return string.Format(CultureInfo.InvariantCulture,
                "[{0}] ut={1:R} fixedT={2:R} dt={3:R} frame={4} | {5}",
                subsystem, ut, fixedT, fdt, fc, msg);
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
