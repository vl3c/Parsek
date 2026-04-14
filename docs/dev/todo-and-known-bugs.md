# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.
Entries 272–303 (78 bugs, 6 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v2.md`.

---

# Known Bugs

## ~~355. Flight anchor-camera ghost can miss engine plumes until watch mode~~

**Observed in:** `logs/2026-04-14_1354_flight-ghost-engine-off` (2026-04-14). In Flight, the primary `Kerbal X` ghost looked like its engines were off from the normal anchor / in-flight camera view at liftoff even though `Watch Ghost` immediately showed the same ghost with engine plumes, and KSC playback also showed the plumes correctly.

**Root cause:** the April 13 deferred-activation changes hid fresh ghosts until playback sync, but the Flight anchor-camera path was still consuming engine/RCS runtime state while that ghost was deferred/inactive. By the time the ghost became visible, the engine start/throttle events had already been applied and the one-shot runtime plume state was gone, so the first visible frame showed the mesh without the running plume FX. `Watch Ghost` and KSC playback did not regress because they replayed or applied the same runtime state while the ghost was already active.

**Fix:** ghost playback now tracks current engine/RCS/audio power while applying runtime events and restores that deferred runtime FX state on the first active frame after deferred sync, but only when visual FX are not suppressed. The fix also clears tracked power on stop/decouple/destroy paths so stale runtime FX state cannot replay later. Focused regression coverage now pins the tracked-power collection/clearing helpers and the first-activation restore gate.

**Status:** ~~Fixed~~ in PR `#281`

---

## ~~354. Orbital end-of-playback spawns can use the wrong vessel snapshot even when the orbit is correct~~

**Observed in:** `logs/2026-04-14_0419_high-warp-orbit-wrong-real-orbit` and `logs/2026-04-14_0434_orbital-spawn-wrong-snapshot-followup` (2026-04-14). After the `#353` orbit-source fixes, the real vessel for `Kerbal X` spawned onto the expected last stable Kerbin orbit instead of dying or inheriting a nonsense orbit, but the spawned vessel state still matched an older breakup-time snapshot instead of the final stable-recording state.

**Root cause:** the bad snapshot was already persisted before spawn. In the breakup-continuous tree design, the active recording keeps its earlier `ChildBranchPointId`, so `FinalizeIndividualRecording()` treated it as a non-leaf and skipped the stable-terminal re-snapshot path that normal landed/splashed/orbiting leaves use. `EnsureActiveRecordingTerminalState()` only set `TerminalStateValue`; it did not refresh `VesselSnapshot`, so the tree kept the old post-breakup `_vessel.craft` sidecar even though the terminal orbit had been updated correctly.

**Fix:** tree finalization now detects when the active recording is the effective leaf for its vessel and ends in a stable spawnable terminal state (`Landed`, `Splashed`, `Orbiting`). For that case it captures terminal orbit/position from the live vessel and rewrites the terminal snapshot before the tree is persisted, using the same stable-terminal snapshot refresh path as ordinary leaf recordings. Added regression coverage for the effective-leaf versus same-PID-continuation gating helper.

**Status:** ~~Fixed~~

---

## ~~353. High-warp orbital end-of-playback spawns can immediately die to on-rails pressure despite a valid rebuilt orbit~~

**Observed in:** `logs/2026-04-14_0301_high-warp-orbit-spawn-missing` and `logs/2026-04-14_0359_high-warp-orbit-stable-orbit-trim` (2026-04-14). During deferred high-time-warp orbital playback for `Kerbal X`, Parsek reported a successful real-vessel spawn but nothing materialized in orbit. The newer bundle also showed the ghost replaying a long stable orbital coast all the way to the original recording end instead of trimming to the normal boring-state buffer after orbit insertion.

**Root cause:** two faults were compounding on the same path. First, `RecordingOptimizer.FindLastInterestingUT()` treated a late zero-throttle `EngineIgnited` seed artifact as real activity, so stable `ExoBallistic` tails were not trimmed to `last real activity + 10s`; playback stayed alive until the raw recording end instead of resolving shortly after the vessel entered its final stable orbit. Second, when the deferred orbital spawn finally ran, the spawn path was mixing time domains: it rebuilt the real vessel at the current `spawnUT`, but it fed `SpawnAtPosition()` a lat/lon/alt + velocity sample captured at the recording endpoint UT. During high warp, that let `GetWorldSurfacePosition()` use a later body rotation while the velocity still belonged to the old inertial state, so the reconstructed orbit could collapse into a suborbital trajectory even though the recording's stored terminal orbit was valid. The reused ascent snapshot also still carried stale packed-vessel and per-part atmospheric fields (`hgt`, PQS bounds, `altDispState`, `tempExt`, `tempExtUnexp`, `staticPressureAtm`), which made the packed vessel look even less like a stock orbital save-state.

**Fix:** stable-orbit boring tails now ignore inert zero-throttle engine/RCS control-state seed artifacts when computing `lastInterestingUT`, so they trim to the normal `~10s` boring-state buffer after the last real activity instead of replaying a full orbital coast. Orbiting end-of-playback spawns also now prefer the recording's stored terminal orbit: `SpawnOrRecoverIfTooClose()` propagates that orbit to the current spawn UT, derives a current lat/lon/alt + orbital velocity from it, and only then calls `SpawnAtPosition()`. `NormalizeOrbitalSpawnMetadata()` also now scrubs both top-level packed fields and per-part atmospheric state back to stock orbital defaults before `ProtoVessel.Load()`. Added regression coverage for the terminal-orbit spawn-state selection, orbital metadata normalization, and zero-throttle engine-seed filtering in boring-tail trim.

**Status:** ~~Fixed~~

---

## ~~352. Pending-tree merge dialogs can ghost-only the active vessel even when playback would spawn it~~

**Observed in:** `wt-fix-mun-landing-persist` follow-up (2026-04-14). In the merge dialog for a pending tree, an active non-leaf vessel from a breakup-continuous Mun landing or splashdown could default to ghost-only even though the same recording would be spawnable once committed and played back.

**Root cause:** `MergeDialog.CanPersistVessel()` reuses `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd()`, but that policy originally resolved effective-leaf and non-leaf safety-net checks only through `RecordingStore.CommittedTrees`. During the merge dialog the tree is still pending, so active non-leaf recordings had no resolvable tree context and lost the breakup/effective-leaf distinction.

**Fix:** `ShouldSpawnAtRecordingEnd()` now accepts an optional `RecordingTree` context and resolves effective-leaf / non-leaf safety-net checks against either the pending tree or the committed tree. `MergeDialog.BuildDefaultVesselDecisions()` passes the pending tree through for both normal leaves and the active non-leaf fallback, and regression coverage now pins pending-tree effective-leaf, same-PID continuation, and null-`ChildBranchPointId` safety-net behavior.

**Status:** ~~Fixed~~

---

## ~~351. Long-range landed ghosts can clip into terrain on held final-pose paths~~

**Observed in:** `logs/2026-04-13_2136` ghost-underground follow-up (2026-04-13). A landed watched ghost in the visual tier could still end a playback step with its mesh partially sunk into terrain even though the normal in-flight clamp path kept earlier frames above ground.

**Root cause:** the immediate surface-position paths (`past-end` hold, loop hold/boundary, overlap expiry) still used only the small fixed landed-ghost floor while normal frame-by-frame playback already used distance-aware terrain clearance. At long watch distances, or on legacy recordings with `TerrainHeightAtEnd = NaN`, the held terminal pose could therefore clip into terrain on the last frame.

**Fix:** `PositionGhostAtPoint` now routes landed/splashed hold positioning through the same clearance-aware `ApplyLandedGhostClearance(...)` logic, and the legacy-NaN fallback keeps the historical minimum floor while allowing larger distance-aware clearance when needed. Added regression coverage for the long-range repro and the NaN fallback policy.

**Status:** ~~Fixed~~ in PR `#262`

---

## ~~350. Automatic watch handoff can briefly snap `HorizonLocked` camera orientation on retarget~~

**Observed in:** automatic watch-retarget follow-up (2026-04-13). When watch auto-follow transferred to a continuation ghost while already `HorizonLocked`, the first frame after retarget could use the new target with a stale horizon basis, causing a visible heading/pitch snap before the next per-frame horizon update corrected it.

**Root cause:** `TransferWatchToNextSegment()` and the watch-mode toggle path applied the new target before refreshing that ghost's `horizonProxy` rotation. Camera-target compensation therefore ran against stale orientation state on the first retargeted frame.

**Fix:** extracted `UpdateHorizonProxyRotation()` and now prime the target orientation before `ApplyCameraTarget()` on auto-follow retargets and watch-mode toggles, so the first `HorizonLocked` frame already uses the correct target basis.

**Status:** ~~Fixed~~ in PR `#255`

---

## ~~350. Boarded EVA re-entry playback can drop the boarded tail and final capsule spawn~~

**Observed in:** `logs/2026-04-14_0000_main-stage-forward-bias/`. User report: during playback, the last kerbal showed the initial EVA exit but not the later circling/re-entry back into the capsule, and at recording end only the two EVA kerbals spawned while the capsule with the re-boarded kerbal never materialized.

**Root cause:** The repro had two separate failures in the same board/re-entry chain.

- `SessionMerger.MergeTree()` rebuilt the parent recording's flat trajectory from stale `TrackSections` even after newer flat points had been appended post-boarding. That collapsed the merged parent down to the section payload prefix and discarded the visible tail covering the last kerbal's circling/re-entry path.
- The boarded child recording ended as a legitimate single-point landed leaf. Playback/spawn gating still treated `< 2` points as "no renderable ghost data", so the leaf never ran the normal completion/spawn path and the final capsule with the boarded kerbal was skipped.

**Fix:** `SessionMerger` now preserves the flat trajectory whenever resolved `TrackSections` only match a prefix of newer flat data, `RecordingStore`/sidecar logic keeps those cases on the conservative flat-fallback path instead of writing them as section-authoritative, and ghost playback/spawn now treats single-point leaf recordings as renderable data while seeding the same interpolated state consumers expect from the normal point path. Added regressions for the merge path, single-point playback gating/state seeding, and direct stale-section serialization fallback coverage.

**Status:** ~~Fixed~~

---

## ~~349. Repaired stand-in rows can hide historical stand-in usage from retirement logic~~

**Observed in:** final GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). After the repair path started rewriting old stand-in `KerbalAssignment` rows back to the slot owner, the kerbals walk only recorded that logical owner name in `allRecordingCrew`. Retirement and roster-healing logic still asked whether the displaced stand-in's own name had ever appeared in recordings, so a historical stand-in like `Kirrim` could be treated as unused and deleted instead of retired once the owner reclaimed the slot.

**Root cause:** The recalculation walk conflated two different identities: the logical kerbal identity used for reservations and the raw snapshot crew names needed to know which stand-in bodies were historically flown.

**Fix:** `KerbalsModule.PrePass()` now caches each recording's raw snapshot crew names, and `ProcessAction()` feeds both the repaired logical owner name and the raw snapshot names into `allRecordingCrew`. That preserves correct retirement/deletion behavior after ledger repair. Added a regression that drives the real `CreateKerbalAssignmentActions()` path for a repaired stand-in recording and verifies the historical stand-in still retires correctly.

**Status:** ~~Fixed~~

---

## ~~348. The displaced-stand-in recreation guard can also suppress retired stand-ins~~

**Observed in:** second GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The first roster-churn fix stopped recreating any displaced, unreserved chain entry. That also covered retired stand-ins, even though the design expects retired kerbals to remain present in the roster and simply be filtered/managed.

**Root cause:** The recreation guard distinguished only "reserved" vs "not reserved". It ignored the separate retired-stand-in case where a displaced kerbal still appears in historical recordings and therefore must remain as a managed roster entry even when no longer actively reserved.

**Fix:** `ShouldEnsureChainEntryInRoster()` now keeps recreating displaced stand-ins that still appear in committed recordings, while continuing to skip genuinely unused displaced metadata. Added a regression that pins the retired stand-in branch separately from the unused branch.

**Status:** ~~Fixed~~

---

## ~~347. Historical stand-in repairs can fail once the live replacement bridge has been cleared~~

**Observed in:** second GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The new persisted-row repair compared old ledger rows against freshly regenerated rows, but regeneration still reverse-mapped stand-ins only through the live `CREW_REPLACEMENTS` bridge. If a slot owner had already reclaimed their place and the current replacement map was empty, an old stand-in row like `Hanley` still regenerated as `Hanley` even though `KERBAL_SLOTS` still knew that stand-in belonged to `Jeb`.

**Root cause:** Reverse-mapping logic ignored persisted slot-chain metadata. `CREW_REPLACEMENTS` is transient bridge state rebuilt from the current derived roster, not a complete historical identity source.

**Fix:** Stand-in reverse-mapping now falls back to persisted `KERBAL_SLOTS` when the live replacement bridge has no match, so migration/repair can still recover the original slot owner from saved chain metadata. Added regression coverage for an empty `CREW_REPLACEMENTS` map plus populated `KERBAL_SLOTS`.

**Status:** ~~Fixed~~

---

## ~~346. Ghost-only handoff fallback can misclassify stable snapshot-less chain tips as finite `Recovered`~~

**Observed in:** second GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The first handoff fix treated every ghost-only chain recording as an internal handoff. Auto-committed tree recordings can also become ghost-only by having `VesselSnapshot` nulled before ledger notification, including stable tips whose terminal state is still `Orbiting`, `Landed`, `Splashed`, or `Docked`.

**Root cause:** `ShouldUseGhostOnlyChainHandoffEndState()` keyed only on `ChainId + no VesselSnapshot + crew source`, so it could not distinguish unresolved handoff segments from stable ghost-only chain tips that still need their normal `Aboard`/`Unknown` semantics.

**Fix:** The ghost-only handoff fallback now applies only when the recording is still unresolved (`TerminalStateValue == null`) or has a genuinely finite terminal (`Recovered`, `Destroyed`, or `Boarded`). Stable ghost-only chain tips with intact terminals stay on the normal unresolved path. Added a regression that pins an `Orbiting` ghost-only chain tip to `Unknown` instead of forced `Recovered`.

**Status:** ~~Fixed~~

---

## ~~345. Existing ledger kerbal rows are not repaired once bad `KerbalAssignment` data is already persisted~~

**Observed in:** GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). The earlier audit fixes corrected new kerbal-action generation, but `MigrateKerbalAssignments()` still treated any recording with at least one existing `KerbalAssignment` row as already migrated. Old saves that had pre-fix stand-in names or ghost/EVA `Unknown` end states therefore kept replaying the stale ledger data forever.

**Root cause:** The migration path only filled missing per-recording kerbal rows; it never compared stored rows against the current derived truth for that recording, so there was no repair path for already-persisted bad actions.

**Fix:** `MigrateKerbalAssignments()` now builds the desired `KerbalAssignment` set for every committed recording, compares it to the stored rows, and rewrites just that recording's kerbal actions when they diverge. Added regression coverage for both repaired stand-in identity rows and repaired ghost-only `Unknown` rows so legacy ledgers heal on the next load.

**Status:** ~~Fixed~~

---

## ~~344. Ghost-only chain segments fall back to open-ended `Unknown` kerbal reservations~~

**Observed in:** GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). Mid-chain vessel/EVA recordings are committed with `VesselSnapshot = null`, but `CreateKerbalAssignmentActions()` still extracted crew from `GhostVisualSnapshot`. Because the old end-state population guard only trusted `VesselSnapshot` or `EvaCrewName`, those segments emitted `KerbalAssignment` rows with `KerbalEndState.Unknown`, which the kerbals walk treated as infinite temporary reservations.

**Root cause:** Ghost-only chain handoff segments had no dedicated fallback end-state rule. The end-state population path assumed "no end snapshot" meant "can't resolve", even though these recordings are a known internal handoff case where the reservation should stay finite until later chain segments extend it.

**Fix:** Ghost-only chain recordings now participate in crew end-state population, and their fallback state is treated as a finite chain handoff (`Recovered`, or `Dead` when the segment truly ended destroyed). `CreateKerbalAssignmentActions()` now forces that shared population path before emitting actions, so commit-time, migration, and load-repair all converge on the same non-`Unknown` result. Added regression coverage for both direct action creation and the load-time safety net.

**Status:** ~~Fixed~~

---

## ~~343. Persisted displaced stand-ins can be recreated on every later roster walk~~

**Observed in:** GPT-5.4 xhigh PR review for `review/kerbals-recording-audit` (2026-04-13). After bug `#339` started preserving displaced chain metadata, `ApplyToRoster()` still recreated any missing non-null chain entry before the delete/retire pass, so a stand-in that had already been deleted as displaced/unused would be re-hired and deleted again on every recalculation.

**Root cause:** The roster-ensure pass only checked whether a chain entry name was missing from `KerbalRoster`. It did not distinguish active/reserved chain occupants from displaced metadata that intentionally no longer had a live roster entry.

**Fix:** `ApplyToRoster()` now skips roster creation/recreation for displaced chain entries unless that stand-in is still actively reserved. The chain metadata remains persisted for deterministic rewinds, but deleted displaced stand-ins no longer churn back into the roster on later passes. Added a regression test that pins the helper decision for active vs displaced chain entries.

**Status:** ~~Fixed~~

---

## ~~342. Tourist passengers can leak into the managed kerbal reservation system~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `CreateKerbalAssignmentActions()` emitted actions for every crew name in the recording snapshot, and `KerbalsModule.ProcessAction()` reserved every `KerbalAssignment` regardless of role, even though the design treats tourist passengers as contract-only temporary crew.

**Root cause:** The kerbal action pipeline had no tourist-role exclusion. Action creation depended on `FindTraitForKerbal`, but there was no defense once a `KerbalAssignment` existed, and the non-runtime fallback could not identify tourists from saved roster history.

**Fix:** `FindTraitForKerbal()` now falls back to the latest saved game-state baseline crew traits when a live KSP roster is unavailable, `LedgerOrchestrator` skips crew whose resolved role is `Tourist`, and `KerbalsModule.ProcessAction()` ignores any tourist assignments already present in the ledger as defense in depth. Added regression coverage for baseline trait fallback, tourist action suppression, and tourist action creation filtering.

**Status:** ~~Fixed~~

---

## ~~341. EVA-only recordings can skip crew end-state population during migration/load repair~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). Commit-time kerbal action creation already treated `EvaCrewName` as a valid crew source, but the legacy migration path and the "populate missing end states on load" safety net only ran when `VesselSnapshot != null`.

**Root cause:** `MigrateKerbalAssignments()` and `PopulateUnpopulatedCrewEndStates()` duplicated an older, narrower guard instead of reusing the commit-time "has crew source" predicate. EVA recordings that stored only `EvaCrewName` therefore skipped end-state inference on those later paths.

**Fix:** The migration and safety-net paths now share a single `NeedsCrewEndStatePopulation()` predicate that accepts either `VesselSnapshot` or `EvaCrewName`. Added `LedgerOrchestratorTests` regressions for both private paths to pin EVA-only recordings to a resolved `Dead`/`Recovered`/etc. end state instead of `Unknown`.

**Status:** ~~Fixed~~

---

## ~~340. Permanent-loss slots can keep stale stand-in occupancy or stay permanently gone after the death is rewound away~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). Slots with an old temporary chain could still present a stand-in as the active occupant after the owner later died permanently, and a saved `permanentlyGone=True` flag could persist even after the death-causing recording was removed from the timeline.

**Root cause:** `OwnerPermanentlyGone` lived in persisted slot state but was not recomputed from scratch each recalculation pass. Once set, it stayed sticky until a full slot reset, and older chain entries could still influence occupant selection unless the active-occupant logic explicitly rejected permanently gone owners.

**Fix:** `PostWalk()` now clears and rebuilds `OwnerPermanentlyGone` from the current reservations on every pass, and regression coverage now pins both sides of the behavior: permanent owners with an existing chain have no active replacement occupant, and rewinding away the permanent loss restores the slot to a normal owner-active state.

**Status:** ~~Fixed~~

---

## ~~339. Chain reclaim can keep the deepest free stand-in active after an earlier occupant should have reclaimed~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `KerbalsModule.GetActiveOccupant()` walked chains from deepest to shallowest, so if `Jeb -> [Hanley, Kirrim]` and only Jeb remained reserved, Parsek still preferred Kirrim instead of letting Hanley reclaim.

**Root cause:** The active-occupant and retirement logic treated "deepest free stand-in" as the winner even after an earlier chain member became free again. That contradicted the design rule that reclaim stops at the first free occupant after the reserved prefix and all deeper entries become displaced.

**Fix:** Kerbal-chain evaluation now follows the reserved prefix and picks the first free occupant as active. Deeper free stand-ins are treated as displaced for retirement/deletion, and `ApplyToRoster()` keeps the chain metadata instead of clearing it wholesale so the derived state stays consistent across later recalculations. Added regression coverage in `KerbalReservationTests` for reclaim ordering and displaced deeper stand-ins.

**Status:** ~~Fixed~~

---

## ~~338. Cold-start `OnLoad` can skip persisted `KERBAL_SLOTS` before the kerbals module exists~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `ParsekScenario.OnLoad` called `LoadCrewAndGroupState()` before `LoadExternalFilesAndRestoreEpoch()`, so `LedgerOrchestrator.Kerbals?.LoadSlots(node)` ran while `LedgerOrchestrator.OnLoad()` had not yet initialized the kerbals module.

**Root cause:** Slot loading relied on an already-created `LedgerOrchestrator.Kerbals` instance, but the first-load ordering guaranteed that the module was still null on a true cold start.

**Fix:** `LoadCrewAndGroupState()` now initializes `LedgerOrchestrator` before loading slots, preserving the intended "load crew replacements + slot graph from save" behavior even on the first load after launching KSP. Added a `QuickloadResumeTests` regression that invokes the private load helper and verifies slot state is populated from the scenario node.

**Status:** ~~Fixed~~

---

## ~~337. KerbalAssignment creation can reserve a stand-in instead of the original kerbal~~

**Observed in:** Kerbals/events-actions audit (2026-04-13). `PopulateCrewEndStates` already reverse-mapped stand-in names back to the original slot owner, but `LedgerOrchestrator.CreateKerbalAssignmentActions` re-extracted raw snapshot crew and emitted the stand-in name directly.

**Root cause:** `ExtractCrewFromRecording` did not apply the same `CrewReplacements` reverse-map that `KerbalsModule.PopulateCrewEndStates` uses. When `CrewEndStates` was keyed by the original owner and the emitted action used the stand-in name, the end-state lookup missed and the ledger reserved the temporary stand-in as `Unknown`.

**Fix:** `ExtractCrewFromRecording` now reverse-maps snapshot crew through `CrewReservationManager.CrewReplacements` before building `CrewInfo`, so `CreateKerbalAssignmentActions` and `CrewEndStates` use the same kerbal identity. Added regression coverage in `LedgerOrchestratorTests` for both extraction and action creation.

**Status:** ~~Fixed~~

---

## ~~314. Save/load can prune branched recordings even when sidecars still contain real data~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). During a branched `Kerbal X` flight, the user saved, loaded, and then merged the tree. Two EVA/branch recordings had real sidecars on disk before the load:

- `33ea504b82cd479cbc2198c6701a9228.prec`
- `519ae674050d40e3a462cba6328a1e34.prec`

Collected evidence from `logs/2026-04-12_1549_storage-followup-playtest/`:

- Before the load, both branch recordings were actively flushing real trajectory and part-event data, and their sidecars were rewritten (`SerializeTrajectoryInto` / `SaveRecordingFiles` for both IDs).
- On load, `RecordingStore` logged sidecar epoch mismatches for both branch recordings: `.sfs expects epoch 1, .prec has epoch 2`, then skipped sidecar load entirely.
- Immediately after load, both recordings were finalized as leaf nodes with `points=0 orbitSegs=0`.
- `PruneZeroPointLeaves` then removed both zero-point leaves and the empty branch point.
- The later merge dialog only offered the root and debris leaves; the branched EVA leaves were already gone.
- The final saved tree still points `activeRecordingId = 33ea504b82cd479cbc2198c6701a9228`, but that recording is no longer serialized in the tree body.

The skipped branch sidecars still existed on disk in both the collected snapshot and the live save, so this was not physical sidecar deletion; it was save-tree loss after load/prune.

**Additional symptom:** The root recording `eb12d51ffaa64d80a79d3a0f3886e568` also appears to come back shortened: before the load it was saved with `skippedTopLevelPoints=186`, but after merge it was rewritten with `skippedTopLevelPoints=44` and `pointCount = 44` in `persistent.sfs`. The EVA branch loss is certain; root truncation may be part of the same bug or a secondary issue.

**Root cause:** The stale-sidecar epoch guard itself was correct, but the follow-on behavior was not. When active-tree recordings hit an epoch mismatch, `LoadRecordingFiles` left them empty and the restore/finalize path still treated them like genuine zero-point debris leaves. That allowed a save/load cycle to replace a matching in-memory pending tree with a broken disk copy and later prune the hydration-failed leaves out of the tree.

**Fix:** The stale-sidecar epoch guard stays in place, but hydration failures are now explicit runtime state instead of silent empties. Active-tree restore keeps a matching in-memory pending tree when the saved active tree hits stale-sidecar epoch failures, and finalize/prune no longer classifies hydration-failed recordings as removable zero-point leaves. That prevents the destructive branch-loss path even when the epoch guard rejects the disk sidecar.

**Status:** ~~Fixed~~

---

## ~~315. TreeIntegrity PID collision check fails on historical vessel reuse~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). The in-game suite reported `RecordingTreeIntegrityTests.NoPidCollisionAcrossTrees` failed with `2 vessel PID(s) claimed by multiple trees`. The collected `persistent.sfs` was otherwise structurally clean: `ParentLinksValid` passed, and manual inspection found no dangling `ParentRecordingId` references.

Concrete repro data from `logs/2026-04-12_1549_storage-followup-playtest/`: three committed `Kerbal X` roots from different trees share the same root vessel PID `2708531065`:

- tree `1683a5d7535f4370baf1ca28b7823069` root `081e7b3ce4b84acc946166a0a3b7926e`
- tree `258d8922c99a45d2a1bb4bf5f7aa7070` root `7f7eadcb943941c1a1668cd44f176459`
- tree `2dc3fa77001f4ad19e766cf6f0ac5277` root `641be2f9522d439397f4ea9fa2caabd2`

**Root cause:** `RecordingTree.RebuildBackgroundMap` populated an ambiguously named cache (`OwnedVesselPids`) from every recording's `VesselPersistentId`, while the in-game integrity test treated that cache like a globally unique cross-tree ownership set. Historical/alternate-history trees can legitimately reuse the same vessel PID, so the test was asserting the wrong contract.

**Fix:** The cache was renamed to `RecordedVesselPids` to match its real meaning ("this PID appears somewhere in this tree's recordings"), `NoPidCollisionAcrossTrees` was replaced with `BackgroundMap` integrity checks that target real corruption, and regression coverage now pins historical PID reuse as valid archived data. Chain trajectory lookup also now prefers chain-participating trees without losing the pre-claim global fallback, so overlapping historical trees do not leave chain ghosts without trajectory data.

**Status:** ~~Fixed~~

---

## ~~316. Breakup debris ghosts can spawn directly into Beyond and never become visible during playback~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). During the `s4` reentry/breakup session, some `Kerbal X Debris` recordings did render normally, but later debris recordings spawned so far from the active watch context that they immediately transitioned into the hidden `Beyond` zone and never became visible to the player.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- Early debris playback did enter the visible path: ghost `#5` transitioned `Physics->Visual dist=4457m`, and ghost `#7` transitioned `Physics->Visual dist=11876m`.
- Later debris playback for the same recording family did not: ghost `#10` spawned, immediately transitioned `Physics->Beyond dist=943546m`, and was hidden by distance LOD in the same tick.
- The same pattern repeated for ghost `#11`, which spawned and immediately transitioned `Physics->Beyond dist=952154m` before being hidden.
- The recordings themselves were present and merged correctly; the issue is playback visibility, not missing recording data.

**Root cause:** The archived failure was two bugs folded together. Early watched-lineage debris could miss protection because the archived build only protected the exact watched row, not same-tree breakup ancestry. Later debris could still fail even on newer ancestry-aware builds because automatic watch exit cleared the only visibility-protection source before those descendant debris recordings began playback.

**Fix:** Same-tree watched-debris protection now survives missing `LoopSyncParentIdx` by following branch ancestry, and playback-driven automatic watch exits now retain a bounded watched-lineage debris protection window through the last pending descendant debris `EndUT`. Failed replacement watch starts no longer clear that retained protection unless a new watch session is actually committed. The camera still exits normally; only the debris visibility exemption is retained. Added archived-topology regression coverage for:

- late debris with `LoopSyncParentIdx == -1` after final chain splitting
- same-tree ancestry fallback from watched segment to root-parented debris
- retained watched-lineage protection through the last late-debris playback window without repeated retention logs
- zone-rendering watch-protection resolution consuming the retained root for late debris
- null-loaded watch-start commits preserving the prior retained protection window
- the existing watch-target rule that still refuses to retarget camera to non-child same-tree debris

This closes the "spawned but never visible" playback path without loosening the camera handoff rule introduced for `#158`.

**Status:** ~~Fixed~~

---

## ~~317. Horizon-locked watch camera can align retrograde instead of prograde during reentry playback~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). While watching the `s4` reentry ghost, the user reported that horizon mode pointed the camera retrograde rather than prograde.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- The session entered and re-entered horizon watch mode multiple times during the watched descent:
  - `Watch camera auto-switched to HorizonLocked (alt=606m, body=Kerbin)`
  - `Watch camera auto-switched to Free (alt=70003m, body=Kerbin)`
  - repeated `Watch camera mode toggled to HorizonLocked (user override)` during the watched reentry path
- The archived logs did not emit the computed horizon forward vector, selected velocity frame, or a prograde/retrograde label, so the original report could not be proven or disproven from the collected bundle alone.

**Root cause:** `WatchModeController.UpdateHorizonProxy` fed raw playback velocity straight into the horizon-lock basis. During atmospheric watch playback, that can disagree with the ghost's surface-relative prograde once body rotation dominates the remaining horizontal component, making the camera appear retrograde even though the vessel is descending prograde relative to the atmosphere/ground track.

**Fix:** Horizon-locked watch mode now uses a dedicated atmospheric heading basis: in atmosphere it derives heading from `playbackVelocity - body.getRFrmVel(position)` before projecting onto the horizon plane, while outside atmosphere and on airless bodies it preserves the previous playback/inertial heading behavior. The watch-camera logs now emit the chosen horizon basis (`velocityFrame`, source, raw alignment, vectors) when the basis changes and also emit a rate-limited verbose snapshot during long watches so same-body direction shifts remain observable. Regression coverage now pins:

- atmospheric rotation-dominated inversion (surface-relative prograde wins over raw playback direction)
- above-atmosphere preservation of the old playback/inertial heading
- airless-body non-conversion
- zero-result fallback to `lastForward`

**Status:** Fixed in PR `#245`

---

## ~~318. Recordings window stats can show impossible distance / altitude summaries on loaded surface recordings~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). In the `s4` save, the recordings window showed incorrect `dist` / `max alt` values for recent recordings.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- `TrajectoryMath.ComputeStats` produced suspicious summaries immediately after load:
  - `points=29 segments=0 events=0 maxAlt=608 maxSpeed=175.1 dist=0 range=0 body=Kerbin`
  - `points=13 segments=0 events=0 maxAlt=611 maxSpeed=0.0 dist=2 range=0 body=Kerbin`
  - nearby recordings in the same table pass produced more plausible values (`points=58 ... dist=177`, `points=13 ... dist=126`)
- The recordings window computes these values live from loaded recordings rather than trusting `.sfs` cache fields, so this points at a loaded-trajectory/stats issue, not merely stale serialized UI metadata.

**Root cause:** Two storage-side consistency gaps were involved:

- section-authoritative recordings could keep stale flat `Points` / `OrbitSegments` after merge or optimizer split because `TrackSections` changed but the derived flat lists were left copied from the pre-merge/pre-split source
- `ComputeStats` treated relative-frame flattened points as if their `latitude` / `longitude` / `altitude` fields were absolute surface coordinates, even though relative sections reuse those fields for `(dx, dy, dz)` offsets

That combination was enough to produce contradictory summaries like `maxSpeed>0` with `dist=0`.

**Fix:** Section-authoritative merge/split paths now resync flat trajectory lists from `TrackSections` whenever the section payload can rebuild them losslessly, instead of keeping stale copied flats. `TrajectoryMath.ComputeStats` also now applies section altitude metadata and handles relative-frame point distances/ranges as offset-space measurements instead of feeding them through surface-distance math.

**Status:** ~~Fixed~~

---

## ~~319. Watch buttons can disable as "no ghost" after chain transfer even when the user expects an in-range watch target~~

**Observed in:** 0.8.0 follow-up storage playtest (2026-04-12). The user reported disabled watch buttons while apparently within the ghost camera cutoff distance.

Collected evidence from `logs/2026-04-12_1857_phase-11-5-storage-followup-s4/`:

- Before transfer, the group-level watch affordance was valid: `Group Watch button 'Kerbal X' main=#0 "Kerbal X" enabled (hasGhost=True sameBody=True inRange=True)`.
- After `TransferWatch re-target: ghost #1 "Kerbal X" ...`, the same group button flipped to `disabled (no ghost) (hasGhost=False sameBody=False inRange=False)`.
- Later rows for descendant recordings `#12` and `#13` also logged `disabled (no ghost)`, not `disabled (out of range)`.
- Debris rows were separately disabled as `debris`, which is expected and distinct from the reported symptom.

**Root cause:** This was not a pure cutoff-distance bug. Watch auto-follow could legitimately retarget to a descendant ghost while the recordings table still evaluated the group `W` button against the group's original main recording, whose own ghost had already been destroyed. That stale source-row evaluation produced a misleading `disabled (no ghost)` state even though watch had already transferred to a valid descendant.

**Fix:** Added shared watch-target resolution in `GhostPlaybackLogic` so group watch affordances follow the same continuation lineage as watch auto-follow, including multi-hop same-PID handoffs through inactive intermediates. Group `W` now evaluates and enters watch on the resolved live target, while per-row `W` semantics stay exact-recording to avoid duplicate/stale `W*` states. Logging now includes both source and resolved watch-target context, and the fallback rules remain aligned with actual auto-follow semantics (no illegal descent through non-breakup fallback branches, no breakup fallback to different-PID children).

**Status:** Fixed

---

## ~~320. Merge confirmation should appear before the stock crash report on vessel destruction~~

**Observed in:** Phase 11.5 storage/watch follow-up playtests (2026-04-12). On vessel destruction, the old/good behavior was: Parsek's merge confirmation appeared before KSP's stock crash/flight-results report. The current ordering regressed, making the crash report take focus first.

**Desired behavior:** When a recording session ends via vessel destruction and Parsek needs merge/commit input, surface the merge confirmation before the stock crash report so the Parsek flow is not hidden behind the stock dialog.

**Root cause:** The recent follow-up fix over-corrected the original ordering bug. When the active vessel crashed and only controller-less debris leaves were still unresolved, `ShowPostDestructionTreeMergeDialog()` stopped finalizing the tree in `FLIGHT` and instead waited for those debris leaves to become terminal or for a later revert/scene-load path to claim ownership. In the real repro, one debris leaf stayed live long enough that the wait timed out, the merge dialog fell back to `OnFlightReady()`, and the deferred crash dialog could then be replayed in the wrong scene or after the wrong owner had already resolved the tree.

**Fix:** The active-crash tree path no longer waits for a later debris-only fallback owner. After the one-frame destruction settle, if the only remaining blockers are debris leaves, Parsek finalizes the tree immediately in `FLIGHT` and shows the merge dialog there. That restores the original ordering: merge confirmation first, stock crash report after the merge/discard choice. The merge dialog's default "spawnable" decisions now also reuse the real playback spawn policy, so debris and `SubOrbital` leaves no longer show up as fake surviving vessels. Regression coverage now pins the debris-only crash classification to same-scene finalization and the merge-dialog spawn-policy alignment.

**Status:** ~~Fixed~~

---

## ~~321. After the main controller vessel crashes, camera recovery should prefer the anchor vessel, not debris~~

**Observed in:** breakup/watch regression follow-up from `logs/2026-04-12_2055_main-stage-freeze-after-separation/` (`s6`). After the main controlling vessel crashed, the player expectation was to return camera focus to the anchor vessel rather than letting it drift to debris-focused behavior.

**Desired behavior:** When the main controller vessel is lost, the camera should recover to the anchor vessel / stable owning vessel context, not to a debris fragment.

**Root cause:** `GhostPlaybackLogic.FindNextWatchTarget` treated the first active tree child as a generic watch handoff fallback when no same-PID continuation existed. On breakup/crash trees, that let debris win the handoff path.

**Fix:** Breakup/crash watch recovery no longer auto-follows any different-PID breakup child as a generic fallback. Same-PID continuation and `#158` recursive hold/retry behavior are unchanged, but breakup branches without same-PID continuation now return `-1` and let the existing watch hold/exit path restore the preserved live vessel context instead of transferring to debris or another fragment. Added regression coverage for non-breakup fallback, breakup no-fallback, debris-only `-1`, and hold-path behavior.

**Status:** Fixed

---

## ~~322. Detached one-ended struts should not remain visible after separation~~

**Observed in:** breakup/separation playback follow-ups (2026-04-12), including the `s6` separation run. After separation, some struts whose opposite endpoint no longer exists remain visible even though they are only attached to a single surviving part.

**Desired behavior:** A strut should not render once separation leaves it effectively connected to only one part.

**Root cause:** Compound-part ghost build parsed the linked-mesh endpoint pose but did not carry the linked target part PID into playback state. After spawn, separation/destroy/inventory events hid direct part visuals, but compound-part replay never revalidated whether the other endpoint still existed logically, so orphaned strut visuals could remain attached to the surviving vessel.

**Fix:** Ghost build now records compound-part target persistent IDs and logical part presence, then playback revalidates compound-part visibility both at spawn and after visibility-affecting part events. The runtime path now removes logically missing targets from the presence set on decouple/destroy/inventory removal, restores compound parts only when a later placement makes the link valid again, and includes focused regression coverage for target PID parsing plus compound visibility / subtree-removal decisions.

**Status:** Fixed

---

## ~~323. Main-stage and debris group hierarchy can appear wrong after commit/playback~~

**Observed in:** `logs/2026-04-12_2128_kerbalx-booster-throttle-regression/` (`s7`). User report: a recording did not correctly group the main-stage recordings group together with the debris group.

**What the logs showed:** raw auto-grouping did run at commit time:

- `Group 'Kerbal X / Debris' assigned to parent group 'Kerbal X'`
- `Auto-grouped 1 stage(s) under 'Kerbal X', 10 debris under 'Kerbal X / Debris'`

That means the bundle did not prove a bad `GroupHierarchyStore.SetGroupParent(...)` call during commit. The failure was later: during an in-session `ParsekScenario.OnLoad`, Parsek preserved the live committed recordings from memory but still reloaded `GROUP_HIERARCHY` from the save file. If the save's hierarchy snapshot was stale, root-level or otherwise corrected debris groups could be overwritten by older parent mappings even though the committed recordings themselves were current.

Concrete evidence from the collected bundle:

- `KSP.log` reported `18 committed recordings`, `2 committed tree(s)`, but only `Saved group hierarchy: 1 entries`
- the same save still serialized recordings under both `Kerbal X` and `Kerbal X / Debris`
- `persistent.sfs` no longer contained the expected `Kerbal X / Debris -> Kerbal X` hierarchy entry

So the problem was not "bad auto-grouping on commit"; it was "stale hierarchy load clobbers the live session view."

**Fix:** `LoadCrewAndGroupState` now only reloads group hierarchy from the save on true initial load. In-session loads and rewinds keep the in-memory hierarchy as the source of truth, matching the already-preserved in-memory committed recordings. This avoids reconstructing or guessing hierarchy from group names and prevents stale `.sfs` `GROUP_HIERARCHY` data from re-parenting root-level debris groups. Coverage now includes:

- unit tests for `ShouldLoadGroupHierarchyFromSave(...)`
- a live in-game `ParsekScenario.OnLoad` regression that seeds a root-level debris group, feeds `OnLoad` a stale saved parent mapping, and proves the in-memory root-level group remains unparented while existing valid mappings survive

**Status:** ~~Fixed~~

---

## ~~324. Pad-drop launch failures can surface a merge dialog instead of auto-discarding~~

**Observed in:** 2026-04-12 follow-up playtests. User report: a vessel launched, engines were never activated, and it simply fell over / down on the pad. Despite essentially zero horizontal travel, Parsek still surfaced a merge dialog instead of treating the run as an auto-discardable launch failure.

**Desired behavior:** Near-zero-distance launch failures on or immediately around the pad should be classified as pad failures / idle launch failures and discarded automatically, not promoted to merge/commit UI.

**Root cause / hypothesis:** The current failure-discard heuristic likely keys too heavily on generic recording existence or total motion while missing the specific "never really left the pad" case when there is some vertical or physics noise but no meaningful horizontal travel. This is probably adjacent to `IsTreeIdleOnPad`, launch-failure classification, and any max-distance / distance-from-launch thresholds used before showing merge UI.

**Fix:** Pad-failure / idle-on-pad classification now keeps the existing `duration < 10 s` / `distance < 30 m` 3D rule, but adds a recording-aware pad-drop override: if a recording's surface range from launch and absolute altitude displacement from launch both stay within the same 30 m pad-local threshold, it is still treated as pad-local even when raw 3D max distance was inflated by a topple or short fall. Tree merge auto-discard and destroyed-split fallback now both use the recording-aware checks. Added unit coverage for toppled pad drops, real vertical ascent, and downhill false positives.

**Status:** Fixed

---

## ~~325. Repeated F5/F9 during a branched recording can leave child sidecars time-discontinuous and break watch handoff~~

**Observed in:** `logs/2026-04-12_2159_f5-f9-watch-regression/` (`s9`). The `Crater Crawler` tree survived repeated quickload cycles structurally, but watching the root playback failed at the branch: when root `#0` completed, watch entered the hold timer and then exited instead of handing off to the continuation.

**Latest follow-up:** 2026-04-13 local smoke reports described the same symptom family in more user-facing terms: a rover/tree mission came back with the "first part missing" after save/load, and later playback still showed a section that should have been abandoned by the reload. The current snapshot-storage validation bundle (`logs/2026-04-13_0315_s13-snapshot-storage-check/`) verified the new compressed `_craft` sidecars were healthy, so this still points at branched quickload stitching rather than snapshot compression.

**Collected evidence:**

- The final saved tree is still present and structurally valid in `persistent.sfs`: root `07bd...`, same-vessel continuation `db41...`, and EVA child `af7d...` under branch point `d3ca...`.
- The watch failure itself is visible in `KSP.log`:
  - root playback completed while watched: `PlaybackCompleted index=0 ... watched=True`
  - `FindNextWatchTarget` saw the branch point, but no re-target happened
  - watch fell into `Watch hold timer set ... (watched #0)` and then `Watch hold expired ... exiting watch`
  - only several seconds later did the child ghosts actually spawn (`Ghost #2 "Jebediah Kerman" spawned`, then `Ghost #1 "Crater Crawler" spawned`)
- The saved metadata and sidecars disagree badly on timing:
  - root `07bd...` ends at the branch as expected (`explicitEndUT = 53.68`, last point `ut ~= 53.58`)
  - continuation `db41...` claims `explicitStartUT = 53.68`, but its `.prec` first point is `ut = 83.04`
  - EVA child `af7d...` claims `explicitStartUT = 53.68` and `explicitEndUT = 72.94`, but its `.prec` first point is `ut = 82.22` and it continues past `ut = 102.39`

**Impact:** This is more than a camera/watch issue. Repeated quickload during a live branched tree can leave child recordings with stale explicit UT metadata and large gaps between the branch boundary and their actual saved trajectory. Watch handoff then fails because the same-PID continuation child is not ghost-active when the root ends, and the shorter hold expires before the delayed child ghost appears.

**Root cause:** The merged fix narrowed the user-visible failure to watch timing, not missing branch data. After repeated quickloads, resumed child recordings could still remain present but not become ghost-active until the first real payload sample well after the parent branch boundary. The old watch-end policy only used a fixed `3-5 s` real-time hold, so the watched parent could expire before the same-PID continuation ever had a chance to spawn.

**Fix:** Watch handoff now asks the committed tree for the earliest pending continuation activation UT, preferring actual trajectory bounds over stale explicit metadata, and extends the watch-end hold using that future activation time plus the current warp rate. `WatchModeController` keeps recomputing and capping that pending hold until the continuation becomes eligible, then adds a short post-activation grace window before allowing the normal watch exit. Added regression coverage for same-PID and allowed fallback branches, actual-payload-start precedence, warp-aware hold sizing, and capped pending-hold expiry handling.

**Status:** ~~Fixed~~ (PR #238)

---

## ~~327. Main stage can appear frozen or off-trajectory after separation while debris paths continue~~

**Observed in:** 2026-04-13 local smoke after the snapshot-storage PR. User report: the main stage appeared to freeze in the air or move along the wrong trajectory position, while the debris recordings continued playing normally.

**Collected evidence so far:**

- The fresh `s13` validation bundle does **not** currently reproduce the failure. In `logs/2026-04-13_0315_s13-snapshot-storage-check/KSP.log`, watched same-vessel handoff stayed on the main PID continuation:
  - `Auto-follow on completion: #0 -> #10 (vessel=Kerbal X)`
  - `Auto-follow on completion: #10 -> #13 (vessel=Kerbal X)`
  - the corresponding watch-focus lines show descending altitude (`278 m` then `65 m`), not a frozen parent trajectory
- The older `s6` breakup bundle (`logs/2026-04-12_2055_main-stage-freeze-after-separation/`) did show a different watch bug where focus transferred to debris (`#0 -> #6 "GDLV3 Debris"`), but that was the now-fixed `#321` issue and is not proof of a current regression.
- The same `s13` bundle **does** show a save/load storage gap in the main-stage `.prec` payload. At `03:03:26`, `FlushRecorderIntoActiveTreeForSerialization` logged `103` live points for root recording `034b687...`, but the sidecar written immediately afterward still contained only `trackSections=1 / sparsePoints=35`; the next quickload rebuilt only that single section. The `a936e14...` and `8109e9e...` same-PID continuations showed the same pattern at later save boundaries.
- A saved local inspector script, `tools/inspect-recording-sidecar.ps1`, confirmed the persisted `Kerbal X` chain had large gaps between section-authoritative sparse segments even though live recording had advanced further in memory before save.

**Impact:** When it happened, the parent/main-stage recording could become visually unreliable after separation even while child debris recordings continued correctly. That made the playback-integrity failure more serious than a simple camera recovery or grouping issue.

**Root cause:** `ParsekFlight.FlushRecorderIntoActiveTreeForSerialization()` appended only already-closed `recorder.TrackSections` into the active-tree recording and then cleared them, but it did **not** checkpoint the recorder's currently-open `TrackSection` first. Mid-flight saves could therefore write a section-authoritative `.prec` sidecar that was missing the live in-progress sparse trajectory chunk, and quickload playback could freeze or drift across that gap while debris/child recordings continued normally.

**Fix:** PR `#242` now checkpoints the open active `TrackSection` during save-time serialization, immediately reopens a continuation section with the same environment/reference metadata, and adds regression coverage for absolute, relative, and orbital-checkpoint cases. The changelog entry for `0.8.1` already records this shipped fix.

**Status:** ~~Fixed~~ (PR #242)

---

## ~~328. Continuous EVA chains can still remain split across `atmo` -> `surface` boundaries~~

**Observed in:** `logs/2026-04-13_0315_s13-snapshot-storage-check/` (`s13`). User expectation was that a continuous EVA should merge if the motion is continuous, but the committed Bill Kerman chain remained split into atmospheric and surface segments.

**Collected evidence:**

- `KSP.log` shows the optimizer refusing to merge before the split:
  - `Optimization pass: no merge candidates found`
  - `Split recording 'Bill Kerman' at section 3 ... 'atmo' [127..157] + 'surface' [156..158]`
- `persistent.sfs` preserves two real Bill recordings in the same tree:
  - chain index `0`, `segmentPhase = atmo`, `pointCount = 49`
  - chain index `1`, `segmentPhase = surface`, `pointCount = 2`
- Pre-fix merge policy made this behavior explicit: `RecordingOptimizer.CanAutoMerge(...)` required both `SegmentPhase` and `SegmentBodyName` to match exactly before any automatic merge was allowed.

**Impact:** On atmospheric bodies, a continuous EVA touchdown / near-surface sequence can still appear as two adjacent recordings even after the old bogus-atmospheric-stub bug (`#326`) was fixed. This is not the same defect as `#326`: both resulting segments contain real data.

**Root cause:** The optimizer treated all phase changes as meaningful split boundaries and all cross-phase neighbors as merge-ineligible. That was too strict for atmospheric-body EVA continuity: a real kerbal recording could be split into `atmo` + `surface` segments during optimization, but the later merge pass could never heal that pair because the phase tags differed. Older already-split saves also carried slight overlap at the boundary, so naïve append-only repair would risk rebuilding non-monotonic flat points from the section payload.

**Fix:** PR `#248` now treats continuous same-body EVA `atmo <-> surface` boundaries as non-meaningful optimizer splits, allows already-split EVA neighbors to rejoin when EVA identity/body/timing match, trims overlapping loaded section frames before rebuilding flat points, and suppresses misleading mixed phase labels so the repaired recording no longer presents as only `atmo` or only `surface`. Regression coverage now pins:

- split suppression for continuous same-body EVA atmosphere/surface sections
- rejection of non-EVA, unknown-body, or gapped boundaries
- repair of already-split overlapping loaded EVA pairs through the section-authoritative rebuild path
- UI/body-label formatting for repaired mixed-phase EVA recordings

**Status:** ~~Fixed~~ (PR #248)

---

## ~~332. In-game FLIGHT test batches can poison the live session (transparent diagnostics window, broken camera/watch context, null active vessel after quickload canary)~~

**Observed in:** 2026-04-13 local FLIGHT batch runs after collecting `logs/2026-04-13_1635_332-flight-save-load-anchor-ui/` and the follow-up `logs/2026-04-13_1739_332-followup-unsolved/`. User report: after running the in-game FLIGHT tests, the diagnostics window became transparent, the camera/watch anchor no longer stayed on the expected anchor vehicle on the pad, and later the whole session was left broken.

**Collected evidence:**

- `KSP.log` repeatedly threw `ArgumentException: Getting control ... position in a group with only ... controls when doing repaint` from `Parsek.SettingsWindowUI.DrawSettingsWindow`, which explains the transparent/corrupt diagnostics/settings window.
- The failing code rendered the tooltip row conditionally: tooltip present emitted `GUILayout.Space(...)` + `GUILayout.Label(...)`, tooltip absent emitted a zero-height label only. That is the same IMGUI layout/repaint mismatch already avoided in `TestRunnerShortcut`.
- The FLIGHT round-trip tests (`SaveLoadTests.ScenarioRoundTripPreservesCount`, `SceneAndPatchTests.ScenarioRoundTripPreservesTreeStructure`, `SceneAndPatchTests.CrewReplacementsRoundTrip`) call live `ParsekScenario.OnSave`/`OnLoad` mid-batch, which re-subscribes scenario/runtime state without a real scene transition.
- `ParsekFlight` already had the right cleanup primitives (`ExitWatchMode`, `StopPlayback`, `DestroyAllTimelineGhosts`), but the destructive tests were not using them, so stale watch/ghost state could survive after the synthetic `OnLoad`.
- The first bundle also showed the quickload canary path timing out after a broken restore while still reporting pass, because `QuickloadResumeHelpers.WaitForFlightReady` and `WaitForActiveRecording` only logged warnings instead of failing the test.
- The follow-up bundle proved the live-session break was still happening after that detection fix: `RuntimeTests.BridgeSurvivesSceneTransition` triggered a real `TriggerQuickload`, stock `FlightDriver.Start()` immediately threw `NullReferenceException`, and the session stayed in `scene=FLIGHT, flightReady=False, activeVessel=null` while `FlightCamera`, autopilot UI, and related systems kept throwing.
- `parsek-test-results.txt` in the follow-up bundle was stale from an earlier run, which means the broken quickload path never reached clean result export; the reliable evidence was the fresh `KSP.log`.

**Root cause:** The final diagnosis was two different harness faults, only one of which the first patch fully addressed:

- the settings/diagnostics window had a real IMGUI control-count bug in its tooltip row
- the destructive live `OnSave`/`OnLoad` tests mutated the running FLIGHT session without being isolated or normalized afterward
- the real F5/F9 quickload scene-transition canaries were still being batched into normal FLIGHT runs, so once quickload wait helpers started failing honestly, the test harness could finally reveal that stock quickload itself was sometimes leaving the live session half-dead; hardening the wait helper did not stop the destructive scene transition from running

**Fix:** `SettingsWindowUI` now always renders a stable tooltip row using cached zero-height/wrapped styles. The destructive live `ParsekScenario.OnSave`/`OnLoad` round-trip tests were removed from the in-game suite entirely instead of trying to normalize the session after mutating it. Quickload wait helpers still fail with explicit scene/readiness context, and the destructive quickload canaries are now marked single-run-only and are excluded from `Run All` / `Run category` batches, with explicit skip reasons in the runner UI and exported results. They remain available from the row play button when intentionally run in a disposable session.

**Status:** ~~Fixed~~

---

## ~~335. Crash merge dialog can open from an empty tree with the wrong message/duration while deferred split resolution is still running~~

**Observed in:** 2026-04-13 crash follow-up playtests after `#320`. User report: three in-flight crash repros showed different merge-dialog text, and on one run the mission duration looked wrong.

**Collected evidence from:** `logs/2026-04-13_1933_crash-merge-message-diff/`

- `GDLV3` behaved correctly: the tree finalized in `FLIGHT`, all leaves were non-persistable, and the dialog opened with `recordings=6, spawnable=0`.
- The first `Jumping Flea` run was wrong. The recorder had just stopped with `58 points ... over 31.2s`, but `FinalizeTreeRecordings` immediately logged the only recording as having no playback data, `PruneZeroPointLeaves` removed it, and Parsek stashed a pending tree with `0 recordings` before opening the merge dialog with `recordings=0, spawnable=0`.
- Immediately after that empty-tree dialog, `DeferredJointBreakCheck` classified the same break as `WithinSegment` and resumed/committed the pending split recorder. That proves the dialog finalized before the deferred split classification finished.
- A later `Jumping Flea` run opened with `recordings=1, spawnable=0`, which explains why the player saw different dialog wording across otherwise similar crash flows.

**Impact:** The merge dialog could be built from an already-emptied tree, which changes the dialog body, can force the displayed duration to `0s`, and risks discarding the just-recorded trajectory that the pending split recorder was still trying to append back into the tree.

**Root cause:** The `#320` debris-only crash-order fix restored same-scene finalization, but `ShowPostDestructionTreeMergeDialog()` still ignored `pendingSplitInProgress`. In false-alarm joint-break cases, the pending split recorder needs one more deferred frame to classify the break and, if it was not a real vessel split, append its captured data back into the active tree via `FallbackCommitSplitRecorder -> TryAppendCapturedToTree`. Finalizing the tree before that happened left the active leaf temporarily empty, and the later zero-point prune removed it before the dialog was shown. The older prune log text also misleadingly said "debris" even though the helper now removes any empty leaf/placeholder, not only debris.

**Fix:** The post-destruction tree dialog now waits for deferred split resolution and any pending crash-coalescer breakup emission to finish before applying the terminal/debris-only finalization policy, then re-runs the normal guards and finalizes with the repaired tree state. Regression coverage now pins the new pending-crash wait policy branch, and the zero-point prune diagnostics were clarified to describe generic empty leaves/placeholders instead of only debris.

**Residual gap:** There is still no coroutine-level regression test that drives the full `OnVesselWillDestroy -> DeferredJointBreakCheck -> TickCrashCoalescer -> ShowPostDestructionTreeMergeDialog` path. Current coverage pins the extracted pending-crash policy/helper decisions, but the end-to-end scene/coroutine ordering still depends on in-game validation.

**Status:** ~~Fixed~~

---

## ~~329. Debris explosion FX can fire noticeably after visible ground contact~~

**Observed in:** 2026-04-13 local smoke after the snapshot-storage PR. User report: there was a visible delay between debris hitting the ground and the explosion effect.

**Collected evidence / current behavior:**

- Breakup branchpoints in the current `s13` run are saved with `coalesceWindow=0.500` in `KSP.log`.
- `CrashCoalescer.Tick(...)` intentionally waits for the coalescing window to expire before emitting the `BREAKUP` branchpoint.
- Ghost explosion FX are triggered later from the playback destruction path (`TriggerExplosionIfDestroyed(...)`), not from the original impact event itself.
- The `s13` logs show the expected explosion trigger lines for multiple debris ghosts, but do not yet pin exact impact-vs-FX deltas for the user-visible cases.

**Impact:** Even when breakup classification and playback are otherwise correct, crash moments can feel late or "soft" because the visual explosion is not aligned tightly enough with the apparent impact point.

**Root cause / hypothesis:** Timing is likely being influenced by two separate mechanisms:

- the deliberate `0.5 s` crash-coalescing window before the breakup branchpoint exists
- explosion FX being tied to ghost terminal/past-end destruction timing instead of the earliest impact-aligned sample

**Fix implemented:** Destroyed debris playback now resolves an earlier explosion UT from the earliest eligible recorded `PartEventType.Destroyed` and completes the debris ghost there instead of waiting for `EndUT`. Flight and KSC playback both use the same helper, while breakup coalescing/classification timing remains unchanged.

**Status:** Fixed in PR `#241`

---

## ~~331. Debris-only breakup false alarms can truncate the active main-stage sparse trajectory after resume~~

**Observed in:** `logs/2026-04-12_2055_main-stage-freeze-after-separation/` (`s6`). User report: during playback, the main/controller stage froze mid-flight or drifted onto the wrong path while debris recordings continued normally.

**Collected evidence:**

- The active recorder started one atmospheric active `TrackSection` at `UT=42.24` and closed it at the first breakup boundary (`UT=53.82`).
- The same session then logged repeated false-alarm resumes on the root recorder with growing preserved flat-point counts:
  - `Recording resumed after false alarm (43 points preserved)`
  - `Recording resumed after false alarm (82 points preserved)`
  - `Recording resumed after false alarm (84 points preserved)`
- But the logs never showed a new active `TrackSection started` after those resumes, and later sidecar writes for the root recording still serialized only `trackSections=1 ... sparsePoints=43`.
- The saved tree metadata and the saved sidecar disagreed badly on the root/main-stage recording:
  - `persistent.sfs` still recorded root `e1767d3aa36142c1a314092aad62a9bb` with `explicitEndUT = 77.58`, `pointCount = 95`, and `childBranchPointId = 6dabc8...`
  - `tools/inspect-recording-sidecar.ps1` showed the persisted `.prec` for that same recording had only one playable section ending at `UT=53.82` with `43` points
- Playback then treated the root as finished at that first saved sparse boundary and transferred watch to debris:
  - `PlaybackCompleted index=0 vessel=GDLV3 ... watched=True`
  - `TransferWatch re-target: ghost #6 "GDLV3 Debris"`

**Impact:** The active/main-stage recording could look frozen or off-trajectory after the first debris-only breakup even though the flat recording data kept advancing in memory and the debris children continued to replay correctly. Because playback is section-authoritative, the missing resumed sparse section was enough to end the watched root early and hand control to debris.

**Root cause:** `StopRecordingForChainBoundary()` correctly closed the active `TrackSection` before building `CaptureAtStop`, but when the split later resolved as a false alarm, `ResumeAfterFalseAlarm()` only restored the flat recorder state. It did **not** reopen a replacement `TrackSection`. After that, new samples kept appending to `Recording.Points` in memory, while section-authoritative sidecar writes continued to persist only the pre-resume sparse section set. That left playback truncated at the first breakup boundary.

**Fix:** PR `#251` now reopens a continuation `TrackSection` whenever a recorder resumes after a false alarm, but it no longer trusts only the last persisted section. Resume now prefers the recorder's latest closed-or-discarded section snapshot, so brief zero-frame relative/environment flickers do not reopen stale metadata, restores the relative anchor when needed, and rehydrates packed `OrbitalCheckpoint` on-rails state by reopening the live orbit segment before the next off-rails transition. Absolute/relative resumes still seed the boundary frame for continuity, and regression coverage now pins absolute, relative, discarded-section, and orbital-checkpoint false-alarm resumes.

**Status:** Fixed in PR `#251`

---

## ~~336. Debris ghosts can appear ahead of their real playback start, then slide into place~~

**Observed in:** `logs/2026-04-13_1959_debris-slide-still-broken-after-pr258/` after the first debris-positioning follow-up on PR `#258`. User report: debris ghosts still appeared in visibly wrong places on their first frame and only settled into the correct path afterward.

**Collected evidence:**

- The fresh `GhostAppearance` logs showed the ghost root was already snapped to the recording's first flat point on the first visible frame, but the ghost had become visible before any playable section was active:
  - `Ghost #1 "Kerbal X Debris" appearance#1 reason=playback ut=19.72 ... activeFrame=none ... recordingStart@20.26 ...`
  - `Ghost #7 "Kerbal X Debris" appearance#1 reason=playback ut=27.58 ... activeFrame=none ... recordingStart@28.12 ...`
- The matching debris sidecars confirmed the same timing gap:
  - recording `ceea24e2f9d04718ad9a63c9b95dd3c2` had `ExplicitStartUT = 19.72`, but its first playable `TrackSection` frame was `ut = 20.26`
  - recording `9c9ea1b5b5964fe48311835181ba7d3d` had `ExplicitStartUT = 27.58`, but its first playable frame was `ut = 28.12`
- The first structural part events landed only after the first visible frame (`Applied 2 part events for ghost #1` / `#2` after the appearance lines), so the replay briefly showed the full breakup snapshot state before the real payload timeline caught up.
- This ruled out the earlier CoM/root-frame hypothesis as the primary cause of the visible "spawn ahead, then slide back" behavior in the fresh repro. The root was not lagging the recording; visibility was starting too early.

**Impact:** Debris ghosts can become visible off branch metadata instead of their first real playable sample, making them appear tens of meters ahead of where the user expects before the steady-state playback path "catches up". Because breakup debris is snapshot-built and event-pruned, that early-visible window is especially obvious.

**Root cause:** breakup child recordings intentionally preserve the branch boundary as `ExplicitStartUT`, but ghost activation was still keyed off `Recording.StartUT` / raw loop start instead of the first playable payload sample. For these debris recordings, `ExplicitStartUT` came from the breakup branch point, while the first real `TrackSection.frames[0].ut` landed ~0.5 s later. The engine therefore treated the ghost as in-range too early, clamped positioning to the future first point, and made the full snapshot visible before any active section or first-frame part-event pruning existed.

**Fix implemented:** ghost activation now resolves from the first playable payload timestamp instead of the outer semantic branch start. `Recording.TryGetGhostActivationStartUT()` now prefers the first real frame/checkpoint payload, `GhostPlaybackEngine` gates normal playback and loop/debris-sync start off that activation UT, and `GhostAppearance` now logs `activationStart` / `activationLead` so future bundles can prove whether a ghost became visible before its real payload window. Added focused regression coverage for:

- recordings whose `ExplicitStartUT` is earlier than the first playable debris frame
- engine-side activation-start resolution preferring the real payload start over semantic branch timing

**Fresh validation:** `logs/2026-04-13_2136` no longer reproduces the bug. The breakup debris ghosts in that bundle all become visible on playable payload frames with zero activation lead:

- booster debris ghosts `#1`-`#8` all log `activationLead=0.00` together with `activeFrame=Absolute`, never the earlier `activeFrame=none` / "visible before payload" failure
- the stack-separated main-stage debris ghost `#9` (`recId=5df4e23c98ba475493fc9790f2f5584a`) appears at `ut=70.58`, `activationStart=70.58`, `activeFrame=Absolute`, and `recordingStart-root=(0.00,0.00,0.00)` even though the breakup branch itself opened earlier at `UT=70.04`

That confirms PR `#258` removed the original "ghost is visible before its first playable frame, then slides into place" failure mode in a fresh replay/save bundle.

**Status:** ~~Fixed~~ in PR `#258`, validated by `logs/2026-04-13_2136`

---

## ~~337. Stack-decoupled main-stage debris can still look spatially late relative to the exact separation event~~

**Observed in:** `logs/2026-04-13_2136` follow-up after PR `#258`, plus user report from the same recent save. The radial-booster debris looked fixed, but the large main-stage debris created by the circular / stack decoupler still did not look quite right at first appearance.

**Collected evidence:**

- the large stage breakup child was created immediately at the branch event: `pid=2483558814`, `recId=5df4e23c98ba475493fc9790f2f5584a`, breakup `UT=70.04`
- the first playable recorded payload for that child still arrives later:
  - `BgRecorder` starts the child's `TrackSection` at `UT=70.56`
  - the first saved trajectory point in `5df4e23c...prec.txt` is `ut = 70.58`
- the first visible ghost frame is now internally consistent, which means this is **not** the old `#336` bug:
  - `Ghost #9 "Kerbal X Debris" appearance#1 reason=playback ut=70.58 activationStart=70.58 activeFrame=Absolute ... recordingStart-root=(0.00,0.00,0.00)`
- the readable snapshot shows the stage had already moved materially by the time that first playable frame existed:
  - `5df4e23c..._ghost.craft.txt` records `distanceTraveled = 72.953649124686493` at `lastUT = 70.5600000000031`

**Impact:** After the PR `#258` fix, the main-stage debris no longer flashes early and slides into place, but it can still appear noticeably displaced from the exact split moment because playback has no visible payload before the first background-sampled frame. On a large, fast-moving stack-separated stage that remaining branch-to-first-sample gap is much easier to notice than on the smaller radial boosters.

**Root cause:** This was a different failure mode from `#336`. Ghost activation was already keyed to the first playable payload frame, but breakup/background children could still be seeded from the later deferred split-check frame rather than the exact decouple callback, which stamped the branch boundary and initial child pose a few hundredths of a second late. On fast booster and stack-separated debris, that was enough to make the first visible ghost frame appear slightly ahead of the true separation point. A narrower playback-side slip could also still happen if the ghost woke visible one render tick after its activation UT.

**Implemented fix:** breakup/background child recordings now capture exact split-time seed points during `onPartDeCoupleNewVesselComplete`, using the child root-part surface pose, and the deferred split path now resolves `branchUT` from those captured seed UTs (or the exact joint-break UT) instead of blindly using the later deferred-check time. If no exact split-time pose exists, the recorder falls back to the first honest post-split live sample rather than backdating it. Playback also keeps the fresh-first-frame guard from the follow-up investigation: if a ghost wakes visible a tick late, only that very first visible frame clamps back to `activationStartUT` within a narrow window.

**Fresh validation:** `logs/2026-04-14_1449_booster-separation-improved-check` confirms the remaining split-timing bias is gone. For the affected radial-booster pairs and later breakup children, the recorder now logs exact captured split seeds (`capturedSeed=T`, `pointSource=decouple-callback`) with matching `splitUT = branchUT = seedUT`, and the corresponding `GhostAppearance` lines show `ut = activationStart`, `activationLead=0.00`, and `recordingStart-root=(0.00,0.00,0.00)`. The same bundle also shows no fallback `pointSource=deferred-live-sample` / `destroyed-before-capture` cases and no non-zero activation lead for those first appearances. The remaining `seedLiveRootDist` values in the breakup diagnostics are expected because they compare the split seed against the live debris root about `0.5 s` later in the coalescer window, not against the split instant.

**Status:** Fixed in PR `#280`, validated by `logs/2026-04-14_1449_booster-separation-improved-check`

---

## 337. Same-tree EVA branches and parachute-bearing secondaries can still be distance-LOD culled while watching a related ghost

**Observed in:** `logs/2026-04-13_2136/` during the late `Kerbal X` watch session. User report: while watch mode was focused on a `Kerbal X` ghost, the same mission's EVA kerbals and their parachute-bearing secondary visuals were still being reduced/culled as if they were far away, even though the actual watch camera was already sitting near the ghost scene.

**Collected evidence:**

- `Player.log:87208` entered watch on ghost `#19 "Kerbal X"` with `camDist=50.0`.
- `Player.log:87217` immediately afterward still evaluated that watched ghost at `dist=18.6km ... activeVessel="#autoLOC_501232"`, which shows the runtime distance number was still coming from the active vessel context rather than the actual flight camera now parked near the ghost.
- `Player.log:87223` then logged `ReduceFidelity: disabled 36/49 renderers` during that watch session.
- The same save proves the EVA recordings are part of the same tree, not unrelated standalone recordings:
  - `persistent.sfs:1073-1097` recording `a1b840609b1c4c3da4db77fa427b6639` / `Bob Kerman` has `treeId = 62395ab8dcd5426ea2ada17811bacd2c`, `evaCrewName = Bob Kerman`, and `parentRecordingId = 9b3cc91fe9a6407baa49e9036eb0ac44`.
  - `persistent.sfs:1196-1220` recording `58303cf3f681418a8e1854e1659acd25` / `Bill Kerman` has the same tree and EVA metadata.
  - `persistent.sfs:1495-1524` shows both EVA children hanging off the same `Kerbal X` branch lineage.
- The current watch-protection code only protects same-tree debris. `GhostPlaybackLogic.IsWatchProtectedRecording(...)` returns early unless `current.IsDebris`.
- The current distance resolver still measures against `FlightGlobals.ActiveVessel.transform.position` (`ParsekFlight.ResolvePlaybackDistanceForEngine(...)`), not against the actual flight camera position.

**Impact:** During watch mode, same-tree EVA branches and other non-debris secondaries can still enter reduced-fidelity or hidden tiers based on pad/active-vessel distance rather than the camera the player is actually using. That means renderer disabling and part-event suppression can hit exactly the tiny visuals the player is looking at most closely: EVA kerbals, EVA packs/parachutes, and related child-branch scene elements.

**Root cause:** The decisive runtime bug was the distance reference, not the branch ancestry. Flight ghost LOD was still keyed off `FlightGlobals.ActiveVessel.transform.position`, so a watched scene that was only ~50 m from the real camera could still be treated like an 18 km-away visual-tier ghost if the active vessel stayed on the pad. Once that happens, unwatched same-tree EVA ghosts naturally fall into reduced-fidelity tiers even though they are physically near the watched scene.

The debris-only watch-protection lineage introduced for `#316` remains separate. It is still needed for far, late debris visibility retention, but it was not the primary cause of this EVA/parachute repro.

**Desired policy:** Distance LOD for flight ghosts should use the real active flight camera position in flight view. If the camera is sitting next to the watched ghost scene, nearby EVA/debris secondaries should naturally stay in the full-fidelity tier; only content that is actually far from the real camera should be reduced or culled.

**Fix:** `ParsekFlight.ResolvePlaybackDistanceForEngine(...)` now resolves playback LOD distance from the live flight camera position in flight view, falls back to the active vessel when no usable scene camera exists or when map view is active, and has focused regression coverage in `PlaybackDistancePolicyTests`.

**Status:** Fix implemented in PR `#260`; pending fresh runtime validation

---

## ~~338. Atmospheric-body EVA touchdown follow-ups can still misreport phase and split after packed landing~~

**Observed in:** `logs/2026-04-13_2136` while validating the latest EVA optimizer follow-up. The user reported two symptoms in the Recordings list:

- Bill and Bob showed purple `Kerbin` phase cells even though their EVA recordings had real landed/surface endings.
- Jeb still committed as two separate recordings at the end (`Kerbin atmo` and `Kerbin surface`) even though this was expected to be one continuous touchdown sequence.

**Collected evidence:**

- Bill and Bob were already a single mixed EVA recording each. Their `.prec` payloads contained continuous atmospheric -> surface sections on Kerbin, but the table was suppressing the mixed phase text down to body-only `Kerbin` while still styling the cell from raw `SegmentPhase`.
- Jeb's committed payload was genuinely discontinuous at touchdown:
  - the atmospheric payload ended at `UT 213.72`
  - the later surface payload did not begin until `UT 274.98`
- The logs showed the missing bridge at the loaded -> on-rails handoff:
  - parachute cut / atmospheric section close at touchdown
  - vessel packed into landed on-rails state
  - no playable surface boundary section persisted at the handoff UT
  - later promotion reopened as a new surface section
- Review follow-up found a second storage-side risk in the same area: ordinary loaded-flight -> packed-orbit fallback shapes still depended on top-level flat `Points` / `OrbitSegments`, but section-authoritative write eligibility was only checking for non-empty `TrackSections`, not whether those sections could exactly rebuild the flat payload.

**Impact:**

- Mixed atmospheric-body EVA rows could still look like exo/orbit recordings in the UI even when they were already a single repaired touchdown recording.
- Atmospheric-body EVA touchdowns could still split into separate `atmo` and `surface` recordings when the kerbal packed into a landed no-payload on-rails state before the surface boundary was persisted.
- The same loaded->on-rails area also risked silently truncating ordinary packed-orbit continuation on save/load if section-authoritative serialization dropped flat fallback orbit segments too aggressively.

**Root cause:** This turned out to be three related follow-up defects around the same boundary:

- `RecordingsTableUI` styled the phase cell from raw `rec.SegmentPhase` even when `RecordingStore.ShouldSuppressEvaBoundaryPhaseLabel(rec)` intentionally hid the mixed EVA `atmo`/`surface` wording and displayed only the body name.
- `BackgroundRecorder.OnBackgroundVesselGoOnRails` preserved only a flat boundary point when a loaded EVA packed into a landed no-payload on-rails state after an environment-class change. That left no playable surface bridge section at the landing UT, so the optimizer still saw a real gap and split the recording.
- `RecordingStore.ShouldWriteSectionAuthoritativeTrajectory` only checked whether `TrackSections` had payload, not whether they could losslessly reproduce the flat `Points` / `OrbitSegments` trajectory actually stored on the recording.

**Fix:** PR `#266` now:

- derives mixed-EVA phase styling from the displayed label semantics instead of the stale raw phase tag, so body-only mixed rows color as surface when their last playable section is surface
- persists a one-point absolute boundary `TrackSection` when a loaded vessel crosses an environment-class boundary and then packs into a no-payload on-rails state, so atmospheric-body EVA touchdown keeps a playable surface bridge at the handoff UT
- hardens section-authoritative sidecar writes so they only activate when `TrackSections` can exactly rebuild the recording's flat `Points` and `OrbitSegments`; otherwise text/binary `.prec` output stays on the conservative flat fallback path
- adds regression coverage for mixed-EVA phase styling, the no-payload touchdown boundary path, and text/binary sidecar round-trips for the recorder loaded->on-rails fallback shape

**Status:** Fixed in PR `#266`

---

## ~~339. Same-mission tree recordings can split between the mission root and partial vessel subgroups~~

**Observed in:** `logs/2026-04-13_2136/` during the late `Kerbal X` validation pass. User report: inside the `Kerbal X` main group, one visible `Kerbal X` subgroup correctly held two recordings, but four other `Kerbal X` recordings from the same mission still sat directly under the main group instead of joining that subgroup. The same pass also called out a missing feature: EVA crew recordings should be grouped under per-mission `Crew` subgroups instead of staying at the mission root.

**Collected evidence:**

- The tree/branch commit itself already knew about the EVA children. `KSP.log` logged the EVA branch creation for the same mission tree, and the saved recordings kept both `parentRecordingId` and `evaCrewName`.
- The persisted committed recordings in `persistent.sfs` still assigned those EVA rows directly to `recordingGroup = Kerbal X`; the only auto-created subgroup under that mission was `Kerbal X / Debris`.
- The visible split inside the Recordings table was not purely a save/grouping bug. In the affected mission, only the later two `Kerbal X` rows carried a `ChainId`, so the UI nested those two together while leaving the earlier same-vessel tree siblings flat at the mission root even though they belonged to the same vessel lineage.

**Impact:** The mission tree looked internally inconsistent in the UI. Same-vessel segments from one `Kerbal X` mission could be split between a partially nested subgroup and flat root-level rows, and EVA branches were harder to scan because crew recordings were mixed into the mission root instead of being collected under a dedicated per-mission crew branch.

**Root cause:** This turned out to be two separate defects:

- commit-time tree grouping only knew how to synthesize the debris subgroup; it never created `Mission / Crew` subgroups for EVA recordings, so committed EVA rows stayed assigned to the mission root group even though their tree metadata clearly identified them as EVA children
- the Recordings table only built nested vessel blocks from `ChainId`, so same-vessel tree recordings without a chain marker stayed flat while later chain-linked siblings nested, producing the partial `Kerbal X` subgroup seen in the repro

**Fix:** PR `#265` now:

- creates `Mission / Crew` subgroups for EVA tree recordings during commit
- re-homes orphaned same-tree EVA recordings into the correct crew subgroup only when their prior standalone group was auto-assigned by Parsek, avoiding accidental adoption of same-named manual groups
- persists and clears that auto-assigned standalone-group marker through save/load and manual group edits so the adoption rule stays explicit
- nests grouped mission rows in the Recordings table by `TreeId + VesselPersistentId` lineage instead of only `ChainId`, while preserving correct chain-member ordering inside the grouped block

**Status:** Fixed in PR `#265`

---

## ~~340. Manual W watch switches reset the camera to the default ghost-entry angle~~

**Observed in:** `logs/2026-04-13_2136/` during the Kerbal X / EVA watch-switch repro in flight mode. User report: switching between active `W` watch targets (vessel, EVA kerbal, later continuation) reset the ghost camera to a default under/overhead angle instead of keeping the previous relative watch angle around the new ghost.

**Collected evidence from the bundle:**

- Automatic watch handoff already preserved the current watch camera state:
  - `TransferWatch re-target: ghost #10 "Kerbal X" ... target='horizonProxy' ... camDist=75.0`
  - `TransferWatch re-target: ghost #13 "Kerbal X" ... target='horizonProxy' ... camDist=82.9`
- Manual row/group `W` retargets did not. Every manual switch logged a fresh `EnterWatchMode` on `cameraPivot` at the hard-coded entry distance:
  - `Switching watch from #16 to #14 "Bill Kerman"`
  - `EnterWatchMode: ghost #14 "Bill Kerman" target='cameraPivot' ... camDist=50.0`
  - `Watch camera auto-switched to HorizonLocked (alt=289m, body=Kerbin)`
  - the same pattern repeated for `#14 -> #11`, `#11 -> #16`, and later `#11 -> #19`
- The reset happened even when the source watch was already horizon-locked and already using a non-default distance, which ruled out button-eligibility or same-body checks as the primary cause.

**Impact:** Manual ghost-to-ghost retargeting visibly snapped the camera to the default entry framing before the new target's mode settled, making watch mode feel inconsistent with its own automatic continuation handoff. The problem was especially obvious when switching between vessel and EVA ghosts because their `cameraPivot` / `horizonProxy` rotations differ.

**Root cause:** `EnterWatchMode()` treated every manual `W` retarget as a brand-new watch entry. On switch it called `ExitWatchMode(skipCameraRestore: true)`, then started a fresh watch session that reset the mode state to `Free`, rebound to the raw `cameraPivot`, and forced `FlightCamera.SetDistance(50f)`. Unlike `TransferWatchToNextSegment()`, it did **not** preserve the current watch orbit, and it did not compensate pitch/heading when the old and new target transforms had different rotations.

**Fix:** Manual watch retarget now snapshots the live watch-camera orbit before tearing down the old target, preserves the original player-camera restore state across repeated ghost-to-ghost switches, resolves the new ghost's effective watch mode, and reapplies the previous orbit to the new target instead of using the default watch-entry framing. The transferred pitch/heading now run through the same target-rotation compensation logic used by watch auto-follow, so `cameraPivot`/`horizonProxy` changes preserve world-facing direction instead of snapping when the target basis changes. Added coverage for:

- watch-switch mode resolution with and without explicit `V`-toggle override
- transferred watch-angle compensation preserving world direction across target-rotation changes

**Status:** Fixed in PR `#267`

---

## ~~326. Landed EVA branch can be seeded as Atmospheric, leaving a bogus 1-point EVA fragment and bad optimizer splits~~

**Observed in:** `logs/2026-04-12_2242_quickload-branch-gaps-s10/` (`s10`). The latest retry did **not** reproduce the exact `s9` watch-handoff failure; main-vessel watch transfer worked on current head. But the run exposed a different regression in the EVA branch path.

**Collected evidence:**

- At the first EVA branch (`UT=70.24`), the new EVA vessel was background-seeded as atmospheric even though the kerbal was effectively on the surface:
  - `BgRecorder TrackSection started: env=Atmospheric ... pid=1614280122 at UT=70.24`
  - then almost immediately after vessel switch/promotion:
    - `TrackSection closed: env=Atmospheric ... frames=1 duration=0.04s pid=1614280122`
    - active EVA recorder started correctly as `SurfaceStationary`
- The final tree preserves that bad fragment as a non-leaf Jeb recording with only one point:
  - runtime timeline: `Recording #2: "Jebediah Kerman" UT 70-79, 1 pts, vessel`
  - saved metadata: recording `5fa6cf9e...`, `pointCount = 1`, `childBranchPointId = c206...`
- The second EVA branch repeated the same wrong initial environment:
  - `BgRecorder TrackSection started: env=Atmospheric ... pid=1487825712 at UT=92.08`
- Later, the optimizer split the second EVA recording into two `atmo` segments:
  - `Split recording 'Jebediah Kerman' ... 'atmo' [92..99] + 'atmo' [99..127]`
  - final runtime timeline: `#5 "Jebediah Kerman" UT 92-99, 13 pts, ghost, chain idx=0` and `#6 ... UT 99-127, 44 pts, vessel, chain idx=1`

**Impact:** A surface EVA can be recorded as if it briefly started in atmosphere, which pollutes the final tree with a tiny non-leaf stub and causes later optimizer output to classify EVA chain segments as `atmo`. That matches the user-visible symptom of "gaps" / badly stitched EVA recordings even though watch transfer on the main vessel path works.

**Root cause:** This turned out to be two related defects:

- when KSP left the source vessel active for one more frame, `CreateSplitBranch -> OnVesselBackgrounded -> InitializeLoadedState` could background-seed the new EVA child from a transient pre-stable loaded state, producing the 1-point `Atmospheric` stub
- later, atmospheric-body EVA classification still trusted transient `FLYING` / `SUB_ORBITAL` state too much for ground-adjacent and splashed kerbals, so near-surface or sea-level bobbing EVAs could still flip into `Atmospheric` and create bogus optimizer splits

**Fix:** EVA branch creation now queues a PID-keyed one-shot initial environment override when the background child is an EVA spawned from a landed / splashed / prelaunch source, and `BackgroundRecorder.InitializeLoadedState` consumes that override on the first real loaded init, including delayed go-off-rails initialization. `EnvironmentDetector`, `FlightRecorder`, and `BackgroundRecorder` now also keep atmospheric-body EVAs in surface segments when they are validly near terrain or bobbing at sea level on an ocean world. Added regression coverage for the branch override helper, pending override bookkeeping, and landed/splashed atmospheric-body EVA classification.

**Status:** ~~Fixed~~

---

## ~~313. Splashed EVA spawn-at-end can place the kerbal slightly underwater~~

**Observed in:** 0.8.0 (2026-04-12). In the Phase 11.5 playtest bundle, the parent splashed vessel (`#24 "Kerbal X"`) was clamped and spawned at sea level, but the EVA child (`#25 "Raydred Kerman"`) spawned at `alt=-0.2` with `terminal=Splashed`. Log sequence:

- `Clamped altitude for SPLASHED spawn #24 (Kerbal X): 0.4 -> 0`
- `Vessel spawn for #24 (Kerbal X) ... alt=0`
- `Snapshot position override for #25 (Raydred Kerman): alt -0.213434... -> -0.213434...`
- `EVA vessel spawn for #25 (Raydred Kerman) ... alt=-0.2 terminal=Splashed`

**Evidence bundle:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/`

**Primary log:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/KSP.log`

**Root cause:** `VesselSpawner.ResolveSpawnPosition` only clamps splashed terminal-state altitudes when `alt > 0`. That fixes the "recorded slightly above the surface" case, but not the observed EVA endpoint case where the final trajectory point is slightly below sea level. The EVA path uses the trajectory endpoint, `OverrideSnapshotPosition` writes the negative altitude into the snapshot unchanged, and `SpawnAtPosition`/terminal-state override does not enforce a sea-surface floor for splashed EVA spawns. The parent vessel takes the clamp; the EVA child does not.

**Test gap:** `SpawnSafetyNetTests.ResolveSpawnPosition_EvaLanded_FallsThroughToSplashedClamp` only covers `alt > 0 -> 0`. There is no regression test for a splashed EVA endpoint with `alt < 0`.

**Fix:** `VesselSpawner.ResolveSpawnPosition` now floors every non-zero `TerminalState.Splashed` altitude to sea level (`0.0`), not just the `alt > 0` case. That applies uniformly to EVA, breakup-continuous, and snapshot-based splashed spawns before `OverrideSnapshotPosition` / `SpawnAtPosition` runs. Added `ResolveSpawnPosition_EvaSplashed_NegativeEndpointAltitude_FloorsToSeaLevel` and updated breakup-continuous coverage so slightly negative terminal samples cannot place a spawned vessel underwater.

**Status:** ~~Fixed~~

---

## ~~312. Duplicate-blocker recovery destroys sibling recordings~~

**Observed in:** 0.8.0 (2026-04-12). Playtest placed 4 "Crater Crawler" ghosts on the runway within ~10 m of each other. Only 2 of 4 spawned -- each new spawn destroyed the previous one. Log sequence showed `Duplicate blocker detected for #6: recovering pid=... at 8m -- likely quicksave-loaded duplicate (#112)` firing for every pair.

**Root cause:** `VesselSpawner.ShouldRecoverBlockerVessel` matched by NAME only. The #112 fix was written for a specific scenario -- KSP's quicksave restores a Parsek-spawned vessel with the same PID while Parsek is also trying to spawn the same recording, creating two copies owned by the same recording. The correct response in that case is to destroy the restored duplicate. But a name-only check cannot distinguish that scenario from "four sibling recordings of the same vessel type landed near each other" -- in the latter, each new spawn found a sibling with the same vesselName and destroyed it, cascading across all four showcases.

**Fix:** `ShouldRecoverBlockerVessel` now also requires the blocker's PID to match THIS recording's own `Recording.SpawnedVesselPersistentId`. That's the only way to be certain the blocker is a duplicate of OURSELVES (the #112 scenario). If the blocker belongs to a sibling recording (different `SpawnedVesselPersistentId`), the check returns false and `CheckSpawnCollisions` falls through to walkback, which finds a clear sub-step along the trajectory and spawns in the correct place.

Signature change: `ShouldRecoverBlockerVessel(Recording rec, string blockerName, string recordingVesselName, uint blockerPid)`. The single call site in `CheckSpawnCollisions` now passes the recording and `blockerVessel.persistentId`. Unit tests in `DuplicateBlockerRecoveryTests` rewritten to cover the new PID-match logic: the #112 self-PID case (recover), the #312 sibling case (walkback), the first-spawn / `SpawnedVesselPersistentId == 0` case (walkback), and preserved null/empty/case-sensitive behavior from the original.

**Status:** Fixed

---

## ~~311. Walkback spawns mid-air on diagonally-descending trajectories~~

**Observed in:** 0.8.0 (2026-04-12). When `TryWalkbackForEndOfRecordingSpawn` steps backward through a trajectory to find a non-overlapping candidate, it historically used the raw trajectory altitude for the clear position. For a vessel diagonally descending onto a landing site, the earlier trajectory points were 10-30 m in the air — walking back found a lateral-clear spot but placed the vessel mid-air, and it fell. Related to #309 (old `ClampAltitudeForLanded` would down-clamp the walkback result aggressively, which *accidentally* masked this, but broke mesh-object positioning).

**Root cause:** The walkback callback used `body.TerrainAltitude(lat, lon)` as the surface reference, which is PQS-only and cannot see the real surface when it includes mesh objects (Island Airfield runway, launchpad, KSC buildings). Either the vessel was spawned underground (mesh-object case) or left mid-air (regular terrain with sparse PQS fallback).

**Fix:** After walkback returns a clear candidate, fire a top-down `Physics.Raycast` at the candidate `(lat, lon)` using the same layer mask as `Vessel.GetHeightFromSurface` (`LayerUtil.DefaultEquivalent | 0x8000 | 0x80000` — default + terrain + buildings). If the raycast hits AND the candidate altitude is more than `WalkbackSurfaceSnapThresholdMeters` (5 m) above the hit, snap the altitude down to `surface + WalkbackSurfaceClearanceMeters` (1 m). If the raycast misses (target area unloaded), fall back to the PQS safety floor via `ClampAltitudeForLanded`. The raycast catches real mesh-object surfaces that PQS terrain alone cannot represent. New helper: `VesselSpawner.TryFindSurfaceAltitudeViaRaycast(body, lat, lon, startAltAboveSurface)`. Uses `FlightGlobals.getAltitudeAtPos` to convert the hit point to ASL altitude.

**Status:** Fixed

---

## ~~310. Spawn collision detection used 2 m-cube blocker approximation~~

**Observed in:** 0.8.0 (2026-04-12). `SpawnCollisionDetector.CheckOverlapAgainstLoadedVessels` approximated every loaded vessel as a 2 m-cube at its `GetWorldPos3D()` position and did an AABB overlap check against the spawn bounds. Large vessels (stations, planes, carriers) were under-represented, letting walkback candidates pass inside their real geometry. Small rovers spawned near a docked station would be flagged as clear despite being inside the station's wings. Conversely, AABB false-positives were possible for sparse vessels with wide bounds.

**Root cause:** The original implementation (#127 or earlier) used `FallbackBoundsSize = 2f` as a conservative placeholder for the blocker vessel's bounds because there was no easy way to get the real bounds from a loaded `Vessel` object. The spawn side had access to the snapshot-computed AABB, but the blocker side used the 2 m cube. This was accurate enough for most rocket-to-rocket cases but broke down for large blockers and for mesh-object-adjacent spawns.

**Fix:** Rewrote `CheckOverlapAgainstLoadedVessels` to use `Physics.OverlapBox` against real part colliders. Each hit is resolved to its owning `Part` via `FlightGlobals.GetPartUpwardsCached` (the same helper KSP uses in `Vessel.CheckGroundCollision`). Non-part hits (terrain, building, runway mesh colliders) are skipped via the null-Part filter — the airfield runway never blocks a spawn, but real vessel parts do.

Layer mask: `LayerUtil.DefaultEquivalent` (0x820001 = bits 0, 17, 23) — the same mask KSP itself uses inside `Vessel.CheckGroundCollision` as the "part-bearing layers" identifier. An earlier revision of this fix used `(1<<0)|(1<<17)|(1<<19)` based on a misreading of Unity's layer map (assumed layer 19 was "PartTriggers"; per Principia's verified layer enum layer 19 is PhysicalObjects and PartTriggers is actually layer 21). Using KSP's own constant directly is both correct and future-proof against layer renumbering.

OverlapBox rotation: `Quaternion.FromToRotation(Vector3.up, upAxis)` where `upAxis` is the surface normal at the spawn position (derived from the enclosing celestial body). Aligns the local-space AABB with the body's local up direction so spawns on the far side of a curved body use a correctly-oriented query box.

Legacy filters (skip debris/EVA/flag, exempt parent vessel PID, skip active vessel) retained. The `BoundsOverlap` pure helper is kept for unit tests using injected predicates.

**Status:** Fixed

---

## ~~309. Rovers on Island Airfield spawn 19 m underground~~

**Observed in:** 0.8.0 (2026-04-12). Rover recordings captured at the Island Airfield runway spawned 19 m below the runway surface, inside the raw PQS terrain. Ghost playback of the same recordings also rendered below the runway. Both spawn and ghost placement were treating the airfield as if it didn't exist.

**Root cause:** Three call sites used `body.TerrainAltitude(lat, lon)` which is PQS-only — it queries `pqsController.GetSurfaceHeight()` and returns the raw planetary surface UNDER any placed mesh object (Island Airfield, launchpad, KSC facilities). For a vessel recorded ON the airfield at alt=133.9 m, `body.TerrainAltitude()` returned ~114.9 m (raw terrain under the runway). The three sites and their bugs:

1. **`ParsekFlight.CaptureTerminalPosition`** stored `rec.TerrainHeightAtEnd = body.TerrainAltitude(...)` — losing the 19 m airfield offset. Downstream consumers computed `recordedClearance = alt - TerrainHeightAtEnd` and got ~19 m, which was meaningless to them.
2. **`VesselSpawner.ClampAltitudeForLanded`** computed `target = terrainAlt + LandedClearanceMeters` and aggressively down-clamped any altitude above the target — specifically to fix #282 (Mk1-3 pod low-clearance clipping). For airfield rovers, this buried them 17 m below the runway.
3. **`VesselGhoster.ApplyTerrainCorrection`** called `TerrainCorrector.ComputeCorrectedAltitude(currentTerrain, recordedAlt, recordedTerrain)` which computed `corrected = currentTerrain + (recordedAlt - recordedTerrain)` — mathematically correct for terrain-relative correction, but `currentTerrain` was PQS-only, so the "corrected" altitude was placed relative to PQS, burying ghosts under the runway.

Decompiling `Vessel.CheckGroundCollision` and `Vessel.GetHeightFromSurface` confirmed the right API: KSP uses `Physics.Raycast` with layer mask `LayerUtil.DefaultEquivalent | 0x8000 | 0x80000` to find the true surface including building colliders. The `Vessel.terrainAltitude` property (documented as "height in meters of the nearest terrain **including buildings**") is reverse-computed from that raycast — available on loaded vessels.

**Fix:** Replaced PQS-only terrain queries with the correct surface-aware sources and changed the clamping philosophy from "force to terrain+2 m" to "trust the recorded altitude, only push up if below PQS safety floor":

1. **`ParsekFlight.CaptureTerminalPosition`** — captures `vessel.terrainAltitude` (raycast-derived, includes buildings) instead of `body.TerrainAltitude()`. Also logs the PQS vs. mesh offset for diagnostics.
2. **`VesselSpawner.ClampAltitudeForLanded`** — rewritten. No more down-clamp. Only clamps UP when `alt < (pqsTerrain + UndergroundSafetyFloorMeters)` (2 m), which only fires when PQS terrain has shifted up since recording (rare: KSP update / terrain mod). The #282 low-clearance case is still caught by this floor. KSP's own `Vessel.CheckGroundCollision` handles part-geometry clipping via `getLowestPoint()` on vessel load, so we don't need to front-run it. Renamed `LandedClearanceMeters` → `UndergroundSafetyFloorMeters` (semantic shift — it's a floor, not a target).
3. **`ParsekFlight.ApplyLandedGhostClearance`** — same philosophy. Trusts the recorded altitude; only pushes up if below `pqsTerrain + 0.5 m`. NaN-fallback legacy path unchanged.
4. **`VesselGhoster.ApplyTerrainCorrection`** — rewritten to apply the underground safety floor (0.5 m above PQS) instead of terrain-relative correction.
5. **Removed `TerrainCorrector.ComputeCorrectedAltitude`** — the terrain-relative correction formula was the core of the bug. Tests that encoded it also removed.

**Test rewrite:** `SpawnSafetyNetTests.ClampAltitudeForLanded_*` updated to match the new semantics. The #282 low-clearance case (176.5 m recorded, 175.6 m PQS terrain, 0.9 m clearance) is preserved as a regression guard — it now triggers the 2 m safety floor and pushes up to 177.6 m. Airfield case (133.9 m recorded, 114.9 m PQS terrain, 19 m mesh offset) passes through unchanged.

**Status:** Fixed

---

## ~~307. Rewind save lost on vessel switch during recording~~

**Observed in:** 0.8.0 (2026-04-12). When the player switches vessels during an active recording session (e.g. switching from a booster to a payload, or clicking a different vessel in the tracking station), the R (rewind) button never appears on recordings committed after the switch.

**Root cause:** `OnVesselSwitchComplete` has two flush paths for the outgoing recorder: (1) still-active recorder transitioned to background (line 1335), and (2) already-backgrounded recorder with pending flush (line 1361). Both paths called `FlushRecorderToTreeRecording` but did not call `CopyRewindSaveToRoot`. The rewind save filename from the outgoing recorder's `CaptureAtStop` was never propagated to the tree root recording. After the switch, `recorder` is set to null, and when the tree is eventually committed, `GetRewindRecording` resolves through the root -- which has a null `RewindSaveFileName`.

Related to T59 (EVA branch case), which fixed the same underlying problem in `CreateSplitBranch`. The vessel-switch paths were missed.

**Fix:** Added `CopyRewindSaveToRoot` calls in both vessel-switch flush paths in `OnVesselSwitchComplete` (lines 1337 and 1362), right after `FlushRecorderToTreeRecording` and before `recorder = null`. Uses `recorder.CaptureAtStop` as primary source with `recorder.RewindSaveFileName` as fallback, consistent with all other flush sites.

**Status:** Fixed

---

## ~~306. Ghost engine nozzles always glow red~~

**Observed in:** 0.8.0 (2026-04-12). Engine nozzle parts on ghost vessels permanently displayed a red/orange emissive glow, as if overheating. Stock KSP engines do not glow during normal operation -- the emissive channel is driven at runtime by the thermal system (`part.temperature / part.maxTemperature`).

**Root cause:** `BuildHeatMaterialStates` and `CollectReentryGlowMaterials` in `GhostVisualBuilder.cs` read `coldEmission` from the cloned prefab material via `materialClone.GetColor(emissiveProperty)`. Engine nozzle prefab materials have non-zero `_EmissiveColor` values baked in. Since ghost parts have no temperature simulation, this inherited emissive became the permanent baseline -- the nozzle always glowed at the prefab's emissive level.

**Fix:** Two changes: (1) Force `coldEmission = Color.black` and clear the emissive property on cloned materials immediately after cloning, in both `BuildHeatMaterialStates` (per-part heat) and `CollectReentryGlowMaterials` (whole-ghost reentry glow). (2) Decouple thermal animation from engine/RCS throttle -- removed `ApplyHeatState` calls from `EngineIgnited/Shutdown/Throttle` and `RCSActivated/Stopped` handlers in `GhostPlaybackLogic`. Thermal glow now driven purely by `ThermalAnimationCold/Medium/Hot` events from `ModuleAnimateHeat` polling. Thresholds adjusted: cool <40%, warm 40-80%, hot >80% (was <10%, 33%+, 66%+). Hysteresis gaps [0.35, 0.40) and [0.75, 0.80).

**Status:** Fixed

---

## ~~304. Raw #autoLOC keys in standalone recording vessel names~~

**Observed in:** Sandbox (2026-04-10). Rovers and stock vessels launched from runway/island airfield showed `#autoLOC_501182` instead of "Crater Crawler" in the Recordings Manager, timeline entries, and log messages.

**Root cause:** Three call sites read `FlightGlobals.ActiveVessel.vesselName` (which is a raw `#autoLOC_XXXX` key for stock vessels) and passed it to `RecordingStore.StashPending()` without calling `Recording.ResolveLocalizedName()`. `BuildCaptureRecording` (FlightRecorder.cs:4541) resolved the name correctly, but `StashPending` creates a new Recording object with whatever string is passed in, discarding the resolved name.

**Fix:** In `StashPendingOnSceneChange`, `ShowPostDestructionMergeDialog`, and `CommitFlight`, prefer `CaptureAtStop.VesselName` (already resolved by `BuildCaptureRecording`) with `ResolveLocalizedName` on the fallback path.

**Status:** Fixed

---

## ~~305. Standalone recordings lost on revert-to-launch~~

**Observed in:** Sandbox (2026-04-10). Rover recordings from runway/island airfield were silently discarded on revert-to-launch. The user had to manually commit via the Commit Flight button instead of getting the merge dialog automatically. Tree recordings (rockets with staging) survived reverts via the Limbo state mechanism, but standalone recordings had no equivalent.

**Root cause:** `DiscardStashedOnQuickload` (ParsekScenario.cs) unconditionally discards pending standalone recordings on any FLIGHT->FLIGHT transition with UT regression. Tree recordings survive because `PendingTreeState.Limbo` is explicitly preserved. Standalone recordings had no Limbo equivalent.

**Fix:** Added `PendingStandaloneState` enum (parallel to `PendingTreeState`) with `Finalized` and `Limbo` values. `StashPendingOnSceneChange` assigns `Limbo` when the destination scene is FLIGHT. `DiscardStashedOnQuickload` preserves Limbo standalones (mirroring tree Limbo preservation). A new Limbo dispatch block in `OnLoad` (parallel to the tree Limbo dispatch) decides:
- If `ScheduleActiveStandaloneRestoreOnFlightReady` is set (F5/F9 mid-recording): discard the stale Limbo standalone, let the restore resume from F5 data.
- If no restore is scheduled (revert-to-launch): finalize the Limbo standalone for the merge dialog.

Design aligned with bug #271 (standalone/tree unification) — the two modes now share symmetric state tracking.

**Status:** Fixed

---

## ~~298b. FlightRecorder missing allEngineKeys -- #298 dead engine sentinels only work for BackgroundRecorder~~

`PartStateSeeder.EmitEngineSeedEvents` emits `EngineShutdown` sentinels for dead engines
using `sets.allEngineKeys` (#298). `BackgroundRecorder.BuildPartTrackingSetsFromState` sets
`allEngineKeys = state.allEngineKeys`, but `FlightRecorder.BuildCurrentTrackingSets` omits it
entirely. FlightRecorder has no `allEngineKeys` field, so `SeedEngines` populates it on a
temporary `PartTrackingSets` that is immediately discarded. The subsequent `EmitSeedEvents`
call creates a new set with an empty `allEngineKeys`, emitting zero sentinels.

**Fix:** Add `private HashSet<ulong> allEngineKeys` to FlightRecorder and include it in
`BuildCurrentTrackingSets`. `SeedEngines` will then populate FlightRecorder's own set
(same reference pattern as `activeEngineKeys`), and the follow-up `EmitSeedEvents` will
see the populated set and emit the sentinels.

**Status:** ~~Fixed~~

---

## ~~297. FallbackCommitSplitRecorder orphans tree continuation data as standalone recording~~

When a vessel is destroyed during tree recording and the split recorder can't resume,
`FallbackCommitSplitRecorder` stashes captured data as a standalone recording via
`RecordingStore.StashPending`, ignoring the active tree. The tree root is truncated
(missing post-breakup trajectory) and the continuation becomes an ungrouped standalone.

Real-world repro: Kerbal X standalone recording promoted to tree at first breakup (root
gets 47 points). More breakups add debris children, root continues recording (83 more
points in buffer). Vessel crashes, joint break triggers `DeferredJointBreakCheck`,
classified as WithinSegment, `ResumeSplitRecorder` detects vessel dead, calls
`FallbackCommitSplitRecorder` which stashes 83-point continuation as standalone.

**Fix:** Extracted `TryAppendCapturedToTree` -- when `activeTree != null`, appends captured
data to the active tree recording (fallback to root) via `AppendCapturedDataToRecording`,
sets terminal state and metadata. Standalone path only runs when not in tree mode.

**Status:** ~~Fixed~~

---

## ~~296. EVA kerbal who planted flag did not appear after spawn~~

Log shows KSCSpawn successfully spawned the EVA kerbal (Bill Kerman, pid=484546861), but the user reports not seeing it. Originally attributed to post-spawn physics destruction.

**Likely duplicate of T57.** The scenario is identical: surface EVA near the launchpad, parent vessel already spawned, kerbal never materialized. The "successfully spawned" log was misleading -- `VesselSpawned = true` is set for abandoned spawns too (prevents vessel-gone reset). T57's fix (exempt parent vessel from EVA collision checks) addresses the root cause. Verify in next playtest.

**Status:** ~~Closed as likely duplicate of T57.~~

---

## ~~290. F5/F9 quicksave/quickload interaction with recordings~~

Broad investigation of F5/F9 + recording system interactions. Bug #292 (F9 after merge drops recordings) was the original smoking gun.

**Bug found:** `CleanOrphanFiles` (cold-start only) built its known-ID set from committed recordings/trees but not the pending tree. On cold-start resume, `TryRestoreActiveTreeNode` stashes the active tree into `pendingTree` before `CleanOrphanFiles` runs, so branch recordings (debris, EVA) were invisible to the orphan scanner and their sidecar files deleted. Data survived the first session (in memory) but non-active branches had `FilesDirty=false`, so sidecars were not rewritten. Second cold start degraded them to 0 points.

**Fix:** Extracted `BuildKnownRecordingIds()` (internal static) from `CleanOrphanFiles`; now includes pending tree recording IDs.

**Other scenarios verified safe:** auto-merge + F9 (no optimizer, no corruption), CommitTreeFlight + F9 (same), rewind save staleness (frozen by design), quicksave auto-refresh (user-initiated only to avoid re-entering OnSave).

**Status:** ~~Fixed~~

---

## ~~290b. RestoreActiveTreeFromPending fails on #autoLOC vessel names~~

**Observed in:** Sandbox (2026-04-11). After F9 quickload, the restore coroutine waited 3s for vessel "Kerbal X" to become active, but KSP's `vesselName` was still the raw `#autoLOC_501232` key (localization not yet resolved). The vessel WAS loaded (correct `persistentId` logged), but the name-only match failed. Tree stayed in Limbo, no recording started, and the orphaned tree eventually triggered a spurious merge dialog.

**Root cause:** `RestoreActiveTreeFromPending` matched only by `vesselName`, which is unreliable immediately after quickload because KSP defers localization resolution.

**Fix:** Added PID-based matching (`VesselPersistentId` vs `Vessel.persistentId`) as the primary check, with name match as secondary fallback. Same change applied to the EVA parent chain walk. PID is locale-proof and available immediately.

**Status:** ~~Fixed~~

---

## ~~290d. MaxDistanceFromLaunch never computed for tree recordings~~

**Observed in:** Sandbox (2026-04-11). All tree recordings had `MaxDistanceFromLaunch = 0.0` despite having real trajectory data (125+ points). `IsTreeIdleOnPad` returned true for all 7 recordings, discarding the entire flight on scene exit to Space Center.

**Root cause:** `MaxDistanceFromLaunch` is computed in `VesselSpawner.ComputeMaxDistance`, which is called from `BuildCaptureRecording`. Tree recordings reach finalization via `ForceStop` (scene exit), which intentionally skips `BuildCaptureRecording` because vessel state may be unreliable during scene transitions. BgRecorder never computes it either. Result: every tree recording has the default `0.0`.

**Fix (three parts):**
1. `FinalizeIndividualRecording` now calls `VesselSpawner.BackfillMaxDistance(rec)` for recordings with `MaxDistanceFromLaunch <= 0.0` and `Points.Count >= 2`. Extracted `ComputeMaxDistanceCore` from the existing private `ComputeMaxDistance` method.
2. `IsTreeIdleOnPad` now requires at least one recording with `Points.Count > 0` before classifying a tree as idle (guards against 0-point recordings from epoch mismatch).
3. `TryRestoreActiveTreeNode` now skips .sfs replacement when the pending tree is already `Finalized` -- the in-memory tree has post-finalize data (MaxDistanceFromLaunch, terminal states) that the .sfs version lacks because KSP's OnSave runs BEFORE `FinalizeTreeOnSceneChange`.

**Status:** ~~Fixed~~

---

## ~~290e. TerrainHeightAtEnd not captured for unloaded vessels at scene exit~~

**Observed in:** Sandbox (2026-04-11). Rover ghosts appeared under the runway surface and spawn-at-end placed the vessel below the runway, causing it to clip through and explode. The rover was a background vessel (player was controlling EVA kerbal) when the scene exited.

**Root cause:** `CaptureTerminalPosition` (which captures `TerrainHeightAtEnd`) is only called when `finalizeVessel != null`. Unloaded background vessels are not findable at scene exit, so `TerrainHeightAtEnd` stays NaN. The spawn safety net falls back to PQS terrain height (~64.8m), which is below the runway structure surface (~70m).

**Fix:** In the `isSceneExit` fallback path of `FinalizeIndividualRecording`, capture terrain height from the last trajectory point's lat/lon coordinates via `body.TerrainAltitude()` for Landed/Splashed recordings.

**Status:** ~~Fixed~~

---

## ~~290f. SegmentPhase classified LANDED vessels as "atmo"~~

**Observed in:** Sandbox (2026-04-11). Rover recordings on the runway showed "Kerbin atmo" instead of "Kerbin surface" in the Phase column.

**Root cause:** Three code paths (`TagSegmentPhaseIfMissing`, `StopRecording`, `ChainSegmentManager`) classified SegmentPhase by altitude alone (`altitude < atmosphereDepth ? "atmo" : "exo"`), ignoring the vessel's situation. A landed rover on Kerbin is technically within the atmosphere by altitude, but its situation is LANDED.

**Fix:** All three sites now check `Vessel.Situations.LANDED/SPLASHED/PRELAUNCH` first and assign "surface" phase. Also added `phaseStyleSurface` (orange) to the UI, changed atmo to blue, exo to light purple.

**Status:** ~~Fixed~~

---

## ~~290g. LaunchSiteName missing from tree recordings~~

**Observed in:** Sandbox (2026-04-11). Site column empty for most recordings in the Recordings window.

**Root cause:** `FlushRecorderIntoActiveTreeForSerialization` (called during OnSave) did not copy `LaunchSiteName` or start location fields from the recorder to the tree recording. Only `FlushRecorderToTreeRecording` (normal stop path via `BuildCaptureRecording`) copied them.

**Fix:** Added LaunchSiteName, StartBodyName, StartBiome, StartSituation copy to `FlushRecorderIntoActiveTreeForSerialization`.

**Status:** ~~Fixed~~

---

## ~~290c. F5/F9 epoch mismatch — BgRecorder and force-writes advance sidecar epoch past .sfs~~

**Observed in:** Sandbox (2026-04-11). F5 quicksave, fly with staging (debris created), F9 quickload. All background recordings lost trajectory data (0 points). Same mismatch on scene exit: Bob Kerman EVA recording lost to epoch drift, then auto-discarded by idle-on-pad false positive.

**Root cause:** `BackgroundRecorder.PersistFinalizedRecording` and the scene-exit force-write loop both call `SaveRecordingFiles`, which unconditionally increments `SidecarEpoch`. Between an F5 (which writes .sfs with epoch N) and F9, BgRecorder can call `PersistFinalizedRecording` multiple times (once per `OnBackgroundVesselWillDestroy`, once per `EndDebrisRecording`), advancing the .prec epoch to N+2 or beyond. On quickload, .sfs expects epoch N but .prec has epoch N+2, triggering `ShouldSkipStaleSidecar` and silently dropping all trajectory data.

**Fix:** Added `bool incrementEpoch = true` parameter to `SaveRecordingFiles`. Out-of-band callers (BgRecorder `PersistFinalizedRecording`, scene-exit force-write loop) pass `incrementEpoch: false` so the .prec keeps the epoch from the last OnSave. OnSave and commit paths use the default `true`, preserving original #270 cross-scene staleness detection.

**Status:** ~~Fixed~~

---

## ~~286. Full-tree crash leaves nothing to continue with — `CanPersistVessel` blocks all `Destroyed` leaves~~

**Fix:** Option (c) implemented. When `spawnCount == 0 && decisions.Count > 0`, the merge dialog body shows: "No flight branches produced a vessel that can continue flying. The recordings will play back as ghosts, but no vessel will be placed." Screen message changed from generic "Merged to timeline!" to "Merged to timeline (no surviving vessels)". Wording is terminal-state-agnostic (covers Destroyed, Recovered, Docked, Boarded). The `CanPersistVessel` blocking logic is unchanged -- the player just needed to know why nothing spawned.

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## ~~189b. Ghost escape orbit line stops short of Kerbin SOI edge~~

For hyperbolic escape orbits, KSP's `OrbitRendererBase.UpdateSpline` draws the geometric hyperbola from `-acos(-1/e)` to `+acos(-1/e)` using circular trig (cos/sin), which clips at a finite distance (~12,000 km for e=1.342). The active vessel shows the full escape trajectory to the SOI boundary because it uses `PatchedConicSolver` + `PatchRendering`. Ghost ProtoVessels don't get a `PatchedConicSolver`.

**Options:**
1. Draw a custom LineRenderer through the recording's trajectory points (accurate but significant work)
2. Extend the orbit line beyond the hyperbola asymptote with a straight-line segment to the SOI exit point
3. Give the ghost a `PatchedConicSolver` (complex, may conflict with KSP internals)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability) — cosmetic, same tier as T25 fairing truss

**Status:** ~~Closed as expected behavior.~~

---

## ~~220. PopulateCrewEndStates called repeatedly for 0-point intermediate recordings~~

Intermediate tree recordings with 0 trajectory points but non-null `VesselSnapshot` could re-enter `PopulateCrewEndStates` on every recalculation walk (36 times in a typical session), even when the recording was just an empty intermediate/probe node and could never yield crew data.

**Root cause:** Kerbal end-state inference only distinguished `CrewEndStates != null` from `CrewEndStates == null`. A recording that had already been conclusively evaluated as "no crew" stayed null forever, so later ledger/recalculation passes treated it like "not processed yet" and reran the inference every time. Missing start-snapshot cases also were not separated cleanly from legitimate no-crew recordings.

**Fix:** Recordings now persist a separate `CrewEndStatesResolved` flag. `PopulateCrewEndStates` marks crewless recordings resolved when a valid start-crew source proves there is no crew, leaves genuinely missing-start-snapshot cases unresolved, and the ledger/tree-load paths skip repeat inference once that resolved-no-crew state is known. Added regression coverage for resolved-no-crew behavior, missing-start-snapshot behavior, and save/load persistence of `crewEndStatesResolved`.

**Status:** ~~Fixed~~ (PR #239)

---

## ~~241. Ghost fuel tanks have wrong color variant~~

Parts whose base/default variant is implicit (not a VARIANT node) showed the wrong color. KSP stores the base variant's display name (e.g., "Basic") as `moduleVariantName`, but no VARIANT node has that name — `TryFindSelectedVariantNode` fell through to `variantNodes[0]` (e.g., Orange) instead of keeping the prefab default. Fix: `MatchVariantNode` returns false when the snapshot names a variant with no matching VARIANT node, so callers skip variant rule application and the prefab's base materials are preserved.

**Fix:** PR #198

---

## ~~242. Ghost engine PREFAB_PARTICLE FX fires perpendicular to thrust axis~~

On Mammoth, Twin Boar, RAPIER, Twitch, Ant, Spider, Puff, and other engines, ghost PREFAB_PARTICLE FX (smoke trails, small engine flames) fired sideways instead of along the thrust axis.

**Root cause:** Ghost model parent transforms (thrustTransform, smokePoint, FXTransform) have their local +Y axis pointing sideways, not along the thrust axis. PREFAB_PARTICLE FX emits along local +Y (Unity ParticleSystem default cone axis). Without rotation correction, particles fire perpendicular to the nozzle.

In KSP's live game, the part model hierarchy is oriented within the vessel so that transforms end up with +Y along thrust. In our ghost, the cloned prefab model keeps the raw model-space orientation where +Y is typically sideways.

**Investigation:** Decompiled `PrefabParticleFX.OnInitialize` — uses `NestToParent` (resets localRotation to identity) then sets `Quaternion.AngleAxis(localRotation.w, localRotation)` from config. Decompiled `ModelMultiParticleFX.OnInitialize` — same pattern with `Quaternion.Euler(localRotation)`. Decompiled `NestToParent` — calls `SetParent(parent)` with worldPositionStays=true, then explicitly resets localPosition=zero and localRotation=identity.

Added `LogFxDirection` diagnostic that logs `emitWorld` (local +Y in world space) and `angleFromDown` for every FX instance. This revealed the pattern: entries with explicit -90 X rotation (from config or fallback) had correct angleFromDown=180, while entries with identity had wrong angleFromDown=90.

Exception: SSME (Vector) has `thrustTransformYup` where +Y already points along thrust — applying -90 X there breaks it.

**Fix:** In `ProcessEnginePrefabFxEntries`, when config has no `localRotation`, check `ghostFxParent.up.y` at process time. If abs(y) > 0.5, the parent +Y already aligns with the thrust axis — use identity. Otherwise apply `Quaternion.Euler(-90, 0, 0)` to rotate emission onto the thrust axis. Existing entries with explicit config `localRotation` (jets with `1,0,0,-90`) are unaffected.

**Remaining:** MODEL_MULTI_PARTICLE entries without config localRotation (Mammoth `ks25_Exhaust`, Twin Boar `ks1_Exhaust`, SSME `hydroLOXFlame`, ion engine `IonPlume`) still show angleFromDown=90 in the diagnostic log. These use `KSPParticleEmitter` (legacy particle system) rather than Unity `ParticleSystem`, so their emission axis may differ from +Y — visually they appear correct despite the diagnostic reading. May need separate investigation if visual issues are reported.

**Status:** ~~Fixed~~ (PREFAB_PARTICLE path)

### 242b. Multi-mode engine ghosts show both modes simultaneously

RAPIER and Panther (and any other `ModuleEnginesFX` multi-mode engines) rendered FX for all engine modes at once instead of only the active mode. Ghost would show jet exhaust and rocket exhaust simultaneously.

**Root cause:** `TryBuildEngineFX` scanned ALL EFFECTS groups for every engine module on a part. Multi-mode engines like RAPIER have separate EFFECTS groups per mode (e.g. `running_closed` for rocket, `running_open` for jet). Without filtering, each `EngineGhostInfo` contained particles from all modes.

**Fix:** Added `GetModuleEffectGroupNames(ModuleEngines)` which downcasts to `ModuleEnginesFX` and reads `runningEffectName`, `powerEffectName`, `spoolEffectName`, `directThrottleEffectName`. These names are used to filter EFFECTS groups so each engine module only scans its own referenced groups. Base `ModuleEngines` (not FX) returns empty set and falls through to scanning all groups (backward compat). Removed the old RAPIER `midx>0` skip that suppressed the second engine module entirely. RAPIER white flame fallback guarded by `modelFxEntries.Count == 0` to avoid doubling with per-module model exhaust. Added RAPIER mode-switch showcase recording with per-moduleIndex events demonstrating jet-to-rocket-to-jet switching.

**Status:** ~~Fixed~~ (PR #220)

---

## ~~242c. Ghost variant geometry not toggled -- extra FX on multi-variant parts~~

Parts with `ModulePartVariants` that toggle geometry via GAMEOBJECTS (e.g. Poodle DoubleBell/SingleBell) show all variant geometry on the ghost, including inactive variants. The Poodle ghost has 3 thrustTransforms (2 from DoubleBell + 1 from SingleBell) instead of 2, producing 3 flames instead of 2.

**Root cause:** Ghost model mesh filtering already excluded inactive variant renderers (#241), but the engine FX builder (`EngineFxBuilder`) and RCS FX builder (`TryBuildRcsFX`) discovered transforms from the raw prefab without variant awareness. `engine.thrustTransforms` (populated by KSP from ALL matching transforms regardless of variant state) was the primary source for the Poodle bug. `FindTransformsRecursive` calls in model/prefab FX methods were secondary sources. `MirrorTransformChain` then created ghost transforms for inactive-variant objects.

**Fix:** Threaded `selectedVariantGameObjects` into `TryBuildEngineFX` and `TryBuildRcsFX`. Extracted `IsAncestorChainEnabledByVariantRule` (pure, testable) from `IsRendererEnabledByVariantRule`. Filter applied at 5 points: `FindNamedTransformsCached`, `ProcessEngineLegacyFx` (engine.thrustTransforms), `ProcessEngineModelFxEntries`, `ProcessEnginePrefabFxEntries`, and `TryBuildRcsFX` (FindTransformsRecursive). Affects 9 engine parts and 3 RCS parts with GAMEOBJECTS variant rules.

**Status:** ~~Fixed~~

---

## ~~270. Sidecar file (.prec) version staleness across save points~~

Latent pre-existing architectural limitation of the v3 external sidecar format: sidecar files (`saves/<save>/Parsek/Recordings/*.prec`) are shared across ALL save points for a given save slot. If the player quicksaves in flight at T2, exits to TS at T3 (which rewrites the sidecars with T3 data), then quickloads the T2 save, the .sfs loads the T2 active tree metadata but `LoadRecordingFiles` hydrates from T3 sidecars on disk — a mismatch.

Not introduced by PR #160, but PR #160's quickload-resume path makes it more reachable (previously, quickloading between scene changes always finalized the tree, so the tree was effectively "new" each time).

**Fix:** Added `SidecarEpoch` counter to Recording, incremented on every `SaveRecordingFiles` write. The epoch is stamped into both the .prec file and the .sfs metadata. On load, `LoadRecordingFiles` validates that the .prec epoch matches the .sfs epoch. On mismatch (stale sidecar from a later save), trajectory load is skipped and a warning is logged. Committed recordings are unaffected (FilesDirty stays false after first write, so .prec is never overwritten and epochs always match). Backward compatible: old saves without epoch (SidecarEpoch=0) skip validation entirely.

**Status:** ~~Fixed~~

---

## ~~271. Investigate unifying standalone and tree recorder modes~~

**Fix:** Always-tree mode -- `ParsekFlight.StartRecording` creates a single-node `RecordingTree` for every recording. Eliminated `StashPendingOnSceneChange`, `PromoteToTreeForBreakup`, `RestoreActiveStandaloneFromPending`, `ShowPostDestructionMergeDialog`, `CommitFlight`, `PendingStandaloneState`, `VesselSwitchDecision.Stop`. Chain system (`StashPending`/`CommitPending`) and standalone merge dialog retained for backward compat -- to be unified when chain system is removed.

**Status:** ~~Fixed~~

---

# TODO

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1`
section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary
`v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and
lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot
sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror
path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen
without unpacking the authoritative files first.
Remaining high-value work should stay measurement-gated and follow
`docs/dev/plans/phase-11-5-recording-storage-optimization.md`:

- fresh live-corpus rebaseline against current `v3` sidecars
- snapshot-side work should keep focusing on `_ghost.craft` / `_vessel.craft` bytes, where the remaining storage bulk still lives after the first lossless compression slice
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if the post-compression rebaseline still shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the
  missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay
  covered by sidecar/load diagnostics
- add an end-to-end active-tree salvage test that proves a later `OnSave` rewrites healed sidecars
  and clears `FilesDirty`
- add a mixed-case salvage test where several recordings fail hydration but only a subset can be
  restored from the matching pending tree

**Priority:** Current Phase 11.5 follow-on work

---

### ~~T6. LOD culling for distant ghost meshes~~

Implemented in `0.8.1` as the shipped Flight ghost LOD policy:

- `0 - 2300 m`: full fidelity
- `2300 m - 50000 m`: reduced mesh / no part events / muted expensive FX
- `50000 m - 120000 m`: hidden mesh, logical playback retained
- watched ghosts inside cutoff bypass the distance degradation path
- diagnostics now report live `full / reduced / hidden / watched override` counts

**Status:** Fixed in `0.8.1`

### ~~T7. Ghost mesh unloading outside active time range~~

The Phase 11.5 LOD follow-up landed: ghosts that enter the hidden tier now unload built mesh/resources while keeping their logical playback shell alive, then rebuild on demand when they return to a visible tier.

**Status:** Fixed in `0.8.1`

### ~~T8. Particle system pooling for engine/RCS FX~~

Phase 11.5 investigation completed as a measurement-first pass without touching FX behavior.

What shipped:

- playback diagnostics now capture live engine/RCS ghost counts, module counts, particle-system counts, and last-frame ghost spawn/destroy timings
- the showcase injection workflow can run the focused diagnostics/observability slice before mutating the save
- the in-game test runner layout/order was cleaned up so diagnostics and FX-heavy categories are easier to run repeatedly during playtests

Outcome:

- the injected showcase validation passed `Diagnostics` and `PartEventFX`
- exported logs did not show a clear FX-specific correctness or performance regression that justifies touching the current engine/RCS FX lifecycle
- the only notable failure in that bundle was `GhostCountReasonable` (`246` ghosts), which points at overall ghost population pressure rather than FX pooling specifically

Conclusion: no pooling or FX lifecycle optimization is scheduled now. Re-open only if future profiling shows playback spikes, spawn/destroy spikes, or GC pressure that clearly correlates with FX-heavy ghost churn.

**Status:** ~~Closed for Phase 11.5 -- measurement shipped, optimization deferred unless future evidence justifies it~~

---

## TODO — Ghost Visuals

### T65. Ghost audio suppression still logs disabled-source warnings on first appearance

The plume regression is fixed, but the fresh smoke bundle initially still showed a follow-up ghost-audio warning. In `logs/2026-04-14_1459_ghost-engine-fix-smoke/KSP.log`, the main `Kerbal X` ghost correctly starts engine state before its first visible Flight appearance (`GhostAudio` start lines at `15228`, `15230`, `15232`; appearance at `15235`; watch mode only later at `15244`), yet KSP logged `Can not play a disabled audio source` immediately beforehand at `15227`, `15229`, and `15231`. The same pattern repeated earlier in the session at `11904-11908` and `15135-15139`.

Root cause: the warnings did **not** come from capped-away audio sources or from the extra engines that were suppressed by the 4-source cap. The started PIDs in the warning cluster are the retained sources. The real issue was first-frame deferred activation: `GhostPlaybackEngine` applied engine events, including `SetEngineAudio(...).Play()`, while a freshly built ghost hierarchy was still inactive for playback-sync positioning. Unity logs the same warning when `Play()` is called on an inactive/disabled source. The existing deferred runtime restore already replays tracked engine/audio power after activation for `#355`; the fix is to let that restore own the first visible looped-audio start instead of calling `Play()` while the ghost is still inactive.

The `suppressed=1` suffix on the surrounding `GhostAudio` start lines was only `ParsekLog.VerboseRateLimited` bookkeeping for repeated start logs, not ghost-audio suppression bookkeeping.

**Status:** Fixed in code — targeted unit slice passes; fresh runtime smoke-log revalidation still recommended

**Priority:** Medium follow-up after merging `#355` — fixed as log noise / correctness issue, not a plume-visibility regression

---

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

Latest investigation: a second procedural-truss attempt was tested against fresh collected logs in `logs/2026-04-13_1529_fairing-truss-artifact`. The run correctly detected `FairingJettisoned` and rebuilt the ghost with `showMesh=True`, but the generated truss still looked bad in game: visible dark bars with transparent gaps following the fairing outline from base to tip. This confirms the simplified procedural replacement is still not shippable.

Important constraint: the current ghost snapshot is just a normal `ProtoVessel`/`ConfigNode` capture (`BackupVessel` output copied into `GhostVisualSnapshot`). That preserves fairing state such as `fsm`, `ModuleStructuralNodeToggle.showMesh`, and `XSECTION`, but it does not preserve the live runtime-generated stock Cap/Truss mesh deformation/material state from `ModuleProceduralFairing`. So the ghost cannot reproduce the exact stock truss visual from snapshot data alone.

To implement properly: prefer a stock-authoritative approach instead of another simplified procedural mesh. Most likely options are either capturing the live stock fairing truss render/mesh state at record time, or spawning/regenerating a hidden stock fairing from the snapshot and cloning the resulting stock truss renderers for the ghost. Only fall back to custom geometry if it can genuinely match stock quality.

**Status:** Open — do not revive the current simplified procedural-strip truss

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort

---

## TODO — Recording Data Integrity

### ~~T65. Revert-finalized active non-leaf recordings can keep `terminal=null` and default ghost-only~~

**Observed in:** `logs/2026-04-14_1449_booster-separation-improved-check/` on the validated `fix/booster-separation-seed-timing` package (`cf5e6dac`). The booster separation seed-timing fix itself is validated in that bundle; the follow-up regression is the separate revert path:

- `KSP.log:10757` classified the load as a real revert
- `KSP.log:10771` finalized active recording `Kerbal X` as `terminal=none`, `leaf=False`
- `KSP.log:10967-10968` merge dialog treated that active non-leaf as `canPersist=False` / `spawnable=0`
- `KSP.log:11012` applied `ghost-only` to the main recording

**Root cause:** `FinalizePendingLimboTreeForRevert()` finalized the pending tree during `OnLoad`, after the scene reset but before the active non-leaf helper had any scene-exit fallback. Leaf recordings already handled "live vessel unavailable during scene exit" by inferring a terminal from trajectory data; `EnsureActiveRecordingTerminalState()` only tried `FindVesselByPid(...)` and otherwise left the active recording at `TerminalStateValue = null`. That made the merge dialog suppress the main recording even though the stashed recording already had a valid end snapshot/trajectory.

**Fix:** `EnsureActiveRecordingTerminalState()` now accepts scene-exit semantics and reuses the same trajectory-based fallback the leaf finalizer uses when the live vessel is unavailable. On the revert/finalize path it now infers the active non-leaf terminal from recorded end data instead of leaving it null. Added focused regression coverage in `Bug278FinalizeLimboTests.cs` for both the scene-exit inference case and the non-scene-exit no-op case.

**Priority:** Medium — the bug only hits revert/finalize on breakup-continuous active recordings, but it blocks the main vessel from being spawnable in the merge dialog and cascades into ghost-only ledger state

**Status:** ~~Fixed~~

---

### ~~T62. Add a cold-start kerbal slot migration integration test~~

This coverage landed in the new kerbal load pipeline suite. `KerbalLoadPipelineTests` now exercises the cold-start load ordering around `LoadCrewAndGroupState(...)` and `LedgerOrchestrator.OnKspLoad(...)`, including:

- persisted `KERBAL_SLOTS` restoring before kerbal recalculation
- stale historical `KerbalAssignment` rows being repaired back to the slot owner
- EVA-only recordings repopulating crew end states during load repair
- a second load pass staying stable instead of rewriting equivalent rows again

That gives us a dedicated restart-path regression test for the exact slot-load + migration + repair convergence path that had been missing.

**Status:** ~~Fixed~~

---

### T63. Expand true `ApplyToRoster()` end-to-end coverage for repaired historical stand-ins

Most of this TODO is now covered. `KerbalLoadDiagnosticsTests` pins:

- retired stand-in recreation and unused stand-in deletion through the roster-application pass
- failed historical recreation not producing a false "kept" repair summary
- a minimal real-`KerbalRoster` wrapper path for the steady-state retired-history case

What still remains is the deeper real-roster mutation path. The production `KerbalRosterFacade` branches for generated stand-ins, recreated stand-ins, and deletions are still validated mainly through the fake roster facade because KSP's native crew-generation path is awkward to exercise safely in xUnit.

The follow-up worth keeping open is a true mutation-heavy wrapper test that proves the real `KerbalRoster` adapter preserves the same recreated/deleted outcomes as the facade-path regression tests.

**Priority:** Low — the behavioral risk is now concentrated in the real-roster adapter seam, not the reservation logic itself

**Status:** Open

---

### ~~T64. Add explicit diagnostics for kerbal slot/assignment repair on load~~

This shipped as `KerbalLoadRepairDiagnostics`. Parsek now emits a concise once-per-load repair summary when kerbal reservation data is actively repaired, including:

- slot source / ignored persisted slot entries
- chain-extension repairs
- repaired recording counts plus old/new assignment row totals
- stand-in remaps, end-state rewrites, and tourist rows skipped during migration
- retired stand-in recreation and unused stand-in deletion during roster application

The emission now works on both cold-start and in-session scene loads, and the regression suite pins the tricky cases that initially produced misleading output: mixed tourist + remap repairs, pure reorder/sequence-only rewrites, chain-extension reporting, and failed historical recreation.

**Status:** ~~Fixed~~

---

### ~~T60. Add regression coverage and diagnostics for R/FF enablement reasons~~

**Evidence bundle:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/`

**Primary log:** `.tmp/logs/2026-04-12_163227_phase-11-5-branch-validation/KSP.log`

Available local playtest logs show R/FF row enablement is driven by recording/runtime state, not ghost distance. Examples:

- `logs/2026-04-10_engine-fx-regression/KSP.log:15443` — `FF #13 "Kerbal X": disabled — Stop recording before fast-forwarding`
- `logs/2026-04-12_0348_bugfixes-2-walkback/KSP.log:23067` — `R #4 "Crater Crawler": disabled — Stop recording before rewinding`
- `logs/2026-04-12_0213_bugfixes-2/KSP.log:12946-12961` — `BeginRewind` is immediately followed by `R #0 "Crater Crawler": disabled — Rewind already in progress`

The local `s3` / LOD archive (`logs/2026-04-11_2339_290-rover-underground/KSP.log`) predates those explicit UI-reason lines, but it does show two long active tree-recording windows:

- `StartRecording succeeded` at `9137`, then the tree is finalized/committed at `11702-12030`
- `StartRecording succeeded` at `12502`, then the tree is finalized/committed at `14659-15003`

That means the long disabled interval reported for the `s3` save lines up with an active recording window; distance is not involved, and the most likely reason in that bundle is the normal `Stop recording before rewinding/fast-forwarding` guard.

The governing code is `RecordingsTableUI` + `RecordingStore.CanRewind/CanFastForward`. Distance/watch state is only used for the `W` button path and should never affect R/FF availability.

**Conclusion:** No real R/FF distance bug found. The runtime behavior was already correct: row/group R/FF enablement comes from recording timing/save/runtime state, while watch distance only gates `W`. In the archived `s3` / LOD save, the long disabled interval tracks active recording; in later reason-logged bundles, the same buttons also legitimately disable during an active rewind.

**Fix:** Added focused coverage for:

- `CanFastForward`: 0-point recordings return `Recording not available`
- `CanFastForward`: current/past recordings return `Recording is not in the future`
- `CanRewind`: tree branches with no root rewind save return `No rewind save available` / `Rewind save file missing`
- transient blocks: `isRecording`, `IsRewinding`, and `HasPendingTree`
- UI-level guard that distance/watch state never changes R/FF enablement
- testable core helpers: `CanFastForwardAtUT` and `CanRewindWithResolvedSaveState` preserve the runtime guard order while making the missing reason cases unit-testable

**Priority:** Medium — current runtime logic is mostly correct, but the failure modes are easy to misread in playtests unless we lock them down with tests and explicit reasoning

**Status:** ~~Fixed~~

---

### ~~T55. AppendCapturedDataToRecording does not copy FlagEvents or SegmentEvents~~

`AppendCapturedDataToRecording` (ParsekFlight.cs:1836) appends Points, OrbitSegments,
PartEvents, and TrackSections, but omits FlagEvents and SegmentEvents. By contrast,
`FlushRecorderToTreeRecording` (ParsekFlight.cs:1675) correctly copies all six lists
including FlagEvents with a stable sort.

This is a pre-existing gap affecting all three call sites of `AppendCapturedDataToRecording`:
- `CreateSplitBranch` line 2137 (merge parent recording with split data at breakup)
- `CreateMergeBranch` line 2283 (merge parent recording with dock/board continuation)
- `TryAppendCapturedToTree` line 1886 (bug #297 fix -- fallback append to tree)

In practice, FlagEvents are rare (only emitted for flag planting, which is uncommon during
breakup/dock/merge boundaries), and SegmentEvents are typically empty in `CaptureAtStop`
because they are emitted into the recorder's PartEvents list, not the SegmentEvents list.
So data loss from this gap is unlikely but possible.

**Fix:** Add FlagEvents and SegmentEvents to `AppendCapturedDataToRecording`, using the same
stable-sort pattern as `FlushRecorderToTreeRecording` (lines 1712-1714). For FlagEvents use
`FlightRecorder.StableSortByUT(target.FlagEvents, e => e.ut)`. SegmentEvents have a `ut`
field and should use the same pattern. Add tests to `AppendCapturedDataTests.cs` verifying
both event types are appended and sorted.

**Fix:** Added FlagEvents and SegmentEvents (with stable sort) to both `AppendCapturedDataToRecording`
and `FlushRecorderToTreeRecording`. Two new tests in `AppendCapturedDataTests.cs`.

**Priority:** Low -- unlikely to cause visible data loss, but should be fixed for correctness

**Status:** ~~Fixed~~

---

### ~~T56. Remove standalone RECORDING format entirely~~

Follow-up to bug #271 (always-tree unification). The runtime now always creates tree recordings, and the injector now produces RECORDING_TREE nodes for all synthetic recordings.

~~Critical subtask (done):~~ Removed the temporary `TreeId != null` skip in `CanAutoSplitIgnoringGhostTriggers`. The existing `RunOptimizationPass` code already added split recordings to `tree.Recordings` and updated `BranchPoint.ParentRecordingIds`; the skip was the only thing preventing tree splits. Added `RebuildBackgroundMap()` after optimization passes for tree consistency. Fixed `TraceLineagePids` to follow chain links so root lineage PID collection works after optimizer splits.

~~Steps 1-5 (done):~~ Deleted `StashPending`/`CommitPending`/`DiscardPending` and the `pendingRecording` slot. Replaced with `CreateRecordingFromFlightData` (factory) and `CommitRecordingDirect` (commit without pending slot). Deleted `MergeDialog.Show(Recording)`, `ShowStandaloneDialog`, `ShowChainDialog`. Deleted standalone RECORDING serialization (`SaveStandaloneRecordings`/`LoadStandaloneRecordingsFromNodes`). Deleted `PARSEK_ACTIVE_STANDALONE` migration shim. Rewrote `ChainSegmentManager.CommitSegmentCore` to use new API. Cleaned ~27 standalone references from ParsekScenario. Updated `FlightResultsPatch` to use `HasPendingTree`. Deleted `GetRecommendedAction`/`MergeDefault`, `AutoCommitGhostOnly(Recording)`, `RestoreStandaloneMutableState`, `isStandalone` flag.

~~Step 6 (done):~~ Collapsed `committedRecordings` into `committedTrees`. Changed `CommittedRecordings` to `IReadOnlyList<Recording>`. Added `AddRecordingWithTreeForTesting` helper. Set `TreeId` on chain segments via `ChainSegmentManager.ActiveTreeId`. `FinalizeTreeCommit` skips already-committed recordings. Migrated ~93 test `.Add()` calls across 24 files.

**Status:** ~~Done~~

**Status:** ~~Fixed (PR #214) -- standalone RECORDING format removed, all recordings are tree recordings, CommittedRecordings is IReadOnlyList~~

---

### ~~T57. EVA spawn-at-end blocked by parent vessel collision~~

EVA recordings created by mid-flight EVA (tree branch) fail to spawn at end because the entire EVA trajectory overlaps with the already-spawned parent vessel. The spawn collision walkback exhausts every point in the trajectory and abandons the spawn.

**Fix:** Added `exemptVesselPid` parameter to `CheckOverlapAgainstLoadedVessels` and threaded it through `CheckSpawnCollisions` and `TryWalkbackForEndOfRecordingSpawn`. New `ResolveParentVesselPid` resolves the parent recording's `VesselPersistentId` via `ParentRecordingId`. EVA spawns skip the parent vessel during collision walkback while still detecting other vessels.

**Status:** ~~Fixed~~

---

### ~~T58. Debris/booster ghost engines show running effects at zero throttle after staging~~

When a booster separates (staging, decouple), the debris recording inherits the engine state from the moment of separation. If the engine was running at separation, the ghost plays back engine FX (flame, smoke) even though the throttle is 0 on the separated stage.

**Root cause:** `MergeInheritedEngineState` added inherited engines to the child's `activeEngineKeys` even when `SeedEngines` had already found the engine part but determined it was non-operational (fuel severed by decoupling). The check at line 1870 only verified the key wasn't in `activeEngineKeys`, not whether `SeedEngines` had already assessed the engine.

**Fix:** Added `allEngineKeys` parameter to `MergeInheritedEngineState`. `SeedEngines` adds ALL engine parts (operational or not) to `allEngineKeys`. If an inherited engine key is in `allEngineKeys` but not `activeEngineKeys`, the child vessel has the engine but it's non-operational -- skip inheritance. Only inherit when the engine wasn't found by `SeedEngines` at all (KSP timing issue).

**Status:** ~~Fixed~~

---

### ~~T59. Rewind save lost after mid-recording EVA branch~~

The R button never appears in the recordings table because `RewindSaveFileName` is lost during the EVA branch flow. `BuildCaptureRecording` copies the filename into `CaptureAtStop` then clears the recorder field. The EVA child recorder never captures one. At commit, only the current (EVA) recorder is checked -- both sources are null.

**Fix:** Extracted `CopyRewindSaveToRoot` (ParsekFlight, internal static) that copies `RewindSaveFileName`, reserved budget (funds/science/rep), and pre-launch budget from a `CaptureAtStop` to the tree's root recording. Called from four sites: `CreateSplitBranch` (the primary T59 fix -- copies at branch time before the EVA recorder takes over), `FinalizeTreeRecordings`, `StashActiveTreeAsPendingLimbo`, and `MergeCommitFlush`. First-wins semantics: root fields are only set if currently empty.

**Status:** ~~Fixed~~

---

## 308. Reserved kerbals appear assignable in VAB/SPH crew dialog

**Observed in:** 0.8.0 (2026-04-12). Reserved kerbals (those whose recordings are playing back as ghosts) appear auto-assigned to vessel seats in the VAB/SPH crew dialog. The player sees them in the crew panel and thinks the reservation system failed.

**Root cause:** KSP's `KerbalRoster.DefaultCrewForVessel` auto-assigns all Available kerbals into command pod seats. Reserved kerbals stay at Available status by design (changing rosterStatus caused tug-of-war bugs with `ValidateAssignments` -- see `CrewDialogFilterPatch` history). The existing `CrewDialogFilterPatch` (prefix on `BaseCrewAssignmentDialog.AddAvailItem`) correctly filters reserved kerbals from the Available crew list, but `DefaultCrewForVessel` runs before that filter, so reserved kerbals are already seated in the manifest. The flight-ready swap (`SwapReservedCrewInFlight` in `CrewReservationManager.cs`) catches this at launch time, but the user sees the wrong crew in the editor.

**Fix:** Added `CrewAutoAssignPatch` (Harmony prefix on `BaseCrewAssignmentDialog.RefreshCrewLists`). Walks the `VesselCrewManifest` before UI list creation and replaces any reserved crew with their stand-ins from `CrewReservationManager.CrewReplacements`. If no stand-in is available, the seat is cleared. Pure decision logic extracted into `DecideSlotAction` (internal static) for testability. 8 unit tests in `CrewAutoAssignPatchTests.cs`. Files: `Patches/CrewAutoAssignPatch.cs`.

**Status:** Fixed

---

## TODO — Nice to have

### ~~T53. Watch camera mode selection~~

**Done.** V key toggles between Free and Horizon-Locked during watch mode. Auto-selects horizon-locked below atmosphere (or 50km on airless bodies), free above. horizonProxy child transform on cameraPivot provides the rotated reference frame; pitch/heading compensation prevents visual snap on mode switch.

### ~~T54. Timeline spawn entries should show landing location~~

Already implemented — `GetVesselSpawnText()` in `TimelineEntryDisplay.cs` includes biome and body via `InjectBiomeIntoSituation()`. Launch entries also include launch site name via `GetRecordingStartText()`.

---

# In-Game Tests

- [x] Vessels propagate naturally along orbits after FF (no position freezing)
- [x] Resource converters don't burst after FF jump
