using System.Diagnostics;

namespace Parsek.Rendering
{
    internal static class NonLoopLivePidGuard
    {
        private static int lookupAttempts;

        internal static int LivePidLookupAttemptsForTesting => lookupAttempts;

        [Conditional("DEBUG")]
        internal static void NonLoopRelativeLivePidLookupAttempted(string context)
        {
            lookupAttempts++;
            ParsekLog.Warn(
                "Anchor",
                "non-loop-relative-live-pid-lookup-attempted context=" + (context ?? "(none)"));
        }

        internal static void ResetForTesting()
        {
            lookupAttempts = 0;
        }
    }
}
