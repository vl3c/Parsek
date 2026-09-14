# VesselSpawner split plan, 2026-09-14

Item 7 of `architecture-opportunities-2026-09-14.md`. A member-level read of
`Source/Parsek/VesselSpawner.cs` (6,897 lines, about 200 declarations) done
before anyone edits it. Analysis only; nothing here has been started. Line
numbers are the 2026-09-14 reading of origin/main.

## 1. Member inventory

Buckets derived mechanically: each member's body was scanned for live-KSP
markers (`FlightGlobals`, `Vessel` instances, `GameEvents`, `KerbalRoster` /
`HighLogic`, `Krakensbane`, `Planetarium`, `TimeJumpManager`) and for
recording-model markers (`Recording`, `RecordingStore`, `RecordingTree`,
`TrackSection`, `RecordingEndpointResolver`, `TerminalOrbitSpawnSafety`).

**(a) Pure over data types: 103 members, about 52 percent.** ConfigNode,
`ResourceManifest` and math only; no store, no live KSP. Examples:
`ExtractResourceManifest` (lines 523-561, reads `RESOURCE` nodes only),
`ComputeInventoryPayloadIdentityHash` (919-942), `DetermineSituation`
(6554-6581). Also the snapshot-validation family (4908-5019), the
canonical-surface-orbit writers (5317-5452), the situation and altitude
predicates (2848-2876, 3438-3487, 6765-6787), the identity-remap helpers
(6617-6746, minus `CollectPartPersistentIds`), and the nested enums and struct
(2928, 4164, 4183, 4196).

**(b) Live-KSP operations: 30 members.** Examples: `TryBackupSnapshot`
(437-465, takes a live `Vessel`, calls `vessel.BackupVessel()`),
`SpawnAtPosition` (1653-1846, `ProtoVessel.Load` plus `GameEvents`),
`RespawnVessel` (1222-1351). Plus the roster readers (3888-3931, 3990-4034),
the body-registry resolvers over `FlightGlobals.Bodies` (4683-4748,
5453-5502), `ApplyPostSpawnStabilization` (6788-6837, rigidbodies), and
`GatherExistingLandedVesselPositions` (343-407).

**(c) Recording-model readers: 50 members.** Examples:
`TryGetPreferredSpawnRotationFrame` (1422-1468, `RecordingEndpointResolver`),
`CollectBodyFixedSectionSamples` (4349-4399, `TrackSection.bodyFixedFrames`),
`TryPassTerminalOrbitSpawnSafety` (5693-5780). Plus the
`BuildValidatedRespawnSnapshot` overloads (4760-4858),
`TryDeriveTerminalOrbitSeedFromTrajectoryTail` (6332-6437), and
`ResolveParentVesselPid` (1847-1891, the one `RecordingStore` read).

**(d) Glue, b plus c: 17 members.** `SpawnOrRecoverIfTooClose` (1897-2256,
360 lines: `FlightGlobals`, `Planetarium`, `ProtoVessel`, `Recording`,
`TerminalOrbitSpawnSafety`), `ResolveSpawnPosition` (2634-2778),
`SnapshotVessel` (4049-4153), `CheckSpawnCollisions` (2369-2521), the
`BackfillMaxDistance*` family (4282-4556),
`TryBuildRecordedTerminalOrbitForSpawn` (6172-6331, `TimeJumpManager` plus
`TrackSection`), `ComputeRecordedTerminalOrbitMeanAnomalyAtUT` (6033-6055,
`TimeJumpManager`), `RespawnValidatedRecording` (5020-5102).

## 2. Caller map

Production files; bucket letters in the last column.

| Caller | Members | Buckets |
| --- | --- | --- |
| `ParsekKSC.cs` | `SpawnAtPosition`, `RespawnVessel`, `GatherExistingLandedVesselPositions`, `ResolveSpawnPosition`, `BuildValidatedRespawnSnapshot`, `IsSurfaceTerminal`, `CorrectUnsafeSnapshotSituation`, 12 more | b, c, d, a |
| `VesselGhoster.cs` | `RespawnValidatedRecording`, `SpawnAtPosition`, `ResolveSpawnPosition`, `TryResolveRecordedTerminalOrbitSpawnState`, `TryGetSnapshotDouble`, 14 more | d, b, c, a |
| `ParsekFlight.cs` and its `.Finalization` / `.TerminalOrbit` partials | `TryBackupSnapshot`, `BackfillMaxDistance*`, `ClassifyMaxDistanceBackfillRoute`, `SpawnOrRecoverIfTooClose`, `CollectPartPersistentIds`, `Extract*Manifest`, `TryReadRootPartFlightId` | b, d, a |
| `FlightRecorder.cs`, `BackgroundRecorder.cs`, `ChainSegmentManager.cs`, `GhostPlaybackLogic.cs`, `IncompleteBallisticSceneExitFinalizer.cs` | `TryBackupSnapshot`, `SnapshotVessel`, `Extract*Manifest`, `ExtractLiveResourceManifest`, `HumanizeSituation`, `TryResolveBiome` | b, d, a |
| `RewindInvoker.cs` | `TryBackupSnapshot` (line 377) | b |
| `GameActions/GameAction.cs` | `NormalizeLoadedInventoryPayloadItems` only (line 2098) | a |
| `Logistics/*` (`RouteCodec`, `LiveInventoryPickupWriter`, `LiveDeliveryWriters`, `RouteAnalysisEngine`, `ResourceTransferability`) | `NormalizeLoadedInventoryPayloadItems`, `ComputeInventoryPayloadIdentityHash`, `BuildInventoryPayloadItem`, `ComputeInventoryPayloadKindKey`, `ExtractResourceManifest` | a only |
| `RouteProofCapture/Codec/Metadata.cs`, `UI/LogisticsHoldPresentation.cs`, `MissionRouteStructureList.cs`, `UI/StructureListWindowUI.cs` | manifest and payload helpers, `TryResolveBiome` | a, one b |
| `GhostMapPresence.cs`, `RecordingTreeRecordCodec.cs`, `ParsekPlaybackPolicy.cs`, `MergeDialog.Commit.cs` | `SpawnOrRecoverIfTooClose`, `HasRecordedTerminalOrbit`, `TryAdoptExistingSourceVesselForSpawn` | d, c |
| `Rendering/TerrainCacheBuckets.cs`, `TerrainCorrector.cs`, `RecordingOptimizer.cs`, `Analyzer/Rules/Inv11EmptyTrackSection.cs`, `Patches/PartMassivePartCheckSeederPatch.cs`, `CrewReservationManager.*`, `KerbalsModule.cs`, `OrbitReseed.cs` | `ClampAltitudeForLanded`, `TryResolveBiome`, `BackfillMaxDistance`, `SeedSinglePackedPart`, crew-roster helpers | a, b, d |

The decisive fact: GameActions and every Logistics caller touch bucket (a)
only. No ledger file calls a live or recording-model member. The feared
upward edge from GameActions does not exist and never needs an interface.

## 3. Proposed split

1. **`VesselSnapshotOps`** (kernel, stays in Core): bucket (a). ConfigNode
   snapshot surgery, manifest and inventory-payload extraction and hashing,
   situation, altitude and identity-remap pure predicates, the three nested
   enums. About 52 percent of the members and the entire GameActions,
   Logistics, RouteProof and UI surface. Keeping it kernel-resident is what
   removes the kernel-to-Recording edge.
2. **`RecordingSpawnPlanner`** (Recording module): buckets (c) and (d).
   Everything that reads `Recording`, `RecordingTree`, `TrackSection`,
   `RecordingEndpointResolver` or `TerminalOrbitSpawnSafety` to derive a spawn
   plan, plus the glue that also needs live KSP. This is where the Recording
   references belong; the kernel then reaches up into nothing.
3. **`LiveVesselOps`** (Recording module): bucket (b). `ProtoVessel.Load`,
   `GameEvents`, roster, body registry, post-spawn stabilization.

Live half to Recording, not Controllers. The callers force it:
`RespawnValidatedRecording` (5020) and `TryBuildRecordedTerminalOrbitForSpawn`
(6172) cannot be separated from bucket (c) without inventing a plan DTO, and
`Analyzer/Rules/Inv11EmptyTrackSection.cs` (an analyzer, not a controller)
calls `BackfillMaxDistance`. Landing them in Controllers would give
Analyzer-to-Controllers; Recording is already below both. GameActions is
unaffected either way because it only calls (a); say so explicitly in the
design doc so a later reader does not "fix" a non-edge. The one live member the
Rewind path uses, `TryBackupSnapshot` (`RewindInvoker.cs:377`), is
Rewind-to-live, an edge Rewind already has.

## 4. Order of moves

Five PR-sized steps, each leaving the build green.

1. **Extract `VesselSnapshotOps`, inventory / manifest / payload subset only**
   (lines 518-1131 and 6617-6764). Cheapest: zero live or recording
   dependencies. Covered by `ResourceManifestTests`, `InventoryManifestTests`,
   `CrewManifestTests`, `Logistics/InventoryPayloadKindKeyTests`,
   `RouteProofCaptureTests`, `SpawnIdentityRegenerationTests`. Lanes:
   `H38/H39/H40-logistics-isolated*`, `H41-logistics-grapple-isolated`,
   `H57-route-start-docked-origin-landed`.
2. **Move the rest of bucket (a)** into `VesselSnapshotOps` (situation,
   altitude, validation, canonical orbit: 3438-3522, 4908-5019, 5317-5452,
   6532-6787). Covered by `VesselSpawnerExtractedTests`,
   `SpawnSafetyNetTests`, `SpawnWalkbackFallbackTests`,
   `TerrainCorrectorTests`, `RuntimePolicyTests`,
   `RigidbodyMassPackedSpawnTests`. Lanes: `H32-snapshot-baseline`,
   `H16-corpus-spawn-health`.
3. **Extract `LiveVesselOps`** (bucket b: 263-490, 3239-4048, 4683-4748,
   5453-5542, 6788-6898). Covered by `SpawnAuditFollowupTests`,
   `PartEventTests`, `Bug170Tests` / `Bug609Tests` / `Bug687Tests`,
   `RescueCompletionGuardTests`. Lanes: `H31-crew-reservation`,
   `H20-eva-spawn-position`, `EVA-1-pad-flag`.
4. **Extract `RecordingSpawnPlanner`** for bucket (c) (1422-1576, 4349-4556,
   4760-4858, 5634-6171, 6332-6493). Covered by
   `TerminalOrbitSpawnSafetyGeometryTests`, `SpawnTerminalOrbitFromTailTests`,
   `SpawnRotationContractTests`, `UndockBgChildEmptySectionTests`,
   `ResolveParentVesselPidTests`, `ChainTests`, `SceneExitInterceptorTests`.
5. **Move bucket (d)**, the big orchestrators (1897-2256, 2369-2778,
   4049-4153, 5020-5102, 6172-6331), into `RecordingSpawnPlanner`, then delete
   the `VesselSpawner` shell (or leave a thin forwarder for one release).
   Covered by `SpawnSafetyNetTests`, `SpawnCollisionBlockLimitTests`,
   `DuplicateBlockerRecoveryTests`, `DeferredSpawnTests`, `KscSpawnTests`,
   `VesselGhosterTests`, `TimeJumpManagerTests`,
   `Bug618ReFlyMergeParentChainTipTests`. Lanes: `H17-flight-integration`,
   `H16-corpus-spawn-health`, `GS-1` / `GS-4` / `GS-7`,
   `CL-1` / `CL-2-pod-impact*`, `L3-career-science-recover`,
   `L6-career-same-name-recover`, `CI-2-refly-claim-tip-pid`.

## 5. Risks

1. **`persistentId` reuse (the CLAUDE.md trap).**
   `MaterializedSourceVesselExists(Recording)` (227-262) is guid-gated through
   `VesselLaunchIdentity.LiveVesselIsRecordedLaunch` (254), while
   `MaterializedSourceVesselExists(uint)` (210-226) and
   `ShouldAllowExistingSourceDuplicateForCurrentFlight` (177-209,
   `FlightGlobals.ActiveVessel.persistentId` at 183) are pid-only by design.
   The pid overload is bucket (a) and the guid overload bucket (c): steps 1
   and 2 would split the pair across types. Keep both in the Recording half,
   or a caller silently drops the guid gate and a relaunch of the same craft
   reads as "already materialized". The test-only override seams (55-70) are
   shared by `TimeJumpManagerTests` and `Bug618...Tests`; whichever type keeps
   them must keep all three.
2. **Floating-origin and grid tolerance in `ResolveSpawnPosition` and
   `TryFindSurfaceAltitudeViaRaycast`** (2634-2778, 2779-2847). Both mix
   `FlightGlobals` world positions with recording-derived lat/lon and
   constants tuned against them (`UndergroundSafetyFloorMeters` 2721,
   `WalkbackSurfaceClearanceMeters` 2737, `LandedGhostClearanceMeters` 2829).
   Moving the constants to `VesselSnapshotOps` while the callers move later
   leaves two visible copies; `TerrainCorrectorTests` and
   `SpawnSafetyNetTests` read them by name, so a rename passes compile and
   changes the safety floor silently. Move constants with their consumer, in
   step 5.
3. **RELATIVE-frame point misreading in the `bodyFixedFrames` family**
   (`CollectBodyFixedSectionSamples` 4349-4399,
   `TryComputeMaxDistanceFromBodyFixedSurfaces` 4418-4476,
   `ClassifyMaxDistanceBackfillRoute` 4235-4281). These resolve
   `TrackSection.referenceFrame` before reading latitude, longitude and
   altitude; a refactor that extracts only the "pure" inner loop into
   `VesselSnapshotOps` strands the frame dispatch in the caller and reproduces
   the deep-inside-the-planet bug CLAUDE.md names.
   `UndockBgChildEmptySectionTests` and
   `Analyzer/Rules/Inv11EmptyTrackSection.cs` are the only guards; keep the
   whole family together in `RecordingSpawnPlanner` (step 4).

Secondary: `SnapshotVessel` (4049-4153) and `ApplyPostSpawnStabilization`
(6788-6837) sit on the Krakensbane / rigidbody seam; both are (b) or (d) and
must not be split mid-body.
