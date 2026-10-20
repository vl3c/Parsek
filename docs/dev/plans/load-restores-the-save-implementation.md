# Load restores the save: implementation plan

Status: PLAN (2026-10-20), not started. Branch `claude/project-thread-hvhou8`.
Design authority: `docs/dev/design-load-restores-the-save.md` (wins over this plan where they
disagree; fix the plan, not the code, when they do). Todo: `QUICKLOAD-RESTORES-THE-SAVE-EXACTLY`;
also closes `COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE`.

Every `file:line` below was read at `beafb83` (main after PR #2151). Line numbers drift: each step
names the symbol, and every VERIFY is confirmed against the code before the step is written.

## Ground rules

- Red first: every behavior change starts with a failing xUnit cell in the same commit as the fix.
- Docs per commit (CLAUDE.md): CHANGELOG (player words, 3-5 lines), todo entries, the design-doc
  paragraphs marked superseded in design section 8 are rewritten in the commit that makes them
  true, `docs/dev/autotest-status.md` for lane changes, the user guide in Phase 2.
- Logging per design section 11; loops use the batch-counter convention.
- No new UI surface.
- Phase 1 changes no load's classification: it makes every save complete and every file safe.
  It can ship alone and already fixes the cold-load gaps. Phase 2 switches F9 and the pause-menu
  Load onto the cold path. Phase 3 deletes what nothing reaches any more.

## Phase 1: complete saves and safe files

### 1.1 The session reset (design 4.2)

1. Add `ParsekScenario.ResetSaveScopedSessionState(string reason)` (new partial file
   `ParsekScenario.SessionReset.cs`). Contents per design 4.2. Reuse the existing per-store
   resets where they are production-safe; where a store only has `ResetForTesting`, add a
   production `ResetForSaveLoad()` that does the same minus test-only hooks.
   - VERIFY: `RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard`
     (RecordingStore.cs:4920) nulls the committed-list notification events; the new helper must
     not. List what it clears and copy everything except those events and the committed lists
     themselves (the cold branch's `ClearCommittedInternal` removes those with notifications).
   - VERIFY: `LedgerOrchestrator.kscSequenceCounter` (LedgerOrchestrator.cs:91): what a loaded
     ledger expects of it. If later KSC rows must sort after loaded ones, set it from the loaded
     ledger's max `Sequence` at load instead of zero; a cell pins the order of a KSC row made
     after the load.
2. Call it at the top of every Cold `OnLoad`, right after `LogLoadClassification`, before
   `LoadCrewAndGroupState`. On a true boot it is a no-op on empty stores (one Info line).
3. The pending-tree drop is memory only (no `DiscardPendingTree`, no `TreeDiscardPurge`, no
   `MarkTreeDiscardedThisSession`). `DiscardStalePendingState` (ParsekScenario.cs:4653) then finds
   nothing on a save-file load; on a boot or main-menu load it behaves as today.
   - VERIFY: what `DiscardStalePendingState` protects on a main-menu Load of the same save (the
     tree stashed when the player quit the flight to the main menu IS the save's active tree).
     After 1.2 its file deletes become retentions, so dropping first is safe; confirm the restore
     then reads the save's version.
4. Tests: the reflection inventory gate (design 4.2) over the seven classes, with an allowlist file
   `scripts/session-reset-allowlist.txt` (one `Type.field reason` per line); one
   set-reset-read cell per reset group; a cell that a reset with a pending Limbo tree leaves its
   files on disk and does not add it to `treesDiscardedThisSession`.

### 1.2 Hashes and retained versions (design 4.4)

1. `Recording` gains `[NonSerialized]`-style runtime fields `PrecHash`, `VesselSnapshotHash`,
   `GhostSnapshotHash` (string, null = unknown) and `RecordingTreeRecordCodec` writes / reads
   `precHash`, `vesselHash`, `ghostHash` (sparse). They are copied by `Recording` clone sites
   that copy `SidecarEpoch` (grep the copy list at Recording.cs:1138).
2. `SidecarContentHash.Compute(byte[])` (pure): SHA-1, first 8 bytes, lower-case hex.
3. Write side: `RecordingSidecarStore` stages the bytes it writes; after the batch commits, set the
   recording's hashes from those bytes.
   - VERIFY: whether each writer has the bytes in memory or streams to the staged file. If it
     streams, hash the staged file before the commit (one read of a just-written file).
4. Retention: `SidecarFileCommitBatch` learns the kind and the old hash for each staged change
   (a new optional field on `StagedChange`: `RetainAsVersion` = target path under `_versions`).
   `CleanupCommittedBackups` moves those backups to their target instead of deleting them; a
   rollback restores from the backup as today. `DeleteExisting` changes for hashed kinds carry the
   same target.
   - VERIFY: every production path that deletes or replaces a hashed sidecar goes through this
     batch or through `RecordingStore.DeleteRecordingFiles` (RecordingStore.OrphanCleanup.cs:27).
     Known delete sites to check: `RemoveRecordingAt`, the switch-segment discard
     (RecordingStore.cs:3293), the Re-Fly discard (MergeDialog.ReFlyDiscard.cs:1244),
     `RewindInvoker.ReapPriorProvisional` (RewindInvoker.cs:2538), `DiscardSidecarReap`
     (DiscardSidecarReap.cs:251), the zombie sweep (LoadTimeSweep.cs:1528), the optimizer merge's
     absorbed recording (RecordingStore.Optimization.cs:98). A grep gate
     (`scripts/grep-audit-sidecar-deletes.ps1` + managed fallback, empty allowlist) fails the
     suite on any new `File.Delete` / `File.Move` over a `.prec` / `.craft` path outside the
     batch and `DeleteRecordingFiles`.
5. Read side: `RecordingPaths.ResolveSidecarForRead(rec, kind)` returns head or version per design
   4.4 steps 1-4; `LoadRecordingFiles` and `TryHydrateVesselSnapshotFromSidecar` call it. The
   promote is two `File.Move`s through the same batch so a failure rolls back. When a hash is
   present it replaces the epoch check (`ShouldSkipStaleSidecar` keeps its job for hashless
   saves).
   - VERIFY: `LoadRecordingFilesFromPathsInternal` reads the `.prec` once (hash those bytes, do
     not read twice); the `.craft` reads go through `ConfigNode.Load(path)`, so hash the file
     bytes before handing the path over.
6. OnSave: a recording serialized with an unknown hash for a kind whose head exists gets the
   head's hash computed once and cached on the recording.
7. Tests (temp directory, `RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting`-style
   override): resolution table; retention on rewrite, on delete, none on identical bytes; rollback;
   promote; hashless save reads with the epoch rule; the optimizer same-epoch rewrite is caught by
   the hash.

### 1.3 The save carries its game state (design 4.3)

1. Split each external writer into "build node" + "write node": `Ledger` (Ledger.cs:851),
   `GameStateStore.SaveEventFile` (GameStateStore.cs:1337), `MilestoneStore.SaveMilestoneFile`
   (MilestoneStore.cs:341), `GameStateStore.SaveBaseline` (GameStateStore.cs:1517). In
   `PersistGameStateAndMilestones` build each node once, write the file, and add the node under
   `GAME_STATE`. Add `PENDING_SCIENCE` from `GameStateRecorder.PendingScienceSubjects`
   (GameStateRecorder.cs:43).
2. Split each reader the same way ("parse node" + "read file") and add
   `LoadGameStateFromNodeOrFiles(ConfigNode scenarioNode)` used by the cold branch in place of
   `LoadExternalFiles`' four reads. The `parsek_rw_*.sfs` sweep and `budgetDeductionApplied`
   stay in `LoadExternalFiles`.
   - VERIFY: `LedgerOrchestrator.OnLoad` (LedgerOrchestrator.cs:5202) runs legacy repairs against
     `GameStateStore.Events` after `Ledger.LoadFromFile`; the node path must run them in the same
     order (events loaded first).
   - VERIFY: `LoadEventFile`'s once-per-folder guard and `MilestoneStore`'s are bypassed by the
     node path and reset by 1.1; nothing else relies on the guard.
3. Policy table: the Cold cells carrying `COLD-LOAD-INTO-OLDER-FLIGHT-SAVE-KEEPS-ABANDONED-FUTURE`
   (LoadReconcilePolicy.cs:627-631, 661-667) flip to `Save` with the reason naming the node; the
   Cold x Routes and Cold x Milestones reasons are updated; the cold `PendingScience` cell becomes
   `Save`. The todo entry is struck through with the fix.
4. Tests: node round trip equals the file round trip for each store (use the committed
   `C1Career` / `C2CareerPostFix` fixture files as inputs); fallback when the node is absent;
   the restore-attempt event filter applied to the node; the gates (version, generation) on the
   node path.

### 1.4 Collection (design 4.5)

1. `SaveReferenceScanner` (pure, `internal static`): `Scan(TextReader) -> SaveReferences`
   (recording ids with their hashes, rewind point ids, rewind save names; fails on unbalanced
   braces). Only values inside the `SCENARIO` node with `name = ParsekScenario`.
   - VERIFY: the exact key names: `recordingId` (RecordingTreeRecordCodec.cs:15), the hash keys
     (1.2), `rewindSave` (RecordingTreeRecordCodec.cs:297), the rewind point id key inside
     `REWIND_POINTS/POINT` (`RewindPoint.SaveInto`), and every node that holds `RECORDING`
     children (committed, active, pending trees: ParsekScenario.cs:1619, 2316, 2658).
2. `ParsekFileCollector.Run(string reason)`: referrers per design 4.5 (`saves/<folder>/*.sfs`,
   `saves/<folder>/Backup/*.sfs`), the per-process `(path, length, mtime)` cache, memory's
   references, the decision table, fail closed. Replaces the delete / quarantine half of
   `RecordingStore.CleanOrphanFiles` (keep its zero-known-ids refusal as a second guard) and the
   file half of `RewindPointReaper.TryDeleteQuicksaveFile` (RewindPointReaper.cs:249) and of the
   rewind-save delete in `DeleteRecordingFiles`.
   - VERIFY (V5): no rewind or Re-Fly path resolves recording bulk data through a
     `Parsek/RewindPoints/*.sfs` or `Parsek/Saves/*.sfs` node's own hashes. Read
     `RewindInvoker`'s temp-save epoch raise (RewindInvoker.cs:2974-3075): it raises the RP save's
     epochs to the current heads, so it must also write the current hashes (or strip them) into
     that temp save, or the cold-path resolution would pick the RP's old versions if that temp
     save were ever loaded cold.
   - VERIFY: `LoadTimeSweep`'s missing-RP-file branch (LoadTimeSweep.cs:302-367) and the reaper
     still agree once the reaper no longer deletes files.
3. Call sites: end of the Cold branch (where `CleanOrphanFiles` runs today,
   ParsekScenario.cs:4753, moved after the rewind points load if it is not already), and the
   in-session branch tail, rate-limited (10 min realtime), skipped during rewind / Re-Fly start /
   Discard Re-fly / an open journal.
4. Tests: scanner cells; decision table; fail-closed; cache invalidation on mtime; a two-save
   cell (S6) over a temp folder proving "after.sfs"'s heads survive a collection run with
   "before.sfs" loaded.

### 1.5 Phase 1 docs and lanes

- CHANGELOG: "Loading an older save no longer loses the recordings of a newer save in the same
  folder, and its career ledger matches that save."
- Design docs: game-actions design storage paragraph (ledger now in the save), flight-recorder
  design (sidecar versions and collection, a new subsection under section 19), user guide
  "Rewind points on disk" note if file lifetime changes what it reports.
- No new lane in Phase 1: the harness cannot drive a main-menu Load until Phase 2's verb change
  makes `LoadGame` a save-file load. The xUnit S6 cell covers it; the existing lanes must stay
  green (no classification changed).

## Phase 2: save-file loads take the cold path

1. `SaveFileLoadIntent` + the `onGameStatePostLoad` subscriber addon (design 4.1). Tests: arm,
   consume, stale (120 s), main-menu clear, revert precedence (`RevertDetector` armed after the
   event, as stock orders them).
2. `LoadReconcilePolicy.ClassifyEarly(..., saveFileLoadPending, revertPending)` and the
   `coldReason` field on the classification line. `OnLoad` sets `initialLoadDone = false` for a
   save-file load before the reset. Update `LoadReconcilePolicyTests` truth tables and
   `LoadReconcileWiringGateTests` (classification before `LoadCrewAndGroupState`, the reset right
   after it).
   - VERIFY (V3): on F9 in flight, `ParsekFlight.OnDestroy` and the ghost teardown ran before the
     new scene's `ParsekScenario.OnLoad` (log order in an existing QL lane's KSP.log), so the cold
     branch never runs with live ghosts of the old scene.
   - VERIFY (V7): on a Space Center F9 into a non-flight quicksave, stock writes the quicksave's
     game to `persistent.sfs` and `SpaceCenterMain.Start` reloads it, so the node `OnLoad` reads is
     the quicksave's.
3. `saveStamp` write in `OnSave` and the in-session canary Warn.
4. Harness `LoadGame` verb fires `GameEvents.onGameStatePostLoad.Fire(game.config)` after
   `LoadGame` succeeds, before the scene request (mirrors `QuickSaveLoad.onQuickloadPipelineFinished`).
   - VERIFY: no lane relies on a mid-run `LoadGame` keeping memory (grep specs for `LoadGame` after
     the first step); each such lane is listed in step 7.
5. The quicksave refresh (design 4.6), only if the owner confirms: `RefreshQuicksaveAfterMerge`
   is no longer called; `RefreshSaveAndQuicksaveAfterDiscard` refreshes `persistent.sfs` only.
   Update the QL-6 header note that relies on the discard rewriting `quicksave.sfs`.
6. Policy table: document the `QuickloadFlight` column as unreached by stock F9 (reason text);
   the `InSessionOther` reasons drop "F9 at the Space Center".
7. Lanes: re-fly every `QL-*` lane and move each log contract from the old machinery's lines to the
   new ones (classification `coldReason=save-file`, `Sidecar version resolved`, the save's ledger
   counts). Expected outcome changes: QL-3 / QL-7 (a Re-Fly merged after the save is simply not in
   the save), QL-6 (the discarded flight resumes with its recording), QL-4b / QL-4c (no
   committed-copy rule line). New lanes, each to "Authoring a fast lane" in `harness/README.md`:
   - QL-8 `quickload-drops-flight-committed-after-save` (S3): Space Center save, fly and commit
     a short hop, return, load the save: committed recordings and ledger rows back to the save's
     counts; the hop's sidecars still on disk.
   - QL-9 `load-older-save-keeps-newer-save-intact` (S6): named save A, commit a flight, named
     save B, `LoadGame` A, `LoadGame` B: B's recordings hydrate with their points.
   - QL-10 `quickload-restores-names-and-groups` : save, rename a vessel and move a recording to a
     group (seam verbs), load: the save's names and groups.
   - QL-11 `quickload-after-scene-change-resolves-version` (S2): F5 in flight, Tracking Station and
     back, F9: `Sidecar version resolved ... source=version promoted=True`, the trajectory ends
     at the F5 UT.
   - QL-12 `quickload-brings-back-sealed-rewind-point` (S9).
8. Docs: user guide lines 401-404 rewritten (one paragraph: "Loading a save, with F9 or the Load
   menu, puts Parsek back exactly as that save was ..."), CHANGELOG, the rewind design's line-137
   paragraph, the section 2.12 Rule 3 note, 6.14's OQ-1 paragraph and 6.16's handoff paragraph
   (save-file loads no longer reach them), the game-actions design QL-R1 / QL-R2 text, the names
   design 7.6 ruling 5 note, `autotest-status.md`.

## Phase 3: delete what nothing reaches

After Phase 2's lanes are green, one commit per mechanism in design section 8, each with the
classification gate proving no remaining load kind reaches it: the committed-copy rule, the
abandoned-fork prune, the resumed-tree row rule and RP owner partition of step B (scene changes
and reverts still use step A), `RetireUntaggedRowsAfterSaveAtInSessionLoad` and
`UNTAGGED_FUTURE_ROWS` (VERIFY first that a revert does not need it), the QL-R1 decline, the
resumed-merged-attempt branches of `PruneStagedRowsNamingAttempt` / `RepairSessionBeforeRetry`
(VERIFY no fresh-session path depends on them), and the `QuickloadFlight` kind. Each deletion
updates the policy table and its tests. Phase 3 is a separate PR.

## VERIFY index

V1 (done at design time): in KSP 1.12.5 `onGameStatePostLoad` is fired only by `QuickSaveLoad`
(quickload), `KSCPauseMenu` (load), `MainMenu` (load, resume), `FlightDriver.RevertToLaunch` /
`RevertToPrelaunch` and mission-mode code; Tracking Station Fly and `StartAndFocusVessel` do not
fire it. V2: every `OnSave` writes the sidecars of a dirty active-tree recording, so the F5 save
names the F5 bytes (EnsureRecordingFilesCurrentForSave, ParsekScenario.cs:2095). V3, V5, V7 above.
