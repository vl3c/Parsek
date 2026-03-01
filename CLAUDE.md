## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

## Build & Test

```bash
cd Source/Parsek && dotnet build          # builds + auto-copies to KSP GameData
cd Source/Parsek.Tests && dotnet test     # all unit tests
dotnet test --filter InjectAllRecordings  # inject 8 synthetic recordings into test save
```

Post-build copy uses `ContinueOnError="true"` — builds succeed when KSP has DLL locked.

## Project Layout

```
Source/Parsek/          # Mod source (SDK-style .csproj)
Source/Parsek.Tests/    # xUnit tests + Generators/ (RecordingBuilder, VesselSnapshotBuilder, ScenarioWriter)
Kerbal Space Program/   # Local KSP instance (gitignored, auto-deployed)
docs/                   # Design docs, roadmap, reference analyses
```

Key source files and what they do — read the relevant one before modifying:
- `ParsekFlight.cs` — main flight-scene controller (playback, timeline, input)
- `FlightRecorder.cs` — recording state + sampling (called by Harmony patch)
- `ParsekUI.cs` — UI windows (main, recordings, actions, settings) and map markers
- `ParsekScenario.cs` — ScenarioModule for save/load, crew reservation & replacement
- `RecordingStore.cs` — static recording storage surviving scene changes
- `GhostVisualBuilder.cs` — ghost mesh building from vessel snapshots
- `TrajectoryMath.cs` — pure static math (sampling, interpolation, orbit search)
- `VesselSpawner.cs` — vessel spawn/recover/snapshot utilities
- `MergeDialog.cs` — post-revert merge dialog
- `ParsekHarmony.cs` + `Patches/` — Harmony patcher + postfix patches

## In-Game Controls

- **F9** — Start/Stop recording
- **F10** — Preview playback
- **F11** — Stop preview
- **Toolbar button** — Toggle Parsek UI

## Debug

```bash
grep "[Parsek]" "Kerbal Space Program/KSP.log"    # all diagnostic logs
pwsh -File scripts/validate-ksp-log.ps1            # structured log validation
```

Alt+F12 opens Unity debug console in-game.

## Post-Change Checklist

After any change to enums, event types, serialized fields, or schema:
1. Verify `ParsekScenario.cs` OnSave/OnLoad handles the new data
2. Verify test generators in `Tests/Generators/` can produce test data for the new feature
3. Consider adding a synthetic recording for end-to-end testing
4. Run `dotnet test` — all tests must pass

## Workflow

See `docs/development-workflow.md` for the full feature development process (vision → scenarios → design doc → plan/build/review cycle).
