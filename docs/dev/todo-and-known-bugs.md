# TODO & Known Bugs

Previous entries (225 bugs, 51 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v1.md`.
Entries 272–303 (78 bugs, 6 TODOs — mostly resolved) archived in `done/todo-and-known-bugs-v2.md`.

---

# Known Bugs

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

## 296. EVA kerbal who planted flag did not appear after spawn

Log shows KSCSpawn successfully spawned the EVA kerbal (Bill Kerman, pid=484546861), but the user reports not seeing it. Likely post-spawn physics destruction — EVA kerbals are fragile and can be killed by terrain collision or slope bounce.

**Investigation:** Enhanced spawn logging to include lat/lon/alt/sit for all spawn paths (KSCSpawn, SpawnAtPosition, RespawnVessel fallback). The existing `RunSpawnDeathChecks` in `ParsekPlaybackPolicy` already detects and logs spawn-death cycles. Next playtest should reveal whether the kerbal is destroyed post-spawn.

**Status:** Investigation logging added. Root cause TBD — needs next playtest data.

---

## 290. F5/F9 quicksave/quickload interaction with recordings — verification needed

User asked: *"check if the quicksave/quickload and recordings systems work well together now (we fixed some bugs) — investigate deep in the logs"*. Bug #292 is the smoking gun — F9 reloads can silently drop recordings created since the last quicksave was made.

**Open questions**:
1. Are there OTHER scenarios beyond #292 where F5/F9 + recordings interact badly? (e.g., F9 during an active recording, F5 during a merge dialog)
2. Should the quicksave auto-refresh on every committed recording change, not just merges? Trade-off: more I/O vs. data safety.
3. Does the rewind save (`parsek_rw_*.sfs`) have the same staleness problem as the quicksave? The rewind save IS Parsek-managed, so we control when it's written.

**Status**: Open — partially investigated by #292. Needs broader scenarios coverage.

---

## 286. Full-tree crash leaves nothing to continue with — `CanPersistVessel` blocks all `Destroyed` leaves (largely mitigated)

Surfaced as the user-facing complaint behind bug #278 ("nothing to continue playing with after a crash recording"). #278's snapshot-loss fix restores the in-memory `hasSnapshot=True` state for background-split debris, but the merge dialog's `CanPersistVessel` predicate at `MergeDialog.cs:450-467` hard-blocks any leaf whose `TerminalStateValue ∈ {Destroyed, Recovered, Docked, Boarded}` regardless of snapshot availability. When the player crashes the entire launch tree (root vessel + all debris destroyed), every leaf falls into one of those gated states, the merge dialog reports `spawnable=0`, and no vessel ends up on the surface for the player to continue with.

**This is a design decision, not a bug.** Three options for the next user discussion:

- **(a) Pre-crash F5 fallback.** When no leaf satisfies `CanPersistVessel`, fall back to spawning the player's most recent quicksave's vessel state.
- **(b) Relax `Destroyed` gating when snapshot exists.** Allow `terminal=Destroyed` to spawn from snapshot for at least the root leaf.
- **(c) Status quo + UX message.** Leave the gating unchanged but surface a clear merge-dialog message: "All recording branches ended in failure — no vessel can be continued."

**Update (2026-04-10 investigation):** The `spawnable=0` cases in the 2026-04-09 playtests were primarily caused by #278's blanket `Destroyed` stamping, not by a true all-crash scenario. With #278 fixed and #284 shipped, the remaining edge case is a genuine full-tree crash where ALL parts are destroyed before merge — rare in practice and arguably correct behavior.

**Priority:** Low (downgraded from Medium). The remaining scenario is a design decision for option (c) UX messaging.

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

## 189b. Ghost escape orbit line stops short of Kerbin SOI edge

For hyperbolic escape orbits, KSP's `OrbitRendererBase.UpdateSpline` draws the geometric hyperbola from `-acos(-1/e)` to `+acos(-1/e)` using circular trig (cos/sin), which clips at a finite distance (~12,000 km for e=1.342). The active vessel shows the full escape trajectory to the SOI boundary because it uses `PatchedConicSolver` + `PatchRendering`. Ghost ProtoVessels don't get a `PatchedConicSolver`.

**Options:**
1. Draw a custom LineRenderer through the recording's trajectory points (accurate but significant work)
2. Extend the orbit line beyond the hyperbola asymptote with a straight-line segment to the SOI exit point
3. Give the ghost a `PatchedConicSolver` (complex, may conflict with KSP internals)

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability) — cosmetic, same tier as T25 fairing truss

---

## 220. PopulateCrewEndStates called repeatedly for 0-point intermediate recordings

Intermediate tree recordings with 0 trajectory points but non-null VesselSnapshot trigger `PopulateCrewEndStates` on every recalculation walk (36 times in a typical session). These recordings can never have crew.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

---

## ~~241. Ghost fuel tanks have wrong color variant~~

Parts whose base/default variant is implicit (not a VARIANT node) showed the wrong color. KSP stores the base variant's display name (e.g., "Basic") as `moduleVariantName`, but no VARIANT node has that name — `TryFindSelectedVariantNode` fell through to `variantNodes[0]` (e.g., Orange) instead of keeping the prefab default. Fix: `MatchVariantNode` returns false when the snapshot names a variant with no matching VARIANT node, so callers skip variant rule application and the prefab's base materials are preserved.

**Fix:** PR #198

---

## 242. Ghost engine smoke emits perpendicular to flame direction

On some engines, the smoke/exhaust particle effect fires sideways (perpendicular to the thrust axis) instead of along it. The flame plume itself is oriented correctly but the secondary smoke effect has a wrong emission direction. Likely a particle system `rotation` or `shape.rotation` not being transformed correctly when cloning engine FX from the prefab EFFECTS config.

**Priority:** Low — cosmetic, only noticeable on certain engine models

---

## 270. Sidecar file (.prec) version staleness across save points

Latent pre-existing architectural limitation of the v3 external sidecar format: sidecar files (`saves/<save>/Parsek/Recordings/*.prec`) are shared across ALL save points for a given save slot. If the player quicksaves in flight at T2, exits to TS at T3 (which rewrites the sidecars with T3 data), then quickloads the T2 save, the .sfs loads the T2 active tree metadata but `LoadRecordingFiles` hydrates from T3 sidecars on disk — a mismatch.

Not introduced by PR #160, but PR #160's quickload-resume path makes it more reachable (previously, quickloading between scene changes always finalized the tree, so the tree was effectively "new" each time).

**Fix plan (long-term):** version sidecar files per save point — stamp each `.prec` with the save epoch or a hash, refuse to load mismatched versions. Alternatively, never rewrite sidecars for committed trees; treat them as immutable.

**Priority:** Low — rare user workflow (quicksave in flight + exit to TS + quickload), and the worst case is playback inconsistency, not data loss. Flag again if it bites during playtest.

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

### T6. LOD culling for distant ghost meshes

Don't render full ghost meshes far from camera. Unity LOD groups or manual distance culling.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

### T7. Ghost mesh unloading outside active time range

Ghost meshes built for recordings whose UT range is far from current playback time could be unloaded and rebuilt on demand.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

### T8. Particle system pooling for engine/RCS FX

Engine and RCS particle systems are instantiated per ghost. Pooling would reduce GC pressure with many active ghosts.

**Priority:** Deferred to Phase 11.5 (Recording Optimization & Observability)

---

## TODO — Ghost Visuals

### T25. Fairing internal truss structure after jettison

After fairing jettison, the ghost currently shows just the payload and base adapter. KSP's real vessel can show an internal truss structure (Cap/Truss meshes controlled by `ModuleStructuralNodeToggle.showMesh`). The prefab meshes are at placeholder scale (2000x10x2000) that only KSP's runtime `ModuleProceduralFairing` can set correctly. A procedural truss mesh was attempted but removed due to insufficient visual quality.

To implement properly: either rescale prefab Cap/Truss meshes from XSECTION data (need to reverse-engineer the mesh unit geometry), or generate higher-fidelity procedural geometry with proper materials.

**Priority:** Low — cosmetic, only visible briefly after fairing jettison

---

## TODO — Compatibility

### T43. Mod compatibility testing (CustomBarnKit, Strategia, Contract Configurator)

Test game actions system with popular mods: CustomBarnKit (non-standard facility tiers may break level conversion formula), Strategia (different strategy IDs/transform mechanics), Contract Configurator (contract snapshot round-trip across CC versions). Requires KSP runtime with mods installed. Investigation notes in `docs/dev/mod-compatibility-notes.md`.

**Priority:** Last phase of roadmap — v1 targets stock only, mod compat is best-effort

---

## TODO — Recording Data Integrity

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

### T56. Remove standalone RECORDING format entirely

Follow-up to bug #271 (always-tree unification). The runtime now always creates tree recordings, and the injector now produces RECORDING_TREE nodes for all synthetic recordings. But the codebase still supports the old standalone RECORDING format:

- `RecordingStore.committedRecordings` list (238 refs across 28 production files, 300 refs across 27 test files)
- `StashPending`/`CommitPending`/`DiscardPending` methods (used by ChainSegmentManager)
- `MergeDialog.Show(Recording)` and `ShowStandaloneDialog` (used by chain commit and deferred split dialogs)
- Standalone RECORDING serialization in `ParsekScenario.OnSave`/`OnLoad`
- `PARSEK_ACTIVE_STANDALONE` migration shim in `TryRestoreActiveStandaloneNode`

To remove: collapse `committedRecordings` into `committedTrees` (every recording accessed through its parent tree), delete the standalone pending slot, delete standalone merge dialog, delete standalone RECORDING serialization, delete chain segment standalone commit paths (dead code in always-tree mode). This is a ~55-file refactor.

~~Critical subtask (done):~~ Removed the temporary `TreeId != null` skip in `CanAutoSplitIgnoringGhostTriggers`. The existing `RunOptimizationPass` code already added split recordings to `tree.Recordings` and updated `BranchPoint.ParentRecordingIds`; the skip was the only thing preventing tree splits. Added `RebuildBackgroundMap()` after optimization passes for tree consistency. Fixed `TraceLineagePids` to follow chain links so root lineage PID collection works after optimizer splits.

Remaining (~55-file refactor, ordered by dependency):
1. Delete `StashPending`/`CommitPending`/`DiscardPending` and the standalone pending slot (`pendingRecording`). All commit paths now go through `StashPendingTree`/`CommitPendingTree`.
2. Delete `MergeDialog.Show(Recording)` and `ShowStandaloneDialog` -- only the tree merge dialog (`ShowTreeDialog`) is used.
3. Delete standalone RECORDING serialization in `ParsekScenario.OnSave`/`OnLoad` (the `PARSEK_ACTIVE_RECORDING` node path, not the `PARSEK_ACTIVE_RECORDING_TREE` path).
4. Delete `PARSEK_ACTIVE_STANDALONE` migration shim in `TryRestoreActiveStandaloneNode`.
5. Delete chain segment standalone commit paths in `ChainSegmentManager` (dead code in always-tree mode).
6. Collapse `committedRecordings` into `committedTrees`: replace all 238 production refs (28 files) and 300 test refs (27 files) with tree-based access (e.g., `committedTrees.SelectMany(t => t.Recordings.Values)`). This is the bulk of the work.

Prerequisite: delete all old save files (no users yet, clean slate).

**Priority:** Medium -- optimizer adaptation done, standalone format removal is cleanup

---

### T57. EVA spawn-at-end blocked by parent vessel collision

EVA recordings created by mid-flight EVA (tree branch) fail to spawn at end because the entire EVA trajectory overlaps with the already-spawned parent vessel. The spawn collision walkback exhausts every point in the trajectory and abandons the spawn.

Observed in t56-optimizer-test session: Bob Kerman did a surface EVA near the launchpad. The parent vessel (Kerbal X) spawned first, then Bob's EVA spawn was abandoned because his entire trajectory (surface walk near pad) was within the 2.5m EVA collision bounds of the parent. Bob never materialized.

Secondary issue: the held-ghost retry logic (`ParsekPlaybackPolicy.cs:395`) checks `VesselSpawned` but not `SpawnAbandoned`, so it logs "succeeded on retry" for abandoned spawns.

Fix options:
1. Exempt EVA vessels from collision checks against their known parent vessel (use `ParentRecordingId` to identify the parent, look up its `VesselPersistentId`)
2. When EVA spawn is abandoned due to parent collision, fall back to boarding the crew member onto the parent vessel instead
3. Both -- try spawning, skip collision against parent, fall back to boarding if still blocked

**Priority:** Medium -- pre-existing issue, not caused by optimizer changes but exposed by enabling tree recording spawns

---

### T58. Debris/booster ghost engines show running effects at zero throttle after staging

When a booster separates (staging, decouple), the debris recording inherits the engine state from the moment of separation. If the engine was running at separation, the ghost plays back engine FX (flame, smoke) even though the throttle is 0 on the separated stage. The engine was shut down by staging but the ghost's seed events or initial state show it as running.

Likely cause: the engine shutdown event at separation is either not captured (the `EngineShutdown` event fires after the part is already on the debris vessel, and the recorder may not catch it on the new vessel's first frame), or the ghost FX system seeds the engine as running from the pre-separation snapshot without checking the throttle value.

Fix: ensure debris recordings either (a) capture the engine shutdown at separation as a seed event, or (b) the ghost FX system checks throttle == 0 and suppresses effects even if the engine state says "running."

**Priority:** Low -- cosmetic, affects ghost visual fidelity only

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
