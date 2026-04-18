namespace Parsek
{
    /// <summary>
    /// Phase 6 of Rewind-to-Staging (design §6.3 + §6.4): static holder carrying
    /// re-fly invocation state across the KSP scene reload that
    /// <see cref="RewindInvoker"/> triggers via
    /// <c>GamePersistence.LoadGame</c> + <c>HighLogic.LoadScene(FLIGHT)</c>.
    ///
    /// <para>
    /// The scene reload destroys the live <see cref="ParsekScenario"/>
    /// MonoBehaviour and its coroutines, so any state the post-load pipeline
    /// needs must be parked on statics. The invoker populates this context
    /// synchronously before calling LoadGame; the new scenario's
    /// <see cref="ParsekScenario.OnLoad"/> consumes it, runs the §6.4 post-load
    /// pipeline (Restore → Strip → Activate → AtomicMarkerWrite), and calls
    /// <see cref="Clear"/> when done.
    /// </para>
    ///
    /// <para>
    /// Mirrors the prior-art pattern in <see cref="RewindContext"/>, which carries
    /// legacy rewind-to-launch state across the same kind of scene reload
    /// (<see cref="RecordingStore.InitiateRewind"/>).
    /// </para>
    /// </summary>
    internal static class RewindInvokeContext
    {
        /// <summary>True while a re-fly invocation is in flight (between
        /// <see cref="RewindInvoker.StartInvoke"/> and the post-load consumer).
        /// </summary>
        internal static bool Pending;

        /// <summary>Unique session id generated pre-load.</summary>
        internal static string SessionId;

        /// <summary>The invoked <see cref="RewindPoint"/>.</summary>
        internal static RewindPoint RewindPoint;

        /// <summary>The selected child slot.</summary>
        internal static ChildSlot Selected;

        /// <summary>RP id (denormalised for easy access / logging even if
        /// <see cref="RewindPoint"/> is null).</summary>
        internal static string RewindPointId;

        /// <summary>Selected slot index (denormalised for logging).</summary>
        internal static int SelectedSlotIndex;

        /// <summary>Selected slot origin recording id (denormalised for logging).</summary>
        internal static string SelectedOriginChildRecordingId;

        /// <summary>Pre-load reconciliation snapshot; restored post-load to
        /// re-install the in-memory state the .sfs reload blew away.</summary>
        internal static ReconciliationBundle CapturedBundle;

        /// <summary>True once <see cref="CapturedBundle"/> is valid.</summary>
        internal static bool HasCapturedBundle;

        /// <summary>Absolute path of the root-level temp quicksave copy that the
        /// post-load consumer must delete after successful consumption.</summary>
        internal static string TempQuicksavePath;

        /// <summary>
        /// Clears every field. Idempotent.
        /// </summary>
        internal static void Clear()
        {
            Pending = false;
            SessionId = null;
            RewindPoint = null;
            Selected = null;
            RewindPointId = null;
            SelectedSlotIndex = -1;
            SelectedOriginChildRecordingId = null;
            CapturedBundle = default(ReconciliationBundle);
            HasCapturedBundle = false;
            TempQuicksavePath = null;
        }

        /// <summary>
        /// Resets all state without logging. For unit tests only.
        /// </summary>
        internal static void ResetForTesting()
        {
            Clear();
        }
    }
}
