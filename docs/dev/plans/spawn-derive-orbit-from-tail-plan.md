# Spawn — Derive terminal orbit from trajectory tail when stored orbit is stale

## Problem

A `terminal=Orbiting` recording can carry orbital metadata that is **older than its
last absolute frame** when a propulsive section ran *after* the most recent
`OrbitSegment` and the recording ended before the recorder/finalizer could
re-snapshot a fresh osculating orbit.

Concrete scenario from logs `2026-05-10_1713`, recording
`d73240bdca8f4f74a19fca3ea4165a16` (Kerbal X upper stage):

| Phase | UT | What happened |
|---|---|---|
| On-rails coast | 477.33 → 958.87 | Recorder emits one `ORBIT_SEGMENT`: sma=667 048, ecc=0.205 (apo +203 km / peri −69 km — sub-orbital) |
| Physics resume | 958.87 → 964.85 | TS#2 (`ExoBallistic`) frames |
| Circ burn | 963.83 → 976.91 | Engine ON/OFF events + TS#3 (`ExoPropulsive`, 13 s, +211 m/s Δv at apoapsis) |
| Post-burn coast | 977.93 → 979.77 | TS#4 (`ExoBallistic`, 1.84 s) — single frame with circular-velocity state |
| Scene exit | 979.77 | `terminal=Orbiting` finalized |

The post-burn frame's state vector (alt 203 587 m, |v| 2098.6 m/s ≈ circular at
that altitude) gives a clean ~205 km circular orbit. But:

- No fresh `ORBIT_SEGMENT` was added — TS#4 is too short and physics-frame
  orbits are not auto-promoted to segments.
- `Recording.TerminalOrbit*` fields persisted to `persistent.sfs` carry the
  same stale `sma=667048, ecc=0.205, epoch=477.33` as the on-rails segment
  (verified at `saves/s14/persistent.sfs:1034-1041`). The mid-burn periodic
  refresh at UT 976.37 did not produce a stable-orbit cache (still burning),
  and the scene-exit refresh did not run vessel-loaded.

At Re-Fly spawn time, [`RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed`](../../Source/Parsek/RecordingEndpointResolver.cs)
walks `OrbitSegments` last-to-first and returns the only entry — the stale
sub-orbital seed. [`TerminalOrbitSpawnSafety.Evaluate`](../../Source/Parsek/TerminalOrbitSpawnSafety.cs)
sees `periapsis −69 446 < safeAlt 75 000` and rejects with
`reason=periapsis-below-safe-altitude`. The capsule never spawns
(KSP.log:31420-31425).

The recording does contain enough information to reconstruct the post-burn
orbit — the last `ExoBallistic` Absolute frame's `(lat, lon, alt, velX/Y/Z)`
reseeds it exactly, and the existing `IncompleteBallisticSceneExitFinalizer`
already uses this exact pattern (`Orbit.UpdateFromStateVectors` over
`body.GetWorldSurfacePosition` + recorded velocity).

The bug is on the **spawn / resolve** side: the orbit selector trusts the last
stored `OrbitSegment` even when the recording has propulsive activity and
absolute frames *after* that segment.

## Why fix on the spawn side, not the recorder side

Three options were considered:

1. **Fix the recorder so `TerminalOrbit*` always reflects the post-burn
   live vessel.** Reasonable but invasive — requires rerouting scene-exit
   finalization to run before unload, and still leaves legacy recordings
   broken.
2. **Append a fresh `OrbitSegment` at scene exit.** Same complaint, plus
   couples optimization/recovery code to a niche end-of-recording case.
3. **Spawn-side: derive a fresh orbit from the trailing absolute frame
   when it post-dates the last `OrbitSegment`.** Localized, self-correcting
   for legacy recordings, no schema/format change.

We pick (3). Recorder remains free to skip emitting a redundant segment in
the common case; spawn safety becomes robust to "no fresh segment" tails.

## Fix overview

Add a helper that walks `Recording.TrackSections` in reverse to find the
latest valid Absolute trajectory frame in a coast (`ExoBallistic`) section,
then reseed an orbit via `Orbit.UpdateFromStateVectors`. If the resulting
tail UT is later than the recording's last stored `OrbitSegment`, this
tail-derived orbit replaces the stored seed for the rest of the spawn
pipeline (safety check + `getOrbitalVelocityAtUT` propagation).

### Data we walk: `Recording.TrackSections`, not `Recording.Points`

`Recording.Points` is rebuilt from `Recording.TrackSections` on load
([`RecordingStore.RebuildPointsFromTrackSections`](../../Source/Parsek/TrajectoryTextSidecarCodec.cs:448)),
**but the rebuild includes every section's `frames` list verbatim** — so
`Points` mixes Absolute frames (real lat/lon/alt) with Relative frames
(anchor-local Cartesian metres in the same fields, per the v6+ `RELATIVE`
contract documented in [`CLAUDE.md`](../../.claude/CLAUDE.md) "Rotation /
world frame"). Picking by `Points[Last]` would silently feed metre-scale
dx/dy/dz into `body.GetWorldSurfacePosition` for any recording whose last
section is Relative, producing garbage.

We must walk `TrackSections` directly so that we can interrogate
`section.referenceFrame` and `section.environment` for each candidate
frame.

### Section filtering rules (revised after Opus review)

For each section walked from last to first:

| Section | Action | Why |
|---|---|---|
| `referenceFrame == Relative`, `absoluteFrames` non-empty | Use last entry of `absoluteFrames` | v7+ shadow is a full TrajectoryPoint with planet-relative position + velocity per CLAUDE.md ("absoluteFrames … is a full TrajectoryPoint, rotation = srfRelRotation, velocity, body, altitude") |
| `referenceFrame == Relative`, no `absoluteFrames` | Skip (continue walking back) | `frames` here are anchor-local metres, NOT lat/lon/alt — would alias |
| `referenceFrame == OrbitalCheckpoint` | Skip | No per-tick `frames`; orbit data lives in `checkpoints` and is already considered by the existing OrbitSegment path |
| `environment == SurfaceMobile`/`SurfaceStationary`/`Approach` | Skip | Vessel is on/near ground; reseeding produces sub-surface orbits, no improvement over existing safety reject and the log line would be misleading |
| `environment == ExoPropulsive` | Skip | Mid-burn osculating orbits are transitional, not meaningful as the "terminal" orbit |
| `environment == Atmospheric` | Skip | Sub-orbital ascent; same misleading-log argument as Surface |
| `environment == ExoBallistic`, `referenceFrame == Absolute`, `frames` non-empty | Pick last frame; finite check; build orbit | The case the fix exists for |

Skipping ExoPropulsive matches our scenario: the helper will walk past
TS#3 (the burn) and land on TS#4's first/only frame at UT 977.93, which
is the post-burn coast.

### Algorithm

```
TryDeriveTerminalOrbitFromTrajectoryTail(rec, body, out tailOrbit, out tailUT):
    tailOrbit = null; tailUT = NaN
    if rec == null or body == null: return false
    if rec.TrackSections == null or .Count == 0: return false

    candidate frame = null
    for s = rec.TrackSections.Count - 1; s >= 0; s--:
        section = rec.TrackSections[s]

        // Pick the candidate frame list per the table above.
        framesList = null
        if section.referenceFrame == Absolute and section.environment == ExoBallistic:
            framesList = section.frames
        elif section.referenceFrame == Relative and section.absoluteFrames is non-empty
             and section.environment == ExoBallistic:
            framesList = section.absoluteFrames
        else:
            continue   // skip Surface/Atmospheric/ExoPropulsive/Approach/non-Absolute-non-shadow

        if framesList == null or framesList.Count == 0:
            continue

        candidate = framesList[framesList.Count - 1]
        if candidate.bodyName != body.name:
            continue        // different body; defer to existing path
        if not IsFinite(candidate.velocity)
           or not IsFinite(candidate.latitude/longitude/altitude):
            continue

        break

    if candidate == null: return false

    // Defer to existing path when a stored OrbitSegment is at least as fresh.
    lastSegmentEndUT = ResolveLatestStoredOrbitSegmentEndUT(rec, body.name)
    if IsFinite(lastSegmentEndUT) and candidate.ut <= lastSegmentEndUT + EPSILON:
        log Verbose "[Spawner] Tail-derived terminal orbit skipped: rec={recId} reason=segment-newer-than-tail tailUT=... segmentEndUT=..."
        return false

    Vector3d worldPos = body.GetWorldSurfacePosition(lat, lon, alt);
    Vector3d worldVel = candidate.velocity;
    if not IsFinite(worldPos) or not IsFinite(worldVel): return false

    Orbit reseeded = new Orbit();
    reseeded.UpdateFromStateVectors(worldPos, worldVel, body, candidate.ut);
    if not IsFiniteOrbitElements(reseeded):
        log Warn "[Spawner] Tail-derived terminal orbit rejected: rec={recId} reason=non-finite-elements"
        return false

    tailOrbit = reseeded
    tailUT = candidate.ut
    log Verbose "[Spawner] Tail-derived terminal orbit: rec={recId} body={body} tailUT=... sma=... ecc=... periAlt=... apoAlt=..."
    return true
```

Mean-anomaly propagation to current spawn UT is handled by the existing
`TimeJumpManager.ComputeEpochShiftedMeanAnomaly` call in
[`TryBuildRecordedTerminalOrbitForSpawn`](../../Source/Parsek/VesselSpawner.cs:4922-4938)
— our helper returns an orbit anchored at `candidate.ut` and lets the
existing code propagate.

### Frame-of-reference correctness

`body.GetWorldSurfacePosition` uses the body's *current* rotation, not the
rotation at the recorded UT. For Re-Fly spawns that fire at
`currentUT ≈ recordedTailUT`, the rotation delta is ≤ a few seconds × Kerbin's
0.0029 rad/s ≈ ~600 m of position drift at orbital radius. After
`UpdateFromStateVectors`, the resulting `(sma, ecc)` differ from the true
post-burn orbit by under a metre / 1e-5 respectively. **Apoapsis and
periapsis altitudes** (the only quantities `TerminalOrbitSpawnSafety` gates
on) **are below the safety margin** of 5 km. LAN/argPe will be off by
approximately the same body-rotation delta, but the existing finalizer
reseed at [`IncompleteBallisticSceneExitFinalizer.cs:1062-1094`](../../Source/Parsek/IncompleteBallisticSceneExitFinalizer.cs)
uses this identical pattern, so we're matching established precedent rather
than introducing a new risk.

For non-Re-Fly spawns where the rotation delta is large, position fidelity
is no worse than the existing pre-burn-segment path which has the same
`body.GetWorldSurfacePosition` approximation when its OrbitSegment is read.

`point.velocity` is recorded as Krakensbane-corrected inertial when
unpacked or `obt_velocity` (body-centric inertial) when packed — both are
the body-centric inertial frame `Orbit.UpdateFromStateVectors` expects.
Confirmed via the existing reseed pattern in `IncompleteBallisticSceneExitFinalizer`.

### Where the new helper lives

New helper as `internal static` on `VesselSpawner` next to
`TryBuildRecordedTerminalOrbitForSpawn`:
`TryDeriveTerminalOrbitFromTrajectoryTail(Recording rec, CelestialBody body, out Orbit orbit, out double tailUT)`.
Direct test surface (xUnit can build synthetic recordings; KSP `Orbit`
construction is what the in-game test will exercise).

`TryBuildRecordedTerminalOrbitForSpawn` calls it before
`TryGetEndpointAlignedRecordedOrbitSeedForSpawn` — so the existing path
remains the fallback when no fresh tail exists.

### Empty-`rec.Points` guard at line 1199

The existing early-return at [`VesselSpawner.cs:1199`](../../Source/Parsek/VesselSpawner.cs:1199)
fires only when `rec.Points.Count == 0`. For section-authoritative
recordings, `rec.Points` is rebuilt from track sections during load
([`TrajectorySidecarBinary.cs:284-298`](../../Source/Parsek/TrajectorySidecarBinary.cs:284))
so `Points.Count > 0` whenever any section has frames. The Kerbal X
recording reaches the orbit-resolver path because of this rebuild; the
new code is reachable on the same condition. We are not changing this
gate, but the reviewer flagged it as worth confirming — confirmed.

### Scope: spawn-side only

`RecordingEndpointResolver.TryGetEndpointAlignedOrbitSeed` is **not
modified.** It has two ghost-rendering callers
([`VesselGhoster.cs:1055`](../../Source/Parsek/VesselGhoster.cs:1055)
and [`GhostMapPresence.cs:7056`](../../Source/Parsek/GhostMapPresence.cs:7056))
that will continue to read the stale stored seed for orbit-line and
ProtoVessel-orbit purposes. Consequence: with a recording in this exact
shape, the ghost orbit line on the map will show the pre-burn sub-orbital
ellipse (passing through Kerbin) even though the spawned real vessel is
correctly circular. This is a **pre-existing** display inconsistency the
fix does not introduce — recordings in this shape today already show the
wrong orbit line; the fix just makes the *spawn* succeed. We add a
`docs/dev/todo-and-known-bugs.md` entry covering the cosmetic issue so
it can be picked up as a follow-up if it surfaces in playtest.

The `VesselSpawner.cs:4018` snapshot-validation-repair path
(`ReplaceSnapshotOrbitNode` flow) calls `TryBuildRecordedTerminalOrbitForSpawn`
for the same reason — it benefits from the fix as a deliberate spillover,
and we add a coverage assertion in the integration test.

## Files to change

| File | Change |
|---|---|
| `Source/Parsek/VesselSpawner.cs` | Add `TryDeriveTerminalOrbitFromTrajectoryTail` + helper `ResolveLatestStoredOrbitSegmentEndUT`. Update `TryBuildRecordedTerminalOrbitForSpawn` to prefer tail-derived orbit when fresher. Logging tag `[Spawner]` with `source=trajectory-tail` vs `source=stored-segment`. |
| New `Source/Parsek.Tests/SpawnTerminalOrbitFromTailTests.cs` | Unit tests on the new helper + integration tests for the resolver-vs-tail decision. |
| `CHANGELOG.md` | One-line entry under current version. |
| `docs/dev/todo-and-known-bugs.md` | (a) New "Done" entry for the fix; (b) New "Open" entry noting the ghost-line cosmetic gap. |

No schema change. No format-version bump. No changes to recorder or
finalizer.

## Tests

The original plan listed 12 xUnit cases; some of those (success-path
integration through `TryBuildRecordedTerminalOrbitForSpawn` + `Evaluate`,
optimizer-split-clone integration, success-path `source=trajectory-tail`
log assertion) require `body.GetWorldSurfacePosition` and
`Orbit.UpdateFromStateVectors`, both of which need Unity transforms that
the test stubs (`TestBodyRegistry.CreateBody` via
`FormatterServices.GetUninitializedObject`) do not provide. The success
path is therefore covered by the in-game test, while xUnit covers the
data-walking and decision branches that don't need Unity. This section
describes what actually shipped, organized by helper.

### `TryFindLatestCoastTrajectoryFrame` (xUnit, 11 cases)

Pure data-walking; no orbit math, fully testable without Unity transforms.

1. `_NoSections_ReturnsFalse` — empty `TrackSections` ⇒ false.
2. `_PicksLastExoBallisticAbsoluteFrame` — straightforward latest-frame pick.
3. `_SkipsExoPropulsiveTail_WalksBackToCoast` — mid-burn section walked past.
4. `_SkipsSurfaceSection` — `SurfaceMobile` walked past.
5. `_SkipsAtmosphericAndApproach` — both env classes walked past.
6. `_AllSurfaceSections_ReturnsFalse` — no coast at all ⇒ false.
7. `_RelativeSectionWithoutShadow_WalksBack` — Relative `frames` skipped (would alias).
8. `_RelativeSectionWithShadow_UsesShadow` — v7+ `absoluteFrames` shadow used.
9. `_BodyMismatch_ReturnsFalse` — frame body ≠ spawn body.
10. `_NonFiniteOnlyFrame_FallsThroughToPriorSection` — section's only frame is corrupt; walks to previous section.
11. `_NonFiniteTailFrame_WalksBackInSection` — section has earlier-valid + corrupt-trailing frames; in-section walk-back picks the prior valid one.

### `ResolveLatestStoredOrbitSegmentEndUT` (xUnit, 4 cases)

12. `_NoSegments_ReturnsNaN` — empty list ⇒ NaN.
13. `_PicksMaxEndUTAcrossSegments` — basic max-end pick.
14. `_FiltersByBody` — Mun segment ignored when resolving for Kerbin.
15. `_SkipsDegenerateSegments` — `sma <= 0` and non-finite `sma` excluded; mirrors `RecordingEndpointResolver.TryGetLastMatchingSegment`'s predicate so a degenerate stored segment cannot shadow a valid coast tail.

### `TryDeriveTerminalOrbitSeedFromTrajectoryTail` (xUnit, 7 cases — negative branches only)

The success path needs `body.GetWorldSurfacePosition`, so xUnit only
exercises the short-circuit branches (which all run before any Unity call).
Each case asserts the structured `declineReason` out-param.

16. `_NullRecording_ReturnsFalse`.
17. `_NullBody_ReturnsFalse`.
18. `_NoCoastFrame_LogsAndReturnsFalse` — `declineReason="no-absolute-coast-tail"`, log line emitted.
19. `_StoredSegmentNewer_LogsAndReturnsFalse` — `declineReason="segment-newer-than-tail"`.
20. `_FreshnessEpsilon_BoundaryTailEqualsSegmentEndRejects` — `tailUT == segmentEndUT` exactly; `<=` check defers.
21. `_DegenerateStoredSegment_DoesNotBlockTail` — degenerate segment doesn't shadow tail; helper progresses to next guard (rotation drift in this test).
22. `_RotationDriftBeyondLimit_LogsAndReturnsFalse` — `|spawnUT − tailUT| > 30 s` ⇒ defer; `declineReason="rotation-drift-out-of-bounds"`.

All tests use `[Collection("Sequential")]` because they touch `ParsekLog.TestSinkForTesting`.

### Not covered by xUnit (and why)

- **`TryBuildRecordedTerminalOrbitForSpawn` end-to-end with `source=trajectory-tail`** — the helper needs `body.GetWorldSurfacePosition` to produce a real `Orbit`, so the success branch that flips `seedSource` to `"trajectory-tail"` and emits the parent log line cannot run in xUnit. The in-game test below covers this path.
- **`TerminalOrbitSpawnSafety.Evaluate` returning `SpawnNow` for the tail-derived elements** — same reason: needs a real Orbit. In-game test below.
- **Vessel actually materialising as a live `Vessel`** — would need `RespawnValidatedRecording`, vessel collision validation, scene-loaded `FlightGlobals.Vessels`. Out of scope for the test layer; covered only by manual playtest (re-fly the Kerbal X recording from `logs/2026-05-10_1713`).

### In-game test

`InGameTests/RuntimeTests.cs:705-825` —
`[InGameTest(Category = "SpawnTerminalOrbit", Scene = GameScenes.FLIGHT)]`
`TerminalOrbitFromTail_DerivesPostBurnCircularOrbit`. Builds a synthetic
recording mirroring the Kerbal X bug (one stale on-rails sub-orbital
`OrbitSegment` ending at UT 958.87, plus an `ExoBallistic` Absolute
TrackSection with a single post-burn frame at UT 977.93 carrying the
circular-velocity state vector). Assertions:

- `TryDeriveTerminalOrbitSeedFromTrajectoryTail` succeeds with non-null
  body name and finite-elliptic elements (`sma > 0`, `0 ≤ ecc < 1`).
- `TryBuildRecordedTerminalOrbitForSpawn` produces a non-null `Orbit` —
  this is the path that xUnit cannot reach because of Unity transforms.
- `TerminalOrbitSpawnSafety.Evaluate(currentAlt, atmosphereDepth,
  safetyMargin, periAlt, apoAlt).Action == SpawnNow` — codifies the
  whole point of the fix. Stronger than `peri > atmosphereDepth` because
  the spawn-time gate uses `safeAltitude = atmosphereDepth + 5 km`.
- `periAlt > safeAlt` (an additional float-level assertion that pins the
  same property).

The test does **not** drive `RespawnValidatedRecording` to actually
materialise a `Vessel` — that requires the full spawn pipeline including
collision detection and `FlightGlobals.Vessels` registration, and the
synthetic Recording does not have a vessel snapshot. The acceptance
assertion (`Action == SpawnNow`) is the same gate the production spawn
path consults; passing it means the bug is fixed at the orbit-resolution
layer that the original log evidence pinpointed.

Verifying the actual Kerbal X recording spawns end-to-end remains a
manual playtest step (re-fly the recording from `logs/2026-05-10_1713`
and confirm the upper stage materialises with `[Spawner] ... source=trajectory-tail`
in KSP.log).

## Logging contract

`TryDeriveTerminalOrbitFromTrajectoryTail` log lines:

- Success: `[Parsek][VERBOSE][Spawner] Tail-derived terminal orbit: rec={recId} body={body} tailUT={ut} sma={sma:F1} ecc={ecc:F4} periAlt={peri:F1} apoAlt={apo:F1}`
- Skip "newer segment exists": `[Parsek][VERBOSE][Spawner] Tail-derived terminal orbit skipped: rec={recId} reason=segment-newer-than-tail tailUT={ut} segmentEndUT={ut}`
- Skip "no Absolute ExoBallistic tail on body": `[Parsek][VERBOSE][Spawner] Tail-derived terminal orbit skipped: rec={recId} reason=no-absolute-coast-tail body={body}`
- Reject "non-finite elements": `[Parsek][WARN][Spawner] Tail-derived terminal orbit rejected: rec={recId} reason=non-finite-elements sma={sma} ecc={ecc}`

`TryBuildRecordedTerminalOrbitForSpawn` extends its existing success log
with `source=trajectory-tail` or `source=stored-segment` so the chosen
path is identifiable from logs alone.

Each log site carries the recording id, body name, tail UT, and resulting
elements (where available). All `[Spawner]` patterns. Verbose for normal
cases; Warn only for non-finite (data corruption signal).

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Tail-derived orbit's LAN/argPe off by body-rotation delta | Safety check uses apo/peri only (which differ from true values by <1 km, well under safety margin). Existing finalizer reseed has the same approximation. Spawn position fidelity unchanged for Re-Fly spawns where `currentUT ≈ tailUT`. |
| Relative-frame frames misinterpreted as lat/lon/alt | Helper hard-skips `Relative` `frames` and only consults `absoluteFrames` shadow when present. CLAUDE.md gotcha §"Rotation / world frame" explicitly handled. |
| Mid-burn `ExoPropulsive` tail used as terminal orbit | Helper hard-skips `ExoPropulsive` sections; walks back to the last `ExoBallistic` Absolute coast. Pinned by `_SkipsExoPropulsiveTail_WalksBackToCoast`. |
| Tail frame from `Surface*` / `Atmospheric` / `Approach` produces sub-surface orbit | Helper hard-skips those environments; falls through to existing path which already handles them. Pinned by `_SkipsSurfaceSection`, `_SkipsAtmosphericAndApproach`, `_AllSurfaceSections_ReturnsFalse`. |
| Tail frame's velocity in unexpected reference frame | Recorder writes Krakensbane-corrected inertial velocity (per CLAUDE.md). Same contract used by existing reseed code in `IncompleteBallisticSceneExitFinalizer`. |
| Recording is `sectionAuthoritative=true` so frames live only in TrackSections | Helper walks `rec.TrackSections` directly (where the data canonically lives) instead of `rec.Points`. Bug-resistant by construction. |
| Existing recordings with proper post-coast `OrbitSegment` regress | Algorithm step "defer to existing path when stored segment ≥ tail UT": only override when `tailUT > lastSegmentEndUT + EPSILON`. Pinned by `_StoredSegmentNewer_LogsAndReturnsFalse` and the boundary case `_FreshnessEpsilon_BoundaryTailEqualsSegmentEndRejects`. |
| Spawn safety check now passes a previously-rejected periapsis | The previous rejection was wrong — the periapsis came from a stale segment. Acceptance based on the actual final orbit is the intended behavior. Codified by the in-game test `TerminalOrbitFromTail_DerivesPostBurnCircularOrbit` which asserts `TerminalOrbitSpawnSafety.Evaluate(...).Action == SpawnNow` for the synthetic Kerbal X shape. |
| Stored segment is degenerate (`sma <= 0`) and could shadow a valid coast tail | `ResolveLatestStoredOrbitSegmentEndUT` mirrors `RecordingEndpointResolver.TryGetLastMatchingSegment`'s `sma > 0 && finite` predicate, so a degenerate segment cannot defer the helper to a "newer" segment that the existing seed picker would itself reject. Pinned by `_SkipsDegenerateSegments` and `_DegenerateStoredSegment_DoesNotBlockTail`. |
| Body-rotation drift between `body.GetWorldSurfacePosition` (current rotation) and `candidate.ut` | `TailDerivedOrbitMaxRotationDriftSeconds = 30 s` clamp: defer to existing path when `\|spawnUT − tailUT\| > 30 s`. Re-Fly spawns at `currentUT ≈ tailUT` keep drift at zero. Pinned by `_RotationDriftBeyondLimit_LogsAndReturnsFalse`. |
| Ghost orbit line / map marker still pulled from stale segment | Pre-existing display gap, not introduced here; documented as an open todo entry. The fix strictly improves spawn behavior. |

## Out of scope

- Recorder-side improvements (auto-emit `OrbitSegment` after `ExoPropulsive`
  closure, scene-exit live-vessel reseed). This fix removes the urgency.
- Optimizer / split-time inheritance of `TerminalOrbit*` fields. The
  split-time copy at `RecordingOptimizer.cs:937` is unchanged; the new
  logic supersedes it at spawn time when applicable. The Kerbal X repro
  itself was a post-split clone (`d73240bd…` is the second half of an
  optimizer split), so the in-game test's synthetic shape exercises this
  scenario class even though there is no dedicated split-clone xUnit case.
- Ghost map presence / orbit-line rendering. Tracked as a new open todo
  entry; not gated on safety, so the cosmetic stale-line is tolerable
  while the spawn path is fixed.

## Acceptance

- All 11 320 existing xUnit tests still pass.
- New unit tests pass: 22 cases in `SpawnTerminalOrbitFromTailTests`
  (11 on `TryFindLatestCoastTrajectoryFrame`, 4 on
  `ResolveLatestStoredOrbitSegmentEndUT`, 7 on
  `TryDeriveTerminalOrbitSeedFromTrajectoryTail` short-circuit branches).
- New in-game test `TerminalOrbitFromTail_DerivesPostBurnCircularOrbit`
  passes when run with Ctrl+Shift+T in flight, asserting
  `TerminalOrbitSpawnSafety.Evaluate(...).Action == SpawnNow` for a
  synthetic Kerbal X shape.
- Manual playtest: re-fly the Kerbal X recording from
  `logs/2026-05-10_1713`. KSP.log shows
  `[Spawner] Tail-derived terminal orbit:` followed by
  `TryBuildRecordedTerminalOrbitForSpawn: ... source=trajectory-tail`,
  and the upper stage actually materialises (no
  `periapsis-below-safe-altitude` Warn).
