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
    /// Within one process a request belongs to the game it was armed in: a failed rewind
    /// launch drops the pending request, and a Cold load or the main-menu transition drops
    /// both (<see cref="DropAll"/>).
    /// </summary>
    internal static class WarpToTimeRequest
    {
        private const string Tag = "WarpTime";

        // The save-scoped state: both requests. A Cold load and the main-menu transition
        // replace the whole object (DropAll), so a field added here is dropped with the rest.
        private sealed class ColdResetState
        {
            internal bool HasPending;
            internal double TargetUT;
            internal Guid SessionId;
            internal bool HasDeferredKscWarp;
            internal int DeferYear;
            internal int DeferDay;
            internal int DeferHour;
            internal int DeferMinute;
            internal Guid DeferSessionId;
        }

        private static ColdResetState state = new ColdResetState();

        internal static bool HasPending { get => state.HasPending; private set => state.HasPending = value; }
        internal static double TargetUT { get => state.TargetUT; private set => state.TargetUT = value; }
        internal static Guid SessionId { get => state.SessionId; private set => state.SessionId = value; }

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
        /// Clears the pending request because it will never be consumed for the game it was
        /// armed in (its rewind did not launch). Logged at Info with the target and the reason;
        /// returns whether a request was armed.
        /// </summary>
        internal static bool DropPending(string reason)
        {
            if (!HasPending) return false;
            ParsekLog.Info(Tag,
                string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "Pending warp dropped: targetUT={0:F1} reason={1}",
                    TargetUT, reason ?? "unspecified"));
            HasPending = false;
            TargetUT = 0;
            SessionId = Guid.Empty;
            return true;
        }

        /// <summary>
        /// Drops both requests because the game they were armed for is gone: a Cold load
        /// (<c>ParsekScenario.ResetSaveScopedSessionState</c>) or the main-menu transition.
        /// Neither runs between a request's arm and its consume on the Warp to time paths: a
        /// plain rewind's load classifies PlainRewind and a flight to Space Center exit
        /// classifies in-session (<see cref="LoadReconcilePolicy.ClassifyEarly"/>), and both
        /// load the Space Center, never the main menu. Returns how many were dropped.
        /// </summary>
        internal static int DropAll(string reason)
        {
            int dropped = DropPending(reason) ? 1 : 0;
            if (HasDeferredKscWarp)
            {
                ParsekLog.Info(Tag,
                    $"Deferred flight warp dropped: Y{DeferYear} D{DeferDay} {DeferHour}:{DeferMinute:00} " +
                    $"reason={reason ?? "unspecified"}");
                dropped++;
            }
            state = new ColdResetState();
            return dropped;
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

        internal static bool HasDeferredKscWarp { get => state.HasDeferredKscWarp; private set => state.HasDeferredKscWarp = value; }
        internal static int DeferYear { get => state.DeferYear; private set => state.DeferYear = value; }
        internal static int DeferDay { get => state.DeferDay; private set => state.DeferDay = value; }
        internal static int DeferHour { get => state.DeferHour; private set => state.DeferHour = value; }
        internal static int DeferMinute { get => state.DeferMinute; private set => state.DeferMinute = value; }

        internal static void SetDeferredKscWarp(int year, int day, int hour, int minute)
        {
            HasDeferredKscWarp = true;
            DeferYear = year;
            DeferDay = day;
            DeferHour = hour;
            DeferMinute = minute;
            state.DeferSessionId = ParsekProcess.ProcessSessionId;
            ParsekLog.Info(Tag,
                $"Deferred flight warp armed: Y{year} D{day} {hour}:{minute:00} session={state.DeferSessionId}");
        }

        internal static void ClearDeferredKscWarp()
        {
            if (!HasDeferredKscWarp) return;
            ParsekLog.Verbose(Tag, "Deferred flight warp cleared");
            HasDeferredKscWarp = false;
            DeferYear = DeferDay = DeferHour = DeferMinute = 0;
            state.DeferSessionId = Guid.Empty;
        }

        internal static bool IsDeferredKscWarpStale()
        {
            return HasDeferredKscWarp && state.DeferSessionId != ParsekProcess.ProcessSessionId;
        }

        internal static void ResetForTesting()
        {
            state = new ColdResetState();
        }
    }
}
