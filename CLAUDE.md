# Claude Development Notes

## Project Configuration

### KSP Installation
- **Local KSP instance** at `Kerbal Space Program/` in project root
- This is a **clean copy** for testing, NOT the Steam installation
- Steam installation stays intact for normal gameplay
- Safe to test and modify without affecting Steam

### KSP Instance Setup
**Minimal mods installed (testing environment):**
- Squad / SquadExpansion (stock game files)
- ModuleManager 4.2.3 (essential for mods)
- Harmony (essential for runtime patching)
- ClickThroughBlocker (prevents UI click-through)
- ToolbarControl (stock + Blizzy toolbar integration)
- **Parsek** (our mod being developed)

All other mods removed for clean testing environment.

### Build Configuration
- KSP assemblies referenced from: `Kerbal Space Program/KSP_x64_Data/Managed/`
- Build output copied to: `Kerbal Space Program/GameData/Parsek/Plugins/`
- Auto-deployed on every build via MSBuild post-build target

### Project Structure
```
Parsek/
├── Kerbal Space Program/    # Local KSP instance for testing
│   ├── GameData/
│   │   ├── Squad/           # Stock parts
│   │   ├── 000_Harmony/     # Essential dependency
│   │   ├── ModuleManager.*  # Essential dependency
│   │   └── Parsek/          # Our mod (auto-deployed)
│   │       └── Plugins/
│   │           ├── Parsek.dll
│   │           └── Parsek.pdb
│   ├── KSP_x64_Data/
│   │   └── Managed/         # Assembly references
│   └── KSP.log              # Debug output here
├── Source/                   # Mod source code
│   ├── Parsek/
│   │   ├── Parsek.csproj    # SDK-style project
│   │   ├── ParsekFlight.cs  # Main flight-scene controller (playback, timeline, input)
│   │   ├── FlightRecorder.cs # Recording state + sampling (called by Harmony patch)
│   │   ├── ParsekHarmony.cs  # Harmony patcher entry point (KSPAddon.Startup.Instantly)
│   │   ├── Patches/
│   │   │   └── PhysicsFramePatch.cs # Harmony postfix on VesselPrecalculate
│   │   ├── ParsekUI.cs      # UI window and map view markers
│   │   ├── ParsekLog.cs     # Shared logging utilities
│   │   ├── ParsekScenario.cs # ScenarioModule for save/load, crew reservation & replacement
│   │   ├── ParsekToolbarRegistration.cs # ToolbarControl registration
│   │   ├── RecordingStore.cs # Static storage surviving scene changes
│   │   ├── TrajectoryPoint.cs # Position/rotation/resource data struct
│   │   ├── OrbitSegment.cs   # Keplerian orbit parameters for on-rails recording
│   │   ├── TrajectoryMath.cs # Pure static math (sampling, interpolation, orbit search)
│   │   ├── VesselSpawner.cs  # Vessel spawn/recover/snapshot utilities
│   │   └── MergeDialog.cs    # Post-revert merge dialog
│   └── Parsek.Tests/         # Unit tests (xUnit)
├── docs/                     # Documentation
├── mods/                     # Reference mods (git-ignored)
├── CLAUDE.md                 # This file
├── build.bat                 # Build script (alternative to dotnet)
└── .gitignore               # Git ignore rules
```

## Development Workflow

### Build
```bash
cd Source/Parsek
dotnet build              # Debug build (default)
dotnet build -c Release   # Release build
```

Builds automatically copy to `Kerbal Space Program/GameData/Parsek/Plugins/`

### Unit Tests
```bash
cd Source/Parsek.Tests
dotnet test
```

### In-Game Test
```bash
"Kerbal Space Program/KSP_x64.exe"
```

**Recording + Timeline (core flow):**
1. Launch KSP, start any flight (career mode for resource tracking)
2. Press **F9** to start recording
3. Fly around for 30-60 seconds
4. Press **F9** to stop recording
5. Revert to Launch (Esc → Revert to Launch)
6. A context-aware merge dialog appears with recommended action:
   - **Vessel barely moved (<100m):** "Merge + Recover" (default), "Merge + Keep Vessel", "Discard"
   - **Vessel destroyed:** "Merge to Timeline" (default), "Discard"
   - **Vessel intact, moved far:** "Merge + Keep Vessel" (default), "Merge + Recover", "Discard"
7. Wait on the pad until UT reaches original recording timestamps
8. Green-cyan ghost sphere appears and replays previous flight
9. Funds/science/reputation deltas are applied at the correct UT

**Vessel persistence test:**
1. Launch to orbit → F9 record → revert → "Merge + Keep Vessel"
2. Go to Tracking Station → vessel appears in orbit
3. Or choose "Merge + Recover" → funds credited, no vessel in orbit

**Crew replacement test:**
1. Record with Jeb → revert → "Merge + Keep Vessel"
2. Check Astronaut Complex: Jeb is Assigned, a new kerbal with same trait appeared
3. Launch new flight — replacement kerbal is available in crew selection
4. Wait for EndUT → Jeb's vessel spawns, replacement is removed from roster
5. Repeat: record again → replacement pool stays stable (no kerbal leak)

**Wipe cleanup test:**
1. Record + merge several times with "Keep Vessel"
2. Click "Wipe Recordings" in Parsek UI
3. All reserved kerbals return to Available, all replacements removed

**Orbital recording test (hybrid orbit segments):**
1. Launch → establish orbit → F9 record → time warp 50x for one orbit → drop to 1x → F9 stop → revert → merge
2. Verify ghost follows orbital path during playback (uses analytical orbit, not sampled points)
3. Record Mun encounter with time warp → verify SOI transition in ghost playback
4. Record with multiple time warp on/off cycles → verify smooth transitions at boundary points

**Manual preview (no revert needed):**
1. Record as above, press **F10** to preview playback immediately
2. Press **F11** to stop preview

**Controls:**
- **F9** — Start/Stop recording
- **F10** — Preview playback (current recording, relative time)
- **F11** — Stop preview
- **Toolbar button** (top-right) — Toggle Parsek UI window

### Automatic Behaviors

These happen silently to keep gameplay smooth. All are logged to `KSP.log` with `[Parsek]` or `[Parsek Scenario]` prefixes.

**Auto-recording:**
- Recording starts automatically when a vessel leaves PRELAUNCH (pad/runway liftoff)
- Recording starts automatically when a kerbal goes EVA from a vessel on the pad/runway
- EVA auto-record is deferred by one frame via `pendingAutoRecord` flag (vessel switch delay)

**Recording safeguards:**
- Recording is blocked while the game is paused
- Recording auto-stops if the active vessel changes (docking, pressing `[`/`]`)
- Recordings shorter than 2 sample points are silently dropped on revert
- Adaptive sampling: Harmony postfix fires every physics frame but only records when velocity direction changes >2deg, speed changes >5%, or 3s max interval elapses
- When vessel goes on rails (time warp), trajectory sampling stops and an OrbitSegment captures the Keplerian orbit parameters instead
- When vessel goes off rails, the orbit segment is finalized and sampling resumes with boundary points at each transition
- SOI changes during on-rails recording close the current orbit segment and open a new one for the new body

**Ghost playback:**
- Time warp is stopped once when UT first enters a recording's range (only if the recording has an unspawned vessel). Time warp during active ghost playback is allowed.
- SOI changes during recording are handled — each trajectory point references its own celestial body
- During orbit segments, ghost position is computed analytically from Keplerian orbit parameters (no interpolation needed)

**Vessel spawning:**
- If a vessel would spawn within 200m of any other vessel, it is offset to 250m away to prevent physics collisions
- Duplicate spawns are prevented by tracking each spawned vessel's `persistentId`
- Dead or missing crew are stripped from vessel snapshots before spawning

**Resource deltas:**
- Funds, science, and reputation deltas are clamped so they never go below zero
- `LastAppliedResourceIndex` is persisted in saves so quickload doesn't double-apply deltas

**Scene transitions:**
- If a pending recording exists when leaving Flight (e.g. Abort Mission → Space Center), it is auto-committed to the timeline without vessel persistence
- If UT passes a recording's EndUT while outside Flight (Space Center, Tracking Station), reserved crew are auto-unreserved to prevent them being stuck as Assigned forever

### Debug
- Check `Kerbal Space Program/KSP.log` for errors
- Look for `[Parsek]` and `[Parsek Scenario]` log entries
- Crew replacement actions logged as `[Parsek Scenario] Hired replacement ...` / `Removed replacement ...`
- Alt+F12 opens Unity debug console in-game

## Git Configuration

**Ignored folders:**
- `Kerbal Space Program/` - Local KSP instance (too large)
- `mods/` - Reference mods (submodules/clones)
- `.claude/` - Local Claude settings
- `bin/`, `obj/` - Build artifacts

**Tracked:**
- `Source/` - Mod source code
- `docs/` - Documentation
- `build.bat` - Build script
- `.gitignore` - Git ignore rules

## Why This Approach

- **Isolated testing** - No conflicts with Steam installation
- **Clean environment** - Minimal mods = easier debugging
- **Fast iteration** - Auto-deployment on build
- **Reproducible** - Can share exact test environment setup
