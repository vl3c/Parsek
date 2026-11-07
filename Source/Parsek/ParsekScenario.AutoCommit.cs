using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // Committing a pending tree outside flight: the safety-net commit, the auto-commit fidelity
    // decision and the ghost-only commit.
    public partial class ParsekScenario
    {
        /// <summary>
        /// Safety net (defense-in-depth): if a pending tree still exists outside Flight
        /// and the dialog is not actively pending, auto-commit ghost-only before serialization.
        /// Under normal operation this is unreachable — Sites A/B handle all paths.
        /// </summary>
        private static void SafetyNetAutoCommitPending()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT && !mergeDialogPending)
            {
                if (PendingTreeSlot.HasPendingTree)
                {
                    AutoCommitTreeGhostOnly(PendingTreeSlot.PendingTree);
                    var treeToCommit = PendingTreeSlot.PendingTree;
                    // Resources were applied live during the originating flight (the
                    // pending tree came from a prior FLIGHT scene). MarkTreeAsApplied
                    // disarms the lump-sum replay path so the next FLIGHT entry does
                    // not re-credit the already-live income — same rationale as
                    // MergeDialog.MergeCommit (Phase C).
                    CommitPendingTreeAsApplied(treeToCommit);
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(treeToCommit);
                    ParsekLog.Warn("Scenario",
                        "Safety net: committed pending tree on save outside Flight");
                }
            }
        }

        /// <summary>
        /// Thin commit-seam used by every non-dialog tree-commit path in
        /// <see cref="ParsekScenario"/> (<see cref="SafetyNetAutoCommitPending"/>,
        /// the scene-exit auto-merge branch, and the outside-Flight auto-commit branch).
        ///
        /// <para>Performs the two steps that every auto-commit path must run in lockstep:</para>
        /// <list type="number">
        ///   <item><description><see cref="TreeCommitService.CommitPendingTree"/> — moves the tree from pending to committed state.</description></item>
        ///   <item><description><see cref="RecordingStore.MarkTreeAsApplied(RecordingTree)"/> - advances each committed tree recording's <c>LastAppliedResourceIndex</c> (serialized bookkeeping; no production code reads it since the ResourceBudget cost helpers were deleted).</description></item>
        /// </list>
        ///
        /// <para>Callers chain <see cref="LedgerOrchestrator.NotifyLedgerTreeCommitted(RecordingTree)"/>
        /// themselves — kept outside this seam because the orchestrator call runs heavy
        /// recalculation + patching and some call sites log / screen-message around it.</para>
        ///
        /// <para>Do NOT call from post-revert or other rollback paths where KSP's funds
        /// have been rewound to pre-flight: those paths intentionally discard or unstash
        /// the pending tree instead of committing it. None of the current callers are on
        /// that path — the revert branch runs <see cref="TreeDiscardService.DiscardPendingTree"/>
        /// (or <see cref="PendingTreeSlot.UnstashPendingTreeOnRevert"/>) instead (#434).</para>
        /// </summary>
        internal static void CommitPendingTreeAsApplied(RecordingTree tree)
        {
            TreeCommitService.CommitPendingTree();
            RecordingStore.MarkTreeAsApplied(tree);
        }

        /// <summary>
        /// How an outside-Flight auto-commit disposes of the pending tree's
        /// <see cref="Recording.VesselSnapshot"/>s. Returned by
        /// <see cref="ClassifyAutoCommitFidelity"/>; the two full-fidelity routes
        /// differ only in the tree state that reached them and in the log token
        /// they emit, so a collected log says WHICH route ran.
        /// </summary>
        internal enum AutoCommitFidelity
        {
            /// <summary>
            /// The lightweight commit: <see cref="AutoCommitTreeGhostOnly"/> nulls
            /// EVERY snapshot. Reserved for the three cases where a snapshot-preserving
            /// commit is either wrong or unsafe — autoMerge off (the player answers a
            /// dialog instead), an active re-fly (a silent MergeCommit would write
            /// supersede rows, plan §4.2/§10), and MAINMENU (the game is unloading and
            /// spawn-at-end never runs).
            /// </summary>
            GhostOnly = 0,

            /// <summary>
            /// A completed-flight <see cref="PendingTreeState.Finalized"/> tree routed
            /// through the dialog's own <see cref="MergeDialog.MergeCommit"/>. The
            /// original plan-§4.1 route.
            /// </summary>
            SilentFullFidelity = 1,

            /// <summary>
            /// A non-re-fly resume-stash (<see cref="PendingTreeState.Limbo"/> /
            /// <see cref="PendingTreeState.LimboVesselSwitch"/>) routed through the SAME
            /// <see cref="MergeDialog.MergeCommit"/>, so the snapshots the stash
            /// deliberately captured survive the commit instead of being destroyed.
            /// See the AUTOMERGE-ON-BY-DEFAULT entry in docs/dev/todo-and-known-bugs.md.
            /// </summary>
            LimboPreservingFullFidelity = 2,
        }

        /// <summary>
        /// Pure classifier for the outside-Flight auto-commit: which of the three
        /// snapshot dispositions applies, and — when it is
        /// <see cref="AutoCommitFidelity.GhostOnly"/> — the grep-stable reason.
        ///
        /// <para><b>The rule.</b> A commit that runs with no dialog must never silently
        /// DESTROY vessel snapshots that exist. Only three states justify the
        /// blanket-nulling ghost-only commit, and they are checked in this order
        /// because each is a stronger statement than the tree's own state:
        /// autoMerge OFF (the player is going to be asked, so this site is a
        /// safety net rather than the commit), an active re-fly (a silent
        /// <see cref="MergeDialog.MergeCommit"/> would run
        /// <c>TryCommitReFlySupersede</c> — the irreversible timeline mutation
        /// plan §4.2 forbids doing silently, and §10 keeps re-fly ghost-only on
        /// purpose), and MAINMENU (no destination scene; spawn-at-end never runs).</para>
        ///
        /// <para><b>Everything else preserves fidelity.</b> A <c>Finalized</c> tree takes
        /// the plan-§4.1 route it already had. A non-re-fly <c>Limbo</c> /
        /// <c>LimboVesselSwitch</c> stash takes the SAME route
        /// (<see cref="AutoCommitFidelity.LimboPreservingFullFidelity"/>) rather than the
        /// ghost-only one it used to take. That is the 2026-08-29 fix: the stash's
        /// snapshots are captured DELIBERATELY by
        /// <c>ParsekFlight.StashActiveTreeAsPendingLimbo</c> ("belt-and-braces … so the
        /// merge dialog can still offer respawn"), and a tree that reaches this site has
        /// already lost its resume — the limbo dispatch it arms fires on
        /// <c>onFlightReady</c>, which cannot happen outside FLIGHT. Destroying those
        /// snapshots was the product's documented recovery path erasing the thing it was
        /// recovering.</para>
        ///
        /// <para><b>Why the same route rather than a preserve-everything variant.</b>
        /// <see cref="MergeDialog.BuildDefaultVesselDecisions"/> is explicitly built for
        /// un-finalized shapes: <c>GhostPlaybackLogic.ShouldSpawnAtRecordingEnd</c>'s
        /// snapshot-situation check exists for "cases where TerminalState is null/Landed
        /// but the snapshot was captured mid-flight" (#114), which is exactly a Limbo
        /// stash. Reusing it keeps plan P2 (one commit implementation), preserves
        /// <c>GhostVisualSnapshot</c> for the leaves it does null, and releases their crew
        /// reservation — none of which a blanket preserve-what-exists pass would do.</para>
        /// </summary>
        /// <param name="ghostOnlyReason">
        /// The reason token for the <see cref="AutoCommitFidelity.GhostOnly"/> result,
        /// empty string otherwise. Note <c>state=</c> is deliberately NOT a reason any
        /// more: a non-Finalized state no longer sends a tree to the ghost-only branch.
        /// </param>
        internal static AutoCommitFidelity ClassifyAutoCommitFidelity(
            bool isAutoMerge,
            PendingTreeState pendingState,
            bool reFlyActive,
            GameScenes loadedScene,
            out string ghostOnlyReason)
        {
            if (!isAutoMerge)
            {
                ghostOnlyReason = "not-automerge";
                return AutoCommitFidelity.GhostOnly;
            }
            if (reFlyActive)
            {
                ghostOnlyReason = "re-fly-active";
                return AutoCommitFidelity.GhostOnly;
            }
            if (loadedScene == GameScenes.MAINMENU)
            {
                ghostOnlyReason = "mainmenu";
                return AutoCommitFidelity.GhostOnly;
            }

            ghostOnlyReason = "";
            return pendingState == PendingTreeState.Finalized
                ? AutoCommitFidelity.SilentFullFidelity
                : AutoCommitFidelity.LimboPreservingFullFidelity;
        }

        // NOTE: the two-way `ShouldSilentFullFidelityCommit` predicate this classifier
        // replaced was DELETED rather than kept as a derived helper (2026-08-29). It had
        // no caller left, and its `false` had become a trap: pre-fix it meant "the
        // ghost-only branch runs", post-fix it would mean only "not the FINALIZED
        // route", which is equally true of the new fidelity-preserving Limbo route.
        // The name still appears in five harness spec headers (S0.9, S0.10, CL-2, GS-1,
        // GS-2) and in the AUTOMERGE-ON-BY-DEFAULT entry, each of which now carries a
        // STATUS note marking it as pre-fix history. A reader arriving from one of
        // those wants ClassifyAutoCommitFidelity and its three-way answer.

        /// <summary>
        /// Counts how a decision map will dispose of the tree's live
        /// <see cref="Recording.VesselSnapshot"/>s, over exactly the inputs
        /// <see cref="MergeDialog.ApplyVesselDecisions"/> reads: a recording keeps its
        /// snapshot unless the map names it with <c>false</c>. Pure, so the
        /// full-fidelity log line can carry the same fidelity fact
        /// <c>snapshotsNulled</c> carries on the ghost-only branch.
        ///
        /// <para><b>Reading `snapshotsPreserved` correctly.</b> It counts snapshots the
        /// commit KEPT, which is not the same as "leaves that will spawn". A decision
        /// map only names leaves (plus the active recording), so a NON-LEAF parent that
        /// still holds a snapshot is absent from it, is therefore never nulled, and
        /// counts as preserved — even though <c>ShouldSpawnAtRecordingEnd</c> rejects it
        /// as a non-leaf and it can never materialise. That is deliberate and matches
        /// the dialog exactly (<see cref="MergeDialog.ApplyVesselDecisions"/> touches
        /// only what the map names), and it is why this number and the <c>spawnable</c>
        /// count on the same log line can legitimately differ in BOTH directions. Do
        /// not read a high preserved count as a spawn-eligibility claim.</para>
        /// </summary>
        internal static int CountSnapshotsPreservedByDecisions(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            out int released)
        {
            released = 0;
            int preserved = 0;
            if (tree == null || tree.Recordings == null)
                return 0;

            foreach (var kvp in tree.Recordings)
            {
                if (kvp.Value == null || kvp.Value.VesselSnapshot == null)
                    continue;
                bool persist;
                if (decisions != null
                    && decisions.TryGetValue(kvp.Key, out persist)
                    && !persist)
                    released++;
                else
                    preserved++;
            }
            return preserved;
        }

        /// <summary>
        /// Silent auto-commit of the current pending tree on an outside-Flight
        /// OnLoad path. <see cref="ClassifyAutoCommitFidelity"/> picks one of three
        /// routes: the dialog's full-fidelity <see cref="MergeDialog.MergeCommit"/>
        /// for a <c>Finalized</c> tree, the SAME MergeCommit for a non-re-fly
        /// Limbo resume-stash (so its deliberately-captured snapshots survive), and
        /// the lightweight ghost-only commit only for autoMerge-off / re-fly /
        /// MAINMENU. Shared by the warm scene-change fallback and the cold-load
        /// pending-outside-flight paths.
        /// </summary>
        private static void AutoCommitPendingTreeOutsideFlight(string context)
        {
            if (!PendingTreeSlot.HasPendingTree)
            {
                // Carries the SAME `autocommit-outside-flight` prefix as the entry line
                // and the two outcome lines below, so ONE grep answers all four states
                // this site can be in: never reached (no line at all), reached with
                // nothing to do (this line), reached and full-fidelity, reached and
                // ghost-only. Without the shared prefix the empty case is the one a
                // reader has to know a different string to find.
                ParsekLog.Verbose("Scenario",
                    $"autocommit-outside-flight ({context}): no-pending-tree — nothing to commit");
                return;
            }

            var pt = PendingTreeSlot.PendingTree;
            var scenario = ParsekScenario.Instance;
            bool reFlyActive =
                !object.ReferenceEquals(null, scenario)
                && scenario.ActiveReFlySessionMarker != null;

            // ENTRY line, emitted BEFORE the branch is chosen and carrying the WHOLE
            // decision input set. Without it the two outcome lines below are this
            // site's only evidence, and "the site never ran" reads identically to
            // "the site ran and chose full fidelity" in a collected log — which is
            // exactly the reading a driven auto-merge scenario has to make. Grep-stable
            // prefix: `autocommit-outside-flight`. See the AUTOMERGE-ON-BY-DEFAULT entry
            // in docs/dev/todo-and-known-bugs.md.
            ParsekLog.Info("Scenario",
                $"autocommit-outside-flight ({context}): entry tree='{pt?.TreeName}' " +
                $"recordings={pt?.Recordings?.Count ?? 0} scene={HighLogic.LoadedScene} " +
                $"autoMerge={IsAutoMerge} pendingState={PendingTreeSlot.PendingTreeStateValue} " +
                $"reFlyActive={reFlyActive}");

            // Hardening: this runs inside ParsekScenario.OnLoad, whose top-level catch
            // rethrows (an OnLoad abort has historically wiped the persistent index). A
            // commit failure must not take down the rest of OnLoad — Error-log and leave
            // the tree stashed instead of propagating.
            //
            // HOW LONG THAT RETRY WINDOW ACTUALLY IS, stated precisely because the
            // obvious reading over-promises: the tree survives until the next OnSAVE,
            // not the next load. Any OnSave taken outside FLIGHT runs
            // SafetyNetAutoCommitPending, which commits it GHOST-ONLY — blanket-nulling
            // every snapshot, the pre-2026-08-29 behaviour. That site is deliberately
            // out of scope (plan §4.1: routing it through MergeCommit would run a
            // quicksave inside OnSave, the reentrancy hazard) and is defense-in-depth
            // that is unreachable under normal operation, so it is the ONE remaining
            // lossy commit path and it is reached only after this catch has already
            // Error-logged. Noted on the AUTOMERGE-ON-BY-DEFAULT entry.
            try
            {
                PendingTreeState pendingStateAtCommit = PendingTreeSlot.PendingTreeStateValue;
                string ghostOnlyReason;
                AutoCommitFidelity route = ClassifyAutoCommitFidelity(
                    IsAutoMerge,
                    pendingStateAtCommit,
                    reFlyActive,
                    HighLogic.LoadedScene,
                    out ghostOnlyReason);

                if (route != AutoCommitFidelity.GhostOnly)
                {
                    var decisions = MergeDialog.BuildDefaultVesselDecisions(pt);
                    int spawnCount = 0;
                    foreach (var v in decisions.Values)
                        if (v) spawnCount++;
                    if (route == AutoCommitFidelity.SilentFullFidelity)
                    {
                        ParsekLog.Info("Scenario",
                            $"Silent full-fidelity auto-commit ({context}): tree='{pt.TreeName}' " +
                            $"recordings={pt.Recordings.Count} spawnable={spawnCount}");
                    }
                    else
                    {
                        // DISTINCT token, deliberately not a reuse of the line above: a
                        // collected log has to say which of the two full-fidelity routes
                        // ran, because they are reached from different tree states and
                        // only this one is the 2026-08-29 AUTOMERGE-ON-BY-DEFAULT fix.
                        // snapshotsPreserved is the mirror of the ghost-only branch's
                        // snapshotsNulled — the fidelity this commit KEPT, where the old
                        // branch reported what it destroyed; snapshotsReleased is what the
                        // dialog's own decisions still ghost-only (non-spawnable shapes,
                        // GhostVisualSnapshot copied and crew unreserved by
                        // ApplyVesselDecisions).
                        int snapshotsReleased;
                        int snapshotsPreserved = CountSnapshotsPreservedByDecisions(
                            pt, decisions, out snapshotsReleased);
                        ParsekLog.Info("Scenario",
                            $"Limbo-preserving full-fidelity auto-commit ({context}, " +
                            $"state={pendingStateAtCommit}): tree='{pt.TreeName}' " +
                            $"recordings={pt.Recordings.Count} spawnable={spawnCount} " +
                            $"snapshotsPreserved={snapshotsPreserved} " +
                            $"snapshotsReleased={snapshotsReleased}");
                    }
                    // MergeCommit runs the full dialog-commit sequence: ApplyVesselDecisions
                    // (keeps spawnable-leaf snapshots), CommitPendingTree + MarkTreeAsApplied,
                    // RunOptimizationPass, NotifyLedgerTreeCommitted, crew swap, and posts its
                    // own screen message. Its M1 guard (pt == PendingTreeSlot.PendingTree) holds.
                    //
                    // refreshQuicksaveAfterCommit: false — we run inside OnLoad, so a
                    // GamePersistence.SaveGame here would re-enter OnSave mid-load and
                    // snapshot before the OnLoad ledger recalc. The commit is durable via
                    // the next normal OnSave (matching the old ghost-only auto-commit,
                    // which never refreshed the quicksave). Also lets the OnLoad recalc
                    // (later this frame) patch the just-committed tree's ledger actions.
                    MergeDialog.MergeCommit(pt, decisions, spawnCount,
                        refreshQuicksaveAfterCommit: false);
                }
                else
                {
                    string reason = ghostOnlyReason;
                    int snapshotsNulled = AutoCommitTreeGhostOnly(pt);
                    CommitPendingTreeAsApplied(pt);
                    LedgerOrchestrator.NotifyLedgerTreeCommitted(pt);
                    ScreenMessages.PostScreenMessage("[Parsek] Tree recording merged to timeline", 5f);
                    CommittedListOptimizer.RunOptimizationPass();
                    // snapshotsNulled is the fidelity fact this line exists to carry: it
                    // is the number of full-fidelity VesselSnapshots this branch DESTROYED
                    // (spawn-at-end eligibility lost). Zero means the tree was already
                    // ghost-only and nothing was given up; nonzero is the measurement the
                    // AUTOMERGE-ON-BY-DEFAULT question turns on.
                    ParsekLog.Info("Scenario",
                        $"Ghost-only auto-commit ({context}, reason={reason}): tree='{pt.TreeName}' " +
                        $"recordings={pt.Recordings.Count} snapshotsNulled={snapshotsNulled}");
                }
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("Scenario",
                    $"AutoCommitPendingTreeOutsideFlight ({context}) threw {ex.GetType().Name}: " +
                    $"{ex.Message} — leaving tree '{pt?.TreeName}' stashed; the next OnLoad " +
                    "retries, but an OnSave outside FLIGHT reaches it first and commits it " +
                    "ghost-only via the safety net");
            }
        }

        /// <summary>
        /// Prepares all recordings in a pending tree for ghost-only commit (no vessel spawn).
        /// Nulls vessel snapshot and unreserves crew. Call TreeCommitService.CommitPendingTree() after this.
        /// <para>Returns the number of recordings that actually HAD a
        /// <see cref="Recording.VesselSnapshot"/> to null — i.e. how much full-fidelity
        /// spawn-at-end eligibility this commit gave up. The caller logs it.</para>
        /// </summary>
        internal static int AutoCommitTreeGhostOnly(RecordingTree tree)
        {
            int snapshotsNulled = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.VesselSnapshot != null) snapshotsNulled++;
                CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                rec.VesselSnapshot = null;
            }
            ParsekLog.Info("Scenario", $"Auto-commit tree ghost-only: tree '{tree.Id}' " +
                $"({tree.Recordings.Count} recordings, {snapshotsNulled} snapshot(s) nulled)");
            return snapshotsNulled;
        }
    }
}
