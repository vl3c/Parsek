# TODO & Known Bugs

Older entries archived alongside this file:

- `done/todo-and-known-bugs-v1.md` — 225 bugs, 51 TODOs (mostly resolved, pre-#272).
- `done/todo-and-known-bugs-v2.md` — entries #272-#303 (78 bugs, 6 TODOs).
- `done/todo-and-known-bugs-v3.md` — everything through the v0.8.2 bugfix cascade up to #461. Archived 2026-04-18.
- `done/todo-and-known-bugs-v4.md` — the v0.8.3 cycle plus the v0.9.0 rewind / post-v0.8.0 finalization / TS-audit closures (closed bugs #462-#569 and the small remaining closures carried over from v3 during its archival). Archived 2026-04-25.

When referencing prior item numbers from source comments or plans, consult the relevant archive file.

---

## Rewind to Staging — v0.9 carryover follow-ups

The feature itself shipped on `feat/rewind-staging` across the v0.9 cycle (design: `docs/parsek-rewind-to-staging-design.md`; pre-implementation spec archived at `docs/dev/done/parsek-rewind-staging-design.md`; roadmap + CHANGELOG under v0.9.0). Items 1-18 of the post-merge follow-up cascade are archived in `done/todo-and-known-bugs-v4.md`.

Items below were landed on PR #514 (`bug/extrapolator-destroyed-on-subsurface`) on top of v4's archive sweep and will move to the next archive when that PR merges.

19. **Unfinished Flight rows duplicated as top-level mission rows alongside the nested Unfinished Flights subgroup.** ~~done~~ — `RecordingsTableUI.DrawGroupTree` populates each tree's auto-generated root group from `grpToRecs`, which stores raw tree membership and does not know about the virtual Unfinished Flights subgroup. After item 18's reap-and-Immutable fix the duplication was no longer driven by lingering RPs, but a pre-merge UF (terminal=Destroyed, RP still alive) was still rendered twice: once via `BuildGroupDisplayBlocks(directMembers, …)` as a regular tree row, and once via `DrawVirtualUnfinishedFlightsGroup(…, nestedUnfinished)` as the nested system group. Fixed by filtering UF members out of `directMembers` in `DrawGroupTree` when `hasNestedUnfinished` is true; the trim is rate-limit-logged via key `uf-filter-out-of-tree-row-<groupName>`. The UF subgroup remains the sole render surface for those rows. Defensive comment added to `DrawVirtualUnfinishedFlightsGroup`'s Rewind/FF placeholder spelling out that the virtual group has no group-level Re-Fly button (each member maps to a specific RP slot, so a single "re-fly all" makes no sense).

20. **Coalescer produced an "Unknown" 0s ghost recording for controllable splits whose child died inside the breakup window.** ~~done~~ — `ParsekFlight.ProcessBreakupEvent`'s controlled-children loop ran `CreateBreakupChildRecording` for every entry in `crashCoalescer.LastEmittedControlledChildPids`, including pids whose live `Vessel` had been torn down by Unity before the breakup window expired. When the child also had no pre-captured snapshot (`crashCoalescer.GetPreCapturedSnapshot(pid) == null`), the resulting recording carried only the seed trajectory point, no events, no snapshot, and `VesselName="Unknown"` (the in-flight name resolution returns null on a destroyed Vessel and `CreateBreakupChildRecording` falls back to the literal "Unknown"). Player saw a 0s "Unknown" row in their tree's auto-group with no playback or replay value next to the real BREAKUP children. Fixed by short-circuiting the loop when both `childVessel == null && ctrlSnap == null`: the BREAKUP branch point on the parent recording already captures that the split happened, so the empty child recording is dropped before allocation. INFO log `ProcessBreakupEvent: skipping dead-on-arrival controlled child pid=… (vessel destroyed before window expired, no pre-captured snapshot) — would produce an 'Unknown' 0s row with no playback value` makes the skip auditable. Items 19 + 20 reproduced from the `2026-04-25_1047_uf-rewind-real-upper-stage` playtest (KSP.log lines 11049 and the IsUnfinishedFlight render trace at 12:08:38).

21. **Re-Fly session marker silently wiped on FLIGHT->SPACECENTER round-trip when the active recording was a previously-promoted Unfinished Flight.** ~~done~~ — Review follow-up: the original carve-out only accepted `CommittedProvisional` for in-place continuation and rejected `Immutable`. But `EffectiveState.IsUnfinishedFlight` accepts both Immutable and CommittedProvisional (line 156-157), and `RewindInvoker.AtomicMarkerWrite` has no MergeState gate — it will write an in-place marker for any committed origin with a matching vessel pid. The same save/load wipe symptom therefore reproduced for Immutable UFs. Extended the carve-out to accept `Immutable` as well as `CommittedProvisional` for in-place continuation; the placeholder pattern (origin != active) stays NotCommitted-only because no committed recording is reused there. Test `MarkerInvalid_InPlaceContinuation_Immutable_Cleared` flipped to `MarkerValid_InPlaceContinuation_Immutable_Preserved`. `MarkerValidator.Validate` enforced `active.MergeState == NotCommitted` for `ActiveReFlyRecordingId`. That worked for the original placeholder pattern (origin != active, where the active row was always a fresh `NotCommitted` placeholder) but post-#514 the in-place continuation pattern (`origin == active`, the existing recording continues being recorded into) reuses the existing recording's MergeState. For a UF-as-source of re-fly that recording is `CommittedProvisional` from the prior tree merge's `ApplyRewindProvisionalMergeStates`, so on the SPACECENTER load that precedes the merge dialog the validator failed and `LoadTimeSweep` cleared the marker. Fixed in `MarkerValidator.Validate` by gating MergeState as: accept `NotCommitted` always; accept `CommittedProvisional` and `Immutable` only when `marker.OriginChildRecordingId == marker.ActiveReFlyRecordingId` (in-place continuation). Tests `MarkerValid_InPlaceContinuation_CommittedProvisional_Preserved`, `MarkerInvalid_PlaceholderPattern_CommittedProvisional_Cleared`, and `MarkerValid_InPlaceContinuation_Immutable_Preserved` in `LoadTimeSweepTests.cs`. Reproduced from the `2026-04-25_1246_uf-rebuild-spawn-message-stale` playtest (KSP.log line 12:42:34.296 `Marker invalid field=ActiveReFlyRecordingId; cleared`).

22. **EVA splits never authored a Rewind Point, so destroyed EVA kerbals could not become Unfinished Flights.** ~~done~~ — `ParsekFlight.IsTrackableVessel` defined "trackable" as `SpaceObject` or any part with `ModuleCommand`. EVA kerbals carry `KerbalEVA` rather than `ModuleCommand`, so the kerbal vessel was classified non-controllable by `SegmentBoundaryLogic.IdentifyControllableChildren`. `TryAuthorRewindPointForSplit` for `BranchPointType.EVA` then ran with `controllable.Count = 1` (mother vessel only), `IsMultiControllableSplit(1) == false`, and bailed with `Single-controllable split: no RP (bp=… type=EVA controllable=1)` — no Rewind Point was created. The EVA kerbal recording was still committed and could end with `terminal=Destroyed`, but `EffectiveState.IsUnfinishedFlight` requires a matching RP via `ParentBranchPointId` or `ChildBranchPointId`, so without an RP the destroyed kerbal silently dropped out of Unfinished Flights and the player had no Re-Fly button. Fixed in `IsTrackableVessel` (and `IsTrackableVesselType`) by treating `v.isEVA || v.vesselType == VesselType.EVA` as trackable up front — EVA kerbals are directly controllable by the player even though their part lacks `ModuleCommand`, so for split-event classification they must count as a controllable output. Test `IsTrackableVesselType_EVA_ReturnsTrue` (renamed from `_ReturnsFalse`) in `SplitEventDetectionTests.cs` pins the type-only branch; the live-vessel branch can only be exercised in-game. Reproduced from the `2026-04-25_1314_marker-validator-fix` playtest (KSP.log lines 134680 and 137082 `Single-controllable split: no RP (bp=… type=EVA controllable=1)`).

23. **Map View ghost orbit gap + post-warp survivor spawn from `2026-04-25_1314_marker-validator-fix`.** ~~done~~ — the playtest had two separate issues. First, Flight Map View map-presence creation called `ResolveMapPresenceGhostSource(... allowTerminalOrbitFallback:false)`, so recording `#8` / `b85acd51...` stayed `source=None reason=no-current-segment terminalFallback=False` across the long sparse gap between the early relative section (`UT 1658.9-1668.1`) and the first orbit segment (`UT 171496.6`). It only created a map vessel once that segment became current, then tore it down again when `CurrentOrbitSegmentAt` returned null. Flight Map View now allows terminal-orbit fallback only when the current UT is inside the recording's activation window, before its end UT, and outside all recorded track-section coverage; existing map vessels are kept and orbit-updated through that fallback with an explicit transition log, while recorded pre-orbit coverage still suppresses future terminal orbit previews. Second, the surviving `#15 "Kerbal X"` capsule reached `terminal=Splashed` and was queued during warp, but `FlushDeferredSpawns` kept it pending forever because the endpoint was outside the active vessel's physics bubble. Deferred spawns now execute once warp is inactive, and the obsolete physics-bubble spawn helper was removed so the policy cannot silently keep terminal materializations queued by distance. Regressions landed in `GhostMapPresenceTests.ResolveMapPresenceGhostSource_TerminalFallback_FillsSparseOrbitGapBeforeEnd`, `GhostMapPresenceTests.ResolveMapPresenceGhostSource_TerminalFallback_DoesNotOverrideRecordedPreOrbitCoverage`, `GhostMapPresenceTests.TryResolveTerminalFallbackMapOrbitUpdate_ExistingOrbitSwitchesAcrossSparseGap`, `GhostMapPresenceTests.ResolveMapPresenceGhostSource_MaterializedRecordingSuppressesMapGhost`, and `DeferredSpawnTests.FlushDeferredSpawns_SpawnsQueuedSplashedSurvivorAfterWarpEnds`.

Review follow-ups raised during the `2026-04-25_0153` post-landing review (design-level and diagnostic polish; not blockers on the current branch):

24. **Re-fly merge supersede only covered the chain head, leaving a chain-tip orphan after env-split crashes.** ~~done~~ — Review follow-up: `EnqueueChainSiblings` originally matched siblings by `ChainId` + `ChainBranch` across every committed recording, mirroring `IsChainMemberOfUnfinishedFlight`. The terminal-chain resolver `ResolveChainTerminalRecording` already scopes by owning tree, and the closure builder feeds both supersede commits and the tombstone scan — a future clone path / import / legacy save that drops the same `ChainId`+`ChainBranch` into a foreign tree could silently pull unrelated recordings into the closure (hidden in ERS, kerbal-death actions retired). Hardened with a `TreeId` gate: candidates must share the dequeued member's `TreeId`; recordings without a `TreeId` skip chain expansion entirely. SplitAtSection always emits same-tree segments by construction (`RecordingStore.cs:1992` sets `second.TreeId = original.TreeId`), so the gate is defense-in-depth for legacy / future shapes. Test `ChainExpansion_DifferentTree_Excluded` in `SessionSuppressedSubtreeTests.cs` installs two trees with colliding `ChainId`+`ChainBranch` and asserts the closure stops at the tree boundary. `EffectiveState.ComputeSessionSuppressedSubtreeInternal` (`Source/Parsek/EffectiveState.cs:523`) walked the suppressed-subtree closure forward via `ChildBranchPointId` only. Merge-time `RecordingOptimizer.SplitAtSection` splits a single live recording at env boundaries (atmo↔exo) into a `ChainId`-linked HEAD + TIP where the HEAD keeps the parent-branch-point link to the RewindPoint but ends with `ChildBranchPointId = null` (moved to the TIP at `RecordingStore.cs:2018-2019`), while the TIP carries the `Destroyed` terminal. After re-fly merge, only the HEAD got a supersede row pointing at the new provisional; the TIP stayed visible with the original "kerbal destroyed in atmo" outcome alongside the new "kerbal lived" re-fly. Fixed by adding an `EnqueueChainSiblings` helper invoked at the top of the dequeue body, BEFORE the `ChildBranchPointId` early-return: for each recording added to the closure, every committed recording sharing both `TreeId`, `ChainId`, and `ChainBranch` is also added (and re-enqueued so its own `ChildBranchPointId` walk runs). The contract matches `EffectiveState.IsChainMemberOfUnfinishedFlight` and `ResolveChainTerminalRecording`. Tests `ChainExpansion_HeadOrigin_IncludesTip`, `ChainExpansion_TipOrigin_IncludesHead`, `ChainExpansion_DifferentChainBranch_Excluded`, `ChainExpansion_ThreeSegments_AllIncluded`, `ChainExpansion_TipWithChildBranchPointId_BpDescendantsAlsoIncluded`, and `ChainExpansion_DifferentTree_Excluded` in `SessionSuppressedSubtreeTests.cs`; `AppendRelations_ChainHeadOrigin_WritesSupersedeRowPerSegment` in `SupersedeCommitTests.cs`; `CommitTombstones_KerbalDeathInTip_TombstonedWithChainOrigin` in `SupersedeCommitTombstoneTests.cs`. No retroactive migration: pre-existing affected saves keep the orphan TIP and require a manual `Discard`. Plan in `docs/dev/plans/fix-chain-sibling-supersede.md`.

The three latent carryover items below are tracked in the design doc under Known Limitations / Future Work and are not yet addressed:

- Index-to-recording-id refactor to lift the 13 grep-audit exemptions added in Phase 3.
- Halt `EffectiveRecordingId` walk at cross-tree boundaries (v1 does not produce cross-tree supersedes; latent-invariant guard).
- Wider v2 tombstone scope (contracts, milestones) when safe.

---

# Known Bugs

## 571. Map View ghost icons show weird trajectories that do not match the recorded path

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. "the ghost icons in map
view had very weird trajectories, did not move on their correct paths."

**Suspected supporting evidence in the cleaned KSP.log:**

- `[LOG 13:11:38.311] [Parsek][VERBOSE][Policy] Deferred ghost map vessel for #7 "Kerbal X Probe" — recording starts pre-orbital`
- `[LOG 13:11:44.714] [Parsek][VERBOSE][Policy] Deferred ghost map vessel for #8 "Kerbal X" — recording starts pre-orbital`
- `[LOG 13:13:19.369] [Parsek][INFO][GhostMap] Created ghost vessel 'Ghost: Kerbal X' ghostPid=1840826626 type=Ship body=Kerbin sma=4070696 for recording index=8 (from segment) orbitSource=visible-segment segmentBody=Kerbin segmentUT=171496.6-193774.6 …`
- `[LOG 13:13:19.582] [Parsek][INFO][GhostMap] Removed ghost map vessel for recording #8 ghostPid=1840826626 reason=ghost-destroyed`

The capture shows ghost map vessels being created from `visible-segment` /
`endpoint-terminal-orbit` sources, then torn down within ~200ms with
`reason=ghost-destroyed` or `reason=tracking-station-existing-real-vessel`.
Combined with the `Deferred ghost map vessel … recording starts pre-orbital`
deferrals, the player sees orbit-line previews that don't track the recorded
trajectory.

**Diagnosis (2026-04-25):** the symptom has two contributing root causes:

- **Part A (primary, recorder-side):** long warp produces a single
  `OrbitalCheckpoint` track section spanning more than one orbital period. In
  this playtest, recording `b85acd51ea7f4005bb5d879207749e8c` covered
  `UT=171496.6-193774.6` (~22 ks, ~1.36 Kerbin orbital periods) as a single
  `OrbitSegment` (sma=4070696, ecc=0.844672, mna=1.185624, epoch=171496.6) with
  9 sparse trajectory points. `GhostMapPresence.CreateGhostVesselFromSegment`
  (`Source/Parsek/GhostMapPresence.cs:934`) faithfully renders that single
  Keplerian arc — but at warp speed the player sees the icon trace the full
  ellipse 1.36 times during one segment window, which reads as a "weird
  trajectory" that does not match the live ship's path. Conceptual fix:
  densify checkpoint sections during long warp by sampling additional
  interpolated trajectory points along the same Keplerian arc the segment
  encodes, so the playback path has motion samples between segment endpoints.
- **Part B (secondary, predicted-tail-side):** the `MissingPatchBody`
  warning floor (#575) discards the entire predicted patched-conic chain on
  the first null-body patch. In the captured log every entry is
  `patchIndex=1`, meaning patch 0 was always valid but `ResetFailedResult`
  threw it away anyway. Without the predicted tail the recording stores no
  augmentation data between checkpoint segments. This half is closed by
  fixing #575 to keep partial results before the first null patch.

**Files (with line references from the diagnosis pass):**

- Recorder-side densification: the `OrbitalCheckpoint` capture path in
  `Source/Parsek/FlightRecorder.cs` and the orbit-segment add path in
  `Source/Parsek/RecordingStore.cs`.
- Render path (already correct): `Source/Parsek/GhostMapPresence.cs` —
  `CreateGhostVesselFromSegment` line 934, `BuildAndLoadGhostProtoVessel`
  line 3009, sparse-orbit-gap fallback at line 1283-1304 (item 23 fix).
- Predicted-tail path (Part B): `Source/Parsek/PatchedConicSnapshot.cs:151-162`
  (`ResetFailedResult` discard floor) and
  `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:287-296`.
- Latent (not the user-visible symptom but flagged during the diagnosis):
  `Source/Parsek/GhostMapPresence.cs:1758-1760` dereferences a possibly-null
  `OrbitSegment?` without `HasValue` check; would throw
  `InvalidOperationException` if `FindOrbitSegmentForMapDisplay` ever returned
  null mid-frame. Worth a follow-up `if (seg.HasValue)` guard.

**Reproducer hooks:** in
`logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned`:

- Recorder pattern: `Boundary point sampled at UT=171496.6` →
  `TrackSection started: env=ExoBallistic ref=OrbitalCheckpoint
  source=Checkpoint at UT=171496.59` → `Orbit segment added to TrackSection
  checkpoints: body=Kerbin UT=171496.59-193774.62` → `Recording #N "Kerbal X"
  eligible: UT=[1659.0,193774.6] points=9`. Fixture recording IDs:
  `8e27ba1144a7484b815847c05c49d10e` (pre-merge),
  `b85acd51ea7f4005bb5d879207749e8c` (post-merge).
- Render: `Created ghost vessel 'Ghost: Kerbal X' ghostPid=1840826626 …
  orbitSource=visible-segment segmentBody=Kerbin
  segmentUT=171496.6-193774.6 … sma=4070696 ecc=0.844672`. Pin segment span
  > 1 orbital period.
- `MissingPatchBody` storm: 153× pairs where every entry has `patchIndex=1`,
  never `patchIndex=0`.

**Status:** Open. Part B will be addressed by the #575 fix; Part A
(checkpoint densification) is a separate recorder-side change still to be
scheduled.

---

## ~~572. Landed capsule was not spawned at the end of its recording~~

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. "the capsule I landed was
not spawned at the end of the recording."

**Diagnosis (2026-04-25):** duplicate of already-closed #570. The capsule the
user described is recording `79a0fa28567c4b9494e7bc5797718037` ("Kerbal X" #15,
terminal=Splashed terminalUT=194183.0, endUT=194195.7). It was queued for
post-warp spawn at `KSP.log` line 537676 (`Deferred spawn during warp: #15
"Kerbal X"`) and then logged 1815× as `Deferred spawn kept in queue (outside
physics bubble): #15 "Kerbal X"` between lines 537702 and 541400. That
diagnostic line is emitted only by the pre-#534 bubble gate in
`ParsekPlaybackPolicy.FlushDeferredSpawns` — the captured commit `ef63407a`
on `bug/extrapolator-destroyed-on-subsurface` predates the merge of `fcb8a656`
(PR #534) on that branch. Current `main` removed `ShouldDeferSpawnOutsideBubble`
entirely; the spawn would now fire the first non-warp frame.

`ShouldSpawnAtRecordingEnd` correctly flagged the recording as needing
materialization (`Spawn #15 'Kerbal X' UT=194195.7 terminal=Splashed` timeline
row at line 23799). Only the now-deleted bubble gate held the spawn. Existing
regression `DeferredSpawnTests.FlushDeferredSpawns_SpawnsQueuedSplashedSurvivorAfterWarpEnds`
already pins this case.

**Action:** re-test on a build with the post-`fcb8a656` fix tail (any current
`main` build); the symptom will not reproduce. Closed as a duplicate of #570.

**Status:** CLOSED 2026-04-25 (duplicate of #570).

---

## ~~573. Real vessel copy of the upper stage materializes alongside the ghost during Re-Fly~~

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. "when I did the Re-Fly,
when controlling the booster, the upper stage ghost was visible, but also a
real vessel copy of the upper stage (that should not exist)." User flagged this
as `(again - was not fixed)` — i.e. believed already-shipped.

**Prior fix on record:** `CHANGELOG.md` 0.9.0 Bug Fixes — *"Strip-killing the
upper stage during re-fly no longer trips spawn-death respawn, so a duplicate
upper-stage vessel doesn't materialise next to the booster."* That fix
addressed the spawn-death respawn path. The user's recurrence suggests either
a regression, a different code path that bypasses the strip-kill protection,
or an incomplete fix that only covered one trigger.

**Suspected supporting evidence in the cleaned KSP.log:**

- `[LOG 13:04:04.437] [Parsek][INFO][GhostMap] Tracking-station handoff skipped duplicate spawn for #1 "Kerbal X" — real vessel pid=2708531065 already exists`
- `[LOG 13:09:32.514] [Parsek][INFO][KSCSpawn] Spawn not needed for #15 "Kerbal X": source vessel pid=2708531065 already exists - adopting instead of spawning duplicate`

The duplicate-protection branches that *do* exist (Tracking-station handoff,
KSCSpawn) fire with the right "already exists" guard. The duplicate that the
user saw must come from a third path.

**Files to investigate:**

- `Source/Parsek/RewindInvoker.cs` — the post-load `Activate` step, atomic
  provisional commit, and `ReFlySessionMarker` write. If the booster activates
  but the upper-stage strip didn't propagate, the upper-stage real vessel
  survives.
- `Source/Parsek/LoadTimeSweep.cs` — discards zombie `NotCommitted` provisionals
  + session-provisional RPs; check whether a stale provisional upper-stage
  recording is materializing during the sweep.
- `Source/Parsek/VesselSpawner.cs` — `SpawnVesselOrChainTipFromPolicy`,
  `MaterializeFromRecording`, and any path that doesn't consult the same
  "already exists" guard as Tracking-station handoff and KSCSpawn.
- `Source/Parsek/ParsekScenario.cs` — `StripOrphanedSpawnedVessels`. The strip
  log around `[LOG 13:10:15.508]` removes 5 orphaned spawned vessels before the
  rewind FLIGHT load; verify the upper-stage `pid` is included.

**Diagnosis (2026-04-25, revised after PR #541 review):** the first attempt at this fix (commit `c9d257f8`) widened `ParsekPlaybackPolicy.RunSpawnDeathChecks` to short-circuit while `RewindContext.IsRewinding` is true. PR review correctly pointed out this could not address the production sequence: `ParsekScenario.HandleRewindOnLoad` calls `RecordingStore.ResetAllPlaybackState` (which zeros `VesselSpawned` + `SpawnedVesselPersistentId` on every recording) and then `RewindContext.EndRewind()` BEFORE OnLoad returns, while still loading into Space Center. By the time the FLIGHT update path can run `RunSpawnDeathChecks`, the rewind flag is false AND the spawn-tracking fields are already zero, so `ShouldCheckForSpawnDeath` returns false on every iteration and the detector never engages.

The actual production duplicate-spawn fires through a different code path. Concretely, in the 2026-04-25_1314 playtest:

1. `[LOG 13:10:13.161] [Rewind] Rewind replay duplicate scope armed: rec=8e27ba1144a7484b815847c05c49d10e sourcePid=2708531065` — `InitiateRewind` arms `RecordingStore.RewindReplayTargetSourcePid = 2708531065` (the booster's pid).
2. `HandleRewindOnLoad` strips ALL recording-named vessels from flightState, calls `ResetAllPlaybackState`, calls `EndRewind`.
3. Player launches a NEW vehicle `Jumping Flea` (pid 2905720181) at 13:10:47.578 — different stock craft, different pid.
4. Player time-warps for ~1.5 minutes; chain replay advances through the rewound `Kerbal X` tree (treeId `7e46a9f16c9a4dcd90d1c1baaea6e2f5`).
5. `[LOG 13:13:19.600] PlaybackCompleted index=15 vessel=Kerbal X ... needsSpawn=True ... Deferred spawn during warp: #15 "Kerbal X"` — recording `2c276b3c6a9c438eb288dc4cbd55a3ee` (chain leaf, terminal Splashed at UT 194195.7, `vesselPersistentId = 2708531065` because chain segments share the source pid) reports `needsSpawn=True` because it has a `VesselSnapshot`, terminal Splashed, and is the effective leaf for that pid.
6. After the player exits warp the deferred-spawn flush calls `ParsekFlight.SpawnVesselOrChainTipFromPolicy → SpawnAtChainTip → SpawnChainTipWithResolvedState → RespawnVessel(preserveIdentity:true)`. `TryAdoptExistingSourceVesselForSpawn` cannot adopt (no real vessel with pid 2708531065 exists — it was stripped). `RespawnVessel` then materialises a fresh vessel with pid 2708531065 — the user's "real vessel copy of the upper stage". `RunSpawnDeathChecks` never runs on this path because spawn-death only fires AFTER a vessel was successfully tracked, then disappeared; here the vessel is being created from scratch.

**Fix:** introduce `Recording.SpawnSuppressedByRewind` (transient field, persisted in tree mutable state). `ParsekScenario.HandleRewindOnLoad` calls a new helper `MarkRewoundTreeRecordingsAsGhostOnly` after `ResetAllPlaybackState` that walks the committed list and sets the flag on every recording either (a) in the rewound tree (matched by TreeId of the rewind owner from `RewindReplayTargetRecordingId`) or (b) whose `VesselPersistentId` matches the armed `RewindReplayTargetSourcePid` (covers standalone-recording rewind). `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` consults the flag immediately after `TerminalSpawnSupersededByRecordingId` and returns `(false, "spawn suppressed post-rewind (ghost-only past, #573)")`. This blocks every spawn entry-point downstream (`SpawnAtChainTip`, `SpawnOrRecoverIfTooClose`, KSC spawn, tracking-station handoff — all gate on `ShouldSpawnAtRecordingEnd` or its `ShouldSpawnAtKscEnd` / `ShouldSpawnAtTrackingStationEnd` callers). `RecordingStore.ResetRecordingPlaybackFields` clears the flag so a subsequent rewind starts clean. Regression coverage in `RewindTimelineTests`: `ShouldSpawn_PostRewindChainLeafSameSourcePid_ReturnsFalse`, `HandleRewindOnLoad_MarksAllRewoundTreeChainLeavesGhostOnly_AllSpawnRefused` (drives the production sequence end-to-end through the helper and asserts every spawn entry-point refuses), `MarkRewoundTreeRecordingsAsGhostOnly_StandaloneRecording_MarksByPidMatch`, `MarkRewoundTreeRecordingsAsGhostOnly_AlreadyMarked_NoOp`, `ResetAllPlaybackState_ClearsSpawnSuppressedByRewind`.

The `RewindContext.IsRewinding` short-circuit in `RunSpawnDeathChecks` from `c9d257f8` is retained as defense-in-depth (with corrected wording in code + tests calling out that it does NOT cover the production sequence), so a future regression that splits the rewind sequence across update ticks can't trip the spawn-death detector.

**Out of scope (separate follow-up):** the diagnosis pass also flagged
`VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay` (`Source/Parsek/VesselSpawner.cs:99-141`) as deserving a sanity audit — its #226 replay/revert duplicate-spawn exception bypasses the adoption guard whenever the source PID matches the scene-entry active vessel, which expanded scope beyond the booster-respawn intent during this playtest. Not changed in this PR; track as a separate concern.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## 574. Extrapolator: 146 sub-surface state rejections classified as Destroyed for the same recordings

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
146 occurrences each of:

- `[Parsek][WARN][Extrapolator] Start rejected: sub-surface state body=Kerbin ut=<F> alt=<F> (threshold=<F>); classifying recording as Destroyed`
- `[Parsek][WARN][Extrapolator] TryFinalizeRecording: patched-conic snapshot failed for '<HEX>' with NullSolver; falling back to live orbit state`
- `[Parsek][INFO][Extrapolator] TryFinalizeRecording: sub-surface destroyed terminal applied for '<HEX>' (terminalUT=<F>) — skipping segment append`

The branch the playtest commit lives on is exactly
`bug/extrapolator-destroyed-on-subsurface`, so the user is already
investigating this. The 146-times repetition for the same recording IDs across
an hour-long session suggests the same recording is being finalized repeatedly
(once per re-evaluation pass) and each pass re-applies the destroyed terminal.

**Files to investigate:**

- `Source/Parsek/BallisticExtrapolator.cs` — `Start`, sub-surface threshold
  check, the `classifying recording as Destroyed` branch.
- `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs` — the scene-exit
  seam. Check whether the "destroyed terminal applied" path is idempotent or
  re-runs on every re-evaluation.
- Cross-reference with bug #571 (weird map trajectories) — sub-surface
  reclassification feeds the orbit data that drives the map vessel preview.

**Status:** Open. Already on `bug/extrapolator-destroyed-on-subsurface`
([PR #514](https://github.com/vl3c/Parsek/pull/514) review thread); this entry
captures the symptom for cross-reference.

---

## ~~575. PatchedSnapshot: MissingPatchBody warning floor of 153/session for the same recordings~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
153 occurrences each of:

- `[Parsek][WARN][PatchedSnapshot] SnapshotPatchedConicChain: vessel=Kerbal X patchIndex=<N> body=(missing-reference-body); aborting predicted snapshot capture`
- `[Parsek][WARN][Extrapolator] TryFinalizeRecording: patched-conic snapshot failed for '<HEX>' with MissingPatchBody; falling back to live orbit state`
- `[Parsek][VERBOSE][Extrapolator] TryFinalizeRecording: skipping live-orbit fallback for '<HEX>' because patched-conic failure MissingPatchBody indicates transient early-ascent state, not a destroyed vessel`

The verbose comment immediately following each pair claims the failure is
*"transient early-ascent state, not a destroyed vessel"* — i.e. expected.
But "transient" is supposed to mean "a few frames", not 153 paired warnings
per recording over a single session. Either:

- The early-ascent window is being held longer than necessary;
- The "transient" classifier is firing for non-transient cases too;
- WARN is the wrong level for an expected condition that fires by design.

**Diagnosis (2026-04-25):** `PatchedConicSnapshot.SnapshotPatchedConicChain`
(`Source/Parsek/PatchedConicSnapshot.cs:151-162`) walked the stock patched-conic
chain and called `ResetFailedResult` the moment any patch returned an empty
`BodyName`, throwing away every previously-captured patch in the same chain.
In the captured log every entry was `patchIndex=1`, never `patchIndex=0` —
patch 0 was always valid, only the *next* patch occasionally had a transient
`referenceBody == null` that KSP's stock solver fixes a few frames later.
With the discard-everything policy the recording therefore had no
patched-conic augmentation, and the downstream
`IncompleteBallisticSceneExitFinalizer` (`Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:287-296`)
took the "transient early-ascent state, not a destroyed vessel" skip path on
every refresh because `appendedSegments.Count == 0`.

**Fix:** preserve the partial chain captured before the first null-body patch
when `failedPatchIndex > 0` — set `FailureReason = MissingPatchBody`,
`HasTruncatedTail = true`, log a single rate-of-context VERBOSE truncation
note ("truncated chain after N valid patch(es), keeping partial result"), and
break out of the loop with the captured segments intact. The genuine
"patch 0 has null body" case (`failedPatchIndex == 0`) keeps the original
ResetFailedResult + WARN behaviour. The downstream finalizer's existing
`snapshot.Segments.Count > 0` branch (line 250-258) appends the partial chain
naturally; the transient-ascent skip at line 287-296 only fires when no
patches at all could be captured, which is the correct intent.

This also closes part B of #571 — the user-visible "weird trajectory"
symptom that included a starved predicted tail. Recordings now retain their
predicted-tail orbit data through ascent solver hiccups instead of falling
back to a single Keplerian arc covering the entire warp window.

Regression
`PatchedConicSnapshotTests.Snapshot_MissingPatchBodyAfterValidPrefix_KeepsPartialResult`
pins the new partial-preservation behaviour;
`Snapshot_MissingPatchBody_FailsWithoutKerbinFallback` still pins the original
patch-0-null discard-and-warn case.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## 576. PatchedSnapshot: 146 "solver unavailable" warnings clustered on a few vessels

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
`[Parsek][WARN][PatchedSnapshot] SnapshotPatchedConicChain: vessel=<name> solver unavailable`:

- `Kerbal X Debris` — 77
- `Ermore Kerman` — 45
- `Magdo Kerman` — 12
- `Kerbal X Probe` — 11
- `Kerbal X` — 1

Plus paired `[Parsek][WARN][Extrapolator] TryFinalizeRecording: patched-conic
snapshot failed for '<HEX>' with NullSolver; falling back to live orbit state`
(146 occurrences, same as #574's NullSolver count).

This is the same WARN level as the MissingPatchBody case in #575 but with a
different cause — the solver itself isn't reachable. Same concern: WARN floor
for an expected-on-startup or transient-during-soi-transition condition.

**Files to investigate:**

- `Source/Parsek/PatchedConicSnapshot.cs` — the `solver unavailable` branch
  (this is the `__solver == null` / `solver.maneuverNodes == null` shape
  check, distinct from #575's `MissingPatchBody`).
- Whether the solver should be considered unavailable as a hard error (no
  fallback) or as a soft transient (rate-limit / VERBOSE).

**Status:** Open.

---

## ~~577. ReFlySession marker invalidated on the `InvokedUT` field on a fresh load~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` line 8889:

`[Parsek][WARN][ReFlySession] Marker invalid field=InvokedUT; cleared sess=sess_d67eb15f0492418aa71074c83870f867 tree=a35db8a2e78b44e1b3bd2c7ba002bcd6 active=70006fbb97c74e56bf4e5cc79165c0b8 origin=70006fbb97c74e56bf4e5cc79165c0b8 rp=rp_8eebf4aeb2de49dca41bda7ddd1473f4 invokedUT=578.13180328350882 invokedRealTime=2026-04-25T09:40:55.0225531Z`

The marker survived save/load but was wiped on the next OnLoad because
`InvokedUT` failed validation. PR #535 / #536 already addressed `MergeState`
and `ActiveReFlyRecordingId` field validation for the in-place continuation
pattern; `InvokedUT` is a separate field and may not have been covered. After
this clear, all subsequent `Marker saved/loaded: none` entries indicate the
session never re-engaged.

**Note:** the timestamp `invokedRealTime=2026-04-25T09:40:55Z` is from a
previous game session (the playtest started at 13:00:21 UTC+3 = 10:00 UTC),
so this marker was loaded from the on-disk save and immediately wiped on the
fresh start. The cause may be a stale-marker survival problem, a too-strict
`InvokedUT` validator, or both.

**Files to investigate:**

- `Source/Parsek/MarkerValidator.cs` — `Validate`, the `InvokedUT` field check
  (compare to the recently-relaxed `MergeState` / `ActiveReFlyRecordingId`
  rules on PR #535 / #536).
- `Source/Parsek/LoadTimeSweep.cs` — the validation seam that calls
  `MarkerValidator.Validate` and clears on failure.
- `Source/Parsek/ReFlySessionMarker.cs` — when `InvokedUT` is allowed to be
  null/zero and when it must be non-zero.

**Diagnosis (2026-04-25):** `InvokedUT` is Planetarium game UT; `InvokedRealTime`
is the wall-clock UTC timestamp. The marker was rejected because the pre-fix
validator compared `marker.InvokedUT > CurrentUt()`: the cited fresh
SPACECENTER load reported current UT 0 in the scenario summary, so the valid
prior-session `invokedUT=578.13180328350882` looked like a future value even
though the referenced RP was from the same UT neighborhood.

**Fix:** `InvokedUT` validation now rejects only corrupt values (NaN/Infinity,
negative, or above the `1E+15` sanity ceiling); current UT is diagnostic-only
and accept logs call out `legacyFutureUtCheck=triggered` when the old rule
would have wiped the marker. Load-time marker accept/reject logs include the
six durable fields plus the specific validation details, including `currentUT`,
`rpUT`, and deltas for `InvokedUT`. Regressions:
`MarkerValid_PriorSessionInvokedUtAfterFreshLoadUt_PreservedAndLogged`,
`MarkerInvalid_InvokedUtNaN_ClearedWithDiagnostic`,
`MarkerInvalid_InvokedUtNegative_ClearedWithDiagnostic`, and
`MarkerInvalid_InvokedUtExtremeFuture_ClearedWithDiagnostic`.

**Status:** CLOSED 2026-04-25. Fixed for v0.8.3.

---

## 578. CrewReservation: 3 orphan placements where pid AND name tiers both fail ~~done~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
3 occurrences of:

- `[Parsek][WARN][CrewReservation] Orphan placement: no matching part with free seat in active vessel for 'Magdo Kerman' → 'Herfrid Kerman' (snapshot pid=<N> name='mk1-3pod') — stand-in left in roster (attempted pidTier=yes nameTier=yes; cumulative pidHits=<N> nameHitFallbacks=<N>)`
- Same shape for `'Kathrick Kerman' → 'Lomy Kerman'`
- Same shape for `'Ermore Kerman' → 'Shepry Kerman'`

Both lookup tiers (`pidTier=yes` AND `nameTier=yes`) attempted and both
failed → stand-in is left in the roster. Three different replacement pairs
all pointing at the same `mk1-3pod` snapshot suggests the active vessel does
not actually contain that part type, or all of its mk1-3pod seats are
occupied at the time of placement.

**Files to investigate:**

- `Source/Parsek/CrewReservationManager.cs` — orphan-placement fallback path,
  the pid-tier and name-tier matchers, why neither resolved.
- `Source/Parsek/KerbalsModule.cs` — replacement dispatch.
- This subsystem is flagged as elevated-risk in
  `memory/project_post_v0_8_0_risk_areas.md` after the 6-bug PR #263 mega-fix.

**Fix:** Confirmed hypothesis (a) for the captured playtest: the orphan-placement
pass ran while the active vessel lacked the snapshot command-pod part, so the
old WARN collapsed a wrong-vessel target into a generic no-free-seat failure.
`CrewReservationManager` now classifies no-match misses with active part,
free-seat, pid-match, and name-match counters, logs the pass as deferred with
`reason=active-vessel-missing-snapshot-part`, keeps the stand-in roster entry
for a later retry, and still rejects the removed tier-3 "any free seat"
fallback. Regression coverage:
`CrewReservationNameHitFallbackTests.TryResolveActiveVesselPartForSeat_Bug578_WrongActiveVessel_DiagnosesMissingSnapshotPart`,
the miss-reason truth-table/log assertions in the same class, and the runtime
`Bug578_OrphanPlacement_NoMatchingPart_LogsDeferredReason` in-game test.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## 579. ~~LedgerOrchestrator: pending recovery-funds queue overflowed for `Kerbal X Debris`~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
1× threshold, 1× flush:

- `[Parsek][WARN][LedgerOrchestrator] OnVesselRecoveryFunds: pending queue exceeded threshold (count=<N> > <N>) — paired FundsChanged(VesselRecovery) events may be missing. Latest deferred request vessel='Kerbal X Debris' ut=<F>`
- `[Parsek][WARN][LedgerOrchestrator] FlushStalePendingRecoveryFunds (rewind end): evicting <N> unclaimed recovery request(s) that never received a paired FundsChanged(VesselRecovery) event. Entries: [vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>, vessel='Kerbal X Debris' ut=<F>]`

Six unpaired recovery-funds requests for `Kerbal X Debris` evicted on rewind
end. Either KSP is not firing the matching `FundsChanged(VesselRecovery)` event
for debris recoveries, or Parsek's pairing logic is missing a non-debris-only
filter.

**Files to investigate:**

- `Source/Parsek/GameActions/LedgerOrchestrator.cs` — `OnVesselRecoveryFunds`,
  `FlushStalePendingRecoveryFunds`, the `FundsChanged(VesselRecovery)` pairing
  expectation.
- Whether `Kerbal X Debris` (vessel type Debris) is excluded from KSP's
  `onVesselRecoveryProcessing` funds events by stock; if so, debris should
  not be enqueued at all.

**Fix:** Debris recoveries now short-circuit before deferred recovery-funds
queueing when no immediate paired funds event exists: `ParsekScenario` passes the
recovered `ProtoVessel`'s `VesselType` into `LedgerOrchestrator`, and the
orchestrator defensively skips the pending queue for `VesselType.Debris`
callbacks. Stock API docs and decompilation show `onVesselRecoveryProcessing`
still fires before `onVesselRecovered`, so an already-recorded debris payout can
still pair immediately; debris-only recoveries that produce no ledger-worthy
`FundsChanged(VesselRecovery)` should not contribute pending ledger recovery
entries.

**Status:** ~~Open~~ Done.

---

## ~~580. MergeTree: 3 boundary discontinuity warnings (`unrecorded-gap`) between Background and Active sources~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned` —
3 occurrences of:

`[Parsek][WARN][Merger] MergeTree: boundary discontinuity=<F>m at section[<N>] ut=<F> vessel='Kerbal X' prevRef=Absolute nextRef=Absolute prevSrc=Background nextSrc=Active dt=<F>s expectedFromVel=<F>m cause=unrecorded-gap`

Background-to-Active source handoff produced a position discontinuity larger
than the velocity-extrapolated bound during merge. Three occurrences for
`Kerbal X`. Either Background sampling stopped a beat too early, Active
sampling started a beat too late, or the krakensbane / frame-of-reference
correction at handoff is missing a term.

**Files to investigate:**

- `Source/Parsek/RecordingMerger.cs` (or the Merger subsystem in
  `BackgroundRecorder.cs` / `FlightRecorder.cs`) — the `MergeTree` boundary
  check, `expectedFromVel` derivation, the handoff-frame guard.
- Whether `unrecorded-gap` should heal the boundary by interpolating or just
  surface as a WARN.

**Status:** ~~Open~~ Fixed.

**Fix:** `SessionMerger.MergeTree` now heals same-reference-frame Background→Active `unrecorded-gap` seams before rebuilding the merged flat trajectory from section-authoritative payload. The merger inserts one interpolated boundary point shared by the Background tail and Active head, preserves validated flat tail data, recomputes `boundaryDiscontinuityMeters`, and logs `MergeTree: healed unrecorded-gap ... #580` instead of leaving the old WARN shape for the three cited Kerbal X boundaries: section[2] UT 193973.61, section[4] UT 193977.99, and section[1] UT 193985.09.

---

## 581. Diagnostics: 2 frame-budget breaches during normal flight playback

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned`:

- 2× `[Parsek][WARN][Diagnostics] Playback frame budget exceeded: <F>ms (<N> ghosts, warp: <N>x)`
- 1× `[Parsek][WARN][Diagnostics] Recording frame exceeded budget: <F>ms for vessel "Kerbal X"`
- 1× `[Parsek][WARN][Diagnostics] Playback budget breakdown (one-shot, first exceeded frame): total=<F>ms mainLoop=<F>ms spawn=<F>ms (built=<N> throttled=<N> max=<F>ms) destroy=<F>ms explosionCleanup=<F>ms deferredCreated=<F>ms (<N> evts) deferredCompleted=<F>ms (<N> evts) observabilityCapture=<F>ms trajectories=<N> ghosts=<N> warp=<N>x`

Each breach is a single frame; the budget breakdown one-shot fires for the
first exceeded frame to capture per-bucket cost. Two breaches in an hour-long
session is low frequency, but worth checking which bucket dominates the
breakdown.

**Files to investigate:**

- `Source/Parsek/Diagnostics/FrameBudget.cs` (or wherever the budget check
  lives) and the `Playback budget breakdown` emission point.
- Cross-reference the breakdown's `spawn=`, `deferredCreated=`,
  `observabilityCapture=` buckets against whichever was largest in the
  one-shot line.

**Status:** Open. Low priority unless reproducible at higher frequency.

---

## ~~582. RELATIVE-frame trajectory points carry anchor-local metres in lat/lon/alt fields — recorder data is correct, contract was under-documented~~

**Source:** in-game observation by user during the
`logs/2026-04-25_1314_marker-validator-fix` playtest. *"the ghost icon started
going inside the planet while following the trajectory points (icon only ghost,
not proto-vessel ghost with trajectory line); please check the recordings
trajectory points, something might be wrong there."*

The user pointed at the recorded data because the first `TRACK_SECTION` of
`Recordings/b85acd51ea7f4005bb5d879207749e8c.prec.txt` (ref=Relative,
UT=1658.96-1668.14, anchorPid=95506284) stores values that look obviously
wrong if read as body-fixed lat/lon/alt:

```
POINT { ut=1658.96 lat=-270.69 lon=-149.22 alt=-0.089 ... }
POINT { ut=1668.14 lat=-376.49 lon=-186.21 alt=-0.114 ... }
```

`lat=-270.69` is outside the legitimate `[-90, 90]` range; `alt=-0.089`
is sub-surface; vessel velocity (~2920 m/s world-frame) implies ~26 km of
displacement over the 9.18 s section, but the field deltas are metre-scale.

**Diagnosis (2026-04-25):** the recorder data is correct under the documented
format-v6 RELATIVE-frame contract. The fields are NOT body-fixed lat/lon/alt
in v6 RELATIVE sections — they store the anchor-local Cartesian offset in
metres, computed as `Inverse(anchor.rotation) * (focusWorldPos - anchorWorldPos)`.

**Evidence chain:**

- `FlightRecorder.cs:5502-5543` (`ApplyRelativeOffset`) overrides the
  `BuildTrajectoryPoint`-seeded body-fixed lat/lon/alt with anchor-local
  Cartesian dx/dy/dz when `isRelativeMode == true` and the recording's
  format version reports `UsesRelativeLocalFrameContract`. The dx/dy/dz
  are written into `point.latitude`, `point.longitude`, `point.altitude`
  (lines 5533-5535), with a verbose log
  `RELATIVE sample: contract=anchor-local version=6 dx=… dy=… dz=… anchorPid=… |offset|=…m`.
- `TrajectoryMath.ComputeRelativeLocalOffset` (recorder side) and
  `TrajectoryMath.ApplyRelativeLocalOffset` /
  `TrajectoryMath.ResolveRelativePlaybackPosition` (playback side) use the
  symmetric pair: rotate the world-frame separation into the anchor's local
  frame for storage; rotate it back for replay.
- The captured KSP.log shows the recorder logging consistent metres-scale
  offsets through the relative section, e.g.
  `RELATIVE sample: contract=anchor-local version=6 dx=-111.11 dy=-43.38 dz=-0.27 anchorPid=95506284 |offset|=119.28m`
  (line 11083). The recorded vessel (focus pid=2708531065) and anchor
  (pid=95506284) both came from a controllable split at UT=1627.16
  (line 10866 — `CreateBreakupChildRecording: pid=95506284`), so they
  co-orbit at nearly identical world velocity; the anchor-local offset
  stays small (a few hundred metres) even while world-frame velocity is
  ~2920 m/s. That matches the file values.
- The dual-write at `FlightRecorder.cs:5584` + `:5596` puts the same
  modified point into both the flat `Recording.Points` list and the
  current `TrackSection.frames`. The flat list is therefore frame-blind:
  any caller iterating `Recording.Points` MUST also resolve the
  enclosing `TrackSection.referenceFrame` for that UT before interpreting
  `point.latitude/longitude/altitude` — calling
  `body.GetWorldSurfacePosition(point.lat, point.lon, point.alt)` on a
  RELATIVE-frame flat point places the icon deep underground because
  metre-scale dx/dy/dz are interpreted as degrees-of-latitude plus
  metres-of-altitude.
- Playback resolution is consistent: `ParsekFlight.TryResolvePlaybackWorldPosition`
  (line 13432) checks the section's reference frame at line 13446 and
  dispatches to `TryResolveRelativeWorldPosition` /
  `TryResolveRelativeOffsetWorldPosition` (line 13604-13685), which
  correctly call `ResolveRelativePlaybackPosition` with the anchor's
  current world position and rotation. The flat-point bypass paths in
  `GhostMapPresence.cs` (lines 1979 and 2047) protect themselves with
  `IsInRelativeFrame` guards at lines 1495 and 1733 before calling
  `body.GetWorldSurfacePosition`. The state-vector frame-blindness fix
  the sibling agent in `Parsek-fix-ghostmap-relative-frame` is shipping
  hardens additional flat-point read sites that were missed.

**Outcome (A) — not a recorder bug, contract documentation gap.** The
icon-going-inside-planet symptom belongs to a downstream playback path,
not to the recorded data. The recorded values match the format-v6 contract
exactly; any path that misreads them is a frame-blindness bug at the
read site.

**Fix:** documentation + regression tests pinning the v6 RELATIVE position
contract.

- `.claude/CLAUDE.md` "Rotation / world frame" section now covers the
  POSITION contract alongside the existing rotation note: anchor-local
  Cartesian metres in `latitude`/`longitude`/`altitude`, with explicit
  warning that any flat-`Recording.Points` reader must dispatch on
  `TrackSection.referenceFrame` before calling
  `body.GetWorldSurfacePosition`.
- `Source/Parsek.Tests/RelativeRecordingTests.cs` regressions:
  - `RecorderContract_V6RelativeStoresAnchorLocalOffset_ReplaysToFocusWorldPos`
    pins the round-trip recorder→storage→playback path.
  - `RecorderContract_V6RelativeOffsetIndependentOfAnchorWorldVelocity`
    pins the "anchor world position must not leak into the stored offset"
    property — a moving anchor at orbital velocity must produce the same
    stored value for the same anchor->focus relative displacement.
  - `RecorderContract_V6RelativeFieldsAreNotBodyFixedLatLonAlt` is a
    tripwire that pins values commonly fall outside `[-90, 90]` for
    legitimate RELATIVE-mode separations, so any future code that treats
    a flat-point's `latitude` as degrees will fail loudly.

**Status:** CLOSED 2026-04-25 (data correct; contract now documented; the
playback-side icon-underground symptom is a separate frame-blindness bug
being addressed by the sibling
`fix/ghostmap-state-vector-relative-frame` branch).

---

## ~~584. State-vector ghost map paths fed RELATIVE-frame anchor offsets into `body.GetWorldSurfacePosition`~~

**Source:** code-review observation while triaging #571. Latent in
`logs/2026-04-25_1314_marker-validator-fix` — every state-vector creation
attempt that session was rejected (`reason=state-vector-threshold`,
`no-state-vector-point`), so the bug did NOT fire in the captured playtest.
Visible symptom when it would fire: a ghost map vessel transitions through a
RELATIVE `TrackSection` (Phase 3b docking / rendezvous) while above the
state-vector threshold; the ghost icon snaps to the body surface at a
horizontally-meaningless lat/lon ("ghost icon goes inside the planet" —
contributes to #571's symptom family).

**Cause:** `GhostMapPresence.CreateGhostVesselFromStateVectors`
(`Source/Parsek/GhostMapPresence.cs:1979`) and
`GhostMapPresence.UpdateGhostOrbitFromStateVectors`
(`Source/Parsek/GhostMapPresence.cs:2047`) called
`body.GetWorldSurfacePosition(point.latitude, point.longitude, point.altitude)`
unconditionally. The `TrajectoryPoint.latitude/longitude/altitude` fields
(`Source/Parsek/TrajectoryPoint.cs:13-15`) are reused as anchor-local XYZ
offsets when the originating section uses `ReferenceFrame.Relative`
(`Source/Parsek/TrackSection.cs:34-38`). Feeding offsets into
`GetWorldSurfacePosition` silently produces a meaningless body-surface
position. The flight-scene playback path
(`ParsekFlight.InterpolateAndPositionRelative`, line 13751) and the
diagnostic summary at `GhostPlaybackEngine.cs:3771` already honour the
contract; only these two map-presence paths skipped it.

The tracking-station orbit-update path pre-gates on `IsInRelativeFrame`
(`GhostMapPresence.cs:1733`) and therefore did not fire the bug. The
flight-scene update path in `ParsekPlaybackPolicy.cs:1019` had no such gate,
so the latent defect was actually reachable there.

**Fix:** added a pure-static helper
`GhostMapPresence.ResolveStateVectorWorldPositionPure` that branches on the
section's `referenceFrame`. Absolute keeps the surface lookup; Relative
resolves through `TrajectoryMath.ResolveRelativePlaybackPosition` (the same
contract `InterpolateAndPositionRelative` uses for flight-scene playback)
using the anchor vessel's `GetWorldPos3D()` + `transform.rotation`;
OrbitalCheckpoint and missing-anchor return an unresolved result that the
wrappers convert into a WARN log and a skip. Both call sites now log a branch
tag (`absolute` / `relative` / `orbital-checkpoint` / `no-section`) so post-hoc
audits can confirm the path that fired. `UpdateGhostOrbitFromStateVectors`
gained an `IPlaybackTrajectory traj` parameter; both call sites in
`GhostMapPresence.UpdateTrackingStationGhostLifecycle` and
`ParsekPlaybackPolicy.CheckPendingMapVessels` were updated.

**Tests:** `Source/Parsek.Tests/StateVectorWorldFrameTests.cs` covers all
four branches of the pure helper (absolute, relative v6, relative legacy v5,
orbital-checkpoint, no-section) plus an explicit discriminator test that
identical point data in Absolute vs Relative sections produces divergent
world positions. `Source/Parsek.Tests/GhostMapObservabilityTests.cs`
(32 tests) covers the structured decision-line builder, the lifecycle-summary
helper, the resolution-branch translator, and the per-branch coordinate
contract.

**Observability (post-fix logging contract):** every create / position /
update / destroy decision in `Source/Parsek/GhostMapPresence.cs` emits a
single structured line via `BuildGhostMapDecisionLine` so a future KSP.log
filtered on `[Parsek][INFO][GhostMap]` / `[Parsek][VERBOSE][GhostMap]`
reconstructs the full per-recording lifecycle without cross-file lookups.
Producers fill `GhostMapDecisionFields` (set NaN sentinels via
`NewDecisionFields(action)`) and call the builder. Standard fields always
present: `action`, `rec`, `idx`, `vessel`, `source`, `branch`, `body`,
`scene`. Optional slots appear only when set: `worldPos`, `ghostPid`,
`segmentBody / segmentUT / sma / ecc / inc / mna / epoch`,
`terminalOrbitBody / terminalSma / terminalEcc`, `stateVecAlt /
stateVecSpeed`, `anchorPid / anchorPos / localOffset`, `ut`, `reason`.

Canonical actions (use these names for new lines so existing greps keep
working): `create-segment-intent`, `create-segment-done`,
`create-terminal-orbit-intent`, `create-terminal-orbit-done`,
`create-state-vector-intent`, `create-state-vector-done`,
`create-state-vector-skip`, `create-state-vector-miss`,
`create-dispatch`, `create-chain-intent`, `create-chain-done`,
`update-segment`, `update-state-vector`, `update-state-vector-soi-change`,
`update-state-vector-skip`, `update-state-vector-miss`,
`update-terminal-orbit-fallback`, `update-chain-segment`,
`destroy`, `destroy-chain`, `source-resolve`. The branch tag uses the
capitalised forms (`Absolute` / `Relative` / `OrbitalCheckpoint` /
`no-section` / `(n/a)`) — convert from the resolver via
`MapResolutionBranch`. Per-frame update paths route through
`ParsekLog.VerboseRateLimited` keyed on `recId` (5 s window) so a long warp
pass leaves a readable trace without spam. Both lifecycle drivers
(`UpdateTrackingStationGhostLifecycle`,
`ParsekPlaybackPolicy.CheckPendingMapVessels`) call
`EmitLifecycleSummary(scope, currentUT)` once per tick, which logs
`vesselsTracked / created / destroyed / updated` and resets the per-tick
counters. Future agents extending GhostMap should pick an existing action
name when the decision shape matches, and add a new entry to the canonical
list above when adding a new decision point — duplicating the line shape is
the goal.

**Renumber note:** this entry was originally numbered `#582` while in
flight on `fix/ghostmap-state-vector-relative-frame`, but PR #546 (the
adjacent recorder-side contract documentation) merged first and took the
`#582` slot. Renumbered to `#584` during the rebase merge of `origin/main`
into this branch. CHANGELOG.md was updated to match. The follow-up entry
`#583` (Relative-frame state-vector ghost CREATION still skips for first
activation inside a Relative section) covers the remaining edge case left
open by this fix's `UPDATE`-side scope.

**Status:** Fixed in PR #547 (state-vector RELATIVE-frame contract +
structured GhostMap observability).

---

## ~~570. Warp-deferred survivor spawn stayed queued outside the active vessel's physics bubble~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log`. Recording #15
`"Kerbal X"` was queued by `Deferred spawn during warp` at line 537676, then
`FlushDeferredSpawns` kept it queued outside the active vessel's physics bubble
1815 times through line 541400.

**Cause:** the deferred spawn queue correctly waits while warp is active, but the
post-warp flush reused active-vessel physics-bubble scoping. That is wrong for a
finished terminal survivor: once warp is inactive, the materialization path can
place the real vessel at its recorded endpoint. Keeping the spawn queued only
made it wait forever unless the active vessel moved within 2.3 km.

**Fix:** `ParsekPlaybackPolicy.FlushDeferredSpawns` now executes pending spawn
items once warp is inactive instead of re-checking the active-vessel
physics-bubble distance. Failed flag replays remain queued as before. Regression
`DeferredSpawnTests.FlushDeferredSpawns_SpawnsQueuedSplashedSurvivorAfterWarpEnds`
pins the splashed-survivor case from the log.

Post-warp flush can materialize every pending spawn in the first non-warp frame.
That is intentional for terminal survivors; expected pending counts are small.
If runtime evidence shows large batches hitch, throttle post-warp materialization
as separate performance work instead of reintroducing distance gating.

The regression uses the policy spawn override, matching existing headless
`DeferredSpawnTests` coverage. A future in-game canary should cover the live
`SpawnVesselOrChainTipFromPolicy` branch if this path regresses in KSP runtime.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## 547. Recording optimizer should surface cross-body exo segments more clearly than the current first-body label

**Source:** `docs/dev/recording-optimizer-review.md` (2026-04-07), especially the traced Kerbin-launch-to-Mun-landing scenario.

**Concern:** the optimizer only splits on environment-class changes, not body changes, so a long exo segment can legitimately span Kerbin orbit, transfer coast, and Mun orbit while still inheriting `SegmentBodyName` from its first trajectory point. The current result is structurally correct and loopable, but the player-facing label can still read like a lie (`Kerbin` even though the recording includes Mun orbit time). We need a deliberate decision here instead of leaving it as an accidental quirk: either keep the single exo segment and surface a multi-body label (`Kerbin -> Mun`), or introduce an optional body-change split criterion if that proves clearer in practice.

**Files:** `Source/Parsek/RecordingOptimizer.cs`, `Source/Parsek/RecordingStore.cs`, timeline/recordings UI that renders `SegmentBodyName`, `docs/dev/recording-optimizer-review.md`.

**Status:** TODO. Likely UX/research follow-up, not a v0.8.3 ship blocker.

---

## 548. Static background continuations and all-boring surface leaf segments should not read like empty ghost recordings

**Source:** `docs/dev/recording-optimizer-review.md` (2026-04-07), issues 1 and 2.

**Concern:** two related outputs are still structurally correct but awkward in the player-facing recordings list:
- stationary landed background continuations can end up as `SurfacePosition`/time-range placeholders with no real ghost trail
- all-boring surface leaf segments can survive optimizer trim because they still carry the final `VesselSnapshot`/spawn responsibility

Both cases are valid data, but they clutter the UI and read like broken/empty ghosts. We should either collapse them visually, mark them explicitly as static/stationary, or trim them to a minimal terminal window while preserving their structural role.

**Files:** `Source/Parsek/BackgroundRecorder.cs`, `Source/Parsek/RecordingOptimizer.cs`, recordings/timeline UI that lists committed segments, `docs/dev/recording-optimizer-review.md`.

**Status:** TODO. UX cleanup / follow-up analysis.

---

## 549. Recording optimizer needs end-to-end branch-point coverage when tree recordings are split post-commit

**Source:** `docs/dev/recording-optimizer-review.md` (2026-04-07), issue 5.

**Concern:** the optimizer has unit coverage for split logic, but we still do not have a full tree-with-branch-points regression that proves post-commit environment splits preserve the intended branch linkage and chain navigation shape. The review did not find a live bug here, but this is exactly the seam most likely to regress silently when optimizer logic or branch-point rewrites change.

**Files:** `Source/Parsek.Tests/RecordingOptimizer*`, `Source/Parsek.Tests/RecordingStore*`, any integration-style optimizer/tree fixture that exercises `RunOptimizationPass` on a multi-stage tree with branch points.

**Status:** TODO. Medium-priority coverage gap.

---

## 435. Multi-recording Gloops trees (main + debris + crew children, no vessel spawn)

**Source:** world-model conversation on #432 (2026-04-17). The aspirational design for Gloops: when the player records a Gloops flight that stages or EVAs, the capture produces a **tree of ghost-only recordings** — main + debris children + crew children — all flagged `IsGhostOnly`, all grouped under a per-flight Gloops parent in the Recordings Manager, and none of them spawning a real vessel at ghost-end. Structurally the same as the normal Parsek recording tree (decouple → debris background recording, EVA → linked crew child), with the ghost-only flag applied uniformly and the vessel-spawn-at-end path skipped.

**Guiding architectural principle:** per `docs/dev/gloops-recorder-design.md`, Gloops is on track to be extracted as a standalone mod on which Parsek will depend. Parsek's recorder and tree infrastructure will become the base that both Gloops and Parsek share — Gloops exposes the trajectory recorder + playback engine, Parsek layers the career-state / tree / DAG / world-presence envelope on top via the `IPlaybackTrajectory` boundary. Multi-recording Gloops must therefore **reuse Parsek's existing recorder, tree, and BackgroundRecorder infrastructure** rather than growing a parallel Gloops-flavored implementation. The ghost-only distinction is a per-recording flag on top of shared machinery, not a separate code path.

**2026-04-19 boundary note:** `GhostPlaybackEngine.ResolveGhostActivationStartUT` no longer casts back to `Recording`; the engine now resolves activation start from playable payload bounds through `PlaybackTrajectoryBoundsResolver` over `IPlaybackTrajectory`. #435 remains otherwise unchanged, but this leak is no longer part of the extraction risk surface.

**Current state (audited 2026-04-17):**

- `gloopsRecorder` is a **parallel** `FlightRecorder` instance with no `ActiveTree` (`ParsekFlight.cs:7460`) — a temporary workaround that the extraction direction wants to retire.
- `BackgroundRecorder` is never initialized in the Gloops path — only alongside `activeTree` for normal recordings. Staging during a Gloops flight does not produce a debris child.
- `FlightRecorder.HandleVesselSwitchDuringRecording` auto-stops Gloops on any vessel switch (`FlightRecorder.cs:5143-5151`), so EVA does not produce a linked crew child either.
- `RecordingStore.CommitGloopsRecording` accepts a single `Recording`, adds it to the flat `"Gloops - Ghosts Only"` group (`RecordingStore.cs:394-418`). No `CommitGloopsTree`, no nested group structure.
- No conditional `IsGloopsMode` branch inside `RecordingTree`, no half-finished Gloops tree scaffolding.

**Net: Gloops is strictly single-recording by design today**, implemented as a parallel workaround. Multi-recording Gloops is a separate, sizable feature that should also consolidate Gloops onto the shared Parsek recorder (retire the parallel `gloopsRecorder` path).

**Desired behavior:**

- Gloops uses Parsek's main `FlightRecorder` + `RecordingTree` + `BackgroundRecorder` path, with a tree-level `IsGhostOnly` flag propagated to every leaf at commit. No parallel `gloopsRecorder`.
- Starting a Gloops recording creates a `RecordingTree` with the ghost-only flag; normal recording continues alongside on the same machinery if already active, or the tree operates solo if not. How the two modes interleave in the UI (explicit toggle, implicit based on UI state, etc.) is for the implementing PR to decide — possibly in coordination with a UI gate preventing concurrent career + Gloops capture.
- Staging during a Gloops flight → debris gets its own ghost-only recording via the normal `BackgroundRecorder` split path, with `IsGhostOnly = true` inherited from the tree.
- EVA during a Gloops flight → linked child ghost-only recording via the normal EVA split path.
- Commit: the whole Gloops tree flushes as a nested group under `"Gloops - Ghosts Only"` — e.g. `"Gloops - Ghosts Only / Mk3 Airshow Flight"` with child debris / crew recordings under it. Every leaf is `IsGhostOnly`.
- No vessel-spawn-at-end for any recording in a Gloops tree. `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` already gates on `!rec.IsGhostOnly` (see `GhostPlaybackLogic.cs:3001`); the tree case reuses this.
- Per-recording delete / regroup / rename in the Recordings Manager works the same as normal trees.
- Apply-side: #432's filter reads `rec.IsGhostOnly` per-recording, so every leaf in a Gloops tree is already excluded from the ledger with no extra work.

**Files likely to touch (sketch, not exhaustive):**

- `Source/Parsek/ParsekFlight.cs` — retire `gloopsRecorder` in favor of the main `recorder`/`activeTree` path; the "Start Gloops" action creates a tree flagged ghost-only. `CheckGloopsAutoStoppedByVesselSwitch` goes away or is folded into normal tree commit.
- `Source/Parsek/FlightRecorder.cs` — remove `IsGloopsMode` branches once the parallel recorder is retired; the recorder becomes agnostic to career semantics (aligning with the extraction boundary in `gloops-recorder-design.md`).
- `Source/Parsek/BackgroundRecorder.cs` — carry a tree-level ghost-only flag so debris children inherit it.
- `Source/Parsek/RecordingStore.cs` — collapse `CommitGloopsRecording` into the normal tree commit path; the ghost-only distinction is per-tree (or per-leaf, if partial-Gloops trees ever become a thing, which they shouldn't).
- `Source/Parsek/UI/GloopsRecorderUI.cs` — controls now drive the main recorder with a ghost-only flag rather than spinning up a parallel instance.
- `Source/Parsek.Tests/` — tree-structural tests for multi-recording Gloops capture and commit.

**Dependencies / sequencing:**

- Ships after #432 (which closes the existing single-recording leak and establishes the per-recording `IsGhostOnly` apply-side filter that multi-recording Gloops will rely on).
- Coordinates loosely with the Gloops extraction work (`docs/dev/gloops-recorder-design.md` Section 11 — the extraction sequence); ideally this consolidation happens before extraction so the extraction moves a single unified recorder, not two.
- Not tied to the deterministic-timeline correctness cluster — this is a feature extension, not a correctness bug.

**Out of scope:**

- Making Gloops spawn real vessels at ghost-end (explicitly not wanted — Gloops is visual-only).
- Turning the existing single-recording Gloops path into a tree retroactively for existing saves (beta, restart the save if you want the new behavior).
- Actually extracting Gloops into its own mod. That's covered by `docs/dev/gloops-recorder-design.md`'s extraction plan. #435 is a preparatory consolidation step on the Parsek side.

**Priority:** Medium. Feature extension + architectural cleanup. Worth scoping after #432 lands.

**Status:** TODO. Size: L. New feature — not a follow-up to anything shipped today.

---

## 430. "Why is this blocked?" explainer for the committed-action dialog

**Source:** follow-up on the "paradox communication" thread — currently when the player tries to re-research a tech or re-upgrade a facility that's already committed to a future timeline event, `CommittedActionDialog` pops up with a short "Blocked action: X — reason" message. The reason is generic and the player has no way to see *which* committed action is causing the block, or *when* it will play out.

**Desired behavior:**

- Replace the one-line reason with a structured block:
  - The action the player tried (e.g. "Research node: Heavier Rocketry").
  - The committed action that blocks it, including the source recording and its UT (e.g. "Already scheduled at UT 183420 in recording 'Mun Lander 3'").
  - A `Go to Timeline` button that opens the Timeline window and scrolls to the offending entry (reuses `TimelineWindowUI.ScrollToRecording`).
  - A `Revert to launch` shortcut if the player actually wants to undo it (routes to the existing rewind dialog pre-filled with the blocking recording).
- Keep the OK/close path unchanged so existing muscle memory still works.

**Why it matters:**

The mental model of "you can't do this because the timeline already did" is counter-intuitive for a first-time player. Showing the *which* and *when* turns a mysterious block into a debuggable constraint, reinforcing the ledger-as-truth principle every time a block fires.

**Files to touch:**

- `Source/Parsek/CommittedActionDialog.cs` — extend the dialog body; accept an optional `blockingRecordingId` + `blockingUT` + `blockingAction` tuple.
- `Source/Parsek/Patches/*Patch.cs` (where blocks are triggered for tech research / facility upgrade / part purchase) — pass the conflict context into the dialog instead of just the short reason string.
- `Source/Parsek/UI/TimelineWindowUI.cs` — already has `ScrollToRecording`; no changes beyond what's there.

**Out of scope for v1:**

- Auto-resolving the block by rewinding silently; this stays an informational dialog, not a one-click rewind.
- Collapsing multiple overlapping blocks into a summary (each block fires its own dialog as today).

**Status:** TODO. Size: S-M. Best quality-per-effort of the paradox-comms work.

---

## 428. Preview-rewind pane

**Source:** follow-up on the "cost-of-rewind is hard to intuit" thread. Rewind is the most consequential single action in Parsek — it moves the player back to a chosen launch point and replays forward with existing ghosts. But right now the rewind confirmation dialog shows a single summary line ("Rewind to 'Mun Lander 3' at Y1 D23?") and a raw count of "how many future recordings exist". A player can't tell before confirming: which exact recordings will be preserved, which will be replayed, which resources / contracts / milestones will be re-rolled, whether crew reservations will shift.

**Desired behavior:**

- Replace the existing one-line confirmation with a two-pane preview dialog anchored on the rewind button.
- Left pane: **"Before rewind point"** — committed recordings whose `EndUT <= rewindTargetUT` (stay intact on the ledger and their ledger effects remain applied); game-action milestones that already fired before the target; crew reservations that complete before the target.
- Right pane: **"Re-rolled forward"** — committed recordings whose `StartUT > rewindTargetUT` (they stay committed; their resource deltas + events re-apply from the target UT forward as the player plays); milestones pending at UT > target (they'll re-fire); crew reservations spanning the target (stand-in chain resets).
- Each pane shows a count + a preview list of the first ~5 items with `...and N more` if longer.
- Confirm / Cancel buttons unchanged.

**Why it matters:**

Rewind currently feels like a commitment to the unknown — the player isn't sure what they'll lose. Making the consequences legible before the dialog closes reduces regret and teaches the two buckets (before / re-rolled), which is the honest mental model: rewind is deterministic replay, nothing is thrown away.

**Files to touch:**

- `Source/Parsek/UI/RewindConfirmationUI.cs` (new or extension of the existing confirmation helper — current code is inlined in `RecordingsTableUI.ShowRewindConfirmation`).
- A `RewindPreview.Build(recordings, ledgerActions, milestones, rewindTargetUT, liveUT)` pure helper that classifies each item as "before rewind point" or "re-rolled forward". Lives next to `TimelineBuilder` since both walk similar data.
- Tests: classification helper fully covered (happy path + each bucket's edge cases + an item spanning the target UT).

**Out of scope for v1:**

- Previewing the new resource balance after rewind. Just show counts + first few items.
- Undo for rewind. One-way operation stays one-way.

**Status:** TODO. Size: M-L. Biggest UX win per dollar on the rewind mechanic.

---

## 427. Proactive paradox warnings surface

**Source:** follow-up on the conversation after shipping the Career State window. Today the mod prevents paradoxes mostly via blocks (action-blocked dialog) and a single red over-committed warning in the Timeline's resource footer. There's no centralized surface that says "your committed timeline has these N potential issues" — so a player can build up a career with, e.g., a contract that expires before its committed completion, or a facility upgrade requiring a level that won't be reached in time, and only discover the contradiction when it fires (or silently zeroes out).

**Desired behavior:**

- A **Warnings** badge on the main ParsekUI button row — hidden when count is 0, shown as `Warnings (N)` when any warning rules fire.
- Clicking opens a small scrollable window listing each warning as a row:
  - Category tag (`Contract`, `Facility`, `Strategy`, `Resource`, `Crew`).
  - One-line description (`Contract "Rescue Kerbal" deadline UT 240000 is before committed completion at UT 250000`).
  - `Go to ...` button linking to the relevant other window (Timeline scroll, Career State tab, etc.).
- Warnings are computed once per `OnTimelineDataChanged` fan-out (same cache-invalidation channel everything else uses).
- Starter rule set, each as a pure static helper in `WarningRules.cs`:
  - **ContractDeadlineMissed** — active contract's `DeadlineUT < terminal-UT of its committed completion recording`.
  - **FacilityLevelRequirement** — an action requires facility level N but the facility doesn't reach N until after that action's UT.
  - **StrategySlotOverflow** — projected active strategies > projected max slots (currently only warned in log, not UI).
  - **ContractSlotOverflow** — same for contracts.
  - **CrewDoubleBooking** — a stand-in appears in two chains at overlapping UT ranges.
  - **ResourceOverCommit** — already shown in Timeline budget footer, but also listed here for one-stop-shop.

**Why it matters:**

Action blocking catches paradoxes at the moment the player tries to violate them. Warnings catch *latent* contradictions that the ledger can detect but won't error on — the subtle ones where the ledger silently picks a resolution the player didn't intend (e.g. contract gets zeroed out because its deadline passed unexpectedly). Surfacing these early turns the mod's "structural paradox prevention" into a communicated design contract rather than a hidden invariant.

**Files to touch:**

- `Source/Parsek/UI/WarningsWindowUI.cs` — new scrollable list window.
- `Source/Parsek/WarningRules.cs` — new pure-static rule evaluators, one method per rule, each returning `List<Warning>` given `(ledger, recordings, modules)`. Heavy unit-test coverage.
- `Source/Parsek/ParsekUI.cs` — add the badge button + open toggle; integrate with `OnTimelineDataChanged` cache invalidation.
- `Source/Parsek.Tests/WarningRulesTests.cs` — one test per rule (happy + each flag condition).

**Out of scope for v1:**

- Auto-fix for any warning. Pure read-only surface.
- Severity levels / color-coding. All warnings are equal in v1; add severity in a follow-up if there are too many of one kind.
- Per-rule disable toggles. Playtesting can decide which rules feel noisy before we add knobs.

**Status:** TODO. Size: M. Complements the help popup (#426) — where help explains the system, warnings explain *your career's* specific issues. Together they turn the mod from "learn by experimenting" to "learn by seeing the model."

---

## 426. In-window help popups explaining each Parsek system

**Source:** follow-up conversation during the #416 UI polish pass. A player unfamiliar with the mod has to read `docs/user-guide.md` (out of the game) to understand what each window's sections and columns mean. The mechanics are specific enough (slots vs. stand-ins vs. reservations, per-recording fates, timeline tiers, resource budget semantics, etc.) that even tooltips-on-hover don't carry the full picture. An in-game help surface keeps the explanation next to the thing it explains.

**Desired behavior:**

- A small `?` icon button rendered in the title bar (or as the last button in the main toolbar row) of each Parsek window: Recordings, Timeline, Kerbals, Career State, Real Spawn Control, Gloops Flight Recorder, Settings.
- Clicking the `?` opens a small modal-ish popup window titled `Parsek - {Window} Help` anchored next to the parent window.
- The popup body is static help text tailored to that window. For tabbed windows (Kerbals, Career State), the help content should also cover each tab, either as one scrolling document or as a small tab-match sub-structure inside the popup. Keep each section brief (5-15 sentences) — the goal is orientation, not exhaustive docs.
- A "Close" button and `GUI.DragWindow()` so the popup can be moved.
- Help text can be hard-coded string constants in `Source/Parsek/UI/HelpContent/` (one file per window). No runtime load, no localization for v1.
- Suggested starter content:
  - **Recordings** — column-by-column walkthrough, L/R/FF/W/Hide button meanings, group vs chain vs ghost-only distinction.
  - **Timeline** — Overview vs Details tiers, Recordings/Actions/Events source toggles, time-range filter, resource-budget footer, loop toggle semantics on entry rows, GoTo cross-link.
  - **Kerbals** — slots vs stand-ins vs reservations (Roster State tab), chronological outcomes per kerbal (Mission Outcomes tab), outcome-click-scrolls-Timeline.
  - **Career State** — contracts / strategies / facilities / milestones tabs, current-vs-projected columns when the timeline holds pending recordings, Mission Control / Administration slot math.
  - **Real Spawn Control** — what it does (warp-to-vessel-spawn), State column, 500m proximity trigger.
  - **Gloops** — ghost-only manual recording, loop-by-default commit, X delete button in Recordings.
  - **Settings** — group-by-group overview (Recording, Looping, Ghosts, Diagnostics, Recorder Sample Density, Data Management); call out Auto-merge, Auto-launch, Camera cutoff, Show-ghosts-in-Tracking-Station.

**Out of scope for v1:**

- Inline tooltips on every sub-control (hover-tooltips already exist for a few buttons; expanding them is a separate follow-up).
- Localization / translation.
- Interactive tutorials.
- Search within help content.
- External hyperlinks (no browser launch from KSP IMGUI reliably).

**Files to touch:**

- New: `Source/Parsek/UI/HelpWindowUI.cs` (shared small popup window; takes a `windowKey` + body-text source).
- New: `Source/Parsek/UI/HelpContent/*.cs` (one static class per window, each exposes `public const string Body` or a `BuildBody()` method if dynamic content is needed later).
- Each existing window UI file (RecordingsTableUI, TimelineWindowUI, KerbalsWindowUI, CareerStateWindowUI, SpawnControlUI, GloopsRecorderUI, SettingsWindowUI): add a small `?` button and an `IsHelpOpen` toggle that feeds HelpWindowUI.
- `ParsekUI.cs`: add a single shared `HelpWindowUI` field + accessor so every window delegates to the same instance (only one popup open at a time).
- `CHANGELOG.md` entry under Unreleased.
- `docs/user-guide.md` can mention the new `?` buttons briefly but stays as the authoritative long-form reference.

**Status:** TODO. Size: M. Style it the same way as the rest of the mod (shared section headers, dark list box for paragraph groups, pressed toggle idiom if any sub-tabs appear).

---

## 160. Log spam: remaining sources after ComputeTotal removal

After removing ResourceBudget.ComputeTotal logging (52% of output), remaining spam sources:
- GhostVisual HIERARCHY/DIAG dumps (~344 lines per session, rate-limited per-key but burst on build)
- GhostVisual per-part cloning details (~370 lines)
- Flight "applied heat level Cold" (46 lines, logs no-change steady state)
- RecordingStore SerializeTrackSections per-recording verbose (184 lines)
- KSCSpawn "Spawn not needed" at INFO level (54 lines)
- BgRecorder CheckpointAllVessels checkpointed=0 at INFO (15 lines)

2026-04-25 update: deferred spawn queue outside-physics-bubble waits are no longer
a spam source; the per-recording kept line and repeated warp-ended summary were
replaced with a rate-limited queue wait summary.

2026-04-25 update (UnfinishedFlights + missed-vessel-switch):
`logs/2026-04-25_1314_marker-validator-fix/KSP.log` was 96 MB / 540k lines, of
which ~511k (94%) were `[Parsek][VERBOSE][UnfinishedFlights]
IsUnfinishedFlight=…` decisions and ~1k were `[Parsek][WARN][Flight] Update:
recovering missed vessel switch` lines. Both fired from per-frame paths:
`EffectiveState.IsUnfinishedFlight` is invoked once per recording per frame from
`RecordingsTableUI` row drawing, `UnfinishedFlightsGroup` membership filtering,
and `TimelineBuilder`; the missed-vessel-switch warn fires in `ParsekFlight`
`Update()` until the recovery handler clears the predicate, which in this
playtest took dozens to hundreds of frames per vessel. Each of the 7 return
paths in `IsUnfinishedFlight` now uses `ParsekLog.VerboseRateLimited` keyed by
`{reason}-{recordingId}` so each (recording, reason) pair logs once per
rate-limit window. The missed-vessel-switch warn now uses
`ParsekLog.WarnRateLimited` keyed by `missed-vessel-switch-{activeVesselPid}`
so each vessel logs at most once per window. Regression
`EffectiveStateTests.IsUnfinishedFlight_RepeatedCallsSameRec_RateLimitedToOneLine`
calls the predicate 100x with the same recording and asserts a single emitted
line.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

**Status:** Open

---

## TODO — Release & Distribution

### T3. CKAN metadata

Create a `.netkan` file or submit to CKAN indexer so users can install Parsek via CKAN. Requires a stable release URL pattern.

**Priority:** Nice-to-have

---

## TODO — Performance & Optimization

### T61. Continue Phase 11.5 recording storage shrink work

The first five storage slices are in place: representative fixture coverage, `v1` section-authoritative `.prec` sidecars, alias-mode ghost snapshot dedupe, header-dispatched binary `v2` `.prec` sidecars, exact sparse `v3` defaults for stable per-point body/career fields, and lossless header-dispatched `Deflate` compression for `_vessel.craft` / `_ghost.craft` snapshot sidecars with legacy-text fallback. Current builds also keep a default-on readable `.txt` mirror path for `.prec` / `_vessel.craft` / `_ghost.craft` so binary-comparison debugging can happen without unpacking the authoritative files first.

Remaining high-value work should stay measurement-gated and follow `docs/dev/done/plans/phase-11-5-recording-storage-optimization.md`:

- any further snapshot-side work now has to clear a higher bar: `.prec` and `_ghost.craft` are already roughly equal buckets after compression, and `_vessel.craft` is small, so "focus on snapshots next" only applies if a future corpus shifts the split back toward snapshots
- keep the readable mirror path strictly diagnostic: authoritative load/save stays on `.prec` / `.craft`, mirror failures stay non-fatal, and stale mirrors should continue to reconcile cleanly on flag changes
- only pursue intra-save snapshot dedupe or any custom binary snapshot schema if a future rebaseline against a larger / more vessel-heavy corpus shows a meaningful measured win
- additional sparse payload work only where exact reconstruction and real byte wins are proven
- post-commit, error-bounded trajectory thinning only after the format wins are re-measured
- snapshot-only hydration salvage must keep the loaded disk trajectory authoritative; if pending-tree data is used to heal bad snapshot sidecars, it should restore only snapshot state, not overwrite trajectory/timing with future in-memory data
- out-of-band `incrementEpoch=false` sidecar writes still rely on the existing `.sfs` epoch and staged per-file replacement; if we ever need crash-proof mixed-generation detection there, add a sidecar-set commit marker/manifest instead of pretending the current epoch gate can prove it
- any further snapshot-side work should preserve current alias semantics, keep the missing-only ghost fallback contract, keep partial-write rollback safety intact, and stay covered by sidecar/load diagnostics

**Priority:** Current Phase 11.5 follow-on work — measurement-gated guidance for future shrink work rather than active tasks

---

## TODO — Ghost Visuals

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
