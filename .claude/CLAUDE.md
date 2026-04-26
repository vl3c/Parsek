## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

## Build & Test

```bash
cd Source/Parsek && dotnet build          # builds + auto-copies to KSP GameData
cd Source/Parsek.Tests && dotnet test     # all unit tests
dotnet test --filter InjectAllRecordings  # inject 8 synthetic recordings into test save
```

Post-build copy uses `ContinueOnError="true"` - builds succeed when KSP has DLL locked.

**Always verify the deployed DLL after building**, especially when working from a worktree or when multiple worktrees exist side-by-side. The post-build copy can silently fail (KSP holding the file, MSBuild reporting "up-to-date" and skipping the copy target, or a concurrent build from a sibling worktree clobbering `GameData/Parsek/Plugins/Parsek.dll` with a different branch's output). When the user reports "I don't see my change in game," the first thing to check is whether the deployed DLL is actually the one you just built.

**Verification recipe:**

```bash
# 1. File size + mtime should match your worktree bin/Debug/Parsek.dll
ls -la "$KSPDIR/GameData/Parsek/Plugins/Parsek.dll"
ls -la Source/Parsek/bin/Debug/Parsek.dll

# 2. Grep the deployed DLL for a distinctive new UTF-16 string from your change
python -c "
with open(r'...GameData/Parsek/Plugins/Parsek.dll','rb') as f: d=f.read()
for s in ['NewLabel','OldLabel']: print(s, d.count(s.encode('utf-16-le')))
"

# 3. If mismatch, force-copy manually
cp Source/Parsek/bin/Debug/Parsek.dll "$KSPDIR/GameData/Parsek/Plugins/Parsek.dll"
```

From a manual worktree, set `KSPDIR` explicitly because the csproj's relative `Kerbal Space Program/` probe only walks parent directories of the csproj — a sibling-of-the-worktree layout at `C:/Users/vlad3/Documents/Code/Parsek/Kerbal Space Program/` is NOT reachable from `C:/Users/vlad3/Documents/Code/Parsek-<branch>/Source/Parsek/` via ancestor walking.

**If multiple worktrees exist**, any of them can overwrite the shared `GameData/Parsek/Plugins/Parsek.dll`. The deployed file belongs to whichever worktree built most recently. Re-verify after every build if you're switching between worktrees or if a sibling session is also building.

## Release

```bash
python scripts/release.py    # build Release, run tests, package zip
```

Produces `Parsek-v{version}.zip` in repo root with `GameData/Parsek/` layout (DLL + version file + toolbar textures). Validates that `Parsek.version` and `AssemblyInfo.cs` versions match before building.

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
- Absolute / surface trajectory points still store surface-relative rotation (`v.srfRelRotation`).
- Format-v6+ `ReferenceFrame.Relative` track sections store anchor-local world rotation: `Inverse(anchor.rotation) * focusWorldRotation`, and playback resolves with `anchor.rotation * localRot`. Live-anchor playback rotates the ghost with the live anchor; if the live anchor's pose diverges from recording (e.g. the anchor is the active Re-Fly target the player is now flying), the ghost rotates and translates with the player's controls until the staleness check / shadow fallback engages.
- Format-v6+ `ReferenceFrame.Relative` track sections store anchor-local Cartesian POSITION offset (metres) in `TrajectoryPoint.latitude`/`longitude`/`altitude`: `Inverse(anchor.rotation) * (focusWorldPos - anchorWorldPos)` — see `FlightRecorder.cs:5502-5543` (`ApplyRelativeOffset`) and `TrajectoryMath.ComputeRelativeLocalOffset` (recorder side); `TrajectoryMath.ApplyRelativeLocalOffset` and `ParsekFlight.TryResolveRelativeOffsetWorldPosition` (playback side). The field NAMES are misleading: in RELATIVE sections those are NOT body-fixed lat/lon/alt — values commonly fall outside `[-90,90]` / `[-180,180]` and represent metres along the anchor's local x/y/z axes. Any code path that reads `point.latitude/longitude/altitude` from a flat `Recording.Points` list MUST first resolve `TrackSection.referenceFrame` for that UT and dispatch through `TryResolveRelativeWorldPosition` when the section is RELATIVE; calling `body.GetWorldSurfacePosition(lat, lon, alt)` directly on a RELATIVE-frame point will silently produce a position deep inside the planet because metre-scale dx/dy/dz are interpreted as degrees + altitude.
- **Format-v7 (current, `RelativeAbsoluteShadowFormatVersion`) RELATIVE sections additionally store an `absoluteFrames` shadow list** (planet-relative absolute position points) alongside the anchor-local `frames`. The shadow is the recorder's snapshot of the focused vessel's true world position at each Relative sample, persisted as `ABSOLUTE_POINT` ConfigNodes (see `RecordingStore.cs:5262-5274` write, `:5408-5411` read). Playback consults the shadow via `ParsekFlight.TryUseAbsoluteShadowForActiveReFlyRelativeSection` and `ResolveAbsoluteShadowPlaybackFrames` whenever the live anchor is unreliable — primarily during an active Re-Fly session when the section's `anchorVesselId` matches the active Re-Fly target PID. Without the shadow, Relative ghosts visibly stick to the player's live vessel; with it, they play back at recorded world coordinates instead.
- Format-v7 enums: `LaunchToLaunchLoopIntervalFormatVersion=4`, `PredictedOrbitSegmentFormatVersion=5`, `RelativeLocalFrameFormatVersion=6`, `RelativeAbsoluteShadowFormatVersion=7=CurrentRecordingFormatVersion` — see `RecordingStore.cs:57-61`. New behaviour gated on a version comparison must use the named constants; raw integer comparisons rot when the next version lands.
- Legacy v5-and-older `ReferenceFrame.Relative` sections keep the older contract (no anchor-local offset, no absolute shadow) and must replay through the legacy path only; do not auto-reinterpret old RELATIVE payloads as v6/v7 anchor-local data.

**Krakensbane-corrected velocity**: `(Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity())`

**ConfigNode file I/O**: `ConfigNode.Save()` writes node CONTENTS only (values + children), NOT the node-name wrapper. `ConfigNode.Load()` returns a node already containing the file contents. Do NOT call `root.GetNode("Name")` after load. Use `FileIOUtils` for safe-write (.tmp + rename).

**InvariantCulture everywhere**: all float/double serialization uses `ToString("R", CultureInfo.InvariantCulture)`. UI formatting (`$"{val:F1}"`) also needs InvariantCulture — comma-locale systems produce broken output otherwise.

**Ghost event ↔ snapshot PID**: `VesselSnapshotBuilder.AddPart` assigns `persistentId = 100000 + idx*1111`. Single-part showcase ghosts must use PID `100000` for their events or playback lookup silently fails. Ghost part GameObjects are named by `persistentId` for O(1) lookup.

**Engine key encoding**: `(ulong)pid << 8 | (uint)moduleIndex` — up to 256 engine modules per part. RCS uses separate dicts (`activeRcsKeys`/`lastRcsThrottle`) so keys may overlap.

**Test working dir**: xUnit runs from `Source/Parsek.Tests/bin/Debug/net472/` — use 5 `..` segments to reach project root. Classes touching shared static state (`ParsekLog`, `RecordingStore`, `ParsekScenario.crewReplacements`) need `[Collection("Sequential")]` and the corresponding `ResetForTesting()` calls.

**Recording storage (format v3)**: bulk data lives in sidecar files under `saves/<save>/Parsek/Recordings/`: `<id>.prec` (trajectory), `<id>_vessel.craft`, `<id>_ghost.craft`, `<id>.pcrf` (ghost geometry). Rewind-to-Staging quicksaves live alongside at `saves/<save>/Parsek/RewindPoints/<rpId>.sfs` (KSP-format; written deferred-one-frame via `FileIOUtils.SafeMove` from the save root). Only lightweight metadata + mutable state stays in `.sfs`. `RecordingPaths.ValidateRecordingId` rejects path traversal and invalid filename chars.

**ERS / ELS routing**: any code reading `RecordingStore.CommittedRecordings` / `Ledger.Actions` must route through `EffectiveState.ComputeERS()` / `ComputeELS()` unless its file is in `scripts/ers-els-audit-allowlist.txt`. Grep gate `scripts/grep-audit-ers-els.ps1` runs in CI via `GrepAuditTests` and fails the build on any un-allowlisted raw read. Add a file-level `[ERS-exempt]` comment + one-line rationale in the allowlist when a new exemption is justified (physical-identity correlation, tombstone construction, etc.).

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
- `RecordingTree.cs` - tree save/load metadata and branch topology. Phase F removed the public tree resource delta fields; legacy `delta*` / `preTree*` / `resourcesApplied` keys are load-only via a transient residual seam, and `TreeFormatVersion` gates the new save shape.
- `ParsekUI.cs` - UI main window, map markers, and coordinator for extracted sub-windows
- `UI/RecordingsTableUI.cs` - recordings table window (sort, rename, group tree, chain blocks, loop period editing)
- `UI/SettingsWindowUI.cs` - settings window (recording, looping, ghost, diagnostics, sampling, data management)
- `UI/TestRunnerUI.cs` - in-game test runner window
- `UI/GroupPickerUI.cs` - group picker popup (recording/chain group assignment)
- `UI/SpawnControlUI.cs` - Real Spawn Control window (nearby vessel proximity spawning)
- `UI/GloopsRecorderUI.cs` - Gloops Flight Recorder window (manual ghost-only recording controls)
- `UI/KerbalsWindowUI.cs` - kerbal roster window (reserved crew, active stand-ins, retired stand-ins)
- `InGameTests/` - runtime test framework: `InGameTestAttribute` (discovery), `InGameAssert` (assertions), `InGameTestRunner` (execution + results export), `TestRunnerShortcut` (global Ctrl+Shift+T addon), `RuntimeTests` + `ExtendedRuntimeTests` (158 tests across 42 categories), `LogContractTests` (log format/level/resource validation migrated from post-hoc KSP.log checker)
- `SelectiveSpawnUI.cs` - pure static methods for Real Spawn Control (proximity candidates, countdown formatting)
- `ParsekScenario.cs` - ScenarioModule for save/load, coroutine hosting, scene transitions
- `CrewReservationManager.cs` - crew reservation lifecycle (reserve/unreserve/swap/clear)
- `GameActions/` - ledger-based game actions system (GameAction, Ledger, RecalculationEngine, 8 resource modules including KerbalsModule, KspStatePatcher, LedgerOrchestrator, GameStateEventConverter)
- `GroupHierarchyStore.cs` - UI recording group hierarchy and visibility state
- `FileIOUtils.cs` - shared safe-write (tmp+rename) utility for ConfigNode file I/O
- `SuppressionGuard.cs` - IDisposable guard struct for GameStateRecorder suppression flags
- `RecordingStore.cs` - static recording storage surviving scene changes
- `PatchedConicSnapshot.cs` - snapshots patched-conic coast chains into predicted `OrbitSegment` lists for scene-exit finalization
- `BallisticExtrapolator.cs` - extrapolates incomplete ballistic tails through atmosphere / terrain / SOI events to a terminal endpoint
- `IncompleteBallisticSceneExitFinalizer.cs` - scene-exit seam that snapshots, extrapolates, validates, and applies extended tail results to recordings
- `GhostVisualBuilder.cs` - ghost mesh building from vessel snapshots
- `TrajectoryMath.cs` - pure static math (sampling, interpolation, orbit search)
- `VesselSpawner.cs` - vessel spawn/recover/snapshot utilities, resource manifest extraction (`ExtractResourceManifest`)
- `ResourceManifest.cs` - `ResourceAmount` struct and `ComputeResourceDelta` for per-resource change computation (Phase 11)
- `MergeDialog.cs` - post-revert tree merge dialog (standalone/chain dialogs removed in T56)
- `GhostMapPresence.cs` - ProtoVessel lifecycle for ghost map presence: creates/destroys lightweight vessels for tracking station, orbit lines, targeting. Manages ghostMapVesselPids HashSet for O(1) guard checks across codebase.
- `ParsekHarmony.cs` + `Patches/` - Harmony patcher + patches (PhysicsFrame, GhostVesselLoad, GhostCommNetVessel, GhostTrackingStation, FacilityUpgrade, FlightResults, ScienceSubject, TechResearch, CrewDialogFilter, KerbalDismissal, GhostOrbitLine)
- `RewindInvoker.cs` - Rewind-to-Staging (v0.9) invocation orchestrator: five-precondition gate, pre-load reconciliation bundle capture, RP quicksave copy to save-root, post-load Restore + Strip + Activate + atomic provisional + `ReFlySessionMarker` write.
- `SupersedeCommit.cs` - re-fly merge tail: appends `RecordingSupersedeRelation` rows for the superseded subtree, flips MergeState (Immutable vs CommittedProvisional by `TerminalKindClassifier`), builds `LedgerTombstone`s for in-scope kerbal-death actions + bundled rep penalties, bumps ERS / ELS cache versions.
- `MergeJournalOrchestrator.cs` - drives the 14-step re-fly merge through five crash-recovery checkpoints (Supersede / Tombstone / Finalize / Durable1Done / RpReap); `RunFinisher` on OnLoad rolls back or drives to completion based on the persisted `MergeJournal.Phase`.
- `LoadTimeSweep.cs` - OnLoad sweep (between journal finisher and reaper) that validates the re-fly marker's six durable fields, discards zombie NotCommitted provisionals + session-provisional RPs, warn-logs orphan supersede/tombstone rows, and clears stray `SupersedeTargetId` fields.

## Worktree Workflow

**HARD RULE — never edit or commit inside `Parsek/` (the main checkout) without explicit per-session approval.** This applies to every change that will produce a commit — code, tests, CHANGELOG trims, todo edits, doc tweaks, anything. "It's just a one-line fix" is not an exception. Recovery if I slip: stash any unrelated WIP, `git worktree add` a new worktree at the tip containing the direct-edit commit, `git reset --hard` `Parsek/` back to the pre-direct-edit tip, `git merge --no-ff` the rescue branch back in, `git stash pop`. Never leave a direct-edit commit standing on `main` or a shared branch.

**`Parsek-<branch>/` sibling worktrees are fair game once opened — keep working in them directly.** If I'm already in a `Parsek-<branch>/` worktree for the current line of work (created earlier this session, or already had ongoing changes I committed), I can keep editing, committing, and pushing inside it for the rest of that line of work. Spinning up a fresh worktree per change is unnecessary ceremony — the merge-back step is also unneeded since the changes already live on the same branch. Only spawn a new worktree when starting a *new* line of work that should land on its own branch.

For manual worktrees (when not using `isolation=worktree`), create as sibling folders:
```bash
cd Parsek
git worktree add ../Parsek-<branch-name> -b <branch-name> <target>
```

Pick `<target>` carefully:
- Branching from `main` → use `origin/main` (local main may be ahead of remote from in-progress work).
- Branching from a feature branch that's about to be merged → compare `git log --oneline <local>..origin/<branch>` first. Use the local ref if it's ahead; use `origin/<branch>` if it's behind or matches.

When a fresh worktree was created off a parent feature branch and finishes its work: commit on the fix/chore branch → in the parent worktree, `git merge --no-ff <branch>` it back → leave the branch around unless the user asks to prune. This merge-back step does NOT apply when you stayed on the same branch the whole time inside one worktree (the changes are already there).

`Parsek.csproj` probes up to 5 parent levels for `Kerbal Space Program/`, so builds work from worktrees at this location.

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
4. Run `dotnet test` - all tests must pass

## Documentation Updates — Per Commit, Not Per PR

Before every commit that changes behavior (not just the first one in a PR), check whether these docs need updating and stage them in the same commit:

- `CHANGELOG.md` — add or update the entry under the current version. On follow-up commits that change the fix approach, edit the existing entry rather than leaving the original wording stale.
- `docs/dev/todo-and-known-bugs.md` — mark completed items as ~~done~~, add newly discovered items, and update the "Fix:" description on follow-up commits when the approach changes.
- This file (`.claude/CLAUDE.md`) — update only when file layout, build commands, workflow, or key patterns change.

**Follow-up commit trap:** When a review comment lands on an open PR and changes the fix approach, the CHANGELOG and todo entries written for the first commit become stale. The reviewer reads those docs as authoritative — they must match the code in the current HEAD. Before pushing the follow-up commit, re-read the existing doc entries for the bug/feature and update them to match the new approach.

**Practical check:** after `git add`, run `git diff --cached` and ask: "does any of this contradict or supersede existing wording in CHANGELOG.md or todo-and-known-bugs.md?" If yes, stage the doc updates in the same commit.

## Workflow

See `docs/dev/development-workflow.md` for the full feature development process (vision → scenarios → design doc → plan/build/review cycle).
