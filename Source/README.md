# Parsek Source Code

## Source Files

```
Parsek/
├── Parsek.csproj        # SDK-style project, targets .NET 4.7.2
├── ParsekSpike.cs       # Main spike — recording, manual preview, timeline auto-playback, UI
├── RecordingStore.cs    # Static storage for pending/committed recordings (survives scene changes)
└── ParsekScenario.cs   # ScenarioModule — persists committed recordings to save games

Parsek.Tests/
├── WaypointSearchTests.cs          # FindWaypointIndex binary search tests
├── InterpolationTests.cs           # Interpolation and edge case tests
├── TrajectoryPointTests.cs         # TrajectoryPoint struct tests
└── QuaternionSanitizationTests.cs  # Quaternion NaN/Infinity handling tests
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

33 tests pass, 1 skipped (QuaternionSlerp NaN edge case — Unity behavior).

## Testing In-Game

1. Launch KSP, start a flight (career mode for resource tracking)
2. A window titled "Parsek Spike" appears

**Controls:**
- **F9** — Start/Stop recording
- **F10** — Preview playback (plays current recording from now)
- **F11** — Stop preview
- **Alt+P** — Toggle UI window

### Timeline Test (core flow)

1. Press F9, fly for 30-60s, press F9 to stop
2. Revert to Launch (Esc > Revert to Launch)
3. "Merge to Timeline?" dialog appears — click **Merge to Timeline**
4. Wait on the pad — when UT reaches the original timestamps, a green-cyan ghost sphere replays the flight
5. Funds/science/reputation deltas are applied at the correct UT

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
- Check KSP.log for `[Parsek Spike]` and `[Parsek Scenario]` entries
- Verify recording was merged (not discarded)
- Current UT must be within the recording's time range

## Architecture

- **RecordingStore** — static class holding pending + committed recordings. Static fields survive scene loads within a KSP session. Pending = just-finished recording awaiting merge/discard. Committed = merged to timeline for auto-playback.
- **ParsekScenario** — KSP ScenarioModule that serializes committed recordings to ConfigNode for save/load persistence. Active in FLIGHT, SPACECENTER, and TRACKSTATION scenes.
- **ParsekSpike** — KSPAddon (Flight only). Handles recording via InvokeRepeating, manual preview playback (relative time), timeline auto-playback (absolute UT), scene change events, merge dialog, and resource delta application.
- **TrajectoryPoint** — struct storing per-tick data: position (lat/lon/alt), rotation, velocity, body name, and career resources (funds, science, reputation). All timestamps use absolute UT.
