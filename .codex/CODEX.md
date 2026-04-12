# Codex Working Reference

This file is the Codex-side reference for this repo. It is based on `.claude/CLAUDE.md` plus the default Codex workflow used in this codebase.

## Source Inputs

- Repo-specific guidance: `.claude/CLAUDE.md`
- Claude local settings: `.claude/settings.json`
- Codex keeps using its own runtime model/instructions; the Claude `model` and `effortLevel` settings are informational only.

## Core Repo Rules

- Do not add `Co-Authored-By` or signature lines to commit messages.
- Build from the local solution and keep the KSP deployment working from sibling worktrees.
- Logging is mandatory for action/state/guard/FX lifecycle paths.
- Every new logic-bearing method needs tests.
- Update docs in the same commit when behavior changes.
- Ghost visuals and recording data should stay visually correct, minimal, and efficient.

## Codex Defaults In This Repo

- Inspect relevant code before editing; do not guess architecture from filenames alone.
- Prefer `rg` / `rg --files` for search.
- Use `apply_patch` for manual file edits.
- Keep changes in logical commits.
- Do not revert unrelated user changes.
- Prefer focused verification after each slice instead of delaying all testing to the end.
- Keep user updates concise and factual while work is in progress.

## Build And Test

```powershell
cd Source/Parsek
dotnet build

cd ..\Parsek.Tests
dotnet test

dotnet test --filter InjectAllRecordings
```

- `dotnet build` auto-copies to KSP `GameData`.
- Post-build copy uses `ContinueOnError="true"`, so a locked DLL should not make the build fail.

Alternative repo-root build:

```powershell
.\build.bat Debug
.\build.bat Release
```

## Release

```powershell
python scripts/release.py
```

- Produces `Parsek-v{version}.zip` in the repo root.
- Validates `Parsek.version` and `AssemblyInfo.cs` version alignment before packaging.

## Project Layout

```text
Source/Parsek/              Mod source
Source/Parsek/InGameTests/  Runtime KSP tests
Source/Parsek.Tests/        xUnit tests + generators
Kerbal Space Program/       Local KSP instance (gitignored, auto-deployed)
docs/                       Design docs, roadmap, analyses
```

### Key Files

- `ParsekFlight.cs`: flight-scene controller and policy host
- `WatchModeController.cs`: watch/camera-follow state machine
- `GhostPlaybackEngine.cs`: ghost playback engine and per-frame playback mechanics
- `ParsekPlaybackPolicy.cs`: reacts to playback lifecycle events
- `IPlaybackTrajectory.cs`: engine-facing trajectory interface
- `IGhostPositioner.cs`: scene-host positioning interface
- `GhostPlaybackEvents.cs`: playback lifecycle/event types
- `ChainSegmentManager.cs`: chain segment state
- `FlightRecorder.cs`: recording and sampling
- `ParsekUI.cs`: main UI coordinator
- `UI/RecordingsTableUI.cs`: recordings table
- `UI/SettingsWindowUI.cs`: settings window
- `UI/TestRunnerUI.cs`: in-game test runner
- `UI/GroupPickerUI.cs`: group picker
- `UI/SpawnControlUI.cs`: real spawn control UI
- `UI/ActionsWindowUI.cs`: game actions window
- `ParsekScenario.cs`: scenario module, save/load, coroutines, scene transitions
- `RecordingStore.cs`: static recording storage
- `GhostVisualBuilder.cs`: ghost mesh construction
- `TrajectoryMath.cs`: pure static math
- `VesselSpawner.cs`: spawning/recovery/snapshot utilities
- `GhostMapPresence.cs`: map presence / orbit lines / tracking station ghosts
- `ParsekHarmony.cs` and `Patches/`: Harmony patching entrypoints

Read the relevant file before modifying it.

## Worktree Workflow

For manual sibling worktrees:

```powershell
cd Parsek
git worktree add ..\Parsek-<branch-name> -b <branch-name> HEAD
```

- `Parsek.csproj` probes up to five parent levels for `Kerbal Space Program/`, so sibling worktrees still deploy correctly.

## In-Game Controls

- Toolbar button: toggle Parsek UI
- `Ctrl+Shift+T`: toggle in-game test runner
- Main UI controls include start/stop recording, preview playback, stop preview, and clear current recording

## Debug Workflow

```powershell
pwsh -File scripts/validate-ksp-log.ps1
python scripts/collect-logs.py <label>
```

- When debugging, snapshot logs first with `python scripts/collect-logs.py <label>`.
- Output goes to `../logs/` outside git.
- `Alt+F12` opens the Unity debug console in game.

## Logging Rules

- If it did not get logged, it effectively did not happen.
- Use `ParsekLog.Info` / `Warn` for important events.
- Use `Verbose` for one-shot diagnostics.
- Use `VerboseRateLimited` for per-frame or per-ghost recurring data.
- Include subsystem tags, IDs, and concrete numeric values.
- Prefer batch summaries when iterating over collections.

## Testing Rules

- Every new method with logic should have unit tests.
- Make pure/static logic `internal static` when possible for direct testing.
- Use `ParsekLog.TestSinkForTesting` for log assertions.
- Use `[Collection("Sequential")]` when tests touch shared static state.
- Use in-game tests for KSP-dependent behavior such as ghost visuals, crew, CommNet, and PartLoader behavior.

## Post-Change Checklist

After changes to enums, event types, serialized fields, or schema:

1. Check `ParsekScenario.cs` save/load coverage.
2. Check test generators under `Source/Parsek.Tests/Generators/`.
3. Consider a synthetic recording for end-to-end coverage.
4. Run tests.

## Documentation Sync

Before each behavior-changing commit, review and update as needed:

- `CHANGELOG.md`
- `docs/dev/todo-and-known-bugs.md`
- `.claude/CLAUDE.md`
- this file when Codex-specific workflow guidance changes

## KSP Investigation

Use `ilspycmd` against `Assembly-CSharp.dll` when KSP internals matter. Decompiled output has obfuscation artifacts, so treat control flow carefully.

## Current Repo Convention

- Prefer work in small, reviewable slices.
- Keep KSP-facing behavior observable through logs and diagnostics.
- Favor backend-owned defaults over exposing tuning knobs unless the setting is genuinely user-facing.
