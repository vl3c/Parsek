## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

## Build & Test

```bash
cd Source/Parsek && dotnet build          # builds + auto-copies to KSP GameData
cd Source/Parsek.Tests && dotnet test     # all unit tests
dotnet test --filter InjectAllRecordings  # inject 8 synthetic recordings into test save
```

Post-build copy uses `ContinueOnError="true"` - builds succeed when KSP has DLL locked.

## KSP Decompilation

To investigate KSP internals, decompile `Assembly-CSharp.dll` using `ilspycmd`:

```bash
# Install (once)
dotnet tool install -g ilspycmd

# Decompile a specific type
ilspycmd "Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll" -t KSP.UI.Screens.SpaceTracking -o /tmp/ksp-decompile/
ilspycmd "Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll" -t Vessel -o /tmp/ksp-decompile/
ilspycmd "Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll" -t MapView -o /tmp/ksp-decompile/
```

Decompiled output has obfuscation artifacts (spurious `while(true) switch` blocks) — ignore those. Also search the web and other KSP mods (Trajectories, Principia, KSPCommunityFixes, VesselMover) for patterns when investigating KSP API behavior.

## Project Layout

```
Source/Parsek/              # Mod source (SDK-style .csproj)
Source/Parsek/InGameTests/  # Runtime test framework (runs inside KSP via Ctrl+Shift+T)
Source/Parsek.Tests/        # xUnit tests + Generators/ (RecordingBuilder, VesselSnapshotBuilder, ScenarioWriter)
Kerbal Space Program/       # Local KSP instance (gitignored, auto-deployed)
docs/                       # Design docs, roadmap, reference analyses
```

Key source files and what they do - read the relevant one before modifying:
- `ParsekFlight.cs` - flight-scene controller (policy, recording, chain management, camera follow, input)
- `GhostPlaybackEngine.cs` - ghost playback mechanics engine: owns ghostStates, per-frame positioning, loop/overlap playback, zone transitions, soft caps, reentry FX. Zero Recording references — accesses trajectories via IPlaybackTrajectory only. Future standalone mod core.
- `ParsekPlaybackPolicy.cs` - event subscriber reacting to engine lifecycle events (spawn decisions, resource deltas, camera management, deferred spawn queue)
- `IPlaybackTrajectory.cs` - interface exposing 27 trajectory/visual/orbital fields from Recording to the engine
- `IGhostPositioner.cs` - 8 positioning methods implemented by ParsekFlight, delegates world-space placement to the host scene
- `GhostPlaybackEvents.cs` - lifecycle event types (PlaybackCompleted, LoopRestarted, OverlapExpired, CameraAction), TrajectoryPlaybackFlags, FrameContext
- `ChainSegmentManager.cs` - chain segment state (active chain ID, continuation tracking, boundary anchors). Owns 16 fields previously scattered across ParsekFlight.
- `FlightRecorder.cs` - recording state + sampling (called by Harmony patch)
- `ParsekUI.cs` - UI windows (main, recordings, settings, test runner) and map markers
- `UI/GroupPickerUI.cs` - group picker popup (recording/chain group assignment)
- `UI/SpawnControlUI.cs` - Real Spawn Control window (nearby vessel proximity spawning)
- `UI/ActionsWindowUI.cs` - Game Actions window (ledger display, budget, retired kerbals)
- `InGameTests/` - runtime test framework: `InGameTestAttribute` (discovery), `InGameAssert` (assertions), `InGameTestRunner` (execution + results export), `TestRunnerShortcut` (global Ctrl+Shift+T addon), `RuntimeTests` (50 tests across 13 categories)
- `SelectiveSpawnUI.cs` - pure static methods for Real Spawn Control (proximity candidates, countdown formatting)
- `ParsekScenario.cs` - ScenarioModule for save/load, coroutine hosting, scene transitions
- `CrewReservationManager.cs` - crew reservation lifecycle (reserve/unreserve/swap/clear)
- `GameActions/` - ledger-based game actions system (GameAction, Ledger, RecalculationEngine, 8 resource modules including KerbalsModule, KspStatePatcher, LedgerOrchestrator, GameStateEventConverter)
- `GroupHierarchyStore.cs` - UI recording group hierarchy and visibility state
- `FileIOUtils.cs` - shared safe-write (tmp+rename) utility for ConfigNode file I/O
- `SuppressionGuard.cs` - IDisposable guard struct for GameStateRecorder suppression flags
- `RecordingStore.cs` - static recording storage surviving scene changes
- `GhostVisualBuilder.cs` - ghost mesh building from vessel snapshots
- `TrajectoryMath.cs` - pure static math (sampling, interpolation, orbit search)
- `VesselSpawner.cs` - vessel spawn/recover/snapshot utilities
- `MergeDialog.cs` - post-revert merge dialog
- `GhostMapPresence.cs` - ProtoVessel lifecycle for ghost map presence: creates/destroys lightweight vessels for tracking station, orbit lines, targeting. Manages ghostMapVesselPids HashSet for O(1) guard checks across codebase.
- `ParsekHarmony.cs` + `Patches/` - Harmony patcher + patches (PhysicsFrame, GhostVesselLoad, GhostCommNetVessel, GhostTrackingStation, FacilityUpgrade, FlightResults, ScienceSubject, TechResearch)

## Worktree Workflow

For manual worktrees (when not using `isolation=worktree`), create as sibling folders:
```bash
cd Parsek
git worktree add ../Parsek-<branch-name> -b <branch-name> HEAD
```

`Parsek.csproj` probes up to 5 parent levels for `Kerbal Space Program/`, so builds work from worktrees at this location. Merge: `cd Parsek && git merge <branch-name>`

## In-Game Controls

- **Toolbar button** - Toggle Parsek UI
- **Ctrl+Shift+T** - Toggle in-game test runner (works in any scene). Results auto-export to `parsek-test-results.txt` in KSP root.
- UI buttons: Start/Stop Recording, Preview Playback, Stop Preview, Clear Current Recording

## Debug

```bash
grep "[Parsek]" "Kerbal Space Program/KSP.log"    # all diagnostic logs
pwsh -File scripts/validate-ksp-log.ps1            # structured log validation
```

Alt+F12 opens Unity debug console in-game.

## Logging Requirements

Every action, state transition, guard condition skip, and FX lifecycle event MUST be logged. The KSP.log is our primary debugging tool — if it didn't get logged, it didn't happen.

- Use `ParsekLog.Info` / `ParsekLog.Warn` for important events
- Use `ParsekLog.Verbose` for detailed diagnostic info (one-shot operations)
- Use `ParsekLog.VerboseRateLimited` for per-frame or per-ghost-per-cycle data (avoids log spam). Use shared keys for aggregate summaries, per-index keys only when the index identity matters for debugging.
- Include subsystem tag, relevant IDs (recording index, vessel name, part PID), and numeric values
- Log format: `[Parsek][LEVEL][Subsystem] message` (handled by ParsekLog.Write)
- **Batch counting convention:** When iterating over collections with per-item decisions (skip/process), declare local `int` counters, increment inside the loop, and log a single summary after the loop. Use `Verbose` for one-shot operations (load/save), `VerboseRateLimited` for per-frame summaries. Do not log per-item inside the loop unless the item count is bounded (under ~20).
- See existing patterns in `GhostPlaybackEngine.cs` (frame batch counters) and `ParsekScenario.cs` (save/load batch summaries) for reference

## Testing Requirements

- Every new method with logic (guards, state transitions, decisions) needs unit tests
- Pure/static methods should be `internal static` for direct testability
- Use `ParsekLog.TestSinkForTesting` to capture log output and assert on it — log assertions verify that code paths executed and logged the expected data
- Test pattern: `ParsekLog.TestSinkForTesting = line => logLines.Add(line)` in constructor, `ParsekLog.ResetTestOverrides()` in Dispose
- Assert with: `Assert.Contains(logLines, l => l.Contains("[Subsystem]") && l.Contains("expected text"))`
- Use `[Collection("Sequential")]` on test classes that touch shared static state (ParsekLog, RecordingStore, etc.)
- See `RewindLoggingTests.cs` for the canonical log-capture test pattern
- **In-game tests** (`InGameTests/RuntimeTests.cs`): for things that require live KSP (ghost visuals, PartLoader resolution, crew roster, CommNet). Use `[InGameTest(Category = "...", Scene = GameScenes.FLIGHT)]` attribute. Tests can return `void` (sync) or `IEnumerator` (multi-frame coroutine). Run via Ctrl+Shift+T or Settings > Diagnostics.

## Visual & Recording Design Principle

Ghost visuals and recording data must be **correct visually, minimal, and efficient**. Many recordings play simultaneously — every per-frame computation and every stored event multiplies across all active ghosts. Prefer coarse-grained state snapshots over continuous sampling. Record threshold crossings, not continuous values. Use debounce to filter noise. If a visual detail isn't noticeable at playback speed, don't record it.

## Post-Change Checklist

After any change to enums, event types, serialized fields, or schema:
1. Verify `ParsekScenario.cs` OnSave/OnLoad handles the new data
2. Verify test generators in `Tests/Generators/` can produce test data for the new feature
3. Consider adding a synthetic recording for end-to-end testing
4. Run `dotnet test` - all tests must pass

## Workflow

See `docs/dev/development-workflow.md` for the full feature development process (vision → scenarios → design doc → plan/build/review cycle).
