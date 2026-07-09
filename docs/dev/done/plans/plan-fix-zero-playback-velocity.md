# Plan: Address one-frame zero `playbackVel` / `rawAlignment=fallback` log events

Branch: `fix/zero-playback-velocity-fallback`
Worktree: `Parsek-fix-zero-playback-velocity`
Date: 2026-04-18

## Problem

The watch-camera horizon log occasionally emits one-frame events where
`playbackVel=(0.0,0.0,0.0)` and `rawAlignment=fallback`, even though the
ghost is clearly flying at that instant (e.g. `alt=1467m`, valid `bodyVel`).
The neighboring frames report non-zero velocity. Observed in
`logs/2026-04-18_1106_ghosts-stuck-at-pad/KSP.log` at lines 15020, 16853,
17507, 18080. Flagged as a possible secondary bug during the post-#361
log investigation; not tracked in `docs/dev/todo-and-known-bugs.md` and
no open PR addresses it (checked 2026-04-18).

Example log line:

```
[Parsek][INFO][CameraFollow] Watch horizon basis: ... alt=1467m
velocityFrame=surfaceRelative source=ProjectedVelocity rawAlignment=fallback
playbackVel=(0.0,0.0,0.0) bodyVel=(-99.6,0.0,-144.3) ...
```

## Root cause (identified)

Sampled playback velocity is computed by linear interpolation of the two
bracketing trajectory points' stored velocities:

- `ParsekFlight.cs:9967-9970` (point interpolation path)
- `ParsekFlight.cs:10706-10708` (relative-frame interpolation path)

Both read `Vector3.Lerp(before.velocity, after.velocity, t)`. When the two
bracketing points have near-opposite velocities (e.g. a decoupler event
recorded ~0.1 s apart flips the vessel's rigidbody velocity, or a landing
that bounces, or any sub-second direction reversal), the linear
interpolation at `t ≈ 0.5` collapses to approximately zero magnitude.

`WatchModeController.DescribeHorizonAlignment` then labels the result
`fallback`:

```csharp
if (velocity.sqrMagnitude < 0.01f) return "fallback";
```

and the horizon-basis computation falls through to the
`HorizonForwardSource.LastForwardFallback` /
`ArbitraryPerpendicularFallback` branch for that single frame.

## Severity assessment — LOW

This matters less than it looks:

1. **Ghost positioning is unaffected.** The ghost's mesh position is
   driven by the stored `position` field of bracketing points, not the
   interpolated velocity. Visually the ghost moves continuously through
   the glitched frame.
2. **Camera fallback is smooth.** `LastForwardFallback` uses the
   previous frame's forward vector, which is still a good approximation
   of prograde across a one-frame transient. The camera does NOT snap.
3. **The "glitch" is essentially log noise.** Users aren't reporting
   visible issues; the fallback events were detected by grepping INFO
   lines.
4. **One-frame transient** — blink-and-miss even if visible.

Consider this a diagnostic-cleanup task, not a user-facing bug fix. It
was promoted to the todo list only because my original log investigation
called it out as an "unknown" that could mask a deeper bug. After the
root cause analysis here, the mechanism is fully explained and benign.

## Options

### Option A — Accept as diagnostic noise (recommended default)

- **Code change:** none.
- **Doc change:** add a todo entry documenting the mechanism so the next
  log-audit doesn't re-investigate this.
- **Rationale:** severity is cosmetic; fixing it introduces a
  correctness/complexity tradeoff for zero perceptible benefit. Track it
  as a known diagnostic artifact.

### Option B — Snap to bracket-point velocity on detected reversal

- **Code change:** in the two interpolation sites in `ParsekFlight.cs`,
  detect `Vector3.Dot(before.velocity, after.velocity) < 0` and use the
  nearer bracket's velocity instead of the Lerp:
  ```csharp
  Vector3 velocity;
  if (Vector3.Dot(before.velocity, after.velocity) < 0f)
      velocity = (t < 0.5f) ? before.velocity : after.velocity;
  else
      velocity = Vector3.Lerp(before.velocity, after.velocity, t);
  ```
- **Pros:** pure-static and unit-testable, eliminates the zero-crossing,
  camera horizon stays on a meaningful direction across a reversal.
- **Cons:** introduces a discontinuity in the logged velocity at `t = 0.5`
  (jumps from `before.velocity` to `after.velocity`), which is arguably
  less physically accurate than the linear interpolation. Also affects
  both the point-interpolation and relative-frame-interpolation paths —
  two call sites to keep in sync.
- **Recording impact:** none. Stored trajectory data unchanged.

### Option C — Use velocity history (EMA) for the horizon log, not the instant sample

- **Code change:** in `WatchModeController.UpdateHorizonProxy`, maintain
  a per-session exponentially-weighted moving average of the watched
  ghost's `lastInterpolatedVelocity`, and log THAT value as `playbackVel`.
  The ghost's actual motion and camera targeting stay on the instant
  sample; only the DIAGNOSTIC log uses the smoothed value.
- **Pros:** zero impact on engine behaviour; log becomes noise-free;
  intuitive physical meaning (the horizon camera cares about sustained
  direction, not instantaneous derivatives).
- **Cons:** hides real sub-second velocity transients from the log if we
  later need to diagnose one. Needs a time constant tuning (e.g.
  half-life of 0.5 s would dampen the one-frame zero to ~50% magnitude
  drop on the affected frame, still above the 0.01 threshold). Adds
  per-frame state to `WatchModeController`.
- **Recording impact:** none.

### Option D — Fall back to numerical differentiation when interpolated velocity is near zero

- **Code change:** in the interpolator, if `Vector3.Lerp(...)` returns
  `sqrMagnitude < 0.01`, recompute velocity from `(after.position -
  before.position) / (after.ut - before.ut)`. Position-based velocity
  can't collapse unless the two points are literally at the same
  position.
- **Pros:** physically meaningful; uses data that's always available.
- **Cons:** returns the AVERAGE velocity over the whole bracket interval
  (same value for `t=0.1` and `t=0.9`), which is a different semantic
  from the Lerp'd value elsewhere in the sample. Callers of the
  interpolator that use velocity for anything physical (not just the
  camera log) would see this semantic shift.

## Recommendation

**Ship Option A.** Add a one-line todo entry documenting the root cause
and the reason we chose not to fix it. The severity does not justify
code churn.

If the user would rather "not see the log line", Option C (EMA for log
only) is the minimal-risk code change. Do NOT touch the interpolation
math (Options B, D) — interpolated velocity is consumed by paths beyond
just the camera log (e.g. the horizon-proxy orientation, horizon
alignment source, possibly others), and changing the semantics to
preserve magnitude under reversal would subtly alter playback math
everywhere.

## If Option A is adopted, the doc entry looks like

```markdown
## 453. Watch camera horizon log occasionally shows `playbackVel=(0,0,0) rawAlignment=fallback` for one frame

**Source:** post-v0.8.0 log investigation `2026-04-18`
(`logs/2026-04-18_1106_ghosts-stuck-at-pad/KSP.log` lines 15020,
16853, 17507, 18080).

**Symptom:** one-frame events in the "Watch horizon basis" log where
`playbackVel=(0.0,0.0,0.0)` and `rawAlignment=fallback`, even though
the ghost is clearly flying (non-zero `alt`, non-zero `bodyVel`).
Frames before and after show non-zero playback velocity.

**Cause:** `ParsekFlight.cs:9967-9970` and `:10706-10708` compute
`velocity = Vector3.Lerp(before.velocity, after.velocity, t)` over the
two bracketing trajectory points. When the stored velocities of the
bracket happen to be near-opposite (sub-second direction reversal from
a decoupler fire, a bounce landing, an rb_velocity flip during an
atmospheric transition), linear interpolation near `t=0.5` collapses
the magnitude to below the 0.01 fallback threshold used by
`WatchModeController.DescribeHorizonAlignment` at line ~2800, and the
horizon basis briefly falls to `LastForwardFallback` /
`ArbitraryPerpendicularFallback`.

**Impact:** cosmetic only. Ghost positioning uses the stored
`position` field, not the interpolated velocity, so the ghost's
motion is unaffected. `LastForwardFallback` reuses the previous
frame's forward vector, so the camera does NOT snap visibly. The
effect is one frame of "fallback" in the log; no user-reported visual
glitch.

**Decision:** leave as-is. Options to snap-to-bracket-velocity on
reversal (risks semantic shift in interpolation used elsewhere) or
log-only EMA smoothing (hides real transients) are documented in
`docs/dev/plan-fix-zero-playback-velocity.md`. Severity does not
justify the code churn or the tradeoff.

**Status:** Known, not fixed (diagnostic artifact).
```

## If Option C is later adopted

Minimal change, two commits:

1. Add `Vector3 smoothedPlaybackVelocity` field to `WatchModeController`,
   update each frame with an EMA:
   `smoothed = Lerp(smoothed, state.lastInterpolatedVelocity, alpha)`
   where `alpha = 1 - exp(-dt / halfLife)` and `halfLife = 0.5f`.
2. In `UpdateHorizonProxy` / the log emission, substitute `smoothed`
   for `state.lastInterpolatedVelocity` ONLY in the log line and in the
   `sqrMagnitude < 0.01` fallback check. Keep the actual
   horizon-forward computation on the instant sample.
3. Add a unit test: construct a sequence of `lastInterpolatedVelocity`
   values including one zero-transient, run the EMA, assert the
   smoothed value never drops below 0.1 magnitude.
4. Reset `smoothedPlaybackVelocity` in `ResetWatchState` so it doesn't
   leak across watch sessions.

## Implementation checklist (only if Option B/C/D chosen)

- [ ] Pick an option (A / B / C / D).
- [ ] If A: add the todo entry and commit. No code change.
- [ ] If B: update both interpolator sites in `ParsekFlight.cs`, add
      unit test for the reversal case, verify no other caller of the
      interpolator breaks.
- [ ] If C: add EMA field + reset site, one unit test, update log
      format if needed. Document the semantic change in the log line.
- [ ] If D: update interpolator, document the semantic shift in XML
      doc, audit all callers, update tests that pin specific
      interpolated velocity values.
- [ ] CHANGELOG / todo updates.
- [ ] Build, deploy, full test suite.
- [ ] Commit, push, PR.

## Out of scope

- Any change to trajectory recording format or sampling cadence.
- Any change to the bracketing logic in `TrajectoryMath.InterpolatePoints`.
- Any change to non-camera consumers of `state.lastInterpolatedVelocity`
  (e.g. sound pitch, engine FX modulation).
