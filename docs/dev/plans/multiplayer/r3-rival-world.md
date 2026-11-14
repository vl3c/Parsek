# The rival world (rival store and rival ghost lane) - implementation plan

Status line: PLAN (2026-11-14), not started. Design sections covered: 6.4, 7.6 (rival store
side), 7.7, 15.5, 15.6, 15.7 (rival store row), 15.13 (scene load and per frame), addition
A5. Phase R3, the "see your rivals" half (import of the payload into memory, display,
isolation gates).

## 1. Goal and scope

Built here:

- `RivalFlight`, the immutable rival recording type that implements `IPlaybackTrajectory`,
  and the loader that fills it from the local rival cache. The loader and `RivalFlight`
  itself (whose interface members are typed with `Recording`-module value types) are the
  only places in `RaceWorld` that name a `Recording` module type. The loader reads metadata
  at store build and the trajectory file only when a ghost first needs it.
- The rival store, rebuilt whole at every scene load from the applied standings in the
  cache (one `RivalStandingVerdict` per rival), keyed by (player id, recording id). There is
  no rival catalog type: the rival engine gets the empty `RecordingPlaybackCatalog.None`
  (OV-47).
- The rival ghost lane in the flight scene (flight view and map view) and in the Tracking
  Station: a second `GhostPlaybackEngine` in flight, `RecordingPlaybackCatalog.None`,
  `RivalPlaybackPolicy` (no spawn, no resource delta, no chain, no CommNet),
  `RivalMapPresence` on the P3 primitive (P3-04, P3-05).
- Leftovers as entries of the rival engine held at their last moment, inside the window the
  owner's standing declares; the launch-area filter on leftovers and flags.
- Rival flags as plaque replicas with a click target that opens the stock plaque dialog
  (spike S7).
- Show or hide per rival (the reader side; the setter is R4-10's).
- The scene calls on R1-08's `RaceRuntime` facade (scene ready, tick, scene exit).
- Addition A5, rival side: the rival lane's style source (on R5-03's
  `PlaybackTrajectoryStyle`) and one ghost-count budget object shared by the two engines, own
  ghosts first, leftovers last.
- Optional watching of a live rival ghost (on P3b), last in the order and deferred with P3b.
- The isolation gates of 6.4 that belong to the lane: the rival-store grep gate and the
  loader entries of R1-06's loader cell, the headless byte-identity test, the in-game
  "changes nothing" categories. The whole-save harness lane is T-27's RACE-4, which this
  slice's acceptance names (OV-05).

Explicitly not built here:

- The standing codec, the copy step, the rival cache layout, the witness log, the chain
  check and the choice of each rival's applied standing (R3E-01, R3E-04, R3E-18, R3E-19).
- The export side (R3E tasks). The board and the Space Race window (R4). The A5 style type
  and the sites that draw it (R5-03, R5-04, R5-05), the look colors themselves and ghost
  kerbal looks (R5-07 onward). The "uses parts you do not have" and "not checked here" notes
  (R4 / R6); this slice only records the facts they read.
- The whole-save harness lane (T-27 RACE-4) and the host lanes of this slice's in-game
  categories (T-29 RACE-10, RACE-11).
- Anything in the Space Center scene (7.7: rivals show there as scenery only).
- Any change to the own lane's behavior. Every task is inert in a single-player save.

## 2. Starts after

Prerefactors (15.11), all landed and green with no behavior change:

- **P2c**: P2c-01 (`LoopSyncParentIdx` off `IPlaybackTrajectory`), needed by R3W-02 (an
  immutable `RivalFlight`); P2c-03 (per-engine diagnostics sink), needed by R3W-06 and by
  R3W-01's order; P2c-04 (`GhostSpawnFrameBudget`, a spawn budget an engine can share),
  needed by R3W-09 and R3W-14.
- **P2a**: P2a-05 (creates `IPlaybackCatalog` and `RecordingPlaybackCatalog`, module
  `Recording`, with the members it calls; P2a-01 is a specification, not a pull request),
  P2a-02 (the engine's catalog argument), P2a-06, P2a-07 (the other catalog sites).
  Needed by R3W-09, which adds `RecordingPlaybackCatalog.None` with its first caller.
- **P2b**: P2b-10 (per-lane state handed to `ParsekFlightGhostPositioner` and the
  late-update helpers, window closed). Needed by R3W-09.
- **P6**: P6-05 (trajectory decode into a plain value plus a production load-from-path).
  Needed by R3W-04. P6 is built in parallel with the other prerefactors and merged when
  green. Until it lands, R3W-04 can use the design's fallback (a private `Recording` inside
  the loader, filled through `TrajectorySidecarBinary.Read`), but only the P6 form keeps
  `RecordingStore.SuppressLogging` out of the read (`TrajectorySidecarBinary.cs:349`).
- **P3**: P3-04 (the ghost-map-vessel primitive in `GhostMapVessels`) and P3-05 (the neutral
  "whose ghost is this" query). Needed by R3W-17 to R3W-20.
- **P3b**: P3b-04 (lane-qualified watch query). Needed only by R3W-26. P3b is deferred out of
  R0b until watching a rival flight is scheduled, so R3W-26 is deferred with it.
- **P12**: P12-01, P12-02 (the list form of `[forbidden]`). Needed by R1-01, which this
  slice builds on.

Other slices:

- **R1**: R1-01 (the three race modules with their layers, forbidden edges and headless
  entry); R1-04 (the race gate); R1-06 (the race grep gates, its shared scanner and the
  "`Recording` types only in the loader" arch cell with its allowlist file
  `scripts/race-loader-files-allowlist.txt`); R1-08 (`RaceSession` with the one static,
  `RaceRuntime`, the `RaceLocal` codec for `Race/local.cfg` including the `SHOW` key);
  R1-10 (a way to create a race save on the dev install: the P10 core and
  `RaceSetup action=create`).
- **R3E**: R3E-01 (`StandingCodec` read side: `RECORDINGS`, `TREES`); R3E-02 (the tree
  payload exported in the production `RecordingTree` node shape, one key list agreed with
  R3W-03); R3E-04 (the rival cache layout, the standing format, the value type
  `RivalStandingVerdict` and `IRacePayload`, whose rival read half is this slice's);
  R3E-14 (`LEFTOVERS` with `vtype`, and `FLAGS`); R3E-18 (the live import, needed by R3W-22
  only). This slice reads only what R3E has copied and accepted ("a complete rival standing
  and its payload are in the local cache").
- **R2**: R2-01's `SiteRow.LaunchAreas`, read through P1-02's
  `LaunchAreaGeometry.Contains`. Needed by R3W-12 and R3W-23 only; earlier tasks run
  without the filter.
- **R5**: R5-03 (`PlaybackTrajectoryStyle`, `IPlaybackStyleSource`, the engine argument;
  lands before R3W-15); R5-04 (`ComposeGhostVesselName` and the map vessel color helper,
  used by R3W-18); R5-07 (`RaceLookTable`: the color of each look and
  `IsKerbalEvaPartName`). R3W-15 ships a neutral placeholder color until R5-07.
- **Test and harness**: T-01 (`SaveWriterDriver`) and T-16 (`RaceFolderBuilder`), used by
  R3W-05 and R3W-29; T-27 (RACE-4, the whole-save lane); T-29 (host lanes RACE-10 FLIGHT and
  RACE-11 TRACKSTATION for this slice's categories).

Spikes:

- **S10** (relative stretches from their body-fixed copy) before R3W-04 is merged. Its count
  decides whether the "fewer than two samples: not drawn" rule is rare or common.
- **S7** (plaque replica, stock plaque dialog with supplied text, click range) before
  R3W-24 and R3W-25.
- **S11** (ghost cost per frame at race scale) before R3W-14; it sets that budget's cap.
- S8 does not gate this slice: the lane opens files in the local cache only, never in the
  shared folder.

## 3. What the code looks like today

### 3.1 The playback interface and the engine

- `IPlaybackTrajectory` is declared at `Source/Parsek/IPlaybackTrajectory.cs:22`. It carries
  `Points`, `OrbitSegments`, `TrackSections` (:25-28), `StartUT` / `EndUT` (:34-35, an outer
  envelope that may be wider than the payload, comment :29-33), `PartEvents` and
  `FlagEvents` (:39-40), `GhostVisualSnapshot` and `VesselSnapshot` (:43-44), the loop
  fields (:59-64), `TerminalStateValue`, `SurfacePos`, `TerrainHeightAtEnd`,
  `PlaybackEnabled`, `IsDebris`, `ParentAnchorRecordingId` (:79), the setter
  `int LoopSyncParentIdx { get; set; }` (:82, removed by P2c) and the terminal orbit fields
  (:85-94).
- `GhostPlaybackEngine` is an instance class (`GhostPlaybackEngine.cs:33`) with ghost state
  keyed by slot index (`ghostStates`, :41), constructed from an `IGhostPositioner` (:362).
  `UpdatePlayback(IReadOnlyList<IPlaybackTrajectory>, TrajectoryPlaybackFlags[],
  FrameContext)` is at :379 and writes the global `DiagnosticsState.playbackBudget`
  (:388-398; P2c gives it a sink).
- Lifecycle events: `OnGhostSpawnPending`, `OnGhostCreated`, `OnGhostDestroyed`,
  `OnPlaybackCompleted`, `OnLoopRestarted`, `OnOverlapExpired` (:267-273), `OnSeamMarker`
  (:281), `OnAllGhostsDestroying` (:283). Policy hooks: `Func<int,bool> IsGhostHeld` (:290),
  `Func<int,int> ResolveChainNextIndex` (:303, null means no chain),
  `IsWatchedGhostStateResolver` (:331).
- `UpdatePlaybackEntry` (:449): a set `skipGhost` goes to `HandleSkippedPlaybackEntry`
  (:466); the entry's `Points` and `HasRenderableGhostData` are read at :472-476, BEFORE the
  activation check at :542. So an entry that has not loaded its payload must be skipped by
  the host, not left to the engine's own before-activation path.
- `HandleSkippedPlaybackEntry` (:751) counts the skip, destroys a live ghost, and fires the
  hidden past-end completion only when `ShouldFireHiddenPastEndCompletion` is true
  (`GhostPlaybackLogic.SpawnDecision.cs:30`), which requires `PlaybackEnabled == false`
  (:39). A rival flight that always reports `PlaybackEnabled = true` never reaches it.
- Skip reasons: `enum GhostPlaybackSkipReason : byte` at `GhostPlaybackEvents.cs:5`, members
  0 to 16 (:7-36); `ToLogToken` at :60; per-reason counters in `CountFrameSkip`
  (`GhostPlaybackEngine.Diagnostics.cs:173`).
- `struct TrajectoryPlaybackFlags` (`GhostPlaybackEvents.cs:141`): `skipGhost`, `skipReason`,
  `skipReasonDetail`, `isMidChain`, `chainEndUT`, `needsSpawn`, `isActiveChainMember`,
  `isChainLooping`, `segmentLabel`, `recordingId`, `vesselPersistentId`, the session flags,
  `anchorReFlyUnstable`, `isChainSeamSuccessor`. `externalGhostCount` (:227-228) is written
  and never read; `GhostPlaybackState.simplified` (`GhostPlaybackState.cs:134`) is never set.
  There is no ghost-count cap anywhere.
- In range: `GhostPlaybackEngine.InRangeRender.cs:325` calls
  `GhostPlaybackLogic.ApplyFlagEvents` for every in-range ghost; :345 applies zone rendering;
  :366 reserves a spawn slot; :415-456 dispatch positioning (orbit tail, interpolate,
  point, surface, orbit).
- Orbit tail: `TryFindOrbitTailPlaybackSegment`
  (`GhostPlaybackEngine.SectionPositioning.cs:637`) plays an orbit segment that starts after
  the last point (:653-671). Past the end: `HandlePastEndGhost`
  (`GhostPlaybackEngine.Completion.cs:107`) positions at the endpoint and queues completion
  (:173); `ResolveRecordingEndpointPlaybackUT` (:513) holds the last sampled pose when the
  explicit end extends past it (:515-517). These two facts make the leftover hold a matter
  of data, not of a new engine mode.
- The one store read in the engine: `TryFindPlaybackRecordingTree`
  (`GhostPlaybackEngine.SectionPositioning.cs:354`, reads `PendingTreeSlot` at :363 and
  `RecordingStore.CommittedTrees` at :371), reached only from
  `ShouldUseLoopAnchoredDebrisChain` (:268), which returns early when
  `ParentAnchorRecordingId` is empty (:272-276).
- Spawn throttle: `TryReserveSpawnSlot` (`GhostPlaybackEngine.SpawnDestroy.cs:436`) against
  `MaxSpawnsPerFrame = 2` (`ParsekConfig.cs:179`), per instance until P2c.
- Ghost build fallback color: `GhostPlaybackEngine.GhostVisuals.cs:84`, sphere at :165.

### 3.2 Flag replay: the side channel

- `GhostPlaybackLogic.ApplyFlagEvents` (`GhostPlaybackLogic.PartEvents.cs:1217`) first calls
  `ApplyFlagPickUps`, returns when `FlagEvents` is empty, else walks a cursor to
  `TrySpawnFlagVessel` (:1498), which calls `GhostVisualBuilder.SpawnFlagVessel` (:1525). That
  builds a real ProtoVessel in `flightState` (`GhostVisualBuilder.cs:1803`), which is saved.
- `ApplyFlagPickUps` (:1371) reads segment events through `(rec as Recording)?.SegmentEvents`
  (:1373; also :1320) and writes `RemovedReplayFlags.RecordRemoval` plus a live removal. A
  trajectory that is not a `Recording` reaches neither.
- Test seams: `SetSpawnFlagOverrideForTesting` (`GhostPlaybackLogic.cs:28`, used at :105) and
  `SetRemoveStandingFlagOverrideForTesting` (`GhostPlaybackLogic.PartEvents.cs:1359`).
- `FlagEvent` (`FlagEvent.cs`) holds ut, site name, placed by, plaque text, `flagURL` (a
  GameDatabase path of the writer's install), lat, lon, alt, rotation, body name.
- Flags are stored only inside the trajectory file: `TrajectorySidecarBinary.Read`
  (`TrajectorySidecarBinary.cs:290`) fills `FlagEvents` at :316 and segment events at :317.

### 3.3 The own lane being mirrored

Policy, `ParsekPlaybackPolicy.cs`:

- The constructor (:103) subscribes to eight engine events.
- `engine.IsGhostHeld = idx => heldGhosts.ContainsKey(idx) || CurrentWatchedRecordingIndex()
  == idx` (:119); `engine.ResolveChainNextIndex = ResolveChainNextSlotIndex` (:127).
- `HandlePlaybackCompleted` (:771) does mid-chain holds and watch auto-follow, retires at the
  KSC (`VesselSpawner.TryRetireEndedFlightAtKsc`, :841), queues spawns
  (`pendingSpawnRecordingIds`, :859), holds ghosts (`heldGhosts`, :879 / :934), spawns
  (`host.SpawnVesselOrChainTipFromPolicy`, :906), activates
  (`DeferredActivateVesselFromPolicy`, :916), counts failures
  (`DiagnosticsState.health.spawnFailures++`, :933), and finally destroys the ghost (:1038).
- Also `RunSpawnDeathChecks` (:165), `FlushDeferredSpawns` (:447), `RetryHeldGhostSpawns`
  (:1047), `HandleGhostCreated` (:1504), `Dispose` (:1973).

Host, `ParsekFlightPlaybackHost.cs` (`class` at :14):

- `ComputePlaybackFlags` (:1532) reads the committed list, ghost CommNet inputs and the
  session suppression state.
- `UpdateTimelinePlaybackViaEngine` (:1870) runs chain positioning, claimed-vessel holds,
  `policy.RunSpawnDeathChecks`, `FlushDeferredSpawns`, `ComputePlaybackFlags`, builds the
  `FrameContext` (:1913-1928: `protectedIndex` from the watch index, `externalGhostCount`,
  `activeReFlyMarker`), drives mission loop units and Reaim substitution, calls
  `host.engine.UpdatePlayback` (:1958), `RetryHeldGhostSpawns` (:1996), the spawn-blocked
  chain tips, `host.mapViewScene.DriveMapPresence` (:2006), `DriveGhostCommNet` (:2012; it
  constructs `new GhostCommNetManager("FLIGHT", ...)` at :2058), the MapRender shadow
  driver, and the watch validation.
- `ApplyGhostPosEntries(phase)` (:69) re-applies `host.ghostPosEntries`;
  `ClampGhostsToTerrain` (:611).

Composition, `ParsekFlight.cs`:

- The positioner is built lazily from the scene object (:950).
- `Start` (:1152): the S9 inert gate (:1157), event subscriptions, `Camera.onPreCull +=
  OnCameraPreCull` (:1198), the watch controller (:1207), `engine = new
  GhostPlaybackEngine(Positioner)` (:1210), engine delegates (:1213-1218), `policy = new
  ParsekPlaybackPolicy(engine, this)` (:1219), the map presence driver (:1222), the
  committed-list index events (:1227-1229).
- `Update` (:1257): returns while a scene change is in progress (:1268); clears
  `ghostPosEntries` (:1288-1289); calls `PlaybackHost.UpdateTimelinePlaybackViaEngine()` in
  TWO places, the pending-split branch (:1324) and the normal path (:1339).
- `LateUpdate` (:1350): `ApplyGhostPosEntries(LateUpdate)` (:1357),
  `ClampGhostsToTerrain` (:1359), `watchMode.UpdateWatchCamera` (:1364).
- `OnCameraPreCull` (:1367): re-apply and clamp (:1399-1401).
- `OnGUI` (:1417): map markers (:1435), watch overlay, `SpawnHost.DrawGhostLabels` (:1444),
  which draws labels for chain ghosts only (`ParsekFlightSpawnHost.cs:688`).
- `OnDestroy` (:1489): CommNet shutdown (:1532), `GhostMapPresence.RemoveAllGhostVessels(
  "scene-cleanup", sceneTeardown: true)` (:1577), `policy?.Dispose(); engine?.Dispose();`
  (:1587-1588), positioner cache clear (:1591).

Tracking Station, `ParsekTrackingStation.cs`: no engine. `Start` (:250) runs the inert gate
(:254) and `GhostMapPresence.CreateGhostVesselsFromCommittedRecordings()` (:262); `Update`
(:286) ticks every `LifecycleCheckIntervalSec = 0.25f` (:40) into
`UpdateTrackingStationGhostLifecycle` (:360) and `DriveGhostCommNet` (:361, method :385);
`OnGUI` (:658); `OnDestroy` (:1285) removes all ghost map vessels (:1301).

### 3.4 Map presence and the guard set

- `GhostMapPresence.ghostMapVesselPids` (`GhostMapPresence.cs:196`) and
  `IsGhostMapVessel(pid)` (:733); test hook `RegisterGhostMapVesselPidForTesting` (:739).
- `BuildAndLoadGhostProtoVesselCore`
  (`GhostMapPresence.ProtoVesselLifecycle.cs:1800`) registers the pid (:1822) before
  `pv.Load`, then hardens physics. `BuildGhostProtoVesselNode` (:1955) builds a one-part
  (`sensorBarometer`), `Ghost: `-named, ORBITING node; the vessel type comes from
  `traj.VesselSnapshot` through `ResolveVesselType` (:2090; `Ship` when null). There is no
  landed variant. `StripFromSave` (:1345, called from `ParsekScenario.Save.cs:37`) drops
  every guarded pid from the saved flight state.
- Index-keyed own presence: `CreateGhostVesselForRecording(int recordingIndex, ...)` (:596),
  `TrackRecordingGhostVessel` (:1365). P3 extracts the build / register / remove / strip
  primitive from this.
- Patches that read the guard set: `GhostVesselLoadPatch.cs` refuses orbit casts
  (:197-208), routes icon clicks by own index (:256-266), refuses `GoOffRails` (:414-424),
  and on `SetActiveVessel` posts "will materialize when its timeline reaches the spawn
  point" for an unknown index (:519-533). `GhostTrackingStationPatch.cs:1008-1020` selects by
  own index or id. `FlightStateGhostBudgetPatch.cs` raises the vessel budget by the guarded
  count. P3's neutral query replaces the three own-timeline assumptions.
- Map icon color: fixed grey (`MapMarkerRenderer.cs:106` `StockVesselIconColor`, drawn at
  :350). Trajectory polyline color: fixed grey
  (`Display/GhostTrajectoryPolylineRenderer.LegDraw.cs:420`).

### 3.5 Recording files the loader reads

- Tree file: `RecordingTree.Load(ConfigNode)` (`RecordingTree.cs:217`). Per recording it
  carries what a header needs (`RecordingTreeRecordCodec.cs`): `vesselName` (:16),
  `explicitEndUT` (:28), `terminalState` (:31), `endpointBodyName` (:35), the terminal orbit
  (:45-52, `tOrbBody` at :52), `TERMINAL_POSITION` (:58-59), `terrainHeightAtEnd` (:64),
  `SURFACE_POS` (:67-70), `loopPlayback` (:123), `isDebris` (:387). No vessel type.
- Trajectory file: `TrajectorySidecarBinary.Read(string path, Recording rec,
  TrajectorySidecarProbe probe)` (:290). A section-authoritative file rebuilds the flat
  lists with `TrajectoryTextSidecarCodec.RebuildPointsFromTrackSections`
  (`TrajectoryTextSidecarCodec.cs:417`, called at `TrajectorySidecarBinary.cs:346`).
- `TrackSection` (`TrackSection.cs:50`): `referenceFrame` (enum at :34: Absolute,
  Relative, OrbitalCheckpoint), `anchorVesselId`, `anchorRecordingId`, `frames`,
  `bodyFixedFrames` (:59), `isBoundarySeam` (:72).
- Missing part prefabs: the ghost build skips them (`GhostVisualBuilder.cs:200-203`) and
  warns once per part (`GhostVisualBuilder.Parsing.cs:120`, "ResolveAvailablePart failed").
  The build node is `GhostVisualSnapshot ?? VesselSnapshot` (`GhostVisualBuilder.cs:460`).
- Test-only loaders today: `RecordingSidecarStore.LoadRecordingFilesFromPathsForTesting`
  (`RecordingSidecarStore.cs:190`), `LoadTrajectorySidecarForTesting` (:2092). P6 gives
  the production entry.

### 3.6 Gates the slice touches

- Module placement: `^Parsek(Flight|KSC|TrackingStation|Scenario|Harmony|Playback)` is
  Controllers (`scripts/arch/modules.toml:322`); a new `^Ghost` root file is Ghost (:298);
  the Core kernel list is explicit (:479). Layers at :511-519; `[forbidden]` at :562-566;
  `[headless.modules]` at :531.
- Grep gates: `Source/Parsek.Tests/GrepAuditTests.cs:47-158`, each a
  `scripts/grep-audit-*.ps1` with allowlist and managed fallback.
- In-game test tallies: `CommittedBatchTallySourceSyncTests` and the lane catalog (a new
  in-game category needs `python harness/tools/lane_catalog.py`).
- Static lifetimes: `scripts/arch/lifetimes.toml`; statics written by another type,
  uncalled methods and headless leaks: `scripts/arch/facts_pins.toml`,
  `scripts/arch/headless_pins.toml`.

## 4. Tasks

Milestone M1 ("one hand-made rival folder plays as a ghost in flight") is R3W-09. Tasks
R3W-01 to R3W-09 are its path; R3W-01 (after P2c-03), R3W-02 and R3W-06 start as soon as
their prerefactor and R1 dependencies have landed; R3W-08 follows R3W-05 and R3W-06.

Uncalled-method ratchet (`uncalledInAssembly`, ruling A5): the live set starts from every
non-production method (`docs/dev/arch/README.md:292-295`), so a member is live when an in-game
test in `Source/Parsek/InGameTests/` or a TestCommands seam verb calls it; an xUnit test does
not count (it is in the other DLL). Every task here is its own pull request. R3W-01 adds no
method (an enum member and cases in existing switches). R3W-02 to R3W-08, whose production
caller is R3W-09, each ship an in-game cell of `RaceRivalLane` (FLIGHT) that calls every member
the task adds and asserts on the result; T-20's `ListHandles kind=rivals` cannot serve, as it
depends on R3W-09. Their input is a scratch rival cache written by the in-game helper
`RivalScratchCache` (`InGameTests/`): whichever of R3W-03 and R3W-04 lands first creates
it (neither depends on the other). R3W-03's part writes the current save's first committed
recording's tree through the production tree writer (the standing is built in memory),
R3W-04's part adds a copy of its `.prec` and `_ghost.craft`, and R3W-05 writes the standing
through R3E-04's `StandingCodec.Write` into a one-rival cache folder in the save's scratch
directory; the folder is deleted after the cell. R3W-02's cell reads the recording directly. A
cell skips, naming the context, when the save has no committed recording. `RivalCacheBuilder`
(created by R3W-05) lives in the test project, so it serves the xUnit tests only. None of these
members is a pure wiring hook, so none moves to R3W-09. R3W-17 and R3W-23 also ship their own
cells and stay their own pull requests; the one member that waits for a later task is
`ApplyOrbitStyle` (R5-04's specification), added by R3W-18's pull request.

### R3W-01 Neutral skip reason for host-filtered entries

- **Change:** `GhostPlaybackEvents.cs`: add `HostFiltered = 17` to `GhostPlaybackSkipReason`
  (additive, runtime-only enum, never persisted), its `ToLogToken` token `host-filtered`,
  and a counter case in `CountFrameSkip` (`GhostPlaybackEngine.Diagnostics.cs:173`). The
  host puts the cause in `skipReasonDetail` as a constant string (`window`, `hidden`,
  `pending`, `not-drawn`, `unplayable`). The own host never sets it.
- **Proof:** xUnit `GhostPlaybackSkipReasonTests.HostFiltered_*`: token, counter, and that
  `HandleSkippedPlaybackEntry` with a fake trajectory (`PlaybackEnabled = true`, past end)
  destroys a live ghost and fires no completion.
- **Registers:** none beyond the test (the enum is not saved; no save format change).
- **Size:** S. **Depends on:** none; lands after P2c-03 (it edits files P2c-03's source gate
  reads, so it is written against that gate). **PC:** no. Its own pull request (no new
  method, so nothing for the ratchet to count).
- **Risk:** low; one additive enum member read by switch statements that have defaults.

### R3W-02 `RivalFlight` value type

- **Change:** new `Source/Parsek/RaceWorld/RivalFlight.cs` (module RaceWorld). `internal
  sealed class RivalFlight : IPlaybackTrajectory` with two parts: a `RivalFlightHeader`
  (player id, recording id, tree id, start and end UT, vessel name, terminal state, endpoint
  body, terminal orbit, terminal and surface position, terrain height, `IsDebris`, leftover
  window, a `Playable` verdict with reason) set at construction, and a payload slot
  (points, orbit segments, sections, part events, ghost snapshot, not-drawn windows) filled
  once by the loader through an internal `AttachPayload` and never changed after. Fixed
  answers: `FlagEvents` an empty shared read-only list; `LoopPlayback` false, loop interval
  0, `LoopAnchorVesselId` 0, loop start and end 0; `ParentAnchorRecordingId` null;
  `VesselSnapshot` null; `PlaybackEnabled` true; `RecordedVesselGuid` null. Before the
  payload is attached every list getter returns an empty list, never null and never a file
  read. Not a `Recording` subclass. Its `IPlaybackTrajectory` members are typed with
  `Recording`-module value types (`PartEvent`, `FlagEvent`, `SurfacePosition`,
  `LoopTimeUnit`), so this task adds `RaceWorld/RivalFlight.cs` to
  `scripts/race-loader-files-allowlist.txt` with that reason (R1-06's "`Recording` types
  only in the loader" cell reads the list).
- **Proof:** xUnit `RivalFlightTests`: each fixed answer, empty lists before attach, attach
  twice refused (logged), `StartUT` / `EndUT` an outer envelope of the payload, and a
  reflection check that `RivalFlight` does not derive from `Recording`.
- **Registers:** none (the RaceWorld folder rule comes with R1-01).
- **Live:** in-game cell `RaceRivalLane.RivalFlightFromOwnRecording` (ratchet, section 4
  intro): builds a `RivalFlight` through the constructor and `AttachPayload` from the
  current save's first committed recording (fields and lists copied in the cell), asserts
  the fixed answers and that a second attach is refused.
- **Size:** S. **Depends on:** P2c-01, R1-01. **PC:** yes (one run of the cell). Its own
  pull request; the category `RaceRivalLane` first appears here (lane catalog regen).
- **Risk:** low; a value type with no callers yet beyond its tests and R3W-03.

### R3W-03 Header loader from the tree file

- **Change:** new `Source/Parsek/RaceWorld/RivalFlightLoader.cs`, the first of the two
  loader files allowed to name `Recording` module types. `TryReadHeaders(treeNode,
  Standing, treeRow, out List<RivalFlightHeader>, out reason)`, where `treeRow` is a
  `Standing.Trees` row (`TREE { id hash proof }`): calls `RecordingTree.Load` on the rival's
  tree file, copies the header fields of each recording that has a `Standing.Recordings` row
  (`REC { id treeId ... }`) with that `treeId`, and drops the `Recording` objects before
  returning. The tree file is the one R3E-02 exports: a
  filtered tree in the production `RecordingTree` node shape, written by the production
  writer with the terminal and endpoint keys included, so this production reader loads it
  (OV-35). The key list is one list, agreed with R3E-02 in R3E-02's pull request; the header
  fields above are this task's side of it. Classifies `Playable`: body unknown to
  this install (a body-name lookup port, live in game, a fake in tests), format newer than
  this build (through `SaveFormat.Classify` on the standing's `recordingFormat` and
  `recordingSchemaGeneration`), or a tree that fails to load. One Verbose line per tree and
  one summary line per rival.
- **Proof:** xUnit `RivalFlightLoaderHeaderTests` over trees built with `RecordingBuilder`:
  fields copied, a missing body gives `unplayable:missing-body`, a newer generation gives
  `unplayable:newer-format`, and a file-open counting port records zero `.prec` and zero
  `.craft` opens.
- **Registers:** `RaceWorld/RivalFlightLoader.cs` in `scripts/race-loader-files-allowlist.txt`,
  the file R1-06's loader arch cell reads. R1-06 lands first, so the entry lands in this
  change or that cell reds; R3W-27 owns the rival loader entries and checks them.
- **Live:** in-game cell `RaceRivalLane.RivalHeadersFromScratchTree`: `TryReadHeaders` over the
  scratch cache's tree file and standing; the header fields equal the own recording's.
- **Size:** S. **Depends on:** R3W-02, R3E-01 (standing rows). **PC:** yes (one run of the
  cell). Its own pull request; it logs the first `RivalStore` line, so it adds the plain tag
  `RivalStore` to R1-06's `RaceLogTags` (`GrepAuditTests.Race.cs`) and refreshes
  `scripts/arch/log_tags.json` (`logtags.py --write`).
- **Risk:** low; reads an existing format through its existing reader.

### R3W-04 Payload loader and the flattening of relative stretches

- **Change:** `Source/Parsek/RaceWorld/RivalFlightLoader.Payload.cs` (second loader file).
  `TryLoadPayload(RivalFlight, payloadPaths, out reason)`: decodes the trajectory through
  P6-05's plain value (fallback: a private `Recording`), then:
  - every `Relative` section with at least two `bodyFixedFrames` becomes an `Absolute`
    section whose `frames` are those body-fixed frames, anchor fields cleared;
  - a `Relative` section with fewer than two becomes a not-drawn window [startUT, endUT];
  - the flat points and orbit segments are rebuilt from the sections with
    `RebuildPointsFromTrackSections` and its orbit sibling;
  - flag events and segment events are not copied into the flight (R3W-23 reads them for
    flags);
  - the ghost snapshot is read from the payload's `_ghost.craft` and its missing parts
    stripped (R3W-21 adds the strip; here it is passed through).
  Logs one line per load: sections, flattened count, not-drawn count, point count.
- **Proof:** xUnit `RivalFlightFlattenTests`: a recording with a relative docking stretch
  that has a full body-fixed copy plays positions equal to the copy; one with a one-sample
  copy yields a not-drawn window; no `Relative` frame survives; `FlagEvents` stays empty
  when the file has flags. A source gate pins that the loader files never call the
  `TryHeal...` path with `markDirty: true` and never write a file.
- **Registers:** CHANGELOG nothing yet (inert); `RaceWorld/RivalFlightLoader.Payload.cs` in
  `scripts/race-loader-files-allowlist.txt` (same reason as R3W-03).
- **Live:** in-game cell `RaceRivalLane.RivalPayloadFromScratchCache`: `TryLoadPayload` on the
  scratch cache's `.prec` and `_ghost.craft`; no `Relative` section survives and the point
  count is the own recording's.
- **Size:** M. **Depends on:** R3W-02, P6-05 (or its fallback), spikes S10a, S10b.
  **PC:** yes (one run of the cell). Its own pull request. P6-05's
  `RecordingSidecarStore.TryLoadTrajectoryPayloadFromPath` already has a production caller
  (`TrySummarizeExistingTrajectorySidecar`), so P6-05 does not wait for this task.
- **Risk:** medium; a flattened stretch may wobble against its partner (design 15.5),
  measured by S10.

### R3W-05 `RivalStore`

- **Change:** new `RaceWorld/RivalStore.cs`. It is built by
  `RivalStore.Build(IReadOnlyList<RivalStandingVerdict>, IRacePayload, IBodyLookup)` from
  each rival's verdict (R3E-04's value type; it replaces the name `AppliedRival`, OV-37)
  plus the tree headers of R3W-03. The verdicts come from the local rival cache, not from
  a sync: this task adds `RaceCore/RivalCache.cs` with
  `ReadVerdicts(IRaceFolder) -> IReadOnlyList<RivalStandingVerdict>`, pure over the port:
  it reads `rivals/index.cfg`, and per rival the cached `player.cfg` and the highest
  standing in `standings/` (only complete standings are ever moved there), and returns one
  verdict per rival with status Applied and its `manifest` (display name, look, slot). A
  scene load in FLIGHT, or a session that starts offline, has no import to ask; the cache
  is the one source, and R3E-18's import builds its Applied verdicts through the same
  reader. The rival cache layout of `RacePaths` (R3E-01 specified it) lands with whichever
  of this task and R3E-18 merges first. The store keeps each rival's display name and look
  from the verdict's manifest (`RivalStore.PlayerOf(playerId)`, added with its first
  reader, R3W-15); it holds `RivalFlight`s keyed by
  (player id, recording id) in a stable order (player id, then start UT, then recording
  id), replaced whole, never edited. Flights whose standing row names no tree, or a payload
  missing from the cache, are kept out with a reason. The payload port is R3E-04's
  `IRacePayload`; this task adds its rival read half (`RaceWorld`, id plus hash to a path in
  the local rival cache, no file opened at build). There is no `RivalCatalog` type (OV-47):
  the rival engine gets the empty `RecordingPlaybackCatalog.None`, which R3W-09 adds with its
  first caller.
  Fixture: this task creates `Source/Parsek.Tests/Generators/RivalCacheBuilder.cs`, which
  writes a hand-made rival
  cache folder (a `player.cfg`, one standing, one tree file, one `.prec` and `_ghost.craft`)
  whose content is built through T-16's `RaceFolderBuilder` (production codec writers,
  `RecordingBuilder` / `VesselSnapshotBuilder` payload), so no second folder builder exists
  (OV-17).
- **Proof:** xUnit `RivalStoreBuildTests`: two rivals, ordering, a removed recording absent,
  key collision across players kept apart, zero `.prec` / `.craft` opens during the build
  (counting port), one summary line `[RivalStore] built rivals=N flights=M
  unplayable=K`. xUnit `RivalCacheBuilderTests`: the built folder round-trips through
  `RivalStore.Build`.
- **Registers:** the store's type names are covered by R3W-27's gate (derived from the
  store's files).
- **Size:** M. **Depends on:** R3W-03, R3E-04 (cache and standing format,
  `RivalStandingVerdict`, `IRacePayload`), T-16. **PC:** yes (one run of the cell). Its own
  pull request, with in-game cell `RaceRivalLane.RivalStoreBuildsFromScratchCache`:
  `RivalStore.Build` over the scratch cache through the rival read half of `IRacePayload`
  (both added here), one flight, zero `.prec` / `.craft` opens during the build.
- **Risk:** medium; the verdict's fields are R3E-04's, so a change there moves this build.

### R3W-06 `RivalPlaybackPolicy`

- **Change:** new `RaceWorld/RivalPlaybackPolicy.cs`. Constructed with a
  `GhostPlaybackEngine` and a watch query delegate. Sets `engine.IsGhostHeld = idx =>
  isWatched(idx)` and `engine.ResolveChainNextIndex = null`. Subscribes to
  `OnPlaybackCompleted` (destroy the ghost; if it is the watched one, ask the host to exit
  watch), `OnGhostCreated` and `OnGhostDestroyed` (Verbose lines with player and recording
  ids). It names no spawner, no `heldGhosts`, no pending-spawn set, no CommNet, no ledger.
  `Dispose` unsubscribes.
- **Proof:** xUnit `RivalPlaybackPolicyTests` with an engine built on a fake positioner (as
  `LoopFirstRunSpawnTests` does): completion destroys and fires nothing else;
  `ResolveChainNextIndex` is null; `IsGhostHeld` is false for any unwatched index;
  `DiagnosticsState.health.spawnFailures` unchanged. The source-sync gate of R3W-28
  forbids the spawn names in this file.
- **Registers:** none.
- **Live:** in-game cell `RaceRivalLane.RivalPolicyOnTestEngine`: builds the policy over an
  engine on an in-game test positioner, asserts `ResolveChainNextIndex` null and `IsGhostHeld`
  false, then `Dispose` (the handlers are bound when it subscribes).
- **Size:** S. **Depends on:** P2c-03 (engine diagnostics sink), R1-01. **PC:** yes (one
  run of the cell). Its own pull request; its Verbose lines are the first under `RivalLane`,
  so it adds that plain tag to `RaceLogTags` and refreshes `log_tags.json`.
- **Risk:** low; it subscribes to existing events and does less than the own policy.

### R3W-07 Rival frame: flags and the open queue (pure)

- **Change:** new `RaceWorld/RivalLaneFrame.cs`, pure. `ComputeFlags(IReadOnlyList<RivalFlight>,
  double ut, Func<string,bool> isShown, TrajectoryPlaybackFlags[] into)`: for each flight,
  `skipGhost = true, skipReason = HostFiltered` with detail `unplayable`, `hidden`,
  `window` (ut outside [start - lead, displayEnd]), `pending` (payload not attached) or
  `not-drawn` (ut in a not-drawn window); otherwise unskipped with `needsSpawn = false`,
  `isMidChain = false`, `chainEndUT = EndUT`, all session flags false, `recordingId` set.
  `SelectOpens(flights, ut, lead, max)` returns at most `max` unopened flights whose window
  starts within `lead`, nearest first.
- **Proof:** xUnit `RivalLaneFrameTests`: a truth table over the five details; no flag ever
  sets `needsSpawn`; `SelectOpens` respects `max` and order; zero allocation per call
  after warm-up (array reused).
- **Registers:** none.
- **Live:** in-game cell `RaceRivalLane.RivalFrameFlagsAndOpens`: `ComputeFlags` and
  `SelectOpens` over the R3W-02 cell's flight at UTs before, inside and after its window.
- **Size:** S. **Depends on:** R3W-01, R3W-02. **PC:** yes (one run of the cell). Its own
  pull request.
- **Risk:** low; pure.

### R3W-08 `RaceRuntime` scene calls

- **Change:** extends R1-08's `RaceWorld/RaceRuntime.cs` (OV-26). R1-08 owns `RaceSession`
  and `RaceRuntime` and the one static, on `RaceSession`; the runtime is an instance owned by
  the session and `internal static RaceRuntime Current => RaceSession.Current?.Runtime`
  forwards (no backing static of its own). Calls added here: `SceneReady(scene)` rebuilds
  the `RivalStore` from the cache (R3W-05's `RivalCache.ReadVerdicts`, then
  `RivalStore.Build`; no sync and no import is needed); `SceneExit()` drops it. No `Tick()`
  or `Sync()` hook is added (a no-op with no caller would move the ratchet): R3E-17's
  `RaceSyncDriver.Tick` is
  the sync entry point. Exposes `Rivals` (the store),
  `CreatePlaybackPolicy(engine, isWatched)`, `IsRivalShown(playerId)` (from the `SHOW` key of
  R1-08's `RaceLocal` codec; default shown).
- **Proof:** xUnit `RaceRuntimeTests`: `Current` is null with no session; scene ready builds
  once per call; scene exit clears; a hidden rival reads hidden. The headless test of
  R3W-29 uses this facade.
- **Registers:** `scripts/arch/lifetimes.toml` only if a static is added (the plan adds
  none).
- **Live:** in-game cell `RaceRivalLane.RaceRuntimeSceneCalls` (race save only, else skip
  naming it): `SceneReady(FLIGHT)` builds `Rivals`, `IsRivalShown` reads the `SHOW` key,
  `CreatePlaybackPolicy` over a test engine, `SceneExit` clears.
- **Size:** S. **Depends on:** R1-08 (`RaceSession`, `RaceRuntime`, `RaceLocal`), R3W-05,
  R3W-06. **PC:** yes (one run of the cell). Its own pull request.
- **Risk:** low; one null check per call site in single player.

### R3W-09 Flight composition (milestone M1)

- **Change:** new `Source/Parsek/ParsekFlightRivalLane.cs` (module Controllers through
  `modules.toml:322`), `internal sealed class ParsekFlightRivalLane`, constructed with the
  `ParsekFlight` host and the `RaceRuntime`. It owns: a P2b lane-state object, `new
  ParsekFlightGhostPositioner(host, laneState)`, `new GhostPlaybackEngine(positioner, ...)`
  in the constructor form that the earlier constructor changes leave (P2c-02, P2c-04,
  P2a-02, R5-03; OV-48) with the empty catalog `RecordingPlaybackCatalog.None` (P2a-02's
  catalog argument), the shared spawn budget below (P2c-04's external-budget overload) and
  its own diagnostics sink (P2c-03), `race.CreatePlaybackPolicy(...)`, the flight list
  (`race.Rivals` flights of shown rivals, fixed for the scene), and the reused flags array.
  Empty catalog: this task adds `RecordingPlaybackCatalog.None` to P2a-05's
  `RecordingPlaybackCatalog.cs` (module `Recording`), a stateless instance that answers
  "none" to every lookup (no pending tree, no tree, no recording), together with this first
  caller, so the uncalled-method ratchet does not move (OV-47). The lane sets it on its own
  lane object (`lane.Catalog`, P2b-09's lane member), so the positioner and the context
  builder read it from there. Rival flights carry no
  parent anchor, so the one engine caller, `ShouldUseLoopAnchoredDebrisChain`, returns
  before it asks.
  Shared spawn budget (OV-46): in a race save `ParsekFlight.Start` builds one
  `GhostSpawnFrameBudget` before the own engine (:1210) and hands it to both engines
  through P2c-04's external-budget
  overload; neither engine owns it, so `ParsekFlight.Update` calls `ResetForFrame()` once per
  frame before the first `UpdateTimelinePlaybackViaEngine()` (P2c-04 has no frame number to
  reset itself, playback 9.9). Outside a race save the own engine keeps the one-argument
  constructor and its owned budget, so single player is unchanged. This budget counts
  spawns per frame; R3W-14's `PlaybackGhostBudget` counts live ghosts and is a different
  budget. Methods and their Unity callbacks in `ParsekFlight.cs`:
  - `Start` (:1152), after the own `policy` is built (:1219): `rivalLane =
    RaceRuntime.Current == null ? null : new ParsekFlightRivalLane(this,
    RaceRuntime.Current, spawnBudget)`, which calls `race.SceneReady(FLIGHT)` and logs one
    line (flights, shown, hidden).
  - `Update`: `spawnBudget?.ResetForFrame()` before the first playback call, then
    `rivalLane?.Tick()` right after BOTH `UpdateTimelinePlaybackViaEngine()` calls (:1324
    and :1339), so own ghosts take the budget first. `Tick` runs
    `SelectOpens` (at most one open per frame in M1), `ComputeFlags`, builds a
    `FrameContext` with `protectedIndex = -1`, `externalGhostCount = 0`,
    `activeReFlyMarker = null`, and calls `engine.UpdatePlayback`. It clears its own
    lane's position entries at the start of its own tick.
  - `LateUpdate` (:1350) and `OnCameraPreCull` (:1367): `rivalLane?.ReapplyAndClamp(phase)`
    after the own lane's re-apply and clamp.
  - `OnDestroy` (:1489), before `policy?.Dispose()` (:1587): `rivalLane?.Dispose()`
    (policy, engine, lane state, `race.SceneExit()`).
  Not called by the lane (each pinned by R3W-28): `RunSpawnDeathChecks`,
  `FlushDeferredSpawns`, `RetryHeldGhostSpawns`, `DriveMapPresence`, `DriveGhostCommNet`,
  `GhostCommNetManager`, `DriveMissionLoopUnits`, `SubstituteReaimTrajectories`, the
  committed-list events, `ParsekFlightMapMarkers`, `DrawGhostLabels`.
  Injector: `RivalCacheBuilder` and its fixture are created by R3W-05; this task adds the
  injector test
  `InjectRivalCache` (like `InjectAllRecordings`), which drops R3W-05's folder into a race
  save on the dev install made through R1-10.
- **Proof:** xUnit `RecordingPlaybackCatalogTests` gains cells: `None` answers "none" to
  every variant and holds no state. In-game category `RaceRivalLane` (FLIGHT only, OV-09),
  `[InGameTest(Scene = FLIGHT)]`
  `RivalFlightPlaysAsGhost`: skips unless the save is a race save with the injected
  folder; warps to inside the flight's window; asserts a ghost GameObject exists in the
  rival engine, none in the own engine for that id, `FlightGlobals.Vessels` count
  unchanged, and the log line `[RivalLane] ghost created player=... rec=...`.
  Owner proof: run `InjectRivalCache`, load the race save, see the ghost fly.
- **Registers:** `CHANGELOG.md` (first entry: rival flights from a race folder play as
  ghosts in flight), todo entry, the in-game category in the lane catalog
  (`python harness/tools/lane_catalog.py`); the category's host is T-29's RACE-10
  (FLIGHT), so once T-29 has landed, every new `RaceRivalLane` test re-pins RACE-10's batch
  tally (`CommittedBatchTallySourceSyncTests`) in the same change; `design-gui-inventory.md`
  not touched (no new surface); the plain log tags `RivalStore` and `RivalLane` are already
  listed (R3W-03, R3W-06).
- **Size:** M. **Depends on:** R3W-04, R3W-05, R3W-06, R3W-07, R3W-08, P2a-02, P2a-05,
  P2a-06, P2a-07, P2b-09, P2b-10, P2c-04, R1-10 (race save creation), R3E-04 (cache format
  and `RivalStandingVerdict`). **PC:** yes (the in-game test and the owner look).
- **Risk:** medium; first time two engines share a scene; P2b is the protection, and the
  in-game test plus a normal single-player flight lane prove both lanes.

### R3W-10 Lazy open budget and payload lifetime

- **Change:** `ParsekFlightRivalLane.Tick` and `RivalLaneFrame.SelectOpens`: open lead
  `RivalOpenLeadSeconds` (game seconds, scaled by the current warp rate so a 1000x warp
  opens far enough ahead), at most one open per frame, a per-scene cap on opens logged when
  hit. An open that fails marks the flight `unplayable:payload` (one Warn per flight per
  scene). Payloads stay attached until scene exit.
- **Proof:** xUnit: lead scales with warp; one open per frame; a failure is not retried in
  the scene. In-game `RaceRivalLane.LazyOpenAtWindow`: log shows no open at scene ready and
  one open when the window nears.
- **Registers:** none.
- **Size:** S. **Depends on:** R3W-09. **PC:** yes (in-game check).
- **Risk:** low; worst case a ghost appears one frame late.

### R3W-11 Leftovers held at their last moment

- **Change:** `RivalFlightLoader` and `RivalStore`: the `Standing.Leftovers` rows (R3E-14's
  `LEFTOVERS { L { recordingId fromUT untilUT vtype } }`) set the header's leftover window
  [fromUT, untilUT]. This task is the one that reads the row's `vtype` key: it is copied onto
  the header's leftover (`LeftoverVesselType`, empty when unknown), which R3W-18 uses for the
  map icon. An empty `untilUT` maps to the named constant
  `RivalFlight.OpenLeftoverEndUT` (a large finite UT, never infinity). Debris and terminal
  states Recovered / Destroyed never get one. With a leftover:
  - the display end becomes `untilUT` and `EndUT` widens to it (allowed by the envelope rule,
    `IPlaybackTrajectory.cs:29-33`);
  - an orbit leftover appends one orbit segment built from the terminal orbit, from the last
    point to `untilUT`, so the engine's orbit tail (`SectionPositioning.cs:637-671`) moves
    the ghost along its orbit;
  - a surface leftover appends nothing: the endpoint hold
    (`Completion.cs:513-517`) keeps the last pose.
  `RivalLaneFrame` treats a leftover as `window` filtered while the clock is in [EndUT of
  the flight, fromUT) and drops it after `untilUT`. A leftover in the far past whose end
  point is outside visual range stays `pending`: its payload is opened only when the
  header's end position is within the lane's open radius of the active vessel.
- **Proof:** xUnit `RivalLeftoverTests`: `ShouldUseOrbitTailPlayback` true inside an orbit
  leftover; `ResolveRecordingEndpointPlaybackUT` equals the last point for a surface one;
  debris and destroyed flights get no leftover; open-ended window stays finite. In-game
  `RaceRivalLane.LeftoverStaysInOrbit` and `LeftoverStandsOnSurface` (PC).
- **Registers:** todo entry; CHANGELOG entry of R3W-09 updated.
- **Size:** M. **Depends on:** R3W-09, R3E-14 (`LEFTOVERS`). **PC:** yes.
- **Risk:** medium; the in-range dispatch past the last point with a widened end has to
  behave as the two static helpers say; the in-game tests are the proof.

### R3W-12 Launch areas kept clear

- **Change:** `RivalStore.Build` drops a leftover whose end position (terminal position or
  surface position) lies inside a launch area: `SiteRow.LaunchAreas` of R2-01's `SiteTable`,
  read through P1-02's `LaunchAreaGeometry.Contains` (no `RaceLaunchAreas.cs`, OV-41), one
  Verbose line per dropped leftover and a count in the summary. Flags get the same test in
  R3W-23. Replays are never filtered.
- **Proof:** xUnit `RivalStoreLaunchAreaTests`: a leftover on a pad dropped, one 1 km away
  kept, the flight itself kept.
- **Registers:** none.
- **Size:** S. **Depends on:** R3W-11, R2-01 (`SiteRow.LaunchAreas`), P1-02
  (`LaunchAreaGeometry.Contains`).
  **PC:** no.
- **Risk:** low; a pure predicate owned elsewhere.

### R3W-13 Show or hide a rival (reader side)

- **Change:** `ParsekFlightRivalLane` builds its list from shown rivals only at scene ready
  (a hidden rival costs nothing: no flight in the list, no open, no map object); a toggle
  read mid-scene through `race.IsRivalShown` skips that rival's entries as `hidden` until
  the next scene. The `SHOW` key is in R1-08's `RaceLocal` codec; the setter lands with its
  first production caller, R4-10 (OV-27).
- **Proof:** xUnit: a hidden rival contributes zero entries and zero opens.
- **Registers:** none.
- **Size:** S. **Depends on:** R3W-09, R1-08 (`RaceLocal` and its `SHOW` key). **PC:** no.
- **Risk:** low.

### R3W-14 A5: one ghost budget for both engines

- **Change:** new floor type `Source/Parsek/PlaybackGhostBudget.cs` with an explicit Core
  placement (beside `IPlaybackTrajectory` in `modules.toml:479`). Three priority classes
  (own, rival flight, leftover), a per-frame `Begin(frame)`, `TryTake(class)` and a cap.
  The own engine takes from it without a limit, so single player is unchanged; the rival
  engine gets `max(0, cap - ownInRange)`, flights before leftovers. The engine is handed
  the budget at construction (default: an unlimited instance; the argument is the last
  constructor change, after P2c-02, P2c-04, P2a-02 and R5-03, and this task updates both
  construction sites, the own engine and R3W-09's rival engine) and consults it where it
  reserves a spawn slot (`SpawnDestroy.cs:436`, as P2c-04 rewrote it); a refused entry is
  skipped like a spawn throttle. Logs a rate-limited summary when the rival lane is cut.
  This budget counts live ghosts; P2c-04's `GhostSpawnFrameBudget` counts spawns per frame
  and is a different object with a different job (OV-46).
- **Proof:** xUnit `PlaybackGhostBudgetTests`: own never refused; leftovers refused before
  flights; reset per frame. Existing engine suites unchanged (default instance).
- **Registers:** `modules.toml` Core rule; headless pins re-pinned if the type moves a
  count; `lifetimes.toml` none (instance).
- **Size:** S. **Depends on:** P2c-04 (the spawn-slot check it extends), R5-03 (the
  constructor form before it), R3W-09, spike S11 (ghost cost per frame at race scale; its
  measurement sets the cap). **PC:** no.
- **Risk:** low; inert for own ghosts by construction.

### R3W-15 A5: per-trajectory style

- **Change:** shrunk to the rival lane's style source (OV-43). R5-03 owns the type
  `PlaybackTrajectoryStyle` (a sealed class, null = no style), `IPlaybackStyleSource.StyleFor`
  and the engine's optional constructor argument; R5-03, R5-04 and R5-05 own the sites (the
  fallback sphere, the map vessel name and orbit color, the Parsek-drawn overlays). This
  task adds `RaceWorld/RivalStyleSource.cs : IPlaybackStyleSource`, which maps a flight to
  its player and returns one style per player: `LabelPrefix = "<display name> - "` (the one
  prefix format, a designer choice, OV-45; display name from the rival store, which holds
  it from the verdict's manifest, R3W-05) and the color of that rival's look, a neutral
  grey placeholder until R5-07's `RaceLookTable` provides the colors. It is handed to the rival
  engine through R5-03's
  argument, to `RivalMapPresence` (R3W-18) and to the flight labels (R3W-16). The own lane
  passes no source, so every own site keeps its present color.
- **Proof:** xUnit `RivalStyleSourceTests`: one style per player, the prefix `Ana - ` for
  display name `Ana`, the placeholder color while no look color is supplied, the same
  instance for two flights of one player.
- **Registers:** `design-gui-inventory.md` note that rival objects carry the prefix and are
  tinted (the tint when R5 fills the color). No `modules.toml` change (R5-03 adds the Core
  rule; the source file is under `RaceWorld/`).
- **Size:** S. **Depends on:** R3W-09, R5-03 (lands first). **PC:** no.
- **Risk:** low; the own lane has no source.

### R3W-16 Labels on rival ghosts in flight

- **Change:** `ParsekFlightRivalLane.DrawLabels()` called from `ParsekFlight.OnGUI` beside
  `SpawnHost.DrawGhostLabels` (:1444): the existing label drawing of
  `ParsekFlightSpawnHost.cs:688` lifted into a shared helper that takes a position, text and
  color; rival text is `<style.LabelPrefix><vessel name>`, for example `Ana - Kerbal X`
  (OV-45). Drawn only for live rival ghosts inside the label range. Question 10.3 is
  assumed answered yes (see section 10).
- **Proof:** xUnit on the helper's text and culling decision; in-game screenshot check on
  the owner's PC.
- **Registers:** `design-gui-inventory.md` (label kinds); UI-scale gate if the helper moves.
- **Size:** S. **Depends on:** R3W-15. **PC:** yes.
- **Risk:** low.

### R3W-17 Landed variant of the ghost map vessel primitive

- **Change:** in the P3 service `GhostMapVessels` (module Ghost, P3-04): `BuildLanded(body,
  lat, lon, alt, name, vtype)` beside the orbit build: the same one-part, guard-registered,
  physics-inert vessel with a LANDED situation. Neutral; no caller in single player except a
  test before R3W-18. Its own pull request: the in-game cell below calls `BuildLanded`, so
  it is live (ratchet, section 4 intro). `GhostMapVessels.cs` order (OV-48): P3-02 to P3-05,
  this task, `ApplyOrbitStyle` (R3W-18's pull request), then R3W-19.
- **Proof:** xUnit on the node builder (situation, lat/lon/alt, name prefix). In-game
  `RaceRivalLane.LandedMapVesselIsInert`: registered in the guard set, not loaded off rails
  near the active vessel, absent from the saved game after a save. This cell is what makes
  `BuildLanded` live in this pull request.
- **Registers:** `headless_pins.toml` if it moves a Ghost leak (it is live code).
- **Size:** S. **Depends on:** P3-04, P3-05. **PC:** yes.
- **Risk:** medium; a landed proto vessel near the active vessel is in physics range and
  relies on the `GoOffRails` refusal (`GhostVesselLoadPatch.cs:414-424`).

### R3W-18 `RivalMapPresence`

- **Change:** new `RaceWorld/RivalMapPresence.cs`: instance state keyed by (player id,
  recording id). Pure `Decide(flight, ut, shown)` returns `None`, `Orbit(segment)` (a live
  flight inside an orbit segment, or an orbit leftover from its terminal orbit) or
  `Landed(position)`; a hidden rival or a filtered leftover is `None`. `Tick(ut)` diffs the
  decisions against what exists and creates or removes through the P3 service
  `GhostMapVessels` (orbit or R3W-17's landed build). The style comes from R3W-15's source:
  the name is composed by `GhostMapPresence.ComposeGhostVesselName` (R5-04, beside
  `GhostVesselNamePrefix`; live through the own path), giving `Ghost: Ana - Kerbal X`, and
  passed to the service finished (P3-04's reading, OV-44); the orbit color is set by this
  tick through `GhostMapVessels.ApplyOrbitStyle(Vessel, PlaybackTrajectoryStyle)` and its
  pure `NeedsOrbitColor`. This task's pull request adds `GhostMapVessels.ApplyOrbitStyle`
  and `NeedsOrbitColor` (specified by R5-04), with their xUnit cells and R5-04's in-game
  cell, because this tick is their first production caller (the service has no per-tick
  pass). Never touches `GhostMapPresence`'s own
  collections, never constructs `GhostCommNetManager`. Vessel type: for a leftover the
  header's `LeftoverVesselType` (the `vtype` key R3W-11 reads, R3E-14, OV-36), else from
  the ghost snapshot once the
  payload is open, else `Ship`. Map spawn throttle (OV-46): P2c-04's spawn budget does not
  cover map presence and P3 plans none, so this type owns its own per-tick create budget (a
  fixed small count of map vessel creates per tick, the rest wait for the next tick, one
  rate-limited line when it cuts). `RemoveAll(reason)` at scene exit.
  Flight wiring: `ParsekFlightRivalLane` calls `Tick` every 0.25 s and `RemoveAll` in its
  `Dispose`. Rivals inside an atmosphere get no map object (question 10.2, assumed (a)).
- **Proof:** xUnit `RivalMapPresenceDecideTests` (truth table) and a throttle case (more
  creates than the per-tick budget spread over ticks). In-game
  `RaceRivalLane.RivalMapVesselIsGuarded`: `IsGhostMapVessel` true; not in the saved
  `FLIGHTSTATE` after a quicksave; not a CommNet node; targetable.
- **Registers:** todo entry; CHANGELOG entry updated.
- **Size:** M. **Depends on:** R3W-09, R3W-11, R3W-15, R3W-17, P3-04, P3-05, R5-04.
  **PC:** yes.
- **Risk:** medium; stock walks over the vessel list are guarded only through the set,
  which R3W-29 and R3W-30 prove.

### R3W-19 "Whose ghost" answers for rivals

- **Change:** this task adds the provider method on `GhostMapVessels` (OV-49): a
  register / unregister pair for the rival provider of P3-05's neutral query, its slot
  written only by `GhostMapVessels`' own methods (never a static field written from
  RaceWorld), lifetime `scene`. P3-05's `TryGetForeignOwner` and its branch at the three
  patch sites stay as they are; this task replaces the branch's plain refusals with the
  provider's answers. `RivalMapPresence` registers at scene ready and unregisters at scene
  exit. The three patch sites then answer for a rival map vessel: `SetActiveVessel` refused
  with the message "<vessel> is
  <rival>'s ghost and cannot be flown" in place of the "will materialize" text
  (`GhostVesselLoadPatch.cs:519-533`); the TS selection shows the name and no Fly; the map
  watch menu offers Watch only when R3W-26 is in (deferred with P3b) and the ghost is live.
- **Proof:** xUnit on the provider; in-game `RaceRivalLane.RivalGhostCannotBeFlown` (the
  click is refused and the message names the rival).
- **Registers:** `design-gui-inventory.md` screen-message count +1; `lifetimes.toml`: the
  provider slot on `GhostMapVessels` tagged `scene` (cleared at scene exit); lifetimes
  re-pin.
- **Size:** S. **Depends on:** R3W-18, P3-05. **PC:** yes.
- **Risk:** low.

### R3W-20 Tracking Station composition

- **Change:** new `Source/Parsek/ParsekTrackingStationRivalLane.cs` (Controllers). In
  `ParsekTrackingStation.Start` (:250) after the own creation (:262): `rivalLane =
  RaceRuntime.Current == null ? null : new ...(race)`, which calls `SceneReady(TRACKSTATION)`
  and builds a `RivalMapPresence`. In `Update` (:286) at the existing 0.25 s tick (:360):
  `rivalLane?.Tick(ut)`. In `OnDestroy` (:1285) before the own removal (:1301):
  `rivalLane?.Dispose()`. No engine, no marker for a rival inside an atmosphere, no CommNet
  drive.
- **Proof:** in-game `RaceRivalLaneTrackingStation.TrackingStationListsRivalOrbit` (a
  TRACKSTATION-only category, OV-09, hosted by T-29's RACE-11): the rival orbit leftover is
  in the vessel list under the ghost filter; removed at scene exit.
- **Registers:** lane catalog regen for the new category; RACE-11's batch tally re-pinned
  once T-29 has landed.
- **Size:** S. **Depends on:** R3W-18, R3W-19. **PC:** yes.
- **Risk:** low.

### R3W-21 Missing parts

- **Change:** `RivalFlightLoader.Payload`: before attaching the ghost snapshot, remove every
  `PART` whose name has no `PartLoader` entry (dot form), through a part-lookup port, except
  the kerbal EVA parts: a part whose name `RaceLookTable.IsKerbalEvaPartName` (R5-07)
  accepts is kept, so R5's dresser sees a DLC suit's kerbal (OV-56, DC-048). If this task
  lands before R5-07, R5-09 adds that exception to the strip. Record
  `MissingParts` (distinct names) on the flight; one summary line per flight
  (`[RivalStore] missing parts rec=... count=N names=...`). The ghost builder therefore
  never warns per part for a rival. A snapshot left with no part is `unplayable:no-parts`.
- **Proof:** xUnit with a fake lookup: parts stripped, names recorded, empty snapshot
  unplayable, a missing `kerbalEVAVintage` kept (that cell lands with the exception); in-game
  check that no
  `ResolveAvailablePart failed` WRN appears for a rival with an unknown part.
- **Registers:** none (the R4 / R6 note reads `MissingParts` later; the summary line above
  is its first production reader, so the ratchet does not move).
- **Size:** S. **Depends on:** R3W-04. **PC:** yes (log check).
- **Risk:** low; a stripped part may leave a floating child; acceptable for a ghost.

### R3W-22 Removal and replacement at scene load

- **Change:** none in code beyond R3W-05 and R3W-08 if they hold: the store is rebuilt from
  the applied standing at every scene ready, so a recording the latest standing no longer
  names is gone with its ghost, its leftover and its flags. This task adds the tests and
  makes an import (R3E-18, run from R3E-17's `RaceSyncDriver.Tick`) during a flight leave
  the lane untouched (log one line "rival store changes wait for the next scene").
- **Proof:** xUnit `RivalStoreReplaceTests`: standing seq 5 then seq 6 without one id; the
  id is absent after the next `SceneReady`; the lane list is unchanged until then.
- **Registers:** none.
- **Size:** S. **Depends on:** R3W-09, R3E-18 (the live import). **PC:** no.
- **Risk:** low.

### R3W-23 Rival flag records

- **Change:** new `RaceWorld/RivalFlags.cs` data half: a `RivalFlagRecord` (player id,
  recording id, site name, placed by, plaque text, flag URL, body, lat, lon, alt, rotation,
  fromUT, untilUT). Source: `Standing.Flags`, the `FLAGS` block written and parsed by R3E-14
  (`F { recordingId site placedBy plaque flagUrl body lat lon alt rot ut pickupUT }`,
  OV-36, see 9.1): `fromUT` is the row's `ut`, `untilUT` the row's `pickupUT` (computed by
  the exporter from its own model; empty means standing, mapped like an open leftover). No
  pick-up matching on this side and no trajectory open. A flag inside a launch area is not
  created (`SiteRow.LaunchAreas` through `LaunchAreaGeometry.Contains`, as in R3W-12). A
  missing local texture falls back to the default flag.
- **Proof:** xUnit `RivalFlagRecordTests`: `pickupUT` ends the window, an empty one stays
  open; launch area drops the flag; unknown texture falls back.
- **Live:** in-game cell `RaceRivalLane.RivalFlagRecordsFromStanding`: writes a standing with
  one `FLAGS` row at the scratch cache (`StandingCodec.Write`), reads it back, builds the
  records through every member this task adds, and asserts the window from `pickupUT` and
  that a flag on the home pad is dropped. So the data half is its own pull request (ratchet,
  section 4 intro); no merge with R3W-24.
- **Registers:** none.
- **Size:** S. **Depends on:** R3W-05, R3W-12, R3E-14 (`FLAGS`). **PC:** yes (one run of the
  cell).
- **Risk:** low; the block and its pick-up UT are R3E-14's (9.1).

### R3W-24 Plaque replicas in flight

- **Change:** `RivalFlags` live half: per flag inside its window and within a draw radius of
  the active vessel, a GameObject with the flag model and the rival's texture, parented to
  the body transform so the floating origin moves
  it, with a trigger collider on a layer vessels do not collide with, used only for the
  click raycast. No ProtoVessel, no `FlagSite`, no entry in `flightState`. Created and
  destroyed by `ParsekFlightRivalLane` at 0.25 s ticks; destroyed in `Dispose`. The model
  is built the way the code builds a flag today (OV-55): the flag part has no `.mu` mesh to
  clone, its meshes come from `FlagDecalBackground` at runtime
  (`GhostVisualBuilder.cs:1800-1801`), so the replica is built through
  `FlagDecalBackground`, never a prefab clone; S7 tests that build (or a real vessel, if it
  cannot) and sets the click range.
- **Proof:** in-game `RaceRivalLane.RivalFlagIsNotAVessel`: vessel count unchanged, no
  `FlagSite` part, saved game has no flag vessel, a kerbal walks through it. The
  `SetSpawnFlagOverrideForTesting` counter stays 0 across the whole category.
- **Registers:** `design-gui-inventory.md` (world object, not a window); CHANGELOG entry.
- **Size:** M. **Depends on:** R3W-23, spike S7. **PC:** yes.
- **Risk:** medium; the flag part has no mesh file to clone (9.5), so the build depends on
  `FlagDecalBackground` working outside a flag vessel, which S7 proves.

### R3W-25 Reading a rival plaque

- **Change:** a click on the replica's trigger (within the click range S7 decides) opens
  the stock plaque dialog with the rival's site name, placer and text, through the entry
  point S7 finds. Logged once per open.
- **Proof:** in-game `RaceRivalLane.RivalPlaqueOpensStockDialog` (the dialog's GameObject
  exists with the supplied text), plus the owner's look.
- **Registers:** `design-gui-inventory.md` (a stock dialog reused, no new surface).
- **Size:** S. **Depends on:** R3W-24, spike S7. **PC:** yes.
- **Risk:** medium; if S7 fails, question 10.1 decides.

### R3W-26 Watching a live rival ghost (optional)

Deferred with P3b: planned work, built when watching a rival flight is scheduled (with the
camera half of R4-11 and R5-06). The first race build shows rivals on the map, in the
Tracking Station and as ghosts nearby, without the Watch camera.

- **Change:** with P3b's lane-qualified target, `ParsekFlightRivalLane` exposes its engine,
  list and index to the watch controller; the rival policy's `isWatched` delegate reads the
  target. Entry point: `TryWatchRivalFlight` on R1-08's `RaceRuntime` facade, called by the
  map watch menu answer of R3W-19 and by R4-11's board cell. It is added by whichever of
  R4-11 and this task lands first (A1, OV-39): if R4-11 is first, its "not available" stub
  is there and this task fills it; if this task is first, it adds the method with the map
  menu as its production caller and R4-11 only calls it. Watching
  ends when the ghost completes (the policy asks the controller to exit). No watch of a
  past flight.
- **Proof:** xUnit on the policy's exit; in-game `RaceRivalLane.WatchRivalGhost`.
- **Registers:** todo; CHANGELOG.
- **Size:** M. **Depends on:** P3b-04, R3W-19 (not on R4-11; R4-11's camera half waits on
  this task instead). **PC:** yes.
- **Risk:** medium; the watch controller is shared with the own lane.

### R3W-27 Grep gates: rival store names and the loader boundary

- **Change:** one script in the `GrepAuditTests` family with an allowlist and a managed
  fallback, on R1-06's shared race scanner: `scripts/grep-audit-rival-store.ps1`. The names
  it guards are derived from the rival store's files (every type declared in
  `RaceWorld/RivalStore.cs`, `RivalFlight.cs` and `RivalFlags.cs`, read from those files),
  so a new store type is covered without an edit (OV-13); they appear only in the files of
  `scripts/rival-store-allowlist.txt`: the RaceWorld lane files, the two `*RivalLane.cs`
  controllers, the board / window files R4 adds and the race in-game test files under
  `Source/Parsek/InGameTests/` (the `RaceRivalLane*` cells and `RivalScratchCache` name the
  store's types). Comment-stripped scan.
  The loader boundary is not a second script (OV-12): R1-06's "`Recording` types only in
  the loader" arch cell owns the rule and reads `scripts/race-loader-files-allowlist.txt`;
  this task owns the rival loader entries there (`RaceWorld/RivalFlightLoader.cs`,
  `RaceWorld/RivalFlightLoader.Payload.cs`, which R3W-03 and R3W-04 add as they land) and
  checks that the only other RaceWorld file listed is `RaceWorld/RivalFlight.cs` (R3W-02's
  entry, for the interface's member types). The recorder ban lives once, in R1-06's
  `race-no-recorder`.
- **Proof:** `GrepAuditTests.GrepAudit_RivalStore*`, mutation check (add a name in a
  non-listed file: red; add a type to `RivalStore.cs` and use it outside: red); R1-06's
  arch cell reds on a planted `Recording` type in a non-loader RaceWorld file.
- **Registers:** `scripts/rival-store-allowlist.txt`; the rival entries of
  `scripts/race-loader-files-allowlist.txt`; CLAUDE.md grep-gate list if it names them.
- **Size:** S. **Depends on:** R3W-05, R1-06. **PC:** no.
- **Risk:** low.

### R3W-28 Source gate: what the rival lane never calls

- **Change:** xUnit `RivalLaneWiringGateTests`, comment-stripped source reads of
  `RivalPlaybackPolicy.cs`, `RivalLaneFrame.cs`, `RivalMapPresence.cs`, `RivalFlags.cs`,
  `ParsekFlightRivalLane.cs` and `ParsekTrackingStationRivalLane.cs`: none names
  `VesselSpawner`, `SpawnVesselOrChainTip`, `TryRetireEndedFlightAtKsc`, `heldGhosts`,
  `pendingSpawn`, `spawnFailures`, `GhostCommNetManager`, `DriveGhostCommNet`,
  `CheckPendingMapVessels`, `SetLoopUnits`, `Reaim`, `PlaybackScopeTracker`,
  `RemovedReplayFlags`, `Ledger`, `GameStateRecorder`, `CommittedRecording`, nor the static
  caches keyed by bare recording id (OV-50, playback 9.6): the tail-lift plan caches
  (`ParsekFlight.cs:1126-1135`), `s_previewRecordingForTailLift`, `SectionAnnotationStore`,
  `RenderSessionState`; the flight lane's `FrameContext` sets `activeReFlyMarker = null`;
  `ResolveChainNextIndex` is assigned null. Lands as soon as the files exist (with R3W-09)
  and grows with them. The id rule this rests on: playback 9.6 finds those caches safe while
  a rival trajectory never reaches them (flattened, no tail lift); R3W-04 leaves no
  Relative section in a rival flight (`RivalFlightFlattenTests` pins it), so the lane must
  not reach them by name either, and this gate pins that. Rival ids are not assumed to
  differ from own ids.
- **Proof:** the gate itself with a mutation check per name group.
- **Registers:** none.
- **Size:** S. **Depends on:** R3W-09. **PC:** no.
- **Risk:** low.

### R3W-29 Headless byte-identity test

- **Change:** xUnit `RivalIsolationByteIdentityTests` (`[Collection("Sequential")]`), the
  one isolation byte-identity test (OV-20; T-23 is dropped into it): one race-save state
  (T-01's `SaveWriterDriver` plus the race mark) loaded twice through the scenario load
  path, once with an empty rival cache and once with a populated one (`RivalCacheBuilder`
  over T-16's `rival-station` content: three rivals, flights, leftovers, flags, an orbiting
  station); `RaceRuntime.SceneReady` is called in both; then the ledger walk
  (`LedgerOrchestrator` recalculation over the headless seam the ledger tests use) and the
  save writers run through the driver; the pools, the action list and the two saved PARSEK
  nodes are compared byte for byte. T-23's flag case: a third case adds rival flag events
  and asserts no flag vessel is planned and none reaches the flight-state writer.
- **Proof:** the test; a mutation (write one rival id into a store) must red it; a log
  capture of the rival store's `built` line proves the full case was really full
  (anti-vacuity).
- **Registers:** none.
- **Size:** M. **Depends on:** R3W-08, R3W-11, R3W-23, T-01, T-16. **PC:** no.
- **Risk:** low; it can only see what a headless run reaches, which is why R3W-30 and
  T-27's RACE-4 exist.

### R3W-30 In-game isolation category

- **Change:** the in-game isolation cells are this slice's (OV-21), split by scene because a
  host lane runs one batch in one scene (OV-09). Category `RaceRivalIsolation` (FLIGHT,
  hosted by T-29's RACE-10):
  - `RivalSoiCrossingChangesNothing` and `RivalLandingChangesNothing`: snapshot
    `ProgressTracking` nodes, the ledger action count, the game-state event count, contract
    states and funds / science / reputation; warp a rival ghost and its map vessel across an
    SOI and onto a surface; compare;
  - `RivalIsNotACommNetNode`: no `CommNetNode` for any rival map vessel or ghost;
  - `RivalFlagIsNotAVessel` (shared with R3W-24);
  - `RivalLeftoverHasNoColliders`: no non-trigger collider under a rival ghost root;
  - `RivalMapVesselStrippedAtSave`.
  Category `RaceRivalIsolationTrackingStation` (TRACKSTATION, hosted by T-29's RACE-11): the
  Tracking Station runs of `RivalIsNotACommNetNode` (map vessels) and
  `RivalMapVesselStrippedAtSave`. T-26 keeps only its seam assertions.
- **Proof:** the categories; results in `parsek-test-results.txt`.
- **Registers:** lane catalog regen; once T-29 has landed, the RACE-10 and RACE-11 batch
  tallies re-pinned in the same change (`CommittedBatchTallySourceSyncTests`).
- **Size:** M. **Depends on:** R3W-18, R3W-20 (the TS cells), R3W-24. **PC:** yes.
- **Risk:** medium; stock progress events can lag a frame, so each test waits a fixed
  settle window and says so.

### R3W-31 Whole-save harness lane

Dropped: replaced by T-27 (OV-05).

### R3W-32 Per-frame cost check

- **Change:** `ParsekFlightRivalLane` logs one rate-limited line per 10 s of game play:
  entries, live ghosts, opens, budget refusals, and the lane's `Tick` time in ms. An
  in-game test `RaceRivalLane.RivalLaneFrameCost` with three rivals of 200 flights each
  (fixture) asserts the median tick under a bound set from the first measured run.
- **Proof:** the test and the log line.
- **Registers:** none.
- **Size:** S. **Depends on:** R3W-14. **PC:** yes.
- **Risk:** low.

## 5. Parallel tracks

- Track A (data, headless): R3W-02, R3W-03, R3W-04, R3W-05, R3W-11 (data half), R3W-12,
  R3W-21, R3W-22, R3W-23. Files: `RaceWorld/RivalFlight*.cs`, `RivalStore.cs`,
  `RivalFlags.cs`, `Generators/RivalCacheBuilder.cs` (R3W-05).
- Track B (lane rules, headless): R3W-01 (after P2c-03), R3W-06, R3W-07, R3W-08. Files:
  `GhostPlaybackEvents.cs`, `GhostPlaybackEngine.Diagnostics.cs`, `RaceWorld/Rival*Policy`,
  `RivalLaneFrame.cs`, `RaceRuntime.cs` (R1-08's file). A and B run at the same time;
  R3W-08 waits for R3W-05 (track A) and R3W-06.
- Track C (composition): R3W-09, R3W-10, R3W-13, R3W-16 in that order, one agent; R3W-26
  last, deferred with P3b.
- Track D (map): R3W-17, R3W-18, R3W-19, R3W-20, after R3W-09; parallel to track C except
  for `ParsekFlightRivalLane.cs` (R3W-18 adds one tick call).
- Track E (A5): R3W-14 and R3W-15 in parallel to each other after R3W-09; R3W-15 after
  R5-03.
- Track F (flags): R3W-23 to R3W-25 after S7.
- Track G (gates): R3W-27 after R3W-05; R3W-28 with R3W-09; R3W-29, R3W-30 and R3W-32
  last.

Hot files that force an order:

- `ParsekFlight.cs`: R3W-09 (wiring, the only task in this slice that touches `Start`),
  R3W-16 (`OnGUI`). Only these two touch it.
- `ParsekFlightRivalLane.cs`: R3W-09, then R3W-10, R3W-13, R3W-16, R3W-18, R3W-24, R3W-26,
  R3W-32 each add a call; rebase in task order.
- `GhostPlaybackEngine` constructor (OV-48): P2c-02, P2c-04, P2a-02, R5-03 land before
  R3W-09, which builds the rival engine on their form; R3W-14 then adds its budget argument
  and updates both construction sites.
- `RecordingPlaybackCatalog.cs`: P2a-05 (creates it), then R3W-09 (adds `None`).
- `GhostMapVessels.cs` (OV-48): P3-02 to P3-05, then R3W-17, then `ApplyOrbitStyle` (R5-04's
  specification, in R3W-18's pull request), then R3W-19.
- `ParsekTrackingStation.cs`: R3W-20 only.
- `GhostPlaybackEvents.cs`: R3W-01 only, after P2c-03.
- `scripts/arch/modules.toml`: R3W-14 (one Core rule); R5-03 adds the style rule.
- `GrepAuditTests.cs`: R3W-27 (one change).
- `scripts/race-loader-files-allowlist.txt`: R3W-03, R3W-04 add their entries; R3W-27 owns
  them.
- `RivalFlightLoader*.cs`: R3W-03, R3W-04, R3W-11, R3W-21 in order.

## 6. Tests and proof

| Kind | Name pattern | Proves | Owner's PC |
|------|--------------|--------|------------|
| xUnit | `RivalFlightTests`, `RivalFlightLoaderHeaderTests`, `RivalFlightFlattenTests` | the fixed answers (no flags, no loop, no anchor); flattening; header without payload opens | no |
| xUnit | `RivalStoreBuildTests`, `RivalCacheBuilderTests`, `RivalStoreReplaceTests`, `RivalStoreLaunchAreaTests` | build from the cache with zero `.prec` / `.craft` opens; the fixture round-trips; removal; launch areas | no |
| xUnit | `RecordingPlaybackCatalogTests` (cells added by R3W-09) | `RecordingPlaybackCatalog.None` answers "none" to every lookup | no |
| xUnit | `RivalPlaybackPolicyTests`, `RivalLaneFrameTests`, `GhostPlaybackSkipReasonTests` | no spawn, no chain, no hold; flags never ask for a spawn; skip path fires no completion | no |
| xUnit | `RivalLeftoverTests` | orbit tail and endpoint hold inside the leftover window | no |
| xUnit | `PlaybackGhostBudgetTests`, `RivalStyleSourceTests`, `RivalMapPresenceDecideTests` | own ghosts never limited; one style per player with the `Ana - ` prefix; map decisions and the map create throttle | no |
| xUnit | `RivalLaneWiringGateTests`, `GrepAudit_RivalStore*` (plus R1-06's loader arch cell over the rival entries) | the left-out calls and bare-id caches stay left out; store names stay in their files; the loader boundary | no |
| xUnit | `RivalIsolationByteIdentityTests` | an empty and a full rival cache give the same ledger and save bytes; rival flags plan no flag vessel | no |
| In-game | `RaceRivalLane.*` (FLIGHT, host RACE-10) | the per-task cells that keep R3W-02 to R3W-08, R3W-17 and R3W-23 live; the ghost plays (M1), lazy open, leftovers, map vessels guarded, labels, flags, plaque, watch, frame cost | yes |
| In-game | `RaceRivalLaneTrackingStation.*` (TRACKSTATION, host RACE-11) | the TS list holds the rival orbit leftover | yes |
| In-game | `RaceRivalIsolation.*` (FLIGHT, host RACE-10), `RaceRivalIsolationTrackingStation.*` (host RACE-11) | SOI and landing change nothing; no CommNet node; flag not a vessel; no colliders; stripped at save | yes |

Every in-game test skips (naming the context it needs) when that context is missing (a
race save with the fixture; for the scratch-cache cells of R3W-02 to R3W-07 and R3W-23, a
committed recording in the save), so single-player batches stay green. The host lanes are
T-29's (RACE-10 FLIGHT, RACE-11 TRACKSTATION). A category no committed spec pins yet needs
no batch-tally re-pin when a cell is added; T-29 pins the tallies when it lands, and only
after that does a new cell re-pin its host's tally in the same change. The whole-save
proof is T-27's RACE-4 (whole saved game identical
with and without rivals), which this slice's acceptance names (OV-05).

## 7. Gates and registrations

- **Architecture map:** files under `RaceWorld/` take R1-01's folder rule;
  `ParsekFlightRivalLane.cs` and `ParsekTrackingStationRivalLane.cs` match the Controllers
  prefix (`modules.toml:322`); `PlaybackGhostBudget.cs` gets an explicit Core placement
  (update the kernel comment at :74-79 and its count); `PlaybackTrajectoryStyle.cs` is
  R5-03's. `RecordingPlaybackCatalog.None` stays in P2a-05's `Recording` file; the rival
  lane names it only from Controllers, so no RaceWorld file names it. The landed primitive
  and the rival provider slot live in P3's Ghost file `GhostMapVessels.cs`.
- **Forbidden edges:** R1 adds 15.4's lists. This slice must not add a `RaceWorld ->`
  edge to GameActions, Crew, Rewind, Missions, Logistics, SwitchIntent, Reaim, Timeline or
  RaceExport; the `Ghost` and `Controllers` edges it adds are allowed (Controllers ->
  RaceWorld is downward; RaceWorld -> Ghost is same layer). Layer pins re-pinned where a
  new same-layer pair appears (`scripts/arch/layer_pins.toml`).
- **Statics:** the plan adds none in RaceWorld (`RaceRuntime.Current` forwards to R1-08's
  one static on `RaceSession`). The rival provider slot R3W-19 adds on `GhostMapVessels` is
  tagged `scene` in `scripts/arch/lifetimes.toml` and written only through its own type's
  methods. `RecordingPlaybackCatalog.None` holds no state (tag it `process` only if
  `test_static_lifetimes` flags it, as P2a-05 does for `Own`).
- **Uncalled-method ratchet (A5):** every task is its own pull request and every member is
  live in it (section 4 intro): R3W-02 to R3W-08, R3W-17 and R3W-23 through their in-game
  cells (InGameTests is non-production, so its calls count; xUnit does not);
  `RecordingPlaybackCatalog.None` lands with R3W-09's rival engine; `ApplyOrbitStyle` lands
  in R3W-18's pull request; the show setter waits for R4-10; `TryWatchRivalFlight` is added
  by whichever of R4-11 and R3W-26 lands first.
- **Headless pins:** RaceWorld is not headless. Core additions must stay leak-free (Color is
  inert, `scripts/arch/facts.toml:22`).
- **Grep gates:** R3W-27 (one new gate, `grep-audit-rival-store.ps1`, on R1-06's scanner;
  the rival entries of R1-06's loader allowlist), the existing GUI mock write-set gate
  untouched.
- **Save format:** no change. Nothing of this slice is written into the save; R3W-29 and
  T-27's RACE-4 prove it. The new enum member is runtime only.
- **GUI gates:** no new window, tab or overlay. `design-gui-inventory.md` gets: the rival
  refusal screen message (R3W-19), the label kind (R3W-16), the plaque dialog reuse
  (R3W-25). No `UiSurface` key.
- **Harness:** the four new in-game categories (`RaceRivalLane`,
  `RaceRivalLaneTrackingStation`, `RaceRivalIsolation`,
  `RaceRivalIsolationTrackingStation`) need `python harness/tools/lane_catalog.py`; their
  host lanes are T-29's RACE-10 and RACE-11, so after T-29 lands each new test re-pins its
  host's batch tally in the same change. No spec is added by this slice.
- **Docs:** CHANGELOG entry opened at R3W-09 and edited by R3W-11, R3W-18, R3W-24, R3W-26;
  todo entries per task; CLAUDE.md "Key source files" gains one line for the rival lane
  files (with `cp .claude/CLAUDE.md AGENTS.md`). The "zero `Recording` references" claim of
  9.8 is corrected by P2a-02 (DC-133), not here.

## 8. Risks

- **Two engines, one scene.** A missed host field after P2b would let the lanes overwrite
  each other's positions. Noticed by: M1's in-game test plus every flight lane passing with
  a race save loaded; a log line per lane with its entry count.
- **Engine reads before activation.** If a later engine change reads trajectory data for
  skipped entries, unopened rival flights read as empty. Noticed by: `RivalFlight` returns
  empty lists, never null, and `RivalLaneFrameTests` pins the skip.
- **Widened end and the in-range path.** If the in-range dispatch past the last point does
  something other than the two static helpers say, leftovers jump. Noticed by: the two
  leftover in-game tests.
- **Landed map vessels in physics range.** Relies on the `GoOffRails` refusal. Noticed by:
  `LandedMapVesselIsInert`.
- **Stock walks over the vessel list.** A walk the guard set does not cover could count a
  rival map vessel (progress, contracts). Noticed by: `RaceRivalIsolation` and T-27's
  whole-save lane RACE-4.
- **Frame cost with hundreds of flights.** The engine walks every entry each frame.
  Noticed by: R3W-32's measured bound; if exceeded, the list is cut to flights whose
  window intersects the scene's plausible clock range and rebuilt on a warp jump.
- **Log noise.** Per-part warnings for missing parts; per-frame skip traces. Mitigated by
  R3W-21 and by the rate-limited summary of R3W-32; checked by the log validator in the
  in-game runs.

## 9. Corrections and gaps

1. **Flags are inside the trajectory file only.** The standing says so (design 6.1, "Flags
   are inside the recordings"), and `TrajectorySidecarBinary.cs:316` reads them there. But
   7.6 and 15.13 forbid opening payload at store build, and 7.7 shows a flag from its plant
   date anywhere. The plan assumes an additive `FLAGS` block in the standing (site, placer,
   text, URL, body, position, rotation, ut, and the pick-up ut when known), written by the
   export slice and parsed by the codec slice. Fallback: a budgeted open of each past
   flight's trajectory at scene ready, one per frame. Settled: R3E-14 adds `FLAGS` as an
   additive block with `pickupUT` and R3W-23 reads it; no fallback is planned (OV-36).
2. **"Built from the standing alone" is not enough for leftovers.** End position, terminal
   orbit, endpoint body, terminal state and the debris flag are not in `RECORDINGS`; they
   are in the tree file (`RecordingTreeRecordCodec.cs:16-70`, :387). The plan reads the
   trees' small cfg files at store build and never a `.prec` or `.craft`; the "no payload
   opened" test is scoped to those two suffixes. Settled: R3E-02 exports the tree in the
   production `RecordingTree` node shape with the terminal and endpoint keys, and R3W-03
   loads it with the production reader; one key list is agreed in R3E-02's PR (OV-35).
3. **No vessel type for map icons.** The tree file has none and `ResolveVesselType` reads
   `VesselSnapshot` (`GhostMapPresence.ProtoVesselLifecycle.cs:2090`), which a rival never
   shares. The plan reads it from the ghost snapshot once opened and uses `Ship` before;
   an additive `vtype` on `LEFTOVERS` rows would make leftover icons right at scene load.
   Settled: R3E-14 adds `vtype` to `LEFTOVERS` rows; R3W-11 reads it onto the header and
   R3W-18 uses it (OV-36).
4. **The P3 primitive is orbit-only** (`BuildGhostProtoVesselNode`, :1955). Landed
   leftovers in map view and the TS need a landed variant; the design is silent. R3W-17.
5. **The flag part has no mesh file** (comment in `GhostVisualBuilder.cs` near
   `SpawnFlagVessel`, :1803), so "the stock flag model" is not a prefab clone. S7 must
   also say how to build the replica's model. Settled: the replica is built through
   `FlagDecalBackground` at runtime, as the code builds flag meshes
   (`GhostVisualBuilder.cs:1800-1801`), and S7 tests that build (OV-55).
6. **The catalog interface shape.** If P2a's `IPlaybackCatalog` returns `RecordingTree`,
   `RivalCatalog.cs` must name a `Recording` type outside the loader. The plan asks P2a for
   a neutral return (or a bool and an id), else `RivalCatalog.cs` joins the loader
   allowlist. Settled: `IPlaybackCatalog` stays in `Recording` and returns `Recording` /
   `RecordingTree`; there is no `RivalCatalog`; the rival engine gets the empty
   `RecordingPlaybackCatalog.None`, added by R3W-09 with its first caller (OV-47, DC-111).
7. **"Hold mode" needs no engine mode.** The engine already holds the last pose when the
   end extends (`GhostPlaybackEngine.Completion.cs:513-517`) and plays an orbit tail after
   the last point (`SectionPositioning.cs:637-671`), while a held completed ghost is not
   repositioned. The plan widens `EndUT` and appends a tail in the loader. An empty
   `untilUT` maps to a finite named horizon.
8. **CLAUDE.md says the engine has zero `Recording` references.** It reads
   `RecordingStore.CommittedTrees` and `PendingTreeSlot`
   (`GhostPlaybackEngine.SectionPositioning.cs:354-371`). P2a removes it; the plan
   corrects the sentence when the lane files are added. Settled: P2a-02, the change that
   removes the reads, rewrites the sentence (DC-133); this slice does not.
9. **Lazy load is unstated against the engine's order.** The engine reads `Points` before
   activation (`GhostPlaybackEngine.cs:472-476`). The plan skips unopened entries from the
   host with a new neutral reason (R3W-01); `PlaybackDisabled` is avoided because its skip
   fires the hidden completion.
10. **Missing parts flood the log.** One Warn per missing part
    (`GhostVisualBuilder.Parsing.cs:120`). The loader strips them first and logs once.
    Settled for kerbal EVA parts: the strip keeps every name
    `RaceLookTable.IsKerbalEvaPartName` accepts, so the dresser sees them (OV-56, DC-048).
11. **Colors are split between R3 and R5.** The brief puts style in this slice; the phase
    table puts colors on labels, icons and lines in R5. The plan builds the A5 seam and a
    grey placeholder; R5 fills the color. Settled: R5-03 owns `PlaybackTrajectoryStyle` and
    the engine argument, R5-04 and R5-05 the sites; R3W-15 is only the rival style source,
    grey until R5-07, and R5-03 lands first (OV-43, DC-118).
12. **The show toggle's setter has no R3 caller.** The window is R4, and the uncalled-method
    ratchet forbids an early setter. R3 reads `SHOW` only. Settled: the `SHOW` key is in
    R1-08's `RaceLocal` codec and the setter is R4-10's (OV-27).
13. **A custom rival flag texture is not shared.** `flagURL` is a path in the writer's
    install. The plan falls back to the default flag when the path does not resolve.

## 10. Questions for the owner

1. **If the stock plaque dialog cannot show a rival's text (spike S7 fails), how does a
   player read a rival's plaque?** Options: (a) a small Parsek dialog with the plaque text,
   opened by the same click (a new dialog, needs a ruling like the Route History one);
   (b) the flag stands but cannot be read; (c) no rival flags in the first build.
   Recommendation: (a), sized as one stock-styled `PopupDialog`.
2. **Should a rival ghost flying inside an atmosphere show anything in the flight map
   view?** Own ghosts get a Parsek-drawn marker there; the design says none for rivals in
   the Tracking Station and is silent about flight. Options: (a) nothing, as in the TS;
   (b) the same marker as own ghosts, tinted later by R5. Recommendation: (a) in R3,
   revisit with R5.
   Assumed until the owner says otherwise: (a), no marker for a rival inside an atmosphere,
   as design 15.6 has it; this is one question with R5-05's condition (OV-58).
3. **Should rival ghosts in flight carry a floating name label ("Ana - Kerbal X")?** Today
   only chain ghosts have one. Options: (a) yes, within the existing label range; (b) no
   label until R5. Recommendation: (a), so a player can tell whose ghost is beside them.
   Assumed until the owner says otherwise: (a) yes, with one prefix format (a designer
   choice, to be listed in design 13.B): `LabelPrefix = "<display name> - "`, so the
   floating label in flight reads `Ana - Kerbal X` and the map and Tracking Station name
   `Ghost: Ana - Kerbal X`. Merged with r5's label question (OV-45, OV-58).
