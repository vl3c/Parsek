# Merge confirmation dialog before scene transition

## Problem

The "Merge to Timeline / Discard" confirmation dialog for tree recordings is shown
*after* the destination scene has finished loading. The flow when the player clicks
Space Center / Tracking Station / Quit-to-Main-Menu in flight or map view:

1. KSP fires `onGameSceneLoadRequested` -> `ParsekFlight.OnSceneChangeRequested`
   (`ParsekFlight.cs:2638`) finalizes the tree and stashes it as pending.
2. Flight scene tears down. Destination scene starts loading.
3. `ParsekScenario.OnLoad` runs in the new scene, sees a pending tree, decides
   "show approval dialog" via `GhostPlaybackLogic.ShouldShowCommitApproval`
   (`GhostPlaybackLogic.cs:195`), and starts `ShowDeferredMergeDialog`
   (`ParsekScenario.cs:1696, 1722, 2010`).
4. That coroutine yields ~60 frames waiting for UI skin / singletons
   (`ParsekScenario.cs:4807-4842`) before calling `MergeDialog.ShowTreeDialog`
   (`MergeDialog.cs:84`).

Result: the player sees the destination scene fully come up, then ~1 s later a
popup asks them about a recording from the flight they already left.

Quit-to-Main-Menu currently also forces auto-merge (no dialog) at
`ParsekScenario.cs:1659`.

## Goal

Show the merge dialog while still in flight, *before* the scene transition starts.
On Merge / Discard, run the same finalization that today happens in
`OnSceneChangeRequested`, then let the scene transition proceed. Stock KSP
warning popups (atmosphere / throttle / quit confirmation) must still run first.

## Decisions (locked in)

| Decision | Choice |
| --- | --- |
| Quit-to-Main-Menu | Show dialog (drop force-auto-merge) |
| Tree finalize timing | Pre-finalize *before* showing the dialog so the dialog operates on an already-stashed pending tree |
| Cancel button on regular dialog | None - click commits the player to leaving |
| Re-Fly-aware dialog | Reuse `MergeDialog.ShowTreeDialog` with Re-Fly-attempt-scoped button labels and body copy (existing dialog already handles Re-Fly merge / discard correctly inside `MergeCommit` / `MergeDiscard`) |
| Stock KSP danger / quit confirmations | Must run first (see patch chokepoint below) |

## Patch chokepoint - decompilation findings

dnSpy of `Assembly-CSharp.dll` (KSP 1.12.x) confirms three flight-exit sources,
all of which converge on `HighLogic.LoadScene(GameScenes)`:

### `PauseMenu` (Esc menu)

Three buttons: Space Center (`PauseMenu:850-878`), Tracking Station
(`PauseMenu:920-948`), Quit-to-Main-Menu (`PauseMenu:1074-1117`).

- If `canSaveAndExit == ClearToSaveStatus.CLEAR`: fire `onSceneConfirmExit`,
  call `saveAndExit(dest, ...)` -> `HighLogic.LoadScene(dest)` at
  `PauseMenu:1803`. Quit-to-Main-Menu additionally checks
  `showExitToMainConfirmation` (settings flag) and routes through
  `ShowExitToMainConfirmation` -> `SubmitExitToMainMenuConfirmation` ->
  `saveAndExit` if the flag is set.
- If `canSaveAndExit != CLEAR`: spawn the orange "you will lose progress"
  warning popup using `drawExitWithoutSaveOptions()`. Yes-button paths:
  - `Parameters.Flight.CanRestart`: direct
    `HighLogic.LoadScene(sceneToLeaveTo)` at `PauseMenu:1632` (no save).
  - Otherwise: `saveAndExit(sceneToLeaveTo, ...)`.

### `FlightResultsDialog` (post-flight crash report)

Three buttons:

- `Btn_KSC` at `FlightResultsDialog:565`: direct
  `HighLogic.LoadScene(SPACECENTER)`. Skips PauseMenu entirely.
- `Btn_Menu` at `FlightResultsDialog:599`: direct
  `HighLogic.LoadScene(MAINMENU)`. Skips PauseMenu entirely.
- `Btn_TS` at `FlightResultsDialog:533`: routes through `onLeavingFlight`
  (PreFlightCheck `FacilityOperational`) -> `onLeavingFlightProceed` ->
  `HighLogic.LoadScene(TRACKSTATION)` at `FlightResultsDialog:710`.

`FlightResultsDialog`'s KSC and Menu buttons never fire `onSceneConfirmExit`
and never run KSP's danger/quit confirmation popups. Patching `saveAndExit`
alone would miss them.

### Map-view shortcuts

`FlightUIModeController` has zero scene-transition calls. The map view does not
own Space Center / Tracking Station shortcuts. Only PauseMenu and
FlightResultsDialog source flight-exit transitions.

### Convergence point

Every flight-exit path eventually calls `HighLogic.LoadScene(GameScenes)`. By
that point all stock warnings / quit confirmations have already been confirmed
by the user. This is the only single-method chokepoint that catches every
case (PauseMenu both branches, FlightResultsDialog all three buttons, future
mods).

## Architecture

### Two existing flight-scene-exit suppression flags (do not duplicate)

`RecordingStore` already exposes two one-shot guards used by Discard Re-Fly:

- `ArmNextTreeSceneExitCommitSuppression(reason)` /
  `TryConsumeNextTreeSceneExitCommitSuppression(scene, out reason)`
  (`RecordingStore.cs:1166, :1175`). Suppresses the auto-stash-as-pending side
  effect of `OnSceneChangeRequested` -> `FinalizeTreeOnSceneChange`. Consumed
  inside `FinalizeTreeOnSceneChange` (`ParsekFlight.cs:2734`) where it routes
  to `DiscardActiveTreeForSuppressedSceneExit` instead of stashing, and inside
  `OnSceneChangeRequested`'s `else if` branch (`ParsekFlight.cs:2690`) when
  there is no active tree.
- `ArmNextActiveTreeRestoreSuppression` / `TryConsumeNextActiveTreeRestoreSuppression`
  (`RecordingStore.cs:1200`, :1209). Distinct flag for the OnLoad active-tree
  restore pass.

`RevertInterceptor.DiscardReFlyHandler` (`RevertInterceptor.cs:514-522`) arms
*both* before calling `HighLogic.LoadScene(SPACECENTER / EDITOR)`. The new
prefix MUST treat that armed-state as "another path owns this transition" and
get out of the way. Otherwise the prefix sees a regular non-Re-Fly exit (the
marker has already been cleared by Discard Re-Fly), spots a pending tree, and
re-prompts the merge dialog - defeating the discard.

### `MergeDialog.ShowTreeDialog` already handles Re-Fly correctly

Critical finding from review: there is no need for a separate
`ReFlyExitDialog` class.

- `MergeDialog.ShowTreeDialog` already detects an active Re-Fly marker
  (`MergeDialog.cs:144-169`) and adapts the body copy to show the Re-Fly
  recording's vessel + duration plus a "this cannot be undone" warning.
- The "Merge to Timeline" button calls `MergeCommit`, which already invokes
  `TryCommitReFlySupersede` (`MergeDialog.cs:336-337`) - the canonical Re-Fly
  merge path with full supersede / tombstone / quicksave handling.
- The "Discard" button calls `MergeDiscard`, which calls
  `TryDiscardActiveReFlyAttempt` first (`MergeDialog.cs:382`). That function
  is the canonical Re-Fly attempt-discard path and performs *all* required
  cleanup: remove committed attempt recordings, purge attempt game-state
  events, purge in-place attempt events, delete attempt recording files,
  clear transient fields, restore in-place original from snapshot or trim
  back to origin RP, promote origin RP, purge session RPs, restore detached
  committed tree, pop pending tree, clear pending science subjects + replay
  scope, clear marker + journal, bump supersede state version, apply
  Re-Fly-revert button gate, clear pre-Re-Fly anchor snapshots, recalculate
  ledger, save persistent state durably (`MergeDialog.cs:405-478`).

The reviewer's P1 concern about an under-implemented `ReFlyExitDialog.Discard`
dissolves because we reuse the existing path. **The Re-Fly variant becomes
"same `ShowTreeDialog`, Re-Fly-scoped button labels".**

### Tree finalize timing - pre-finalize before any decision

The dialog operates on a `RecordingTree` that must already be the *pending*
tree (because `MergeCommit` -> `RecordingStore.CommitPendingTree` and
`MergeDiscard` -> `RecordingStore.DiscardPendingTree` / `PopPendingTree`
expect that contract). So the prefix runs the existing finalization flow
*before* showing the dialog, then shows the dialog on the pending tree.

**Pre-finalize must come before the decision matrix and the idle-on-pad
fast path.** `FinalizeTreeOnSceneChangeCore` (`ParsekFlight.cs:2721`) is
where `FinalizeTreeRecordings` runs (`ParsekFlight.cs:2800`), and that's
the call that backfills per-recording `TerminalStateValue` and
`MaxDistanceFromLaunch` (the `#290d` post-finalize-state contract called
out in the comment at `ParsekFlight.cs:2828`). If the prefix consults
those fields before the call:

- `ShouldShowCommitApproval` (autoMerge ON, landed/splashed approval gate)
  reads a null `TerminalStateValue` and falls through to `None`. Stock
  LoadScene runs, `OnSceneChangeRequested` finalizes, the destination
  scene's `ShowDeferredMergeDialog` re-shows the same popup we tried to
  prevent. The fix regresses to the old behaviour for the most common
  approval case.
- The idle-on-pad fast path reads a stale `MaxDistanceFromLaunch` and
  may auto-discard a recording that has actually moved.

So the ordering is: pre-finalize unconditionally first, *then* run the
variant decision and the idle-on-pad fast path against the pending tree
that pre-finalize produced.

After Merge or Discard, both `pendingTree` and `activeTree` are clean. When
the dialog callback re-invokes `HighLogic.LoadScene(dest)`, the resulting
`OnSceneChangeRequested` finds `activeTree == null` (early-out at
`ParsekFlight.cs:2686`), and we have not armed the existing
`NextTreeSceneExitCommitSuppression` flag, so the `else if` at
`ParsekFlight.cs:2690` is also a no-op. There is nothing for
`OnSceneChangeRequested` to do for the tree path - it just runs the rest of
its scene-exit cleanup (watch mode, ghosts, terrain cache, etc.).

This eliminates the need for the `SuppressNextTreeSceneExitFinalize` flag
that the previous draft proposed - the existing pending-tree handoff is
already the right separation of concerns.

### Persisting our mutations to disk

`PauseMenu.saveAndExit` at `PauseMenu.cs:1781-1804` writes persistent.sfs at
line 1785 *before* calling `HighLogic.LoadScene`. By the time our prefix
fires, the stock save reflects the pre-merge / pre-discard state. Our
callback then mutates `CommittedRecordings`, `pendingTree`, the ledger,
science subjects, RPs, supersede relations, marker, journal, etc. - and
re-invokes `HighLogic.LoadScene` directly. None of those mutations reach
disk through the stock saveAndExit pipeline.

Symptom matrix:

- **Space Center / Tracking Station** destinations: the destination scene's
  ScenarioModule cycle eventually re-saves and our state lands in
  persistent.sfs - but a crash or quit between dialog and next save loses
  the merge / discard.
- **Quit to Main Menu**: the game unloads immediately. `persistent.sfs`
  stays at the pre-mutation state. Re-loading the save reveals
  inconsistent Parsek state.
- **PauseMenu dangerous-no-save path** (`PauseMenu.cs:1611-1633`,
  `Parameters.Flight.CanRestart`): stock did not save at all. Our merge
  /discard mutates state but nothing reaches disk.
- **FlightResultsDialog Btn_KSC / Btn_Menu** (direct LoadScene at
  `FlightResultsDialog.cs:565, 599`): same as above - stock did not save.

Fix: the proceed callback **always** writes a fresh persistent save before
re-invoking `HighLogic.LoadScene`:

```csharp
GamePersistence.SaveGame(
    HighLogic.CurrentGame.Updated(),
    "persistent",
    HighLogic.SaveFolder,
    SaveMode.OVERWRITE);
SceneExitInterceptor.s_AllowNextLoadScene = true;
HighLogic.LoadScene(scene);
```

This is what stock saveAndExit does, hoisted into our callback so it
captures our post-dialog state. For destinations stock would not have
saved (CanRestart, FlightResultsDialog direct), this is a net new save -
acceptable because the player just made an explicit Merge/Discard choice
and we want it persisted. The callback runs the save unconditionally; no
"did stock already save?" detection needed.

`MergeDialog.TryDiscardActiveReFlyAttempt` already calls
`SaveDiscardedReFlyStateDurably(sessionId)` at `MergeDialog.cs:459`. The
unconditional callback save is redundant but not harmful for that path
(both write the same persistent.sfs). The redundant write is cheap and
removes a "did the right path save?" branching surface.

## Implementation

### New file: `Source/Parsek/SceneExitInterceptor.cs`

Owns:

- A Harmony Prefix on `HighLogic.LoadScene(GameScenes)`.
- `internal static bool s_AllowNextLoadScene` - one-shot bypass token. Set by
  the dialog button callbacks just before re-invoking `HighLogic.LoadScene`,
  consumed by the next prefix entry.
- `internal static DialogVariant ShouldShowDialogBeforeSceneChange(GameScenes
  destination)` - pure decision helper, unit-testable.
- `internal enum DialogVariant { None, RegularMerge, ReFlyAttempt }`.
- `Action<GameScenes> ShowHookForTesting` test seam mirroring `RevertInterceptor`.

Patch sketch:

```csharp
[HarmonyPatch(typeof(HighLogic), nameof(HighLogic.LoadScene), new[] { typeof(GameScenes) })]
internal static class HighLogic_LoadScene_Patch
{
    static bool Prefix(GameScenes scene)
    {
        // (1) one-shot self-bypass token: our own dialog callback re-invoked LoadScene.
        if (SceneExitInterceptor.s_AllowNextLoadScene)
        {
            SceneExitInterceptor.s_AllowNextLoadScene = false;
            ParsekLog.Verbose("SceneExit",
                $"LoadScene prefix: bypassing via s_AllowNextLoadScene dest={scene}");
            return true;
        }

        // (2) cheap filter - only intercept exits from FLIGHT to a flight-exit dest.
        if (HighLogic.LoadedScene != GameScenes.FLIGHT) return true;
        if (scene != GameScenes.SPACECENTER &&
            scene != GameScenes.TRACKSTATION &&
            scene != GameScenes.MAINMENU &&
            scene != GameScenes.EDITOR) return true;

        // (3) PEEK existing Discard-Re-Fly suppression. If RevertInterceptor armed
        //     ArmNextTreeSceneExitCommitSuppression, this transition is already
        //     owned by Discard Re-Fly - get out of the way without consuming the
        //     flag (OnSceneChangeRequested / FinalizeTreeOnSceneChange consume it).
        if (RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed)
        {
            ParsekLog.Info("SceneExit",
                $"LoadScene prefix: bypassing - existing tree-scene-exit-commit " +
                $"suppression armed (Discard Re-Fly path) dest={scene}");
            return true;
        }

        // (4) pre-finalize FIRST so all decisions read finalized data
        //     (TerminalStateValue, MaxDistanceFromLaunch). Skip if a previous
        //     dialog open already produced a pending tree (popup-teardown
        //     retry case): activeTree was nulled at the end of the prior
        //     FinalizeTreeOnSceneChangeCore, so a second invocation would NRE.
        var flight = ParsekFlight.fetch;
        bool needsPreFinalize =
            flight != null
            && flight.HasActiveTree
            && !RecordingStore.HasPendingTree;
        if (needsPreFinalize)
        {
            flight.FinalizeTreeOnSceneChangeForCallback(scene);
        }

        // (5) decision matrix - reads the now-finalized pending tree.
        var pending = RecordingStore.PendingTree;
        if (pending == null)
        {
            // Nothing to confirm. Let stock LoadScene proceed.
            return true;
        }

        var variant = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(scene, pending);
        if (variant == DialogVariant.None) return true;

        // (6) idle-on-pad auto-discard fast path mirroring ParsekScenario.cs:1682-1689.
        //     Reads finalized MaxDistanceFromLaunch on the pending tree.
        if (SceneExitInterceptor.TryAutoDiscardIdlePendingTree(scene, pending))
            return true;

        // (7) show dialog. Same dialog for both variants - Re-Fly-aware copy
        //     and Re-Fly-aware MergeCommit / MergeDiscard already live inside
        //     MergeDialog. The variant only changes button-label copy.
        Action proceed = () =>
        {
            // P1.B: persist the dialog's mutations before the transition.
            // Stock saveAndExit (PauseMenu.cs:1785) saved BEFORE our prefix
            // ran, so its on-disk state is pre-mutation. Mainline-quit and
            // CanRestart-no-save paths never saved at all. Always write a
            // fresh persistent save so committed/discarded state lands on
            // disk regardless of which entry point fired.
            try
            {
                GamePersistence.SaveGame(
                    HighLogic.CurrentGame.Updated(),
                    "persistent",
                    HighLogic.SaveFolder,
                    SaveMode.OVERWRITE);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SceneExit",
                    $"Pre-LoadScene SaveGame threw {ex.GetType().Name}: {ex.Message} - " +
                    $"continuing transition (state may be lost on game unload)");
            }
            SceneExitInterceptor.s_AllowNextLoadScene = true;
            HighLogic.LoadScene(scene);
        };

        MergeDialog.ShowTreeDialog(
            pending,
            buttonLabels: variant == DialogVariant.ReFlyAttempt
                ? MergeDialogButtonLabels.ReFlyAttempt
                : MergeDialogButtonLabels.Default,
            postChoice: proceed);

        return false;   // block stock LoadScene; dialog drives it
    }
}
```

Notes:

- The bypass flag is intentionally global / one-shot. KSP is single-threaded
  for game logic; lifetime is "set immediately before re-invoke, consumed by
  next prefix". If a stray `HighLogic.LoadScene` slips in between set and
  consume, log Warn and fall through.
- The prefix early-returns on the first comparison for non-FLIGHT loads, so
  the cost added to KSP's hot LoadScene path is one branch.
- Step (3) is a *peek*, not a consume - the existing Discard Re-Fly path
  relies on `FinalizeTreeOnSceneChange` consuming the flag at
  `ParsekFlight.cs:2734`, so we must not steal it.
- Step (4)'s `HasActiveTree && !HasPendingTree` guard handles the
  popup-teardown retry case (P2): a prior dialog open already pre-finalized
  (cleared `activeTree`, stashed `pendingTree`) but the popup was dismissed
  without a button click. On the next prefix entry we skip pre-finalize and
  reuse the existing pending tree. `FinalizeTreeOnSceneChangeCore`
  dereferences `activeTree` unconditionally (`ParsekFlight.cs:2800, :2810,
  :2829, :2836`); calling it with `activeTree == null` would NRE.
- `ParsekFlight.HasActiveTree` is a new public read-only property exposing
  `activeTree != null` without exposing the field itself.

### Decision helper

Pure helper `ShouldShowDialogBeforeSceneChange(GameScenes destination,
RecordingTree finalizedPendingTree)`. Mirrors the OnLoad logic at
`ParsekScenario.cs:1660-1723` so the pre-transition gate matches the
existing post-load gate, but reads from the *already-finalized* pending
tree the prefix passes in:

```
if (Re-Fly session active)                                                   -> ReFlyAttempt
if (autoMerge OFF)                                                           -> RegularMerge
if (autoMerge ON
    && destination in {SPACECENTER, TRACKSTATION}
    && root recording terminal state in {Landed, Splashed})                  -> RegularMerge   // = ShouldShowCommitApproval
if (destination == MAINMENU)                                                 -> RegularMerge   // new: was force-auto-merge
otherwise                                                                    -> None
```

The "no pending tree" case is filtered earlier in the prefix (step 5) so
the helper is only invoked when a finalized tree exists.

Reading `TerminalStateValue` from the finalized pending tree (not the
live-recorder activeTree) is what makes the autoMerge approval gate work
pre-transition. See "Tree finalize timing" above for why ordering matters.

Idle-on-pad auto-discard (`ParsekScenario.cs:1682-1689, :4825-4834`) runs
as a fast path *after* the variant decision inside the prefix and reads
`MaxDistanceFromLaunch` from the same finalized tree so it sees the
post-#290d-backfill value rather than the stale live-recorder value.

### `MergeDialog.cs` changes

- New `ShowTreeDialog(RecordingTree tree, MergeDialogButtonLabels labels,
  Action postChoice)` overload. Existing two-button layout (Merge / Discard)
  is unchanged - only the button label strings come from the new
  `MergeDialogButtonLabels` enum (`Default` -> "Merge to Timeline" /
  "Discard"; `ReFlyAttempt` -> "Merge Re-Fly to Timeline" / "Discard
  Re-Fly attempt"). Wrap the existing `MergeCommit` and `MergeDiscard`
  callbacks to invoke `postChoice?.Invoke()` after their existing work.
- `MergeCommit` and `MergeDiscard` are unchanged - they already do the
  right thing for Re-Fly via `TryCommitReFlySupersede` and
  `TryDiscardActiveReFlyAttempt`. **No new `ReFlyExitDialog` class.**
- Existing Re-Fly body-copy adaptation (`MergeDialog.cs:144-169`) stays.
  Tweak the headline copy slightly when the dialog is opened via the
  pre-transition path (e.g. "Re-Fly attempt - leaving flight" instead of
  "Confirm Merge to Timeline") - same body, different title and button
  labels.
- Existing `OnDismiss -> ClearPendingFlag` path stays
  (`MergeDialog.cs:217-223`). No change needed.
- **Add the merge-journal-active guard to the discard handlers, not just
  to button construction (P1.C).** The previous draft was wrong:
  `MergeDialog.MergeDiscard` does *not* currently check
  `ParsekScenario.ActiveMergeJournal`, and `TryDiscardActiveReFlyAttempt`
  unconditionally clears `scenario.ActiveMergeJournal = null` at
  `MergeDialog.cs:452`. `RevertInterceptor.DiscardReFlyHandler` is the
  only existing site with the defensive refusal (at
  `RevertInterceptor.cs:345-352`). Mirror that pattern in two places:
  - `MergeDialog.MergeDiscard`: at the top, before `TryDiscardActiveReFlyAttempt`,
    refuse if `ActiveMergeJournal != null` with a `ParsekLog.Warn` and a
    `PostScreenMessage("Discard: merge in progress - retry in a moment")`.
  - `MergeDialog.TryDiscardActiveReFlyAttempt`: at the top, refuse with
    the same pattern. Belt-and-braces for any future call site that
    bypasses `MergeDiscard`.
  Hide the Discard button at button-build time too when the journal is
  active (mirrors `ReFlyRevertDialog`'s button gate). Hiding alone is not
  enough - the handler refusal is the load-bearing safety check, since
  any test path or future call site that bypasses the dialog can still
  reach the handler. This also fixes a pre-existing bug (the post-load
  deferred dialog has the same hole today); the new pre-transition path
  just makes it more reachable because Re-Fly transitions now route
  through `MergeDialog.MergeDiscard` more consistently.

### `RecordingStore.cs` changes

- Add `internal static bool IsNextTreeSceneExitCommitSuppressionArmed` -
  read-only public peek (no consume) for use by the new prefix. The
  existing `NextTreeSceneExitCommitSuppressionArmedForTesting` getter
  (`RecordingStore.cs:1191`) does the same thing under a "ForTesting"
  name; either rename + reuse, or add a sibling property. Renaming is
  cleaner since the flag's existence is no longer test-only.
- No new flag. The `SuppressNextTreeSceneExitFinalize` flag the previous
  draft proposed is unnecessary - the existing pending-tree contract
  carries the same information.

### `ParsekFlight.cs` changes

- New `internal void FinalizeTreeOnSceneChangeForCallback(GameScenes scene)`
  - delegates to the existing `FinalizeTreeOnSceneChangeCore`. The
  in-callback finalize is identical to what `OnSceneChangeRequested` would
  have done a moment later. Caller is responsible for guarding against
  `activeTree == null` (the prefix's `HasActiveTree && !HasPendingTree`
  check covers this).
- New `internal bool HasActiveTree => activeTree != null;` - read-only peek
  for the prefix without exposing the field.
- No changes to `FinalizeTreeOnSceneChange` itself. After the callback runs
  `MergeCommit` or `MergeDiscard`, both `activeTree` and `pendingTree` are
  cleared, so the existing checks at `ParsekFlight.cs:2686, :2690` are
  no-ops on the second LoadScene re-invoke.

### `ParsekScenario.cs` changes

- Drop `forceAutoMerge = LoadedScene == MAINMENU` at `ParsekScenario.cs:1659`.
  Pre-transition path now handles main menu; deferred-fallback path will too.
- Reuse `SceneExitInterceptor.ShouldShowDialogBeforeSceneChange` from the
  OnLoad path instead of duplicating the matrix. (Optional - shared logic
  is nicer but not strictly required for correctness.)
- Add canary Warn inside `ShowDeferredMergeDialog` coroutine
  (`ParsekScenario.cs:4807-4842`): "deferred merge dialog fired -
  pre-transition intercept missed scene=... reason=...". Useful canary for
  paths we did not patch (mods, KSP version drift).

## Files changed

| Status | Path | Change |
| --- | --- | --- |
| New | `Source/Parsek/SceneExitInterceptor.cs` | Harmony patch + decision helper + idle-pad fast path; pre-finalize-then-decide ordering; persistent save in callback |
| Modified | `Source/Parsek/MergeDialog.cs` | `(labels, postChoice)` overload of `ShowTreeDialog`; new `MergeDialogButtonLabels` enum; pre-transition title copy; **add `ActiveMergeJournal` guard to `MergeDiscard` and `TryDiscardActiveReFlyAttempt` (P1.C)**; hide Discard when journal active |
| Modified | `Source/Parsek/ParsekFlight.cs` | Expose `FinalizeTreeOnSceneChangeForCallback` and `HasActiveTree` |
| Modified | `Source/Parsek/RecordingStore.cs` | Rename `NextTreeSceneExitCommitSuppressionArmedForTesting` to `IsNextTreeSceneExitCommitSuppressionArmed` (or add sibling) |
| Modified | `Source/Parsek/ParsekScenario.cs` | Drop main-menu force-auto-merge; deferred-coroutine canary Warn; (optional) reuse decision helper |
| New | `Source/Parsek.Tests/SceneExitInterceptorTests.cs` | Decision helper matrix; prefix bypass conditions; pre-finalize wiring; popup-teardown retry; persistent-save test seam |
| Modified | `Source/Parsek.Tests/MergeDialogTests.cs` | New `(labels, postChoice)` path; ReFlyAttempt button labels; journal-active guard tests for `MergeDiscard` + `TryDiscardActiveReFlyAttempt` |
| Modified | `Source/Parsek/InGameTests/RuntimeTests.cs` | Dialog appears in flight scene, not after load (multiple flight-exit paths) |

No `ReFlyExitDialog.cs` (removed from previous draft - existing
`MergeDialog.ShowTreeDialog` covers Re-Fly natively).

## Test matrix

### `ShouldShowDialogBeforeSceneChange` decision helper

| autoMerge | ReFly active | Has pending tree | Term state | Destination | Expected |
| --- | --- | --- | --- | --- | --- |
| OFF | no | yes | any | KSC / TS / MAINMENU / EDITOR | RegularMerge |
| ON | no | yes | Landed | KSC | RegularMerge |
| ON | no | yes | Landed | TS | RegularMerge |
| ON | no | yes | Crashed | KSC | None |
| ON | no | yes | any | MAINMENU | RegularMerge (new) |
| any | yes | yes | any | any non-flight | ReFlyAttempt |
| any | no | no | - | any | None |

### Prefix bypass conditions (P1 + P2 coverage)

- `s_AllowNextLoadScene` armed -> prefix returns true, flag consumed.
- `s_AllowNextLoadScene` armed but destination is not flight-exit -> still
  consumed (one-shot guarantee).
- `RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed` true (Discard
  Re-Fly path) -> prefix returns true, flag NOT consumed (preserves
  existing `FinalizeTreeOnSceneChange` consume contract).
- `LoadedScene != FLIGHT` -> prefix returns true regardless of other state.
- Destination not in {KSC, TS, MAINMENU, EDITOR} -> prefix returns true.

### Pre-finalize / suppression flag separation (P1.A + P2 coverage)

- Prefix calls `FinalizeTreeOnSceneChangeForCallback` BEFORE consulting
  `TerminalStateValue` or `MaxDistanceFromLaunch`. Resulting
  `RecordingStore.PendingTree` is non-null and carries finalized
  per-recording terminal state. `ParsekFlight.activeTree` is null
  afterwards.
- AutoMerge ON, Landed/Splashed terminal: pre-finalize backfills
  `TerminalStateValue`, decision helper returns `RegularMerge`, dialog
  shows. Without pre-finalize-first ordering this case would have
  fallen through to `None` and shown the deferred post-load dialog
  instead - covers the P1.A regression.
- Idle-on-pad fast path: reads `MaxDistanceFromLaunch` from the finalized
  pending tree, not from the live recorder. Asserts the
  pre-#290d-backfill stale value does not trigger a false auto-discard.
- Dialog `MergeCommit` runs: `pendingTree` is committed and cleared.
  Re-invoke `HighLogic.LoadScene` -> `OnSceneChangeRequested` finds
  `activeTree == null`, no `NextTreeSceneExitCommitSuppression` armed,
  `else if` branch is a no-op. No double-commit.
- Dialog `MergeDiscard` runs: `pendingTree` is popped. Re-invoke ->
  same no-op path. No leak of pending tree.
- Popup-teardown retry (P2): first prefix entry pre-finalizes (clears
  `activeTree`, stashes `pendingTree`). Popup is dismissed without a
  button click. Player triggers another scene exit. Prefix re-enters
  with `HasActiveTree == false` and `HasPendingTree == true`; skips
  pre-finalize, reads decision from existing pending tree, shows dialog.
  No NRE on the second `FinalizeTreeOnSceneChangeForCallback` call.
- Pre-finalize produces no pending tree (e.g. unfinalized recorder had
  nothing to commit): prefix's step 5 sees `pending == null` and lets
  the transition through.
- Existing Discard Re-Fly path: `RevertInterceptor.DiscardReFlyHandler`
  arms the existing flag, calls `LoadScene`, our prefix bypasses (step
  3), stock `OnSceneChangeRequested` runs, `FinalizeTreeOnSceneChange`
  consumes the flag and discards `activeTree` per the existing contract.
  Unchanged.

### Persistence (P1.B coverage)

- Callback writes `GamePersistence.SaveGame(persistent, OVERWRITE)`
  before re-invoking `HighLogic.LoadScene`.
- Quit-to-Main-Menu: assert that post-callback persistent.sfs reflects
  the merge / discard state (not the pre-callback state stock saved).
- `CanRestart`-no-save path: assert callback save fires even though
  stock did not save.
- FlightResultsDialog Btn_KSC / Btn_Menu direct-LoadScene: assert
  callback save fires (stock did not save).
- Save throws (disk full, IO error): assert prefix logs Warn and
  continues with LoadScene (degraded behaviour - state may be lost on
  unload, but blocking the transition entirely is worse).

### Merge-journal-active guard (P1.C coverage)

- `MergeDialog.MergeDiscard` with `ActiveMergeJournal != null`: refuses
  with Warn log + screen message; no state mutation; pending tree
  remains stashed.
- `MergeDialog.TryDiscardActiveReFlyAttempt` invoked directly with
  `ActiveMergeJournal != null` (test bypass / future caller): same
  refusal contract.
- Merge button still works when journal is active (matches
  `ReFlyRevertDialog`'s "Merge always allowed, Discard hidden" gate).
- Button-build hides Discard when journal active - regression test
  asserts the dialog only ever exposes one button in that state.

### Defensive tests

- Idle-on-pad pre-transition auto-discard fires before showing dialog
  (using finalized `MaxDistanceFromLaunch` per P1.A).
- Merge-journal-active hides Discard on regular and Re-Fly button-labels
  variants; handler refusal also fires when invoked directly.
- Popup `OnDismiss` without a button click: existing
  `ClearPendingFlag("popup teardown")` runs; no `s_AllowNextLoadScene`
  leak (it was never set); pending tree remains stashed. Subsequent
  scene-exit attempt re-enters the prefix and reuses the existing
  pending tree without re-finalizing.
- Deferred coroutine path emits the canary Warn when triggered.

### In-game tests

- In flight, click Esc -> Space Center: dialog appears in flight scene,
  not after Space Center loads.
- In flight, click Esc -> Tracking Station: same.
- In flight, click Esc -> Quit to Main Menu: stock confirmation popup runs
  first, then our merge dialog, then main menu loads.
- Crash, FlightResultsDialog appears, click Space Center button: merge
  dialog appears in flight scene, not after Space Center loads.
- Re-Fly session active, click Esc -> Space Center: dialog appears with
  Re-Fly-scoped button labels, Discard wires through
  `TryDiscardActiveReFlyAttempt`.
- Re-Fly session active, click Revert button: existing `ReFlyRevertDialog`
  fires (covered by `RevertInterceptor`). Discard Re-Fly from that dialog
  triggers `RecordingStore.ArmNextTreeSceneExitCommitSuppression` ->
  `HighLogic.LoadScene(SPACECENTER)`; our prefix sees the armed flag and
  bypasses without re-prompting.
- Low-altitude flight (atmosphere, can't save): orange "you will lose
  progress" popup runs first, then on Yes our merge dialog appears (not
  after the destination scene loads).

## Risks

- We patch `HighLogic.LoadScene`, a hot KSP method. Filter is one
  comparison; cost is negligible. Verify in-game with `Verbose` log on
  every prefix entry during the first few minutes of a session.
- Pre-finalize side effects fire in flight (BG checkpoint, ballistic-tail
  extension, IncompleteBallisticSceneExitFinalizer, ledger-orchestrated
  resource deltas). These already run during `OnSceneChangeRequested` -
  we just hoist them by a few frames. No KSP-state mutation that depends
  on the destination scene having loaded yet.
- Pre-finalize "starves" the active recorder (`recorder = null`,
  `backgroundRecorder.Shutdown()` at `ParsekFlight.cs:2840-2845`). If the
  player dismisses the popup without clicking a button (P2 retry case),
  the player is left in flight with no recording. Acceptable - they
  can't escape via Esc anyway because `MultiOptionDialog` without an
  explicit Cancel button does not allow Esc-dismiss; the popup-teardown
  case is a Unity edge condition, not a normal flow.
- Save throw in callback: logged as Warn, transition continues. Player
  may lose Parsek state on game unload. Worse alternative would be to
  block the transition forever - declined.
- Dialog while in flight: KSP's `MultiOptionDialog` works in any scene
  with a live UI canvas. Confirmed by existing `ReFlyRevertDialog` which
  spawns the same kind of popup mid-flight.
- Interaction with `fix-320` deferred-flight-results state machine
  (`docs/dev/done/plans/fix-320-merge-confirm-before-crash-report.md`):
  arming / disarming must still be correct when the merge dialog now opens
  pre-transition. Re-read fix-320 doc before touching any of its code.
- Interaction with Discard Re-Fly (P1.1): covered by the
  `IsNextTreeSceneExitCommitSuppressionArmed` peek-bypass. Test coverage
  asserts the flag is not consumed by the prefix.
- KSP version drift: if a future KSP update adds new flight-exit entry
  points or changes the `HighLogic.LoadScene` signature, the prefix needs
  updating. The deferred-coroutine canary is the early-warning system.

## CHANGELOG / todo updates

Per `.claude/CLAUDE.md` "Documentation Updates - Per Commit, Not Per PR":

- `CHANGELOG.md`: "Merge confirmation dialog now appears before leaving
  flight, not after the destination scene loads. Quit to Main Menu also
  shows the dialog (previously force-auto-merged). Re-Fly sessions show
  Re-Fly-attempt-scoped button labels on the same dialog."
- `docs/dev/todo-and-known-bugs.md`: add the dialog-timing item if
  missing; mark closed once shipped.
