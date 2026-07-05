namespace Parsek.Logistics
{
    /// <summary>
    /// Pure, Unity-free classifier for the Rec-3 OBSERVABILITY scope of the
    /// logistics &lt;-&gt; time-rewind determinism work (plan
    /// <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c> Phase 4;
    /// report <c>docs/dev/research/logistics-time-rewind-compat-report.md</c>
    /// risk #6, Medium-High).
    ///
    /// <para><b>The leak this classifies (NOT the leak it fixes).</b> A recurring
    /// supply route fires LIVE on a UT-pinned loop grid and physically mutates the
    /// world through three writers that have <b>no reverse method</b>:
    /// <see cref="LiveDeliveryWriters"/> (<c>WriteResource</c> / <c>WriteInventory</c>),
    /// <see cref="LiveOriginDebitWriters"/> (<c>WriteResourceDebit</c>), and
    /// <see cref="LiveInventoryPickupWriter"/> (<c>RemoveOne</c>). Their <b>only</b>
    /// rollback is the Rewind-to-Separation quicksave (a full-world
    /// <c>GamePersistence.LoadGame</c>): there is no per-writer undo, the
    /// <c>ReconciliationBundle</c> does not snapshot route state, and the recalc only
    /// reconstructs the funds scalar — never the physical tanks/inventory.</para>
    ///
    /// <para>So if a route physically fires <b>inside a segment the player then
    /// discards WITHOUT a rewind</b>, that mutation leaks into the surviving timeline
    /// with no rollback path. The discard cores
    /// (<c>RecordingStore.DiscardPendingTree</c> /
    /// <c>TryDiscardActiveSwitchSegmentAttempt</c> /
    /// <c>ParsekFlight.AutoDiscardActiveTreeCore</c>) and
    /// <c>MergeDialog.ReFlyDiscard</c> hold no quicksave —
    /// <c>MergeDialog.ReFlyDiscard</c> reverts purely via
    /// <c>LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions</c>
    /// (NO <c>LoadGame</c>), so the physical effect is never undone there either.</para>
    ///
    /// <para><b>Scope of THIS file (observability only — the full fix is DEFERRED).</b>
    /// This is a pure, testable predicate that answers "does this route physical
    /// mutation currently have a quicksave-backed revert path?" so a writer call site
    /// can emit a Warn when it does not (the leak is observable in KSP.log). It is
    /// deliberately NOT wired into the production writers: gating the real writers
    /// needs runtime context (the live rewind/RP-session state, the active-segment
    /// discardability) and IS the deferred full Rec-3 fix (either reverse-on-discard
    /// writers, or disabling physical route mutation for any revert lacking a
    /// quicksave). Per the plan, do not wire this in here.</para>
    ///
    /// <para><b>ERS/ELS grep-gate:</b> takes its inputs BY PARAMETER (plain booleans)
    /// and reads neither the static ledger action list nor the committed-recordings
    /// store, so it is not a grep-gate concern and needs no allowlist entry.</para>
    /// </summary>
    internal static class RouteRevertSafety
    {
        /// <summary>
        /// True when a route physical mutation that fires under the current
        /// segment HAS a quicksave-backed revert path, false when it does not
        /// (the un-revertable case where the leak lives).
        ///
        /// <para><b>Semantics.</b> Revert-safety for the no-reverse-method writers
        /// is entirely "is the full-world quicksave restore reachable for this
        /// segment?":</para>
        /// <list type="bullet">
        ///   <item><description><paramref name="rewindOrRpBackedSessionActive"/> —
        ///   the live session was entered through a Rewind-to-Separation invoke and
        ///   still owns the originating RewindPoint quicksave (the rewind path can
        ///   roll the whole world back to RP UT). When true, the mutation is
        ///   revertable: a subsequent rewind would undo the physical write with the
        ///   rest of the world.</description></item>
        ///   <item><description><paramref name="inFlightNonRewindSegment"/> — the
        ///   mutation fired inside a plain in-flight segment that the player can
        ///   discard WITHOUT any quicksave (a normal recording segment, a switch/Fly
        ///   continuation segment, or a Re-Fly discard whose revert is a recalc, not
        ///   a <c>LoadGame</c>). When true with no rewind backing, the physical write
        ///   has no rollback path — this is the leak.</description></item>
        /// </list>
        ///
        /// <para>The rewind/RP backing is the AUTHORITY: if a quicksave-backed revert
        /// is available, the mutation is revertable regardless of the in-flight flag
        /// (the rewind restore reverts the whole world, segment or not). Only the
        /// "in-flight, non-rewind, no quicksave" cell is un-revertable.</para>
        /// </summary>
        /// <param name="rewindOrRpBackedSessionActive">
        /// True when a Rewind-to-Separation / RewindPoint-backed session is active
        /// for this mutation (a full-world quicksave revert is reachable).
        /// </param>
        /// <param name="inFlightNonRewindSegment">
        /// True when the mutation fired inside a non-rewind in-flight segment that can
        /// be discarded without a quicksave.
        /// </param>
        /// <returns>
        /// True if the mutation can be reverted via a quicksave restore; false if it
        /// would leak (a non-rewind in-flight segment with no quicksave backing).
        /// </returns>
        internal static bool PhysicalMutationRevertable(
            bool rewindOrRpBackedSessionActive,
            bool inFlightNonRewindSegment)
        {
            // Rewind/RP backing wins: a reachable full-world quicksave revert makes
            // the mutation revertable no matter what segment it fired under.
            if (rewindOrRpBackedSessionActive)
                return true;

            // No quicksave backing. The mutation is un-revertable precisely when it
            // fired inside a non-rewind in-flight segment that the player can discard
            // without a quicksave — that is the leak the full Rec-3 fix targets.
            // Outside such a segment (e.g. a committed timeline with no pending
            // discardable segment) there is nothing to discard, so it is treated as
            // revertable (the normal-play case: routes ARE meant to mutate and the
            // mutation is part of the durable, non-discardable timeline).
            return !inFlightNonRewindSegment;
        }
    }
}
