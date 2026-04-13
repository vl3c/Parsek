# Parsek Source Code

## Source Files

```
Parsek/
├── Parsek.csproj              # SDK-style project, targets .NET 4.7.2
├── ParsekFlight.cs            # Main flight controller (timeline playback, ghost lifecycle, input)
├── FlightRecorder.cs          # Physics-frame recording logic (Harmony-driven, part event polling)
├── ParsekHarmony.cs           # Harmony patcher entry point (KSPAddon.Startup.Instantly)
├── Patches/
│   ├── PhysicsFramePatch.cs   # Harmony postfix hook for per-frame sampling
│   ├── TechResearchPatch.cs   # Action blocking: prevent re-researching committed tech
│   ├── FacilityUpgradePatch.cs # Action blocking: prevent re-upgrading committed facilities
│   └── ScienceSubjectPatch.cs # Science subject tracking patch
├── ParsekUI.cs                # UI windows (main window, recordings manager) and map view markers
├── ParsekSettings.cs          # GameParameters settings (auto-record, thresholds)
├── RecordingStore.cs          # Static storage for pending/committed recordings (survives scene changes)
├── ParsekScenario.cs          # ScenarioModule - persists recordings to save games, crew reservation
├── ParsekLog.cs               # Shared logging utilities
├── TrajectoryPoint.cs         # Position/rotation/resource data struct
├── TrajectoryMath.cs          # Pure static math (sampling, interpolation, orbit search, orbital-frame rotation)
├── OrbitSegment.cs            # Keplerian orbit parameters + orbital-frame rotation for on-rails recording
├── PartEvent.cs               # Part event enum + struct (28 event types)
├── GhostVisualBuilder.cs      # Ghost mesh building from vessel snapshots (engine/RCS FX, fairings, re-entry)
├── VesselSpawner.cs           # Vessel spawn/recover/snapshot utilities
├── MergeDialog.cs             # Post-revert merge dialog
├── RecordingPaths.cs          # Save-scoped path resolution for external recording files
├── ParsekToolbarRegistration.cs # ToolbarControl registration
├── CommittedActionDialog.cs   # Popup dialog for blocked actions
├── GameStateEvent.cs          # Career event struct (tech, parts, facilities, contracts, crew, resources)
├── GameStateRecorder.cs       # Subscribes to KSP career GameEvents, records into GameStateStore
├── GameStateStore.cs          # Static persistent event log
├── GameStateBaseline.cs       # Full game state snapshot at commit points
├── Milestone.cs               # Groups game state events into committed timeline units
├── MilestoneStore.cs          # Milestone collection management
├── ResourceBudget.cs          # On-the-fly resource budget computation from recordings + milestones
├── RecordingTree.cs           # Rooted DAG of recordings for multi-vessel missions
├── BranchPoint.cs             # Links parent/child recordings at split/merge events
├── BackgroundRecorder.cs      # Dual-mode recording for non-active tree vessels
├── TerminalState.cs           # How a recording ended (8 end conditions)
└── SurfacePosition.cs         # Background recording data for landed/splashed vessels

Parsek.Tests/
├── Generators/
│   ├── RecordingBuilder.cs          # Fluent RECORDING ConfigNode builder
│   ├── VesselSnapshotBuilder.cs     # Minimal VESSEL ConfigNode builder
│   └── ScenarioWriter.cs           # SCENARIO assembly + .sfs injection
├── WaypointSearchTests.cs           # FindWaypointIndex binary search tests
├── TrajectoryPointTests.cs          # TrajectoryPoint struct tests
├── QuaternionSanitizationTests.cs   # Quaternion NaN/Infinity handling tests
├── AdaptiveSamplingTests.cs         # Adaptive threshold sampling tests
├── OrbitSegmentTests.cs             # Orbit segment + orbital-frame rotation tests
├── RecordingStoreTests.cs           # RecordingStore stash/commit/discard tests
├── VesselPersistenceTests.cs        # Merge-decision logic tests
├── PartEventTests.cs                # Part event serialization, subtree, transition tests
├── ChainTests.cs                    # Recording chain merge/handoff tests
├── DockUndockChainTests.cs          # Docking/undocking chain tests
├── DiagnosticLoggingTests.cs        # Regression tests for playback logging
├── RuntimePolicyTests.cs            # Runtime decision logic tests
├── RecordingsManagerTests.cs        # Recordings Manager UI logic tests
├── SyntheticRecordingTests.cs       # Synthetic recording generation + save file injection
├── AtmosphereSplitTests.cs          # Atmosphere boundary split tests
├── BackgroundRecorderTests.cs       # Background recorder tests
├── BackwardCompatTests.cs           # Backward compatibility tests
├── CameraFollowTests.cs            # Camera follow ghost tests
├── CommittedActionTests.cs          # Committed action blocking tests
├── ComputeStatsTests.cs            # Recording statistics computation tests
├── FxDiagnosticsTests.cs           # FX diagnostic logging tests
├── GameStateEventTests.cs          # Game state event serialization tests
├── LiveKspLogValidationTests.cs    # KSP.log contract validation
├── MergeDialogTests.cs             # Merge dialog logic tests
├── MergeEventDetectionTests.cs     # Tree merge event detection tests
├── MilestoneTests.cs               # Milestone creation/replay tests
├── ParsekLogTests.cs               # Logging utility tests
├── ParsekKspLogParserTests.cs      # KSP log parser tests
├── ParsekLogContractCheckerTests.cs # Log contract checker tests
├── RecordingTreeTests.cs           # Recording tree serialization/query tests
├── ReentryIntensityTests.cs        # Re-entry FX intensity formula tests
├── ResourceBudgetTests.cs          # Resource budget computation tests
├── RewindTests.cs                  # Rewind logic tests
├── RewindLoggingTests.cs           # Rewind diagnostic logging tests
├── SplitEventDetectionTests.cs     # Tree split event detection tests
├── TerminalEventTests.cs           # Terminal event detection tests
├── TreeCommitTests.cs              # Tree commit logic tests
├── TreeLogVerificationTests.cs     # Tree logging verification tests
└── VesselSwitchTreeTests.cs        # Vessel switch tree decision tests
```

## Building

### Prerequisites

1. **KSP 1.12.x** installed
2. **.NET SDK** with .NET Framework 4.7.2 targeting pack
3. (Optional) Set `KSPDIR` environment variable to your KSP installation path

### Build

```bash
cd Source/Parsek
dotnet build              # Debug build (default)
dotnet build -c Release   # Release build
```

The DLL is automatically copied to `KSP/GameData/Parsek/Plugins/` on build.

**Note:** If KSP is running, the post-build copy will fail because the DLL is locked. Close KSP, rebuild, then relaunch.

### Set KSP Path

The default path is `../../Kerbal Space Program` (local instance in project root). To override:

```cmd
setx KSPDIR "C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program"
```

Or edit `Parsek.csproj`:
```xml
<KSPDir Condition="'$(KSPDir)' == ''">YOUR_KSP_PATH_HERE</KSPDir>
```

### Unit Tests

```bash
cd Source/Parsek.Tests
dotnet test
```

1342 tests total.

### Live KSP Log Validation

After running a manual in-game scenario, validate the latest Parsek session in `KSP.log`:

```bash
pwsh -File scripts/validate-ksp-log.ps1
```

The script exits non-zero if required log contracts fail.

## Testing In-Game

1. Launch KSP, start a flight (career mode for resource tracking)
2. Open the Parsek window from the toolbar button

**Controls:**
- **F9** - Start/Stop recording
- **F10** - Preview playback (plays current recording from now)
- **F11** - Stop preview
- **Toolbar button** - Show/hide the Parsek window

### Timeline Test (core flow)

1. Press F9, fly for 30-60s, press F9 to stop
2. Revert to Launch (Esc > Revert to Launch)
3. Context-aware merge dialog appears with recommended action
4. Wait on the pad - when UT reaches the original timestamps, an opaque ghost vessel replays the flight
5. Funds/science/reputation deltas are applied at the correct UT

### Vessel Persistence Tests

**Persist in orbit:**
1. Launch to orbit, F9 record, revert to launch
2. Dialog defaults to "Merge to Timeline" - click it
3. Go to Tracking Station - vessel should appear in orbit

**Destroyed vessel:**
1. Launch, crash, revert
2. Dialog says "vessel was destroyed" - offers "Merge to Timeline" or "Discard"

### Crew Replacement Tests

**Basic flow:**
1. Record with Jeb → revert → "Merge to Timeline"
2. Check Astronaut Complex: Jeb is Assigned, a new kerbal with matching trait appeared
3. Launch new flight - replacement kerbal is available in crew selection
4. Wait for EndUT → Jeb's vessel spawns, replacement is cleaned up from roster

**Revert cycle:**
1. Record → merge with "Keep Vessel" → revert → record again
2. Replacement pool stays stable - no kerbal duplication or leak

**Wipe cleanup:**
1. Record + merge several times with "Keep Vessel"
2. Click "Wipe Recordings" in Parsek UI
3. All reserved kerbals return to Available, all replacements removed

### Save/Load Persistence

1. Record, merge to timeline
2. Quicksave (F5), quit to menu, reload save
3. Committed recordings should survive and replay

## Troubleshooting

**"Assembly-CSharp not found" or similar errors:**
- Verify KSPDIR points to correct KSP installation
- Check that `KSP_x64_Data/Managed/` folder exists with DLLs

**No window appears in game:**
- Check KSP.log for errors mentioning "Parsek"
- Verify DLL is in `GameData/Parsek/Plugins/`

**Ghost doesn't appear on timeline:**
- Check KSP.log for `[Parsek]` and `[Parsek Scenario]` entries
- Verify recording was merged (not discarded)
- Current UT must be within the recording's time range

## Architecture

- **RecordingStore** - static class holding pending + committed recordings. Static fields survive scene loads within a KSP session. Pending = just-finished recording awaiting merge/discard. Committed = merged to timeline for auto-playback. Also holds vessel persistence fields (snapshot, distance, destruction state) and the `GetRecommendedAction()` merge-decision logic.
- **ParsekScenario** - KSP ScenarioModule that serializes committed recordings to ConfigNode for save/load persistence. Active in FLIGHT, SPACECENTER, TRACKSTATION, and EDITOR scenes. Manages crew reservation (marking snapshot crew as Assigned) and the crew replacement system (hiring/removing replacement kerbals to keep the available pool constant).
- **ParsekFlight** - KSPAddon (Flight only). Handles timeline auto-playback (absolute UT), ghost lifecycle, scene change events, context-aware merge dialog, vessel snapshot/respawn/recovery, destruction tracking, resource delta application, and manual preview playback.
- **FlightRecorder + PhysicsFramePatch** - recording pipeline. `FlightRecorder` owns sampling state and part event polling (engines, parachutes, deployables, lights, gear, cargo bays, fairings, RCS, inventory placement/removal); Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats()` provides per-physics-frame callbacks. Dock/undock boundaries are committed via chain logic in `ParsekFlight`.
- **ParsekUI** - UI window drawing (main Parsek window + Recordings Manager window). Recordings Manager shows a sortable table of all committed recordings with per-recording loop toggle, status indicator, and delete button.
- **GhostVisualBuilder** - builds ghost vessel meshes from vessel snapshots using prefab parts. Handles engine particle FX (cloned MODEL_MULTI_PARTICLE), RCS FX, fairing cone mesh generation, and deployable animation state sampling.
- **TrajectoryPoint** - struct storing per-tick data: position (lat/lon/alt), rotation, velocity, body name, and career resources (funds, science, reputation). All timestamps use absolute UT.
- **PartEvent** - struct + enum covering 28 event types: decoupled, destroyed, parachute deploy/cut/destroyed, shroud jettison, engine ignition/shutdown/throttle, deployable extend/retract, light on/off/blink, gear deploy/retract, cargo bay open/close, fairing jettison, RCS activate/stop/throttle, dock/undock, inventory place/remove.

### External Recording Files (v5)

Bulk data (trajectory points, orbit segments, part events, snapshots) is stored in external sidecar files under `saves/<save>/Parsek/Recordings/`, keeping the `.sfs` save file lightweight. Authoritative file types: `.prec` (trajectory), `_vessel.craft` / `_ghost.craft` (vessel snapshots). A default-on diagnostics flag also writes readable `.prec.txt`, `_vessel.craft.txt`, and `_ghost.craft.txt` mirror files for debugging/comparison without changing the authoritative load path. Safe-write via `.tmp` + rename, and readable mirrors are reconciled separately so mirror failures do not roll back the real sidecars.

### Vessel Persistence

On scene change (revert), the active vessel is snapshotted via `Vessel.BackupVessel()` → `ProtoVessel` → `ConfigNode`. The merge dialog uses a decision matrix:

| Condition | Default Action |
|-----------|---------------|
| Vessel moved <100m from launch | **Recover** (refund parts) |
| Vessel moved >=100m AND destroyed | **Merge only** (trajectory captured) |
| Vessel moved >=100m AND intact | **Persist** (respawn via ProtoVessel injection) |

Vessel respawn uses `ProtoVessel` injection into `flightState.protoVessels`. Recovery uses `ShipConstruction.RecoverVesselFromFlight`. Vessel snapshots are persisted to external `.craft` sidecar files (v5 format).

### Crew Replacement System

When a kerbal is reserved for a deferred vessel spawn ("Merge to Timeline"), they're marked as `Assigned` and become unavailable in the VAB/SPH. To prevent the player from running out of crew, the system automatically hires a replacement kerbal with the same trait (Pilot/Engineer/Scientist).

**Lifecycle:**
1. **Reserve** - `ReserveCrewIn()` sets kerbal to Assigned, hires replacement via `roster.GetNewKerbal()`, stores mapping in `crewReplacements[originalName] = replacementName`
2. **Unreserve** - `UnreserveCrewInSnapshot()` sets kerbal back to Available, calls `CleanUpReplacement()`:
   - If replacement is still Available (unused) → removed from roster
   - If replacement is Assigned (player put them on a mission) → kept as a "real" kerbal
3. **Wipe** - `ClearReplacements()` cleans up all replacements and clears the mapping

**Serialization:** The `crewReplacements` dictionary is persisted as a `CREW_REPLACEMENTS` ConfigNode with `ENTRY` sub-nodes (each containing `original` and `replacement` values). Loaded on both initial save load and revert paths.

**Revert handling:** On revert, KSP restores the roster from the launch quicksave. Replacements created before launch exist in the quicksave; those created after don't. The `!ContainsKey` guard in `ReserveCrewIn()` prevents duplicate replacement hiring when re-reserving crew that already have mappings from the quicksave.
