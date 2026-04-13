namespace Parsek.InGameTests.Helpers
{
    /// <summary>
    /// Guards and cleanup helpers for tests that call live ParsekScenario.OnSave/OnLoad
    /// inside an already-running KSP scene.
    /// </summary>
    internal static class SyntheticScenarioLoadHelpers
    {
        internal static void EnsureRoundTripSafeToRun()
        {
            if (RecordingStore.HasPendingTree || ParsekScenario.MergeDialogPending)
            {
                InGameAssert.Skip(
                    "Live session has pending merge state; OnSave/OnLoad round-trip would mutate it");
            }

            var flight = ParsekFlight.Instance;
            if (flight != null
                && (flight.IsRecording
                    || flight.HasActiveTree
                    || flight.IsPlaying
                    || flight.IsWatchingGhost
                    || flight.TimelineGhostCount > 0
                    || flight.HasDeferredWatchAfterFastForward))
            {
                InGameAssert.Skip(
                    "Live FLIGHT playback/watch state is active; OnSave/OnLoad round-trip would mutate it");
            }
        }

        internal static void CleanupFlightRuntime(string reason)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT)
                return;

            var flight = ParsekFlight.Instance;
            if (flight == null)
                return;

            flight.NormalizeAfterSyntheticScenarioLoad(reason);
        }
    }
}
