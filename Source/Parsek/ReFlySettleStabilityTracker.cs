using System;
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
        private static string lastSettleActiveRecordingId;
        private static int lastSettleActiveFrame = NoFrame;
        private static string lastSettleClearedRecordingId;
        private static int lastSettleClearedFrame = NoFrame;

        internal static void Reset()
        {
            lastFloatingOriginShiftFrame = NoFrame;
            lastFloatingOriginShiftRefPos = Vector3d.zero;
            lastFloatingOriginShiftNonFrame = Vector3d.zero;
            lastSettleActiveRecordingId = null;
            lastSettleActiveFrame = NoFrame;
            lastSettleClearedRecordingId = null;
            lastSettleClearedFrame = NoFrame;
        }

        internal static void RecordSettleActive(string recordingId, int frame)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lastSettleActiveRecordingId = recordingId;
            lastSettleActiveFrame = frame;
        }

        internal static void RecordSettleCleared(string recordingId, int frame)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lastSettleClearedRecordingId = recordingId;
            lastSettleClearedFrame = frame;
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

            Vector3d offsetNonKrakensbane = refPos + nonFrame;
            double magnitude = Math.Sqrt(offsetNonKrakensbane.sqrMagnitude);
            string wallclock = realtimeSinceStartup >= 0f
                ? realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture)
                : "(n/a)";
            string message =
                $"FloatingOrigin.setOffset refPos={DiagnosticFormatters.FormatVector3d(refPos)} " +
                $"nonFrame={DiagnosticFormatters.FormatVector3d(nonFrame)} " +
                $"offsetNonKrakensbane={DiagnosticFormatters.FormatVector3d(offsetNonKrakensbane)} " +
                $"magnitude={magnitude.ToString("F2", CultureInfo.InvariantCulture)} " +
                $"wallclock={wallclock} " +
                $"frame={frame.ToString(CultureInfo.InvariantCulture)}";
            if (HasRecentSettleActivity(frame))
                ParsekLog.Info("ReFlySettle", message);
            else
                ParsekLog.Verbose("ReFlySettle", message);
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

            if (IsRecentMatch(recordingId, lastSettleClearedRecordingId, lastSettleClearedFrame, frame,
                    FlightRecorder.StabilitySettleClearHoldFrames))
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
            return IsRecentMatch(recordingId, lastSettleClearedRecordingId, lastSettleClearedFrame,
                    lastFloatingOriginShiftFrame, FlightRecorder.StabilitySettleClearHoldFrames)
                || IsRecentMatch(recordingId, lastSettleActiveRecordingId, lastSettleActiveFrame,
                    lastFloatingOriginShiftFrame, FlightRecorder.StabilitySettleClearHoldFrames);
        }

        private static bool HasRecentSettleActivity(int frame)
        {
            return IsRecentFrame(lastSettleClearedFrame, frame, FlightRecorder.StabilitySettleClearHoldFrames)
                || IsRecentFrame(lastSettleActiveFrame, frame, FlightRecorder.StabilitySettleClearHoldFrames);
        }

        private static bool IsRecentFrame(int candidateFrame, int frame, int windowFrames)
        {
            return candidateFrame != NoFrame
                && frame >= candidateFrame
                && frame - candidateFrame <= windowFrames;
        }

        private static bool IsRecentMatch(
            string recordingId,
            string candidateRecordingId,
            int candidateFrame,
            int frame,
            int windowFrames)
        {
            return candidateFrame != NoFrame
                && frame >= candidateFrame
                && frame - candidateFrame <= windowFrames
                && string.Equals(recordingId, candidateRecordingId, StringComparison.Ordinal);
        }

        private static string ShortId(string id)
        {
            if (string.IsNullOrEmpty(id)) return "(none)";
            return id.Length <= 8 ? id : id.Substring(0, 8);
        }
    }
}
