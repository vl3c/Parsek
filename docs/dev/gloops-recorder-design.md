# Gloops - Ghost Loop Recording and Playback

*Design document for Gloops, a standalone KSP1 mod that records a vessel and replays it as looping ghosts, and for the shared ghost core that Parsek compiles in. Rewritten 2026-10-05 from an owner interview and a measured read of the codebase; it supersedes the 2026-04 draft, whose "ready to move" extraction table no longer matched the code (section 9 records the measurements).*

**Status (2026-10-05):** model and boundary agreed; WHETHER and WHEN to extract is still open (section 11). No code has moved. The in-Parsek Gloops recorder still exists, unreachable by players (its launcher was retired in 0.10.4).

---

## 1. Owner rulings (2026-10-05)

1. **Gloops leaves Parsek.** The in-Parsek Gloops feature (the ghost-only manual recorder, its window, its group, its seam verbs) is deleted from Parsek. Gloops becomes a standalone mod in its own repository, consumed by Parsek as a git submodule. Parsek does not control Gloops: no API between them, no UI-suppression handshake, no detection of a standalone install.
2. **Isolation.** A player may install standalone Gloops next to Parsek. The two must not interfere: Parsek bundles its own copy of the shared code, and the standalone mod works on its own. Ideally the two copies are fully separate (section 4).
3. **Career fields stay.** `TrajectoryPoint.funds` / `science` / `reputation` are not stripped during the split; stripping them changes the `.prec` layout (a schema-generation bump and a fixture re-harvest). Revisit later.
4. **Keep it in this repository for now, but begin separation work.** The core lives in-repo until engine churn settles; the submodule split is the last, mechanical step.
5. **Namespace `Gloops`** for the new code. Parsek-side names that say "Gloops" go away with the feature removal.
6. **Recording is separate.** Parsek and standalone Gloops may run different versions, so they record separately and know nothing about each other. Both build on the same core building blocks; neither reads the other's data.
7. **No export bridge for now.** Parsek does not export `.gloop` files; Gloops does not import Parsek recordings.
8. **The take model** in section 5, including the hard rule that nothing pops into existence.

---

## 2. What Gloops is

A KSP1 mod that records a vessel's flight and replays it as ghosts: visual copies with no physics, no colliders and no effect on game state, optionally looping. It knows nothing about careers, timelines, rewind, recording trees, crew reservation, funds or vessel spawning.

Gloops is two layers in one repository:

**Gloops Core (a library).**

- Trajectory data model: points, track sections, orbit segments, part events, flag events, surface position, terminal state.
- Recording building blocks: the adaptive sampling decision, environment classification, part-event detection (the pure `Check*Transition` decisions and their per-frame pollers), part-state seeding.
- Ghost construction: meshes from a vessel snapshot, variants, fairings, engine / RCS / reentry FX, audio presets.
- Playback engine: per-frame placement, part-event replay, loops and overlaps, distance zones.

Core has NO `KSPAddon`, NO Harmony patch, NO `ScenarioModule`, NO GameEvents subscription of its own, NO UI, NO persistence, NO settings file, and reads NO fixed GameData path. Anything with a global side effect, or that KSP discovers by scanning assemblies or by type name, belongs to the host. Core talks to its host only through interfaces: trajectories plus per-ghost flags in, world placement through a positioner, lifecycle events out (playback completed, loop restarted, overlap expired, camera action), logging through a host-supplied sink.

**Gloops Standalone (the mod players install).** The only host of Core inside the Gloops repository: its own `KSPAddon` host and a simple positioner (no anchors, chains or re-fly), its own take recorder (section 5) and UI (start / stop / preview / discard / loop settings), its own storage (`.gloop` files; content packs later, section 8). Ships as `GameData/Gloops/`.

---

## 3. What Gloops is not

- Not a dependency Parsek controls or talks to at runtime.
- Not a career system: no ledger, crew, resources, contracts, rewind, merge, supersede, missions, logistics.
- Not a spawner: a ghost never becomes a real vessel.
- Not a world-presence system: no tracking-station entries, orbit lines, map markers or CommNet relay (those are Parsek's `GhostMapPresence` / `GhostCommNet` and stay there).

---

## 4. How Parsek uses Gloops Core

- **Source inclusion, not a DLL reference.** `Parsek.csproj` compiles the Core source files directly into `Parsek.dll`. Parsek ships ONE DLL as today; the release zip, the harness provisioner and its DLL hash checks are unchanged.
- **Isolation follows from that.** Parsek's copy and a standalone `Gloops.dll` have different assembly identities, so there is no type clash, no version drift between them, and statics are per copy. Two engines may run at once, each drawing its own ghosts, sharing nothing. `internal` keeps working across the Core / Parsek boundary because inside Parsek they are one assembly, so no `InternalsVisibleTo` and no `KSPAssemblyDependency`.
- **This is why Core must stay side-effect free.** A `KSPAddon`, Harmony patch or `ScenarioModule` inside Core would run twice with both mods installed.
- **Logging.** Parsek's copy logs through `ParsekLog`, so every `[Parsek][LEVEL][Subsystem]` line, and the harness log contracts that grep them, stay byte-identical.
- **Parsek is the host for its copy.** It implements positioning (relative / anchor frames, the body-fixed primary surface for parent-anchored recordings), computes every policy flag (chains, spawn, supersede, re-fly suppression, mission loop units) and reacts to lifecycle events (spawn at end, watch camera, resources). It keeps everything career-related: recording trees and branch points, `.prec` / sidecar persistence, ledger, crew, map and tracking-station presence, CommNet, missions, logistics.
- **Change flow after the split.** A Core change Parsek needs lands in the Gloops repository first; Parsek bumps its submodule pointer. Standalone must keep building against the same Core, which is the real proof that the boundary holds.

---

## 5. The Gloops take model

A **take** is one recording session of one vessel family. It replays as one loop.

### 5.1 Hard rule

**Nothing pops into existence.** Every ghost in a take is visible from the take's start, or comes off a ghost that is already visible (a stage separating, a kerbal leaving a hatch, a flag or ground part leaving a kerbal's hands). This rule filters every other rule.

**Disappearing is allowed.** A member that goes out of range, is destroyed, or (an EVA kerbal) boards another vessel simply disappears. No ballistic completion of out-of-range debris (owner ruling 2026-10-05).

The start and end of a loop, where the whole family appears and disappears, are a content concern (start and finish takes at rest or out of view), not a recorder rule.

### 5.2 Members

At take start the recorder captures the identity of every part and every crew member aboard the vessel. **Members** are:

- the starting vessel;
- anything that separates from a member: stages, debris, EVA kerbals, deployed ground parts, planted flags;
- a vessel formed when two members dock.

Each member has its own trajectory (samples, part events, and a ghost appearance captured when it separated), recorded whether or not it has focus. All trajectories share the take's clock; on playback a stage appears when it separates, tumbles away, and disappears where it left range, and the whole family loops together (the engine's `LoopSyncParentIdx` already locks a trajectory to another's loop clock).

### 5.3 A member's trajectory ends (its ghost disappears) when

- it goes out of range (KSP unloads it);
- it is destroyed;
- it docks with another member: both trajectories end at the dock and the merged vessel starts a new trajectory with a fresh appearance (one appearance per trajectory, so playback never needs a mid-flight mesh change);
- it is a kerbal that boards a vessel. Boarding a member is a member merge (the kerbal disappears into the hatch, the vessel continues). Boarding an external vessel is the one external interaction that ends only the kerbal's own trajectory.

An ended member is no longer a member.

### 5.4 The take continues through

Focus moving between members, staging, decoupling, breakup, undock, EVA, and docking between members (for example a transposition-and-docking manoeuvre). A vessel can only be extended by its own members.

### 5.5 The take ends when

- focus moves to a non-member;
- a member docks with, claws onto, or otherwise couples to an external vessel (an external tank is still external: including it would need its path from the take's start, which brings back the pop-in problem);
- no members remain;
- the player stops it.

When a kerbal boards an external vessel KSP moves focus to that vessel, so the focus rule ends the take at that moment: on screen the kerbal vanishes at the hatch and the loop ends there. (Open alternative, not chosen: keep recording the remaining members until the player switches back to one.)

### 5.6 Why external vessels are never recorded

1. Which external vessel matters is only known at contact; avoiding pop-in would need every nearby vessel recorded from the take's start, or its earlier path reconstructed, and "nearby" is unbounded (a station 50 km away is visible).
2. External vessels are real, persistent objects. A ghost of a base sits on the real base; a ghost of a station duplicates one that still exists. The recorded vessel itself has moved on by the time the loop replays; an external one often has not.
3. The closed family is pop-in free by construction.

What is given up: a loop that includes the station. The approach is still recorded and the take ends at contact; if the real station still exists at replay, the ghost flies up to it and ends there.

### 5.7 Still to decide

- The focused member is destroyed while others survive: proposed "continue if KSP moves focus to a member, else end".
- Leaving the scene, reverting, or a far switch that reloads the scene: commit the take up to that point, or discard it (today's in-Parsek recorder discards).
- Time warp: members that are loaded but packed keep orbit-only data until they unload (proposed).
- Range: KSP's unload distance (proposed; it is when samples stop anyway) or a fixed Gloops distance.

### 5.8 What the standalone recorder must get right (lessons from the in-Parsek recorder)

The in-Parsek recorder (section 7) shows what a single-pid recorder misses. The standalone take recorder must:

1. Hook rails transitions (orbit segments, the on-rails lifecycle INCLUDING a take started while packed, SOI rotation).
2. Record a destroy ending: final sample, destroyed flag, terminal state.
3. Rebuild module caches and prune departed engine / RCS keys when a vessel is modified, so a dropped stage's engines do not emit terminal shutdown events.
4. Cover every separation: consume the deferred joint-break check and the new-vessel-root `Decoupled` fallback (#263), so symmetric radial decouplers do not leave pieces visible.
5. Take a trajectory's name and end snapshot from the recorded vessel, never from `FlightGlobals.ActiveVessel`.
6. Clear the atmosphere / altitude / SOI boundary flags it raises, and decide an anchor / relative-frame policy (or none).
7. Record N vessels at once: the part-event poller and sampler run per member vessel.

---

## 6. Parsek's own multi-vessel recording (reference, measured 2026-10-05)

For contrast with section 5. Parsek records a tree that follows every vessel that matters, for career purposes:

| Situation | What Parsek records |
|---|---|
| Staging, controlled piece (probe core) | `JointBreak` / DECOUPLE branch point; the active recording continues (breakup-continuous); the child gets its own parent-anchored recording, recorded indefinitely; a Rewind Point when two or more controllable pieces come out |
| Staging, debris (no command part) | own `IsDebris` recording for `BackgroundRecorder.DebrisTTLSeconds = 60` s, or until unloaded / parent packed / destroyed; debris with under 3 parts AND under 0.5 t is skipped (`ParsekFlight.ShouldRecordDebris`); second-generation background splits are not tracked (`MaxRecordingGeneration = 1`) |
| Breakup / crash | splits inside a 0.5 s window coalesce into one `Breakup` branch point (`CrashCoalescer`) |
| Undock | `Undock` branch point, parent closes, two children; the unfocused half is background-recorded; undocks between two background vessels are ignored |
| Dock / claw | `Dock` merge, parents stamped `Docked`, merged child with a full merged snapshot; a claw differs only by `TransferKind=Grapple` |
| EVA / Board | `EVA` split (never a Rewind Point); `Board` merge, both parents stamped `Boarded` |
| Vessel switch | `[`/`]`: old vessel to background, new one continues its own recording or starts a `Launch` branch; Map / TS / KSC Fly: a `VesselSwitchContinuation` segment |
| Not recorded | vessels outside the tree; unloaded physics; background dock / undock; crew transfers as events (`SegmentEventType.CrewTransfer`, `PartEventType.Docked` / `Undocked` are declared but never emitted in production) |

Only trackable vessels branch (`ParsekFlight.IsTrackableVessel`: a `ModuleCommand`, an EVA kerbal, or a SpaceObject).

---

## 7. The in-Parsek Gloops recorder today (to be deleted)

- **What it is.** A second `FlightRecorder` with `IsGloopsMode = true` (`ParsekFlight.StartGloopsRecording`), ticked by `PhysicsFramePatch.GloopsRecorderInstance` alongside the main recorder. It follows ONE vessel pid; any pid change auto-stops and commits (`FlightRecorder.HandleVesselSwitchDuringRecording`). Commit (`RecordingStore.CommitGloopsRecording`) adds one treeless `IsGhostOnly` recording to the "Gloops - Ghosts Only" group, looping off.
- **Gates.** `IsGloopsMode` skips pre-launch resources, the rewind save, route origin proof, the run-cargo manifest and all harvest capture. About 15 career sites exclude `IsGhostOnly` recordings: ledger vessel cost / crew assignment / crew penalty and `PurgeGhostOnlyActionsFromLedger`, crew recovery, pending-ledger vessel match, end-of-recording spawn, CommNet continuation, map-presence retention.
- **Reach.** Players cannot open it (`UiSurface.MainButtonGloops` retired in every mode since 0.10.4). Only the harness seam reaches it: `GloopsStart` / `GloopsStop`, `UiAction op=open window=gloops`, lanes `GL-1`, `GL-2`, `GUI-16`.
- **Known defects** (filed under GLOOPS-EXTRACTION-2026-10-05 in `todo-and-known-bugs.md`; resolved by removal, not fixed in place): a committed take is treeless while `ParsekScenario.OnSave` writes only `RECORDING_TREE` nodes, so takes are very likely lost on save and reload (inferred, not run); a take started while packed freezes after its first sample; auto-stop names and snapshots the take from the NEW active vessel; dropped-stage engine keys are never pruned; the #263 `Decoupled` fallback never runs for it.

**Removal inventory:** `UI/GloopsRecorderUI.cs`; the `ParsekFlight` gloops region and its call sites; `FlightRecorder.IsGloopsMode` and its gates; `PhysicsFramePatch.GloopsRecorderInstance`; `RecordingStore.CommitGloopsRecording` / `GloopsGroupName` and the permanent-root-group special case; the `IsGhostOnly` flag, its codec key and its career exclusions (once no save can carry one); the Basic-mode guard; `TestCommandGloopsVerbs` / `ParsekTestCommandAddon.Gloops.cs` and their dispatcher entries; the gloops `UiAction` window; harness lanes `GL-1`, `GL-2`, `GUI-16` and their registry cells; the tests named after Gloops. Before deleting the codec key, one reload test settles whether any `IsGhostOnly` recording can survive in a save.

---

## 8. Standalone features (future, carried from the 2026-04 draft)

- **`.gloop` file.** A serialized take: header (format version, creator, vessel name, body, duration), one trajectory block per member (track sections, part events, flag events, appearance snapshot), and the loop clock links between members. Gloops owns its own header; Parsek's `.prec` keeps its `PSK0` header. If the binary element writers in `TrajectorySidecarBinary` move to Core (section 10, phase 4), both formats can share the element encoding.
- **Content packs.** `GameData/Gloops/Packs/<pack>/` with a `GLOOPS_PACK` manifest listing loops, anchor body / position, spawn condition (`KSC_LOADED`, `BODY_LOADED`, `DISTANCE`, `ALWAYS`), loop interval and priority; validation on load (missing parts degrade the mesh, broken loops are skipped); per-save enable state.
- **Custom meshes** (non-vessel content: birds, scenery) as a second ghost-builder entry point.

---

## 9. Measured coupling (2026-10-05)

The 2026-04 draft claimed a clean engine core. Measured against `main` at `7b424ca`:

| Area | Lines | State |
|---|---|---|
| `GhostPlaybackEngine*.cs` | ~10.1k | references `RecordingStore.PendingTree` / `CommittedTrees` directly (`TryFindPlaybackRecordingTree`), `ParsekFlight.BodyFixedPrimaryCoversPlaybackUT` (3 sites), `ReFlySessionMarker` (via `FrameContext`), `ChainHandoffLogic`, `DebrisRelativePlaybackPolicy`, `RecordingEndpointResolver`, `IndexShift`, `ParsekConfig` |
| `GhostPlaybackLogic*.cs` | ~14.3k | 14+ static signatures take `Recording`; reaches `RecordingTree`, `BranchPoint`, `GhostChainWalker`, `ParsekScenario`, `GhostMapPresence`; `SpanClock` pulls in the Reaim / mission stack; `WatchMode` is Parsek policy |
| `GhostVisualBuilder*.cs` | ~9.4k | needs `FlightRecorder` (engine-key codec, module classifiers), `VesselSpawner`, `GhostMapPresence.HardenGhostVesselPartPhysics`, `PartStateSeeder` |
| `IPlaybackTrajectory` | 36 members | Parsek-semantic members (`RecordingId`, `ParentAnchorRecordingId`, `LoopAnchorVesselId`, terminal orbit "for ghost map presence"); a mutable `LoopSyncParentIdx {get;set;}`; `LoopTimeUnit` lives in `Recording.cs` |
| `TrajectoryPlaybackFlags` | 16 fields | chain / re-fly / session fields; `GhostPlaybackSkipReason` has 20 Parsek reasons |
| `IGhostPositioner` | 11 methods | `RelativeSectionPlaybackTarget` carries recording ids; only `ParsekFlight` implements it |
| `Rendering/` | ~6.5k | Parsek-side (tied to `Recording`, re-fly), not engine core; the engine never calls it |
| `FlightRecorder*.cs` / `BackgroundRecorder*.cs` | ~14.7k / ~9.8k | part-event DECISIONS (`Check*Transition`) are shared and pure; the per-frame wrappers are duplicated between the two recorders (`BackgroundRecorder.PartEventPolling.cs` says so); both entangled with tree, rewind, logistics, re-fly |
| `TrajectorySidecarBinary` | 1.3k | takes `Recording`, calls ~8 `RecordingStore` healing helpers, writes a Parsek `PSK0` header with a sidecar epoch; element writers are reusable |
| `GhostSoftCapManager` | - | does not exist |

**Clean today (~6k lines, movable with `ParsekLog` + `ParsekConfig`):** the data structs (`TrajectoryPoint`, `TrajectoryPointFlags`, `TrackSection`, `OrbitSegment`, `PartEvent`, `FlagEvent`, `SurfacePosition`, `TerminalState`, `GhostPlaybackState`); the FX stack (`EngineFxBuilder`, `WaterfallCompat`, `PristinePartFxResolver`, `ReStockPatchFxIndex`, `GhostFxEmissionProbe`, `GhostFxFingerprint`, `GhostPartEventApplyLog`, `PlaybackTrace`, `MaterialCleanup`); `RenderingZoneManager` and `GhostAudioPresets`; `ParsekLog` (a deliberate dependency leaf; `ParsekSettings` injects its verbose provider) and `ParsekConfig` (pure constants).

**Test and tooling coupling.** About 342 test files touch engine types, mostly through `internal`. About 12 tests read specific engine / recorder files by path (`LoopUnitSetCoherenceTests`, `GrepAuditNonLoopLivePidTests`, `ReFlyAnchorBypassWiringTests`, `Bug278SnapshotPersistenceTests`, and others); whole-tree grep audits (`scripts/grep-audit-*.ps1`, `GrepAuditTests`) scan `Source/Parsek` only and would silently stop covering moved files; `harness/lib/test_ghostlife.py` and `test_samplingq.py` read engine / recorder files by path and would fail, not skip. 49 in-game test files reference engine types. There is no `.gitmodules`; CI checks out without submodules.

---

## 10. Extraction phases (if and when it is scheduled)

Each phase is behavior-identical for Parsek: `.prec` bytes identical, no schema generation bump, `[Parsek]` log lines identical, xUnit and harness tiers green.

0. **Decisions and doc** (this document).
1. **In-repo Core folder, leaf moves.** A top-level `Gloops/Core/` folder shaped like the future repository, compiled by `Parsek.csproj`. Move the clean leaves (section 9), the engine-key codec and the pure part-event classifiers. Teach every path-reading test, grep audit and harness cell a list of source roots. Mostly mechanical.
2. **Invert the engine's back-edges.** Replace the `RecordingStore`, `ParsekFlight`, `ReFlySessionMarker` and `GhostMapPresence` calls with host interfaces; reduce the skip reason to a skip flag plus a log string; split `GhostPlaybackLogic` (event replay, FX, zones and loop clock to Core; spawn, chain, watch mode and SpanClock / Reaim stay in Parsek). The real work: 9k-line files under active churn, in several small PRs.
3. **Recorder building blocks.** One per-vessel part-event poller and sampler that writes to a sink, replacing the duplicated foreground / background wrappers; `EnvironmentDetector`, `ShouldRecordPoint`, `PartStateSeeder` with it. Worth doing for Parsek on its own (it removes the duplication).
4. **Codec elements.** Binary element writers work on a trajectory DTO; Parsek keeps the `PSK0` header and healing helpers. Golden-byte tests on existing fixtures.
5. **Standalone shell** in `Gloops/Standalone/`: host, positioner, take recorder (section 5), UI, `.gloop` storage. Delete the in-Parsek Gloops feature (section 7); it does not depend on phases 1-4 and can go first.
6. **Repository split.** `git filter-repo --path Gloops/` into the new repository, then mount it back at the same path as a submodule. Wire CI `submodules: recursive` (plus a credential if private), the cloud session-start hook, and worktree setup (`git submodule update --init` per worktree).

---

## 11. Open questions

1. **Whether and when to extract at all.** Raised 2026-10-05: the value of a standalone Gloops, and of the boundary to Parsek, is not yet established; Parsek's recorder carries much more than a ghost recorder needs, and a Core cut too low gives Gloops little, while a cut too high drags Parsek concepts into it. Phase 3 (deduplicating the pollers) and the removal in phase 5 are worth doing for Parsek regardless.
2. The four items in section 5.7.
3. `.gloop` encoding: ConfigNode (ecosystem-consistent, verbose) or the compact binary element encoding.
4. Custom mesh source for content packs: AssetBundles, `.mu`, or OBJ.
