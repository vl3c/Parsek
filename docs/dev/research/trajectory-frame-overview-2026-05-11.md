# Trajectory Frame Overview

**Date:** 2026-05-11
**Branch:** `claude/investigate-trajectory-logic-OtzVx`
**Status:** Updated after the v0 recording/rendering schema reset. This is a current-contract overview, not a legacy compatibility guide.

---

## Current Baseline

The accepted recording and trajectory sidecar baseline is v0 with `recordingSchemaGeneration = 1`:

- `RecordingStore.CurrentRecordingFormatVersion == 0`
- `RecordingStore.CurrentRecordingSchemaGeneration == 1`
- binary `.prec` sidecars use the `PSK0` magic and BinaryV0 payload
- text ConfigNode `.prec` files are debug mirrors only; authoritative text sidecar load is unsupported
- `RecordingTreeRecordCodec` and sidecar probes reject pre-reset files by generation, format mismatch, or magic mismatch

Pre-reset Relative payloads are historical implementation notes only. Do not add new migration paths for them unless the release policy changes.

---

## Recorder Surfaces

### Absolute Sections

Absolute `TrackSection.frames` store body-fixed lat/lon/alt samples plus surface-relative rotation. Playback uses `InterpolateAndPosition` and `body.GetWorldSurfacePosition`.

### Relative Sections

Relative `TrackSection.frames` store anchor-local Cartesian metres in the misleading `TrajectoryPoint.latitude`, `longitude`, and `altitude` fields. Playback must always dispatch through the active `TrackSection.referenceFrame` before interpreting those fields.

Relative sections can also carry `TrackSection.bodyFixedFrames`. Those are full body-fixed `TrajectoryPoint` samples for the focus vessel at the same UTs. They are not position-only records; body, rotation, velocity, and altitude travel with the point.

### Orbital Checkpoints

Orbital checkpoint sections store `OrbitSegment` data and render through Kepler propagation. On-rails background vessels write `OrbitSegments` or `SurfacePos` instead of loaded-bubble `TrackSection` frames.

---

## Debris Contract

Parent-anchored debris has `Recording.IsDebris == true` and a non-empty `Recording.DebrisParentRecordingId`.

Recording behavior:

- The split path stamps `DebrisParentRecordingId` to the parent recording id.
- Debris opens a Relative section only while the parent is a valid live anchor: loaded/unpacked and inside the parent-proximity band.
- Parent proximity uses full-rate sampling at `<=250 m`, half-rate sampling and Relative entry through `<=500 m`, and Relative exit beyond `550 m`.
- Every Relative debris sample writes both anchor-local `frames` and a body-fixed primary peer in `bodyFixedFrames`.
- The initial close-range Relative seed also writes its body-fixed peer.
- When the parent is unavailable or debris leaves the parent-proximity band, the recorder uses a non-relative section or accepted orbit tail rather than extending stale Relative payload.

Playback behavior:

- Ordinary parent-anchored debris tries `bodyFixedFrames` first when the active Relative section covers the playback UT.
- The body-fixed primary path requires at least two `bodyFixedFrames` samples and rejects UTs outside their actual endpoint coverage. It must not clamp a single or out-of-range point.
- Anchor-local Relative `frames` remain as the secondary surface for loop-anchored chains and diagnostics.
- Loop-anchored debris chains can use the live loop-relative path only after each parent link proves it has active Relative coverage at the playback UT. If that proof fails, playback falls back to the strict body-fixed primary route or retires.
- If neither authored Relative frames nor body-fixed primary coverage applies, `DebrisRelativePlaybackPolicy` retires the ghost for that section.

---

## Re-Fly Contract

Active Re-Fly does not rewrite old recordings or translate old ghosts toward the player's live vessel. Old recordings render at their original recorded coordinates:

- Absolute sections render through body lookup.
- Relative non-loop sections resolve through recorded anchor ids.
- Parent-anchored debris follows the current body-fixed primary contract above.
- Orbital checkpoint sections render through orbit propagation.

The live player vessel is consulted only for the explicit loop-anchor carve-out using `Recording.LoopAnchorVesselId`.

---

## Quick Matrix

| Section or role | Recorded data | Playback surface |
|---|---|---|
| Absolute | body-fixed `frames` | body lookup |
| Relative non-debris | anchor-local `frames`, optional `bodyFixedFrames` | recorded-anchor resolver, strict body-fixed fallback, then retire |
| Parent-anchored debris | anchor-local `frames` plus `bodyFixedFrames` | strict body-fixed primary first; loop chains may try live-loop relative first after parent coverage proof |
| OrbitalCheckpoint | `OrbitSegment` checkpoints | Kepler propagation |
| On-rails background vessel | `OrbitSegments` or `SurfacePos` | Kepler or surface placement |
| Loop-anchored recording | Relative payload plus `LoopAnchorVesselId` | explicit live PID loop anchor |

---

## Footguns

1. A flat `Recording.Points` list is not enough to interpret a Relative sample. Resolve the `TrackSection` for the UT first.
2. `bodyFixedFrames` are the current body-fixed primary list. Do not reintroduce legacy shadow routers or tumbling-parent render gates.
3. A Relative section's declared `startUT`/`endUT` is not proof that `bodyFixedFrames` cover the same interval. Use the body-fixed list endpoints.
4. A single body-fixed sample is not renderable coverage for current debris playback; clamping it creates stale frozen ghosts.
5. Ballistic extrapolated tails are never Relative.
