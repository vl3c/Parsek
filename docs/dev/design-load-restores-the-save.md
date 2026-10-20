# Parsek Load Restores the Save - Design Document

*Design specification for making every load of a save file (F9, the pause-menu Load, the main-menu Load) put Parsek back exactly as that save holds it.*

**Status:** DESIGN, pre-implementation (2026-10-20). Todo `QUICKLOAD-RESTORES-THE-SAVE-EXACTLY`.
Implementation plan: `docs/dev/plans/load-restores-the-save-implementation.md`.

**Owner rulings implemented (V, 2026-10-19):**

- "a quick/load should work exactly as a quickload - restoring the state of the game exactly to
  what it was at the moment of the quicksave / save".
- "quickloading takes the whole game state back to what it was during that specific moment and
  flight, so there should not be any related paradoxes".
- Decision card "Should a quickload also undo Space Center purchases made after the quicksave?":
  Undo them.
- Decision card "Should a quickload also drop other flights committed after the save?": Drop
  them. Flights committed after the save, Re-Fly merges, renames and groups made after it are
  gone too.
- Rewinds keep their current behavior (ruling D2, "committed is permanent").

**Out of scope:** stock Revert to Launch / VAB / SPH (its own contract,
`docs/parsek-rewind-to-separation-design.md` and the revert branch of `ParsekScenario.OnLoad`),
plain rewinds and every Re-Fly load (D2, unchanged), ordinary scene changes (they already read
what was just saved), mission and scenario game modes (Parsek is inert there).

**Related docs:** `docs/parsek-rewind-to-separation-design.md` (sections 2.12, 6.14, 6.16, D2),
`docs/parsek-game-actions-and-resources-recorder-design.md` (ledger storage, QL-R1 / QL-R2),
`docs/parsek-flight-recorder-design.md` (sidecar storage), the load policy table
`Source/Parsek/LoadReconcilePolicy.cs`, the audit appendix of
`docs/dev/plans/refly-ledger-safety-implementation.md`.

---

## 1. Introduction

### 1.1 The problem

A KSP save folder holds several save files (`persistent.sfs`, `quicksave.sfs`, every named save,
KSP's `Backup/` copies), but Parsek keeps one shared `Parsek/` directory per folder whose files
always hold the LATEST state:

- The ledger, game-state events, milestones and baselines live in `Parsek/GameState/*` and are
  rewritten at every `OnSave` (`ParsekScenario.PersistGameStateAndMilestones`,
  ParsekScenario.cs:2679). No save file records which version it was written with.
- Recording sidecars (`Parsek/Recordings/<id>.prec`, `<id>_vessel.craft`, `<id>_ghost.craft`)
  are keyed by recording id only and are rewritten in place after commit (OnSave of a dirty
  recording, the optimizer, the Re-Fly HEAD/TIP split, a re-commit, a MergeState flip that marks
  files dirty).

Parsek then reconciles the gap two different ways, and neither is exact:

- **In-session loads** (F9 anywhere, the Space Center pause-menu Load) keep committed
  recordings, the ledger, missions, groups, names and the Re-Fly bookkeeping in MEMORY and
  trim or retire only what they can prove is the abandoned future of the resumed flight
  (design QL-R1, QL-R2, OQ-1, OQ-2, OQ-3, the in-session staged-state handoff, the committed-copy
  rule, the abandoned-fork prune, the untagged-row retire). Everything else made after the save
  stays: other flights, Re-Fly merges, renames, groups, rewind points, routes.
- **Cold loads** (game start, main-menu Load, a save-folder change) read the save, but take the
  external game-state files as last written (todo
  `COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE`), drop any recording whose sidecar
  epoch moved after the save (a stale root drops its whole tree, ParsekScenario.cs:5979), and
  then move every sidecar the loaded save does not know into `_quarantine`
  (`RecordingStore.CleanOrphanFiles`, RecordingStore.OrphanCleanup.cs:345). Loading an older
  save therefore quarantines the recordings of every newer save in the folder, and loading the
  newer save afterwards finds them missing. A main-menu Load of the same save in the same KSP
  session also keeps the in-memory events and milestones (once-per-folder guards,
  GameStateStore.cs:1399, MilestoneStore.cs:362).

### 1.2 The rule

**A load of a save file sets Parsek's state to exactly what that save file holds.** Nothing from
memory survives it except install-wide settings. Ordinary scene changes, stock reverts, plain
rewinds and the Re-Fly loads keep their current contracts.

To make that possible, every save file has to be a complete, self-consistent snapshot: it must
carry, or reference exactly, everything Parsek needs to rebuild its state, and the files it
references must still exist when it is loaded.

### 1.3 What the player sees

| Player action | Today | With this design |
|---|---|---|
| F5, launch and commit flight B, F9 | B stays committed and replays | B is gone, as at the F5 |
| F5, buy a tech node at the Space Center, F9 | undone (PR #2151) | undone |
| F5 during flight A, merge a Re-Fly of A's booster, F9 | the save's flight resumes, the Re-Fly rows follow the save (6.16) | same result, by construction |
| F5, rename a vessel or regroup recordings, F9 | the rename and the groups stay | the names and groups of the save |
| F5 during flight A, discard A, F9 | A records again from the F5 moment, its recording does not come back (QL-R1) | A resumes with its recording as of the F5 |
| F5, Seal or Stash an Unfinished Flight, F9 | the seal stays | the slot as it was at the F5 |
| Save "before", commit B, save "after", main-menu Load "before", then Load "after" | B's files are quarantined by the first load; "after" loses B | both loads are exact; B is intact in "after" |
| Main-menu Load of an older flight quicksave | the abandoned future's ledger rows and events survive | exact |
| Scene change, stock revert, Rewind, Re-Fly | unchanged | unchanged |

### 1.4 Worked example

Career at UT 1000, funds 50,000. The player presses F5 at the Space Center (`quicksave.sfs`,
UT 1000). They launch "Mun Lander", land it, return, and the flight auto-commits at UT 9000
(+12,000 contract reward, a `RecordingTree` with 4 recordings). At the Space Center they buy
"Advanced Rocketry" (-5,000 at UT 9100), rename "Mun Lander" to "Eagle", and press F9.

1. Stock `QuickSaveLoad.quickLoad` loads `quicksave.sfs`, fires
   `GameEvents.onGameStatePostLoad`, writes `persistent.sfs` and loads the Space Center.
2. Parsek's post-load hook arms the save-file-load intent. The next `ParsekScenario.OnLoad`
   classifies the load as `Cold` with reason `save-file` (section 4.1), resets every save-scoped
   store (section 4.2) and runs the cold branch.
3. The cold branch reads the ledger, events, milestones and baselines from the save's own
   `GAME_STATE` node (section 4.3): funds 50,000 at UT 1000, no contract reward, no tech
   purchase. It loads the recording trees the save lists: "Mun Lander" is not among them. Its
   sidecars stay on disk (other saves may reference them, section 4.5).
4. The vessel-name store comes from the save: no "Eagle" decision. Groups, missions, routes,
   rewind points and the Re-Fly bookkeeping come from the save.
5. The ledger recalculation patches stock funds to 50,000, which agrees with the stock state the
   save itself restored. Nothing is left over to reconcile.

---

## 2. Design Philosophy

1. **A save file is a complete snapshot.** Everything Parsek needs to rebuild its state is in the
   save file or referenced from it by content, never by "whatever is on disk now".
2. **One rule per load source.** A load of a save file reads the save. Memory never wins on such
   a load. The other loads (scene change, revert, rewind, Re-Fly) keep their own contracts; this
   design does not touch them.
3. **Files a save may reference are immutable.** A file that may be referenced by some save is
   never overwritten or deleted. A rewrite keeps the replaced content as a retained version; a
   file is deleted only by a collection that proves no save file in the folder references it.
4. **The simpler model replaces the special cases.** Exact restore makes most of the in-session
   quickload reconciliation unreachable (section 8). It is removed in a separate, later phase,
   after the new path is proven by lanes.
5. **Observable from the log alone.** Every load says how it was classified and why; every
   resolution of an older file version, every retained version and every collection logs a
   line with the ids.

---

## 3. Terminology

| Term | Meaning |
|---|---|
| Save-file load | A load KSP performs from a `.sfs` file through stock's player-facing load paths: F9 (`QuickSaveLoad.quickLoad`), the Space Center or flight pause-menu Load (`KSCPauseMenu`, `QuickSaveLoad`'s load dialog), the main-menu Load and Resume (`MainMenu`). Stock fires `GameEvents.onGameStatePostLoad` on every one of them and on no other load except a revert. |
| Scene-change load | An `OnLoad` that reads the node the immediately preceding `OnSave` wrote (Space Center to VAB, flight to Space Center, a vessel-switch reload, Tracking Station Fly). Memory and the save agree. |
| Referrer | A save file whose contents keep files alive: every `*.sfs` directly in `saves/<folder>/` (including Parsek's transient save-root copies, which only errs toward keeping) and in `saves/<folder>/Backup/`. Parsek's own `Parsek/RewindPoints/*.sfs` and `Parsek/Saves/*.sfs` are not referrers (section 4.5). |
| Content hash | A hash of a sidecar file's bytes, written into the save next to the recording that uses it. |
| Head | `Parsek/Recordings/<id><suffix>` (suffix `.prec`, `_vessel.craft`, `_ghost.craft`, ...): the file the current code reads and writes, unchanged from today. |
| Retained version | A replaced head kept as `Parsek/Recordings/_versions/<id>.<hash><suffix>`. |
| Collection | The pass that deletes retained versions and Parsek-owned files no referrer and no in-memory state references. |

---

## 4. Design

### 4.1 Classifying a save-file load

**Signal.** Stock fires `GameEvents.onGameStatePostLoad(ConfigNode)` after it builds a `Game`
from a save file, before it requests the scene (decompiled KSP 1.12.5:
`QuickSaveLoad.quickLoad` / `onQuickloadPipelineFinished`, `KSCPauseMenu` load, `MainMenu`
load and Resume). It fires on two other paths: `FlightDriver.RevertToLaunch` and
`RevertToPrelaunch` (followed by `OnRevertToLaunchFlightState` / `OnRevertToPrelaunchFlightState`,
which `RevertDetector` already consumes) and the mission-mode builders (Parsek is inert there).
`GamePersistence.LoadGame` and `LoadGameCfg` do NOT fire it, so Parsek's own loads (rewind,
Re-Fly start, Discard Re-fly, the test-batch restores) never arm it. Scene changes never fire it.

**Intent.** A new `SaveFileLoadIntent` (static, like `DiscardReFlyLoadIntent`) is armed by an
`onGameStatePostLoad` handler. The handler lives on a `KSPAddon(Startup.Instantly, once: true)`
`DontDestroyOnLoad` object, because the main-menu Load fires the event while no Parsek scenario
exists (an instance-method handler, as the `RegisterMainMenuHook` note at ParsekScenario.cs:5553
requires). The intent records the frame, the realtime and the node's game UT.

**Classification.** `LoadReconcilePolicy.ClassifyEarly` gains one input, `saveFileLoadPending`,
and one rule placed after the rewind / Re-Fly / Discard Re-fly checks:

```
if (!initialLoadDone)        -> Cold (reason boot | main-menu | folder-change | batch)
if (isRewinding)             -> PlainRewind
if (reFlyInvokePending)      -> ReFlyStart
if (discardReFlyPending)     -> DiscardReFly
if (saveFileLoadPending && !revertPending) -> Cold (reason save-file)
else                         -> InSession
```

`revertPending` is `RevertDetector`'s armed kind read without consuming it (the revert branch
consumes it later, as today). A save-file load becomes a Cold load: `OnLoad` runs the session
reset (4.2), sets `initialLoadDone = false` and takes the cold branch. The `LoadKind` enum does
not change; the classification line gains `coldReason=<boot|main-menu|folder-change|save-file|batch>`.
The intent is consumed by the first `OnLoad` after it is armed, whatever that load classifies as,
and cleared by the main-menu transition.

**The clock.** A save-file load's cold reconcile and its ledger recalculation run at the save's
own `flightState` UT (the UT the save was written at), the same clock a main-menu Load of that
save uses today. Nothing in the cold path reads the UT the player was at before pressing F9.

**Canary.** Every `OnSave` writes `saveStamp = <guid>` into the Parsek node and remembers the
last stamp written. The check runs after the refined classification (`ClassifyRefined`, after
`RevertDetector.Consume`) and only for `InSessionOther` / `QuickloadFlight`; a `StockRevert`
reads the launch or prelaunch snapshot, which carries an older stamp by design, and is skipped.
A checked load whose node carries a different stamp read a save file that no stock path
announced (another mod's loader, a future stock path). It logs one Warn
(`[LoadPolicy] In-session load read a save this session did not just write ...`) and is NOT
reclassified: the canary finds gaps, it does not decide.

**The harness `LoadGame` verb** (`ParsekTestCommandAddon.LoadGameImpl`) loads through
`GamePersistence.LoadGame`, which fires nothing. It gains an opt-in argument, `playerLoad=true`,
that fires `onGameStatePostLoad` with the loaded node exactly where stock's `quickLoad` does, so a
lane that means "the player pressed F9" exercises the production classification. Without the
argument the verb behaves as today: the lanes that use it to stage a fixture, pick a scene or set
`focusPid` keep their current in-session semantics and their log contracts.

### 4.2 The session reset

A save-file load replaces everything, so it must first forget everything. Today three code paths
each forget part of it (`OnMainMenuTransition`, `DetectSaveFolderChange`,
`PrepareForIsolatedBatchFlightBaselineRestore`), and a main-menu Load of the same save keeps the
once-per-folder event and milestone stores and several session statics.

One helper, `ParsekScenario.ResetSaveScopedSessionState(string reason)`, runs at the start of
every Cold `OnLoad` (all reasons), right after classification and before
`LoadCrewAndGroupState`. It runs inside `OnLoad` because the scene that held ghosts and
index-keyed state has been destroyed by then (the same moment the existing cold path already
runs `ClearCommittedInternal` with notifications); the main-menu transition only marks the next
load Cold, as today. The helper:

- clears the pending-tree slot and the saved-pending slot WITHOUT running a discard: the tree
  stashed at the F9 scene exit is the abandoned future, not a player Discard, so no
  `TreeDiscardPurge`, no events purge, no `treesDiscardedThisSession` entry, and its files stay
  (4.4 keeps them resolvable for any save that references them);
- clears `treesDiscardedThisSession` (QL-R1 does not survive a load), the committed-tree restore
  attempt, `committedTreeStateLoaded` (so the save's committed trees are read, not kept from
  memory), the scene-entry and fresh-rollout fields, the rewind carries, the UT-adjust and
  suppression flags, `PendingCleanup*`, `PendingRevertPreExistingPids`;
- resets `GameStateStore` and `MilestoneStore` including their once-per-folder guards,
  `GameStateRecorder`'s pending milestones, contract dedup, recovery context and pending
  science, `LedgerOrchestrator`'s session statics (`initialized`, seed flags,
  `kscSequenceCounter`, the committed-future snapshot, the strategy debit baseline, the pending
  recovery funds and consumed keys, `RecoveryPayoutContextStore`), `KspStatePatcher`'s
  `lastPatched*` and session latches, `PlaybackScopeTracker`, the route escrow;
- clears the `ParsekScenario` session statics `vesselSwitchPending` / frame,
  `currentFlightLaunchUT`, `ScheduleActiveTreeRestoreOnFlightReady`,
  `pendingActiveTreeResumeRewindSave`, `pendingFreshRecordingAfterDiscard`,
  `mergeDialogPending`, `budgetDeductionApplied`, `lastSceneChangeRequestedUT`;
- drops `DiscardReFlyLoadIntent` and the quickload resume context. `InSessionStagedStateHandoff`
  is not touched here: step A already drops it on every cold load (reason `cold-load`).

It does NOT null the committed-list notification events (the batch reset does; the new scene's
controllers subscribe to them) and does not touch settings.

A pure inventory gate (xUnit) lists every `static` field in `RecordingStore`, `GameStateStore`,
`MilestoneStore`, `GameStateRecorder`, `LedgerOrchestrator`, `KspStatePatcher` and
`ParsekScenario` by reflection and requires each to be either reset by the helper (proved by a
set-then-reset-then-read cell) or named in an allowlist with a one-line reason (settings,
caches keyed by content, log rate limiters). A new static then fails the suite until someone
decides which side it is on.

### 4.3 The save carries its game state

`OnSave` adds a `GAME_STATE` child to the Parsek scenario node holding the same nodes it writes to
the external files, built once and used for both:

```
GAME_STATE
{
    LEDGER { ... }              // Ledger.BuildSaveNode: version, recordingSchemaGeneration,
                                // GAME_ACTION*, the pre-ledger progress seed, RetiredRouteCargo
    PARSEK_GAME_STATE { ... }   // GameStateStore: GAME_STATE_EVENT*, CONTRACT_SNAPSHOT*,
                                // SCIENCE_SUBJECTS, with the same restore-attempt filter
                                // as the file (#866)
    PARSEK_MILESTONES { ... }   // MilestoneStore, after the OnSave flush
    BASELINES { PARSEK_BASELINE { ... }* }
    PENDING_SCIENCE { SUBJECT { ... }* }   // GameStateRecorder.PendingScienceSubjects
    ATTEMPT_EVENTS { attemptTreeId = <id>  attemptArmReason = <reason>  GAME_STATE_EVENT* }   // see below
}
```

**Attempt events.** While a committed-tree restore attempt is live (a switch or Fly segment that
reuses committed recording ids), the event writer drops that attempt's same-id events from the
file and the milestone flush is deferred (#866, `ShouldDeferPendingEventMilestoneFlushForSave`):
they stay memory-only until the player merges, so a cold load can never make an unmerged
attempt's events durable. An F5 inside such an attempt followed by an F9 must bring those
events back, because the save's active tree IS that attempt. `ATTEMPT_EVENTS` therefore carries
exactly the events the filter removed, tagged with the attempt's tree id and arm reason; it is
written at every `OnSave` while an attempt is live, also when no event was held back, so the
save always names the attempt. On load they are restored to the in-memory event list (never to
the file, never into a milestone) only when the restored active tree is that same attempt and
the attempt is armed again for it; otherwise they are dropped with a Verbose line. The #866
invariant is unchanged: the events become durable only through the attempt's merge. The node
also carries the contract snapshots of the held-back contract accepts. Unit cells pin both
branches.

VERIFY V8 outcome (Phase 2, open question 3 closed): the cold active-tree restore re-arms the
attempt. In `TryRestoreActiveTreeNode`, on a Cold load whose save names an attempt for the
restored tree id, `TryRearmSaveAttemptForRestoredTree` arms it from the save's committed copy of
that tree (just loaded by `LoadRecordingTrees`), the same input the original arm in
`TryTakeCommittedTreeForSpawnedVesselRestore` had, so the recording ids and event cutoffs are
the original ones; the reason reads `cold load re-armed it from the save; armed by: <original>`
(not wrapped again on later cycles). For a re-armed attempt the restore then skips the splice
and the detach: both trees come from the same `OnSave` and the clone descends from the committed
copy, so the splice has nothing newer to bring in, while its same-id refresh would overwrite the
attempt's own edits (a resumed spawn's segment link) and mark the members dirty; and the
committed original stays committed, as in a live attempt, so a later Discard drops only the
clone and a Merge replaces the original. `ApplySaveAttemptEventsAfterActiveTreeRestore` then
restores the events. Nothing later on the cold path clears the attempt: the committed-copy rule
and its clear apply to in-session loads only, the session reset runs before the restore, the
reapers and sweeps do not read it, and `ParsekFlight.ResetFlightReadyState` runs on the new
scene's controller with no active tree, so its id-matched clear is a no-op; the flight-ready
Quickload restore pops the tree into the recorder as before. A save that names an attempt but
holds no committed copy of its tree does not re-arm (the attempt cannot be reconstructed) and
drops the events with a Verbose line. Limit: an attempt that resumed a committed recording in
place and had written to it is not in the save at all, because the overlap skip in
`PlanActiveTreeSidecarSaves` leaves the active-tree node out of every save while such a member
is dirty; that load restores no active tree, the flight-ready committed-tree restore arms a
fresh attempt, and the save's held-back events are dropped
(`ATTEMPT-EVENTS-DROPPED-WHEN-RESUMED-IN-PLACE`). Lane QL-13 (to add): inside a spawned-vessel resume
that continues in a new segment, earn something, F5, F9, merge: the earning is in the ledger
once.

- **Load.** The cold branch reads each store from `GAME_STATE` when present (replacing
  `LoadExternalFiles`' reads: `GameStateStore.LoadEventFile`, `LoadBaselines`,
  `MilestoneStore.LoadMilestoneFile`, `LedgerOrchestrator.OnLoad` -> `Ledger.LoadFromFile`), with
  the same version and schema-generation gates the file readers apply. A save without the node
  (written by an older build, or a fixture built by a tool) falls back to the files, which is
  today's behavior.
- **The files stay.** `Parsek/GameState/*` are still written at every `OnSave` as a mirror of the
  latest save, for the offline analyzer, the harness parsers and the fixture builders that read
  them. Nothing in the mod reads them when the node exists.
- **Size.** Measured over the 41 committed fixtures that carry GameState files: 0.3% to 8% of
  the save file (the largest, `C1Career`, adds about 83 KB to a 994 KB save). A ledger row is
  about 230 bytes, a baseline 3 to 5 KB.
- **Rewind point and rewind saves** (`Parsek/RewindPoints/*.sfs`, `Parsek/Saves/*.sfs`) carry the
  node too, because `OnSave` writes it into every save. Rewinds and Re-Fly loads keep reading
  memory (D2); they ignore it.
- This is a scenario-node key, not a recording shape change: the recording schema generation
  does not move.

With this node, the Cold x AbandonedFutureEvents / AbandonedFutureLedgerRows / Milestones /
Routes known gaps (`COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE`) close: the save's
rows are the rows of its moment.

### 4.4 Exact recording data: content hashes and retained versions

**Hashes in the save.** Each `RECORDING` node gains up to three values, written when known:

```
precHash = <16 hex>       // hash of <id>.prec bytes as last written or loaded
vesselHash = <16 hex>     // <id>_vessel.craft
ghostHash = <16 hex>      // <id>_ghost.craft
```

The hash is the first 64 bits of SHA-1 over the raw file bytes (for the two `.craft` kinds, the
PSN0 bytes before `SnapshotSidecarCodec.TryLoad` decodes them; for the `.prec`, the bytes of the
full read, not the header probe). The writer hashes the bytes it just produced; the reader hashes
the bytes it read. It is identity, not security. The existing `sidecarEpoch` stays exactly as it
is: logistics route sources capture it as proof of source (`RouteStore.RevalidateSources`), so
its meaning must not change. The readable `.txt` mirrors are not hashed or versioned (never read
back), and neither is the `.pann` cache, which is handled under "Loads" below.

**A hash is never laundered.** Memory holds, per recording and kind, the hash of the bytes it
actually holds: the hash written, or the hash read. A recording whose hydration failed (missing,
unresolved or stale sidecar) keeps the hash the save NAMED, not the hash of whatever head is on
disk, so the next `OnSave` writes the same name again and a later load can still find the right
version. Computing a missing hash from the head (a save from an older build) happens only for a
recording that hydrated from that head.

**Writes retain what they replace.** Every sidecar write goes through
`SidecarFileCommitBatch.ApplyStagedSidecarChanges`, which today moves the replaced head to a
`.bak.<guid>` backup (the `File.Replace` backup argument, or `File.Move` for a delete) and deletes
the backup after a successful batch (`CleanupCommittedBackups`). For the three hashed kinds:

1. The backup path becomes `_versions/<id><suffix>.incoming.<guid>`: same directory tree, so the
   replace is still a rename, and a rollback restores from it exactly as today.
2. After the batch commits, each backup is hashed from its own bytes (always; memory's idea of
   the head's hash is not trusted here, because a head can be rewritten under the same epoch,
   RecordingStore.Optimization.cs:442) and renamed to `_versions/<id>.<hash><suffix>`.
3. If that target already exists, it holds the same bytes (same id, same hash), so the incoming
   backup is deleted instead. If the hash equals the new head's hash (a byte-identical rewrite),
   the backup is deleted too.
4. A failure in steps 2-3 leaves the `.incoming.` file in place, logged; the next collection
   hashes and files it (4.5). Nothing is deleted on an error path.

A `DeleteExisting` change (a recording deleted by a discard, a merge-journal rollback, an
optimizer merge) is a retention too: the deleted head goes through the same steps.
`RecordingStore.DeleteRecordingFiles` therefore deletes only the non-hashed kinds (`.pann`,
`.txt` mirrors) and routes the three hashed kinds through a batch.

**Loads resolve the version the save names, on Cold loads only.** Version resolution and promote
run only inside a Cold `OnLoad` (every cold reason: boot, main menu, folder change, save file,
batch), where memory was just reset and the save is the only authority. Every other load kind
(scene change, revert, rewind, Re-Fly start, Discard Re-fly) keeps memory's recordings and reads
any head it needs with today's epoch rule, unchanged: those loads never replace memory with the
save, so the head memory last wrote is the right file. The lazy
`TryHydrateVesselSnapshotFromSidecar` follows the kind of load that loaded the recording (a flag
on the recording set by the cold hydration).

On a Cold load, `RecordingSidecarStore.LoadRecordingFiles` resolves each hashed path before
reading:

1. The save names no hash (older build): read the head with today's epoch rule
   (`ShouldSkipStaleSidecar`).
2. The head's bytes hash to the named hash: read the head.
3. Otherwise `_versions/<id>.<hash><suffix>` exists: read it. Then promote it to head when exactly
   one node of the loaded save names this recording id (a recording named by both the active and
   a committed tree, or by two pending slots, is read from the version and not promoted, logged).
   A promote is one commit batch over all of the recording's version-sourced kinds: each version
   is copied to a staged file, the copy replaces the head, and the current head is hashed and
   moved to `_versions/<id>.<headHash><suffix>` (deleted instead when that target exists,
   identical bytes). Only after every kind committed are the version files deleted, so a failure
   anywhere, the batch's own rollback included, never consumes a version. Every promoted head
   again matches the save the game is running from, so the offline tools read the right file. A
   promote that fails leaves the recording reading from the version path, logged.
4. Neither exists: the same failure today's stale or missing sidecar produces
   (`stale-sidecar-epoch` / `trajectory-missing`), with a reason naming the hash.

When a hash is present it replaces the epoch comparison: a matching hash is the stronger proof,
and it also catches the optimizer's same-epoch rewrite window that the epoch rule accepts
(RecordingStore.Optimization.cs:442).

**The `.pann` cache.** The `.pann` is keyed by `SourceSidecarEpoch`, which a retained version can
share with the head (the same-epoch rewrite), so the epoch key cannot tell them apart. A recording
read from a version (step 3, promoted or not) deletes its `.pann` and recomputes it from the
trajectory it loaded, as a missing `.pann` is recomputed today.

### 4.5 Collection

Files are deleted only by `ParsekFileCollector.Run`. Today seven sites delete a file under
`Parsek/Recordings/`, `Parsek/RewindPoints/` or `Parsek/Saves/`; after this design each one either
moves to the collector or stops deleting:

| Site | Today | After |
|---|---|---|
| `RecordingStore.CleanOrphanFiles` (OrphanCleanup.cs:345) | quarantines heads of ids no loaded tree names | decision moves to the collector (reference set below) |
| `RecordingStore.DeleteRecordingFiles` | deletes all kinds | non-hashed kinds only; hashed kinds retained (4.4) |
| `SidecarFileCommitBatch.CleanupCommittedBackups` | deletes `.bak.<guid>` | hashed kinds filed into `_versions` (4.4); others as today |
| `RewindPointReaper.TryDeleteQuicksaveFile` (:249) | deletes a sealed point's file | unlists only; the collector deletes |
| `TreeDiscardPurge.TryDeleteQuicksaveFile` (TreeDiscardPurge.cs:381) | deletes a discarded tree's point files | unlists only; the collector deletes |
| `FlightRecorder.CleanupOrphanedRewindSave` (:5428) | deletes the replaced `parsek_rw_` save on a promotion | stops; the collector deletes when nothing names it |
| `InGameTestRunner` batch crash reconcile | restores the batch baseline | unchanged (test-only path, never reached by a player) |

A source grep gate (`scripts/grep-audit-parsek-file-deletes.ps1`, run by `GrepAuditTests`, with
the managed fallback on Linux) fails the suite on any `File.Delete` / `File.Move` /
`File.Replace` / `Directory.Delete` site in `Source/Parsek` that resolves a path under those three
directories outside an allowlist naming the collector, the commit batch and the test-only reconcile.

**What keeps a file alive.** The reference set is the union of:

- every referrer's Parsek scenario node: each recording's id and its three hashes (active,
  pending and committed trees alike), every rewind point id in `REWIND_POINTS`, and every
  `rewindSave` and `resumeRewindSave` file name;
- the in-memory state: the same items over memory's trees, pending tree and rewind point list,
  plus `pendingActiveTreeResumeRewindSave` and the live recorder's `RewindSaveFileName`.

`Parsek/RewindPoints/*.sfs` and `Parsek/Saves/*.sfs` are not referrers: rewinds and Re-Fly loads
read recordings from memory, never through the rewind file's own node (VERIFY V5 in the plan).
`Backup/*.sfs` are referrers so a player who restores one of KSP's backup quicksaves finds its
files. A `.sfs` with no `ParsekScenario` node is a valid referrer that names nothing.

**Nothing names any recording.** When the reference set holds no recording id at all (no referrer
and no memory recording) while heads exist on disk, the run touches nothing and Warns
(`reason=zero-known-ids`): that is the "load lost its tree state" fingerprint. Memory alone is no
test, since a load of a save made before any flight was recorded holds no recording while the
save names its heads.

**Unreadable referrers fail closed.** A referrer that cannot be opened (locked, I/O error) or
whose Parsek node cannot be scanned (truncated mid-node, unbalanced braces) stops the whole
collection: nothing is deleted on that run, one Warn names the file, and the next run tries
again. A save the player leaves broken therefore stops collection for good; files only
accumulate, which is the safe direction. The Warn is rate-limited per path so the log says it
once per process.

**What is collected.**

| File | Collected when |
|---|---|
| `_versions/<id>.<hash><suffix>` | no referrer and no memory recording names `(id, hash)` |
| `_versions/<id><suffix>.incoming.<guid>` | always filed first: hashed and renamed (4.4), then judged as above |
| head `<id><suffix>` (all kinds) | no referrer and no memory names `id`; moved to `_quarantine` as today, never deleted outright |
| `Parsek/RewindPoints/<rp>.sfs` | no referrer's and no memory's `REWIND_POINTS` lists `rp` |
| `Parsek/Saves/parsek_rw_<id>.sfs` | no referrer and no memory names it as `rewindSave` or `resumeRewindSave` |
| any other `Parsek/Saves/*.sfs` (`parsek_career_start.sfs`, unknown names) | never |

The rewind point reaper keeps deciding WHEN a point is done (all slots sealed); it removes the
point from memory's list and no longer deletes the file. The file goes when no save lists the
point any more. The same holds for the files of a discarded flight or a pruned Re-Fly fork: they
go when the last save that knew the flight is overwritten or deleted.

**When it runs.** At the end of every Cold `OnLoad`, after `LoadTimeSweep.Run` and
`RewindPointReaper.ReapOrphanedRPs` (cold branch ParsekScenario.cs:4986 / :5002, in-session
:4589 / :4618), so memory's lists are
final; and on in-session scene-change loads at the same point, at most once per 10 minutes of
real time. It never runs inside `OnSave` (KSP writes the save file after `OnSave` returns), during
a plain rewind, a Re-Fly start or a Discard Re-fly load, or while a merge journal is open.

**Cost.** The scan reads each referrer once per process and re-reads it only when its size or
modification time changes (an in-memory cache keyed by path). The reader is a line scanner that
tracks node depth inside the `SCENARIO` whose `name = ParsekScenario` and collects the four value
kinds above; it does not build a `ConfigNode`.

**Disk.** Retained versions accumulate between collections: one set per dirty recording per
`OnSave`, so mostly the live flight's growing trajectory. A save the player keeps for a long time
keeps its versions alive; that is the point of the design. `Parsek/RewindPoints/*.sfs` files of
sealed points now live as long as some save lists them.

### 4.6 Parsek stops writing the player's quicksave

`RecordingStore.RefreshQuicksaveAfterMerge` (the merge dialog's Merge,
MergeDialog.Commit.cs:151) and the quicksave half of `RefreshSaveAndQuicksaveAfterDiscard` (the
dialog's Discard, MergeDialog.ReFlyDiscard.cs:122) overwrite `quicksave.sfs` so an F9 could not
bring the merged or discarded flight back. Under the rule they move the player's F9 point to the
merge or discard moment. With exact restore an F9 to the real F5 moment is consistent, so both
stop writing `quicksave.sfs`; the discard keeps refreshing `persistent.sfs`, which is the game's
own current state. Owner decision 2026-10-20: stop (F9 brings back the player's own F5).

### 4.7 Where each piece of state comes from on a save-file load

Every row reads the save; the right column names the reader.

| State | Source and reader |
|---|---|
| Committed trees, recordings, metadata | save `RECORDING_TREE` nodes, `LoadRecordingTrees` |
| Recording bulk data | the hashed versions the save names (4.4) |
| Active and pending tree (resumed flight) | save, `TryRestoreActiveTreeNode(Cold)` / `TryRestorePendingTreeNode`; its sidecars are the F5 versions, so the resume trim has nothing after the save to cut |
| Ledger rows, progress seed, retired route cargo | `GAME_STATE/LEDGER` |
| Game-state events, contract snapshots, science subjects | `GAME_STATE/PARSEK_GAME_STATE` |
| Milestones and their replay indices | `GAME_STATE/PARSEK_MILESTONES` + `MILESTONE_STATE` |
| Baselines | `GAME_STATE/BASELINES` |
| Pending science | `GAME_STATE/PENDING_SCIENCE` |
| Supersede rows, retirements, tombstones, merge journal, Re-Fly marker, rewind points | save staging nodes (`LoadRewindStagingState`); the handoff is dropped |
| Crew replacements, kerbal slots | save (unchanged) |
| Groups, hidden groups, archive state | save (`LoadGroupHierarchy`) |
| Missions | save (`MissionStore.Load`) |
| Vessel-name decisions | save (`VesselNameStore.Load`) |
| Routes | save (`RouteStore.LoadRoutesFrom`) with the save's own ledger rows |
| Switch segment session, stock-action intent, test batch marker | save (unchanged) |
| Settings | `settings.cfg` (install-wide, unchanged) |

---

## 5. Scenario walk-throughs

Each scenario is a lane candidate; the plan names the lane.

**S1. F9 in flight, flight not committed.** F5 at UT 2000 in "Probe A" (recording R1, epoch 7,
`precHash=aa..`). Fly to UT 2600, F9. Scene exit stashes the live tree as pending Limbo. OnLoad:
intent armed, `Cold save-file`; the reset drops the pending slot. `TryRestoreActiveTreeNode(Cold)`
reads the save's tree; R1's head was rewritten at no save since F5 (no OnSave ran), so the head
hash equals `aa..`, and the restore reads it. The resume trim finds no point after UT 2000.
Ledger rows tagged to R1 after UT 2000 do not exist in `GAME_STATE`. Identical to a KSP restart
into the quicksave.

**S2. F9 in flight after a scene change.** As S1, but between F5 and F9 the player went to the
Tracking Station and back (two OnSaves). R1's head is now epoch 9, hash `cc..`; `_versions`
holds `R1.aa...prec` and `R1.bb...prec`. The restore resolves `aa..` from `_versions` and promotes
it. Exact. (Today: the committed-copy rule and the trim reconstruct it.)

**S3. Space Center F9 after committing another flight.** Section 1.4.

**S4. Re-Fly merged after the save.** F5 at UT 5000 during a Re-Fly session S of booster B
(marker in the save, provisional P NotCommitted in the active tree). Merge at UT 5600: supersede
rows, tombstones, HEAD/TIP split of the origin (origin O's head rewritten, hash changes), P
committed. F9. Cold: the save's staging nodes have no supersede rows or tombstones of S; O
resolves its pre-split version; the marker and P restore; `LoadTimeSweep` validates the marker,
and the session resumes unmerged with the ledger of UT 5000. The OQ-1 "resumed merged attempt"
machinery has nothing to do: the rows it prunes are not there.

**S5. Discard after the save.** F5 at UT 3000 in flight A, the player leaves, the merge dialog
Discard deletes A's recordings (heads move to `_versions`), F9. Cold; QL-R1's discarded-tree set
was reset; A's tree is in the save and resolves from `_versions`. A resumes as at UT 3000.

**S6. Two saves, main-menu loads.** "before.sfs" at UT 1000; commit flight B (recordings B1..B3);
"after.sfs" at UT 9000. Main menu, Load "before": B is not loaded; the collection sees "after.sfs"
naming B1..B3 and keeps their heads (today: quarantined). Main menu, Load "after": B loads intact.

**S7. Optimizer rewrite.** Committed recording C was split by the optimizer at a cold load after
the save "old.sfs" was written (C's head rewritten under the same epoch, new hash; a new
recording C' created). Load "old.sfs": C's hash names the pre-split version, retained when the
optimizer's flush replaced the head; it loads and is promoted; C' is not in "old.sfs" and is not
loaded. The optimizer may split C again at this load, which is its normal load-time work.

**S8. Rewind after the save, then F9.** F5 at UT 8000; Rewind to Launch of a recording to UT
1000 (D2: memory kept); fly and commit flight D at UT 1500; F9. Cold: the save at UT 8000 is
restored exactly; D is gone, the rewind is undone. Consistent: the player loaded a moment in which
neither existed.

**S9. Sealed rewind point after the save.** F5 at UT 4000 while rewind point RP1 has an open
slot. Seal the slot at UT 4100: the reaper unlists RP1, its file stays (quicksave.sfs lists it).
F9: RP1 and its open slot are back; the file is there. Without retention `LoadTimeSweep` would
find the file missing and conclude the slot (LoadTimeSweep.cs:302).

**S10. Scene change.** Flight to Space Center: OnSave, OnLoad with no intent: `InSession`, as
today. The auto-commit of the stashed flight runs as today.

---

## 6. Edge cases

1. **Revert to Launch.** `onGameStatePostLoad` fires, then `OnRevertToLaunchFlightState`; both
   are armed when `OnLoad` runs, the revert wins (`revertPending`), the load stays `InSession` /
   `StockRevert`. Unchanged.
2. **Load fails after the event.** Stock fires the event only after `LoadGameCfg` returned a
   compatible game, and requests a scene right after it. If no `OnLoad` follows (an exception in
   stock), the intent waits for the next `OnLoad`; it is cleared by the main-menu transition and
   ignored (with a Warn) when older than 120 s real time at consumption.
3. **F9 during a plain rewind's replay.** The rewind context is memory only; the save-file load
   resets it. The save is restored exactly.
4. **F9 during a live Re-Fly session to a save from before the session.** The save has no
   marker; the session's provisional and session rewind point are dropped from memory; their
   files stay until collected.
5. **F9 while the merge dialog is open.** The dialog belongs to the scene being destroyed; the
   reset clears `mergeDialogPending` and the pending tree.
6. **A committed-tree restore attempt (switch segment) live at F9.** Its events are memory only by
   design (#866 filter); memory's attempt is dropped by the reset. When the save itself was
   written inside an attempt, the cold restore re-arms that attempt and brings back its
   held-back events (4.3, V8 outcome).
7. **A save written by an older build.** No `GAME_STATE` node: files fallback. No hashes: epoch
   rule. No `saveStamp`: the canary stays silent. Behavior equals today's cold load, except that
   the collection now keeps files other saves reference.
8. **A save loaded by an older build after this one wrote it.** Unknown keys and the
   `GAME_STATE` node are ignored; heads are current. Old behavior.
9. **External deletion** of a retained version or a head the save names: the recording fails to
   hydrate exactly as a missing sidecar does today (`trajectory-missing`, tree drop rules
   unchanged), with the hash in the reason.
10. **Player copies a `.sfs` into the folder from elsewhere.** It becomes a referrer at the next
    collection; files it names that exist are kept.
11. **Player deletes a save in the load dialog.** Its references disappear; the next collection
    frees what only it held.
12. **Parsek's own save writes.** The calls on the live game (`SaveGame(name, folder, mode)`:
    rewind point quicksaves, rewind saves, the career start snapshot, the scene-exit and seal
    persistent refreshes) run `OnSave`, so the file carries `GAME_STATE`, the hashes and a fresh
    `saveStamp` like any save. The Discard Re-fly persistent rewrite
    (`RevertInterceptor.DiscardReFlyHandler`) calls `SaveGame(Game, ...)` on the loaded rewind
    point game, which copies each scenario's stored node verbatim and runs no `OnSave`; it first
    splices in the Parsek node `persistent.sfs` already held (the last live save), so the file
    carries that node's `GAME_STATE`, hashes and stamp unchanged. That is correct: it is the
    state the session last saved, and the load that follows is a `DiscardReFly`, not a save-file
    load. The harness `SaveGame(game, ...)` sites copy their stored nodes the same way.
13. **Two writers in one OnSave** (a recording rewritten twice in one session without a save in
    between): only the head that a save might reference needs retaining, but the design retains
    every replaced head; collection removes the unreferenced ones.
14. **Same bytes rewritten** (a byte-identical sidecar write): hash equal, nothing retained.

---

## 7. What doesn't change

- Scene changes, stock reverts, plain rewinds (D2), Re-Fly start and Retry, Discard Re-fly:
  same classification and branches as today.
- The recording schema generation, the `.prec` format, `sidecarEpoch` and its uses (route
  proof-of-source, the `.pann` cache key).
- The external `Parsek/GameState/*` files keep being written.
- No new player-facing UI. The only player-visible differences are the outcomes in 1.3.

---

## 8. Superseded rulings and machinery

For a save-file load the following become unreachable, because the load no longer keeps memory:

| Ruling or mechanism | Where | Status after Phase 2 |
|---|---|---|
| D2 applied to quickloads (QL-R2 "every other committed flight is kept") | rewind design, game-actions design, user guide 402-403 | reversed for save-file loads; rewinds keep D2 |
| QL-R1 (a discarded flight records again) | `TryDeclineDiscardedActiveTree`, user guide 404 | reversed: the flight resumes with its recording |
| OQ-2 resume reconcile (`TrimAndReconcileForQuickloadResume` retire halves) | ParsekScenario.Trim.cs:818 | no abandoned future reaches it on a save-file load; the trajectory trim stays as a guard |
| OQ-3 rewind point owner partition, step B, resumed-tree row rule | InSessionStagedStateHandoff, ParsekScenario.InSessionHandoff.cs | only scene changes and reverts reach the handoff |
| Committed-copy rule (`DecideCommittedCopyRestore`), stale-epoch keep, abandoned Re-Fly fork prune | ParsekScenario.HydrationRepair.cs, Trim.cs | unreachable on a save-file load |
| Untagged rows after the save (`RetireUntaggedRowsAfterSaveAtInSessionLoad`, `UNTAGGED_FUTURE_ROWS`) | ParsekScenario.Trim.cs:1337 | unreachable on a save-file load (the save's ledger is exact) |
| OQ-1 resumed-merged-attempt pruning (`PruneStagedRowsNamingAttempt` on a resumed attempt), `RepairSessionBeforeRetry` detached-tree restore | MergeDialog | the rows are never there; kept for fresh sessions (prune finds none) |
| `QuickloadFlight` load kind and its policy column | LoadReconcilePolicy | no longer produced for stock F9 |
| Names ruling 5 for in-session loads | design-vessel-and-mission-names.md 7.6 | reversed for save-file loads |

Phase 2 leaves this code in place and unreached (the policy table says so per cell); Phase 3
deletes what no remaining load kind reaches, each deletion proved by the classification gate
and the lane set. A scene-change load and a revert still use the handoff and the revert branch.

---

## 9. Backward compatibility

- Additive keys only (`GAME_STATE`, `precHash` / `vesselHash` / `ghostHash`, `saveStamp`). No
  generation bump (CLAUDE.md: purely additive keys are not a shape change).
- New build, old save: today's cold behavior per missing piece (6.7), plus the safer collection.
- Old build, new save: ignores the new keys; reads heads, which are current for the save the
  game last ran.
- No migration pass. The first `OnSave` of a session on the new build writes the node and the
  hashes for every recording it serializes that hydrated from its head (the hash is taken from
  the bytes hydration read, so it costs no extra read; a recording whose hydration failed writes
  back the hash its save named, or none).

---

## 10. Performance budget

| Cost | When | Size |
|---|---|---|
| Cold branch on F9 (eager sidecar hydration of every committed recording, optimizer pass, recalc) | every save-file load | the same as a main-menu Load today; measured by the existing `OnLoad: <ms>ms` line in the first lane, compared against the in-session F9 of the same save |
| Hashing on write | per sidecar write | one SHA-1 over bytes already in memory |
| Filing the replaced head | per hashed sidecar rewrite | one read + SHA-1 of the backup, one rename |
| Hashing on load | per sidecar read | one SHA-1 over bytes already read |
| Hashing heads not written this process | at hydration (the bytes are already read) | no extra read |
| Reference scan | each collection | changed referrers only; a line scan |
| `GAME_STATE` node | every OnSave and load | 0.3% - 8% of the save |

If the first lane shows F9 noticeably slower than today, a follow-up reuses memory recordings
whose id and three hashes equal the save's instead of re-reading their sidecars (a content-keyed
cache cannot be stale). Not part of this design until measured.

---

## 11. Diagnostic logging

- `[LoadPolicy] Load classified: ... coldReason=save-file intentAgeFrames=<n> nodeUT=<ut>`
  (the existing classification line gains the field).
- `[LoadPolicy] Save-file load intent armed: nodeUT=<ut> scene=<s>` (Info, once per event);
  `... intent dropped reason=<main-menu|stale|consumed-by-<kind>>` (Verbose).
- `[LoadPolicy] In-session load read a save this session did not just write: stamp=<s> lastWritten=<s>` (Warn, the canary).
- `[Scenario] Save-scoped session state reset: reason=<r> pendingTreeDropped=<bool> storesReset=<n>` (Info).
- `[Scenario] OnLoad game state from save node: ledgerRows=<n> events=<n> milestones=<n> baselines=<n> pendingScience=<n>` or `... from files (no GAME_STATE node)` (Info).
- `[RecordingStore] Sidecar version resolved: rec=<id> kind=<prec|vessel|ghost> hash=<h> source=<head|version> promoted=<bool>` (Verbose per recording; one Info summary per load with counts by source).
- `[RecordingStore] Sidecar retained: rec=<id> kind=<k> hash=<h> reason=<rewrite|delete> outcome=<filed|identical-exists|same-as-head|left-incoming>` (Verbose; batch summary per OnSave / flush).
- `[RecordingStore] Sidecar version read without promote: rec=<id> kind=<k> hash=<h> reason=<multiple-owners|promote-failed>` (Info), and `... annotation cache dropped: rec=<id> reason=version-read` (Verbose).
- `[FileCollector] Collection: referrers=<n> scanned=<n> cached=<n> versionsDeleted=<n> headsQuarantined=<n> rewindPointFilesDeleted=<n> rewindSavesDeleted=<n> bytesFreed=<n> ms=<n>` (Info), and `... skipped reason=<unreadable-referrer path=...|journal-open|rewind|...>` (Warn for unreadable, Verbose otherwise).
- `[MergeDialog] Quicksave not refreshed after <merge|discard> (the player's F5 point is kept)` (Verbose).

---

## 12. Test plan

**Unit (xUnit), each red before its fix:**

- `ClassifyEarly` truth table with `saveFileLoadPending` x `revertPending` x the existing inputs;
  the intent's arm / consume / stale / main-menu clear.
- The session-reset inventory gate (4.2) and one set-reset-read cell per reset static group.
- `GAME_STATE` round trip: OnSave node -> cold load stores, equal to the file round trip; the
  fallback when the node is absent; the restore-attempt event filter applied to the node.
- Hash resolution (pure over a temp directory): head match, version match and promote, both
  missing, no hash (epoch rule), promote failure, two owners in one save (read, no promote), a
  non-Cold load kind (epoch rule, no resolution), the `.pann` dropped on a version read, a failed
  hydration keeping the named hash (no laundering into the next save).
- Retention: a staged rewrite files the old head into `_versions` under the hash of its own bytes
  (also when memory's hash for it is wrong); an existing identical target drops the incoming
  file; a byte-identical rewrite retains nothing; `DeleteExisting` retains; rollback still
  restores the head; a filing failure leaves `.incoming.` for the collector.
- `ATTEMPT_EVENTS`: written for every live restore attempt (also with no event held back, with
  its tree id and arm reason); the cold restore re-arms the attempt from the save's committed
  copy and keeps that copy committed; restored only when the restored active tree is that
  attempt; dropped when the save holds no committed copy; never written to the events file or
  flushed to a milestone.
- The canary: silent on `StockRevert`, Warn on a foreign stamp for `InSessionOther`.
- The harness `LoadGame` verb fires the event only with `playerLoad=true`.
- Reference scan over hand-written referrers (nested nodes, a `RECORDING` outside the Parsek
  scenario ignored, a truncated file fails closed, a `.sfs` without a Parsek node names nothing,
  `resumeRewindSave` counted) and the collection decision table (4.5), including
  `parsek_career_start.sfs` and an unknown `Parsek/Saves` name never collected.
- The delete-site grep gate (4.5) red on a planted delete under `Parsek/Recordings`.
- The quicksave refresh no longer writes `quicksave.sfs`.
- `LoadReconcilePolicyTests`: the Cold column's known-gap cells flip to Save; the QuickloadFlight
  column documented unreachable.

**Lanes (harness, flown on V's PC):** the plan lists them: new ones for S1/S2 (F9 after a scene
change), S3 (flight committed after the save), S4 (Re-Fly merged after the save, reshaping
QL-3 / QL-7), S5 (discard then F9, reshaping QL-6), S6 (two saves, data intact), renames and
groups after the save, a sealed rewind point after the save (S9), plus a re-flight of every
existing `QL-*` lane with its log contract moved from the old machinery's lines to the new ones.

---

## 13. Risks

| Risk | Mitigation |
|---|---|
| A static the reset misses carries state across an F9 | reflection inventory gate (4.2) |
| Collection deletes a file some save needs | referrer set includes every save file and memory; fail closed on any unreadable referrer; heads are quarantined, never deleted; only `parsek_rw_` rewind saves are ever collected; one grep gate owns every delete site; lane S6 |
| A retained version is filed under the wrong hash | the filing hashes the backup's own bytes, never memory's value |
| A version read serves stale annotations | the `.pann` is dropped and recomputed on every version read |
| Disk growth from retained versions | collection at every cold load and periodically in-session; versions are bounded by what saves reference |
| F9 becomes slower (cold branch) | measured in the first lane; content-keyed reuse as a follow-up |
| A stock or modded load path that does not fire `onGameStatePostLoad` keeps memory | the `saveStamp` canary Warn; the harness verb fires the event so lanes cover the path |
| A cold load in a scene that had live ghosts | the scene is destroyed before `OnLoad`; `ClearCommittedInternal` notifies as on every cold load; VERIFY V3 in the plan |
| Lanes and docs encode the old behavior | every `QL-*` lane re-flown; user guide, CHANGELOG and the three design docs updated in the commit that changes the behavior |

---

## 14. Open questions

1. ~~4.6, the quicksave refresh~~: decided 2026-10-20, Parsek stops writing quicksave.sfs.
2. Whether the Tracking Station Fly and the far vessel switch (`FlightDriver.StartAndFocusVessel`
   after a `persistent` save) ever fire `onGameStatePostLoad` in practice; decompiled code says
   no, the first lane log confirms (VERIFY V1).
3. ~~A save written inside a committed-tree restore attempt: does a save-file load re-arm it?~~
   Closed (Phase 2): the cold active-tree restore re-arms the attempt from the save's committed
   copy and its held-back events come back; see the V8 outcome in 4.3, including the
   resumed-in-place limit. Lane QL-13.
