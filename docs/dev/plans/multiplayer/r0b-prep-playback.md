# Playback prerefactors P2c, P2a, P2b (phase R0b) - implementation plan

Status line: PLAN (2026-11-14), not started. Design sections 15.5, 15.6, 15.11 (rows P2a, P2b,
P2c). Phase R0b.

## 1. Goal and scope

Change single-player code, before any multiplayer feature code exists, so that a second playback
lane (a second `GhostPlaybackEngine` with its own trajectories, its own positioner state and its
own catalog) can later run in one flight scene beside the player's own lane. Every task keeps
single-player behavior unchanged; the second lane is not built here.

Built:

- P2c. `LoopSyncParentIdx` stops being writable through `IPlaybackTrajectory`. The engine writes
  its diagnostics through a sink it is given, not the global `DiagnosticsState`. The per-frame
  spawn throttle reads a spawn budget object that two engines can share.
- P2a. An `IPlaybackCatalog` port (find a recording by id, find the tree holding an id, the
  pending tree) and its own-timeline implementation. The tree lookups and by-id lookups in the
  engine, the positioner, the two anchor resolvers, the flight anchor context builder and the map
  state vectors call it, each keeping its present search order and fallbacks (section 3.2).
- P2b. One per-lane state object, `ParsekFlightPlaybackLane`, holding the orbit cache, the ghost
  position entries and their reapply frame stamps, the seven log-once sets, the loop-unit lookup,
  the watch query, the ghost-world query and the catalog. It is handed to
  `ParsekFlightGhostPositioner` and to the late-update helpers of `ParsekFlightPlaybackHost`, and
  it owns its resets.

Not built: the rival engine, `RivalFlight`, the empty catalog the rival lane uses
(`RecordingPlaybackCatalog.None`, added by R3W-09 with its first caller, OV-47), any second
positioner instance, any rival-specific watch behavior (P3b, deferred out of R0b), the
ghost-map-vessel primitive (P3), the trajectory codec change (P6), the ghost-count budget
`PlaybackGhostBudget` (addition A5, R3W-14), the per-frame reset of a shared spawn budget
(R3W-09) and a spawn throttle for rival map vessels (R3W-18) (OV-46), any UI.

## 2. Starts after

- Version 1.1.0 is released and the owner says go (brief).
- P12 (P12-01 `[forbidden]` list form, P12-02 `ParsekRace*` placement) and P11 (P11-01,
  P11-02, derived window counts) per the 15.11 order. Nothing in this slice needs their
  content; the order only keeps the arch tooling and harness specs stable while these
  refactors re-pin canaries.
- P7 (P7-01, P7-02), P1 (P1-01 to P1-03; P1-04 is dropped), P10 (P10-01) per the 15.11
  order. No file overlap with this slice (section 5); they are an ordering preference, not a
  dependency.
- Inside this slice: P2c before P2a before P2b (15.11). P2a-01 is the port's specification, not
  a pull request; P2a-05 lands first among the P2a tasks and creates the catalog files, and the
  other P2a tasks depend on it. P2b-02 starts after P2a-02 to P2a-07
  and P2c-03 are merged (Tracks A, C, D of section 5). P2b-01 (tooling) may land any time.
- No spike is needed.
- After this slice: P3 (P3-01 to P3-05; P3-02 lands after P2a-07, E.3.8). P6 (P6-01 to P6-05)
  is built in parallel with the prerefactors and merged when green, but never merges inside
  the P2b window (P2b-02 to P2b-10 and its flight proof), so that proof flies one known change
  set. P3b (P3b-01 to P3b-04) is deferred out of R0b: it is built when watching a rival flight
  is scheduled (with R3W-26, the camera half of R4-11 and R5-06); when built, it starts after
  P2b-10 (P2b-08 adds the watch query seam).
- Later slices that order against this one: R3W-01 lands after P2c-03 (E.5.4); the engine
  constructor is changed in the order P2c-02, P2c-04, P2a-02, R5-03, R3W-09, R3W-14 (OV-48,
  second-pass ruling A3): R5-03 lands after P2a-02 and before the P2b window opens, R3W-09
  builds the rival engine on the form R5-03 leaves, and R3W-14 lands after R3W-09 and updates
  both construction sites.

## 3. What the code looks like today

### 3.1 P2c facts

- `IPlaybackTrajectory.cs:82` declares `int LoopSyncParentIdx { get; set; }`.
  - Readers: `GhostPlaybackEngine.LoopPlayback.cs:54-59` (`trajectories[traj.LoopSyncParentIdx]`,
    `currentLoopUnits.OwnerByIndex.ContainsKey`) and `GhostPlaybackLogic.cs:772`
    (`IsLoopSyncedDebrisOfWatchedLineage`, on concrete `Recording`).
  - Writers: only `CommittedListOptimizer.cs:724,745` (`PopulateLoopSyncParentIndices`, typed
    `IReadOnlyList<Recording>`), so no production write goes through the interface.
  - Implementers: `Recording.cs:49` (auto-property, default -1; cloned at :1139),
    `Reaim/ReaimedTrajectory.cs:117` (get and set forward to the inner trajectory),
    `Source/Parsek.Tests/MockTrajectory.cs:43`, six test trajectories in
    `InGameTests/RuntimeTests.cs` (27179, 27276, 27377, 27427, 27510, 27578).
  - Test setters: `ZoneRenderingTests.cs` 663-885, `Issue316WatchProtectionTests.cs` 155, 165.
  - Runtime-only: `SaveRoundTrip/RecordingSaveRoundTripTests.cs:41` excludes it from round trip.
- Engine diagnostics writes into the static `Diagnostics/DiagnosticsState.cs`:
  - `GhostPlaybackEngine.cs:388,393,398` reset `playbackBudget = default` on early returns;
  - `GhostPlaybackEngine.cs:882-887` set six `playbackBudget` fields;
  - `GhostPlaybackEngine.cs:889` appends to `playbackFrameHistory`;
  - `GhostPlaybackEngine.cs:928` calls
    `DiagnosticsComputation.CheckPlaybackBudgetThresholdWithBreakdown`;
  - health counters: `GhostPlaybackEngine.GhostVisuals.cs:62` (ghost builds), :236 (reentry FX
    deferred), :242 (reentry FX skipped), `GhostPlaybackEngine.ReentryFx.cs:341` (reentry FX
    builds), `GhostPlaybackEngine.SpawnDestroy.cs:154` (reused across cycle), :628 (destroys).
- Spawn throttle:
  - field `frameSpawnCount` at `GhostPlaybackEngine.cs:91`, reset at :1060 inside
    `ResetPerFramePlaybackCounters` (:1050), incremented at
    `GhostPlaybackEngine.GhostVisuals.cs:25`;
  - checked in `TryReserveSpawnSlot` (`GhostPlaybackEngine.SpawnDestroy.cs:436-448`) through
    `GhostPlaybackLogic.ShouldThrottleSpawn(frameSpawnCount, GhostPlayback.MaxSpawnsPerFrame)`;
    the cap is 2 (`ParsekConfig.cs:179`);
  - also read for logs at `GhostPlaybackEngine.cs:911` and `GhostPlaybackEngine.Diagnostics.cs:297`;
  - test hooks at `GhostPlaybackEngine.cs:1338-1361` (`TryReserveSpawnSlotForTesting`,
    `IncrementFrameSpawnCountForTesting`, `FrameSpawnCountForTesting`,
    `ResetPerFrameCountersForTesting`).
- `FrameContext` (`GhostPlaybackEvents.cs:207-245`) carries no frame number, so a shared budget
  cannot reset itself by comparing frames.
- `GhostMapPresence` keeps its own `frameSpawnCount` (`GhostMapPresence.OverlapInstances.cs:172,
  192,400`, `GhostMapPresence.OverlapHighWarp.cs:168`). Out of scope here.
- 26 xUnit files construct `new GhostPlaybackEngine(...)` (for example
  `LoopFirstRunSpawnTests.cs:145`, `Issue316WatchProtectionTests.cs:240`). The constructor is
  `GhostPlaybackEngine(IGhostPositioner positioner)` at `GhostPlaybackEngine.cs:362`.

### 3.2 P2a facts: every lookup site and its exact order

| Id | Site | Search order and match | Fallback / failure |
|---|---|---|---|
| L1 | `GhostPlaybackEngine.SectionPositioning.cs:354-382` `TryFindPlaybackRecordingTree` (match helper :386-396) | pending tree first (:365-367), then `RecordingStore.CommittedTrees` in list order; match is `tree.Recordings.TryGetValue(id, out rec) && rec != null` | false, null tree |
| L2 | `ParsekFlightGhostPositioner.cs:4551-4584` `TryFindRelativeAnchorFocusTree` (caller :4439) | committed trees in order, match `tree.Recordings.ContainsKey(id)`; then pending tree (:4575), same match | false |
| L3 | `RecordedRelativeAnchorPoseResolver.cs:165-200` `TryFindFocusTree` | committed trees in order, match `(TreeId nonempty && tree.Id == TreeId) || Recordings.ContainsKey(id)` (a null `Recordings` is skipped); then pending (:189), same match | false |
| L4 | `Rendering/ProductionAnchorWorldFrameResolver.cs:411-445` `TryFindFocusTree(Recording)` | identical to L3 (pending at :433) | false |
| L5 | `RecordedRelativeAnchorPoseResolver.cs:203-258` `TryFindRecordingById` | committed trees (`TryGetValue`, non-null), then pending (:227, same), then the flat `RecordingStore.CommittedRecordings` list | false |
| L6 | `GhostMapPresence.StateVectors.cs:735-785` `TryFindRecordingByIdForBodyFixedAnchorPid` | identical to L5 (pending at :765) | false |
| L7 | `ParsekFlightGhostPositioner.cs:1677` `ResolveRecordingById` | committed list only, inside try/catch | null; `ResolveRecordingForTailLift` (:5306) then falls back to `ParsekFlight.s_previewRecordingForTailLift` |
| L8 | `ParsekFlightGhostPositioner.cs:509` (diagnostic) | `RecordingStore.TryFindCommittedRecordingById` = committed list only (`RecordingStore.cs:1269-1280`) | no diagnostic distance |
| L9 | `RecordedRelativeAnchorPoseResolver.cs:120-133` `TryFindFocusRecording` | `trajectory as Recording` cast first, else L5 | false |
| P1 | `ParsekFlight.TimelinePlayback.cs:563` `BuildFlightRelativeAnchorResolverContext` | `pendingTree: PendingTreeSlot.HasPendingTree ? PendingTreeSlot.PendingTree : null` | null |
| P2 | `RecordedRelativeAnchorPoseResolver.cs:151` `TryBuildContext` | same pending read | null |
| P3 | `Rendering/ProductionAnchorWorldFrameResolver.cs:403` `TryBuildRelativeAnchorResolverContext` | same pending read | null |

Callers that thread a catalog:

- L1 is reached only through `internal static ShouldUseLoopAnchoredDebrisChain`
  (`GhostPlaybackEngine.SectionPositioning.cs:268`). It is called from the static
  `ShouldApplyOrdinaryParentAnchoredDebrisCoveragePolicy` (:77, :83) and from instance methods
  (:120, :417, :445); `ShouldApplyOrdinary...` is called from :67 and :91 (inside the
  statics declared at :62 and :86),
  `GhostPlaybackEngine.LoopPlayback.cs:1321`, `ParsekKSC.Playback.cs:247` and
  `GhostMapPresence.StateVectors.cs:712`. Tests call the 2-argument form statically
  (`GhostPlaybackEngineTests.cs` 1171-1269).
- L7 is reached through static `ResolveTailLiftPlan` (`ParsekFlightGhostPositioner.cs:5323`) and
  `ParsekFlight.ResolveEffectiveAltitudeWithTailLift` (`ParsekFlight.TailLift.cs:29`), which the
  positioner calls at :1163, :1167, :1766.
- `RecordedRelativeAnchorPoseResolver` is a static class
  (`RecordedRelativeAnchorPoseResolver.cs:29`) entered through `TryResolveSectionAnchorPose`
  (:49, :63), called from `ParsekKSC.Playback.cs:549`
  and `GhostMapPresence.StateVectors.cs:1106`.
- `ProductionAnchorWorldFrameResolver` is an instance type built at
  `Rendering/AnchorPropagator.cs:169` and `Rendering/RenderSessionState.cs:1129`.
- `ParsekFlight.GhostPositioner.cs:321-333` (`ResolveZoneWatchState`) reads
  `RecordingStore.CommittedRecordings` / `CommittedTrees` by index through
  `GhostPlaybackLogic.IsWatchProtectedRecording` (`GhostPlaybackLogic.cs:705-735`). That is a
  watch-protection query over the own list, not a by-id lookup; P2b-08 handles it.
- `scripts/ers-els-audit-allowlist.txt` already lists `GhostMapPresence.StateVectors.cs`,
  `ParsekFlightGhostPositioner.cs`, `ParsekFlight.GhostPositioner.cs`,
  `RecordedRelativeAnchorPoseResolver.cs` and `ParsekFlightPlaybackHost.cs`. The gate has no
  stale-entry check.

### 3.3 P2b facts: every member and every reference site

Members on `ParsekFlight` (declaration line first):

| Member | Declared | Reference sites outside the declaration |
|---|---|---|
| `orbitCache` (`Dictionary<long, Orbit>`, not readonly) | `ParsekFlight.cs:2503` | positioner 936, 937, 1415, 1818, 1892, 1912, 2830, 2849, 3494, 3512; `ParsekFlightPlaybackHost.cs:202, 318` |
| `ghostPosEntries` (`internal readonly List<GhostPosEntry>`) | `ParsekFlight.cs:477` | `ParsekFlight.cs` 487, 489, 492, 509, 1288, 1289, 1374, 1389, 1401, 1415; `ParsekFlightPlaybackHost.cs` 72, 74, 618, 620 |
| `GhostPosEntry` (nested struct) | `ParsekFlight.cs:401-475` | positioner 216, 1717, 2078; `ParsekFlightPlaybackHost.cs` 111, 187, 199, 292, 386, 398, 483, 495, 504; tests `PlaybackHostUnitTests.cs:723`, `Rendering/LateUpdateSplineDispatchTests.cs:15` |
| `ghostPreCullReapplyFrame` | `ParsekFlight.cs:478` | `ParsekFlight.cs` 1374, 1398 |
| `ghostLateUpdateReapplyFrame` | `ParsekFlight.cs:479` | `ParsekFlight.cs` 1360, 1374 |
| `AddOrReplaceGhostPosEntry` | `ParsekFlight.cs:481-510` | positioner 1346, 1804, 2057, 2438, 2766, 4201, 4340, 4839, 4968; `ParsekFlightPlaybackHost.cs` 3166, 3440 |
| `loggedOrbitSegments` | `ParsekFlight.cs:873` | positioner 1398, 1409, 2386; `ParsekFlight.cs` 1592, 2357, 2384; `ParsekFlightPlaybackHost.cs` 738, 2597 |
| `loggedOrbitRotationSegments` | `ParsekFlight.cs:874` | positioner 2045, 2047; `ParsekFlight.cs` 1593, 2358, 2385; `ParsekFlightPlaybackHost.cs` 739, 2598 |
| `loggedRelativeStart` | `ParsekFlight.cs:2506` | positioner 3847; `ParsekFlight.cs:1597`; `ParsekFlightPlaybackHost.cs:2602` |
| `loggedRecordedRelativeStart` | `ParsekFlight.cs:2507` | positioner 183; `ParsekFlight.cs:1598`; `ParsekFlightPlaybackHost.cs:2603` |
| `loggedRelativeBodyFixedPrimaryStart` (Ordinal) | `ParsekFlight.cs:2509` | positioner 4650; `ParsekFlight.cs:1599`; `ParsekFlightPlaybackHost.cs:2604` |
| `loggedAnchorNotFound` | `ParsekFlight.cs:2512` | positioner 3869, 4884, 5008; `ParsekFlight.cs:1600`; `ParsekFlightPlaybackHost.cs:2605` |
| `unknownFrameTagWarned` | `ParsekFlight.cs:2516` | positioner 1239, 1730; `ParsekFlight.cs:1601`; `ParsekFlightPlaybackHost.cs` 126, 2606 |
| `cachedLoopUnits` | `ParsekFlight.cs:1102` | `ParsekFlight.cs:1109` (`CurrentCachedLoopUnits`); positioner 1021; `ParsekFlightPlaybackHost.cs` 1582, 2024, 2202, 2206, 2308, 2309, 2310, 2324 |
| `watchMode` (stays on ParsekFlight) | `ParsekFlight.cs:892` | positioner 2483-2485 (`ResolveCameraAnchorWorldPosition`); `ParsekFlight.GhostPositioner.cs` 84, 91, 325, 328 |
| `engine` (stays on ParsekFlight) | `ParsekFlight.cs:942` | positioner 451-452 (`TryGetGhostWorldByRecordingId`, diagnostic) |
| `ApplyZoneRenderingImpl` (stays) | `ParsekFlight.GhostPositioner.cs:23` | positioner 1082 (`IGhostPositioner.ApplyZoneRendering`) |
| `Log` (stays, stateless) | `ParsekFlight.cs:2549` | positioner 1141, 1195, 1399, 1410, 1879, 1887, 1908, 1922, 1928, 1951, 1967, 1979, 1988, 2387 |

The positioner (`ParsekFlightGhostPositioner.cs`, constructor `(ParsekFlight host)` at :16) has 54
`host.` references to the 14 members above (counted with `\bhost\.`). Its own per-instance
fields (`reaimLastGhostFrame`, `loopSeamLastGhostFrame`, `predictedOrbitTailContinuityCache`,
`recordedRelativeAnchorVisited`, `activeBodyFixedShiftSeconds`) are already per positioner, so a
second positioner instance gets its own. `ClearOrbitPlaybackCaches` (:1816-1820) clears
`host.orbitCache` and its own `predictedOrbitTailContinuityCache`.

Other readers and owners:

- `CurrentCachedLoopUnits` is read by `Display/GhostTrajectoryPolylineRenderer.cs:1444, 1462, 1475`
  and `InGameTests/StandaloneLoopMapInGameTests.cs:195, 201`. It stays on ParsekFlight.
- `builtLoopUnits` (`ParsekFlightPlaybackHost.cs:45`, rebuilt at :2289) is the signature-cached
  build behind `cachedLoopUnits`; it stays on the playback host (the rival lane has no loop units,
  15.5).
- Static per-recording-id caches the positioner uses stay static: `s_tailLiftPlanCache`,
  `s_tailLiftPlanTerminalBodyCache` (`ParsekFlight.cs:1126-1135`, cleared by
  `InvalidateTailLiftPlanCache` at :1652), `s_previewRecordingForTailLift`.

Lifetime and resets:

- `ParsekFlight` is `[KSPAddon(KSPAddon.Startup.Flight, false)]` (`ParsekFlight.cs:24`): one
  instance per flight scene, so every lane field starts empty on each scene. Nothing in
  `OnFlightReady` / `ResetFlightReadyState` touches these members (the grep above shows no site).
- Engine wiring at `ParsekFlight.cs:1207-1219` builds `watchMode`, then
  `engine = new GhostPlaybackEngine(Positioner)` (lazy `Positioner` at :948-950).
- Per frame: `Update` clears `ghostPosEntries` (:1288-1289); `LateUpdate` (:1352-1366) calls
  `PlaybackHost.ApplyGhostPosEntries`, `ClampGhostsToTerrain`, then stamps
  `ghostLateUpdateReapplyFrame`; `OnCameraPreCull` (:1368-1402) re-applies and clears.
- Scene teardown `ParsekFlight.cs:1590-1601`: `Positioner.ClearOrbitPlaybackCaches()`, the two
  orbit log sets, then three non-lane sets (`nearbySpawnCandidates`, `proximityVelocitySamples`,
  `notifiedSpawnRecordingIds`), then the five other log sets.
- `DestroyAllTimelineGhosts` (`ParsekFlightPlaybackHost.cs:2585-2607`): same set as teardown.
- Preview stop (`ParsekFlightPlaybackHost.cs:737-739`): orbit caches and the two orbit log sets.
- Committed-list index shifts: `OnCommittedRecordingRemoved` (`ParsekFlight.cs:2345-2366`) and
  `OnCommittedRecordingInserted` (:2375-2389) call `Positioner.ClearOrbitPlaybackCaches()`,
  `Positioner.ShiftLoopSeamTraces*`, `mapMarkers?.ClearMapMarkerCache()` and clear the two orbit
  log sets. The orbit cache keys are index-derived (`index * 10000` at positioner :1077).

Tests and gates that pin these names:

- `FlightGhostSkipCacheCleanupTests.cs:102-115` reflection-sets `orbitCache` and six log sets on
  `ParsekFlight` by field name (`Assert.NotNull(field)`), then calls `DestroyAllTimelineGhosts`.
- `CommittedListNotificationTests.cs:430-441` pins the handler call order by substring.
- `LoopUnitSetCoherenceTests.cs:59, 61, 97` pins
  `"host.engine.SetLoopUnits(host.cachedLoopUnits);"`,
  `"internal GhostPlaybackLogic.LoopUnitSet CurrentCachedLoopUnits => cachedLoopUnits;"` and
  `"loopUnits = flCtl.CurrentCachedLoopUnits;"`.
- `RelativeAnchorResolutionTests.cs:758` pins the substring `"loggedAnchorNotFound.Add("` (3 sites).
- `LoopSeamTraceIndexShiftTests.cs:14` builds `new ParsekFlightGhostPositioner(null)`.
- `GrepAuditNonLoopLivePidTests.cs:105-130` runs regexes over the positioner and host files.
- `ParsekFlightSourceFiles.cs:64-73` lists the service files `ReadAll` appends.
- 22 xUnit files build `ParsekFlight` with `FormatterServices.GetUninitializedObject`, so field
  initializers do not run there (for example `Issue316WatchProtectionTests.cs:64`, which then sets
  `watchMode` by reflection at :244).
- In-game: `RuntimeTests.cs:11787, 22974` build a second
  `new GhostPlaybackEngine(flight.Positioner)` sharing the first lane's positioner.
  `GhostReapplyFrameInGameTest.cs` and
  `ChainTipBlockedGhostInGameTest.cs` read `PendingGhostPosEntryCountForInGameTest`
  (`ParsekFlight.cs:1406-1415`).

### 3.4 Tooling facts

- `scripts/refactor/il_compare.py` (`--fold OLD=NEW` repeatable at :448, `--host-field` single
  name at :450, fold rules at :169-186). Under a fold every `NEW::` token reads `OLD::`, the hop
  `ldfld class OLD NEW::<host>` and the lazy accessor `call(virt) instance class NEW OLD::get_*()`
  are dropped, callvirt reads call, and nested paths of NEW read OLD. A renamed field (a
  positioner `lane` field in place of `host`) or a new parameter is not folded.
- Its tests are `scripts/refactor/test_parsekflight_tools.py` (fold cells at :119-446). CI does not
  run them: `.github/workflows/tests.yml` runs `harness/lib`, `missions/lib`, `provision` (:89-91)
  and `scripts/arch` (:100, :191) only.
- Module placement (`scripts/arch/modules.toml`): `^Parsek(Flight|...|Playback)|^WatchMode` goes to
  Controllers (:320-322); `^Ghost` goes to Ghost (:297-298); `^Recording` goes to Recording
  (:308-310); `IPlaybackTrajectory.cs` is in the Core kernel list of eight files (:476-478). A new
  root file named otherwise needs a rule.
- Layers (`modules.toml:516-520`): floor = LogIO, Config, Core, Trajectory; model = Recording,
  GameActions, Crew; features include Ghost and Rendering; entry = Controllers, Patches.
  `scripts/arch/layer_pins.toml:125` pins `Ghost -> Controllers`.
- Knot canary `scripts/arch/test_archview.py:4031` asserts `[435, 9, 4, 3, 2]`.

## 4. Tasks

Every task keeps single-player behavior identical; nothing is gated by a race. A task marked
"fold" is a pure move proven by `scripts/refactor/il_compare.py` reporting zero moved-body
differences, with the leftovers (accessor, host field, constructor) named in the PR.

### P2c: immutable trajectory, engine sink, shared spawn budget

**P2c-01 Make LoopSyncParentIdx read-only on the interface**

- **Change:** `IPlaybackTrajectory.cs:82` becomes `int LoopSyncParentIdx { get; }`. `Recording`
  keeps `{ get; set; }` (`Recording.cs:49`), so `CommittedListOptimizer` is untouched.
  `Reaim/ReaimedTrajectory.cs:117` drops its setter. `MockTrajectory` and the six in-game test
  trajectories keep their auto-properties (a class may keep a setter). Any test that sets the value
  through an `IPlaybackTrajectory`-typed variable sets it through the concrete type instead.
- **Proof:** full xUnit suite; `il_compare` without a fold lists exactly two removed members
  (`IPlaybackTrajectory::set_LoopSyncParentIdx`, `ReaimedTrajectory::set_LoopSyncParentIdx`) and
  no body differences.
- **Registers:** `scripts/arch/facts_pins.toml` re-pin if `uncalledInAssembly` falls (slack is not
  enforced, so optional). CLAUDE.md: none.
- **Size:** S. **Depends on:** none. **PC:** no.
- **Risk:** low; compile-checked, and no production write uses the interface setter.

**P2c-02 Engine diagnostics sink: health counters**

- **Change:** new `Source/Parsek/GhostPlaybackDiagnosticsSink.cs` (module Ghost by `^Ghost`):
  `internal interface IGhostPlaybackDiagnosticsSink` with `CountGhostBuild()`,
  `CountReentryFxDeferred()`, `CountReentryFxSkipped()`, `CountReentryFxBuild()`,
  `CountGhostReusedAcrossCycle()`, `CountGhostDestroy()`; and
  `internal sealed class GlobalGhostPlaybackDiagnosticsSink` whose bodies are the six
  `DiagnosticsState.health.<field>++` statements. `GhostPlaybackEngine` gets a private readonly
  `diagnostics` field and a second constructor `(IGhostPositioner, IGhostPlaybackDiagnosticsSink)`;
  the existing one-argument constructor chains to it (so it has a production caller, A1) and
  passes `new GlobalGhostPlaybackDiagnosticsSink()` (one
  instance per engine, no static). The six sites in 3.1 call the sink.
- **Proof:** new xUnit `GhostPlaybackDiagnosticsSinkTests`: the global sink moves each
  `DiagnosticsState.health` counter by exactly one (`[Collection("Sequential")]`,
  `DiagnosticsState.ResetForTesting`); a source gate `GhostPlaybackEngineDiagnosticsSourceGateTests`
  asserts no `DiagnosticsState.health` token in `GhostPlaybackEngine*.cs` (comment-stripped).
  Existing `DiagnosticsIntegrationTests` stay green. In-game category `Diagnostics` (lane H27).
- **Registers:** knot canary and README numbers if the sink class joins the knot (it references
  `DiagnosticsState` only; expected not to). `scripts/arch/log_tags.json` unchanged (no new log).
- **Size:** S. **Depends on:** none. **PC:** in-game `Diagnostics` category, lane H27 (nightly).
- **Risk:** low; six counter increments behind one interface call each.

**P2c-03 Engine diagnostics sink: frame budget, history and threshold**

- **Change:** the sink gains `ClearPlaybackBudget()` (the three early-return resets),
  `PublishPlaybackBudget(long total, long spawn, long destroy, int ghosts, float warpRate, ...)`
  carrying the six fields of `GhostPlaybackEngine.cs:882-887` with their present types,
  `AppendPlaybackFrame(float realtime, long totalMicroseconds)` (:889) and
  `CheckPlaybackBudgetThreshold(...)` with the argument list of :928. The global sink bodies are
  the present statements verbatim. The engine keeps the order of these calls.
- **Proof:** sink unit tests for each member against `DiagnosticsState` (budget fields equal, one
  history sample, the threshold check reached with the same arguments through a log-sink capture of
  its existing line); the source gate widens to every `DiagnosticsState.` and
  `DiagnosticsComputation.` token in `GhostPlaybackEngine*.cs`. In-game `Diagnostics`; lane H27;
  `test_runindex.HealthFactSourceSyncTests` must stay green (it reads
  `Diagnostics/DiagnosticsComputation.cs`, which this task does not edit).
- **Registers:** none beyond P2c-02.
- **Size:** S. **Depends on:** P2c-02. **PC:** in-game `Diagnostics`, lane H27.
- **Risk:** low; a misordered publish would show as a wrong budget line, which H27 reads.

**P2c-04 Spawn budget object an engine can share**

- **Change:** new `Source/Parsek/GhostSpawnFrameBudget.cs` (module Ghost): a class with
  `int Used`, `int MaxPerFrame` (from `GhostPlayback.MaxSpawnsPerFrame`), `bool IsExhausted`
  (through `GhostPlaybackLogic.ShouldThrottleSpawn(Used, MaxPerFrame)`), `void Consume()`,
  `void ResetForFrame()`. The engine gets `spawnBudget` and `ownsSpawnBudget`: a constructor
  overload takes an optional external budget; null creates a private budget and owns it, a
  budget passed in is not owned. The existing constructors chain to it with null, so the
  overload has a production caller before R3W-09 (section 8).
  `ResetPerFramePlaybackCounters` calls `spawnBudget.ResetForFrame()`
  only when it owns the budget. `TryReserveSpawnSlot` checks `spawnBudget.IsExhausted`; the
  increment at `GhostPlaybackEngine.GhostVisuals.cs:25` also calls `spawnBudget.Consume()`.
  `frameSpawnCount` stays as the engine's own count for its log lines (:911,
  `Diagnostics.cs:297`) and the throttle log at `SpawnDestroy.cs:444` keeps its text, printing
  the budget's `Used`. The test hooks keep their names and drive both counters. With one engine
  the two counters are always equal, so behavior is identical. Owners outside this task
  (OV-46): R3W-09 builds one shared budget, passes it to both engines and resets it once per
  frame before the first engine runs; the ghost-count budget `PlaybackGhostBudget` (R3W-14) is
  a different budget with its own name; the `GhostMapPresence` throttle is untouched and the
  rival map vessels' throttle is R3W-18's. Constructor order (OV-48, A3): this task follows
  P2c-02 and precedes P2a-02, R5-03, R3W-09 and R3W-14.
- **Proof:** new xUnit `GhostSpawnFrameBudgetTests`: an owned budget resets each frame; two
  engines on one external budget refuse the third spawn across both; an external budget is not
  reset by either engine; the throttle log line is unchanged (log-sink capture). Existing
  `LoopFirstRunSpawnTests`, `ChainLoopFirstRunSpawnTests`, `Bug406GhostReuseLoopCycleTests` and
  `StalePastEndCleanupDeferralTests` stay green. In-game `GhostPlayback` (lane S1.4, daily) and
  `GhostLifecycle` (LT-5, nightly).
- **Registers:** knot canary if the budget class joins the knot (it references only
  `GhostPlaybackLogic`; re-pin if it moves). No static, so no lifetime entry.
- **Size:** S. **Depends on:** P2c-02 (both edit the engine constructor at :362).
- **PC:** in-game `GhostPlayback`, lanes S1.4, LT-5.
- **Risk:** low; one engine sees one budget whose count equals today's field.

### P2a: the catalog port

**P2a-01 The port and the own-timeline catalog, no callers**

Not a pull request of its own. This task is the definition of the port: the member list, the
variant orders and the truth table below are the specification that P2a-02 to P2a-07 implement.
No P2a member lands without its production caller (the uncalled-method ratchet, section 8):

- The two files, the class and `Own` are created by the first P2a task to land, which is
  expected to be P2a-05 (no dependency outside P2a, no hot file shared with Track A or the
  positioner track; section 5). It adds only the members its call sites use, and it carries
  the registrations listed below.
- Every later P2a task adds the member(s), enum member(s) and catalog method(s) its own call
  sites use, together with those calls and the matching rows of
  `RecordingPlaybackCatalogTests`. The other P2a tasks depend on P2a-05 instead of on P2a-01.
  If another P2a task lands first, it takes over the creating role and its registrations.
- Who adds what (expected order): P2a-05 adds `PendingTree`, `TryFindFocusTree` and
  `TryFindRecording` with `CatalogRecordingScope.TreesThenPendingThenList`; P2a-03 adds
  `TryFindTreeHolding` with `CatalogTreeOrder.CommittedFirstContainsKey` and
  `CatalogRecordingScope.CommittedListOnly`; P2a-02 adds `CatalogTreeOrder.PendingFirstNonNull`
  (and `TryFindTreeHolding` itself if it lands before P2a-03). P2a-04, P2a-06 and P2a-07 call
  members already added and add only their test rows.

The port as specified:

- **Change:**
  - New `Source/Parsek/IPlaybackCatalog.cs`: `internal interface IPlaybackCatalog` with
    - `RecordingTree PendingTree { get; }` (null when none);
    - `bool TryFindTreeHolding(string id, CatalogTreeOrder order, out RecordingTree tree)` with
      `CatalogTreeOrder { PendingFirstNonNull, CommittedFirstContainsKey }`;
    - `bool TryFindFocusTree(string recordingId, string treeId, out RecordingTree tree)` (L3 / L4:
      committed first, then pending, id-or-containment, null `Recordings` skipped);
    - `bool TryFindRecording(string id, CatalogRecordingScope scope, out Recording rec)` with
      `CatalogRecordingScope { CommittedListOnly, TreesThenPendingThenList }`.
    The enum members name the orders of section 3.2 so each call site states its own order.
  - New `Source/Parsek/RecordingPlaybackCatalog.cs` (module Recording by `^Recording`):
    `internal sealed class RecordingPlaybackCatalog : IPlaybackCatalog` over `RecordingStore`
    and `PendingTreeSlot`, each variant a verbatim copy of the site it will replace, and
    `internal static readonly RecordingPlaybackCatalog Own` (stateless).
  - No empty catalog here. The port stays in the `Recording` module and returns `Recording` /
    `RecordingTree`; the rival lane's empty catalog `RecordingPlaybackCatalog.None` (every
    lookup answers none) is added by R3W-09 together with its first caller, because an uncalled
    member would move the uncalled-method ratchet. There is no `RivalCatalog` type (OV-47).
- **Proof (specification):** xUnit `RecordingPlaybackCatalogTests` (`[Collection("Sequential")]`,
  `RecordingStore.ResetForTesting`), created by the first P2a task, each row landing with the
  task that adds its variant: a truth table per variant over pending only, committed only,
  both holding the id (proves which wins), a tree whose `Recordings` holds the id with a null value
  (L1 rejects, L2 accepts), a tree with a null `Recordings` dictionary, a tree-id match without
  containment (L3 accepts, L2 rejects), an id only in the flat committed list (L5 finds, L1 does
  not), null and empty ids.
- **Registers (land with the first P2a task, expected P2a-05):** `scripts/arch/modules.toml`:
  add `^IPlaybackCatalog[.]cs$` to the Recording services rule at :266-267 (see Corrections 2).
  `scripts/ers-els-audit-allowlist.txt`: add `RecordingPlaybackCatalog.cs` with `[ERS-exempt]`
  (id lookup for anchor and loop resolution, the same reason the sites it replaces carry).
  Lifetimes: `Own` holds no state; if `test_static_lifetimes` flags it, tag it `process` in
  `scripts/arch/lifetimes.toml`.
- **Size:** none (no pull request). **Depends on:** none. **PC:** no.
- **Risk:** a member landed ahead of its caller reds `uncalledInAssembly`; the rule above
  prevents it.

**P2a-02 Engine tree lookup through the catalog (L1)**

- **Change:** the engine gets a private readonly `catalog` field and a constructor overload that
  takes one; the existing constructors chain to it passing `RecordingPlaybackCatalog.Own`, so
  the overload has a production caller before R3W-09 exists. It adds
  `CatalogTreeOrder.PendingFirstNonNull` to the catalog (and `TryFindTreeHolding` if P2a-03 has
  not landed), with its `RecordingPlaybackCatalogTests` rows (P2a-01).
  `ShouldUseLoopAnchoredDebrisChain` and `ShouldApplyOrdinaryParentAnchoredDebrisCoveragePolicy`
  gain a trailing optional `IPlaybackCatalog catalog = null` (null reads `Own`), so the static
  callers in `ParsekKSC.Playback.cs:247`, `GhostMapPresence.StateVectors.cs:712` and the tests are
  untouched. The call sites at :67 and :91 sit inside two more statics
  (`ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage`, :62, and
  `ShouldSkipRecordedRelativeResolverForAuthoredFrameGap`, :86), which have no instance
  catalog: both gain the same trailing optional `IPlaybackCatalog catalog = null` and hand
  it on, and their instance callers pass `catalog`. The engine's instance call sites (:120,
  :417, :445, `LoopPlayback.cs:1321`) pass `catalog`. `TryFindPlaybackRecordingTree` and
  `TryFindRecordingInTree` are deleted; the body uses
  `catalog.TryFindTreeHolding(id, PendingFirstNonNull, ...)`. The loop over `tree.Recordings` for
  loop anchors (:268 onward) stays.
- **Proof:** `GhostPlaybackEngineTests` 1165-1269 unchanged and green; a new cell passes a fake
  catalog and proves the engine consults it, not `RecordingStore`. In-game `GhostPlayback`,
  `Pipeline-Anchor`; lanes S1.4, H11.
- **Registers:** CLAUDE.md "Key source files" bullet for `GhostPlaybackEngine.cs`: replace "Zero
  `Recording` references" with "reads recordings only through `IPlaybackTrajectory` and the
  `IPlaybackCatalog` port" (Corrections 5); then `cp .claude/CLAUDE.md AGENTS.md`. This task
  owns that correction (E.4.14, DC-133); no other plan edits the sentence. Layer pins:
  Ghost -> Recording is a forward edge; no backward pair moves.
- **Size:** S. **Depends on:** P2a-05, P2c-04 (constructor; OV-48 order P2c-02, P2c-04, this
  task, then R5-03, R3W-09, R3W-14). **PC:** in-game, lanes S1.4, H11.
- **Risk:** low; the copy is verbatim and the default path is the same store read.

**P2a-03 Positioner and flight context builder through the catalog (L2, L7, L8, P1)**

- **Change:** `ParsekFlightGhostPositioner` gets a `catalog` field set from a trailing optional
  constructor parameter (null reads `RecordingPlaybackCatalog.Own`; one constructor, no
  test-only overload). The catalog gains `TryFindTreeHolding` with
  `CatalogTreeOrder.CommittedFirstContainsKey` and `CatalogRecordingScope.CommittedListOnly`,
  with their `RecordingPlaybackCatalogTests` rows (P2a-01). `TryFindRelativeAnchorFocusTree`
  (:4551) becomes an instance
  call of `catalog.TryFindTreeHolding(id, CommittedFirstContainsKey, ...)`; the diagnostic at :509
  uses `TryFindRecording(id, CommittedListOnly, ...)`. `BuildFlightRelativeAnchorResolverContext`
  (`ParsekFlight.TimelinePlayback.cs:553`) takes an `IPlaybackCatalog` parameter and reads
  `PendingTree` from it; its two callers (positioner :4474, `ParsekFlightPlaybackHost.cs:2367`)
  pass the own catalog. L7 is P2a-04.
- **Proof:** `RelativeAnchorResolutionTests`, `RecordedRelativeAnchorResolver*` tests,
  `GrepAuditNonLoopLivePidTests` green; a test that `TryFindRelativeAnchorFocusTree` sees a tree
  only the fake catalog holds. In-game `Pipeline-Anchor`, `PlaybackFidelity`; lanes H11, LT-1,
  H36.
- **Registers:** none.
- **Size:** S. **Depends on:** P2a-05. **PC:** in-game, lanes H11, LT-1, H36.
- **Risk:** low; one order per site, pinned by P2a-01's truth table.

**P2a-04 Tail-lift recording lookup through the catalog (L7)**

- **Change:** `ResolveRecordingById` (:1677) becomes
  `catalog.TryFindRecording(id, CommittedListOnly, ...)` inside the same try/catch;
  `ResolveRecordingForTailLift` (:5306), `ResolveTailLiftPlan` (:5323) and
  `ParsekFlight.ResolveEffectiveAltitudeWithTailLift` (`ParsekFlight.TailLift.cs:29`) gain a
  trailing optional catalog parameter (null reads `Own`); the positioner's three calls (:1163,
  :1167, :1766) pass its catalog. The `s_previewRecordingForTailLift` fallback and the static
  plan caches stay (Corrections 6). No catalog member is added (P2a-03 added
  `CommittedListOnly`).
- **Proof:** existing tail-lift tests (`*TailLift*` in `Source/Parsek.Tests`) green; a cell proving
  a fake catalog's recording drives the plan. In-game `GhostPlayback`; lane S1.4.
- **Registers:** none.
- **Size:** S. **Depends on:** P2a-03 (same positioner constructor). **PC:** in-game, lane S1.4.
- **Risk:** low.

**P2a-05 RecordedRelativeAnchorPoseResolver through the catalog (L3, L5, L9, P2)**

- **Change:** both `TryResolveSectionAnchorPose` overloads (:49, :63) gain a trailing optional
  `IPlaybackCatalog catalog = null` (null reads `Own`) that flows to `TryFindFocusRecording`,
  `TryBuildContext` (`pendingTree: catalog.PendingTree`), `TryFindFocusTree`
  (`catalog.TryFindFocusTree`) and `TryFindRecordingById`
  (`catalog.TryFindRecording(id, TreesThenPendingThenList, ...)`). The `is Recording` cast at
  :120-133 stays first. Callers `ParsekKSC.Playback.cs:549` and
  `GhostMapPresence.StateVectors.cs:1106` pass nothing.
- **First P2a task (P2a-01):** this task creates `IPlaybackCatalog.cs`,
  `RecordingPlaybackCatalog.cs` and `Own` with only the members its call sites use:
  `PendingTree`, `TryFindFocusTree`, and `TryFindRecording` with the enum
  `CatalogRecordingScope` holding `TreesThenPendingThenList` only. Picked because it depends on
  nothing outside P2a and edits no file of Track A or the positioner track.
- **Proof:** `RecordedRelativeAnchorPoseResolver` tests and `RelativeAnchorResolutionTests` green;
  a fake-catalog cell; new `RecordingPlaybackCatalogTests` with the P2a-01 truth-table rows of
  its three members (L3 / L4 focus tree, L5 / L6 trees-then-pending-then-list, pending read).
  In-game `Pipeline-Anchor`, `GhostMap`; lanes H11, H44.
- **Registers:** P2a-01's list: the `modules.toml` rule for `IPlaybackCatalog.cs`, the ERS
  allowlist entry for `RecordingPlaybackCatalog.cs`, and the `process` lifetime tag for `Own` if
  `test_static_lifetimes` flags it.
- **Size:** M (two new files, the registrations and the first test class beside the resolver
  change). **Depends on:** none. **PC:** in-game, lanes H11, H44.
- **Risk:** low.

**P2a-06 ProductionAnchorWorldFrameResolver through the catalog (L4, P3)**

- **Change:** the resolver takes an `IPlaybackCatalog` as a trailing optional constructor
  parameter (null reads `Own`; no test-only overload; the two construction sites
  `Rendering/AnchorPropagator.cs:169` and `Rendering/RenderSessionState.cs:1129`
  pass nothing). `TryFindFocusTree(Recording)` (:411) uses `catalog.TryFindFocusTree`; the context
  builder (:403) reads `catalog.PendingTree`. No catalog member is added (P2a-05 added both).
- **Proof:** the `Rendering/` anchor tests green; a fake-catalog cell. In-game `MapRender`,
  `GhostMap`; lanes S1.6, S1.7, H44.
- **Registers:** layer pins: Rendering -> Recording is forward; none move.
- **Size:** S. **Depends on:** P2a-05. **PC:** in-game, lanes S1.6, S1.7, H44.
- **Risk:** low.

**P2a-07 Map state vectors through the catalog (L6)**

- **Change:** `TryFindRecordingByIdForBodyFixedAnchorPid` (`GhostMapPresence.StateVectors.cs:735`)
  becomes a call of `RecordingPlaybackCatalog.Own.TryFindRecording(id, TreesThenPendingThenList,
  ...)`. The raw reads at :1274-1288 and :1591-1598 are not lookups by id and stay (they are P3's).
  No catalog member is added (P2a-05 added the variant).
- **Proof:** `GhostMapPresence` state-vector tests green. In-game `GhostMap`; lanes H44, S1.6.
- **Registers:** none.
- **Size:** S. **Depends on:** P2a-05. P3-02 (same file) lands after this task (E.3.8).
- **PC:** in-game, lanes H44, S1.6.
- **Risk:** low; the one map-side site, kept on the own catalog because map presence is own-lane
  until P3 adds rival presence.

### P2b: the lane object

The P2b series lands as one window: no other PR that touches the files in section 5's hot list
merges between P2b-02 and P2b-10, no P6 PR merges in that window either (it shares no file, but
the proof must fly one known change set), and the flight proof (section 6.4) runs once
after P2b-10.

**P2b-01 il_compare folds a renamed reference field**

- **Change:** `scripts/refactor/il_compare.py` gains a repeatable
  `--alias-field TYPE::NEW=OLD`, applied after the folds: `ldfld class X TYPE::NEW` reads
  `ldfld class X TYPE::OLD`, and the alias field's declaration is listed among the expected
  leftovers. Docstring updated.
- **Proof:** new cells in `scripts/refactor/test_parsekflight_tools.py`: an alias folds a
  `lane.x` body onto a `host.Lane.x` body; without the alias the difference is reported; an alias
  on another type does not apply. Run locally with
  `python3 -m unittest discover -s scripts/refactor -q` (not in CI, Corrections 11).
- **Registers:** none.
- **Size:** S. **Depends on:** none. **PC:** no.
- **Risk:** low; tooling only, and a too-broad alias would hide differences, which the negative
  cells pin.

**P2b-02 The lane type and the seven log-once sets (fold)**

- **Change:** new `Source/Parsek/ParsekFlightPlaybackLane.cs` (Controllers by the `^ParsekFlight`
  rule): `internal sealed class ParsekFlightPlaybackLane` holding the seven sets with the same
  names, types, comparers, modifiers and initializers as `ParsekFlight.cs:873-874, 2506-2516`.
  `ParsekFlight` gains `private ParsekFlightPlaybackLane lane;` and
  `internal ParsekFlightPlaybackLane Lane => lane ?? (lane = new ParsekFlightPlaybackLane());`
  (lazy, because 22 test files build an uninitialized host). Every site in 3.3 for these seven
  members reads `Lane.<name>` (on ParsekFlight) or `host.Lane.<name>` (positioner, playback host).
- **Proof:** `il_compare BEFORE AFTER --fold Parsek.ParsekFlight=Parsek.ParsekFlightPlaybackLane`
  reports 0 moved-body differences; leftovers: `lane`, `Lane`, `get_Lane`, the lane constructor.
  `FlightGhostSkipCacheCleanupTests` reads the six sets from `host.Lane` (reflection on the lane
  type). `CommittedListNotificationTests` and `RelativeAnchorResolutionTests:758` pass unchanged
  (substrings still match). Full xUnit.
- **Registers:** `ParsekFlightSourceFiles.ServiceFileNames` adds the file (so `ReadAll` and the
  "no service listens or writes" gate cover it). CLAUDE.md "Key source files", `ParsekFlight.cs`
  bullet: one sentence naming the lane and what it holds; `cp .claude/CLAUDE.md AGENTS.md`. Knot:
  the lane references only BCL types now, so the canary should not move; run it.
- **Size:** S. **Depends on:** P2a-02 to P2a-07, P2c-03 and R5-03 (Tracks A, C, D merged;
  P2a-03 and P2a-04 are the positioner edits that must land first; R5-03's engine arguments
  merge before the window opens, an order, not a code need).
- **PC:** no (IL-identical bodies); covered by the series run.
- **Risk:** low; fold-proven.

**P2b-03 Orbit cache on the lane (fold)**

- **Change:** `orbitCache` moves to the lane (same declaration). Sites: positioner 936, 937, 1415,
  1818, 1892, 1912, 2830, 2849, 3494, 3512; `ParsekFlightPlaybackHost.cs:202, 318`.
- **Proof:** the same fold, 0 moved-body differences. `FlightGhostSkipCacheCleanupTests` reads
  `orbitCache` from the lane. `complexityWatch` in `scripts/arch/facts_pins.toml` lists
  `ParsekFlightGhostPositioner::InterpolateAndPosition`, whose body changes only by the accessor
  call; re-pin if its measure moves.
- **Registers:** knot canary if the lane joins (it now names KSP's `Orbit`, an external type; not
  expected).
- **Size:** S. **Depends on:** P2b-02. **PC:** no.
- **Risk:** low.

**P2b-04 Ghost position entries and reapply stamps on the lane (fold)**

- **Change:** `ghostPosEntries`, `ghostPreCullReapplyFrame`, `ghostLateUpdateReapplyFrame`,
  `AddOrReplaceGhostPosEntry` and the nested struct `GhostPosEntry` move to the lane (the struct as
  `ParsekFlightPlaybackLane.GhostPosEntry`, so the lane does not name `ParsekFlight` and stays off
  the knot). Sites in 3.3; type references in the positioner (216, 1717, 2078), the playback host
  (111, 187, 199, 292, 386, 398, 483, 495, 504) and the two test files.
  `PendingGhostPosEntryCountForInGameTest` and `PositionGhostAtSurfaceForInGameTest` stay on
  ParsekFlight reading `Lane`.
- **Proof:** the same fold (nested paths fold), 0 moved-body differences; `PlaybackHostUnitTests`,
  `Rendering/LateUpdateSplineDispatchTests` green after the type-name edit.
- **Registers:** knot canary (expect no move; re-pin and README if it does).
- **Size:** S. **Depends on:** P2b-03. **PC:** no.
- **Risk:** low.

**P2b-05 Loop-unit lookup on the lane (fold)**

- **Change:** `cachedLoopUnits` moves to the lane. `CurrentCachedLoopUnits` stays on ParsekFlight
  as `=> Lane.cachedLoopUnits`. Sites: positioner 1021; `ParsekFlightPlaybackHost.cs` 1582, 2024,
  2202, 2206, 2308-2310, 2324. `builtLoopUnits` stays on the playback host.
- **Proof:** the same fold, 0 moved-body differences. `LoopUnitSetCoherenceTests` :59 and :61
  re-pin to `"host.engine.SetLoopUnits(host.Lane.cachedLoopUnits);"` and
  `"... CurrentCachedLoopUnits => Lane.cachedLoopUnits;"` (the test still proves one reference
  feeds engine, polyline and map).
- **Registers:** knot canary: the lane now names `GhostPlaybackLogic.LoopUnitSet`; if that type is
  in the knot the lane joins it (+1); re-pin the canary and the README numbers in this commit.
- **Size:** S. **Depends on:** P2b-04. **PC:** no.
- **Risk:** low.

**P2b-06 The positioner holds the lane (fold with alias)**

- **Change:** the one constructor becomes `ParsekFlightGhostPositioner(ParsekFlight host,
  ParsekFlightPlaybackLane lane, ...)` (P2a-03's optional catalog parameter stays last until
  P2b-09). No one-argument form is kept: only `LoopSeamTraceIndexShiftTests.cs:14` would call
  it, and a test-only constructor moves the uncalled-method ratchet (section 8); that test
  passes `(null, null)`. Every `host.Lane.` in the positioner reads `lane.`. ParsekFlight's
  lazy `Positioner` (:948-950) passes `Lane`.
- **Proof:** `il_compare --fold Parsek.ParsekFlight=Parsek.ParsekFlightPlaybackLane
  --alias-field Parsek.ParsekFlightGhostPositioner::lane=host` reports 0 moved-body differences;
  leftovers: the field and the changed constructor. Full xUnit.
- **Registers:** none.
- **Size:** S. **Depends on:** P2b-01, P2b-05. **PC:** no.
- **Risk:** low.

**P2b-07 The late-update helpers take the lane**

- **Change:** `ParsekFlightPlaybackHost.ApplyGhostPosEntries` (:69), the `Reapply*` helpers
  (:111-480) and `ClampGhostsToTerrain` (:611) take a `ParsekFlightPlaybackLane lane` parameter in
  place of reading `host.Lane` (sites :72, :74, :126, :202, :318, :618, :620) and reach the
  positioner through `host.Positioner` as today. `ParsekFlight.LateUpdate` (:1352-1366) and
  `OnCameraPreCull` (:1368-1402) pass `Lane`.
- **Proof:** not foldable (a parameter replaces a field read). Proof is `il_compare --show
  ApplyGhostPosEntries` (and each helper) showing only `ldarg` in place of the host-and-accessor
  pair; `PlaybackHostUnitTests`, `Rendering/LateUpdateSplineDispatchTests` green. In-game
  `GhostReapplyFrame` and `ChainTipBlockedGhost` (run by hand, no spec) and `GhostPlayback`;
  daily tier.
- **Registers:** none.
- **Size:** S. **Depends on:** P2b-06.
- **PC:** yes: the two hand-run categories plus the daily tier (about 0.4 h).
- **Risk:** medium; this is the per-frame re-apply path, and a wrong lane argument would only show
  in a flight.

**P2b-08 Watch and ghost-world queries on the lane**

- **Change:**
  - New `Source/Parsek/ParsekFlightLaneQueries.cs` (Controllers): `internal interface
    IGhostLaneWatch` with `bool IsWatchingGhost { get; }`,
    `bool TryGetWatchedGhostAnchorWorldPosition(out Vector3d)` and
    `ZoneRenderingResult ApplyZoneRendering(...)` with the parameter list of
    `ParsekFlight.ApplyZoneRenderingImpl` (`ParsekFlight.GhostPositioner.cs:23`).
  - New `Source/Parsek/GhostWorldLookup.cs` (Ghost): `internal interface IGhostWorldLookup` with
    `bool TryGetGhostWorldByRecordingId(string, out Vector3d)`.
  - ParsekFlight implements both explicitly. The bodies are today's expressions:
    `watchMode != null && watchMode.IsWatchingGhost`, the anchor read, `ApplyZoneRenderingImpl(...)`
    (which keeps `ResolveZoneWatchState` and its index-keyed watch-protection read), and
    `engine != null && engine.TryGetGhostWorldByRecordingId(...)`.
  - The lane gets `Watch` and `GhostWorlds`, set by the lazy `Lane` getter to `this`, so an
    uninitialized test host behaves as today.
  - Positioner sites 451-452, 1082 and 2483-2485 call `lane.GhostWorlds` / `lane.Watch`.
  - A rival lane later supplies a watch that answers "not watched, not protected"; the first
    race build keeps that answer. P3b, deferred out of R0b, extends it when watching a rival
    flight is scheduled.
- **Proof:** `Issue316WatchProtectionTests`, `WatchModeCleanupRegressionTests`,
  `WatchModeControllerTests`, `ZoneRenderingTests` green; new cells: the positioner calls the lane's
  watch (fake), and a null `watchMode` still answers not-watching. In-game `Watch`, `GhostPlayback`;
  lanes LT-1, W1-watch-distance-cutoff (operator), daily tier.
- **Registers:** knot canary: the interfaces name `ZoneRenderingResult` and `GhostPlaybackState`;
  re-pin if a type joins. Layer pins: `IGhostWorldLookup` is Ghost and the engine implements it (no
  new backward pair); `ParsekFlight` -> both is entry -> lower (forward).
- **Size:** M. **Depends on:** P2b-06.
- **PC:** yes: in-game `Watch`, lanes LT-1 and W1-watch-distance-cutoff.
- **Risk:** medium; interface dispatch replaces direct calls on the zone path, and the null
  handling must equal today's.

**P2b-09 The catalog moves onto the lane**

- **Change:** the lane gets `IPlaybackCatalog Catalog` (lazy getter sets `Own`; R3W-09's rival
  lane sets `RecordingPlaybackCatalog.None` on its own lane object, OV-47). The positioner's
  P2a `catalog` field is replaced by `lane.Catalog`; the playback host's context-builder call
  (:2367) passes `host.Lane.Catalog`. The positioner constructor no longer takes a catalog.
- **Proof:** full xUnit; the P2a fake-catalog cells now set the catalog on a lane.
- **Registers:** none.
- **Size:** S. **Depends on:** P2b-06, P2a-03, P2a-04. **PC:** no.
- **Risk:** low.

**P2b-10 The lane owns its resets**

- **Change:** the lane gets two methods:
  - `ClearForTeardown(string reason)`: clears the orbit cache, the seven sets and the position
    entries, and logs one `ParsekLog.Verbose("Flight", "Playback lane cleared: reason=... orbits=N
    logSets=N posEntries=N")` line;
  - `ClearIndexKeyedState(string reason, int index)`: clears the orbit cache and the two orbit log
    sets, same line shape.

  A third method `ClearOrbitCache()` clears the orbit cache alone. The positioner's
  `predictedOrbitTailContinuityCache` stays its own and is still cleared through
  `Positioner.ClearOrbitPlaybackCaches()`, which calls `lane.ClearOrbitCache()` for the lane half.
  Call sites:
  - teardown (`ParsekFlight.cs:1590-1601`);
  - `DestroyAllTimelineGhosts` (`ParsekFlightPlaybackHost.cs:2595-2606`);
  - preview stop (:737-739);
  - the two index-shift handlers (`ParsekFlight.cs:2357-2358, 2384-2385`).

  The non-lane clears (`nearbySpawnCandidates` and the two others) keep their places. Within a
  site, independent `HashSet` clears change order, which is not observable. Flight ready needs
  nothing (one instance per scene, 3.3).
- **Proof:** `CommittedListNotificationTests.FlightHandlers_StayOnParsekFlight_InOrder_...` re-pins
  the two handlers' call lists to `Lane.ClearIndexKeyedState(` after
  `Positioner.ShiftLoopSeamTraces*` and `mapMarkers?.ClearMapMarkerCache();`.
  `FlightGhostSkipCacheCleanupTests` asserts every lane set is empty after
  `DestroyAllTimelineGhosts`. A new `ParsekFlightPlaybackLaneTests` covers:
  - each reset clears exactly its members;
  - two lane instances are independent;
  - the log line text (log-sink capture, invariant culture).

  In-game `GhostPlayback`, `GhostLifecycle`, `StandaloneLoopMap`.
- **Registers:** `scripts/arch/log_tags.json` unchanged (tag `Flight` exists). CLAUDE.md
  `ParsekFlight.cs` bullet: one clause on the lane resets.
- **Size:** S. **Depends on:** P2b-09.
- **PC:** yes: the representative flight proof of section 6.4 (18 lanes plus the daily tier,
  through the parallel runner) runs after this task.
- **Risk:** medium; a missed clear would leak a log-once key or a stale orbit across an index shift,
  which only long loop and map flights show.

## 5. Parallel tracks

Hot files (two tasks touching one of them must land in order):

- `GhostPlaybackEngine.cs` (constructor :362, :91, :388-928, :1050): P2c-02, P2c-03, P2c-04,
  P2a-02. Constructor order across plans (OV-48, A3): P2c-02, P2c-04, P2a-02, R5-03, R3W-09,
  R3W-14. R5-03 merges before P2b-02 opens the window (this file is on the hot list); R3W-09
  and R3W-14 come after P2b-10.
- `GhostPlaybackEngine.Diagnostics.cs`, `GhostPlaybackEvents.cs`: P2c-02, P2c-03, then R3W-01
  (written against P2c-03's source gate, E.5.4).
- `ParsekFlightGhostPositioner.cs`: P2a-03, P2a-04, P2b-02 to P2b-09.
- `ParsekFlight.cs` (fields, wiring :1207-1219, teardown :1590-1601, handlers :2345-2389):
  P2b-02 to P2b-10.
- `ParsekFlightPlaybackHost.cs`: P2a-03 (:2367), P2b-02 to P2b-10.
- `GhostMapPresence.StateVectors.cs`: P2a-05 (callers only, no edit), P2a-07, then P3-02 (other
  slice).
- `IPlaybackCatalog.cs`, `RecordingPlaybackCatalog.cs`, `RecordingPlaybackCatalogTests.cs`:
  created by P2a-05; P2a-03 and P2a-02 add members and rows; P2a-04, P2a-06, P2a-07 add test
  rows only. The edits are additive (one member, one enum member, one variant), so a rebase is
  trivial; a second creator is the only real conflict, which the P2a-05-first order avoids.

Tracks:

- Track A (engine): P2c-02 -> P2c-03 -> P2c-04 -> P2a-02 (P2a-02 also after P2a-05).
- Track B (independent, any time): P2c-01, P2b-01. P2a-01 is the port's specification, not a
  pull request.
- Track C (anchors): P2a-05 first (it creates the catalog files, P2a-01), then P2a-06 and
  P2a-07 in parallel with each other; all of Track C runs in parallel with Track A (separate
  files).
- Track D (positioner, after P2a-05): P2a-03 -> P2a-04.
- Track E (lane, serial, alone): P2b-02 -> ... -> P2b-10 after Tracks A, C, D are merged.

Against other prerefactors:

- In parallel with P1 (P1-01 to P1-03: `SpawnCollisionDetector`, `HomeSpaceCenter` in Core),
  P7 (P7-01, P7-02: `RecordingPaths`), P10 (P10-01: `TestCommands` save core), P11 (P11-01,
  P11-02: harness specs, `ParsekUI` constructors) and P12 (P12-01, P12-02: `scripts/arch`
  parsers, a Controllers placement rule): every P2c and P2a task touches none of their files.
  Shared registration files are `scripts/arch/modules.toml` (P2a-05 adds the
  P2a-01 rule; P12 edits `[forbidden]` and the Controllers rule) and the knot canary in
  `scripts/arch/test_archview.py:4031`: these merge as text conflicts that the later PR re-derives
  mechanically.
- P2a-02 to P2a-07 and P2c-02 to P2c-04 must not run beside P2b (shared positioner and engine
  files); the 15.11 order already puts them first.
- P3 collides with P2a-07 (`GhostMapPresence.StateVectors.cs`); P3-02 lands after P2a-07
  (E.3.8).
- P3b collides with P2b-08 (`watchMode` use, the watch seam) and with every P2b step that edits
  `ParsekFlight.cs`. P3b is deferred out of R0b (built when watching a rival flight is
  scheduled); when built, it starts after P2b-10.
- P6 (P6-01 to P6-05, trajectory codec) touches none of these files; it is built in parallel
  with the other prerefactors and merged when green, but it does not merge inside the P2b window
  (P2b-02 to P2b-10 and the flight proof of 6.4), so that proof flies one known change set.
- R3W-01 lands after P2c-03 (E.5.4): it shares `GhostPlaybackEngine.Diagnostics.cs` and
  `GhostPlaybackEvents.cs` and is written against P2c-03's source gate.

## 6. Tests and proof

### 6.1 Linux (cloud or any agent)

- `scripts/cloud-test.sh` for every task (full xUnit).
- `python3 -m unittest discover -s scripts/arch -q`, and after a Debug build the same command with
  `PARSEK_ARCH_IL=Source/Parsek/bin/Debug/Parsek.dll` set
  for every task (knot, layers, lifetimes, facts, headless).
- `il_compare` for P2c-01 (no fold), P2b-02 to P2b-06 (fold, 0 moved-body differences), P2b-07
  (`--show` diff limited to the parameter). The PR names every leftover.
- `python3 -m unittest discover -s scripts/refactor -q` for P2b-01.

### 6.2 New xUnit classes

`GhostPlaybackDiagnosticsSinkTests`, `GhostPlaybackEngineDiagnosticsSourceGateTests`,
`GhostSpawnFrameBudgetTests`, `RecordingPlaybackCatalogTests` (created by P2a-05; P2a-03 and
P2a-02 add the rows of the variants they add, P2a-01), fake-catalog cells in the existing
engine, positioner and resolver test classes, `ParsekFlightPlaybackLaneTests`. None adds an
in-game test, so no batch tally (`CommittedBatchTallySourceSyncTests`) and no lane catalog
(`harness/tools/lane_catalog.py`) changes.

### 6.3 In-game categories (owner's PC, Ctrl+Shift+T or the lane named)

| Category | Proves | Driven by |
|---|---|---|
| Diagnostics | sink publishes the same budget and counters | H27 (nightly) |
| GhostPlayback | positioning, tail lift, spawn throttle | S1.4 (daily) |
| GhostLifecycle | spawn, reuse, destroy, resets | LT-5 (nightly) |
| Pipeline-Anchor | catalog order for relative anchors | H11 (nightly), LT-1 |
| PlaybackFidelity | relative and body-fixed playback | H36 (nightly) |
| GhostMap, MapRender | map state vectors and render anchors | H44, S1.6, S1.7 |
| StandaloneLoopMap | loop units through the lane | SLM-1, SLM-1T, SLM-2 (operator) |
| Watch | watch query seam | LT-1, V*M lanes |
| GhostReapplyFrame, ChainTipBlockedGhost | late-update re-apply through the lane | none; run by hand |

### 6.4 The P2b flight proof

P2b changes how every ghost is positioned every frame. The owner's answer (question 1, Q-05,
2026-11-14): no run of every lane for this; after P2b-10 the owner flies a short set of the
most representative lanes, one per distinct way the positioner places a ghost, to recheck the
ghost system. The full run (every lane on every install, `tier_runner.py --tier full`) is not
flown for P2b; it runs once, at release time. No P6 PR merges between P2b-02 and this run, so
it flies one known change set.

The set flies through the parallel runner (`harness/README.md`, "Parallel runs"), on two
copies while the owner works at the PC and three when away, with the daily tier added in the
same run (a lane selected twice flies once):

```
cd harness
python tools/parallel_runner.py run --name p2b --tier daily --ids-file set.txt --slots 3
```

where `set.txt` holds the 18 lane ids below, one per line.

Every lane below is on the `stock-minimal` install, which is the profile the runner needs a
provisioned copy of (`provision.py --profile stock-minimal --copy 2`, then `--copy 3`), and
none is an exclusive (cursor or focus) lane, so none is held back for `--include-exclusive`.
Tier and p50 come from the spec and from `harness/coverage/duration.json` (the measured p50
the runner orders by; the generated lane catalog `docs/dev/flight-profiles.md` lists no p50).

| Lane | Placement path it exercises | Tier | Measured p50 |
|---|---|---|---|
| S1.4-injected-playback | basic playback over the injected synthetic corpus: positioning and tail lift (`GhostPlayback` category), map presence and the polyline | daily | 70 s |
| H17-flight-integration | body-fixed surface placement: a ghost's world position matches its recorded lat / lon / alt through `GetWorldSurfacePosition` (D3 `surface-body-fixed`) | nightly | 69 s |
| GS-12-kerbalx-loop-cycles | a flown atmospheric ascent replayed (Kerbal X), its booster debris ghosts (parent-anchored debris, `Kerbal X Debris` spawned at least 6), then the mission looped in three period modes under 10x rails warp, overlap cap | operator | 432 s |
| S1.8-soi-crossing-playback | orbital segment resolution across the two SOI handoffs of a real flown transfer (`FindOrbitSegment*`, `EvaluateOrbitSegmentTruePositionAtUT`); no ghost spawned | daily | 54 s |
| V7W-minmus-physics-warp | orbital ghosts replayed under physics warp, across the Kerbin to Minmus SOI seam and in watch mode | nightly | 98 s |
| V6M-mun-player-loop | a moon mission loop (same-parent, phase-locked retiming): Warp-to, the SOI seam on rails, Watch at the Mun park, two cycles | operator | 53 s |
| RL-1-relative-loop-live-anchor | a loop-anchored Relative section placed against a live anchor vessel by the flight loop positioner (D3 `relative-loop`) | nightly | 139 s |
| LT-2-long-tail-spacecenter | a non-loop Relative section with `anchorRecordingId`, placed through the recorded-anchor pose resolver by the KSC positioner (D3 `relative-anchored-nonloop`) | nightly | 53 s |
| SLM-1-standalone-loop-map-position | a standalone loop's map ghost at the loop clock UT (loop units through the lane, `StandaloneLoopMap`) | operator | not measured |
| LT-5-long-tail-playback-flight | active ghosts, loop-unit and overlap cells (`GhostLifecycle`) | nightly | 70 s |
| W1-watch-distance-cutoff | the Watch camera following a ghost, refused at 1,064 km and entered at 0.46 km (the 300 km cutoff) | operator | 147 s |
| S1.7-maprender-parity | the flight map render spine: descent, dock-undock, overlap, parent-anchored and BG-on-rails cells, the polyline (`MapRender`) | daily | 69 s |
| H44-ghost-map-trackstation | Tracking Station ghost map presence, polyline legs, marker hold (`GhostMap`, TRACKSTATION) | nightly | 67 s |
| TP-1-mun-loop-map-pictures | the picture lane: the looped Mun mission on the open flight map, captured at transfer, arrival and park | operator | not measured |
| V22K-kerbin-splashdown-ksc-arrival | Space Center scene ghosts (`ParsekKSC` host, `KSC SURFACE playback resolved`) of a splashdown descent | operator | 62 s |
| RF-8-ghost-during-refly | the committed sibling's ghost during a live Re-Fly, watched and drawn on the map | operator | 61 s |
| H11-pipeline-anchor | the anchor seed epsilon through `ProductionAnchorWorldFrameResolver` and the SOI and checkpoint fixtures (resolves, plays nothing back) | nightly | 47 s |
| H66-playback-control-isolated | spawn at the end of a ghost's flight, exactly once (D6 `spawn-at-end-pid-dedup`) | nightly | 70 s |

Summed measured p50, one lane at a time: 1,561 s, about 26 minutes, plus the two lanes not
measured (SLM-1, TP-1). On three copies, an estimate from the one measurement so far (the
daily tier took 12 min 35 s on three copies against about 25 minutes one lane at a time, about
half): about 13 minutes plus the two unmeasured lanes, and never less than the longest lane
(GS-12, 432 s). The daily tier added to the same run is about 25 minutes one lane at a time
and 12 min 35 s on three copies (measured), less the three lanes it shares with the set.

Paths with no lane, said rather than stretched: the ghost position re-apply in late update
(`GhostReapplyFrame`, `ChainTipBlockedGhost`; no lane runs either category, so they stay hand
runs); a non-loop Relative section placed by the FLIGHT positioner (LT-2 covers the same
recorded-anchor resolver at the KSC only); a Re-Fly ghost placed through an anchor (RF-8 shows
the sibling ghost during a Re-Fly, H11 the anchor resolver without playback; RW-5, RW-6 and
RW-7 are ledger lanes on another install with no measured p50).

Per-task checks that stay: the daily tier (about 0.4 h one lane at a time) after P2b-07 and
P2b-08, and after P2b-10 inside the set's run as above; the two hand-run categories
(`GhostReapplyFrame`, `ChainTipBlockedGhost`) after P2b-07 and again after P2b-10, since no
lane covers them.

P2c and P2a need no full run either: their in-game categories and the lanes named per task,
flown once after Track A, C and D merge (nightly tier, about 9.5 h one lane at a time, covers
H27, H11, H36, LT-5; S1.4, S1.6, S1.7 are daily). That nightly also flies through the
parallel runner; the nightly on it is not yet measured, so its time there is only the
estimate of about half.

Not provable without the owner's PC: every in-game category above, every harness lane, and the
absence of a visual difference in ghosts on screen (TP-1 in the set is the picture check until
the release run's picture lanes).

## 7. Gates and registrations

- Architecture map: `IPlaybackCatalog.cs` needs a placement rule (P2a-01, landed by P2a-05).
  All other new files
  place by existing prefix rules: `GhostPlaybackDiagnosticsSink.cs`, `GhostSpawnFrameBudget.cs`,
  `GhostWorldLookup.cs` (Ghost); `RecordingPlaybackCatalog.cs` (Recording);
  `ParsekFlightPlaybackLane.cs`, `ParsekFlightLaneQueries.cs` (Controllers).
- Knot canary `scripts/arch/test_archview.py:4031` and the README numbers: re-pinned in the commit
  of any task whose new type joins a cycle (most likely P2b-05 and P2b-08). The semantic knot
  residue ratchet re-pins the same way.
- Layers: P2a edges go from Ghost, Rendering and Controllers down to Recording (forward). No
  backward pair is added if the port is in Recording (Corrections 2). Putting it in Core would add
  Core -> Recording pairs (`layer_pins.toml`, pinned at 10 today) and change the two Core kernel
  canaries.
- Forbidden edges (`Missions -> Logistics`, `Ghost -> GameActions`): untouched.
- Lifetimes: P2b moves instance fields only (the gate covers statics).
  `RecordingPlaybackCatalog.Own` is a stateless static readonly instance; tag it `process` only
  if the gate asks.
- Facts: `complexityWatch` entry `ParsekFlightGhostPositioner::InterpolateAndPosition` re-pins if
  its measure moves (P2b-03); `uncalledInAssembly` must not rise, so no production member exists
  only for tests (the throttle test hooks already exist): each catalog member lands with its
  caller (P2a-01), the engine's new constructor overloads are reached by chaining (P2c-04,
  P2a-02), and the positioner keeps one constructor (P2a-03, P2b-06); `crossTypeStaticWrites`
  unchanged.
- Headless: Core changes only by P2c-01's setter removal (no leak change). No headless module
  gains code.
- Grep-audit gates: `scripts/ers-els-audit-allowlist.txt` gains `RecordingPlaybackCatalog.cs`
  (P2a-01, landed by P2a-05). No other gate changes.
- Source gates re-pinned in the same commit: `LoopUnitSetCoherenceTests` (P2b-05),
  `CommittedListNotificationTests` (P2b-10), `FlightGhostSkipCacheCleanupTests` (P2b-02, -03,
  -10); `ParsekFlightSourceFiles.ServiceFileNames` gains the lane file (P2b-02).
- Save format: none (`LoopSyncParentIdx` is runtime-only; no persisted key changes).
- GUI gates and `docs/dev/design-gui-inventory.md`: none.
- CHANGELOG: none (nothing the player sees). `docs/dev/todo-and-known-bugs.md`: one entry per
  prerefactor (P2c, P2a, P2b) marked done as its last task lands. CLAUDE.md: P2a-02 (engine
  bullet), P2b-02 and P2b-10 (ParsekFlight bullet), each followed by
  `cp .claude/CLAUDE.md AGENTS.md`.

## 8. Risks

- A catalog variant differs from the site it replaces in one case (null value in a tree, tree-id
  match, flat-list fallback). Noticed by the P2a-01 truth table, written from section 3.2, each
  row landing with the task that moves its caller, and by the anchor lanes H11 and H36.
- A member lands before its production caller. `uncalledInAssembly` counts every production
  method the roots do not reach, including one only a test calls, and may not exceed its bound
  (`docs/dev/arch/README.md`, `scripts/arch/facts_pins.toml`), so a port member, a constructor
  overload or a lane member with no production caller reds CI. Rule for every task here: no
  member lands without its production caller (P2a-01's split; P2c-04 and P2a-02 chain their
  overloads; P2b-06 drops the test-only positioner constructor). Checked for P2b-01 to P2b-10:
  every lane, query and reset member has a production caller in the task that adds it.
- The fold hides a real change. The fold drops only the accessor and host hops; the alias in
  P2b-01 is per type and per field, pinned by negative cells; every leftover is named in the PR.
- A lane reset misses a member or runs at the wrong time. Noticed by
  `ParsekFlightPlaybackLaneTests`, `FlightGhostSkipCacheCleanupTests` and the handler-order gate;
  at runtime by stale log-once keys
  (a missing first-time log line) in the loop and map lanes of the P2b set (GS-12, V6M,
  SLM-1, TP-1, S1.7, H44; section 6.4).
- Uninitialized test hosts see a null lane member and throw. Prevented by the lazy getter setting
  every lane member; noticed by the 22 uninitialized-host test files.
- The shared spawn budget is reset twice or never. With one engine it is owned and reset as today;
  the two-engine case is only unit-tested until the rival lane exists (R3W-09 owns that reset,
  OV-46).
- Knot and pin churn conflicts with P12 and other slices. Each PR re-derives the canary from a
  fresh run rather than hand-editing numbers.
- The P2b series is interrupted by another playback PR. The window rule in section 4 and the hot
  file list in section 5 make the order explicit; the supervisor holds other playback PRs.

## 9. Corrections and gaps

1. The design disagrees with itself on `LoopSyncParentIdx`: the 15.11 table says the member leaves
   `IPlaybackTrajectory`; the 15.6 table says the setter leaves. No production code writes through
   the interface (3.1), and two engine readers need the getter. The plan removes the setter only;
   `RivalFlight` returns -1.
2. 15.6 puts `IPlaybackCatalog` "on the floor". Its signatures return `Recording` and
   `RecordingTree` (model layer), so a floor placement adds Core -> Recording backward pairs and
   changes the Core kernel canaries. The plan places it in the Recording module, below every
   consumer. A rival implementation of this port also names `Recording` types, which 15.4's rule
   (Recording types only in the loader) forbids; since rival flights are flattened at load (15.5),
   the plan assumes the rival catalog is empty (no trees, no pending tree, every lookup fails).
   Settled (OV-47, DC-111): the port stays in the `Recording` module returning `Recording` /
   `RecordingTree`; the rival lane uses the empty catalog `RecordingPlaybackCatalog.None` of the
   `Recording` module, added by R3W-09 with its first caller (not by P2a-01, since an uncalled
   member would move the uncalled-method ratchet); there is no `RivalCatalog` type, R3W-05 keeps
   only `RivalStore`, and design 15.6 is corrected (the port is not on the floor).
3. The analysis (section 3.1) calls the tree lookups "five copies". They are not copies: L1 is
   pending-first with a non-null value match; L2 is committed-first with containment; L3 and L4
   also accept a tree-id match; L5 and L6 add a flat-list fallback; L7 and L8 read the list only.
   The port carries named variants.
4. The analysis lists `ParsekFlight.GhostPositioner.cs:323` among store lookups. It is the
   index-keyed watch-protection read over the own list (`GhostPlaybackLogic.cs:705-735`), part of
   the watch query (P2b-08), not the catalog. The analysis also misses the three pending-tree reads
   in the anchor context builders (P1 to P3 in 3.2); the plan routes them through the port.
5. CLAUDE.md says `GhostPlaybackEngine` has zero `Recording` references.
   `GhostPlaybackEngine.SectionPositioning.cs:354-396` reads `RecordingStore`, `PendingTreeSlot` and
   `RecordingTree`. P2a-02 rewrites the sentence. Settled (E.4.14, DC-133): P2a-02 owns the
   correction; the rival-world plan does not edit it.
6. 15.5 keys rival data by (player id, recording id). Static caches keyed by bare recording id
   remain shared across lanes: the tail-lift plan caches (`ParsekFlight.cs:1126-1135`),
   `s_previewRecordingForTailLift`, `SectionAnnotationStore`, `RenderSessionState`. This plan does
   not re-key them; it is safe only while a rival trajectory never reaches them (flattened, no
   tail lift) or recording ids stay globally unique. The rival-lane slice must decide.
   Settled (OV-50): the item stays open here; the owner is R3W-28, whose not-called list names
   these caches, or the rival-world plan states the rule that a rival id cannot collide with an
   own id.
7. In-game tests `RuntimeTests.cs:11787, 22974` build a second engine on `flight.Positioner`, so
   two engines share one lane today. P2b keeps that; they are not a model for the rival lane.
8. `GhostMapPresence` has its own spawn throttle (3.1); P2c's shared budget does not cover map
   presence. P3's slice owns that. Settled (OV-46): the rival map spawn throttle is R3W-18's,
   not P3's (P3 plans no spawn throttle); the existing `GhostMapPresence` throttle stays as it
   is.
9. `FrameContext` has no frame number, so a shared budget cannot reset itself; the owner of a
   shared budget (the future race host) must reset it once per frame before the first engine runs.
   Settled (OV-46): R3W-09 passes one shared `GhostSpawnFrameBudget` to both engines and resets
   it once per frame.
10. 15.11 says P2b "lands alone". The plan cuts it into nine code PRs plus a tooling PR, landed as
    one uninterrupted window with one flight proof at the end. Settled (supervisor, P6
    order): P6 is built in parallel with the prerefactors and merged when green, but not inside
    this window.
11. `il_compare` cannot fold a renamed field or a new parameter (P2b-01 adds the alias; P2b-07
    stays a reviewed non-fold step), and CI does not run `scripts/refactor` tests.
12. Addition A5 brings "one ghost budget object" (ghost count). P2c-04's object is a per-frame spawn
    count; it is named `GhostSpawnFrameBudget` so the two do not collide. Settled (OV-46): two
    budgets, two names: `GhostSpawnFrameBudget` (P2c-04, spawns per frame) and
    `PlaybackGhostBudget` (R3W-14, ghost count); the spawn budget does not live "beside" the
    ghost budget.
13. The analysis counts "about 60 references to 14 members". Measured (3.3): 54 `host.`
    references to 14 members in the positioner, plus 52 sites in `ParsekFlight.cs` and
    `ParsekFlightPlaybackHost.cs`, plus 14 `GhostPosEntry` type references. Four of the 14
    members (`watchMode`, `engine`, `ApplyZoneRenderingImpl`, `Log`) stay on ParsekFlight and the
    positioner reaches the first three through lane queries (P2b-08).
14. `builtLoopUnits` (`ParsekFlightPlaybackHost.cs:45`) is lane-like but stays on the playback
    host; the plan assumes a rival lane has no loop units (15.5).

## 10. Questions for the owner

1. How long should your PC fly to prove the lane change before the next step starts? The lane
   change touches where every ghost is drawn every frame.
   **Answered 2026-11-14 (Q-05):** "there's no point in running every single test for that,
   we can do it later at release; pick the most representative tests to run to recheck the
   ghost system after changes". The proof is the representative set of section 6.4 (18
   lanes, one per way a ghost is placed), flown through the parallel runner with the daily
   tier; the full run happens once, at release time.
