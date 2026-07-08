# S4 Arrival Re-Stitch (M-MIS-2, the last gated Phase-4 piece)

Branch: `reaim-s4-restitch` (off `origin/main`). Parent plan:
`docs/dev/plans/reaim-destination-arrival-alignment.md` (Phase 6 gated on this). Fix direction
specified in `docs/dev/todo-and-known-bugs.md` "#27 Problem 2 / S4 arrival-restitch".

Product decision RATIFIED by the maintainer (2026-07-07): the landing ALWAYS takes place at the
recorded site. The re-stitch rotates the recorded arrival APPROACH only; the body-fixed descent and
touchdown are never moved. The relocate-the-landing alternative is REJECTED and is not built.

## 1. The transform

A rigid rotation of the transfer member's recorded in-SOI arrival chain (the approach hyperbola +
capture + destination parking conics) about the destination body's CENTER, about its SPIN AXIS.
The spin axis is the only site-preserving choice: any other axis changes the latitude reachable by
re-timing the descent, i.e. moves the landing site (the rejected option). In KSP all bodies have
zero axial tilt, so the spin axis equals the orbital reference-plane normal and the rotation is an
exact rigid rotation implemented as a LAN advance on the Duna-bodied OrbitSegments (the shipped
`ReaimSegmentAssembler.RotateLanForParkRephase` machinery, reused verbatim).

FRAME NOTE (review finding, the PR #1196 .z-vs-.y trap): the .xzy-unswizzled vectors the resolver
compares live in KSP's WORLD frame, whose reference-plane normal (world up, the zero-tilt spin
axis) is +Y, NOT +Z. In-plane components are x and z; the prograde-positive bearing is
atan2(-z, x), calibrated against the shipped park-rephase pairing (Cross(r, v_prograde) points +Y
and a LAN advance of +D moves a prograde orbit's position +D in this sense - the same sense body
spin advances a surface site, so the trigger-offset pairing holds).

**The angle is PER-WINDOW, computed in the resolver, never at build time.** The re-aimed transfer's
destination-relative entry bearing advances by the synodic angle every window (~48 deg for
Kerbin-Duna), and on the parking (F2) path the synthesized transfer flies the geometric Hohmann tof
rather than the recorded tof, so its approach bearing also differs from the recorded one in a
tof-dependent way. Both effects are captured exactly by comparing states the resolver already has:

```
recordedEntryDir = ArrivalLeg orbit (plan.ArrivalLeg elements, parent = targetBody)
                   .getRelativePositionAtUT(plan.RecordedArrivalUT).xzy          (dest-relative)
newEntryDir      = transferOrbit.getRelativePositionAtUT(soiEntryUT).xzy
                   - targetBody.orbit.getRelativePositionAtUT(soiEntryUT).xzy    (dest-relative)
theta_k          = signed in-plane angle recordedEntryDir -> newEntryDir about +Y
                   (the unswizzled WORLD frame's pole; pure helper, xUnit-tested; the live-frame
                   sense is additionally pinned by the in-game canary)
```

`theta_k` is applied to every non-predicted targetBody-bodied segment with
`startUT >= RecordedArrivalUT - eps` inside `ReplaceHeliocentricLeg` (the SAME segment population
the shipped captureShift re-time touches; rotation and time-shift are independent and compose).
Rotating the WHOLE in-SOI chain coherently closes the transfer->approach seam and keeps the
approach->capture->parking seams closed among themselves.

## 2. Composition with the descent trigger (the recorded-site invariant)

Rotating the parking conic moves the deorbit point's inertial bearing by `theta_k`. The recorded
descent is body-fixed, so the site reaches that bearing `theta_k / omega_rot` seconds later. The
descent trigger's rotation congruence therefore shifts by

```
siteAlignOffsetSeconds(k) = normalize(theta_k) * T_rot / 360, into [0, T_rot)
```

i.e. the congruence target becomes `recordedDeorbitUT + offset (mod T_rot)`. ONLY the congruence
moves; the descent head stays anchored at `recordedDeorbitUT`, the clip plays verbatim, and the
touchdown lat/lon is the recorded one BY CONSTRUCTION (body-fixed data is never touched on any
path). This composes with (does not duplicate) `DescentTrigger`: `ComputeRotationAlignedTriggerUT`
gains an offset parameter defaulting to 0 (byte-identical), threaded through `ComputeDescentTiming`
/ `ComputeDescentMemberHead` / `TryComputeTransferDeorbitHead` / `TryResolveDescentMemberHead`.

**Single source of truth:** the offset for cycle k is derived from the SAME resolver cache entry
that rendered window k's rotation (`ReaimPlaybackResolver` `CacheEntry.ArrivalRestitchRotationDeg`,
new accessor keyed by `(transferMemberRecordingId, window)`; `LoopUnit` gains
`TransferMemberRecordingId`). If the window declined to faithful (or the cache is cold), the
rendered arrival is UNROTATED and the offset is 0: both fall back together to today's shipped
behavior, so the rotation and the trigger can never disagree. The P4 loiter trim composes
unchanged: cuts and holds act on the loop CLOCK; the rotation acts on inertial ORIENTATION.

## 3. Engage point and gates (fail closed, byte-identical off)

Engage is decided in `ReaimPlaybackResolver.BuildWindowSegments` (in-memory, loop-only, per-cycle,
cached per (member, window); recorded data is NEVER written). ALL of the following must hold, else
`theta_k = 0` and every downstream consumer sees today's exact behavior:

- `plan.ArrivalRestitchEligible` (new plan field): stamped by `MissionLoopUnitBuilder` inside the
  descent-trigger engage success block AND additionally gated on the LANDING discriminator
  (destination constraint set Supported with `HasLandingRotation`, no station anchor, mode not
  Drop) - the descent trigger alone also serves orbital dock/rendezvous approaches, which must
  stay unstamped (their approach members carry no heliocentric leg and would render unrotated,
  and a T_rot wait is meaningless for a station-phase target). Orbit-only arrivals, station
  docks, Drop mode, unsupported destinations, chain shapes that declined the trigger: never
  eligible, byte-identical by construction.
- `hasDepartureOverride` (the F2 parking synth bundle fired this window): the rotation rides the
  same gate as captureShift, so a clocks-diverge / direct-path window stays byte-identical.
- A usable `soiEntryUT` + encounter from the synthesizer, finite entry vectors, non-degenerate
  in-plane projections (near-polar entry declines), and a valid `plan.ArrivalLeg`. The
  proximity-fallback `soiEntryUT` (first coarse sample inside the SOI, up to tof/96 late) is
  REFINED to the actual SOI-sphere crossing by bisection, and an entry radius still far off the
  sphere after refinement declines (the bearing there is flyby-depth-contaminated).

Every decline logs `ParsekLog.Verbose("Reaim", "... S4 restitch declined (<reason>) ...")`; every
engage logs one `ParsekLog.Info("Reaim", "S4 restitch ENGAGED ...")` per window build with
`theta_k`, both entry bearings, the out-of-plane residual, and the velocity-direction residual
after rotation (measure-first: these residuals are observability for tuning, not inputs).

## 4. Accepted residuals (logged, not fixed here)

- The transfer's in-SOI stub (center-to-center Lambert renders to the center-arrival UT) and the
  velocity-magnitude/direction kink at the SOI seam: the whole-chain synthesis that would remove
  them is the reverted option-3 arc (do NOT re-attempt; see the regression guards).
- Out-of-plane entry residual: the spin-axis rotation matches bearing, not latitude; matching
  latitude would move the site (rejected). Logged per window.
- A faithful-declined window keeps today's unrotated render + unshifted trigger (shipped behavior).

## 5. Tests

- Failing-first synthetic fixture (pure xUnit): circular-equatorial parking + hyperbolic approach
  elements; assert (a) the rotated chain's entry bearing equals the new entry bearing, (b) the
  deorbit-point bearing advance equals theta, (c) with the congruence offset the site bearing at
  triggerUT equals the rotated deorbit-point bearing (approach connects), and (d) the descent head
  at trigger is exactly `recordedDeorbitUT` (touchdown site untouched).
- Recorded-site invariance: body-fixed descent data untouched under any engaged theta; offset only
  ever re-times, never re-sites; guard declines (offset 0) rather than moving the site.
- Byte-identity: theta 0 / not-eligible / direct-path / station / Drop / orbit-only produce
  segment lists and trigger times bitwise-identical to main (regression fence tests).
- Composition: one unit with the P4 destination cut + arrival hold + descent trigger + S4 offset
  engaged; assert the trigger congruence and the loop clock agree.
- Cache-coherence: rotation and offset read the same entry; a declined window yields (no rotation,
  offset 0).
- In-game: the P6 on-camera landing-coincidence canary (below) + the existing descent E2E suites.

## 6. P6 on-camera landing acceptance (unblocked by this)

The parent plan's Phase 6 tooltip half shipped with P4; the gated half is the in-game
landing-coincidence canary. Wire it as an InGameTest (pattern: `DescentReStitchInGameTest`): build
a live re-aim landing fixture, drive the resolver at a triggered UT, assert the parking deorbit
point's world position and the body-fixed recorded-site world position coincide within tolerance
at the trigger with S4 engaged, and that the touchdown sample resolves to the recorded lat/lon.

## 7. What does NOT change

`ReaimClassifier` scope, the window scheduler, the Lambert/tof search, captureShift semantics and
its build-time approximation, the P4 trim, the launch-side park rephase, `RecordingStore` data,
`.prec` sidecars, OrbitSegments on disk (all changes are on the per-window in-memory copies the
resolver already builds), and every non-landing re-aim shape.
