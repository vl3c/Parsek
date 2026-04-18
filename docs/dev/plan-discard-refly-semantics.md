# Plan: Discard Re-fly semantic rewrite (preserve tree's committed supersede state)

Branch: `fix/discard-refly-semantics`
Worktree: `Parsek-discard-refly`
Date: 2026-04-18
Based on: tip of `feat/rewind-staging` (includes the VAB/SPH interceptor
extension that just merged).

## Problem

The shipped v0.9 "Full Revert (Discard Re-fly)" option in the
Revert-during-re-fly dialog calls `TreeDiscardPurge.PurgeTree(marker.TreeId)`.
That entry point is the tree-scoped nuclear option (design §6.17): it
removes every Rewind Point for the tree, every `RecordingSupersedeRelation`
with either endpoint in the tree, every `LedgerTombstone` whose target
`GameAction.RecordingId` is in the tree, plus the active marker and journal.
It was wired into the revert dialog on the assumption that "Full Revert"
ought to undo everything the current re-fly attempt sat on top of.

The user rejected this in review: players who already chose to merge an
earlier re-fly inside the same tree now risk losing that committed supersede
state simply by using the flight-scene Revert button on a later re-fly
attempt that went badly. Their stated intent:

> "prefer just to discard the current flight and leave all the other
> committed stuff intact; it only removes that specific flight attempt and
> goes back to the state and time the Rewind button indicated (but just
> like a regular Rewind — in KSC mode)."
>
> "in the VAB context we should do what the player asks — take him to the
> VAB at the correct UT and discard the re-fly attempt."

The button must therefore be narrowed to session-scoped cleanup:
throw away the CURRENT re-fly attempt's artifacts only, reload the RP
quicksave so the timeline reads at the split UT, then transition to the
scene the player originally clicked (KSC for Launch context, VAB/SPH for
Prelaunch context). Every committed supersede relation, tombstone, and
sibling Rewind Point in the tree stays intact. The Unfinished Flights
entry for the origin split stays visible — the split really is still
unfinished, the player just declined to finish it on this attempt.

## Scope / Non-scope

### In scope

- Rename dialog button "Full Revert (Discard Re-fly)" -> "Discard Re-fly"
  and revise body copy in both Launch and Prelaunch variants.
- Rewrite `RevertInterceptor.FullRevertHandler` to
  `RevertInterceptor.DiscardReFlyHandler` with session-scoped semantics
  (no `TreeDiscardPurge.PurgeTree` call).
- Load the RP quicksave through the same save-root temp-copy + `LoadGame`
  path `RewindInvoker` already uses, then drive `HighLogic.LoadScene` to
  the target scene (SPACECENTER for Launch, EDITOR for Prelaunch — with
  the clicked `EditorFacility` plumbed through whatever API stock uses).
- Update `RecordingStore` if it does not already expose a
  "remove-by-recording-id" helper suitable for session cleanup. (It does:
  `RemoveCommittedInternal(Recording)` at `RecordingStore.cs:514`. No new
  API surface required. See Approach step 2.)
- Log line updates: `[ReFlySession] End reason=discardReFly ...`.
- Unit test rewrite: rename `FullRevert_*` tests to `DiscardReFly_*`, add
  scene-transition and tree-preservation assertions.
- In-game test updates (`ReFlyRevertDialogTest.cs`,
  `ReFlyRevertDialogPrelaunchTest.cs`) to reflect new destinations.
- Documentation: `docs/parsek-rewind-to-staging-design.md` §2.3, §6.14,
  §6.17, §A.4; `docs/user-guide.md`; `CHANGELOG.md`.

### Not in scope

- `RecordingStore.DiscardPendingTree` keeps calling `TreeDiscardPurge.PurgeTree`
  as-is (`RecordingStore.cs:1117`). The merge-dialog "Discard" button is
  the player's explicit "throw away the tree" action and its semantics
  are correct.
- Any other caller of `TreeDiscardPurge.PurgeTree` stays unchanged. The
  only production caller removed by this plan is the revert interceptor.
- `RetryHandler` semantics are unchanged — Retry still re-invokes
  `RewindInvoker.StartInvoke` with a fresh session id on the same RP/slot,
  and still lands the player in FLIGHT at the split moment regardless of
  which Revert button they clicked.
- `CancelHandler` is unchanged.
- The Harmony prefixes on `FlightDriver.RevertToLaunch` and
  `FlightDriver.RevertToPrelaunch` stay in place; they still block the
  stock revert when a session is active and hand off to the dialog.
- No changes to the marker, journal, RP, supersede, tombstone, or
  sweep data shapes.

## Approach

### New handler: `DiscardReFlyHandler(marker, target, facility)`

Replaces `FullRevertHandler` in `RevertInterceptor.cs:253-329`. Name
change matches the button rename and avoids the ambiguity of "Full
Revert" (which now ships in v0.9 with the stock-tree-purge semantics we
are moving away from).

Step-by-step. Each step logs at Info or Verbose and is recoverable —
a failure in step N must not strand half-cleared state for step N+1.

1. **Capture & validate inputs.** Pull `sessionId`, `rpId`,
   `activeReFlyRecordingId`, `originChildRecordingId`, `treeId` off
   `marker` into locals. Null-check: if `marker == null` log Warn + return
   (mirrors the existing `FullRevertHandler` early-out at
   `RevertInterceptor.cs:258-262`). If `marker.RewindPointId` is empty
   we cannot reload the RP; log Error, clear the marker to unstick the
   session, and skip straight to scene transition. If the RP id is
   present but `FindRewindPointById(rpId)` returns null, same treatment
   (the marker validator would have caught this on a fresh load, but
   the handler is invoked from live flight — defense in depth).

2. **Remove the provisional from `RecordingStore`.** Resolve the provisional
   `Recording` by id (`committedRecordings[i].RecordingId == activeReFlyRecordingId`
   is straightforward — no existing public lookup; a small internal helper
   `RecordingStore.FindCommittedById(string)` is the cleanest place to
   centralise this, but if we want zero surface-area change we can iterate
   inline in the handler). The provisional is `MergeState.NotCommitted` by
   invariant (marker validation guarantees this on load and no production
   path upgrades it); the handler itself can treat any state as "remove
   anyway" since we are discarding the session. Call
   `RecordingStore.RemoveCommittedInternal(rec)` (`RecordingStore.cs:514`) —
   it already bumps `StateVersion`, which in turn invalidates the ERS
   cache. Log Info `[RecordingStore] Removed provisional rec=<id> sess=<sid>`.
   If the provisional is not found, log Warn but continue — session cleanup
   is desired even on a partially-corrupted marker.

   *Implementation detail:* `RemoveCommittedInternal` does NOT delete
   sidecar files. Provisional recordings never have sidecar files on disk
   until merge, so no file cleanup is needed for this path. Verify by
   reading `RecordingStore.AddProvisional` (`RecordingStore.cs:495`) which
   adds in-memory only.

3. **Clear scenario session state.** Set `scenario.ActiveReFlySessionMarker = null`
   and `scenario.ActiveMergeJournal = null`. The journal field is
   invariantly null during an active re-fly whose merge has not started
   (design §5.10), but `RewindInvokeContext.Pending` can spawn a journal
   mid-merge. If the player hit Revert during the merge window (extremely
   narrow, but possible around checkpoint rollback windows), clearing the
   journal with no further action is safe: the merge has not committed
   any durable changes at any pre-Durable-1 phase, and any post-Durable-1
   phase would have already flipped the provisional to `CommittedProvisional`
   or `Immutable` — at which point the "remove provisional" step above
   becomes a tree-mutating action. **Decision:** if
   `scenario.ActiveMergeJournal != null`, refuse the Discard (show a
   "merge in progress — retry in a moment" popup or just suppress the
   button for that window). Cleaner: gate the dialog's Discard button at
   `ReFlyRevertDialog.Show` time on `scenario.ActiveMergeJournal == null`.
   Flag as an open question for review — see §9.

4. **Bump `SupersedeStateVersion`.** Call `scenario.BumpSupersedeStateVersion()`
   (`ParsekScenario.cs:55`). Step 2's `BumpStateVersion` on `RecordingStore`
   already triggers ERS rebuild via `RecordingStore.StateVersion`, but
   `EffectiveState` also caches on `SupersedeStateVersion` — belt-and-braces.

5. **Delete the temp quicksave file** at
   `saves/<save>/Parsek_Rewind_<sessionId>.sfs` if it still exists.
   `RewindInvoker` normally deletes it in `ConsumePostLoad` after a
   successful load (search `TryDeleteTemp` at `RewindInvoker.cs:451`),
   so by the time we reach flight during a live session the file is gone.
   The deletion is defensive: compute the path
   (`Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, $"Parsek_Rewind_{sessionId}.sfs")`),
   `File.Exists` + `File.Delete` wrapped in try/catch, Verbose log.

6. **Stage the RP quicksave for re-load.** Call the existing
   `RewindInvoker.CopyQuicksaveToSaveRoot(rp, sessionId, out tempPath, out tempLoadName)`
   (`RewindInvoker.cs:593`) — same source, same destination pattern.
   Reuse is important: the file naming, subdirectory handling, and
   error logs are already correct for this use. We generate a new
   `discardSessionId = "discard_" + Guid.NewGuid().ToString("N")` (not
   the old session id — the old one is gone; this is a fresh transient
   copy for the LoadGame call) so a concurrent failed-Retry doesn't
   collide with our in-flight temp file.

7. **Load the game.** `GamePersistence.LoadGame(tempLoadName, HighLogic.SaveFolder, true, false)`
   (same call as `RewindInvoker.cs:294`). Null-check the result; on
   failure log Error + show a user popup + delete the temp file +
   return (the marker is already cleared; we are just stuck in FLIGHT
   with no attempt to redirect scene). On success, assign
   `HighLogic.CurrentGame = game` (parallels `RecordingStore.cs:3218`).

8. **Transition to target scene.**
   - `RevertTarget.Launch` -> `HighLogic.LoadScene(GameScenes.SPACECENTER)`
     (parallels `RecordingStore.cs:3219`).
   - `RevertTarget.Prelaunch` -> editor scene selector. The stock
     `FlightDriver.RevertToPrelaunch(EditorFacility facility)` internally
     sets `EditorDriver.StartupBehaviour` or an equivalent KSP-side
     selector before `LoadScene(GameScenes.EDITOR)`. Confirm the exact
     KSP API call sequence during implementation (ilspycmd on
     `Assembly-CSharp.dll`, search `RevertToPrelaunch` decompiled body).
     Best-guess is: `EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.LOAD_FROM_CACHE`
     with the facility set on `EditorDriver.editorFacility`; see §9
     open question — if the plumbing turns out to be trickier, we
     may need to invoke `EditorDriver.StartAndLoadVessel` or walk the
     stock `KSCFacility` selector at SpaceCenter entry instead.

9. **Delete the temp file after scene load initiates.** `LoadGame` itself
   reads the file synchronously then the scene swap begins; the stock
   pattern `File.Delete(tempPath)` right after `LoadGame` returning is
   safe (see `RecordingStore.cs:3197` for the precedent). Put this
   between step 7 and step 8 to mirror the existing pattern.

10. **Log.** `ParsekLog.Info(SessionTag, $"End reason=discardReFly sess={sessionId} target={target}" + (target == Prelaunch ? $" facility={facility}" : ""))`.
    Emitted AFTER the scene dispatch call so an exception during the
    dispatch still logs "Reason dispatched then blew up" on the stack above.

### Test seams

Mirror the existing `StockRevertInvokerForTesting` /
`StockRevertToPrelaunchInvokerForTesting` pattern with two new fields on
`RevertInterceptor`:

- `internal static Action<RewindPoint, string /*tempLoadName*/> DiscardReFlyLoadGameForTesting;`
  — receives the RP + temp load name; when non-null, suppresses the real
  `GamePersistence.LoadGame` call so unit tests can observe the handler's
  intent without Unity statics.
- `internal static Action<GameScenes, EditorFacility> DiscardReFlyLoadSceneForTesting;`
  — receives the target scene + (meaningful only for Prelaunch) the
  facility; suppresses `HighLogic.LoadScene`.
- Retain `TreeDiscardPurgeInvokerForTesting` — null by default; if any
  test wires it up we assert it is NEVER invoked by the Discard path
  (see test plan `DiscardReFly_DoesNotCallTreeDiscardPurge`).

Add a `ResetTestOverrides()` line for each new seam in the existing
`RevertInterceptor.ResetTestOverrides` (`RevertInterceptor.cs:84`).

### Dialog changes

`ReFlyRevertDialog.cs`:

- Line `:147`: button label "Full Revert (Discard Re-fly)" ->
  "Discard Re-fly".
- `BuildBody(RevertTarget)` (`:204-233`): rewrite the middle bullet for
  both variants. Launch variant:

  > "- Discard Re-fly: throw away this re-fly attempt and return to
  > the Space Center at the moment you opened the Rewind Point. The
  > tree's other Rewind Points, supersede relations, and tombstones
  > stay intact. Unfinished Flights still shows this split so you can
  > try again."

  Prelaunch variant:

  > "- Discard Re-fly: throw away this re-fly attempt and return to
  > the VAB or SPH at the moment you opened the Rewind Point. The
  > tree's other Rewind Points, supersede relations, and tombstones
  > stay intact. Unfinished Flights still shows this split so you can
  > try again."

- `onFullRevert` callback parameter name in `Show(...)` ->
  `onDiscardReFly` for readability. Overload shim for external callers
  (not expected — it is `internal static`) can be avoided.
- Update XML doc `<summary>` bullets (`:11-16`) to describe the new
  semantics.

### Interceptor wiring

`RevertInterceptor.Prefix` (`:107-142`):

- Update the log message at `:117-118` — no wording changes needed
  (marker already names "showing re-fly dialog").
- The `ReFlyRevertDialog.Show` call at `:133-138` now receives
  `onDiscardReFly: () => DiscardReFlyHandler(capturedMarker, capturedTarget, capturedFacility)`
  instead of `onFullRevert: () => FullRevertHandler(...)`.

## Files to modify

- `Source/Parsek/RevertInterceptor.cs`
  - Remove/rewrite `FullRevertHandler` (`:253-329`) as `DiscardReFlyHandler`.
  - Update XML doc bullets (`:42-45`) and callback-wiring lambda (`:137`).
  - Update/rename test seams (`:74-81`) + `ResetTestOverrides` (`:84-90`).
- `Source/Parsek/ReFlyRevertDialog.cs`
  - Button label (`:147`).
  - `BuildBody` both variants (`:204-233`).
  - XML doc + param renames.
- `Source/Parsek.Tests/ReFlyRevertDialogTests.cs`
  - Rename `FullRevert_*` tests + extend with the new assertions below.
  - Existing `FullRevertCallback_*` tests at `:182, :231, :250, :259` all
    need rewrites.
  - Prelaunch-context tests at `:480-534`: rewrite to assert scene-dispatch
    instead of `RevertToPrelaunch` re-dispatch.
- `Source/Parsek/InGameTests/ReFlyRevertDialogTest.cs`
  - Update assertion set to reflect new destination (SPACECENTER).
- `Source/Parsek/InGameTests/ReFlyRevertDialogPrelaunchTest.cs`
  - Update assertion set to reflect EDITOR destination with facility.
- `docs/parsek-rewind-to-staging-design.md`
  - §2.3 (line 73): sentence about supersede semantics — no change
    needed in the body, but double-check no paragraph claims Full Revert
    purges the tree.
  - §6.14 (line 838-852): rewrite the second bullet (`Full Revert`) to
    `Discard Re-fly`, describe the RP reload + scene dispatch, drop the
    `TreeDiscardPurge.PurgeTree` mention.
  - §6.17 (line 876-891): remove "Triggered by Full Revert during a
    re-fly session" from the trigger list; callers are now only
    merge-discard and other explicit tree-discard paths.
  - §A.4 (line 1418-1426): rewrite step 4 ("Full Revert") as
    "Discard Re-fly" with the new semantics — RP reload, scene swap
    to KSC or VAB/SPH, everything else in the tree preserved, the
    origin split still in Unfinished Flights.
- `docs/user-guide.md` (line 72): rewrite the "Revert during re-fly"
  bullet.
- `CHANGELOG.md` v0.9.0 block (line 11-12 area): add the new Discard
  Re-fly line as specified in "Documentation updates" below.
- `docs/dev/todo-and-known-bugs.md`: no entry unless we leave the
  FLIGHT-flash UX on the polish list (see §9).

## Files to create

None. Every change touches an existing file. If the handler grows past
~150 lines and starts to dwarf the rest of `RevertInterceptor`, we can
consider pulling it into its own `DiscardReFlyHandler.cs`, but the
current shape (three handlers + helpers) does not justify it yet.

## Test plan

### Unit tests (`Source/Parsek.Tests/ReFlyRevertDialogTests.cs`)

Rename + rewrite:

- `FullRevertCallback_InvokesTreeDiscardPurge_WithCorrectTreeId` ->
  `DiscardReFly_DoesNotCallTreeDiscardPurge` (assert via a test seam or
  via pre-population + post-assertion that supersede/tombstone/RP lists
  for the tree are unchanged by the handler).
- `FullRevertCallback_EmptyTreeId_ClearsMarker_StillTriggersStockRevert` ->
  `DiscardReFly_EmptyTreeId_StillClearsSessionArtifacts_StillDispatchesScene`
  (empty tree id is no longer the decision point for "call PurgeTree or
  don't"; the handler always clears the marker + provisional + journal).
- `FullRevertHandler_PrelaunchContext_InvokesStockRevertToPrelaunch` ->
  `DiscardReFly_PrelaunchContext_InvokesLoadSceneEditor_WithFacility`
  (assert via `DiscardReFlyLoadSceneForTesting` that the handler
  requested `GameScenes.EDITOR` + the passed `EditorFacility`).
- `FullRevertHandler_LaunchContext_InvokesStockRevertToLaunch` ->
  `DiscardReFly_LaunchContext_InvokesLoadSceneSpaceCenter`.
- `FullRevertHandler_PrelaunchContext_LogsTargetInEndLine` ->
  `DiscardReFly_PrelaunchContext_LogsTargetAndFacilityInEndLine`
  (new log format `End reason=discardReFly`).

Add new:

- `DiscardReFly_LaunchContext_ClearsSessionArtifacts_LoadsRpQuicksave_TransitionsToKSC`
  — full flow assertion: marker cleared, journal null, provisional gone
  from `CommittedRecordings`, the `DiscardReFlyLoadGameForTesting` seam
  received the expected temp name, the `DiscardReFlyLoadSceneForTesting`
  seam received `(SPACECENTER, VAB)` (facility ignored for Launch).
- `DiscardReFly_PrelaunchContext_VAB_TransitionsToVAB`
  — scene seam receives `(EDITOR, VAB)`.
- `DiscardReFly_PrelaunchContext_SPH_TransitionsToSPH`
  — scene seam receives `(EDITOR, SPH)`.
- `DiscardReFly_SupersedeRelationsForOtherSplitsInTree_Preserved`
  — pre-populate two `RecordingSupersedeRelation` rows tied to other
  split ids in the same tree; invoke handler; assert both rows still
  present and `SupersedeStateVersion` bumped exactly once.
- `DiscardReFly_TombstonesForOtherSplitsInTree_Preserved`
  — pre-populate `LedgerTombstone` rows for other splits in the tree;
  assert preservation.
- `DiscardReFly_UnfinishedFlightsEntryForThisSplit_StaysVisible`
  — after handler, `EffectiveState.IsUnfinishedFlight(originRecording)`
  still returns true (origin is CommittedProvisional and its RP + slot
  still resolve in the scenario).
- `DiscardReFly_RemovesProvisionalFromCommittedRecordings`
  — focused assertion on the `RecordingStore.CommittedRecordings`
  delta: by-id lookup on `marker.ActiveReFlyRecordingId` returns null
  post-handler.
- `DiscardReFly_BumpsSupersedeStateVersion_InvalidatesERSCache`
  — capture `scenario.SupersedeStateVersion` + `RecordingStore.StateVersion`
  pre-call; assert both advanced post-call; assert
  `EffectiveState.ResetCachesForTesting` was triggered (or that the next
  `ComputeERS` rebuilds).
- `DiscardReFly_NullMarker_LogsWarn_NoOp` — null-marker guard.
- `DiscardReFly_UnresolvableRpId_LogsError_ClearsMarker_DoesNotDispatchScene`
  — if the RP id on the marker cannot resolve, the handler clears the
  session artifacts but does NOT try to `LoadGame`/`LoadScene` (the
  player is stuck in FLIGHT; they can click Revert again or Esc).

### Unit tests — session lifecycle

- `DiscardReFly_WhileJournalActive_GatedOrRefused` — pre-populate
  `ActiveMergeJournal`; assert the handler either refuses to run or
  (per §9 open question) crashes out safely. The Show dialog-gate
  approach would be tested on the dialog side; if the gate lives on
  the handler, test it here.

### Log-assertion tests

Extend `RewindLoggingTests.cs` (or the nearest log-contract spot) with
a case that the exact format `[ReFlySession] Info: End reason=discardReFly sess=<id> target=<Launch|Prelaunch> facility=<VAB|SPH>`
appears for each of the three target/facility combinations. Pattern:
install `ParsekLog.TestSinkForTesting` at handler call time, assert the
end line is emitted exactly once, assert `End reason=fullRevert` is NOT
emitted (catches any stale code path).

### In-game tests

- `ReFlyRevertDialogTest.cs` (Launch context): rewrite the post-click
  assertion. Before: "stock revert to launch ran and took us to the
  launchpad"; after: "we are at the Space Center, the marker is cleared,
  the provisional recording is not in `CommittedRecordings`, the
  tree's supersede relations and other RPs are still in
  `RewindPoints`". Scene is SPACECENTER.
- `ReFlyRevertDialogPrelaunchTest.cs`: rewrite for VAB + SPH. Assert
  final scene is EDITOR with the correct facility.
- Add a new in-game test `DiscardReFlyPreservesMergedSupersedeTest` —
  set up a synthetic two-split tree where the first split was previously
  merged (`RecordingSupersedeRelation` committed), start a re-fly of the
  second split, Discard; assert the first split's supersede relation is
  still committed and the first split's recording is still
  `Immutable`/`CommittedProvisional`.

## Documentation updates

- `docs/parsek-rewind-to-staging-design.md` §6.14 second bullet rewrite:

  > "**Discard Re-fly** -- `DiscardReFlyHandler`: remove the provisional
  > re-fly recording from `CommittedRecordings`, clear
  > `ActiveReFlySessionMarker` and `ActiveMergeJournal`, bump
  > `SupersedeStateVersion`, delete the session temp quicksave if still
  > present, then stage the RP quicksave through the save-root copy path
  > and `GamePersistence.LoadGame`. On success, `HighLogic.LoadScene`
  > dispatches to `GameScenes.SPACECENTER` for a Launch context or
  > `GameScenes.EDITOR` (pre-selecting the clicked `EditorFacility`)
  > for a Prelaunch context. `TreeDiscardPurge.PurgeTree` is NOT called:
  > the tree's other Rewind Points, supersede relations, and
  > tombstones are preserved. The origin split's
  > `CommittedProvisional` recording plus its RP still resolve, so
  > `IsUnfinishedFlight` stays true and the Unfinished Flights row
  > stays visible. Log
  > `[ReFlySession] Info: End reason=discardReFly sess=<sid> target=<Launch|Prelaunch>`
  > (Prelaunch line also includes `facility=<VAB|SPH>`)."

- §6.17 opening paragraph: strike "Triggered by Full Revert during a
  re-fly session"; reword to "Triggered by merge-discard
  (`RecordingStore.DiscardPendingTree`) or by any other path that
  discards a whole tree."

- §2.3 sweep-read pass for any sentence that implicitly endorses
  "Full Revert wipes the tree" — the philosophy sentence can stand;
  we are strengthening the invariant by removing the one caller that
  purged the tree on a still-live re-fly path.

- §A.4 step 4 rewrite: Discard Re-fly reloads the RP quicksave, drops
  into the Space Center (Launch variant) or VAB/SPH (Prelaunch variant),
  the Unfinished Flights entry is still present, career state is
  unchanged, the tree's committed supersede relations / tombstones /
  RPs are preserved. Add a line at the end of §A.4 noting that "other
  re-fly attempts already merged into the tree are unaffected" so the
  user story covers the motivating concern directly.

- `docs/user-guide.md` bullet rewrite:

  > "- **Revert during re-fly** -- pressing stock Revert-to-Launch or
  > Revert-to-VAB/SPH while a session is active shows the same
  > three-option dialog: Retry from Rewind Point (re-loads the split
  > moment in FLIGHT either way), Discard Re-fly (throws away the
  > current attempt and returns you to the scene you clicked at the
  > split UT; the tree's other re-fly state is preserved and the
  > Unfinished Flights entry remains), or Continue Flying."

- `CHANGELOG.md` v0.9.0 block: add a line near the "Revert to VAB/SPH"
  line (line 12):

  > "- Discard Re-fly (was `Full Revert`) during an active re-fly
  > session now preserves the tree's supersede relations, tombstones,
  > and other Rewind Points; only the current re-fly session's
  > artifacts and provisional recording are cleared. Launch context
  > returns to KSC; VAB/SPH context returns to the clicked editor
  > at the RP's UT."

  Update the existing "Revert to VAB/SPH during an active re-fly"
  line to say "Discard Re-fly" instead of "Full Revert" to keep
  terminology consistent within the same changelog block.

## Commit strategy

Three commits on `fix/discard-refly-semantics`, each independently
reviewable:

1. **This plan** (current commit). Just the new plan file.
2. **Code + unit tests.** `RevertInterceptor.cs` rewrite + dialog copy
   + `ReFlyRevertDialogTests.cs` rename/extension + log-assertion test.
   Also the `CHANGELOG.md` line (per repo convention: docs staged with
   code). Post-commit: build, verify deployed DLL contains the new
   UTF-16 string `"End reason=discardReFly"`, run full `dotnet test`.
3. **In-game tests + design / user-guide docs.**
   `ReFlyRevertDialogTest.cs`, `ReFlyRevertDialogPrelaunchTest.cs`,
   `DiscardReFlyPreservesMergedSupersedeTest.cs`, and the doc edits
   for §6.14 / §6.17 / §A.4 / user-guide. Post-commit: build + in-game
   test run in KSP via Ctrl+Shift+T.

Each commit message follows the repo's imperative style; no
`Co-Authored-By` trailer.

## Risks / open questions

1. **FLIGHT flash during scene transition.** The RP quicksave is a
   FLIGHT-scene save. `GamePersistence.LoadGame` instantiates the flight
   state, then `HighLogic.LoadScene(SPACECENTER|EDITOR)` swaps away. The
   one-frame interstitial may briefly render the flight scene (or a
   loading overlay depending on KSP's internals). User explicitly
   accepted this trade-off: "(i) player can land in flight but just
   watching the ghost I guess". Track as a polish TODO in
   `docs/dev/todo-and-known-bugs.md` AFTER first in-game observation
   — if it reads as instantaneous we skip the todo entry entirely.

2. **Editor scene entry API.** `HighLogic.LoadScene(GameScenes.EDITOR)`
   without pre-setting the facility may drop the player into a default
   editor (usually VAB). Stock `FlightDriver.RevertToPrelaunch(EditorFacility)`
   handles this internally. Implementation must decompile
   `FlightDriver.RevertToPrelaunch` via `ilspycmd` and replicate the
   facility-selector calls:
   - Likely candidates: `EditorDriver.StartupBehaviour`, `EditorDriver.editorFacility`,
     `EditorFacility` static fields on `EditorDriver`.
   - Fallback if the private API is unstable: call
     `FlightDriver.RevertToPrelaunch(facility)` directly after clearing
     the marker (the interceptor's own prefix now returns true). This
     matches the current v0.9 Full Revert dispatch exactly — we would
     just be moving the session-cleanup BEFORE the stock call instead
     of after `TreeDiscardPurge.PurgeTree`. **If decompilation of the
     stock path reveals that calling it directly still loads the RP
     quicksave correctly (via `HighLogic.CurrentGame` being set first),
     we can elide our own `LoadGame`/`LoadScene` entirely and let the
     stock revert drive both.** Confirm during implementation.

3. **Is the RP reload necessary at all?** Alternative formulation:
   "just clear the session artifacts and let the player keep flying
   at the current UT" — no scene change, no load. The user's stated
   intent ("goes back to the state and time the Rewind button
   indicated") rules this out; the explicit "like a regular Rewind
   but to KSC mode" phrasing is unambiguous that the timeline should
   wind back to the split UT. Preserving the current interpretation.

4. **Interaction with the post-load sweep.** After the scene
   transition, the new scenario's `OnLoad` runs through:
   `MergeJournalOrchestrator.RunFinisher` -> `RewindInvoker.ConsumePostLoad`
   -> `LoadTimeSweep.Run` -> `RewindPointReaper.ReapOrphanedRPs`. In
   the Discard Re-fly path:
   - `ActiveMergeJournal` is null -> `RunFinisher` no-op.
   - `RewindInvokeContext.Pending` is false (we did not set it) ->
     `ConsumePostLoad` no-op.
   - `LoadTimeSweep.Run`: marker is null -> marker-validation branch
     no-op. Any `NotCommitted` provisional or session-provisional RP
     in the RP quicksave's snapshot gets treated as a zombie and
     cleaned up.
   - `RewindPointReaper`: reaps RPs tied to committed/immutable
     splits whose supersede chains are closed; preserves live
     Unfinished Flights RPs.

   Verify this chain explicitly in the new in-game test
   `DiscardReFlyPreservesMergedSupersedeTest`: after the scene
   transition, the origin split's RP is still present in
   `RewindPoints`, the first split's supersede relation is still
   present in `RecordingSupersedes`, and the Unfinished Flights UI
   surfaces the origin split.

5. **Merge-in-progress collision (see Approach step 3).** The
   `ActiveMergeJournal != null` edge case needs a design call. Options:
   (a) gate the Discard button at dialog `Show` time when the journal
   is non-null; (b) handler refuses with a user popup; (c) handler
   proceeds and accepts the merge rollback. The journal has its own
   crash-recovery invariants; option (c) may leave the journal in a
   pre-Durable-1 state that the next load's `RunFinisher` rolls back,
   which is arguably correct. **Default choice: option (a) — dialog
   hides the Discard button when a journal is active** (cleaner UX,
   no user surprise). Review if this is overreach for an edge case
   the user is unlikely to hit.

6. **Temp file cleanup on failed `LoadGame`.** Step 7 error path must
   delete the staged temp file. Existing pattern at
   `RewindInvoker.cs:451` (`TryDeleteTemp`) is a good template;
   extract it to a shared helper if the copy-paste bothers review.
