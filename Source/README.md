# Parsek Source Code

## Source Files

```
Parsek/
├── Parsek.csproj        # SDK-style project, targets .NET 4.7.2
├── ParsekFlight.cs      # Main flight controller (recording, preview, timeline playback, UI)
├── FlightRecorder.cs    # Physics-frame recording logic (Harmony-driven)
├── Patches/PhysicsFramePatch.cs # Harmony postfix hook for per-frame sampling
├── RecordingStore.cs    # Static storage for pending/committed recordings (survives scene changes)
└── ParsekScenario.cs   # ScenarioModule — persists committed recordings to save games

Parsek.Tests/
├── WaypointSearchTests.cs          # FindWaypointIndex binary search tests
├── InterpolationTests.cs           # Interpolation and edge case tests
├── TrajectoryPointTests.cs         # TrajectoryPoint struct tests
├── QuaternionSanitizationTests.cs  # Quaternion NaN/Infinity handling tests
├── RecordingStoreTests.cs          # RecordingStore stash/commit/discard tests
└── VesselPersistenceTests.cs       # Merge-decision logic tests
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

124 tests total: 123 pass, 1 skipped (QuaternionSlerp NaN edge case — Unity behavior).

## Testing In-Game

1. Launch KSP, start a flight (career mode for resource tracking)
2. Open the Parsek window from the toolbar button

**Controls:**
- **F9** — Start/Stop recording
- **F10** — Preview playback (plays current recording from now)
- **F11** — Stop preview
- **Toolbar button** — Show/hide the Parsek window

### Timeline Test (core flow)

1. Press F9, fly for 30-60s, press F9 to stop
2. Revert to Launch (Esc > Revert to Launch)
3. Context-aware merge dialog appears with recommended action
4. Wait on the pad — when UT reaches the original timestamps, a green-cyan ghost sphere replays the flight
5. Funds/science/reputation deltas are applied at the correct UT

### Vessel Persistence Tests

**Persist in orbit:**
1. Launch to orbit, F9 record, revert to launch
2. Dialog defaults to "Merge + Keep Vessel" — click it
3. Go to Tracking Station — vessel should appear in orbit

**Auto-recover on pad:**
1. Sit on pad, F9 record briefly, revert
2. Dialog defaults to "Merge + Recover" — funds returned

**Destroyed vessel:**
1. Launch, crash, revert
2. Dialog says "vessel was destroyed" — offers "Merge to Timeline" or "Discard"

### Crew Replacement Tests

**Basic flow:**
1. Record with Jeb → revert → "Merge + Keep Vessel"
2. Check Astronaut Complex: Jeb is Assigned, a new kerbal with matching trait appeared
3. Launch new flight — replacement kerbal is available in crew selection
4. Wait for EndUT → Jeb's vessel spawns, replacement is cleaned up from roster

**Revert cycle:**
1. Record → merge with "Keep Vessel" → revert → record again
2. Replacement pool stays stable — no kerbal duplication or leak

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

- **RecordingStore** — static class holding pending + committed recordings. Static fields survive scene loads within a KSP session. Pending = just-finished recording awaiting merge/discard. Committed = merged to timeline for auto-playback. Also holds vessel persistence fields (snapshot, distance, destruction state) and the `GetRecommendedAction()` merge-decision logic.
- **ParsekScenario** — KSP ScenarioModule that serializes committed recordings to ConfigNode for save/load persistence. Active in FLIGHT, SPACECENTER, TRACKSTATION, and EDITOR scenes. Manages crew reservation (marking snapshot crew as Assigned) and the crew replacement system (hiring/removing replacement kerbals to keep the available pool constant).
- **ParsekFlight** — KSPAddon (Flight only). Handles manual preview playback (relative time), timeline auto-playback (absolute UT), scene change events, context-aware merge dialog, vessel snapshot/respawn/recovery, destruction tracking, and resource delta application.
- **FlightRecorder + PhysicsFramePatch** — recording pipeline. `FlightRecorder` owns sampling state; Harmony postfix on `VesselPrecalculate.CalculatePhysicsStats()` provides per-physics-frame callbacks.
- **TrajectoryPoint** — struct storing per-tick data: position (lat/lon/alt), rotation, velocity, body name, and career resources (funds, science, reputation). All timestamps use absolute UT.

### Vessel Persistence

On scene change (revert), the active vessel is snapshotted via `Vessel.BackupVessel()` → `ProtoVessel` → `ConfigNode`. The merge dialog uses a decision matrix:

| Condition | Default Action |
|-----------|---------------|
| Vessel moved <100m from launch | **Recover** (refund parts) |
| Vessel moved >=100m AND destroyed | **Merge only** (trajectory captured) |
| Vessel moved >=100m AND intact | **Persist** (respawn via ProtoVessel injection) |

Vessel respawn uses `ProtoVessel` injection into `flightState.protoVessels`. Recovery uses `ShipConstruction.RecoverVesselFromFlight`. Snapshots are transient (not saved to disk).

### Crew Replacement System

When a kerbal is reserved for a deferred vessel spawn ("Merge + Keep Vessel"), they're marked as `Assigned` and become unavailable in the VAB/SPH. To prevent the player from running out of crew, the system automatically hires a replacement kerbal with the same trait (Pilot/Engineer/Scientist).

**Lifecycle:**
1. **Reserve** — `ReserveCrewIn()` sets kerbal to Assigned, hires replacement via `roster.GetNewKerbal()`, stores mapping in `crewReplacements[originalName] = replacementName`
2. **Unreserve** — `UnreserveCrewInSnapshot()` sets kerbal back to Available, calls `CleanUpReplacement()`:
   - If replacement is still Available (unused) → removed from roster
   - If replacement is Assigned (player put them on a mission) → kept as a "real" kerbal
3. **Wipe** — `ClearReplacements()` cleans up all replacements and clears the mapping

**Serialization:** The `crewReplacements` dictionary is persisted as a `CREW_REPLACEMENTS` ConfigNode with `ENTRY` sub-nodes (each containing `original` and `replacement` values). Loaded on both initial save load and revert paths.

**Revert handling:** On revert, KSP restores the roster from the launch quicksave. Replacements created before launch exist in the quicksave; those created after don't. The `!ContainsKey` guard in `ReserveCrewIn()` prevents duplicate replacement hiring when re-reserving crew that already have mappings from the quicksave.
