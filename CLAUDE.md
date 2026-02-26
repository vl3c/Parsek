## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

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
│   │   │   ├── PhysicsFramePatch.cs # Harmony postfix on VesselPrecalculate
│   │   │   ├── TechResearchPatch.cs # Blocks re-researching committed tech
│   │   │   └── FacilityUpgradePatch.cs # Blocks re-upgrading committed facilities
│   │   ├── ParsekUI.cs      # UI window and map view markers
│   │   ├── ParsekLog.cs     # Shared logging utilities
│   │   ├── ParsekScenario.cs # ScenarioModule for save/load, crew reservation & replacement
│   │   ├── ParsekToolbarRegistration.cs # ToolbarControl registration
│   │   ├── RecordingStore.cs # Static storage surviving scene changes
│   │   ├── TrajectoryPoint.cs # Position/rotation/resource data struct
│   │   ├── OrbitSegment.cs   # Keplerian orbit parameters for on-rails recording
│   │   ├── TrajectoryMath.cs # Pure static math (sampling, interpolation, orbit search)
│   │   ├── VesselSpawner.cs  # Vessel spawn/recover/snapshot utilities
│   │   ├── MergeDialog.cs    # Post-revert merge dialog
│   │   ├── PartEvent.cs      # Part event enum + struct (decoupled, destroyed, parachute)
│   │   ├── GhostVisualBuilder.cs # Ghost mesh building from vessel snapshots
│   │   ├── RecordingPaths.cs # Save-scoped path resolution
│   │   ├── ParsekSettings.cs # GameParameters.CustomParameterNode (per-save settings)
│   │   ├── Milestone.cs      # Milestone data class (events grouped into timeline commits)
│   │   ├── MilestoneStore.cs # Static milestone collection, file I/O, committed action queries
│   │   ├── ResourceBudget.cs # On-the-fly budget computation from recordings + milestones
│   │   ├── GameStateEvent.cs # Event struct + enum (18 types), ContractSnapshot
│   │   ├── GameStateStore.cs # Persistent event log, contract snapshots, baselines
│   │   ├── GameStateRecorder.cs # Career event subscriber (contracts, tech, crew, facilities)
│   │   ├── GameStateBaseline.cs # Full game state snapshot at commit points
│   │   └── CommittedActionDialog.cs # PopupDialog for blocked actions
│   └── Parsek.Tests/         # Unit tests (xUnit)
│       ├── Generators/
│       │   ├── RecordingBuilder.cs       # Fluent RECORDING ConfigNode builder
│       │   ├── VesselSnapshotBuilder.cs  # Minimal VESSEL ConfigNode builder
│       │   └── ScenarioWriter.cs         # SCENARIO assembly + .sfs injection
│       ├── SyntheticRecordingTests.cs    # 8 synthetic recordings + injection tests
│       ├── PartEventTests.cs            # Part event serialization, subtree, parachute tests
│       ├── DiagnosticLoggingTests.cs    # Regression tests for playback logging
│       ├── MilestoneTests.cs            # Milestone creation, flushing, partial replay
│       ├── ResourceBudgetTests.cs       # Resource reservation, partial replay, over-commit
│       ├── GameStateEventTests.cs       # Event serialization and epoch filtering
│       └── CommittedActionTests.cs      # Action-blocking logic tests
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

### Local KSP.log Validation

After running a manual in-game scenario, validate the latest Parsek session:

```bash
pwsh -File scripts/validate-ksp-log.ps1
```

The validator reads `KSP.log`, selects entries from the last
`[Parsek][INFO][Init] SessionStart runUtc=...` marker, and fails if
structured log contracts are violated. Verbose logging is expected to be
enabled during development. The script exits non-zero on failure.

### Synthetic Recording Injection
```bash
cd Source/Parsek.Tests
dotnet test --filter InjectAllRecordings
```

Injects 8 synthetic recordings into `saves/test career/` (both `persistent.sfs` and `1.sfs`):
- **Pad Walk** — 30s EVA ghost (Jeb walks near launchpad)
- **KSC Hopper** — 56s ghost ship (Val hops east ~600m)
- **Flea Flight** — 90s vessel spawn (Bob, lands ~2km east, real flight data)
- **Suborbital Arc** — 300s ghost ship (Bill, gravity turn to 71km)
- **KSC Pad Destroyed** — 12s sphere ghost (no snapshot, destroyed vessel)
- **Orbit-1** — 3000s vessel spawn (Bill, orbit with Keplerian segment)
- **Close Spawn Conflict** — 12s vessel spawn (Jeb, tests proximity offset)
- **Island Probe** — 180s vessel spawn (Val, flies to island airfield)

**How it works:**
- Save UT is set to 17000 (KSC mid-morning) during `--clean-start` for consistent daytime lighting
- Recording offsets are 30s apart from that base UT (first ghost at +30s)
- Injected into both `persistent.sfs` and `1.sfs` (KSP's `initialLoadDone` guard skips reload if only one is patched)
- All crewed recordings use the FleaRocket craft (mk1pod.v2 + solidBooster.sm.v2 + parachuteSingle) with real part positions

**Reproducible end-to-end test workflow:**
1. `dotnet test --filter InjectAllRecordings` — inject recordings
2. (Re)start KSP (required if DLL changed — post-build copy fails while KSP locks it)
3. Load `test career` → save 1
4. Enter flight (launch any vessel or EVA from pad)
5. Time warp — ghosts appear at scheduled UTs, vessels spawn at end
6. `grep "[Parsek]" "Kerbal Space Program/KSP.log"` — verify full diagnostic narrative

See `docs/synthetic-recordings.md` for builder API docs.

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
8. Ghost vessel appears (opaque replica with original part textures) and replays previous flight
9. Engine flames and smoke appear on the ghost during burn phases
10. Decoupled/destroyed parts disappear from ghost at the correct time
11. Funds/science/reputation deltas are applied at the correct UT

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

**Part event test (staging/decoupling):**
1. Launch vessel with SRB + parachute → F9 record → stage SRB → deploy chute → F9 stop → revert → merge
2. Watch ghost: SRB + everything below decoupler disappears at staging UT
3. Parachute events logged (canopy mesh not replayed — procedural)

**EVA child recording test:**
1. Launch with crew → F9 record → EVA kerbal → verify parent auto-commits + child recording starts
2. Revert → both ghosts play back (vessel ghost and EVA kerbal ghost)
3. Parent vessel spawns without EVA'd kerbal → EVA kerbal spawns separately

**Settings test:**
1. Open Parsek UI → Settings → toggle auto-record off
2. Launch → verify no auto-recording
3. Toggle auto-warp-stop off → warp past recording → verify warp continues
4. Adjust sampling sliders → record → observe different sample density
5. Click Defaults → verify all values reset
6. Save/reload → verify settings persist
7. Esc > Settings > Parsek → verify same values in KSP difficulty screen

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
- Mid-recording EVA: auto-stops parent, commits it to timeline, starts linked child recording for EVA kerbal

**Part event recording:**
- Part death (`onPartDie`) and joint break (`onPartJointBreak`) are recorded as `PartEvent` entries
- Parachute state transitions are polled every physics frame (before adaptive sampling early-return)
- Part events are serialized as `PART_EVENT` ConfigNodes in the save file

**Recording safeguards:**
- Recording is blocked while the game is paused
- Recording auto-stops if the active vessel changes (docking, pressing `[`/`]`)
- Recordings shorter than 2 sample points are silently dropped on revert
- Adaptive sampling: Harmony postfix fires every physics frame but only records when velocity direction changes >2deg, speed changes >5%, or 3s max interval elapses
- When vessel goes on rails (time warp), trajectory sampling stops and an OrbitSegment captures the Keplerian orbit parameters instead
- When vessel goes off rails, the orbit segment is finalized and sampling resumes with boundary points at each transition
- SOI changes during on-rails recording close the current orbit segment and open a new one for the new body

**Ghost playback:**
- Ghost built from vessel snapshot using prefab meshes with original materials (fully opaque, realistic appearance)
- Falls back to green sphere if no vessel snapshot available
- Engine FX (flames, smoke) displayed during burn phases — modern EFFECTS engines use cloned MODEL_MULTI_PARTICLE prefabs; legacy stock parts (Flea SRB, LV-T30) use cloned fx_* prefab children with SmokeTrailControl stripped
- Part events applied during playback: decoupled subtrees hidden, destroyed parts hidden, parachute events logged
- Ghost part tree (persistentId-based) enables O(1) part lookup and recursive subtree hiding on decouple
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
- `grep "[Parsek]" KSP.log` captures all diagnostic logs (single prefix)
- `[Parsek Scenario]` prefix used only by legacy OnSave/OnLoad bookkeeping
- Alt+F12 opens Unity debug console in-game

**Diagnostic log narrative** (all use `[Parsek]` prefix):
- **Scenario load:** recording count, per-recording status (future/in-progress/past), crew reservations
- **Flight ready:** per-recording summary (UT range, point count, orbit segments, vessel/ghost-only)
- **Ghost lifecycle:** ENTERED/EXITED range with UT, spawn or no-spawn reason
- **Orbit segments:** activation with cache key, body, SMA, UT range
- **Resource deltas:** completion summary with recorded fund/science/rep totals
- **Vessel spawn:** situation, crew list, nearest vessel distance
- **Warp stop:** one log per recording (not per-frame — guarded by HashSet)

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

## Post-Change Checklist

After any major change (new enum values, new event types, new serialized fields, schema changes):
1. **Check save serialization** — verify `ParsekScenario.cs` OnSave/OnLoad handles the new data (int casts, ConfigNode keys)
2. **Check synthetic recording injector** — verify `RecordingBuilder` / `VesselSnapshotBuilder` / `ScenarioWriter` in `Tests/Generators/` can produce test data exercising the new feature
3. **Consider adding a synthetic recording** that exercises the new feature for end-to-end KSP testing via `dotnet test --filter InjectAllRecordings`
4. **Run `dotnet test`** — all existing tests must still pass

## Why This Approach

- **Isolated testing** - No conflicts with Steam installation
- **Clean environment** - Minimal mods = easier debugging
- **Fast iteration** - Auto-deployment on build
- **Reproducible** - Can share exact test environment setup
