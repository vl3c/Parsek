using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Quickload and revert decisions on load: the quickload discard, the limbo-tree dispatch, the
    // revert pending-tree disposition and the stale pending-state discard.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Pure decision: returns true if the game clock regressed across a
        /// scene transition, i.e. the player quickloaded/reverted/rewound.
        /// <paramref name="preChangeUT"/> is <c>-1.0</c> when unset (no scene
        /// change has been stamped, e.g. first load). The strict <c>&lt; 0.0</c>
        /// check (rather than <c>&lt;= 0.0</c>) preserves the legitimate
        /// <c>preChangeUT == 0.0</c> case for fresh sandbox saves that start at
        /// UT 0 — a scene change in the very first frame must still be able to
        /// detect a backwards quickload. Directly testable without Unity.
        /// </summary>
        internal static bool IsQuickloadOnLoad(
            double preChangeUT, double currentUT, double epsilon)
        {
            if (preChangeUT < 0.0) return false;
            if (epsilon < 0.0) return false;
            return currentUT < preChangeUT - epsilon;
        }

        /// <summary>
        /// Discards a pending tree from an abandoned future / stale context.
        /// #434: previously also cleared FlightResultsPatch deferred-results state; that patch
        /// is gone now. Kept as a single entry point so callers have a named reason-string slot
        /// for logging.
        /// </summary>
        internal static void DiscardPendingTreeAndAbandonDeferredFlightResults(string reason)
        {
            ParsekLog.Verbose("Scenario", $"DiscardPendingTree abandon path: {reason}");
            // Abandon path = quickload-backwards / revert / stale-pending-from-another-save.
            // KSP's economy is being rolled back (or belongs to a different save), so do NOT
            // re-home the discarded tree's contracts/science/milestones into the ledger —
            // that would credit economy KSP no longer reflects (see DiscardPendingTree).
            TreeDiscardService.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);
        }

        /// <summary>
        /// Discards a pending tree after its live effects have already been allowed to stay
        /// in KSP (for example while waiting on a merge/discard decision), then immediately
        /// rebuilds state from the committed ledger.
        /// </summary>
        internal static void DiscardPendingTreeAndRecalculate(string reason)
        {
            if (!PendingTreeSlot.HasPendingTree)
                return;

            ParsekLog.Verbose("Scenario", $"DiscardPendingTree recalc path: {reason}");
            TreeDiscardService.DiscardPendingTree();
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                GetCurrentTimelineUTForLedgerRecalc(),
                "pending-tree-discard");
        }

        /// <summary>
        /// #434 follow-up: dispatch-level guard that decides whether the OnLoad
        /// quickload-discard branch should fire. The pure overload takes the
        /// three scene-classification bits plus the active re-fly session bit so
        /// it can be unit-tested in isolation — the <see cref="OnLoad"/> call
        /// site it gates is itself not reachable from xUnit (ScenarioModule
        /// lifecycle).
        ///
        /// The truth table that matters:
        /// <list type="bullet">
        ///   <item>Pure F5/F9 quickload (<c>isRevert=false</c>, UT back, flight-to-flight): <b>true</b> — hard-discard the stashed-this-transition tree.</item>
        ///   <item>Revert to Launch (<c>isRevert=true</c>, UT back, flight-to-flight): <b>false</b> — the revert branch owns pending-tree handling via <see cref="PendingTreeSlot.UnstashPendingTreeOnRevert"/>, which preserves sidecar files for F9-from-flight-quicksave.</item>
        ///   <item>Active re-fly / retry invoke (<c>isReFlySessionActive=true</c>): <b>false</b> — the session owns the pending tree and rewind point being reloaded.</item>
        ///   <item>Scene reload with same UT: <b>false</b> — nothing to discard.</item>
        /// </list>
        /// </summary>
        internal static bool ShouldRunQuickloadDiscard(
            bool utWentBackwards, bool isFlightToFlight, bool isRevert)
        {
            return ShouldRunQuickloadDiscard(
                utWentBackwards,
                isFlightToFlight,
                isRevert,
                IsReFlySessionActiveForQuickloadDiscard());
        }

        internal static bool ShouldRunQuickloadDiscard(
            bool utWentBackwards,
            bool isFlightToFlight,
            bool isRevert,
            bool isReFlySessionActive)
        {
            return utWentBackwards
                && isFlightToFlight
                && !isRevert
                && !isReFlySessionActive;
        }

        /// <summary>
        /// #434: the OnLoad revert decision. A load is a revert exactly when the
        /// scene classifier did NOT call it a vessel switch and
        /// <see cref="RevertDetector"/> handed back a real revert kind (the
        /// GameEvents hook fires synchronously inside
        /// <c>FlightDriver.RevertTo{Launch,Prelaunch}</c>, before
        /// <c>HighLogic.LoadScene</c>, so the flag is already set when OnLoad runs).
        ///
        /// <para>Pure so it can be unit-tested; the OnLoad call site is not
        /// reachable from xUnit (ScenarioModule lifecycle). The pre-#434 epoch /
        /// recording-count / orphaned-limbo heuristics are NOT part of this
        /// decision any more - they were replaced by the event, and
        /// <c>hasOrphanedLimboTree</c> survives only as a Verbose diagnostic.</para>
        /// </summary>
        internal static bool ComputeIsRevertOnLoad(bool isVesselSwitch, RevertKind revertKind)
        {
            return !isVesselSwitch && revertKind != RevertKind.None;
        }

        /// <summary>
        /// Diagnostic classification used by OnLoad's revert-detection log line
        /// (bug #300): a pending tree that StashActiveTreeAsPendingLimbo put into
        /// Limbo, where the save handed to TryRestoreActiveTreeNode carried no
        /// active tree to overwrite it with. A quickload (F5/F9) always restores
        /// an active tree from the save, so it never reads as orphaned.
        ///
        /// <para>Pure so it can be unit-tested; the OnLoad call site is not
        /// reachable from xUnit (ScenarioModule lifecycle).</para>
        /// </summary>
        internal static bool ComputeHasOrphanedLimboTree(
            bool hasPendingTree,
            PendingTreeState pendingTreeState,
            bool activeTreeRestoredFromSave)
        {
            return hasPendingTree
                && pendingTreeState == PendingTreeState.Limbo
                && !activeTreeRestoredFromSave;
        }

        /// <summary>
        /// Disposition of the OnLoad pending-Limbo dispatch. The block this
        /// classifies only runs when a pending tree is in Limbo or
        /// LimboVesselSwitch; on a revert the branch above has already discarded
        /// the pending tree, so there is no revert outcome here.
        /// </summary>
        internal enum LimboDispatchOutcome
        {
            /// <summary>Pre-transitioned tree (#266): reinstall via the vessel-switch restore coroutine.</summary>
            VesselSwitchRestore = 0,
            /// <summary>Limbo (not LimboVesselSwitch) but the classifier says vessel switch: pre-#266 finalize.</summary>
            SafetyNetFinalize = 1,
            /// <summary>Quickload / cold-start resume: defer the restore to OnFlightReady.</summary>
            QuickloadRestore = 2,
        }

        /// <summary>
        /// Pure form of the OnLoad pending-Limbo dispatch decision. Ordering is
        /// load-bearing: a LimboVesselSwitch stash was pre-transitioned at stash
        /// time and must take the restore path even when the classifier also says
        /// vessel switch; only a plain Limbo stash under a vessel switch falls
        /// back to the safety-net finalize.
        /// </summary>
        internal static LimboDispatchOutcome ClassifyLimboDispatch(
            bool isVesselSwitch, PendingTreeState pendState)
        {
            if (pendState == PendingTreeState.LimboVesselSwitch)
                return LimboDispatchOutcome.VesselSwitchRestore;
            if (isVesselSwitch)
                return LimboDispatchOutcome.SafetyNetFinalize;
            return LimboDispatchOutcome.QuickloadRestore;
        }

        /// <summary>
        /// True when quickload discard must treat a re-fly session as active.
        /// This includes the normal persisted marker and the retry/invoke window
        /// before <see cref="RewindInvoker.AtomicMarkerWrite"/> recreates the
        /// marker from <see cref="RewindInvokeContext"/>.
        /// </summary>
        internal static bool IsReFlySessionActiveForQuickloadDiscard()
        {
            var scenario = Instance;
            if (!object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null)
                return true;

            return RewindInvokeContext.Pending;
        }

        /// <summary>
        /// True when an active Re-Fly session is operating under the
        /// in-place continuation contract: <see cref="ActiveReFlySessionMarker"/>
        /// is non-null AND its <c>InPlaceContinuation</c> flag is set.
        ///
        /// <para>
        /// Stricter than <see cref="IsReFlySessionActiveForQuickloadDiscard"/>:
        /// the placeholder-mode marker (where <see cref="RewindInvoker.AtomicMarkerWrite"/>
        /// could not eagerly attach the fork to a tree because PID changed
        /// or the chain tip was orphaned) returns FALSE here. The OnFlightReady
        /// recorder-restore carve-out relies on the in-place marker swap
        /// (<see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>);
        /// in placeholder mode that swap returns <c>placeholder-pattern</c>
        /// and the wait loop targets the pre-rewind PID, so the carve-out
        /// must NOT fire there — instead, the original merge-dialog fallback
        /// runs and the player can discard the orphan attempt.
        /// </para>
        ///
        /// <para>
        /// Unlike <see cref="IsReFlySessionActiveForQuickloadDiscard"/>,
        /// this helper does NOT consider <see cref="RewindInvokeContext.Pending"/>:
        /// during the brief invoke window the marker is null and the
        /// in-place-vs-placeholder decision has not been made yet. Callers
        /// that need to cover the brief window should combine this with a
        /// separate <c>RewindInvokeContext.Pending</c> check.
        /// </para>
        /// </summary>
        internal static bool IsReFlyInPlaceContinuationActive()
        {
            var scenario = Instance;
            if (object.ReferenceEquals(null, scenario)) return false;
            var marker = scenario.ActiveReFlySessionMarker;
            return marker != null && marker.InPlaceContinuation;
        }

        private static string DescribeReFlySessionForQuickloadDiscard()
        {
            var scenario = Instance;
            var marker = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.ActiveReFlySessionMarker;
            if (marker != null)
            {
                return "marker "
                    + $"sess={marker.SessionId ?? "<no-id>"} "
                    + $"rp={marker.RewindPointId ?? "<no-rp>"}";
            }

            if (RewindInvokeContext.Pending)
            {
                return "pending-invoke "
                    + $"sess={RewindInvokeContext.SessionId ?? "<no-id>"} "
                    + $"rp={RewindInvokeContext.RewindPointId ?? "<no-rp>"}";
            }

            return "none";
        }

        /// <summary>
        /// What OnLoad's revert branch does with the pending tree. Extracted so the decision is
        /// testable without a Unity lifecycle; the call site is a straight dispatch over the result
        /// and keeps the same log lines and the same PendingStashedThisTransition clear.
        /// </summary>
        internal enum RevertPendingTreeDisposition
        {
            /// <summary>Stashed during THIS scene transition: fresh, so it must survive the revert
            /// branch long enough for the current OnLoad to classify it.</summary>
            KeepFreshlyStashed,

            /// <summary>Nothing pending; the branch does nothing.</summary>
            NothingPending,

            /// <summary>A Limbo / LimboVesselSwitch tree: stashed for OnLoad dispatch, never
            /// orphaned (#290), even though the flag has already been reset by
            /// DiscardPendingOnQuickload.</summary>
            KeepLimbo,

            /// <summary>Stale from a previous flight: discard it so the OnFlightReady fallback does
            /// not show the merge dialog again (#64).</summary>
            DiscardOrphaned,
        }

        /// <summary>
        /// PURE revert-branch decision for the pending tree (OnLoad, revert path). The freshly
        /// stashed case wins over everything, then an absent pending tree, then Limbo state, and a
        /// pending tree in any other state is the orphan.
        /// </summary>
        internal static RevertPendingTreeDisposition ClassifyRevertPendingTreeDisposition(
            bool stashedThisTransition, bool hasPendingTree, PendingTreeState pendingTreeState)
        {
            if (stashedThisTransition)
                return RevertPendingTreeDisposition.KeepFreshlyStashed;
            if (!hasPendingTree)
                return RevertPendingTreeDisposition.NothingPending;
            if (pendingTreeState == PendingTreeState.Limbo
                || pendingTreeState == PendingTreeState.LimboVesselSwitch)
                return RevertPendingTreeDisposition.KeepLimbo;
            return RevertPendingTreeDisposition.DiscardOrphaned;
        }

        /// <summary>
        /// Discards any pending tree that was stashed
        /// during the current scene transition, on a detected quickload
        /// (UT regressed between OnSceneChangeRequested and OnLoad). Clears
        /// <see cref="PendingTreeSlot.PendingStashedThisTransition"/> and also
        /// clears in-memory science subjects accumulated post-F5 (they are
        /// not serialized to .sfs, so they can only ever be stale on
        /// quickload). Limbo pending trees are explicitly preserved — they
        /// are the quickload-resume carrier for tree-mode recording and are
        /// handled by the restore-and-resume dispatch further down in OnLoad.
        /// Extracted as <c>internal static</c> so log-assertion tests can
        /// exercise it without a full Unity lifecycle.
        ///
        /// <para>
        /// <b>Caller contract (#434 follow-up, 2026-04-17):</b> this method is
        /// destructive — it deletes sidecar files via
        /// <see cref="TreeDiscardService.DiscardPendingTree"/> — and MUST NOT be
        /// called from the revert path. Revert-to-Launch in particular matches
        /// the "UT went backwards and scene stayed in FLIGHT" quickload
        /// heuristic, but reverted recordings must survive on disk so a flight
        /// F5 quicksave's F9 can still restore. The production caller in
        /// <see cref="OnLoad"/> gates the call through
        /// <see cref="ShouldRunQuickloadDiscard"/>; the defense-in-depth checks
        /// on the first lines below refuse to proceed if a revert just fired
        /// or a re-fly session is active.
        /// </para>
        /// <para>
        /// Retry during the Re-Fly dialog is the same class of exclusion as
        /// revert: it clears the old marker, arms <see cref="RewindInvokeContext"/>
        /// for the fresh invocation, and quickloads the RP back into FLIGHT.
        /// During that pre-marker window the pending tree and RP quicksave are
        /// still dependencies of the live session, so the quickload-discard
        /// heuristic must not delete them.
        /// </para>
        /// <para>
        /// <paramref name="clearPendingScience"/> is the
        /// <see cref="LoadReconcilePolicy"/> decision for
        /// <see cref="LoadStateCategory.PendingScience"/> on this load; OnLoad passes it from
        /// the refined load kind.
        /// </para>
        /// </summary>
        internal static void DiscardStashedOnQuickload(
            double preChangeUT, double currentUT, bool clearPendingScience = true)
        {
            // Defense-in-depth: even if a future refactor inlines or removes
            // the ShouldRunQuickloadDiscard gate at the OnLoad call site, the
            // armed RevertDetector state means a revert is in progress.
            // OnLoad consumes the flag immediately before the dispatch call,
            // so under the correct (!isRevert) path this check always reads
            // None; only a buggy caller that skipped the gate would see an
            // armed kind. The method refuses rather than delete sidecar files
            // tied to the reverted flight.
            if (RevertDetector.PendingKind != RevertKind.None)
            {
                ParsekLog.Warn("Scenario",
                    "DiscardStashedOnQuickload: refusing to run with armed RevertDetector " +
                    $"(pending={RevertDetector.PendingKind}). Caller skipped the #434 guard at " +
                    "the OnLoad dispatch — sidecar preservation invariant enforced here.");
                return;
            }

            if (IsReFlySessionActiveForQuickloadDiscard())
            {
                ParsekLog.Warn("Scenario",
                    "DiscardStashedOnQuickload: refusing to run with active re-fly session " +
                    $"({DescribeReFlySessionForQuickloadDiscard()}). Caller skipped the re-fly " +
                    "guard at the OnLoad dispatch — rewind point and pending-tree " +
                    "preservation invariant enforced here.");
                return;
            }

            ParsekLog.Info("Scenario",
                "Quickload detected: UT " +
                preChangeUT.ToString("F2", CultureInfo.InvariantCulture) +
                " → " +
                currentUT.ToString("F2", CultureInfo.InvariantCulture) +
                " — discarding recordings stashed this transition");

            int discardedTree = 0;

            if (PendingTreeSlot.HasPendingTree
                && PendingTreeSlot.PendingStashedThisTransition
                && PendingTreeSlot.PendingTreeStateValue != PendingTreeState.Limbo
                && PendingTreeSlot.PendingTreeStateValue != PendingTreeState.LimboVesselSwitch)
            {
                // Non-Limbo pending tree stashed this transition: discard.
                // Limbo + LimboVesselSwitch trees are the quickload-resume / switch
                // carriers and must survive — they are picked up later in OnLoad by
                // the ScheduleActiveTreeRestoreOnFlightReady path.
                string treeName = PendingTreeSlot.PendingTree?.TreeName;
                int recCount = PendingTreeSlot.PendingTree?.Recordings?.Count ?? 0;
                DiscardPendingTreeAndAbandonDeferredFlightResults(
                    "pending tree discarded on quickload");
                discardedTree = 1;
                ParsekLog.Info("Scenario",
                    $"Quickload: discarded pending tree '{treeName}' " +
                    $"({recCount} recording(s), state != Limbo)");
            }

            PendingTreeSlot.PendingStashedThisTransition = false;

            // Science subjects are never serialized to .sfs, so any entries
            // in PendingScienceSubjects accumulated between the quicksave and
            // the quickload are by definition from the discarded future. Clear
            // them so they don't get mis-attached to the next commit.
            int staleScience = 0;
            int pendingScience = GameStateRecorder.PendingScienceSubjects.Count;
            if (pendingScience > 0 && clearPendingScience)
            {
                staleScience = pendingScience;
                GameStateRecorder.PendingScienceSubjects.Clear();
                ParsekLog.Info("Scenario",
                    $"Quickload: cleared {staleScience} stale pending science subject(s)");
            }
            else if (pendingScience > 0)
            {
                ParsekLog.Info("Scenario",
                    $"Quickload: kept {pendingScience} pending science subject(s) (load policy decision is not Clear)");
            }

            ParsekLog.Info("Scenario",
                $"Quickload discard complete: tree={discardedTree} " +
                $"science={staleScience}");
        }

        /// <summary>
        /// Clears pending trees and stale rewind flags that may have leaked from a
        /// previous save. Static fields survive scene changes, so without this cleanup,
        /// pending state from save A would be auto-committed into save B's timeline.
        /// </summary>
        private void DiscardStalePendingState()
        {
            if (PendingTreeSlot.HasPendingTree)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: discarding pending tree from previous save");
                DiscardPendingTreeAndAbandonDeferredFlightResults(
                    "stale pending tree discarded on initial load");
            }
            if (RewindContext.IsRewinding)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: clearing stale rewind flags from previous save");
                RewindContext.EndRewind();
                RecordingStore.RewindUTAdjustmentPending = false;
            }
            // Defensive: clear stale UT adjustment flag even outside the rewind block.
            // The flag may have been left set if the coroutine didn't complete (crash/exit).
            if (RecordingStore.RewindUTAdjustmentPending)
            {
                ParsekLog.Warn("Scenario",
                    "OnLoad initial: clearing stale RewindUTAdjustmentPending flag");
                RecordingStore.RewindUTAdjustmentPending = false;
            }
        }

        /// <summary>
        /// Finalizes a Limbo tree on the revert path (and on the safety-net
        /// vessel-switch path where the stash missed pre-transitioning to
        /// LimboVesselSwitch). Routes each recording through
        /// <see cref="ParsekFlight.FinalizeIndividualRecording"/> — the same
        /// per-recording helper the live commit path uses — so any leaf whose
        /// vessel is still loaded in <c>FlightGlobals</c> gets its actual
        /// situation (Landed/Splashed/Orbiting/SubOrbital), and only leaves
        /// whose vessel is gone fall back to Destroyed.
        ///
        /// Then runs <see cref="ParsekFlight.EnsureActiveRecordingTerminalState"/>
        /// for the active-non-leaf case and <see cref="ParsekFlight.PruneZeroPointLeaves"/>
        /// to drop empty placeholders. Finally flips the pending tree state from
        /// Limbo (or LimboVesselSwitch) to Finalized so the existing auto-commit /
        /// merge-dialog path handles it normally.
        ///
        /// Bug #278 (the rewrite from blanket-Destroyed to situation-aware) means
        /// EVA kerbals walking on the surface at the moment of revert/switch keep
        /// their real Landed state and remain canPersist=True. The previous
        /// blanket-stamped-Destroyed code killed canPersist for every leaf even
        /// when the vessel was still loaded.
        /// </summary>
        private static void FinalizePendingLimboTreeForRevert()
        {
            var tree = PendingTreeSlot.PendingTree;
            if (tree == null) return;

            RecorderStateLog.RecState("FinalizeLimboForRevert:entry", CaptureScenarioRecorderState());

            // Bug #278: run the same per-recording finalize the live commit path uses
            // (ParsekFlight.FinalizeTreeRecordings, minus the active-recorder-flush
            // step which already happened during StashActiveTreeAsPendingLimbo).
            // FinalizeIndividualRecording attempts FlightRecorder.FindVesselByPid +
            // RecordingTree.DetermineTerminalState; if the vessel is alive in
            // FlightGlobals, the leaf gets its actual situation (Landed/SubOrbital/
            // Orbiting/etc.) and CaptureTerminalOrbit + CaptureTerminalPosition fill
            // in the orbital metadata. If the vessel is gone, it falls back to
            // Destroyed + PopulateTerminalOrbitFromLastSegment — same behavior as
            // the previous blanket-Destroyed code, but only for leaves that actually
            // lost their vessel. Surviving leaves (notably EVA kerbals walking on
            // the surface at the moment of revert/switch) keep their real situation
            // and remain canPersist=True.
            double commitUT = Planetarium.GetUniversalTime();
            int newlySet = 0;
            int alreadyTerminal = 0;
            foreach (var kvp in tree.Recordings)
            {
                var rec = kvp.Value;
                bool wasTerminal = rec.TerminalStateValue.HasValue;
                // isSceneExit: true skips FinalizeIndividualRecording's
                // re-snapshot branch (the `if (!isSceneExit)` block at L5795
                // of ParsekFlight). The limbo tree is a frozen snapshot —
                // StashActiveTreeAsPendingLimbo already captured each leaf's
                // VesselSnapshot at the moment of OnSceneChangeRequested, and
                // re-mutating those snapshots here would invalidate the
                // "limbo = exact state at scene-change time" invariant the
                // dispatch comment (L926-948) relies on. The vessel-switch
                // case in particular must not re-snapshot, because the new
                // active vessel may have already started physics-loading
                // and a fresh snapshot would capture mid-load state.
                ParsekFlight.FinalizeIndividualRecording(rec, commitUT, isSceneExit: true, treeContext: tree);
                if (wasTerminal)
                    alreadyTerminal++;
                else if (rec.TerminalStateValue.HasValue)
                    newlySet++;
            }
            ParsekFlight.EnsureActiveRecordingTerminalState(tree, isSceneExit: true);
            ParsekFlight.PruneZeroPointLeaves(tree);
            ParsekFlight.PruneSinglePointDebrisLeaves(tree);

            PendingTreeSlot.MarkPendingTreeFinalized();
            ParsekLog.Info("Scenario",
                $"FinalizePendingLimboTreeForRevert: {newlySet} recording(s) got terminal state set, " +
                $"{alreadyTerminal} already had it, in tree '{tree.TreeName}' — " +
                $"transitioned Limbo → Finalized");
            RecorderStateLog.RecState("FinalizeLimboForRevert:post", CaptureScenarioRecorderState());
        }
    }
}
