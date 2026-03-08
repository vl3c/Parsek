# Recording Playback Bug List (2026-02-11)

Scope: issues observed while testing ghost/vessel replay from `save 1` into `save 2`.

## Status (after code pass)

- Implemented in code, pending in-game verification:
- Bug 1 warp exit hardening (timeline + preview start path).
- Bug 2 relocation ground-contact rewrite (surface offset + terrain altitude for landed-like snapshots).
- Bug 4 preview sphere parity (preview now attempts vessel snapshot visual first).
- Bug 5 EVA switch behavior refinement (auto-stop now captures snapshot for recorded vessel pid at switch time).
- Bug 6 snapshot transform key mismatch (real ProtoVessel uses `position`/`rotation`; builder now supports both `pos`/`rot` and `position`/`rotation`).
- Bug 8 destroyed recordings keep last-good snapshot for visual replay (no EndUT respawn for destroyed).
- Bug 9 EVA ghost renderer support expanded to include SkinnedMesh-based visuals.
- Still pending:
- Bug 3 save baseline hygiene/tooling cleanup.

## Current Evidence Snapshot

- Log file checked: `Kerbal Space Program/KSP.log` (latest session around 21:11-21:19).
- Save files checked: `Kerbal Space Program/saves/test career/1.sfs`, `Kerbal Space Program/saves/test career/2.sfs`, `Kerbal Space Program/saves/test career/persistent.sfs`.
- Key log confirmations:
- Timeline ghosts are now snapshot-built for recordings (`Timeline ghost #N: built from vessel snapshot`).
- Warp-stop triggers for timeline starts (`Stopped time warp for recording #N ...`).
- EVA recording captured and merged as vessel name `Tedorf Kerman`.
- Offset spawn occurred (`Offset vessel #4 ... from 76m to 250m ...`), then respawned as LANDED.

## Bug List

### New Findings From Save 2 Compare (latest run)

### 7) Resource replay feedback loop when recording overlaps timeline playback

- Symptom:
- New merged recording (`Untitled Space Craft`) repeatedly applies large deltas on replay (`funds +5760` multiple times, plus science/rep bumps).
- Evidence:
- `KSP.log` shows repeated timeline resource logs for the same recording window.
- In `2.sfs`, the `Untitled Space Craft` recording has non-zero step deltas baked into points:
- funds: `+5760` appears 6 times
- science: `+1` appears 2 times
- rep: ~`+1.2` appears multiple times
- Root cause:
- Recording points capture global funds/science/rep as-is while other timeline recordings are actively applying resource deltas.
- The resulting recording replays those timeline-origin deltas again later, amplifying economy progression.
- Fix direction:
- During active recording, either:
- ignore timeline-origin resource mutations in point capture, or
- freeze timeline resource application for overlapping windows, or
- tag point-level resource mutations with source and only replay player-origin deltas.

### 8) Destroyed recording still uses sphere fallback (expected behavior, but UX mismatch)

- Symptom:
- `Untitled Space Craft` timeline playback appears as green sphere.
- Evidence:
- Log: `Timeline ghost #5: using sphere fallback`.
- `2.sfs` entry for this recording has `ghostGeometryProbeStatus = vessel_destroyed`, `ghostGeometryError = vessel_destroyed`, no `VESSEL_SNAPSHOT`.
- Root cause:
- Recording was marked destroyed (`Active vessel destroyed during recording!`) and merged with `recommended=MergeOnly`, so no snapshot geometry exists.
- Status:
- Not a regression in ghost rendering code; this is current designed fallback behavior.
- Potential UX improvement:
- Surface explicit UI text at merge/playback time: "Destroyed recording uses sphere fallback."

### 1) Warp control is incomplete at playback start

- Symptom:
- Timeline playback can stop regular time warp in some cases, but behavior is not consistently enforced for all playback starts.
- Manual preview playback does not force warp exit.
- Root cause in code:
- Timeline only checks `TimeWarp.CurrentRate > 1f` and calls `TimeWarp.SetRate(0, true)` near recording start.
- No explicit handling for physics warp mode.
- Manual preview (`StartPlayback`) does not touch warp at all.
- Relevant code:
- `Source/Parsek/ParsekFlight.cs:433`
- `Source/Parsek/ParsekFlight.cs:448`
- `Source/Parsek/ParsekFlight.cs:356`
- Fix direction:
- Add one shared `ExitAllWarpForPlaybackStart()` path used by both timeline and manual preview.
- Handle both regular time warp and physics warp modes.

### 2) Offset-spawned vessel can appear floating

- Symptom:
- When end-spawn is moved away from crowded pad area, spawned vessel may appear above ground.
- Root cause in code:
- Reposition uses world-space offset from closest vessel center and writes only top-level `lat/lon/alt`.
- Offset vector is not projected onto terrain tangent plane.
- Part-level snapshot transforms are not re-derived for moved landed spawn.
- Relevant code:
- `Source/Parsek/VesselSpawner.cs:121`
- `Source/Parsek/VesselSpawner.cs:125`
- `Source/Parsek/VesselSpawner.cs:131`
- `Source/Parsek/VesselSpawner.cs:133`
- Log evidence:
- `Offset vessel #4 (Tedorf Kerman) from 76m to 250m ...`
- Fix direction:
- Compute relocation on-surface (great-circle/tangent), then set ground-contact altitude at target site.
- Validate landed vessel snap-to-ground on spawn for relocated cases.

### 3) Reloading `save 1` still shows replay vessels on map

- Symptom:
- After running timeline and exiting, loading `save 1` can still show replay vessels present.
- What data shows:
- `1.sfs` already contains recording vessel snapshots with fixed pids (for synthetic recordings), including pids that correspond to prior spawned ids.
- `2.sfs` and `persistent.sfs` contain 5 recordings and `spawnedPid` fields, including the EVA recording.
- Root cause:
- Test/injection data currently seeds vessel snapshots/pids in save files, so map presence can be real save state, not only runtime replay.
- Also expected KSP behavior: once spawned and saved, vessels persist unless loading an earlier file that does not contain them.
- Fix direction:
- Decide desired semantics for test saves:
- Option A: "pure timeline replay only" test save (no pre-existing spawned vessels/pids).
- Option B: "mixed persistence" test save (current behavior).
- Add a cleanup/reset tool command to strip spawned replay vessels/pids from a baseline save before tests.

### 4) Manual preview playback still uses green sphere

- Symptom:
- F10 preview still shows primitive sphere, while timeline ghosts now use vessel visuals.
- Root cause in code:
- `StartPlayback()` still hard-codes `CreateGhostSphere("Parsek_Ghost_Preview", Color.green)`.
- Relevant code:
- `Source/Parsek/ParsekFlight.cs:364`
- Fix direction:
- Route preview ghost creation through the same snapshot visual builder + fallback as timeline ghosts.
- Apply same ghost material styling path for consistency.

### 5) EVA + revert creates kerbal replay instead of parent vessel replay

- Symptom:
- Launch recording with EVA can become a kerbal recording, then replay as EVA/kerbal (green kerbal + chute behavior), not the original vessel.
- Root cause in code:
- Recorder is bound to initial vessel pid.
- On physics frame, if active vessel pid changes, recording auto-stops.
- Scene-change stash then snapshots current active vessel (which can be EVA kerbal at that moment).
- Relevant code:
- `Source/Parsek/FlightRecorder.cs:136`
- `Source/Parsek/FlightRecorder.cs:140`
- `Source/Parsek/ParsekFlight.cs:189`
- `Source/Parsek/VesselSpawner.cs:280`
- Log evidence:
- `Active vessel changed during recording ... auto-stopping`
- `Stashed pending recording ... from Tedorf Kerman`
- `Vessel snapshot taken ... Situation: EVA Kerbin`
- Fix direction:
- Decide policy:
- Option A: stop-and-discard on vessel switch during recording.
- Option B: keep tracking original vessel only, ignore active-vessel switch.
- Option C: split recording into segments (vessel + EVA) and require explicit merge choice.
- Current policy (implemented):
- Continue recording when switching from vessel to EVA, but stop when switching from EVA back to a non-EVA vessel.
- Known behavior gap:
- `flight -> EVA` is captured in one recording, but `EVA -> vessel re-entry` currently ends that recording.
- Full continuity across split/rejoin requires future multi-actor track support.

### 6) Post-merge vessel spawn disappearance report

- Symptom:
- "vessels don't appear anymore after merged recording finishes" was reported previously.
- Current status:
- Major blocker (PART `name` vs `part`) is fixed and committed.
- Remaining risk is tied to the issues above (spawn relocation/warp/save baseline), not snapshot build failure.
- Relevant fix already landed:
- `Source/Parsek/GhostVisualBuilder.cs:29`

## Recommended Work Order

1. Implement unified warp-exit on playback start (timeline + preview).
2. Upgrade manual preview to snapshot vessel visual path.
3. Harden relocation spawn to ground-contact placement.
4. Lock recording vessel-switch policy (EVA handling), then implement.
5. Add a save-baseline reset utility for repeatable tests (`save 1` clean start, `save 2` result capture).

## When To Run In-Game Visual Checks

- Checkpoint A (after warp fix): start playback under both regular warp and physics warp; verify immediate return to normal-time simulation.
- Checkpoint B (after preview visual parity): F10 preview must show vessel ghost, not sphere, with same tint as timeline ghost.
- Checkpoint C (after relocation fix): force close-spawn case near KSC pad; verify spawned vessel is grounded and stable.
- Checkpoint D (after EVA policy fix): reproduce EVA-then-revert flow; verify resulting recording matches chosen policy.
- Checkpoint E (after save baseline cleanup): load `save 1`, run timeline, exit, reload `save 1`; verify deterministic pre-timeline state.
