using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Parsek
{
    internal static class ReFlySettleStabilityTracker
    {
        private const int NoFrame = int.MinValue;

        private static int lastFloatingOriginShiftFrame = NoFrame;
        private static Vector3d lastFloatingOriginShiftRefPos;
        private static Vector3d lastFloatingOriginShiftNonFrame;
        private static readonly Dictionary<string, int> lastSettleActiveFrameByRecording =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> lastSettleClearedFrameByRecording =
            new Dictionary<string, int>(StringComparer.Ordinal);

        internal static void Reset()
        {
            lastFloatingOriginShiftFrame = NoFrame;
            lastFloatingOriginShiftRefPos = Vector3d.zero;
            lastFloatingOriginShiftNonFrame = Vector3d.zero;
            lastSettleActiveFrameByRecording.Clear();
            lastSettleClearedFrameByRecording.Clear();
        }

        internal static void RecordSettleActive(string recordingId, int frame)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lastSettleActiveFrameByRecording[recordingId] = frame;
        }

        internal static void RecordSettleCleared(string recordingId, int frame)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lastSettleClearedFrameByRecording[recordingId] = frame;
            ParsekLog.Verbose("ReFlySettle",
                $"Settle clear recorded: rec={ShortId(recordingId)} frame={frame.ToString(CultureInfo.InvariantCulture)}");
        }

        internal static void RecordFloatingOriginShift(
            Vector3d refPos,
            Vector3d nonFrame,
            int frame,
            float realtimeSinceStartup = -1f)
        {
            lastFloatingOriginShiftFrame = frame;
            lastFloatingOriginShiftRefPos = refPos;
            lastFloatingOriginShiftNonFrame = nonFrame;

            // The shift STATE above is updated unconditionally (it feeds
            // LastFloatingOriginShiftFrame, which the GhostRenderTrace large-delta
            // detector reads). Only the diagnostic LINE is conditional. Build the
            // message lazily so the dominant non-settle path pays no string-format
            // cost on the frames the rate limiter suppresses.
            Func<string> buildMessage = () =>
            {
                Vector3d offsetNonKrakensbane = refPos + nonFrame;
                double magnitude = Math.Sqrt(offsetNonKrakensbane.sqrMagnitude);
                string wallclock = realtimeSinceStartup >= 0f
                    ? realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture)
                    : "(n/a)";
                return
                    $"FloatingOrigin.setOffset refPos={DiagnosticFormatters.FormatVector3d(refPos)} " +
                    $"nonFrame={DiagnosticFormatters.FormatVector3d(nonFrame)} " +
                    $"offsetNonKrakensbane={DiagnosticFormatters.FormatVector3d(offsetNonKrakensbane)} " +
                    $"magnitude={magnitude.ToString("F2", CultureInfo.InvariantCulture)} " +
                    $"wallclock={wallclock} " +
                    $"frame={frame.ToString(CultureInfo.InvariantCulture)}";
            };

            if (HasRecentSettleActivity(frame))
            {
                // Inside an actual re-fly settle window every floating-origin shift is
                // diagnostically valuable, so emit unconditionally at INFO.
                ParsekLog.Info("ReFlySettle", buildMessage());
            }
            else
            {
                // Outside a settle window FloatingOrigin.setOffset fires on (nearly) every
                // physics frame the world re-centres. This was by far the largest log-spam
                // source in long sessions: ~185k lines (~56% of all verbose output) in the
                // 2026-06-07 career playtest, none of it from a re-fly. Throttle to a
                // shared-key heartbeat plus a "suppressed=N" tail so the patch is still
                // observably alive and a sample magnitude is retained.
                ParsekLog.VerboseRateLimited(
                    "ReFlySettle", "floating-origin-shift", buildMessage, 30.0);
            }
        }

        /// <summary>
        /// Frame counter of the most recent stock `FloatingOrigin.setOffset`
        /// observed by the postfix patch, or `int.MinValue` if no shift has
        /// been observed yet. Read by diagnostic-only consumers (e.g. the
        /// `GhostRenderTrace` large-delta detector) that need to suppress
        /// per-frame anomaly attribution on frames where the world coordinates
        /// shifted under all ghosts simultaneously.
        /// </summary>
        internal static int LastFloatingOriginShiftFrame =>
            lastFloatingOriginShiftFrame;

        /// <summary>
        /// Test seam: production reads `lastFloatingOriginShiftFrame` directly
        /// via the property above; xUnit needs to inject a synthetic value
        /// without going through `RecordFloatingOriginShift` (which logs).
        /// </summary>
        internal static void SetLastFloatingOriginShiftFrameForTesting(int frame)
        {
            lastFloatingOriginShiftFrame = frame;
        }

        internal static bool IsHoldActiveForRecording(string recordingId, int frame)
        {
            string reason;
            return TryGetHoldReasonForRecording(recordingId, frame, out reason);
        }

        internal static bool TryGetHoldReasonForRecording(string recordingId, int frame, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            if (TryGetRecentFrame(
                    lastSettleClearedFrameByRecording,
                    recordingId,
                    frame,
                    FlightRecorder.StabilitySettleClearHoldFrames,
                    out _))
            {
                reason = "clear-hold";
                return true;
            }

            if (lastFloatingOriginShiftFrame != NoFrame
                && frame >= lastFloatingOriginShiftFrame
                && frame - lastFloatingOriginShiftFrame <= FlightRecorder.StabilityExtensionFramesAfterShift
                && IsFloatingOriginShiftRelatedToSettle(recordingId))
            {
                reason = "extension-window";
                return true;
            }

            return false;
        }

        private static bool IsFloatingOriginShiftRelatedToSettle(string recordingId)
        {
            return TryGetRecentFrame(
                    lastSettleClearedFrameByRecording,
                    recordingId,
                    lastFloatingOriginShiftFrame,
                    FlightRecorder.StabilitySettleClearHoldFrames,
                    out _)
                || TryGetRecentFrame(
                    lastSettleActiveFrameByRecording,
                    recordingId,
                    lastFloatingOriginShiftFrame,
                    FlightRecorder.StabilitySettleClearHoldFrames,
                    out _);
        }

        private static bool HasRecentSettleActivity(int frame)
        {
            return HasAnyRecentFrame(lastSettleClearedFrameByRecording, frame,
                    FlightRecorder.StabilitySettleClearHoldFrames)
                || HasAnyRecentFrame(lastSettleActiveFrameByRecording, frame,
                    FlightRecorder.StabilitySettleClearHoldFrames);
        }

        private static bool HasAnyRecentFrame(
            Dictionary<string, int> framesByRecording,
            int frame,
            int windowFrames)
        {
            foreach (int candidateFrame in framesByRecording.Values)
            {
                if (IsRecentFrame(candidateFrame, frame, windowFrames))
                    return true;
            }

            return false;
        }

        private static bool IsRecentFrame(int candidateFrame, int frame, int windowFrames)
        {
            return candidateFrame != NoFrame
                && frame >= candidateFrame
                && frame - candidateFrame <= windowFrames;
        }

        private static bool TryGetRecentFrame(
            Dictionary<string, int> framesByRecording,
            string recordingId,
            int frame,
            int windowFrames,
            out int candidateFrame)
        {
            candidateFrame = NoFrame;
            return !string.IsNullOrEmpty(recordingId)
                && framesByRecording.TryGetValue(recordingId, out candidateFrame)
                && IsRecentFrame(candidateFrame, frame, windowFrames);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(none)";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }
    }
}
