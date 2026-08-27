using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Partial: the thin Unity applier for the two-phase <c>InvokeRewindToLaunch</c> verb.
    /// Every decision it makes is factored into the pure
    /// <see cref="TestCommandRewindToLaunch"/>; this file only samples live state, routes
    /// through the product's own gate, calls the product's own entry point, and polls the
    /// settled scene.
    ///
    /// <para>
    /// WHY THIS VERB EXISTS. Rewind-to-Launch is the Recordings-table "R" button
    /// (<c>RecordingsTableUI.ShowRewindConfirmation</c> -> <c>RecordingStore.InitiateRewind</c>):
    /// reload the <c>parsek_rw_*</c> quicksave captured at a flight's launch, wound back by
    /// the pre-launch lead time, with the flight's own vessel stripped so it replays as a
    /// ghost. It is a DIFFERENT mechanism from Re-Fly / Rewind-to-Separation, which
    /// <c>InvokeRewind</c> already drives: no RewindPoint, no <c>ReFlySessionMarker</c>, no
    /// MergeJournal, no supersede write - <c>BeginRewindForOwner</c>'s own log line spells
    /// that out. Until now nothing unattended could reach it, so every lane that wanted a
    /// flight re-played from its launch had to fake the state instead of driving the
    /// product path.
    /// </para>
    ///
    /// <para>
    /// ARGS. <c>tree=&lt;treeId&gt;</c> (OPTIONAL). With no arg the verb auto-selects when
    /// the save holds exactly ONE committed tree and otherwise refuses
    /// (<c>no-committed-tree</c> / <c>ambiguous-tree</c>) rather than guessing which flight
    /// to unwind - the operation is irreversible in the same breath. The rewind OWNER is
    /// resolved the way the UI resolves it: hand the tree's ROOT recording to
    /// <c>InitiateRewind</c>, which runs <c>GetRewindRecording</c> itself (a branch
    /// recording resolves to the root that captured the quicksave).
    /// </para>
    ///
    /// <para>
    /// TWO CONTRACTS ARE LOAD-BEARING HERE.
    /// </para>
    /// <para>
    /// (1) <c>InitiateRewind</c> RETURNS VOID and its failure paths only LOG (no owner, an
    /// active merge journal, a null <c>LoadGame</c>, a throwing copy / pre-process). The
    /// applier therefore treats <c>RecordingStore.IsRewinding</c> immediately after the
    /// call as the synchronous success test: the whole pre-load half of
    /// <c>ExecuteRewindSaveLoad</c> runs inside that call, and every failure path inside it
    /// ends at <c>ResetRewindFlags</c>. Still false -> terminal ERROR <c>rewind-failed</c>,
    /// never a PENDING that would then burn the whole budget waiting for a reload that was
    /// never requested.
    /// </para>
    /// <para>
    /// (2) LATCH <c>adjustedUT</c> AT EXECUTE. <c>RewindContext.SetAdjustedUT</c> is stamped
    /// synchronously inside <c>InitiateRewind</c> (right after the save parses, before the
    /// <c>LoadScene</c>), and <c>RewindContext.EndRewind()</c> - the very tail of
    /// <c>HandleRewindOnLoad</c> that MAKES the completion CompleteOk - zeroes it again.
    /// Reading <c>RecordingStore.RewindAdjustedUT</c> at completion time would therefore
    /// report <c>0.0</c> on every successful rewind. It is read once, here, while it is
    /// still populated.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        // InvokeRewindToLaunch two-phase state: the ids to echo back in the terminal
        // payload plus the latched adjusted UT (see contract (2) above). Re-armed WHOLESALE
        // at the start of every InvokeRewindToLaunchImpl Execute - the EnterWatchMode /
        // StartLoopPlayback convention - so a stale value can never be read across
        // commands, which is why they are not cleared in ClearTwoPhase.
        private string rewindToLaunchRecArg;
        private string rewindToLaunchTreeArg;
        private double rewindToLaunchAdjustedUt;

        // ----- InvokeRewindToLaunch (two-phase, irreversible, gate-routed) -----
        // Resolves the target committed tree + its root recording, routes through the real
        // RecordingStore.CanRewind gate (decline -> REJECTED rewind-gate <reason> verbatim),
        // then InitiateRewind and hold the FIFO head until the loaded Space Center settles
        // with the rewind flags cleared. Dispatch already refused merge-journal /
        // load-in-flight / recording-active.
        private void InvokeRewindToLaunchImpl(ParsedCommand cmd)
        {
            string treeArg = ArgOrNull(cmd, "tree");
            ParsekLog.Info(Tag, $"invokerewindtolaunch start tree={treeArg ?? string.Empty}");

            rewindToLaunchRecArg = null;
            rewindToLaunchTreeArg = null;
            rewindToLaunchAdjustedUt = 0.0;

            List<RecordingTree> trees = RecordingStore.CommittedTrees;
            List<string> treeIds = new List<string>();
            if (trees != null)
            {
                for (int i = 0; i < trees.Count; i++)
                    if (trees[i] != null && !string.IsNullOrEmpty(trees[i].Id))
                        treeIds.Add(trees[i].Id);
            }

            RewindToLaunchTarget target = TestCommandRewindToLaunch.ResolveTarget(treeIds, treeArg);
            if (target.Outcome != RewindToLaunchTargetOutcome.Selected)
            {
                ParsekLog.Warn(Tag,
                    $"invokerewindtolaunch refused: {target.RefusalReason} " +
                    $"tree={treeArg ?? string.Empty} committedTrees={treeIds.Count.ToString(CultureInfo.InvariantCulture)}");
                SetExecResult("REJECTED", null, target.RefusalReason);
                return;
            }

            // The tree's ROOT recording is what the UI hands InitiateRewind; the owner walk
            // (GetRewindRecording) happens inside the product call. A tree whose root cannot
            // be resolved falls through to CanRewind below with a null recording, which the
            // product answers honestly ("No rewind save available") rather than needing a
            // second vocabulary here.
            //
            // The RAW root is handed over deliberately - NOT an ERS / supersede walk of it.
            // Measured facts: RecordingTreeSplitter keeps tree.RootRecordingId on HEAD
            // across a Re-Fly split (Step12 logs "id preservation kept the tree root pointer
            // valid"), and SupersedeCommit.IsPreRewindCarveOut filters HEAD out of the
            // supersede write-set - so the raw root IS what the UI's ERS-visible rows
            // resolve to, and a walk here would be the identity at best. At worst (a
            // hypothetical whole-root supersede) it would RETARGET the rewind to a different
            // launch, because the effective recording's own RewindSaveFileName
            // short-circuits GetRewindRecording inside the product call.
            RecordingTree selectedTree = FindCommittedTreeById(target.TreeId);
            Recording rootRec = null;
            if (selectedTree != null
                && !string.IsNullOrEmpty(selectedTree.RootRecordingId)
                && selectedTree.Recordings != null)
            {
                selectedTree.Recordings.TryGetValue(selectedTree.RootRecordingId, out rootRec);
            }

            // Mirror InitiateRewind's OWN merge-journal refusal up front. That refusal is a
            // silent void return inside the product call, so without this the verb would
            // report PENDING and then time out on a rewind that never started. Dispatch's
            // MergeJournalInFlight guard tests `!= null`; the product's tests the finer
            // `Phase != Complete`, so this is the narrower of the two and cannot be folded
            // into the dispatch row.
            ParsekScenario scenario = ParsekScenario.Instance;
            if (scenario != null && scenario.ActiveMergeJournal != null
                && scenario.ActiveMergeJournal.Phase != MergeJournal.Phases.Complete)
            {
                ParsekLog.Warn(Tag,
                    $"invokerewindtolaunch refused: merge-journal-in-flight journalPhase={scenario.ActiveMergeJournal.Phase}");
                SetExecResult("REJECTED", null, "merge-journal-in-flight");
                return;
            }

            // The REAL product gate, with the live recorder state sampled from the same
            // source DispatchState.Recording uses (dispatch already rejects a live recorder,
            // so this is belt-and-suspenders rather than a second policy).
            bool isRecording = ParsekFlight.HasLiveRecorderForTagging();
            if (!RecordingStore.CanRewind(rootRec, out string reason, isRecording))
            {
                string msg = TestCommandRewindToLaunch.GateRefusalMsg(reason);
                ParsekLog.Warn(Tag,
                    $"invokerewindtolaunch refused: {msg} tree={target.TreeId} " +
                    $"rec={(rootRec != null ? rootRec.RecordingId : string.Empty)}");
                SetExecResult("REJECTED", null, msg);
                return;
            }

            string recId = rootRec != null ? rootRec.RecordingId : string.Empty;
            ParsekLog.Info(Tag,
                $"invokerewindtolaunch invoking tree={target.TreeId} rec={recId} " +
                $"startUT={(rootRec != null ? rootRec.StartUT : 0.0).ToString("F1", CultureInfo.InvariantCulture)} " +
                "- plain Rewind-to-Launch (NOT a Re-Fly)");

            // Synchronous pre-load half + the LoadScene request happen inside this call.
            RecordingStore.InitiateRewind(rootRec);

            if (!RecordingStore.IsRewinding)
            {
                // Contract (1): every failure path inside InitiateRewind /
                // ExecuteRewindSaveLoad ends at ResetRewindFlags and only logs, so a cleared
                // flag here is the synchronous failure. The KSP.log carries the product's own
                // reason line immediately above this one.
                ParsekLog.Error(Tag,
                    $"invokerewindtolaunch failed reason=rewind-failed tree={target.TreeId} rec={recId} " +
                    "- InitiateRewind returned without arming the rewind (see the preceding [Rewind] error)");
                SetExecResult("ERROR", null, "rewind-failed");
                return;
            }

            // Contract (2): latch the adjusted UT while EndRewind has not yet zeroed it.
            rewindToLaunchRecArg = recId;
            rewindToLaunchTreeArg = target.TreeId;
            rewindToLaunchAdjustedUt = RecordingStore.RewindAdjustedUT;
            ParsekLog.Info(Tag,
                $"invokerewindtolaunch armed tree={target.TreeId} rec={recId} " +
                $"adjustedUT={rewindToLaunchAdjustedUt.ToString("F1", CultureInfo.InvariantCulture)} " +
                "- awaiting the settled SPACECENTER with rewind flags cleared");
            SetExecResult(PendingVerdict, null, null);
        }

        // Bounded two-phase completion. Routes the live rewind flag / settled scene /
        // elapsed truth through the pure decider so a failed load (which leaves the scene in
        // FLIGHT with the flags reset) or a never-settling reload produces a terminal ERROR
        // the harness can classify, instead of hanging PENDING to the run budget.
        private void TryCompleteInvokeRewindToLaunch(double now)
        {
            double elapsed = now - completionStartedAt;
            double budget = DeferralBudget.BudgetSeconds("InvokeRewindToLaunch");
            bool isRewinding = RecordingStore.IsRewinding;
            // The deferred half of HandleRewindOnLoad: both flags are armed immediately
            // BEFORE the EndRewind() that clears IsRewinding, and are cleared by
            // ApplyRewindResourceAdjustment (the UT set, then the ledger recalc's finally).
            // Either one still set means the rewound world is not yet the world.
            bool deferredPending = RecordingStore.RewindUTAdjustmentPending
                || RewindContext.RewindResourceAdjustmentInProgress;
            TestCommandScene scene = MapScene(HighLogic.LoadedScene);
            RewindToLaunchCompletionDecision decision =
                TestCommandRewindToLaunch.DecideRewindToLaunchCompletion(
                    elapsed, isRewinding, scene == TestCommandScene.SpaceCenter,
                    deferredPending, budget);

            if (decision == RewindToLaunchCompletionDecision.StillWaiting)
                return;

            string id = completionId; long seq = completionSeq; string verb = completionVerb;
            string recId = rewindToLaunchRecArg; string treeId = rewindToLaunchTreeArg;
            double adjustedUt = rewindToLaunchAdjustedUt;
            ClearTwoPhase();

            switch (decision)
            {
                case RewindToLaunchCompletionDecision.CompleteOk:
                {
                    List<KeyValuePair<string, string>> payload =
                        TestCommandRewindToLaunch.BuildCompletePayload(recId, treeId, adjustedUt);
                    ParsekLog.Info(Tag,
                        $"invokerewindtolaunch complete rec={recId ?? string.Empty} tree={treeId ?? string.Empty} " +
                        $"adjustedUT={adjustedUt.ToString("F1", CultureInfo.InvariantCulture)} " +
                        $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                    EmitExecutedTerminal(id, seq, verb, "OK", payload, null, dequeueHead: true);
                    break;
                }
                case RewindToLaunchCompletionDecision.RewindFailed:
                    ParsekLog.Error(Tag,
                        $"invokerewindtolaunch failed reason=rewind-failed scene={scene} " +
                        $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s " +
                        "- the rewind flags cleared without reaching SPACECENTER (the save load failed)");
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null, "rewind-failed", dequeueHead: true);
                    break;
                case RewindToLaunchCompletionDecision.RewindTimeout:
                    TestCommandDiagnostics.Timeout(id, verb, elapsed, "rewind-timeout");
                    ParsekLog.Error(Tag,
                        $"invokerewindtolaunch failed reason=rewind-timeout scene={scene} " +
                        $"isRewinding={Bool(isRewinding)} deferredPending={Bool(deferredPending)} " +
                        $"elapsed={elapsed.ToString("F1", CultureInfo.InvariantCulture)}s");
                    // Seam-abort cleanup. ResetRewindFlags only ever runs inside
                    // ExecuteRewindSaveLoad, so a reload that never settles leaves
                    // RewindContext.IsRewinding armed for the rest of the PROCESS: the next
                    // ParsekScenario.OnLoad would then take the rewind branch for a load
                    // nobody asked to rewind, and every later CanRewind would refuse. The
                    // verb owns the abort, so it owns the reset.
                    if (RecordingStore.IsRewinding)
                    {
                        ParsekLog.Info(Tag,
                            "invokerewindtolaunch timeout-cleanup: rewind flags reset " +
                            "(IsRewinding was still armed; a stale flag would hijack the next OnLoad's rewind branch)");
                        RecordingStore.ResetRewindFlags();
                    }
                    EmitExecutedTerminal(id, seq, verb, "ERROR", null, "rewind-timeout", dequeueHead: true);
                    break;
            }
        }
    }
}
