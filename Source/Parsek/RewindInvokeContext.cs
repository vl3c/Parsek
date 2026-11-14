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
    /// (<see cref="RewindLauncher.InitiateRewind"/>).
    /// </para>
    /// </summary>
    internal static class RewindInvokeContext
    {
        // The load-transition state: every field below. Clear (the Cold reset among its
        // callers) replaces the whole object, so a field added here is cleared with the rest.
        private sealed class ColdResetState
        {
            internal bool Pending;
            internal string SessionId;
            internal RewindPoint RewindPoint;
            internal ChildSlot Selected;
            internal string RewindPointId;
            internal int SelectedSlotIndex;
            internal string SelectedOriginChildRecordingId;
            internal ReconciliationBundle CapturedBundle;
            internal bool HasCapturedBundle;
            internal string TempQuicksavePath;
            internal EconomySnapshot? ReadbackPreRewind;
        }

        private static ColdResetState state = new ColdResetState();

        /// <summary>True while a re-fly invocation is in flight (between
        /// <see cref="RewindInvoker.StartInvoke"/> and the post-load consumer).
        /// </summary>
        internal static bool Pending { get => state.Pending; set => state.Pending = value; }

        /// <summary>Unique session id generated pre-load.</summary>
        internal static string SessionId { get => state.SessionId; set => state.SessionId = value; }

        /// <summary>The invoked <see cref="RewindPoint"/>.</summary>
        internal static RewindPoint RewindPoint { get => state.RewindPoint; set => state.RewindPoint = value; }

        /// <summary>The selected child slot.</summary>
        internal static ChildSlot Selected { get => state.Selected; set => state.Selected = value; }

        /// <summary>RP id (denormalised for easy access / logging even if
        /// <see cref="RewindPoint"/> is null).</summary>
        internal static string RewindPointId { get => state.RewindPointId; set => state.RewindPointId = value; }

        /// <summary>Selected slot index (denormalised for logging).</summary>
        internal static int SelectedSlotIndex { get => state.SelectedSlotIndex; set => state.SelectedSlotIndex = value; }

        /// <summary>Selected slot origin recording id (denormalised for logging).</summary>
        internal static string SelectedOriginChildRecordingId { get => state.SelectedOriginChildRecordingId; set => state.SelectedOriginChildRecordingId = value; }

        /// <summary>Pre-load reconciliation snapshot; restored post-load to
        /// re-install the in-memory state the .sfs reload blew away.</summary>
        internal static ReconciliationBundle CapturedBundle { get => state.CapturedBundle; set => state.CapturedBundle = value; }

        /// <summary>True once <see cref="CapturedBundle"/> is valid.</summary>
        internal static bool HasCapturedBundle { get => state.HasCapturedBundle; set => state.HasCapturedBundle = value; }

        /// <summary>Absolute path of the root-level temp quicksave copy that the
        /// post-load consumer must delete after successful consumption.</summary>
        internal static string TempQuicksavePath { get => state.TempQuicksavePath; set => state.TempQuicksavePath = value; }

        /// <summary>
        /// Live career economy captured in <see cref="RewindInvoker.StartInvoke"/> BEFORE
        /// the <c>GamePersistence.LoadGame</c> — the "career state sticks" pre-rewind
        /// witness (E_before) for the rewind read-back divergence guard. Survives LoadGame
        /// because this context is static; RUNTIME-ONLY (never serialized — the rewind
        /// completes within one process). Cleared with the rest of the context in
        /// <see cref="Clear"/> so the guard never carries a stale witness.
        /// </summary>
        internal static EconomySnapshot? ReadbackPreRewind { get => state.ReadbackPreRewind; set => state.ReadbackPreRewind = value; }

        /// <summary>
        /// Clears every field. Idempotent.
        /// </summary>
        internal static void Clear()
        {
            // A cleared context names no slot (-1); the process starts at the field default.
            state = new ColdResetState { SelectedSlotIndex = -1 };
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
