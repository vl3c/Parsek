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

## ~~571. Map View ghost icons show weird trajectories that do not match the recorded path~~

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
- Latent guard fixed with Part A: the tracking-station refresh path now checks
  `FindOrbitSegmentForMapDisplay` for `HasValue` before reading `seg.Value`, so
  a mid-frame missing segment retires the ghost instead of throwing.

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

**Resolution (2026-04-25):** Closed by the predicted-tail partial-prefix fix
from PR #542 plus Part A recorder-side checkpoint densification. Long
`OrbitalCheckpoint` sections now keep the `OrbitSegment` as the Keplerian source
of truth, but add section-local trajectory points at 5 degrees of true anomaly
(minimum window 600s, max 360 points, endpoints included); the representative
`UT=171496.6-193774.6` Kerbin checkpoint adds 42 points and short 300s windows
add none. Format-v6 `.prec` sidecars preserve those checkpoint frames, the
optimizer trims them with the section instead of dropping them as noise, playback
logs the checkpoint point and resolved world position, and map-source decisions
now log `Segment` / `TerminalOrbit` / `StateVector` / `None` source detail in one
structured line.

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

**Fix:** introduce scoped `Recording.SpawnSuppressedByRewind` metadata, persisted in tree mutable state as `spawnSuppressedByRewind`, `spawnSuppressedByRewindReason`, and `spawnSuppressedByRewindUT`. `ParsekScenario.HandleRewindOnLoad` calls `MarkRewoundTreeRecordingsAsGhostOnly` after `ResetAllPlaybackState`, but the helper now applies the marker only to the active/source recording (`reason=same-recording`) or a same-source recording whose UT range overlaps the rewind target. Same-tree future recordings are logged as `reason=same-tree-future-recording` and intentionally left spawn-eligible, so #573 no longer turns an entire tree into ghost-only history (#589). `GhostPlaybackLogic.ShouldSpawnAtRecordingEnd` still treats `same-recording` as an absolute #573 duplicate-source block, but consumes/clears legacy unscoped markers before continuing through normal spawn-at-end gates. `RecordingStore.ResetRecordingPlaybackFields` clears the marker and metadata so repeated rewinds start clean. Regression coverage in `RewindTimelineTests` and `RewindSpawnSuppressionTests`: same-recording #573 suppression, future same-tree spawn eligibility at `endUT`, legacy marker consumption, repeated-rewind stale-marker clearing, reset lifecycle logging, and log assertions for applied/skipped/cleared decisions.

The `RewindContext.IsRewinding` short-circuit in `RunSpawnDeathChecks` from `c9d257f8` is retained as defense-in-depth (with corrected wording in code + tests calling out that it does NOT cover the production sequence), so a future regression that splits the rewind sequence across update ticks can't trip the spawn-death detector.

**Out of scope (separate follow-up):** the diagnosis pass also flagged
`VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay` (`Source/Parsek/VesselSpawner.cs:99-141`) as deserving a sanity audit — its #226 replay/revert duplicate-spawn exception bypasses the adoption guard whenever the source PID matches the scene-entry active vessel, which expanded scope beyond the booster-respawn intent during this playtest. Not changed in this PR; track as a separate concern.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~574. Extrapolator: 146 sub-surface state rejections classified as Destroyed for the same recordings~~

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

**Diagnosis (2026-04-25):** the normal finalisation-cache producer path was
recording-state harmless after the first Destroyed terminal because the cache
appliers reject already-finalized recordings, but it still rebuilt a failed
finalization cache and re-emitted the NullSolver/sub-surface logs every 5s.
The lower-level `IncompleteBallisticSceneExitFinalizer.TryApply` path was not
strictly idempotent if invoked directly: a second call could re-run the
delegate and overwrite terminal UT/orbit fields. No downstream ERS,
GhostMapPresence, timeline, or Re-Fly merge path needed the repeated
classification once `TerminalState=Destroyed` was already known.

**Fix:** already-Destroyed recordings now short-circuit before the live-orbit
fallback/default ballistic finalizer. The live-orbit origin-adjacent
`NullSolver` fallback remains a trusted first-time Destroyed signal; the first
sub-surface transition logs the recording id, terminalUT, body, altitude, and
threshold once, while later cache refreshes emit a per-recording
`VerboseRateLimited` skip diagnostic and an INFO refresh summary with
`recordingsExamined`, `alreadyClassified`, and `newlyClassified`.

**Status:** CLOSED 2026-04-25. Fixed for v0.8.3.

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

## ~~576. PatchedSnapshot: 146 "solver unavailable" warnings clustered on a few vessels~~

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

**Diagnosis (2026-04-25):** `IPatchedConicSnapshotSource.IsAvailable` is
`vessel != null && vessel.patchedConicSolver != null && vessel.orbit != null`
(`Source/Parsek/PatchedConicSnapshot.cs:295-297`). In stock KSP,
`Vessel.patchedConicSolver` is null **by design** for any vessel whose flight
controller does not own a piloted/probe solver: `VesselType.Debris` (no command
module — 77/77 of the `Kerbal X Debris` hits), EVA kerbals (jetpack motion
system, no solver — 45+12 hits across `Ermore Kerman` / `Magdo Kerman`), and
probe-debris that has lost its active-vessel solver state (11/11 of the
`Kerbal X Probe` hits). Only the lone `Kerbal X` hit at 13:07:48 was the
"genuine transient" case the original WARN tier was designed for. The downstream
`IncompleteBallisticSceneExitFinalizer` (`...:280-286`) explicitly documents
that NullSolver is the destroyed-vessel / no-solver-by-design fingerprint that
drives the live-orbit fallback — the WARN tier was correct as a fallback
signal but wrong for log noise of this shape.

**Fix:** swap both paired warns from `Warn` to `WarnRateLimited` with
distinguishing keys — `solver-unavailable-{vesselName}` for the
`PatchedConicSnapshot` site, and `finalize-snapshot-failed-{recordingId}-{failureReason}`
for the paired `IncompleteBallisticSceneExitFinalizer` site. The `FailureReason
= NullSolver` flow downstream is unchanged: only the level routing through the
30-second-window rate limiter changes. The first hit per key still emits at
WARN level so a fresh regression on a piloted craft mid-flight surfaces
immediately; subsequent hits within 30 s on the same key are absorbed into a
single line per window with a `suppressed=N` suffix.

Regression `PatchedConicSnapshotTests.Snapshot_NullSolver_RateLimitsRepeatsForSameVessel`
pins the per-vessel keying, the cross-vessel independence, and the
30-second-window expiry suffix. Regression
`SceneExitFinalizationIntegrationTests.TryCompleteFinalizationFromPatchedSnapshot_NullSolver_WarnRateLimitedPerRecordingAndReason`
pins the paired Extrapolator rate-limit. The pre-existing
`Snapshot_NullSolver_ReturnsEmptyList` test still pins the WARN level
(`[Parsek][WARN][PatchedSnapshot]`) of the FIRST hit per key.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

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

## ~~581. Diagnostics: 2 frame-budget breaches during normal flight playback~~

**Source:** `logs/2026-04-25_1314_marker-validator-fix/KSP.log.cleaned`:

- 2× `[Parsek][WARN][Diagnostics] Playback frame budget exceeded: <F>ms (<N> ghosts, warp: <N>x)`
- 1× `[Parsek][WARN][Diagnostics] Recording frame exceeded budget: <F>ms for vessel "Kerbal X"`
- 1× `[Parsek][WARN][Diagnostics] Playback budget breakdown (one-shot, first exceeded frame): total=<F>ms mainLoop=<F>ms spawn=<F>ms (built=<N> throttled=<N> max=<F>ms) destroy=<F>ms explosionCleanup=<F>ms deferredCreated=<F>ms (<N> evts) deferredCompleted=<F>ms (<N> evts) observabilityCapture=<F>ms trajectories=<N> ghosts=<N> warp=<N>x`

Each breach is a single frame; the budget breakdown one-shot fires for the
first exceeded frame to capture per-bucket cost. Two breaches in an hour-long
session is low frequency, but worth checking which bucket dominates the
breakdown.

**Diagnosis (2026-04-25):** the captured log has exactly one breakdown line
(`grep "budget breakdown" KSP.log.cleaned` → 1 hit, line 517040): `total=11.6ms
mainLoop=7.51ms spawn=3.44ms (built=1 throttled=0 max=3.44ms) destroy=0.00ms
explosionCleanup=0.00ms deferredCreated=0.28ms (1 evts) deferredCompleted=0.00ms
(0 evts) observabilityCapture=0.39ms trajectories=18 ghosts=0 warp=1x`. This is
a hybrid spike: partly mainLoop (7.51 ms / 65 % of frame) + partly a single
non-trivial spawn (3.44 ms / 30 %). It falls in a diagnostic gap between the
existing #450 (gate: `spawnMaxMicroseconds >= 15 ms`) and #460 (gate:
`mainLoop >= 10 ms` AND `spawn < 1 ms`) sub-breakdown latches: heaviest spawn
under #450's threshold AND mainLoop under #460's floor. `grep "mainLoop
breakdown\|spawn build breakdown" KSP.log.cleaned` confirms 0 hits — the session
captured the generic #414 breakdown but no Phase-B attribution.

The frequency itself (2 playback breaches in an hour-long session, plus 1
recording breach) is well inside expected Unity-frame jitter and does not
constitute a regression. The functional fix is therefore **none**: the budget
itself stays at 8 ms, the existing latches are unchanged.

**Fix:** add a fourth one-shot latch — "Playback hybrid breakdown" — that
fires when total > budget AND `spawnMaxMicroseconds <
BuildBreakdownMinHeaviestSpawnMicroseconds` AND `mainLoopMicroseconds <
MainLoopBreakdownMinMainLoopMicroseconds`. Reuses the existing
`PlaybackBudgetPhases` field set; adds mainLoop / spawn percent-of-frame
fractions so a future hybrid breach reports a Phase-B-actionable per-bucket
itemisation rather than just the generic #414 breakdown that the gap-shaped
spike in the captured log received.

The new latch is independent of #414 / #450 / #460 (matches the precedent set
by #460 itself): a session that already burned the prior three latches on
bigger spikes can still capture the next gap-shaped breach. Test seam
`SetBug460BreakdownLatchFiredForTesting` lets the independence pair-test
between #460 and #581 land without reaching for `ResetForTesting`.

Regression `Bug581HybridBreakdownTests` (8 cases: captured-log-shape format,
one-shot latch, two negative-gate cases asserting the latch declines on
#450-shape and #460-shape spikes, two latch-independence cases for #450 and
#460, total-below-budget defensive case, zero-total degenerate-case
non-throwing).

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0 — observability-only, no
functional change to the budget itself.

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

## ~~583. Map-view state-vector ghost creation still skips when activation first lands inside a Relative-frame section~~

**Source:** PR #547 review follow-up — out-of-scope note attached to the
P1 fix that landed in commit `57aec636` on
`fix/ghostmap-state-vector-relative-frame` (now merged into main as
`8eaebfbb`). Distinct from PR #547's P1 review item ("flight-scene update
path thresholds dz as altitude") which covered the *"ghost already
exists, then enters Relative section"* case.

**Concern:** the create / pending-create resolver path still treats a
Relative-frame current UT as "no map-visible source" and returns
`TrackingStationGhostSource.None`. After the PR #547 P1 follow-up, an
existing map ghost survives a Relative section and stays attached to its
anchor through `UpdateGhostOrbitFromStateVectors`'s Relative branch. But
the symmetric "no ghost exists yet, and the first map-visible source is
inside a Relative section" case still produces missing map presence —
the resolver never picks `StateVector` for a Relative-frame point, so
`CreateGhostVesselFromStateVectors` (which already has a working
Relative branch since PR #547) never gets called for that path.

This is a missing-ghost defect, not a wrong-position one — the #584 fix
(merged via PR #547) already prevents the icon-deep-inside-planet
outcome for ghosts that get created. Likeliest player-visible scenario:
a docking / rendezvous recording whose first map-visible UT is inside
the docking-relative section never gets a map vessel until the
trajectory crosses out of the Relative section (e.g., undock + re-enter
Absolute or OrbitalCheckpoint frame).

**Files to investigate:**

- `Source/Parsek/GhostMapPresence.cs` — `ResolveMapPresenceGhostSource`
  (line 1619). Specifically the `if (!traj.HasOrbitSegments)` branch
  around line 1741 that gates state-vector resolution: a Relative-frame
  current UT short-circuits there because the trajectory may have
  OrbitalCheckpoint segments elsewhere. Decide whether to: (a) extend
  the state-vector branch to fire when the current UT is in a Relative
  section regardless of `HasOrbitSegments`, gated on anchor-resolvability;
  or (b) introduce a new `TrackingStationGhostSource.Relative` source
  kind that flows through to `CreateGhostVesselFromStateVectors`'s
  existing Relative branch.
- `Source/Parsek/ParsekPlaybackPolicy.cs:795-870` — `CheckPendingMapVessels`
  pending-create flow. Whatever the resolver returns must dispatch
  cleanly through `CreateGhostVesselFromSource`.
- `Source/Parsek/GhostMapPresence.cs:2861` —
  `CreateGhostVesselFromStateVectors`. Already dispatches on
  `referenceFrame` (#584). Verify it handles the "anchor unresolvable
  at create-time" case sensibly (defer? skip with VERBOSE? warn?).

**Design questions to answer in the implementing PR:**

- When the anchor vessel for a Relative-frame point is not resolvable
  at create-time (e.g., anchor not yet in `FlightGlobals.Vessels`), what
  should happen? Re-defer until the next tick? Skip silently? Skip with
  WARN? Fall back to terminal-orbit if available?
- If the recording is mid-section and the anchor disappears mid-flight
  (vessel destroyed), should the existing ghost stay parked at last
  known position, or be removed? Today
  `UpdateGhostOrbitFromStateVectors`'s Relative branch presumably
  already handles this — the create-side decision needs to match.

**Coordination note:** PR #547 (which closed #584) is already merged
into `main`. The two `CHANGELOG.md` lines under v0.9.0 Bug Fixes that
prefix `#584` describe the existing-ghost path: *"a ghost that
traverses a Relative-frame docking/rendezvous segment stays attached
to its anchor vessel"* and *"a ghost in a docking/rendezvous Relative
section is no longer wrongly removed and re-deferred"*. Both are
existing-ghost claims, not "all map creation" claims, so neither
overclaims relative to the resolver gap left here. When the
implementing PR for #583 lands, add a sibling `#583` line under v0.9.0
Bug Fixes naming the creation-side fix.

**Status:** ~~Open~~ Fixed.

**Fix:** `GhostMapPresence.ResolveMapPresenceGhostSource` now considers
state-vector resolution when the current UT lies inside a Relative-frame
section even if the trajectory has `OrbitSegments` elsewhere (the gate
widens from `!HasOrbitSegments` to `!HasOrbitSegments || IsInRelativeFrame`).
`TryResolveStateVectorMapPoint` was rewritten as a pure helper
(`TryResolveStateVectorMapPointPure`) that takes a `Func<uint,bool>`
anchor-resolvability lookup, so xUnit can exercise both branches without
KSP's `FlightGlobals`. In the Relative branch the helper bypasses the
`ShouldCreateStateVectorOrbit` altitude/speed threshold (mirroring the
PR #547 P1 update-path gate, since `point.altitude` is the anchor-local
dz offset, not geographic altitude) and gates creation on
`FlightRecorder.FindVesselByPid(anchorVesselId) != null`. When the
anchor isn't yet loaded, the resolver returns `None` with a new
dedicated skip reason `relative-anchor-unresolved`; the existing
`pendingMapVessels` retry loop in `ParsekPlaybackPolicy.CheckPendingMapVessels`
re-resolves on the next tick. Sections without an anchor id keep the
legacy `relative-frame` skip wording so that subset is observably
distinct from "anchor present but not yet resolvable". Five regression
tests (`ResolveMapPresenceGhostSource_RelativeFrame_AnchorResolvable_*`,
`_AnchorUnresolvable_*`, `_NoAnchorId_*`, `_DzBelowAltitudeThreshold_*`,
`_WithOrbitSegmentsElsewhere_*`) pin the new contract.

PR #556 review follow-up (P2): `RefreshTrackingStationGhosts` used to
expire any state-vector ghost whose currentUT was inside a Relative
section (`if (!pt.HasValue || IsInRelativeFrame(rec, currentUT))` →
`tracking-station-state-vector-expired`). After widening the resolver,
that path tore the ghost down every refresh tick while the create path
re-added it next tick — flicker. The refresh path now mirrors the
flight-scene gate in `ParsekPlaybackPolicy.CheckPendingMapVessels`:
remove on `!pt.HasValue` only, then skip the
`ShouldRemoveStateVectorOrbit` threshold for Relative-frame points and
hand off to `UpdateGhostOrbitFromStateVectors` (which already dispatches
on `referenceFrame`). Two more tripwires
(`TrackingStationRefresh_RelativeFrameStateVector_WouldTripRemovalWithoutGate`
and `_AbsoluteFrameStateVector_StillEvaluatesThreshold`) document the
joint precondition the gate suppresses, mirroring the existing
flight-scene `RuntimePolicyTests.RelativeFrameGuard_*` pair.

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

## ~~585. In-place continuation Re-Fly leaves the active tree in Limbo, the booster recording un-merged, and the merge dialog shows the recording as 0s~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — playtest where the
user re-flew the `Kerbal X Probe` booster (recording
`01384be4319544aebbc7b4a3e0fdd45c`) via the rewind dialog, flew it for ~7
minutes, then went back to the Space Center. User-visible symptoms:

- During the re-fly the recording did not appear in map view.
- The merge confirmation dialog at scene exit "said the recording was 0s".
- After clicking Merge, the booster recording never appeared in map view —
  user flagged this as game-breaking ("the recording of the flight to orbit
  of the Re-Fly booster did not appear in map view, I guess it was not
  merged").

**Diagnosis (2026-04-25):** the in-place continuation path in
`RewindInvoker` and `MarkerValidator` (#577 / #514 follow-ups) handles the
`ReFlySessionMarker` correctly — `AtomicMarkerWrite: in-place continuation
detected — marker → origin 01384be4319544aebbc7b4a3e0fdd45c (no
placeholder created)` at 19:12:24.265 is the expected log line. But the
post-load tree restore never completes and leaves the entire active tree
in Limbo for the rest of the session:

1. Rewind quicksave loads at 19:12:23.118.
   `RecordingStore.RestoreActiveTreeNode` walks all 13 tree recordings,
   the sidecar load fails twice with `Sidecar epoch mismatch for
   5294a8d9c77a4c289bcb5b0a944437e6: .sfs expects epoch 2, .prec has epoch
   6 — sidecar is stale (bug #270), skipping sidecar load (trajectory +
   snapshots)` and the same shape for `01384be4` (.sfs epoch=1, .prec
   epoch=4). The mitigation for #270 protects against corruption but
   leaves the active recording (`5294a8d9` "Kerbal X" capsule) and the
   in-place continuation target (`01384be4` "Kerbal X Probe" booster)
   in memory with empty trajectories and no snapshots.
2. `Stashed pending tree 'Kerbal X' (8 recordings, state=Limbo)` —
   the tree drops from 13 recordings to 8 in the stash and goes to
   `MergeState.Limbo` because of the `2 sidecar hydration failure(s)`
   noted on the same line.
3. `RestoreActiveTreeFromPending: waiting for vessel 'Kerbal X'
   (pid=2708531065) to load (activeRecId=5294a8d9c77a4c289bcb5b0a944437e6)`
   at 19:12:24.534. The expected vessel is the pre-rewind active vessel
   (the capsule that was on the Mun, pid=2708531065). After the strip,
   that vessel is gone — the live active vessel is the booster
   (pid=3474243253, name="Kerbal X Probe"), which is what the player
   wants to fly.
4. `RestoreActiveTreeFromPending: vessel 'Kerbal X' (and no EVA parent
   fallback) not active within 3s — leaving tree in Limbo` at
   19:12:27.525. The 3s coroutine times out without binding the live
   recorder to the in-place continuation recording.
5. The recorder, looking for a vessel to track, fires
   `Post-switch auto-record armed: vessel='Kerbal X Probe' pid=3474243253
   tracked=False reason=vessel switch to outsider while idle` at
   19:12:24.264 — it treats the booster as an "outsider", not as the
   active recording continued in place.

The downstream effect is that `01384be4` is never re-attached to the
recorder. Throughout the 7-minute booster flight no new trajectory
points or snapshot land in `01384be4`, and on scene exit the second
merge dialog at 19:19:39.944 reads:

```
BuildDefaultVesselDecisions: leaf='01384be4319544aebbc7b4a3e0fdd45c'
  vessel='Kerbal X Probe' terminal=null hasSnapshot=False canPersist=False
BuildDefaultVesselDecisions: active-nonleaf='5294a8d9c77a4c289bcb5b0a944437e6'
  vessel='Kerbal X' terminal=null hasSnapshot=False canPersist=False
Tree merge dialog: tree='Kerbal X', recordings=8, spawnable=0
```

`terminal=null hasSnapshot=False` is what the dialog renders as a 0s
duration; `spawnable=0` is why the merged recording produces no real
vessel and no trajectory lines in map view post-merge. The supersede
finalize log even confirms it took the in-place branch:
`TryCommitReFlySupersede: in-place continuation detected (provisional ==
origin == 01384be4319544aebbc7b4a3e0fdd45c); skipping supersede merge ...
and finalizing continuation`.

This is the same family as #21 ("Re-Fly session marker silently wiped
... when the active recording was a previously-promoted Unfinished
Flight") but on a different axis: #21 patched the
`MarkerValidator.MergeState` gate; this one is downstream — the marker
survives, but the tree-restore coroutine still keys on the old active
vessel name and drops the tree to Limbo when the rewind made that
vessel unreachable.

**Files to investigate:**

- `Source/Parsek/ParsekFlight.cs` — `RestoreActiveTreeFromPending`
  vessel-name match logic. For an in-place continuation Re-Fly the
  expected active vessel must be the marker's `ActiveReFlyRecordingId`
  vessel (the booster), not the tree's pre-rewind `activeRecId` vessel
  (the capsule). Probably needs an in-place-continuation carve-out that
  consults the live `ReFlySessionMarker`.
- `Source/Parsek/ParsekScenario.cs` — the tree-stash path that emits
  `stashed active tree 'Kerbal X' (8 recording(s), activeRecId=...) into
  pending-Limbo slot ... with 2 sidecar hydration failure(s)`. Decide
  whether sidecar hydration failure on the ACTIVE recording during a
  rewind quicksave load should still bind the recorder, or whether the
  Limbo stash itself should be expressed as "needs merge dialog before
  next flight".
- `Source/Parsek/RecordingStore.cs` — sidecar epoch mismatch
  short-circuit for `5294a8d9` and `01384be4`. The `.prec` was written
  with `epoch=6` after the original mission, the rewind quicksave's
  `.sfs` has `epoch=2`, mitigation drops the trajectory load. Needed:
  reconcile what the rewind quicksave should restore vs. what the
  on-disk `.prec` already encodes for an in-place continuation
  origin (drop trajectory back to the pre-rewind `epoch=2`? rebuild
  from `.prec` post-rewind UT? something else?).
- `Source/Parsek/FlightRecorder.cs` — `Post-switch auto-record armed:
  ... reason=vessel switch to outsider while idle` is the recorder's
  fallback when the tree is Limbo. For the in-place continuation case,
  the recorder should resume into `01384be4` instead of treating the
  booster as a fresh outsider.
- `Source/Parsek/MergeDialog.cs` —
  `BuildDefaultVesselDecisions` emitting `hasSnapshot=False
  canPersist=False` for the in-place continuation recording. Once the
  underlying restore is fixed the dialog should render real duration +
  spawnable count.

**Resolution (2026-04-26):** Fixed in `fix/585-inplace-continuation-limbo`
by teaching `RestoreActiveTreeFromPending` to consult the live
`ReFlySessionMarker`. When the marker is in-place continuation
(`OriginChildRecordingId == ActiveReFlyRecordingId`) and its recording
id is present in the freshly-popped tree, the coroutine swaps the
wait target to the marker's recording id, vessel name, and pid before
the 3s wait loop. The pure-static decision lives in
`ReFlySessionMarker.ResolveInPlaceContinuationTarget`. A companion
gate in `ParsekFlight.OnVesselSwitchComplete`
(`IsInPlaceContinuationArrivalForMarker`) suppresses the misleading
"vessel switch to outsider while idle" arming for the same in-place
case, so the post-switch watcher cannot race the restore coroutine.
The sidecar epoch mismatch error surface is unchanged: bug #270's
mitigation still drops the trajectory for stale sidecars, the empty
trajectory list is the resumed recording's expected pre-rewind shape,
and the recorder repopulates it on the first frame after binding;
the existing `StashActiveTreeAsPendingLimbo` null-snapshot recapture
path covers `hasSnapshot=False` at scene exit. Design note in
`docs/dev/plans/refly-inplace-continuation-tree-restore.md` (closes
#590) lays out the three contract questions and the
deferred-snapshot-only-rescue follow-up. Tests:
`Bug585InPlaceContinuationRestoreTests` (15 cases covering marker
absence, placeholder pattern, tree-id mismatch, missing-from-tree,
already-pointing-at-marker, pid-match-vs-pid-mismatch, post-fix
merge dialog rendering with `canPersist=True`).

**Review follow-ups (PR #558):** P1 review caught that the async-FLIGHT-load
path schedules the restore coroutine before `RewindInvoker.RunStripActivateMarker`
gets a chance to run `AtomicMarkerWrite` -- both are deferred to
`onFlightReady` and can race. Fixed by gating the marker read on
`RewindInvokeContext.Pending`: the restore coroutine yields until the
context clears (or 300 frames timeout) before reading
`ActiveReFlySessionMarker`, so the marker is guaranteed to be written
before the swap decision. P2 review caught that the post-swap
`tree.BackgroundMap` still contained the newly active recording, so
`EnsureBackgroundRecorderAttached` would seed the background recorder
from a map that listed the live recording as both active and background.
The swap branch now calls `tree.RebuildBackgroundMap()` after mutating
`ActiveRecordingId`, which re-runs `IsBackgroundMapEligible` against
the swapped value and excludes it from the map. Two new tests in
`Bug585InPlaceContinuationRestoreTests`
(`RebuildBackgroundMap_AfterSwapping_ActiveRecordingId_ExcludesSwappedTarget`,
`RebuildBackgroundMap_DestroyedRecording_NotInBackgroundMap`) pin the
post-swap rebuild contract.

**Status:** CLOSED 2026-04-26.

---

## 586. Ghost map vessel "Set Target" via icon click logs success but does nothing in KSP ~~done~~

**Source:** same playtest as #585. User: "when controlling the booster, I
tried to click on Set Target on the ghost proto-vessel (the upper stage
heading to the Mun), but the button did not work."

**Suspected supporting evidence in KSP.log:**

- 19:13:25.634 `[Parsek][INFO][GhostMap] Ghost 'Ghost: Kerbal X' set as
  target via icon click`
- 19:13:27.772 same line again — user clicked twice expecting a visible
  effect
- 19:13:30.213 `[Parsek][INFO][GhostMap] Ghost 'Ghost: Kerbal X' focused
  via menu (recIndex=1)` — user fell back to the focus menu

Parsek logs the click as if it succeeded, but the user-observable
behaviour (target marker, distance / velocity readouts on the navball,
encounter-prediction line to the ghost) never materialises.

**Root cause confirmed:**

- KSP did accept Parsek's `FlightGlobals.fetch.SetVesselTarget(...)`
  call, but Parsek had populated the ghost vessel `OrbitDriver` as if
  it were a body driver: `orbitDriver.celestialBody = Kerbin`. Stock
  `OrbitTargeter.DropInvalidTargets()` treats any target driver with a
  `celestialBody` equal to the active vessel's reference body as "the
  current main body" and clears the target. Normal vessel targets keep
  identity in `OrbitDriver.vessel` and leave `OrbitDriver.celestialBody`
  null.
- Fixed by normalizing ghost orbit-driver target identity after
  ProtoVessel load and every ghost orbit update: `OrbitDriver.vessel`
  points at the ghost vessel, `OrbitDriver.celestialBody` stays null,
  and the reference body remains on the `Orbit`.
- The Set Target menu paths now capture target state before and
  immediately after `SetVesselTarget`, then log success only after a
  delayed KSP-validation check confirms `FlightGlobals.fetch.VesselTarget`
  still resolves to the ghost vessel. Rejections log a warning with
  the final reason (`null`, current-main-body, parent-body, wrong
  vessel, wrong object) plus target type/name/body, active vessel
  `targetObject`, ghost `MapObject`, orbit-driver identity, and
  `FlightGlobals.Vessels` registration state.

**Files to investigate:**

- `Source/Parsek/GhostMapPresence.cs` — search for "set as target via
  icon click" to find the click handler. Verify the `SetVesselTarget`
  invocation actually runs and what KSP returns.
- The ghost vessel construction path in `BuildAndLoadGhostProtoVessel`
  / `CreateGhostVesselFromSegment` — confirm the resulting `Vessel`
  is a valid KSP target (correct `vesselType`, has a `MapObject`,
  has an `OrbitDriver`, is registered with `FlightGlobals`).

**Status:** Closed. Tests: `GhostMapTargetingTests` pins the verified
success/failure logging contract, and
`GhostMapVesselTargeting_SyntheticSameBodyGhost_Sticks` is an in-game
runtime canary for production `SetGhostMapNavigationTarget` acceptance
on a synthetic same-body ghost after stock validation frames.

---

## ~~587. KSP shows a phantom "Kerbin Encounter T+" prediction and limits warp to 50× during booster Re-Fly~~

**Source:** same playtest as #585. User: "the kerbalx probe booster
(when I flew the real booster after Re-Fly, in map view) had sections
when it glitched out — orbit disappeared, message in map icon saying
'Kerbin Encounter T+' (wrong), time warp limited to 50x."

50× warp limit + a flagged encounter is the KSP-stock behaviour the
patched-conic solver triggers when it predicts an SOI transition for
the active vessel within the warp horizon. For the booster on a normal
sub-orbital / orbital flight there should be no Kerbin encounter at all.

**Suspected supporting evidence in KSP.log:**

- 19:12:24.275 `[Parsek][WARN][Rewind] Strip left 1 pre-existing
  vessel(s) whose name matches a tree recording: [Kerbal X Debris] —
  not related to the re-fly, will appear as second Kerbal X-shaped
  object in scene` — the strip explicitly leaves a leftover
  `Kerbal X Debris` vessel in the scene whose orbit is independent of
  the re-fly. Stock patched conics walks every nearby vessel's orbit
  to find encounters, and a low-altitude leftover debris on a
  near-identical orbit can trip the encounter solver.
- 19:14:37.328 `[Parsek][WARN][Diagnostics] Playback frame budget
  exceeded: 9.3ms (1 ghosts, warp: 50x) | suppressed=2` — confirms
  warp was being held at 50×.

**Files to investigate:**

- `Source/Parsek/RewindInvoker.cs` /
  `Source/Parsek/Patches/...` — the strip pass that decides what to
  remove pre-rewind. The post-strip warning lists the leftover by
  name match; for in-place continuation, debris from the prior
  flight that pre-dates the rewind UT (UT=160 here) should be
  stripped, not "left alone" because it shares a tree-recording name.
- The encounter prediction itself is KSP-stock and not directly
  controllable — the fix is to remove the cause (the leftover debris).
  Once #585 is fixed, the strip pass needs to handle this corner.
- Cross-check with #573's strip-kill protection logic — that fix made
  the strip not kill the upper-stage ghost during re-fly, but did not
  rule on residual debris.

**Resolution (2026-04-26):** Fixed in `fix/585-inplace-continuation-limbo`
by adding a strip-pass supplement
(`RewindInvoker.StripPreExistingDebrisForInPlaceContinuation`) that
runs after `AtomicMarkerWrite`. For an in-place continuation
re-fly, leftover debris vessels carried in the rewind quicksave's
protoVessels (e.g., the playtest's three pre-existing
`Kerbal X Debris` instances at pids 3749279177 / 2427828411 /
526847698) get killed via `Vessel.Die()` inside a
`SuppressionGuard.Crew()` when (a) the vessel name matches a
Destroyed-terminal recording in the marker's tree and (b) the pid
is NOT in the protected set (selected slot vessel + marker's
ActiveReFlyRecordingId vessel pid). #573's strip-kill protection
is preserved by the protected-pid exclusion, and the post-strip
spawn-death short-circuit in `ParsekPlaybackPolicy.RunSpawnDeathChecks`
already skips during an active re-fly session so the new kills do
not leak into the policy as "spawned vessel died, please re-spawn".
Pure decision in
`RewindInvoker.ResolveInPlaceContinuationDebrisToKill`. Tests:
`Bug587StripPreExistingDebrisTests` (8 cases covering null-marker,
placeholder pattern, tree-id mismatch, no-Destroyed-recordings,
matching-debris-killed, name-matches-Orbiting-recording (kept
alive), protected-pid-not-killed, empty-leftAlone). The
warn-and-continue diagnostic via
`WarnOnLeftAloneNameCollisions` still fires for the
non-in-place-continuation path so the original heads-up message
about prior-career relics is preserved.

**Review follow-up (PR #558):** P2 review caught that the kill loop walked
`FlightGlobals.Vessels` while calling `Vessel.Die()`, which removes the
vessel from the live list and shifts subsequent indices -- consecutive
matching debris would be skipped, exactly the multi-debris case the PR
is supposed to fix. Fixed by snapshotting the targets before any `Die()`
runs via a new pure-static helper
`RewindInvoker.SnapshotKillTargets<T>(IList<T>, HashSet<uint>, Func<T,uint>)`
that returns a stable list of items to kill. The Die() loop then iterates
this snapshot. Six new tests in `Bug587StripPreExistingDebrisTests`
(null-source / null-killset / empty-killset / null-pidGetter / filter-and-skip-zero
/ source-mutated-during-consumption / no-matches) pin the contract;
the source-mutated case explicitly simulates Die-removes-from-live-list
and asserts both targets are still killed.

**Status:** CLOSED 2026-04-26.

---

## 588. Ghost upper stage destroyed at SOI change to Mun and never re-created — `state-vector-from-orbital-checkpoint` skip blocks the fallback

**Source:** same playtest as #585. User: "after rewind, watching the
upper stage get to the Mun — the ghost position in Mun orbit was not
right, it jumped around when warping, did not generate a proto-vessel
in a proper Mun orbit."

**Suspected supporting evidence in KSP.log:**

- 19:29:26.116 `[Parsek][INFO][GhostMap] SOI change for recording #1 —
  new body=Mun` — ghost crossed SOI into Mun.
- 19:29:31.158 `[Parsek][INFO][GhostMap] destroy: rec=37ad80001b3c4baf98056e7c64ad0910
  ... body=Mun ... ut=16510.9 ... reason=gap-between-orbit-segments`
  — the ghost was destroyed because the recording has a gap between
  the Kerbin-frame orbit segments and the next Mun-frame segment.
- 19:29:31.169 `[Parsek][WARN][GhostMap] create-state-vector-skip:
  rec=37ad80001b3c4baf98056e7c64ad0910 ... source=StateVector
  branch=OrbitalCheckpoint body=Mun stateVecAlt=47481 stateVecSpeed=515.4
  ut=21687.8 scene=FLIGHT reason=state-vector-from-orbital-checkpoint`
  — the state-vector fallback would have placed the ghost at altitude
  47.5km / speed 515 m/s above Mun (i.e. a real Mun orbit) but the
  resolver is hard-coded to refuse `StateVector` sources whose
  underlying branch is an `OrbitalCheckpoint` track section.

**Likely root cause:** the gating logic in
`Source/Parsek/GhostMapPresence.cs:2908`
(`FailureReason = "state-vector-from-orbital-checkpoint"`) was added to
prevent the recorder-side densification regression in #571 from
re-introducing wrong ghost positions. But it is over-broad: an
OrbitalCheckpoint section's per-frame state vectors are a perfectly
valid map-presence source when the only alternative is a hole between
two segments around an SOI change. The user-visible result is exactly
what the user reported — the ghost jumps between the last available
segment endpoint and nothing, never settling into a Mun orbit.

**Files to investigate:**

- `Source/Parsek/GhostMapPresence.cs:2861` —
  `CreateGhostVesselFromStateVectors`. The
  `state-vector-from-orbital-checkpoint` reject covers all
  `branch=OrbitalCheckpoint` state-vector paths. For an SOI-change
  hole where the only available data is a checkpoint frame on the
  Mun side, the reject prevents recovery.
- `Source/Parsek/GhostMapPresence.cs` — `ResolveMapPresenceGhostSource`
  segment-gap fallback. After the destroy at 19:29:31.158 the
  resolver should pick a Mun-frame segment if one exists; if it
  doesn't (recording sparse around SOI change), state-vector fallback
  is the only option.
- Cross-reference with #571 part A (recorder-side checkpoint
  densification) — the densified checkpoints around SOI change should
  produce enough Mun-frame segments to avoid hitting this path. If
  this still fires post-#571, the densifier may not be running on
  the SOI-transition window.

**Related:** #570 (real-vessel spawn at end of Mun-mission) and
#589 (real-vessel spawns at end of recordings after rewind) are
sibling symptoms in the same playtest.

**Fix:** `ResolveMapPresenceGhostSource` now treats checkpoint-derived
state vectors as a distinct `StateVectorSoiGap` source only when the
flight map lifecycle explicitly re-queued the recording after
`gap-between-orbit-segments`, no current segment source is available,
the checkpoint body matches the post-gap SOI/body, and both the current
UT and candidate state-vector UT are inside the recording playback
window. Normal `OrbitalCheckpoint` state-vector creates still reject,
and current segments still win. The structured `[GhostMap]` lines now
emit `reason=soi-gap-state-vector-fallback` for accepted recoveries and
specific reject reasons for safer segment, not-SOI-gap, body mismatch,
or outside-window cases. Covered by
`GhostMapSoiGapStateVectorTests` plus the explicit opt-in branch in
`StateVectorWorldFrameTests`.

**Status:** ~~done~~.

---

## ~~589. Real-vessel spawns at end of mission recordings never materialize after a tree-wide rewind — `SpawnSuppressedByRewind` keeps the entire tree ghost-only forever~~

**Source:** same playtest as #585. After the booster Re-Fly merge, the
user issued a second rewind from the recordings table back to
`Kerbal X` at UT 6.8 (i.e., the very beginning of the mission) at
19:21:47. User: "did not spawn the real vessels after Mun landing
(EVA kerbal, flag and lander)."

**Suspected supporting evidence in KSP.log:**

- 19:21:50.229 `[Parsek][INFO][Rewind] OnLoad: SpawnSuppressedByRewind=true
  on 13 recording(s) — chain-leaf spawns blocked for the rewound tree
  (#573)` — the flag is set on every recording in the rewound tree.
- 19:21:50.229 thirteen lines of
  `SpawnSuppressedByRewind: #N "..." id=... tree=a9391bdd... reason=tree-match`
  — covers every recording in the tree, including future-UT recordings
  whose endpoints lie far beyond the rewind UT (#12 Bob Kerman at
  Mun UT 24034, #11 Kerbal X landed, #2 Kerbal X upper stage).
- 19:22:04.007+ recurring
  `Spawn suppressed for #12 "Bob Kerman": spawn suppressed post-rewind
  (ghost-only past, #573) | suppressed=554...591` — the flag is still
  blocking the spawn 30+ minutes later when the player would have
  reached UT 24034 in real time, and would presumably continue
  blocking forever.

**Likely root cause:** the `SpawnSuppressedByRewind` flag was added by
#573 to prevent the Re-Fly's strip from triggering spawn-death respawn
of a chain-leaf vessel that the player is actively re-flying.
`reason=tree-match` is too broad: every recording sharing the rewound
tree gets the flag, including recordings whose endpoints lie ahead of
the rewind UT and whose terminal vessels (EVA kerbal Bob, Mun lander
Kerbal X, planted flag) are exactly what the spawn-at-end Phase 4
design says should materialize when ghost playback crosses the
recording's `endUT`.

The flag needs to be cleared once playback advances past the
recording's `endUT`, or scoped only to recordings whose UT ranges
overlap the rewind UT (i.e., recordings the player is actually
re-flying over), not every recording in the tree.

**Files to investigate:**

- `Source/Parsek/RewindContext.cs` /
  `Source/Parsek/Recording.cs` —
  `SpawnSuppressedByRewind` flag lifecycle. Find the set sites
  (OnLoad, "tree-match" reason) and verify there is any clear path.
- `Source/Parsek/ParsekPlaybackPolicy.cs` — `ShouldSpawnAtRecordingEnd`
  / spawn-at-end gate. Check whether the flag is consulted as an
  absolute block, or as a "block until past rewind UT" gate.
- Cross-reference with #573 — that fix's INTENT was strip-kill
  protection for the actively re-flown vessel, not "make the entire
  tree ghost-only forever". The reason-string `tree-match` should
  probably be split into `same-recording` (the actual #573 case) and
  `same-tree-future-recording` (the case that needs an `endUT >
  rewindUT` carve-out).

**Fix:** split the rewind suppression semantics. `reason=same-recording`
is the #573 strip-kill/source duplicate protection case and remains an
absolute spawn-at-end block. `reason=same-tree-future-recording` is now
logged as an intentional skip: recordings whose `StartUT` and `EndUT`
are ahead of the rewind UT are not marked ghost-only and materialize
normally when playback reaches their endpoint. New persisted metadata
(`spawnSuppressedByRewindReason`, `spawnSuppressedByRewindUT`) scopes
future saves, and the spawn gate consumes/clears legacy unscoped markers
so broken saves from the whole-tree implementation stop blocking future
terminal spawns. Diagnostics now distinguish applied #573 protection,
future same-tree skip, stale marker clear/reset, and spawn allowed despite
same-tree rewind.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~590. Tree restore Limbo + sidecar hydration failure pattern needs a unified diagnosis~~

**Source:** umbrella for the diagnoses that fed #585. Listed separately
because the underlying invariants — "tree restore from rewind quicksave
must keep the in-place continuation recording bound to the live
recorder" and "sidecar epoch mismatch must NOT silently move the tree
into Limbo" — touch
`ParsekScenario`, `RecordingStore`, `RewindInvoker`, `MergeJournal`,
and `MarkerValidator` together.

**Suggested next move:** before patching #585's symptom, read these
files together and write a short design note answering:

- What is the contract between `ReFlySessionMarker.OriginChildRecordingId`
  and `RestoreActiveTreeFromPending`'s expected-active-vessel decision?
- What is the contract between `Recording.Epoch` on the rewind
  quicksave's `.sfs` and the on-disk `.prec` for an in-place
  continuation origin, and which side is authoritative when they
  disagree?
- When sidecar hydration drops trajectory + snapshots for the active
  recording during a rewind, is the right recovery to load `.prec` and
  trim points after rewind UT, or to drop the trajectory entirely and
  re-record from rewind UT, or to refuse the rewind and surface a
  user-facing error?

**Resolution (2026-04-26):** Closed alongside #585. Design note
[`docs/dev/plans/refly-inplace-continuation-tree-restore.md`](plans/refly-inplace-continuation-tree-restore.md)
answers all three questions:

1. The marker's `ActiveReFlyRecordingId` is authoritative for the
   in-place continuation Re-Fly's expected active vessel; the rewind
   quicksave's `ActiveRecordingId` is stale. Carve-out lives in
   `ReFlySessionMarker.ResolveInPlaceContinuationTarget` and is
   consumed by `ParsekFlight.RestoreActiveTreeFromPending` before the
   3s wait loop.
2. For an in-place continuation, neither side is fully authoritative:
   the rewind quicksave's `.sfs` epoch is correct for trajectory POINTS
   (which we re-record from rewind UT anyway) and the on-disk `.prec`
   is correct for the SNAPSHOT (which the player landed/staged with
   at end of original mission). Bug #270's drop-on-mismatch stays the
   default; the fix lives in the marker-aware coroutine, not in the
   sidecar load path. The deferred snapshot-only-rescue (when
   on-disk `.prec` epoch > `.sfs` expected epoch) is filed as a
   future invariant-tightening pass — `StashActiveTreeAsPendingLimbo`
   already re-captures null-snapshot leaves at scene exit, which
   covers the playtest's `hasSnapshot=False` symptom in practice.
3. The Limbo error surface is correct as the default; the empty
   trajectory list IS the resumed recording's expected shape.
   Bug #270's safety net stays intact for non-in-place-continuation
   cases (corrupt save, half-written file, etc).

**Status:** CLOSED 2026-04-26.

---

## ~~591. Log spam: `OnVesselSwitchComplete:entry/post` RecState lines fire ~10000 times in a single session during missed-vessel-switch recovery~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 9969
occurrences of `RecState [#NNNN][OnVesselSwitchComplete:entry|post]`,
clustered in two windows:

- 19:11:28.888 → 19:12:23 (~55s, ~9300 lines): the `Bob Kerman` EVA
  vessel was destroyed (sub-surface state on Mun) and the recorder
  cleared `recorderPid=0`. `ParsekFlight.Update` (line ~6705) detects
  `activeVessel != recorderVessel` every frame and runs the recovery
  path: a single `WarnRateLimited("missed-vessel-switch-{pid}")`
  warning is suppressed (suppressed=589 etc.) so the WARN line itself
  is fine — but the recovery branch unconditionally calls
  `OnVesselSwitchComplete(activeVessel)` after the warn, and the two
  RecState dispatches inside it (`OnVesselSwitchComplete:entry` at
  `ParsekFlight.cs:1881` and `:post` at `ParsekFlight.cs:2030`) are
  not rate-limited.
- 19:11:28 → 19:11:53: at the same time, sibling
  `[WARN][Flight] Update: recovering missed vessel switch ... | suppressed=589`
  rate-limit summaries fire at 5s intervals — the WARN side
  rate-limit works, only the inner RecState logs spam.

**Why it matters:** the two RecState lines together are ~280 KB of
log spam in 55 seconds (avg ~5 KB/s), well above the project's
"log volume must stay readable" target. Every line is identical
shape, no useful per-frame state changes, on a hot path
(`Update()`).

**Files to investigate:**

- `Source/Parsek/ParsekFlight.cs:1881` and `:2030` — the
  `RecState("OnVesselSwitchComplete:entry"/":post")` calls. They
  exist for tracing legitimate vessel-switch boundaries; for the
  recovery loop they fire every frame for the same activePid.
- `Source/Parsek/ParsekFlight.cs:6700-6710` — the missed-vessel-switch
  recovery branch. The WARN is correctly rate-limited via
  `WarnRateLimited("missed-vessel-switch-{activeVesselPid}")`, but
  the subsequent `OnVesselSwitchComplete(activeVessel)` call is
  unconditional. Either gate the call on the same rate-limit key, or
  rate-limit the RecState lines on the same key, or detect
  "recoverer is firing for the same activePid as last frame" and
  short-circuit before logging.

**Status:** ~~Open.~~ Done. Fix: the recovery branch still calls
`OnVesselSwitchComplete(activeVessel)`, preserving the recovery behavior,
but passes a recovery diagnostic context so only the nested
`RecState("OnVesselSwitchComplete:entry"/":post")` lines are
rate-limited. The key includes activePid plus recorder/tracking
fingerprint (recorder pid, live/background flags, tracked/armed state,
chain-to-vessel pending flag, active recording id, and BackgroundMap
count), so repeated identical Update frames coalesce into 5s
`suppressed=N` summaries while changed state or normal non-recovery
vessel-switch boundaries emit fresh diagnostics.

---

## ~~592. Log spam: time-warp rate-change checkpoint logs fire ~3300 times per session from KSP's chatty `onTimeWarpRateChanged` GameEvent~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 1122 ×
`[BgRecorder] CheckpointAllVessels at UT=...`, 1121 ×
`[Checkpoint] Time warp rate changed to N x at UT=... — checkpointing
all background vessels`, and 1121 × `[Checkpoint] Active vessel orbit
segments handled by on-rails events`. ~3364 lines total — the single
biggest log-spam source not already in #591 / #160.

**Diagnosis (2026-04-25):** of the 1121 rate-change events, 1090 were
`1.0x` and only 248 unique UT values were seen — KSP's
`GameEvents.onTimeWarpRateChanged` re-fires aggressively at the same
rate during scene transitions, warp-to-here, and similar transients.
Three `Verbose` log lines were emitted per event with no rate-limit,
plus the underlying `CheckpointAllVessels` walk did real work each
time even though closing+reopening an orbit segment at the same UT is
idempotent.

**Fix:** all three log calls in `ParsekFlight.OnTimeWarpRateChanged`
(`ParsekFlight.cs:5889` / `:5899`) and the summary in
`BackgroundRecorder.CheckpointAllVessels`
(`BackgroundRecorder.cs:2084`) now route through
`ParsekLog.VerboseRateLimited`. The two `Checkpoint` lines are keyed
per warp-rate string (so transitions between distinct rates still log
on the first event), and the BgRecorder summary is keyed by the
`(checkpointed, skippedNotOrbital, skippedNoVessel)` shape so a
genuine count change still surfaces immediately. Regression
`BackgroundRecorderTests.CheckpointAllVessels_RepeatedCallsSameShape_RateLimitedToOneLine`
calls 50x with the same shape and asserts a single emitted summary
line.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0. Log-only fix; the
underlying "KSP fires rate-change at 1x ~4x more often than there are
real rate changes" concern is tracked separately as #597.

---

## ~~593. Log spam: repeatable record milestones (`RecordsSpeed`/`RecordsAltitude`/`RecordsDistance`) re-emit the same `Milestone funds` / `stays effective` / `Milestone rep at UT` line on every recalc walk~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — ~1190 lines:
- 510 × `[Milestones] Repeatable record milestone '<id>' stays
  effective at UT=...` (170 each for Speed / Altitude / Distance).
- 510 × `[Funds] Milestone funds: +N, milestoneId=Records...,
  runningBalance=...` (170 each).
- 393 × `[Reputation] Milestone rep at UT=...: milestoneId=Records...,
  ...`.

**Diagnosis (2026-04-25):** `MilestonesModule.ProcessMilestoneAchievement`
walks every committed action in the ledger on every recalc; for the
three repeatable record-milestone IDs the credit is established on
the first hit and every subsequent walk re-takes the
`isRepeatableRecordMilestone` branch with identical milestoneId,
recordingId, fundsAwarded and repAwarded, producing structurally
identical log lines. With ~57 recalcs in a 30-min session times three
record-milestones, that produces ~170 lines per branch.

**Fix:** `MilestonesModule.ProcessMilestoneAchievement` (the repeatable
"stays effective" branch), `FundsModule.ProcessMilestoneEarning`, and
`ReputationModule.ProcessMilestoneRep` now all route through
`ParsekLog.VerboseRateLimited` keyed by the stable
`GameAction.ActionId` (with a `(milestoneId, recordingId, ut, reward)`
tuple as fallback if `ActionId` is empty). The intended invariant is
"recalculating the SAME action collapses its log line"; two distinct
record-milestone hits sharing the same milestoneId+recordingId but
with different UT or reward have different `ActionId`s and still log
on their first walk. Each emitted line now includes `actionId=...` so
the identity is debuggable. Regressions
`FundsModuleTests.MilestoneEarning_SameActionRecalculated_RateLimitedToOneLine`,
`MilestonesModuleTests.RepeatableRecordMilestone_SameActionRecalculated_RateLimitedToOneLine`,
and `ReputationModuleTests.MilestoneRep_SameActionRecalculated_RateLimitedToOneLine`
re-walk a single action 100 times and assert exactly one emitted line.
Companion regressions
`*_DistinctActionsSamePair_LogSeparately` and
`*_NullRecordingId_StillKeysOnActionId` confirm distinct actions
(including null-recording standalone/KSC paths) survive the gate.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~594. Log spam: `KspStatePatcher.PatchMilestones` bare-Id fallback fires per recalc for the same `(nodeId, qualifiedId)` pair~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 221 ×
`[KspStatePatcher] PatchMilestones: bare-Id fallback match for 'Orbit'
(qualified='...' not found — old recording?)`.

**Diagnosis (2026-04-25):** the bare-Id fallback diagnostic exists to
flag old-format recordings whose milestones stored the bare body-
specific node ID (`Landing`) instead of the qualified path
(`Mun/Landing`). Once such a fallback exists, every recalc walk
re-emits the same line because the recording's milestone-credit
state is steady. Useful as a one-shot "old recording detected" hint;
useless and noisy as a per-recalc line.

**Fix:** the `Verbose` call at `KspStatePatcher.cs:988` now routes
through `VerboseRateLimited` keyed by `(nodeId, qualifiedId)` so each
distinct fallback pair logs at most once per rate-limit window; new
fallback pairs surface immediately on first match.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~595. Log spam: `OrbitalCheckpoint point playback` and `Recorder Sample skipped` rate-limit windows were too tight~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 413 ×
`[Playback] OrbitalCheckpoint point playback: rec=<HEX> currentUT=...`
and 197 × `[Recorder] Sample skipped at ut=...; waiting for motion/
attitude trigger`. Both lines were already routed through
`VerboseRateLimited`, but with custom 1.0s and 2.0s windows
respectively — tight enough that long-playing OrbitalCheckpoint
sections still emitted ~14 lines/min per `(recId, sectionIdx)` and
stationary recordings ~7 lines/min.

**Diagnosis (2026-04-25):** both lines convey steady-state telemetry
(per-section ghost playback / "still stationary, no sample taken"),
not state transitions, so the rate-limit window can safely widen to
the project default 5s without losing diagnostic value — the per-key
identity (`recId+sectionIdx` for OrbitalCheckpoint, single shared key
for Sample skipped) means new sections / new recorders still log on
their first frame.

**Fix:** both call sites (`ParsekFlight.cs:13870` and
`FlightRecorder.cs:5458`) now use the default 5s rate-limit window
inherited from `ParsekLog.DefaultRateLimitSeconds`.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## ~~596. Log spam: `KspStatePatcher.PatchFacilities` emits an INFO summary on every recalc even when there is nothing to patch~~

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 42 ×
`[KspStatePatcher] PatchFacilities: levels patched=0, skipped=0,
notFound=0, total=0`. Earlier playtests (the same KSP.log around lines
26791-27996) showed several thousand of these once the recalculation
engine churned the same empty `FacilitiesModule` repeatedly — the
no-op summary fires unconditionally at INFO on every PatchFacilities
call.

**Diagnosis (2026-04-25):** the summary's purpose is to make
"facility patching changed game state or hit a missing facility"
visible at INFO. `skippedCount` increments on the no-op pass (a
facility already at its target level), so a steady-state non-empty
`FacilitiesModule` would still re-emit the INFO summary on every
recalc if `skipped` counted toward the gate.

**Fix:** `KspStatePatcher.PatchFacilities` now gates the INFO summary
on `patchedCount + notFoundCount > 0`. The skipped-only steady-state
case (`patched=0, notFound=0, skipped>0`) routes through
`VerboseRateLimited` with key `patch-facilities-skipped-only`. The
empty-totals case (no tracked facilities at all) keeps its existing
`patch-facilities-empty` rate-limited Verbose path. Regressions
`KspStatePatcherTests.PatchFacilities_NotFound_LogsInfoSummary` and
`PatchFacilities_Empty_DoesNotLogInfo_UsesRateLimitedVerbose` pin
the INFO branch (notFound>0) and the empty-Verbose branch. The
skipped-only branch needs real `UpgradeableFacility` refs in
`ScenarioUpgradeableFacilities.protoUpgradeables` and is verified
in-game during the next playtest pass instead of an xUnit canary.

**Status:** CLOSED 2026-04-25. Fixed for v0.9.0.

---

## 597. Underlying logic: KSP's `onTimeWarpRateChanged` GameEvent fires at 1x roughly 4x more often than there are real rate changes, and `OnTimeWarpRateChanged` always re-runs `CheckpointAllVessels`

**Source:** `logs/2026-04-25_1933_refly-bugs/KSP.log` — 1090 of 1121
events were `1.0x`, but only 248 unique UT values appeared, and many
of those 248 had multiple sub-second-apart 1.0x events (e.g.
`19:08:26.557` and `19:08:26.567` both at UT≈21526.10). KSP fires the
event spuriously across scene transitions, warp-to-here, save/load
boundaries, and similar transients.

**Why it matters:** Bug #592 only addresses the LOG noise. The
underlying `BackgroundRecorder.CheckpointAllVessels` call still runs
every event, closing and re-opening the same orbit segment at the
same UT for every background vessel. The work is idempotent (same
UT → identical segment shape → no observable behaviour change), so
it has not produced a known correctness defect, but it is wasted
work scaling with `backgroundVesselCount × eventCount` and could
mask a real correctness regression in the future ("why is this orbit
segment getting reopened mid-flight?").

**Files to investigate:**

- `Source/Parsek/ParsekFlight.cs:5842` — `OnTimeWarpRateChanged`. Add
  a `lastSeenWarpRate` field and short-circuit when both the rate and
  the UT have not advanced past the last invocation. Care needed
  because the warp-start / warp-end branch above this call also
  depends on the event firing.
- `Source/Parsek/BackgroundRecorder.cs:2030` — `CheckpointAllVessels`.
  An alternate fix is to make this method itself idempotent at the
  same UT (skip the close+reopen if the segment is already closed
  at this exact UT).

**Status:** Open. Performance / hygiene; no observed correctness
defect. Defer until a measurable hot-path latency or a specific
ghost-orbit anomaly points at it.

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

2026-04-25 update (post-#591 second-tier cleanup): the `2026-04-25_1933_refly-bugs`
KSP.log surfaced six more spam sources, addressed as numbered bugs #592-#596
(closed in this commit) plus #597 (open underlying-logic concern). #592 covers
the ~3300 `Time warp rate changed` / `CheckpointAllVessels` / `Active vessel
orbit segments handled` lines from KSP's chatty `onTimeWarpRateChanged`
GameEvent. #593 covers ~1190 lines from repeatable record milestones
(`Records*` IDs) re-emitting the same `Milestone funds` / `stays effective` /
`Milestone rep at UT` line on every recalc walk. #594 covers 221 KspStatePatcher
bare-Id fallback lines. #595 widens the OrbitalCheckpoint playback and Recorder
sample-skipped rate-limit windows from 1-2s to the default 5s. #596 gates the
PatchFacilities INFO summary on having actual work. #597 tracks the underlying
"KSP fires rate-change at 1x ~4x more often than real rate changes" concern as
a separate open todo (performance / hygiene only — no observed correctness
defect).

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
