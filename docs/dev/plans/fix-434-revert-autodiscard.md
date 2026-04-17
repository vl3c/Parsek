# Fix #434 — Revert auto-discards recording; stop intercepting the stock crash report

**Branch:** `fix/434-revert-autodiscard` (off `feat/416-career-window`)
**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-434-revert-autodiscard`
**Depends on:** #431 (event-purge-on-discard) — ship #431 first, then this.

## Problem

Two coupled misbehaviours in career mode:

1. When a vessel is destroyed, Parsek intercepts KSP's stock `FlightResultsDialog` (the crash/mission report) and shows its merge/discard popup first. The stock dialog — which contains the Revert button — is replayed only after the player commits or discards. A player who picks "Merge to Timeline" and then picks Revert from the replayed stock dialog ends up with a committed recording whose mission the game considers reverted: the committed ghost later spawns its vessel, producing the deterministic-timeline paradox.
2. Revert (to Launch or to VAB/SPH) currently only increments the milestone epoch. The pending tree is not discarded. On the FLIGHT→FLIGHT reload after Revert to Launch, `OnFlightReady` reaches the fallback at `ParsekFlight.cs:4377-4381` and re-opens `MergeDialog.ShowTreeDialog`, which offers the player another chance to commit a mission that was just explicitly un-done.

## Invariant to enforce

Revert is a player declaration that "this mission never happened." It is not a choice point. On revert, the pending tree (in any state — Finalized, Limbo, LimboVesselSwitch) is discarded unconditionally, with the same purge semantics as the merge-dialog Discard button.

## Key file / line map (as of 2026-04-17 on `fix/434-revert-autodiscard`)

- `Source/Parsek/Patches/FlightResultsPatch.cs` — the whole file; Harmony prefix on `FlightResultsDialog.Display`. **Delete after the interception is removed.**
- `Source/Parsek/ParsekFlight.cs:1186` — `ArmForDeferredMerge("active vessel destroyed in tree mode")` — the interception trigger for the destruction path.
- `Source/Parsek/ParsekFlight.cs:1244 / 1263 / 1322 / 1340 / 1351 / 1396` — paired `CancelDeferredMerge(...)` calls on abort paths.
- `Source/Parsek/ParsekFlight.cs:1429` — `MergeDialog.ResolveDeferredFlightResults()` after auto-commit.
- `Source/Parsek/ParsekFlight.cs:1434` — `MergeDialog.ShowTreeDialog(RecordingStore.PendingTree)` inside Flight after destruction. **This is the in-flight merge dialog that must stop firing before the stock report is dismissed.**
- `Source/Parsek/ParsekFlight.cs:4377-4388` — OnFlightReady fallback that re-opens the merge dialog if a pending tree reached the flight scene. After the revert-discard lands, this stays as a non-revert safety net (e.g. on cold-start with a still-pending tree).
- `Source/Parsek/ParsekFlight.cs:4392-4396` — `FlightResultsPatch.HasPendingResults` / `ReplayFlightResults` safety net — remove after patch deletion.
- `Source/Parsek/ParsekFlight.cs:4444-4455` — `FlightResultsPatch.ClearPending(...)` on scene change — remove after patch deletion.
- `Source/Parsek/ParsekScenario.cs:964-977` — the `isRevert` branch. Insert the discard here.
- `Source/Parsek/ParsekScenario.cs:992-1058` — pending-Limbo dispatch inside the `isRevert` branch. The revert-discard short-circuits this.
- `Source/Parsek/MergeDialog.cs:44-47 / 95 / 116` — `ResolveDeferredFlightResults` uses — remove after patch deletion.
- `Source/Parsek/ParsekHarmony.cs` — unregister the `FlightResultsPatch` Harmony target.
- `Source/Parsek/ParsekScenario.cs:764` — revert detection; covers both Revert-to-Launch (FLIGHT→FLIGHT) and Revert-to-VAB/SPH (FLIGHT→EDITOR) via epoch/count regression, no changes needed.

### Merge-dialog re-entry after scene change (confirmed working)

`ParsekScenario.cs:1113-1118` (non-revert, non-Flight destination) and `:1274-1279` (secondary cold-start path) already schedule `ShowDeferredMergeDialog` via coroutine. `ShowDeferredMergeDialog` (`:2252`) shows `MergeDialog.ShowTreeDialog` after ~60 frames in Space Center / Tracking Station / Editor. Once the in-flight merge dialog is suppressed on destruction, this existing mechanism will surface the dialog on Space Center / Recover / any non-revert exit from the stock dialog.

## Plan

1. **Delete the FlightResultsPatch interception.** Drop the `ArmForDeferredMerge` call at `ParsekFlight.cs:1186` and every paired `CancelDeferredMerge` call (lines listed above). Drop the in-flight `MergeDialog.ShowTreeDialog` at `ParsekFlight.cs:1434`. Keep the finalize + stash logic in `ShowPostDestructionTreeMergeDialog` — the tree must survive as `RecordingStore.PendingTree` so the deferred dialog can surface on the next scene change.
1a. **Also drop the in-flight auto-merge commit.** `ShowPostDestructionTreeMergeDialog` at `ParsekFlight.cs:1423-1428` currently calls `RecordingStore.CommitPendingTree` + `LedgerOrchestrator.NotifyLedgerTreeCommitted` when `ParsekScenario.IsAutoMerge` is true, which bakes the recording into the timeline before the stock crash report surfaces. A player who then picks Revert from the report ends up with a reverted mission whose committed recording/ghost survives — exactly the footgun #434 is meant to close. Remove the in-flight commit branch too: always stash the pending tree on destruction regardless of `IsAutoMerge`. The auto-commit then happens at `ParsekScenario.OnLoad` (`:1065-1111`) after the player has chosen a non-revert exit; if they pick Revert, the new `isRevert` discard path at step 3 catches the still-pending tree.
2. **Delete the patch file** (`Source/Parsek/Patches/FlightResultsPatch.cs`) and its Harmony registration. Delete `MergeDialog.ResolveDeferredFlightResults` and its call sites in `MergeDialog.cs`. Delete the safety-net replay and `ClearPending` calls in `ParsekFlight.cs` (`:4392-4396`, `:4444-4455`). **Compile-gate cleanup:** after the patch file is gone, grep the whole codebase for `FlightResultsPatch` to catch any leftover `ParsekScenario` helper, in-game-test fixture, or doc reference, and delete those too — the branch must build cleanly with the patch removed.
3. **Auto-discard in `ParsekScenario.OnLoad` isRevert branch.** At the top of the `if (isRevert)` block (currently `:964`), before the limbo dispatch:
   ```csharp
   if (RecordingStore.HasPendingTree)
   {
       var discardedName = RecordingStore.PendingTree?.TreeName;
       RecordingStore.DiscardPendingTree();
       ScreenMessages.PostScreenMessage("[Parsek] Recording discarded (revert)", 4f);
       ParsekLog.Info("Scenario",
           $"Revert detected — auto-discarded pending tree '{discardedName}'");
   }
   ```
   Short-circuit the limbo-dispatch block (`:992-1058`) with a `HasPendingTree` guard after the discard — Finalized, Limbo, and LimboVesselSwitch all resolve to "discard" on revert. The epoch-increment at `:970` stays for now; it becomes redundant once #431 purges events by `recordingId`.
4. **Flight-scene revert with an in-progress (not yet stashed) tree** — OnSave already stashes the active tree to Limbo before the scene change fires OnLoad, so step 3 catches it. Cover with a test.
5. **Tests.** New `Source/Parsek.Tests/RevertDiscardTests.cs`:
   - Synthetic revert with a Finalized PendingTree → asserts `DiscardPendingTree` runs, screen-message log line fires, no `MergeDialog.ShowTreeDialog` path executes.
   - Synthetic revert-to-EDITOR (epoch regresses, not FLIGHT→FLIGHT) → same assertions.
   - Synthetic revert with a Limbo PendingTree (as if OnSave stashed from in-flight) → asserts discard runs and the limbo-dispatch is skipped.
   - Scene exit to Space Center with a pending tree (non-revert) → asserts `ShowDeferredMergeDialog` still fires (regression guard against the deferred mechanism).
   - Use `ParsekLog.TestSinkForTesting` with `[Collection("Sequential")]`.
6. **Docs.**
   - Mark #434 as ~~done~~ in `docs/dev/todo-and-known-bugs.md`.
   - Add one line to `docs/user-guide.md` under Automatic Behaviors → Scene Transitions: "Revert to Launch and Revert to VAB/SPH auto-discard the in-progress recording. Use 'Space Center' from the mission report if you want the merge dialog."
   - CHANGELOG entry: one line, user-facing, per the CHANGELOG style rule.
7. **Post-build checks.** Run `dotnet build` and `dotnet test` from the worktree; verify the deployed DLL in `GameData/Parsek/Plugins/` contains the new screen-message string (UTF-16 grep) before requesting an in-game playtest.

## Out of scope for this PR

- The event-purge-on-discard work (covered by #431, ships first).
- A dev toggle to restore the pre-fix behaviour. Revert is a declaration, not a debug knob.
- Retroactive cleanup of saves that already contain a ghost from a past merge-then-revert sequence — document the limitation if needed.

## Risks

- A previously-armed `FlightResultsPatch` session cached in memory on an older build could replay a stale suppressed dialog after the patch is removed. Mitigation: in-game test a hot reload? Not a real concern — Harmony unregistration tears down on assembly reload, and the patch file is gone.
- `ShowDeferredMergeDialog` waits 60 frames for the destination scene to settle. If the player clicks quickly through Space Center UI, the dialog still appears but after the click. Preserve as-is; it was already the behaviour on non-destruction exits. Mentioned here only because the destruction path now joins the deferred flow.
