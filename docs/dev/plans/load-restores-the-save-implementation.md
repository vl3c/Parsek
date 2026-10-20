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
   - The reset also clears `committedTreeStateLoaded` (the three existing resets at
     ParsekScenario.cs:5579 / :5736 / :8858 already do), so the cold branch reads the save's
     committed trees.
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
   recording's hashes from those bytes. Memory's hash is always the hash of bytes memory holds
   (written or read); a recording whose hydration failed keeps the hash its save named and
   writes it back unchanged (design 4.4 "never laundered").
   - VERIFY: whether each writer has the bytes in memory or streams to the staged file. If it
     streams, hash the staged file before the commit (one read of a just-written file).
4. Retention (design 4.4 steps 1-4): `StagedChange` gains `RetainKind` (prec / vessel / ghost /
   none). For a retained kind the backup path is `_versions/<id>.<ext>.incoming.<guid>` (created
   on demand; still a same-volume rename, so `File.Replace` and the rollback work as today).
   After the batch commits, `CleanupCommittedBackups` hashes each incoming backup from its own
   bytes, never from memory's value, and renames it to `_versions/<id>.<hash>.<ext>`; an existing
   target (identical bytes) or a hash equal to the new head's drops the incoming file instead. A
   failure leaves the `.incoming.` file, logged, for the collector to file. `DeleteExisting`
   changes for hashed kinds go through the same steps.
   - VERIFY: every production path that deletes or replaces a hashed sidecar goes through this
     batch or through `RecordingStore.DeleteRecordingFiles` (RecordingStore.OrphanCleanup.cs:27).
     Known delete sites to check: `RemoveRecordingAt`, the switch-segment discard
     (RecordingStore.cs:3293), the Re-Fly discard (MergeDialog.ReFlyDiscard.cs:1244),
     `RewindInvoker.ReapPriorProvisional` (RewindInvoker.cs:2538), `DiscardSidecarReap`
     (DiscardSidecarReap.cs:251), the zombie sweep (LoadTimeSweep.cs:1528), the optimizer merge's
     absorbed recording (RecordingStore.Optimization.cs:98). The grep gate
     `scripts/grep-audit-parsek-file-deletes.ps1` (+ managed fallback, run by `GrepAuditTests`)
     fails the suite on any `File.Delete` / `File.Move` / `File.Replace` / `Directory.Delete`
     whose path resolves under `Parsek/Recordings`, `Parsek/RewindPoints` or `Parsek/Saves`
     outside its allowlist (`scripts/parsek-file-deletes-allowlist.txt`: the commit batch, the
     collector, the test-only batch crash reconcile). It lands in 1.4 once the collector exists;
     in 1.2 it starts with the sidecar paths only.
5. Read side: `RecordingPaths.ResolveSidecarForRead(rec, kind, resolveVersions)` returns head or
   version per design 4.4 steps 1-4. `resolveVersions` is true only inside a Cold `OnLoad`; the
   recording remembers it (`HydratedFromSaveVersions`) so a later lazy
   `TryHydrateVesselSnapshotFromSidecar` follows the same rule. Every non-Cold load reads heads
   with the epoch rule, unchanged. When a hash is present on a Cold load it replaces the epoch
   check (`ShouldSkipStaleSidecar` keeps its job for hashless saves and for every other kind).
   - Promote only when exactly one node of the loaded save names the recording id (count ids
     over the committed, active and pending tree nodes before hydrating). Promote = hash the head,
     move it to `_versions/<id>.<headHash>.<ext>` (delete it when that target exists, identical
     bytes), move the version into the head position; both moves through one batch so a failure
     rolls back and the recording reads from the version path.
   - A recording read from a version (promoted or not) deletes its `.pann` and lets the
     annotation pass recompute it (the epoch key cannot tell a same-epoch version from the head).
   - VERIFY: `LoadRecordingFilesFromPathsInternal` opens the `.prec` twice (the header probe
     `TryProbeTrajectorySidecar`, then the full read): hash the bytes of the full read; resolve
     the path once, before the probe, so both opens read the same file. The `.craft` files are
     PSN0 binaries read through `SnapshotSidecarCodec.TryLoad`: hash the raw file bytes before
     decoding (read the bytes once and decode from memory if the codec allows it).
6. OnSave: a recording that hydrated from its head but has no hash for a kind (a save from an
   older build) gets the head's hash computed once and cached on the recording. A recording whose
   hydration failed writes back the hash its save named, or none.
7. Tests (temp directory, `RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting`-style
   override): resolution table; retention on rewrite, on delete, none on identical bytes; rollback;
   promote; two owners (read, no promote); a non-Cold load (epoch rule, no resolution); `.pann`
   dropped on a version read; incoming filing with a wrong memory hash, an identical existing
   target, a filing failure; a failed hydration keeps the named hash; hashless save reads with the
   epoch rule; the optimizer same-epoch rewrite is caught by the hash.

### 1.3 The save carries its game state (design 4.3)

1. Split each external writer into "build node" + "write node": `Ledger` (Ledger.cs:851),
   `GameStateStore.SaveEventFile` (GameStateStore.cs:1337), `MilestoneStore.SaveMilestoneFile`
   (MilestoneStore.cs:341), `GameStateStore.SaveBaseline` (GameStateStore.cs:1517). In
   `PersistGameStateAndMilestones` build each node once, write the file, and add the node under
   `GAME_STATE`. Add `PENDING_SCIENCE` from `GameStateRecorder.PendingScienceSubjects`
   (GameStateRecorder.cs:43), and `ATTEMPT_EVENTS` (design 4.3): exactly the events the #866
   filter drops from the file, tagged with the restore attempt's tree id, written only while an
   attempt is live.
   - VERIFY (V8): which call in `TryRestoreActiveTreeNode` re-arms the committed-tree restore
     attempt for the restored tree, and that it runs before the events are installed; the
     `ATTEMPT_EVENTS` restore goes right after it, memory only (never the file, never a
     milestone flush). A cell pins both branches (same attempt: restored; other or no attempt:
     dropped).
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
   the restore-attempt event filter applied to the node; `ATTEMPT_EVENTS` written and restored
   per its two branches; the gates (version, generation) on the node path.

### 1.4 Collection (design 4.5)

1. `SaveReferenceScanner` (pure, `internal static`): `Scan(TextReader) -> SaveReferences`
   (recording ids with their hashes, rewind point ids, `rewindSave` and `resumeRewindSave`
   names; fails on unbalanced braces; a file with no Parsek node names nothing). Only values inside the `SCENARIO` node with `name = ParsekScenario`.
   - VERIFY: the exact key names: `recordingId` (RecordingTreeRecordCodec.cs:15), the hash keys
     (1.2), `rewindSave` (RecordingTreeRecordCodec.cs:297), `resumeRewindSave`
     (ParsekScenario.cs:2325), the rewind point id key inside
     `REWIND_POINTS/POINT` (`RewindPoint.SaveInto`), and every node that holds `RECORDING`
     children (committed, active, pending trees: ParsekScenario.cs:1619, 2316, 2658).
2. `ParsekFileCollector.Run(string reason)`: referrers per design 4.5 (`saves/<folder>/*.sfs`,
   `saves/<folder>/Backup/*.sfs`), the per-process `(path, length, mtime)` cache, memory's
   references (including `pendingActiveTreeResumeRewindSave` and the live recorder's
   `RewindSaveFileName`), the decision table, fail closed. `Parsek/Saves` collects only
   `parsek_rw_*.sfs` names (never `parsek_career_start.sfs` or an unknown name). It files any
   leftover `_versions/*.incoming.*` first. It replaces every delete site in design 4.5's table:
   the delete / quarantine half of `RecordingStore.CleanOrphanFiles` (keep its zero-known-ids
   refusal as a second guard), the file half of `RewindPointReaper.TryDeleteQuicksaveFile`
   (RewindPointReaper.cs:249), `TreeDiscardPurge.TryDeleteQuicksaveFile` (TreeDiscardPurge.cs:381),
   `FlightRecorder.CleanupOrphanedRewindSave` (FlightRecorder.cs:5428) and the rewind-save
   delete in `DeleteRecordingFiles`. The grep gate from 1.2.4 extends to `Parsek/RewindPoints`
   and `Parsek/Saves` here.
   - VERIFY (V5): no rewind or Re-Fly path resolves recording bulk data through a
     `Parsek/RewindPoints/*.sfs` or `Parsek/Saves/*.sfs` node's own hashes. Read
     `RewindInvoker`'s temp-save epoch raise (RewindInvoker.cs:2974-3075): it raises the RP save's
     epochs to the current heads, so it must also write the current hashes (or strip them) into
     that temp save, or the cold-path resolution would pick the RP's old versions if that temp
     save were ever loaded cold.
   - VERIFY: `LoadTimeSweep`'s missing-RP-file branch (LoadTimeSweep.cs:302-367) and the reaper
     still agree once the reaper no longer deletes files.
3. Call sites: the Cold branch right after `LoadTimeSweep.Run` and
   `RewindPointReaper.ReapOrphanedRPs` (ParsekScenario.cs:4986 / :5002; `CleanOrphanFiles` at
   :4753 runs earlier today and moves here), and the in-session branch after its sweep and reaper
   (:4589 / :4618), rate-limited (10 min realtime). Skipped during rewind / Re-Fly start /
   Discard Re-fly / an open journal.
4. Tests: scanner cells; decision table (including the `Parsek/Saves` name rules); fail-closed
   on a locked and on a truncated referrer; cache invalidation on mtime; a two-save
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
   - The cold reconcile and recalc of a save-file load run at the save's `flightState` UT
     (design 4.1 "The clock"). VERIFY which UT the cold branch reads today (the loaded
     `Planetarium` UT after `Game.Load` vs a live read) and pin it with a wiring cell.
   - VERIFY (V3): on F9 in flight, `ParsekFlight.OnDestroy` and the ghost teardown ran before the
     new scene's `ParsekScenario.OnLoad` (log order in an existing QL lane's KSP.log), so the cold
     branch never runs with live ghosts of the old scene.
   - VERIFY (V7): on a Space Center F9 into a non-flight quicksave, stock writes the quicksave's
     game to `persistent.sfs` and `SpaceCenterMain.Start` reloads it, so the node `OnLoad` reads is
     the quicksave's.
3. `saveStamp` write in `OnSave` and the canary Warn, checked after `ClassifyRefined` for
   `InSessionOther` / `QuickloadFlight` only (a `StockRevert` reads an older snapshot by design).
4. Harness `LoadGame` verb gains the opt-in arg `playerLoad=true`; with it the verb fires
   `GameEvents.onGameStatePostLoad.Fire(game.config)` after `LoadGame` succeeds, before the scene
   request (mirrors `QuickSaveLoad.onQuickloadPipelineFinished`). Without it nothing changes, so
   lanes that use the verb to stage a fixture, pick a scene or set `focusPid` keep their
   semantics. The arg joins the verb's closed-arg table and the hlib mirror that pins it.
   - VERIFY: list the specs that `LoadGame` mid-run to mean "the player loaded"; those (the QL
     lanes) gain `playerLoad=true` in step 7, every other use stays as it is.
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
names the F5 bytes (EnsureRecordingFilesCurrentForSave, ParsekScenario.cs:2095). V3, V5, V7, V8
above.
