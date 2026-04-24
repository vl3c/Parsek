# Fix Post-Switch Attitude Docking Alignment

**Related:** `#546`, `#534`

## Problem

The current post-switch auto-record watcher only treats translation, engine/RCS activity, and authored vessel-state deltas as meaningful triggers. That misses a common docking flow:

1. The player switches from a capsule to a nearby station.
2. They use SAS, reaction wheels, or a light RCS trim to align the station docking port.
3. They switch back and dock.

Focus switch alone should not claim the station, but the deliberate alignment should. Today pure attitude change can be missed, and the existing RELATIVE frame contract is only half-relative: offsets are stored in world space while playback composes rotation as if it were anchor-local. That makes the "rotate station, then dock" assumption false for new recordings too.

## Invariant

When the player resumes or switches to a nearby vessel, rotates it to align a docking port, switches back, and docks, Parsek must preserve the visible docking geometry and route the later dock into the correct tree merge shape.

More concretely:

- focus switch alone must not start recording
- a deliberate attitude change after the switch must start or promote recording
- SAS hold noise and micro jitter must not false-trigger
- new RELATIVE sections must be truly anchor-frame relative
- legacy recordings must continue to replay through the old contract

## Decisions

- Attitude trigger threshold: `3f` degrees from the captured baseline, with a short debounce (`0.2s`)
- Scope: any real non-EVA vessel, not only vessels already proven to be in docking range
- Relative-frame fix: do the full contract repair in this PR, not a partial trigger-only change

## Recording Contract Change

Add recording format `v6`.

For new `ReferenceFrame.Relative` track sections:

- position payload stores anchor-local offset:
  `Inverse(anchor.rotation) * (focusWorldPos - anchorWorldPos)`
- rotation payload stores anchor-local world rotation:
  `Inverse(anchor.rotation) * focusWorldRotation`
- playback resolves with:
  `anchorWorldPos + anchor.rotation * localOffset`
- playback rotation resolves with:
  `anchor.rotation * localRot`

Compatibility rule:

- `v5` and older RELATIVE sections stay on the legacy playback path
- do not reinterpret older saved RELATIVE payloads as anchor-local
- loop-interval migration or other unrelated format normalization must not silently bump old recordings onto the v6 contract

## Post-Switch Trigger Design

The watcher remains "armed on switch, start on later change."

Baseline captured after switch:

- vessel pid/name
- baseline world position
- baseline world rotation
- baseline orbit/resource/crew/part-state digests
- baseline trajectory point used to seed the initial alignment window once recording starts

Trigger priority:

1. Engine activity
2. Sustained RCS activity
3. Attitude change
4. Crew/resource/part-state change
5. Landed motion
6. Orbit change

Attitude gating:

- compare `Quaternion.Angle` on sign-canonicalized quaternions
- require `>= 3f` degrees from the baseline
- require the armed pid to still match the active vessel
- hold the trigger above threshold for a short debounce so SAS hold noise does not start recording

## Sampling Change

The watcher can decide to start recording, but recorder sampling must also notice attitude-only motion after start. Add world-rotation bookkeeping to the adaptive sampler and record when either:

- normal velocity/orbit thresholds fire, or
- world rotation changes by a smaller sampling threshold (`1f` degree after the minimum sample interval)

That keeps reaction-wheel-only alignment dense enough to preserve the visible port approach instead of emitting one late point after the vessel is already aligned.

## Initial Alignment Window

If the post-switch start was caused by `AttitudeChange`, seed the new recording with:

- the baseline pose captured before the threshold fired
- the current pose at start time

That avoids losing the first few degrees of motion between "player started rotating" and "threshold/debounce accepted."

## Docking Outcomes To Verify

### No-op switch

Switch to a real vessel, do nothing meaningful, switch away or dock unchanged.

Expected:

- no recording start
- no promotion
- existing one-parent foreign merge behavior remains unchanged if the vessel never actually changed

### RCS alignment

Switch to a nearby vessel and rotate/translate with RCS only.

Expected:

- sustained RCS still triggers as before
- seeded baseline + current pose preserve the initial alignment window
- later RELATIVE playback preserves the visible geometry against the nearby vessel

### Reaction-wheel / SAS alignment

Switch to a nearby vessel and rotate it with wheels/SAS while velocity/orbit are nearly unchanged.

Expected:

- switch alone does nothing
- deliberate alignment crosses the attitude trigger threshold and starts recording
- adaptive attitude-aware sampling captures enough points to preserve the alignment arc

### Dock with a tree member

If the aligned vessel was already tracked in the active tree background set:

- the attitude trigger promotes that background member into the active recording
- the later dock can merge two tracked parents as normal

### Dock with an outsider

If the aligned vessel was not yet in the active tree:

- the attitude trigger starts a fresh active recording before the dock
- the later dock can become a two-parent merge only if the outsider actually changed first
- if the outsider never changed before docking, the existing one-parent foreign merge remains valid

## Tests

- xUnit: relative local offset/rotation round-trip
- xUnit: legacy relative playback path stays legacy for pre-v6 recordings
- xUnit: attitude trigger below threshold ignored, above threshold accepted, no baseline ignored, wrong-vessel ignored
- xUnit: attitude-aware adaptive sampling
- xUnit: post-switch decision routing for tracked background promote, outsider fresh start, and no-op mismatch
- runtime canary for station alignment if practical; otherwise document as manual validation follow-up

## Docs To Update

- `CHANGELOG.md` under `0.9.0`
- `docs/dev/todo-and-known-bugs.md`
- `.claude/CLAUDE.md` rotation/world-frame section

The docs need to stop claiming that all recorded rotations are surface-relative. That remains true for absolute/surface playback and ProtoVessel snapshots, but format-v6 RELATIVE track sections now store anchor-local world rotation.
