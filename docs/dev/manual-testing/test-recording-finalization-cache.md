# Recording Finalization Cache Manual Checklist

Use this checklist after the recording-finalization cache phases land. Run it from a disposable save.

## Automated Runtime Canaries

1. Enter FLIGHT with any non-EVA vessel.
2. Open the in-game test runner with `Ctrl+Shift+T`.
3. Run the `RecordingFinalization` category.
4. Expected result: all tests pass.
5. Export results from the runner.

These canaries are synthetic but run inside the live KSP runtime. They pin the cache applier, deletion-UT trimming, stable background cache application, non-scene active crash fallback, and failed-refresh cache preservation. The gameplay canaries below are still required for real vessel unload/delete evidence.

## Gameplay Canaries

### Atmospheric Booster Unload

1. Launch a simple two-stage vessel with detachable boosters.
2. Start recording before launch.
3. Separate an atmospheric booster while the core continues upward.
4. Fly the core until the booster leaves the physics bubble or is deleted by stock KSP.
5. End the flight or return to the Space Center.

Expected:

- The booster child recording ends as `Destroyed`.
- Its recording has a predicted tail after the last authored sample.
- `KSP.log` contains `[FinalizerCache] Apply accepted` with a background consumer path.
- If stock deletion happens before the predicted impact, terminal UT is the deletion UT.

### Stable Background Orbiter

1. Put a vessel with at least two controllable pieces in stable orbit.
2. Split the craft and focus one controllable vessel.
3. Leave the sibling coasting unfocused in stable orbit.
4. End the flight or return to the Space Center.

Expected:

- The background sibling finalizes as `Orbiting`, not `Destroyed`.
- The recording keeps terminal orbit metadata for the correct body.
- Logs show `[FinalizerCache]` cache application or a live stable terminal path, with no degraded inference warning.

### Scene Exit Mid-Burn

1. Start recording during powered ascent.
2. While still thrusting, exit to the Space Center.
3. Reopen the recording and inspect ghost playback.

Expected:

- The recording continues past the last authored point using a synthetic tail.
- Logs show a fresh scene-exit finalizer path first; cache is only a fallback if the vessel is already missing.

### Planned Maneuver Node

1. In orbit, add a stock maneuver node that would change the future orbit.
2. Do not execute the burn.
3. End the recording or exit the scene.

Expected:

- Finalization follows the actual current vessel state, not the hypothetical node.
- Logs may report a maneuver-node boundary, followed by fallback to current-state propagation.

### Focused Crash

1. Record a focused reentry or descent that crashes.
2. Let the stock crash/destruction flow complete.
3. Reach the post-destruction pending-tree flow.

Expected:

- The active recording finalizes as `Destroyed`.
- The endpoint includes the cached synthetic tail instead of only stale last-sample inference.
- Logs show `[FinalizerCache] Apply accepted` before any non-scene destroyed fallback.

## Log Sweep

After the run, collect logs:

```powershell
python scripts/collect-logs.py recording-finalization-cache
```

Check `KSP.log` for:

- `[FinalizerCache]`
- `[PatchedSnapshot]`
- `[Extrapolator]`
- `[BgRecorder]`

Unexpected degraded paths to investigate:

- `Apply rejected` immediately followed by a destroyed fallback.
- `Refresh failed; preserving previous cache` every frame instead of at cadence.
- `inferred` terminal state for a recording that should have had a cache.
- missing sidecar persistence after a background terminal path.

## Calibration

Repeat one atmospheric split with three or more background debris vessels. The expected log cadence is one refresh per dynamic vessel roughly every five seconds, plus forced refreshes at lifecycle events. Do not lower the cadence unless logs show stale cache application is common in normal play.
