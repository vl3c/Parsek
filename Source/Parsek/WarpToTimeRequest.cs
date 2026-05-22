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

        internal static void ResetForTesting()
        {
            HasPending = false;
            TargetUT = 0;
            SessionId = Guid.Empty;
        }
    }
}
