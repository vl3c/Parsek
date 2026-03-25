## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

## Build & Test

```bash
cd Source/Parsek && dotnet build          # builds + auto-copies to KSP GameData
cd Source/Parsek.Tests && dotnet test     # all unit tests
dotnet test --filter InjectAllRecordings  # inject 8 synthetic recordings into test save
```

Post-build copy uses `ContinueOnError="true"` - builds succeed when KSP has DLL locked.

## Project Layout

```
Source/Parsek/          # Mod source (SDK-style .csproj)
Source/Parsek.Tests/    # xUnit tests + Generators/ (RecordingBuilder, VesselSnapshotBuilder, ScenarioWriter)
Kerbal Space Program/   # Local KSP instance (gitignored, auto-deployed)
docs/                   # Design docs, roadmap, reference analyses
```

Key source files and what they do - read the relevant one before modifying:
- `ParsekFlight.cs` - flight-scene controller (policy, recording, chain management, camera follow, input)
- `GhostPlaybackEngine.cs` - ghost playback mechanics engine: owns ghostStates, per-frame positioning, loop/overlap playback, zone transitions, soft caps, reentry FX. Zero Recording references — accesses trajectories via IPlaybackTrajectory only. Future standalone mod core.
- `ParsekPlaybackPolicy.cs` - event subscriber reacting to engine lifecycle events (spawn decisions, resource deltas, camera management, deferred spawn queue)
- `IPlaybackTrajectory.cs` - interface exposing 19 trajectory/visual fields from Recording to the engine
- `IGhostPositioner.cs` - 8 positioning methods implemented by ParsekFlight, delegates world-space placement to the host scene
- `GhostPlaybackEvents.cs` - lifecycle event types (PlaybackCompleted, LoopRestarted, OverlapExpired, CameraAction), TrajectoryPlaybackFlags, FrameContext
- `ChainSegmentManager.cs` - chain segment state (active chain ID, continuation tracking, boundary anchors). Owns 16 fields previously scattered across ParsekFlight.
- `FlightRecorder.cs` - recording state + sampling (called by Harmony patch)
- `ParsekUI.cs` - UI windows (main, recordings, actions, settings, Real Spawn Control) and map markers
- `SelectiveSpawnUI.cs` - pure static methods for Real Spawn Control (proximity candidates, countdown formatting)
- `ParsekScenario.cs` - ScenarioModule for save/load, coroutine hosting, scene transitions
- `CrewReservationManager.cs` - crew reservation lifecycle (reserve/unreserve/swap/clear)
- `ResourceApplicator.cs` - resource mutation (tick deltas, budget deduction, rewind correction)
- `GroupHierarchyStore.cs` - UI recording group hierarchy and visibility state
- `RecordingStore.cs` - static recording storage surviving scene changes
- `GhostVisualBuilder.cs` - ghost mesh building from vessel snapshots
- `TrajectoryMath.cs` - pure static math (sampling, interpolation, orbit search)
- `VesselSpawner.cs` - vessel spawn/recover/snapshot utilities
- `MergeDialog.cs` - post-revert merge dialog
- `ParsekHarmony.cs` + `Patches/` - Harmony patcher + postfix patches

## Worktree Workflow

For manual worktrees (when not using `isolation=worktree`), create as sibling folders:
```bash
cd Parsek
git worktree add ../Parsek-<branch-name> -b <branch-name> HEAD
```

`Parsek.csproj` probes up to 5 parent levels for `Kerbal Space Program/`, so builds work from worktrees at this location. Merge: `cd Parsek && git merge <branch-name>`

## In-Game Controls

- **Toolbar button** - Toggle Parsek UI
- UI buttons: Start/Stop Recording, Preview Playback, Stop Preview, Clear Current Recording

## Debug

```bash
grep "[Parsek]" "Kerbal Space Program/KSP.log"    # all diagnostic logs
pwsh -File scripts/validate-ksp-log.ps1            # structured log validation
```

Alt+F12 opens Unity debug console in-game.

## Logging Requirements

Every action, state transition, guard condition skip, and FX lifecycle event MUST be logged. The KSP.log is our primary debugging tool — if it didn't get logged, it didn't happen.

- Use `ParsekLog.Log` / `ParsekLog.Info` / `ParsekLog.Warn` for important events
- Use `ParsekLog.Verbose` for high-frequency or detailed diagnostic info
- Use `ParsekLog.VerboseRateLimited` for per-frame data (avoids log spam)
- Include subsystem tag, relevant IDs (recording index, vessel name, part PID), and numeric values
- Log format: `[Parsek][LEVEL][Subsystem] message` (handled by ParsekLog.Write)
- See existing patterns in `ParsekFlight.cs` (ghost lifecycle) and `GhostVisualBuilder.cs` (FX build chain) for reference

## Testing Requirements

- Every new method with logic (guards, state transitions, decisions) needs unit tests
- Pure/static methods should be `internal static` for direct testability
- Use `ParsekLog.TestSinkForTesting` to capture log output and assert on it — log assertions verify that code paths executed and logged the expected data
- Test pattern: `ParsekLog.TestSinkForTesting = line => logLines.Add(line)` in constructor, `ParsekLog.ResetTestOverrides()` in Dispose
- Assert with: `Assert.Contains(logLines, l => l.Contains("[Subsystem]") && l.Contains("expected text"))`
- Use `[Collection("Sequential")]` on test classes that touch shared static state (ParsekLog, RecordingStore, etc.)
- See `RewindLoggingTests.cs` for the canonical log-capture test pattern

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
