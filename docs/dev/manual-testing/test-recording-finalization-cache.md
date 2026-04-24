# Recording Finalization Cache Manual Checklist

Use this checklist after the recording-finalization cache phases land. Run it from a disposable save.

## Automated Runtime Canaries

1. Enter FLIGHT with any non-EVA vessel.
2. Open the in-game test runner with `Ctrl+Shift+T`.
3. Run the `RecordingFinalization` category.
4. Expected result: all tests pass.
5. Export results from the runner.

These canaries are synthetic but run inside the live KSP runtime. They pin the cache applier, deletion-UT trimming, stable background cache application, and non-scene active crash fallback (failed-refresh cache preservation is covered by the headless xUnit suite, which also asserts log content). The gameplay canaries below are still required for real vessel unload/delete evidence.

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

1. Build a craft with a probe core (or second command pod) docked to the main command module via a decoupler. Both pieces must be independently controllable (probe core needs SAS + power).
2. Launch and circularize at ~80 km Kerbin orbit; start recording before launch.
3. Decouple the probe-core / second pod and switch focus to the main command module.
4. Wait at least 10 real-time seconds with the sibling coasting (this lets the 5 s background-cache refresh cadence land at least one Fresh sample).
5. Return to the Space Center.

Expected:

- The background sibling finalizes as `Orbiting`, not `Destroyed`.
- The recording keeps terminal orbit metadata for the correct body.
- Logs show `[Parsek][INFO][FinalizerCache] Refresh accepted ... terminal=Orbiting` for the sibling at least twice during the wait, then a `Finalization source=cache ... terminal=Orbiting` (or live stable terminal stamp) at scene exit. No `inferred` line for the sibling recording.

### Scene Exit Mid-Burn

1. Start recording during powered ascent.
2. While still thrusting, exit to the Space Center.
3. Reopen the recording and inspect ghost playback.

Expected:

- The recording continues past the last authored point using a synthetic tail.
- Logs show a fresh scene-exit finalizer path first; cache is only a fallback if the vessel is already missing.

### Planned Maneuver Node

1. In orbit, add a stock maneuver node whose burn vector would meaningfully change the orbit (e.g., a 200 m/s prograde burn).
2. Do not execute the burn.
3. End the recording or exit the scene.

Expected:

- Finalization discards the stock patched-conic tail (which would have followed the hypothetical post-burn trajectory) and falls back to extrapolator-driven propagation of the current vessel state.
- `KSP.log` contains `[Parsek][INFO][PatchedSnapshot] ... maneuver-node boundary detected ... discarding stock patched-conic tail and falling back to current-state propagation` for the active recording.
- The terminal orbit metadata reflects the pre-burn state, not the planned post-burn state.

### Focused Crash

1. Record a focused reentry or descent that crashes (recommended: deorbit a probe with no parachute and let it impact terrain).
2. Let the stock crash/destruction flow complete (wait for the destruction confirmation deferred check, ~1 s after impact).
3. Reach the post-destruction pending-tree flow.

Expected:

- The active recording finalizes as `Destroyed`.
- The endpoint includes the cached synthetic tail instead of only stale last-sample inference.
- `KSP.log` contains `[Parsek][INFO][BgRecorder] Finalization source=cache consumer=DeferredDestructionCheck ... terminal=Destroyed` for the destroyed vessel, followed by `PersistFinalizedRecording` for the same recording.
- No `marking Destroyed` warning for that recording (that's the no-cache fallback path).

## Log Sweep

After the run, collect logs:

```powershell
python scripts/collect-logs.py recording-finalization-cache
```

Check `KSP.log` for:

- `[FinalizerCache]` — refresh accept/decline + apply accept/reject
- `[PatchedSnapshot]` — patched-conic + maneuver-node boundary
- `[Extrapolator]` — ballistic extrapolation fallback
- `[BgRecorder]` — background apply + sidecar persistence

Healthy patterns (these should appear):

- `[Parsek][VERBOSE][FinalizerCache] Refresh accepted: owner=... rec=... terminal=...` — periodic cache refresh, roughly every 5 s per dynamic vessel.
- `[Parsek][INFO][FinalizerCache] Apply accepted: consumer=... rec=... terminal=...` — scene-exit consumer landed the cached tail.
- `[Parsek][INFO][BgRecorder] Finalization source=cache consumer=... terminal=...` — background-end consumer landed the cached tail.

Degraded patterns to investigate:

- `[Parsek][WARN][FinalizerCache] Apply rejected: ... reason=...` immediately followed by an `inferred` line for the same recording (cache present but the applier refused it).
- `[Parsek][WARN][FinalizerCache] Refresh failed; preserving previous cache` more than once per vessel per minute (refresh path is permanently broken, not just transiently slow).
- `[Parsek][INFO][Flight] FinalizeTreeRecordings: ... inferred ... from trajectory` for a recording that should have had a cache (cache resolver missed the lookup).
- `[Parsek][WARN][Flight] Finalization source=cache applied stale cache ...` more than once per scene exit (refresh cadence is not keeping up).
- Missing `PersistFinalizedRecording` line after a background terminal path.

## Calibration

Repeat one atmospheric split with three or more background debris vessels. The expected log cadence is one refresh per dynamic vessel roughly every five seconds, plus forced refreshes at lifecycle events. Do not lower the cadence unless logs show stale cache application is common in normal play.
