# Probe Tail Orbit Spawn — KSP State-Vector Frame Mismatch

**Status:** OPEN. Investigation 2026-05-10. Worktree `Parsek-investigate-probe-tail-orbit`, branch `investigate-probe-tail-orbit`.

**Symptom:** After a Re-Fly, watching the resulting ghosts to completion, the upper-stage ghost spawns into a real vessel at the end of its recording but the probe-booster ghost does not — even though both end in stable orbits. Reproduced by `logs/2026-05-10_2123` against tip `30bae837` (`origin/main`).

## TL;DR

`VesselSpawner.TryDeriveTerminalOrbitSeedFromTrajectoryTail` (and a handful of sibling reseed sites) call KSP's `Orbit.UpdateFromStateVectors` with **world-absolute position and Y-up Unity-axis velocity**, but the API contract requires **body-relative position and Z-up Planetarium-axis vectors** — i.e. `(pos − body.position).xzy` and `vel.xzy`. The result is a structurally-valid but physically-wrong orbit whose periapsis is below the surface, which the safety gate rightly rejects. The probe never spawns.

The previous v0.9.2 fix that introduced the tail-derive helper got it right *as far as it walked* — it picks the correct tail frame, frees the spawn from the stale on-rails segment — but it left in place the same frame-mismatch error that already lived in the older sibling call sites. So the helper produces an answer; just not the right one.

## Inputs (from the captured save + log)

Recording `rec_f1363fc127ab47a28812ce4be6515453` ("Kerbal X Probe", Re-Fly fork in tree `f3bac1b9…`):

| Field | Value | Source |
|---|---|---|
| `terminalState` | `0` (Orbiting) | [persistent.sfs:1120](../../../logs/2026-05-10_2123/saves/s15/persistent.sfs:1120) |
| `tOrbSma` / `tOrbEcc` | 4 547 677 m / 0.822 | [persistent.sfs:1126](../../../logs/2026-05-10_2123/saves/s15/persistent.sfs:1126) |
| Periapsis from saved tOrb | sma·(1−ecc) − R = 4 547 677 · (1 − 0.82238) − 600 000 ≈ 207 760 m (≈ 208 km altitude) | derived |
| Last absolute coast frame | `ut=453.66, lat=−0.021°, lon=−36.66°, alt=208 283 m, vel=(296.02, 3.84, −2806.12)` | [`rec_f1363fc…prec.txt`](../../../logs/2026-05-10_2123/parsek/Recordings/rec_f1363fc127ab47a28812ce4be6515453.prec.txt) |
| Last `OrbitSegment` (stale ascent) | `epoch=142.16, sma=512 941, ecc=0.575` (periAlt = −381 km, sub-surface) | same |

Analytic check on the tail frame (Kerbin: μ = 3.5316 × 10¹² m³/s², R = 600 000 m):

```
r = R + alt = 808 283 m
v² = 296² + 3.84² + 2806² = 7 961 267 m²/s²
ε = v²/2 − μ/r = 3.981×10⁶ − 4.370×10⁶ = −3.89×10⁵ m²/s²   (bound ellipse)
sma = −μ/(2ε) = 4.54 × 10⁶ m   ✓ matches saved tOrbSma
```

The state vector itself is consistent; the recording is correct.

## What the spawn path actually does ([KSP.log:101040–101290](../../../logs/2026-05-10_2123/KSP.log))

1. `currentUT = 455.25` (1.59 s after `tailUT = 453.66`).
2. `TryDeriveTerminalOrbitSeedFromTrajectoryTail` reseed (the relevant block):

   ```csharp
   Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
   Vector3d worldVel = new Vector3d(velocity.x, velocity.y, velocity.z);
   reseeded.UpdateFromStateVectors(worldPos, worldVel, body, candidate.ut);
   ```

   Returns `sma = 567 357, ecc = 0.7149, periAlt = −438 222 m, apoAlt = 372 936 m` — physically nonsense.
3. `TerminalOrbitSpawnSafety.Evaluate` propagates that orbit to `currentUT`, gets `propagatedAlt = −98 949 m` (under the surface), returns `DeferUntilSafe` with `nextSafeUT = 557.32`.
4. Retry at `currentUT = 559.25`. Now `spawnUT − tailUT = 105.59 s > TailDerivedOrbitMaxRotationDriftSeconds = 30 s`, so the helper bails with `rotation-drift-out-of-bounds`. The picker falls through to `TryGetEndpointAlignedRecordedOrbitSeedForSpawn`, which returns the only stored segment — the pre-burn ascent at `epoch=142.16, sma=512 941, ecc=0.575, periAlt = −381 796 m`.
5. Safety gate: `decision = CannotSpawnSafely, reason = periapsis-below-safe-altitude`. `WARN Deferred spawn cannot execute safely`.

Probe is retired without ever materialising. The upper stage spawns because its tail carries an authoritative `OrbitalCheckpoint` `OrbitSegment` (`sma = 812 460, ecc = 0.006`, ≈ circular at 212 km), so the picker never has to call into the broken tail-derive at all.

## Root cause — KSP API contract violation

`Orbit.UpdateFromStateVectors` decompiled from `Assembly-CSharp.dll`:

```csharp
/// <param name="pos">The initial position of the object RELATIVE TO refBody at UT (YZ flipped)</param>
/// <param name="vel">The initial velocity of the object (YZ flipped)</param>
public void UpdateFromStateVectors(Vector3d pos, Vector3d vel, CelestialBody refBody, double UT)
{
    pos = Planetarium.Zup.LocalToWorld(pos);
    vel = Planetarium.Zup.LocalToWorld(vel);
    UpdateFromFixedVectors(pos, vel, refBody, UT);
    ...
}
```

The canonical KSP wrapper at the same call depth (`Orbit.OrbitFromStateVectors`):

```csharp
public static Orbit OrbitFromStateVectors(Vector3d pos, Vector3d vel, CelestialBody body, double UT)
{
    Orbit orbit = new Orbit();
    orbit.UpdateFromStateVectors((pos - body.position).xzy, vel.xzy, body, UT);
    return orbit;
}
```

Two transforms the caller is responsible for, and both are required:

1. **`(pos − body.position)`** — convert world-absolute to body-relative. `body.position` is the body's current world-space transform position (decompiled `CelestialBody.cs:399–408`, set from `bodyTransform.position`). It is **not** zero in general — it is zero only when the active reference body coincides with the active vessel's main body and Krakensbane has snapped on top of it. Outside that special case, the magnitude can be anywhere from ~hundreds of metres up to interplanetary distances depending on which body is the active reference and where Krakensbane sits. `body.GetWorldSurfacePosition(lat, lon, alt)` itself implements `BodyFrame.LocalToWorld(GetRelSurfacePosition(...).xzy).xzy + position`, so the `+ position` is exactly the term we have to subtract back out.
2. **`.xzy`** — KSP's `Orbit` math runs in a Z-up "Planetarium" frame, but Unity's `rb.position` / `rb.velocity` (and therefore `body.GetWorldSurfacePosition` and the recorder's stored velocity) are in Y-up Unity world. `.xzy` is the per-axis swap. Magnitudes are preserved by the swap, so `.xzy` alone does not change `sma` — but it does change orbit *orientation* (LAN, argPe, inclination, mean anomaly), so once we fix `(1)` we must fix `(2)` to land on the right ellipse and not just a same-shape ellipse pointed somewhere wrong.

The Parsek tail-derive does neither. It feeds `worldPos` and `worldVel` as-is.

### Why we get a bound elliptical garbage answer instead of obvious nonsense

`sma` is determined by `|pos|` and `|v|` only. Without the `(pos − body.position)` step, `|pos|` is whatever `body.GetWorldSurfacePosition` returns. In the captured run, that magnitude happens to be ≈ 498 km (numerically inferred: with `|v| = 2822 m/s`, the only `|pos|` that yields the observed `sma = 567 357` is `|pos| ≈ 498 km` via `μ/r = v²/2 + μ/(2·sma)`). That magnitude is a physical artefact of where Krakensbane and the active reference body sat at the moment of the spawn call — not of any property of the recording. A different scene state at spawn time would produce a different bogus magnitude, which is exactly why this bug looks "intermittent" in the field: the upper stage and the probe sat on adjacent rails at the same moment but only one of them tripped the gate, because only one of them needed the broken helper.

## Other call sites with the same bug

Audit of `UpdateFromStateVectors` call sites — split by where the position and velocity inputs come from, because the right transform differs by source frame:

| File:line | `pos` source (frame) | `vel` source (frame) | Position fix | Velocity fix |
|---|---|---|---|---|
| `Source/Parsek/VesselSpawner.cs:1001` | `body.GetWorldSurfacePosition` (Y-up world, absolute) | caller-provided `velocity` (frame depends on caller — needs audit) | `(pos − body.position).xzy` | **audit before applying** |
| `Source/Parsek/VesselSpawner.cs:4054` | `body.GetWorldSurfacePosition` (Y-up world, absolute) | `TryResolveEndpointStateVector` out-param (frame depends on resolver — needs audit) | `(pos − body.position).xzy` | **audit before applying** |
| `Source/Parsek/VesselSpawner.cs:5135` | `body.GetWorldSurfacePosition` (Y-up world, absolute) | `TrajectoryPoint.velocity` (recorder Y-up world; per `FlightRecorder.SampleCurrentVelocity` either `obt_velocity` or `rb_velocityD + Krakensbane.GetFrameVelocity()`) | `(pos − body.position).xzy` | `vel.xzy` |
| `Source/Parsek/GhostMapPresence.cs:5562, 5785` | `resolution.WorldPos` (frame depends on resolver — needs audit) | `TrajectoryPoint.velocity` (Y-up world, same as 5135) | likely `(pos − body.position).xzy` (audit `WorldPos` shape) | `vel.xzy` |
| `Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs:1090` | `body.GetWorldSurfacePosition` (Y-up world, absolute) | `TrajectoryPoint.velocity` (Y-up world) | `(pos − body.position).xzy` | `vel.xzy` |
| `Source/Parsek/FlightRecorder.cs:7887` | `vessel.orbit.getPositionAtUT(startUT)` returning `referenceBody.position + getRelativePositionAtT(T).xzy` (Y-up world, **absolute**) — `rawWorld` after `TryApplyReFlyRecordingFrameOffsetToWorld` (Y-up world, fork-shifted) | `vessel.orbit.getOrbitalVelocityAtUT(startUT)` returning `Planetarium.Zup.WorldToLocal(...)` (**already Z-up Planetarium / body-relative inertial**) | `(canonicalWorld − body.position).xzy` | **none — `vel` is already Zup**; applying `.xzy` here would double-flip and produce a worse orbit |
| `Source/Parsek/TimeJumpManager.cs:511` | `v.orbit.pos` (already body-relative Zup) | `v.orbit.vel` (already body-relative Zup) | none | none — already correct |

So the doc-comment claim of "every reseed except `TimeJumpManager` has the same bug" was sloppy. The shared error is **the missing `(pos − body.position).xzy` on the position when the source is world-absolute**. The velocity story is per-site:

- Five sites read `TrajectoryPoint.velocity` directly (recorder Y-up world). They need `vel.xzy` because the recorder doesn't pre-flip when storing. Confirmed for `VesselSpawner.cs:5135`, `IncompleteBallisticSceneExitFinalizer.cs:1090`. The two `GhostMapPresence` sites read `point.velocity` from a `TrajectoryPoint` and their `resolution.WorldPos` shape needs a quick audit before fixing both axes mechanically.
- One site (`FlightRecorder.cs:7887`) reads `getOrbitalVelocityAtUT`, which is already in Zup. That one needs the position fix only and **must not** get a blanket `vel.xzy`.
- Two sites (`VesselSpawner.cs:1001, 4054`) take a caller-provided `velocity` whose frame I haven't traced — those need a per-call-site audit before applying any transform.

Player-visible breakage from the broken sites is masked today by:

- The `TryGetEndpointAlignedRecordedOrbitSeedForSpawn` path winning whenever the recording carries any valid stored `OrbitSegment` (which is the common case — that's why we don't get reports for "every Re-Fly").
- The ghost-map orbit lines being purely cosmetic, drawn inside the wrong-LAN ellipse where most players wouldn't notice unless they superimposed the live and recorded orbits for the same body.

The probe-spawn case is the first one we've caught where the broken helper is the *only* path the picker has, so the wrong answer goes all the way to the visible "vessel never spawns" outcome.

The `TerminalOrbitFromTail_DerivesPostBurnCircularOrbit` in-game test (`Source/Parsek/InGameTests/RuntimeTests.cs:705`) does not catch this because its assertions only verify "sma > 0", "ecc < 1", and "periAlt > safeAlt" against a single synthetic recording where the bug happened to fall on the safe side of the safety gate (or the test wasn't actually rerun after the helper landed — the captured `parsek-test-results.txt` for this session has zero `[SpawnTerminalOrbit]` entries, so we don't even have a real green run).

## Proposed fix

### Primary — fix the failing spawn path first; audit-then-fix the rest

Land two narrowly-scoped helpers so the call sites stop reaching for `body.GetWorldSurfacePosition` + `UpdateFromStateVectors` directly. The helpers are **two** because the inputs split into two natural shapes:

```csharp
internal static class OrbitReseed
{
    /// <summary>
    /// Build orbit elements from a body-fixed lat/lon/alt position and a
    /// recorder-frame velocity (Y-up Unity world axes — what
    /// FlightRecorder.SampleCurrentVelocity emits).
    /// </summary>
    internal static void FromLatLonAltAndRecordedVelocity(
        Orbit dst, CelestialBody body,
        double lat, double lon, double alt,
        Vector3d recordedVelWorldYup,
        double ut)
    {
        Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
        dst.UpdateFromStateVectors(
            (worldPos - body.position).xzy,
            recordedVelWorldYup.xzy,
            body,
            ut);
    }

    /// <summary>
    /// Build orbit elements from a world-absolute Y-up position and a
    /// pre-Zup-local velocity (e.g. the output of Orbit.getOrbitalVelocityAtUT).
    /// </summary>
    internal static void FromWorldPosAndZupVelocity(
        Orbit dst, CelestialBody body,
        Vector3d worldPosYup,
        Vector3d velAlreadyZup,
        double ut)
    {
        dst.UpdateFromStateVectors(
            (worldPosYup - body.position).xzy,
            velAlreadyZup,           // no second flip
            body,
            ut);
    }
}
```

Then route the failing path (`VesselSpawner.cs:5135`, `TryDeriveTerminalOrbitSeedFromTrajectoryTail`) through `FromLatLonAltAndRecordedVelocity`. That single change ships the probe-spawn fix.

Audit the sibling sites separately (each is a small surface):

- `IncompleteBallisticSceneExitFinalizer.cs:1090`: same shape as 5135 — recorded `TrajectoryPoint.velocity` plus body-fixed lat/lon/alt — route through `FromLatLonAltAndRecordedVelocity`.
- `FlightRecorder.cs:7887`: world-absolute pos + Zup velocity — route through `FromWorldPosAndZupVelocity`. Subtract `body.position` from the (offset-corrected) `canonicalWorld`, leave the velocity alone.
- `GhostMapPresence.cs:5562, 5785`: confirm `resolution.WorldPos` is Y-up world-absolute (its construction site needs a quick read before fixing). Velocity is `point.velocity`, recorder Y-up — route through `FromLatLonAltAndRecordedVelocity` if the position turns out to come from a `body.GetWorldSurfacePosition`-shaped path, otherwise `FromWorldPosAndZupVelocity` plus a `.xzy` on velocity.
- `VesselSpawner.cs:1001`: spawn-time orbit subnode rebuild from `body.GetWorldSurfacePosition` + caller-provided `velocity`. Trace the `velocity` arg back to its callers (it threads through several spawn paths) — pick the right helper per source.
- `VesselSpawner.cs:4054`: `TryRepairSnapshotOrbit` endpoint state-vector branch. Same — depends on what `TryResolveEndpointStateVector` returns for the velocity frame.

`TimeJumpManager.cs:511` already takes `v.orbit.pos` and `v.orbit.vel`, which are body-relative Zup by `Orbit`'s internal contract — needs no change.

The split-helper shape (rather than one monolithic helper) is what stops a future reader from mechanically applying `.xzy` everywhere and re-introducing the FlightRecorder.cs:7887 trap.

### Secondary — the deferred-retry trap

Even with the primary fix in place, the retry path remains brittle:

- First attempt fires the corrected tail-derive at `spawnUT ≈ tailUT` (drift = 0).
- If the safety gate defers (e.g. spawning during atmosphere over an apoapsis), the policy re-fires the spawn at `nextSafeUT`.
- On the retry, `spawnUT − tailUT > 30 s` and the rotation-drift gate hard-skips the tail-derive, falling back to whatever stored `OrbitSegment` the picker can find.

For the probe shape (orbiting recording with no segment after the closing burn), the fallback is the stale ascent ellipse; the gate is guaranteed to reject. Two cleaner options:

1. **Cache the tail-derived elements once** — compute the orbit on the first attempt, propagate it forward via Kepler's equation on each retry. The `epoch` is the tail UT; `meanAnomalyAtSpawnUT` is already what `TryBuildRecordedTerminalOrbitForSpawn` computes via `TimeJumpManager.ComputeEpochShiftedMeanAnomaly`. We just need to remember the tail-derived elements across retries instead of re-deriving them. Cleanest if we land them as a synthesised `OrbitSegment` on the recording itself at finalize time, which has the side benefit of also fixing the ghost-map orbit-line gap noted in `docs/dev/todo-and-known-bugs.md` ("ghost map orbit line drawn from stale OrbitSegment for orbiting recordings whose post-burn frames superseded it").
2. **Lift `TailDerivedOrbitMaxRotationDriftSeconds`** — the 30 s clamp protects LAN/argPe from drifting under the body's *current* rotation when `body.GetWorldSurfacePosition` is called at a UT later than the tail. With `(pos − body.position)` applied, the body-relative position is independent of where the body sits in the Krakensbane frame; only `BodyFrame` (the body's own rotation) still matters, and the LAN/argPe error per second is `body.angularVelocity × seconds`, which on Kerbin is `2.916 × 10⁻⁴ rad/s` ≈ 0.017°/s. Even at 600 s of drift, that's 10° of LAN error — meaningful for orbit visualisation but well within the 5 km safe-altitude margin in periapsis altitude (the linearisation of LAN error to periAlt error vanishes for purely-prograde orbits and stays sub-km for any sane inclination). 30 s is over-conservative; 600 s would be fine, and "no clamp at all for the safety check, log the drift" is defensible.

Option (1) is the better fix, because it also closes the ghost-map orbit-line bug. Option (2) is a one-line bandage if we want to ship the spawn fix sooner without committing to the recording-shape change.

### Coverage

Existing in-game test at `RuntimeTests.cs:705` should stay, but its acceptance criteria need to harden — the present assertions (`sma > 0`, `ecc < 1`, `periAlt > safeAlt`) all happen to be true under the buggy frame in some inputs. Add element-by-element checks against a known orbit:

```csharp
// circular at 200 km on the equator going prograde
Assert.InRange(sma, 800_000, 810_000);                 // tight
Assert.InRange(ecc, 0, 0.005);                          // near-circular
Assert.InRange(inclination, 0, 0.5);                    // ~equatorial
Assert.InRange((LAN + 360) % 360, expectedLAN ± 1°);    // pinned by lon and time
Assert.InRange(argPe, expectedArgPe ± 1°);
```

LAN/argPe pin is what catches the missing `.xzy`; the tight sma window is what catches the missing `(pos − body.position)`.

A complementary xUnit `OrbitReseedTests` exercising the new helper against synthetic state vectors (no `body` dependency in the math itself — Kerbin's `gravParameter`, `Radius`, and a stub `body.position` are all the helper needs) would be cheap and run in CI. The Unity-runtime test stays for end-to-end coverage of `body.GetWorldSurfacePosition` returning what we think it does.

## Open questions for the fix author

- Should the secondary fix land in the same PR as the primary, or stage them? Argument for splitting: the primary is a focused frame-correction, low risk per call site; the secondary changes recording shape (a cached `OrbitSegment` synthesised at finalize) which has its own audit surface. Argument for bundling: the present probe-spawn repro only manifests after a deferral, so a primary-only PR would need a synthetic test that triggers a deferral to demonstrate the spawn going through.
- Are there callers of `IncompleteBallisticSceneExitFinalizer.TryBuildReseededPredictedTailSegmentFromRecordedAnchor` whose output already gets re-validated via the residual-offset check (lines 1085–1087, 1122–1124)? If yes, those residuals are currently expected to be huge (broken pos in, broken pos out, comparison meaningless). After the fix, residuals should drop to near-zero and any threshold tuned to the broken values needs revisiting.
- `FlightRecorder.cs:7887` is a *canonical* orbit built from a round-trip through `vessel.orbit.getPositionAtUT` (Y-up world) + `getOrbitalVelocityAtUT` (already Zup). The position fix is `(canonicalWorld − body.position).xzy`; the velocity must be left alone. Worth confirming whether any downstream reader of that canonical orbit cares about LAN/argPe at all — if everyone consumes only `sma`/`ecc`, the site has been silently working anyway, but routing it through the new helper still helps future readers.
- For each `VesselSpawner` site (1001, 4054), trace the `velocity` argument back to its origin and decide per call site. If a caller passes `Orbit.*` output, no `.xzy` on velocity. If a caller passes a recorder `TrajectoryPoint.velocity`, apply `.xzy`. If a caller passes a freshly-sampled live `vessel.obt_velocity`, that's body-relative inertial Y-up (since `Vessel.obt_velocity` is `Vessel.GetObtVelocity()` which is inertial in world axes per KSP's contract) and needs `.xzy`. The two-helper split makes this the call-site's choice instead of the helper's guess.

## File pointers

- The buggy primary helper: `Source/Parsek/VesselSpawner.cs:5039–5178` — `TryDeriveTerminalOrbitSeedFromTrajectoryTail`.
- The buggy lines: `5122–5135`.
- The picker that fans into it: `Source/Parsek/VesselSpawner.cs:4881–5002` — `TryBuildRecordedTerminalOrbitForSpawn`.
- The deferred-retry policy: `Source/Parsek/ParsekPlaybackPolicy.cs:279, 323` — both `pendingSpawn && rec.TerminalSpawnCannotSpawnSafely` arms.
- The KSP API contract: `Orbit.UpdateFromStateVectors` decompiled from `Kerbal Space Program/KSP_x64_Data/Managed/Assembly-CSharp.dll` (line 688 of the decompilation), `Orbit.OrbitFromStateVectors` at line 2661 — the canonical wrapper showing the correct `(pos - body.position).xzy, vel.xzy` form.
- The previously-shipped helper PR: `docs/dev/todo-and-known-bugs.md` Done item "v0.9.2 Re-Fly spawn refused circularized upper stage with stale on-rails OrbitSegment".
