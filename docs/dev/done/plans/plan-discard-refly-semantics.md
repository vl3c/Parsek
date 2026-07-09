# Plan: Discard Re-fly semantic rewrite (preserve tree's committed supersede state)

Branch: `fix/discard-refly-semantics`
Worktree: `Parsek-discard-refly`
Date: 2026-04-18
Based on: tip of `feat/rewind-staging` (includes the VAB/SPH interceptor
extension that just merged).

## Problem

The shipped v0.9 "Full Revert (Discard Re-fly)" option calls
`TreeDiscardPurge.PurgeTree(marker.TreeId)` — the tree-scoped nuclear
option (design §6.17) that removes every Rewind Point, every
`RecordingSupersedeRelation` with either endpoint in the tree, every
`LedgerTombstone` whose target is in the tree, plus the marker + journal.
Players who already merged an earlier re-fly in the same tree lose
that committed supersede state if a later re-fly attempt goes badly
and they click Revert. User's stated intent:

> "prefer just to discard the current flight and leave all the other
> committed stuff intact; it only removes that specific flight attempt
> and goes back to the state and time the Rewind button indicated
> (but just like a regular Rewind — in KSC mode)." "in the VAB context
> take him to the VAB at the correct UT and discard the re-fly attempt."

Narrow the button to session-scoped cleanup: throw away the CURRENT
re-fly attempt's artifacts only, reload the RP quicksave so the
timeline reads at the split UT, transition to the scene the player
originally clicked (KSC for Launch, VAB/SPH for Prelaunch). Every
committed supersede relation, tombstone, and sibling RP stays intact.
The Unfinished Flights entry for the origin split stays visible.

## Scope / Non-scope

### In scope

- Rename dialog button "Full Revert (Discard Re-fly)" -> "Discard Re-fly"
  and revise body copy in both Launch and Prelaunch variants.
- Rewrite `RevertInterceptor.FullRevertHandler` ->
  `RevertInterceptor.DiscardReFlyHandler` with session-scoped semantics
  (no `TreeDiscardPurge.PurgeTree` call).
- Load the RP quicksave through the same save-root temp-copy +
  `LoadGame` path `RewindInvoker` already uses, then
  `HighLogic.LoadScene` to SPACECENTER (Launch) or EDITOR
  (Prelaunch, with `EditorDriver.editorFacility` + `START_CLEAN`).
- Reuse `RecordingStore.RemoveCommittedInternal(Recording)` at
  `RecordingStore.cs:514` (no new API surface required).
- Log line: `[ReFlySession] End reason=discardReFly ...`.
- Unit test rewrite (see §Test plan) + in-game test updates.
- Docs: `parsek-rewind-to-separation-design.md` §2.3/§6.14/§6.17/§A.4;
  `user-guide.md`; `CHANGELOG.md`.

### Not in scope

- `RecordingStore.DiscardPendingTree` keeps calling
  `TreeDiscardPurge.PurgeTree` as-is (`RecordingStore.cs:1117`); the
  merge-dialog "Discard" button's tree-wide semantics are correct.
- All other callers of `TreeDiscardPurge.PurgeTree` unchanged — the
  revert interceptor is the only one this plan removes.
- `RetryHandler` / `CancelHandler` unchanged.
- Harmony prefixes on `FlightDriver.RevertToLaunch` /
  `RevertToPrelaunch` stay in place (block stock revert when session
  active, hand off to dialog).
- No changes to marker / journal / RP / supersede / tombstone / sweep
  data shapes.

## Approach

### New handler: `DiscardReFlyHandler(marker, target, facility)`

Replaces `FullRevertHandler` in `RevertInterceptor.cs:253-329`. Each
step logs at Info or Verbose and is recoverable — a failure in step
N must not strand half-cleared state for step N+1.

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

   Defensive RP-quicksave file check: before step 6 does the copy-to-
   save-root, the handler calls `File.Exists(ResolveAbsoluteQuicksavePath(rp))`.
   If the file is gone on disk (user deleted the `Parsek/RewindPoints/`
   directory, storage failure, etc.) log Error, show a
   `ScreenMessages.PostScreenMessage("Discard Re-fly failed: rewind
   point quicksave missing")` toast, clear the session artifacts
   (steps 2-4 still run) but do NOT call `LoadGame`/`LoadScene`. The
   player is left in flight with no session; they can click Revert
   again and take Continue Flying or Retry. `CopyQuicksaveToSaveRoot`
   also returns a null `tempLoadName` on missing source (see
   `RewindInvoker.cs:600-607`), so step 6 double-guards.

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

3. **Preserve the origin RP across the load-time sweep.** This is the
   critical invariant for v0.9's "Unfinished Flights row stays visible
   after Discard Re-fly" story. The RP backing the origin split is
   `SessionProvisional=true` (see `RewindPointAuthor.cs:123` —
   invariant: RPs are always created session-provisional; `TagRpsForReap`
   in `MergeJournalOrchestrator.cs:441-465` is the ONLY production path
   that flips the flag to `false`, and it fires from the merge-commit
   tail we are explicitly skipping). After the handler clears the
   marker and reloads the RP quicksave, the new scenario's
   `ParsekScenario.OnLoad` runs `LoadTimeSweep.Run`. With marker null
   the spare set is empty (`LoadTimeSweep.cs:92-104`), and the sweep's
   RP-discard pass (`LoadTimeSweep.cs:132-144`) adds every
   `SessionProvisional=true` RP to `discardRps`, which
   `RemoveDiscardRps` (line 192) then removes from
   `scenario.RewindPoints`. The origin RP would disappear — which
   breaks "Unfinished Flights still shows this split" directly, because
   `EffectiveState.IsUnfinishedFlight` (`EffectiveState.cs:192-200`)
   requires a matching RP to be present at the origin's parent
   BranchPoint.

   **Chosen mechanism — Option (A) synchronous promotion before marker
   clear.** Look up the RP by `marker.RewindPointId` via
   `scenario.FindRewindPointById(rpId)` (or an inline walk of
   `scenario.RewindPoints`; use inline to keep the surface minimal,
   matching the provisional-lookup decision in step 2). If non-null
   and `rp.SessionProvisional == true`, set `rp.SessionProvisional = false`
   and `rp.CreatingSessionId = null`. Log Info
   `[ReFlySession] Origin RP promoted to persistent rp=<id> sess=<sid> reason=discardReFly`.
   Rationale: the player has exercised their right to keep the split
   open (same end state as a successful merge, minus the tree-mutating
   supersede/tombstone writes). Promoting is one line, zero new plumbing,
   and symmetrical with `TagRpsForReap`'s behavior. Options (B)
   "preserve-for-next-load flag on the scenario that LoadTimeSweep
   honors" and (C) "add a CreatingSessionId-based exemption in the
   sweep" were considered; both require schema or sweep-logic changes
   across multiple files and crash-recovery invariants, for no
   additional semantic benefit.

4. **Clear scenario session state.** Set
   `scenario.ActiveReFlySessionMarker = null` and
   `scenario.ActiveMergeJournal = null`. See Risks §5 for the
   merge-in-progress edge case — journal-non-null is gated at the
   dialog AND refused at the handler (defense in depth).

5. **Bump `SupersedeStateVersion`.** Call `scenario.BumpSupersedeStateVersion()`
   (`ParsekScenario.cs:55`). Step 2's `RemoveCommittedInternal` bumps
   `RecordingStore.StateVersion` via `BumpStateVersion()`
   (`RecordingStore.cs:514-520` — confirmed), which the ERS cache
   observes. The explicit `BumpSupersedeStateVersion` here is belt-and-
   braces: supersede-state-version cache-keys also need to roll so
   `EffectiveState.ComputeERS`'s supersede-aware path rebuilds. We do
   NOT bump `TombstoneStateVersion` because no tombstone rows are
   touched in a Discard Re-fly — the tree's existing tombstones remain
   valid for any already-merged sibling splits.

6. **Delete the prior session's temp quicksave file** at
   `saves/<save>/Parsek_Rewind_<sessionId>.sfs` if it still exists.
   `RewindInvoker` normally deletes it in `ConsumePostLoad`'s `finally`
   block at `RewindInvoker.cs:451-453` after a successful session-start
   load, so on the 99% path the file is already gone by the time we
   reach flight during a live session. This deletion is defensive-only:
   compute the path
   (`Path.Combine(KSPUtil.ApplicationRootPath, "saves", HighLogic.SaveFolder, $"Parsek_Rewind_{sessionId}.sfs")`),
   `File.Exists` + `File.Delete` wrapped in try/catch, Verbose log.

7. **Stage the RP quicksave for re-load.** Call the existing
   `RewindInvoker.CopyQuicksaveToSaveRoot(rp, discardSessionId, out tempPath, out tempLoadName)`
   (`RewindInvoker.cs:593`) — same source, same destination pattern.
   Reuse is important: the file naming, subdirectory handling, and
   error logs are already correct for this use. We generate a new
   `discardSessionId = "discard_" + Guid.NewGuid().ToString("N")` (not
   the old session id — the old one is gone; this is a fresh transient
   copy for the LoadGame call) so a concurrent failed-Retry doesn't
   collide with our in-flight temp file. `CopyQuicksaveToSaveRoot`
   already `File.Exists`-gates the source at
   `RewindInvoker.cs:600-607` and returns null `tempLoadName` on miss;
   the handler treats a null `tempLoadName` as the failure branch
   described in step 1.

8. **Load the game + transition to target scene.** `LoadGame + LoadScene
   is the ONLY correct path.** Do NOT fall back to stock
   `FlightDriver.RevertToLaunch` / `FlightDriver.RevertToPrelaunch`:
   decompiled (via `ilspycmd` on `Assembly-CSharp.dll`)
   `FlightDriver.RevertToLaunch` rebuilds from the flight's cached
   `PostInitState`, and `RevertToPrelaunch(EditorFacility)` calls
   `EditorDriver.StartEditor(facility)` wired to the cached
   `PreLaunchState` — neither restores the game to the RP's UT; both
   return to the current flight's launch moment.

   - Invoke `Game game = GamePersistence.LoadGame(tempLoadName, HighLogic.SaveFolder, true, false)`
     (same call as `RewindInvoker.cs:294`). Null-check the result.
     - **LoadGame failure path:** log Error, delete the temp file,
       call
       `ScreenMessages.PostScreenMessage("Discard Re-fly failed: could not load rewind point", 5f, ScreenMessageStyle.UPPER_CENTER)`
       so the player sees the failure (the marker is already cleared
       by step 4, so the player is stuck in FLIGHT with no session;
       they can take Continue Flying or Retry on the next Revert
       click). Return without calling `LoadScene`.
     - On success, assign `HighLogic.CurrentGame = game` (parallels
       `RecordingStore.cs:3218`).
   - **Launch variant**: `HighLogic.LoadScene(GameScenes.SPACECENTER)`
     (parallels `RecordingStore.cs:3219`).
   - **Prelaunch variant**: pre-select the target editor facility
     with
     `EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN`
     and `EditorDriver.editorFacility = facility`, then
     `HighLogic.LoadScene(GameScenes.EDITOR)`.
     `LOAD_FROM_CACHE` is wrong here: after `LoadGame` replaces
     `HighLogic.CurrentGame`, `EditorDriver`'s cached ShipConstruct
     is stale / irrelevant (it was cached for the previous game's
     editor session, if any; decompilation shows the field is a
     shallow `ShipConstruct` reference populated at the last editor
     exit). `START_CLEAN` opens a fresh editor against the just-
     loaded career state — which is what the player wants after a
     revert-to-RP-UT. The `EditorDriver.StartupBehaviour` enum lives
     at `EditorDriver.StartupBehaviours` (decompile-confirmed).

9. **Delete the temp file after scene load initiates.** `LoadGame` itself
   reads the file synchronously then the scene swap begins; the stock
   pattern `File.Delete(tempPath)` right after `LoadGame` returning is
   safe (see `RecordingStore.cs:3197` for the precedent). Put this
   between the `LoadGame` call and the `LoadScene` call to mirror the
   existing pattern.

10. **Log.** `ParsekLog.Info(SessionTag, $"End reason=discardReFly sess={sessionId} target={target}" + (target == Prelaunch ? $" facility={facility}" : ""))`.
    Emitted AFTER the scene dispatch call so an exception during the
    dispatch still logs "Reason dispatched then blew up" on the stack above.

### Test seams

**Delete the existing `StockRevertInvokerForTesting` and
`StockRevertToPrelaunchInvokerForTesting` fields entirely** along with
their `ResetTestOverrides` lines. The handler no longer invokes stock
revert under any branch (see the stock-revert-fallback discussion in
step 8 and in Risks §2 below — the LoadGame + LoadScene path is the
only correct mechanism), so the seams have no remaining callers. Any
unit test that referenced the stock seams gets rewritten against the
two new seams below.

Two new fields on `RevertInterceptor`:

- `internal static Action<RewindPoint, string /*tempLoadName*/> DiscardReFlyLoadGameForTesting;`
  — receives the RP + temp load name; when non-null, suppresses the real
  `GamePersistence.LoadGame` call so unit tests can observe the handler's
  intent without Unity statics.
- `internal static Action<GameScenes, EditorFacility> DiscardReFlyLoadSceneForTesting;`
  — receives the target scene + (meaningful only for Prelaunch) the
  facility; suppresses `HighLogic.LoadScene` and the
  `EditorDriver.StartupBehaviour` / `EditorDriver.editorFacility`
  pre-sets.
- Retain `TreeDiscardPurgeInvokerForTesting` — null by default; if any
  test wires it up we assert it is NEVER invoked by the Discard path
  (see test plan `DiscardReFly_DoesNotCallTreeDiscardPurge`).

Add a `ResetTestOverrides()` line for each new seam in the existing
`RevertInterceptor.ResetTestOverrides` (`RevertInterceptor.cs:84`) and
remove the lines for the deleted stock seams.

### Dialog changes

`ReFlyRevertDialog.cs`:

- Line `:147`: button label "Full Revert (Discard Re-fly)" ->
  "Discard Re-fly".
- `BuildBody(RevertTarget)` (`:204-233`): rewrite the middle bullet.
  Launch variant says "return to the Space Center"; Prelaunch variant
  says "return to the VAB or SPH". Both end: "at the moment you
  opened the Rewind Point. The tree's other Rewind Points, supersede
  relations, and tombstones stay intact. Unfinished Flights still
  shows this split so you can try again."

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

- `Source/Parsek/RevertInterceptor.cs` — rewrite `FullRevertHandler`
  (`:253-329`) as `DiscardReFlyHandler`; update XML doc (`:42-45`) +
  callback-wiring lambda (`:137`); **delete** the stock-revert test
  seams (`:74-81`) entirely and add the two new seams; update
  `ResetTestOverrides` (`:84-90`) accordingly.
- `Source/Parsek/ReFlyRevertDialog.cs` — button label (`:147`);
  `BuildBody` both variants (`:204-233`); `onFullRevert` param rename
  to `onDiscardReFly`; XML doc `<summary>` bullets (`:11-16`); dialog
  `Show` adds `ActiveMergeJournal == null` precondition that hides
  the Discard button when merge is in flight.
- `Source/Parsek.Tests/ReFlyRevertDialogTests.cs` — rename
  `FullRevert_*` tests, extend per test-plan section below; the
  existing stock-revert-expecting tests at `:182, :231, :250, :259,
  :480-534` get rewritten to the new scene-dispatch seam assertions.
- `Source/Parsek/InGameTests/ReFlyRevertDialogTest.cs` /
  `ReFlyRevertDialogPrelaunchTest.cs` — reflect new destinations
  (SPACECENTER / EDITOR+facility) + origin-RP preservation assertions.
- `docs/parsek-rewind-to-separation-design.md` §2.3, §6.14, §6.17, §A.4
  per the Documentation updates section below.
- `docs/user-guide.md` (line 72): rewrite the "Revert during re-fly"
  bullet.
- `CHANGELOG.md` v0.9.0 block (line 11-12): Discard Re-fly line per
  Documentation updates.

No new files. If the handler grows past ~150 lines it can be split
into `DiscardReFlyHandler.cs` later; the current three-handler shape
of `RevertInterceptor` does not justify the split yet.

## Test plan

### Unit tests (`Source/Parsek.Tests/ReFlyRevertDialogTests.cs`)

Rename + rewrite (existing names -> new names; assertions updated
from stock-revert-seam checks to the new `DiscardReFlyLoadGameForTesting`
/ `DiscardReFlyLoadSceneForTesting` seams):

- `FullRevertCallback_InvokesTreeDiscardPurge_WithCorrectTreeId` ->
  `DiscardReFly_DoesNotCallTreeDiscardPurge` (pre-populate + assert
  supersede/tombstone/RP lists unchanged).
- `FullRevertCallback_EmptyTreeId_ClearsMarker_StillTriggersStockRevert` ->
  `DiscardReFly_EmptyTreeId_StillClearsSessionArtifacts_StillDispatchesScene`
  (empty tree id no longer branches behavior).
- `FullRevertHandler_PrelaunchContext_InvokesStockRevertToPrelaunch` ->
  `DiscardReFly_PrelaunchContext_InvokesLoadSceneEditor_WithFacility`
  (`(EDITOR, facility)` via scene seam).
- `FullRevertHandler_LaunchContext_InvokesStockRevertToLaunch` ->
  `DiscardReFly_LaunchContext_InvokesLoadSceneSpaceCenter`.
- `FullRevertHandler_PrelaunchContext_LogsTargetInEndLine` ->
  `DiscardReFly_PrelaunchContext_LogsTargetAndFacilityInEndLine`
  (new log format `End reason=discardReFly`).

Add new:

- `DiscardReFly_LaunchContext_ClearsSessionArtifacts_LoadsRpQuicksave_TransitionsToKSC`
  — full flow: marker cleared, journal null, provisional gone from
  `CommittedRecordings`, `DiscardReFlyLoadGameForTesting` received the
  expected temp name, `DiscardReFlyLoadSceneForTesting` received
  `(SPACECENTER, VAB)` (facility ignored for Launch).
- `DiscardReFly_PrelaunchContext_VAB_TransitionsToVAB` — scene seam
  receives `(EDITOR, VAB)`.
- `DiscardReFly_PrelaunchContext_SPH_TransitionsToSPH` — scene seam
  receives `(EDITOR, SPH)`.
- `DiscardReFly_SupersedeRelationsForOtherSplitsInTree_Preserved` —
  pre-populate two `RecordingSupersedeRelation` rows tied to other
  split ids; assert both present post-handler and
  `SupersedeStateVersion` bumped exactly once.
- `DiscardReFly_TombstonesForOtherSplitsInTree_Preserved` —
  `LedgerTombstone` rows for other splits preserved;
  `TombstoneStateVersion` NOT bumped.
- `DiscardReFly_UnfinishedFlightsEntryForThisSplit_StaysVisible` —
  `EffectiveState.IsUnfinishedFlight(originRecording)` returns true
  after handler. Per `EffectiveState.cs:161-166` the predicate
  short-circuits false when `rec.MergeState != MergeState.Immutable`,
  so the test setup puts the origin-crash recording at
  `MergeState.Immutable` (correct for a crashed origin split — no
  re-fly commit occurred, so it remains `Immutable`). Also asserts
  the origin RP is still in `scenario.RewindPoints` with
  `SessionProvisional=false`.
- `DiscardReFly_OriginRp_SurvivesLoadTimeSweep` — end-to-end invariant:
  pre-populate an origin RP (`SessionProvisional=true`), invoke
  handler, then invoke `LoadTimeSweep.Run` with the cleared marker;
  assert the origin RP is STILL present and has
  `SessionProvisional=false`.
- `DiscardReFly_RemovesProvisionalFromCommittedRecordings` — by-id
  lookup on `marker.ActiveReFlyRecordingId` returns null post-handler.
- `DiscardReFly_BumpsSupersedeStateVersion_InvalidatesERSCache` —
  `scenario.SupersedeStateVersion` + `RecordingStore.StateVersion`
  both advanced; next `ComputeERS` rebuilds.
- `DiscardReFly_NullMarker_LogsWarn_NoOp` — null-marker guard.
- `DiscardReFly_UnresolvableRpId_LogsError_ClearsMarker_DoesNotDispatchScene`
  — if RP id cannot resolve, handler clears session artifacts but does
  NOT call `LoadGame`/`LoadScene` (player left in FLIGHT).
- `DiscardReFly_RpQuicksaveMissing_LogsErrorAndShowsToast` —
  `File.Exists(ResolveAbsoluteQuicksavePath(rp))` returns false;
  assert Error log emitted, `ScreenMessages.PostScreenMessage`
  invoked with the failure message (spy via test seam on the screen-
  message dispatch), no scene dispatch.
- `DiscardReFly_WhileJournalActive_Refused` — pre-populate
  `ActiveMergeJournal`; handler refuses (Warn log +
  `ScreenMessages.PostScreenMessage("merge in progress — retry in a moment")`
  + no-op on session state).

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
  still committed and the first split's recording is still at its
  correct committed MergeState (`Immutable` for a crashed sealed split,
  `CommittedProvisional` for a successfully-merged sibling that has
  not yet been sealed by the classifier).

## Documentation updates

- `docs/parsek-rewind-to-separation-design.md` §6.14 second bullet rewrite:

  > "**Discard Re-fly** -- `DiscardReFlyHandler`: remove the provisional
  > re-fly recording from `CommittedRecordings`, promote the origin
  > RP (the RP named by `marker.RewindPointId`) by flipping
  > `SessionProvisional=false` and clearing `CreatingSessionId` so the
  > post-load `LoadTimeSweep` treats it as persistent, clear
  > `ActiveReFlySessionMarker` and `ActiveMergeJournal`, bump
  > `SupersedeStateVersion` (not `TombstoneStateVersion` — no tombstone
  > rows are touched), delete the prior session temp quicksave if still
  > present, then stage the RP quicksave through the save-root copy path
  > and `GamePersistence.LoadGame`. On success, for the Prelaunch
  > context pre-set
  > `EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN`
  > (NOT `LOAD_FROM_CACHE` — the cached ShipConstruct is stale after
  > `LoadGame` swaps `HighLogic.CurrentGame`) and
  > `EditorDriver.editorFacility = facility`; then
  > `HighLogic.LoadScene` dispatches to `GameScenes.SPACECENTER` for
  > Launch or `GameScenes.EDITOR` for Prelaunch. Stock
  > `FlightDriver.RevertToLaunch` / `RevertToPrelaunch` are NOT used —
  > decompilation shows they restore the current flight's cached
  > `PostInitState`/`PreLaunchState`, which are tied to the current
  > flight's launch moment, not the RP's UT.
  > `TreeDiscardPurge.PurgeTree` is NOT called: the tree's other
  > Rewind Points, supersede relations, and tombstones are preserved.
  > The origin split's `Immutable` crash recording plus its promoted RP
  > still resolve, so `EffectiveState.IsUnfinishedFlight` stays true
  > (the predicate short-circuits false on `MergeState != Immutable`
  > per `EffectiveState.cs:161`, so the `Immutable` origin is required
  > AND sufficient together with the RP and a crashed terminal) and
  > the Unfinished Flights row stays visible. Log
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
  the Unfinished Flights entry is still present (because the origin
  crash recording remains at `MergeState=Immutable` and its RP was
  promoted to persistent by the handler before marker clear, so
  `IsUnfinishedFlight` continues to resolve true per
  `EffectiveState.cs:161-200`), career state is unchanged, the tree's
  committed supersede relations / tombstones / RPs are preserved. Add
  a line at the end of §A.4 noting that "other re-fly attempts already
  merged into the tree are unaffected" so the user story covers the
  motivating concern directly.

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

Two commits on `fix/discard-refly-semantics` (the plan review called
out three-commit overhead; the first two collapse cleanly because the
CHANGELOG line is small and the unit-test surface is already touched
by the code commit):

1. **Plan + code + unit tests + CHANGELOG.**
   `docs/dev/plan-discard-refly-semantics.md` (this file — amended in
   place with the review fixes), `RevertInterceptor.cs` rewrite, dialog
   copy, `ReFlyRevertDialogTests.cs` rename/extension, new log-assertion
   test in `RewindLoggingTests.cs`, `CHANGELOG.md` line. Post-commit:
   build, verify deployed DLL contains the new UTF-16 string
   `"End reason=discardReFly"` per `.claude/CLAUDE.md` verification
   recipe, run full `dotnet test`.
2. **In-game tests + design / user-guide docs.**
   `ReFlyRevertDialogTest.cs`, `ReFlyRevertDialogPrelaunchTest.cs`,
   `DiscardReFlyPreservesMergedSupersedeTest.cs`, and the doc edits
   for §6.14 / §6.17 / §A.4 / user-guide. Post-commit: build + in-game
   test run in KSP via Ctrl+Shift+T.

Each commit message follows the repo's imperative style; no
`Co-Authored-By` trailer (per `.claude/CLAUDE.md` git-commits rule).

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
   without pre-setting the facility drops the player into whatever
   editor was last open (default VAB for a fresh career). Implementation
   pre-sets
   `EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN`
   and `EditorDriver.editorFacility = facility` before
   `LoadScene(EDITOR)`. Decompilation evidence (via `ilspycmd` on
   `Assembly-CSharp.dll`) for the API choice:
   - `FlightDriver.RevertToLaunch()` rebuilds the flight from the
     current flight's cached `PostInitState` — it returns to the
     flight's own launch moment, not to the RP's UT. Stock revert
     CANNOT target an arbitrary quicksave.
   - `FlightDriver.RevertToPrelaunch(EditorFacility facility)` calls
     `EditorDriver.StartEditor(facility)` wired to the current
     flight's cached `PreLaunchState` — same problem: it goes to the
     current flight's prelaunch state, not to the RP.
   - `EditorDriver.StartupBehaviours` enum: decompile-confirmed
     values include `START_CLEAN` and `LOAD_FROM_CACHE`. `LOAD_FROM_CACHE`
     re-opens the editor's last cached `ShipConstruct`, which after
     `GamePersistence.LoadGame` replaces `HighLogic.CurrentGame` is
     stale / irrelevant (it may reference parts or resource state from
     the wrong save). `START_CLEAN` opens the editor fresh against
     the just-loaded career, which is the correct behavior for a
     revert-to-RP-UT. No fallback — `LoadGame` + `LoadScene` is the
     only correct path.

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
   - `LoadTimeSweep.Run`: marker is null, spare set is empty
     (`LoadTimeSweep.cs:92-104`). The RP-discard pass
     (`LoadTimeSweep.cs:132-144`) would normally add every
     `SessionProvisional=true` RP to `discardRps` and reap them at
     line 192 — **this is the bug that motivated Approach step 3**.
     The handler's step 3 flips the origin RP's `SessionProvisional`
     to `false` BEFORE clearing the marker, so the sweep sees the
     origin RP as persistent (line 139: `if (!rp.SessionProvisional) continue;`)
     and leaves it alone. Any OTHER session-provisional RPs (stale
     crashed-session leftovers, zombies from a different tree) still
     get cleaned up as intended.
   - `RewindPointReaper`: reaps RPs tied to committed/immutable
     splits whose supersede chains are closed; preserves live
     Unfinished Flights RPs. Our promoted origin RP has
     `SessionProvisional=false` AND at least one slot still resolving
     to an `Immutable` crash recording — the reaper will NOT reap it
     because its `IsReapEligible` check walks every slot and requires
     ALL slots to resolve to Immutable-and-fully-superseded; the
     origin split's slot is neither (no supersede exists). See
     `RewindPointReaper.cs:165-200`.

   Verify this chain explicitly in the new in-game test
   `DiscardReFlyPreservesMergedSupersedeTest` and the unit test
   `DiscardReFly_OriginRp_SurvivesLoadTimeSweep`: after the scene
   transition, the origin split's RP is still present in
   `RewindPoints`, the first split's supersede relation is still
   present in `RecordingSupersedes`, and the Unfinished Flights UI
   surfaces the origin split.

5. **Merge-in-progress collision (see Approach step 4).** The
   `ActiveMergeJournal != null` edge case. **Chosen: defense in
   depth — option (a) AND the handler-side refusal.** The dialog
   gate at `ReFlyRevertDialog.Show` hides the Discard button when
   `scenario.ActiveMergeJournal != null` (primary UX signal to the
   player); the handler re-checks at entry and refuses with a
   "merge in progress — retry in a moment" toast + Warn log if
   called anyway (defense for any other call-site we haven't
   foreseen, including tests that bypass the dialog). Option (c)
   "handler proceeds and accepts the merge rollback" was rejected
   because the rollback happens in `RunFinisher` on the next load,
   which is after our `LoadGame` + scene swap — the two roll back
   through different scenario lifecycles and the interleaving is
   not obviously safe.

6. **Temp file cleanup on failed `LoadGame`.** Step 8 error path
   deletes the staged temp file. Existing pattern at
   `RewindInvoker.cs:451-453` (`TryDeleteTemp` in a `finally`) is
   the template; the handler uses the same `TryDeleteTemp` helper
   directly if it is made `internal`, otherwise inlines the
   try/File.Delete/catch block (it is three lines — no helper
   extraction needed for a single extra call-site).
