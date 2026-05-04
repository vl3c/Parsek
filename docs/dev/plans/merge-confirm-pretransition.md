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

### Map-view, app-launcher, and toolbar shortcuts

`FlightUIModeController` has zero direct scene-transition calls. App
launcher and toolbar buttons that route to KSC / TS during flight all
funnel through `HighLogic.LoadScene` (verified by spot-grep on
`Assembly-CSharp.dll`). The chokepoint patch covers them automatically -
the plan doesn't need a per-source patch list.

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

### Tree finalize timing - defer everything to the dialog callback

**Update from Opus re-review pass 2**: the previous draft proposed
"pre-finalize before showing dialog". That is unsafe because
`FinalizeTreeOnSceneChangeCore` (`ParsekFlight.cs:2721`) is monolithic:
it calls `FinalizeTreeRecordings` at `:2800` which calls
`recorder.ForceStop()` at `ParsekFlight.cs:11218` and flushes the
recorder into the tree, then drops `recorder`, `backgroundRecorder`,
and `activeTree` to null at `ParsekFlight.cs:2840-2847`. There is no
clean "compute terminal state in place without stopping the recorder"
seam. If the popup is dismissed without a button click (Unity
edge case, mod-spawned higher-priority popup, scene-load orchestrator
forced UI teardown, `PopupDialog.DismissPopup` from foreign code),
the player is stranded in flight with a dead recorder and a stashed
pending tree.

**Resolution: defer all finalize work to the dialog callback.** The
prefix shows the dialog on the live `activeTree` (recorder still
running). Decision-helper queries read live state directly:

- Variant: read `ParsekScenario.ActiveReFlySessionMarker` and
  `ParsekScenario.IsAutoMerge`; for the autoMerge-ON Landed/Splashed
  approval gate, query the active vessel's
  `vessel.LandedOrSplashed` directly instead of relying on the
  post-finalize `TerminalStateValue`. The two reads agree on the
  outcome - `FinalizeTreeRecordings`' terminal-state computation
  derives from the same vessel situation snapshot that's live now.
- Idle-on-pad fast path: implement
  `ParsekFlight.IsActiveTreeIdleOnPad()` mirroring the existing
  `IsTreeIdleOnPad(RecordingStore.PendingTree)` (`ParsekScenario.cs:1684`).
  Walk the active recordings and read the live vessel's
  `srfRelRotation` / position vs the launch reference. Doesn't depend
  on `MaxDistanceFromLaunch` because we have the live vessel.

On button click (Merge or Discard), the callback runs the full
finalize-then-act sequence:

```csharp
Action proceed = () =>
{
    var fl = ParsekFlight.fetch;
    fl?.FinalizeTreeOnSceneChangeForCallback(scene);   // stash + teardown
    var pending = RecordingStore.PendingTree;
    if (pending != null)
    {
        if (userChoseMerge) MergeDialog.MergeCommit(pending, decisions, spawnCount);
        else                MergeDialog.MergeDiscard(pending);
    }
    SafeWritePersistent(scene);                        // P1.B (see below)
    SceneExitInterceptor.s_AllowNextLoadScene = true;
    HighLogic.LoadScene(scene);
};
```

Result:

- **Popup dismissed without click**: nothing was finalized. Recorder
  is still recording. `activeTree` is intact. Player resumes flight.
  No leaked state. Plan's previous "pendingTree leak on dismiss"
  branch is gone.
- **Popup clicked**: finalize, commit/discard, save, transition. Same
  end-state as the post-load deferred dialog produces today.
- **F9 quickload while dialog open**: KSP calls `HighLogic.LoadScene(FLIGHT)`
  to tear down for quickload. Our prefix's destination filter passes
  `FLIGHT` through. Popup is destroyed during scene tear-down. No
  pre-finalize happened, no pendingTree leak. Quickload proceeds
  normally.
- **MergeCommit operates on the just-stashed pending tree**: the
  callback calls `FinalizeTreeOnSceneChangeForCallback` *first*, which
  goes through the existing `StashPendingTree` path. So when
  `MergeCommit` calls `RecordingStore.CommitPendingTree()` next, the
  pending tree exists. Same contract as today's post-load dialog.

The downside is that the dialog's body copy can't show the
post-finalize summary (terminal state label, ballistic-extension
endpoint, etc.) - it shows the pre-finalize live state. For the
common case (vessel sitting at KSC after a flight) this is identical
to the post-finalize state. Edge cases (incomplete ballistic extension,
re-snapshot of stable terminals) show summary text computed on live
state, which is a minor regression from today's deferred dialog.
Acceptable - the alternative is reviving the recorder on dismiss, and
that's a much harder refactor.

After Merge or Discard runs in the callback, both `pendingTree` and
`activeTree` are clean. When the callback re-invokes
`HighLogic.LoadScene(dest)`, the resulting `OnSceneChangeRequested`
finds `activeTree == null` (early-out at `ParsekFlight.cs:2686`), no
`NextTreeSceneExitCommitSuppression` armed, `else if` branch is a
no-op. No double-finalize.

This eliminates the need for the `SuppressNextTreeSceneExitFinalize`
flag that the original draft proposed.

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
re-invoking `HighLogic.LoadScene`. Save failure is **fatal for MAINMENU**
(no later save will run before unload) and **logged-and-continued for
other destinations** (the destination scene's own save cycle will eventually
write our state):

```csharp
bool SafeWritePersistent(GameScenes dest)
{
    try
    {
        GamePersistence.SaveGame(
            HighLogic.CurrentGame.Updated(),
            "persistent",
            HighLogic.SaveFolder,
            SaveMode.OVERWRITE);
        return true;
    }
    catch (Exception ex)
    {
        ParsekLog.Error("SceneExit",
            $"Pre-LoadScene SaveGame threw {ex.GetType().Name}: {ex.Message} dest={dest}");
        if (dest == GameScenes.MAINMENU)
        {
            // Hard block: there is no later save. If we proceed, persistent.sfs
            // stays at the pre-merge state and the player loads back into an
            // inconsistent world. Show an error popup, leave the player in flight.
            PopupDialog.SpawnPopupDialog(...
                "Could not save before quitting to main menu. Try again, or " +
                "quit to space center first.");
            return false;
        }
        return true;   // SC/TS/EDITOR: degraded continuation acceptable
    }
}

Action proceed = () =>
{
    // ... finalize + MergeCommit/MergeDiscard above ...
    if (!SafeWritePersistent(scene)) return;   // dialog reappears next exit attempt
    SceneExitInterceptor.s_AllowNextLoadScene = true;
    HighLogic.LoadScene(scene);
};
```

For destinations stock would not have saved (CanRestart no-save path,
FlightResultsDialog direct LoadScene paths), the callback save is a net
new save - acceptable because the player just made an explicit Merge /
Discard choice and we want it persisted. The callback runs the save
unconditionally on success path; no "did stock already save?" detection
needed.

**Crew reservation race (P1.C from re-review)**: `MergeCommit` calls
`CrewReservationManager.SwapReservedCrewInFlight()` at `MergeDialog.cs:327`.
Stock saveAndExit already saved the *unswapped* roster. Our callback save
captures the post-swap state. Same correctness story as the rest of the
mutations: the callback save is mandatory, and a save throw on MAINMENU
is fatal for the same reason.

`MergeDialog.TryDiscardActiveReFlyAttempt` already calls
`SaveDiscardedReFlyStateDurably(sessionId)` at `MergeDialog.cs:459`. The
unconditional callback save is redundant but not harmful for that path
(both write the same persistent.sfs). On slow disks two sequential
saves of a large persistent.sfs may stutter for ~1s; acceptable. If
playtest reveals the stutter is annoying, a one-shot
`s_PersistentSavedThisTransition` flag inside `SafeWritePersistent`
collapses the double-save - keep it as a follow-up rather than gating
v1 on it.

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

Patch sketch (deferred-finalize):

```csharp
[HarmonyPatch(typeof(HighLogic), nameof(HighLogic.LoadScene), new[] { typeof(GameScenes) })]
[HarmonyPriority(Priority.Last)]
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
        //     dest == FLIGHT (quickload, RewindInvoker, vessel switch) passes through.
        if (HighLogic.LoadedScene != GameScenes.FLIGHT) return true;
        if (scene != GameScenes.SPACECENTER &&
            scene != GameScenes.TRACKSTATION &&
            scene != GameScenes.MAINMENU &&
            scene != GameScenes.EDITOR) return true;

        // (3) PEEK existing Discard-Re-Fly suppression. If RevertInterceptor armed
        //     ArmNextTreeSceneExitCommitSuppression, this transition is already
        //     owned by Discard Re-Fly - get out of the way without consuming the
        //     flag (FinalizeTreeOnSceneChange consumes it at ParsekFlight.cs:2734).
        if (RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed)
        {
            ParsekLog.Info("SceneExit",
                $"LoadScene prefix: bypassing - existing tree-scene-exit-commit " +
                $"suppression armed (Discard Re-Fly path) dest={scene}");
            return true;
        }

        // (4) decision matrix on LIVE state. No pre-finalize - that runs in
        //     the callback if and only if the player commits to a Merge or
        //     Discard choice. Popup-dismiss-without-click leaves the recorder
        //     and activeTree intact.
        var flight = ParsekFlight.fetch;
        if (flight == null || !flight.HasActiveTree) return true;

        var variant = SceneExitInterceptor.ShouldShowDialogBeforeSceneChange(
            scene, flight);
        if (variant == DialogVariant.None) return true;

        // (5) idle-on-pad auto-discard fast path. Reads live recorder /
        //     activeTree state via ParsekFlight.IsActiveTreeIdleOnPad.
        if (SceneExitInterceptor.TryAutoDiscardIdleActiveTree(scene, flight))
            return true;

        // (6) show dialog on the live activeTree. Same dialog for both
        //     variants - Re-Fly-aware copy and Re-Fly-aware MergeCommit /
        //     MergeDiscard already live inside MergeDialog.
        Action<bool> proceed = (isMerge) =>
        {
            // Phase 1: full finalize-then-act (matches what
            // OnSceneChangeRequested would have done a moment later).
            flight.FinalizeTreeOnSceneChangeForCallback(scene);
            var pending = RecordingStore.PendingTree;
            if (pending != null)
            {
                if (isMerge)
                {
                    var decisions = MergeDialog.BuildDefaultVesselDecisionsForPending(pending);
                    MergeDialog.MergeCommit(pending, decisions, ComputeSpawnCount(decisions));
                }
                else
                {
                    MergeDialog.MergeDiscard(pending);
                }
            }

            // Phase 2: persist mutations. Hard-block on MAINMENU save throw.
            if (!SafeWritePersistent(scene)) return;

            SceneExitInterceptor.s_AllowNextLoadScene = true;
            HighLogic.LoadScene(scene);
        };

        MergeDialog.ShowTreeDialog(
            flight.ActiveTreeForDisplay,
            buttonLabels: variant == DialogVariant.ReFlyAttempt
                ? MergeDialogButtonLabels.ReFlyAttempt
                : MergeDialogButtonLabels.Default,
            onMerge: () => proceed(isMerge: true),
            onDiscard: () => proceed(isMerge: false));

        return false;   // block stock LoadScene; dialog drives it
    }
}
```

Notes:

- `[HarmonyPriority(Priority.Last)]` (Opus re-review #13): we run after
  any other prefix that may already have rejected the call. Reduces the
  risk of starting a dialog that another mod's prefix would have
  cancelled. Verify against KSPCommunityFixes / KSPModFileLocalizer in
  playtest.
- The bypass flag is intentionally global / one-shot. KSP is
  single-threaded for game logic; lifetime is "set immediately before
  re-invoke, consumed by next prefix". A stray `HighLogic.LoadScene`
  in between would consume the flag with the wrong destination - log
  Warn and fall through. Narrow window since only our own callback
  re-invokes LoadScene from in-flight context.
- Step (3) is a *peek*, not a consume - the existing Discard Re-Fly
  path relies on `FinalizeTreeOnSceneChange` consuming the flag at
  `ParsekFlight.cs:2734`, so we must not steal it.
- Step (4) reads from `flight.HasActiveTree` only; no pre-finalize
  before the dialog. If the popup is dismissed without a button click,
  no state was mutated.
- Decision helper signature changed to take `ParsekFlight` instead of a
  pending tree. The helper queries live state - active recording's
  vessel via `flight.ActiveVessel` for the autoMerge-ON Landed/Splashed
  approval gate, etc.
- `flight.ActiveTreeForDisplay`, `flight.IsActiveTreeIdleOnPad()`, and
  `flight.HasActiveTree` are new public read-only seams on
  `ParsekFlight` that don't expose the private `activeTree` field
  directly.

### Decision helper

Pure helper `ShouldShowDialogBeforeSceneChange(GameScenes destination,
ParsekFlight flight)`. Mirrors the OnLoad logic at
`ParsekScenario.cs:1660-1723` so the pre-transition gate matches the
existing post-load gate, but reads live state from the supplied
`ParsekFlight`:

```
if (Re-Fly session active)                                                   -> ReFlyAttempt
if (autoMerge OFF)                                                           -> RegularMerge
if (autoMerge ON
    && destination in {SPACECENTER, TRACKSTATION}
    && active vessel.LandedOrSplashed)                                       -> RegularMerge
if (destination == MAINMENU)                                                 -> RegularMerge   // new: was force-auto-merge
otherwise                                                                    -> None
```

The autoMerge ON Landed/Splashed gate (which today uses
`ShouldShowCommitApproval` reading `TerminalStateValue` post-finalize)
is now computed from the live vessel via `vessel.LandedOrSplashed`. The
two values agree on the outcome - `FinalizeTreeRecordings` derives
`TerminalStateValue` from the same vessel-situation snapshot that the
live read returns. Document this in a comment in the helper so future
maintainers understand they cannot drift.

Idle-on-pad auto-discard runs after the variant decision and reads the
live activeTree via a new `ParsekFlight.IsActiveTreeIdleOnPad()` that
mirrors the existing `IsTreeIdleOnPad(RecordingStore.PendingTree)`
check used by the OnLoad path (`ParsekScenario.cs:1684`). The new
method walks active recordings and computes idle-on-pad from live
vessel position rather than from the post-#290d-backfilled
`MaxDistanceFromLaunch` field.

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
    `ParsekLog.ScreenMessage("Discard: merge in progress - retry in a moment", 3f)`
    (Opus re-review #11: existing code uses `ParsekLog.ScreenMessage`,
    not `ScreenMessages.PostScreenMessage`).
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
  in-callback finalize is identical to what `OnSceneChangeRequested`
  would have done a moment later. Only invoked from the dialog
  callback's Phase 1.
- New `internal bool HasActiveTree => activeTree != null;` - read-only
  peek for the prefix without exposing the field.
- New `internal RecordingTree ActiveTreeForDisplay => activeTree;`
  read-only accessor for the dialog's display data. The dialog must
  not mutate the returned reference; the eventual mutation happens
  inside `MergeCommit` / `MergeDiscard` after the callback's Phase 1
  finalize.
- New `internal bool IsActiveTreeIdleOnPad()` - mirrors the existing
  `IsTreeIdleOnPad(RecordingTree)` (private static helper used by
  `ParsekScenario.cs:1684` via `RecordingStore.PendingTree`) but reads
  the live `activeTree` and live vessel position. Walks recordings,
  computes distance-from-launch from live data rather than from the
  post-#290d `MaxDistanceFromLaunch` field.
- No changes to `FinalizeTreeOnSceneChange` itself. After the callback
  runs `MergeCommit` or `MergeDiscard`, both `activeTree` and
  `pendingTree` are cleared, so the existing checks at
  `ParsekFlight.cs:2686, :2690` are no-ops on the second LoadScene
  re-invoke.

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

### Deferred-finalize lifecycle (P1.A + Opus #1 + P2 coverage)

- Prefix decision reads live state only (`activeTree`, live vessel,
  marker, autoMerge setting). No pre-finalize, no recorder mutation,
  no `activeTree` teardown.
- AutoMerge ON, vessel landed at KSC, exit -> SPACECENTER: helper sees
  `vessel.LandedOrSplashed && IsAutoMerge && dest == SPACECENTER` ->
  `RegularMerge`. Dialog shows. Click Merge: callback finalizes,
  commits. Click Discard: callback finalizes, discards. Either way,
  state lands on disk via `SafeWritePersistent`.
- Popup dismissed without click: nothing was finalized; `activeTree`,
  recorder, `backgroundRecorder` all alive; player resumes flight.
  Subsequent scene-exit re-enters the prefix from the same in-flight
  state. No NRE risk because pre-finalize never ran.
- F9 quickload during open dialog: KSP calls
  `HighLogic.LoadScene(FLIGHT)`. Our prefix's filter rejects
  `dest == FLIGHT`. Popup is destroyed by scene tear-down. No
  pre-finalize had run, so no leaked pending tree. Quickload proceeds
  cleanly.
- Callback's full finalize (`FinalizeTreeOnSceneChangeForCallback`) +
  `MergeCommit` or `MergeDiscard` produces clean state: both
  `activeTree` and `pendingTree` are null afterwards. Re-invoked
  `HighLogic.LoadScene` triggers `OnSceneChangeRequested`, which
  early-outs at `ParsekFlight.cs:2686` (`activeTree == null`).
- Existing Discard Re-Fly path: `RevertInterceptor.DiscardReFlyHandler`
  arms the existing flag, calls `LoadScene`, our prefix bypasses (step
  3), stock `OnSceneChangeRequested` runs,
  `FinalizeTreeOnSceneChange` consumes the flag and discards
  `activeTree` per the existing contract. Unchanged.

### Persistence (P1.B coverage)

- Callback writes `GamePersistence.SaveGame(persistent, OVERWRITE)`
  before re-invoking `HighLogic.LoadScene`.
- Quit-to-Main-Menu: assert that post-callback persistent.sfs reflects
  the merge / discard state (not the pre-callback state stock saved).
- `CanRestart`-no-save path: assert callback save fires even though
  stock did not save.
- FlightResultsDialog Btn_KSC / Btn_Menu direct-LoadScene: assert
  callback save fires (stock did not save).
- Crew-swap captured (Opus #3): assert post-merge persistent.sfs has
  the swapped roster (`CrewReservationManager.SwapReservedCrewInFlight`
  ran, our save captured it).
- Save throws on MAINMENU: assert transition is *blocked*, error popup
  shown, `s_AllowNextLoadScene` not set, `pendingTree` not stashed
  (callback aborted before finalize? - actually finalize runs first;
  decide whether to roll-back finalize on save fail or accept the
  inconsistency. Document the trade-off in the plan.) Implementation
  decision: finalize runs first, save fails, error popup shown -
  user retries the exit, which re-enters the prefix; the second
  attempt sees `HasActiveTree == false` (already finalized) but
  `HasPendingTree == true`, takes a different code path (no
  pre-finalize, dialog already showed?). Need a "merge journal active /
  pending tree from previous prefix attempt" branch in the prefix.
  Open question - see "Open questions" below.
- Save throws on SPACECENTER / TRACKSTATION / EDITOR: assert prefix
  logs Warn and continues with LoadScene; destination scene's own
  save cycle eventually persists.

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
- In flight, click Esc -> Quit to Main Menu: stock confirmation popup
  runs first, then our merge dialog, then main menu loads.
- Crash, FlightResultsDialog appears, click Space Center button: merge
  dialog appears in flight scene, not after Space Center loads.
- Re-Fly session active, click Esc -> Space Center: dialog appears
  with Re-Fly-scoped button labels, Discard wires through
  `TryDiscardActiveReFlyAttempt`.
- Re-Fly session active, click Revert button: existing
  `ReFlyRevertDialog` fires (covered by `RevertInterceptor`). Discard
  Re-Fly from that dialog triggers
  `RecordingStore.ArmNextTreeSceneExitCommitSuppression` ->
  `HighLogic.LoadScene(SPACECENTER)`; our prefix sees the armed flag
  and bypasses without re-prompting.
- Low-altitude flight (atmosphere, can't save): orange "you will lose
  progress" popup runs first, then on Yes our merge dialog appears
  (not after the destination scene loads).
- Test runner overlay (Ctrl+Shift+T) open during merge dialog
  (Opus #8): both render correctly. Merge dialog input lock is
  `ControlTypes.All` which blocks game input but doesn't block IMGUI
  windows. Test runner stays interactive.
- F9 quickload while merge dialog is up (Opus #5): scene tears down,
  popup destroyed, quickload completes cleanly, no leaked pending
  tree.
- KSPCommunityFixes installed: assert our prefix runs after KSPCF's
  `HighLogic.LoadScene` patches. `HarmonyPriority.Last` is set on
  our prefix.

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

## Open questions

1. **MAINMENU save-fail rollback**: if Phase 1 finalize runs (committing
   or discarding the tree), then Phase 2 save fails on MAINMENU and we
   block the transition, the player is left in flight with `activeTree
   == null` (Phase 1 already cleared it) and either a committed tree
   in the persistent storage or a popped pending tree. They cannot
   resume recording the same flight. They can:

   - Try Quit-to-Main-Menu again - prefix sees `HasActiveTree ==
     false` and short-circuits to `return true` (no dialog needed,
     transition proceeds with the in-memory state we already
     mutated). But persistent.sfs is still pre-mutation. So this is
     just kicking the can down the road.
   - Try Quit-to-Main-Menu again, but the second time the save
     succeeds (transient disk error resolved). Acceptable recovery.
   - Use Space Center instead - prefix sees `HasActiveTree == false`,
     transitions to SC, the SC scene's own save cycle runs, captures
     our state.

   Document path 3 in the error popup ("Could not save before quitting
   to main menu. Try going to Space Center instead, then quit from
   there."). Accept that path 1 is degraded but path 3 is clean.

2. **Suppression flag leak on `LoadScene` throw** (Opus #6): narrow
   window where our prefix peeks the flag, bypasses, but stock
   `LoadScene` itself throws before reaching `OnSceneChangeRequested`.
   Flag stays armed; next regular flight-exit silently discards. Not
   worth a watchdog timer; document the risk and add a one-frame
   reaper in `ParsekFlight.Update` if observed in playtest.

## CHANGELOG / todo updates

Per `.claude/CLAUDE.md` "Documentation Updates - Per Commit, Not Per PR":

- `CHANGELOG.md`: "Merge confirmation dialog now appears before leaving
  flight, not after the destination scene loads. Quit to Main Menu also
  shows the dialog (previously force-auto-merged). Re-Fly sessions show
  Re-Fly-attempt-scoped button labels on the same dialog."
- `docs/dev/todo-and-known-bugs.md`: add the dialog-timing item if
  missing; mark closed once shipped.
