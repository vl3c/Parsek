# Re-aim Arrival-Seam Restitch (S4) - Design + Implementation Plan

Status: PLAN (pre-implementation, revised after clean-Opus design review). Branch `reaim-arrival-seam` off `main` (after #982).
Builds on: re-aim interplanetary transfers (shipped #981) and the descent polyline coverage (shipped #982).
This is the deferred S4 / S2->S3 arrival-seam item from `docs/dev/plans/reaim-interplanetary-transfers.md` and the deferred entry in `docs/dev/todo-and-known-bugs.md`.

Authored from two read-only investigations (seam mechanics + #982 review), direct code reads, and a clean-Opus design review that corrected the rotation math (frame, full-frame vs direction-only, analytic asymptote, time-shift) and tightened the v1 scope. The implementer must still verify the exact KSP Orbit element conventions against the live API while coding.

## 1. Problem

A looped Kerbin->Duna recorded mission replays as a map ghost. Each synodic window the heliocentric Sun-transfer leg is re-planned (live Lambert) to the target's CURRENT position and substituted; the launch / parking / escape and the target-body arrival + capture + descent legs replay AS RECORDED. The recorded target-body segments are Duna-relative Kepler `OrbitSegment`s rendered via `new Orbit(elements, liveDuna)`, so they follow live Duna, but they keep their ORIGINAL recorded approach orientation (inc / LAN / argPe relative to Duna's inertial frame).

Result: the re-aimed transfer reaches Duna's SOI from the re-planned direction (mesh ~14.7 Mm from Duna), then the recorded arrival hyperbola is spliced on with its recorded approach vector (~33 Mm out on a different bearing), so the ghost jumps back out and re-approaches along the recorded direction. The seam jump is ~1.37 Gm (about 28 Duna-SOI radii). On the map: "the ghost approached, then went back and approached again."

Evidence (`logs/2026-05-31_0311_duna-descent-position`): `ReaimSeam seg#9->10 body=Duna ... jump=1374718336m`; `dist[Duna]` 14.7 Mm -> 33 Mm -> re-approach. A REAL position gap (both endpoints share the identical recorded->live UT mapping; not a clock desync or render-span artifact).

## 2. Goal / Non-goals

Goal: close the gross arrival seam so the re-aimed ghost's target-body approach hyperbola + capture join the re-aimed transfer continuously, eliminating the ~1.37 Gm jump-back-out, down to a small SOI-edge residual set by the |v_inf|-magnitude difference.

v1 SCOPE (this PR): rotate the recorded target-body ORBIT segments (approach hyperbola + capture + low-orbit descent segments) by a rigid rotation R about the target body's center. This fixes the gross seam for the ghost MESH and the map ORBIT LINE. The SOI-crossing time-shift is COMPUTED and LOGGED as a playtest diagnostic but NOT applied in v1 (applying it would re-introduce a smaller seam against the un-shifted full-span transfer end; its correct application is a fast-follow, see section 4.4).

EXPLICIT v1 LIMITATION (fast-follow, NOT this PR): the descent POLYLINE draws from the RAW recorded body-fixed lat/lon/alt points (`GhostTrajectoryPolylineRenderer` reads `RecordingStore.CommittedRecordings` directly, not the re-aim resolver), so it stays at the recorded landing site while the mesh descends to the rotated (re-aimed) site. v1 leaves this descent-tail polyline-vs-mesh offset. Rotating the body-fixed descent points to follow requires reaching the renderer's per-frame `localScaled` cache with a per-window R-version invalidation (review finding M3); that is best done AFTER a playtest validates R, so it is a separate fast-follow PR. The PR description must state this limitation plainly so the owner can weigh it.

Non-goals (v1):
- Exact periapsis-altitude / arrival-timing match (the |v_inf|-magnitude residual remains; accepted class, same as the departure seam).
- Keeping the recorded landing SITE fixed (ROTATE-ALL moves it; see section 3).
- Same-parent (intra-Kerbin-system) missions (no heliocentric leg; untouched).
- Steeply-inclined-target re-aim (already declines to faithful replay).

## 3. Product decision (v1 default: ROTATE-ALL)

- ROTATE-APPROACH-ONLY (keep recorded landing site): to hit the SAME recorded surface lat/lon from a NEW direction requires RE-SOLVING the arrival (new plane, new capture, new descent) -- a non-rigid synthesis of an arrival the recording does not contain. Not v1-viable.
- ROTATE-ALL (rigid rotation; landing site MOVES): one rotation R about the target body's center maps the recorded incoming approach FRAME onto the re-aimed one and carries the whole chapter. Internally seam-free, cheap, pure-testable.

Decision for v1: ROTATE-ALL (orbit segments this PR, descent-points fast-follow). Rationale: it is a genuine rigid transform; it kills the gross seam (the actual bug); it is consistent with the "the transfer is re-planned, not byte-faithful" contract; the alternative is non-rigid synthesis.

This REVERSES the earlier deferred-todo stance (which prioritized keeping the landing site). Flagged for the owner: the only honest alternative is keep-deferred (status quo, no rotation). No cheap middle option.

## 4. The restitch math (corrected per review C1 / C2 / M1 / M2)

### 4.1 Frame discipline (review C1)

Everything for the ELEMENT rotation is computed and applied in KSP's swizzled Zup body-relative frame -- the SAME frame `new Orbit(inc, ecc, sma, LAN, argPe, mEp, epoch, body)` interprets its angles in (the actual playback reconstruction). Do NOT apply `.xzy` to velocities used to derive element-space rotation: `getOrbitalVelocityAtUT(...)` already returns Zup-local; `.xzy` would convert to Y-up world and corrupt the orientation (the contract documented in `OrbitReseed.cs`). A separate Y-up `R_world` (same rotation, swizzled basis) is only needed by the descent-point rotation, which is the fast-follow, so v1 needs only the Zup R.

### 4.2 Full-frame rotation, not direction-only (review C2)

Match BOTH the incoming v_inf asymptote direction AND the orbital-plane normal, so the recorded arrival's PLANE rotates onto the re-aimed approach plane (a direction-only `FromToRotation` leaves the b-plane unmatched -> up to ~1 SOI-radius position residual). For each conic build an orthonormal frame from its inbound asymptote `s` and its plane normal `h` (for a hyperbola the asymptote lies in the orbital plane, so `s` is exactly perpendicular to `h`):

```
recorded:  x = s_rec,  z = h_rec_hat,  y = z cross x
re-aimed:  x'= s_re,   z'= h_re_hat,   y'= z' cross x'
R = [x' y' z'] * [x y z]^T     (maps the recorded incoming frame onto the re-aimed one)
```

R is a proper 3-DOF rotation about the body center. Apply R to the recorded arrival's state and read back (inc, LAN, argPe) via KSP's own conversion (see 5.1). Keep eccentricity, sma, meanAnomalyAtEpoch, epoch UNCHANGED (shape + along-orbit phase preserved). `orbitalFrameRotation` and `angularVelocity` are velocity-frame-relative, so they are NOT rotated (the mesh attitude follows the rotated velocity automatically).

HANDEDNESS GUARD (review re-pass): build R only when `dot(h_rec_hat, h_re_hat) > 0` (the recorded capture and the re-aimed approach share the prograde-normal sense). If the dot is negative, the recorded arrival and the re-aimed approach have opposite handedness; rotating would flip the orbit's travel direction (a retrograde join, ghost on the wrong side). In that case log it and fall back to faithful (no rotation) for that window, the same fail-safe the synthesizer uses elsewhere. The `y = z cross x` choice is self-canceling across both frames, so it is not a trap.

### 4.3 Analytic inbound asymptote from (e, h) (review M1)

Derive each incoming direction as the analytic hyperbolic asymptote from the conic's eccentricity vector `e` and angular-momentum vector `h` (frame-pure, UT-free; do NOT sample an instantaneous velocity at the SOI, which is not the asymptote):

```
e_hat = e / |e|              (unit periapsis direction)
h_hat = h / |h|              (unit plane normal)
q_hat = h_hat cross e_hat    (in-plane, prograde at periapsis)
s = normalize( sqrt(1 - 1/ecc^2) * e_hat  +  (ecc - 1/ecc) * q_hat )   (inbound v_inf direction, ecc > 1)
```

- Recorded side: build the recorded arrival Orbit from `ReaimMissionPlan.ArrivalLeg` (`new Orbit(elements, targetBody)`), read its (e, h) in Zup directly via KSP `Orbit.GetEccVector()` / `Orbit.GetOrbitNormal()` (avoid a second hand-rolled derivation), derive `s_rec`, `h_rec`.
- Re-aimed side: from the synthesized transfer's target-relative state at `soiEntryUT` in Zup: `r_rel = transfer.getRelativePositionAtUT(soiEntryUT) - targetBody.orbit.getRelativePositionAtUT(soiEntryUT)` (both Sun-relative Zup positions; their difference is the target-relative position -- NOT a literal `- 0`), `v_rel = transfer.getOrbitalVelocityAtUT(soiEntryUT) - targetBody.orbit.getOrbitalVelocityAtUT(soiEntryUT)` (Zup, no `.xzy`), then `h_re = r_rel cross v_rel`, `e_re = (v_rel cross h_re)/mu_target - r_rel_hat`, derive `s_re`, `h_re`. (`mu_target` = target body gravitational parameter.)

### 4.4 Time / phase alignment (review M2) -- v1 COMPUTES + LOGS, does NOT apply (post-review M1)

Rotation fixes geometry, not the SOI-crossing instant. The recorded-span image of the re-aimed handoff instant is `soiEntryUT + shift`; the recorded arrival sub-chain starts at `recordedArrivalUT`. The shift amount = `(soiEntryUT + shift) - recordedArrivalUT` (pure `ReaimSegmentAssembler.ComputeArrivalTimeShift`).

**v1 (post-review M1): COMPUTE this value and LOG it, but do NOT apply it.** In the validated playtest the window tof equalled the recorded tof so the shift was ~0. Applying a non-zero shift in v1 would re-introduce a smaller seam, on an unexercised path: the transfer leg always renders to `recordedArrivalUT` (full-span, the `ReplaceHeliocentricLeg` renderEnd clamp), so moving the rotated arrival sub-chain off `recordedArrivalUT` opens a discontinuity (a negative shift starts the arrival BEFORE the transfer end -> overlap, where `FindOrbitSegment` returns the first sort-order match; a positive shift -> coverage gap). The rotation ALONE already closes the gross ~1.37 Gm seam, and because it never touches the segment UTs the arrival stays anchored at `recordedArrivalUT`, contiguous with the transfer end by construction. v1 only logs the shift (a playtest diagnostic: tells us whether a sub-tof window ever produces a materially non-zero value).

**Fast-follow:** the correct APPLICATION moves the transfer render-END and the arrival start TOGETHER to `soiEntryUT + shift` (relaxing the `ReplaceHeliocentricLeg` renderEnd clamp so both endpoints move as one), done once a playtest shows the shift is ever materially non-zero. Deferred alongside the descent-polyline rotation.

## 5. Implementation (v1 = orbit-segment rotation; time-shift computed + logged, not applied -- post-review M1)

### 5.1 Rotation math: pure primitives + a KSP state-vector round-trip for the element read-back

Split the work so the GEOMETRY is pure xUnit and the element read-back uses KSP's own elements<->state conversion (correct by construction, which matters because we cannot playtest the Zup angle convention during this run; hand-deriving it would only be tested against itself).

PURE primitives (double precision, NO UnityEngine, xUnit-tested) in `ReaimSegmentAssembler` (or a small `ReaimRotation` helper):
- `InboundAsymptoteDir(double[] eVec, double[] hVec, double ecc) -> double[3]` (4.3 formula).
- `RotationFrameToFrame(double[] sFrom, double[] hFrom, double[] sTo, double[] hTo) -> double[3x3]` (4.2). Returns the proper rotation; callers check the 4.2 handedness guard `dot(hFrom_hat, hTo_hat) > 0` first.
- `RotateVector(double[3x3] R, double[] v) -> double[3]` (trivial; the only thing applied to the state vector).

UNITY element rotation (canary-tested, not xUnit; correct by construction): a live helper that, for each target-body segment, builds its Orbit (`new Orbit(elements, targetBody)`), samples its Zup state at `epoch` (`getRelativePositionAtUT(epoch)`, `getOrbitalVelocityAtUT(epoch)` -- both already Zup, NO `.xzy`), applies `RotateVector(R, .)` to BOTH pos and vel, calls `rotatedOrbit.UpdateFromStateVectors(rotPos, rotVel, targetBody, epoch)`, and reads back (inc, LAN, argPe, mEp, epoch) into a copy of the segment. Keep ecc/sma/orbitalFrameRotation/angularVelocity from the original (the round-trip preserves ecc/sma to round-off; copy them verbatim to avoid drift). Rotating the state vector rigidly rotates the orbit, so KSP's conversion yields the correctly-rotated elements without us touching the Zup angle convention by hand.

The segment-list driver (live, sits beside the resolver since it needs KSP Orbits): rotate every non-predicted, `bodyName == targetBody`, `startUT >= recordedArrivalUT - eps` segment via the Unity helper; pass all others through. Log (count rotated, count skipped non-target-bodied). Anchor on `recordedArrivalUT` (the classifier's arrival boundary), NOT "transfer end". R = identity -> byte-identical (no-op guard, exercised by the canary).

### 5.2 Compute the two incoming frames

- `ReaimTransferSynthesizer.TrySynthesizeTransfer`: add out-params returning the re-aimed transfer's target-relative `(s_re, h_re)` (the 4.3 re-aimed derivation) at `soiEntryUT`, computed in Zup from `transferOrbit` + `targetBody`. Reuse the existing scope; works on both the encounter and proximity-fallback paths (do not depend on `nextPatch`).
- Recorded-side helper (Unity-side, in the resolver): build the `ArrivalLeg` Orbit (`new Orbit(...)`), take its (e, h) in Zup, derive `(s_rec, h_rec)`. Feed plain doubles into the pure `RotationFrameToFrame`.

### 5.3 Apply R in `ReaimPlaybackResolver.BuildWindowSegments`

After `ReplaceHeliocentricLeg` returns the assembled list (cached per window): compute `R = RotationFrameToFrame(s_rec, h_rec, s_re, h_re)`, call `RotateBodyRelativeSegments(assembled, targetBody, recordedArrivalUT, R)`, then COMPUTE the 4.4 time-shift and LOG it (do NOT apply it in v1 -- post-review M1). R is computed once per window. `BuildWindowSegments` already has `transferOrbit`, `soiEntryUT`, `targetBody`, `plan.ArrivalLeg`, `plan.CommonAncestor`, and the assembled list in scope (verified).

### 5.4 Diagnostics

`ParsekFlight.LogReaimGhostTrace`: at the Sun->target-body seam, log both endpoints' target-relative state vectors (position + velocity), the R angle, and the post-rotation seam magnitude, so the residual is directly measurable in a playtest.

### 5.5 Descent-polyline rotation -- FAST-FOLLOW, NOT v1 (review M3)

Deferred to a separate PR after a playtest validates R. It requires: publish per-recording R + window from the resolver to the renderer; in the Driver per-frame capture, rotate each body-fixed descent point by `R_world` (Y-up) after `GetWorldSurfacePosition` before `LocalToScaledSpace`; force the `localScaled` recapture whenever the published R changes (add an R-version into the recapture predicate, not just `lowWarp`); bridge member-id <-> recording-id; clear on the resolver cache cadence; strict gate + no-op regression test for non-re-aim recordings. v1 documents the descent-tail offset that remains until this lands.

## 6. Test strategy

Pure (xUnit, no Unity):
- `InboundAsymptoteDir`: known-conic asymptote; high-e limit tends to q_hat; e just above 1 stays finite/normalized; sign is the inbound branch.
- `RotationFrameToFrame`: orthonormal proper rotation; identity when frames equal; maps sFrom->sTo and hFrom->hTo exactly.
- `RotateOrbitSegmentOrientation` round-trip (the load-bearing test): elements -> state vector -> rotate state by R -> re-derive elements MUST equal elements -> rotate basis -> elements. Cover hyperbolic (ecc>1, sma<0) and elliptic capture; degenerate node (inc ~ 0, LAN undefined) clamps as KSP does; assert ecc/sma/mEp/epoch/orbitalFrameRotation/angularVelocity unchanged.
- `RotateBodyRelativeSegments`: only target-bodied post-arrival segments rotate; Sun/other-body/predicted/pre-arrival pass through; R = identity -> byte-identical (no-op guard).
- Time-shift: `ComputeArrivalTimeShift` returns 0 when window tof == recorded tof and the correct sign for a shorter tof (the diagnostic quantity v1 logs; v1 does NOT apply it, so there is no apply-path test).

In-game (playtest-gated, load-bearing):
- Extend the re-aim canary in-game test to assert the post-rotation seam jump at the Sun->target-body boundary is below an SOI fraction (now meaningful with full-frame match). 
- PR manual checklist: no jump-back-out on Duna approach; orbit line + mesh approach/capture are continuous; the SOI-edge residual is small; (known) the descent polyline tail still sits at the recorded site pending the fast-follow.

## 7. Risks + KSP gotchas

- FRAME is the top risk (C1): keep the element rotation entirely in Zup; never feed `.xzy` velocities into element-space rotation. Round-trip tests guard this.
- Element read-back conventions (degrees vs radians, inc [0,180], LAN/argPe wrap, degenerate-node clamp when inc ~ 0). Prefer reading angles back from rotated basis vectors with the recorder's exact convention (`ReaimOrbitSegmentConverter`).
- `orbitalFrameRotation` + `angularVelocity` are velocity-relative -> NOT rotated.
- Moon flyby (e.g. Ike): v1 rotates only target-bodied (Duna) segments; a mid-arrival Ike excursion would leave the Ike-relative segment unrotated, producing a jump at BOTH the Duna->Ike and Ike->Duna boundaries (two boundaries, per review m4). Log every non-target-bodied segment skipped; accept the brief residual in v1; the classifier already declines multi-hop missions so an Ike ARRIVAL cannot be the target.
- Cannot be verified in-game during this run (no KSP); the PR is "implemented + unit-tested + Opus-reviewed, ready for playtest." Pure math fully tested; seam-closing is playtest-gated.
- Known v1 limitation: the descent-tail polyline stays at the recorded site (5.5 fast-follow).

## 8. Files (v1)

- `Source/Parsek/Reaim/ReaimSegmentAssembler.cs` -- pure rotation helpers (asymptote, frame-to-frame R, orbit-element rotation, segment-list rotation; reuse ShiftInTime).
- `Source/Parsek/Reaim/ReaimTransferSynthesizer.cs` -- return the re-aimed (s_re, h_re) at soiEntryUT (Zup).
- `Source/Parsek/Reaim/ReaimPlaybackResolver.cs` -- recorded-side (s_rec, h_rec), compute R, apply rotation to the assembled list; compute + log the time-shift (not applied in v1, post-review M1).
- `Source/Parsek/ParsekFlight.cs` (`LogReaimGhostTrace`) -- seam state-vector + post-rotation diagnostics.
- Tests: `Source/Parsek.Tests/Reaim*Tests.cs` (pure rotation math) + the re-aim canary in-game test (seam threshold).
- Docs: `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md` (S4 v1 = orbit-segment restitch under ROTATE-ALL; descent-points fast-follow), `docs/dev/plans/reaim-interplanetary-transfers.md` (note S4 v1 implemented).

## 9. Phases (v1 PR)

1. Pure rotation math + xUnit (asymptote, frame-to-frame R, orbit-element rotation w/ round-trip test, segment-list rotation, no-op guard). No wiring.
2. v_inf frame extraction (recorded-arrival + re-aimed-arrival helpers) + apply R in the resolver to the orbit segments + compute/log the 4.4 time-shift (not applied in v1). Seam diagnostics. Build + deploy-verify.
3. Canary in-game seam-threshold test + docs (CHANGELOG / todo / design-doc).
4. Clean Opus review -> fix -> build/deploy-verify/test green -> open PR (ready-for-playtest; ROTATE-ALL + the descent-tail + time-shift-application fast-follow limitations flagged).

(Descent-polyline rotation, section 5.5, AND the 4.4 time-shift APPLICATION are SEPARATE follow-up PRs after the rotation is playtest-validated. Post-review M1: applying a non-zero time-shift against the full-span transfer render-end re-introduces a smaller seam, so v1 only computes + logs the shift.)

## 10. Open questions for the owner (do not block v1)

- Product: ROTATE-ALL (landing site moves) vs keep-deferred (status quo). v1 proceeds with ROTATE-ALL.
- Descent-tail offset: v1 leaves the descent polyline at the recorded site (mesh/orbit-line move, polyline does not) until the 5.5 fast-follow. Acceptable for the first PR, or hold until both land together? v1 ships the orbit-segment fix first (lower risk, playtest-validates R before the renderer hot-path change).
- Moon flyby residual: v1 accepts the brief Ike residual; rotate moon-relative segments later if visible.
