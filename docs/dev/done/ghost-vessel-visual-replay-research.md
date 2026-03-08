# Ghost Vessel Visual Replay Research and Implementation Plan

## Goal
Replace the current green sphere ghosts with vessel-like visual ghosts during playback, while preserving existing behavior (timeline timing, map markers, deferred EndUT spawn, resource deltas, crew reservation logic).

## Status
This document is updated after plan review and recent repo changes. Open questions from the previous version are now resolved where possible, and implementation sequencing is adjusted around a mandatory feasibility spike.

## Locked Decisions (Pre-Implementation)
1. Geometry capture timing: capture at recording stop.
2. Atomic capture rule: capture `VesselSnapshot` and ghost geometry in one operation. If geometry fails, keep snapshot and mark geometry unavailable.
3. Geometry storage location: save-scoped files under `saves/<SaveName>/Parsek/Recordings/<recordingId>.pcrf`.
4. Versioning: add `recordingFormatVersion` and `ghostGeometryVersion` now.
5. Initial fidelity target: best-effort part visual hierarchy (not guaranteed perfect for procedural/fairings/all variant cases in v1).
6. Initial visual style: recognizable vessel visuals with ghost treatment (tint/alpha pass), plus sphere fallback on failure.
7. Strategy remains hybrid: visual-only ghost first, real-vessel playback/handoff later as a separate high-risk track.

## Current State in Parsek
- Playback ghost is currently a sphere via `CreateGhostSphere` in `Source/Parsek/ParsekFlight.cs`.
- Playback timing and positioning are already stable:
  - point interpolation,
  - orbit segment interpolation,
  - timeline spawning/despawning,
  - EndUT real spawn/recover via `VesselSpawner`.
- Map markers already consume `transform.position` and are object-agnostic (`Source/Parsek/ParsekUI.cs`).

Recent baseline improvements (already landed):
- Playback lifecycle diagnostics are now structured and broad (`cbed2ec`): scenario load summary, ghost enter/exit, orbit activation, spawn context, etc.
- Warp-stop spam near playback start is fixed with one-shot guard per recording (`cbed2ec`).
- Synthetic recording tooling is now robust and standardized (`01132e5`, `32909d2`), with UT auto-read and injection workflow supporting both `persistent.sfs` and target save.

Roadmap alignment (`docs/parsek-architecture.md`):
- Open: `Ghost as actual vessel model (replace sphere)`.
- Open: `Take control of playback vessel`.
- Deferred but relevant: `IgnoreGForces(240)` when/if ghost becomes a real vessel.

## Key Findings from Helper Mod Analyses

## PersistentTrails (primary reference for visual ghost approach)
Source: `docs/reference/PersistentTrails-architecture-analysis.md`
- Uses geometry reconstruction for ghost visuals (`CraftLoader`) with fallback sphere.
- Ghost objects are visual hierarchies, not full gameplay vessels.
- Disables colliders/lights/animations by default.
- Strong precedent for "best effort visuals + reliable fallback".

## VesselMover (primary reference for real-vessel control risk)
Source: `docs/reference/VesselMover-architecture-analysis.md`
- Real vessel kinematic driving requires strict per-frame safety pattern:
  - `IgnoreGForces(240)` first,
  - `SetPosition`, `SetRotation`, `SetWorldVelocity(0)`, angular zeroing.
- Packed/off-rails handling is mandatory.
- Confirms real-vessel ghosts are high-risk and should be deferred.

## LazySpawner (snapshot/spawn hygiene)
Source: `docs/reference/LazySpawner-architecture-analysis.md`
- Emphasizes snapshot correctness (`isBackingUp = true`) and ConfigNode completeness.
- Highlights ID hygiene issues for programmatic vessel construction.
- Relevant for keeping snapshot/spawn path robust while extending recording format.

## KSPCommunityFixes (time/rails behavior)
Source: `docs/reference/KSPCommunityFixes-architecture-analysis.md`
- Packed/unpacked transitions and time warp behavior are key sources of replay bugs.
- Confirms need to keep warp and scene lifecycle handling conservative.

## FMRS / StageRecovery (state coordination patterns)
Sources:
- `docs/reference/FMRS-architecture-analysis.md`
- `docs/reference/StageRecovery-architecture-analysis.md`
- Reinforce event-driven lifecycle handling, polling fallback for edge states, and save/load race avoidance.

## Core Uncertainty (Now Explicit)
The hardest problem is geometry capture + reconstruction fidelity for all KSP part types.

Known hard cases:
- multi-MODEL parts,
- rescale/offset behavior,
- fairings/procedural/generated meshes,
- part variants.

Because of this, implementation begins with a spike rather than immediate full rollout.

## Revised Approach

## Approach A (phase 1, recommended): visual-only ghost object
- Keep ghost as GameObject hierarchy (not a KSP Vessel).
- Reconstruct from captured geometry payload.
- Fall back to existing sphere when unavailable/invalid.

## Approach B (future, separate track): real-vessel ghost + handoff
- Separate high-risk effort.
- Requires VesselMover safety pipeline and rails/timewarp hardening.

## Revised Implementation Plan

## Spike 0: Feasibility and Fidelity Boundary (mandatory)
Goal: determine practical geometry pipeline before committing implementation details.

Validate:
1. Can we clone/capture live vessel visual hierarchy reliably at recording stop?
2. Can we strip physics/behaviors and keep render-only hierarchy stable?
3. Can we serialize minimal representation and reconstruct in later sessions?
4. Which part classes fail or degrade (procedural/fairings/variants)?

Output:
- Short report with supported/unsupported categories.
- Finalized v1 fidelity scope.
- Chosen capture strategy (reconstruct from model references vs captured visual structure).

## Milestone 1: Data + Plumbing (merged from prior M0/M1)
1. Add format fields:
  - `recordingFormatVersion`
  - `ghostGeometryVersion`
  - `ghostGeometryRef` (or equivalent metadata)
2. Implement save-scoped geometry file path convention:
  - `saves/<SaveName>/Parsek/Recordings/<recordingId>.pcrf`
3. Implement atomic capture transaction at recording stop:
  - capture snapshot + geometry together,
  - if geometry fails: mark missing and continue with snapshot.
4. Preserve compatibility with old recordings lacking geometry.

Acceptance:
- New recordings persist version + geometry metadata.
- Old recordings load unchanged.
- Missing geometry does not break load/playback.

## Milestone 2: Reconstruction + Playback Integration
1. Build visual ghost from geometry payload.
2. Replace sphere creation call sites in `ParsekFlight` with:
  - geometry ghost when available,
  - sphere fallback when not.
3. Keep interpolation/orbit code unchanged (works on transform).
4. Keep map marker logic unchanged.

Acceptance:
- New recordings show vessel-like ghost visuals.
- Legacy recordings continue with sphere.
- No regressions in timeline playback and EndUT spawn behavior.

## Milestone 3: Visual Policy + Transition Quality + Geometry-Specific Diagnostics
1. Finalize and implement ghost material policy for v1.
2. Ensure EndUT transition remains clear and deterministic (ghost despawn -> real spawn).
3. Add diagnostics only for new geometry pipeline concerns:
  - geometry capture/load failure reason,
  - fallback activation reason,
  - version mismatch handling.

Note: general playback lifecycle diagnostics already exist and should be reused, not duplicated.

Acceptance:
- Visual style is consistent and readable.
- EndUT transition behavior is robust across success/fallback combinations.
- Geometry failures are diagnosable with one grep path.

## Milestone 4: Performance Validation (optimize only if needed)
1. Measure with realistic expected concurrency (likely 1-3 active ghosts).
2. Add caching only where measured benefit exists.
3. Avoid premature LOD/culling complexity unless required.

Acceptance:
- Stable framerate in expected usage.
- No sustained allocation/leak growth during repeated playback cycles.

## Explicitly Removed from Near-Term Plan
- No early `IGhostPlaybackEntity` abstraction milestone.
- No real-vessel ghost implementation in this feature phase.

Rationale: YAGNI and risk containment.

## Recording Format and Migration
- Format versioning is mandatory from first geometry-enabled release.
- Backward compatibility behavior:
  - recording with no geometry -> sphere fallback.
  - unknown/newer geometry version -> safe fallback + warning log.

## Atomicity and Consistency Rules
At recording stop:
1. Capture `VesselSnapshot`.
2. Capture ghost geometry payload.
3. Commit recording only with explicit status fields indicating which artifacts were captured.

This prevents ambiguous states and makes EndUT behavior deterministic.

## Risks and Mitigations (Updated)
- Geometry fidelity gaps across modded parts:
  - Mitigation: spike-defined support matrix + best-effort scope + fallback.
- Save portability issues:
  - Mitigation: store geometry in save folder, not GameData.
- Format evolution pain:
  - Mitigation: version fields now.
- Visual confusion with real vessels:
  - Mitigation: explicit ghost material treatment and consistent style.
- Scene/load/timewarp regressions:
  - Mitigation: keep existing lifecycle logic unchanged in phase 1 and preserve current warp guard behavior.

## Testing Plan (Updated)

Automated:
1. Versioned recording serialization/deserialization round-trip.
2. Geometry-missing recording loads and falls back cleanly.
3. Unknown geometry version falls back cleanly.
4. Playback object lifecycle remains consistent across multiple recordings.

Manual:
1. New recording replay shows vessel-like ghost.
2. Old save replay still works (sphere).
3. EndUT transition cases:
  - snapshot+geometry present,
  - snapshot present + geometry missing,
  - snapshot missing.
4. Scene change and quicksave/quickload preserve expected behavior.
5. Multiple recordings replay independently.

Use existing synthetic recording injection workflow as base harness (`Source/Parsek.Tests/SyntheticRecordingTests.cs`, `docs/synthetic-recordings.md`) and extend it with geometry-present/geometry-missing fixtures.

## Next Steps
1. Stabilize current bugfixes first:
  - destroyed recordings should keep last-good vessel snapshot for visual 1:1 replay (no forced sphere unless snapshot truly unavailable),
  - destroyed recordings remain non-spawning at EndUT,
  - EVA ghost visuals should render (SkinnedMesh support),
  - record/snapshot backup cadence should be time-based and measured (avoid frame-coupled backup spikes).
2. Prevent resource-feedback loops when recording overlaps timeline replay.
3. Start multi-actor replay track (stage separation and EVA split support):
  - capture event-driven snapshots on vessel split/dock/undock/EVA transitions,
  - maintain per-actor tracks keyed by persistentId,
  - replay split actors in parallel while preserving parent/child timelines.
