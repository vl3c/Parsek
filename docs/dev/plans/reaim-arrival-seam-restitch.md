# Re-aim Arrival-Seam Restitch (S4) - Design + Implementation Plan

Status: PLAN (pre-implementation). Branch `reaim-arrival-seam` off `main` (after #982).
Builds on: re-aim interplanetary transfers (shipped #981) and the descent polyline coverage (shipped #982).
This is the deferred S4 / S2->S3 arrival-seam item from `docs/dev/plans/reaim-interplanetary-transfers.md` (the S4 row and the "S2 -> S3" seam section) and the deferred entry in `docs/dev/todo-and-known-bugs.md`.

This plan was authored from two read-only investigations (the seam mechanics + the #982 review) plus direct code reads. It is the basis for implementation; the implementer must still verify the exact KSP Orbit-element conventions against the live API while coding.

## 1. Problem

A looped Kerbin->Duna recorded mission is replayed as a map ghost. Each synodic window the heliocentric Sun-transfer leg is re-planned (live Lambert) to the target's CURRENT position and substituted; the launch / parking / escape and the target-body arrival + capture + descent legs replay AS RECORDED. The recorded target-body segments are Duna-relative Kepler `OrbitSegment`s rendered via `new Orbit(elements, liveDuna)`, so they follow live Duna, but they keep their ORIGINAL recorded approach orientation (inc / LAN / argPe relative to Duna's inertial frame).

Result: the re-aimed transfer reaches Duna's SOI from the re-planned direction (mesh ~14.7 Mm from Duna), then the recorded arrival hyperbola is spliced on with its recorded approach vector (~33 Mm out on a different bearing), so the ghost jumps back out and re-approaches along the recorded direction. The seam jump at the recorded Sun->Duna boundary is ~1.37 Gm (about 28 Duna-SOI radii). On the map this reads as "the ghost approached the landing site, then went back and approached again," and the descent then draws at the recorded landing site at the end of that recorded arrival replay.

Evidence (playtest `logs/2026-05-31_0311_duna-descent-position`): `ReaimSeam seg#9->10 body=Duna ... jump=1374718336m`; `dist[Duna]` 14.7 Mm -> 33 Mm -> re-approach. The seam is a REAL position gap (both endpoints share the identical recorded->live UT mapping, so it is not a clock desync or render-span artifact).

## 2. Goal / Non-goals

Goal: close the gross DIRECTIONAL arrival seam so the re-aimed ghost's whole target-body chapter (approach hyperbola + capture + any moon flyby + descent) is one continuous, internally-consistent trajectory that joins the re-aimed transfer, eliminating the ~1.37 Gm jump-back-out.

Non-goals (v1):
- Exact periapsis-altitude / arrival-timing match. The recorded and re-aimed hyperbolae have different v_inf MAGNITUDES (different transfers), so a pure rotation cannot make the join exact; a small SOI-edge residual remains. This is the same accepted class as the existing departure seam (S0->S1).
- Keeping the recorded landing SITE fixed (see section 3; the chosen v1 option moves it).
- Same-parent (intra-Kerbin-system) missions, which have no heliocentric leg and are untouched.
- Steeply-inclined-target re-aim (already declines to faithful replay; unrelated).

## 3. Product decision (v1 default: ROTATE-ALL)

Two options, with their geometric consequences:

- ROTATE-APPROACH-ONLY (keep the recorded landing site): to hit the SAME recorded surface lat/lon from a NEW approach direction requires RE-SOLVING the entire arrival (a new hyperbola plane, a new capture, a new descent to the fixed site) -- a non-rigid synthesis of an arrival the recording does not contain. Not a rigid transform, not v1-viable, and it would invent a landing the player never flew.

- ROTATE-ALL (rigid rotation of approach + capture + flyby + descent; the landing site MOVES): one rotation R about the target body's center maps the recorded incoming v_inf direction onto the re-aimed incoming v_inf direction and carries the whole target-body chapter with it. Internally seam-free (the chapter is one rigid body), cheap, and mostly pure-testable. The landing site rotates to a new lat/lon consistent with the re-aimed approach.

Decision for v1: ROTATE-ALL. Rationale: (1) it is a genuine rigid transform; (2) it eliminates the gross seam, which is the actual user-visible bug; (3) it is consistent with the feature's existing contract that the transfer is re-planned, not byte-faithful, so a moved landing site is a natural extension of "your transfer is re-aimed"; (4) the alternative is non-rigid synthesis the recording cannot support.

This REVERSES the stance in the earlier deferred-todo note (which prioritized keeping the landing site). Flagged for the owner: if keeping the recorded landing site matters more than closing the seam, the only honest alternative is to NOT rotate and keep the deferred snap (status quo). There is no cheap middle option.

## 4. The restitch math

Define a single rigid rotation R about the target body's center (an inertial rotation, body position fixed):

```
R = rotation mapping  dir(v_inf_recorded)  ->  dir(v_inf_reaimed)
```

- `v_inf_reaimed`: the re-aimed transfer's target-relative incoming velocity at the re-aimed SOI entry. Compute frame-consistently from the synthesized transfer Orbit and the target body's orbit:
  `v_inf_reaimed = transfer.getOrbitalVelocityAtUT(soiEntryUT).xzy - targetBody.orbit.getOrbitalVelocityAtUT(soiEntryUT).xzy`
  (the heliocentric arrival velocity minus the target body's heliocentric velocity). Use this rather than `nextPatch`: the playtest shows the synthesizer takes the proximity-fallback path, which never populates `nextPatch`.
- `v_inf_recorded`: the recorded arrival hyperbola's target-relative incoming velocity. Source = `ReaimMissionPlan.ArrivalLeg` (the recorded S3 first target-body `OrbitSegment`, `ReaimClassifier.cs:29`). Build its Orbit (`new Orbit(elements, targetBody)`), sample its target-relative velocity at the recorded SOI-entry UT (or derive the inbound hyperbolic asymptote direction from the elements). The recorded SOI-entry UT is the recorded arrival window end already used by the assembler (`recordedArrivalUT`).
- `R = Quaternion.FromToRotation(dir(v_inf_recorded), dir(v_inf_reaimed))`, computed in ONE consistent inertial frame (mind the KSP `.xzy` swizzle; the synthesizer already round-trips it carefully).

Residual after R: because `|v_inf|` differs, the rotated recorded hyperbola does not have the re-aimed hyperbola's exact periapsis altitude or exact SOI-crossing phase. Direction-match is the right v1 target; the residual is a few km of periapsis and a few seconds of phase at the SOI edge -- far below the 1.37 Gm seam and the same accepted class as the departure seam. Tests assert the seam shrinks below an SOI fraction, not to zero.

What R is applied to (the whole target-body chapter must rotate by the SAME R or the next boundary re-seams):

1. Target-body (Duna) `OrbitSegment`s after the transfer (approach hyperbola, capture, low-orbit descent segments). Rotating an orbit rigidly about the body center = rotate its orbital-plane normal `h` and its periapsis/eccentricity direction `e` by R, then read back (inclination, LAN, argPe). Keep eccentricity, sma, meanAnomalyAtEpoch, epoch UNCHANGED (shape + phase preserved). `orbitalFrameRotation` is velocity-relative (prograde-relative) so it does NOT need separate rotation -- the mesh attitude follows the rotated velocity automatically.
2. Body-fixed descent POINTs (the recorded lat/lon/alt surface track that the descent polyline draws). These ride Duna's SPIN frame; an inertial R must be applied as: lat/lon/alt -> inertial position relative to Duna center at that point's UT -> apply R -> back to lat/lon/alt at that UT. This is the fiddly part (see section 5, descent-polyline handling), and it is required so the descent polyline follows the moved landing site and stays consistent with the rotated low-orbit segments the ghost mesh rides.

Moon flyby segments (e.g. a recorded Ike flyby, body=Ike, NOT Duna-relative): for v1, rotate only the target-body (Duna) segments and accept a tiny residual at the brief moon-flyby boundary, OR rotate the Ike-relative segments by re-expressing them through Duna (rotate their Duna-relative position at epoch, then back to Ike-relative). v1 recommendation: rotate Duna-bodied segments only, log any non-target body segments skipped; revisit if the flyby residual is visible in playtest.

## 5. Implementation

### 5.1 Pure rotation helper (xUnit-testable, no Unity)

Add to `ReaimSegmentAssembler` (mirrors the existing pure `ShiftInTime`):

- `internal static OrbitSegment RotateOrbitSegmentOrientation(OrbitSegment seg, double[] R3x3)` (or a small pure quaternion/double3 type): given a segment and a rotation expressed as a pure 3x3 double matrix (NOT UnityEngine.Quaternion, to keep the helper Unity-free and double-precision), recompute (inclination, longitudeOfAscendingNode, argumentOfPeriapsis) for the rotated orbit. Implement by building the orbit basis vectors (`h` normal from inc+LAN; node vector; `e`/periapsis direction from argPe), rotating both by R3x3, and reading the angles back. Keep ecc/sma/mEp/epoch/body unchanged.
- `internal static List<OrbitSegment> RotateBodyRelativeSegments(IReadOnlyList<OrbitSegment> segments, string targetBodyName, double[] R3x3)`: returns a copy with every `targetBodyName`-bodied, non-predicted segment whose startUT is at/after the transfer end rotated by `RotateOrbitSegmentOrientation`; all others passed through. Pure.
- Pure helper to build R3x3 from two direction vectors (the FromToRotation in double precision) so R itself is unit-testable: `internal static double[] RotationMatrixFromTo(double[] fromDir, double[] toDir)`.
- Pure descent-point rotation: `internal static void RotateBodyFixedPointInertial(...)` taking lat/lon/alt + the body's spin-frame-to-inertial basis at the point UT (passed in as plain doubles) + R3x3, returning rotated lat/lon/alt. The Unity caller supplies the body rotation; the math stays pure.

Decision: implement R and the element rotation as PURE double-precision math (3x3 matrices / double3), not UnityEngine.Quaternion, so the whole rotation pipeline is xUnit-tested off Unity. The Unity caller only supplies input vectors (velocities, body rotation) and feeds them in.

### 5.2 Compute the two v_inf directions

- `ReaimTransferSynthesizer.TrySynthesizeTransfer` (`ReaimTransferSynthesizer.cs`): add an out-param returning the re-aimed transfer's target-relative incoming v_inf direction at `soiEntryUT` (the velocity subtraction above). It already has `transferOrbit`, `soiEntryUT`, and `targetBody` in scope, so this is a small addition on both the encounter and proximity-fallback paths.
- Recorded v_inf direction: a helper that builds `ArrivalLeg`'s Orbit at the target body and samples its target-relative velocity at `recordedArrivalUT` (or derives the inbound asymptote from the hyperbola elements). This is Unity-side (needs a live `Orbit`), so it lives in the live resolver, feeding plain direction doubles into the pure helper.

### 5.3 Apply R in the resolver

`ReaimPlaybackResolver.BuildWindowSegments`: after `ReplaceHeliocentricLeg` returns the assembled list, compute R3x3 from (recorded v_inf dir, re-aimed v_inf dir) and call `RotateBodyRelativeSegments(assembled, targetBodyName, R3x3)`. Log both endpoint state vectors + the rotation angle. Expose R (per member, keyed by recording id + window) so the descent polyline can apply the same rotation (see 5.4).

### 5.4 Descent polyline (the consistency requirement)

The re-aim ghost MESH rides the (now-rotated) low-Duna `OrbitSegment`s. The descent POLYLINE (`GhostTrajectoryPolylineRenderer`) reads the RAW body-fixed lat/lon/alt from `RecordingStore.CommittedRecordings` and does not go through the re-aim resolver, so without action it would stay at the recorded landing site while the mesh moves -> a new mesh-vs-polyline mismatch at the descent.

Approach: publish a per-recording-per-window re-aim rotation (the same R + the target body + the window timing) from the resolver into a small lookup the renderer consults (mirrors how the resolver already drives other re-aim map state). For a recording being played re-aimed, the renderer rotates each body-fixed descent point by R (via `RotateBodyFixedPointInertial`, using live Duna's spin basis at the point UT) before `GetWorldSurfacePosition`. Gate strictly on "this recording is a re-aim member in the active window" so non-re-aim ghosts and same-parent missions are byte-identical.

This is the highest-risk part (it reaches the shared renderer + spin-frame math). The plan review must confirm the cleanest publish/consume seam; if it proves too invasive for v1, fall back to: rotate only the orbit segments in this PR (fixes the gross approach jump-back-out) and split the descent-point rotation into an immediate follow-up, explicitly noting the descent-tail mesh/polyline offset that remains until the follow-up lands. Prefer doing both in one PR if the seam is clean.

### 5.5 Diagnostics

`ParsekFlight.LogReaimGhostTrace`: at the Sun->target-body seam, log both endpoints' target-relative state vectors (position + velocity), the computed R angle, and the post-rotation seam magnitude, so the residual is directly measurable in a playtest (the todo's recommended first diagnostic).

## 6. Test strategy

Pure (xUnit, no Unity):
- `RotationMatrixFromTo`: maps from-dir to to-dir; identity when equal; orthonormal result; antiparallel-edge-case handling.
- `RotateOrbitSegmentOrientation`: rotate a known Duna hyperbola by a known R; assert the rotated (inc, LAN, argPe) reproduce a rigidly-rotated state vector (round-trip a state vector through rotate -> elements -> state and compare). Cover hyperbolic (ecc>1, sma<0) and elliptic capture cases. Assert ecc/sma/mEp/epoch unchanged.
- `RotateBodyRelativeSegments`: only target-bodied post-transfer segments rotate; Sun/other-body/predicted/pre-transfer segments pass through; count + identity assertions.
- `RotateBodyFixedPointInertial`: rotate a known surface point by a known R + body basis; round-trip; assert a zero rotation is identity (byte-identical no-op for non-re-aim).
- v_inf direction extraction math where it can be made pure.

In-game (playtest-gated, the load-bearing visual check):
- Extend the existing re-aim canary in-game test to assert the post-rotation seam jump at the Sun->target-body boundary is below an SOI-fraction threshold (e.g. < 1 SOI radius) instead of ~1.37 Gm.
- Manual playtest checklist in the PR: no jump-back-out on Duna approach; the descent polyline + mesh + orbit line move together to the re-aimed landing site; the residual at the SOI edge is small.

## 7. Risks + KSP gotchas

- Recomputing (inc, LAN, argPe) under rotation is the error-prone part. KSP `Orbit` orientation conventions (the swizzled frame, inclination 0..180, LAN/argPe wrap) must be respected. Prefer reading angles back from rotated basis vectors with the SAME convention the recorder used. `ReaimOrbitSegmentConverter` documents inc/LAN/argPe in DEGREES, mEp in RADIANS, epoch in UT -- match it.
- The `.xzy` swizzle between Unity world and KSP orbit frame: compute R and apply it in one consistent frame.
- `orbitalFrameRotation` is velocity-relative; do NOT rotate it separately (it follows the rotated velocity).
- Spin-frame vs inertial: the descent-point rotation must convert lat/lon/alt to inertial at the point UT before applying the inertial R; do not rotate lat/lon directly.
- Shared-renderer reach (5.4): the descent-point rotation touches `GhostTrajectoryPolylineRenderer`, which runs for every ghost. Gate strictly on re-aim-member-in-window; add a byte-identical no-op regression test for non-re-aim recordings (mirror the #982 no-op guard).
- Cannot be verified in-game by the author (no KSP during this run); the PR is "implemented + unit-tested + Opus-reviewed, ready for playtest." The pure math is fully tested; the visual seam-closing and the descent-polyline-following are playtest-gated.

## 8. Files

- `Source/Parsek/Reaim/ReaimSegmentAssembler.cs` -- new pure rotation helpers (R matrix, orbit-element rotation, segment-list rotation, body-fixed-point rotation).
- `Source/Parsek/Reaim/ReaimTransferSynthesizer.cs` -- return the re-aimed v_inf direction (reuse transferOrbit + soiEntryUT).
- `Source/Parsek/Reaim/ReaimPlaybackResolver.cs` -- compute R, apply to assembled list, publish per-member R for the renderer.
- `Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs` -- apply R to body-fixed descent points for re-aim members (gated), no-op for everyone else.
- `Source/Parsek/ParsekFlight.cs` (`LogReaimGhostTrace`) -- seam state-vector + post-rotation diagnostics.
- Tests: `Source/Parsek.Tests/Reaim*Tests.cs` (pure rotation math) + the re-aim canary in-game test (seam threshold).
- Docs: `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md` (mark S4 in progress / done with the ROTATE-ALL decision), `docs/dev/plans/reaim-interplanetary-transfers.md` (note S4 implemented under ROTATE-ALL).

## 9. Phases

1. Pure rotation math + xUnit (R matrix, orbit-element rotation, segment-list rotation, body-fixed-point rotation). No wiring.
2. v_inf extraction (synthesizer out-param + recorded-arrival velocity helper) + apply R in the resolver to the orbit segments. Seam diagnostics. Build + deploy.
3. Descent-polyline rotation (publish/consume R, gated, no-op regression test).
4. Canary in-game test threshold + docs.
5. Clean Opus review -> fix -> build/deploy-verify/test -> open PR (ready-for-playtest, ROTATE-ALL flagged).

## 10. Open questions for the owner (do not block v1)

- Product: ROTATE-ALL (landing site moves) vs keep-deferred (status quo, no rotation). v1 proceeds with ROTATE-ALL.
- Moon flyby residual: accept the brief Ike-flyby seam in v1, or rotate moon-relative segments too. v1 accepts the brief residual.
- Descent-polyline rotation in one PR vs split to a fast-follow if the renderer seam is too invasive. v1 prefers one PR; review decides.
