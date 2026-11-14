# Map primitive, lane-qualified watch and trajectory payload - implementation plan

Status: PLAN (2026-11-14), not started. Design sections covered: 15.11 (P3, P3b, P6),
with 15.5 and 15.6 as the consumer contract and 7.7 (watching) for P3b. Phase: R0b
(prerefactors to single-player code, before any race feature code).

## 1. Goal and scope

Three prerefactors to single-player code. Each lands with no behavior change: the same saves,
the same sidecar bytes, the same log lines, the same map and watch behavior.

- **P3, the ghost-map-vessel primitive.** Move the mechanics that make a map ghost a stock
  vessel (build the ProtoVessel node, register its pid in the guard set before `Load`, load,
  harden, remove, strip at save) out of `GhostMapPresence` into a small static service
  `GhostMapVessels` in the Ghost module. The service knows who owns each registered pid:
  the own timeline (the default, no bookkeeping) or another lane (an opaque lane id plus a
  recording id, recorded only when a caller says so; never in single player). The three
  Harmony patch sites that assume an own-timeline ghost ask the service first. Everything
  keyed by recording index, recording id, chain, overlap cycle or own-lane render state
  stays in `GhostMapPresence`.
- **P3b, a lane-qualified watch target.** `WatchModeController` names what it follows by a
  `WatchModeTarget` (lane, index, id) and reaches ghost states and trajectories through a
  lane adapter instead of `host.Engine` and the committed list directly. The own lane keeps
  every behavior; a foreign lane gets camera follow only. Deferred out of R0b (question
  10.2, answered 2026-11-14: later): built when watching a rival flight is scheduled, and
  R3W-26, the camera half of R4-11 and R5-06 wait with it. The first race build shows rivals
  on the map, in the Tracking Station and as ghosts nearby, without the Watch camera.
- **P6, the trajectory payload.** The binary trajectory codec decodes into a plain
  `RecordingTrajectoryPayload` (the six lists plus header facts). `Recording` becomes one
  consumer of it; the two production sites that today build a scratch `Recording` only to
  read a file become payload consumers; a production entry loads a payload from a given path.
  The writer is not touched. Built in parallel with the other prerefactors and merged when
  green, but not inside the P2b window (supervisor decision; section 2).

Not in scope: `RivalMapPresence` (R3W-18), `RivalFlight`, any rival UX on a click (R3W-19),
any race file or directory, the engine lane state (P2b), the catalog port (P2a), `LoopSyncParentIdx`
(P2c), ghost colors and label prefixes (A5: R5-03, R5-04), a spawn throttle for rival map
vessels (R3W-18 owns it, OV-46), the snapshot codec (it already has a production path entry,
section 9), and any change to what is written to disk.

## 2. Starts after

- P12-01, P12-02 (tooling: `[forbidden]` list form, placement rules). None of these tasks
  adds a forbidden edge, so they are a soft prerequisite; listed because 15.11 orders P12
  first.
- P3-01 to P3-05: P3-01 can start as soon as the owner says go. P3-02 lands after P2a-07
  (order fix E.3.8): P2a-07 edits `GhostMapPresence.StateVectors.cs`, a file of the same
  partial class. No other P2a, P2b or P2c task touches the map files (section 5).
- P3b-01 to P3b-04: deferred out of R0b (section 1); when scheduled, they start after
  P2c-04 (the engine's own diagnostics sink and shared spawn budget change the engine surface
  the lane adapter wraps) and after P2b-10 (P2b moves "the watch query" read by the
  positioner and the playback host into lane state, P3b-04 makes that query lane-qualified,
  and P2b-08 and the P2b steps that edit `ParsekFlight.cs` share the watch files).
- P6-01 to P6-05: no prerefactor prerequisite. They share no file with P2a to P2c, P3 or
  P3b. Built in parallel with the other prerefactors and merged when green (design 15.11's
  "P6 last" relaxed by the supervisor; question 10.1). No P6 PR merges inside the P2b window
  (between the P2b-02 and P2b-10 merges), so the P2b flight proof flies one known change set.
- No spike is needed. The decompile facts the tasks rest on (`ProtoVessel.Load` firing
  `onVesselCreate`, hence the pre-registration) are already encoded in the code being moved.

## 3. What the code looks like today

### 3.1 GhostMapPresence (P3)

- One static partial class, tag `"GhostMap"` (`Source/Parsek/GhostMapPresence.cs:33`). It
  holds 36 static collections; the guard set is
  `internal static readonly HashSet<uint> ghostMapVesselPids` (`GhostMapPresence.cs:196`),
  read by `IsGhostMapVessel` (`GhostMapPresence.cs:733-736`). A teardown depth counter
  `ghostTeardownDepth` (`GhostMapPresence.cs:211`) with `BeginGhostTeardown` /
  `EndGhostTeardown` / `IsGhostTeardownInProgress` (`:215`, `:222`, `:229`), and
  `RegisteredGhostMapVesselCount` (`:255`). Test seam `RegisterGhostMapVesselPidForTesting`
  (`:739-742`). `GhostVesselNamePrefix = "Ghost: "` (`:756`).
- The 36 collections split as follows. The primitive needs exactly one: the guard set.
  Everything else stays in `GhostMapPresence` and is own-lane state:
  - per recording index or id, own committed list: `lastKnownByRecordingIndex` (`:69`),
    `vesselsByRecordingIndex` (`:419`), `vesselPidToRecordingIndex` (`:425`),
    `vesselPidToRecordingId` (`:474`), `overlapInstanceVessels` (`:444`),
    `boundaryOverlapSecondaryPids` (`:458`), the TS and flight state-vector caches (`:460`,
    `:463`, `:1621`, `:1628`), `activeReFlyDeferredStateVectorGhostSessions` (`:466`),
    `flightPendingMapVessels` (`:1588`), `flightSoiGapStateVectorExpectedBodies` (`:1591`),
    `flightLastMapOrbitByIndex` (`:1604`), `flightTerminalMapRetentionLoggedIds` (`:1607`),
    `flightChainMapOwner` (`:1615`), `chainTipSegmentsCache` (`:1048`);
  - per chain: `lastKnownByChainPid` (`:72`), `vesselsByChainPid` (`:413`);
  - per-frame draw bookkeeping: `drawnRecordingIdsThisFrame` (`:493`),
    `protoLessCoverageRecordingIdsThisFrame` (`:507`), `paintedRecordingIdsThisFrame`
    (`:538`), `protoBearingRecordingIdScratch` (`:544`), `lastPolylineOwningRealTimePerPid`
    (`:282`);
  - per-pid render state written by the own orbit apply and icon logic:
    `ghostsWithSuppressedIcon` (`:265`), `ghostParkingConicLineHoldUntilUT` (`:327`),
    `ghostOrbitBounds` (`:552`), `ghostBodyFrameOrbitBounds` (`:565`),
    `ghostOrbitLoopShiftedPids` (`:578`), `ghostOrbitEpochShift` (`:595`),
    `ghostIconDrivePropagation` (`:635`), `ghostLastAppliedOrbitBody` (`:691`),
    `ghostLastAppliedOrbitElements` (`:703`), `ghostNoBoundsSuppressLastFrame` (`:866`).
  A rival map presence needs its own equivalents of the per-pid render state; whether it
  reuses these maps is R3W-18's decision, not this plan's.
- Writers of the guard set: `Add` at `GhostMapPresence.ProtoVesselLifecycle.cs:1822` (the
  pre-registration inside `BuildAndLoadGhostProtoVesselCore`), at
  `GhostMapPresence.OverlapInstances.cs:691` and in the test seam (`GhostMapPresence.cs:741`);
  `Remove` at `ProtoVesselLifecycle.cs:277`, `:831`, `:2079`, `OverlapInstances.cs:767`,
  `GhostMapPresence.cs:1483`; `Clear` at `ProtoVesselLifecycle.cs:405` (inside
  `RemoveAllGhostVessels`, production), `GhostMapPresence.cs:1252` (`ResetForTesting`) and
  `:1552` (`ResetBetweenTestRuns`, the in-game runner's safety net, called at
  `InGameTests/InGameTestRunner.cs:665`).
- `RemoveAllGhostVessels` is called from production at `ParsekFlight.cs:1577`
  (`scene-cleanup`), `ParsekTrackingStation.cs:1301` (`tracking-station-cleanup`),
  `ParsekFlightPlaybackHost.cs:2589` (`rewind`) and `GhostMapPresence.cs:1487`.
- The load core `BuildAndLoadGhostProtoVesselCore(IPlaybackTrajectory, Orbit, CelestialBody,
  string logContext, ...)` (`ProtoVesselLifecycle.cs:1800`) already takes an
  `IPlaybackTrajectory`. Callers: `ProtoVesselLifecycle.cs:1486`, `:1545`,
  `GhostMapPresence.FlightMapLifecycle.cs:134`, `GhostMapPresence.StateVectors.cs:1357`. Its
  sequence: `BuildGhostProtoVesselNode` (`:1955-1988`: one `sensorBarometer` part, Owned
  discovery, `ResolveVesselType(traj.VesselSnapshot)`, name
  `GhostVesselNamePrefix + (traj.VesselName ?? "Unknown")`, `vesselSpawning` False, `prst`
  True, `cln` False, `EnsureDefensiveVesselNodes`), `new ProtoVessel`, pre-register (`:1822`),
  add to `flightState.protoVessels`, `pv.Load`, an Error and `RemoveGhostProtoVessel` when
  `vesselRef` is null (`:1830-1838`), `HardenGhostVesselPartPhysics` and
  `NormalizeGhostOrbitDriverTargetIdentity` (`:1847-1848`), a driver-state string built from
  `BuildGhostOrbitDriverIdentity` and `IsVesselRegistered`, the OrbitRenderer force-enable,
  vessel type restore, then the "Created ghost vessel" Info line (its visibility part from
  `BuildGhostProtoVesselVisibilityState`, `GhostMapPresence.Observability.cs:156`, which reads
  `ghostsWithSuppressedIcon`) and `MapRenderTrace.EmitStructural("GhostCreated", ...)`.
- `RemoveGhostProtoVessel(ProtoVessel, bool)` (`ProtoVesselLifecycle.cs:2077-2084`) unregisters
  the pid. `StripFromSave(FlightState)` (`ProtoVesselLifecycle.cs:1345-1359`) removes every
  ProtoVessel whose pid is in the guard set and logs "Stripped {0} ghost ProtoVessel(s) from
  save"; its one production caller is `ParsekScenario.Save.cs:37`
  (`StripGhostMapVesselsFailClosed`, which first reads the set's `Count` at `:33`).
- Helper callers outside the primitive: `ResolveVesselType` from `ParsekFlightMapMarkers.cs`
  (3) and `ParsekTrackingStation.cs` (2); `FindBodyByName` (`ProtoVesselLifecycle.cs:1423`)
  from four `GhostMapPresence` partials and `ParsekFlightGhostPositioner.cs` (4);
  `HardenGhostVesselPartPhysics` from `GhostVisualBuilder.cs` (1);
  `MakeLoadedGhostVesselPhysicsInert` from `Patches/GhostVesselLoadPatch.cs` (1);
  `NormalizeGhostOrbitDriverTargetIdentity` (`TargetingAndSelection.cs:373`) from
  `StateVectors.cs` and `TargetingAndSelection.cs`.
- Outside readers of the guard set field itself: `ParsekScenario.Save.cs:33`,
  `MapRenderProbe.cs:311`, `:500-501`, `MapRender/GhostMapSceneBase.cs:35`,
  `Logistics/RouteEndpointResolver.cs:234`, `:253`, `:353`, `:434`,
  `MapGeometryRecorder.cs:328`, `Patches/GhostOrbitDominantBodyPatch.cs:50`, several in-game
  tests, and 40 xUnit references (e.g. `GhostMapPresenceTests.cs:290-327`). `IsGhostMapVessel`
  appears 164 times in 77 files under `Source/`.
- The three patch sites:
  - `Patches/GhostVesselLoadPatch.cs:253` `GhostIconClickPatch` Prefix resolves the index at
    `:266` through `FindRecordingIndexByVesselPid`, then offers Focus / Set Target / Watch.
  - `Patches/GhostVesselLoadPatch.cs:517-545` `GhostVesselSwitchPatch` Prefix shows the
    "will materialize" ScreenMessage (`:526-530`) when the index is negative, else calls
    `GhostMapWatchHelper.HandleWatchRequest` (`:92`, which itself has the message at
    `:102-106` and `:124-128`).
  - `Patches/GhostTrackingStationPatch.cs:1007-1040` `SelectGhost` resolves index and id at
    `:1012-1013` and builds `TrackingStationGhostSelectionInfo`.
- Source gates on this code:
  - `harness/lib/test_saveparse.py:5918-5925` reads `GhostMapPresence.cs` for
    `GhostVesselNamePrefix = "Ghost: ";` and `GhostMapPresence.ProtoVesselLifecycle.cs` for
    `ProtoVessel.CreatePartNode("sensorBarometer", 0)`; `.claude/CLAUDE.md:257` describes it.
  - `Source/Parsek.Tests/SavePathHardeningTests.cs:136-164` pins exactly one occurrence of
    `GhostMapPresence.StripFromSave(` in production source.
  - `Source/Parsek.Tests/MapRenderTracerCoverageTests.cs:224-249` reads `GhostMapPresence.cs`
    plus `GhostMapPresence.*.cs` and asserts `MapRenderTrace.EmitStructural(` and
    `"GhostCreated"`.
  - The same glob is read by `MapRender/MapPresenceSeamTests.cs:44-62`,
    `MapRender/ShadowRenderDriverTests.cs:600-610` and
    `GhostOrbitLineCascadeDeleteGateTests.cs:54-58` (forbidden cascade symbols);
    `GrepAuditNonLoopLivePidTests.cs:115-120` lists each `GhostMapPresence` file by name.
  - `scripts/ers-els-audit-allowlist.txt:138-147` lists the `GhostMapPresence` files.
- Architecture: a root file starting with `Ghost` lands in module Ghost
  (`scripts/arch/modules.toml:296-298`), layer features (`modules.toml:517`). Lifetimes:
  `GhostMapPresence::ghostMapVesselPids` and `::ghostTeardownDepth` are in the untagged set
  (`scripts/arch/lifetime_pins.toml:80`, `:88`); the type's tagged fields are at
  `scripts/arch/lifetimes.toml:537-548`.
- Map contracts that must not move: the marker decision `ShouldDrawNonProtoMarkerForGhost`,
  the icon floor and `ghostsWithSuppressedIcon` (kept permanent), the Tier-A
  `GhostCreated` / `GhostDestroyed` events and the line-blink stamp sites
  (`docs/dev/design-map-ts-render-architecture.md` Appendix A from `:876`;
  `docs/dev/design-map-ts-render-tracer.md` Appendix A from `:788`). P3 moves none of them.

### 3.2 WatchModeController (P3b)

- `Source/Parsek/WatchModeController.cs` (3791 lines, plus `.Cycle`, `.Diagnostics`,
  `.ExitCameraProbe`, `.Math`, `.Runtime` partials) is module Controllers by
  `^WatchMode` (`modules.toml:320-322`) and is ERS-exempt (`WatchModeController.cs:45-51`,
  `scripts/ers-els-audit-allowlist.txt:148`). It holds `private readonly ParsekFlight host`
  (`:52`).
- Identity is an index into the own committed list: `watchedRecordingIndex` (`:95`, 118
  uses in the main file, 2 in `.Diagnostics`), `watchedRecordingId` (`:96`),
  `watchedOverlapCycleIndex`, lineage protection index / id / until (`:112-114`).
  Public face: `IsWatchingGhost`, `WatchedRecordingIndex`, `WatchProtectionRecordingIndex`,
  `WatchedLoopCycleIndex` (`:145-149`).
- It reads the committed list on 16 lines (`:186`, `:198`, `:629`, `:972`, `:1073`, `:1571`,
  `:2043`, `:2212`, `:2520-2521`, `:2537`, `:2942`, `:3620`, `:3663`, `:3677`) and the engine
  through `host.Engine` 32 times (ghost states, overlap ghosts,
  `EnsureGhostVisualsLoadedForWatch` `:1939` / `:3631`, `loopPhaseOffsets` `:2142`,
  `DestroyGhost` `:2961` / `:3014`, `IsLoopUnitMember` `:3042`,
  `TryGetUnitThroughLineRoot` `:3219`).
- Entry `EnterWatchMode(int index)` (`:1569-1772`) resolves `Recording` and ghost state
  (`TryResolveWatchEntryState`, `:1774`), starts the session (`TryStartWatchSession`,
  `:1875`; `TryCommitWatchSessionStart`, `:1948`) and clears spawn suppression on the
  `Recording` (`:1674`). Recording-only behaviors: chain transfer
  (`TransferWatchToNextSegment`, `:2535`; `FindNextWatchTarget`, `:2511`), lineage protection
  (`PreserveLineageProtectionOnExit`, `:184-198`), loop phase reset (`:2125`), overlap
  retarget (`:3198`), end-hold timer (`:2936`), list rebinding on delete / insert
  (`:3660`, `:3674`, `:3683`).
- Outside readers: `ParsekPlaybackPolicy.cs` (12 `WatchedRecordingIndex`), `ParsekFlight.cs`
  (`:1437`, `:1668`, `:2269-2285`, `:2577-2584`), `ParsekFlightPlaybackHost.cs` (`:1921`,
  `:1975`, `:2322-2327`), `ParsekFlightSpawnHost.cs`, `ParsekFlightGhostPositioner.cs:2484`,
  `ParsekFlight.GhostPositioner.cs:328`, the UI tables and timeline, the test-command
  addon and `Patches/GhostVesselLoadPatch.cs` (`GhostMapWatchHelper.EnterWatchMode`, `:173`).

### 3.3 Trajectory codec (P6)

- `Source/Parsek/TrajectorySidecarBinary.cs` (module Recording by the R0 rule,
  `modules.toml:88-91`): `TryProbe` (`:91-158`), `TryValidatePayload` (`:180-200`, builds
  `new Recording()` and calls `Read`), `Write(path, Recording, sidecarEpoch)` (`:225-288`),
  `Read(path, Recording, probe)` (`:290-384`). `Read` clears the six `Recording` lists in
  place and fills them through private readers that take plain lists (`ReadPointList` `:540`,
  `ReadOrbitSegmentList` `:612`, `ReadPartEventList` `:699`, `ReadFlagEventList` `:741`,
  `ReadSegmentEventList` `:780`, `ReadTrackSections` `:824`). Then: the flat-fallback heal
  when not section-authoritative (`:316-332`), version / generation / id stamped on the
  `Recording` (`:334-337`), the section-authoritative rebuild (`:339-354`), one Verbose
  `ReadBinaryTrajectoryFile` line on either path (`:343-367`), and
  `DropNonMonotonicTrajectoryPoints` with an unconditional Warn (`:373-382`).
- The heal `TryHealMalformedFlatFallbackTrajectoryFromTrackSections(Recording, bool, bool)`
  (`TrajectoryTextSidecarCodec.cs:601-648`) may replace `rec.Points` and `rec.OrbitSegments`
  with new lists, then sets `CachedStats = null`, `CachedStatsPointCount = 0` and either
  `MarkFilesDirty()` or `InvalidateSegmentBodyDisplayLabelCache()` plus a Verbose "healed in
  memory only" line keyed by `rec.RecordingId` (read before `Read` stamps the id). Its
  helpers read only `Points`, `OrbitSegments` and `TrackSections` of the `Recording`
  (`:658-715`, `:717-735`, `:818-930`). `TryBuildBodyFixedPrimaryFlatPointsForRelativeSections`
  is also on the write path through `GetFlatFallbackPointsForWrite` (`:650-656`).
- `RecordingSidecarStore.cs` (module Recording): `TryProbeTrajectorySidecar` (`:2025-2078`),
  `DeserializeTrajectorySidecar(path, probe, rec)` (`:2080-2090`, BinaryV0 only),
  `LoadTrajectorySidecarForTesting(path, rec)` (`:2092-2100`, test-hook name; callers:
  7 xUnit files including `Analyzer/SaveDirectoryLoader.cs`, the analyzer every lane runs).
  The production load reaches `DeserializeTrajectorySidecar` from `:429` (cold load) and
  `:1335` (`TrySummarizeExistingTrajectorySidecar`, `:1300-1352`, which builds a scratch
  `Recording` to count points and sections, calling
  `ShouldWriteSectionAuthoritativeTrajectory(existing)`, `TrajectoryTextSidecarCodec.cs:397`).
- The snapshot side already has a production path entry:
  `SnapshotSidecarCodec.TryLoad(path, out ConfigNode, out probe)` (`SnapshotSidecarCodec.cs:123`),
  called in production at `RecordingSidecarStore.cs:752`, `:830`, `:1152`, `:1837`.
- Data types: `TrackSection` is module Trajectory (`modules.toml:255`); `PartEvent`,
  `FlagEvent`, `SegmentEvent` are module Recording (`modules.toml:364-367`). A payload that
  holds all six lists therefore belongs in Recording; in the Trajectory floor it would add
  backward edges. `^Recording` places a root file in Recording (`modules.toml:308-310`).
- Proof material: `Source/Parsek.Tests/Fixtures/FormatGolden/v1/` holds 7 `.prec` files
  (frozen, gated by `SaveFormatGoldenGateTests`); 9 `.prec` under
  `Source/Parsek.Tests/Fixtures/` in all and 501 under `harness/fixtures/`. The analyzer rule
  `Analyzer/Rules/Inv10CodecRoundtrip.cs:110-125` writes, probes and reads each recording of
  a save and compares lists (not bytes).

## 4. Tasks

### P3: the ghost-map-vessel primitive

**P3-01 Pin the primitive's runtime contract with an in-game test**
- **Change:** new `Source/Parsek/InGameTests/GhostMapPrimitiveRuntimeTest.cs`, category
  `GhostMap`, scenes FLIGHT and TRACKSTATION. Builds a minimal `IPlaybackTrajectory` stub on
  a Kerbin orbit, creates a ghost map vessel through today's
  `GhostMapPresence.BuildAndLoadGhostProtoVessel` path, and asserts: the pid is in the guard
  set at the moment `onVesselCreate` fires (subscribe for the test), the vessel is inert
  (`HardenGhostVesselPartPhysics` effect on the part), its name starts with `Ghost: `, a
  `StripFromSave` over a test-built `FlightState` holding its ProtoVessel and one ordinary
  ProtoVessel removes exactly it, and removal unregisters the pid. Cleans up through
  `RemoveAllGhostVessels`.
- **Proof:** the test passes on today's code; it is then the runtime witness for P3-02 to
  P3-04. No production change.
- **Registers:** the `GhostMap` category is pinned by committed specs
  (`H44-ghost-map-trackstation` and `S1.6-render-parity` run it), so the pinned
  `BATCH_COMPLETE` totals move: run `CommittedBatchTallySourceSyncTests` and
  `MultiCategoryBatchWiringGroupTests` locally and re-pin the specs they name in the same
  change. This task changes only those pinned `BATCH_COMPLETE` tallies in the two spec
  files. It does not change the generated lane catalog `docs/dev/flight-profiles.md`: the
  catalog takes the category NAMES a lane's `RunTests` steps name, not the tallies
  (`harness/lib/lanecatalog.py:1-30`, `:154-161`), and `GhostMap` is an existing category,
  so `test_lanecatalog` stays green with no regeneration. (Index C.1 / E.5.5 list P3-01 as a
  writer of that file; they should drop it.)
- **Size:** S. **Depends on:** none. **PC:** yes, one in-game run (Ctrl+Shift+T, both scenes).
- **Risk:** low; a test-only change whose only coupling is the tally pins.

**P3-02 Move the guard set and the teardown depth into `GhostMapVessels`**
- **Change:** new `Source/Parsek/GhostMapVessels.cs` (module Ghost), `internal static class
  GhostMapVessels` with `internal static readonly HashSet<uint> Pids`, `teardownDepth`,
  `IsGhostMapVessel(uint)`, `BeginTeardown()`, `EndTeardown()`, `IsTeardownInProgress`,
  `RegisteredCount`, `ResetForTesting()`. In `GhostMapPresence`, the field
  `ghostMapVesselPids` becomes `internal static HashSet<uint> ghostMapVesselPids =>
  GhostMapVessels.Pids;` (same instance, so the 30-odd outside readers and the 40 xUnit
  references compile unchanged), and `IsGhostMapVessel`, `BeginGhostTeardown`,
  `EndGhostTeardown`, `IsGhostTeardownInProgress`, `RegisteredGhostMapVesselCount` forward.
  `GhostMapPresence.ResetForTesting` calls `GhostMapVessels.ResetForTesting()` instead of
  writing the moved counter (a direct write would be a new cross-type static write).
  Uncalled-method ratchet (second pass A1): the old entry points chain to the new members,
  so each has a production caller in this pull request (`IsGhostMapVessel` from 160-odd
  sites; `BeginTeardown` / `EndTeardown` from `GhostMapPresence.cs:1458`, `:1479` and
  `OverlapInstances.cs:749`, `:760`; `IsTeardownInProgress` / `RegisteredCount` from
  `GhostTrackingStationPatch.cs:417-418` and `FlightStateGhostBudgetPatch.cs:143`);
  `ResetForTesting` is a test hook by name, which the ratchet leaves out.
- **Proof:** the whole xUnit suite unchanged (`GhostMapPresenceTests` `IsGhostMapVessel_*`,
  `ResetForTesting_ClearsAllState`, `GhostVesselLoadPatchTests`,
  `GhostTrackingStationPatchTests`, `PostLoadStripperTests`, `SavePathHardeningTests`); new
  `GhostMapVesselsTests` (register / unregister / reset / teardown nesting). No new log line.
- **Registers:** `scripts/arch/lifetimes.toml`: a `[[type]] Parsek.GhostMapVessels` entry
  (`Pids` "scene: guard set of live ghost map vessels, emptied by every scene teardown",
  `teardownDepth` "scoped: depth counter, back to 0 at the end of each teardown"); re-pin
  `lifetime_pins.toml` (the two `GhostMapPresence` entries leave the untagged set) and run
  `facts_pins` / `layer_pins` re-pins after a Debug build; `scripts/arch/log_tags.json`
  unchanged (no new tag).
- **Size:** S. **Depends on:** P3-01, P2a-07 (order: same partial class, E.3.8). **PC:** no
  (the in-game test is re-run with P3-04).
- **Risk:** low; same object, same calls, proven by the unchanged suite.

**P3-03 Owner record and a teardown that leaves other lanes' pids alone**
- **Change:** in `GhostMapVessels.cs`, `internal readonly struct GhostMapVesselOwner` with two
  public readonly FIELDS `LaneId` and `RecordingId` (fields, not properties: a field adds no method
  to the uncalled count), one constructor `(string laneId, string recordingId)`, `IsOwnTimeline =>
  LaneId == null`, and `static readonly GhostMapVesselOwner OwnTimeline = new
  GhostMapVesselOwner(null, null)`. `Register(uint pid, GhostMapVesselOwner owner)` adds to `Pids`
  and, only when the owner is not the own timeline, to a `foreignOwners` dictionary.
  `Unregister(uint)` removes from both. `UnregisterAllOwnTimeline()` removes every pid that has no
  foreign owner. Re-point the own writers: `ProtoVesselLifecycle.cs:1822` and
  `OverlapInstances.cs:691` call `Register(pid, GhostMapVesselOwner.OwnTimeline)`; the five `Remove`
  sites call `Unregister`; the production `Clear()` in `RemoveAllGhostVessels`
  (`ProtoVesselLifecycle.cs:405`) calls `UnregisterAllOwnTimeline()`. The two test-reset clears
  (`GhostMapPresence.cs:1252`, `:1552`) stay whole clears. `ResetForTesting` also clears
  `foreignOwners`. Uncalled-method ratchet (A1): every new member has a production caller in this
  pull request. The constructor is reached from the type's static constructor (the `OwnTimeline`
  initializer, a root); `IsOwnTimeline` from `Register`; `Register`, `Unregister` and
  `UnregisterAllOwnTimeline` from the re-pointed own writers. `RegisterForeignForTesting` is a test
  hook by name. No foreign-owner factory is added: the first production code that builds a foreign
  owner is R3W-18, which calls the constructor.
- **Proof:** in single player `foreignOwners` is always empty, so `UnregisterAllOwnTimeline`
  equals `Clear`. xUnit: `GhostMapVesselsTests` adds cases with a foreign owner registered
  through a new `RegisterForeignForTesting` seam: own teardown keeps the foreign pid and its
  owner; `Unregister` drops both. Suite unchanged otherwise.
- **Registers:** `lifetimes.toml` gets `foreignOwners` ("scene: owner of a non-own ghost map
  pid; empty in single player"); lifetimes re-pin.
- **Size:** S. **Depends on:** P3-02. **PC:** no.
- **Risk:** low; the only production behavior edge is a whole-set clear replaced by a
  per-pid filter over an empty dictionary.

**P3-04 Move the ProtoVessel mechanics into `GhostMapVessels`**
- **Change:** move, bodies verbatim apart from re-homing, from
  `GhostMapPresence.ProtoVesselLifecycle.cs` and `GhostMapPresence.TargetingAndSelection.cs`
  into `GhostMapVessels.cs`: `BuildGhostProtoVesselNode` (now taking the finished
  `vesselName` from the caller, so `GhostVesselNamePrefix` stays in `GhostMapPresence.cs`),
  `EnsureDefensiveVesselNodes`, `ResolveVesselType`, `HardenGhostVesselPartPhysics`,
  `MakeLoadedGhostVesselPhysicsInert`, `LogGhostVesselMadeInert`,
  `NormalizeGhostOrbitDriverTargetIdentity`, `BuildGhostOrbitDriverIdentity`,
  `IsVesselRegistered`, `RemoveGhostProtoVessel`, `StripFromSave`, `FindBodyByName`. Split
  `BuildAndLoadGhostProtoVesselCore` (`:1800-1953`): the service gets
  `TryLoadGhostProtoVessel(IPlaybackTrajectory traj, Orbit orbit, string vesselName,
  GhostMapVesselOwner owner, string logContext, out GhostMapVesselLoad load)` covering node
  build, `new ProtoVessel`, pre-registration, `protoVessels.Add`, `Load`, the null
  `vesselRef` Error and removal, hardening, driver normalization, the driver-state string,
  the renderer force-enable and the type restore; `GhostMapVesselLoad` is a readonly struct
  of public readonly fields (`Vessel`, `VesselType`, `VesselName`, `DriverState`,
  `RendererForceEnabled`; fields, so no getter can sit uncalled), built by the service. The
  `GhostMapPresence` core keeps its signature and its four callers, calls the service with
  `GhostMapVesselOwner.OwnTimeline`, and keeps the "Created ghost vessel" Info line (with
  `BuildGhostProtoVesselVisibilityState`) and `MapRenderTrace.EmitStructural("GhostCreated",
  ...)`. The service logs under the same `"GhostMap"` tag with the same texts. Forwarders stay
  on `GhostMapPresence` only for members with production callers outside the service:
  `StripFromSave` (`ParsekScenario.Save.cs:37`), `ResolveVesselType`, `FindBodyByName`,
  `HardenGhostVesselPartPhysics`, `MakeLoadedGhostVesselPhysicsInert`,
  `NormalizeGhostOrbitDriverTargetIdentity`, `BuildGhostOrbitDriverIdentity`,
  `IsVesselRegistered`. The xUnit tests of `EnsureDefensiveVesselNodes` move to call the
  service (a test-only forwarder would count as uncalled). The service never composes a
  vessel name and has no per-tick pass (OV-44): the caller passes the finished name; A5's
  `ComposeGhostVesselName` (R5-04) sits beside `GhostVesselNamePrefix` in
  `GhostMapPresence.cs`, where `test_saveparse` reads the prefix, and the rival caller uses
  it; a rival orbit color is set by R3W-18's tick through `GhostMapVessels.ApplyOrbitStyle`,
  a helper R5-04 specifies and that lands with R3W-18, its first production caller (A1).
  Uncalled-method ratchet (A1): `TryLoadGhostProtoVessel` is called by the
  `GhostMapPresence` core, every moved member keeps its callers (a forwarder or a call inside
  the service), and a moved method that was already uncalled only swaps its key inside the
  bound.
- **Proof:** whole xUnit suite; `GhostMapObservabilityTests`; the P3-01 in-game test and the
  `GhostMap`, `MapPresence`, `MapRender`, `MapView`, `GhostMapOrbits` in-game categories in
  FLIGHT and TRACKSTATION; `H44` and `S1.6`, which run the `GhostMap` batch in the
  harness; harness lanes that pin `GhostMap` lines: `GC-4`, `GUI-9`, `CN-5`,
  `RF-7M`, `RF-8`, `V14T`, `V21T`, `V23T` (TS arrivals) and `FAR-1`, each compared with the
  last green run of the same lane for the count of "Created ghost vessel", "Stripped",
  `GhostCreated` and `GhostDestroyed` lines. Log lines are byte-identical by construction
  (same tag, same format strings, same order).
- **Registers:** `harness/lib/test_saveparse.py:5918-5925` reads the `CreatePartNode`
  literal from `GhostMapVessels.cs` (the prefix literal stays in `GhostMapPresence.cs`);
  `.claude/CLAUDE.md:257` names the new file (then `cp .claude/CLAUDE.md AGENTS.md`);
  `harness/lib/saveparse.py:664-696` comment names it; `SavePathHardeningTests.cs:163` also
  pins one occurrence of `GhostMapVessels.StripFromSave(` (inside the forwarder);
  `GrepAuditNonLoopLivePidTests.cs` gains an `AuditCheck` for `GhostMapVessels.cs`;
  `GhostOrbitLineCascadeDeleteGateTests` gains `[InlineData("GhostMapVessels.cs")]`; the
  CLAUDE.md `GhostMapPresence.cs` key-file entry and the architecture doc Appendix A get one
  sentence each naming the service as the owner of the guard set and the proto build;
  re-pin lifetimes, facts (`uncalledInAssembly` must not rise), layers; no ERS allowlist
  entry (the service reads no store).
- **Size:** M. **Depends on:** P3-03. **PC:** yes, the in-game categories above, and the
  harness lanes on request.
- **Risk:** medium; the load sequence runs inside stock's `onVesselCreate` cascade, so an
  ordering slip shows only at runtime, which the P3-01 test and the lanes catch.

**P3-05 The neutral "whose ghost is this" query at the three patch sites**
- **Change:** `GhostMapVessels.TryGetForeignOwner(uint pid, out GhostMapVesselOwner owner)`.
  Each patch site asks it first; on a foreign owner it skips the own-store resolution, logs
  one Verbose line `[GhostMap] foreign-lane ghost <site> pid=<pid> lane=<id>
  recording=<id>` and returns without the own path:
  `GhostIconClickPatch` Prefix (before `GhostVesselLoadPatch.cs:266`) shows no Parsek menu;
  `GhostVesselSwitchPatch` Prefix (before `:526`) blocks the switch with no ScreenMessage
  (the "will materialize" text is false for a rival flight);
  `GhostTrackingStationPatch.SelectGhost` (before `:1012`) selects nothing. R3W-19 adds the
  rival provider registration method on `GhostMapVessels` (lifetime scene) and replaces
  these refusals with its own behavior (OV-49); this task only makes the query and the branch,
  and adds no register method. Uncalled-method ratchet (A1): `TryGetForeignOwner` and the
  extracted deciders have production callers in this pull request, the three Harmony
  Prefixes (roots by the patch rule), so the query does not wait for R3W-19.
- **Proof:** xUnit in `GhostVesselLoadPatchTests` / `GhostTrackingStationPatchTests` over the
  pure decision each site gets (extract `internal static` deciders if the site has none),
  with a foreign pid registered through `RegisterForeignForTesting`; own-pid cases unchanged.
  In single player the branch is never taken, so no lane can show a difference.
- **Registers:** `docs/dev/design-gui-inventory.md` only if it lists the "will materialize"
  message's conditions (check; the message itself is unchanged). CHANGELOG: none (no player
  change).
- **Size:** S. **Depends on:** P3-03. **PC:** no.
- **Risk:** low; dead in single player, covered by unit cases.

### P3b: the lane-qualified watch target (deferred)

Deferred out of R0b (question 10.2, Q-07, answered 2026-11-14: later, not in the first
build): planned work, built when watching a rival flight is scheduled. What waits with it:
R3W-26 and the camera half of R4-11 (on P3b-04), and R5-06 (on P3b-03 and R3W-26).

**P3b-01 A watch target value**
- **Change:** new `Source/Parsek/WatchModeTarget.cs` (module Controllers by `^WatchMode`):
  `internal readonly struct WatchModeTarget` (`LaneId` string, null for own; `RecordingIndex`
  int; `RecordingId` string; `IsOwnLane`; `None`; `Own(int, string)`). `WatchModeController`
  gets a private `watchedLaneId` field (null) and an `internal WatchModeTarget CurrentTarget`
  property composed from it, `watchedRecordingIndex` and `watchedRecordingId`.
  Uncalled-method ratchet (A1): nothing reads `CurrentTarget`, `IsOwnLane` or `Own` before
  P3b-03, so this task is a specification, not a pull request: the type and the property
  land in P3b-03's pull request with their first production callers (as the playback plan
  did with P2a-01).
- **Proof:** xUnit `WatchModeTargetTests` (equality, `None`, own construction), run in
  P3b-03's pull request.
- **Registers:** lifetimes: none (instance field). Facts re-pin with P3b-03.
- **Size:** none as a pull request (lands in P3b-03). **Depends on:** P2c-04, P2b-10.
  **PC:** no.
- **Risk:** low; additive.

**P3b-02 A lane adapter between the watch camera and the engine**
- **Change:** new `Source/Parsek/WatchModeLane.cs` (Controllers): `internal interface
  IWatchLane` with exactly what the follow path uses: `TryGetGhostState(int, out
  GhostPlaybackState)`, overlap ghost enumeration, `EnsureGhostVisualsLoadedForWatch`,
  `DestroyGhost`, `TryGetTrajectory(int, out IPlaybackTrajectory)`, `Count`, the loop
  schedule queries `host` exposes (`TryGetLoopScheduleForWatch`,
  `ShouldLoopPlaybackForWatch`, `TryResolveUnitMemberPlaybackUTForWatch`,
  `TryComputeLoopPlaybackUTForWatch`, `GetLoopIntervalSecondsForWatch`) and the loop-unit
  lookups (`IsLoopUnitMember`, `TryGetUnitThroughLineRoot`). `OwnWatchLane(ParsekFlight
  host)` implements it over `host.Engine` and `RecordingStore.CommittedRecordings`.
  `WatchModeController` replaces its 32 `host.Engine` uses with `ownLane.<member>`; the 16
  committed-list reads that need a `Recording` (chain transfer, lineage, spawn
  suppression, delete / insert rebinding) stay as they are and stay own-lane only.
- **Proof:** whole xUnit suite; `scripts/refactor/il_compare.py --fold` cannot prove this
  (call targets change), so the proof is the `Watch` in-game category plus the
  `WatchAutoFollow*` / `WatchCoverage*` runtime tests, and the watch lanes `GS-7`, `GS-8`,
  `GS-12`, `FAR-6`, `V10`, `MC-5`, compared with their last green runs for `[Watch]` lines.
- **Registers:** `scripts/ers-els-audit-allowlist.txt` lists `WatchModeLane.cs` with an
  `[ERS-exempt]` comment (same index-keyed rationale as `WatchModeController.cs`); facts and
  layers re-pin.
- **Size:** M. **Depends on:** P2c-04, P2b-10 (P3b-01 is not a pull request; its type lands
  in P3b-03). **PC:** yes, the watch category and lanes.
- **Risk:** medium; a per-frame path, so a missed call or an extra lookup shows as a
  camera hitch or a wrong exit, which the lanes' `[Watch]` lines show.

**P3b-03 Enter and follow by target**
- **Change:** `EnterWatchMode(WatchModeTarget)` becomes the core; `EnterWatchMode(int)`
  builds `WatchModeTarget.Own(index, id)` and calls it, so every existing caller keeps its
  call. For a foreign target the core uses the target's `IWatchLane` (resolved through a
  `Func<string, IWatchLane>` the controller is handed, null in single player, so a foreign
  target is refused with one Verbose line) and runs the follow path only: no chain
  transfer, no lineage protection, no spawn-suppression clear, no loop phase reset, no
  overlap retarget, no rebinding on delete / insert; it exits when the lane reports the
  ghost gone. `OnRecordingDeleted` / `OnRecordingInserted` act only when the target is own.
  This pull request also carries P3b-01 (the `WatchModeTarget` type and `CurrentTarget`),
  whose first production callers are here. Uncalled-method ratchet (A1): the resolver is a
  field with no setter in this task; R3W-26, its first production writer, adds the setter.
- **Proof:** xUnit over the pure decisions this adds (`ShouldRunOwnLaneWatchStep(target)`
  style predicates, refusal of a foreign target with no resolver, with log assertions);
  in-game `Watch` category; the lanes of P3b-02.
- **Registers:** none beyond facts re-pin.
- **Size:** M (with P3b-01). **Depends on:** P3b-02. **PC:** yes, the watch category.
- **Risk:** medium; the own path must stay byte-identical in its decisions, which the
  unchanged lanes show.

**P3b-04 Lane-qualified watch query for the playback lanes**
- **Change:** `WatchedRecordingIndex` and `WatchProtectionRecordingIndex` return -1 when the
  target is foreign (so own playback never protects an unrelated own index); add
  `WatchedTarget`. The lane-state watch query P2b introduces for the positioner
  (`ParsekFlightGhostPositioner.cs:2484`, `ParsekFlight.GhostPositioner.cs:328`) and the
  playback host (`ParsekFlightPlaybackHost.cs:1921`, `:1975`, `:2322-2327`) answers "is this
  lane's index watched" from `WatchedTarget`.
- **Proof:** xUnit on the query with own and foreign targets; whole suite; the watch lanes.
- **Registers:** facts re-pin.
- **Size:** S. **Depends on:** P3b-03, P2b-10. **PC:** yes, the watch lanes on request.
- **Risk:** low; in single player the target is always own and the answers are unchanged.

### P6: the trajectory payload

**P6-01 Commit an identity manifest over every committed trajectory fixture**
- **Change:** new xUnit `Source/Parsek.Tests/TrajectoryCodecFixtureIdentityTests.cs`
  (`[Collection("Sequential")]`) and manifest
  `Source/Parsek.Tests/Fixtures/TrajectoryCodecIdentity/manifest.tsv`. For each committed
  `.prec` under `Source/Parsek.Tests/Fixtures/` and `harness/fixtures/` (found by walking,
  5 `..` to the repo root) one row: relative path, SHA-256 of the file, SHA-256 of a
  canonical dump of what `Read` produced (the six lists through
  `TrajectoryTextSidecarCodec.SerializeTrajectoryInto` into a `ConfigNode`, plus format
  version, schema generation and id), SHA-256 of the bytes `Write` produces from that
  `Recording` (temp file outside the repo), and SHA-256 of the `RecordingStore`-tag lines
  `Read` logged (captured through `ParsekLog.TestSinkForTesting`). The test compares a row
  only when the file SHA matches; rows under `FormatGolden/` must always match (frozen);
  a listed file that is missing fails naming the row. It prints compared and stale counts.
  The manifest is regenerated only when `PARSEK_WRITE_CODEC_MANIFEST=1` is set.
- **Proof:** the test passes on today's code; it is the byte and decode witness for P6-02 to
  P6-05. Check that no fixture-set gate lists the `Fixtures/` directories (if one does,
  register the new folder there).
- **Registers:** none in production. `docs/dev/todo-and-known-bugs.md`: none.
- **Size:** S. **Depends on:** none. **PC:** no.
- **Risk:** low; test-only. 510 files and about 11 MB read in one test.

**P6-02 List-level cores for the flat-fallback heal**
- **Change:** in `TrajectoryTextSidecarCodec.cs`, add list-level forms of
  `TryBuildBodyFixedPrimaryFlatPointsForRelativeSections`,
  `AppendSafeFlatFallbackTailOutsideFrameSections`,
  `TryHealMalformedFlatFallbackPointsFromTrackSections`,
  `TryHealMalformedFlatFallbackOrbitSegmentsFromTrackSections` and
  `HasTrackSectionPayloadMatchingFlatTrajectory`, taking `List<TrackSection>`,
  `List<TrajectoryPoint>`, `List<OrbitSegment>` (and returning a replacement list through an
  `out` / `ref` parameter where today they assign `rec.Points` / `rec.OrbitSegments`). A
  list-level `TryHealMalformedFlatFallbackListsFromTrackSections` returns whether it healed.
  The `Recording` overloads become one-line callers plus the `Recording` side effects, which
  move into one helper `ApplyFlatFallbackHealSideEffects(Recording, bool markDirty)` (cache
  reset, `MarkFilesDirty` or label-cache invalidation plus the existing Verbose line).
- **Proof:** P6-01 manifest unchanged (read and write digests, log digest);
  `Bug458BinaryFlatFallbackHealTests`, `RecordingStorageRoundTripTests`, `SaveFormatTests`,
  `SaveFormatGoldenGateTests`; whole suite.
- **Registers:** facts re-pin (the new list forms are called by the `Recording` forms, so
  `uncalledInAssembly` does not rise); no complexity-watch key moves (none in this file).
- **Size:** S. **Depends on:** P6-01. **PC:** no.
- **Risk:** low; the write path goes through the same list code, which the write digest
  covers.

**P6-03 The payload type; `Read` becomes its first consumer**
- **Change:** new `Source/Parsek/RecordingTrajectoryPayload.cs` (module Recording by
  `^Recording`): `internal sealed class RecordingTrajectoryPayload` with `Points`,
  `OrbitSegments`, `PartEvents`, `FlagEvents`, `SegmentEvents`, `TrackSections` (lists),
  `FormatVersion`, `SchemaGeneration`, `SidecarEpoch`, `ProbeRecordingId`,
  `SectionAuthoritative`, and a `RecordingTrajectoryDecodeStats` struct (sparse-point stats,
  healed flag, pre-heal counts, deduped copies, dropped non-monotonic count). A static
  `OverListsOf(Recording)` builds one whose lists ARE the recording's list instances.
  `TrajectorySidecarBinary.Decode(string path, TrajectorySidecarProbe probe,
  RecordingTrajectoryPayload into)` holds today's body of `Read` minus every `Recording`
  touch and every log line: clear and fill the lists, the list-level heal from P6-02, the
  section-authoritative rebuild, the non-monotonic drop. `Read(path, rec, probe)` becomes:
  payload over `rec`'s lists, `Decode`, assign back any list the heal replaced, call
  `ApplyFlatFallbackHealSideEffects(rec, markDirty: false)` when healed (before the id is
  stamped, as today), stamp version / generation / id, then emit the same Verbose lines and
  the same Warn in the same order from the stats. Uncalled-method ratchet (A1): the payload
  and the stats struct hold public FIELDS, not properties, so a value no consumer reads yet
  (`SidecarEpoch`, `ProbeRecordingId`, read by P6-05 and R3W-04) adds no method; `Decode` and
  `OverListsOf` are called by `Read`, which every committed recording load reaches. The
  payload has one constructor (no arguments), called by `OverListsOf`.
- **Proof:** P6-01 manifest unchanged (this is the step the log digest exists for); new
  xUnit `RecordingTrajectoryPayloadTests`: for every FormatGolden `.prec`, `Decode` into a
  fresh payload equals what `Read` put into a `Recording`; list identity kept (the
  recording's list objects are the same before and after `Read` when no heal ran); a
  throwing decode leaves the same partial state as before (truncated-file case from
  `TryValidatePayload` tests). Analyzer: `Inv10CodecRoundtrip` and
  `SaveDirectoryLoader` run unchanged in every lane's analyzer pass, so any read drift on a
  real save shows as an INV10 finding.
- **Registers:** facts and layers re-pin; `CLAUDE.md` only if the key-file list names the
  codec (it does not today); no save format change, no golden folder, no `SaveFormat.cs`
  step.
- **Size:** M. **Depends on:** P6-02. **PC:** no; the analyzer pass rides on whatever lanes
  are flown next.
- **Risk:** medium; every committed recording loads through this path, so an ordering slip
  in the log or a lost list identity would matter, and the manifest and identity test catch
  both.

**P6-04 `TryValidatePayload` decodes without a `Recording`**
- **Change:** `TrajectorySidecarBinary.TryValidatePayload` (`:180-200`) decodes into a
  scratch payload instead of `new Recording()`. It keeps the unconditional Warn for dropped
  non-monotonic points (today `Read` emits it with the probe's id because the scratch
  recording's id is empty) through one shared `LogDroppedNonMonotonic(string id, int n)`
  helper that `Read` uses too. Uncalled-method ratchet (A1): both new call paths are
  production (`LogDroppedNonMonotonic` from `Read` and `TryValidatePayload`; `Decode`'s
  second caller is `TryValidatePayload`, called in production at
  `RecordingSidecarStore.cs:1096` and not in the uncalled set today).
- **Proof:** `AreRecordingFilesCurrentAtPaths` tests, `Bug585FollowupSaveSkipTests`, the
  truncation and payload-corrupt cases; P6-01 manifest unchanged; a log-capture case pins
  the Warn text.
- **Registers:** facts re-pin (`Decode` now has a second production caller).
- **Size:** S. **Depends on:** P6-03. **PC:** no.
- **Risk:** low; the only output of this path is a bool and a reason string.

**P6-05 A production entry to load a trajectory from a given path**
- **Change:** `RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath(string path, bool
  quietProbe, out RecordingTrajectoryPayload payload, out TrajectorySidecarProbe probe)`:
  probe through `TryProbeTrajectorySidecar(path, out probe, quietProbe)`, refuse an
  unsupported probe, dispatch BinaryV0 to `Decode`, throw on another encoding as
  `DeserializeTrajectorySidecar` does. `TrySummarizeExistingTrajectorySidecar`
  (`:1300-1352`) calls it with `quietProbe: true` and reads counts and the section-authoritative
  fact from the payload (list-level `HasTrackSectionPayloadMatchingFlatTrajectory` from
  P6-02), keeping its `SuppressLogging` scope and the same non-monotonic Warn with the
  caller's recording id. `LoadTrajectorySidecarForTesting` keeps its name and body (the
  analyzer and 7 test files call it). Uncalled-method ratchet (A1): the entry's first
  production caller is `TrySummarizeExistingTrajectorySidecar`, in this pull request
  (reached from the save path at `RecordingSidecarStore.cs:1233`), so it does not wait for
  R3W-04, which is its second caller.
- **Proof:** the shrink-diagnostic tests in `RecordingStorageRoundTripTests` /
  `Bug585FollowupSaveSkipTests`; new xUnit cases for the entry (missing file, magic
  mismatch, unsupported version, a FormatGolden file, a truncated file); P6-01 manifest
  unchanged; a log-capture case that a save's shrink diagnostic writes the same lines.
- **Registers:** facts re-pin (the entry has a production caller, so it is not uncalled).
  Design 15.5 can then name this entry as the `RivalFlight` loader's codec call (R3W-04
  consumes it).
- **Size:** S. **Depends on:** P6-04. **PC:** no.
- **Risk:** low; the summary feeds one diagnostic Warn on save, nothing written.

## 5. Parallel tracks

Three tracks that share no file:

| Track | Tasks | Files |
|-------|-------|-------|
| Map | P3-01 to P3-05 | `GhostMapPresence*.cs`, new `GhostMapVessels.cs`, `Patches/GhostVesselLoadPatch.cs`, `Patches/GhostTrackingStationPatch.cs`, lifetime TOMLs, `harness/lib/test_saveparse.py` |
| Watch (deferred) | P3b-01 to P3b-04 | `WatchModeController*.cs`, new `WatchModeTarget.cs`, `WatchModeLane.cs`, ERS allowlist |
| Codec | P6-01 to P6-05 | `TrajectorySidecarBinary.cs`, `TrajectoryTextSidecarCodec.cs`, `RecordingSidecarStore.cs`, new `RecordingTrajectoryPayload.cs` |

- Inside a track the tasks are sequential. P3-05 can go beside P3-04 once P3-03 is in
  (different files: the patches and the service's query member are new code; take care in
  `GhostMapVessels.cs`, the one shared file).
- Hot files: `GhostMapVessels.cs` (P3-02 to P3-05, then R3W-17, R5-04, R3W-19, in that
  order, OV-48; R3W-17 is its own pull request, made live by its in-game cell, and only
  R5-04's `ApplyOrbitStyle` half lands with R3W-18, its first production caller),
  `WatchModeController.cs` (all of P3b), `TrajectorySidecarBinary.cs` (P6-03, P6-04), the
  lifetime and facts pin files (every task that re-pins; re-pin last after
  merging `main`), `.claude/CLAUDE.md` (P3-04).
- Against P2a / P2b / P2c: the map and codec tracks touch none of the engine,
  `ParsekFlightGhostPositioner.cs` or `ParsekFlightPlaybackHost.cs`.
  `GhostMapPresence.FindBodyByName` is called from the positioner; P3-04 keeps that forwarder so the
  positioner file is not touched. The map track shares the `GhostMapPresence` partial class with
  P2a-07 (`GhostMapPresence.StateVectors.cs`), so P3-02 lands after P2a-07 (E.3.8). The codec track
  is built in parallel and merged when green, but not inside the P2b window (between the P2b-02 and
  P2b-10 merges). The watch track is deferred; when scheduled, P3b-01 to P3b-03 start after P2c-04
  (the engine surface the adapter wraps) and P2b-10, and P3b-04 after P2b-10 (the watch query is
  part of P2b's lane state).
- Against additions: A5 (per-trajectory label prefix and color, R5-04) will touch the ghost map
  vessel name; P3-04 makes the name a parameter of the service, so R5-04 lands after P3-04 (after
  R3W-17 in the `GhostMapVessels.cs` order) and composes the name on the caller's side, with
  `ComposeGhostVesselName` beside the prefix in `GhostMapPresence.cs` (OV-44). P7-01 (recording id
  check) touches `RecordingPaths`, not the codec files; no conflict.

## 6. Tests and proof

- **xUnit (cloud or local):** new `GhostMapVesselsTests`, `WatchModeTargetTests`,
  `TrajectoryCodecFixtureIdentityTests`, `RecordingTrajectoryPayloadTests` and the patch and
  watch decision cases; existing `GhostMapPresenceTests`, `GhostMapObservabilityTests`,
  `GhostVesselLoadPatchTests`, `GhostTrackingStationPatchTests`, `PostLoadStripperTests`,
  `ReFlySaveScrubTests`, `SavePathHardeningTests`, `MapRenderTracerCoverageTests`,
  `MapPresenceSeamTests`, `ShadowRenderDriverTests`, `GrepAuditNonLoopLivePidTests`,
  `GhostOrbitLineCascadeDeleteGateTests`, `Bug458BinaryFlatFallbackHealTests`,
  `Bug585FollowupSaveSkipTests`, `RecordingStorageRoundTripTests`, `SaveFormatTests`,
  `SaveFormatGoldenGateTests`, `ReflyARecordedFixtureCodecTests`, `SyntheticRecordingTests`.
  Each P6 step must leave the P6-01 manifest unchanged: that is the byte-identity proof
  (write digest), the decode-identity proof (dump digest) and the log-identity proof (log
  digest) over 510 committed sidecars.
- **Architecture suites:** DLL-free `scripts/arch` suite on every task; after a Debug build
  the semantic, ilanalysis, layers, lifetimes and ilheadless suites with re-pins where the
  task says.
- **In-game (owner's PC):** P3-01 adds `GhostMapPrimitiveRuntimeTest`; P3-04 runs the
  `GhostMap`, `MapPresence`, `MapRender`, `MapView`, `GhostMapOrbits` categories in FLIGHT and
  TRACKSTATION; P3b-02 and P3b-03 (deferred, when scheduled) run `Watch` and the
  `WatchAutoFollow*` / `WatchCoverage*` tests.
- **Harness lanes (owner's PC, on request only):** after P3-01: `H44`, `S1.6` (the new test's first
  flight inside the pinned batch). After P3-04: `H44`, `S1.6`, `GC-4`, `GUI-9`, `CN-5`, `RF-7M`,
  `RF-8`, `FAR-1`, `V14T`, `V21T`, `V23T`. After P3b-02 / P3b-03 / P3b-04 (deferred, when
  scheduled): `GS-7`, `GS-8`, `GS-12`, `FAR-6`, `V10`, `MC-5`. After P6-03: any daily tier run,
  because every lane's analyzer pass loads its save through `Read` and runs INV10; no dedicated
  lane. For each, the evidence is the verdict plus an unchanged count of the lane's pinned log lines
  against its last green run.
- **Single-player inertness at every step:** no task adds a branch that single player can
  take: the foreign-owner dictionary is empty, the foreign watch resolver is null, and the
  payload path is the same decode.

## 7. Gates and registrations

- **Architecture map:** new root files `GhostMapVessels.cs` (Ghost by `^Ghost`),
  `WatchModeTarget.cs` and `WatchModeLane.cs` (Controllers by `^WatchMode`),
  `RecordingTrajectoryPayload.cs` (Recording by `^Recording`). No new module, no rule
  edit. No forbidden edge: Patches and Controllers (entry) may reference Ghost (features);
  the payload references only Recording and Trajectory types.
- **Layers:** no backward pair added; re-pin `layer_pins.toml` only if a type-pair count
  moves inside an existing backward pair.
- **Lifetimes:** P3-02 and P3-03 tag `GhostMapVessels::Pids` (scene), `::teardownDepth`
  (scoped), `::foreignOwners` (scene) and re-pin `lifetime_pins.toml`. No other task adds a
  static (R3W-19's provider slot on `GhostMapVessels`, lifetime scene, is R3W-19's, OV-49).
- **Facts:** `uncalledInAssembly` must not rise (second pass A1); every new production member has a
  production caller in its own pull request, each task says which: P3-02 and P3-04 chain the old
  entry points to the new members; P3-03's owner constructor is reached from the static initializer
  of `OwnTimeline`; P3-05's query from the three Harmony Prefixes; P6-03's payload holds fields only
  and P6-05's entry is called by `TrySummarizeExistingTrajectorySidecar`; P3b-01 lands in P3b-03's
  pull request; the rival-only members (`ApplyOrbitStyle`, the provider method, a foreign-owner
  build, the watch resolver setter) land with their first callers R3W-18, R3W-19 and R3W-26.
  `*ForTesting` hooks are left out of the count by name (`scripts/arch/facts.toml:105`).
  `crossTypeStaticWrites` must not rise (P3-02's reset note). No `complexityWatch` key moves.
- **Headless pins:** none of the touched modules is headless.
- **Grep-audit gates:** ERS allowlist entry for `WatchModeLane.cs` (P3b-02, deferred with
  P3b). No new thread, no new store read in `GhostMapVessels.cs`.
- **Source gates moved:** `harness/lib/test_saveparse.py:5918-5925` (P3-04),
  `SavePathHardeningTests` (P3-04), `GrepAuditNonLoopLivePidTests` and
  `GhostOrbitLineCascadeDeleteGateTests` extended (P3-04). `MapRenderTracerCoverageTests`
  stays as it is because the `GhostCreated` emission stays in `GhostMapPresence`.
- **Save format:** no change. No `SaveFormat.cs` step, no golden folder. P6-01's manifest is a
  test fixture, not a format.
- **GUI gates:** no new surface, no changed text; `TooltipEchoBudgetTests` unaffected.
- **Docs:** CHANGELOG none (no player change in any task). `todo-and-known-bugs.md`: none
  unless a task finds a defect. `.claude/CLAUDE.md` (and `AGENTS.md`) in P3-04;
  `docs/dev/design-map-ts-render-architecture.md` Appendix A one sentence in P3-04;
  `docs/dev/design-gui-inventory.md` checked in P3-05. Log tags: `"GhostMap"` and
  `"RecordingStore"` keep their modules, so `scripts/arch/log_tags.json` does not change.

## 8. Risks

- **The guard set is shared across lanes.** A rival ghost removed or cleared by the own
  lane would lose its guard and be treated as a real vessel by every
  `IsGhostMapVessel` check. P3-03 fixes the production clear; the unit case with a foreign
  pid notices a regression.
- **Ordering inside `onVesselCreate`.** The pid must be in the set before `pv.Load`. A move
  that reorders this would only show at runtime; P3-01's in-game test asserts it directly.
- **Log identity.** Harness lanes and the log validator read `GhostMap` and
  `RecordingStore` lines. The P6-01 log digest and the lane line counts notice a change.
- **List identity in `Read`.** Code may hold a reference to a recording's list across a
  reload. P6-03 decodes into the recording's own lists; `RecordingTrajectoryPayloadTests`
  checks the instances.
- **Manifest staleness.** A re-harvested harness fixture makes its row stale. The test then
  compares fewer rows and says so; FormatGolden rows never go stale. A P6 PR states the
  compared count.
- **Watch per-frame cost.** The adapter adds an interface call per engine access on the
  watch path. It runs for one watched ghost, not per ghost; the watch lanes' timings in the
  run index would show a regression.
- **Pin churn.** Several tasks re-pin the same TOML files; parallel PRs conflict there.
  Re-pin after merging `main`, never by hand-merging pin lists.

## 9. Corrections and gaps

- The analysis counts `IsGhostMapVessel` at 120 sites in 61 files; a grep today over
  `Source/` finds 164 occurrences in 77 files (tests included). The plan keeps every call
  site through a forwarder, so the count does not change any task.
- Neither document mentions that `RemoveAllGhostVessels` clears the whole guard set
  (`GhostMapPresence.ProtoVesselLifecycle.cs:405`). With a rival lane registered in the same
  set, any own scene cleanup or rewind would unregister rival pids still alive. The plan
  adds P3-03 for it.
- The "about 400 lines" primitive is not cleanly separable as a whole: the load core also
  writes the "Created ghost vessel" line from own-lane icon state (`ghostsWithSuppressedIcon`)
  and emits the `GhostCreated` trace, which `MapRenderTracerCoverageTests.cs:224-249` pins to
  `GhostMapPresence*.cs`. The plan leaves both in `GhostMapPresence`; a rival map presence
  emits its own creation line and trace (design 15.6 is silent on rival tracing).
- Save stripping is lane-agnostic: one strip at save removes every registered ghost,
  whichever lane owns it, and `SavePathHardeningTests.cs:163` pins a single production
  call. Design 15.6 lists "strip at save" as part of the per-lane primitive; the plan keeps
  one strip for all lanes.
- Design 15.11's reason for "P6 last" (it touches saved bytes) does not hold at the file
  level: P6 shares no file with P2a to P2c, P3 or P3b and writes no byte differently (the
  writer is untouched). The plan lets it run in parallel; see 10.1. Settled: P6 is built in
  parallel and merged when green, never inside the P2b window (supervisor decision).
- The analysis lists test-hook-only path loads for trajectories and whole recordings. For
  snapshots a production path entry already exists (`SnapshotSidecarCodec.TryLoad`,
  `SnapshotSidecarCodec.cs:123`), so P6 adds only the trajectory entry. The whole-recording
  loader (`LoadRecordingFilesFromPathsForTesting`, `RecordingSidecarStore.cs:190`) needs no
  production twin, because `RivalFlight` never loads a `Recording`.
- `Read` is more than a decode: it changes `Recording` caches and logs keyed by the
  recording's id before stamping it. The analysis calls the readers "already list-based",
  which is true of the private readers only. The plan keeps those side effects in the
  `Recording` consumer.
- `Inv10CodecRoundtrip` compares decoded lists, not bytes
  (`Analyzer/Rules/Inv10CodecRoundtrip.cs:110-125`). "Analyzer pass in every lane" proves
  decode stability on real saves, not byte identity; P6-01's write digest is the byte proof.
- The 45 watch sites in the analysis (13 list reads, 32 engine uses) are the controller's
  own. Today the controller has 16 committed-list read lines, and about 40 outside reads of
  `WatchedRecordingIndex` / `IsWatchingGhost` across 15 files. P3b keeps the outside reads
  unchanged except the playback lane query in P3b-04.

## 10. Questions for the owner

**10.1 May the trajectory payload change land before the playback prerefactors finish?**
Design 15.11 puts P6 last because it touches saved bytes. In practice it writes nothing
differently and shares no file with the other prerefactors, and its proof (every committed
recording file decodes, re-encodes and logs identically) runs in the normal test suite.
- Option A: build P6 in parallel, merge when green. Shortens R0b by the P6 track.
- Option B: keep it last, as written.
Recommendation: A.

Assumed until the owner says otherwise: A, settled as a designer choice. P6 is built in
parallel and merged when green, but no P6 PR merges inside the P2b window (between the P2b-02
and P2b-10 merges), so the P2b flight proof flies one known change set.

**10.2 Build the rival watch camera's groundwork now, or later?**
Without P3b a rival flight is still seen on the map, in the Tracking Station and as a ghost
near the player, but the Watch camera cannot follow it; a distant rival flight is then map
only (design 7.7 names this as the trim). P3b is four tasks, two of them medium risk on the
per-frame watch path, and the last waits for P2b.
- Option A: build P3b in R0b with the rest.
- Option B: skip it in R0b; build it when watching rivals is scheduled.
Recommendation: A only if watching is in the first race build; otherwise B, since nothing
else depends on it.

Answered 2026-11-14 (Q-07): later, not in the first build, which is B. P3b is deferred out
of R0b and built when watching a rival flight is scheduled; R3W-26, the camera half of R4-11
and R5-06 wait with it. The first race build shows rivals on the map, in the Tracking Station
and as ghosts nearby, without the Watch camera.
