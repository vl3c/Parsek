using System;

namespace Parsek
{
    /// <summary>
    /// Session-scoped holder for a pending "Warp to time" forward jump that must run after
    /// a scene load settles in the Space Center (a rewind reload, or a flight->KSC exit).
    /// NOT serialized: a plain rewind / scene exit completes within one process session
    /// (like <see cref="RewindContext"/>). The <see cref="SessionId"/> stamp lets the
    /// consumer reject a request orphaned across a process restart (e.g. a quickload into
    /// the Space Center), mirroring <see cref="StockActionIntentMarker"/>'s staleness guard.
    /// </summary>
    internal static class WarpToTimeRequest
    {
        private const string Tag = "WarpTime";

        internal static bool HasPending { get; private set; }
        internal static double TargetUT { get; private set; }
        internal static Guid SessionId { get; private set; }

        /// <summary>Arms a pending forward-warp to <paramref name="targetUT"/>.</summary>
        internal static void Set(double targetUT)
        {
            HasPending = true;
            TargetUT = targetUT;
            SessionId = ParsekProcess.ProcessSessionId;
            ParsekLog.Info(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Pending warp armed: targetUT={0:F1} session={1}", targetUT, SessionId));
        }

        /// <summary>Clears the pending request.</summary>
        internal static void Clear()
        {
            if (!HasPending) return;
            ParsekLog.Verbose(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Pending warp cleared: targetUT={0:F1}", TargetUT));
            HasPending = false;
            TargetUT = 0;
            SessionId = Guid.Empty;
        }

        /// <summary>
        /// True when the request was armed by a different process session (orphaned across
        /// a restart). The consumer clears such a request without acting on it.
        /// </summary>
        internal static bool IsStale()
        {
            return HasPending && SessionId != ParsekProcess.ProcessSessionId;
        }

        // ---- Deferred flight warp ------------------------------------------------------
        // The flight path defers the WHOLE warp to the next Space Center arrival so the
        // scene-exit Merge/Discard dialog (SceneExitInterceptor) handles the active recording
        // first - we never auto-commit. The entered date is re-resolved and executed KSC-side
        // (WarpToTimeController.ExecuteAtKsc) with no further confirmation.

        internal static bool HasDeferredKscWarp { get; private set; }
        internal static int DeferYear { get; private set; }
        internal static int DeferDay { get; private set; }
        internal static int DeferHour { get; private set; }
        internal static int DeferMinute { get; private set; }
        private static Guid deferSessionId;

        internal static void SetDeferredKscWarp(int year, int day, int hour, int minute)
        {
            HasDeferredKscWarp = true;
            DeferYear = year;
            DeferDay = day;
            DeferHour = hour;
            DeferMinute = minute;
            deferSessionId = ParsekProcess.ProcessSessionId;
            ParsekLog.Info(Tag,
                $"Deferred flight warp armed: Y{year} D{day} {hour}:{minute:00} session={deferSessionId}");
        }

        internal static void ClearDeferredKscWarp()
        {
            if (!HasDeferredKscWarp) return;
            ParsekLog.Verbose(Tag, "Deferred flight warp cleared");
            HasDeferredKscWarp = false;
            DeferYear = DeferDay = DeferHour = DeferMinute = 0;
            deferSessionId = Guid.Empty;
        }

        internal static bool IsDeferredKscWarpStale()
        {
            return HasDeferredKscWarp && deferSessionId != ParsekProcess.ProcessSessionId;
        }

        internal static void ResetForTesting()
        {
            HasPending = false;
            TargetUT = 0;
            SessionId = Guid.Empty;
            HasDeferredKscWarp = false;
            DeferYear = DeferDay = DeferHour = DeferMinute = 0;
            deferSessionId = Guid.Empty;
        }
    }
}
