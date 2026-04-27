# Changelog

All notable changes to Parsek are documented here.

---

## 0.9.0

### Features

- **Rewind to Separation** — re-fly unfinished missions after multi-controllable splits. When a vessel stages, undocks, or EVAs into 2+ controllable pieces, Parsek captures a Rewind Point (a transient quicksave under `Parsek/RewindPoints/`). If any sibling ends badly — a destroyed booster, a dead kerbal EVA, a crashed lander — it appears in a read-only "Unfinished Flights" group with a Rewind button to replay the split moment. Merging the re-fly supersedes the retired sibling so the replayed attempt becomes the canonical playback; career state (contracts, milestones, facilities, strategies) is unchanged, but kerbal deaths in the retired attempt are reversed. A Revert-during-re-fly dialog offers Retry from Rewind Point / Discard Re-fly / Continue Flying. Crash recovery is journaled so an F5, quit, or disk interruption mid-merge can resume cleanly on next load.
- Revert to VAB/SPH during an active re-fly session now triggers the same 3-option dialog as Revert to Launch (was previously unhandled, bypassing the dialog entirely); Discard Re-fly returns the player to the editor as originally clicked.
- Discard Re-fly (was `Full Revert`) during an active re-fly session now preserves the tree's supersede relations, tombstones, and other Rewind Points; only the current re-fly session's artifacts and provisional recording are cleared, and the origin RP's quicksave is reloaded so the timeline winds back to the split UT. Launch click returns to the Space Center; VAB/SPH click returns to the clicked editor.
- New Settings > Diagnostics line shows live rewind-point disk usage (directory size + file count, refreshed every 10 seconds).

### Internals — Rewind to Separation rollout (reference)

- Phase 1 — data model (MergeState tri-state, RewindPoint + ChildSlot + RecordingSupersedeRelation + LedgerTombstone + ReFlySessionMarker + MergeJournal; scenario persistence + one-shot legacy migration).
- Phase 2 — EffectiveState helper (ERS / ELS with tombstone-only filter, SessionSuppressedSubtree forward-only closure, IsVisible / IsUnfinishedFlight / EffectiveRecordingId; StateVersion counters drive cache invalidation).
- Phase 3 — route existing raw-recording/ledger readers through ERS/ELS; `scripts/grep-audit-ers-els.ps1` CI gate with per-file allowlist.
- Phase 4 — rewind points captured at multi-controllable splits (`RewindPointAuthor.Begin`) with scene guard, warp-to-zero, deferred quicksave + safe-move.
- Phase 5 — Unfinished Flights virtual UI group (membership derived from ERS filtered by IsUnfinishedFlight; cannot hide / cannot be drop target).
- Phase 6 — rewind button end-to-end (`RewindInvoker`: 5-precondition gate, reconciliation bundle, quicksave copy to save-root, scene reload, post-load Restore + Strip + Activate + atomic provisional + marker write).
- Phase 7 — session-suppressed subtree hides superseded ancestors during re-fly; kerbal dual-residence carve-out lets live re-fly crew bypass reservation lock.
- Phase 8 — merge-time supersede relations + Immutable vs CommittedProvisional commit decision (`SupersedeCommit.CommitSupersede` + `TerminalKindClassifier`).
- Phase 9 — narrow v1 tombstone scope: only kerbal-death actions (plus bundled rep penalties within a 1s window) are retired on merge; career state stays sticky.
- Phase 10 — journaled staged commit (`MergeJournalOrchestrator` with 5 crash-recovery checkpoints; OnLoad finisher rolls back or drives-to-completion).
- Phase 11 — rewind-point reap after merge (`RewindPointReaper`); tree discard purges related RPs, supersede relations, and tombstones (`TreeDiscardPurge`).
- Phase 12 — Revert-during-re-fly dialog (Retry from Rewind Point / Discard Re-fly / Continue Flying) intercepts both `FlightDriver.RevertToLaunch` and `FlightDriver.RevertToPrelaunch` while a session is active. Discard Re-fly is session-scoped: it removes only the current attempt's provisional recording, promotes the origin RP to persistent, reloads the RP quicksave, and transitions to the Space Center (Launch) or VAB/SPH (Prelaunch) — other RPs, supersede relations, and tombstones in the tree are preserved.
- Phase 13 — load-time sweep (`LoadTimeSweep.Run`): validates marker's six durable fields, discards zombie NotCommitted provisionals + session-provisional RPs, warns on orphan supersede/tombstone rows.
- Phase 14 — polish + pre-release prep: disk-usage diagnostics line in Settings; rename persists + hide warns on Unfinished Flight rows; dialog copy polish (Merge + ReFlyRevert).
- Continued refactor-4 (Pass 2) with a behavior-neutral `SidecarFileCommitBatch` extraction: staged sidecar write/delete commit, rollback, and artifact cleanup now live in a focused helper while preserving the `[RecordingStore]` log tag, per-step rollback semantics from `#366`, sidecar epoch ordering, and `FilesDirty` mutation order.

### Documentation

- Added `docs/dev/observability-audit-2026-04-26.md` and `docs/dev/plan-observability-logging-visibility.md`, a full logging-coverage audit plus phased implementation plan covering spam controls, silent skip gates, persistence/rewind context gaps, UI/map diagnostics, and follow-up test-contract work needed to keep Parsek decisions reconstructable from `KSP.log`.

### Enhancements

- `#541` Main-window navigation labels now keep `Kerbals` count-free and shorten `Career State` to `Career`, leaving the detailed roster totals inside the destination windows instead of on the launch surface.
- Real Spawn Control window adds a `Rel Speed` column and tints the distance + relative-speed cells green when the FF button preconditions are met; ghosts within an outer 1 km / 50 m/s "show in list" envelope now stay listed (with a disabled FF button) so the player can see what is blocking the warp ("still too far", "closing too fast"). The FF-enable gate stays at 250 m / 2 m/s.
- `#542` Ghost watch range is now a fixed 300 km config default instead of a user-editable setting. Watch eligibility, watch-mode auto-exit, and watched-ghost full-fidelity checks now all read the shared config constant directly; the Settings window and persistence layer no longer expose or restore a mutable camera-cutoff override.
- `#543` `LoopTimeUnit.Auto` now uses one global launch queue instead of per-recording independent cadence. Flight playback, KSC playback, and watch-mode loop reconstruction all schedule Auto recordings from the shared queue order, so the global Auto interval is the gap between successive launches across the queue and each recording's relaunch cadence scales by queue length instead of clumping at each recording's own start UT.
- `#544` Rewind-to-launch now restores 15 seconds of pre-launch setup time instead of 10 before loading the stripped launch save, and the rewind UT coverage now reads the same shared launch lead-time constant as the production path.
- Added active and background recording-finalization cache refresh producers, including the 5-second dynamic refresh cadence, stable-coast digest skipping, background on-rails orbit summaries, and maneuver-node-safe tail generation.
- `#586` Ghost map `IsVesselRegistered` now reads from `FlightGlobals.PersistentVesselIds` instead of scanning the full `FlightGlobals.Vessels` list, dropping the per-`Set As Target` cost from O(N) to O(1).
- Runtime observability now adds compact recorder, map, Tracking Station, post-switch, game-action, and in-game test summaries without repeating stable no-op decisions.
- Recordings table renames the Unfinished Flights `Re-Fly` button to `Fly` so the rec window and Timeline window use the same glyph for the same action; the confirmation popup that follows the click also relabels its accept button from `Re-Fly` to `Fly` for consistency.
- Recordings table hides every loop control inside the virtual `Unfinished Flights` group — the per-row loop checkbox and period editor on member rows, plus the group-header aggregate loop toggle — and the Timeline window hides the `L` button on Unfinished Flight rows; loop remains editable from the recording's row in its real (mission) group.

### Bug Fixes

- Watching a ghost whose Relative-frame section is anchored to a vessel now in a totally different physical location (e.g. previously re-flown booster sitting in stable orbit while the player flies a fresh launch from the pad) no longer mis-positions the ghost hundreds of km away and trips the watch-mode camera cutoff. The relative-anchor resolver compares the live anchor's current pose to the recorded anchor's pose at the playback UT and, when they disagree by more than 250 m, prefers the recorded pose so the ghost stays on its recorded ground-relative trajectory.
- Re-Fly merge now supersedes destroyed sibling recordings that share the in-place origin's vessel across chain boundaries, so a destroyed run no longer lingers in the mission list and as a ghost after re-flying.
- Re-Fly ghosts whose relative-frame anchor matches the re-flown vessel — not just direct parents but any sibling chain too — now play back at their recorded ground-relative positions instead of locking onto the player's live pose, eliminating the upper-stage ghost map jumps and below-ground renders.
- Recorder no longer re-enters Relative mode against a stale anchor when an off-rails vessel comes back into focus, so post-rail recording sections capture clean Absolute trajectories instead of offsets to a vessel that's no longer there. When the stale-anchor downgrade fires at a vessel-switch resume, the boundary point is sourced from the v7 absolute shadow instead of the prior Relative section's anchor-local offset, so the new Absolute section's first sample is body-fixed coords as declared (no mis-framed seam).
- Parent-chain Re-Fly state-vector ProtoVessel suppression now also fires for the v7 absolute-shadow positioning branch (not just the legacy `relative` branch), keeping the doubled-ProtoVessel guard intact on v7 recordings during active Re-Fly.
- Tracking Station with a ghost selected no longer floods `KSP.log` — the chain-walk diagnostic lines (`HasGhostingTriggerEvents`, `Vessel PID claimed`, `WalkToLeaf`, `ResolveTermination`, `Chain built`, `Found claims`) now coalesce silently when the chain state is unchanged, so per-frame `RefreshGhostActionCache` calls stop multiplying into ~30 verbose lines per frame.
- Ghost vessels in physics range no longer flood `KSP.log` with one `Blocked GoOffRails` line per FixedUpdate — the Harmony prefix that keeps ghost ProtoVessels on rails now emits once per ghost PID and stays silent across the per-physics-tick retry storm.
- Recording finalizer no longer classifies an orbit as `Orbiting` when its periapsis is inside the body's atmosphere. A grazing low-Kerbin orbit (Pe ≈ 36 km) used to be locked in as a stable orbit at scene exit even though the trajectory will deorbit within a couple of orbits via drag; the recording now finalizes as `SubOrbital` so the ballistic-tail extrapolator can carry it to the actual destruction point.

- Recording-with-no-crew bug after re-launch: when a player relaunched a vessel whose original crew was still aboard a previous mission (forcing stand-in substitution in the editor), the FLIGHT-scene roster sweep deleted the just-substituted stand-ins before recording started, leaving the seats empty in the persisted vessel snapshot and the new recording's crew permanently empty. The displaced-unused branch now retains stand-ins that are currently seated on a live vessel.

- Tracking Station ghost-detail panel now matches the rest of the Parsek window family (opaque dark style, consistent title font / padding) and no longer flickers; the always-disabled stock `Fly` / `Delete` / `Recover` buttons are gone, leaving `Materialize` on its own row with a clear hint of what it does.
- Custom ghost map icons, ghost labels, and Parsek windows now hide while the Esc / pause overlay is open in flight, KSC, and Tracking Station, so they no longer punch through the pause menu.

- KSC ghost playback now honors v6 RELATIVE track sections, keeping rendezvous/docking ghosts attached to their anchor instead of drawing them underground.

- `#613` Re-Fly relative ghosts now reconstruct unsafe or unavailable anchors from the recorded ground-frame anchor trajectory before retiring. This keeps other vessels' paths ground-relative instead of locking to the live Re-Fly target.

- Re-Fly in-place continuations now freeze the active recording's pre-Re-Fly trajectory at invoke time and use that frozen copy for parent-chain relative-anchor playback, so upper-stage ghosts no longer replay at a constant offset from the newly flown booster/probe path.

- Re-Fly parent-chain relative sections now seed and bridge the v7 absolute-shadow trajectory at the RELATIVE boundary, preventing upper-stage ghosts from clamping forward to the first later shadow sample and appearing behind or far off the booster immediately after Fly.

- Re-Fly parent-chain absolute-shadow playback now forward-bridges under-sampled initial RELATIVE sections from the adjacent section's shadow frames, so the first post-separation ghost pose interpolates instead of freezing at the stale boundary point.

- Controlled split-child recordings now replace a stale decouple-callback seed with a live root-part sample only when the seed's velocity-propagated pose misses the loaded child root by more than 50 m, keeping Re-Fly upper-stage/booster playback aligned without treating normal high-speed travel during the coalescer window as drift.

- Re-Fly invocation now loads a slot-scrubbed temp copy of the RP save: before KSP parses the quicksave, every real vessel except the selected Re-Fly vessel is removed and the temp save's active vessel index is repointed to that slot. The original RP save and `persistent.sfs` stay untouched, and post-load strict stripping remains as a safety net.

- Re-Fly temp-save scrub now refreshes each recording's `.sfs` `sidecarEpoch` from the current `.prec` sidecar before load, so post-merge recording-tree mutations are not rejected as stale when an older Rewind Point quicksave is used. If the temp save already carries a newer epoch than the sidecar, the scrub keeps the newer `.sfs` value and reports the sidecar as skipped instead of downgrading.

- Re-Fly temp-save scrub failures now abort before `GamePersistence.LoadGame` instead of loading an unscrubbed quicksave, and the post-load strict-strip safety net now logs one compact summary for unmatched vessels instead of one WARN per vessel.

- Re-Fly in-place supersede now repairs a non-split target's missing terminal state from its captured scene-exit situation before writing supersede rows, covering the size=1 chain case where there is no optimizer-created tip to resolve.

- Re-Fly in-place supersede now falls back to contiguous optimizer split bounds when quickload/merge restore loses transient session tags, so stale destroyed tails are superseded by the new Re-Fly chain tip and the session marker clears instead of leaving broken mission rows.

- Re-Fly parent-chain suppression now reuses the composed committed-plus-pending tree search view across stable frames, avoiding per-frame list allocations in visual ghost playback and GhostMap state-vector updates while still invalidating when the committed tree list mutates.

- Re-Fly watch playback no longer treats unresolved, `NaN`, infinite, or negative ghost distances as in-range full-fidelity watched ghosts, preventing watch camera resets caused by stale relative-section transforms after rewind.

- Re-Fly ghost reentry FX no longer activates during hidden spawn priming at the stale KSC-surface pose; first-frame visual FX are suppressed until after the playback transform and ghost activation order are synchronized.

- Breakup coalescing now drops dead-on-arrival controlled children whenever their live vessel is already gone, even if a pre-captured snapshot exists, preventing single-point `Unknown` 0s rows from being committed after Re-Fly merge.

- Breakup coalescing now rejects unrelated stock asteroid/comet vessels discovered by the deferred joint-break global scan, preventing an AsteroidSpawner race from recording an asteroid as a controllable split child and creating duplicate, non-invokable Unfinished Flight rows.

- Unfinished Flight membership and commit-time crash promotion now require the recording to resolve to a real RewindPoint child slot, so debris siblings under the same branch point no longer surface as disabled `Fly` rows.

- `#613` Fresh loop and overlap-primary spawns that retire during relative-frame priming now suppress the first-spawn `RetargetToNewGhost` camera event, so watch mode never receives a pivot from the hidden origin-positioned ghost.

- Watch auto-follow now treats missing or partially built target ghosts as a deferred transfer, starts a retry hold, and only logs success after the transfer actually lands; watch range also has 300 km entry / 305 km exit hysteresis so near-cutoff transfers do not immediately pop the camera back out.

- `#615` Re-Fly post-spawn no longer churns crew stand-ins after the rescue path placed the original kerbal back on the spawned vessel; the guard skips the stand-in recreate only when the kerbal is currently on the SAME vessel where the rescue placed them (pid-scoped marker), so fresh reservations on the active player vessel still get their swap stand-in even if the kerbal happened to be rescued onto a different vessel earlier in the same session.

- Re-Fly slot precondition log no longer spams `KSP.log` with thousands of identical `slot-ok` lines per session; the on-change gate now uses a per-call-site decision cache that reliably suppresses repeats from the OnGUI draw loop.

- `#612` Boring-tail trim now actually fires for stable on-rails orbits. The orbital-shape match used exact float equality so rails/pack-unpack jitter blocked every real recording; the comparison now uses tolerances, angular fields (LAN, argument of periapsis, inclination) use a shortest-angle delta so a tail that crosses the 0/360 boundary still matches, and each `TrimBoringTail` skip reports which guard rejected.

- Optimizer splits and boring-tail trims now stamp exact explicit start/end UT bounds after mutating the payload, preserving a stable `.sfs` timing fallback if sidecar hydration is unavailable.

- Terminal-state classification now downgrades KSP `ORBITING` vessels whose captured periapsis is below the body surface or whose eccentricity is unbound to `SubOrbital`, so ballistic upper-stage arcs are no longer presented as stable orbiting final states.

- Re-Fly merge no longer leaves a clickable real upper stage alongside the playback ghost; non-leaf parent recordings inside the session-suppressed subtree now default to ghost-only in the merge dialog.

- Re-Fly in-place continuation merge now records supersede rows for sibling and parent recordings inside the closure, so a previously destroyed sibling like a Kerbal X Probe no longer lingers in the Recordings list after merge — including when the new flight crossed an atmo-exo boundary and the optimizer split it into chained segments.

- Raw-indexed Recordings table, KSC playback, and Tracking Station map-ghost surfaces now also honor explicit supersede relations, so retained old rows such as a probe-booster recording stay hidden outside ERS as well.

- Re-Fly in-place continuation merge now distinguishes new session-owned optimizer split segments from stale same-chain segments left by the original flight, so old booster exo tails are superseded by the new chain tip instead of being skipped as self-links.

- Optimizer atmo/exo splits now keep branch-point parent links on the half whose UT range actually owns the branch point, preventing a staging branch at UT 116 from being repointed to a later upper-stage segment that starts around UT 170.

- Persistence and Re-Fly rewind diagnostics now report save/load, sidecar, path, cleanup, and precondition failures with enough context to debug from `KSP.log`.

- `#611` Re-Fly doubled-vessel suppression now searches `RecordingStore.PendingTree` alongside `CommittedTrees` for both the active recording's PID lookup AND the parent-chain BFS walk, so the predicate fires during the load window when `TryRestoreActiveTreeNode`'s post-splice `RemoveCommittedTreeById` has just emptied the committed copy. Previously the gate bailed at the PID lookup with `not-suppressed-active-rec-pid-unknown` (because `CommittedRecordings` no longer held this tree's recordings) before reaching the parent-chain walk, and the doubled `Ghost: <name>` ProtoVessel got created. The success reason now also carries `activePidSource=search-tree:<id>` or `committed-recordings-flat-list` so the load-window vs steady-state distinction is auditable. The BFS walk returns a structured `walkTrace` (`active-not-found` / `active-has-no-parent` / `found-victim-in-parent-chain` / `exhausted-without-victim` plus visited-BP ids and parents-encountered ids) bubbled into both the suppressed and not-suppressed structured log lines so future "predicate didn't fire" diagnoses can read the BFS state from `KSP.log` alone — no more debugging by absence-of-line. A new `[GhostMap] create-state-vector-not-suppressed-during-refly` Verbose decision line fires when a Re-Fly session is active but the predicate declines to suppress, recording the rejection reason + walk trace.

- `#614` Re-Fly doubled-vessel suppression now also walks chain-predecessor links, not just BranchPoint parents, so optimizer-split chain ancestors of the active Re-Fly target (e.g. the root recording two hops up the chain) are correctly suppressed instead of getting a bogus state-vector map ghost.

- `#610` Re-Fly load no longer destroys other vessels' continued timelines. The quickload-resume tail trim previously ran tree-wide regardless of whether the resume was an F9 quickload (where every recording's post-cutoff data is genuinely stale) or a Re-Fly in-place continuation (where the splice has just restored other-vessel post-RP recordings as preserved forks). On Re-Fly the tree-wide path was clipping the capsule's exo-half to the cutoff UT and pruning its remaining sections as "future-only", so the original timeline of any vessel that wasn't the re-flown one disappeared the moment the new recorder started. A new pure-function `ChooseQuickloadTrimScope(treeId, marker, out reason)` now picks `ActiveRecOnly` when the live `ReFlySessionMarker` pins this tree (only the in-place continuation target's tail is trimmed, so the recorder can append fresh post-cutoff samples without colliding with the pre-cutoff timeline) and falls back to the existing `TreeWide` path otherwise. The chosen scope + reason are appended to the `Quickload resume prep:` Recorder log line so the branch is auditable from `KSP.log` alone.

- `#609` (spawner-side downstream of `#608`) Re-Fly-stripped capsule recordings no longer permanently abandon their end-of-recording vessel spawn when the original crew is `Missing`. The spawn-block check now treats reserved-but-Missing crew as spawnable (symmetric with `RemoveDeadCrewFromSnapshot`'s reserved-keep branch), and a new `RescueReservedMissingCrewInSnapshot` pre-spawn step flips them back to `Available` before the snapshot loads. The abandon WARN now reports a per-category breakdown (`strictlyDead=N missingNotReserved=N reservedMissing=N alive=N`) instead of just a name list.

- `#572` follow-up: scene-exit `FinalizeTreeRecordings` no longer clobbers the just-restored terminal state of a Re-Fly-stripped recording with a stale `Landed` inference. When `RestoreHydrationFailedRecordingsFromCommittedTree` repairs an active-tree record from the committed copy, the next finalize pass detects the missing live pid is a deliberate Re-Fly strip casualty (not a natural unload), skips the surface inference, and emits a structured `[Flight]` `FinalizeTreeRecordings: skipping Landed/Splashed inference … repaired from committed tree this frame` log line; the existing orbit-then-land Landed-inference path is unchanged.

- `#601` Re-Fly load now preserves recording-tree mutations (like atmo/exo splits) that the merge ran AFTER the Rewind Point's quicksave was authored. The frozen `.sfs` only knows the pre-merge tree shape; `TryRestoreActiveTreeNode` now splices any post-RP recordings (and updated BranchPoint parent IDs) from the in-memory committed tree into the loaded tree before the committed copy is detached, AND refreshes any same-id recording that the merge mutated in place (truncated trajectory + moved terminal payload + reassigned child branch-point link), including the active recording — the post-split atmo half keeps the original id, so the active first half was the one most likely to stay stale. The active refresh runs in a recorder-state-preserving mode that keeps load-time mitigation flags (FilesDirty, SidecarLoadFailed, continuation-rollback bookkeeping) intact, since at splice time the recorder has not yet rebound to the active recording.

- `#605` Map-view `HasOrbitData(IPlaybackTrajectory)` no longer floods `KSP.log` with ~1678 identical `body=… sma=… result=True` lines per session. The diagnostic now emits once per state change keyed on `(recording, body, sma)` and surfaces a `| suppressed=N` counter on the next flip.

- `#606` Finalizer cache diagnostics now keep classification context while collapsing stable no-op refreshes.

- Phase 1 observability spam hygiene now keeps finalization, map, diagnostics, KSC playback, ledger, and sandbox patch logs useful without repeating stable no-op decisions.

- `#607` Re-Fly post-strip `Strip left N pre-existing vessel(s)` WARN now reports `vessels=N collidingNames=M` separately and re-surveys at warn time scoped to the (pid, name) pairs the stripper actually left alone, with belt-and-suspenders exclusion of the actively re-flown vessel, freshly stripped pids, and any GhostMap ProtoVessel — so the WARN can no longer be tripped by the active vessel, a ghost, or a same-name vessel from a parallel flight, and the structured payload now carries `leftAlonePidsAlive=N excludedSelected=N excludedStripped=N excludedGhostMap=N`.

- `#600` Stationary landed or splashed ghosts now stay visible above the 50x high-warp mesh-hide threshold. Moving ghosts and overlap clones still hide for performance, and FX/audio suppression is unchanged.

- `#585` follow-up: Re-Fly load no longer destroys the on-disk `.prec` of sibling tree recordings whose sidecar load was skipped by bug `#270`'s stale-epoch mitigation. Two layers of protection: `SaveActiveTreeIfAny` first attempts to repair hydration-failed records by copying trajectory data from the matching committed tree (`RestoreHydrationFailedRecordingsFromCommittedTree`), so the in-memory state is restored and the recording remains playable in-session; if no committed-tree donor is available, the save path then refuses to overwrite a recording whose `SidecarLoadFailed` flag is still set AND whose in-memory state has no trajectory points, orbit segments, track sections, snapshots, or part events, preserving the original `.prec` until either the recorder rebinds (which clears the flag) or an explicit deletion path runs. The 2026-04-25 playtest's launch recording (`22c28f04`) was being clobbered with `points=0 wroteVessel=False` on scene exit; a structured `SaveRecordingFiles: skipping write … preserving on-disk .prec` WARN reports each callee-side save-skip decision and `SaveActiveTreeIfAny: skipped empty sidecar overwrite` reports each caller-side skip.

- `#587` follow-up: Re-Fly post-supplement strip now also kills pre-existing vessels whose name matches a recording in the session-suppressed subtree, not just `Destroyed`-terminal recordings. The 2026-04-25 playtest left a non-Destroyed phantom in scene that the player saw as a clickable "second Kerbal X-shaped object". The kill-eligible-name set now unions Destroyed-terminal recordings with `EffectiveState.ComputeSessionSuppressedSubtree` membership, while still excluding the active Re-Fly target's own vessel name and respecting the `#573` protected-pid contract; a structured kill-summary VERBOSE log line breaks down the match counts.

- `#587` third facet: Re-Fly no longer creates a real "Ghost: \<name\>" vessel colocated with the player's active vessel. A parent recording mid-flight in a Relative-frame section anchored to the active Re-Fly target's pid now skips state-vector ProtoVessel creation; the in-physics-zone playback ghost (visuals, audio, parts) is unaffected.

- Map-vessel source-resolve and spawn-suppression diagnostics no longer storm `KSP.log` when the same per-frame decision repeats every tick. Stable `(source, reason)` tuples now emit once per recording and stay quiet until the decision flips, with the suppressed count surfaced as `| suppressed=N` on the next state change.

- In-game `SaveLoadTests.CurrentFormatTrajectorySidecarsProbeAsBinary` no longer flags legacy-migrated recordings whose binary `.prec` sidecar predates the v4 loop-interval semantic bump. The lag exception is exactly v3 sidecar with v4 recording (the documented metadata-only migration); any other lag, including v3-or-older sidecar paired with a v5 / v6 recording, still fails the assertion so genuinely stale binary trajectory data is caught.

- `#571` In-game regression `GhostMapCheckpointSourceLogResolvesWorldPosition` now matches the shipped resolver contract: an OrbitalCheckpoint section that coexists with its seed orbit segment resolves to `Segment` (the densified frames sample along the same Keplerian arc), not `StateVector`.

- `#526` Time-jump auto-record suppression now also reports synthetic-spawn-vessel situation flickers as "suppressing time-jump transient" (instead of as a generic non-active-vessel skip), so the in-game pad canaries reliably observe the protective branch firing during Real Spawn Control and Timeline FF jumps. Auto-record start behaviour for the real pad vessel is unchanged.

- `#525` Destroyed loop-cycle boundaries now emit exactly one `OnLoopCameraAction` event (the `ExplosionHoldStart`) instead of also emitting a redundant `RetargetToNewGhost` from the ghost-reuse step. The watch handler already ignored the second event while in explosion-hold state, so camera behaviour is unchanged; the contract clean-up unblocks the in-game terrain-clearance regression that pins the explosion anchor to the same terrain-clamped position the camera holds at.

- In-game test `Bug289.FinalizeReSnapshot_StableTerminal_LiveVessel_UpdatesSnapshotAndMarksDirty` now passes again: the stable-terminal re-snapshot Info log line emitted by `FinalizeIndividualRecording` carries the `[#289]` tag the test scans for.

- In-game test `CrewReservationTests.ReplacementsAreValid` no longer NREs in MAINMENU when no save is loaded; it now skips cleanly when `HighLogic.CurrentGame` is null, mirroring the sibling `ReservedCrewNotAssigned` guard.

- `#591` Missed-vessel-switch recovery no longer floods `KSP.log` with redundant recorder-state snapshots. Identical recovery frames now collapse into 5-second `suppressed=N` summaries while normal vessel-switch diagnostics are unchanged.

- Re-Fly post-load activation now holds timeline playback while `RewindInvokeContext` is still pending, so the selected re-fly vessel cannot briefly render as both the activated real vessel and its pre-marker timeline ghost.

- Re-Fly session marker / RewindPoint state now survives the stale-sidecar restore path seen in the `2026-04-25_2210_refly-bugs` playtest. `MarkerValidator` now accepts a marker whose `TreeId` resolves through `RecordingStore.PendingTree` (not just `CommittedTrees`), so the playtest's `21:59:57` `Marker invalid field=TreeId; cleared` event no longer fires when the active tree is still in pending-Limbo. `RewindPointReaper.ReapOrphanedRPs` now preserves any RP whose id matches `ActiveReFlySessionMarker.RewindPointId`, so the playtest's `22:07:14` `Marker invalid field=RewindPointId` event no longer fires after a reap pass eats the marker's own RP. (Active-tree sidecar overwrite + in-memory repair are described in the `#585` follow-up entry above.)

- `#571` Long on-rails OrbitalCheckpoint warp sections now get derived trajectory samples every 5 degrees of true anomaly, so ghost icons follow the checkpoint window instead of replaying one sparse Kepler segment. The representative 22 ks Kerbin warp adds 42 points and preserves them through format-v6 `.prec` round trips.

- `#576` PatchedConicSnapshot `solver unavailable` and the paired Extrapolator `patched-conic snapshot failed for ... with NullSolver; falling back to live orbit state` WARNs are now rate-limited per (vessel-name) and per (recording-id, failure-reason) respectively. The 2026-04-25 marker-validator-fix playtest emitted 146 of each — almost all from debris, EVA-kerbals, and probe-debris that have no patched-conic solver by design in stock KSP. Downstream NullSolver semantics (live-orbit fallback for the destroyed-vessel case) are unchanged; only the log-noise floor is trimmed.

- `#581` New "Playback hybrid breakdown" one-shot diagnostic WARN closes the gap between the existing #450 (heaviest spawn ≥ 15 ms) and #460 (mainLoop ≥ 10 ms with spawn < 1 ms) sub-breakdown latches. The 2026-04-25 playtest's only budget-exceeded frame was a hybrid 11.6 ms spike (mainLoop 7.51 ms + spawn 3.44 ms) that fit neither prior latch and produced no Phase-B attribution; the new latch reports per-bucket itemisation plus mainLoop/spawn percent-of-frame fractions on the next such gap-shaped breach.

- `#582` Format-v6 RELATIVE TrackSection position contract is now documented in `AGENTS.md` and `.claude/CLAUDE.md`, and pinned by regression tests so flat `Recording.Points` readers cannot silently misinterpret anchor-local metres as body-fixed lat/lon/alt.

- MergeTree now heals velocity-consistent Background-to-Active handoff gaps by inserting a shared boundary point, preventing Kerbal X-style ghost trajectory pops from section-authoritative merged recordings.

- `#584` Map-view state-vector ghosts now honour the originating track section's reference frame, so a ghost that traverses a Relative-frame docking/rendezvous segment stays attached to its anchor vessel instead of snapping to the body surface at a meaningless lat/lon. Ghost-map create / position / update / destroy paths now emit a single structured `[GhostMap]` decision line (action, source, branch, body, world position, anchor, segment / terminal-orbit / state-vector data, scene) so a future "ghost icon went weird in map mode" report can be reconstructed from the KSP.log alone.

- `#584` Flight-scene state-vector update path no longer thresholds a Relative-frame point's anchor-local dz as if it were geographic altitude, so a ghost in a docking/rendezvous Relative section is no longer wrongly removed and re-deferred (review follow-up). Source-resolve decision lines now carry the real recording index (`-1` sentinel when unknown) instead of misleadingly logging every entry as `idx=0`.

- `#583` Map-view state-vector ghost creation now also fires when the first map-visible UT lands inside a Relative-frame docking/rendezvous section, so a recording that starts already-relative gets a map vessel attached to its anchor instead of staying invisible until the trajectory leaves the section.

- `#578` Crew orphan-placement misses now distinguish a wrong active vessel from a full matching pod, so stand-ins stay available for a later correct-vessel retry without falling back to an unrelated seat.

- `#585` In-place continuation Re-Fly now resumes recording into the booster's recording instead of timing out the tree to Limbo, so the post-Re-Fly merge dialog renders the recording with real duration instead of `0s` `hasSnapshot=False`. The async-FLIGHT-load path now waits for `RewindInvokeContext` to clear before reading the marker, so the deferred marker write never races the restore coroutine; the marker swap also rebuilds the tree's `BackgroundMap` so the newly active recording is no longer tracked as both active and background.

- `#587` Re-Fly strip pass now also kills pre-existing debris vessels carried in the rewind quicksave whose name matches a Destroyed-terminal recording in the actively re-flown tree, so leftover prior-career debris no longer trips KSP-stock patched conics into a phantom Kerbin Encounter prediction and a 50x warp cap. The kill loop now snapshots its targets before iterating, so consecutive matching debris cannot be skipped when `Vessel.Die()` removes entries from `FlightGlobals.Vessels` mid-loop.

- Re-fly merge now supersedes every chain segment of an env-split crashed recording. Previously the closure walker followed `ChildBranchPointId` only, so an exo HEAD + in-atmo TIP chain produced by `RecordingOptimizer.SplitAtSection` left the TIP behind as an orphan "kerbal destroyed in atmo" row alongside the new "kerbal lived" provisional. Saves committed before this fix that already completed a chain-crossing crashed re-fly merge are not retroactively healed; affected players can `Discard` the orphan via the table.

- EVA splits now author a Rewind Point, so a destroyed EVA kerbal becomes an Unfinished Flight with a Re-Fly button. Previously `IsTrackableVessel` only recognised parts with `ModuleCommand`, so the kerbal didn't count as a controllable output, the split classified as single-controllable, and no RP was authored.

- Re-Fly session marker now survives the SPACECENTER round-trip that precedes the merge dialog when the active recording is a previously-promoted Unfinished Flight; previously the load-time validator wiped it and the merge fell through to the regular tree-merge path (no force-Immutable, no RP reap, no UF clear-out). The carve-out covers both `CommittedProvisional` and `Immutable` in-place origins — `IsUnfinishedFlight` accepts both, so the validator must too.

- Re-Fly confirmation dialog renamed to `Parsek - Finish Flight` with a plain-language prompt ("Do you want to fly this again?") and a `Re-Fly` accept button instead of `Rewind`.

- Unfinished Flight rows no longer appear twice (once as a top-level tree row, once inside the nested Unfinished Flights subgroup); they render only inside the Unfinished Flights group.

- Controllable split children whose vessel dies before the breakup window expires no longer produce a 0s "Unknown" recording in the table; the parent's BREAKUP branch point already records the split.

- Re-fly merge dialog body trimmed to a centered `<vessel> - <duration>` headline plus "Commit this re-flight attempt permanently to the timeline. This cannot be undone!".

- Regular merge dialog drops the spawnable=0 advisory; crashed / recovered recordings replaying as ghosts is the obvious outcome.

- Strip-killing the upper stage during re-fly no longer trips spawn-death respawn, so a duplicate upper-stage vessel doesn't materialise next to the booster.

- Patched-conic snapshots now keep the valid prefix of a chain when KSP's stock solver leaves a later patch with a null reference body, instead of discarding everything. Recordings now retain their predicted-tail orbit data through transient ascent solver hiccups, and the previous WARN tier downgrades to a single VERBOSE truncation note.

- Plain Rewind-to-Launch (R-button) now scopes `SpawnSuppressedByRewind` to the active/source recording stripped during rewind instead of marking the whole tree (#589). The #573 duplicate-source protection still blocks that same recording from respawning, while future same-tree terminal recordings (EVA kerbals, flags, landers, etc.) remain spawn-eligible when playback crosses their `endUT`; stale legacy whole-tree markers are cleared/ignored at the spawn gate with diagnostics.

- Merging an in-place re-fly now reaps the Rewind Point and seals the recording as Immutable, so it's promoted out of Unfinished Flights even if the re-flight crashed.

- Unfinished Flights rows now show a `Re-Fly` button (the action loads a staging Rewind Point — different from the legacy `R` / `FF` time-rewind on every other row).

- Timeline window now lists controllable staging splits as `Separation of Unfinished Flight: <vessel>` (with a `Fly` button) or `Separation: <vessel>` post-merge. Debris splits stay hidden.

- Re-fly invocation now points the session marker directly at the recording that will receive samples, eliminating the placeholder-and-redirect detour.

- Re-fly merges refuse supersede rows when the re-fly recording has no trajectory or terminal state, catching the placeholder-as-supersede-target class of bug at commit time.

- Rewind to Separation re-checks preconditions on dialog confirm, cancelling with a toast if state changed between show and click.

- Recordings table no longer draws duplicate rewind-to-launch `R` buttons on tree-branch rows; only the recording that owns the launch save renders one.

- Re-fly merges with a Limbo-restored origin recording no longer write a self-supersede row, and load-time sweep purges any such rows left from older saves.

- Rewind to Separation warns after Strip when a left-alone vessel shares a name with a tree recording, so players can tell a pre-existing orbital "Kerbal X" apart from the current flight's ghost.

- `#533` Timeline kerbal-hire rows now live in the Details tier instead of the Overview tier. Sandbox, Mission, and Science saves render hire rows as `Hire: <kerbal>` without a funds suffix because those modes have no funds ledger.

- Nearby-vessel switches now treat deliberate attitude-only alignment as a meaningful post-switch change, so docking-port alignment done with SAS, reaction wheels, or light RCS is no longer lost just because translation/orbit barely moved. New relative-frame recordings now store true anchor-local docking geometry, while older recordings keep replaying through the legacy path for compatibility.

- Contextual auto-record starts now show one notification: pad/runway launches, post-switch first-modification starts, and EVA-from-pad starts suppress the generic `Recording STARTED` toast when they post their own `(auto)` message.

- Deferred spawn queue waits outside the physics bubble now log a rate-limited queue summary instead of repeating the same kept/warp-ended pair every frame while the spawn remains queued.

- Unfinished-flight predicate and missed-vessel-switch fallback now rate-limit their per-frame diagnostic lines, so KSP.log is no longer dominated by hundreds of thousands of identical `[UnfinishedFlights]` decisions or repeated `recovering missed vessel switch` warnings during a normal session.

- `#592` Time-warp rate-change checkpoint logs now rate-limit per warp rate, so KSP's chatty `onTimeWarpRateChanged` GameEvent no longer re-emits ~3300 redundant `Time warp rate changed to 1.0x` / `CheckpointAllVessels` / `Active vessel orbit segments handled` lines during a single session of scene transitions and warp-to-here.

- `#593` Repeatable record milestones (`RecordsSpeed`, `RecordsAltitude`, `RecordsDistance`) and their funds/rep modules now rate-limit the `Milestone funds`, `Repeatable record milestone stays effective`, and `Milestone rep at UT` lines per stable action identity, so a steady recalc loop walking the same committed grant collapses to one line while two distinct grants of the same milestone in the same recording at different UT or reward still each log on their first walk.

- `#594` `KspStatePatcher.PatchMilestones` bare-Id fallback diagnostic now rate-limits per `(nodeId, qualifiedId)` pair so an old-format recording with bare body-specific milestone IDs only logs once per pair per window instead of on every recalc walk.

- `#595` `OrbitalCheckpoint point playback` and `Recorder Sample skipped` rate-limit windows widened from 1.0s and 2.0s to the default 5s, dropping per-section playback churn and stationary-recording skip lines from the hundreds per session into the tens.

- `#596` `KspStatePatcher.PatchFacilities` now gates the INFO summary on real game-state work (`patched + notFound > 0`); skipped-only steady states and the all-zero empty case both route through rate-limited Verbose, so a non-empty `FacilitiesModule` whose facilities are already at their target levels no longer floods INFO every recalc.

- Boring-end trim now clamps the displayed and playable end time to the trimmed trajectory instead of keeping the scene-exit time. Trim diagnostics also report `trimUT` and `lastInterestingUT`.
- Boring-end trim now tolerates normal landed physics jitter in idle tails while still rejecting meaningful movement. Skipped trims log the first divergent field to make future tolerance problems easier to diagnose.
- Resume-on-scene-enter screen toast (`Recording STARTED (resume)`) now appears after the flight UI is ready instead of being swallowed during scene load.
- `#567` Returning to a vessel that already has a committed recording now auto-resumes recording after save/load. The resumed recording can be committed as a real-spawned vessel again instead of falling back to ghost-only.
- `#521` Career State now keeps its cached view model until the next visible timeline boundary instead of rebuilding on every sub-frame `Planetarium` UT tick while the window is open. That removes the main-window flicker during Parsek UI interactions without leaving the banner or pending/current rows stale.
- `#529` Live `BackupVessel()` snapshots now normalize landed/splashed `ORBIT` nodes through the shared backup path instead of only one finalize call site. Stable-terminal persistence, limbo pre-capture, split/chain snapshots, and other live snapshot users all get the canonical surface tuple for the live body, the rewrite logs explicitly, and spawn validation still self-heals older same-body stale surface sidecars from endpoint or snapshot coordinates.
- `#526` Timeline FF and other time jumps no longer let the real pad vessel auto-start a bogus launch recording during the jump transient.
- `#527` Rewind follow-up post-rewind FLIGHT-load recalculations now rebuild career state at the current loaded UT instead of walking the full ledger. The later FLIGHT `OnLoad` pass no longer restores future funds/contracts immediately after rewind; those actions stay filtered until replay reaches their UT again. The cutoff-dispatch log now includes every decision input, the other deferred `ParsekScenario` recalcs were audited as intentional full-ledger non-rewind paths, and a manual-only live rewind canary now exercises the real load flow.
- `#530` Pending timeline ghost shells now seed their playback body metadata before a split lazy build finishes, so Timeline and Recordings `W` buttons no longer open in a false disabled state just because the snapshot build is still advancing across frames.
- `#532` `PatchScience` now holds back recent unmatched `RnDTechResearch` debits when KSP has already deducted science but the matching KSC `TechResearched` action has not landed in the ledger yet, so same-UT tech unlock bursts no longer momentarily refund science back into the pool.
- `#535` Tracking Station ghost creation now prefers the recording's currently visible orbit segment over any later terminal-orbit tuple, and it only falls back to terminal orbit after that recording has actually reached its own end UT. `KSP.log` now also records each source decision and splits `before-activation` / `before-terminal-orbit` skips out of the startup `noOrbit` bucket, so future-tip suppression is diagnosable instead of looking like generic missing orbit data.
- `#536` Tracking Station now switches chain visibility on the child-start boundary instead of on mere child existence: existing parent ghosts retire and new parent presence stays suppressed only after a child recording with a resolvable start actually becomes current, so the Kerbin-exit handoff no longer drops the active continuation or leaves the parent ghost lingering past the handoff.
- `#533` `ContractAccepted -> ContractAccept` conversion now preserves `contractType` on both the immediate KSC ledger path and the later commit-time conversion path. New captures write `type=` into the accepted-contract event detail, and conversion backfills older events from the stored contract snapshot when that detail field is absent.
- `#531` Destroyed recordings without a preserved vessel snapshot now diagnose as `vessel destroyed` instead of the misleading `no vessel snapshot`, so playback/rewind/KSC spawn suppression logs report the real terminal state.
- `#525` Flight ghost explosions now resolve a clearance-checked anchor before spawning FX or emitting the watch hold position. If a watched ghost root is momentarily below PQS terrain at destruction time, the explosion and the watch camera now use the same terrain-safe anchor instead of burying both effects at the raw root transform.
- `#534` Returning to a spawned chain-tip vessel after a FLIGHT->FLIGHT switch now restores the existing mission tree instead of stranding the continuation in a fresh tree.
- `#537` Tracking Station now runs the real-vessel end-of-recording handoff for eligible recordings instead of stopping at ghost ProtoVessels. Eligible orbital handoffs now materialize directly through `VesselSpawner` in Tracking Station, dedup against already-live real vessels, and remove terminal-orbit ghosts once the recording is already materialized.
- `#538` Atmospheric reentry fire now uses the emission-rate lerp as the primary density dial, doubling the fire particle range from `300-2000` to `600-4000` particles/sec while only lifting the particle cap from `1500` to `2000` so the denser stream has headroom without opening a full 2x peak-particle budget.
- `#545` Timeline milestone rows now squash same-moment duplicate entries for the same milestone into one richer entry, including near-UT copies inside the same 0.1s window and same-timestamp rows separated by another entry. The surviving row unions missing funds/rep/science reward legs while leaving genuinely conflicting reward values split instead of inventing a combined total. Timeline milestone labels now also show science rewards, reducing the remaining “looks double-counted” milestone presentation path from `#522`.
- `#546` Idle vessel switches now arm auto-record and start on the first meaningful physical modification.
- `#550` Real-vessel materialization now uses a shared source-vessel adoption guard before spawning from a recording snapshot. KSC end-of-recording spawn, Flight tree-leaf spawn, Flight end-of-recording spawn handoffs, and chain-tip spawns now adopt a surviving source PID instead of creating a duplicate real vessel at the same endpoint; the new VesselSpawner guard also layers defense-in-depth on Tracking Station's existing `ShouldSkipTrackingStationDuplicateSpawn` path, and the #226 replay/revert duplicate-spawn exception remains an explicit opt-in at its call site.
- `#568` Landed respawns now preserve their recorded orientation instead of loading with a double-applied surface rotation that could leave them tilted or on their side.
- `#569` Time jumps that cross ghost chain tips now keep the materialized tip recording attached to the spawned vessel PID, so later spawn tracking and watch handoff follow the real vessel instead of rediscovering it.
- `#565` Continued scene-enter resume replays no longer materialize an older endpoint as an intermediate rover before the continued recording reaches its final spawn.
- Spawn-path audit follow-ups now route the remaining KSC end-of-recording and chain-tip normal/blocked/walkback materialization paths through the shared resolved-state spawn flow, including subdivided walkback interpolation for blocked chain tips. Failed respawns and flag spawns also clean up any transient `ProtoVessel` inserted before `ProtoVessel.Load()` aborts, and scene-load tree-leaf spawns now use the same shared materialization helper.
- `#528` Launchpad science gathered before a flight starts no longer gets committed onto that later recording, and mixed tree/chain commits now keep science attached to the correct recording.
- A booster left behind during upper-stage time warp that KSP destroyed on reentry now correctly terminates as `Destroyed` and appears in `Unfinished Flights` with a working `Rewind` button, including when the booster's recording was split across atmo/exo chain segments.
- `Unfinished Flights` virtual group now nests under its owning mission's group instead of floating at the root of the Recordings Manager, and the legacy rewind-to-launch `R` button is suppressed on chain continuations of a rewindable booster chain.
- Clicking `Rewind` on an Unfinished Flight now correctly activates the target vessel after the Space Center→Flight scene load completes, instead of failing silently with "selected vessel not present on reload" and dropping the player onto the wrong vessel.
- `#504` Rewind-to-Separation unfinished-flight rows now preempt the legacy tree-root launch rewind in the normal Recordings Manager list as well as in the virtual "Unfinished Flights" group, so a staged child such as `Kerbal X Probe` invokes its Rewind Point slot and returns to FLIGHT with that vessel live instead of loading the parent launch save in Space Center.
- `#504` Rewind-to-Separation now preserves normal staging Rewind Points across the KSC/TrackingStation load that shows the merge dialog, promotes them to persistent once the tree is accepted, stamps crash-terminal RP children as `CommittedProvisional`, and lets those rows populate "Unfinished Flights"; a staged booster such as `Kerbal X Probe` no longer loses its group entry before merge.
- `#523` Strategy lifecycle SPACECENTER canaries now hydrate `Administration.Instance` by creating a hidden stock Administration canvas, re-check that hydration after warmup, and keep Activate/Deactivate assertions in the same frame as the stock strategy calls. This closes both the plain-KSC singleton timeout and the latest KSC batch race where the first canary observed `Activate()` succeed but `IsActive` had flipped false after a yield while the next canary timed out on a null `Administration.Instance` after hidden-canvas teardown.
- Scene-exit tree finalization now consumes recording-finalization caches before trajectory inference, preserving live-finalizer precedence while giving missing active and background vessels their cached synthetic terminal tails; rejected caches still fall through to inference and stale cache consumption now warns in logs.
- Background premature-end finalization now consumes recording-finalization caches for debris TTL, out-of-bubble/missing-vessel endings, and confirmed background destruction, capping destroyed predictions at the actual deletion UT before persisting the sidecar.

### Tests

- Added regressions for pending-tree marker validation, active-marker RP preservation during reap, Re-Fly pending-invocation timeline playback gating, and committed-tree repair of hydration-failed active-tree sidecars.
- Added observability guardrails that summarize retained logs and catch malformed Parsek log lines during validation.

- `#527` In-game `RewindToLaunch_PostRewindFlightLoad_KeepsFutureFundsAndContractsFiltered` now drives `CommitTreeFlight` to land its launch recording in the timeline before staging the rewind, replacing the earlier `StopRecording`-then-wait pattern that timed out because stop alone never commits.
- `#526` Added headless and isolated in-game coverage for the pad-vessel time-jump regression: the shared jump suppression now has explicit boundary tests, and the FLIGHT canary fast-forwards from a real pad vessel, asserts the suppression path fires, and verifies no new auto-recording starts.
- `#525` Added headless coverage for explosion-anchor body resolution and an in-game terrain/watch regression that drives the loop-explosion engine path and verifies the emitted watch hold anchor and loop-restart explosion payload both use the same terrain-clamped anchor.
- `#536` Added headless Tracking Station regressions covering future-child suppression timing, live parent-ghost retirement at child start, indeterminate child-start fail-open behavior, current orbit continuation ghost creation, and the atmospheric-marker continuation handoff.
- `#534` Added spawned chain-tip restore regressions covering committed-tree ownership, restorable-leaf filtering, multi-tree selection, and the throttled Update-time retry guard.
- `#537` Added headless Tracking Station spawn-policy coverage for orbital handoff eligibility, scene-entry-PID duplicate-real-vessel dedup after removing the stale bypass, ghost-chain suppression reasons, preserve-identity chain-tip decisions, and suppression of already-materialized map ghosts.
- `#538` Added deterministic headless coverage pinning the tuned `600-4000` reentry-fire emission range and `2000` cap, plus a live `ReentryFx` runtime regression that waits on elapsed realtime instead of a fixed frame count before asserting the emission rate can exceed the old `2000` particles/sec ceiling. The live runtime check still skips on non-atmospheric saves.
- `#539` Removed the last two permanently-skipped `GhostPlaybackEngineTests` placeholders from the shipped xUnit suite: `SpawnGhost_PrimesFreshGhostToCurrentPlaybackUT` now relies on an in-game replacement that seeds its own synthetic playback recording from the active-vessel snapshot instead of depending on save-local committed data, and the pending loop-cycle boundary case now has a dedicated runtime regression that drives `UpdatePlayback -> UpdateLoopingPlayback` on a `ghost == null` pending-build state while the headless `ReusePrimaryGhostAcrossCycle_NullGhost_AdvancesCycleWithoutEvents` helper keeps the pure cycle-advance invariant pinned.
- `#540` `Parsek.Tests` now builds cleanly without the remaining xUnit style warnings: `FormatCoroutineState_ReportsActiveAndIdleSlots` is a real `[Fact]`, and the Kerbals subitem-indent regressions now use xUnit `Assert.StartsWith(...)` while preserving the original `StringComparison.Ordinal` semantics instead of the old `Assert.True(text.StartsWith(..., StringComparison.Ordinal))` form.
- Added manual-only in-game coverage for the deferred FLIGHT `Merge to Timeline` commit path, a synthetic `Keep Vessel` playback-control canary that fast-forwards into playback and asserts the end-of-recording vessel spawn happens exactly once, a stock `Revert to Launch` canary that asserts the shipped soft-unstash / no-merge revert semantics, and two real `Space Center` exit canaries that drive the deferred merge-dialog `Merge to Timeline` and `Discard` branches end-to-end.
- `#535` Expanded headless `GhostMapPresenceTests` coverage for tracking-station future-tip suppression to assert the new source-decision log trail and the startup skip-summary buckets. No runtime test landed because this regression is resolved in the pure source-selection/logging layer.
- `#491` Archived live runtime evidence now covers both `SceneExitMerge` canaries: the stock `Space Center` exit discard branch clears the pending tree without a commit, and the merge branch commits the pending tree into `CommittedTrees` / `CommittedRecordings`.
- Added deterministic in-game `PartEventTiming` canaries that assert light-toggle and deployable-transform ghost playback flips exactly at their authored UT boundaries, and retained live bundles under `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-04-21_2008_finish-line-validation\` and `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-04-21_2042_live-collect-script\` now show both exported `FlightIntegrationTests.PartEventTiming_*` rows passing in `FLIGHT`.
- Added an explicit `Run All + Isolated` / `Run+` in-game test-runner mode that captures a temporary FLIGHT baseline save and quickloads it between selected destructive tests (`AutoRecord`, FLIGHT merge-dialog, watch-cleanup regression, `Keep Vessel`, and the `QuickloadResume` / `RevertFlow` canaries) while still leaving the `SceneExitMerge` stock-transition tests manual-only.
- `#493` The launch-backed `Quickload_MidRecording` isolated canary now records through the live-log observer again, so F5/F9 validation keeps writing the same KSP log evidence instead of swallowing those lines into a sink-only test hook.
- `#493` The launch-backed `Quickload_MidRecording` isolated canary now uses the runner's stronger StageManager gate, waits through transient-null `FlightInputHandler.state` until input stays stable, and waits for the first real trajectory point before asserting already-live recordings, reducing stage-race and first-sample flakes.
- `#486` Added runway quickload follow-up coverage clarifying the shipped `0.8.3` scope: restored trees still trim in place and resume the same recording id, while a quicksave made before liftoff can still finish as the normal short `surface` plus `atmo` phase split within that resumed recording.
- `#493` Retained April 22 live evidence under sibling-workspace bundle `../logs/2026-04-22_2118_validate-493-watch-cleanup-pass/` now closes the destructive `FLIGHT` isolated-batch gap on the quickload-hardened tree: `parsek-test-results.txt` records `FLIGHT captured=190 Passed=154 Failed=0 Skipped=36`, including passes for both `Quickload_MidRecording_ResumesSameActiveRecordingId` and `RunAllDuringWatch_DoesNotLeakSunLateUpdateNREs`, while `KSP.log` shows watch mode exiting before ghost teardown and contains no `Sun.LateUpdate` / `FlightGlobals.UpdateInformation` / `NullReferenceException` signatures. `SceneExitMerge` still remains manual-only because of stock post-run contamination.
- `#494` `pwsh -File scripts/test-coverage.ps1` is now validated end-to-end on the current tree: it restored and ran `Parsek.Tests` (`Passed: 7730, Skipped: 2, Total: 7732`) and emitted the first baseline Cobertura packet at `41.50%` line / `39.95%` branch / `56.28%` method coverage across `325` classes.
- `#496` Added headless coverage for the remaining thin IMGUI owners by extracting pure `TestRunnerPresentation`, `SettingsWindowPresentation`, `SpawnControlPresentation`, and `GroupPickerPresentation` helpers for test-runner labels/tooltips, Settings edit/default rules, Real Spawn Control sort/row-state decisions, and Group Picker selection/tree deltas.
- `#497` Added explicit ownership-style builder suites for `EngineFxBuilder` and `GhostVisualBuilder`: the new headless seams cover effect-group filtering, config-entry parsing, fallback rotation-mode decisions, ghost snapshot/root selection, prefab-name normalization, color-changer grouping, and stock explosion guard behavior. Follow-up coverage now also captures seam-level `EngineFx` logs for guard/fallback branches and pins the malformed-`localRotation` fallback path, while live Unity object construction remains covered by in-game runtime tests and true visual confirmation still depends on runtime/manual evidence.
- `#524` `TimelineWindowUITests` now pins the row-action width helper that all Timeline recording-row buttons use, so `W`, `FF`, `R`, and `L` stay aligned while `GoTo` remains intentionally wider for its label.
- `ParsekUITests` now pin the short main-window labels so `Kerbals` stays count-free and the launch-surface button text stays `Career` rather than drifting back to `Career State`.
- `#542` Added regression coverage pinning the fixed 300 km watch cutoff helper, the removal of the mutable `ParsekSettings` cutoff field, and the persistence-store cleanup that now only tracks the remaining sticky user-intent toggles.
- `#546` Added headless and runtime coverage for post-switch auto-record follow-up.
- `#550` Added headless source-vessel materialization guard coverage for adoption, no-mutation and replay-bypass cases, validated-spawn short-circuiting before snapshot validation, chain-tip adoption before collision/snapshot work, KSC spawn adoption, time-jump chain-tip bypass preservation, and committed-tree restore matching of adopted source vessels.
- `#532` Added headless coverage for the live KSC tech-unlock debit holdback, so the xUnit suite now pins both the unmatched-burst gap calculation and the `PatchScience` target adjustment that prevents temporary science refunds.
- `#504` Added headless coverage for Rewind-to-Separation row routing: RP-backed unfinished flights now resolve their child slot from normal rows, non-crashed children keep the legacy temporal controls, disabled slots block before a scene load, and the row-level RP route is pinned ahead of `RecordingStore.CanRewind`.
- Added headless coverage for the recording-finalization cache applier, including identity mismatches, stale-cache rejection, terminal-UT rollback rejection, predicted-tail trimming, authored-data preservation, surface metadata preservation, and terminal orbit stamping.
- Added headless coverage for recording-finalization cache refresh cadence, live-vessel surface/extrapolated/atmospheric-fallback producers, background on-rails cache production and cleanup, active-to-background cache adoption, and UI maneuver-node fallback behavior.
- Added headless scene-exit fallback coverage for live-finalizer precedence, missing-vessel cache application on leaf and active non-leaf recordings, stable-cache override guards, and background cache lookup by recording id.
- Added headless premature-end coverage for background cache application, deletion-UT trimming, confirmed-destruction cache guards, stable-cache missing-vessel classification, and non-scene active crash fallback.
- Added FLIGHT runtime canaries for recording-finalization cache application, deletion-UT trimming, stable background cache application, and active crash fallback, with log-line assertions on the consumer accept signatures.
- Added headless spawn-path audit coverage for `VesselSpawner` route selection and prepared-snapshot validation, plus subdivided walkback interpolation of UT, velocity, and rotation for blocked chain-tip recovery.

### Documentation

- `#526` Updated the auto-record manual checklist and todo entry for the shared time-jump pad-vessel regression and its visible suppression evidence.
- `#546` Updated the auto-record manual checklist and tracked the remaining `#534` gate in the todo doc.
- `#504` Documented the normal-row Rewind-to-Separation affordance so the design spec no longer implies that only the virtual group can invoke a Rewind Point.
- Added a recording-finalization cache manual checklist covering runtime canaries, atmospheric booster deletion, stable background orbiters, scene-exit mid-burns, maneuver-node ignoring, focused crashes, log sweeps, and cadence calibration.
- Updated the spawn audit design note and todo docs to mark the KSC, chain-tip, tree-leaf, and orphan-`ProtoVessel` follow-ups closed.

## 0.8.3

### Features

- `#563` Tracking Station now has a Parsek toolbar button and compact in-scene control surface. The panel exposes the Tracking Station ghost-visibility toggle, shared Recordings and Settings windows, and a small status readout for committed recordings, map ghosts, suppressed entries, and materialized vessels.
- Timeline window now has a `Rewind/FF` filter button that shows only recordings you can currently rewind to or fast-forward to.
- Timeline recording rows now expose the same `W` / `W*` watch control as the Recordings Manager, including disabled states when a ghost is unavailable and a clickable `W*` to stop watching.

### Enhancements

- Map View and Tracking Station custom ghost icons and unpinned hover labels now draw at 80% opacity by default; pinned labels and their icons return to 100%.
- Ghost vessel explosions in flight now use KSP's stock explosion effects and bundled audio, matching stock vessel destruction; KSC keeps the prior custom renderer since the stock system is flight-scene-only.
- Raised the per-recording concurrent-ghost hard cap from 10 to 20 in both the flight and KSC scenes. The cap bounds how many live clones of the same recording (primary + overlap) can coexist while looping; each clone is its own GameObject/renderer/FX/audio stack (mesh vertex data is still shared via Unity `sharedMesh`, so per-frame cost scales with the clone count, not with vertex budget). Because `GhostPlaybackLogic.ComputeEffectiveLaunchCadence` enforces the cap as `ceil(duration/cadence) <= cap`, doubling the cap halves the minimum effective looping interval (floor = `duration / cap`) - e.g., a 60-second recording's floor drops from 6s to 3s before the runtime-cadence clamp kicks in. Distance-based LOD (full-fidelity inside the 2.3km physics bubble, simplified out to 50km, hidden beyond 120km) is independent of this cap and unchanged.
- Consolidated scattered tunables into a single `Source/Parsek/ParsekConfig.cs`. `DistanceThresholds` moved from its standalone file into the same config file; new top-level static classes `GhostPlayback` (concurrency caps, per-frame throttles, prewarm/hold buffers), `LoopTiming` (loop/cycle periods, boundary epsilon), `WarpThresholds` (FX-suppress / ghost-hide warp levels), and `WatchMode` (grace windows, camera entry defaults, pending-bridge frame budget) own the numbers that used to live inside `GhostPlaybackEngine`, `GhostPlaybackLogic`, `ParsekKSC`, and `WatchModeController` (including the duplicated KSC copy of the concurrent-ghost cap). Behaviour-neutral refactor - every constant keeps its value.
- `#473` The `Gloops - Ghosts Only` group is now treated as a permanent root group in the Recordings window: no disband `X`, stale parent assignments self-heal back to root, and the group stays pinned above every other root item whenever it has recordings.
- `#450 B2` Timeline ghost snapshot construction now advances in staged chunks across multiple playback frames instead of instantiating the entire snapshot in one `UpdatePlayback` tick, eliminating the remaining bimodal single-spawn hitch after the B3 lazy-reentry follow-up.
- Kerbals window Mission Outcomes fold headers now bold only the main kerbal name next to the fold arrow, leaving the arrow and folded mission summary in normal weight.

### Bug Fixes

- Flight playback, watch handoff, and ghost map visibility diagnostics now explain skipped or blocked playback decisions without adding per-frame spam.
- Re-Fly relative-frame playback now falls back to the recorded anchor trajectory when a live anchor is unsafe or unavailable, including the case where another ghost's anchor pid resolves to the active Re-Fly target. This keeps other vessels' trajectories ground-relative during Re-Fly, prevents stale-transform watch cutoffs after rewind, suppresses hidden-prime reentry FX bursts, and drops dead-on-arrival controlled children that would otherwise commit `Unknown` 0s rows.
- `#616/#617/#619` Post-merge Re-Fly now suppresses GhostMap state-vector ProtoVessels that would later enter a relative section anchored to the active Re-Fly target, removes/re-defers any already-created map ghost when an update transitions into that unsafe relative-anchor relationship, preserves the live-anchor fast path for unrelated relative playback, delays lazy reentry FX until the ghost has had a real playback sync, warns when a recorded-anchor fallback must use a far-away absolute pose, and logs unresolved relative distances as `unresolved` instead of formatting `double.MaxValue` as metres.
- `#623` Relative track sections now record both anchor-local frames and planet-relative absolute shadow frames in v7 sidecars. During active in-place Re-Fly, parent-chain upper-stage ghosts use the absolute shadow path instead of reconstructing through the re-flown booster/probe, keeping their observed trajectory fixed to the planet rather than at a constant booster-relative offset.
- `#618` Re-Fly merge defaults now resolve optimizer-created parent-chain terminal tips with pending-tree context and mark directly connected stale parent-chain tips ghost-only in the merge decisions, preserving the active in-place Re-Fly chain while preventing the old upper-stage chain tip from remaining spawnable by default.
- `#620` Terminal materialization now rejects corrupt vessel snapshots before `ProtoVessel.Load` when they have no `PART` nodes, non-finite surface/orbit metadata, or unrecoverable body/orbit provenance. Flight, KSC, and chain-tip fallback paths now mark those recordings abandoned/ghost-only instead of repeatedly retrying KSP loads that can die with NaN orbits.
- `#588` Flight Map View now allows `OrbitalCheckpoint` state-vector map ghosts only for explicit orbit-segment gap recovery after an SOI/body transition: a current segment still wins when available, the fallback body must match the post-gap body, and the UT must stay inside the playback window. Accepted recoveries log `source=StateVectorSoiGap` / `reason=soi-gap-state-vector-fallback`; rejected checkpoint candidates now say whether a safer segment existed, the source was not an SOI-gap recovery, the body mismatched, or the UT was outside the valid window.
- `#586` Ghost map vessel `Set As Target` now sticks instead of being silently dropped by stock KSP. Failed targeting attempts now log a warning with diagnostic state instead of a false success.

- `#574` Already-Destroyed recordings no longer re-run the sub-surface ballistic finalizer on cache refresh. The first Destroyed classification now logs once with body, altitude, and threshold; later refreshes emit a rate-limited skip diagnostic plus refresh summary instead of a repeated WARN storm.
- `#577` Re-Fly session markers loaded from an earlier game session now survive fresh-load scenes where KSP reports UT 0 during validation. The validator still rejects corrupt `InvokedUT` values and now logs current-UT/RP-UT comparisons for both accepts and rejects.

- MergeTree now heals velocity-consistent Background-to-Active handoff gaps by inserting a shared boundary point, preventing Kerbal X-style ghost trajectory pops from section-authoritative merged recordings.
- Follow-up cleanup to `#431/#432`: retired `MilestoneStore.CurrentEpoch` from production branch filtering. Timeline rows, milestone bundling, reward enrichment, revert bookkeeping, and load-time ledger recovery now exclude abandoned branches through recording-tag visibility plus deterministic discard/unstash behavior instead of epoch stamping/filtering.
- `#579` Debris recoveries without an immediate funds event no longer enter the LedgerOrchestrator pending recovery-funds queue, preventing false overflow and rewind-flush warnings after debris-only recoveries.
- `#552` Vessel recovery funds now tolerate stock firing `onVesselRecovered` before the paired `FundsChanged(VesselRecovery)` event. Parsek defers the recovery request and pairs it when the funds event arrives, preferring vessel-name matches over nearest-UT, warning on ambiguous ties, and evicting unclaimed requests on scene switches, rewind boundaries, and save loads.
- `#553` Untagged lifecycle events (contract accept/complete/fail/cancel, tech, part purchase, crew hire, milestone, strategy activate/deactivate, facility upgrade) now forward directly to the ledger even in FLIGHT, so launch-site events that occur before any Parsek recording owner exists do not get stranded only in `GameStateStore`. Tagged FLIGHT teardown events remain protected by the non-empty recording tag gate.
- `#555` Tracking Station orbit-source diagnostics now report visible-segment, terminal-orbit, state-vector fallback, endpoint-conflict, and already-materialized decisions with endpoint/seed metadata. Startup and lifecycle scans now aggregate repeated skip reasons while preserving the first detailed sample for each source/reason, and map-visible window fallback logs use shared rate-limit keys to avoid per-vessel spam.
- Flight Map View ghost map vessels now fill sparse recorded-orbit gaps with terminal-orbit fallback only when no track section covers the current UT, keep existing map vessels alive through that fallback instead of tearing them down between segments, and suppress map ghosts once the matching real vessel has materialized.
- Warp-deferred final vessel spawns now flush as soon as warp ends even when the survivor endpoint is outside the active vessel's physics bubble, so landed/splashed/orbiting survivors materialize at mission end after a rewind + fast-forward.
- `#551` Tracking Station ghost creation now consumes the same map-presence source decision as Flight Map View, including visible segment priority, state-vector fallback, terminal-orbit endpoint checks, endpoint-conflict skips, and suppression once the real vessel has materialized.
- `#561` Tracking Station ghost clicks now clear KSP's private selected-vessel field before blocking Fly/Delete/Recover, so a stale asteroid/comet selection cannot be flown after focusing a materialized Parsek vessel. Tracking Station terminal-orbit ghosts also require an endpoint-aligned orbit seed before creation, terminal-orbit-only records can seed from their own terminal orbit when there is no conflicting endpoint evidence, and ghost creation logs now report the actual ProtoVessel orbit SMA for segment ghosts.
- `#557` Initial science and reputation seeds now prefer captured game-state baselines (including legitimate zero values) over live KSP singleton balances. A zero seed is now authoritative instead of being upgraded later from future live state, so rewind/cutoff recalculations no longer turn post-launch science or reputation into UT0 budget.
- `#558` Rewind/cutoff resource patching now shows cashflow-projected spendable funds and science at the top bar instead of the gross current balance or a blunt full-future spend subtraction. Future spendings only reserve current headroom when the projected balance would dip below the current value, future earnings before those spendings can cover them without inflating current spendability, and reputation stays a current-UT running value with no reservation.
- `#559` Rewind/cutoff patching now restores the R&D tech tree to the nodes researched at the selected past UT, gated strictly on the rewind path so live unlocks after the latest baseline are preserved.
- `#556` Tracking Station `buildVesselsList` finalizer now suppresses only the known ghost ProtoVessel missing-orbit-renderer NRE shape, using the first failing missing-renderer vessel plus the stock IL offset when available; unrelated or ambiguous stock exceptions are warned with vessel-context counts and left visible.
- `#562` Tracking Station ghost-selection clearing now also zeroes the previously selected vessel's `orbitRenderer.isFocused`/`drawIcons` and detaches its patched-conics solver before nulling the private `selectedVessel` field, so focusing a Parsek ghost no longer leaves the earlier real-vessel orbit focus and patched-conics state latched without re-entering stock `SpaceTracking.SetVessel`.
- `#564` Tracking Station ghost selections now open a Parsek-owned ghost action panel with safe Focus, Target, Recording details, and a selected-recording-only Materialize action keyed by stable recording ID; the panel refreshes action eligibility every GUI frame, and when that ghost resolves, Target and map Focus now hand off to the materialized real vessel, while stock Fly/Delete/Recover remain explicitly blocked and the private KSP selected-vessel field is still cleared on every ghost block.
- Timeline initial-resource rows now respect the save mode: Sandbox hides resource seed rows entirely, and Science mode shows only the science baseline instead of zero funds/reputation noise.

### Tests

- Added focused coverage for the new playback visibility diagnostics.
- Added post-merge Re-Fly regressions for GhostMap create-time lookahead/update-time suppression, relative-anchor source selection and recorded fallback, v7 relative absolute-shadow sidecar round-trips, optimizer trimming of relative shadow frames, unresolved zone-distance formatting, hidden-prime lazy reentry suppression, parent-chain terminal tip merge defaults, and corrupt terminal spawn snapshot quarantine.
- `#586` Added log-capture regressions and a live KSP runtime canary for verified ghost-map targeting.
- Added focused regressions proving hidden old-branch events stay out of milestones, timeline legacy rows, reward write-back, and ledger recovery for recording-visibility reasons rather than `CurrentEpoch` checks, and updated the remaining fixtures that previously mutated `MilestoneStore.CurrentEpoch`.
- `#552` Added recovery-pairing regressions for callback-before-funds-event ordering, the no-paired-event deferral path, vessel-name-preferred pairing over nearest-UT, ambiguous-tie warning, lifecycle-boundary staleness eviction, and queue-overflow threshold warning.
- `#553` Added direct-ledger forwarding predicate coverage for tagged teardown suppression, untagged KSC events, and untagged pre-recording FLIGHT events across tech, part-purchase, crew-hire, milestone, strategy activate/deactivate, and facility upgrade handlers, plus an evt.recordingId-vs-resolver drift test.
- `#555` Expanded `GhostMapPresenceTests` log assertions for segment and terminal source decisions, endpoint-conflict skips, already-materialized suppression, endpoint seed diagnostics, and aggregated repeated Tracking Station skip reasons.
- `#551` Added headless parity coverage for Map View versus Tracking Station source decisions, state-vector Tracking Station ghosts, endpoint-conflict skips, and materialized-real-vessel suppression.
- Added headless coverage for Flight Map View terminal-orbit fallback across sparse orbit/coast gaps, existing map-vessel fallback updates, materialized-real suppression, legacy sparse-point coverage, and a warp-deferred survivor-spawn regression.
- `#557` Added a rewind cutoff regression based on the April 23 log package shape: funds stay on the seed-minus-rollout path, while zero baseline science/reputation remain zero even when future science earnings, tech spending, and reputation milestones exist later in the ledger.
- `#558` Added rewind affordability regressions for science and funds covering future spending reservation and the matching future-earning-before-future-spending case that preserves current headroom.
- `#559` Added `PatchTechTree` log-assertion coverage for the skip paths (no target set, missing R&D singleton, reflection failure) plus the existing `BuildTargetTechIdsForPatch` baseline/affordability/rehydration coverage.
- `#561` Added headless coverage for Tracking Station private-selection clearing, terminal-orbit-only ghost seed resolution, and the conflicting-endpoint skip that prevents repeated unseedable terminal-orbit ghost creation attempts.
- `#560` The default solution build now compiles the deployable `Parsek` plugin project only, avoiding SDK 6.0's parallel solution-build false exit `1`; full test validation remains `dotnet test Source\Parsek.Tests\Parsek.Tests.csproj`.
- `#556` Added headless coverage for known ghost NRE suppression plus earlier/later non-ghost missing-renderer mixes, different-stock-offset, unrelated-exception, and prior-stock-null `buildVesselsList` exception handling.
- `#562` Added a headless regression that pins `GhostTrackingStationSelection.TryClearSelectedVessel` to the deselection-only orbit-renderer/patched-conics path and asserts it does not invoke stock `SetVessel` on the cleared selection.
- `#563` Added headless Tracking Station control-surface UI coverage for status counts, compact labels, ghost-visibility setting application, and reuse of the shared Recordings/Settings window wiring.
- `#554` Added deterministic Tracking Station runtime canaries for scene entry, synthetic orbital ghost show/hide/recreate, ghost object count/no exception spam, and a manual materialized-vessel Fly stale-selection check that first proves ghost selection clears stale private stock selection before driving `Fly`. Release bundle validation now has a `release-tracking-station` profile that fails missing required TS rows and documents the optional Fly row when it was not captured.
- `#564` Added headless action-presentation coverage for safe ghost actions, blocked stock actions, chain ghosts without committed recording rows, repeated stale stock asteroid/comet selection clearing before ghost interactions, the single-recording Materialize path, stable recording-ID lookup after raw index changes, and the Focus/Target handoff eligibility checks used when a Tracking Station ghost resolves.
- `Bug278FinalizeLimboTests` now pins the orbit-only terminal-body heal path: a leaf with a stale `TerminalOrbitBody` but only orbit-segment evidence heals to the segment body and emits the `PopulateTerminalOrbitFromLastSegment: healed stale cached terminal orbit` log line.
- The last xUnit smoke/assertion follow-ups now catch headless `ParsekUI.Cleanup()` teardown in the KSC wiring smoke test and anchor the Bug219 negative log checks to the full production log prefix instead of the overlapping `ShouldPopulate...` diagnostic.
- Headless landed snapshot-repair coverage now survives Unity pseudo-null `CelestialBody` fixtures all the way through the real `REF` rewrite path instead of bailing out before the repaired surface orbit node is written.
- The last SOI attitude xUnit follow-up now compares normalized quaternion rotation equivalence by absolute dot product, so exact `q` / `-q` handoff matches no longer fail just because the same world rotation chose the opposite sign.
- The predicted-tail flat-fallback regression now appends its extra orbit segment at the end of the fixture's real checkpoint payload instead of seeding a pre-track timestamp, so the test again asserts the intended "tail beyond sections" shape instead of a malformed earlier segment.
- Remaining xUnit follow-ups now match the exact `PopulateTerminalOrbitFromLastSegment` log prefix, keep `ParsekUI` opaque-style teardown headless-safe through the whole cleanup path, and resolve body-index seam overrides by reference so the landed spawn-repair fixture rewrites `REF` through the real production seam.
- Terminal-orbit preserve tests now match the exact `PopulateTerminalOrbitFromLastSegment:` log prefix, and the orbital-frame continuity regressions now compare canonicalized normalized rotations instead of raw non-unit quaternion tuples.
- Headless spawn-validation seam tests now install the body-index override in the landed surface-repair fixture and verify the private production body-index resolver directly, so the xUnit coverage actually exercises the same REF rewrite path that spawn repair uses.
- Headless body-index seam coverage now treats synthetic `CelestialBody` fixtures by reference instead of Unity-style null semantics, so the xUnit resolver override again drives landed snapshot repair through the real production `REF` rewrite path.
- Headless `ParsekUI` cleanup now ignores Unity GUI-object teardown calls that are unavailable in plain xUnit, and the title-color regression now exercises the pure normalization helper instead of constructing `GUIStyle` in a non-Unity process.
- Headless scene-exit finalization tests now skip the live incomplete-ballistic extrapolator when Unity `FlightGlobals` is unavailable, and `TryApply()` now treats escaped headless `SecurityException` / `TypeInitializationException` probes as the same guarded decline path so xUnit reaches its real fallback assertions instead of dying in engine startup.
- Headless snapshot-validation tests now resolve both snapshot `ORBIT.REF` and the later endpoint-body repair lookup through test seams, and the production fallback now declines cleanly if Unity's body registry still fails to bootstrap, so xUnit can verify endpoint-body repairs and mismatch rejection without booting `FlightGlobals`.
- Spawn-rotation helper coverage now lives in ten `SpawnRotation` in-game tests, so the suite still checks real Unity quaternion semantics without asking headless xUnit to execute `Quaternion.Euler`.
- Endpoint-body resolution no longer lets a same-UT point lose to a stale terminal orbit on another body, and the rejection now logs the conflicting bodies and UTs instead of silently picking the cached orbit.
- `#520` `FinalizeIndividualRecording` now preserves a correct cached terminal-orbit body when the last point shares a conflicting later segment's start UT on another body, and the finalize regressions now pin both the preserve path and the stale-tuple heal-from-matching-body follow-up without changing the shared load/backfill helper rules.
- Landed-tail trimming now ignores identity terminal rotations when matching a stable surface tail, so recordings with a perfectly upright terminal pose still trim instead of treating identity as a mismatched authored rotation.
- Text trajectory sidecars now preserve predicted orbit segments even for legacy format versions, so old-format round-trips no longer silently drop `isPredicted`.
- Flat-binary fallback detection now checks only the extension suffix beyond track-section payload, so predicted tails past the section-authoritative checkpoint set still serialize through the intended flat fallback path.
- Endpoint-aligned spawn-orbit tests now keep terminal-orbit fallback constrained to the resolved endpoint body, surface terminals no longer reuse stale terminal-orbit tuples, and spawn-validation logs include both the caller context and vessel name.
- `#488` Quickload-resume in-game helpers now drive the stock `FlightDriver.StartAndFocusVessel` resume path instead of a bare `LoadScene(FLIGHT)`, and they now skip early on a missing/empty `quicksave.sfs` before attempting the scene reload.
- Release-closeout evidence bundles now have a dedicated `scripts/validate-release-bundle.py` gate that fails when required artifacts are missing, `log-validation.txt` did not pass, or required runtime rows are absent / not `PASSED`.
- Added direct sidecar-codec coverage for `SnapshotSidecarCodec` and `TrajectorySidecarBinary`, including probe metadata, checksum/error paths, section-authoritative layout checks, and flat-fallback round trips.
- Added direct fluent-builder coverage for `VesselSnapshotBuilder` and `RecordingBuilder`.
- `scripts/test-coverage.ps1` now bootstraps missing Windows `dotnet` path variables and clears stale coverage artifacts before each run, so the coverage job fails on the real restore/build/test problem instead of environmental path drift or leftover files.
- `scripts/test-coverage.ps1` now validates the non-Cobertura report formats structurally, applies stricter metadata/content checks to the emitted Cobertura report, and preserves `dotnet test`'s native exit code when report validation is blocked.
- `#484` Terminal-orbit backfill now keeps an already-correct cached orbit instead of needlessly rewriting it, and the preserve/heal logs stay stable across comma-decimal locales.
- `#482` Added xUnit coverage for recording-path validation log routing, including the dedicated production-`WARN` branch and the explicit test-context `VERBOSE` branch (now including invalid file-name chars).
- Added spawn-rotation regressions for the format-v0 surface-relative ProtoVessel path, including SpawnAtPosition node-prep coverage for Kerbin/Mun fixtures, snapshot-override rotation rewrites, terminal-surface-pose precedence, and the surface-only fallback gate.
- Headless finalization/body-registry fallbacks now walk wrapped `FlightGlobals` bootstrap failures without poisoning later live caches, and the in-game spawn-rotation suite now pins that ProtoVessel rotation writes do not need a resolved body transform.
- Post-walk partial-tracker integration coverage now seeds recording-scoped events consistently, so the test again isolates the intended science mismatch instead of tripping a spurious reputation warning.
- Legacy endpoint backfill now preserves exact-boundary terminal-orbit decisions, and headless surface snapshot repair can resolve body indexes through the same test seam instead of depending on live `FlightGlobals.Bodies`.
- Recording-store writes now keep legacy flat-sidecar predicted segments without leaking `isPredicted` into legacy track-section checkpoints, and flat-tail fallback detection again accepts real appended suffixes instead of rejecting them for unrelated monotonicity.
- Post-walk partial-tracker xUnit coverage now expects the current summary log shape, including the explicit `compared=` and `cutoffUT=` fields that production already emits.
- Legacy recording-store checkpoint serialization now omits `isPredicted` before v5 unless the file is writing the flat trajectory payload itself, which restores the old track-section contract without re-breaking legacy predicted-tail round-trips.
- The partial-tracker post-walk integration fixture now pins the full summary log contract instead of the older pre-`compared=` format, so it only fails when reconciliation behavior changes rather than when the summary text grows new stable fields.
- Ballistic orbital-frame reconstruction now canonicalizes quaternion sign at the encode/decode seam, so SOI handoffs and scene-exit predicted tails preserve the same raw rotation components instead of bouncing between equivalent signs.
- Long-horizon sea-level impact scans now keep one cutoff step past the predicted crossing when they narrow the sampling window, so real impacts still produce a sign-change bracket instead of escaping as `Orbiting`.
- Post-merge xUnit fixture follow-ups now keep internal restore-mode coverage and facility-upgrade event seeding aligned with the current production enums, so `Parsek.Tests` builds cleanly on top of merged `main`.
- `#487` Added an in-game `TestRunner` regression that drives the scene-reset + missing-skin path, clears any preexisting cache before the initial build, and asserts lagging hover/focus/active states fall back to the ready normal window background instead of caching a transparent frame.
- `#487` Followed the same guarded opaque-window rebuild and missing-state fallback into the shared `ParsekUI` window-style cache, so the Settings-hosted Test Runner and other Parsek windows no longer bypass the original Ctrl+Shift+T transparency fix.
- `#487` Hardened both the global shortcut runner and shared Parsek windows against scene-transition IMGUI tint leakage by normalizing `GUI.color` / `GUI.backgroundColor` / `GUI.contentColor` during window draws, and added a focused `TestRunner` regression that verifies the neutralized draw helper restores the prior GUI colors afterward.
- `#461` Added in-game loop-cycle reuse visibility regressions that drive the full `UpdatePlayback -> UpdateLoopingPlayback` boundary path, pinning both the same-frame visible reactivation case and the hidden-by-zone deferred/inactive case.
- `#478` `RuntimeTests.MapMarkerIconsMatchStockAtlas` now skips outside `FLIGHT` and `TRACKSTATION` instead of failing in `EDITOR`, `MAINMENU`, and `SPACECENTER`, so the runtime test only asserts `MapView.fetch` where that API actually exists.
- `#480` Strategy lifecycle in-game regressions now wait for stock strategy hydration to stabilize before probing activation, so the SPACECENTER career tests fail with targeted readiness diagnostics instead of early `NullReferenceException`s.
- `#481` `RuntimeTests.TimeScalePositive` now distinguishes real zero-timescale failures from stock pause windows, so the SPACECENTER flake no longer reports paused frames as broken time progression.
- `#472` Added unit coverage for watch-camera retarget angle resolution so preserved pitch/heading stays pinned to the same world orbit direction across ghost handoffs.
- `#474` Added runtime coverage for fresh ghost watch-pivot centering and ghost audio re-anchoring / stereo-default configuration.
- `#475` Added regressions for exact-boundary endpoint-body persistence, malformed-snapshot spawn refusal, remaining ghost-body alignment paths, and stale surface-site label cleanup during snapshot repairs.
- Added `BallisticExtrapolator` regressions for zero-progress SOI guardrails, long-horizon surface-scan narrowing, explicit surface-coordinate injection, and the new start/fallback/terminal logging paths.
- `#476` Added unit and integration coverage for tracker-unavailable post-walk/commit-time earnings reconciliation, including full sandbox-style skips and partial per-resource gating.
- `#477` Added unit coverage for coalesced same-UT milestone windows on both the matching path and the missing-event warning path.
- `#462` Added a regression for mixed null-tagged/tagged post-walk milestone windows so legacy siblings cannot reclaim ownership of a tagged `Progression` burst purely because they appear earlier in the ledger.
- `#469` Added post-walk reconciliation regressions for pruned milestone history, stale pre-live-event history after an epoch bump, invariant-culture WARN formatting, and the still-live missing-event warning path.
- `#467` Added `GameStateRecorder` regression coverage for near-threshold `ReputationChanged` deltas, including the stock-rounded `+/-0.9999995` shape and a control case that still ignores clearly sub-threshold reputation noise.
- Added focused scene-exit finalization regressions for rejected hook outputs, decline diagnostics, ghost-only surface metadata preservation, and preservation of hook-authored terminal-orbit metadata.
### Documentation

- Added `docs/parsek-recording-finalization-design.md` and `docs/dev/done/plans/recording-finalization-reliability.md`. Specifies the terminal-state/synthetic-tail reliability contract for scene exit, crash, vessel unload/delete, and background recording end paths that Rewind-to-Separation depends on.
- Added the Tracking Station audit action plan (`#551`-`#556`) covering Map View lifecycle parity, a TS control surface, safe ghost actions, TS runtime coverage, orbit-source diagnostics, and the broad `buildVesselsList` finalizer.
- `#558` Updated the game-actions design document to define top-bar funds/science as current-UT cashflow-projected spendable resources and to clarify that rewound R&D state locks future tech nodes while keeping their future costs in the projection.
- Added `docs/dev/test-coverage-matrix.md`, a current-tree subsystem matrix that maps major Parsek areas to their headless xUnit, in-game runtime, `KSP.log` validation, and manual coverage surfaces.
- Release-closeout docs now define three named evidence bundles and require `collect-logs.py`, `validate-ksp-log.ps1`, and `validate-release-bundle.py` to pass on each retained packet.
- Release-closeout docs now require the `.claude/CLAUDE.md` deployed-DLL verification recipe before trusting in-game evidence, so a stale `GameData/Parsek/Plugins/Parsek.dll` does not produce false-pass runtime bundles.
- README now lists KSP Community Fixes in the Supported Mods table. KCF is fully compatible with Parsek; it replaces the body of `VesselPrecalculate.CalculatePhysicsStats` for performance, and Parsek's recording postfix on the same method composes cleanly.

### Bug Fixes

- Simple ghost map/tracking-station marker labels now appear on icon hover again, while left-click still pins or unpins the label and non-left clicks continue to pass through to stock handlers.
- Ballistic orbital-frame storage now normalizes as well as canonicalizes the saved quaternions, so SOI handoffs keep the same represented world attitude instead of drifting when a frozen playback rotation started as a scaled quaternion.
- Hyperbolic predicted segments now reconstruct true anomaly with a quadrant-safe formula, so parent-body SOI handoffs keep the same boundary state and frozen playback attitude instead of folding the new segment onto the wrong outbound branch.
- Hyperbolic predicted segments now keep periapsis orientation even when the parent escape orbit is equatorial, so SOI handoffs no longer serialize the parent segment with `argumentOfPeriapsis = 0` and then reconstruct the wrong start state and frozen attitude.
- Body-index repair now falls back to explicit reference/name scans over the loaded body list when the test seam or Unity equality path cannot resolve a `CelestialBody` directly, so landed snapshot repairs can still rewrite `ORBIT.REF` deterministically in both live KSP and headless seam tests.
- Flat-trajectory extension detection now accepts a real appended orbit-segment suffix immediately after the rebuilt checkpoint payload, so current-format predicted tails beyond track sections still take the intended flat-binary fallback path while malformed suffixes keep failing closed.
- Session-merge flat-copy preservation now requires a safe appended suffix beyond the rebuilt track-section payload, so duplicated or non-monotonic flat tails fall back to track-section rebuild instead of leaking bad copies into merged recordings.
- Incomplete-ballistic scene-exit finalization now only caches permanent `FlightGlobals` probe failures, so a transient `ready=false` teardown frame cannot disable later live finalization in the same KSP session.
- `#489` Incomplete ballistic recordings no longer freeze at scene exit. Ghosts now continue suborbital, descent, and flyby coasts to their natural endpoint instead of stopping mid-air.
- Patched-conic snapshot capture now fails closed when a predicted patch has no reference body or the private solver `patchLimit` hook is unavailable, logs those conditions explicitly, and reports truncation as a boolean tail flag instead of a pseudo-count.
- `#486` Quickload after a runway takeoff no longer leaves stale future samples or fake save/load discontinuity warnings in the merged recording tree.
- `#482` KSP.log no longer accumulates spurious recording-id rejection WARN lines during test runs, while real invalid ids in live save/load/delete paths still log at `WARN`.
- `#487` The Ctrl+Shift+T test runner window now defers opaque-style rebuilds until the destination scene's IMGUI skin exposes a real normal window background, and any lagging hover/focus/active variants fall back to that ready background instead of being cached as transparent states. Scene-reset cleanup also destroys the copied opaque textures before rebuilding to avoid leaking them across repeated transitions.
- `#463` Deferred warp-end spawns now replay already-due `FlagEvents` for the spawned recording, so flags planted mid-recording still materialise even if you time-warp past that recording while watching something else.
- `#466` `RecalculateAndPatch` now defers KSP state patching while a live, active, or pending flight tree is still uncommitted, so mid-flight/load-time recalculations no longer snap funds back down to the committed-ledger target. Discard paths now explicitly recalculate once the pending tree is gone.
- `#470` Funds recalculation no longer logs `FundsSpending: -0, source=Other` for zero-cost replay entries during module walks. The no-op action still participates in affordability/balance tracking; only the useless VERBOSE line is suppressed.
- `#471` Gloops recordings now commit with looping off by default, so fresh ghost-only captures stay idle until you turn looping on and then follow the normal auto loop timing.
- `#472` Watch-mode camera retargets now preserve the current pitch/heading when follow rebinds to a replacement ghost, eliminating the visible camera jerk on loop/overlap handoffs, quiet-expiry primary rebinds, and stock vessel-switch re-targets.
- `#474` Ghost audio now recenters on the fresh watch pivot instead of staying on off-axis part transforms, and ghost sources use a softer 3D blend so Watch mode no longer hard-pans loops or one-shots into a single speaker.
- Ghost audio now uses distance-aware priority tiers when Unity hits the voice cap: explosion one-shots stay at normal game-sound priority, ghost rocket loops outrank other ghost loops, quieter ghost engines outrank jets, and the 4-loop ghost cap is enforced at runtime so late-activating engines are not discarded at build time.
- Ghost activation-start resolution no longer casts `IPlaybackTrajectory` back to `Recording`. `GhostPlaybackEngine` now resolves the first playable payload time through a shared interface-only bounds helper, keeping the standalone ghost boundary concrete-type-free and adding non-`Recording` regression coverage.
- `#475` Endpoint-body decisions now persist across finalize/save/load, malformed spawn snapshots are repaired or refused instead of silently defaulting to Kerbin, remaining ghost builders honor the same endpoint-aligned body/orbit decision as real spawns, and snapshot situation repairs clear stale surface site labels.
- `#476` Post-walk and commit-time earnings reconciliation now skip sandbox / tracker-unavailable resource legs instead of comparing against zeroed stock stores. When funds, science, and reputation tracking are all unavailable, Parsek emits a one-shot VERBOSE skip line instead of flooding `KSP.log` with false `store delta=0.0` and `no matching event` WARNs.
- `#477` Post-walk milestone reconciliation now compares coalesced `Progression` bursts once per window instead of attributing the summed funds/rep delta to each individual `MilestoneAchievement`. Same-UT milestone bursts now log grouped ids/counts, eliminating the misleading 2x / 3x `expected=` warnings while preserving correct aggregate matching.
- `#462` Post-walk milestone reconciliation now prefers tagged recording-scoped actions over null-tagged legacy siblings when picking the owner of a mixed-scope `Progression` window, closing the remaining order-dependent false-positive WARN where a legacy row could re-fold another recording's delta back into `expected=`.
- `#468` Post-walk science reconciliation now matches `ScienceTransmission` across the owning recording span for end-anchored committed `ScienceEarning` rows.
- `#483` Science captured before takeoff now reconciles correctly later in the same flight instead of falling into repeated false science-warning loops in `KSP.log`.
- `#469` Post-walk earnings reconciliation now skips ledger history the live `GameStateStore` can no longer represent after milestone pruning or epoch changes, eliminating false `"no matching FundsChanged keyed 'Progression'"` WARNs against already-processed milestone rewards while preserving live-tail mismatch detection.
- `BallisticExtrapolator` now drops the dead `EventCandidate.BodyName` path, removes the duplicate body-radius assignment, guards zero-progress SOI handoff loops, narrows long-horizon cutoff scans around sea-level/periapsis candidates, and emits explicit start / SOI / fallback / horizon / terminal diagnostics instead of failing silently.
- `#464` Timeline Details no longer renders duplicate gray legacy milestone / strategy lifecycle rows when a matching ledger `GameAction` exists at the same UT and key; the view now keeps the richer action entry and suppresses only the redundant legacy shadow row.
- `#465` KSC ghost engine/RCS audio now pauses with the stock ESC menu and resumes on unpause. KSC now latches the pause state before replaying runtime part events, so ghosts spawned while ESC is open stay silent instead of restarting looped engine/RCS audio or one-shot part-event audio; tracking-station ghosts were checked and remain map-only (no `AudioSource`s there to pause).
- `#467` `ReputationChanged` no longer drops stock `+1`/`-1` reputation deltas that arrive as `0.9999995`/`-0.9999995` due to float rounding, so records-milestone reputation legs now reconcile instead of falsely warning as missing.
- `#479` Stable-terminal finalize re-snapshots now normalize unsafe cached `sit` values on the fresh `BackupVessel()` snapshot before persisting it, so one-frame situation lag no longer leaves `FLYING` / `SUB_ORBITAL` in landed, splashed, or orbiting sidecars.
- Incomplete-ballistic scene-exit finalization now rejects unset/invalid terminal states and retrograde `terminalUT` results, logs real-hook declines, preserves hook-authored terminal-orbit metadata, and explains ghost-only surface metadata preservation instead of silently clearing it.
- `#485` The SPACECENTER strategy-lifecycle readiness probe now waits for stock strategy hydration to settle before deciding whether to fail or skip, and it reports bounded settle/timeout summaries instead of per-strategy exception spam. Unexpected probe failures still log full stack traces.

---

## 0.8.2

### Features

- In-game test results file (`parsek-test-results.txt`) now preserves per-scene history so KSC and Flight runs accumulate instead of overwriting each other.
- New **Gloops Flight Recorder** window for manual ghost-only recordings (Start/Stop, Preview, Discard). Ghost-only recordings auto-commit with looping on, run parallel to auto-recording, and get an X delete button in the recordings table.
- `#385` New **Kerbals** window (reserved crew, active stand-ins, retired stand-ins) opened from the main Parsek window; Retired Stand-ins removed from the Timeline footer.
- `#415` Kerbals window shows a **Per-Recording Fates** section — which recording each kerbal appeared in, color-coded by end-state (Aboard / Dead / Recovered).
- `#415-1` Per-Recording Fates entries are foldable per kerbal with a compact `N missions — X Dead, Y Recovered, Z Aboard` summary.
- `#415-2` New chain topology view in the Kerbals window replaces the flat Reserved / Active / Retired sections with a per-owner collapsible tree.
- `#416` New **Career State** window (Contracts / Strategies / Facilities / Milestones tabs) with "current" vs. "at timeline end" columns. Kerbals mission-outcome rows are now clickable and scroll the Timeline to the matching recording.
- `#388` New **Show ghosts in Tracking Station** toggle (Settings → Ghosts); sticky across rewind, quickload, and KSP session restart.
- `#389` Timeline and Recordings windows now share a time-range filter with quick presets (Last Day, 7d, 30d, This Year, All) and a custom-range dual-slider. A filter indicator with a Clear button stays visible in the Recordings table.
- Added an **L** (loop toggle) button next to R on the timeline for loopable recordings; active loops show green text.

### Enhancements

- Real Spawn Control proximity gate tightened: candidate range cut from 500 m to 250 m and a new <=2 m/s relative-speed requirement (derived frame-agnostically from the change in active-vessel/ghost separation between scans), so fast-forward only offers ghosts you have actually rendezvoused with.
- `#450 B3` Ghost spawn hitch reduced on reentry-capable recordings: the reentry-FX build is now deferred until the ghost actually enters atmosphere above Mach 1.2. Orbital-only and sub-Mach-1.2 trajectories skip the build entirely; a per-session `deferred / buildsAvoided` counter in the diagnostics health line shows how often the deferral saved real build work.
- Map view ghost icon right-click now pins the label (stock behavior) instead of opening the Parsek menu. Left-click still opens the menu.
- `#386` Ghost map and tracking station icons now hide their label by default and toggle it with a left click on the icon; hover no longer reveals the label, and non-left clicks pass through to stock handlers.
- Gloops Flight Recorder window keeps its three buttons (Start/Stop, Preview, Discard) in fixed positions across states, graying out unavailable actions instead of rearranging them.
- `#416` Recordings table header and body columns now line up across recording, group, and chain rows. Row index numbers sit under the `#` character, buttons (G/W/FF/R) and the Period input are 10 px inset from the cell left, and body text indents 5 px to match header text.
- Replaced the four sampling sliders with a single **Recorder Sample Density** setting (Low / Medium / High). Legacy slider-based saves migrate to the nearest preset on load.
- `#375` Demoted chatty per-appearance `GhostAppearance` logs from Info to Verbose.
- `#378` Added a rate-limited warn when on-save monotonicity rebuild exceeds 5 ms on a single recording, so save-time stutter is visible in `KSP.log`.
- `#449` Merger boundary-discontinuity warnings now include inter-section `dt=`, velocity-implied gap, and a `cause=` tag so quickload-resume stitches are distinguishable from real recorder bugs in `KSP.log`.
- `#376` Documented the dual-storage invariant for auto-assigned standalone group names.
- Gloops Flight Recorder window now has a Close button at the bottom, matching other Parsek windows.
- Recordings window opens wider by default (1280 px) so more columns fit without a horizontal scroll; the Info-expanded width scales up to match.
- Group column in the Recordings table widened to match the Loop column so the G and X buttons sit under the header.
- Main Parsek window now has a Close button in the footer, inline to the right of the version text (which moved from the right to the left).

### Tests

- `T68` Added cleanup-order unit coverage for watch-mode exit-before-destroy, plus in-game regressions for watch-cleanup and low-altitude Kerbin ghost spawn exception containment.
- `#458` Added a binary sidecar regression test for duplicated flat-prefix loads and expanded the in-game committed-recording monotonicity failure to report track-section prefix/source diagnostics.
- `#371` Added a `MergeInto` continuous-EVA boundary merge round-trip test plus an assertion that the optimizer rejects orbital-phase pairs.
- `#384` Added the Learstar A1 S16 mission to the `DefaultCareer` fixture so `InjectAllRecordings` covers a far-away / map-view recording.
- `#399` Regression tests for `ScienceModule.ComputeTotalSpendings` at duplicate UTs.
- `#390` 10 unit tests for `GameStateStore.PruneProcessedEvents` and `MilestoneStore.GetLatestCommittedEndUT`.
- `#391` 6 unit tests for `GameStateStore.RebuildCommittedScienceSubjects`.
- `T67` Replaced the skipped Unity-GameObject xUnit priming test with an in-game runtime equivalent.
- `T63` Pinned `KerbalsModule.ApplyToRoster` real-roster Remove path in `KerbalLoadDiagnosticsTests`.
- `T66` In-game runtime regression for fresh watch-entry camera orientation (canonical pitch/heading, no 180° flip).
- `T61` Two hydration-salvage regression tests covering `RestoreHydrationFailedRecordingsFromPendingTree` and mixed subset-restorable trees.
- `#365` Unit coverage for v2/v3 binary sidecar reader bounds and full codec round-trip matrix.
- Added runtime in-game test for strategy lifecycle Harmony patch capture (#439 Phase A follow-up).

### Documentation

- `T61` Refreshed the live storage rebaseline against the April 14-16 playtest bundles.

### Bug Fixes

- Looping recordings whose ghost never built (missing vessel snapshot) no longer spam `ReusePrimaryGhostAcrossCycle ... null ghost` WARNs every frame in `KSP.log`; the skip branch now advances the cycle counter so the condition logs at most once per rate-limit window.
- Demoted the chatty `CameraFollow Camera pivot recalculated` line from Info to VerboseRateLimited; it was firing thousands of times per session on any recording with part visibility changes.
- Fix #439B: strategy activate setup cost reconciliation now covers Funds, Science, and Reputation legs, closing the known limitation that shipped with #439.
- `#438` Commit-time earnings reconciliation now correctly accounts for contract advances and facility upgrade/repair deltas, eliminating spurious WARNs when those actions land inside a recording's commit window.
- `#406 follow-up` Looping ghosts now reuse the same ghost GameObject across loop-cycle boundaries instead of destroying and rebuilding, eliminating the remaining ~21 ms per-cycle hitch on flight recordings with reentry FX. Per-cycle mutable state (engine throttle, RCS power, robotic servo, char intensity, reentry intensity) resets to the fresh-spawn baseline so the new cycle does not inherit stale readings.
- `#459` Between-run timeline ghost cleanup now rebinds stock camera targets off the watched ghost before teardown, then exits watch mode; `Sun.LateUpdate` also defensively short-circuits once on a missing/destroyed stock target instead of flooding `KSP.log` with per-frame `NullReferenceException`s.
- `#458` Binary `.prec` flat-fallback loads now run the malformed-prefix healer against track-section data, logging `healed=true/false` with pre/post counts and marking healed recordings dirty so the corrected sidecar flushes back out on the next save.
- `#456` Reserved crew are now placed in the tightest-fit same-name part when the snapshot's part pid can't be matched (e.g. after launching a new vessel that reuses a showcase ghost's part), preferring a 1-seat cockpit over a larger cabin.
- `#455` `PatchMilestones` no longer spams thousands of `repeatable node '<Body>/<Name>' is missing stock record fields` WARNs on every recalculation; one-shot per-body progress nodes (`Bop/Orbit`, `Dres/Flight`, …) now correctly fall through to the one-shot patch path instead of being short-circuited by the repeatable-record branch.
- `#387` Ghost map icons for `DeployedScienceController` and `DeployedGroundPart` now render the stock icon instead of the generic diamond fallback; their sprites live on separate atlas textures that the single-atlas init path used to silently reject.
- Loop period cells now show the runtime-effective overlap cadence when the 10-ghost cap raises a recording's launch cadence; clamped rows render in amber, explain the clamp in the Recordings window tooltip area, and still preserve the raw stored value when you start editing.
- Overlap cadence clamping now snaps directly to the minimum cap-safe cadence instead of overshooting in powers of two.
- Fix #440: post-walk reconciliation now covers strategy-transformed and curve-applied reward types (contract complete/fail/cancel, milestone, reputation earning/penalty, KSC-path funds/science earning), emitting a warning when post-walk derived values diverge from observed KSP deltas.
- Fix #440B: commit-time earnings reconciliation (`ReconcileEarningsWindow`) now reads post-walk `Transformed*` / `EffectiveRep` / `EffectiveScience` reward fields, matching the rewind-path post-walk hook and closing a latent double-WARN on future non-identity reward transforms. Also silences a false-positive subject-cap WARN that fired on capped science subjects.
- `#462 partial` Earnings reconciliation now scopes `FundsChanged` / `ReputationChanged` / `ScienceChanged` events by `recordingId`, so sibling recordings at the same UT no longer fold each other's `Progression` deltas into a WARN. Null-tagged legacy/recovered actions still match tagged store events.
- Fix #439: capture strategy activate/deactivate lifecycle so StrategiesModule sees input on strategy-using careers and eliminates the spurious PatchFunds suspicious-drawdown warning on revert/rewind after a strategy activates.
- `#448` KSC reconciliation no longer false-positive WARNs on every R&D part purchase under the stock-default `BypassEntryPurchaseAfterResearch=true` difficulty; the harder no-bypass difficulty still WARNs on genuine debit mismatches.
- `#452` Cancelled-rollout build costs now render with a "(cancelled rollout)" suffix in the Actions and Timeline views so they're distinguishable from adopted, recording-tagged build costs.
- `#451` R&D part-purchase ledger now records the actual stock debit in `cost=` (`0` under bypass=on, `entryCost` under bypass=off). Load heals the immediately previous bad save shape so stock-default free auto-unlocks no longer reload as paid purchases or reserved funds.
- `#441` Legacy flights whose net science or reputation was negative now reconcile cleanly on load (previously skipped), including long missions that overlap unrelated KSC activity. Optimizer merges that rewrite a tree's root also retag any ledger synthetics onto the new root so they survive subsequent reconcile passes.
- `#447` Single-point debris leaves that land, splash, or are recovered are now pruned on commit (previously only `Destroyed` leaves were pruned, so the zero-duration stubs tripped recording-integrity checks on reload).
- `#446` Discarding a Gloops Flight Recorder ghost-only recording no longer throws a `NullReferenceException` on the next UI frame.
- `#445` Rollout costs for vessels cancelled before launch are now captured in the ledger; previously the build cost vanished when the player reverted to VAB/SPH without ever starting a recording.
- `#444` Vessel recoveries from the tracking station or post-flight summary now reach the ledger; previously funds recovered outside a live recording window were silently dropped.
- Fix `#436` (Phase F): remove tree-level `DeltaFunds`/`DeltaScience`/`DeltaReputation` and the standalone resource applier. Legacy saves still migrate on first load via Phase A, and `TreeFormatVersion` now warns if a pre-Phase-F save cannot be recovered.
- `#443` Milestone rewards now patch the stored event correctly on saves that have been reverted at least once; previously `Kerbin/Landing` and similar `OnProgressComplete` milestones landed in the ledger with `funds=0` on any save with a non-zero milestone epoch.
- `#442` World-record progress nodes (`RecordsSpeed`/`Altitude`/`Distance`/`Depth`) now credit their funds and reputation rewards to the ledger; previously every world-first dropped its entire reward because the nodes call `AwardProgress` without firing `OnProgressComplete`.
- Every non-revert tree-commit path now disarms the legacy lump-sum replay on the just-committed tree, matching in-flight Commit Flight so the next FLIGHT scene cannot re-credit those resources on top of the ledger.
- Pre-existing committed flights now reconcile their funds/science/reputation against the ledger on load, so saves that persisted a tree's lump-sum delta no longer cause a silent drawdown after revert/rewind cycles.
- KSC-side ledger writes (part purchases, tech unlocks, facility upgrades/repairs, crew hires, contract advances) now key-match against their paired `FundsChanged`/`ScienceChanged` event and WARN on missing or mismatched deltas, surfacing missing earning channels at the point they occur. Transformed reward types (contract, milestone, reputation) stay VERBOSE-skipped on the KSC per-action path and are reconciled by the post-walk hook added in #440.
- Removed the legacy tree lump-sum and per-recording resource replay paths; committed recordings are now the single source of truth for funds, science, and reputation.
- `#434` KSP's stock crash/mission report now shows first on vessel destruction; Parsek's merge dialog no longer pre-empts it. Revert to Launch/VAB/SPH soft-clears the pending recording so a flight quicksave can still be F9'd back in, with the bumped milestone epoch keeping reverted events out of the current ledger.
- `#434` Fixed a NullReferenceException in `RevertDetector.Subscribe` that aborted `ParsekScenario.OnLoad` before the merge-dialog dispatch path could run, so going back to Space Center after a flight silently auto-committed the pending tree instead of showing the merge/discard dialog.
- `#434` Revert to Launch no longer accidentally deletes the in-flight recording's files, so a flight F5 quicksave can still be F9'd back in after reverting. Stale science captured between the launch quicksave and the revert is also now cleared even when no recording was yet stashed.
- `#433` The Recordings Manager enable checkbox is now purely visual: disabling a recording hides its ghost but no longer drops its vessel spawn, crew reservations, or any other career effect.
- `#432` Gloops (ghost-only) recordings no longer leak kerbal assignments or vessel costs into the career ledger — they are purely visual.
- `#431` Discarding a recording now reverses every career effect it captured — contracts, tech, crew changes, milestones, and resource deltas are all purged, including events the flush-on-save path had already bundled into a milestone.
- `#416` New career no longer starts with zero funds.
- `#416` Crashed-vessel recordings now keep their R (rewind) button.
- `#419` Debris recordings from a crash breakup no longer violate monotonic-UT invariants at the parent-breakup boundary.
- `#383` Ghost engine flames now render at roughly stock full-thrust size.
- `#366` Sidecar rollback no longer aborts mid-way when one step fails — each step catches independently and keeps going.
- `#369` Hardened loop-pause hold computation against a NaN warp rate.
- `#377` Removed dead visual-root offset code; auto-generated tree groups from the legacy heuristic log once; stopped per-frame renderer-creation calls while waiting for the ghost in MapView.
- `#394`, `#395`, `#396`, `#397`, `#398`, `#400`, `#401`, `#402`, `#403`, `#404`, `#405` Fixed a cascade of career-mode ledger bugs that drained funds to zero, lost accepted contracts on scene transition, pinned science at the starting seed, and zeroed out milestone funds/rep rewards. Broken sci1/c1 saves are repaired automatically on first load.
- `#391` Deleting a recording no longer leaves stale science entries that could re-synthesize on next load.
- `#390` Events are pruned after each commit to prevent unbounded growth in long careers.
- `#393` Fixed misleading "sandbox/science mode" log message in `PatchScience`.
- `#406` Map-view framerate with many looping showcase ghosts no longer collapses.
- `#362` Terminal crash-end decouple fragments (parachutes, heat shields, late shrapnel) now become proper debris branches instead of being silently dropped.
- `#370` Hardened the group Watch button against a latent `IndexOutOfRangeException`.
- `#373` Landed-ghost clearance fallback now emits a rate-limited warning instead of silently regressing to the legacy 0.5 m floor.
- `#380` `scripts/release.py` runs end-to-end without aborting on a pre-existing test failure.
- `#381` Loop "Period" field is now launch-to-launch period; pre-`#381` saves are version-migrated on load.
- `#382` Group `W` button cycles to the next watchable vessel on each press instead of always toggling the same target.
- `#409` Fixed a watch-mode dispatch mismatch for recordings with a loop subrange.
- `#410` Fixed a one-frame `playing → paused → playing` flicker at exact loop-cycle boundaries.
- `#411` Loop playback now uses the effective loop subrange consistently in flight and KSC.
- `#387` Ghost map icons now match stock ProtoVessel icons for each vessel type (Ship, Plane, Probe, Station, …).
- Fixed a per-frame `ResolveLoopInterval` clamp-warning log storm on saves with legacy loop data — one warn per recording per session now.
- `#412` Fixed looping showcase recordings reaching playback with a 0-second period; synthetic recordings auto-derive period and degenerate saves are auto-repaired once on load.
- `InjectAllRecordings` test fixture now purges stale sidecars before writing fresh ones, so re-injects no longer leave orphan files for KSP to sweep.
- `#420` In-game test `CurrentFormatTrajectorySidecarsProbeAsBinary` no longer fails on tree roots that legitimately have no `.prec` sidecar.
- `#421` Ghost audio "AudioClip not found" warnings are now deduped per (ghost, pid, clip) — once per ghost lifetime instead of per loop rebuild.
- `#417`, `#418` Running the in-game test runner's Run All / Run Category twice in the same session no longer compounds leftover ghosts and orphan ghost-map PIDs.
- `#422` Freshly-loaded test saves no longer emit spurious per-tree WARNs when every failure is a synthetic-fixture marker.
- `#413` Replacement kerbals are now seated in the correct part after revert/merge.
- `#414` Ghost visual builds are now throttled to at most 2 per frame, amortizing the scene-load warm-up burst that produced zero-ghost spikes up to ~175 ms. Watch-mode and loop-cycle-rebuild spawns remain exempt so user-visible responsiveness is unaffected.
- `#424` The `Show ghosts in Tracking Station` toggle now responds to flips made from KSP's stock Game Parameters UI.
- `#425` Map-view ghost markers no longer stay stuck on the fallback diamond for an entire scene when the first draw hits an uninitialized prefab or icon array.
- Rewind now filters the ledger walk to actions at or before the rewind UT, so post-rewind T0 no longer re-credits milestones or other post-rewind events. Contract deadlines that expired between the last pre-cutoff action and the rewind target now correctly fail, and tech-research affordability checks use current UT so post-rewind unlocks cannot read future science.
- `InjectAllRecordings` now refuses live-save injection while KSP is actively holding `KSP.log`, and `scripts/collect-logs.py` snapshots live recording sidecars by default under the save bundle while preserving the legacy `parsek/Recordings` copy; `--skip-recordings` still opts out.

### Maintenance

- `#392` Added clarifying comments to the `HasSeed` early-return guards in `PatchScience`/`PatchFunds`/`PatchReputation`.
- `#372` Removed orphaned synthetic-scenario test helpers.
- `#454` `GameStateRecorder.Emit` and `GameStateStore.AddEvent` now take `ref GameStateEvent` so field stamps (`epoch`, `recordingId`) propagate to the caller's local — eliminates the value-type field-mirror bug class that produced #443.

---

## 0.8.1

### Recording System Optimization

- Binary/lossless recording sidecars are cutting recording payload size by 85.17% across 12 comparable packages (1.34 MB authoritative '.prec' / '_vessel.craft' / '_ghost.craft' files versus 9.03 MB readable text mirrors, 7.69 MB saved); April 13, 2026 log-bundle measurements also showed the latest bundle dropping from 745,079 B to 102,845 B (86.20% smaller).
- Phase 11.5 snapshot-storage follow-up now writes `_vessel.craft` / `_ghost.craft` sidecars as lossless header-dispatched `Deflate` envelopes at the highest built-in .NET compression level, while still loading legacy text snapshot files, preserving the existing alias/separate fallback rules, only falling back to vessel visuals when the ghost sidecar is actually missing, and using staged sidecar writes so caught partial-write failures roll back cleanly.
- Recording storage now has a default-on diagnostics flag that writes readable `.txt` mirrors next to the authoritative `.prec`, `_vessel.craft`, and `_ghost.craft` sidecars for comparison/debugging, deletes stale mirrors when the flag is turned off, and includes mirror bytes in the storage diagnostics breakdown without making mirror failures fatal to real saves.

### Improvements

- Phase 11.5 ghost LOD is now live in Flight: shared distance thresholds, unwatched reduced tier at `2.3-50 km`, hidden-mesh tier at `50-120 km`, and live diagnostics counts for `full / reduced / hidden / watched override`.
- Hidden-tier ghosts now unload built mesh/resources while keeping their logical playback shell alive, prewarm shortly before visible-tier re-entry or imminent structural part events, and rebuild from snapshot state without replaying transient puff/audio effects.
- Ghost performance tuning is now backend-owned. The old ghost soft-cap settings and the soft-cap subsystem were removed instead of leaving user-facing knobs that conflicted with the new distance policy.

### Bug Fixes

- `#360` Fresh ghost watch entry now lands on a canonical behind-the-ghost framing (`pitch 12°, heading 0°, 50 m`) instead of copying the active vessel's camera angles, so pad-side watch entries keep the default KSC-side view and the `V` camera-mode toggle no longer drifts the camera around the ghost on repeated presses (`PR #293`).
- `#361` Flat-fallback committed recording loads now heal malformed sidecars whose top-level point/orbit lists start by duplicating the exact `TrackSection` payload and only later resume with a valid tail, so old committed debris/background recordings no longer fail monotonicity validation just because the stale flat fallback survived on disk.
- `#359` Background `TrackSection` flush/merge paths now dedupe real multi-point overlaps instead of only shaving a single boundary frame, and finalize/revert now prune one-point destroyed debris stubs before they can poison commit metrics, so merged recordings stay structurally monotonic and the related recording-integrity / stop-metrics failures are gone (`PR #285`).
- `#358` Trees committed after an in-flight `F5`/`F9` quickload-resume now keep the root rewind save, reserved resource budget, and pre-launch baseline across quickload save, vessel-switch/background, split/promotion, and finalize paths, so destroyed-end merged recordings keep a working `Rewind` button instead of silently losing `R` (`PR #285`).
- `#357` Deferred orbital end-of-playback spawns now recover missed active-vessel handoffs when KSP switches to the spawned vessel without Parsek seeing the normal vessel-switch path, so the live recorder/tree no longer stays attached to the previous vessel through the post-spawn frame sequence.
- `#356` Optimizer boring-tail trims now require the tail after `lastInterestingUT + buffer` to already be the exact final spawn state, so orbiting/docked/suborbital and landed/splashed recordings are only shortened when nothing changes again before the real spawn.
- `#355` Flight anchor-camera ghost playback now restores deferred engine/RCS runtime FX state on the first visible frame after deferred activation, so launch ghosts no longer appear to have their engines off until `Watch Ghost` or KSC playback reapplies the same runtime state (`PR #281`).
- Fresh first-appearance ghost engine audio now waits until the ghost hierarchy is actually active before calling Unity `AudioSource.Play()`, so the retained engine sources no longer emit `Can not play a disabled audio source` warnings on the same deferred first frame that `#355` restored runtime FX/audio state (`PR #282`).
- `#354` Active breakup-continuous tree recordings that end in a stable spawned state now get a fresh terminal snapshot during tree finalization, so effective-leaf orbital end-of-playback spawns no longer reuse the old post-breakup `_vessel.craft` node after the orbit itself has already been corrected.
- `#353` High-warp orbital end-of-playback spawns now trim stable orbital boring tails against real activity instead of stale zero-throttle engine seed artifacts, then propagate stored terminal orbits to the current spawn UT and scrub stale packed-vessel/part atmospheric metadata before `ProtoVessel.Load()`, so stable-orbit recordings resolve around the normal 10-second boring-state buffer and deferred orbital spawns no longer mix an old endpoint state with a later planet rotation on the way into KSP's on-rails `SUB_ORBITAL`/`101.3 kPa` pressure kill path.
- `#352` Pending-tree merge dialogs now evaluate active non-leaf vessels against the current tree structure instead of only committed trees, so breakup-continuous landings and splashdowns default to persist exactly when runtime playback would spawn them.
- Mission-generated tree groups now keep their disband protection even when reparented under custom groups, the Recordings Manager hides the `X` button for those auto-generated `Mission` / `... / Debris` / `... / Crew` groups, and direct disband requests are blocked as a safety net, so tree-owned groups cannot be deleted accidentally (`PR #269`).
- Breakup child ghosts now always build visuals from the crash coalescer's split-time snapshot instead of a later live vessel snapshot, and debris ghosts no longer apply an extra snapshot center-of-mass offset on top of the recorded trajectory point, so freshly separated boosters and debris no longer appear visually ahead of their actual breakup position (`PR #271`).
- Map-view ghost watch actions now use the same availability rules as the main UI, support stop/refresh when re-clicking the watched ghost, and surface explicit "future ghost" / "different SOI" feedback instead of silently failing, so icon-menu and double-click watch interactions stay predictable (`PR #272`).
- Ghost map orbit lines now stay alive through brief same-body same-SOI gaps in orbit-segment coverage, merging equivalent windows while still breaking at real body changes, so continuous orbital journeys no longer flicker or lose their tracking-station/map-view path between adjacent segments (`PR #273`).
- Entering map view while already in watch mode now restores the map camera focus to the watched ghost once its map object and orbit renderer are ready, so watch-mode map transitions no longer strand the camera on the previous target or empty space (`PR #274`).
- Deferred split detection now falls back to decouple-created vessels when KSP never delivers a matching joint-break callback, but only for splits created from the recorded vessel itself, so delayed booster staging is still recorded without sweeping unrelated live debris into the current split (`PR #275`).
- `#350` Boarded-EVA playback now preserves post-board flat trajectory tails when merged `TrackSections` are only a stale prefix, and single-point boarded leaf recordings now count as renderable playback/spawn data, so the last kerbal can finish the visible re-entry path and the final capsule with the re-boarded kerbal now spawns at recording end.
- `#349` Kerbal retirement tracking now preserves raw snapshot crew names alongside the repaired logical owner names, so historical stand-ins still retire/delete correctly after `KerbalAssignment` repair rewrites their ledger rows back to the slot owner.
- `#348` The displaced-stand-in roster fix now preserves retired stand-ins as a special case, so missing retired roster entries are still recreated while truly unused displaced stand-ins stay deleted.
- `#347` Historical stand-in repair now falls back to persisted `KERBAL_SLOTS` as well as the live `CREW_REPLACEMENTS` bridge, so old ledgers with stale stand-in names still heal after the current replacement map has already been cleared.
- `#346` Ghost-only handoff fallback is now limited to unresolved or truly resolved chain segments instead of every snapshot-less chain recording, so auto-committed stable chain tips with `Orbiting`/`Landed`/`Splashed`/`Docked` terminals no longer get incorrectly collapsed to finite `Recovered` reservations.
- `#345` Save-load kerbal-action migration now rewrites stale per-recording `KerbalAssignment` rows instead of only filling missing ones, so legacy ledgers with old stand-in names or pre-fix `Unknown` end states are repaired in place on the next load.
- Load-time kerbal repair diagnostics now emit one concise summary per actual repair on both cold-start and in-session scene loads, covering slot-source fallback, chain-extension repairs, assignment remaps/end-state rewrites/tourist skips, and roster-side retired-stand-in recreation or unused-stand-in deletion while suppressing false positives from reorder-only rewrites and steady-state retired history.
- `#344` Ghost-only chain segments now resolve fallback kerbal end states as finite handoffs instead of open-ended `Unknown` reservations, so mid-chain vessel/EVA commits still reserve crew but no longer poison the reservation graph with indefinite placeholders.
- `#343` `ApplyToRoster()` no longer recreates displaced, unreserved stand-ins just because their chain metadata is still persisted, so deleted stand-ins stay deleted across later recalculation walks instead of churn-appearing and being deleted again every pass.
- `#342` Tourist passengers are now excluded from new `KerbalAssignment` generation, and the kerbals module ignores any legacy tourist assignment actions that already exist in the ledger, keeping tourist contracts out of the managed reservation/stand-in system.
- `#341` EVA-only recordings now populate crew end states during kerbal-action migration and the save-load safety net even when they only carry `EvaCrewName`, so legacy EVA recordings no longer degrade to `Unknown` reservations just because `VesselSnapshot` is null.
- `#340` Permanent-loss slot state is now recomputed on every kerbals walk, and permanently gone owners no longer keep stale stand-ins as active occupants after a prior temporary chain or after rewinding away the death.
- `#339` Replacement-chain reclaim now stops at the first free occupant after the reserved prefix, so deeper free stand-ins are treated as displaced metadata and retire/delete correctly instead of continuing to masquerade as the active slot occupant.
- `#338` Initial save load now initializes the kerbals module before reading `KERBAL_SLOTS`, so persisted replacement-chain state is restored on a cold start instead of being skipped until a later recalculation rebuilds partial slot data from reservations alone.
- `#337` KerbalAssignment creation now reverse-maps stand-in names through `CrewReplacements` before looking up `CrewEndStates`, so later recordings reserve the original slot owner instead of emitting open-ended reservations for the temporary stand-in.
- Recording timing bounds now combine actual trajectory coverage (points, orbit segments, playable track sections) with explicit outer bounds, so watch handoff and playback activation no longer get stuck when a section-authoritative continuation starts before its first flat point while still preserving live background and terminal end times.
- Optimizer boring-tail trims now cut nested section-authoritative frame/checkpoint payloads as well as the flat mirrors, so trimmed tails no longer grow back after save/load, and relative-section splits now resync their flat playback caches the same way the storage format does.
- Optimizer merges now carry the later segment's branch/end-state metadata and remove absorbed recordings from tree ownership cleanly, so merged chains keep branch continuation, terminal state, and tree/background bookkeeping consistent.
- Splashed terminal spawns now floor any slightly negative endpoint altitude back to sea level before spawn, so EVA and breakup-continuous splashdowns no longer materialize a few centimeters underwater (`#313`).
- Section-authoritative recording merges/splits now resync derived flat trajectory lists when the section payload can rebuild them losslessly, and recordings-window stats now use section altitude metadata plus relative-offset distance handling instead of treating relative frames as absolute surface coordinates (`#318`).
- Active-tree restore now keeps a matching in-memory pending tree when the saved active tree hits stale-sidecar epoch failures, and hydration-failed recordings are no longer pruned as disposable zero-point leaves during finalize (`#314`).
- Historical vessel PID reuse across committed trees no longer fails the in-game TreeIntegrity suite: the ambiguous tree-local PID cache is now explicitly `RecordedVesselPids`, cross-tree uniqueness is no longer asserted for archived history, and chain trajectory lookup still falls back globally when a claiming tree has no pre-claim path for the vessel (`#315`).
- Active-tree restore still keeps the full matching pending tree for stale-sidecar epoch failures, and other matched hydration failures now salvage only the failed recordings from the pending tree into the loaded disk tree, marking them dirty so the next save heals the sidecars without jumping the whole restore to the future timeline.
- Snapshot-only hydration failures now heal from the matching pending tree without replacing the loaded disk trajectory, so a bad `_vessel.craft` / `_ghost.craft` sidecar cannot make quickload restore a future-timeline track just to recover snapshot state.
- Separate ghost snapshot sidecars now rewrite on later saves instead of behaving like write-once files, so `_ghost.craft` stays aligned with `ghostSnapshotMode=Separate` after snapshot changes.
- Snapshot-side staged writes now clean transient `.stage.*` / `.bak.*` / `.tmp` artifacts conservatively, and orphan cleanup recognizes any leftovers from interrupted writes instead of leaving them behind forever.
- Mixed background recordings no longer let incomplete `TrackSections` suppress top-level trajectory on disk. Current-format sidecars now fall back conservatively when loaded background sections have not yet captured their checkpoint payload, preserving secondary-vessel and debris orbit continuation across save/load.
- Background `TrackSection` flushes now append their concrete frames back into the live flat `Points`/`OrbitSegments` lists with boundary dedupe, so background playback/UI paths do not need to wait for a save/load round-trip before secondary-vessel trajectory becomes usable.
- Pad-drop launch failures now use a recording-aware pad-local heuristic instead of raw 3D max distance alone, so topple / fall-over launches that never meaningfully leave the pad auto-discard instead of surfacing merge UI (`#324`).
- `#320` Tree-destruction merge confirmation now finalizes immediately in `FLIGHT` when the only remaining blockers are debris, so the merge dialog appears before the stock crash report again without waiting for a revert/fallback owner. Debris and `SubOrbital` leaves also no longer count as spawnable in the merge UI, keeping the default choices aligned with real playback spawn policy.
- `#335` Tree-destruction merge dialogs now wait for deferred joint-break and crash-coalescer resolution before finalizing the tree, so false-alarm crash branches no longer surface empty / `0s` merge dialogs and real breakup children are still attached before the merge UI is built.
- Watched ghosts now use exact watched-state identity (`recording + overlap cycle`) for full-fidelity protection. Overlap copies of the same looping recording no longer inherit watched-only exemptions or diagnostics counts.
- Watch cutoff / zone state for hidden looped ghosts now follows their logical playback position instead of a stale hidden transform, so watch eligibility and auto-exit stay correct while a loop is off-screen.
- Breakup child recordings now preserve split-time ghost visuals even when the child vessel mutates during the coalescing window, and watch transfers immediately bind/log the new watched cycle/target so debris and secondary-vessel handoff failures are diagnosable from `KSP.log`.
- Breakup/crash watch recovery now only auto-follows same-PID continuation. Different-PID breakup fragments no longer steal watch handoff, so crash-end watch now falls back to the normal hold/exit path instead of transferring the camera to debris or another fragment (`#321`).
- Compound-part ghost playback now tracks linked target persistent IDs through spawn-time build and runtime part-event updates, so one-ended detached struts/fuel lines no longer remain visible after separation, destroy, or inventory-removal paths leave their opposite endpoint logically gone (`#322`).
- Watch-mode observability now logs structured camera-focus summaries and W-button eligibility context, so playback focus and watch-button enable/disable state can be correlated from the same log bundle.
- `#317` Horizon-locked watch mode now follows surface-relative prograde during atmospheric playback instead of raw playback velocity, preserving the previous playback/inertial heading outside atmosphere and on airless bodies while adding explicit horizon-basis diagnostics to `KSP.log`.
- `#319` Group watch buttons now resolve the live continuation target across watch auto-follow handoffs instead of evaluating only the original group-main segment after its ghost despawns, so post-transfer group `W` no longer flips to misleading `disabled (no ghost)` while the watched descendant is still active.
- Watching a ghost now keeps its loop-synced breakup debris visible even beyond the normal distance-hiding tier, so long-range booster/debris playback no longer disappears just because the camera stayed on the parent vessel.
- Watched-debris protection now follows same-tree ancestry through branch points as well as loop-sync parents, so secondary breakup fragments inherit the watched ghost's visibility protection even when they are no longer directly loop-synced.
- Playback-driven automatic watch exits now retain same-lineage debris visibility through the last pending descendant debris playback across hold-expiry, high-warp hide, cutoff, policy, and target-loss exits, and failed replacement watch starts no longer clear that retained protection before a new watch session is actually established.
- Zero-throttle breakup debris now emits `EngineShutdown` sentinels instead of looking like a zero-event orphan-engine recording, so replay no longer auto-starts max-throttle booster FX/audio for staged-off debris.
- `#323` In-session `ParsekScenario.OnLoad` no longer reloads stale `GROUP_HIERARCHY` data over the live session state, so root-level `... / Debris` groups keep their intended parentage (or lack of parentage) after commit/playback instead of being silently re-parented by an older save snapshot.
- The old warp-only orbital exemption no longer punches through the new `50-120 km` hidden-mesh tier. Orbital ghosts still get the legacy exemption only in the true `Beyond` zone.
- Entering watch mode now uses the tracked playback distance first, avoiding false "in range" decisions from a hidden ghost's stale transform.
- Flight ghost LOD in watch mode now measures from the live flight camera in flight view and falls back to the active vessel only when no usable scene camera exists or when map view is active, so nearby EVA/parachute secondaries no longer get reduced or hidden just because the active vessel is far away (`PR #260`).
- Automatic watch auto-follow now primes the destination ghost's horizon basis before applying camera compensation, so continuation handoffs no longer spend their first `HorizonLocked` frame using stale orientation state (`PR #255`).
- `#351` Landed/splashed ghosts now reuse distance-aware terrain clearance even on past-end / loop-hold positioning paths, so long-range watched terminal ghosts no longer clip into terrain on their held final pose (`PR #262`).
- `#325` Branched quickload watch handoff now derives pending continuation timing from the child's real ghost-activation UT when available and extends the watched-parent hold window with warp-aware pending-activation timing plus a short post-activation grace period, so delayed same-vessel continuations no longer drop watch just because resumed payload starts later than the branch boundary.
- `#326` EVA branch recordings no longer seed bogus atmospheric start fragments when a landed or splashed kerbal is backgrounded before KSP finishes the vessel switch. The branch path now carries a one-shot surface override through delayed child initialization, and atmospheric-body EVA classification now keeps ground-adjacent or sea-level bobbing kerbals in surface segments instead of producing stray `atmo` optimizer splits.
- `#328` Continuous same-body kerbal EVA recordings no longer split across optimizer `atmo`/`surface` boundaries. The optimizer now keeps continuous EVA atmosphere/surface sections together, repairs older split-at-load pairs by trimming overlapping section payload before flat-point rebuild, and suppresses misleading mixed phase labels so vehicle-exit-through-touchdown EVA stays a single recording.
- Atmospheric-body EVA touchdown follow-ups are now consistent end-to-end: the Recordings table no longer colors suppressed mixed-EVA `Kerbin` rows as if they were exo/orbit segments, loaded EVA touchdowns that pack directly into a landed no-payload on-rails state now persist a surface boundary section so the optimizer keeps the landing as one recording, and trajectory sidecars now stay on the flat fallback path whenever `TrackSections` cannot exactly rebuild the stored flat `Points`/`OrbitSegments` (`PR #266`).
- Tree commits now create `Mission / Crew` subgroups for EVA branches and only re-home stale standalone EVA groups when they still carry Parsek's auto-assigned marker, while grouped mission rows in the Recordings table now nest by tree-local vessel lineage instead of only `ChainId`; a `Kerbal X` mission no longer leaves some same-vessel recordings flat at the mission root while later siblings appear in a separate subgroup (`PR #265`).
- `#220` Crew end-state inference now persists a separate resolved-no-crew state, so 0-point intermediate/probe recordings do not rerun `PopulateCrewEndStates` on every recalculation pass while genuinely missing start-snapshot cases still stay unresolved for later recovery.
- Destroyed debris playback now triggers whole-vessel explosion FX from the earliest eligible recorded destroy event instead of waiting for `EndUT`, so debris that visibly hits the ground no longer hangs before the final blast in either Flight or KSC playback (`#329`).
- Mid-flight active-tree saves now checkpoint the recorder's currently-open `TrackSection` before serializing and immediately reopen a continuation section afterward, so quickload no longer drops the live sparse trajectory chunk and post-separation main-stage playback does not freeze or drift across the missing save/load interval (`#327`).
- `#331` Debris-only breakup false alarms now reopen the active recorder's continuation `TrackSection` from the latest closed-or-discarded section context, restore packed `OrbitalCheckpoint` on-rails state, and seed the boundary frame when appropriate so section-authoritative main-stage playback no longer truncates or drift-resume at the first false-alarm separation boundary.
- `#336` Breakup/background child recordings now seed split-time trajectory only from genuinely captured split poses, keep destroyed coalescer-window children from losing that seed, and resolve seeded-orbit spawn/loop/chain endpoints from the true orbit end instead of snapping back to the separation frame.
- `PR #280` Breakup child recordings now capture exact decouple-time split seeds and root-part surface poses instead of stamping the later deferred split-check frame, and fresh ghost first frames clamp back to the activation start if visibility wakes slightly late, so radial boosters and other breakup debris no longer appear slightly ahead of the true separation point on first appearance.
- Manual watch-button retargets now preserve the live watch camera orbit and compensate for target-frame changes (`cameraPivot`/`horizonProxy`) when switching between ghosts, so hopping between vessel/EVA watch targets no longer resets to the default `50 m` under/overhead entry angle (`PR #267`).
- The in-game test runner window now uses a more compact layout without the visible blank rows between tests, marks destructive scene-transition checks as `[single]`, and skips those single-run-only quickload canaries during `Run All` / category batches instead of driving them through a normal FLIGHT test run.
- `#332` Running the in-game FLIGHT suite no longer batches the destructive quickload scene-transition canaries that were leaving KSP in a broken `flightReady=false / activeVessel=null` state after stock quickload blew up, and the synthetic live `ParsekScenario.OnSave`/`OnLoad` round-trip tests were removed from the in-game suite entirely. The settings window still keeps a stable tooltip layout every frame, and the quickload canaries remain available as explicit single-run diagnostics.

### Developer Tools

- Verbose ghost FX/audio/visual diagnostics in hot rebuild paths now use shared rate-limited keys, so repeated engine/RCS ghost rebuilds no longer flood `KSP.log` every cycle and instead roll up into periodic `suppressed=N` summaries.
- Added regression coverage for fresh watch-entry camera-state prep, pinning the atmospheric/orbital initial-mode resolution and the default entry-distance behavior behind `#360` / `PR #288`.
- Added regression coverage for malformed flat-fallback committed sidecars whose duplicated `TrackSection` prefix leaves a later monotonic tail, so load-time healing now preserves the real suffix and rewrites the sidecar cleanly on the next save (`#361`).
- Added regression coverage for quickload-resume rewind metadata propagation and background recording integrity, including root rewind-budget fallback, recorder-backed commit-path wiring, overlap-aware flat-tail preservation, destroyed-stub prune guards, and the refreshed in-game runtime log-contract assertion behind the `#358` / `#359` fallout.
- Added regression coverage for missed vessel-switch recovery after deferred orbital spawns, including the pure guard cases and the `Update()` ordering requirement that recovery run before other tree transition handlers (`#357`).
- Added regression coverage for terminal-state-aware boring-tail trim gating, including exact-match orbit/surface stability checks and the `SubOrbital` orbit-tail path so trims only happen once the spawn state has truly stopped changing (`#356`).
- Added regression coverage for deferred ghost runtime FX restore, including tracked current-power collection/clearing and first-activation restore gating so anchor-camera launch ghosts keep their engine plume/audio state on the first visible frame (`#355`).
- Added regression coverage for high-warp orbital spawn hardening and stable-orbit tail trimming: terminal-orbit spawn-state selection, top-level and per-part orbital metadata normalization, and zero-throttle engine-seed filtering in boring-tail trim so stable orbital coasts resolve on the normal 10-second buffer instead of replaying their full tail (`#353`).
- Expanded ghost-visual frame coverage so breakup child ghosts stay anchored to split-time snapshots without a duplicate debris center-of-mass offset (`PR #271`).
- Added regression coverage for map-view ghost watch actions and watched-ghost focus restore, including start/stop/refresh behavior plus future/different-SOI refusal paths (`PR #272`, `PR #274`).
- Added regression coverage for same-SOI orbit-gap display and decouple-only split detection, including equivalent-window carry rules plus recorded-parent filtering for delayed booster staging (`PR #273`, `PR #275`).
- Added regression coverage for boarded-EVA re-entry playback: board-merge stale-section tail preservation, single-point ghost renderability/state seeding, and direct stale-track fallback serialization for `#350`.
- Added regression coverage for exact split-branch timing and fresh first-frame playback gating, including deferred split branch UT resolution preferring captured decouple seeds plus the narrow activation-start clamp for newly visible breakup ghosts (`PR #280`).
- Added cold-start/load-path regression coverage for kerbal repair convergence: persisted slot restoration, EVA-only end-state repair, mixed tourist/remap/end-state migration, reorder-only assignment rewrites, chain-extension diagnostics, failed retired-stand-in recreation, and both facade-path and minimal real-roster `ApplyToRoster()` checks.
- Documented the kerbals hardening follow-up plans in `docs/dev/plans/` and refreshed `todo-and-known-bugs.md` to close the cold-start slot-migration and load-diagnostics follow-ups while narrowing the remaining real-roster `ApplyToRoster()` coverage gap.
- Added regression coverage for R/FF enablement reasons, including future/past timing, tree-branch rewind save resolution, and a UI guard that pins rewind/fast-forward independence from watch-distance state (`T60`).
- Added regression coverage for compressed snapshot sidecars: legacy/new mixed corpora, alias/separate/ghost-only fallback behavior, corrupt/unsupported/oversized-envelope rejection, snapshot-hydration failure surfacing without misleading fallback logs, snapshot-only quickload salvage that preserves disk trajectory and alias invariants, transient sidecar-artifact cleanup, and staged-write rollback/heal behavior for both first-write and stale-ghost-delete branches.
- Added regression coverage for readable sidecar mirrors: default-on mirror generation, mirror cleanup when the flag is disabled, orphan/transient mirror cleanup, diagnostics byte accounting, scenario fixture generation, and non-fatal mirror reconcile failures that must not roll back authoritative sidecars.
- Added regression coverage for exact watched-cycle protection, hidden-tier warp exemption, watched-override diagnostics counting, and the new frame-context watch-cycle field.
- Added regression coverage for hidden-tier shell-state handling so unloaded ghosts keep their logical loop identity and rebuild paths preserve playback bookkeeping.
- Added regression coverage for historical PID reuse across committed trees, `BackgroundMap` duplicate detection, overlapping rewind continuations in `GhostChainWalker`, and chain trajectory lookup that prefers chain trees without dropping the global pre-claim fallback (`#315`).
- Added regression coverage for effective group watch-target resolution across single-hop and multi-hop handoffs plus parity checks for breakup/non-breakup fallback behavior, so the group `W` button stays aligned with real watch auto-follow semantics (`#319`).
- Added regression coverage for watched-lineage debris visibility, so watch-mode protection now stays pinned to the intended same-tree same-vessel debris path instead of only the exact watched recording row.
- Added archived-topology regression coverage for issue `#316`, including missing loop-sync parents after chain splitting, retained watched-lineage protection through late debris end times, and the current non-retarget-to-debris watch-target rule.
- Added focused regression coverage for compound-part target PID parsing and logical link-visibility decisions, including missing-target, inactive-target, source-removed, and subtree-removal cases behind `#322`.
- Added regression coverage for zero-throttle engine seeding vs. orphan-engine auto-start, so staged-off debris boosters are pinned against replaying as visually full-throttle.
- Added regression coverage for pending watch activation after branched quickloads, including actual-payload-start precedence over stale explicit UT, warp-aware hold sizing, and capped post-activation grace handling (`#325`).
- Added `#323` regression coverage for in-session hierarchy preservation: unit coverage pins the load-policy gate so a root-level debris group survives a stale saved hierarchy without being re-parented.
- Added regression coverage for EVA branch surface seeding and atmospheric/splashed EVA environment classification, covering the queued background override path and the near-ground / sea-level EVA surface heuristics behind `#326`.
- Added regression coverage for continuous EVA `atmo`/`surface` optimizer behavior, including split suppression for live same-body EVA sections, repair of already-split overlapping loaded pairs, and mixed-label UI formatting (`#328`).
- Added regression coverage for mixed-EVA phase styling, no-payload loaded->on-rails touchdown boundary persistence, and text/binary sidecar fallback serialization for recorder loaded->on-rails transitions (`PR #266`).
- Added regression coverage for EVA crew subgroup commits/adoption, auto-assigned standalone-group marker persistence/clearing, and grouped-tree vessel nesting/order in the Recordings table (`PR #265`).
- Added regression coverage for resolved-no-crew end-state bookkeeping, including missing-start-snapshot behavior and save/load persistence of `crewEndStatesResolved` (`#220`).
- Added regression coverage for save-time `TrackSection` checkpointing across active, relative, and orbital-checkpoint sections, and saved a reusable `tools/inspect-recording-sidecar.ps1` inspector for decoding `.prec` sparse trajectory payloads while debugging storage/playback issues like `#327`.
- Destructive live `ParsekScenario.OnSave`/`OnLoad` round-trip tests were removed from the in-game suite, quickload wait helpers fail with explicit scene context, and destructive F5/F9 scene-transition tests are single-run only instead of part of normal FLIGHT batches (`#332`).
- Added regression coverage for false-alarm recorder resume restoring absolute, relative, and orbital-checkpoint `TrackSection` continuity, including discarded-section metadata recovery and on-rails resume state for `#331`.
- Added regression coverage for manual watch-camera transfer mode resolution in the unit suite and for transferred watch-angle compensation in the in-game runtime suite, pinning the manual watch-switch camera fix against both `50 m` reset regressions and target-basis snap regressions (`PR #267`).
- Added regression coverage for post-destruction merge resolution waiting on pending crash cleanup, so debris-only crash ordering does not regress into empty-tree / wrong-duration merge dialogs while deferred split or coalescer work is still finishing (`#335`).
- Added regression coverage for split-time breakup seeding, destroyed coalescer-window child retention, seeded single-point-orbit activation handoff, and seeded-orbit endpoint resolution across spawn/chain/playback paths (`#336`).
- Diagnostics now report live engine/RCS FX counts plus last-frame ghost spawn/destroy timings, giving a measurement-first view of FX cost without changing FX behavior.
- `scripts/inject-recordings.ps1 --run-diagnostics-tests` now runs the focused diagnostics/observability slice before showcase injection, including observability logging and in-game test runner ordering coverage.

---

## 0.8.0

### Breaking Changes

- Recordings from 0.7.x saves are no longer loaded. The standalone RECORDING format has been removed; only tree recordings (RECORDING_TREE) are supported. Start a fresh save or re-record flights.

### Improvements

- Phase 11.5 recording storage groundwork: added representative storage fixtures, golden round-trip coverage, and `v1` `.prec` sidecars that make `TrackSections` authoritative on disk instead of duplicating flat `POINT` / `ORBIT_SEGMENT` trajectory data.
- Recording sidecars now alias identical ghost snapshots to `_vessel.craft` via `ghostSnapshotMode` metadata instead of always writing a duplicate `_ghost.craft` file. Diagnostics and load paths understand alias mode and stale ghost sidecars are cleaned up on save.
- Phase 11.5 storage now writes current-format `.prec` sidecars as compact `v3` binary files with `PRKB` header dispatch, exact scalar payloads, a file-level string table, and conservative sparse defaults for stable per-point body/career fields. Legacy text `v0` / `v1` sidecars and binary `v2` sidecars still load, and the fixture/generator path covers mixed-format corpora.
- Migrated 9 log contract checks from post-hoc KSP.log analysis to in-game tests (Ctrl+Shift+T) -- catches format, resource, and recording metric issues at runtime instead of after session ends.
- Unified standalone and tree recording systems -- all recordings now use tree architecture internally (#271).
- Optimizer now splits tree recordings at environment boundaries, restoring per-phase segment display in the UI.
- Removed standalone RECORDING format entirely (T56) -- deleted pending slot, standalone merge/chain dialogs, standalone serialization, legacy migration shim. All committed recordings now require tree ownership.
- `#286` Merge dialog now explains when no flight branches produced a vessel that can continue flying.

### Bug Fixes

- Recording tree metadata now preserves the last written `ghostSnapshotMode` instead of recomputing it from live snapshots on every save, preventing `.sfs` alias/separate drift from disagreeing with the actual sidecar files on disk.
- `#307` Rewind (R button) now works for recordings committed after an in-flight vessel switch -- the rewind save is now copied to the tree root in both vessel-switch flush paths.
- `#308` Reserved kerbals no longer appear auto-assigned in the VAB/SPH crew dialog -- new Harmony patch replaces reserved crew with their stand-ins before the dialog builds.
- `#309` Rovers and ghosts recorded on the Island Airfield (or launchpad/KSC buildings) no longer spawn 19 m underground -- recording now captures the true surface height from KSP's raycast-derived `vessel.terrainAltitude` instead of PQS-only terrain, and spawn/ghost altitudes trust the recorded value with only an underground safety floor against PQS.
- `#310` Spawn collision detection now uses `Physics.OverlapBox` against real part colliders instead of a 2 m-cube blocker approximation, so large vessels (stations, planes, carriers) block spawns correctly.
- `#311` Walkback spawns on diagonally-descending trajectories are now snapped to the true surface via top-down `Physics.Raycast`, preventing mid-air spawns that fall.
- `#312` Placing multiple showcase ghosts of the same vessel type near each other no longer causes each new spawn to destroy the previous one -- the duplicate-blocker recovery now requires a PID match against the recording's own previous spawn, so siblings correctly fall through to walkback.
- `#241` Ghost parts with the base/default color variant (e.g., BlackAndWhite fuel tanks) no longer show the wrong variant texture (was Orange).
- `#297` Vessel destruction during tree recording no longer orphans continuation data as a standalone recording.
- `#304` Stock vessel recordings now show resolved names instead of raw `#autoLOC` keys in the UI, timeline, and logs.
- `#305` Standalone recordings (rovers, planes) now survive revert-to-launch and show the merge dialog instead of being silently discarded.
- `#306` Ghost engine nozzles no longer glow red permanently -- inherited prefab emissive values are cleared to black at build time. Thermal glow now driven purely by temperature thresholds (cool <40%, warm 40-80%, hot >80%), decoupled from engine/RCS throttle.
- Landed ghost terrain correction now clamps downward when terrain height decreased since recording (was only clamping upward, leaving ghosts floating above ground).
- Test runner window no longer spams IMGUI layout exceptions when opened during flight.
- `T55` FlagEvents and SegmentEvents are now preserved across tree recording splits and flushes.
- `#298b` Dead engine shutdown sentinels now work for active-vessel recordings (were only emitted for background vessel recordings).
- `T59` Rewind (R button) now works after mid-recording EVA -- rewind save filename and budget are copied to the tree root at branch time.
- `#270` Quickloading no longer loads stale sidecar trajectory data from a later save point.
- `T58` Debris ghost engines no longer show running FX at zero throttle after staging -- inherited engine state respects the child vessel's operational assessment.
- `T57` EVA recordings now spawn at end instead of being abandoned -- parent vessel is exempt from collision checks during EVA walkback.
- `#290` Five cascading bugs caused total recording loss on F5/F9 quickload: epoch drift, vessel name matching, idle-on-pad false positives, missing MaxDistanceFromLaunch, and Finalized tree overwrite by stale .sfs.
- `#290` Spawn-at-end no longer places vessels below runway/launchpad structures when the vessel was unloaded at scene exit.
- `#290` Phase column now correctly shows "surface" for landed/splashed vessels instead of "atmo"; LaunchSiteName now populates during OnSave for tree recordings.
- `#290` Debris and EVA branch recordings in the active tree no longer lose trajectory data after two cold starts (quit KSP mid-flight, relaunch, quit again).
- `#242` Ghost PREFAB_PARTICLE FX (smoke, small engine flames) no longer fires sideways -- auto-detects parent transform orientation and applies -90 X correction when needed.
- `#242` RAPIER and Panther ghost FX now shows only the active engine mode (jet or rocket) instead of both simultaneously.
- `#242c` Ghost engine and RCS FX on multi-variant parts (Poodle, Terrier, RV-105, etc.) now only fires from the selected variant's nozzles instead of all variants simultaneously.

### New Features

- Watch camera mode: press V during ghost watch to toggle between free orbit and horizon-locked (ground always at screen bottom). Auto-selects horizon-locked near planetary surfaces, free in orbit.
- Sortable "Site" column in the Recordings Manager — sorts by launch site name with UT tiebreak within the same site.
- Renamed the expanded-stats toggle button from "Stats" to "Info".
- Resource snapshots: recordings capture physical resource manifests (LF, Ox, Ore, etc.) at start and end. Hover tooltip in Recordings Manager shows resource changes per recording. Prerequisite for logistics routes.
- Inventory manifests: recordings capture stored inventory items (count, slots) at start and end. Hover tooltip shows inventory changes per recording.
- Crew manifests: recordings capture crew by trait (Pilot/Scientist/Engineer/Tourist) at start and end. Hover tooltip shows crew changes per recording.
- Dock target vessel PID captured at chain boundaries for future route endpoint identification.

---

## 0.7.2

Dev notes: technical narratives for the fixes below live in `docs/dev/todo-and-known-bugs.md` under the matching bug numbers. Commit messages have the full root-cause detail per PR.

### Bug Fixes — Name resolution & ghost positioning

- Vessel names in split/EVA/background recordings now resolve KSP localization tags (e.g., `#autoLOC_501232` → "Kerbal X") instead of displaying raw tags.
- `#282` Landed ghosts now sit at their natural recorded height above terrain instead of floating 4m up, using `Recording.TerrainHeightAtEnd` when available (NaN fallback preserves old behavior for legacy recordings).
- `#BugC` Switching to a spawned vessel no longer fills empty seats with extra kerbals — crew swap skips Parsek-spawned vessels entirely.

### New Features

- Ghost positional audio: 3D engine sounds, decoupler pops, and explosions with distance fade and atmospheric attenuation. Volume slider in Settings. Compatible with RocketSoundEnhancement.
- Start/End position columns in the expanded recordings table — launch site, EVA source vessel, and situation/biome/body for start; terminal state with location context for end.
- Per-recording storage tooltip in the Recordings Manager (file sizes + storage efficiency on hover).

### Developer Tools

- Recorder state observability: `grep "[RecState]" KSP.log` now shows every recorder lifecycle boundary as a single structured line (mode, active recording id, buffer levels, pending slots, chain state, sequence numbers). Diagnoses F5/F9 / EVA / dock / undock bugs in one grep pass.
- Diagnostics report accessible via Ctrl+Shift+T or Settings > Diagnostics — storage, memory, frame budget, save/load timing, health counters. Dumps to KSP.log.
- Automatic rate-limited `[WARN]` when playback exceeds 8ms/frame or recording exceeds 4ms/frame.
- New `ParsekLog.WarnRateLimited` API.

### Bug Fixes — Quickload-resume hardening

- `#293` F5/F9 during a multi-stage (tree) recording no longer kills the recording — quickload seamlessly resumes where the quicksave was made.
- `#294` F5/F9 during a single-vessel recording now seamlessly resumes recording from the quicksave point instead of silently stopping.
- `#267` Restore coroutine reentrancy guard: `OnVesselSwitchComplete`, `OnVesselWillDestroy`, and `FinalizeTreeOnSceneChange` now skip while the quickload-resume or vessel-switch restore coroutine is mid-yield.
- `#268` Belt-and-braces snapshot capture in `StashActiveTreeAsPendingLimbo` ensures null-snapshot leaves get a fresh vessel snapshot while the vessel is still alive, before the scene reload.

### Developer Tools

- `#269` Test runner now survives scene transitions (`Instantly + DontDestroyOnLoad`), enabling multi-scene coroutine tests. Three QuickloadResume in-game tests added: bridge canary, mid-recording resume identity, reentrancy guard verification.
- `#269` QuickloadResume's mid-recording canary now self-primes through a real launch-triggered auto-record before F5/F9, so it no longer depends on manual pad setup while still covering an in-flight recording.
- `#265` Nine in-game tests added for code paths xUnit can't reach (AudioSource dependency): ghost audio pause/unpause, terminal orbit backfill from orbit segments, and part-state seeder consistency.

### Bug Fixes & Maintenance

- `#302` Tree recordings no longer falsely auto-discarded as "idle on pad" after a scene change — max distance from launch was lost during save/load, making every recording appear to have never left the pad.
- `#303` Ghosts outside the physics bubble no longer clip underground — terrain LOD mismatch at distance caused the 0.5m clearance floor to be insufficient; now lerps from 2m at the bubble edge to 5m at 120km.
- **Fix ghost icon popup appearing at screen center instead of near cursor (#196).** Matched KSP's stock `MapContextMenu` positioning pattern: (0,0) anchors, forced layout rebuild, and `AnchorOffset` so the menu opens below the click point.
- `#283` Fixed ghost position pops at TrackSection boundaries by seeding the new section with the closing section's boundary point; also skips spurious cross-reference-frame discontinuity warnings.
- `#298` Ghost engine/RCS flames now auto-start on debris booster ghosts whose engines were running at separation. Debris recordings also get proper EngineIgnited seed events inherited from the parent vessel at decouple time.
- `#299` Terminal EngineShutdown events no longer survive into committed recordings when a standalone recording promotes to a tree during breakup.

- `#291` EVA spawn walkback now correctly detects the active vessel (parent rocket) as a blocker, so kerbals walk back to a clear position instead of spawning on top of their parent vessel.
- `#285` Background vessel splits no longer create empty parent-continuation recordings when the parent vessel is already destroyed.
- `#288` Ghost map icons now appear immediately after re-entry from orbit instead of requiring W (Watch) to be pressed first.
- `#289` End-of-mission spawn-at-end now correctly materializes splashed-down vessels and EVA kerbals after a rewind+merge.
- `#292` F9 quickload after a Tree Merge no longer silently drops recordings created during the merge.
- `#298` Rewind buttons no longer blocked by "Merge or discard pending recording first" after a tree merge — pending standalone recordings are auto-committed.
- `#299` EVA kerbal crew end states now correctly inferred (previously showed Unknown with infinite reservation because snapshot crew extraction fails for EVA vessels).
- `#300` Phantom terrain crash detection: EVA kerbals destroyed by KSP's pack-time terrain collision within 5s of a safe (Landed/Splashed) state are classified as Landed instead of Destroyed.
- `#301` Vessels alive at scene exit no longer marked Destroyed — terminal state inferred from trajectory when vessel unloaded during KSC transition.
- `#290a` Pending Limbo tree no longer discarded on revert — fixes lost debris recordings and missing proto vessels after merge-then-revert.
- `#290b` Ghost engine skirt/shroud now hidden at build time when the snapshot shows the shroud was already jettisoned.
- `#290c` Ghost map icon no longer flickers during time warp — zone warp exemption threshold lowered from >4x to >1x, and ghost warp suppression is now skipped in map view so icons stay visible at any warp speed.
- `#287` Ghost engine flames on multi-stage rockets (Kerbal X Mainsail + boosters) no longer turn off permanently after booster staging.
- `#278` EVA kerbals walking at the moment of revert/vessel-switch are now correctly classified Landed instead of Destroyed, so the merge dialog can spawn them at end of recording.
- `#277` Stand-in crew is now placed correctly when a launch crew member was on EVA at merge time.
- `#284` Background debris recording capped at primary debris only — cuts the recording count from ~25 to ~4-6 entries per Kerbal X launch.
- `#282` Landed ghost vessels and end-of-recording respawned vessels no longer clip into terrain.
- `#297` Map view now shows custom icons for all active ghost vessels, including atmospheric/landed ghosts beyond visual range.
- `#295` Vessels that sit idle on the launch pad are now auto-discarded instead of triggering a merge dialog.
- `#300` Revert-to-launch on a first flight no longer silently loses the recording — the merge dialog now appears correctly instead of the recording being mistaken for a quickload resume.
- `#280` Background debris recordings now persist their trajectory data across scene reloads.
- `#281` Decouple events on mixed-symmetry stages are no longer lost when a terminal-event collision occurs at the same physics frame.
- `#272` F5/F9 no longer destroys the entire launch tree.
- `#273` Tree recording trajectory is no longer silently lost across scene reloads.
- `#274` F9 after EVA resumes the tree instead of finalizing it.
- `#276` F5 → fly → F9 no longer commits the intermediate flight as an orphan recording.
- `#275` Watch buttons in the Recordings Manager now show explanatory tooltips for every disabled state.
- `#264` EVA kerbals spawn at their exact recorded walked position instead of on top of the parent vessel.
- `#263` Ghost boosters are no longer left visible on the ghost after a symmetry-group decouple.
- `#266` Switching to a distant vessel via the Tracking Station now preserves the in-progress mission tree instead of finalizing it on scene reload.
- Quicksave + quickload during an active recording no longer fragments the mission, produces partial ghost orbits, or scatters crew across orphan recordings.
- `#244` Ghost no longer stays icon-only for the entire Kerbin→Mun transfer.
- `#246` EVA on an airless body no longer generates multiple "approach" segments.
- `#248` EVA boarding is no longer misclassified as vessel destruction.
- `#254` Capsule spawned with wrong crew — now reverse-maps stand-ins through `CrewReplacements` before inferring end states.
- `#245`/`#247` Ghost map markers no longer appear at wrong positions for hidden ghosts.
- `#243` Watch camera now honors the distance cutoff for orbital recordings.
- `#250` End column no longer shows "-" for chain mid-segments.
- `#249` Planted flags are now visible during ghost playback when the ghost is in the Beyond zone.
- `#251` Recording phase label now updates after SOI change.
- `#255` Engine FX no longer killed during ghost playback after a false-alarm booster decouple.
- `#259` Orbital recordings now capture `TerminalOrbitBody` on every terminal-state path.
- `#258` Non-chronological trajectory points from quickload mid-recording are trimmed.
- `#257` Hyperbolic escape orbits no longer fail the `OrbitSegmentBodiesValid` in-game test.
- `#260` Removed dead `.pcrf` ghost geometry scaffolding.
- `#261` Diagnostics playback budget shows `N/A` instead of `0.0 ms` when no data available.
- `#262` Diagnostics storage scan no longer warns about recordings with null snapshots.
- `#264` follow-up: `StripEvaLadderState` no longer writes an invalid literal to the EVA FSM `state` field.
- `#256` Capped slow-motion trajectory sampling; new `Min sample interval` slider in Settings (default 0.2 s).
- `#252` Group hide checkbox now toggles all member recordings.
- Ghost orbit line now suppressed below atmosphere for segment-based ghosts.
- Rover recordings no longer include stale chain-promotion seed events.
- Ghost audio now mutes when the ESC pause menu is open.
- Ghost camera cutoff persists across rewind and KSP restarts.
- Closed `#156` (lifecycle test coverage — decision logic already extracted to pure/static methods with full coverage).
- Closed `#188` (spawned surface vessels in map view are real KSP vessels, expected).
- Deferred `#189b` (ghost escape orbit line) to Phase 11.5.
- Deferred `T43` (mod compatibility testing) to the last phase of the roadmap.
- Bump version to 0.7.2.

### In-Game Tests added

- `SpawnHealth` (3 tests) — regression coverage for `#132`, `#112`, `#149`.
- `ContinuationIntegrity` (2 tests) — regression coverage for `#95`.
- `RewindSaves` (1 test) — regression coverage for `#159`, `#166`.
- `TerminalOrbit` (2 tests) — regression coverage for `#203`, `#219`.
- `CrewReservationLive` (2 tests) — regression coverage for `#233`, `#46`.

---

## 0.7.1

### Bug Fixes

- **Fix crash breakup debris not recorded when recorder tears down before coalescer (#218).** `ShowPostDestructionMergeDialog` stopped the recorder after one frame, but the crash coalescer's 0.5s window hadn't expired yet. By the time the BREAKUP event emitted, no recorder existed to attach it to. Now waits for the coalescer to finish before proceeding, with a 5s real-time timeout for safety. Continuation recorder marked `VesselDestroyedDuringRecording` after tree promotion to prevent tree dialog guard from incorrectly aborting.
- **Fix spawn permanently blocked by duplicate vessel after rewind (#112).** After rewind, a quicksave-loaded duplicate of a spawned vessel could survive cleanup and permanently block the spawn position. Added defensive duplicate recovery in `CheckSpawnCollisions`: when a collision blocker's name matches the recording's vessel name, recover the blocker once then re-check. `DuplicateBlockerRecovered` flag prevents recovery loops. Also fixed pre-existing gap where `CollisionBlockCount`/`SpawnAbandoned` survived rewind (now reset by `ResetRecordingPlaybackFields`).
- **Fix atmospheric ghost markers not appearing in Tracking Station (#240).** `OnGUI` had a terminal state filter that skipped non-Orbiting/non-Docked recordings, blocking atmospheric trajectory markers for SubOrbital, Destroyed, Recovered, and Landed recordings even during their active flight window. The UT range check already handles temporal visibility correctly. Extracted `ShouldDrawAtmosphericMarker` as testable pure method.
- **Fix delayed proto-vessel ghost creation after merge dialog commit.** When a recording was committed via the merge/approval dialog while in the Tracking Station, proto-vessel ghosts took up to 2 seconds to appear (waiting for the lifecycle tick). Now detects committed recording count changes and forces an immediate lifecycle tick.
- **Fix deferred spawn queue split-brain (#132).** `HandlePlaybackCompleted` in `ParsekPlaybackPolicy` added deferred spawn IDs to the policy's `pendingSpawnRecordingIds`, but `FlushDeferredSpawns` in `ParsekFlight` read from its own never-populated duplicate set. Deferred spawns during warp silently never flushed. Moved `FlushDeferredSpawns` to the policy, eliminated the duplicate fields.
- **Wire up spawn-death detection (#132).** `RunSpawnDeathChecks` now runs before each engine update, detecting spawned vessels that died since last frame. Increments `SpawnDeathCount`, resets for re-spawn, or abandons after 3 cycles. Previously `SpawnDeathCount` was never incremented in the engine path.
- **Fix mid-tree spawn entries at EVA/staging boundaries (#227).** When a kerbal EVA'd or a stage separated, the timeline showed a premature "Spawn: Kerbal X" at the branch time. Two-sided fix: (1) suppress parent spawn when a same-PID continuation child exists via new `HasSamePidTreeContinuation` helper, (2) allow tree-child leaf recordings to produce spawn entries. Breakup-only recordings (no same-PID continuation) correctly still spawn.
- **Fix toolbar icon texture compression warning (#154).** Replaced 38x38 and 24x24 toolbar icons with 64x64 and 32x32 power-of-two versions. KSP can now DXT-compress the textures.
- **Fix ghost creation failing for orbital debris ("no orbit data") (#219).** `CaptureTerminalOrbit` only ran when `FindVesselByPid` returned a live vessel. Orbital debris with 30s TTL was often destroyed by finalization time, leaving terminal orbit fields empty. `PopulateTerminalOrbitFromLastSegment` now recovers orbit data from the last `OrbitSegment`.
- **Fix green sphere ghost for debris destroyed during coalescing window (#157).** Pre-capture vessel snapshots at split detection time and carry them through the CrashCoalescer. When `CreateBreakupChildRecording` runs 0.5s later and the vessel is destroyed, the pre-captured snapshot provides `GhostVisualSnapshot` instead of falling back to a green sphere.
- **Fix Settings window GUILayout exception (#217).** The ghost soft caps toggle caused an IMGUI Layout/Repaint mismatch: the early `return` in `DrawGhostSettings` skipped slider controls when caps were disabled, but a toggle click between passes changed the control count. 72 exceptions per session, window stuck at 10px. Fix: sliders always drawn, grayed out via `GUI.enabled` when caps disabled.
- **Fix W (watch) button staying enabled on debris boosters (#194).** After booster separation, one debris recording could have an active ghost with the W button enabled. Added `IsDebris` guard to watch eligibility check and "Debris is not watchable" tooltip.
- **Fix vessels and EVA kerbals spawning high in the air (#231).** Vessels with `terminal=Landed` spawned at their last trajectory point altitude (still descending), fell, and exploded. Three spawn paths lacked altitude clamping (flight scene, KSC, tree leaves). Now all paths clamp LANDED to terrain+2m clearance, override snapshot position AND rotation from the last trajectory point. Vessels spawn in their near-landing orientation instead of mid-flight descent pose.
- **Fix ghost map markers missing / wrong positions after save/load (#203).** `SaveRecordingMetadata` / `LoadRecordingMetadata` (standalone recordings) never serialized the 8 terminal orbit fields. After save/load, all standalone recordings had `TerminalOrbitBody = null`, so `HasOrbitData` returned false and no ghost map ProtoVessels could be created. Tree recordings were unaffected (separate serialization path). Now serialized in both paths.
- **Fix tree root recording showing T+ countdown instead of terminal state (#186).** Continuation sampling extends a committed recording's `EndUT` past current time, causing the status column to show "T+5m 23s" instead of "Landed". Now checks `TerminalStateValue` in all three status paths (row display, group aggregate, sort key). Group status also shows the best non-debris terminal state instead of generic "past".
- **Fix green sphere fallback for debris ghosts with no snapshot (#232).** Debris from mid-air booster collisions had no vessel snapshot, causing distracting green spheres during watch mode playback. Now skips ghost creation entirely for snapshotless debris. Non-debris keeps sphere fallback as safety net.
- **Fix continuation data persisting through revert (#95, items 3-5).** After EVA or undock, the continuation system appended trajectory points and overwrote snapshots on already-committed recordings. On revert/rewind, these mutations persisted — ghosts showed trajectory from an abandoned timeline. Fix: `ContinuationBoundaryIndex` tracks the commit-time point count; pre-continuation snapshots are backed up. On normal stop, the boundary is cleared (data baked as canonical). On revert, `RollbackContinuationData` truncates points and restores snapshots. All 8 stop sites audited: 5 bake (normal lifecycle transitions), 3 don't (vessel destroyed — revert undoes destruction).
- **Fix R (rewind) button missing on tree branch recordings (#159, #166).** Tree branch recordings (EVA kerbals, decoupled stages) had no `RewindSaveFileName` because rewind saves are only captured at launch. Added tree-aware lookup: `GetRewindRecording` resolves through the tree root so branches can rewind to the original launch point. `InitiateRewind` and `ShowRewindConfirmation` now use the owner recording's fields for correct vessel stripping, UT display, and future-recording count.
- **Fix timeline not refreshing after commits, rewinds, and KSC spending.** `LedgerOrchestrator.OnTimelineDataChanged` callback now wires to `TimelineWindowUI.InvalidateCache()`, ensuring the timeline view refreshes when data changes from commits, rewinds, time warp, KSC spending, and game load.
- **Fix FormatDuration overflow for long careers.** Changed `int` → `long` to support durations exceeding 68 years.
- **Fix timeline footer VesselSpawn count.** Footer now correctly counts all Recording-source entries instead of skipping VesselSpawn via early `continue`.

### Spawn System Hardening

- **Per-part identity regeneration on spawn (#234).** `RegenerateVesselIdentity` now regenerates per-part `persistentId` (via `FlightGlobals.GetUniquepersistentId`), `flightID` (via `ShipConstruction.GetUniqueFlightID`), `missionID`, and `launchID` — not just vessel-level GUID. Previously, spawned copies shared all part PIDs with the original vessel, causing tracking station/map view conflicts and likely contributing to #112 (spawn blocked by own copy). Uses delegate injection for unit testability.
- **G-force suppression after spawn (#235).** `IgnoreGForces(240)` now called on newly spawned vessels in both `RespawnVessel` and `SpawnAtPosition`. Without this, KSP calculates extreme g-forces from position correction after `ProtoVessel.Load()` and can destroy the vessel immediately. The existing `MaxSpawnDeathCycles = 3` guard was treating this symptom.
- **Global PID registry cleanup (#237).** Old part `persistentId` values are now removed from `FlightGlobals.PersistentUnloadedPartIds` before assigning new ones during identity regeneration. Prevents phantom entries accumulating over spawn/revert cycles in long sessions.
- **Robotics reference patching (#238).** `PatchRoboticsReferences` remaps `ModuleRoboticController` (KAL-1000) part PID references in `CONTROLLEDAXES`/`CONTROLLEDACTIONS`/`SYMPARTS` after identity regeneration. Without this, Breaking Ground DLC robotics controllers lose their servo bindings on spawned copies.
- **Post-spawn velocity zeroing (#239).** `ApplyPostSpawnStabilization` zeroes linear and angular velocity on freshly spawned surface vessels (LANDED/SPLASHED/PRELAUNCH) to prevent physics jitter. Orbital spawns are excluded via `ShouldZeroVelocityAfterSpawn` guard.
- **isBackingUp investigation (#236).** Confirmed via decompilation that `Vessel.BackupVessel()` sets `isBackingUp = true` internally. No fix needed — added documentation comment.

### Recording Optimizer

- **All-boring leaf recordings now trimmed to minimal window.** After optimizer splits produce an all-SurfaceStationary or all-ExoBallistic leaf recording (e.g., vessel sitting on Mun after approach/surface split), `TrimBoringTail` now trims to `Points[1].ut + buffer` instead of skipping. Previously, these recordings survived indefinitely because `FindLastInterestingUT` returned NaN with no reference point. The ghost now finishes in seconds instead of replaying minutes of stationary vessel.
- **Documented ChildBranchPointId split invariant.** Added comments in `RecordingStore.RunOptimizationPass` explaining why unconditional move of `ChildBranchPointId` to the second half is safe (BP is always at recording's temporal end) and why `BranchPoint.ChildRecordingIds` doesn't need updating (chain linkage provides the connection).

### Code Quality

- **Remove dead forwarding properties (#133).** Removed unused `overlapGhosts` and `loopPhaseOffsets` private forwarding properties from ParsekFlight — zero internal callers, external code accesses `engine.*` directly.
- **Centralize time conversion system (#187).** Created `ParsekTimeFormat` static class as single source of truth for calendar-aware time formatting. `FormatDuration` (compact: "2d 3h"), `FormatDurationFull` (all units: "1y, 2d, 3h"), and `FormatCountdown` ("T-2d 3h 15m 5s") all respect `GameSettings.KERBIN_TIME`. Replaced 4 duplicate `FormatDuration` implementations (RecordingsTableUI, MergeDialog, TimelineEntryDisplay, ParsekUI) and moved calendar constants from SelectiveSpawnUI. MergeDialog now correctly shows days/years for long recordings.
- **Timeline per-frame allocation cleanup.** Deduplicated vesselNameById dictionary, replaced `OrderBy().ToList()` with in-place `Sort()`, cached retired kerbals and stats text (rebuilt only on filter change), added `Dictionary<string, Recording>` for O(1) `FindRecordingById` lookup (was O(N) per row).

### Tests

- 119 new test cases. 5064 total passing.

### Location Context (Phase 10)

- **Biome captured at recording start and end.** Timeline shows "Landed at Midlands on Mun" instead of "Landed on Mun". Uses `ScienceUtil.GetExperimentBiome` — works for all stock bodies and biomes.
- **Vessel situation at recording start.** Humanized display: "Flying", "Orbiting", "Prelaunch" (not raw KSP ALL_CAPS enum names).
- **Stock launch site name captured.** Launch Pad, Runway, and Making History DLC sites (Desert Airfield, Woomerang Launch Site, Island Airfield) detected via `FlightDriver.LaunchSiteName`. Timeline shows "Launch: Vessel from Launch Pad on Kerbin".
- **Biome injected into spawn entries.** Surface situations (Landed, Splashed) get "at [biome] on [body]". Orbital situations pass through unchanged.

### Timeline Improvements

- **Tree branch recordings filtered from timeline.** Staging debris, booster separations, and decouple children no longer show as spurious "Launch:" or "Spawn:" entries. Only true launches and EVAs appear.
- **Destroyed terminal spawn entries removed.** "Spawn: Bob (Destroyed)" no longer appears — you can't spawn a destroyed vessel.
- **Spawn text shows body name for all terminal states.** Previously only orbital terminals showed the body.
- **Diagnostic logging for timeline construction.** Each included/excluded entry logged with classification flags and skip reasons.
- **Crew death entries in timeline (#229).** New `CrewDeath` timeline entry type (T1 significance). When a recording has `CrewEndStates` with `Dead` kerbals, "Lost: Bob Kerman (Vessel Name)" entries appear at recording EndUT with red-tinted color. Makes crew deaths visible in the mission narrative.
- **EVA crew reassignment noise filtered (#228).** KSP auto-reshuffles remaining crew when someone EVAs, generating spurious KerbalAssignment actions. These are now filtered by matching `(RecordingId, UT)` against EVA branch keys built from EVA recordings' parent info.

### Crew Reservation

- **Fix EVA kerbals disappearing after spawn or player EVA (#46, #233).** `RemoveReservedEvaVessels` was deleting any EVA vessel whose crew name was in the `crewReplacements` dict, including Parsek-spawned vessels (#233) and player-created EVAs (#46). Two guards added: (1) loaded EVA vessels (in the physics bubble) are always kept. (2) Vessels whose `persistentId` matches a committed recording's `SpawnedVesselPersistentId` are kept.

### Tests

- 4 new test cases (all-boring leaf trim, tree-with-branch-point optimizer integration, all-boring-too-few-points guard, end-to-end approach/surface split+trim). 4977 total passing.
- 23 new test cases (crew death timeline entries, EVA reassignment filtering, spawned EVA PID guard, BuildEvaBranchKeys, BuildSpawnedVesselPidSet). 5007 total passing.

### Documentation

- **Gloops design document rewritten.** `docs/dev/gloops-recorder-design.md` now grounded in actual codebase — maps 1:1 to existing types (IPlaybackTrajectory, GhostPlaybackEngine). Extraction plan identifies 17 files to move, 2 needing pre-extraction splitting.
- **Roadmap updated.** Phase 10 renamed to Location Context. Phase 11.5 (Recording Optimization & Observability) added. Phase 15 (Mod Compatibility) added as final phase — stock-first approach. Gloops extraction placed as Phase 13 prerequisite.
- `docs/dev/recording-optimizer-review.md` — full investigation of the recording post-processing optimizer covering multi-environment split logic, branch point re-parenting safety, and two end-to-end mission scenarios (Mun landing, Duna rover to Kerbin sample return).

---

## 0.7.0

### Timeline (Phase 9)

- **Unified timeline view replaces Game Actions window.** Chronological, read-only view of all committed career events sorted by UT. Current-UT divider separates past (full color) from future (dimmed). See `docs/parsek-timeline-design.md`.
- **Two-tier filtering.** Overview shows mission structure (launches, milestones, contracts, facilities). Detail adds all resource transactions (science, funds, reputation).
- **Three source filters.** Recordings (launches/spawns), Actions (deliberate player choices: tech, build, hire), Events (gameplay consequences: milestones, science earned, contract completions).
- **Rewind / Fast-Forward from timeline.** R button on past recordings, FF button on future recordings — same behavior as Recordings Manager.
- **GoTo cross-link.** GoTo button on recording entries opens the Recordings Manager, unhides the recording if hidden, expands parent groups, and scrolls to it.
- **Humanized display text.** Science subjects split and spaced (`crewReport@KerbinSrfLaunchpad` → `Crew Report @ Kerbin Launchpad`). Tech nodes, milestones, and strategy names humanized. Milestone IDs with `/` shown as ` - ` separator.
- **EVA-aware entries.** EVA recordings show `EVA: Jeb from Mun Lander (MET 5s)` instead of `Launch:`. Boarded kerbals show `Board: Jeb (Mun Lander)`. EVA self-assignment entries filtered out.
- **Spawn entries at EndUT with situation.** `Spawn: Vessel (Landed on Mun)` using KSP's VesselSituation. Falls back to terminal state + orbit body.
- **Mission Elapsed Time on launch entries.** `Launch: Vessel (MET 1d, 2h, 30m)` showing only non-zero components (KSP calendar: 6h days, 426d years). Chain recordings show full chain duration.
- **Resource budget summary.** Funds/science/reputation reserved vs. available at top of window. Game-mode aware (hidden in sandbox, science-only in science mode).
- **Footer statistics.** Recording count, revert count, action count, event count.
- 53 timeline-specific tests, 4870 total.

---

## 0.6.2

### Bug Fixes

- **Fix vessel not spawned at end of playback when parts break off on impact (#224).** Breakup-continuous foreground recordings (where `ProcessBreakupEvent` sets `ChildBranchPointId` without creating a same-PID continuation) are now recognized as effective leaves via `IsEffectiveLeafForVessel`. Snapshot refreshed post-breakup to reflect surviving vessel. Added diagnostic spawn-suppression-reason logging.
- **Fix spawn-in-air: altitude clamping for surface terminal states.** Breakup-continuous recordings used the last trajectory point (still descending) as spawn position. Splashed vessels spawned above sea level; KSP reclassified them to FLYING. Altitude now clamped to 0 for Splashed, terrain height for Landed.
- **Fix boring tail trim skipped for breakup-continuous leaves (#185).** `IsLeafRecording` in `RecordingOptimizer` rejected recordings with `ChildBranchPointId` without checking effective-leaf status. Ghost sat motionless on the surface for the full recording duration instead of trimming the idle tail and spawning promptly.
- **Fix camera black screen on distant vessel spawn.** `DeferredActivateVessel` called `ForceSetActiveVessel` on unloaded vessels (beyond physics range), triggering a full FLIGHT→FLIGHT scene reload. Now skips activation for unloaded vessels — camera stays on the pad.
- **Fix GhostChain.GhostStartUT set to rewindUT instead of earliest claim time (#225, T51).** `GhostStartUT` was initialized to the `rewindUT` parameter, making the in-game test `ChainTimeRangesValid` fail for all 13 chains with valid SpawnUT. Now set to `links[0].ut` (earliest chain link time).
- **Stateless spawn dedup bypass replaces fragile ForceSpawnNewVessel flag (#226).** Per-recording transient flag (lost on Recording object recreation mid-scene) replaced with single static `RecordingStore.SceneEntryActiveVesselPid`. `SpawnVesselOrChainTip` derives bypass decision from current game state at spawn time. Removed `MarkForceSpawnOnActiveVesselRecordings`, `MergeDialog.MarkForceSpawnOnTreeRecordings`, and all per-recording flag-setting code.

### Crew Reservation

- **Refactor kerbal reservation to not use rosterStatus=Assigned (T44).** Reserved kerbals now stay at their natural rosterStatus (typically Available) instead of being set to Assigned. A new `CrewDialogFilterPatch` Harmony prefix on `BaseCrewAssignmentDialog.AddAvailItem` filters reserved and retired kerbals from the VAB/SPH crew selection dialog. Eliminates the `KerbalAssignmentValidationPatch` tug-of-war (~27 KSP warnings per session) and the `AssignedCrewCountPatch` Astronaut Complex count mismatch. Both workaround patches deleted. Dead `ReserveSnapshotCrew` method removed.

### Code Quality — Refactor-3

- **Pass 1: 48 sub-methods extracted across 9 files.** Method extraction within files for long methods in GhostPlaybackEngine, FlightRecorder, GhostVisualBuilder, GhostPlaybackLogic, ParsekUI, ParsekFlight, RecordingStore, VesselSpawner, ParsekScenario. No logic changes.
- **Pass 3A: SafeWriteConfigNode deduplicated.** Four independent safe-write implementations (Ledger, RecordingStore, GameStateStore, MilestoneStore) consolidated into shared `FileIOUtils.SafeWriteConfigNode`. MilestoneStore gains error handling it previously lacked.
- **Pass 3B: SuppressionGuard struct.** 10 manual try/finally suppression-flag blocks across 4 files replaced with `IDisposable` `SuppressionGuard` struct (Crew, Resources, ResourcesAndReplay factories).
- **Pass 3C: ParsekUI window extractions.** Three self-contained windows extracted from ParsekUI (4,773 → 3,698 lines): `UI/GroupPickerUI` (373 lines), `UI/SpawnControlUI` (321 lines), `UI/ActionsWindowUI` (500 lines).
- **T45: `HasOrbitSegments` added to `IPlaybackTrajectory` interface.** 13 inline `OrbitSegments != null && .Count > 0` checks replaced across 6 files. `MockTrajectory` updated.
- **Pass 4: Remaining UI & watch-mode extractions (T46-T50).** Five more extractions completing the refactor:
  - `UI/TestRunnerUI` (276 lines) — test runner window from ParsekUI.
  - `UI/SettingsWindowUI` (353 lines) — settings window from ParsekUI.
  - `UI/RecordingsTableUI` (2,251 lines) — recordings table from ParsekUI (largest extraction, 57 fields, 30+ methods). GroupPickerUI ownership moved here.
  - `WatchModeController` (963 lines) — camera-follow / watch-mode from ParsekFlight (15 fields, 18 methods). ParsekFlight keeps forwarding methods for external callers.
  - `MilestoneStore.SuppressLogging` dead code removed (field written in 80 tests, never read).
- 4,816 tests pass throughout. Zero logic changes.

### Tests

- **40 new in-game runtime tests across 9 categories (PR #130).** Nearly doubles the runtime test suite (50 → 90). New categories: GhostLifecycle (orphan ghosts, NaN positions, overlap cap, explosion leaks, soft-cap coherence), PartEventFX (engine/RCS particle systems, parachute canopy, light components, fairing meshes, deployable transforms), GameActionsHealth (stuck suppression flags, career resource singleton bounds), GhostChains (stale recording refs, double-ghosting, time range validity, missing tip snapshots), TreeIntegrity (broken parent/child links, PID collisions across trees, EndUT coverage), SceneAndPatch (KSC/TS controller presence, ghost vessel load patch, scenario+crew round-trips), KspApiSanity (body rotation stability, UT monotonicity, PartLoader cache, Krakensbane, floating origin NaN drift), GhostMapOrbits (degenerate orbital elements), SpawnCollision (vessel bounds, distant overlap).
- **`InGameAssert.Skip()` for honest test reporting.** Tests that cannot exercise their assertions (no active ghosts, no committed trees, wrong game mode) now report as SKIPPED instead of silently passing. The test runner catches `InGameTestSkippedException` in both sync and coroutine paths, so results clearly distinguish "tested and passed" from "could not test".

---

## 0.6.1

### Game Actions & Resources

- **Fix career funds/science/reputation zeroed on save load (#222).** Loading a career save (especially after a sandbox save in the same session) would set all resources to 0. The ledger's `seedChecked` flag was never reset between saves, so no `FundsInitial`/`ScienceInitial`/`ReputationInitial` actions were created for the career save — the recalculation engine computed target=0 and `KspStatePatcher` actively zeroed KSP's correct values. Fix: added `HasSeed` guard to each resource module so patching is skipped when no seed exists, reset `seedChecked` on each save load, and added a deferred seeding coroutine that captures correct values after KSP finishes loading.

### Ghost Visuals

- **Fix invisible shrouds on ghost engines with variants (PR #124).** Engine shrouds (e.g. Poodle skirt, EP37 engine plate covers) were permanently invisible on ghosts. Three fixes: (1) Variant name resolution now reads `moduleVariantName` from the PART level in snapshots, where KSP actually persists it, not just inside the MODULE node. (2) Multi-MODEL parts (engine plates) have transform names with full GameDatabase paths; variant GAMEOBJECTS rules now match after stripping the path prefix and `(Clone)` suffix. (3) The transform-visibility fallback in `CheckJettisonState` misinterpreted variant-hidden transforms as jettisoned shrouds, emitting false `ShroudJettisoned` events at recording start that permanently hid all jettison geometry on playback. Fixed by skipping the fallback for parts with `ModulePartVariants`.

### Watch Mode

- **Fix watch camera cutting off on orbital ghosts.** The 300km camera cutoff was unconditionally exiting watch mode when a ghost exceeded the distance — even for orbital recordings that naturally travel far during ascent/orbit. Orbital recordings (those with orbit segments) are now exempt from both the watch-exit cutoff and the Watch button distance gate. Also added missing log entry on individual recording Watch button clicks.

### Recording

- **Close commit-to-save crash window (T15).** Recording data no longer lives only in RAM between commit and the next `OnSave()` cycle. New `FlushDirtyFiles()` writes `.prec`, `_vessel.craft`, and `_ghost.craft` to disk immediately on commit and again after the recording optimization pass (merge/split/trim). If the immediate write fails, `FilesDirty` stays true and `OnSave` retries as before — no behavior change in the failure path.

### Code Quality

- **ChainSegmentManager field encapsulation (T35).** Added `ApplyChainMetadataTo(Recording)`, `IsTrackingContinuation`/`IsTrackingUndockContinuation` properties, and `TryGetContinuationRecording`/`TryGetUndockContinuationRecording` accessors. ParsekFlight chain metadata copy sites and continuation access patterns now use these instead of direct field access.
- **Continuation recording index validation (T36).** `ContinuationRecordingId` and `UndockContinuationRecId` stored alongside int indices. `TryGet` accessors validate the ID matches before returning, detecting stale indices if recordings are ever removed mid-flight.
- **Breakup child recording dedup (T31).** Extracted `CreateBreakupChildRecording` static helper. 4 child-recording creation loops across `ProcessBreakupEvent` and `PromoteToTreeForBreakup` replaced with one-liner calls.

### UI

- **Simplified merge dialog.** All merge/commit confirmation dialogs (standalone, chain, tree, multi-vessel tree) now use a unified simple format: vessel/tree name and duration, with consistent "Merge to Timeline" / "Discard" buttons. Multi-vessel trees auto-apply default persist/ghost-only decisions (surviving vessels persist, destroyed are ghost-only) without per-vessel UI. Removed verbose per-vessel summaries, point counts, distances, and situation text.
- **In-game test runner.** New runtime test framework accessible via **Ctrl+Shift+T** (any scene) or Settings > Diagnostics button. Discovers and runs tests inside KSP to verify systems that xUnit structurally cannot cover (real Unity GameObjects, live KSP APIs, ghost visual construction, part name resolution, crew roster state). 50 tests across 13 categories: ghost visual builds, recording data health, body name resolution, save/load round-trips, crew reservation integrity, ghost map presence, CommNet antenna power, and Flight-scene integration. Supports sync and multi-frame coroutine tests, per-category and individual run buttons, color-coded pass/fail results with inline error messages, and auto-exports `parsek-test-results.txt` to the KSP root folder after each run.

### Showcase Recordings

- **Fix kerbal-with-flag height mismatch (T37).** Flag plant showcase kerbalEVA now uses `ShowcaseAltitudeOffset` for top-aligned height, matching other showcase parts.

### Ghost Playback

- **Fix ghost icon through planet (#212b).** Ghost vessel icon no longer follows the full Keplerian ellipse through the planet in tracking station. New `GhostOrbitIconClampPatch` prefix on `OrbitDriver.updateFromParameters` clamps the propagation UT to the visible arc — the icon freezes at the arc endpoint instead of going underground. The previous approach (drawIcons postfix on LateUpdate) silently failed because `vessel.orbitDriver` was null for ghost ProtoVessels despite `OrbitRendererBase.driver` being valid.
- **Chain-aware tracking station ghosts (#215).** Tracking station ghost creation now respects recording chains. Intermediate recordings superseded by later recordings in the same chain no longer get stale ghost ProtoVessels. Only chain-tip recordings with orbital data create ghosts.
- **Tracking station ghost lifecycle (#215).** Ghost ProtoVessels are now removed from the tracking station vessel list when game time passes their orbit segment endUT. Previously, ghosts were created once at scene init and persisted until scene exit regardless of time progression. Ghosts are also created dynamically during time warp when UT enters an orbit segment range.
- **Atmospheric ghost icons in tracking station.** Ghost vessel icons are now visible during atmospheric flight phases (launch, reentry) in the tracking station. Uses direct OnGUI rendering from trajectory data — same projection pipeline as flight-scene map markers. No ProtoVessel (avoids the known OrbitDriver state-vector roundtrip position mismatch, #172). New `MapMarkerRenderer` static helper shares icon atlas, vessel type colors, and rendering logic between flight and tracking station scenes.
- **Watch mode for distant ghosts (T39).** Watch button is no longer disabled for ghosts beyond the 120km visual rendering zone. The zone boundary is about rendering from the active vessel's camera — irrelevant for watch mode which moves the camera to the ghost. The only limit is now the user-configurable `ghostCameraCutoffKm` setting (default 300km).

### Recording

- **Debris recording filtering (T38).** Trivial crash fragments (single struts, panels, shroud pieces) are no longer recorded during breakup events. `ShouldRecordDebris` filters debris before Recording creation — vessels with fewer than 3 parts AND less than 0.5 tons are skipped entirely (no BackgroundRecorder tracking, no per-frame sampling). Spent boosters and multi-part stages pass the filter.

### Tests

- **ChainSegmentManager unit tests (T34).** 16 new tests covering `SampleContinuationVessel` guard paths (pid=0 early return, stale/negative index → stop callback), `UpdateContinuationSampling`/`UpdateUndockContinuationSampling` wrappers (no-op and stale-index propagation), `StopAllContinuations` branching (neither/one/both active, chain identity preservation), and `RefreshContinuationSnapshotCore` guards (pid=0, negative recIdx, stale recIdx). Total: 46 tests for ChainSegmentManager (up from 30).

### Architecture

- **KerbalsModule converted to IResourceModule (T42).** KerbalsModule now participates in the RecalculationEngine walk lifecycle instead of operating as a separate bridge. Added `PostWalk()` phase to IResourceModule interface. Removed 19 redundant `RecalculateAndApply()` calls. Added old-save migration for KerbalAssignment actions. Fixed latent bug where dead crew (absent from VesselSnapshot) weren't reserved for MIA respawn override.
- **Fix Astronaut Complex "Assigned" tab mismatch (#216).** KSP's `ValidateAssignments` set Parsek-reserved kerbals to Missing (not on any vessel), and `GetAssignedCrewCount` counted them despite the list being empty. Two Harmony postfixes: `KerbalAssignmentValidationPatch` restores Assigned status after validation, `AssignedCrewCountPatch` subtracts managed kerbals from the tab count.

### Documentation

- **TODO cleanup.** Marked T17 (game actions redesign), T25/D20 (playback engine extraction), T28/D2 (commit-pattern dedup), T32 (test audit), T34 (ChainSegmentManager tests), T41 (suborbital orbit line), T41b (atmosphere on-rails skip), T18/T35/T36/T37, T14, T29/T40 (closed), T31, and T13 as done.

---

## 0.6.0

### Ghost Playback

- **Suborbital orbit line (T41).** Ghost orbit lines are now visible during suborbital coasting phases. Recordings with orbit segments show the ballistic arc when the ghost enters a coast phase; pure-physics recordings (no time warp) construct the orbit from interpolated state vectors once above the atmosphere. Orbit line is atmosphere-aware: hidden below `body.atmosphereDepth` (70km on Kerbin) to avoid wild/flickering lines from atmospheric drag. On airless bodies, orbit line appears above 1500m. Hysteresis thresholds prevent flicker. Tracking station orbit lines remain restricted to stable orbital recordings.
- **Ghost orbit line suppression (Harmony).** New `GhostOrbitLinePatch` postfix on `OrbitRendererBase.LateUpdate` hides the orbit line for ghost ProtoVessels below atmosphere while keeping the native KSP map icon visible. Also hides Ap/Pe/AN/DN markers when orbit line is hidden.
- **Debris map markers hidden.** Debris ghost recordings no longer show green dot markers in map view.
- **Stock vessel type icons for ghost markers.** Ghost map markers now use KSP's actual vessel type icons (Ship, Probe, Rover, Station, Plane, etc.) from the orbit icon atlas instead of a plain green dot. Icons are color-tinted per vessel type. Falls back to a diamond shape before MapView initialization.
- **Ghost orbit arc clipping.** Ghost orbit lines in the tracking station now render only the arc between the orbit segment's `startUT` and `endUT`, instead of the full Keplerian ellipse. Suborbital trajectories no longer show orbit lines passing through the planet surface. Uses a Harmony prefix on `OrbitRendererBase.UpdateSpline` that applies the same eccentric-anomaly arc-clipping logic as KSP's own `PatchRendering`. Terminal-orbit ghosts (stable orbits) continue to show the full ellipse. Ap/Pe/AN/DN nodes are hidden for partial-arc ghosts to prevent misleading markers at out-of-arc positions.
- **Fix ghost icon through planet (#212b).** Ghost vessel icon no longer circles through the planet on the underground portion of the orbit. Replaced broken eccentric/true anomaly arc check (sign mismatch bug) with orbital-time-based check using `orbit.getObtAtUT()`. Added rate-limited logging for icon visibility decisions.
- **Chain-aware tracking station ghosts (#215).** Tracking station ghost creation now respects recording chains. Intermediate recordings superseded by later recordings in the same chain no longer get stale ghost ProtoVessels. Only chain-tip recordings with orbital data create ghosts. Fixes stale orbit display for vessels that have deorbited or been destroyed.
- **Ghost ProtoVessel pressure protection.** New `GhostCheckKillPatch` prevents KSP from destroying ghost ProtoVessels due to on-rails atmospheric pressure. Deorbit orbits pass through the atmosphere, triggering KSP's stock vessel destruction — if the map camera was focused on the ghost, this caused a NullRef cascade that broke scaled space rendering (planet disappeared, stuck exit).
- **Ghost surface clamp.** Ghost mesh clamped to body surface when Keplerian orbit goes underground (deorbit orbits with sub-surface periapsis). Prevents ghost tunneling through the planet during orbit-only recording sections.

### Format Reset

- **Recording format reset to version 0 (PR #114).** Clean break: reset `CurrentRecordingFormatVersion` from 7 to 0. Removed all legacy format migration code (v4→v5 rotation conversion, `SyncVersionFromPrecFile`, `CorrectForBodyRotation`), the `surfaceRelativeRotation` version-branching (all rotation is now unconditionally surface-relative), ghost geometry legacy fields (`GhostGeometryVersion`, `GhostGeometryCaptureStrategy`, `GhostGeometryProbeStatus`), and the `loopPauseSeconds` field-rename fallback. -500 lines. No behavioral change — all removed code paths were already dead (no users with old-format recordings exist).

### Release & Distribution

- **Parsek.version file (T1).** Added `GameData/Parsek/Parsek.version` for AVC and CKAN version detection. Auto-copied to KSP GameData on build.
- **UI version display (T2).** Version label ("v0.6.0") shown at the bottom of the main Parsek window, read from AssemblyVersion at runtime.

### Recording Optimizer

- **Fix optimizer over-splitting (PR #111).** The optimizer was splitting recordings at every ExoPropulsive↔ExoBallistic boundary (engine on/off), creating 10+ chain segments for multi-burn missions. Introduced `SplitEnvironmentClass` to coarsen split decisions: ExoPropulsive/ExoBallistic are the same class ("exo"), SurfaceMobile/SurfaceStationary are the same class ("surface"). Splits now only happen at meaningful boundaries: atmo↔exo, exo↔approach, approach↔surface.
- **Approach environment for airless bodies.** New `SegmentEnvironment.Approach` (=5) classifies vessels below approach altitude on airless bodies (Mun, Minmus). Enables the optimizer to split landing/takeoff recordings so they can be looped independently. A typical Kerbin→Mun landing now produces ~4 segments (atmo, exo, approach, surface) instead of 10+.
- **Unified tree root recording.** `PromoteToTreeForBreakup` no longer creates separate root and continuation recordings. The main vessel gets one continuous recording through all breakups. Decoupled part events handle ghost visual updates (booster detach) during playback. Eliminates the 14s root fragment that couldn't show later staging events.
- **Debris loop sync.** Debris ghosts (separated boosters, fairings) now replay in sync with the parent recording's loop cycle. New `LoopSyncParentIdx` field links debris to the parent recording whose loop clock drives their playback. Boosters visibly separate and fly away on each loop iteration.
- **Boring tail trimming.** Leaf recordings that end with a long idle period (surface stationary or orbital coasting) are automatically trimmed to ~10 seconds past the last meaningful activity. Prevents ghosts from sitting motionless for extended periods before the real vessel spawns. Only applies to leaf recordings (no child branches or chain continuations).

### Game Actions & Resources System

Full career-mode resource tracking across the rewind timeline. Science, funds, reputation, milestones, contracts, kerbals, facilities, and strategies are now recorded, reconciled on rewind, and patched back into KSP's singletons.

- **Ledger-based recalculation engine.** All game state changes are stored as `GameAction` entries in a chronological ledger. On every commit, rewind, warp exit, or load, the engine walks the full action list from UT=0 forward, recomputing derived state from scratch. Deterministic — same actions always produce the same result.
- **7 resource modules** participate in the recalculation walk:
  - **Science** — per-subject credited totals with diminishing returns, once-ever semantics, tech tree cost tracking
  - **Funds** — earnings, spendings, advance payments, affordability checks, hire costs, facility costs
  - **Reputation** — raw accumulation with KSP's diminishing-returns curve applied during the walk
  - **Milestones** — once-ever achievement tracking with path-qualified IDs for body-specific milestones
  - **Contracts** — full lifecycle (accept/complete/fail/cancel), slot management, deadline expiration with synthetic failure generation, once-ever completion semantics
  - **Facilities** — upgrade level and destruction/repair state tracking per KSC building
  - **Strategies** — activation/deactivation tracking with commitment-based contract reward diversion
- **KSP state patching.** After recalculation, `KspStatePatcher` syncs KSP singletons to match the ledger's computed state:
  - Science pool balance and per-subject credited totals (Science Archive)
  - Funds balance
  - Reputation (direct set, no double curve)
  - Facility levels via `UpgradeableFacility.SetLevel` and destruction state via `DestructibleBuilding.Demolish/Repair`
  - Milestone achievement flags via reflection on private `reached`/`complete` fields (no public reversal API)
  - Active contracts restored from ConfigNode snapshots via `Activator.CreateInstance` + `Contract.Load`
- **Contract deadline failures.** Accepted contracts with deadlines are tracked. If a deadline expires before the contract is resolved, the engine injects a synthetic `ContractFail` action at the deadline UT with the contract's failure penalties. Deadline and penalty data captured at accept time with backward-compatible structured detail format.
- **Kerbal rescue detection.** Subscribes to `GameEvents.onKerbalTypeChange` to detect Unowned-to-Crew transitions (rescue pickup). Covers all rescue scenarios: EVA boarding, docking, claw grab, crew transfer.
- **Game state event recording.** `GameStateRecorder` subscribes to 15+ KSP GameEvents (contract lifecycle, crew changes, facility upgrades, tech research, milestones, science experiments, kerbal type changes). Events stored in `GameStateStore` per recording, converted to ledger actions at commit time.
- **Milestone ID path qualification.** Body-specific milestones now captured as `"Mun/Landing"` instead of ambiguous bare `"Landing"`. Uses reflection to read the private `CelestialBody body` field on KSPAchievements subclasses.
- **Strategy commitment rates.** Strategy reward diversion uses the player's chosen commitment level (0.01-0.25) instead of a hardcoded 1.0.
- **Warp facility patching.** Facility visuals patched at warp start and full recalculation on warp exit.
- **Retired Stand-ins UI.** Game Actions window shows retired kerbals (stand-ins displaced by returning originals) with count header. Section hidden when empty.

### Kerbal Lifecycle Management

- **Crew end-state inference.** Each crew member's fate is inferred from the recording's terminal state and end-of-recording vessel snapshot (Aboard, Dead, Recovered, Unknown).
- **Reservation system.** Reserved kerbals (appearing in committed recordings) are set to Assigned status, preventing KSP from assigning them to new missions. Permanent reservations for dead crew, temporary for recovered.
- **Stand-in chain system.** When a reserved kerbal needs to fly again, a stand-in is hired with the same trait. Chains can go multiple levels deep. Stand-in names persisted in KERBAL_SLOTS ConfigNode.
- **Retirement tracking.** When a stand-in is displaced by the original returning, the stand-in becomes "retired" — kept in roster (may appear in recordings) but blocked from dismissal.
- **MIA respawn override.** KSP's respawn mechanic (Dead-to-Available after delay) is overridden on every recalculation — reserved kerbals stay Assigned regardless.

### Bug Fixes

- **Chain segment gap event loss (#204).** When the optimizer splits a recording into chain segments at TrackSection boundaries, events falling in the UT gap between consecutive segments (e.g., `RecordsAltitude` at the atmosphere/space transition) were silently lost. `NotifyLedgerTreeCommitted` now extends each chain continuation's event window backward to the predecessor's EndUT. In career mode, this prevented milestone rewards from being credited.
- **Ledger reconcile pruning KSC milestones (#205).** `Ledger.Reconcile` pruned earning-type actions with null `recordingId`, incorrectly removing legitimate KSC spending milestones like `FirstCrewToSurvive` that are not associated with any recording. Fixed condition to allow null-recordingId earnings through.
- **KerbalDismissalPatch Harmony failure (#206).** The patch targeting `KerbalRoster.Remove` failed with "Ambiguous match" because the method has multiple overloads. Switched to `TargetMethod()` with explicit parameter types `new[] { typeof(ProtoCrewMember) }`.
- **Duplicate CrewStatusChanged events (#207).** KSP's delayed `onKerbalStatusChange` callbacks fired after the crew mutation suppression window closed, producing redundant `Assigned->Missing` events on every save cycle. Added `KerbalsModule.IsManaged` check to suppress status change noise for reserved/stand-in kerbals.
- **Chain tip missing terminalState (#208).** In tree mode, the active recording is non-leaf (has debris branches) so `FinalizeIndividualRecording` skipped its terminalState. Added explicit terminalState determination for the active recording in `FinalizeTreeRecordings`, which the optimizer propagates to the chain tip via `SplitAtSection`.
- **Looping chain crew release (#209).** Fixing the terminalState alone would free crew at the tip's EndUT while the ghost still loops. Added a `loopingChains` pre-scan in `KerbalsModule.Recalculate`: Recovered crews on chains with any looping segment keep `endUT=Infinity`. Disabling the loop correctly releases them.
- **Ghost tunnels underground during atmospheric on-rails (#210, PR #116).** When a vessel went on-rails during atmospheric flight (e.g. reentry time warp), Keplerian orbit segments were recorded that ignore drag — the ghost dove through the planet. Added `ShouldSkipOrbitSegmentForAtmosphere` check: orbit segment creation is skipped when `altitude < atmosphereDepth`. Point interpolation lerps across the gap instead. Applies to FlightRecorder and BackgroundRecorder.

### Code Quality & Refactoring

- **RewindContext encapsulation (R3-3).** Extracted 8 scattered static rewind fields from `RecordingStore` into `RewindContext` static class with controlled `BeginRewind()`/`EndRewind()` mutation API.
- **Dock/undock state dedup (R3-5).** Extracted `ClearDockUndockState()` and `RestartRecordingAfterDockUndock()` from ParsekFlight, eliminating 4x duplicated cleanup and restart blocks.
- **Removed ActionReplay.** Legacy action replay system (499 lines) replaced by ledger-based recalculation.
- **Removed ResourceApplicator.** Legacy resource delta application (318 lines) replaced by KspStatePatcher.

### Bug Fixes

- **Fix #195: Ghost orbit lines not visible in tracking station.** Ghost ProtoVessels created in `SpaceTracking.Awake` prefix had null `orbitRenderer` because `MapView.fetch` wasn't set yet (Unity Awake ordering is undefined). `buildVesselsList` line 751 unconditionally accesses `vessel.orbitRenderer.onVesselIconClicked` with no try/catch — single NRE aborted the entire method including `ConstructUIList()`. Fix: added Prefix on `buildVesselsList` calling `EnsureGhostOrbitRenderers()` which uses Traverse to invoke private `AddOrbitRenderer()` on ghosts with null renderer. Also added defensive FLIGHTPLAN/CTRLSTATE/VESSELMODULES ConfigNode children to ghost ProtoVessel.
- **Fix: Ghost map missing in tracking station when TerminalOrbit fields empty.** `CreateGhostVesselsFromCommittedRecordings` only checked `HasOrbitData()` (terminal orbit fields) which returned false for all recordings. Now falls back to the last `OrbitSegment` — same pattern used by the flight scene's deferred creation path.
- **Fix: Duplicate ghost orbit lines during time warp across chain segments.** Multiple chain segments each created their own ghost map ProtoVessel during fast time warp. Added per-chain dedup via `chainMapOwner` dict — when a new chain segment creates a ghost map vessel, the previous segment's is removed.
- **Fix T41: Ghost orbit line persists after landing.** `CheckPendingMapVessels` skipped with `continue` when `FindOrbitSegment` returned null (ghost past all orbit segments). The stale orbit line remained indefinitely. Now removes the ghost map ProtoVessel when UT exits all orbit segments.

### Tests

- **4621 tests** (up from 2419 on main). 20 new test classes covering all game action modules, serialization round-trips, recalculation engine, kerbal reservation lifecycle, milestone patching, contract deadline injection, and ChainSegmentManager state management.

### Research & Documentation

- **3 API investigation reports** in `docs/dev/research/`: milestone ProgressNode tree, contract type registry, rescue docking detection.
- **Design document** updated to v0.5 (Phases 1-8 complete).
- **Recording format version 6** (v5-to-v6: SegmentEvents, TrackSections, ControllerInfo, extended BranchPoint types).

---

## 0.5.3

### Features

- **Departure-aware Real Spawn Warp.** When a nearby ghost will leave its current orbit before spawn time (e.g., parking orbit → Mun transfer), the RSW window now shows a "Departs T-Xm Xs" state column and replaces the "FF-Spawn" button with "FF-Depart" — an epoch-shifted warp to the departure moment that preserves rendezvous geometry. Orbit comparison uses SMA, eccentricity, inclination, and argument of periapsis (eccentric orbits only) with tight tolerances to detect any intentional maneuver. Handles SOI changes, surface terminal states, off-rails gaps, and return-trip scenarios.
- **T97: Altitude-based chain splits for airless bodies.** Recordings auto-split when crossing the approach altitude threshold on bodies without atmosphere (Mun, Minmus, Tylo, etc.). Uses KSP's native `timeWarpAltitudeLimits[4]` (100x warp limit) as the threshold, with `body.Radius * 0.15` as fallback. Enables selective looping of landing approaches without looping orbital coasts.
- **T97: "approach" phase tagging.** Airless body segments below the threshold are tagged `"approach"` (sky blue in UI) instead of `"space"`. All phase tagging sites updated.
- **T97: TrackSection altitude metadata.** Min/max altitude tracked per TrackSection during recording. Serialized as sparse keys, backward compatible with existing saves.
- **T97: Recording optimization pass.** Automatic housekeeping merges redundant consecutive chain segments on save load (same phase, same body, no branch points, no ghosting triggers, no user-modified settings).
- **T98: Per-phase looping for all recording modes.** Tree recordings are now split into per-phase segments after commit, matching chain mode's per-phase loop toggles. The optimizer's split pass (`FindSplitCandidatesForOptimizer`) breaks multi-environment recordings at environment boundaries without the conservative ghosting-trigger check. Each phase gets its own loop toggle in the UI. Auto loop range trims boring bookends (orbital coasts, surface idle) when loop is toggled on.
- **T98: Loop range fields.** New `LoopStartUT`/`LoopEndUT` fields on Recording narrow the loop range. Engine (`TryComputeLoopPlaybackUT`, `ShouldLoopPlayback`), save/load (both paths), and `CanAutoMerge` updated. Backward compatible (NaN defaults = existing behavior).
- **T98: Policy modularity refactor.** Migrated scattered `TreeId != null` / `ChainId != null` policy checks to `IsTreeRecording` / `IsChainRecording` / `ManagesOwnResources` query properties. Extracted `ClassifyVesselDestruction` and `ShouldSuppressBoundarySplit` as testable static methods.

### Ghost Map Presence (bug #60)

Ghost vessels now appear in KSP's tracking station, show orbit lines in map view, and can be targeted for rendezvous planning. Works for both ghost chain vessels and timeline playback ghosts.

- **ProtoVessel-based map integration** — lightweight ProtoVessel (single `sensorBarometer` part) per ghost provides automatic tracking station entry, orbit line (OrbitRenderer), clickable map icon (MapObject), and navigation targeting (ITargetable). Created on chain init or engine ghost spawn, removed on resolve/destroy/rewind/scene cleanup, stripped from saves.
- **Timeline playback + chain ghosts** — both recording-index ghosts (from the playback engine) and chain ghosts (from `VesselGhoster`) get ProtoVessels. Parallel tracking dicts with unified cleanup via `RemoveAllGhostPresenceForIndex`.
- **Deferred orbit line creation** — recordings that start pre-orbital (launch-to-orbit) don't show orbit lines during atmospheric ascent. ProtoVessel created when ghost enters first orbital segment, with the current segment's orbit (not terminal orbit).
- **Per-frame orbit segment tracking** — ghost ProtoVessel orbit updates as the ghost traverses segments (Hohmann transfers, SOI transitions). Both chain and recording-index ghosts use `ApplyOrbitToVessel` with direct element assignment via `Orbit.SetOrbit()`.
- **Terminal state filtering** — only Orbiting/Docked recordings get orbit lines. Destroyed, SubOrbital, Landed, Splashed skip (misleading orbit). Debris always skipped.
- **30 guard rails** across 10 source files — `IsGhostMapVessel(pid)` checks on all `FlightGlobals.Vessels` iteration sites and vessel GameEvent handlers.
- **6 Harmony patches** — `Vessel.GoOffRails` (prevent physics loading), `CommNetVessel.OnStart` (prevent duplicate CommNet nodes), `FlightGlobals.SetActiveVessel` (redirect to watch mode), `SpaceTracking.FlyVessel`/`OnVesselDeleteConfirm`/`OnRecoverConfirm` (block tracking station actions with screen message, release input lock via `OnDialogDismiss`).
- **Tracking station scene support** — `ParsekTrackingStation` addon creates ghost ProtoVessels from committed recordings when visiting tracking station directly.
- **Soft cap integration** — `Despawn` removes ProtoVessel; `ReduceFidelity` and `SimplifyToOrbitLine` keep it (orbit line stays visible when mesh is hidden).
- **Target transfer** — if ghost was the navigation target when chain resolves, the spawned vessel becomes the new target.
- **VesselType mirroring** — ghost uses the original vessel's type from snapshot for correct filter placement.
- **Green dot suppression** — `DrawMapMarkers` skips the old GUI overlay dot when a native KSP map icon exists for that ghost.
- **Merge dialog re-evaluation** — `MergeDialog.OnTreeCommitted` callback triggers chain re-evaluation so ghost ProtoVessels are created immediately after commit+revert.
- **46 tests** — PID tracking, HasOrbitData, ComputeGhostDisplayInfo, ResolveVesselType, terminal state filtering, debris filtering, StartsInOrbit, orbit segment tracking, log assertions.

### Group UI Enhancements

- **Group header columns in recordings window.** Groups now display Launch (earliest member StartUT), Duration (sum of member durations), and Status (closest active T- countdown) columns. Groups participate in column-based sorting alongside chains and standalone recordings instead of always rendering first. Six `internal static` helpers extracted for testability with 27 unit tests.
- **Fix #176: Group hide checkbox misaligned when expanded stats visible.** Group rows were missing spacers for the MaxAlt/MaxSpd/Dist/Pts columns, causing the trailing Hide checkbox to shift out of alignment when the Stats panel was open.

### Bug Fixes

- **Fix #175: EVA kerbal spawns at recording start position instead of endpoint.** EVA vessel snapshots are captured at EVA start (kerbal on the pod's ladder), but the kerbal walks elsewhere during the recording. On spawn, the snapshot's baked-in lat/lon/alt placed the kerbal on top of the parent vessel, grabbing its ladder and triggering KSP's "Kerbals on a ladder — cannot save" error. `ResolveSpawnPosition` now routes EVA recordings to the trajectory endpoint; `OverrideSnapshotPosition` patches the snapshot before `RespawnVessel`.
- **Fix #179: Orbital vessel destroyed by pressure on spawn.** Three-part fix: (1) `terminalOverridesUnsafe` includes `TerminalState.Orbiting`, allowing spawn eligibility. (2) KSC spawn defers orbital vessels to flight scene (Space Center `pv.Load()` crashes them through terrain). (3) Flight-scene spawn uses `SpawnAtPosition` for orbital vessels to construct correct Keplerian orbit from last trajectory point position+velocity — `RespawnVessel` used the raw ascent snapshot orbit whose periapsis was in atmosphere. Additionally, `SpawnAtPosition` now accepts an optional `terminalState` parameter: when the terminal state is Orbiting/Docked but `DetermineSituation` returns FLYING (last trajectory point captured during ascent at suborbital speed), the situation is overridden to ORBITING to prevent KSP's on-rails 101.3 kPa pressure check from destroying the vessel.
- **Fix #172: Ghost map icon position + orbit lines not rendering + icon click menu.** Three-part fix: (1) Replaced `Orbit.UpdateFromOrbitAtUT()` with `Orbit.SetOrbit()` in `ApplyOrbitToVessel` — the old path roundtripped through state vectors, introducing floating-point drift in `argumentOfPeriapsis` for near-circular orbits (confirmed 0.0m offset after fix). (2) Added `deferredCreatedEvents.Add()` to `UpdateLoopingPlayback` and `UpdateOverlapPlayback` in `GhostPlaybackEngine` — only `RenderInRangeGhost` was firing `OnGhostCreated`, so looping ghosts never got ProtoVessels and orbit lines never rendered. (3) Added `GhostIconClickPatch` (postfix on `objectNode_OnClick`) showing a popup near cursor with "Set As Target" / "Watch" options. Ghost orbit lines are visual-only (not clickable via `GhostOrbitCastPatch`) to avoid ambiguity with real vessels sharing the same orbit. Watch mode entry distance now reads user's `ghostCameraCutoffKm` setting instead of hardcoded 100km.
- **Fix #180: Clicking ghost vessel in tracking station traps user with input lock.** `GhostTrackingFlyPatch` blocked `FlyVessel` for ghost map vessels but didn't dismiss the dialog, leaving a stale input lock. Now calls `OnDialogDismiss` after blocking. Also fixed `GhostVesselSwitchPatch` overload targeting — replaced unreliable attribute-based `Type[]` with explicit `TargetMethod()` for `FlightGlobals.SetActiveVessel`.
- **Fix #171: Orbital ghost disappears during 50x time warp.** During warp >4x, ghosts with orbital segments are now exempt from zone-based mesh hiding (`ShouldExemptFromZoneHide` in `GhostPlaybackLogic`). Prevents orbital ghosts from completing playback while invisible in the Beyond zone.
- **Fix #172: Ghost destruction reason logged as "unknown".** `RetryHeldGhostSpawns` now passes per-action reason strings to `DestroyGhost`: `"held-spawn-succeeded"`, `"held-already-spawned"`, `"held-spawn-timeout"`, `"held-invalid-index"`.
- **Fix #173: Zero-point debris leaf recordings saved from same-frame destruction.** Added `PruneZeroPointLeaves` step in `FinalizeTreeRecordings` — removes leaf recordings with zero trajectory points, no orbit segments, and no surface position. Prevents `.prec` sidecar files and tree nodes for instantly-destroyed debris.
- **Fix #174: ChainWalker evaluates terminated chains every frame.** Two-level filtering: `IsTreeFullyTerminated` skips scanning trees where all leaves are Destroyed/Recovered; `EvaluateAndApplyGhostChains` excludes terminated chains from `activeGhostChains`.
- **Fix #158: Watch mode auto-follow picks debris instead of core vessel after separation.** `FindNextWatchTarget` now recursively descends through PID-matched continuations that have no active ghost (boundary seed recordings). If the PID-matched continuation exists but has no ghost anywhere in its subtree, debris fallback is suppressed — watch hold expires naturally instead of following a booster.
- **Fix #168: Spawned vessels not re-spawned after rewind/revert.** After vessel stripping on revert, `SpawnedVesselPersistentId` was restored from the save but pointed to a stripped vessel — blocking re-spawn permanently. Added `ReconcileSpawnStateAfterStrip` that checks surviving PIDs in flightState after all strip operations and resets spawn tracking for recordings whose vessel no longer exists.
- **Fix #170: Vessel spawned near launch pad collides with infrastructure, chain-explodes player's rocket.** Added 50m KSC exclusion zones around launch pad and runway start point (`IsWithinKscExclusionZone` in `SpawnCollisionDetector`) to block spawns near infrastructure. Fixed `RemoveDeadCrewFromSnapshot` to remove reserved crew who are Dead (reservation no longer overrides death). Added `ShouldBlockSpawnForDeadCrew` guard to abandon spawn when all crew are dead.
- **Fix #72: GhostCommNetRelay antenna combination formula wrong for non-combinable strongest.** Extracted `ResolveCombinationExponent` pure method. When the overall strongest antenna is non-combinable, the combination exponent now comes from the strongest *combinable* antenna, matching KSP's actual formula.
- **Fix #81: TrackSection struct shallow copy shares mutable list references.** Extracted `Recording.DeepCopyTrackSections` that creates independent `frames` and `checkpoints` lists for each copied TrackSection. Used in `ApplyPersistenceArtifactsFrom`.
- **Fix #122: Dead->Dead crew status identity transitions logged as events.** Added `IsRealStatusChange` guard in `GameStateRecorder.OnKerbalStatusChange` to filter identity transitions before recording.
- **Fix #123: #autoLOC localization keys in internal log messages.** Wrapped `v.vesselName` in `TimeJumpManager` and `other.vesselName` in `SpawnCollisionDetector` with `Recording.ResolveLocalizedName()`.
- **Fix #131: Explosion GO count can reach ~90 for overlapping reentry loops.** Added `MaxActiveExplosions = 30` cap in `TriggerExplosionIfDestroyed`. New explosions are skipped (with logging) when at cap; ghost parts are still hidden.

- **Fix #78: DetermineTerminalState maps DOCKED to Orbiting.** Changed `case 128` (DOCKED) to return `TerminalState.Docked` instead of `TerminalState.Orbiting`. Edge case for debris that docks.
- **Fix #80: TimeJumpManager.ExecuteJump no warp guard.** Added warp stop at the start of `ExecuteJump` — calls `TimeWarp.SetRate(0, true)` when `CurrentRateIndex > 0` to prevent desync from `SetUniversalTime` during warp.
- **Fix #75: GhostPlaybackLogic inconsistent negative interval handling.** Added early guard in `ComputeLoopPhaseFromUT` for `currentUT < recordingStartUT`, consistent with `TryComputeLoopPlaybackUT`. Removed redundant duplicate guard.
- **Fix #82: IsDebris, Controllers, SurfacePos not serialized for standalone recordings.** Added save/load for all three fields in `ParsekScenario.SaveStandaloneRecordings` / `LoadStandaloneRecordingsFromNodes`, matching the tree recording pattern.

- **Fix #134: CleanupOrphanedSpawnedVessels destroys freshly-spawned past vessels after rewind.** The rewind path populated `PendingCleanupNames` with all recording vessel names for `StripOrphanedSpawnedVessels`, but left them set for `CleanupOrphanedSpawnedVessels` in `OnFlightReady`, which then destroyed correctly-spawned past vessels. Fix: clear `PendingCleanupPids`/`PendingCleanupNames` immediately after the strip completes.
- **Fix #43: Update known-bugs status.** Shader fallback lookup (`FindShaderOnRenderers`) was already implemented in commit 25ccfa9 but doc status was stale.
- **Fix #95: Preserve VesselSnapshot on committed recordings.** Removed snapshot nulling from continuation vessel destroyed and EVA boarding handlers. `VesselDestroyed` flag gates spawn and is now reset by `ResetRecordingPlaybackFields` on revert/rewind. `UpdateRecordingsForTerminalEvent` skips all committed recordings. Items 3-5 (continuation sampling/refresh) deferred as tech debt.
- **Fix #96: Hold ghost until spawn succeeds.** Ghost no longer disappears when spawn is blocked or warp-deferred. `HandlePlaybackCompleted` holds the ghost at its final position via `heldGhosts` dict. `RetryHeldGhostSpawns` retries each frame, releasing on success or 5s timeout.
- **Fix #99: Spawn real vessels at KSC when ghost timelines complete.** `ParsekKSC.TrySpawnAtRecordingEnd` calls `VesselSpawner.RespawnVessel` when ghosts exit range. Chain mid-segment suppression via `IsChainMidSegment`. `OnSave` auto-unreserve guarded at SpaceCenter to prevent snapshot pre-emption.

- **Fix #48: Use actual body radius in ComputeBoundaryDiscontinuity.** Replaced hardcoded Kerbin radius (600,000m) with lookup from static dictionary of 17 stock KSP body radii. Diagnostic-only fix — logged discontinuity magnitude is now accurate on all bodies.
- **Fix #77: Use InvariantCulture for TerrainCorrector log formatting.** Replaced 8 `{val:F1}` interpolation sites with `.ToString("F1", IC)` to prevent comma-decimal output on non-English locales.
- **Fix #73: Filter vessel types in CheckWarningProximity.** Extracted `ShouldSkipVesselType` helper (Debris/EVA/Flag/SpaceObject) shared between `CheckOverlapAgainstLoadedVessels` and `CheckWarningProximity`.
- **Fix #129: Strip future PRELAUNCH vessels on rewind.** Unrecorded pad vessels from the future persisted after rewind because `StripOrphanedSpawnedVessels` only matched recorded names. Added PID-based quicksave whitelist: `PreProcessRewindSave` captures surviving vessel PIDs, `HandleRewindOnLoad` strips any PRELAUNCH vessel not in the whitelist.
- **Fix #137: Rescue reserved crew from Missing after EVA vessel removal.** `vessel.Unload()` in `RemoveReservedEvaVessels` orphaned crew → KSP set them Missing. Added `RescueReservedCrewAfterEvaRemoval` to restore Missing→Assigned for crew in `crewReplacements` dict.
- **Fix #64: Clear pending tree/recording on revert.** Merge dialog shown twice when reverting during tree destruction. `pendingTree` (static) persisted across scene transitions without cleanup. Now discarded in the OnLoad revert path.
- **Fix #71: Remove old CommNode before re-registration.** `RegisterNode` now removes existing node from CommNet before adding new one, preventing orphaned nodes.
- **Fix #79: SpawnCrossedChainTips no longer mutates caller's dict.** Returns spawned PIDs list; caller removes after call.
- **Fix #84: int→long for cycleIndex.** Prevents integer overflow in loop phase calculations for very long sessions. Updated across 10 files (state, events, logic, engine, KSC, flight).
- **Fix #101: BackgroundRecorder.SubscribePartEvents now called.** Part events (onPartDie, onPartJointBreak) are now subscribed for background vessels at both tree creation sites.
- **Fix #102: CreateSplitBranch copies FlagEvents and SegmentEvents.** Previously omitted, causing flag/segment data loss on tree split.
- **Fix #130: Cache vesselName on GhostPlaybackState.** Destroy events now show vessel name even when trajectory reference is null (loop restart).

- **Fix #139: Merge dialog not shown on revert to launch.** Bug #64 fix unconditionally discarded freshly-stashed pendings. Added `PendingStashedThisTransition` flag to distinguish fresh (keep) vs stale (discard) pendings across scene transitions.
- **Fix #140: Camera resets to active vessel on loop ghost cycle boundary.** Non-destroyed looped ghosts left FlightCamera with null target between destroy/respawn. `ExplosionHoldEnd` now creates a temporary camera bridge anchor; `RetargetToNewGhost` cleans it up.
- **Fix #141: Budget deduction drives science/funds/reputation negative.** Extracted `ClampDeduction(reserved, available)` pure method. All three resource types clamped to available balance.
- **Fix #142: Ghosts spawning into dying scene after DestroyAllGhosts.** Added `sceneChangeInProgress` flag to suppress `Update()`/`LateUpdate()` after `OnSceneChangeRequested`.
- **Fix #143: ApplyTreeResourceDeltas per-frame no-op overhead.** Added fast-path early-out when all trees already have `ResourcesApplied=true`.
- **Fix #144: Degraded trees (0 points) deduct budget.** Extracted `RecordingTree.IsDegraded`/`ComputeEndUT()`. Trees with no trajectory data skip budget application.
- **Fix #145: Ghoster WARN spam for non-existent synthetic vessels.** Pre-check vessel existence before ghosting; downgraded to VERBOSE.
- **Fix #22 (revised): Facility upgrade replay deferred instead of dropped.** Facility upgrades in Flight scene now set `deferred=true`, stopping the watermark so they are retried on next scene load (previously marked as replayed and permanently skipped).
- **Fix #146: Ghost frozen at final position after watch hold.** `watchEndHoldUntilUT` was set but never consumed — ghost held indefinitely. Added expiry check in `UpdateWatchCamera` that retries auto-follow during hold, then destroys ghost on timeout.
- **Fix #147: Watch mode auto-follow race condition.** Continuation ghost not yet spawned when `FindNextWatchTarget` runs at completion. Hold timer now retries every frame; auto-follows as soon as continuation appears.
- **Fix #148: Fast-forward doesn't transfer watch to target.** `FastForwardToRecording` now exits watch and defers entering on the FF target after engine positions ghosts.
- **Fix #149: RCS throttle event spam.** Deadband increased from 1% to 5% — reduces RCS part events by ~90% for SAS-active flights.

- **Fix #135: ParsePartPositions wrong key.** `SpawnCollisionDetector.ParsePartPositions` only checked `"pos"` but KSP vessel snapshots use `"position"`. Parsed 0/40 parts on real vessels, falling back to inaccurate 2m bounds. Now checks both keys.
- **Fix #150: Engine/RCS FX not stopped at on-rails.** `FlightRecorder.OnVesselGoOnRails` now calls `EmitTerminalEngineAndRcsEvents()` before going on-rails. Ghost engine plumes no longer persist during orbit segments.
- **Fix #151: FF watch renders broken scene.** Added 100km distance guard in `EnterWatchMode` — refuses watch when ghost is beyond rendering-safe distance from active vessel. Rate-limited `FindNextWatchTarget` logging during watch hold.
- **Fix: Enter key on camera cutoff input.** Enter key now commits the value (KeyDown was consumed by TextField before the check ran).
- **Fix #74: RELATIVE mode boundary point at on-rails.** `SamplePosition` recorded absolute lat/lon/alt into a RELATIVE TrackSection at on-rails boundaries. Moved RELATIVE clearing before boundary sampling.
- **Fix #107: Engine/SRB smoke trails vanish on ghost despawn.** Particle systems are now detached from the ghost hierarchy before destruction, allowing trails to fade naturally (8s linger).
- **Fix #125: Engine plate shrouds not visible on ghost.** Inactive-variant renderer filter preempted GAMEOBJECT rules. When explicit variant rules exist, they are now the sole authority on object inclusion.
- **Engine throttle deadband increased to 5%.** SRBs with smooth thrust curves generated excessive EngineThrottle events at 1% deadband. Matches RCS deadband (#149).
- **Fix #56: Auto-record EVA from any vessel situation.** Removed PRELAUNCH restriction — kerbals EVA'ing from landed bases, orbiting stations, etc. now auto-record.
- **Fix #57: Boarding confirmation timeout too short.** Increased from 3 frames (~60ms) to 10 frames (~200ms).
- **Fix #115/#116: Crew lost to Missing after rewind vessel strip.** New `RescueOrphanedCrew` sets orphaned Assigned crew to Available after vessel stripping, before KSP's validation marks them Missing.
- **Fix #155: Orphaned recording lost on auto-record vessel switch.** `StartRecording` now commits the orphaned recorder's data before creating a new one.
- **Fix #76: GhostExtender hyperbolic fallback negative altitude.** Added `Math.Max(0, ...)` to prevent ghost underground placement.
- **Fix #157: Green sphere ghost for ghost-only debris.** `ApplyVesselDecisions` now preserves `GhostVisualSnapshot` before nulling spawn snapshot.
- **Fix #161: EVA snapshot situation stale.** `ShouldSpawnAtRecordingEnd` overrides unsafe-situation check when terminal state is Landed/Splashed.
- **Fix #162: AutoCommitGhostOnly strips snapshot from landed EVAs.** Preserves `VesselSnapshot` for Landed/Splashed terminals.
- **Fix #163: KSC spawns vessels from the future after rewind.** `ShouldSpawnAtKscEnd` now checks `currentUT >= EndUT`.
- **Fix #165: Engine flame flash on ignition.** Seed events for engines at zero throttle (staged but idle on the pad) are now skipped entirely — no plume at playback start. The first real throttle-up event starts the plume at the correct time. Playback retains `Math.Max(0.01f)` floor for backward compatibility with older recordings.
- **Fix #169: EVA vessel spawned FLYING destroyed by on-rails pressure.** EVA snapshot captured `sit=FLYING` but terminal state was Landed. KSP's on-rails pressure check killed the vessel instantly, crew set to Dead. `CorrectUnsafeSnapshotSituation` now corrects FLYING/SUB_ORBITAL to LANDED/SPLASHED before spawning when terminal state indicates safe surface arrival.
- **Fix #164: Strip all future vessels on rewind, not just PRELAUNCH.** Flags, landed capsules, and other player-created vessels from the future now removed after rewind.
- **Fix #167: Crew swap not executed for KSC-spawned vessels.** `SwapReservedCrewInFlight` only runs in flight scene — KSC spawns via `TrySpawnAtRecordingEnd` never swapped reserved crew. Added `SwapReservedCrewInSnapshot` to replace reserved crew names directly in the snapshot ConfigNode before spawning.
- **DeferredActivateVessel timeout increased** from 10 frames to 5 seconds. Distant spawned vessels (37km+) couldn't load in 10 frames.
- **ComputeTotal logging removed.** Eliminated 52% of all Parsek log output (pure computation was logging every UI frame).
- **Status column widened** (95→120px) for longer T+ timestamps.
- **R/FF button state transition logging** for debugging enable/disable issues.
- **Fix #197: Ghost ProtoVessel missing for stable orbits reported as SUB_ORBITAL.** KSP reports `SUB_ORBITAL` for off-rails vessels in stable orbits (e.g., Mun orbit). Added `DetermineTerminalState(int, Vessel)` overload that checks `orbit.eccentricity < 1.0 && orbit.PeR > body.Radius` to override to Orbiting, enabling ghost ProtoVessel creation and orbit line rendering.
- **Fix #198: Duplicate green dot map markers during time warp.** During warp, multiple chain segments can be active simultaneously (short segments from optimizer splits). Added per-chain dedup in `DrawMapMarkers` — only the highest-index (latest) ghost per chain draws a marker.
- **Fix #199: Checkpoint log spam.** Downgraded `CheckpointAllVessels` and warp-rate-changed messages from Info to Verbose (~8,800 lines per Mun mission).
- **Fix: Recording optimizer hang on save load.** `RunOptimizationPass` called `SaveRecordingFiles` synchronously during OnLoad for every merge/split — with 400k+ trajectory points this blocked the main thread long enough for Windows to kill KSP as not responding. File saves now deferred to OnSave.
- **Fix: OnSave re-serializing unchanged recordings.** Added `FilesDirty` flag on Recording — OnSave skips `SaveRecordingFiles` for recordings whose sidecar data hasn't changed. Eliminates ~80 seconds of redundant I/O per session for large saves.
- **Fix #200: 128km trajectory gap at environment transitions.** Environment hysteresis transitions and anchor detection transitions called `CloseCurrentTrackSection` without sampling a boundary point. Added `SamplePosition(v)` before all 4 affected `CloseCurrentTrackSection` calls — matches the on-rails transition pattern.
- **Fix #202: Spawned vessel deleted when switching to it.** Vessel switching triggers FLIGHT→FLIGHT scene reload, which was misidentified as a revert. The spawned vessel was stripped, re-spawned, then immediately deleted by the orphan cleanup. Added `vesselSwitchPending` flag via `onVesselSwitching` to distinguish vessel switches from reverts.
- **Fix #201: Optimizer split creates temporal gap at section boundaries.** `SplitAtSection` now interpolates a synthetic boundary point at exactly `splitUT` when no trajectory point falls at the split time. Both halves share the boundary point, eliminating visible jumps during chain playback.
- **Fix map marker camera in flight view.** `DrawMapMarkerAt` used `PlanetariumCamera` unconditionally — correct for map view but wrong for flight view. Now uses `FlightCamera` in flight view, `PlanetariumCamera` + ScaledSpace in map view.
- **Default recordings sort: Launch time ascending.** Recordings window now defaults to chronological order (earliest launch at top) instead of index order.
- **Ghost orbit lines for intermediate chain segments.** ProtoVessels and orbit lines now appear during every coast phase (transfer orbits, parking orbits), not just the terminal orbit. `CreateGhostVesselFromSegment` builds from OrbitSegment data when terminal orbit is unavailable.
- **Fix: 10 tests failing due to FindObjectsOfType in KspStatePatcher.** `PatchDestructionState` called `UnityEngine.Object.FindObjectsOfType<DestructibleBuilding>()` which throws `SecurityException` outside Unity. `protoUpgradeables` is an empty dict (not null) in the test env, so `PatchFacilities` fell through to the Unity call. Added `SuppressUnityCallsForTesting` guard.

### Features

- **Settings window: "Ghosts" group.** Merged "Ghost Camera" and "Ghost Soft Caps" sections into a single "Ghosts" group. Added checkbox-to-label spacing for all settings toggles.
- **Fix #50: Chain block enable/loop checkboxes.** Chain headers now have aggregate enable and loop checkboxes (were empty spacers).
- **Fix #98: Merge Countdown into Status column.** Status now shows `T-Xm Xs` for future, `Active` for playing, terminal state name for past.
- **Fix #88: Commit approval dialog for landed/splashed vessels.** When leaving Flight to KSC or Tracking Station with a landed/splashed vessel, shows Keep/Discard dialog instead of auto-committing. Game exit still auto-commits.

- **Ghost camera cutoff setting.** Settings > Ghost Camera > Cutoff [300] km. Watch mode auto-exits when ghost exceeds this distance. Watch button disabled for ghosts beyond cutoff. Default 300km, configurable 10-10000km.
- **Watch mode distance overlay.** "Watching: Vessel (45.2 km)" in the notification bar shows distance from ghost to active vessel.
- **Watch mode auto-follow on stage separation.** Camera automatically follows the controller vessel through tree branch points and chain continuations.
- **T+ mission time in Countdown column.** Past/live recordings show elapsed time since launch (T+Xh Ym Zs) instead of "LIVE" or "-".
- **Debris subgroups in recordings window.** Debris recordings from stage separations are auto-grouped under a "Vessel / Debris" subgroup. Orphaned split segments adopted into the tree group on commit.

### Previously Fixed (Confirmed)

- **#43** (shader fallback), **#49** (RealVesselExists O(n)) — already fixed in prior releases.
- **#121** (Ghost SKIPPED log spam) — resolved by T25 Phase 9 engine extraction.
- **#133** — removed 6 dead forwarding methods + 2 unused properties from ParsekFlight, inlined call sites.
- **#63** — added `errorWhitelist` parameter to `ParsekLogContractChecker.ValidateLatestSession`.
- **#83** — CommNet stale nodes concern is not a bug; follows stock KSP re-registration pattern.
- **#108** — engine cutoff polling logic (`EngineIgnited && isOperational`) correctly catches flameout; remaining inconsistency needs in-game repro.
- **#113** — stock FX modules infeasible on ghost architecture; current reimplementation is correct.
- **#153** — AnimateHeat nose cone classification requires reflection hack for negligible visual effect; won't fix.
- **#156** — extracted `IsPadFailure` static method + 7 tests; remaining items need Unity runtime.

Log spam audit and cleanup. Analyzed a 28,923-line KSP.log from a 70-second KSC session with 273 recordings — Parsek was 68.4% of all output (19,771 lines). Identified and fixed the top spam sources.

### Log Cleanup

- **Removed `ParsekLog.Log()` method** — all 26 call sites (16 in EngineFxBuilder, 10 in GhostVisualBuilder) were using the subsystem-less `Log()` wrapper, producing 2,651 lines tagged as `[General]` (55% of all INFO output). Migrated to proper `Verbose("EngineFx")` / `Verbose("GhostVisual")` / `Info("GhostVisual")`. Deleted the method to prevent future untagged usage.
- **ReentryFx INFO→VERBOSE** — mesh combination and fire shell overlay messages fired per ghost build at INFO level (2,148 lines in 70s). Downgraded to Verbose.
- **KSC per-ghost spawn/destroy INFO→VERBOSE** — per-ghost spawn, enter-range, re-show, warp-hide, and no-longer-eligible messages at INFO level (1,347 lines). Downgraded to Verbose. Added batch summary in OnDestroy (`Destroyed N primary + N overlap KSC ghosts`).
- **FlightRecorder point logging rate-limited** — `Recorded point #N` logged every 10th physics frame at Verbose without rate limiting (~50 lines/sec during recording). Changed to `VerboseRateLimited` with 5s interval.
- **Mass ghost teardown batched** — KSC `DestroyKscGhost` per-ghost log (277 consecutive in one burst) changed to `VerboseRateLimited`. Overlap ghost destroy in `GhostPlaybackEngine` similarly rate-limited.
- **Per-renderer VERBOSE diagnostics removed** — individual MR[N]/SMR[N] per-renderer logs (1,041+ lines), per-renderer damaged-wheel skip logs, and per-SMR bone fallback logs removed. Per-part summary already captures the same counts.
- **Subsystem tag consolidation** — `Store`→`RecordingStore` (1 occurrence), `GhostBuild`→`GhostVisual` (5 occurrences). Reduces tag count from 63 to 61.

### Round 2 — Ghost Lifecycle Batch Logging

- **Frame batch summary** — replaced per-ghost spawn/destroy/build Verbose logs (15,489 Engine lines) with per-frame counters and one `VerboseRateLimited` summary: `Frame: spawned=N destroyed=N active=N`.
- **DestroyGhost reason parameter** — all 7+ call sites now pass a reason string (`"cycle transition"`, `"soft cap despawn"`, `"anchor unloaded"`, etc.). Per-ghost destroy log restored at 1s rate limit with full context.
- **SpawnGhost per-ghost log restored** — 1s rate-limited per-index key with build type (snapshot/sphere), part/engine/rcs counts.
- **ShouldTriggerExplosion skip logs removed** — 1,959 lines/session of pure predicate noise (caller already knows the result).
- **CrewReservation null snapshot log removed** — 515 lines of expected-path noise.
- **ReentryFx → shared rate-limit keys** — mesh combination messages now dedup across all ghosts (was per-ghost-index).
- **Overlap/explosion lifecycle → shared VRL keys** — overlap move, overlap expired, explosion created, parts hidden, loop restarted, overlap expired all changed from per-index to shared keys.
- **Zone rendering Info→VRL** — per-ghost zone transition messages downgraded from Info to VerboseRateLimited (1,008 lines).
- **Bug #135 cleanup** — fixed 12 garbled comments in ShouldSpawnAtRecordingEnd left from prior partial edit.

### Round 3 — Serialization Batch Summaries

- **Per-recording serialization logs removed** — 12 Verbose logs in RecordingStore (orbit segments, track sections, segment events, file summaries) and 2 per-recording metadata logs in ParsekScenario removed. These produced ~2,900 lines per save/load cycle.
- **4 batch summaries added** — standalone save/load and tree save/load now log one summary each with aggregate counters (points, orbit segments, part events, track sections, snapshots).
- **DeserializeSegmentEvents** — changed from always-log to Warn-only when events are skipped.

### Round 4 — Remaining Spam Sources

- **SpawnWarning FormatChainStatus** — Verbose → VerboseRateLimited shared key. Per-frame poll logging identical status (1,165 lines, 802-line burst).
- **Zone transition per-ghost** — Info → VerboseRateLimited shared key. 248-ghost bursts at scene switch collapsed to 1 line.
- **Scenario per-recording index dump** — Info → Verbose. Summary header stays at Info; per-recording detail demoted.
- **Per-recording "Loaded recording:"** — ScenarioLog (Info) → Verbose. Batch summary covers aggregates.
- **"Triggering explosion"** — Info → VerboseRateLimited per-index 10s. Looping overlap re-explosions deduplicated.

### Documentation

- Log audit report: `docs/dev/log-audit-2026-03-25.md`
- CLAUDE.md: added batch counting convention to Logging Requirements, removed obsolete `ParsekLog.Log` reference

### Design & Research

- Ghost orbits & trajectories investigation document (12 scenarios, 37 edge cases)
- KSP API decompilation reference (17 classes: ProtoVessel, OrbitRenderer, MapObject, SpaceTracking, etc.)
- KSPTrajectories mod architecture analysis (rendering, coordinate transforms, NavBall integration)

---

## 0.5.2

Second-pass structural refactoring + game action system modularization + continued decomposition. ~80 method extractions, ~105 logging additions, 103 new tests. 1 latent bug fixed, 1 latent IMGUI bugfix. Zero logic changes (except bug fixes).

### Code Refactor

- **Pass 1 — Method extraction + logging + tests** across 18 source files
  - `AddPartVisuals` reduced from 802 → 454 lines (parachute, deployable, heat phases extracted)
  - `RecordingStore` POINT/ORBIT serialization dedup (-140 lines, 4 shared helpers)
  - `ParsekScenario.OnLoad` split from 587 → ~450 lines (HandleRewindOnLoad, DiscardStalePendingState, LoadRecordingTrees)
  - `ParsekFlight.OnSceneChangeRequested` split from 205 → ~50 lines
  - `FlightRecorder` triple-dedup: FinalizeRecordingState shared across StopRecording/StopRecordingForChainBoundary/ForceStop
  - `FlightRecorder.CreateOrbitSegmentFromVessel` dedup (was duplicated in 4 sites)
  - `GhostPlaybackLogic.BuildDictByPid<T>` replaces 6 identical dict-construction blocks
  - `PartStateSeeder.EmitSeedEvents` -60 lines via local emit helper
  - `GhostChainWalker` zero-logging gaps fixed (4 methods now have full diagnostics)
  - `GhostExtender.PropagateOrbital` split from 83 → 15 lines (ComputeOrbitalPosition + CartesianToGeodetic)
- **Pass 2 — Architecture analysis** (dependency graph, static state inventory, cross-file duplication analysis)
- **Pass 3 — SOLID restructuring**
  - `EngineFxBuilder` extracted from GhostVisualBuilder (-975 lines)
  - `MaterialCleanup` MonoBehaviour extracted to own file
  - Loop constants consolidated into GhostPlaybackLogic
  - Shared ghost interpolation extracted to TrajectoryMath
  - `BudgetSummary` and `UIMode` nested types extracted to top-level
  - Dead code removed: `GetFairingShowMesh`, `GenerateFairingTrussMesh` (zero call sites)
  - `SanitizeQuaternion` unnecessary instance wrapper removed
- **T25 — Ghost Playback Engine extraction** (ParsekFlight 9900 → 8657 lines)
  - `GhostPlaybackEngine` (1553 lines) — extracted ghost lifecycle, per-frame rendering, loop/overlap playback, zone transitions, soft caps, reentry FX from ParsekFlight. Zero Recording references; accesses trajectories via `IPlaybackTrajectory` interface only. Fires lifecycle events (OnGhostCreated, OnPlaybackCompleted, OnLoopRestarted, etc.) for policy layer.
  - `ParsekPlaybackPolicy` (192 lines) — event subscriber handling spawn decisions, resource deltas, camera management, deferred spawn queue.
  - `IPlaybackTrajectory` interface — 19-property boundary exposing only trajectory/visual data from Recording. Enables future standalone ghost playback mod.
  - `IGhostPositioner` interface — 8 positioning methods implemented by ParsekFlight, delegates world-space placement to the host scene.
  - `GhostPlaybackEvents` — TrajectoryPlaybackFlags, FrameContext, lifecycle event types, CameraActionEvent for watch-mode decomposition.
  - 109 new tests (MockTrajectory, engine lifecycle, query API, interface isolation, log assertions)
- **Pass 4 — Continued dedup**
  - `SampleAnimationStates` unified core extracted from 4 near-identical methods (D15/T27, -139 lines)
  - `AnimLookup` enum + `FindAnimation` resolver parameterize 3 animation lookup strategies
  - 4 animation sample caches consolidated into 1 `animationSampleCache`
  - `CommitBoundaryAndRestart` shared tail extracted from atmosphere/SOI split handlers (D7)
- **Pass 5 — Game action system modularization** (ParsekScenario reduced by ~1020 lines)
  - `GroupHierarchyStore` extracted — UI group hierarchy + visibility (~200 lines, zero coupling to crew/resources)
  - `ResourceApplicator` extracted — resource ticking (TickStandalone, TickTrees), budget deduction, rewind baseline correction. Coroutine shells stay on ParsekScenario.
  - `CrewReservationManager` extracted — crew reservation lifecycle (Reserve/Unreserve/Swap/Clear), replacement hiring, EVA vessel cleanup. ~40 call sites updated across 7 source files.
  - `ResourceDelta` struct + `ComputeStandaloneDelta` added to ResourceBudget — pure testable delta computation
  - `SuppressActionReplay` + `SuppressBlockingPatches` merged into single `IsReplayingActions` flag
  - `ActionReplay.ParseDetailField` removed, callers use `GameStateEventDisplay.ExtractDetailField`
  - Guard logs added to all silent early-return paths in ResourceApplicator and CrewReservationManager
- **Pass 6 — GhostPlaybackEngine decomposition** (D5, D8)
  - `ApplyFrameVisuals` extracted — deduplicates part events + flag events + reentry FX + RCS toggle from 4 call sites. `skipPartEvents` parameter preserves Site 1 semantics.
  - `RenderInRangeGhost` (~84 lines) + `HandlePastEndGhost` (~47 lines) extracted from `UpdatePlayback` loop body. Loop body reduced from ~207 to ~70 lines.
- **Pass 7 — ChainSegmentManager extraction** (T26, ParsekFlight 8657 → 8098 lines)
  - `ChainSegmentManager` (686 lines) — owns 16 chain state fields + 16 methods. ~150 field accesses migrated from ParsekFlight. `ClearAll()` replaces 13-line scattered reset.
  - Phase 1: State isolation (16 fields moved, `StopContinuation`/`StopUndockContinuation` moved)
  - Phase 2: 12 methods moved (Group A: 8 continuation methods. Group B: 4 commit methods refactored with recorder-as-parameter + bool return for abort handling)
  - `CommitSegmentCore` shared pattern (T28/D2) — stash/tag/commit/advance extracted with `Action<Recording>` callback for per-method customization. All 4 commit methods delegate to core (nullable CaptureAtStop handled for boundary splits).
  - `ClearChainIdentity()` — replaces inline 4-field reset patterns in 3 locations
  - 3 orchestration methods stay on ParsekFlight (HandleDockUndockCommitRestart, HandleChainBoardingTransition, CommitBoundaryAndRestart — own StartRecording lifecycle)
- **Pass 8 — UI dedup** (T30/D18, D19)
  - `HandleResizeDrag` + `DrawResizeHandle` static helpers — 4 drag blocks + 4 handle blocks replaced with 8 one-liner calls
  - `DrawSortableHeaderCore<TCol>` generic method — unifies `DrawSortableHeader` and `DrawSpawnSortableHeader` via `ref` sort state + `Action onChanged`. `ToggleSpawnSort` removed.
- **Pass 9 — Encapsulation** (T33)
  - `GroupHierarchyStore` accessor migration — 5 new accessor methods (`AddHiddenGroup`, `RemoveHiddenGroup`, `IsGroupHidden`, `TryGetGroupParent`, `HasGroupParent`). All ~20 ParsekUI.cs direct field accesses migrated to accessors/read-only properties.
- **Performance**
  - Per-frame `List<PartEvent>` allocations eliminated — 4 transition-check methods now append to reusable buffer (T19)
  - `TimelineGhosts` dictionary cached per-frame instead of allocating on every property access (T20)
  - `ResourceBudget.ComputeTotal` cached per-frame, shared across `DrawResourceBudget` and `DrawCompactBudgetLine` (T21)
  - Chain ghost `cachedIdx` persisted on `GhostChain` — O(n) → O(1) amortized trajectory lookup (T9)
  - `RealVesselExists` HashSet cache — O(n) linear scan → O(1) per frame with manual invalidation (T10)
- **Ghost Soft Caps** (T5)
  - `ReduceFidelity` implemented — disables 75% of renderers by index for coarse LOD silhouette
  - `SimplifyToOrbitLine` improved — hides ghost mesh with `simplified` flag, frame-skip to avoid re-processing
  - Caps-resolved branch restores fidelity and re-shows simplified ghosts
- **Audits**
  - C2: namespace consistency verified — all 73 files correct (`Parsek` or `Parsek.Patches`)
  - C3: one-class-per-file verified — 5 files have multiple types but all are acceptable data-type bundles or tightly coupled enum+class pairs
  - C4: inventory doc line counts updated to final values

### Bug Fixes

- **KSC ghost heat initialization** — KSC scene ghosts now properly start heat-animated parts in cold state. Previously, the KSC private copy of `PopulateGhostInfoDictionaries` missed the cold-state initialization that the flight scene had. Fixed by deleting the private copy and calling the shared `GhostPlaybackLogic` version.
- **Group Popup drag event leak** — Group popup window resize drag was missing `Event.current.Use()` on MouseDrag, allowing drag events to fall through to underlying windows. Fixed by extracting shared `HandleResizeDrag` helper that applies `Use()` uniformly across all 4 windows (T30/D18).
- **RestoreGhostFidelity renderer over-enable** — `RestoreGhostFidelity` previously re-enabled all renderers unconditionally, overriding part-event visibility state (decoupled/destroyed parts could reappear for one frame after soft cap resolution). Now tracks which renderers were disabled by `ReduceGhostFidelity` and only re-enables those.
- **CommitSegmentCore log index off-by-one** — Post-commit log message showed the *next* segment's index instead of the committed segment's index. Now captures index before increment.
- **ParsekUI build error** — Missing `using System` for `Action` type in `DrawSortableHeaderCore<TCol>` generic method.
- **Simplified ghost re-shown by warp-down logic** — `SimplifyToOrbitLine` soft cap hid a ghost (`activeSelf=false`, `simplified=true`), but the warp-down re-show logic saw an inactive ghost in a non-Beyond zone and re-activated it, defeating the soft cap. Fixed by adding `!state.simplified` to both re-show conditions.
- **CommitVesselSwitchTermination orphaned undock continuation** — Only cleaned up vessel continuation (`ContinuationVesselPid`) but not undock continuation. Could leave an active undock continuation until next `ClearAll()`.
- **StopContinuation incomplete reset** — Did not reset `ContinuationLastVelocity`/`ContinuationLastUT`, asymmetric with `ClearAll()` and `StopUndockContinuation`.
- **Log spam cleanup** — "Terminated chain spawn suppressed" (26k lines/session) rate-limited; "GetCachedBudget" (6.8k) rate-limited; per-save serialization logs (4k) downgraded to Verbose; explosion FX spawn log (237) downgraded to Verbose; redundant "0 segment events" log (1.8k) removed. Total ~53% reduction in Parsek log output.

### Test Suite Audit (T32)

Deep audit of all 110 test files (~55k lines). 43 files changed, +170/-1182 lines:
- 8 exact duplicate test pairs deleted
- 28 always-passing/tautological tests removed (zero-assertion, property setter, inline-math-only)
- 12 tests not exercising production code deleted (hand-written logic, ConfigNode API tests, ParsekLog direct calls)
- 17 test classes given `IDisposable` for proper shared state cleanup
- 4 misleading test names fixed
- 3 unused `logLines` captures removed, dead code cleaned up, `[Collection("Sequential")]` added to WaypointSearchTests
- 3 `.NET framework behavior` tests deleted (HashSet operations in AnchorLifecycleTests)

### Test Coverage

3227 → 3374 tests (net +147: +212 new, -65 from T32 audit cleanup). New test areas:
- GroupTreeDataTests (14): recordings tree data-computation
- PostSpawnTerminalStateTests (12): spawn terminal state clearing
- InterpolatePointsTests (11): trajectory interpolation edge cases
- SerializationEdgeCaseTests (16): POINT/OrbitSegment round-trip, NaN, InvariantCulture
- ParsePartPositions + WalkbackAlongTrajectory (14): spawn collision parsing and walkback
- ReindexTests (7): ghost dict reindexing after deletion
- AppendCapturedDataTests (7): recording data append + sort
- GhostSoftCapManager Enabled=false guard (T22)
- SessionMerger frame trimming verification (T23)
- EnvironmentDetector ORBITING/ESCAPING situations (T24, 5 tests)
- ComputeStandaloneDelta (3): no-advance, multi-point, negative-index edge cases
- ResourceApplicator.TickStandalone (6): skip tree/loop/short, advance index, no-advance
- ResourceApplicator.DeductBudget (2): marking recordings and trees as applied
- CrewReservationManager serialization (4): LoadCrewReplacements log assertions, SaveCrewReplacements round-trip

### Documentation

- Refactor plan, inventory, review checklist, architecture analysis
- 21 deferred items tracked in `refactor-2-deferred.md` with Open/Done/Closed status
- Deferred items completed: D2, D5, D7, D8, D15, D18, D19, D20, D21
- TODO items completed: T5, T9, T10, T19-T27, T28, T30, T33, C1-C4
- `CLAUDE.md` updated with `ChainSegmentManager.cs` description
- Inventory doc (C4) updated with final line counts for all modified files

---

## 0.5.1

Spawn safety hardening, ghost visual improvements, booster/debris tree recording, flag planting, Fast Forward redesign, Real Spawn Control window. 20 PRs merged, 26 bugs fixed.

### Bug Fixes (Late)

- **Localization key mismatch on rewind** — stock vessels using `#autoLOC` keys (e.g., Aeris 4A stored as `#autoLOC_501176`) survived rewind vessel strip because name comparisons failed. Now resolves localization keys via `ResolveLocalizedName()` at all 4 strip/cleanup sites and all recording-creation sites (#126)
- **Collision check at wrong position** — `SpawnOrRecoverIfTooClose` checked the trajectory endpoint for collisions but `RespawnVessel` spawned at the snapshot position, allowing vessels to materialize on top of existing vessels. Now reads lat/lon/alt from the vessel snapshot for the collision check, with trajectory fallback. Also fixed in chain-tip spawn path (#127)

### Spawn Safety & Reliability

- **Bounding box collision detection** — replaced proximity-offset heuristic with oriented bounding box overlap checks against all loaded vessels (active vessel, debris, EVA, flags excluded)
- **Spawn collision retry limit** — 150-frame (~2.5s) collision block limit for non-chain spawns; walkback exhaustion flag for chain-tip spawns; spawn abandoned with WARN after limit hit (#110)
- **Spawn-die-respawn prevention** — 3-cycle death counter with permanent abandon for vessels destroyed immediately after spawn (e.g., FLYING at sea level killed by on-rails aero) (#110b)
- **Spawn abandon flag** — `SpawnAbandoned` prevents vessel-gone reset cycle from re-triggering spawn indefinitely
- **Non-leaf spawn suppression** — non-leaf tree recordings and FLYING/SUB_ORBITAL snapshot situations blocked from spawning; crew stripped from Destroyed-terminal-state spawn snapshots (#114)
- **SubOrbital terminal spawn suppression** — recordings with SubOrbital terminal state no longer attempt vessel spawn (#45)
- **Debris spawn suppression** — debris recordings (`IsDebris=true`) blocked from spawning real vessels
- **Orphaned vessel cleanup** — spawned vessels stripped from FLIGHTSTATE on revert and rewind; guards preserve already-set cleanup data on second rewind (#109)
- **ForceSpawnNewVessel on tree merge** — tree recordings correctly set ForceSpawnNewVessel during merge dialog callback, preventing PID dedup from skipping spawn after revert (#120)
- **ForceSpawnNewVessel on flight entry** — all same-PID committed recordings marked at flight entry for standalone recordings
- **Terminal state protection** — recovered/destroyed terminal state no longer corrupts committed recordings (#94)
- **Save stale data leak** — `initialLoadDone` reset on main menu transition prevents old recordings leaking into new saves with the same name (#98)

### Recording Improvements

- **Booster/debris tree recording** — `PromoteToTreeForBreakup` auto-promotes standalone recordings to trees on staging; creates root, continuation, and debris child recordings with 60s debris TTL. Continuation seeded with post-breakup points from root recording (#106 watch camera fix)
- **Controlled child recording** — `ProcessBreakupEvent` now creates child recordings for controlled children (vessels with probe cores surviving breakup), not just debris. Added to BackgroundRecorder with no TTL. Fixes RELATIVE anchor availability during playback (#61)
- **Flag planting recording/playback** — flag planting captured via `afterFlagPlanted`, stored as `FlagEvent` with position/rotation/flagUrl. Ghost flags built from stock flagPole prefab. Flags spawn as real vessels at playback end with world-space distance dedup
- **Auto-record from LANDED** — recording now triggers from LANDED state (not just PRELAUNCH) with 5-second settle timer to filter physics bounces, enabling save-loaded pad vessels and Mun takeoffs
- **Settle timer seed on vessel switch** — `lastLandedUT` seeded in `OnVesselSwitchComplete` for already-landed vessels, fixing auto-record for spawned vessels (#111)
- **Terminal engine/RCS events** — synthetic EngineShutdown, RCSStopped, and RoboticMotionStopped events emitted at recording stop for all active entries, preventing ghost plumes from persisting past recording end (#108)
- **Localization resolution** — `#autoLOC` keys resolved to human-readable names in vessel names and group headers via `Localizer.Format()` (#103)
- **Group name dedup** — multiple launches of same craft get unique group names: "Flea (2)", "Flea (3)" etc. (#104)
- **Chain boundary fix** — boundary splits skip standalone chain commits during tree mode, preventing nested groups in UI (#87)

### Ghost Visual Improvements

- **Compound part visuals** — fuel lines and struts render correctly on ghosts via PARTDATA/CModuleLinkedMesh fixup
- **Plume bubble fix** — ghost plume bubble artifacts eliminated by using KSP-native `KSPParticleEmitter.emit` via reflection instead of Unity emission module (#105)
- **Smoke trail fix** — Unity emission only disabled on FX objects that have KSPParticleEmitter; objects without it (smoke trails) keep their emission intact
- **Engine plume persistence** — `ModelMultiParticlePersistFX`/`ModelParticleFX` kept alive on ghosts for native KSP plume visuals (stripping them killed smoke trails)
- **Fairing cap** — `GenerateFairingConeMesh` generates flat disc cap when top XSECTION has non-zero radius (#85)
- **Fairing internal structure** — prefab Cap/Truss meshes permanently hidden; internal structure revealed only on `FairingJettisoned` event (#91)
- **Heat material fallback** — fallback path only clones materials that are tracked in `materialStates`, preventing red tint on non-heat parts (#86)
- **Surface ghost slide fix** — orbit segments skipped for LANDED/SPLASHED/PRELAUNCH vessels; `IsSurfaceAtUT` suppresses orbit interpolation for surface TrackSections; SMA < 90% body radius rejected (#93)
- **Terrain clamp** — ghost positions clamped above terrain in LateUpdate, preventing underground ghosts regardless of interpolation source
- **RELATIVE anchor fallback** — ghosts freeze at last known position instead of hiding when RELATIVE section anchor vessel is missing
- **Part events in Visual zone** — structural part events (fairing jettison, staging, destruction) now applied in the Visual zone (2.3-120km), not just Physics zone

### UI Improvements

- **Real Spawn Control window** — proximity-based UI showing ghosts within 500m whose recording ends in the future. Per-craft Warp button, sortable columns (Craft, Dist, Spawns at, In T-), and "Warp to Next Spawn" quick-jump button
- **Countdown column** — `T-Xd Xh Xm Xs` countdown in Recordings Manager, updates live during playback
- **Screen notification** when ghost craft enters spawn proximity range (10-second duration)
- **Toggle button** — "Real Spawn Control (N)" in main window, grayed out when no candidates nearby
- **Fast Forward redesign** — FF button performs instant UT jump forward (like time warp) instead of loading a quicksave; uses reflection for `BaseConverter.lastUpdateTime` to prevent burst resource production
- **Pinned bottom buttons** — Warp, Close, and action buttons pinned to window bottom in Actions, Recordings, and Spawn Control windows
- **Recordings window widened** — 1106 collapsed, 1324 expanded for better readability
- **Spawn abandon status** — spawn warnings show "walkback exhausted" / "spawn abandoned" status instead of silently retrying
- **Watch exit key** — changed from Backspace (conflicts with KSP Abort action group) to `[` or `]` bracket keys (#124)
- **Watch button guards** — disabled for out-of-range ghosts (tooltip: "Ghost is beyond visual range") and past recordings (#89, #90)
- **Group-level W and R/FF buttons** — recording group headers now show Watch and Rewind/FastForward buttons targeting the group's main recording (earliest non-debris descendant), no need to expand groups to access these controls (#115)
- **Watch overlay repositioned** — moved to left half of screen to avoid altimeter overlap

### Performance & Logging

- **CanRewind/CanFastForward log spam removed** — per-frame VERBOSE logs eliminated (was 578K lines/session, 94% of all output) (#117)
- **Main menu hook warning downgraded** — "Failed to register main menu hook" from WARN to VERBOSE (#118)
- **Spawn collision log demotion** — per-frame overlap log from Info to VerboseRateLimited (was ~24K lines/session)
- **GC allocation reduction** — per-frame allocations reduced in spawn UI via cached vessel names and eliminated redundant scans
- **Ghost FX audit** — systematic review of KSP-native component usage on ghosts; `KSPParticleEmitter` kept alive with `emit` control, `SmokeTrailControl` stripped (sets alpha to 0 on ghosts), `FXPrefab` stripped (pollutes FloatingOrigin), engine heat/RCS glow reimplementations retained (#113)
- **ParsekLog thread safety** — test overrides made thread-static to prevent cross-test pollution (#47)

### Bug Fixes

- **#45**: SubOrbital terminal state recordings no longer attempt vessel spawn
- **#47**: ParsekLog test overrides made thread-static
- **#85**: Fairing nosecone cap added to generated cone mesh
- **#86**: Heat material fallback only clones tracked materials, preventing red tint
- **#87**: Chain boundary commits no longer create nested groups in tree mode
- **#89**: Watch button disabled for ghosts beyond visual range
- **#90**: Watch button disabled for past (finished) recordings
- **#91**: Fairing internal structure hidden on ghost, revealed on jettison
- **#92**: Zone rendering tests updated for Visual-zone part events
- **#93**: Surface ghost slide fixed via orbit segment skip and SMA sanity check
- **#94**: Recovered/destroyed terminal state no longer corrupts committed recordings
- **#98**: Save data leak on same-name save recreation fixed via main menu reset
- **#103**: Localization keys resolved in vessel names and group headers
- **#104**: Multiple launches of same craft get unique group names
- **#105**: Ghost plume bubble artifacts fixed by using KSP-native emission
- **#106**: Watch camera booster fix via continuation point seeding
- **#108**: Synthetic shutdown events emitted at recording stop for all active engines/RCS
- **#109**: Spawned vessel cleanup preserved on second rewind
- **#110**: Spawn collision retry limited to 150 frames with abandon
- **#110b**: Spawn-die-respawn infinite loop stopped after 3 death cycles
- **#111**: Auto-record settle timer seeded on vessel switch
- **#114**: Non-leaf and FLYING/SUB_ORBITAL recordings blocked from spawning; crew stripped from destroyed-vessel snapshots
- **#119**: Watched ghosts exempt from zone distance hiding
- **#120**: Tree recordings set ForceSpawnNewVessel on merge
- **#61**: Controlled children now recorded after breakup (was "deferred to Phase 2")
- **#124**: Watch exit key changed from Backspace to brackets (Abort action group conflict)

---

## 0.5.0

Recording system redesign: multi-vessel sessions, ghost chain paradox prevention, spawn safety, time jump, and rendering zones. Recording format v5 → v7 (backward compatible).

### Recording System Redesign

- **Segment boundary rule** — only physical structural separation creates new segments. Controller changes, part destruction without splitting, and crew transfers are recorded as SegmentEvents within a continuing segment.
- **Crash coalescing** — rapid split events grouped into single BREAKUP BranchPoints via 0.5s window.
- **Environment taxonomy** — 5-state classification (Atmospheric, ExoPropulsive, ExoBallistic, SurfaceMobile, SurfaceStationary) with hysteresis (1s thrust, 3s surface speed, 0.5s surface/atmospheric bounce).
- **TrackSections** — typed trajectory chunks tagged with environment, reference frame, and data source.
- **Reference frames** — ABSOLUTE for physics, ORBITAL_CHECKPOINT for on-rails, RELATIVE for anchor-vessel proximity.

### Multi-Vessel Sessions

- **Background vessel recording** — all vessels in the physics bubble sampled at proximity-based rates (<200m: 5Hz, 200m-1km: 2Hz, 1-2.3km: 0.5Hz) with full part event capture.
- **Background vessel split detection** — creates tree BranchPoints + child recordings for all new vessels from separations. Debris children get 30s TTL.
- **Debris split detection** — `onPartDeCoupleNewVesselComplete` catches booster/debris vessels synchronously at decouple time. Debris trajectory recording planned for v0.5.1.
- **Highest-fidelity-wins merge** — overlapping Active/Background/Checkpoint TrackSections merged per vessel with snap-switch at boundaries.
- **Per-vessel merge dialog** — extended dialog shows per-vessel persist/ghost-only decisions.

### Relative Frames & Anchoring

- **Anchor detection** — nearest in-flight vessel with 2300m entry / 2500m exit hysteresis. Landed/splashed vessels excluded (not loaded during playback far from surface).
- **Relative recording** — offsets stored as dx/dy/dz from anchor vessel for pixel-perfect docking playback.
- **Relative playback** — ghost positioned at anchor's current world position + stored offset, FloatingOrigin-safe.
- **Loop phase tracking** — preserves phase across anchor vessel load/unload via pure arithmetic.

### Ghost Chain Paradox Prevention

- **Ghost chain model** — committed recordings that interact with pre-existing vessels (docking, undocking, etc.) cause those vessels to become ghosts from rewind until the chain tip resolves.
- **Chain walker algorithm** — scans all committed trees for vessel-claiming events, builds ordered chains, resolves cross-tree links.
- **Intermediate spawn suppression** — multi-link chains (bare-S → S+A → S+A+B) only spawn at the tip.
- **Ghost conversion** — real vessels despawned and replaced with ghost GameObjects during chain windows.
- **PID preservation** — chain-tip spawns preserve the original vessel's persistentId for cross-tree chain linking.
- **Ghosting trigger taxonomy** — structural events, orbital changes, and part state changes trigger ghosting; cosmetic events (lights) do not.

### Spawn Safety

- **Bounding box collision detection** — spawn blocked when overlapping with loaded vessels (active vessel, debris, EVA, flags excluded).
- **Ghost extension** — ghost continues on propagated orbit/surface past recording end while spawn is blocked.
- **Trajectory walkback** — for immovable blockers, walks backward along recorded trajectory to find a valid spawn position.
- **Terrain correction** — surface spawns adjusted for terrain height changes between recording and playback.

### Time Jump

- **Relative-state time jump** — discrete UT skip that advances the game clock while keeping the physics bubble frozen in place, preserving rendezvous geometry across ghost chain windows.
- **Epoch-shifted orbits** — orbital elements recomputed at the new UT from captured state vectors for Keplerian consistency.
- **TIME_JUMP SegmentEvent** — records the discontinuity for playback handling.

### Ghost World Presence

- **CommNet relay** — ghost vessels register as CommNet nodes with antenna specs from ModuleDataTransmitter, maintaining communication network coverage during ghost windows.
- **Ghost labels** — floating text labels showing vessel name, ghost status, and chain tip UT.
- **Map view / tracking station** — infrastructure stubs for ghost orbit lines and nav targets (full KSP integration pending).

### Rendering & Performance

- **Distance-based zones** — Physics (<2.3km, full fidelity), Visual (2.3-120km, mesh only), Beyond (120km+, no mesh).
- **Zone-aware playback** — per-ghost distance computation, zone transition detection, part events gated to Physics zone.
- **Ghost soft caps** — configurable thresholds with priority-based despawning (LoopedOldest first, FullTimeline kept longest). Disabled by default until profiled.
- **Settings UI** — three slider controls for cap thresholds with enable toggle and live apply.
- **Log spam mitigation** — rate-limited high-volume diagnostics (SoftCap, zone, heat, engine FX).

### Bug Fixes (70 tracked, 48 fixed)

- **#51**: Chain ID lost on vessel-switch auto-stop — proper segment commit and chain termination
- **#52**: CanRewind log spam (485K lines) — verbose removed from success path
- **#53**: Re-show log spam (16K lines) — deduplicated via loggedReshow HashSet
- **#54**: Watch mode beyond terrain range — 2s grace period then auto-exit
- **#55**: RELATIVE anchor on debris — vessel type filtering + surface skip
- **#9**: Zero-frame TrackSections from brief RELATIVE flickers — discarded
- Active TrackSections not flushed to tree recordings — FlushRecorderToTreeRecording, CreateSplitBranch, CreateMergeBranch now copy TrackSections
- Watch mode camera re-targeting — deferred spawn no longer switches camera to spawned vessel after watch mode ends at recording boundary
- Rewind save propagation fixed across tree/EVA/split paths
- Soft cap spawn-despawn loop — suppression set prevents re-spawn after cap despawn
- Zone hide vs warp re-show loop — check currentZone before re-showing
- False RELATIVE anchor at launchpad — skip anchor detection on surface
- Watch mode on beyond-range looped ghost — loop phase offset reset
- Background split children capture vessel snapshots for ghost playback
- See `docs/dev/todo-and-known-bugs.md` for full list

### Format Changes

- Recording format v5 → v7 (additive, backward compatible)
- v6: SegmentEvents, TrackSections, ControllerInfo, extended BranchPoint types (Launch, Breakup, Terminal)
- v7: TerrainHeightAtEnd for surface spawn terrain correction
- Old recordings (v5) play back unchanged using legacy flat Points path

### Test Coverage

2994 tests (up from 1748 in v0.4.3).

---

## 0.4.3

Code refactor + UI improvements.

### Code Refactor

- ~73 methods extracted across 38 source files
- 6 new focused files: GhostTypes.cs, Recording.cs, GhostPlaybackState.cs, GhostPlaybackLogic.cs, PartStateSeeder.cs, GhostBuildResult
- ParsekFlight.cs reduced from 8,225 to ~7,000 lines
- ParsekKSC/ParsekFlight coupling eliminated

### New Features

- Hide column replaces Delete in Recordings Manager
- Group disband replaces group delete
- Context-aware rewind button (R for past, FF for future)
- Sandbox rewind fix

### Test Coverage

1748 tests (up from 1394 in v0.4.2).

---

## 0.4.2

### New Features

- Auto-loop with per-recording unit selector (sec/min/hr/auto)
- Recording groups with multi-membership and nesting
- KSC scene ghost playback with overlap support
- Time warp visual cutoffs and deferred spawn queue

### Visual Improvements

- 3-state heat model (cold/medium/hot)
- Heat shield ablation, smoke puff + spark FX on decouple/destroy
- FXModuleAnimateRCS, ModuleColorChanger, fairing ghost visuals
- EVA kerbal facing fix

### Bug Fixes

- #28-#44: 17 fixes including damaged wheel filtering, RCS debounce, FX activation, rate-limited log spam

---

## 0.4.1

### Bug Fixes

- #26: EVA crew swap after merging from KSC
- #40: Save contamination between saves
- #41: Watch camera stuck after loop explosion with time warp

### New Features

- Explosion visual effect on impact with camera hold
- Overlapping ghost support for negative loop intervals
- Loop interval editing per-recording
- KSC toolbar UI
- Short recording auto-discard (<10s AND <30m)

---

## 0.4.0

### New Features

- Orbital rotation fidelity — ghosts hold recorded SAS orientation during orbital playback
- PersistentRotation mod support — spinning vessels reproduced during ghost playback
- Camera recenters on ghost after separation events in Watch mode

### Bug Fixes

- #17: Re-entry FX too large — replaced with mesh-surface fire particles matching stock aeroFX
- #18: Engine nozzle glow persists after shutdown
- #19: Watch button broken for looped segments
- #21-#24: Ghost build spam, facility warnings, dead geometry stubs, variant warnings

### Test Coverage

1263 tests.

---

## 0.3.1

### Bug Fixes

- Auto-migrate v4 world-space rotation to v5 surface-relative
- Remove incorrect planetary spin correction
- Fix rewind save lost on atmosphere boundary false alarm
- Fix chain orphan on vessel destruction
- Fix EVA ghost showing capsule mesh

### Improvements

- Collapsible recording groups
- Recording format v5 (surface-relative rotation)

---

## 0.3.0

Initial public release.

- Position recording with adaptive sampling
- Ghost playback with opaque vessel replicas
- Rewind to any earlier timeline point
- Multi-vessel recording (undock, EVA, dock)
- Career mode: milestones, resource budgeting, action blocking
- 28 part event types replayed on ghosts
- Orbital recording with analytical Keplerian orbits
- Recordings Manager UI

**Dependencies:** Module Manager, HarmonyKSP, ClickThroughBlocker, ToolbarControl
