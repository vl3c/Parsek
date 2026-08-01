## Git Commits
- Do NOT add `Co-Authored-By` or any signature line to commit messages

## Build & Test

```bash
cd Source/Parsek && dotnet build          # builds + auto-copies to KSP GameData
cd Source/Parsek.Tests && dotnet test     # all unit tests (does NOT deploy to KSP)
dotnet test --filter InjectAllRecordings  # inject 8 synthetic recordings into test save
```

**KSP deploy is intentional-only:** the post-build copy to `GameData/Parsek/Plugins` runs ONLY when the build is started from the building checkout's own `Source/Parsek` directory, or with `-p:ForceKspDeploy=true`. This works from ANY worktree: `cd Parsek-<branch>/Source/Parsek && dotnet build` deploys that branch's DLL (testing unmerged branches is unchanged). What never deploys: `dotnet test` (builds Parsek via ProjectReference from the Tests dir), builds started from the repo root or elsewhere, and `release.py`; those print `KSP deploy skipped` instead. `-p:SkipKspDeploy=true` suppresses the deploy even from the project dir. Rationale: with multiple worktrees sharing one KSP install, every sibling test run used to clobber the deployed DLL with whatever branch ran tests last.

Post-build copy uses `ContinueOnError="true"` - builds succeed when KSP has DLL locked.

**The autotest harness runs a DIFFERENT KSP instance.** Harness flights do NOT use the dev instance at `Kerbal Space Program/`; they run the provisioned instance at `automation/stock-minimal/`, which has its own `GameData/Parsek/Plugins/Parsek.dll`. `cd Source/Parsek && dotnet build` deploys ONLY to the dev instance, so verifying that DLL proves nothing about a harness run. To get a C# change into a harness flight:

```bash
cd harness && python provision/provision.py --profile stock-minimal
```

Its DEPLOY phase copies this worktree's `Source/Parsek/bin/Debug/Parsek.dll` into the automation instance; then verify `automation/stock-minimal/GameData/Parsek/Plugins/Parsek.dll` (not the dev one) carries your new string. The assembly version string is identical across builds, so `Parsek' V<x.y.z>` in the log does NOT discriminate. Skipping this costs a full flight: a 2026-07-25 B12 run flew green but red'd on a required logContract token, which looked exactly like a real Parsek defect until the collected KSP.log showed every commit line present and zero occurrences of the new one.

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

From a manual worktree, set `KSPDIR` explicitly because the csproj's relative `Kerbal Space Program/` probe only walks parent directories of the csproj - a sibling-of-the-worktree layout at `C:/Users/vlad3/Documents/Code/Parsek/Kerbal Space Program/` is NOT reachable from `C:/Users/vlad3/Documents/Code/Parsek-<branch>/Source/Parsek/` via ancestor walking.

**If multiple worktrees exist**, any of them can overwrite the shared `GameData/Parsek/Plugins/Parsek.dll` via a direct `cd Source/Parsek && dotnet build` (test runs no longer deploy since the intentional-only deploy gate). The deployed file belongs to whichever worktree deployed most recently. Re-verify (hash-compare, not just mtime) right before every KSP launch if a sibling session is also active.

**Diagnosing which build produced a collected log.** A `collect-logs.py` snapshot can run a clobbered DLL from a *different* branch than you expect (sibling-worktree race). Two cross-checks: (1) read `git-state.txt` in the log folder for the branch/commit the collection captured, but note it reflects the directory the script ran from, not necessarily the deployed DLL; (2) grep `KSP.log` for feature-signature strings to confirm what code actually loaded (e.g. `RouteOriginProof` / `Route proof dock window` for logistics, `OnVesselsUndocking` vs `DeferredHandleTransientUndock` for the undock handler, `Parsek' V<x.y.z>` for the assembly version). If a log lacks the signatures of the feature you're investigating, that session ran the wrong DLL and proves nothing about your change.

## Release

```bash
python scripts/release.py    # build Release, run tests, package zip
```

Produces `Parsek-v{version}.zip` in repo root with `GameData/Parsek/` layout (DLL + version file + toolbar textures). Validates that `GameData/Parsek/Parsek.version` and `AssemblyInfo.cs` versions match before building.

## Multi-Agent Workflows & Token Discipline

Default to lean, targeted work. Favor the smallest set of agents that produces a correct answer. Do not fan out broadly or stack redundant verification passes unless the task genuinely needs it (large migration, repo-wide audit, multi-subsystem read). For ordinary tasks, work inline or use one or two direct agents, not a workflow.

- **Model routing:** use Fable for planning and reviews; use Opus for everything else (implementation, bulk reads, mechanical work). Hard caps: at most 2 Fable agents in parallel, at most 8 Opus agents in parallel.
- The Workflow tool's concurrent-agent cap is `min(16, cpu cores - 2)` per workflow and is NOT configurable: there is no env var, `settings.json` key, or CLI flag, and the feature request for one was closed as not planned. Do not try to set a max.
- To bound fan-out, shape the script: process items in batches of N via `parallel()` (each batch is a barrier, so at most N agents run at once). Do not spawn an unbounded fan-out and rely on the engine cap.
- Reserve heavy patterns (multi-vote, adversarial verify, loop-until-dry, large finder pools) for explicit "thorough"/"audit" requests. They multiply token cost; skip them on routine work.

## Investigating KSP Internals

When investigating KSP API behavior, search the web and read other open-source KSP mods (Trajectories, Principia, KSPCommunityFixes, VesselMover) for patterns and prior art.

## KSP API & Code Gotchas

**Enums / APIs**
- `GameScenes.TRACKSTATION` (not `TRACKINGSTATION`)
- `PopupDialog` / `MultiOptionDialog` live in `Assembly-CSharp` - no extra Unity module reference needed
- `ScenarioCreationOptions.AddToAllGames` for ScenarioModules that must exist in every save
- `FlightCamera.camPitch/camHdg` are **radians**, not degrees (stock defaults 0.2/0.3 = ~11.5°/~17°); pivot rotation is `frameOfReference * Yaw(camHdg) * Pitch(camPitch)`
- `VesselPrecalculate.vessel` is protected - compare with `__instance.gameObject != v.gameObject` instead
- `ModuleEngines.runningEffectName`/`directThrottleEffectName` not accessible at compile time - scan EFFECTS config instead
- `onPartJointBreak` signature: `(PartJoint joint, float breakForce)`
- **Docking-port undock event order** (decompiled `Part.Undock`, KSP 1.12.5): inside one `Part.Undock()` call KSP fires `onPartUndock(part)` FIRST (once, before the split, part still on the combined vessel), runs `attachJoint.DestroyJoint()` (fires the synchronous `onPartJointBreak`), creates the new vessel, then fires `onVesselsUndocking(oldVessel, newVessel)` LAST and **unconditionally on every path** with final PIDs. Subscribe to `onVesselsUndocking` for the authoritative undock split signal; never wait for a second `onPartUndock` (there is only one). Parsek handles this in `ParsekFlight.OnPartUndock` (snapshot + `pendingUndockRootPartSeed` only) -> `OnVesselsUndocking` -> `DeferredUndockBranch` -> `CreateSplitBranch`. See `docs/dev/dock-undock-recording-structure.md` §2.2.

**Part names**: KSP converts underscores to dots at runtime. cfg `name = solidBooster_v2` → runtime `solidBooster.v2`. Always use dot-form in `PartLoader.getPartInfoByName` and ghost snapshot part names.

**Rotation / world frame**: KSP uses two different rotation contracts here; do not mix them.
- Surface-relative capture: `srfRelRotation = Inverse(body.bodyTransform.rotation) * v.transform.rotation`
- Live `Transform.rotation` playback/ghost placement: `worldRot = body.bodyTransform.rotation * srfRelRotation`
- ProtoVessel snapshots: `VESSEL.rot` is parsed as `ProtoVessel.rotation` and `ProtoVessel.Load()` assigns it to `vesselRef.srfRelRotation`, so Parsek-authored ProtoVessel nodes must write the raw recorded `srfRelRotation`, not `body.bodyTransform.rotation * srfRelRotation`.
- Absolute / surface trajectory points store surface-relative rotation (`v.srfRelRotation`).
- `ReferenceFrame.Relative` track sections store anchor-local world rotation: `Inverse(anchor.rotation) * focusWorldRotation`; playback resolves with `anchor.rotation * localRot`.
- `ReferenceFrame.Relative` track sections store anchor-local Cartesian POSITION offset (metres) in `TrajectoryPoint.latitude`/`longitude`/`altitude`: `Inverse(anchor.rotation) * (focusWorldPos - anchorWorldPos)`. Recorder side: `FlightRecorder.ApplyRelativeOffset` -> `TrajectoryMath.ComputeRelativeLocalOffset`. Playback side: `TrajectoryMath.ApplyRelativeLocalOffset` and `ParsekFlight.TryResolveRelativeOffsetWorldPosition`. The field NAMES are misleading: in RELATIVE sections those are NOT body-fixed lat/lon/alt (values commonly fall outside `[-90,90]` / `[-180,180]`; they are metres along the anchor's local axes). Any code path that reads `point.latitude/longitude/altitude` from a flat `Recording.Points` list MUST first resolve `TrackSection.referenceFrame` for that UT and dispatch through `TryResolveRelativeWorldPosition` when the section is RELATIVE; calling `body.GetWorldSurfacePosition(lat, lon, alt)` directly on a RELATIVE-frame point silently produces a position deep inside the planet. There is exactly one RELATIVE contract (the legacy world-offset path is gone).

**Recording schema**: the current contract is `RecordingStore.CurrentRecordingFormatVersion = 1` with `RecordingStore.CurrentRecordingSchemaGeneration = 4` - one constant, no per-generation named constants. Recordings and sidecars carrying a different format or older/newer generation are rejected on load through `RecordingStore.IsRecordingSchemaCompatible` (reasons: `generation-missing`, `generation-older`, `generation-newer`, `format-version-mismatch`). Treat the format as one current contract; do NOT add migration or compatibility paths for pre-bump recordings.

- **`TrackSection.anchorRecordingId`** carries the anchor recording id for non-loop Relative sections; non-loop flight, map, and KSC playback resolve through recorded anchor trajectories. Loop Relative playback stays on the live-PID contract via `Recording.LoopAnchorVesselId` plus explicit loop-only gates.
- **`Recording.ParentAnchorRecordingId`** is set on parent-anchored recordings to the parent recording's id; null on top-level recordings. Two populations carry it: genuine debris (`IsDebris=true`) and controlled-decoupled children (`IsDebris=false`; probes/landers/capsules that come off a parent through a decoupler). `IsDebris` and `ParentAnchorRecordingId != null` are orthogonal on disk; there is no per-cell generation constant (`ControlledChildParentAnchorSchemaGeneration` is not a code symbol).
- **Parent-anchored contract:** any recording with `ParentAnchorRecordingId != null` records two surfaces while close to its parent: `TrackSection.bodyFixedFrames` (full `TrajectoryPoint`s in body-fixed form: lat/lon/alt + `srfRelRotation` + velocity + body + altitude) is the primary playback surface, and `TrackSection.frames` (anchor-local metre offsets + anchor-local rotation) is the secondary surface for loop-anchored chains and diagnostics. Ordinary parent-anchored recordings render through `bodyFixedFrames` first. Loop-anchored debris chains try the live loop-relative path first and fall back to `bodyFixedFrames` only when the loop anchor cannot resolve. Body-fixed primary playback requires at least two samples and a playback UT inside the actual `bodyFixedFrames` endpoint range; never clamp a single or out-of-range body-fixed sample into a stale ghost. Controlled-decoupled children record post-hysteresis-exit Absolute tails indefinitely; their post-window playback dispatches through the standard Absolute path via per-section routing.
- Parent-anchored debris Relative `TrackSection` metadata is not proof of renderable coverage. The authored surface used by the chosen path must cover the playback UT; otherwise playback retires the debris instead of clamping to stale child offsets or falling through to orbit / flat point tails.
- Recorder persistence follows the parent-anchored debris-frame invariant: a parent-anchored Relative section must not outlive recorder-persistable authored coverage (`section.frames`, two-point `bodyFixedFrames`, or non-predicted checkpoints). A single Relative frame persists only its own UT, flat `Recording.Points` / boundary samples do not extend a parent-anchored Relative tail, and a later real non-relative section or accepted orbit tail is required to extend the recording past the Relative payload.

**Krakensbane-corrected velocity**: `(Vector3)(v.rb_velocityD + Krakensbane.GetFrameVelocity())`

**ConfigNode file I/O**: `ConfigNode.Save()` writes node CONTENTS only (values + children), NOT the node-name wrapper. `ConfigNode.Load()` returns a node already containing the file contents. Do NOT call `root.GetNode("Name")` after load. Use `FileIOUtils` for safe-write (.tmp + rename).

**InvariantCulture everywhere**: all float/double serialization uses `ToString("R", CultureInfo.InvariantCulture)`. UI formatting (`$"{val:F1}"`) also needs InvariantCulture - comma-locale systems produce broken output otherwise.

**Ghost event ↔ snapshot PID**: `VesselSnapshotBuilder.AddPart` assigns `persistentId = 100000 + idx*1111`. Single-part showcase ghosts must use PID `100000` for their events or playback lookup silently fails. Ghost part GameObjects are named by `persistentId` for O(1) lookup.

**`persistentId` is craft-baked, NOT launch-unique**: KSP bakes `persistentId` into the `.craft` file and reuses it verbatim on every launch of that craft (vessel pid AND part pids), regenerating only on collision with a *currently-live* vessel. Parsek keeps historical recordings of the same craft carrying the baked pid, invisible to KSP's live-dedup, so a fresh launch's pid collides with prior recordings of the same craft. NEVER trust a bare `persistentId` match (stored-vs-live, or recording-vs-recording) as proof of "same physical object / same launch". The launch-unique discriminator is KSP's `Vessel.id` (a Guid, assigned fresh per launch, not stored in the `.craft`), captured as `Recording.RecordedVesselGuid` and compared via `VesselLaunchIdentity` (`LiveVesselIsRecordedLaunch` / `RecordingsShareLaunch`): a pid match is an identity match only when the guid does not conclusively differ; an unknown guid falls back to pid-only. Adoption-stamp sites (`SpawnedVesselPersistentId == VesselPersistentId`) collide and must be guid-gated; genuine Parsek spawns use a KSP-unique spawn pid and stay pid-only. Chain-continuation segments of one launch share a guid; distinct launches differ. The fresh-rollout pid (`RecordingStore.SceneEntryFreshRolloutVesselPid`) is the cheap in-flight fast path for recordings still lacking a guid.

**Engine key encoding**: `(ulong)pid << 8 | (uint)moduleIndex` - up to 256 engine modules per part. RCS uses separate dicts (`activeRcsKeys`/`lastRcsThrottle`) so keys may overlap.

**Test working dir**: xUnit runs from `Source/Parsek.Tests/bin/Debug/net472/` - use 5 `..` segments to reach project root. Classes touching shared static state (`ParsekLog`, `RecordingStore`, `ParsekScenario.crewReplacements`) need `[Collection("Sequential")]` and the corresponding `ResetForTesting()` calls.

**Recording storage (sidecar layout)**: bulk data lives in sidecar files under `saves/<save>/Parsek/Recordings/`: `<id>.prec` (trajectory), `<id>_vessel.craft`, `<id>_ghost.craft`, `<id>.pcrf` (ghost geometry). Rewind-to-Separation quicksaves live alongside at `saves/<save>/Parsek/RewindPoints/<rpId>.sfs` (KSP-format; written deferred-one-frame via `FileIOUtils.SafeMove` from the save root). Only lightweight metadata + mutable state stays in `.sfs`. `RecordingPaths.ValidateRecordingId` rejects path traversal and invalid filename chars.

**On-rails BG vessels emit no env-classified per-frame TrackSections**: `BackgroundOnRailsState` (`BackgroundRecorder.cs`) deliberately omits `currentTrackSection` / `trackSections` / `environmentHysteresis`, and `OnBackgroundPhysicsFrame` early-returns on `bgVessel.packed`. Packed/on-rails closes may emit `OrbitalCheckpoint`/`Checkpoint` sections that wrap closed `OrbitSegment`s, but they are orbit-only bridges, not per-frame Atmospheric/ExoBallistic classifications. An eccentric BG-recorded orbit grazing atmosphere across N orbits cannot generate optimizer-splittable Atmospheric<->ExoBallistic toggles. Don't add a TrackSection field or environment hysteresis to the on-rails state, and don't move env-classification ahead of the packed/isOnRails gates without re-reading `docs/dev/research/extending-rewind-to-stable-leaves.md` §S16. Guarded by `EccentricOrbitOptimizerInvariantTests`.

**Optimizer split predicate (§3 ordering)**: `RecordingOptimizer.IsSplittableEnvOrBodyBoundary` walks the boundary classification top-down: (1) seam short-circuit on `TrackSection.isBoundarySeam` - hard "always wins" override; (2) not-a-boundary skip; (3) same-class ExoBallistic body change kept cohesive for transfer coasts, with UI labels showing the body path; (4) other body changes (#251), including ExoPropulsive SOI boundaries; (5) Surface (class 2) default split, except brief Atmospheric/Approach runs bracketed by Surface on both sides suppress as surface grazes; (6) ExoPropulsive at the crossing; (7) persistence predicate (`IsGrazePattern` collapse-walk on `SplitEnvironmentClass` runs, suppressing brief bracketed runs < `BriefSectionMaxSeconds = 120s`). Producer-emitted recorder bookkeeping artifacts (e.g. `BackgroundRecorder.FlushLoadedStateForOnRailsTransition`) carry `isBoundarySeam=true`; future producers should set the same flag, NOT replicate the persistence predicate at producer level. See `docs/dev/done/plans/optimizer-persistence-split.md` (rationale) and `docs/dev/research/optimizer-meaningful-split-rule.md` (historical dead end).

**ERS / ELS routing**: any code reading `RecordingStore.CommittedRecordings` / `Ledger.Actions` must route through `EffectiveState.ComputeERS()` / `ComputeELS()` unless its file is in `scripts/ers-els-audit-allowlist.txt`. Grep gate `scripts/grep-audit-ers-els.ps1` runs in CI via `GrepAuditTests` and fails the build on any un-allowlisted raw read. Add a file-level `[ERS-exempt]` comment + one-line rationale in the allowlist when a new exemption is justified (physical-identity correlation, tombstone construction, etc.).

## Project Layout

```
Source/Parsek/              # Mod source (SDK-style .csproj)
Source/Parsek/InGameTests/  # Runtime test framework (runs inside KSP via Ctrl+Shift+T)
Source/Parsek.Tests/        # xUnit tests + Generators/ (RecordingBuilder, VesselSnapshotBuilder, ScenarioWriter)
harness/                    # Automated-testing harness (Python, stdlib-only; also fixtures/, tools/, status.py, warp_audit.py)
docs/                       # Design docs, roadmap, reference analyses
../Kerbal Space Program/    # Local dev KSP instance (UMBRELLA root, outside git; auto-deploy target)
../automation/              # Provisioned harness KSP instances (stock-minimal; umbrella root, outside git)
```

**harness/ (Python automated-testing pipeline).** Pure-decision-library + thin-I/O-shell split, mirrored across three modules; stdlib only, no third-party deps on the base interpreter. Run its tests: `cd harness && python -m unittest discover -s lib -q`, then `discover -s provision -q` and `discover -s missions/lib -q`.

- `harness/lib/hlib.py` (pure M-A5 decisions) + `harness/run.py` (thin I/O orchestrator: admit -> stage -> launch -> drive seam -> verifier chain -> classify -> collect-logs -> result JSON). `harness/lib/_fake_ksp.py` + `test_run_smoke.py` drive full runs with no real game.
- `harness/scenarios/*.toml` - declarative scenario specs; `harness/coverage/registry.toml` - the committed D1-D18 dimension value set. Results/coverage/flake outputs are generated and gitignored.
- `harness/provision/` - the M-A6 stack provisioner (`provlib.py` pure + `provision.py` shell + `pins.toml` / `profiles/*.toml`); live CLONE/BUILD-TT/INSTALL/VERIFY/DEPLOY phases plus `--repair`. DEPLOY requires this worktree's own `Source/Parsek/bin/Debug/Parsek.dll` (or `--parsek-dll`) or it aborts EC-9.
- `harness/missions/` - the M-B1 mission library: `lib/mlib.py` (pure mission decisions) + `mission_runner.py` (injectable MissionControl seam over kRPC) + mission shells + schema TOMLs + `bootstrap_venv.py` (gitignored `.venv/`; krpc==0.5.4).
- `harness/lib/oracle.py` - the M-B2 pure ledger oracle (expected-vs-parsed career diff; hard drift classifies `PARSEK-FAIL(ledger)`).
- `harness/lib/saveparse.py` - the M-C2 pure save-structure parser + evaluator behind the `saveParse` verifier row (R9): parses the produced save's ParsekScenario surfaces (RECORDING_TREE topology, supersede rows, tombstones, rewind retirements, REWIND_POINTS/slots) and evaluates `[expectations.rewind]` / `[expectations.recordings.structure]`. REPORT-ONLY unless a block declares `gating = true`; exactly ONE committed spec does (`S4.1-rewind-merge`, armed 2026-07-31), guarded by an allowlist cell. Arming is a per-scenario operator decision taken only after a report-only reading run whose facets match the declared windows.

Harness traps that bite C# work:
- **Two hlib test cells read OUTSIDE `harness/`:** `CommittedBatchTallySourceSyncTests` walks `Source/Parsek` to keep each spec's pinned `BATCH_COMPLETE v1 total=N ... skipped=S` tally in step with the C# `[InGameTest]` attributes it counts - adding an in-game test to `Missions`, `Periodicity`, `GameActionsHealth`, `RouteRewindTimeline`, `RecordingInvariants` or `GhostPlayback` reds locally instead of on the next nightly, and an attribute spelling the parse does not model also reds. `test_doc_spec_sync.py` reads `docs/dev`.
- **Mission-vs-Parsek orthogonality:** a mission that did not fly is driver-INVALID, never PARSEK-FAIL. Post-mission RECORDING seam steps are non-gating on a MISSION-OK run (a mis-recorded good flight reds through the verifier chain); post-mission OUTCOME steps DO gate as `PARSEK-FAIL(mission-outcome)` via the `missionOutcome` verifier row (the M-C2 EVA verbs; a driver-INVALID would discard evidence and retry an intermittent subject death into a PASS). A HANDOFF mission declares what it did not verify via `mlib.MISSION_HANDOFF_CONTRACTS`.
- Two dev-script seams the harness passes (additive, inert by default): `scripts/analyze-recordings.ps1 -FreshSaveGate` and `scripts/validate-ksp-log.ps1 -KilledRun` / `-NoRecordingRun` (the C# checker's `ParseSuppressionList` rejects suppressing FMT/WRN - the cannot-mask guarantee).

Design authorities (binding): `docs/dev/design-autotest-harness-core.md` (M-A5), `design-autotest-command-seam.md` (M-A2), `design-autotest-autorun-hooks.md` (M-A3), `design-autotest-offline-analyzer.md` / `design-autotest-findings-baseline.md` (M-A1), `design-autotest-stack-setup.md` (M-A6), `design-autotest-mission-library.md` (M-B1), `design-autotest-ledger-oracle.md` (M-B2). Module layout details: `harness/README.md`. Status authority: `docs/dev/autotest-status.md`.

Key source files and what they do - read the relevant one before modifying:
- `ParsekFlight.cs` - flight-scene controller (policy, recording, chain management, input). Camera follow delegated to WatchModeController. `TryConsumeStockActionIntent` runs from `OnVesselSwitchComplete` (Map Switch-To) and `OnFlightReady` (TS Fly / KSC marker Fly) to validate an armed `StockActionIntentMarker`, pick one of three branches (committed-tree clone, BG-member continuation, standalone), mutate `activeTree` via `SwitchSegmentBuilder`, arm a fresh `SwitchSegmentSession`, clear the consumed marker, and disarm the first-modification watcher.
- `WatchModeController.cs` - camera-follow / watch-mode state machine (enter/exit watch, camera anchoring, overlap retarget, explosion hold)
- `GhostPlaybackEngine.cs` - ghost playback mechanics engine: owns ghostStates, per-frame positioning, loop/overlap playback, zone transitions, soft caps, reentry FX. Zero Recording references - accesses trajectories via IPlaybackTrajectory only. Future standalone mod core.
- `ParsekPlaybackPolicy.cs` - event subscriber reacting to engine lifecycle events (spawn decisions, resource deltas, camera management, deferred spawn queue)
- `IPlaybackTrajectory.cs` - interface exposing 27 trajectory/visual/orbital fields from Recording to the engine
- `IGhostPositioner.cs` - 8 positioning methods implemented by ParsekFlight, delegates world-space placement to the host scene
- `GhostPlaybackEvents.cs` - lifecycle event types (PlaybackCompleted, LoopRestarted, OverlapExpired, CameraAction), TrajectoryPlaybackFlags, FrameContext
- `ChainSegmentManager.cs` - chain segment state (active chain ID, continuation tracking, boundary anchors). Owns 16 fields previously scattered across ParsekFlight.
- `FlightRecorder.cs` - recording state + sampling (called by Harmony patch). Always-tree mode: every recording gets a RecordingTree (#271). `DecideOnVesselSwitch` has no Stop decision.
- `RecordingTree.cs` - tree save/load metadata and branch topology
- `ParsekUI.cs` - UI main window, map markers, and coordinator for extracted sub-windows
- `UI/RecordingsTableUI.cs` - recordings table window (sort, rename, group tree, chain blocks, loop period editing)
- `UI/SettingsWindowUI.cs` - settings window (recording, looping, ghost, diagnostics, sampling, data management)
- `UI/TestRunnerUI.cs` - in-game test runner window
- `UI/GroupPickerUI.cs` - group picker popup (recording/chain group assignment)
- `UI/SpawnControlUI.cs` - Real Spawn Control window (nearby vessel proximity spawning)
- `UI/GloopsRecorderUI.cs` - Gloops Flight Recorder window (manual ghost-only recording controls)
- `UI/KerbalsWindowUI.cs` - kerbal roster window (reserved crew, active stand-ins, retired stand-ins)
- `InGameTests/` - runtime test framework: `InGameTestAttribute` (discovery), `InGameAssert` (assertions), `InGameTestRunner` (execution + results export), `TestRunnerShortcut` (global Ctrl+Shift+T addon), `RuntimeTests` + `ExtendedRuntimeTests`, `LogContractTests` (log format/level/resource validation). Discovery reflects over the WHOLE assembly (500+ `[InGameTest]` declarations across 90+ categories; count them mechanically via `hlib.parse_ingame_test_declarations`, never by hand); standalone test files are auto-discovered too (e.g. `ContractTombstonesAcrossSupersedeTest`, `LedgerGroundTruthHarness`). `InGameFixtureMath` holds the pure fixture math a FLIGHT test needs to size itself against whatever vessel the batch is flying: `SceneFloatGridToleranceMeters` / `ToleranceResolvesSignal` for world-position assertions (KSP's floating origin tracks the active vessel, so a `Vector3`'s float grid step is `magnitude * 2^-23` - a fixed millimetre epsilon is a bet on a landed craft, not a tolerance) plus walkback trajectory sizing. A FLIGHT test that cannot self-set-up must `InGameAssert.Skip` naming the required context, never assert against an assumed one.
- `Analyzer/` (namespace `Parsek.Analyzer`, `Parsek.Analyzer.Rules`) - the offline recording-analyzer CORE (model types, `InvariantRegistry`, pure rule evaluators) lives in `Parsek.dll` so the in-game `RecordingInvariants` (H5) category reuses the exact rules the offline analyzer runs; the ~27 analyzer test files stay in `Parsek.Tests`.
- **Per-save findings baseline** (design `docs/dev/design-autotest-findings-baseline.md`): reporting-layer filter accepting already-known findings so a gated run reds only on NEW ones. Pure core `BaselineFilter` / `BaselineTypes` in `Source/Parsek/Analyzer/`; codec + `-WriteBaseline` builder in `Source/Parsek.Tests/Analyzer/` (baseline at `<save>/analysis/baseline.cfg`). The `.analysis.txt` header's terminal `RED=<0|1>` token is the SINGLE gate source (`analyze-recordings.ps1 -FailOnRed` reads it, never recomputes). Modes: `Forbid` (baseline-presence-is-FAIL, the harness fresh-save guard), `Apply`/`Ignore` via `PARSEK_ANALYZER_BASELINE_MODE` + `-UseBaseline`. `AnalyzerVersion` is currently `3`.
- **Automation env hooks (M-A3, inert by default):** three launch-time env vars, read ONCE at addon Awake: `PARSEK_AUTORUN_TESTS` (auto-fire an in-game batch after the scene settles), `PARSEK_AUTORUN_EXIT=1` (quit KSP after the batch), `PARSEK_TEST_COMMANDS=1` (arm the M-A2 command-seam addon). Pure decision core in `InGameTests/AutorunHooks.cs`. Unset = zero per-frame work, nothing written to any save. Design: `docs/dev/design-autotest-autorun-hooks.md`.
- `TestBatchMarker.cs` - serializable `PARSEK_TEST_BATCH_MARKER` singleton written into `persistent.sfs` at batch start. Campaign-isolation contract: `InGameTestRunner.CaptureBatchBaseline` writes the clean `.bak` BEFORE the marker (per-scene mode via `ClassifyBatchIsolationMode`); clean teardown / cancel revert from the `.bak`; a mid-batch crash is caught on the next `ParsekScenario.OnLoad` in a DIFFERENT process by `RunTestBatchCrashReconcile` (revert `.bak` + sweep + deferred REAL `GamePersistence.LoadGame` - NEVER `SaveGame` from inside OnLoad).
- `InGameTests/LedgerGroundTruthHarness.cs` - in-game `LedgerGroundTruth` category test (Layer B of the ledger ground-truth harness): quicksaves the live career, parses it independently, runs `LedgerOrchestrator.RecalculateAndPatch()`, and diffs the reconstruction against the parsed save. NON-circular (recalc-output vs KSP's own on-disk save). HARD-asserts the seeded pools (funds/science/rep) + guid-corroborated vessel-recovery consistency; per-identity facets + phantoms are report-only by default. Design: `docs/dev/design-ledger-groundtruth-harness.md`.
- `LedgerGroundTruth.cs` / `CareerSaveParser.cs` / `LedgerGroundTruthDiff.cs` - pure, headless-testable Layer A: independent `.sfs` GAME-node parse + per-facet diff with the report-only-vs-hard policy. Unit-tested in `LedgerGroundTruth{Parser,Diff}Tests.cs`.
- `SelectiveSpawnUI.cs` - pure static methods for Real Spawn Control (proximity candidates, countdown formatting)
- `ParsekScenario.cs` - ScenarioModule for save/load, coroutine hosting, scene transitions
- `CrewReservationManager.cs` - crew reservation lifecycle (reserve/unreserve/swap/clear)
- `GameActions/` - ledger-based game actions system (GameAction, Ledger, RecalculationEngine, 9 resource modules including RouteModule, KspStatePatcher, LedgerOrchestrator, GameStateEventConverter; the ninth module, `KerbalsModule.cs`, lives at `Source/Parsek/` root)
- `GroupHierarchyStore.cs` - UI recording group hierarchy and visibility state
- `RecordingGroupStore.cs` - recording group membership/orchestration helpers (auto-generated tree groups, group mutations, in-memory mirror of `Recording.AutoAssignedStandaloneGroupName`)
- `MissionGroupLink.cs` - keeps a tree's main mission name (Missions tab) and its root group name (Recordings tab) in sync: `RenameMissionGroup` atomically renames the root group + auto `/ Debris` + `/ Crew` subgroups + the main `Mission.Name` (reject-both on a group-name collision); both UI rename commit paths route through it. `MissionStore.EnsureDefaultsForTrees` seeds the default mission name from `AutoGeneratedRootGroupName`.
- `FileIOUtils.cs` - shared safe-write (tmp+rename) utility for ConfigNode file I/O, plus `CopyDirectory`
- `PreParsekBackup.cs` - one-time pre-Parsek safety backup: on the first cold `ParsekScenario.OnLoad` of a save with no Parsek footprint, stage-copies persistent.sfs (+ loadmeta + Ships/Subassemblies) into a staging dir in the save folder (NOT under `Parsek/`, so a failed copy leaves no false footprint), then atomic-moves it to a sibling `saves/<Name> (pre-Parsek <ts>)/` visible in the Load menu. Fail-open (backs up on any parse doubt), fail-loud (Error + on-screen warn, retry next cold load). Gated by the `autoBackupExistingSaves` setting.
- `SuppressionGuard.cs` - IDisposable guard struct for GameStateRecorder suppression flags
- `RecordingStore.cs` - static recording storage surviving scene changes; delegates group orchestration to RecordingGroupStore
- `PatchedConicSnapshot.cs` - snapshots patched-conic coast chains into predicted `OrbitSegment` lists for scene-exit finalization
- `BallisticExtrapolator.cs` - extrapolates incomplete ballistic tails through atmosphere / terrain / SOI events to a terminal endpoint
- `IncompleteBallisticSceneExitFinalizer.cs` - scene-exit seam that snapshots, extrapolates, validates, and applies extended tail results to recordings
- `GhostVisualBuilder.cs` - ghost mesh building from vessel snapshots
- `GhostFxEmissionProbe.cs` - one-shot diagnostic MonoBehaviour on every cloned ghost engine FX instance: samples LIVE particles and logs the MEASURED mean particle velocity direction (`[FxEmissionProbe] measured:` lines). Mechanism-agnostic ground truth for FX orientation - transform-axis assumptions were proven unreliable for smokeTrail prefabs. Showroom fixtures stand upright, so angleFromDown ~0 = correct, ~180 = inverted.
- `GhostFxFingerprint.cs` - canonical per-part ghost FX fingerprints (`[FxFingerprint]` Verbose lines after every engine/RCS FX build, stock AND Waterfall installs) for mechanical A/B parity diffs; paired with the in-game `AllEnginesPristineFxResolveExactly` parity sweep (WaterfallCompat category).
- `WaterfallCompat.cs` - per-part gate for the Waterfall pristine-config ghost FX fallback (name check, no compile-time Waterfall reference). Gate closed = stock installs behavior-identical.
- `PristinePartFxResolver.cs` - recovers a part's pre-ModuleManager EFFECTS node, per-ordinal engine/RCS effect names, and legacy `fx_*` keys from the pristine on-disk .cfg (MM patches GameDatabase in memory only; disk PART names matched via `diskName.Replace('_','.')`). Consumed by `EngineFxBuilder.TryApplyPristineEngineFxFallback` and `GhostVisualBuilder.TryApplyPristineRcsFxFallback` when Waterfall config packs (SWE) delete the stock particle definitions. Legacy `fx_*` names resolve through KSP's builtin `Effects/{name}` Resources path so exact size-variant flames win; NEVER add `Effects/` to the shared `TryResolveFxPrefabExact` probes (stock paths rely on the deliberate `fxPrefabFallbacks` substitutions). A wanted flame that never resolves gets the white-flame fallback. NOTE: stock Swivel/Poodle/Mainsail are LEGACY parts (plain ModuleEngines + top-level `fx_*` keys, NO EFFECTS node); Spark/RAPIER have real pristine EFFECTS particles.
- `ReStockPatchFxIndex.cs` - lazy per-session index of the EFFECTS definitions ReStock authors for stock parts, parsed from ReStock's MM patch FILES on disk (all three roots: Patches + PatchesMH + PatchesLegacy; MM strips patch nodes from GameDatabase post-patch). Patch node names matched by prefix; the part target is the token between the FIRST `[` and `]` and the wildcard skip checks THAT TOKEN ONLY. Fresh `EFFECTS` matched by EXACT name (never `!EFFECTS`); per-ordinal names read through MM value prefixes (`%`/`@`/`&` accepted, `!`/`-`/`#` skipped). Absent directory = permanently empty index = stock-install no-op. Consumers: `EngineFxBuilder.TryScanReStockEffectsEntries`, `GhostVisualBuilder.TryApplyPristineRcsFxFallback`, and `HasAuthoredEffectsFor`, which stands down the hardcoded stock per-part FX tunings whenever ReStock authored the part's EFFECTS (ReStock-presence gated, NOT Waterfall-gated).
- `Display/GhostTrajectoryPolylineRenderer.cs` - map-view non-orbital ghost trajectory polyline (data structs + pure builder + cache + DDOL Driver MonoBehaviour walking `RecordingStore.CommittedRecordings` for atmospheric / non-orbital phases; `[ERS-exempt]`, always on). Current contract after the Director cutover: the Director pipeline renders unconditionally (no setting; grep gates `grep-audit-map-render-director-drive.ps1` / `grep-audit-map-render-phase-spine-drive.ps1` / `grep-audit-active-leg-recordings.ps1` enforce the removed flags stay removed). A Director-owned TracedPath leg draws via `TracedPathTreatment.TryDrawOwnedLeg`; the Driver walk is RETAINED as the fenced single draw host - the only renderer for proto-less pid-0 recordings, StockConic Driver-direct bridge legs, the boundary-overlap secondary, and the forward legs/arcs/bridges (its I1 deorbit clock consumed exclusively through `CrossMemberSeamStitcher.TryResolveTransferDeorbitTailHead`; file gate `PolylineDriverWalkDeleteGateTests`). Ownership: `drewNonOrbitalLegRecordings` is the SOLE ownership source, published ONLY on an ACTUAL draw (either the owned or the Driver-direct path - the draw, not the TracedPath/StockConic classification, decides whether the proto line must hide); `IsRenderingNonOrbitalLeg` resolves membership via the pure `ResolveNonOrbitalLegOwnership`. Per-leg head-UT gate + contiguous-span merge; `[DefaultExecutionOrder(-50)]` so the publish precedes the orbit-patch read. The icon floor + `ghostsWithSuppressedIcon` + `IsIconSuppressed` are a KEPT PERMANENT fallback - the ONLY marker signal for below-atmosphere descent, off-arc / window-clamp, and no-bounds (loiter / terminal / atmospheric) ghosts; do not delete them. The S0 polyline-coverage instrument (`unaccounted-drawn-recording` / `AssertDrawnRecordingsAccounted`) stays as the cheap coverage proof. Tier-C `rigid-seam-tangent-discontinuity` raises at the owned descent draw (tracing-gated, once-per-onset).
- `TrajectoryMath.cs` - pure static math (sampling, interpolation, orbit search)
- `VesselSpawner.cs` - vessel spawn/recover/snapshot utilities, resource manifest extraction (`ExtractResourceManifest`)
- `ResourceManifest.cs` - `ResourceAmount` struct and `ComputeResourceDelta` for per-resource change computation
- `MergeDialog.cs` - post-revert tree merge dialog. Hosts the switch-segment scoped Discard hook (`MergeDiscardRanToCompletion` calls `RecordingStore.TryDiscardActiveSwitchSegmentAttempt` before the whole-pending-tree fallback; scoped Discard sweeps the topological subtree rooted at the segment recording, catching in-segment debris). `BuildWholeTreeMergeDialogBody` renders the unified `"{TreeName} - {Duration}"` body for both switch-segment and whole-tree merges; the duration line is the load-bearing distinguisher. `MergeCommit` clears the `SwitchSegmentSession` marker after a successful commit. `ShowPreSwitchDecisionDialog` spawns the rapid-switch Merge / Discard dialog before stock map focus runs; the patch's button handlers commit-in-flight or scoped-discard the prior session, then arm a fresh intent + call `FlightGlobals.SetActiveVessel`.
- `GhostMapPresence.cs` - ProtoVessel lifecycle for ghost map presence: creates/destroys lightweight vessels for tracking station, orbit lines, targeting; manages the `ghostMapVesselPids` HashSet for O(1) guard checks. Marker-draw authority: both marker call sites (`ParsekUI.DrawMapMarkers` flight-map, `ParsekTrackingStation.ClassifyAtmosphericMarkerSkip` TS) route the "draw our non-proto marker?" decision through `ShouldDrawNonProtoMarkerForGhost(pid)` (pure core `ResolveMarkerDrawDecision`): `IsTracedPathOwnedThisFrame || IsPolylineOwningGhostPhase || IsIconSuppressed`. The decision is a SUPERSET of the line-hide, so no marker gap; the line Postfix sets `drawIcons=NONE` whenever the TracedPath owns, so no double marker. Marker rides the line via `GhostTrajectoryPolylineRenderer.TryAnchorMarkerToPolyline` when a leg drew, else the trajectory head. The `IsIconSuppressed` / `ghostsWithSuppressedIcon` disjunct is the KEPT PERMANENT no-conic / suppressed-icon fallback (see polyline entry).
- `MapRenderTrace.cs` - gated map/TS ghost render observability (sibling of flight-scene `GhostRenderTrace.cs`); off by default behind the `mapRenderTracing` setting. Tier-A structural events (`EmitStructural`: GhostCreated/Destroyed/FirstPosition -> Info), Tier-B change-based truth (`EmitOnChange` -> Verbose; routes straight to `Verbose` with caller-owned change detection, NOT through `ParsekLog.VerboseOnChange`, whose identity dict is not cleared on scene switch and would drop the first post-re-entry transition), Tier-C anomalies (`EmitAnomaly`; pure Unity-ECall-free predicates `IsIconJump` / `IsLineBlink`). `RecordLineIntent` + `ReconcileLineState` / `ReconcilePolylineOverlap` reconcile intended-vs-actual line/icon state (`decision-vs-truth` / `polyline-orbit-overlap`). Every line carries `pid=` + `recId=`. Formatters are SELF-CONTAINED (duplicated from `GhostRenderTrace`; do NOT refactor a shared formatter out or touch `GhostRenderTrace.cs`). Design: `docs/dev/design-map-ts-render-tracer.md`.
- `MapRenderProbe.cs` - end-of-frame truth probe for the map/TS render path (`[DefaultExecutionOrder(10000)]` DDOL addon, FLIGHT/TRACKSTATION + `ghostMapVesselPids` gated, on only when `MapRenderTrace.IsEnabled`). Reads renderer/line/orbit truth per tracked ghost and emits Tier-B truth + Tier-C `icon-jump` / `line-blink` anomalies. The `icon-jump` delta is measured in the orbit's OWN reference-body frame (`GetWorldPos3D - referenceBody.position`), NOT raw world: KSP builds an on-rails position as `referenceBody.position + orbitRelative`, so the body-relative delta is the actual orbital arc; comparing a raw-world delta against body-centered orbital speed false-positives smooth fast coasts at high warp (the body's own world motion dominates). SOI-crossing frames suppressed via `bodyChanged`. Also emits the Tier-A `FirstPosition` event per pid and reconciles intended-vs-actual line/icon state each `Sample`.
- `LedgerTrace.cs` - gated, EVENT-DRIVEN observability for the ledger apply boundary; off by default behind `ledgerTracing`. NO per-frame probe, no window registry - the read-back reconcile runs synchronously inside each `KspStatePatcher` Patch*. Tier-A: one grep-stable `phase=Structural ...` line per recalc (emitted ONCE from `LedgerOrchestrator.ApplyRecalculatedStateToKsp` after `PatchAll`, never inside a Patch*). Tier-B: per-identity change lines reusing the patch-site changed-sets. Tier-C: read-back `ledger-vs-truth` anomaly via pure predicates (NaN/Inf actual not flagged, NaN/Inf target flagged - RewindReadbackGuard semantics). Monotonic `recalcSeq` stamped on every line so one recalc burst is grep-sliceable. Formatters SELF-CONTAINED (do not refactor a shared formatter out of the render tracers).
- `ParsekHarmony.cs` + `Patches/` - Harmony patcher + patches (PhysicsFrame, GhostVesselLoad, GhostCommNetVessel, GhostTrackingStation, FacilityUpgrade, FlightResults, ScienceSubject, TechResearch, CrewDialogFilter, KerbalDismissal, GhostOrbitLine)
- `Patches/GhostTrackingStationPatch.cs` (`SwitchIntentTrackingStationFlyPatch`) - arms `StockActionIntentMarker` (TS Fly) on real-vessel `SpaceTracking.FlyVessel` clicks; mirrors the ghost-block guard.
- `Patches/KscVesselMarkerFlyPatch.cs` - arms `StockActionIntentMarker` (KSC marker Fly) on `KSCVesselMarkers.FlyVessel(Vessel)` clicks.
- `Patches/MapFocusObjectOnSelectPatch.cs` - arms `StockActionIntentMarker` (Map Switch-To) on `MapContextMenuOptions.FocusObject.OnSelect`; Prefix-arms / Postfix-refunds on `SetActiveVessel` early-return. The Prefix spawns `MergeDialog.ShowPreSwitchDecisionDialog` instead of arm-and-skip in two cases (filtered by `DecidePreSwitchDialogAction`): **Case A** - a `SwitchSegmentSession` is already armed and the new target differs from the session's focused PID (handlers commit or scoped-discard the prior session). **Case B** - no session armed, an in-flight recording exists, and `target.loaded == false` (Map Switch-To to a far vessel triggers `FlightDriver.StartAndFocusVessel` scene reload, which bypasses the SceneExit FLIGHT->FLIGHT filter; handlers commit or active-tree-discard the live `activeTree`). Both cases arm a fresh intent + call `FlightGlobals.SetActiveVessel` from the button handlers. Same-target re-clicks and concurrent-dialog re-entry are filtered by the same predicate.
- `RewindInvoker.cs` - Rewind-to-Separation (v0.9) invocation orchestrator: five-precondition gate, pre-load reconciliation bundle capture, RP quicksave copy to save-root, post-load Restore + Strip + Activate + atomic provisional + `ReFlySessionMarker` write.
- `SupersedeCommit.cs` - re-fly merge tail: appends `RecordingSupersedeRelation` rows for the superseded subtree, flips MergeState (Immutable vs CommittedProvisional by `TerminalKindClassifier`), builds `LedgerTombstone`s for in-scope kerbal-death actions + bundled rep penalties, bumps ERS / ELS cache versions. `IsPreRewindCarveOut` filters HEAD (pre-rewind chain head) and pre-rewind debris out of the supersede write-set after the split orchestrator runs.
- `ReFlyProvisionalBinding.cs` - pure raise predicates for R1-EMPTY-PROVISIONAL's fixture-independent statement: a Re-Fly session can reach the merge orchestrator with no recorder ever bound to its provisional. `EvaluateRestoreGiveUp` and `EvaluateSidecarRewrite` both emit a grep-stable `outcome=unbound-refly-provisional` Warn. OBSERVATION ONLY - neither changes control flow (the route to that state is not established; the only demonstrated route was fixture-shaped). Do NOT relax `ReFlySessionMarker.ResolveInPlaceContinuationTarget`'s tree-id gate to "fix" it - that is a genuine stale-marker guard; any marker-driven adoption must be an ADDITIONAL entry point.
- **RP quicksave fixtures are production-shaped:** `ScenarioWriter.BuildRewindPointQuicksave` authors the RP's tree as `RECORDING_TREE isActive=True` (what `TryRestoreActiveTreeNode` reads back, and what a production `GamePersistence.SaveGame` always writes), and stamps each slot's sidecar VESSEL `pid` from `DeriveVesselLaunchGuid(originRecordingId)` so it agrees with the recording's `recordedVesselGuid`. Both exist so an injected re-fly exercises the same load path as a real one; a sidecar missing either lets a FIXTURE divergence read as a product defect (it did once, and cost a wrong diagnosis). Guarded by `RewindB9FixtureTests.Inject_RpSidecar*`.
- `EffectiveState.cs` - effective state computation (ERS / ELS / subtree closure / supersede walking / chain resolution / RP slot resolution). `EffectiveRecordingId` is the pure supersede walker used by visibility checks; `EffectiveTipRecordingId` is the composite chain+supersede walker used by slot tip resolution (chases HEAD → chain → TIP → supersede → fork in one traversal, cycle-safe via a single shared visited set).
- `RecordingTreeSplitter.cs` - Re-Fly merge-time origin splitter: when the closure-root recording spans the rewind UT, splits it into HEAD (pre-rewind, kept visible) + TIP (post-rewind, superseded by the fork). 13-step orchestrator with deep-clone snapshot + ChainIndex map + incremental retag ledger for transactional rollback on exception. Delegates the in-recording cut to `RecordingOptimizer.SplitAtUT` (partitions Points / PartEvents / SegmentEvents / FlagEvents / TrackSections / checkpoints / OrbitSegments at the split UT, tail-cloning straddling OrbitSegments). Post-split, `RecordingOptimizer.CanAutoMerge` carries an explicit supersede-row guard preventing re-merge of HEAD + TIP across the split boundary.
- `MergeJournalOrchestrator.cs` - drives the re-fly merge through Begin → Split → Supersede → Tombstone → Finalize → Durable1Done → RpReap → MarkerCleared → Durable2Done crash-recovery checkpoints; only `Begin` rolls back on crash, every post-Begin phase drives forward via `CompleteFromPostDurable`'s idempotent re-run. `RunFinisher` on OnLoad dispatches Begin → RollBack and later phases → CompleteFromPostDurable.
- `LoadTimeSweep.cs` - OnLoad sweep (between journal finisher and reaper) that validates the re-fly marker's six durable fields, discards zombie NotCommitted provisionals + session-provisional RPs, warn-logs orphan supersede/tombstone rows, and clears stray `SupersedeTargetId` fields.
- `ParsekProcess.cs` - process-wide static identity helper holding the AppDomain-lifetime `ProcessSessionId` GUID used by stock-action intent markers to detect cross-run orphaned saves.
- `StockActionIntentMarker.cs` - positive intent marker armed only by confirmed stock-UI Fly / Switch-To click handlers; carries TTL + UT + `ProcessSessionId` and a pure `EvaluateStaleness` predicate for the FLIGHT-side consume site.
- `SwitchSegmentSession.cs` - live segment-attempt marker armed when a switch/Fly click actually starts a new segment in FLIGHT, owned by `ParsekScenario` and serialized through OnSave/OnLoad so scoped Discard survives save/reload.
- `SwitchSegmentBuilder.cs` - pure tree-mutation helper for switch/Fly continuation segments: `ResolveSwitchContinuationParent` walks PID-coherent terminal leaves and `CreateSwitchContinuationSegment` attaches a new `VesselSwitchContinuation` branch + recording under the chosen parent (or standalone). The live-side wrapper handles parent-side BG flush and BG-map removal.
- `SwitchSegmentConsume.cs` - pure consume decision predicate (`StockActionIntentConsumeDecision.Evaluate`) and the route/result types returned by `ParsekFlight.TryConsumeStockActionIntent`. Maps staleness / setting / target-mismatch / duplicate / missed-switch-recovery outcomes to clear-reason strings and routes.
- `SwitchSegmentNoOpClassifier.cs` - pure predicate `IsNoOpSegment` + `IsMeaningfulPartEvent` + `SwitchSegmentDisposition`: decides whether a resumed (Fly / Switch-To) segment changed nothing meaningful so it can be auto-discarded instead of prolonging the ghost state. NOTE: deliberately NOT `RecordingOptimizer.IsInertPartEventForTailTrim` for engine/RCS (that treats `EngineShutdown` as non-inert; here shutdown/stop/zero-throttle resume seeds are inert and only positive throttle counts). Live side: `RecordingStore.TryClassifyActiveSwitchSegmentNoOp` + `ParsekFlight.TryEvaluateActiveSwitchSegmentNoOp`. Hooks: scene exit (`SceneExitInterceptor.TryAutoDiscardNoOpSwitchSegment`) and in-flight map re-switch (Case A's `DiscardPriorAndSwitchTo`).

## Worktree Workflow

**HARD RULE - never edit or commit inside `Parsek/` (the main checkout) without explicit per-session approval.** This applies to every change that will produce a commit - code, tests, CHANGELOG trims, todo edits, doc tweaks, anything. "It's just a one-line fix" is not an exception. Recovery if I slip: stash any unrelated WIP, `git worktree add` a new worktree at the tip containing the direct-edit commit, `git reset --hard` `Parsek/` back to the pre-direct-edit tip, then land the rescue branch via PR. Never leave a direct-edit commit standing on `main` or a shared branch.

**HARD RULE - every change that will produce a commit starts in my own dedicated sibling worktree.** Do not edit or commit in another task's `Parsek-<branch>/` worktree unless the user specifically asks me to work in that exact worktree. Create a dedicated `../Parsek-<branch-name>` worktree from the right base, work there, and push that branch. Reuse a worktree only when it is mine and already dedicated to the same line of work - within one line of work, keep editing, committing, and pushing inside the same worktree. Spinning up a fresh worktree per change is unnecessary ceremony.

For manual worktrees (when not using `isolation=worktree`), create as sibling folders:
```bash
cd Parsek
git worktree add ../Parsek-<branch-name> -b <branch-name> <target>
```

Pick `<target>` carefully:
- Branching from `main` → use `origin/main` (local main may be behind or ahead of remote).
- Branching from a feature branch that's about to be merged → compare `git log --oneline <local>..origin/<branch>` first. Use the local ref if it's ahead; use `origin/<branch>` if it's behind or matches.

When a worktree's branch finishes its work, land it via a GitHub PR, NOT a local merge into the main checkout: commit on the branch, `git push -u origin <branch>`, then `gh pr create` (clean body, no Co-Authored-By / AI attribution). The PR is reviewed and merged on GitHub. Do NOT `git merge --no-ff <branch>` into `Parsek/` (the main checkout) to land finished work. A fix/chore branch stacked on an unmerged feature branch can open a PR targeting that feature branch instead of `main`; leave the branch around unless the user asks to prune.

`Parsek.csproj` probes up to 5 parent levels for `Kerbal Space Program/`, so builds work from worktrees at this location.

## In-Game Controls

- **Toolbar button** - Toggle Parsek UI
- **Ctrl+Shift+T** - Toggle in-game test runner (works in any scene). Results auto-export to `parsek-test-results.txt` in KSP root.
- UI buttons: Start/Stop Recording, Preview Playback, Stop Preview

## Debug

```bash
grep "[Parsek]" "Kerbal Space Program/KSP.log"    # all diagnostic logs
pwsh -File scripts/validate-ksp-log.ps1            # log pipeline health check (4 rules: session markers, recording start/stop)
python scripts/collect-logs.py [label] [--save NAME]  # gather all logs/saves/test results into ../logs/ timestamped folder
```

When asked to debug an issue, run `python scripts/collect-logs.py <label>` first to snapshot all relevant state, then work from the collected files. The script also runs the log validation automatically. Output goes to `../logs/` (sibling of repo root, outside git).

Alt+F12 opens Unity debug console in-game.

**Map-render warp debug aid (`MapRenderWarpControl`).** When debugging a map / tracking-station render moment that high time-warp keeps skipping over, this temporary aid decelerates time-warp into the moment so it is observable instead of warped clean over. It **ships OFF and BREAKS GAMEPLAY when on** - enable it only while debugging, behind a double gate: (1) set `DebugFlags.MapRenderWarpEnabled` (in `Source/Parsek/ParsekConfig.cs`) to `true` and rebuild (code flag, not a Settings checkbox); (2) turn on the `mapRenderTracing` setting. A re-aim descent auto-registers its window; for any other moment call `MapRenderWarpControl.RegisterWatchWindow(triggerUT, windowEndUT, "label")`. It caps warp DOWN only. Grep `[MapRenderWarp]`. It is a TEMPORARY debug aid that must be removed once the render moment is debugged - see the removal recipe banner at the top of `MapRenderWarpControl.cs`. Do NOT ship it enabled or add a CHANGELOG entry for it.

## Logging Requirements

Every action, state transition, guard condition skip, and FX lifecycle event MUST be logged. The KSP.log is our primary debugging tool - if it didn't get logged, it didn't happen.

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
- Use `ParsekLog.TestSinkForTesting` to capture log output and assert on it - log assertions verify that code paths executed and logged the expected data
- Test pattern: `ParsekLog.TestSinkForTesting = line => logLines.Add(line)` in constructor, `ParsekLog.ResetTestOverrides()` in Dispose
- Assert with: `Assert.Contains(logLines, l => l.Contains("[Subsystem]") && l.Contains("expected text"))`
- Use `[Collection("Sequential")]` on test classes that touch shared static state (ParsekLog, RecordingStore, etc.)
- See `RewindLoggingTests.cs` for the canonical log-capture test pattern
- **In-game tests** (`InGameTests/`): for things that require live KSP (ghost visuals, PartLoader resolution, crew roster, CommNet). Use `[InGameTest(Category = "...", Scene = GameScenes.FLIGHT)]`. Tests can return `void` (sync) or `IEnumerator` (multi-frame coroutine). Run via Ctrl+Shift+T or Settings > Diagnostics.

## Visual & Recording Design Principle

Ghost visuals and recording data must be **correct visually, minimal, and efficient**. Many recordings play simultaneously - every per-frame computation and every stored event multiplies across all active ghosts. Prefer coarse-grained state snapshots over continuous sampling. Record threshold crossings, not continuous values; a binary or 3-state signal beats a continuous float. Use debounce to filter noise. Reserve the continuous-sampling budget for trajectory data. If a visual detail isn't noticeable at playback speed, don't record it.

## Post-Change Checklist

After any change to enums, event types, serialized fields, or schema:
1. Verify `ParsekScenario.cs` OnSave/OnLoad handles the new data
2. Verify test generators in `Tests/Generators/` can produce test data for the new feature
3. Consider adding a synthetic recording for end-to-end testing
4. Run `dotnet test` - all tests must pass

## Documentation Updates — Per Commit, Not Per PR

Before every commit that changes behavior (not just the first one in a PR), check whether these docs need updating and stage them in the same commit:

- `CHANGELOG.md` - add or update the entry under the current version. On follow-up commits that change the fix approach, edit the existing entry rather than leaving the original wording stale.
- `docs/dev/todo-and-known-bugs.md` - mark completed items as ~~done~~, add newly discovered items, and update the "Fix:" description on follow-up commits when the approach changes.
- `docs/dev/autotest-status.md` - for automated-testing changes only: update the status tables when a PR ships a module, live-proves a scenario, adds a test case, or opens/closes a gate. It is the SINGLE status authority for that system (its doc-map section defines which doc owns what - do not duplicate status elsewhere).
- This file (`.claude/CLAUDE.md`) - update only when file layout, build commands, workflow, or key patterns change. **Mirrors:** the umbrella-root copies `../.claude/CLAUDE.md` and `../AGENTS.md` (outside git) are synced by the `SyncAgentInstructionMirrors` post-build target under the same intentional-deploy gate as the KSP deploy (last deploy wins). A docs-only change never triggers a build, so after one lands either run `cd Source/Parsek && dotnet build` or re-copy the file over both mirrors manually.

**Follow-up commit trap:** When a review comment lands on an open PR and changes the fix approach, the CHANGELOG and todo entries written for the first commit become stale. The reviewer reads those docs as authoritative - they must match the code in the current HEAD. Before pushing the follow-up commit, re-read the existing doc entries for the bug/feature and update them to match the new approach.

**Practical check:** after `git add`, run `git diff --cached` and ask: "does any of this contradict or supersede existing wording in CHANGELOG.md or todo-and-known-bugs.md?" If yes, stage the doc updates in the same commit.

## Code Review Follow-Ups

Do one full review at the end of a task/worktree before creating or finalizing the PR, except for low-risk small single-file fixes, docs-only changes, test-only changes, and obvious bug fixes with focused validation; for those, self-review and report validation.

When a reviewer flags fixes on an open PR, re-review only the follow-up changes and any directly affected code paths. Do not restart a full-PR review from scratch on every follow-up unless the new changes actually broaden the risk surface.

Do not re-review excessively. Re-review is for risky code changes: shared behavior, serialization/schema, runtime-only paths, broad refactors, concurrency/lifecycle changes, or fixes that change the PR's behavioral contract. Docs-only edits, small copy/typo fixes, and test-only clarifications do not need another review pass; self-review them and report the validation performed.

## Workflow

See `docs/dev/development-workflow.md` for the full feature development process (vision → scenarios → design doc → plan/build/review cycle).
