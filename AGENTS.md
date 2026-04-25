## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

## Build & Test

```bash
dotnet build Source/Parsek/Parsek.csproj                         # builds + auto-copies to KSP GameData
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj             # full xUnit suite
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter InjectAllRecordings
pwsh -File scripts/test-coverage.ps1                            # local xUnit coverage report
```

Post-build copy uses `ContinueOnError="true"` - builds succeed when KSP has DLL locked.
Prefer running these commands from the repo/worktree root so they behave the same in sibling worktrees.

`dotnet test` only covers the headless xUnit suite. Runtime / in-game tests live under `Source/Parsek/InGameTests/` and must be run inside KSP via `Ctrl+Shift+T`; results export to `parsek-test-results.txt` in the KSP root.

Tests marked `AllowBatchExecution = false` are single-run only. They do not execute under `Run All` / batch category runs and will show up as `(never run)` in `parsek-test-results.txt` until you launch them individually from the in-game test runner.

If a machine-specific environment issue blocks `dotnet test` or `dotnet restore` (for example testhost socket startup or NuGet initialization), treat that as an environment blocker, not a repo failure. In that case, use:

```bash
dotnet build Source/Parsek.Tests/Parsek.Tests.csproj --no-restore
```

and record clearly that full xUnit execution was blocked by the local environment.

If a command that needs network access fails in the sandbox (for example `git fetch`, `dotnet restore`, package downloads, or DNS/host resolution), retry it with an escalation request instead of treating it as a repo failure. If the sandbox shell/network path still misbehaves, rerun the command via `pwsh`/`pwsh.exe` and record that the blocker was environmental.

## Release

```bash
python scripts/release.py    # build Release, run tests, package zip
```

Produces `Parsek-v{version}.zip` in repo root with `GameData/Parsek/` layout (DLL + version file + toolbar textures). Validates that `Parsek.version` and `AssemblyInfo.cs` versions match before building.

## Utility Scripts

- `build.bat [Debug|Release] [KSP_PATH]` - wrapper around `dotnet build Source/Parsek/Parsek.csproj`; resolves `KSPDIR` automatically and relies on the project post-build copy to deploy `Parsek.dll` into `GameData/Parsek/Plugins`.
- `python scripts/collect-logs.py [label] [--save NAME] [--skip-validation] [--skip-recordings] [--ksp-dir PATH]` - gathers `KSP.log`, `Player.log`, `parsek-test-results.txt`, save snapshots, and recording sidecars into `../logs/<timestamp>[_label]/`; runs log validation unless explicitly skipped.
- `pwsh -File scripts/inject-recordings.ps1 [--clean-start] [--save-name NAME] [--target-save FILE] [--build] [--run-diagnostics-tests]` - injects synthetic test recordings into a chosen KSP save, optionally rebuilding first and/or running the diagnostics/observability test slice before injection.
- `python scripts/release.py` - builds Release, runs the full headless test suite, and packages `Parsek-v{version}.zip` with the `GameData/Parsek/` release layout.
- `pwsh -File scripts/test-coverage.ps1 [-TestProject PATH] [-OutputDir DIR] [-Format cobertura] [-NoRestore] [-NoBuild]` - runs local xUnit coverage, validates the emitted report shape, and writes coverage artifacts under `TestResults/Coverage` by default.
- `pwsh -File scripts/validate-ksp-log.ps1 [-LogPath PATH] [-NoBuild]` - resolves the latest `KSP.log`, then runs `LiveKspLogValidationTests.ValidateLatestSession` against it and fails if the retained session does not satisfy the log contract.
- `python scripts/validate-release-bundle.py <bundle-dir> [--profile NAME]` - validates a retained release-closeout bundle: required artifacts, `log-validation.txt` pass marker, and required runtime rows in `parsek-test-results.txt`; writes `release-bundle-validation.txt` into the bundle.

## Investigating KSP Internals

When investigating KSP API behavior, search the web and read other open-source KSP mods (Trajectories, Principia, KSPCommunityFixes, VesselMover) for patterns and prior art.

## KSP API & Code Gotchas

**Enums / APIs**
- `GameScenes.TRACKSTATION` (not `TRACKINGSTATION`)
- `PopupDialog` / `MultiOptionDialog` live in `Assembly-CSharp` — no extra Unity module reference needed
- `ScenarioCreationOptions.AddToAllGames` for ScenarioModules that must exist in every save
- `FlightCamera.camPitch/camHdg` are **radians**, not degrees (stock defaults 0.2/0.3 = ~11.5°/~17°); pivot rotation is `frameOfReference * Yaw(camHdg) * Pitch(camPitch)`
- `VesselPrecalculate.vessel` is protected — compare with `__instance.gameObject != v.gameObject` instead
- `ModuleEngines.runningEffectName`/`directThrottleEffectName` not accessible at compile time — scan EFFECTS config instead
- `onPartJointBreak` signature: `(PartJoint joint, float breakForce)`

**Part names**: KSP converts underscores to dots at runtime. cfg `name = solidBooster_v2` → runtime `solidBooster.v2`. Always use dot-form in `PartLoader.getPartInfoByName` and ghost snapshot part names.

**Rotation / world frame**: KSP uses two different rotation contracts here; do not mix them.
- Surface-relative capture: `srfRelRotation = Inverse(body.bodyTransform.rotation) * v.transform.rotation`
- Live `Transform.rotation` playback/ghost placement: `worldRot = body.bodyTransform.rotation * srfRelRotation`
- ProtoVessel snapshots: `VESSEL.rot` is parsed as `ProtoVessel.rotation` and `ProtoVessel.Load()` assigns it to `vesselRef.srfRelRotation`, so Parsek-authored ProtoVessel nodes must write the raw recorded `srfRelRotation`, not `body.bodyTransform.rotation * srfRelRotation`.
- All recording rotation is unconditionally surface-relative since format v0. Orbital rotation needs inertial-frame + `Planetarium.Rotation` snapshot (future work).
- Format-v6 `ReferenceFrame.Relative` track sections store anchor-local world rotation: `Inverse(anchor.rotation) * focusWorldRotation`, and playback resolves with `anchor.rotation * localRot`.
- Format-v6 `ReferenceFrame.Relative` track sections store anchor-local Cartesian POSITION offset (metres) in `TrajectoryPoint.latitude`/`longitude`/`altitude`: `Inverse(anchor.rotation) * (focusWorldPos - anchorWorldPos)` — see `FlightRecorder.cs:5502-5543` (`ApplyRelativeOffset`) and `TrajectoryMath.ComputeRelativeLocalOffset` (recorder side); `TrajectoryMath.ApplyRelativeLocalOffset` and `ParsekFlight.TryResolveRelativeOffsetWorldPosition` (playback side). The field NAMES are misleading: in v6 RELATIVE sections those are NOT body-fixed lat/lon/alt — values commonly fall outside `[-90,90]` / `[-180,180]` and represent metres along the anchor's local x/y/z axes. Any code path that reads `point.latitude/longitude/altitude` from a flat `Recording.Points` list MUST first resolve `TrackSection.referenceFrame` for that UT and dispatch through `TryResolveRelativeWorldPosition` when the section is RELATIVE; calling `body.GetWorldSurfacePosition(lat, lon, alt)` directly on a RELATIVE-frame point will silently produce a position deep inside the planet because metre-scale dx/dy/dz are interpreted as degrees + altitude.
- Legacy v5-and-older `ReferenceFrame.Relative` sections keep the older contract and must replay through the legacy path only; do not auto-reinterpret old RELATIVE payloads as v6 anchor-local data.

**Krakensbane-corrected velocity**: `(Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity())`

**ConfigNode file I/O**: `ConfigNode.Save()` writes node CONTENTS only (values + children), NOT the node-name wrapper. `ConfigNode.Load()` returns a node already containing the file contents. Do NOT call `root.GetNode("Name")` after load. Use `FileIOUtils` for safe-write (.tmp + rename).

**InvariantCulture everywhere**: all float/double serialization uses `ToString("R", CultureInfo.InvariantCulture)`. UI formatting (`$"{val:F1}"`) also needs InvariantCulture — comma-locale systems produce broken output otherwise.

**Ghost event ↔ snapshot PID**: `VesselSnapshotBuilder.AddPart` assigns `persistentId = 100000 + idx*1111`. Single-part showcase ghosts must use PID `100000` for their events or playback lookup silently fails. Ghost part GameObjects are named by `persistentId` for O(1) lookup.

**Engine key encoding**: `(ulong)pid << 8 | (uint)moduleIndex` — up to 256 engine modules per part. RCS uses separate dicts (`activeRcsKeys`/`lastRcsThrottle`) so keys may overlap.

**Test working dir**: xUnit runs from `Source/Parsek.Tests/bin/Debug/net472/` — use 5 `..` segments to reach project root. Classes touching shared static state (`ParsekLog`, `RecordingStore`, `ParsekScenario.crewReplacements`) need `[Collection("Sequential")]` and the corresponding `ResetForTesting()` calls.

**Recording storage (format v3)**: bulk data lives in sidecar files under `saves/<save>/Parsek/Recordings/`: `<id>.prec` (trajectory), `<id>_vessel.craft`, `<id>_ghost.craft`, `<id>.pcrf` (ghost geometry). Only lightweight metadata + mutable state stays in `.sfs`. `RecordingPaths.ValidateRecordingId` rejects path traversal and invalid filename chars.

## Project Layout

```
Source/Parsek/              # Mod source (SDK-style .csproj)
Source/Parsek/InGameTests/  # Runtime test framework (runs inside KSP via Ctrl+Shift+T)
Source/Parsek.Tests/        # xUnit tests + Generators/ (RecordingBuilder, VesselSnapshotBuilder, ScenarioWriter)
Kerbal Space Program/       # Local KSP instance (gitignored, auto-deployed)
docs/                       # Design docs, roadmap, reference analyses
```

Key source files and what they do - read the relevant one before modifying:
- `ParsekFlight.cs` - flight-scene controller (policy, recording, chain management, input). Camera follow delegated to WatchModeController.
- `WatchModeController.cs` - camera-follow / watch-mode state machine (enter/exit watch, camera anchoring, overlap retarget, explosion hold)
- `GhostPlaybackEngine.cs` - ghost playback mechanics engine: owns ghostStates, per-frame positioning, loop/overlap playback, zone transitions, soft caps, reentry FX. Zero Recording references — accesses trajectories via IPlaybackTrajectory only. Future standalone mod core.
- `ParsekPlaybackPolicy.cs` - event subscriber reacting to engine lifecycle events (spawn decisions, resource deltas, camera management, deferred spawn queue)
- `IPlaybackTrajectory.cs` - interface exposing 27 trajectory/visual/orbital fields from Recording to the engine
- `IGhostPositioner.cs` - 8 positioning methods implemented by ParsekFlight, delegates world-space placement to the host scene
- `GhostPlaybackEvents.cs` - lifecycle event types (PlaybackCompleted, LoopRestarted, OverlapExpired, CameraAction), TrajectoryPlaybackFlags, FrameContext
- `ChainSegmentManager.cs` - chain segment state (active chain ID, continuation tracking, boundary anchors). Owns 16 fields previously scattered across ParsekFlight.
- `FlightRecorder.cs` - recording state + sampling (called by Harmony patch). Always-tree mode: every recording gets a RecordingTree (#271). `DecideOnVesselSwitch` has no Stop decision.
- `ParsekUI.cs` - UI main window, map markers, and coordinator for extracted sub-windows
- `UI/RecordingsTableUI.cs` - recordings table window (sort, rename, group tree, chain blocks, loop period editing)
- `UI/SettingsWindowUI.cs` - settings window (recording, looping, ghost, diagnostics, sampling, data management)
- `UI/TestRunnerUI.cs` - in-game test runner window
- `UI/GroupPickerUI.cs` - group picker popup (recording/chain group assignment)
- `UI/SpawnControlUI.cs` - Real Spawn Control window (nearby vessel proximity spawning)
- `UI/ActionsWindowUI.cs` - Game Actions window (ledger display, budget, retired kerbals)
- `InGameTests/` - runtime test framework: `InGameTestAttribute` (discovery), `InGameAssert` (assertions), `InGameTestRunner` (execution + results export), `TestRunnerShortcut` (global Ctrl+Shift+T addon), `RuntimeTests` (74 tests across 21 categories), `LogContractTests` (log format/level/resource validation migrated from post-hoc KSP.log checker)
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
- `VesselSpawner.cs` - vessel spawn/recover/snapshot utilities, resource manifest extraction (`ExtractResourceManifest`)
- `ResourceManifest.cs` - `ResourceAmount` struct and `ComputeResourceDelta` for per-resource change computation (Phase 11)
- `MergeDialog.cs` - post-revert tree merge dialog (standalone/chain dialogs removed in T56)
- `GhostMapPresence.cs` - ProtoVessel lifecycle for ghost map presence: creates/destroys lightweight vessels for tracking station, orbit lines, targeting. Manages ghostMapVesselPids HashSet for O(1) guard checks across codebase.
- `ParsekHarmony.cs` + `Patches/` - Harmony patcher + patches (PhysicsFrame, GhostVesselLoad, GhostCommNetVessel, GhostTrackingStation, FacilityUpgrade, FlightResults, ScienceSubject, TechResearch, CrewDialogFilter, KerbalDismissal, GhostOrbitLine)

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
pwsh -File scripts/validate-ksp-log.ps1            # log pipeline health check (4 rules: session markers, recording start/stop)
python scripts/collect-logs.py [label] [--save NAME]  # gather all logs/saves/test results into ../logs/ timestamped folder
```

When asked to debug an issue, run `python scripts/collect-logs.py <label>` first to snapshot all relevant state, then work from the collected files. The script also runs the log validation automatically. Output goes to `../logs/` (sibling of repo root, outside git).

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
4. Run `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` - all headless tests must pass
5. If the change affects runtime-only behavior, run the relevant in-game tests via `Ctrl+Shift+T` and keep `parsek-test-results.txt` / `KSP.log` evidence

## Documentation Updates — Per Commit, Not Per PR

Before every commit that changes behavior (not just the first one in a PR), check whether these docs need updating and stage them in the same commit:

- `CHANGELOG.md` — add or update the entry under the current version. On follow-up commits that change the fix approach, edit the existing entry rather than leaving the original wording stale.
- `docs/dev/todo-and-known-bugs.md` — mark completed items as ~~done~~, add newly discovered items, and update the "Fix:" description on follow-up commits when the approach changes.
- This file (`AGENTS.md`) and `.claude/CLAUDE.md` — update when file layout, build commands, workflow, or key patterns change.

**Follow-up commit trap:** When a review comment lands on an open PR and changes the fix approach, the CHANGELOG and todo entries written for the first commit become stale. The reviewer reads those docs as authoritative — they must match the code in the current HEAD. Before pushing the follow-up commit, re-read the existing doc entries for the bug/feature and update them to match the new approach.

**Practical check:** after `git add`, run `git diff --cached` and ask: "does any of this contradict or supersede existing wording in CHANGELOG.md or todo-and-known-bugs.md?" If yes, stage the doc updates in the same commit.

## Code Review Follow-Ups

When a reviewer flags fixes on an open PR, re-review only the changed fixes and any directly affected code paths. Do not restart a full-PR review from scratch on every follow-up unless the new changes actually broaden the risk surface.

## Workflow

See `docs/dev/development-workflow.md` for the full feature development process (vision → scenarios → design doc → plan/build/review cycle).
