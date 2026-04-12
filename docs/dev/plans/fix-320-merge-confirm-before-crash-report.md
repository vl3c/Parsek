## Fix 320: Merge confirmation before stock crash report

### Goal

When a recording session ends via vessel destruction and Parsek needs user merge/discard input, Parsek's merge confirmation must appear before KSP's stock flight-results / crash dialog.

### Investigation summary

Current destruction flow for tree recordings:

1. `FlightRecorder.OnVesselWillDestroy()` marks `VesselDestroyedDuringRecording = true` and captures the final point.
2. `ParsekFlight.OnVesselWillDestroy()` sets `treeDestructionDialogPending` and starts `ShowPostDestructionTreeMergeDialog()`.
3. `ShowPostDestructionTreeMergeDialog()` waits one frame, checks `RecordingTree.AreAllLeavesTerminal(...)`, finalizes the tree, stashes it via `RecordingStore.StashPendingTree(...)`, then shows `MergeDialog.ShowTreeDialog(...)`.
4. `FlightResultsPatch` suppresses `FlightResultsDialog.Display(...)` only when one of these is true:
   - `RecordingStore.HasPendingTree`
   - `PhysicsFramePatch.ActiveRecorder?.VesselDestroyedDuringRecording == true`

That suppression policy is timing-based rather than intent-based.

### Findings

#### 1. The patch is keyed to transient state, not to "a merge dialog is owed"

`FlightResultsPatch` currently infers suppression from two short-lived signals:

- live recorder state before finalization
- pending-tree state after finalization

The merge dialog itself is scheduled by a coroutine and only becomes concrete after a one-frame delay plus tree-finalization work. That leaves a race window where Parsek has already decided "show merge dialog after destruction", but the suppressor still depends on whichever transient state happens to be visible at the exact `FlightResultsDialog.Display(...)` call.

#### 2. The regression lines up with the T56 pending-slot removal

Before `9de3293` (`2026-04-11`), the patch gated on `RecordingStore.HasPending`. After standalone pending removal it now gates on `RecordingStore.HasPendingTree`.

That change made the tree destruction path depend even more on the narrow handoff between:

- active recorder still present and flagged destroyed
- pending tree already stashed

There is no longer any explicit persistent "defer flight results until merge dialog resolves" state.

#### 3. Existing logs show the tree merge dialog path and the flight-results suppression path are not tightly coupled

Older logs show successful tree-destruction merge dialogs with no corresponding intercepted flight-results replay, which is consistent with the current suppressor being opportunistic rather than guaranteed. The current implementation can work when timing happens to line up, but it does not pin the ordering contract.

#### 4. The scene-change safety net would still be wrong if deferred stock results survive into fallback merge handling

`OnFlightReady()` currently replays suppressed stock results immediately if `FlightResultsPatch.HasPendingResults()` is true, before it later shows the fallback pending-tree merge dialog. `OnSceneChangeRequested()` currently avoids that wrong ordering by clearing pending stock results on scene change.

That means any fix that keeps deferred stock results alive across a FLIGHT -> FLIGHT / FLIGHT -> KSC fallback merge path must also change the `OnFlightReady()` safety net so it does not replay the stock crash dialog before the fallback merge dialog.

### Root-cause hypothesis

The ordering regression is caused by two coupled issues:

1. `FlightResultsPatch` relies on inferred state (`HasPendingTree` / `HasActiveDestroyedRecording`) instead of an explicit deferred-results state machine.
2. The tree destruction path has a real handoff gap: `FinalizeTreeRecordings()` stops / flushes the active recorder before `RecordingStore.StashPendingTree(...)` creates the pending-tree state that the patch also depends on.

Together, that leaves the stock flight-results dialog gated by transient timing rather than by Parsek's actual intent to surface a merge dialog first.

### Fix plan

#### 1. Introduce an explicit deferred-flight-results state machine

Add explicit state to `FlightResultsPatch`, e.g.:

- `ArmForDeferredMerge(string reason)`
- `ReplayCapturedResults(string reason)`
- `DisarmDeferredMerge(string reason)`
- `HasArmedSuppression`
- `HasCapturedResults`

Behavior:

- when armed, suppress the next stock `FlightResultsDialog.Display(...)` call and store the outcome message
- keep using the existing replay path after the merge dialog resolves
- preserve `Bypass` semantics for replay
- treat early arming as provisional: if a stock dialog was captured and the flow later determines no merge dialog will happen, replay immediately instead of silently clearing

This makes suppression depend on Parsek's intent, not on a timing-sensitive snapshot of recorder / pending-tree state.

#### 2. Arm suppression as early as the tree-destruction flow is declared

Arm the latch in the earliest authoritative tree-destruction decision points:

- active-vessel branch in `ParsekFlight.OnVesselWillDestroy()` when `treeDestructionDialogPending` is set
- background-vessel terminal path when `DeferredDestructionCheck()` determines all leaves are terminal and schedules `ShowPostDestructionTreeMergeDialog()`

This should happen before the coroutine yield, so the stock dialog cannot slip through the gap.

Important nuance: this arm is provisional. `ShowPostDestructionTreeMergeDialog()` must become the place where Parsek decides whether the armed state graduates into a real merge-dialog flow, stays armed for a fallback scene-change merge path, or must replay / disarm because no merge dialog will actually happen.

#### 3. Resolve the armed state correctly on abort, fallback, and auto-discard paths

If the flow concludes that no merge dialog will happen, resolve the deferred stock dialog explicitly:

- if stock results were already captured, replay them immediately
- if nothing was captured, just disarm the provisional arm

Expected resolution sites:

- active vessel survived / false alarm
- tree auto-discard paths such as pad failure / idle on pad
- scene-cleanup paths that abandon the deferred merge flow entirely

Important nuance: `not all leaves terminal` means no merge dialog is currently owed. In that case the deferred-results state should resolve immediately:

- if stock results were already captured, replay them now
- if nothing was captured, just disarm the provisional arm

Later scene-exit merge flows should only own deferred stock results when they actually carry a pending tree / merge-dialog owner across the transition.

#### 4. Keep replay coupled to merge resolution, and always disarm even when nothing was intercepted

No behavior change to the end of the flow:

- merge / discard button callbacks must resolve deferred flight results, not merely "replay if pending"
- auto-merge path must resolve deferred flight results the same way
- successful merge / discard / auto-merge must disarm the deferred state even if no stock dialog was ever captured

This avoids stale suppression leaking into a later unrelated `FlightResultsDialog.Display(...)` call.

#### 5. Adjust the scene-change / `OnFlightReady()` safety net

If deferred stock results are carried across scene change for a fallback merge dialog, `OnFlightReady()` must not replay them before the fallback merge dialog is shown.

Plan change:

- gate or reorder the `OnFlightReady()` safety net so it only replays when no pending tree / merge dialog owner exists
- stop treating `ClearPending()` on scene change as the only protection against wrong ordering
- make the fallback merge-dialog path responsible for resolving deferred stock results after user choice

#### 6. Add tests that pin the ordering contract

Add low-cost tests around the new explicit state machine:

- arming causes `FlightResultsPatch` to suppress the next `Display(...)`
- replay consumes the stored message and sets `Bypass`
- abort-with-captured-message replays instead of silently clearing
- success paths disarm even when no stock dialog was intercepted
- repeated arm / clear calls are idempotent
- duplicate `Display(...)` handling is defined while only one captured message slot exists

If practical, add an integration-style test or log assertion around the tree-destruction path documenting this sequence:

1. Parsek arms suppression
2. stock flight results are intercepted
3. Parsek merge dialog is shown
4. stock flight results replay only after merge / discard

Additional cases to pin:

1. intercepted -> same-scene merge dialog -> replay after user choice
2. intercepted -> scene change -> fallback pending-tree merge dialog -> replay after user choice, not in `OnFlightReady()`
3. intercepted -> auto-discard / abandoned merge flow -> immediate replay
4. no intercept -> merge / discard still disarms deferred state cleanly

### Risks

- Arming too broadly could suppress stock results when no merge dialog is actually needed.
- Clearing too aggressively could reintroduce the original race.
- Multiple destruction events in the same breakup frame must not overwrite or replay the stored stock outcome incorrectly.
- Carrying deferred stock results across scene change without changing the `OnFlightReady()` safety net would reintroduce the wrong ordering in the fallback path.

### Recommended implementation order

1. Add explicit suppression state and pure helper methods in `FlightResultsPatch`.
2. Wire arm / resolve / disarm calls into the tree-destruction scheduling, fallback, and abort paths.
3. Update the scene-change and `OnFlightReady()` safety-net handling so fallback merge dialogs still come first.
4. Add tests for the new suppression lifecycle and fallback ordering.
5. Verify with an in-game destruction repro that the merge dialog appears first and the stock dialog replays only after user choice.
