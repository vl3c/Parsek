# Quickload-Resume Recording (Bug C + cascading H, I)

## Problem

When the player quicksaves and then quickloads during an active recording, Parsek incorrectly treats the quickload as a "revert" and **commits the active tree as terminal**. The user's mission fragments: the committed recording only has data up to the quicksave UT, there are 2+ minutes of untracked flight until the user notices and manually restarts recording, and crew tracking splits across multiple unlinked recordings.

Observed in the 2026-04-09 KerbalX playtest:

```
10:33:36 StartRecording pid=2708531065           (launch Kerbal X)
10:33:50 – 10:34:22 6 decouple events            (asparagus staging)
10:38:14 Game State Saved to s28/quicksave       ← user quicksaved
10:38:26 CommitTreeRevert: finalizing tree at UT=1888.1   ← wrong
10:38:26 stashed pending tree 'Kerbal X'
10:38:27 OnLoad: isFlightToFlight=True, isVesselSwitch=True, isRevert=False
10:38:29 Committed tree 'Kerbal X' (8 recordings)
10:40:18 StartRecording pid=1794174953           ← manual restart, 2 min later
```

### Cascading bugs this causes

- **Bug H** — ghost orbits are incomplete (partial arcs, vessel disappears mid-flight). The committed tree has trajectory only up to UT ~1888; playback ends there.
- **Bug I** — wrong kerbal(s) spawn at end of mission. The Kerbal X return recording only has Valentina in `CrewEndStates`; the original crew got scattered across standalone "Bilny Kerman" (Bob state=Unknown) and "Jenming Kerman" (Bill state=Aboard) recordings with no tree linkage.

Both dissolve automatically when C is fixed.

## Root cause

Three compounding defects (the doc originally listed two plus a "minor" third; review pushback: C3 is load-bearing, not minor):

### C1: `OnSceneChangeRequested` commits the tree unconditionally on FLIGHT→FLIGHT

`ParsekFlight.OnSceneChangeRequested` (`ParsekFlight.cs:751`) fires on every `onGameSceneLoadRequested` event — including quickloads, reverts, and vessel switches that trigger a scene reload. When `activeTree != null` and the destination scene is FLIGHT, it calls `FinalizeTreeOnSceneChange(scene)` which unconditionally calls `CommitTreeRevert(commitUT)`:

```csharp
// ParsekFlight.cs:787-804
private void FinalizeTreeOnSceneChange(GameScenes scene)
{
    double commitUT = Planetarium.GetUniversalTime();
    ...
    if (scene == GameScenes.FLIGHT)
    {
        // Revert: preserve snapshots for merge dialog
        CommitTreeRevert(commitUT);
    }
    else if (!ParsekScenario.IsAutoMerge) { ... }
    else { CommitTreeSceneExit(commitUT); }
}
```

`CommitTreeRevert` stops the recorder, calls `FinalizeTreeRecordings` (sets terminal state, captures terminal orbit/position), and stashes the tree as pending. By the time `ParsekScenario.OnLoad` runs, the tree is already finalized — **there is no going back**.

### C2: Active tree state is not serialized to disk

`ParsekScenario.OnSave` (`ParsekScenario.cs:30-94`) only persists `RecordingStore.CommittedRecordings` and `RecordingStore.CommittedTrees`. The currently-active tree (the one being recorded into) lives only in `ParsekFlight.activeTree` and `ParsekFlight.recorder`, which are in-memory fields destroyed when the scene reloads. Verified empirically: `s28/quicksave.sfs` contains **0 recording nodes** from the playtest session.

So even if C1 is fixed and we refuse to commit on FLIGHT→FLIGHT, a quickload still loses the active tree because it was never written to the save file the quickload is loading from.

### C3: Revert detection conflates FLIGHT→FLIGHT with revert

`ParsekScenario.OnLoad` flags `isRevert = true` for any FLIGHT→FLIGHT transition that isn't a vessel switch:

```csharp
// ParsekScenario.cs:325-328
bool isRevert = !isVesselSwitch
                && (savedEpoch < MilestoneStore.CurrentEpoch
                    || totalSavedRecCount < recordings.Count
                    || isFlightToFlight);
```

The `|| isFlightToFlight` clause wrongly classifies quickloads as reverts. Only the first two conditions (epoch and count comparisons) are reliable revert indicators; the FLIGHT→FLIGHT clause should be removed.

**Important**: removing this clause alone is *not* safe. Today's cleanup logic at `ParsekScenario.cs:343-398` (spawned-vessel stripping, crew rescue, epoch bump, budget deduction coroutine) only runs when `isRevert` is true. If we flip a FLIGHT→FLIGHT quickload from "revert" to "not revert" without also fixing C1+C2, we'd end up with the tree still committed (C1 unchanged) but without the cleanup running — a strictly worse state than today.

**Additional defense in depth** (review suggestion): replace the clause-based `isRevert` with an explicit positive-state check: `isRevert = !isVesselSwitch && (savedEpoch < MilestoneStore.CurrentEpoch || totalSavedRecCount < recordings.Count)`. Intermediate guard logs if `isFlightToFlight && !isRevert` so we can spot regressions where count/epoch heuristics miss a real revert.

## Design

### Key invariants

- **User intent is the source of truth.** If the user quickloaded, they want to resume from the quicksave point. If they reverted to launch, they want to restart from t=0. The recording lifecycle should follow this intent.
- **Never lose trajectory data silently.** Whatever the recorder had captured before quickload must be either (a) kept because the quicksave contained it, or (b) discarded because the quickload rewound past it.
- **Idempotent on repeated quickloads.** Double-F9 with no edits between must leave the tree in the same state as single-F9.
- **At most one active tree exists at any time.** Enforced by assertion in `StashPendingTree` when `state == Limbo`.
- **`PARSEK_ACTIVE_TREE` only written when truly in flight.** OnSave guards on `HighLogic.LoadedScene == GameScenes.FLIGHT && activeTree != null && recorder != null`. Outside flight there is nothing to resume.

### Approach: serialize + restore the active tree

The only safe way to survive a scene reload is to write the active-tree state to disk before the scene unloads. Extend `OnSave` to serialize the currently-active tree inside the ScenarioModule's config, then `OnLoad` restores it.

### Consolidate `limboTree` into `pendingTree` (review feedback)

The original draft added a third static slot (`limboTree`) alongside `pendingTree`. Review pushback: three slots fighting over "who owns the tree now" compounds state leak paths.

**Revised approach**: reuse the existing `pendingTree` field with a new state enum:

```csharp
internal enum PendingTreeState
{
    Finalized,   // Existing behavior — tree.TerminalStateValue set, snapshots captured, ready to commit
    Limbo,       // New — raw in-flight state, recorder field refs torn down but tree data intact
}

// In RecordingStore:
private static RecordingTree pendingTree;
private static PendingTreeState pendingTreeState;
```

`StashPendingTree(tree, state)` takes the state. Existing callers pass `Finalized` (no behavior change). The new scene-reload path passes `Limbo`.

`OnLoad` branches on the state when deciding what to do:
- `Finalized` → current auto-commit / merge-dialog flow
- `Limbo` → restore-and-resume OR finalize-then-commit based on the revert-vs-quickload decision

Single slot, fewer leak paths, and the serializer can reuse `SaveTreeRecordings` directly with an `IsActive=true` flag on the node rather than maintaining a parallel serializer (review suggestion #2).

### Flow

1. **OnSave** — if `HighLogic.LoadedScene == FLIGHT && activeTree != null && recorder != null`:
   - Flush the recorder's buffered `Recording.Points` / `PartEvents` / `OrbitSegments` into `activeTree.Recordings[activeTree.ActiveRecordingId]` (same logic as `FlushRecorderToTreeRecording` but without finalization).
   - Write `activeTree` as a `RECORDING_TREE` node with an extra `isActive=true` attribute. Re-use `SaveTreeRecordings` with a flag parameter — no parallel serializer.
   - Also capture the recorder's `RewindSaveFileName` and the chain manager's `ActiveChainId` / `BoundaryAnchor` on the node so resume can restore them.
   - **Important**: explicitly flush `chainManager.pendingSplitRecorder` if it exists (see edge cases below). Either merge its data into the tree or abort the split before stashing.

2. **OnSceneChangeRequested** (destination == FLIGHT):
   - **Replace** the `CommitTreeRevert` call with `StashActiveTreeAsPendingLimbo()`:
     - Stop the recorder (no `FinalizeTreeRecordings`, no terminal state capture, no snapshot nulling).
     - Flush recorder buffers into the active tree (same as OnSave flush).
     - Call `RecordingStore.StashPendingTree(activeTree, PendingTreeState.Limbo)`.
   - Tear down recorder, background recorder, event subscriptions as today.

3. **ParsekScenario.OnLoad** — after revert detection runs:
   - **State = `Limbo`, `isRevert = true`** (true revert, based on epoch/count): *finalize then commit*. Run the equivalent of `CommitTreeRevert`'s finalization logic on the limbo tree, flip its state to `Finalized`, and fall through to the existing pending-tree handling (merge dialog or auto-commit). The tree lands as a normal completed mission.
   - **State = `Limbo`, `isRevert = false`** AND save node has `PARSEK_ACTIVE_TREE` flag: *restore and resume*. Call `ParsekFlight.RestoreActiveTreeFromPending()`. See below.
   - **State = `Finalized`**: current behavior unchanged.

4. **`ParsekFlight.RestoreActiveTreeFromPending()`** (called from `OnLoad` once the scene is ready):
   - Pop the pending tree, assign to `activeTree`.
   - Find the active vessel whose name matches `activeTree.Recordings[ActiveRecordingId].VesselName`. Coroutine-wait up to 3s if not yet loaded.
   - Update `activeTree.VesselPersistentId` and the active recording's `VesselPersistentId` to the current vessel's PID (KSP regenerates PIDs on quickload).
   - Construct a fresh `FlightRecorder` with `ActiveTree = activeTree`, `BoundaryAnchor` restored from save node, `RewindSaveFileName` restored.
   - Call `StartRecording(isPromotion: true)`. With the Bug A fix already in `fix/playtest-failures`, this skips seed-event emission, so no duplicate `DeployableExtended` / `LightOn` / etc. events at the quickload UT.
   - Subscribe the recorder to Harmony and GameEvents.

### New types / methods

- `PendingTreeState` enum in `RecordingStore`.
- `RecordingStore.StashPendingTree(RecordingTree, PendingTreeState)` — replaces existing single-arg overload; existing callers pass `Finalized`.
- `RecordingStore.PendingTreeStateValue` property (read-only).
- `ParsekFlight.StashActiveTreeAsPendingLimbo(double commitUT)` — replaces `CommitTreeRevert` on the FLIGHT→FLIGHT path.
- `ParsekFlight.RestoreActiveTreeFromPending()` — the resume coroutine.
- `SaveTreeRecordings(ConfigNode, bool isActiveTreePass = false)` — existing method gets an optional flag; when true, writes the one active tree under a node with `isActive=true`.
- `OnLoad` gets a new `TryRestoreActiveTreeNode(ConfigNode)` call site immediately after `ParsekSettingsPersistence.ApplyTo(Current)` — parses the active-tree node into the pending slot with `state = Limbo`, then lets the existing revert-detection code decide the final fate.

### Resume semantics: does recording actually restart?

Yes, but the new recorder appends to the **existing chain segment**, not a new one. Specifically:

- The tree's `ActiveRecordingId` points to the segment that was active at quicksave time.
- The new `FlightRecorder` is constructed with `BoundaryAnchor` unset (no chain boundary to cross) and `isPromotion: true` (so bug A's seed-event skip kicks in — no duplicate seed events at the quickload UT).
- `StartRecording(isPromotion: true)` initializes module caches and tracking HashSets, but skips seed event emission.
- The recorder's `Recording` list is empty at start; new `CommitRecordedPoint` calls append to this list. At the next `FinalizeRecordingState` (real revert, scene exit, or chain boundary), `FlushRecorderToTreeRecording` merges the list into `activeTree.Recordings[ActiveRecordingId].Points`.

### Handling the vessel PID change

KSP regenerates `persistentId`s on quickload in some scenarios (observed in the playtest: `2708531065` before quicksave, `1794174953` after quickload). The tree's `VesselPersistentId` and `activeRecordingId` vessel reference would become stale.

**Match by name, not PID.** On resume, find the current active vessel whose `vesselName` matches the tree's active recording `VesselName`. If none found within 3 seconds (coroutine wait), log a warning and leave the tree in pending for the merge dialog to offer spawning.

### Edge cases (addressed per review feedback)

1. **Quickload into a non-flight scene while a tree was active.** If the user quicksaved in flight, went to Tracking Station (which commits the tree via `CommitTreeSceneExit`), then quickloads into the in-flight save: the quicksave file was written while the tree was active (via step 1 above), but the current in-memory state shows a finalized tree (from the TS transition). On load, we detect the `PARSEK_ACTIVE_TREE` node and the current `pendingTree` slot is empty → restore path runs normally.
   - *Guard*: OnSave never writes `PARSEK_ACTIVE_TREE` unless `HighLogic.LoadedScene == GameScenes.FLIGHT && activeTree != null && recorder != null`. Scenes outside flight cannot emit the node even if a pending tree exists.

2. **Multiple active trees.** Current code has a single `activeTree` field on `ParsekFlight`, so structurally only one tree can be active. Add an explicit assertion in `StashPendingTree(tree, Limbo)` and in `TryRestoreActiveTreeNode` that no prior limbo tree exists. If one does, log a warning and discard the old one (fail loud, not silent).

3. **EVA boarded between quicksave and quickload.** Tree's `ActiveRecordingId` points to an EVA sub-recording whose vessel (the kerbal) was absorbed back into the parent ship between save and load. On restore, the name match for the EVA kerbal fails (they're no longer a vessel — they're now crew inside the parent ship).
   - *Handling*: if name match fails for the active recording's vessel, fall back to matching the parent recording's vessel (walk `ParentRecordingId`). If found, set that recording as the active one (the EVA was absorbed into the parent) and restart recording on the parent. Log the transition.
   - If even the parent can't be found, log a warning and leave the tree in pending for the merge dialog.

4. **Quickload mid-chain-continuation window.** `ChainSegmentManager` uses a `pendingSplitRecorder` field (`ParsekFlight.cs:164`) during the narrow window between `OnPartUndock` detecting a split and the continuation coroutine deciding to commit or resume. This field is in-memory only and is not part of `activeTree` yet.
   - *Handling*: `StashActiveTreeAsPendingLimbo` explicitly calls `chainManager.AbortPendingSplit("scene reload")` before stashing. Any decouple that happened right before the quicksave is reflected by the physical vessel state (e.g. debris now exists as a separate vessel in the save); on resume, the background recorder will rediscover debris via normal `OnVesselCreate` / `OnVesselLoaded` events.
   - A stricter alternative (deferred): flush the `pendingSplitRecorder`'s buffered data into a new child recording in the tree. More complex; skip for v1.

5. **KSP's automatic prelaunch `persistent.sfs`.** Written before physics starts when a flight is launched. At that moment `activeTree == null` (recording hasn't started yet), so OnSave's guard skips `PARSEK_ACTIVE_TREE` write. No new edge case — verified by inspection of `ParsekFlight.Awake` / `Start`.

6. **Idempotent repeated quickloads.** First F9: restore from `PARSEK_ACTIVE_TREE`, resume. If the user F9s again immediately without any intervening recording, the new quicksave IS the same file (KSP overwrites `quicksave.sfs` on each F5, but F9 doesn't modify it). On the second F9, the restored tree data matches the first restore exactly → same outcome. Assertion in the in-game test.

7. **Quickload during physics warp.** Warp doesn't fire `OnSave`, so a quicksave captured before warp has the pre-warp state. On quickload, warp rate is typically reset by KSP. No interaction with the active-tree restore path beyond what a normal quickload triggers.

8. **Rewind interaction.** `RewindContext.IsRewinding` flag indicates the load was triggered by Parsek's rewind feature (`parsek_rw_*` quicksave). The restore-active-tree path must NOT run when rewinding — rewind explicitly wants to reset playback state. Guard: `if (RewindContext.IsRewinding) { /* discard any PARSEK_ACTIVE_TREE from save */ }`.

### Scope: what we do NOT try to preserve

- **Live recorder field state** that isn't visible in committed data: `extendedDeployables`, `lightsOn`, `deployedFairings`, etc. These are re-seeded from the current vessel state on resume (via `SeedExistingPartStates`, no event emission since `isPromotion: true`). Any transitions that happened between the in-memory state and the quicksave state are lost, but the next physics frame will detect real transitions.
- **Background recorder state** for non-active tree vessels (debris, etc.). Same rationale — rehydrate from `Vessel` state on load.
- **Continuation / boundary anchor timing** beyond what's in the tree config. If the user quickloads mid-continuation-window, the continuation is considered consumed.

### Backwards compatibility

- No active tree in save → old behavior (nothing to restore). Safe for existing saves.
- Existing `PARSEK_SCENARIO` format is unchanged — the new node is just a `RECORDING_TREE` with an `isActive` flag (optional field; defaults to `false`, making old saves parse unchanged).
- No format version bump needed since the new flag is purely additive.

### Interaction with Group 1 fixes (explicit per review feedback)

- **Bug A (seed-event skip on `isPromotion=true`):** critical dependency. `RestoreActiveTreeFromPending` calls `StartRecording(isPromotion: true)`, which triggers the bug A fix in `ResetPartEventTrackingState(v, emitSeedEvents: false)`. Without bug A's fix, every quickload would re-inject spurious `DeployableExtended`/`LightOn`/etc. events at the quickload UT, re-creating the rover trim issue. Confirmed at `FlightRecorder.cs:3991` and `:4293` in the fix branch.
- **Bug F2 (`ParsekSettingsPersistence.ApplyTo` in OnLoad):** ordering matters. The new `TryRestoreActiveTreeNode` call runs **after** `ParsekSettingsPersistence.ApplyTo(Current)` (so restored settings like auto-merge and sampling interval take effect) but **before** the revert-detection block. Pinned ordering: `ApplyTo` → `TryRestoreActiveTreeNode` (populates pending-limbo if node present) → revert detection → state-specific dispatch.

## Test strategy

Layered: unit → xUnit integration (via `ScenarioWriter` round-trip) → in-game tests → manual playtest. The xUnit integration layer is new — it lets us exercise the full save/restore round-trip without booting KSP, catching ~80% of what in-game tests would.

### Unit tests (pure static, no Unity)

1. **`isRevert` logic fixture.** Given saved epoch/count/`lastOnSaveScene`/`loadedScene`/`vesselSwitchPending`, verify the new `isRevert` evaluates correctly for:
   - Real revert (epoch-1, count-1, FLIGHT→FLIGHT): `true`
   - Quickload (same epoch, same count, FLIGHT→FLIGHT, no vessel switch): `false`
   - Vessel switch (same epoch, same count, FLIGHT→FLIGHT, `vesselSwitchPending=true`): `false`
   - KSC→FLIGHT (same epoch, same count, not FLIGHT→FLIGHT): `false`
   - Real revert but `isFlightToFlight=true` still works (count/epoch still catches it): `true`
2. **`PendingTreeState` transitions.** `StashPendingTree(tree, Limbo)` → `PendingTreeStateValue == Limbo`. `StashPendingTree(tree, Finalized)` → `Finalized`. Repeat-stash logs warning and overwrites. `DiscardPendingTree` clears both tree and state.
3. **Assertion: multi-tree rejection.** Calling `StashPendingTree` with `Limbo` state when an existing pending tree exists logs a warning and replaces (documents the invariant).

### xUnit integration tests (ScenarioWriter round-trip)

4. **Active-tree serialization round-trip.** Build a `RecordingTree` with 2 chain segments (parent + Valentina EVA child), ~50 points, 3 part events, 1 orbit segment. Use `ScenarioWriter` helpers to write a `PARSEK_SCENARIO` node containing the tree with `isActive=true`. Parse back. Assert structural equality (recording IDs, points count, events count, rewind save filename, `BoundaryAnchor`, chain state).
5. **`SaveTreeRecordings(isActiveTreePass=true)` emits `isActive=true`** on the output node; with `isActiveTreePass=false` it does not emit the flag. Round-trip preserves the flag.
6. **OnLoad `TryRestoreActiveTreeNode` path.** Write a scenario ConfigNode with one committed tree and one active (`isActive=true`) tree. Call the new parse method. Assert the active one landed in pending-limbo, the committed one in `committedTrees`. Tests the dispatch logic without needing `HighLogic.CurrentGame`.

### In-game tests (`InGameTests/RuntimeTests.cs`)

7. **`QuickloadMidRecordingResumesTree`** (happy path):
   - Start recording a simple KerbalX-style launch
   - Record pre-F5 state: `int p0 = recorder.Recording.Count`, `double ut0 = Planetarium.GetUniversalTime()`, `string recId0 = activeTree.ActiveRecordingId`, `uint pid0 = FlightGlobals.ActiveVessel.persistentId`
   - Quicksave via `QuickSaveLoad.QuickSave` helper
   - Advance UT via time warp for ~20 seconds
   - Quickload via `QuickSaveLoad.QuickLoad`
   - Assert: `activeTree != null` (tree survived)
   - Assert: `activeTree.ActiveRecordingId == recId0` (same recording ID, stable across save/load)
   - Assert: `Planetarium.GetUniversalTime() < ut0 + 1.0` (time rewound to around quicksave UT, within tolerance)
   - Assert: `activeTree.Recordings[recId0].Points.Last().ut <= ut0 + 0.5` (no points from the 20-second fly phase that got rewound)
   - Assert: `activeTree.VesselPersistentId == FlightGlobals.ActiveVessel.persistentId` (PID refreshed)
   - Assert: `recorder != null && recorder.IsRecording` (new recorder running, appending to the restored tree)
   - Assert: no duplicate seed events — count `DeployableExtended` events for each deployable part and verify count ≤ 1

8. **`RevertToLaunchStillCommitsTree`** (revert path unchanged):
   - Start recording, fly 30s, press Revert to Launch
   - Assert: `committedTrees.Count == 1 + baseline` (tree finalized, moved to committed)
   - Assert: `activeTree == null` (no active tree after revert)
   - Assert: merge dialog shown if `autoMerge` disabled

9. **`VesselSwitchPreservesActiveTree`** (new behavior from removing `|| isFlightToFlight`):
   - Launch, fly 30s, use `FlightGlobals.SetActiveVessel` to switch to an unloaded nearby vessel that forces a scene reload
   - Assert: `activeTree` survives the switch
   - Assert: old vessel's recording moved to `BackgroundMap`, new vessel is either backgrounded or has no active recording

10. **`DoubleQuickloadIdempotent`** (invariant test):
    - Start recording, fly 30s, F5, F9, F9 (two quickloads in a row, no flight between)
    - Assert: `activeTree.Recordings[recId].Points.Count` after second F9 equals `Points.Count` after first F9

11. **`QuickloadDuringWarpResumes`** (edge case #7):
    - Same as test 7 but engage 4x physics warp before F5
    - Assert: post-F9 warp rate is reset (KSP default), tree still resumes

12. **`QuickloadIntoNonFlightSceneDoesNotRestoreActive`** (edge case #1):
    - Start recording, fly, F5, transition to Tracking Station (tree committed to pending-Finalized), F9 back to flight
    - Assert: tree is finalized normally; no stale `PARSEK_ACTIVE_TREE` restored as if recording was still active

### Manual playtest validation

13. **Reproduce the 2026-04-09 KerbalX + Rover scenario end-to-end.** Launch KerbalX, quicksave during ascent, quickload, continue to orbit, do EVAs, return, land. Verify the mission does NOT fragment into multiple unlinked recordings. Verify the final landing's crew matches the launch crew.

## Risks and open questions

- **Pending dock / undock continuation state.** Covered by edge case 4 — `pendingSplitRecorder` gets aborted on scene reload.
- **Crew reservation state.** Crew reserved by the in-progress recording needs to stay reserved after quickload, not double-reserved. The `crewReplacements` dict is already in the scenario save, so this should be a non-issue, but needs verification via in-game test assertion `assert crewReplacements_postQuickload == crewReplacements_preQuicksave`.
- **~~Tree-preservation on vessel switch.~~** ~~Original PR #160 punted on vessel switches by routing them through the finalize path; tracked as bug #266 follow-up.~~ **Resolved**: bug #266 implemented the pre-transition stash + `RestoreActiveTreeFromPendingForVesselSwitch` coroutine. New `PendingTreeState.LimboVesselSwitch` carries a tree across the scene reload with the old vessel already in `BackgroundMap`. Test #9 (`VesselSwitchPreservesActiveTree`) is partially covered by `OutsiderActiveTreeSurvivesOnSaveOnLoadRoundTrip_Bug266` and the unit test suite; the full programmatic vessel-switch + scene-reload test is deferred to manual playtest because in-game tests can't drive a `FlightDriver` reload from inside a single test method.
- **Rewind save state.** The recorder's `RewindSaveFileName` is preserved by serializing it on the active-tree node. Resume path restores it to the new recorder so the R button for the resumed recording still works.
- **Interaction with the existing `HandleRewindOnLoad` path** (`ParsekScenario.cs:737+`). Covered by edge case 8 — `RewindContext.IsRewinding` guard skips the active-tree restore path.
- **Memory vs disk.** Serializing the full active tree on every quicksave adds I/O. Expected size for a typical 5-minute flight (~200 points, 20 part events): under 20 KB. Acceptable, but confirm with a diagnostic log line measuring serialized size on first use.
- **Transient `SpawnedPid` fields.** `Recording.SpawnedVesselPersistentId` and related fields are already re-derived on OnLoad via the existing `RestoreStandaloneMutableState` / tree restore path. The active-tree restore should NOT clobber these if they happen to be set — though for an in-progress recording they should be 0.
- **The `RunOptimizationPass` interaction.** Optimization (merge / split / boring-tail trim) runs on committed trees. An active tree in limbo should NOT be optimized — it's still growing. The restore path must re-commit the tree normally (not via `CommitTree`) on eventual finalization, and only then should the optimizer run over it.
- **`onVesselChange` firing during restore.** When KSP finishes loading a quicksave, it fires `onVesselChange` for the current vessel. `ParsekFlight.OnVesselSwitchComplete` may react to this and mutate tree state. The restore coroutine must be idempotent against this, or explicitly set a `restoring` flag that suppresses reactive handlers during the restore window.

## Implementation order

**Key insight from review:** steps cannot ship sequentially. Removing `|| isFlightToFlight` without the pending-limbo flow in place would leave FLIGHT→FLIGHT reloads hitting `CommitTreeRevert` but skipping the cleanup logic gated on `isRevert=true` — a strictly worse state. The serializer without the restore path leaves quickloads losing the tree entirely.

**All changes land in one atomic commit** (the first in the new worktree), with full unit and xUnit integration tests. In-game tests can be added as follow-up commits since they require manual verification.

### Step 1 (atomic, single commit)

1a. Add `PendingTreeState` enum in `RecordingStore`.
1b. Extend `StashPendingTree` to take `PendingTreeState state`; existing callers pass `Finalized`.
1c. Add `PendingTreeStateValue` read-only property.
1d. Extend `SaveTreeRecordings(ConfigNode, bool isActiveTreePass = false)`. When true, writes `activeTree` as an extra `RECORDING_TREE` with `isActive=true`. Parse flag back in `LoadRecordingTrees`.
1e. `ParsekScenario.OnSave`: after `SaveTreeRecordings(node)` for committed trees, conditionally call `SaveTreeRecordings(node, isActiveTreePass: true)` gated on `HighLogic.LoadedScene == GameScenes.FLIGHT && flight?.activeTree != null && flight?.recorder != null`. Flush the recorder's buffered data into the tree before writing.
1f. `ParsekScenario.OnLoad`: add `TryRestoreActiveTreeNode(ConfigNode)` call immediately after `ParsekSettingsPersistence.ApplyTo(Current)`. Parses the `isActive=true` tree into `pendingTree` with `state = Limbo`. Skip if `RewindContext.IsRewinding` (edge case 8).
1g. Remove `|| isFlightToFlight` from `isRevert` at `ParsekScenario.cs:325-328`. Add a diagnostic log line: `if (isFlightToFlight && !isRevert) ParsekLog.Info("Scenario", "FLIGHT→FLIGHT without revert indicators — not treating as revert")`.
1h. `ParsekScenario.OnLoad`: after revert detection, add the state-specific dispatch:
   - `Limbo + isRevert` → finalize-then-commit (inline the `FinalizeTreeRecordings` + pending-auto-commit logic)
   - `Limbo + !isRevert` → schedule `ParsekFlight.RestoreActiveTreeFromPending` coroutine in `OnFlightReady`
   - `Finalized` → existing logic
1i. `ParsekFlight.FinalizeTreeOnSceneChange`: replace the FLIGHT destination's `CommitTreeRevert` call with a new `StashActiveTreeAsPendingLimbo` that flushes recorder buffers, calls `chainManager.AbortPendingSplit("scene reload")`, stops the recorder without finalization, and `RecordingStore.StashPendingTree(activeTree, PendingTreeState.Limbo)`.
1j. `ParsekFlight.RestoreActiveTreeFromPending` coroutine:
   - Pop pending tree, set `activeTree`
   - Name-match vessel with up to 3s wait, fall back to parent recording's vessel (edge case 3)
   - Update `VesselPersistentId` to current vessel
   - Construct new `FlightRecorder`, restore `BoundaryAnchor` / `RewindSaveFileName` / chain state
   - `StartRecording(isPromotion: true)` — relies on bug A fix for seed event skip
   - Log the restore with UT, point count, vessel name
1k. Unit tests (1-3 from test strategy): `isRevert` fixture, `PendingTreeState` transitions, multi-tree rejection.
1l. xUnit integration tests (4-6): round-trip, `isActive` flag, `TryRestoreActiveTreeNode` dispatch.

### Step 2 (separate commit): In-game tests

2a. Tests 7-12 from the test strategy. These require KSP to run, so they're separated for iteration speed.

### Step 3 (separate commit): Manual playtest validation

3a. Execute the manual playtest scenario. Iterate on any issues before opening the PR.

### Why atomic: concrete failure modes of non-atomic rollout

- **Remove `isFlightToFlight` alone**: FLIGHT→FLIGHT reloads still hit `CommitTreeRevert` (C1 unchanged) but skip the `isRevert`-gated cleanup (spawned-vessel strip, crew rescue, epoch bump). Orphan trees with dangling spawned vessels leak into subsequent flights.
- **Add `PendingTreeState.Limbo` without the serializer**: `StashActiveTreeAsPendingLimbo` runs, tree in pending-limbo slot, scene reloads, OnLoad sees no `PARSEK_ACTIVE_TREE` in save → pending-limbo tree gets discarded or auto-committed as an orphan. Worse than today.
- **Add the serializer without the switching of `FinalizeTreeOnSceneChange`**: `OnSave` writes `PARSEK_ACTIVE_TREE`, scene reloads, OnLoad restores it into pending-limbo — but `FinalizeTreeOnSceneChange` already called `CommitTreeRevert` in memory, so now we have the same tree in both committed state and pending-limbo state. Duplicate mess.

All three failure modes are avoided by landing step 1 as a single atomic change.

## Scope

This design covers bugs C, H, I together. It does NOT cover:

- Bug E (ghost mesh after decouple) — separate fix needed, tracked as #263
- Bug B (EVA exact position) — separate fix, tracked as #264
- Time warp interacting with active tree serialization — warp doesn't trigger OnSave, so no new edge case here beyond the usual "quicksave during warp" behavior

## Rollout

Implemented in a dedicated worktree branched off `fix/playtest-failures` (which has the Group 1 fixes from the current PR). After the Group 1 PR lands, this worktree rebases onto `main`. Targets its own PR for review.
