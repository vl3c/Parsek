# Fix Plan: Sub-orbital orbit segment rejected by distance resolver

Date: 2026-05-11 (rev. 25)

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-investigate-probe-orbital-jump`

Branch: `investigate-probe-orbital-jump`

Reproduction bundle: `C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-10_2123`

## Revision History

- **rev. 1:** original plan with primary (relax SMA guard) and secondary (frame-aware flat-points fallback) fixes; static-seam refactor described as a footnote.
- **rev. 2:** review feedback. Secondary fix dropped — `FindTrackSectionForUT(targetUT)` doesn't defend against the actual hazard, which lives in *bracket points* found by `TrajectoryMath.InterpolatePoints` ([TrajectoryMath.cs:830](../../../Source/Parsek/TrajectoryMath.cs:830)) that span sections. Static seam promoted to its own implementation step. Risk 1 strengthened with verified recorder gates. Pre-fix companion test added.
- **rev. 3:** second review feedback.
  - **Sibling SMA guards.** Two additional sites with the same `absSma < bodyRadius * 0.9` rule: [`ParsekFlight.cs:23394`](../../../Source/Parsek/ParsekFlight.cs:23394) (`InterpolateAndPosition` legacy/non-checkpoint path) and [`GhostPlaybackEngine.cs:6105`](../../../Source/Parsek/GhostPlaybackEngine.cs:6105) (`TryResolvePendingOrbitSegmentInterpolation`, gated by an `applySubSurfaceGuard` flag). Both relaxed in this fix for consistency — sub-orbital arcs are valid playback data on every path that reads them.
  - **Full orbit-element finite checks** added: validate `inclination`, `eccentricity`, `semiMajorAxis`, `longitudeOfAscendingNode`, `argumentOfPeriapsis`, `meanAnomalyAtEpoch`, `epoch` are all finite before constructing `new Orbit(...)`. Validate `worldPos` is finite immediately after `orbit.getPositionAtUT(targetUT)` plus any offset, and again after the surface clamp — matches and tightens the existing pattern in [`TryResolveOrbitTailWorldPosition`](../../../Source/Parsek/ParsekFlight.cs:20290) which today does have the post-`getPositionAtUT` finite check that `TryResolveOrbitWorldPosition` lacks.
  - **Frame-aware fallback (still out of scope).** Description sharpened: a future correct implementation must look up the section of each bracket point (`before.ut`, `after.ut`) returned by `TrajectoryMath.InterpolatePoints`, not the section at `targetUT`, since brackets span sections.
  - **Test seam** spelled out: use [`TestBodyRegistry`](../../../Source/Parsek.Tests/TestBodyRegistry.cs:24) with an injectable body resolver. The seam options (static `OrbitResolution` helper vs. `[InternalsVisibleTo]` against `ParsekFlight`) are described.
  - **Markdown links** corrected for the file's location at `Parsek-investigate-probe-orbital-jump/docs/dev/plans/`. Repo files: `../../../Source/...`. Bundle (lives in parent workspace `Code/logs/`): `../../../../logs/...`.
- **rev. 4:** third review feedback.
  - **Pending metadata site stays body-only.** `TryResolvePendingOrbitSegmentInterpolation` currently returns `InterpolationResult(..., seg.bodyName, 0.0)`. This fix changes whether a valid sub-orbital segment is accepted as an orbit-precedence candidate, not the metadata altitude contract. Tests assert orbit `bodyName` and `altitude == 0.0` only for the non-surface/no-shadow precedence branch; active surface sections still fall through to point/surface metadata after logging the skip. Computing real orbit altitude there would be a separate behavior change.
  - **Degenerate-orbit rejection is unconditional.** The old `applySubSurfaceGuard:false` paths must not continue accepting `sma=0`, NaN, or infinite elements. The flag may remain for source compatibility, but it no longer bypasses `IsFiniteOrbitSegment` / minimum-SMA rejection; it only preserves any non-degenerate fallback behavior that callers intentionally requested.
  - **xUnit surface projection hazard test corrected.** Fake `CelestialBody` instances from `TestBodyRegistry` support explicit body resolver / radius lookups, but `GetWorldSurfacePosition` is not reliable outside KSP because transform state is not initialized. The xUnit pre-fix companion now asserts the bracket-section hazard using `TrajectoryMath.InterpolatePoints` + `FindTrackSectionForUT`; planet-scale distance confirmation stays in manual/in-game validation.
  - **Existing stale tests must be updated.** The plan now explicitly calls out `GhostPlaybackEngineTests.TryResolvePendingPlaybackInterpolation_SubSurfaceMixedOrbitSegment_FallsBackToPoints` and `BugfixRegressionTests.SmaSubSurfaceCheckTests` as expected-to-change tests, not just new coverage.
- **rev. 5:** GPT-5.5 xhigh review feedback.
  - **Additional stale pending-metadata test** added: `TryResolvePendingPlaybackInterpolation_SurfaceTrackSection_SubSurfaceOrbitSegment_DoesNotLogSkippedOrbitPrecedence` also pins the old radius-threshold outcome and must be updated.
  - **xUnit body resolver corrected**: tests should pass `name => TestBodyRegistry.ResolveBodyByName(name, out var body) ? body : null` into `OrbitResolution`, not rely on `FlightGlobals.Bodies?.Find(...)` resolving fake bodies.
  - **Actual positioning finite guard** added: `PositionGhostFromOrbit` must validate the orbit world position before body API calls and after any clamp before assigning `ghost.transform.position`, so relaxing site 2 cannot write NaN/Infinity transforms.
- **rev. 6:** GPT-5.5 xhigh review feedback.
  - **Unchecked orbit construction closed:** `TryGetOrbitForSegment` also constructs `new Orbit(...)` and is used by checkpoint/tail positioning. It must route through `OrbitResolution.TryResolveOrbitFromSegment` (or equivalent finite/min-SMA validation) before construction.
  - **Continuity offset preservation clarified:** `PositionGhostFromOrbit` must preserve `TryResolvePredictedOrbitTailContinuityOffset` semantics by applying the offset before terrain clamp, then finite-checking the final assigned position.
- **rev. 7:** pre-final self-review tightening.
  - **Finite guard ordering clarified:** `TryComputeOrbitWorldPosition` must reject non-finite `getPositionAtUT(...) + offset` results before calling `CelestialBody.GetAltitude` / latitude / longitude helpers, then re-check after any surface clamp.
- **rev. 8:** GPT-5.5 xhigh review feedback.
  - **Orbit reapply path covered:** `GhostPosMode.Orbit` reapply recomputes `orbit.getPositionAtUT(e.orbitUT) + continuityOffset` and assigns through `ApplyGhostReapplyTransform`; it must use the same finite/clamp helper or skip assignment on failure.
  - **Positioning orbit construction covered:** `PositionGhostFromOrbit` itself constructs `new Orbit(...)` and is called directly by tail / orbit-only positioning paths, so it must route construction through `OrbitResolution.TryResolveOrbitFromSegment`, not rely on `TryGetOrbitForSegment` being fixed elsewhere.
  - **Pending metadata branch clarified:** accepted sub-orbital orbit metadata wins only on the non-surface/no-shadow orbit-precedence branch. Active surface sections still log "skipping orbit precedence" and fall through to point/surface metadata.
  - **First uncached playback constructor covered:** `TryResolveFlightOrbitalAnchorPose` also constructs an `Orbit` from recorded `OrbitSegment` data. Add an uncached factory overload so recorded playback/anchor constructors can validate before `new Orbit(...)` even when they do not use `orbitCache`.
- **rev. 9:** GPT-5.5 xhigh follow-up feedback.
  - **Additional recorded anchor constructors covered:** the uncached factory / world-position helper must also be used by `RecordedRelativeAnchorPoseResolver`, `ProductionAnchorWorldFrameResolver`, and `TrajectoryMath.EvaluateOrbitSegmentAtUT`.
  - **Anchor pose finite/clamp checks covered:** recorded anchor pose paths must not return raw `orbit.getPositionAtUT(...)` directly; they use the same finite-before-body-API and post-clamp validation as the positioning/distance paths.
  - **Logging contract made explicit:** production rejection branches must use a shared validation/logging helper or pass logging context into factory/resolver calls; `IsFiniteOrbitSegment` alone is not enough for site 2, site 3, or uncached anchor callers.
  - **Finite-ordering test seam made enforceable:** add a small delegate/core seam so xUnit can assert body API delegates are not called for non-finite raw positions and that non-finite clamp output is rejected.
- **rev. 10:** GPT-5.5 xhigh follow-up feedback.
  - **Remaining stored-`OrbitSegment` constructors covered:** include `BackgroundRecorder`, `FlightRecorder`, and `GhostMapPresence` map-presence paths in the shared factory / world-position helper scope.
  - **Non-validation rejection logging clarified:** `missing-body` and `orbit-construction-failed` are explicit rejection/log reasons and get test coverage.
  - **Checkpoint strategy corrected:** the extraction commit preserves old call-site behavior; the new threshold, new rejection semantics, logging, finite/clamp guards, and extra skip/null outcomes belong to the behavior-change commit.
- **rev. 11:** GPT-5.5 xhigh follow-up feedback.
  - **Endpoint-segment map ProtoVessel seed covered:** `GhostMapPresence.BuildAndLoadGhostProtoVessel(IPlaybackTrajectory, string)` can receive stored `OrbitSegment` elements via `TryResolveGhostProtoOrbitSeed` / `RecordingEndpointResolver`; route that seed through the same validation/logging path.
  - **Map ProtoVessel contract tested:** add focused coverage proving `map-presence-proto` logs validation failures and does not use the terrain-clamp world-position helper.
  - **API sketch context fixed:** `TryResolveOrbitWorldPosition` includes an explicit `context` parameter so primary resolver logging cannot silently lose call-site context.
- **rev. 12:** GPT-5.5 xhigh follow-up feedback.
  - **`PositionGhostFromOrbit` caller contract fixed:** convert it from `void` to a success-returning method with validated/clamped world-position metadata, and make the checkpoint orbit-only caller abort/log on failure instead of recomputing raw orbit position and altitude.
  - **Extraction/behavior split clarified:** extraction may introduce compatibility-mode wrappers, but all new logging, caught construction failures, validation failures, and new false/null outcomes belong to the behavior-change commit.
  - **Endpoint-segment validation scoped:** do not remove the shared `RecordingEndpointResolver` SMA prefilter globally because spawn uses it too. Add a map-presence-specific endpoint-segment selection/validation path, or update spawn in the same behavior commit with its own validation/logging/tests.
- **rev. 13:** GPT-5.5 xhigh follow-up feedback.
  - **Spawn-safe endpoint handling:** map-presence endpoint validation must not globally remove `RecordingEndpointResolver`'s SMA prefilter unless `VesselSpawner` and its mirror predicate/tests are updated in the same behavior commit.
  - **World-position failure reasons exposed:** `TryComputeOrbitWorldPositionCore` / wrapper APIs return an `OrbitWorldPositionFailureReason` so callers can distinguish pre-body finite rejection, body API failure, post-clamp finite rejection, and orbit-position failure.
  - **Primary resolver extraction mode fixed:** `TryResolveOrbitWorldPosition` takes `OrbitSegmentValidationMode`, matching the behavior-preserving extraction requirement.
- **rev. 14:** GPT-5.5 xhigh follow-up feedback.
  - **All direct positioning callers covered:** every `PositionGhostFromOrbit` caller must handle `TryPositionGhostFromOrbit == false`; no caller may activate or compute metadata from a stale transform.
  - **Distance resolver failure reason exposed:** `TryResolveOrbitWorldPosition` also returns `OrbitWorldPositionFailureReason` so primary distance logging can distinguish orbit-position/body/clamp failures.
  - **ValidateAndLog scoping clarified:** `missing-body` and `orbit-construction-failed` logging applies only in behavior mode / `ValidateAndLog`, not in the mechanical extraction commit.
  - **Endpoint seed ordering preserved:** map-presence-specific endpoint validation must preserve `RecordingEndpointResolver` source ordering and conflict semantics, adding validation only to the candidate selected by those rules.
- **rev. 15:** GPT-5.5 xhigh follow-up feedback.
  - **Engine-facing orbit placement failure propagated:** change `IGhostPositioner.PositionFromOrbit` to a success-returning contract and make engine call sites skip activation/post-position work on failure.
  - **Chain fallback caller covered:** `PositionGhostFromOrbitOnly` and `TryPositionChainFallbackFromRecording` must propagate failure before updating orbit state or logging "positioned".
  - **Terminal map ProtoVessel seeds covered:** `terminal-orbit` fallback seeds also validate/log with `context=map-presence-proto` and keep the no-terrain-clamp contract.
  - **Extraction-mode missing-body wording scoped:** body resolver misses are silent only when the old call site was silent; `missing-body` logging is a behavior-mode requirement.
- **rev. 16:** GPT-5.5 xhigh follow-up feedback.
  - **Failed engine orbit placement cleans up active ghosts:** orbit placement failure enters a teardown/suppression branch, not a silent skip, so already-active ghosts cannot keep stale transforms, FX, or audio alive.
  - **Endpoint orbit placement calls covered:** `PositionGhostAtRecordingEndpoint` orbit, checkpoint-backed orbit, and last-segment orbit fallback paths must propagate placement failure before endpoint explosions/completion side effects.
  - **Pending metadata validation made body-resolving:** site 3 uses a helper that can log `missing-body` in `ValidateAndLog` mode instead of a pure finite/SMA predicate that cannot see resolver misses.
- **rev. 17:** GPT-5.5 xhigh follow-up feedback.
  - **Loaded-ghost/watch-sync orbit placement covered:** hidden priming and watch synchronization use `PositionLoadedGhostAtPlaybackUT`; its orbit-tail and orbit-data calls must use the same cleanup/failure contract.
  - **Map-presence gates covered:** upstream `HasOrbitData` gates must not bypass `map-presence-proto` validation/logging for invalid terminal-orbit data such as NaN SMA.
  - **Invalid endpoint outcomes made explicit:** `sma=0`, tiny positive SMA, and NaN endpoint segments get separate expected source/failure outcomes matching current resolver ordering plus new validation.
  - **Orbital rotation inputs guarded:** velocity/start-position/radial/LookRotation inputs get finite/non-zero guards so valid positions cannot be paired with invalid rotations.
- **rev. 18:** GPT-5.5 xhigh follow-up feedback.
  - **Loop playback orbit failures covered:** `PositionLoopAtPlaybackUT` / `IGhostPositioner.PositionLoop` / `PositionLoopGhost` join the same success-returning or latch-aware cleanup contract.
  - **Checkpoint-point LateUpdate rotation covered:** `GhostPosMode.CheckpointPoint` reapply uses the same finite rotation guard as orbit reapply and checkpoint interpolation.
  - **Negative hyperbolic SMA wording fixed:** endpoint `sma == 0` remains the terminal-fallback ordering skip; large finite negative SMA is a valid non-degenerate endpoint seed when selected by the shared resolver and spawn path.
  - **Interface implementers listed:** test and in-game fake `IGhostPositioner` implementations are included in the signature-change surface.
- **rev. 19:** GPT-5.5 xhigh follow-up feedback.
  - **Hidden-tier prewarm covered:** `HandleHiddenGhostByDistance`'s prewarm caller of `PositionLoadedGhostAtPlaybackUT` must also suppress cleanup on orbit placement failure.
  - **Policy map-presence gate covered:** `ParsekPlaybackPolicy.HandleGhostCreated` must not return before `map-presence-proto` validation/logging when terminal orbit fields exist but fail current gates.
  - **Endpoint prefilter table wording fixed:** implementation step now says to bypass the shared resolver while mirroring its `<= 0` ordering skip, not bypass that skip.
  - **Commit strategy made green per commit:** behavior changes ship with matching tests and documentation updates, not in a later tests-only commit.
- **rev. 20:** GPT-5.5 xhigh follow-up feedback.
  - **Packed-start landed gate added:** `FlightRecorder.StartRecording` must skip packed-start orbit segment initialization for `LANDED` / `SPLASHED` / `PRELAUNCH` vessels before relaxing resolver guards.
  - **Landed-junk risk wording corrected:** the mitigation is existing recorder gates plus this new packed-start gate and focused coverage, not a claim that all current recorder add paths are already sealed.
- **rev. 21:** GPT-5.5 xhigh follow-up feedback.
  - **Background recorder surface gates added:** background on-rails initialization and checkpoint reopen must recheck `LANDED` / `SPLASHED` / `PRELAUNCH` and atmosphere before opening orbit segments.
  - **Legacy-junk exposure made explicit:** relaxing the reader guards is a behavior expansion for legacy surface-junk recordings unless paired with a targeted legacy surface-junk scrub/rejection and regression coverage.
- **rev. 22:** GPT-5.5 xhigh follow-up feedback.
  - **Map orbit update path covered:** `UpdateGhostOrbitForRecording` / `ApplyOrbitToVessel` must validate/log stored elements before `Orbit.SetOrbit`, with no terrain clamp.
  - **Additional recorder reopen paths covered:** false-alarm resume and background SOI-change reopen get the same surface/prelaunch/atmosphere gates.
  - **Legacy scrub location constrained:** targeted legacy surface-junk rejection must be load-time, or site 2 must receive recording-level classification before reader guards are relaxed.
- **rev. 23:** GPT-5.5 xhigh follow-up feedback.
  - **Transition-to-background atmosphere gate added:** `FlightRecorder.TransitionToBackground` must skip atmospheric vessels before opening the background orbit segment.
  - **False-alarm fallback specified:** if resume orbit segment creation is suppressed, reopen as an Absolute/non-orbit section with boundary seeding, not an orphaned OrbitalCheckpoint section.
  - **Map orbit update success contract added:** `ApplyOrbitToVessel` returns success/failure and both chain and recording update callers early-out or remove the map vessel on rejection.
  - **TrajectoryMath method name corrected:** current checkpoint helper is `EvaluateOrbitSegmentAtUT`, not `TryResolveOrbitalCheckpointPosition`.
- **rev. 24:** GPT-5.5 xhigh follow-up feedback.
  - **Map update rejection cannot leave stale orbit lines:** invalid update segments and any `TryApplyOrbitToVessel` failure must remove/retire the existing map vessel through the normal lifecycle path, not merely early-out with the previous valid orbit still visible.
  - **Map apply failure modes covered:** the update success contract includes validation, missing orbit driver/orbit, `Orbit.SetOrbit`, `updateFromParameters`, and renderer-refresh failures.
  - **False-alarm boundary seed source defined:** suppressed `OrbitalCheckpoint` resumes seed the replacement non-orbit section from the current live absolute sample when available, otherwise from a validated finite checkpoint evaluation or by deferring reopening until the next sample with a log.
  - **Recorder skip logging and docs scope tightened:** transition-to-background atmosphere skips require log assertions, and docs updates must include the new `OrbitResolution` / `IGhostPositioner` contract.
- **rev. 25:** post-main-merge context update.
  - **PR #818 cross-reference added:** the same reproduction bundle and `Kerbal X Probe` recording were also used for the separate spawn-time tail-orbit state-vector frame fix; this plan remains scoped to playback distance/orbit-segment rendering.

## Problem

During watch-mode playback of a multi-vessel chain (Re-Fly tree, post-staging), the user watches the upper-stage capsule ghost in its `OrbitalCheckpoint` exo section. A second co-orbital ghost — the booster (also in an `OrbitalCheckpoint` exo section, ~5 m away in real space) — is **invisible at 1× rate** and **pops in only when the user time-warps**. Both ghosts are physically next to each other; only one is rendered.

The user expects both to be visible together: they're in the same place.

## Concrete Evidence

Reproduction bundle `logs/2026-05-10_2123/`:

- Active vessel: `pid=ac3846917f`, PRELAUNCH on Launch Pad ([persistent.sfs:1311](../../../../logs/2026-05-10_2123/saves/s15/persistent.sfs)).
- Watched ghost #1: `e19eb61d…` "Kerbal X" upper-stage continuation, exo, UT [135.30, 469.76].
- Peer ghost #9: `rec_f1363fc…` "Kerbal X Probe" booster, exo, UT [135.66, 453.66] (active Re-Fly recording from a prior session, now replayed as a ghost from the launch-pad perspective).
- Cross-reference: PR #818's research doc, [`probe-tail-orbit-spawn-frame-mismatch.md`](../research/probe-tail-orbit-spawn-frame-mismatch.md), uses this same bundle and `Kerbal X Probe` recording to fix a spawn-time `Orbit.UpdateFromStateVectors` frame mismatch. It calls the same section-3 orbit `epoch=142.16, sma=512941, ecc=0.575` a stale ascent segment, but does not change the playback distance resolver SMA guard targeted here.
- Watch transferred from chain root #0 → upper-stage #1 at 21:21:39 ([KSP.log:88800](../../../../logs/2026-05-10_2123/KSP.log)).

Probe ghost lifecycle around that watch transfer:

| time | UT | mode | pos relative to active (launch pad) | visible? |
|---|---|---|---|---|
| 21:21:44.681 | 140.629 | `RecordedRelative` (section 2, anchored to upper stage) | `(-105697, 172, -30999)` ≈ 110 km | yes (active=true) |
| 21:21:46.226 | 142.169 | `OrbitalCheckpoint` (section 3, sma=512941) | (zone reports ~660 km) | **hidden by zone** |
| 21:22:14.448 | ~190+ | `OrbitalCheckpoint` (warp activated) | (zone reports ~600 km) | **visible** ("exempt during warp (orbital ghost)") |

Section 3 of `rec_f1363fc127ab47a28812ce4be6515453.prec.txt`:

```
TRACK_SECTION
{
    env = 2  (ExoBallistic)
    ref = 2  (OrbitalCheckpoint)
    startUT = 142.16307…
    endUT   = 415.02214…
    ORBIT_SEGMENT
    {
        sma   = 512941     // < 600 000 = Kerbin radius
        ecc   = 0.5746
        epoch = 142.16307
        ...
    }
}
```

This is a legitimate sub-orbital ballistic arc: apoapsis at sma·(1+e)−R ≈ 208 km altitude, periapsis at sma·(1−e)−R ≈ −382 km (impacts surface).

## Root Cause

There are two code paths from a recording's UT to a world position. They disagree.

### Path A — actual ghost positioning (works)

`IGhostPositioner.InterpolateAndPosition` ([ParsekFlight.cs:16563](../../../Source/Parsek/ParsekFlight.cs:16563)) → `TryInterpolateAndPositionCheckpointSection` ([:19699](../../../Source/Parsek/ParsekFlight.cs:19699)) → `TryInterpolateAndPositionCheckpointSectionWithOrbitRotation` ([:19771](../../../Source/Parsek/ParsekFlight.cs:19771)) → **`PositionGhostFromOrbit`** ([:19001](../../../Source/Parsek/ParsekFlight.cs:19001)).

`PositionGhostFromOrbit` constructs `new Orbit(...)` from the segment's elements, calls `orbit.getPositionAtUT(ut)`, and ground-clamps via `body.GetWorldSurfacePosition(lat, lon, 0)` if `body.GetAltitude(worldPos) < 0` ([:19044-19056](../../../Source/Parsek/ParsekFlight.cs:19044)). **No SMA guard.** Sub-orbital orbits resolve correctly.

The log line at UT 195 confirms: `mode=Orbit ... rawWorld=(-178831.25, 223.25, -80309.56) orbitAlt=121859.84`. World position is right; alt is positive (probe is above ground at apex).

### Path B — distance resolver for zone-hide (broken)

`ResolvePlaybackDistanceForEngine` ([ParsekFlight.cs:19606](../../../Source/Parsek/ParsekFlight.cs:19606)) → `ResolvePlaybackDistanceFromReferencePosition` ([:19646](../../../Source/Parsek/ParsekFlight.cs:19646)) → **`TryResolvePlaybackWorldPosition`** ([:20120](../../../Source/Parsek/ParsekFlight.cs:20120)).

For a probe at UT 142.169 in `OrbitalCheckpoint` section 3:

1. `TryResolveOrbitTailWorldPosition` ([:20147](../../../Source/Parsek/ParsekFlight.cs:20147)) returns false — gated on `playbackUT > lastPointUT + 1e-6` ([GhostPlaybackEngine.cs:5470](../../../Source/Parsek/GhostPlaybackEngine.cs:5470)); recording's last sampled UT is 453.66, playback UT is 142.
2. Track-section lookup finds section 3. The `OrbitalCheckpoint` branch at [:20193](../../../Source/Parsek/ParsekFlight.cs:20193) requires `section.frames != null && section.frames.Count > 0`. Section 3 carries only `checkpoints` (one `ORBIT_SEGMENT`), no per-point frames. **Branch skipped.**
3. `TryGetAbsoluteSectionPlaybackFramesForPlayback` returns false (not `Absolute`).
4. Fall-through: `TryResolveInterpolatedWorldPosition(traj.Points, traj.OrbitSegments, …)` ([:20226](../../../Source/Parsek/ParsekFlight.cs:20226)).
5. That tries **`TryResolveOrbitWorldPosition`** ([:20883](../../../Source/Parsek/ParsekFlight.cs:20883)) over `traj.OrbitSegments`. The SMA guard at lines **20900-20903**:

   ```csharp
   double bodyRadius = body.Radius;
   double absSma = System.Math.Abs(seg.semiMajorAxis);
   if (absSma < bodyRadius * 0.9)
       return false;
   ```

   For Kerbin: 0.9 × 600 000 = 540 000. Section 3's `sma = 512941` < 540 000 → **rejected.**
6. Final fallback: `TryResolvePointWorldPosition(traj.Points, ...)` ([:20828](../../../Source/Parsek/ParsekFlight.cs:20828)). This walks the **flat aggregated** `traj.Points` list, finds the surrounding entries by UT via `TrajectoryMath.InterpolatePoints` ([TrajectoryMath.cs:830](../../../Source/Parsek/TrajectoryMath.cs:830) — brackets by UT alone, freely spanning sections), and at line 20860:

   ```csharp
   bodyBefore.GetWorldSurfacePosition(before.latitude, before.longitude, before.altitude);
   ```

   The "before" entry at UT ≈ 142.16 is the **last sample of section 2 (Relative)**, which stores anchor-local metre offsets in the `latitude/longitude/altitude` fields (last logged: `localOffset=(-0.29, -4.99, 0.03)`). `GetWorldSurfacePosition` interprets `(-0.29°, -4.99°, 0.03 m)` as planet coords and returns a world position on Kerbin's equator near `lon = -5°`. The launch pad is at `lon = -74.55°`; arc length on Kerbin's surface ≈ 728 km — matches the `659 620 m` zone-hide log.

This is the silent miscoordinate hazard `.claude/CLAUDE.md` warns about ("calling `body.GetWorldSurfacePosition(lat, lon, alt)` directly on a RELATIVE-frame point will silently produce a position deep inside the planet because metre-scale dx/dy/dz are interpreted as degrees + altitude"). The actual positioning paths (Path A) got the treatment; the **distance-resolver fallback never did**.

### Why warp "fixes" it

`PositionGhostFromOrbit` (Path A) always positions correctly. With warp ON, [`ShouldExemptFromZoneHide`](../../../Source/Parsek/GhostPlaybackLogic.cs:201) returns true for orbital-segment ghosts, the `hidden-by-zone` short-circuit is bypassed at [ParsekFlight.cs:16404-16419](../../../Source/Parsek/ParsekFlight.cs:16404), the ghost is positioned correctly via Path A, and it appears next to the upper stage. With warp OFF, Path B's bogus 660 km distance triggers `hidden-by-zone` → ghost is hidden even though Path A would have placed it correctly.

### Origin of the SMA guard

[Commit `b60c35ab`](https://github.com/vl3c/Parsek) (Apr 2 2026) — predecessor of the `Math.Abs` fix — comment: *"reject orbit segments where the orbit is mostly sub-surface (SMA < 90% of body radius). This catches old recordings that have invalid orbit segments for surface vessels."*

The intent was to filter degenerate "orbit" parameters that landed-vessel recordings sometimes ship with. **But sub-orbital ballistic arcs legitimately have `sma < bodyRadius`** — every staged-off booster post-MECO and every reentering vessel has one. The guard is over-aggressive and currently misclassifies them as "garbage orbit" → forces the fallback path that has the silent miscoordinate failure.

## Fix Design

### Three sites, one rule change

The `absSma < bodyRadius * 0.9` rule appears in three places — all need to be relaxed to a finite-element sentinel. All three currently fall through (or, for the metadata resolver, return `false`) when the orbit is sub-orbital, none of which are correct outcomes for a valid sub-orbital arc.

| Site | File:Line | Method | Reachable for the bug? |
|---|---|---|---|
| 1 | [`ParsekFlight.cs:20902`](../../../Source/Parsek/ParsekFlight.cs:20902) | `TryResolveOrbitWorldPosition` (distance resolver) | **Yes — the immediate cause.** |
| 2 | [`ParsekFlight.cs:23394`](../../../Source/Parsek/ParsekFlight.cs:23394) | `InterpolateAndPosition` (legacy non-checkpoint positioning path) | Not for this repro (probe uses checkpoint sections that route through `TryInterpolateAndPositionCheckpointSection`). Reachable for legacy / pre-track-section recordings with sub-orbital orbits. |
| 3 | [`GhostPlaybackEngine.cs:6105`](../../../Source/Parsek/GhostPlaybackEngine.cs:6105) | `TryResolvePendingOrbitSegmentInterpolation` (body-only metadata resolver, called from [`TryResolvePendingPlaybackInterpolation:5873`](../../../Source/Parsek/GhostPlaybackEngine.cs:5873) with `applySubSurfaceGuard:true`) | Possibly — pending-ghost metadata resolution runs during scene-load before ghosts are positioned. A re-fly probe in a sub-orbital section could hit this if its metadata is queried before the first frame. This site returns body metadata and `altitude=0.0`; this fix does not add orbit-altitude computation there. |

Treating sub-orbital arcs as valid playback data means all three sites should accept them.

Degenerate validation is unconditional at all three sites. If site 3 keeps the `applySubSurfaceGuard` parameter for source compatibility, that flag must not bypass the new `IsFiniteOrbitSegment` / minimum-SMA sentinel; the flag can only preserve call-site intent around non-degenerate fallback ordering.

### The new rule

```csharp
// The sub-surface SMA guard is too strict — sub-orbital ballistic arcs
// have valid finite elements with SMA < bodyRadius (apoapsis above ground,
// periapsis below). orbit.getPositionAtUT resolves them correctly, and the
// caller terrain-clamps when world altitude < 0 (matching the actual-positioning
// path PositionGhostFromOrbit). Reject only obviously degenerate orbits.
const double MinValidSmaMeters = 1.0;
if (!IsFiniteOrbitSegment(seg) || System.Math.Abs(seg.semiMajorAxis) < MinValidSmaMeters)
    return false;
```

This replaces the body-radius threshold everywhere. Do not implement it as "old threshold when `applySubSurfaceGuard=true`, no checks when false"; zero / NaN / infinite orbit elements are invalid regardless of caller.

`IsFiniteOrbitSegment` is a new static helper in `OrbitResolution` (see Implementation Surface):

```csharp
internal static bool IsFiniteOrbitSegment(OrbitSegment seg)
{
    return IsFiniteDouble(seg.inclination)
        && IsFiniteDouble(seg.eccentricity)
        && IsFiniteDouble(seg.semiMajorAxis)
        && IsFiniteDouble(seg.longitudeOfAscendingNode)
        && IsFiniteDouble(seg.argumentOfPeriapsis)
        && IsFiniteDouble(seg.meanAnomalyAtEpoch)
        && IsFiniteDouble(seg.epoch);
}

private static bool IsFiniteDouble(double v)
    => !double.IsNaN(v) && !double.IsInfinity(v);
```

`bodyName` resolution is part of the factory/resolver contract. In `ValidateAndLog` mode, a `null` / empty name or resolver miss returns false with `reason=missing-body` and logs with the call-site context. In `PreserveLegacyBehavior` mode, keep the old call site's silent return or existing log/error behavior.

### Post-`getPositionAtUT` finite checks

After `worldPos = orbit.getPositionAtUT(targetUT) + additiveWorldOffset`, validate `worldPos` is finite before calling any `CelestialBody` methods. After the optional surface clamp, validate it again before returning true. This mirrors and tightens the pattern in `TryResolveOrbitTailWorldPosition` at [ParsekFlight.cs:20290](../../../Source/Parsek/ParsekFlight.cs:20290):

```csharp
worldPos = orbit.getPositionAtUT(targetUT) + additiveWorldOffset;
if (!IsFiniteVector3d(worldPos))
    return false;
double orbitAlt = body.GetAltitude(worldPos);
if (orbitAlt < 0)
{
    double lat = body.GetLatitude(worldPos);
    double lon = body.GetLongitude(worldPos);
    worldPos = body.GetWorldSurfacePosition(lat, lon, 0);
}
if (!IsFiniteVector3d(worldPos))
    return false;
return true;
```

This catches edge cases where pathologically-shaped orbits (e.g. parabolic with `ecc ≈ 1.0`) produce non-finite values from KSP's orbit math. Today's SMA guard happens to reject those before the orbit constructor runs; the new guard does not, so the post-check earns its keep.

Apply this to every path that turns an `Orbit` position into a distance or transform target:

- **Distance / world-position resolver paths** (`TryResolveOrbitWorldPosition`, and any shared helper it calls).
- **Actual positioning path** `PositionGhostFromOrbit` ([ParsekFlight.cs:19026](../../../Source/Parsek/ParsekFlight.cs:19026)), before assigning `ghost.transform.position`.
- **Orbit reapply path** `GhostPosMode.Orbit` ([ParsekFlight.cs:1322](../../../Source/Parsek/ParsekFlight.cs:1322)) before it calls `ApplyGhostReapplyTransform` ([ParsekFlight.cs:1694](../../../Source/Parsek/ParsekFlight.cs:1694)).
- **Recorded anchor / checkpoint pose paths** before they return an `AnchorPose` or checkpoint position: `TryResolveFlightOrbitalAnchorPose` ([ParsekFlight.cs:15541](../../../Source/Parsek/ParsekFlight.cs:15541)), `BackgroundRecorder.TryResolveBackgroundOrbitalAnchorPose` ([BackgroundRecorder.cs:5105](../../../Source/Parsek/BackgroundRecorder.cs:5105)), `FlightRecorder.TryResolveOrbitalAnchorPoseForRecorder` ([FlightRecorder.cs:7694](../../../Source/Parsek/FlightRecorder.cs:7694)), `RecordedRelativeAnchorPoseResolver.TryResolveOrbitalAnchorPose` ([RecordedRelativeAnchorPoseResolver.cs:261](../../../Source/Parsek/RecordedRelativeAnchorPoseResolver.cs:261)), `ProductionAnchorWorldFrameResolver.TryResolveOrbitalAnchorPose` ([Rendering/ProductionAnchorWorldFrameResolver.cs:565](../../../Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:565)), and `TrajectoryMath.EvaluateOrbitSegmentAtUT` ([TrajectoryMath.cs:165](../../../Source/Parsek/TrajectoryMath.cs:165)).
- **Map-presence world-position path** `GhostMapPresence.TryResolveOrbitWorldPosition` ([GhostMapPresence.cs:4226](../../../Source/Parsek/GhostMapPresence.cs:4226)).

Recommended extraction: add `OrbitResolution.TryComputeOrbitWorldPosition(Orbit orbit, CelestialBody body, double targetUT, Vector3d additiveWorldOffset, out OrbitPlacementResult placement)` that performs `getPositionAtUT`, applies the optional world offset, rejects non-finite positions before body API calls, runs the surface clamp, and performs the final finite check. `OrbitPlacementResult` carries final world position plus raw orbit position / clamp metadata so existing rotation, interpolation, and trace code keep their current diagnostics. Provide a zero-offset overload for call sites that do not use predicted-tail continuity.

To make the finite-ordering testable without calling `CelestialBody.GetWorldSurfacePosition` in xUnit, split the helper into a production wrapper plus a small internal core seam, for example:

```csharp
internal static bool TryComputeOrbitWorldPositionCore(
    Vector3d rawWorldPos,
    Vector3d additiveWorldOffset,
    Func<Vector3d, double> altitudeResolver,
    Func<Vector3d, Vector3d> surfaceClampResolver,
    out Vector3d worldPos)
```

The core first applies `additiveWorldOffset`, checks `IsFiniteVector3d`, and only then calls `altitudeResolver`. Tests pass delegates that record whether they were invoked and can force a non-finite clamp result; production wrappers pass `body.GetAltitude` and the latitude/longitude/surface-projection closure.

`PositionGhostFromOrbit` must keep its current continuity behavior: resolve `TryResolvePredictedOrbitTailContinuityOffset`, pass that offset into `TryComputeOrbitWorldPosition`, and clamp/check the **offset-adjusted** world position. Change it from `void` to a success-returning method, for example `bool TryPositionGhostFromOrbit(..., out OrbitPlacementResult placement)`, where `placement` carries the validated/clamped final world position, raw orbit position, terrain-clamp flag, altitude, continuity metadata, `Orbit`, and `CelestialBody` as needed by existing trace/rotation callers. If the helper returns false, log rate-limited, return false, and do not assign `ghost.transform.position`, leaving the existing transform untouched rather than writing NaN/Infinity coordinates.

The checkpoint orbit-only caller (`TryInterpolateAndPositionCheckpointSectionWithOrbitRotation` at [ParsekFlight.cs:19771](../../../Source/Parsek/ParsekFlight.cs:19771)) must consume that success result. It must not call `PositionGhostFromOrbit` and then recompute `orbit.getPositionAtUT(playbackUT)` / `orbitBody.GetAltitude(rawPos)` on its own. On failure, it aborts that interpolation branch (return false or hide according to the existing caller contract) after logging; on success, it builds `InterpolationResult` and diagnostics from the validated `placement` data.

All other direct callers must also check the boolean:

- Predicted-tail positioning ([ParsekFlight.cs:17233](../../../Source/Parsek/ParsekFlight.cs:17233)): on failure, log and leave the ghost unchanged/inactive according to the existing state path; do not treat the tail as positioned.
- Orbit-only recording playback ([ParsekFlight.cs:19477](../../../Source/Parsek/ParsekFlight.cs:19477)): on failure, do not activate the ghost; return after logging so a stale transform is not shown.
- Legacy orbit positioning ([ParsekFlight.cs:23409](../../../Source/Parsek/ParsekFlight.cs:23409)): on failure, fall through to point interpolation or return unresolved according to the existing fallback contract; do not compute `segAlt` from `ghost.transform.position` after a failed placement.

`PositionGhostFromOrbitOnly` must also become success-returning, because `TryPositionChainFallbackFromRecording` ([ParsekFlight.cs:14276](../../../Source/Parsek/ParsekFlight.cs:14276)) currently logs "positioned", calls `UpdateChainGhostOrbitIfNeeded`, and returns true after the orbit-only helper. On `TryPositionGhostFromOrbit == false`, that caller must skip the orbit update, skip the success log, and return false / continue its existing fallback path instead of certifying a stale transform as positioned.

`GhostPosMode.Orbit` reapply must use the same helper with `e.orbitBody`, `e.orbitUT`, and `ResolveOrbitContinuityOffset(e, e.orbitUT)` before `ApplyGhostReapplyTransform`. If `e.orbitBody` is null or the helper returns false, log rate-limited and skip the reapply assignment for that phase. Keep raw orbit position only for rotation math after it has been finite-checked; do not let reapply undo the surface clamp that `PositionGhostFromOrbit` applied during Update.

Orbital rotation needs its own guard, because successful finite position is not enough to prove every rotation input is valid. Convert `ComputeOrbitalRotation` ([ParsekFlight.cs:19408](../../../Source/Parsek/ParsekFlight.cs:19408)), the `GhostPosMode.Orbit` reapply rotation block ([ParsekFlight.cs:1342](../../../Source/Parsek/ParsekFlight.cs:1342)), and the duplicated `GhostPosMode.CheckpointPoint` LateUpdate reapply block ([ParsekFlight.cs:1432](../../../Source/Parsek/ParsekFlight.cs:1432)) to a `TryComputeOrbitalRotation` / guarded helper shape. Validate `getOrbitalVelocityAtUT(ut)`, `getOrbitalVelocityAtUT(segment.startUT)`, `getPositionAtUT(segment.startUT)`, `radialOut`, normalized vectors, spin axes, and final quaternions before feeding `SafeOrbitalLookRotation`, `Quaternion.LookRotation`, `Quaternion.AngleAxis`, or `ApplyGhostReapplyTransform`. On invalid rotation inputs, preserve the current rotation or skip the rotation update after logging; never assign a non-finite quaternion. Tests should cover finite position with invalid velocity/start-position and prove the transform position path still succeeds without corrupting rotation.

The engine-facing positioner contract must propagate this too. Change `IGhostPositioner.PositionFromOrbit` ([IGhostPositioner.cs:88](../../../Source/Parsek/IGhostPositioner.cs:88)) to `bool TryPositionFromOrbit(...)` or an equivalent success-returning API. Engine call sites such as `GhostPlaybackEngine.RenderInRangeGhost` ([GhostPlaybackEngine.cs:1184](../../../Source/Parsek/GhostPlaybackEngine.cs:1184)) and the activation/post-position path ([GhostPlaybackEngine.cs:1309](../../../Source/Parsek/GhostPlaybackEngine.cs:1309)) must check the return value. A failed orbit placement is not just "skip the rest": it enters an explicit orbit-placement-failed cleanup path that mirrors the existing retired-anchor handling at [GhostPlaybackEngine.cs:1217](../../../Source/Parsek/GhostPlaybackEngine.cs:1217). For a previously active ghost, call `ApplyFrameVisuals(..., skipPartEvents:true, suppressVisualFx:true, allowTransientEffects:false)` or an equivalent teardown helper, hide/deactivate or mark the ghost retired for the frame, do not call `MarkPrimaryGhostPositionedThisFrame`, do not activate, do not trace/track appearance, and do not restore FX/audio from a stale transform. This prevents the engine from leaving old plumes/audio visible or reactivating/decorating a stale transform after the host rejected orbit placement.

Endpoint positioning has the same contract. `PositionGhostAtRecordingEndpoint` ([GhostPlaybackEngine.cs:2480](../../../Source/Parsek/GhostPlaybackEngine.cs:2480), [:2506](../../../Source/Parsek/GhostPlaybackEngine.cs:2506), [:2527](../../../Source/Parsek/GhostPlaybackEngine.cs:2527)) must become success-returning or set the same orbit-placement-failed latch when its orbit endpoint, checkpoint-backed endpoint, or last-segment orbit fallback cannot position. `HandlePastEndGhost` must check that result before `TriggerExplosionIfDestroyed`, `completedEventFired.Add`, or deferred completion event creation. On failure, run the teardown/suppression path and return for the frame, or complete only through an explicit no-visual-side-effects branch with `GhostWasActive=false`; never trigger endpoint explosions, completion visuals, or events from the previous transform.

Loaded-ghost priming, hidden-tier prewarm, and watch synchronization have the same contract. `PositionLoadedGhostAtPlaybackUT` ([GhostPlaybackEngine.cs:5351](../../../Source/Parsek/GhostPlaybackEngine.cs:5351), [:5384](../../../Source/Parsek/GhostPlaybackEngine.cs:5384)) is reached by hidden-tier prewarm in `HandleHiddenGhostByDistance` ([GhostPlaybackEngine.cs:5125](../../../Source/Parsek/GhostPlaybackEngine.cs:5125)), `PrimeLoadedGhostForPlaybackUT` ([GhostPlaybackEngine.cs:5205](../../../Source/Parsek/GhostPlaybackEngine.cs:5205)), and `SynchronizeLoadedGhostForWatch` ([GhostPlaybackEngine.cs:5243](../../../Source/Parsek/GhostPlaybackEngine.cs:5243)). Convert it to return success / set the same orbit-placement-failed latch. Hidden-tier prewarm and hidden priming must not apply persistent part state after a failed orbit placement; watch sync must use the cleanup/suppression branch and skip activation, FX restore, appearance tracking, camera retarget decisions based on stale position, and normal visual application.

Loop playback has the same contract. `PositionLoopAtPlaybackUT` ([GhostPlaybackEngine.cs:3337](../../../Source/Parsek/GhostPlaybackEngine.cs:3337)) calls `IGhostPositioner.PositionLoop`, and `ParsekFlight.PositionLoopGhost` can route through checkpoint orbit-only or legacy orbit positioning ([ParsekFlight.cs:21978](../../../Source/Parsek/ParsekFlight.cs:21978), [:22060](../../../Source/Parsek/ParsekFlight.cs:22060)). Convert `IGhostPositioner.PositionLoop` to `bool TryPositionLoop(...)` or make it set the same orbit-placement-failed latch when any loop orbit branch fails. Primary loop, overlap-primary, overlap-cycle, and loop-endpoint callers must check that result before normal `ApplyFrameVisuals`, activation, FX restore, appearance tracking, or endpoint handling; on failure they use the same cleanup/suppression branch as non-loop orbit placement. Tests must cover primary loop and overlap loop failures from checkpoint and legacy orbit paths.

Recorded anchor pose paths use the zero-offset wrapper. They should compute rotation from the validated/clamped world position, matching `PositionGhostFromOrbit`'s current behavior. If the helper fails, return false / null for that anchor pose candidate and log the rejection with the relevant recording ID when available.

Map-presence orbit-to-world-position paths use the same zero-offset wrapper. Map-presence ProtoVessel paths that need an `Orbit` object rather than a world position still route construction through the shared factory, validate/log `missing-body` / non-finite / minimum-SMA failures, and do not call the surface clamp helper because they are intentionally producing an orbit-line vessel, not a transform target. This includes `BuildAndLoadGhostProtoVessel(..., OrbitSegment, ...)` at [GhostMapPresence.cs:6939](../../../Source/Parsek/GhostMapPresence.cs:6939), `BuildAndLoadGhostProtoVessel(IPlaybackTrajectory, string)` at [GhostMapPresence.cs:6997](../../../Source/Parsek/GhostMapPresence.cs:6997) when it receives `endpoint-segment` values copied from stored `traj.OrbitSegments` by `RecordingEndpointResolver` ([RecordingEndpointResolver.cs:318](../../../Source/Parsek/RecordingEndpointResolver.cs:318)), and the same method's `terminal-orbit` fallback seed ([GhostMapPresence.cs:7078](../../../Source/Parsek/GhostMapPresence.cs:7078)) whose current `TerminalOrbitSemiMajorAxis <= 0.0` guard lets NaN through ([GhostMapPresence.cs:7130](../../../Source/Parsek/GhostMapPresence.cs:7130)). All map ProtoVessel seed sources validate/log with `context=map-presence-proto` before constructing an `Orbit`; none invoke the terrain-clamp helper.

Map-presence orbit updates use the same no-clamp validation contract. `UpdateGhostOrbit` for chain ghosts ([GhostMapPresence.cs:1963](../../../Source/Parsek/GhostMapPresence.cs:1963)) and `UpdateGhostOrbitForRecording` ([GhostMapPresence.cs:5074](../../../Source/Parsek/GhostMapPresence.cs:5074)) pass stored segments into `ApplyOrbitToVessel`, and `ApplyOrbitToVessel` directly calls `Orbit.SetOrbit(...)` from segment fields ([GhostMapPresence.cs:5871](../../../Source/Parsek/GhostMapPresence.cs:5871)). Route this update path through the same `map-presence-proto` element/body validation before `SetOrbit`; convert `ApplyOrbitToVessel` to `TryApplyOrbitToVessel` (or equivalent) that returns success/failure for validation rejection, missing `OrbitDriver`/`Orbit`, `Orbit.SetOrbit`, `updateFromParameters`, and orbit-renderer refresh failures. On any failure, log and have both callers remove/retire the existing map vessel through the normal lifecycle path before returning, so the previous valid orbit line cannot remain visible as stale state. The failure branch must also avoid `lifecycleUpdatedThisTick++`, `GetWorldPos3D`, update decision logging, and last-known state updates. Do not call the terrain-clamp world-position helper here.

Do not let upstream map-presence gates hide terminal-orbit data before it reaches validation. `HasTerminalOrbitData` / `HasOrbitData` currently classify NaN and non-positive terminal SMA as no orbit data ([GhostMapPresence.cs:1697](../../../Source/Parsek/GhostMapPresence.cs:1697)), and callers can return before `BuildAndLoadGhostProtoVessel` ([GhostMapPresence.cs:1882](../../../Source/Parsek/GhostMapPresence.cs:1882), [:3100](../../../Source/Parsek/GhostMapPresence.cs:3100), [:3901](../../../Source/Parsek/GhostMapPresence.cs:3901)). `ParsekPlaybackPolicy.HandleGhostCreated` has an additional upstream gate ([ParsekPlaybackPolicy.cs:1000](../../../Source/Parsek/ParsekPlaybackPolicy.cs:1000)) that can return when terminal orbit fields exist but `HasOrbitData == false`, `HasOrbitSegments == false`, and there are no points. In the behavior commit, add a separate "has map-presence orbit candidate" predicate or validation entrypoint that routes non-empty terminal body + terminal elements and endpoint-segment candidates through `map-presence-proto` validation/logging once before returning null/None, and make both `GhostMapPresence` callers and `ParsekPlaybackPolicy` use it before early return. Keep the existing no-spam `VerboseOnChange` behavior for stable per-frame gates, but do not silently bypass validation for `TerminalOrbitBody` present with `TerminalOrbitSemiMajorAxis=NaN`, zero, tiny, large finite negative, or otherwise non-current-gate-compatible values. Large finite negative SMA should pass the shared validator when `abs(sma) >= MinValidSmaMeters`; zero/tiny/NaN still reject with explicit reasons.

Do not let endpoint-segment validation change endpoint-source ordering or conflict semantics. The implementation deliberately changed `RecordingEndpointResolver.TryGetLastMatchingSegment` from a broad `seg.semiMajorAxis <= 0.0` skip to an exact `seg.semiMajorAxis == 0.0` skip, and updated the spawn/tail freshness mirror tests with valid negative-SMA coverage. That preserves the terminal fallback for exactly zero endpoint segments while allowing non-degenerate negative-SMA endpoint seeds to remain valid across map presence and spawn. The map-presence seed path still logs invalid endpoint-segment candidates when encountered and validates the source selected by those ordering rules. Expected outcomes:

- Matching endpoint segment with `sma == 0.0`: log the invalid matching endpoint-segment candidate for evidence, but preserve current resolver source ordering: it is not selected; if matching terminal fallback is available, `endpoint-terminal-orbit` still wins; if no matching terminal exists, the failure remains `endpoint-orbit-segment-missing` or `endpoint-conflict` as `RecordingEndpointResolver` would report.
- Matching endpoint segment with large finite negative SMA (`sma <= -MinValidSmaMeters`): this is valid under the shared validator and is selected by `RecordingEndpointResolver`; terminal fallback does not override it solely because the SMA is negative.
- Matching endpoint segment with tiny positive SMA (`0 < sma < MinValidSmaMeters`) or NaN/non-finite elements: current resolver would select `endpoint-segment`, so map presence validates that selected source, logs `below-min-sma` / `non-finite-elements`, and returns false for the seed; it must not silently fall through to terminal fallback because that would change selected-source ordering.
- No selected endpoint segment and valid matching terminal orbit: `endpoint-terminal-orbit` still wins.
- Terminal body conflict with no selected endpoint segment: `endpoint-conflict` remains reported.

If implementation instead changes the shared resolver, update `VesselSpawner.TryBuildRecordedTerminalOrbitForSpawn` ([VesselSpawner.cs:4976](../../../Source/Parsek/VesselSpawner.cs:4976)) and the mirror predicate/comment in `ResolveLatestStoredOrbitSegmentEndUT` ([VesselSpawner.cs:5286](../../../Source/Parsek/VesselSpawner.cs:5286)) with spawn-specific validation/logging/tests.

### Why the original "landed-vessel orbit junk" concern is addressed

Most recorder OrbitSegments.Add paths are already gated on the vessel being legitimately in space when the segment was started. Specifically `OnVesselGoOnRails` ([FlightRecorder.cs:9036-9053](../../../Source/Parsek/FlightRecorder.cs:9036)):

```csharp
// Layer 1: Surface vessels (LANDED/SPLASHED/PRELAUNCH) stay in place on rails …
if (v.situation == Vessel.Situations.LANDED ||
    v.situation == Vessel.Situations.SPLASHED ||
    v.situation == Vessel.Situations.PRELAUNCH)
{
    … skip orbit segment creation, return.
}
// Layer 2: Vessels below atmosphere — Keplerian orbit ignores drag, …
if (ShouldSkipOrbitSegmentForAtmosphere(v.mainBody.atmosphere, v.altitude, v.mainBody.atmosphereDepth))
{
    … skip orbit segment, return.
}
```

The transition-to-background path ([:9398-9413](../../../Source/Parsek/FlightRecorder.cs:9398)) has the same `isSurfaceVessel` skip. `OnVesselSOIChanged` ([:9230](../../../Source/Parsek/FlightRecorder.cs:9230)) and `OnVesselWillDestroy` ([:9300](../../../Source/Parsek/FlightRecorder.cs:9300)) only finalize an existing segment when `isOnRails` (which those gates normally control).

The missing active-flight surfaces are packed-start, false-alarm resume, and transition-to-background. `StartRecording` can call `InitializeOnRailsOrbitSegment` for `v.packed` after only the atmosphere skip ([FlightRecorder.cs:5712](../../../Source/Parsek/FlightRecorder.cs:5712), [:5730](../../../Source/Parsek/FlightRecorder.cs:5730)). On an airless body, a `LANDED` / `SPLASHED` / `PRELAUNCH` packed vessel could therefore open an orbit segment, later finalized by `FinalizeRecordingState` ([FlightRecorder.cs:6380](../../../Source/Parsek/FlightRecorder.cs:6380)) or `FinalizeOpenOrbitSegment` ([FlightRecorder.cs:9472](../../../Source/Parsek/FlightRecorder.cs:9472)). `TransitionToBackground` skips surface vessels but not atmospheric vessels before opening the background orbit segment ([FlightRecorder.cs:9397](../../../Source/Parsek/FlightRecorder.cs:9397)). This behavior fix must add the same surface/prelaunch/atmosphere gate before packed-start `InitializeOnRailsOrbitSegment`, false-alarm resume orbit creation, and transition-to-background orbit segment creation, then test those gates before relaxing the playback resolver guard.

The missing background-recorder surfaces are similar. `BackgroundRecorder.InitializeOnRailsState` currently treats only `LANDED` / `SPLASHED` as landed ([BackgroundRecorder.cs:3189](../../../Source/Parsek/BackgroundRecorder.cs:3189)), so a packed/on-rails `PRELAUNCH` background vessel on an airless body can fall through to `CreateOrbitSegmentFromVessel` ([BackgroundRecorder.cs:3233](../../../Source/Parsek/BackgroundRecorder.cs:3233)). `CheckpointAllVessels` closes an existing segment and opens a fresh one without rechecking surface or atmosphere state ([BackgroundRecorder.cs:3060](../../../Source/Parsek/BackgroundRecorder.cs:3060)). `OnBackgroundVesselSOIChanged` also reopens with only a `v.orbit != null` check ([BackgroundRecorder.cs:2320](../../../Source/Parsek/BackgroundRecorder.cs:2320)). This behavior fix must add a shared background surface/atmosphere gate before opening or reopening background orbit segments and test `PRELAUNCH` plus landed/splashed/atmospheric reopen cases.

False-alarm resume is another active-flight reopen path. `ResumeRecordingAfterFalseAlarm` can call `CreateOrbitSegmentWithRotation` for a packed vessel when resuming an `OrbitalCheckpoint` section ([FlightRecorder.cs:6728](../../../Source/Parsek/FlightRecorder.cs:6728)). Gate and log surface/prelaunch/atmosphere cases. If the gate suppresses `resumeOrbitSegment`, `RestoreTrackSectionAfterFalseAlarm` must not reopen an `OrbitalCheckpoint` section with `isOnRails=false` and only a warning ([FlightRecorder.cs:6895](../../../Source/Parsek/FlightRecorder.cs:6895)); instead downgrade/reopen as `ReferenceFrame.Absolute` or the appropriate non-orbit section. Seed that replacement section from a current live-vessel absolute sample using the same capture path as normal absolute sampling when the vessel is available. If the live vessel is unavailable, either evaluate the previous checkpoint into a finite absolute point through the validated orbit helper and seed from that result, or leave the section closed until the next real sample and log that reopening was deferred. Tests must assert the first sample/boundary point is in the replacement section's frame and is not copied blindly from an empty or orbit-framed checkpoint section.

Legacy recordings written before these gates can still ship sub-surface "orbit" elements for surface vessels. Tail playback was already exposed via `TryResolveOrbitTailWorldPosition` ([ParsekFlight.cs:20257](../../../Source/Parsek/ParsekFlight.cs:20257)), but the old body-radius guard still suppressed such junk in the in-range distance resolver ([ParsekFlight.cs:20900](../../../Source/Parsek/ParsekFlight.cs:20900)), legacy positioning ([ParsekFlight.cs:23391](../../../Source/Parsek/ParsekFlight.cs:23391)), and pending metadata ([GhostPlaybackEngine.cs:6101](../../../Source/Parsek/GhostPlaybackEngine.cs:6101)). Therefore relaxing those readers is a legacy behavior expansion unless the behavior commit also adds targeted legacy surface-junk rejection. Preferred plan: add a load-time scrub/rejection for surface-only legacy orbit junk (for example recordings whose terminal/surface state is `Landed` or `Splashed`, or whose stored vessel situation / surface metadata identifies a prelaunch surface vessel, whose orbit segment has surface-junk markers such as `epoch=0` or no playable exo coverage, and whose real playback should use `SurfacePos` / points instead). If this is not load-time, then thread recording-level classification into every reader that needs it, including legacy `InterpolateAndPosition` site 2 ([ParsekFlight.cs:23374](../../../Source/Parsek/ParsekFlight.cs:23374)), because segment-only validation there cannot distinguish surface-only legacy junk from valid sub-orbital exo data. Add regression coverage proving those legacy surface-junk segments remain rejected after the SMA threshold is relaxed, while the probe's valid sub-orbital exo segment is accepted.

If broader landed-junk legacy recordings surface later, the right follow-up is a wider `RecordingStore` scrub. This fix only needs the targeted surface-junk guard/regression above so the three readers that used to be protected by the radius threshold do not silently start accepting obvious legacy surface junk.

### Logging

Add one `ParsekLog.VerboseRateLimited` line at each rejection site, distinguishing finite-element vs. minimum-SMA failure modes. Use `traj.RecordingId` (or the equivalent — see Implementation Surface for parameter threading) as the dedup key:

```csharp
ParsekLog.VerboseRateLimited(
    "Playback",
    "orbit-resolver-reject-" + (recordingId ?? "<none>") + "-" + context,
    string.Format(CultureInfo.InvariantCulture,
        "Orbit segment rejected by resolver: context={0} |sma|={1:F0} epoch={2:F1} body={3} reason={4}",
        context ?? "<none>",
        absSma, seg.epoch, seg.bodyName ?? "<none>",
        FormatOrbitRejectionReason(reason)));
```

Do not use `IsFiniteOrbitSegment` as a silent production guard. It is a pure predicate for tests and composition. Production paths that reject a stored orbit segment use either:

- `TryCreateOrbitFromSegment(..., recordingId, context, ...)` / `TryResolveOrbitFromSegment(..., recordingId, context, ...)`, where the helper logs before returning false; or
- `TryValidateOrbitSegment(seg, out OrbitRejectionReason reason)` followed by `LogOrbitSegmentRejected(recordingId, context, seg, reason)` at the call site.

This applies to the distance resolver, `TryGetOrbitForSegment`, `PositionGhostFromOrbit`, legacy site 2, pending metadata site 3, recorded anchor resolvers, `TrajectoryMath` orbital checkpoint helper, and map-presence orbit helpers. The `context` string should distinguish call sites (`distance`, `position`, `reapply`, `legacy-position`, `pending-metadata`, `anchor-flight`, `anchor-background`, `anchor-recorder`, `anchor-relative`, `anchor-production`, `trajectory-checkpoint`, `map-presence-position`, `map-presence-proto`) so the rate-limited log remains debuggable. For pending metadata, do not use a pure `TryValidateOrbitSegment` predicate if `missing-body` logging is expected: introduce a body-resolving body-metadata validation helper that preserves the `altitude=0.0` metadata contract but can reject/log null, empty, or unresolved bodies in `ValidateAndLog` mode.

In `ValidateAndLog` mode, `TryCreateOrbitFromSegment` / `TryResolveOrbitFromSegment` also log `missing-body` when the injected resolver returns null and `orbit-construction-failed` if the KSP `Orbit` constructor throws despite prior validation. Those are production rejection branches in the behavior commit, and tests should assert them. In `PreserveLegacyBehavior` mode, keep the old call site's missing-body return, exception propagation/catch behavior, and logging/no-logging behavior.

World-position helper failures also log at the caller with the same `recordingId` / `context`, using a distinct reason such as `non-finite-position-before-body-api`, `non-finite-position-after-clamp`, `orbit-position-failed`, or `body-api-failed`. The helper must expose this via `out OrbitWorldPositionFailureReason` (or equivalent), so callers can preserve old catch/return behavior in extraction mode and emit precise logs in behavior mode without coupling the pure core seam to `ParsekLog`.

## Test Plan

xUnit tests live in `Source/Parsek.Tests/`. Use `[Collection("Sequential")]` for any test that touches `ParsekLog.TestSinkForTesting` or `TestBodyRegistry`.

### Step 0 — extract the static seam

`TryResolveOrbitWorldPosition` references `orbitCache` (instance `Dictionary<long, Orbit>` on `ParsekFlight`) and `FlightGlobals.Bodies?.Find(...)`. Extract a new helper class `OrbitResolution` ([Source/Parsek/OrbitResolution.cs](../../../Source/Parsek/OrbitResolution.cs), new file):

```csharp
internal static class OrbitResolution
{
    internal enum OrbitSegmentValidationMode
    {
        PreserveLegacyBehavior,
        ValidateAndLog,
    }

    internal enum OrbitRejectionReason
    {
        None,
        NonFiniteElements,
        BelowMinimumSma,
        MissingBody,
        OrbitConstructionFailed,
    }

    internal enum OrbitWorldPositionFailureReason
    {
        None,
        OrbitPositionFailed,
        NonFinitePositionBeforeBodyApi,
        BodyApiFailed,
        NonFinitePositionAfterClamp,
    }

    internal struct OrbitPlacementResult
    {
        internal Vector3d RawWorldPosition;
        internal Vector3d FinalWorldPosition;
        internal double Altitude;
        internal bool TerrainClamped;
        internal Orbit Orbit;
        internal CelestialBody Body;
    }

    internal static bool IsFiniteOrbitSegment(OrbitSegment seg) { … }

    internal static bool IsFiniteDouble(double v) { … }

    internal static bool IsFiniteVector3d(Vector3d v) { … }

    internal static bool TryValidateOrbitSegment(
        OrbitSegment seg,
        out OrbitRejectionReason reason)
    { … }

    internal static void LogOrbitSegmentRejected(
        string recordingId,
        string context,
        OrbitSegment seg,
        OrbitRejectionReason reason)
    { … }

    internal static bool TryCreateOrbitFromSegment(
        OrbitSegment seg,
        Func<string, CelestialBody> bodyResolver,
        OrbitSegmentValidationMode mode,
        string recordingId,
        string context,
        out Orbit orbit,
        out CelestialBody body)
    { … }

    internal static bool TryResolveOrbitFromSegment(
        OrbitSegment seg,
        Func<string, CelestialBody> bodyResolver,
        long cacheKey,
        IDictionary<long, Orbit> orbitCache,
        OrbitSegmentValidationMode mode,
        string recordingId,
        string context,
        out Orbit orbit,
        out CelestialBody body)
    { … }

    internal static bool TryComputeOrbitWorldPositionCore(
        Vector3d rawWorldPos,
        Vector3d additiveWorldOffset,
        Func<Vector3d, double> altitudeResolver,
        Func<Vector3d, Vector3d> surfaceClampResolver,
        out OrbitWorldPositionFailureReason failureReason,
        out Vector3d worldPos)
    { … }

    internal static bool TryComputeOrbitWorldPosition(
        Orbit orbit,
        CelestialBody body,
        double targetUT,
        Vector3d additiveWorldOffset,
        out OrbitWorldPositionFailureReason failureReason,
        out OrbitPlacementResult placement)
    { … }

    internal static bool TryResolveOrbitWorldPosition(
        IList<OrbitSegment> segments,
        double targetUT,
        long orbitCacheBase,
        Func<string, CelestialBody> bodyResolver,
        IDictionary<long, Orbit> orbitCache,
        OrbitSegmentValidationMode mode,
        string recordingId,
        string context,
        out OrbitWorldPositionFailureReason failureReason,
        out Vector3d worldPos)
    { … }
}
```

`ParsekFlight` becomes a thin wrapper that supplies its `orbitCache` field and `name => FlightGlobals.Bodies?.Find(b => b.name == name)` as the production resolver. `TryResolveOrbitWorldPosition`, `TryGetOrbitForSegment`, and `PositionGhostFromOrbit` construct cached orbits through `OrbitResolution.TryResolveOrbitFromSegment`; uncached stored-`OrbitSegment` playback constructors use `OrbitResolution.TryCreateOrbitFromSegment`, including `TryResolveFlightOrbitalAnchorPose` ([ParsekFlight.cs:15532](../../../Source/Parsek/ParsekFlight.cs:15532)), `BackgroundRecorder.TryResolveBackgroundOrbitalAnchorPose` ([BackgroundRecorder.cs:5096](../../../Source/Parsek/BackgroundRecorder.cs:5096)), `FlightRecorder.TryResolveOrbitalAnchorPoseForRecorder` ([FlightRecorder.cs:7685](../../../Source/Parsek/FlightRecorder.cs:7685)), `RecordedRelativeAnchorPoseResolver.TryResolveOrbitalAnchorPose` ([RecordedRelativeAnchorPoseResolver.cs:252](../../../Source/Parsek/RecordedRelativeAnchorPoseResolver.cs:252)), `ProductionAnchorWorldFrameResolver.TryResolveOrbitalAnchorPose` ([Rendering/ProductionAnchorWorldFrameResolver.cs:556](../../../Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:556)), `TrajectoryMath.EvaluateOrbitSegmentAtUT` ([TrajectoryMath.cs:165](../../../Source/Parsek/TrajectoryMath.cs:165)), `GhostMapPresence.TryResolveOrbitWorldPosition` ([GhostMapPresence.cs:4217](../../../Source/Parsek/GhostMapPresence.cs:4217)), `GhostMapPresence.BuildAndLoadGhostProtoVessel(..., OrbitSegment, ...)` ([GhostMapPresence.cs:6939](../../../Source/Parsek/GhostMapPresence.cs:6939)), and the endpoint-segment plus terminal-orbit seeds used by `GhostMapPresence.BuildAndLoadGhostProtoVessel(IPlaybackTrajectory, string)` ([GhostMapPresence.cs:6997](../../../Source/Parsek/GhostMapPresence.cs:6997)) via `RecordingEndpointResolver` ([RecordingEndpointResolver.cs:318](../../../Source/Parsek/RecordingEndpointResolver.cs:318)) / `TryResolveTerminalOrbitGhostSeed` ([GhostMapPresence.cs:7078](../../../Source/Parsek/GhostMapPresence.cs:7078)). The goal is that every playback/anchor/map-presence path that constructs an `Orbit` from stored `OrbitSegment` data sees the same validation/logging before `new Orbit(...)`. Sites 2 (`InterpolateAndPosition` legacy) and 3 (`TryResolvePendingOrbitSegmentInterpolation`) get their own thin-wrapper conversions. The behavior change is the SMA threshold and the new finite checks; the surface delta is otherwise mechanical.

For the extraction commit, use `OrbitSegmentValidationMode.PreserveLegacyBehavior` (or an equivalent compatibility wrapper) so the helper reproduces each caller's old missing-body handling, exception propagation/catch behavior, SMA threshold, and logging/no-logging behavior. The behavior-change commit flips the relevant call sites to `ValidateAndLog`; do not mix the new rejection behavior into the mechanical extraction commit.

**Test seam**: tests use [`TestBodyRegistry.Install("Kerbin", 600000.0, 3.5316e12)`](../../../Source/Parsek.Tests/TestBodyRegistry.cs:24), then pass an explicit resolver into `OrbitResolution`:

```csharp
CelestialBody BodyResolver(string name)
{
    return TestBodyRegistry.ResolveBodyByName(name, out CelestialBody body)
        ? body
        : null;
}
```

Do not rely on `FlightGlobals.Bodies?.Find(...)` in xUnit; the fake body registry exposes `ResolveBodyByName` precisely so tests do not depend on the KSP global list shape. Production wrappers keep the `FlightGlobals.Bodies` lookup. Cache: pass an empty `Dictionary<long, Orbit>`; cache reuse across calls is exercised by calling the resolver twice with the same cache key.

Do not use `TryResolvePointWorldPosition` or any direct `CelestialBody.GetWorldSurfacePosition` surface projection in pure xUnit tests unless a dedicated test seam is added first. `TestBodyRegistry` creates enough fake body state for explicit `ResolveBodyByName` / `Radius` lookup and existing metadata tests; it does not guarantee initialized body transforms. The finite/clamp ordering is covered by `TryComputeOrbitWorldPositionCore`; if KSP `Orbit` construction remains awkward in xUnit, keep the constructor/factory tests focused on validation, missing-body logging, cache insertion, and body resolver behavior, and leave full `Orbit` math to integration/manual validation.

### Unit — `TryResolveOrbitWorldPosition` accepts sub-orbital orbits

New file: `Source/Parsek.Tests/PlaybackDistanceResolverOrbitTests.cs`. Class is `[Collection("Sequential")]`.

- **Pre-fix companion (locks in the fall-through hazard without live KSP surface projection).** Build a recording with section topology mirroring the reproduction: section 0 Absolute (1 point), section 1 Relative (last frame at UT 142.16 with `latitude=-0.29, longitude=-4.99, altitude=0.03` — anchor-local metres), section 2 OrbitalCheckpoint (no frames, ORBIT_SEGMENT with `sma=512941, ecc=0.575, epoch=142.16, body=Kerbin`), section 3 Absolute (1 point at UT 415, planetary lat/lon). Call `TrajectoryMath.InterpolatePoints(traj.Points, …)` at UT 142.169 and assert the selected `before.ut` maps to a `ReferenceFrame.Relative` section while `targetUT` maps to `ReferenceFrame.OrbitalCheckpoint`. This pins the exact hazard: if the orbit resolver falls through, the flat-points fallback will consume a Relative-frame bracket point. The planet-scale distance confirmation uses the retained manual/in-game repro, not xUnit fake `CelestialBody.GetWorldSurfacePosition`.
- **Post-fix.** Same recording, call `OrbitResolution.TryResolveOrbitWorldPosition(traj.OrbitSegments, ut=142.169, …)`. Assert it returns true, resolves to a finite world position, and reports `failureReason=None`.
- **Companion: degenerate orbits still rejected.**
  - `sma = 0` → returns false.
  - `sma = NaN` → returns false.
  - `sma = +∞` → returns false.
  - Any other element NaN (inc, ecc, lan, argPe, mna, epoch) → returns false.
  - Rejected segments do not add an `Orbit` entry to the supplied cache (proves validation happens before `new Orbit(...)` / cache insertion).
  - The uncached factory path also returns false for the same degenerate elements (covers playback anchor callers that do not use `orbitCache`).
- **Companion: true orbital still works.** `sma = Kerbin.Radius * 1.2` (above the old guard threshold) → resolves.
- **Companion: non-finite computed world positions are rejected, with ordering pinned.** Call `OrbitResolution.TryComputeOrbitWorldPositionCore` directly with a non-finite `rawWorldPos`; assert it returns false with `failureReason=NonFinitePositionBeforeBodyApi` and the `altitudeResolver` / `surfaceClampResolver` delegates were not invoked. Then call it with a finite below-surface raw position and a clamp delegate that returns `Vector3d(NaN, …)`; assert it returns false with `failureReason=NonFinitePositionAfterClamp` after invoking the clamp. Add a throwing delegate case for `BodyApiFailed`. This proves "no body API before finite check", "re-check after clamp", and precise failure reporting without relying on xUnit `CelestialBody` transform state.
- **Logging.** Capture `ParsekLog.TestSinkForTesting`. In `ValidateAndLog` mode, assert `orbit-resolver-reject-…` emits exactly when the resolver/factory or explicit call-site validation returns false, distinguishing `reason=below-min-sma`, `reason=non-finite-elements`, `reason=missing-body`, and `reason=orbit-construction-failed`, and including the call-site `context`. Does NOT emit for the sub-orbital case. Add focused assertions for at least the main resolver, cached factory, uncached factory, legacy site 2 helper path, pending metadata site 3 helper path, one recorded-anchor helper path, and `map-presence-proto` so no behavior-mode production guard remains silent.
- **Map ProtoVessel contract.** Add focused tests around the map-presence ProtoVessel seed helper / construction seam: a degenerate `endpoint-segment` seed logs `context=map-presence-proto` and returns null/false before constructing an `Orbit`; a degenerate `terminal-orbit` fallback seed, including `TerminalOrbitSemiMajorAxis=NaN`, is rejected/logged before `new Orbit(...)`; valid endpoint and terminal sub-orbital seeds construct an `Orbit` without invoking `TryComputeOrbitWorldPositionCore` or any terrain-clamp delegate. Add update-path coverage proving both `UpdateGhostOrbit` and `UpdateGhostOrbitForRecording` / `TryApplyOrbitToVessel` validate before `Orbit.SetOrbit`, return failure for validation and orbit-application/refresh failures, remove/retire any existing map vessel so no stale orbit line remains visible, and do not continue into lifecycle counters, stale position reads, update logs, or last-known state updates. Add entrypoint/gate tests proving `CreateGhostVesselForRecording`, tracking-station decisions, and `ParsekPlaybackPolicy.HandleGhostCreated` do not bypass validation/logging when terminal body exists but terminal SMA is NaN/zero/tiny, and that large finite negative terminal SMA routes through validation and is accepted if KSP orbit construction succeeds. This protects the "validate/log but do not terrain clamp orbit-line vessels" contract.
- **Checkpoint orbit-only caller contract.** Add a focused test seam for `TryInterpolateAndPositionCheckpointSectionWithOrbitRotation`: when `TryPositionGhostFromOrbit` / `TryComputeOrbitWorldPosition` fails, it does not call `body.GetAltitude` on a raw orbit position, does not report a resolved `InterpolationResult`, and emits the rejection log. When it succeeds, interpolation altitude/diagnostics come from the validated/clamped placement metadata.
- **Orbital rotation guards.** Add focused tests for `TryComputeOrbitalRotation` / reapply rotation across checkpoint interpolation, `GhostPosMode.Orbit`, and `GhostPosMode.CheckpointPoint`: finite final position with non-finite velocity, non-finite start position, zero radial vector, zero/NaN spin axis, or non-finite quaternion result logs and preserves current rotation / skips only rotation assignment. No test should allow `Quaternion.LookRotation` or `ApplyGhostReapplyTransform` to receive non-finite rotation inputs.
- **Orbit positioning caller contracts.** Add focused seams/tests proving failure propagation at every post-position boundary: `IGhostPositioner.TryPositionFromOrbit == false` and `IGhostPositioner.TryPositionLoop == false` make `GhostPlaybackEngine` run the orbit-placement-failed cleanup path for a previously active ghost, suppressing part events/visual FX/audio and hiding or retiring the ghost for the frame; it does not activate, mark positioned, restore FX, trace/track appearance, or apply normal post-position visuals from a stale transform. Cover normal render, hidden-tier prewarm, loaded-ghost hidden priming, loaded-ghost watch synchronization, loop primary, loop overlap, loop endpoint, and endpoint positioning. `PositionGhostAtRecordingEndpoint` failure is covered for all three orbit calls: direct orbit endpoint, checkpoint-backed orbit endpoint, and last-segment orbit fallback; each suppresses endpoint side effects and does not trigger explosion/completion from a stale transform. `TryPositionChainFallbackFromRecording` sees `PositionGhostFromOrbitOnly == false`, does not call `UpdateChainGhostOrbitIfNeeded`, does not emit the "positioned" log, and returns false / follows its existing fallback path.
- **Endpoint-segment prefilter.** Add map-presence tests for explicit endpoint outcomes. With a matching terminal fallback available, `sma=0` endpoint segment logs invalid candidate evidence but terminal fallback still wins because the resolver skips exactly zero SMA; large finite negative SMA is selected as a valid endpoint seed instead of treated as a resolver-ordering skip; tiny positive SMA and NaN/non-finite endpoint segment values log rejection and fail the selected `endpoint-segment` source instead of falling through to terminal. Add companion tests that terminal-orbit precedence and endpoint-conflict behavior match `RecordingEndpointResolver` when no selected endpoint segment exists. Also add spawn/tail freshness tests for the shared negative-SMA behavior.

### Unit — sibling SMA guard sites

Sites 2 (`InterpolateAndPosition` legacy non-checkpoint) and 3 (`TryResolvePendingOrbitSegmentInterpolation`) each take their own focused unit test:

- Site 2: build a recording with flat `Points` + a sub-orbital `OrbitSegment` and no track sections (legacy v0 schema). Do not instantiate Unity `GameObject` in xUnit. Instead, test the extracted acceptance helper that site 2 calls (same finite/min-SMA predicate and segment lookup) and assert the sub-orbital segment is selected as usable rather than rejected. Add the companion legacy surface-junk case from `LegacySurfaceOrbitJunkTests`: when recording-level classification marks the recording as surface-only landed/splashed/prelaunch, the same site-2 path must see the scrubbed/rejected segment and fall through rather than accepting it. Actual transform positioning through `InterpolateAndPosition` is covered by manual/in-game repro because it requires Unity scene objects.
- Site 3 / orbit precedence: build the same recording with no active surface section and no authored-frame shadow at the target UT, then call `TryResolvePendingPlaybackInterpolation` directly. Assert `result.bodyName = "Kerbin"` and `result.altitude == 0.0`, matching the existing pending-metadata contract. The regression is that the sub-orbital segment is accepted as the metadata source on the orbit-precedence branch instead of falling through to point/surface metadata; this fix does not compute real orbit altitude in the pending resolver. Add a `ValidateAndLog` body-resolution case for site 3: empty or unresolved `bodyName` rejects/logs `reason=missing-body` with `context=pending-metadata`, while extraction/compatibility mode preserves the old silent/fallback behavior.
- Site 3 / active surface section: build or update the surface-section case so the sub-orbital orbit segment is accepted as a candidate (`canUseOrbitPrecedence == true`), the existing "surface track section active, skipping orbit precedence" diagnostic is logged, and the result still comes from the point/surface fallback rather than the orbit segment. Surface-section branch ordering is intentionally preserved.

Both tests already need `TestBodyRegistry.Install("Kerbin", 600000.0, 3.5316e12)`.

### Existing test updates

Update stale assertions that pin the old radius-threshold rule:

- [`GhostPlaybackEngineTests.TryResolvePendingPlaybackInterpolation_SubSurfaceMixedOrbitSegment_FallsBackToPoints`](../../../Source/Parsek.Tests/GhostPlaybackEngineTests.cs:3618) should become a sub-orbital-accepted test. Expected: `resolved == true`, `result.bodyName` from the orbit segment, `result.altitude == 0.0`.
- [`GhostPlaybackEngineTests.TryResolvePendingPlaybackInterpolation_SurfaceTrackSection_SubSurfaceOrbitSegment_DoesNotLogSkippedOrbitPrecedence`](../../../Source/Parsek.Tests/GhostPlaybackEngineTests.cs:3962) also pins the old outcome. With sub-orbital segments now accepted as orbit-precedence candidates, `surfaceSkip && canUseOrbitPrecedence` should log the existing "surface track section active, skipping orbit precedence" diagnostic, then preserve the existing point/surface fallback result. Update only the log/candidate expectation, not the resolved metadata source.
- [`BugfixRegressionTests.SmaSubSurfaceCheckTests`](../../../Source/Parsek.Tests/BugfixRegressionTests.cs:269) currently treats positive `sma=400000` as rejected. Replace or rewrite this helper coverage around the new predicate: finite positive sub-orbital SMA above `MinValidSmaMeters` is accepted; zero, tiny below 1 m, NaN, and infinity are rejected; large negative hyperbolic SMA remains accepted when finite and `abs(sma) >= 1.0`.

### Integration — end-to-end the reproduction case

Synthetic recording mirrors `rec_f1363fc127ab47a28812ce4be6515453` plus a sibling anchor recording so the Relative frames resolve.

Drive `engine.ResolvePlaybackDistanceOverride = ParsekFlight.ResolvePlaybackDistanceForEngine` (or a test-friendly variant) at `playbackUT = 145.0`. Assert the resulting distance is **plausible** (within 100 km). Assert the test sink does NOT contain the new `orbit-resolver-reject-…` log line for this UT (the orbit *is* accepted now).

### Manual repro check

Reload the bundle save (`logs/2026-05-10_2123/saves/s15`):

1. Verify deployed DLL matches build (per `.claude/CLAUDE.md` recipe — UTF-16 grep for the new log tag `orbit-resolver-reject` on `GameData/Parsek/Plugins/Parsek.dll`).
2. Watch the upper-stage chain → expect both upper-stage and booster ghosts visible together at 1× rate during the exo on-rails section.
3. Time-warp → both should remain visible (no behavior regression on the warp-exempt path).
4. Exit watch → both should fall under standard zone-hide rules from the launch-pad active vessel (booster legitimately far from active should still hide; the fix is about *consistency between Path A and Path B*, not unconditional render).

## Risk Assessment

### Risk 1 — landed-vessel orbit junk slipping through the relaxed guard

**Mitigated by existing gates plus one required recorder fix.** Most recorder OrbitSegments.Add sites are already gated:

- `OnVesselGoOnRails` ([FlightRecorder.cs:9036](../../../Source/Parsek/FlightRecorder.cs:9036)): explicit early-return for `LANDED/SPLASHED/PRELAUNCH` and for in-atmosphere vessels.
- `OnVesselSOIChanged` ([:9230](../../../Source/Parsek/FlightRecorder.cs:9230)): finalizes only when `isOnRails` (which the above gates control).
- `OnVesselWillDestroy` ([:9300](../../../Source/Parsek/FlightRecorder.cs:9300)): finalizes only when `isOnRails`.
- Transition-to-background ([:9398](../../../Source/Parsek/FlightRecorder.cs:9398)): explicit `isSurfaceVessel` skip on the new segment creation; preserves the existing-segment finalize path which is `isOnRails`-gated.
- `FinalizeOpenOrbitSegment` ([:9472](../../../Source/Parsek/FlightRecorder.cs:9472)): only if `isOnRails`.
- Required in this fix: packed-start `StartRecording` ([FlightRecorder.cs:5712](../../../Source/Parsek/FlightRecorder.cs:5712), [:5730](../../../Source/Parsek/FlightRecorder.cs:5730)) must apply the same `LANDED` / `SPLASHED` / `PRELAUNCH` surface-vessel skip before calling `InitializeOnRailsOrbitSegment`, so an airless-body landed packed vessel cannot set `isOnRails` and later finalize junk.
- Required in this fix: false-alarm resume ([FlightRecorder.cs:6728](../../../Source/Parsek/FlightRecorder.cs:6728)) must apply the same surface/prelaunch/atmosphere skip before creating a resumed orbit segment. If suppressed, `RestoreTrackSectionAfterFalseAlarm` ([FlightRecorder.cs:6895](../../../Source/Parsek/FlightRecorder.cs:6895)) must reopen as Absolute/appropriate non-orbit section with boundary seeding from a live absolute sample, a validated finite checkpoint evaluation, or a logged deferred reopen, not as an `OrbitalCheckpoint` section without `isOnRails`.
- Required in this fix: transition-to-background ([FlightRecorder.cs:9397](../../../Source/Parsek/FlightRecorder.cs:9397)) must apply the atmosphere skip before opening a new background orbit segment and log the skip.
- Required in this fix: `BackgroundRecorder.InitializeOnRailsState` ([BackgroundRecorder.cs:3189](../../../Source/Parsek/BackgroundRecorder.cs:3189), [:3233](../../../Source/Parsek/BackgroundRecorder.cs:3233)) must classify `PRELAUNCH` as surface/non-orbit data, and `CheckpointAllVessels` ([BackgroundRecorder.cs:3060](../../../Source/Parsek/BackgroundRecorder.cs:3060)) plus `OnBackgroundVesselSOIChanged` ([BackgroundRecorder.cs:2320](../../../Source/Parsek/BackgroundRecorder.cs:2320)) must recheck surface/prelaunch/atmosphere before reopening a fresh orbit segment.
- Required in this fix: add load-time targeted legacy surface-junk scrub/rejection, or thread recording-level classification into site 2 and the other affected readers, so in-range distance, legacy positioning, and pending metadata do not start accepting obvious surface-only legacy junk that the old radius threshold suppressed.

Residual legacy risk remains for weird old recordings outside that targeted pattern; document as accepted residual risk and prefer a broader `RecordingStore` migration only if field evidence appears.

### Risk 2 — distance-resolver behavior change

Path B currently produces a 660 km bogus distance for sub-orbital ghosts that fall through the SMA guard. The fix replaces that with a real distance (likely 100-500 m in the reproduction). Anything downstream that relied on the bogus large value would change.

Audit:

- `state.lastDistance` / `state.lastRenderDistance` — cached and read by zone classification. New plausible distance → ghost stays in `Physics`/`Visual` zone instead of `Beyond`. **This is the desired behavior change.**
- Watch-cutoff distance comparisons ([WatchModeController](../../../Source/Parsek/WatchModeController.cs)): with a plausible distance, watch holds correctly instead of false-positively exiting. **Desired.**
- Map-presence orbit-line decisions: use the orbit segments directly, not the resolver's distance. No effect.

### Risk 3 — sibling SMA-guard sites change behavior for non-bug-repro recordings

Sites 2 and 3 affect legacy non-checkpoint recordings and pending-ghost metadata respectively. Recordings written today use checkpoint sections, so site 2 only fires for old recordings; site 3 fires briefly during scene-load.

Site 2 behavior change: a sub-orbital legacy recording (e.g. an old test save) would now position via orbit math instead of falling through to point interpolation. This is the same behavior change as the primary fix — sub-orbital arcs become valid playback data on the legacy path too. If a recording's flat `Points` list happens to disagree with its `OrbitSegments` for sub-orbital UTs, the orbit now wins. Mitigation: the xUnit test for site 2 pins the extracted acceptance decision without requiring Unity scene objects, and the manual/in-game repro verifies the transform-positioning path.

Site 3 behavior change: pending-ghost metadata for a sub-orbital UT is now allowed to use the orbit segment as an orbit-precedence candidate. On the non-surface/no-shadow branch, the result uses the orbit segment's body name, matching the body the ghost will actually be on once positioned, and preserves the existing placeholder altitude `0.0`. On active surface sections, the branch intentionally logs "surface track section active, skipping orbit precedence" and falls through to the point/surface metadata result exactly as today. This plan does not add orbit-altitude evaluation to pending metadata. Mitigation: tests cover both branches — orbit body/altitude `0.0` for non-surface precedence, and preserved point/surface metadata plus the skip log for active surface sections.

### Risk 4 — `OrbitResolution` extraction touches production code beyond the bug

Pure-mechanical extraction. No behavior change in the wrapper sites — `ParsekFlight` retains `orbitCache` and supplies the body resolver, while anchor resolvers keep their existing body-resolution contracts. Extraction means moving the orbit-construction / validation / world-position code into `OrbitResolution` and converting the distance, positioning, pending metadata, legacy, and recorded-anchor callers to thin wrappers; review surface is larger than the original single-site fix, but it keeps all stored-`OrbitSegment` playback readers on one validation path.

### Risk 5 — orbit cache key collision

`TryResolveOrbitWorldPosition` reuses `orbitCache[orbitCacheBase + i]`. The `orbitCacheBase = index * 10000` allocation in callers ensures no collision across recordings. Sub-orbital orbits are constructed identically to orbital ones; no cache contract change.

## Out of Scope (for v1)

- **Frame-aware flat-points fallback** (was the rev-1 secondary fix). Dropped: the only correct version requires per-bracket-point section lookup, since `TrajectoryMath.InterpolatePoints` ([TrajectoryMath.cs:830](../../../Source/Parsek/TrajectoryMath.cs:830)) brackets by UT alone and freely spans sections. A correct future implementation would look up the section of each of `before.ut` and `after.ut` returned by `InterpolatePoints` and reject if either maps to `ReferenceFrame.Relative`. The primary fix (relaxing the SMA guard so the orbit resolver succeeds) prevents the fall-through from being reached for this bug; defending the fallback itself is a separate hardening task with its own test surface.
- Broad recorder-side cleanup of unusual legacy landed-vessel orbit junk beyond the targeted surface-only guard in this fix. Track as a follow-up if real-world fallout surfaces.
- Repo-wide audit of all flat-`traj.Points` consumers for frame-awareness. CLAUDE.md guidance is repeated for a reason; a per-call-site audit is its own task.
- Unifying Path A and Path B into a single "resolve world position from recording" API. They serve different needs (positioning vs. distance estimation) and have different fallback contracts.

## Implementation Surface

| Step | File | Change |
|---|---|---|
| 1 | `Source/Parsek/OrbitResolution.cs` | New file. Static helper class. `OrbitSegmentValidationMode`, `OrbitRejectionReason`, `OrbitWorldPositionFailureReason`, `OrbitPlacementResult` (or equivalent placement metadata DTO), `IsFiniteOrbitSegment`, `IsFiniteDouble`, `IsFiniteVector3d`, `TryValidateOrbitSegment`, `LogOrbitSegmentRejected`, `TryCreateOrbitFromSegment`, `TryResolveOrbitFromSegment`, `TryComputeOrbitWorldPositionCore`, `TryComputeOrbitWorldPosition`, `TryResolveOrbitWorldPosition`. Body resolver, orbit cache, validation mode, `recordingId`, and `context` passed in as parameters where needed. |
| 2 | `Source/Parsek/ParsekFlight.cs` | Replace inlined `TryResolveOrbitWorldPosition` with a thin wrapper delegating to `OrbitResolution`, supplying `orbitCache`, validation mode, `recordingId`, `context="distance"`, and `name => FlightGlobals.Bodies?.Find(b => b.name == name)`. Thread `traj.RecordingId` through to the resolver and propagate `OrbitWorldPositionFailureReason` for logging. Also route both `TryGetOrbitForSegment` ([:19944](../../../Source/Parsek/ParsekFlight.cs:19944)) and `PositionGhostFromOrbit` ([:19001](../../../Source/Parsek/ParsekFlight.cs:19001)) through `OrbitResolution.TryResolveOrbitFromSegment` with call-site contexts, so cached playback paths validate/log before constructing/caching `new Orbit(...)`. |
| 3 | `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/BackgroundRecorder.cs`, `Source/Parsek/FlightRecorder.cs`, `Source/Parsek/RecordedRelativeAnchorPoseResolver.cs`, `Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs`, `Source/Parsek/TrajectoryMath.cs`, `Source/Parsek/GhostMapPresence.cs`, `Source/Parsek/ParsekPlaybackPolicy.cs`, `Source/Parsek/RecordingEndpointResolver.cs`, `Source/Parsek/VesselSpawner.cs` | Route uncached stored-`OrbitSegment` orbit construction through `OrbitResolution.TryCreateOrbitFromSegment`: `TryResolveFlightOrbitalAnchorPose` ([:15532](../../../Source/Parsek/ParsekFlight.cs:15532)), `BackgroundRecorder.TryResolveBackgroundOrbitalAnchorPose` ([:5096](../../../Source/Parsek/BackgroundRecorder.cs:5096)), `FlightRecorder.TryResolveOrbitalAnchorPoseForRecorder` ([:7685](../../../Source/Parsek/FlightRecorder.cs:7685)), `RecordedRelativeAnchorPoseResolver.TryResolveOrbitalAnchorPose` ([:252](../../../Source/Parsek/RecordedRelativeAnchorPoseResolver.cs:252)), `ProductionAnchorWorldFrameResolver.TryResolveOrbitalAnchorPose` ([:556](../../../Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs:556)), `TrajectoryMath.EvaluateOrbitSegmentAtUT` ([:165](../../../Source/Parsek/TrajectoryMath.cs:165)), `GhostMapPresence.TryResolveOrbitWorldPosition` ([:4217](../../../Source/Parsek/GhostMapPresence.cs:4217)), `GhostMapPresence.BuildAndLoadGhostProtoVessel(..., OrbitSegment, ...)` ([:6939](../../../Source/Parsek/GhostMapPresence.cs:6939)), the `endpoint-segment` seed path inside `GhostMapPresence.BuildAndLoadGhostProtoVessel(IPlaybackTrajectory, string)` ([:6997](../../../Source/Parsek/GhostMapPresence.cs:6997)), that same method's `terminal-orbit` fallback seed ([:7078](../../../Source/Parsek/GhostMapPresence.cs:7078)), `GhostMapPresence.UpdateGhostOrbit` / `UpdateGhostOrbitForRecording` / `ApplyOrbitToVessel` ([:1963](../../../Source/Parsek/GhostMapPresence.cs:1963), [:5074](../../../Source/Parsek/GhostMapPresence.cs:5074), [:5871](../../../Source/Parsek/GhostMapPresence.cs:5871)), and the `ParsekPlaybackPolicy.HandleGhostCreated` map-presence gate ([:1000](../../../Source/Parsek/ParsekPlaybackPolicy.cs:1000)). In extraction commit only, use `PreserveLegacyBehavior` and preserve each call site's existing threshold/fallback behavior. In the behavior commit, the shared endpoint resolver was updated to skip exactly `sma == 0.0`, accept non-degenerate negative SMA, and update the spawn tail freshness mirror tests accordingly. |
| 4 | `Source/Parsek/OrbitResolution.cs` | Apply the new rule: `!IsFiniteOrbitSegment(seg) || abs(sma) < MinValidSmaMeters` (1.0 m), replacing `absSma < bodyRadius * 0.9`. Add `TryComputeOrbitWorldPositionCore` and production wrappers with finite checks on `worldPos` before body API calls and after the optional surface clamp. |
| 4a | `Source/Parsek/FlightRecorder.cs`, `Source/Parsek/BackgroundRecorder.cs` | Add missing recorder surface gates. In `FlightRecorder.StartRecording`, when `v.packed` and `v.situation` is `LANDED`, `SPLASHED`, or `PRELAUNCH`, skip `InitializeOnRailsOrbitSegment`, leave `isOnRails=false`, and log the skip before any later finalize path can add a landed orbit segment. In `FlightRecorder.ResumeRecordingAfterFalseAlarm`, apply the same surface/prelaunch/atmosphere skip before `CreateOrbitSegmentWithRotation`; if an `OrbitalCheckpoint` resume is suppressed, reopen as Absolute/appropriate non-orbit section with boundary seeding from a live absolute sample, a validated finite checkpoint evaluation, or a logged deferred reopen instead of an orphaned checkpoint. In `FlightRecorder.TransitionToBackground`, apply the atmosphere skip before opening the background orbit segment and log the skip. In `BackgroundRecorder.InitializeOnRailsState`, classify `PRELAUNCH` with landed/splashed non-orbit data before `CreateOrbitSegmentFromVessel`; in `CheckpointAllVessels` and `OnBackgroundVesselSOIChanged`, recheck `LANDED` / `SPLASHED` / `PRELAUNCH` and atmosphere before reopening a fresh orbit segment. Preserve existing packed atmospheric behavior. |
| 4b | `Source/Parsek/RecordingStore.cs` preferred, or shared playback validation helper plus reader signature changes | Add targeted legacy surface-junk rejection before relaxing reader guards. Preferred: load-time scrub/rejection marks/removes surface-only legacy orbit segments before any reader runs. If not load-time, thread recording-level classification into distance, legacy-position site 2, and pending metadata; segment-only validation is insufficient for site 2. Surface-only legacy recordings with `TerminalStateValue=Landed/Splashed`, `SurfacePos`/`TerminalPosition` or prelaunch vessel-situation evidence, and surface-junk orbit markers such as `epoch=0` / no playable exo coverage must have those orbit segments scrubbed or rejected before distance, legacy-position, and pending-metadata readers can accept them. Do not reject valid sub-orbital exo recordings that later land. |
| 5 | `Source/Parsek/OrbitResolution.cs` | Add `ParsekLog.VerboseRateLimited("Playback", "orbit-resolver-reject-" + recordingId + "-" + context, …)` on rejection branches, with reason="below-min-sma", "non-finite-elements", "missing-body", or "orbit-construction-failed". |
| 6 | `Source/Parsek/ParsekFlight.cs` | Site 2: convert `InterpolateAndPosition` ([:23394](../../../Source/Parsek/ParsekFlight.cs:23394)) to use `TryValidateOrbitSegment` + `LogOrbitSegmentRejected` (or a helper wrapper) with `context="legacy-position"`; do not use `IsFiniteOrbitSegment` as a silent guard. Threads `traj.RecordingId` if not already present. |
| 7 | `Source/Parsek/ParsekFlight.cs` | Replace `PositionGhostFromOrbit` ([:19026](../../../Source/Parsek/ParsekFlight.cs:19026)) with `TryPositionGhostFromOrbit` (or equivalent) that calls `OrbitResolution.TryComputeOrbitWorldPosition` with the existing predicted-tail continuity offset, returns `false` without assigning the transform if the offset-adjusted/clamped world position is non-finite, and returns placement metadata on success. Update all direct callers: checkpoint orbit-only ([:19771](../../../Source/Parsek/ParsekFlight.cs:19771)) uses placement for `InterpolationResult` / diagnostics and aborts/logs on failure; predicted-tail positioning ([:17233](../../../Source/Parsek/ParsekFlight.cs:17233)) does not treat a failure as positioned; orbit-only recording playback ([:19477](../../../Source/Parsek/ParsekFlight.cs:19477)) does not activate a stale ghost on failure; legacy orbit positioning ([:23409](../../../Source/Parsek/ParsekFlight.cs:23409)) falls through or returns unresolved without computing metadata from an unchanged transform; `PositionGhostFromOrbitOnly` returns false on placement failure; `TryPositionChainFallbackFromRecording` ([:14276](../../../Source/Parsek/ParsekFlight.cs:14276)) checks that result before orbit updates, success logging, or returning true. Keep existing trace output by preserving raw-position / clamp metadata through the helper or a local wrapper. |
| 7a | `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/OrbitResolution.cs` | Guard orbital rotation inputs for `ComputeOrbitalRotation` ([:19408](../../../Source/Parsek/ParsekFlight.cs:19408)), `GhostPosMode.Orbit` reapply ([:1342](../../../Source/Parsek/ParsekFlight.cs:1342)), and `GhostPosMode.CheckpointPoint` reapply ([:1432](../../../Source/Parsek/ParsekFlight.cs:1432)): finite-check orbit velocities, start positions, radial vectors, spin axes, normalized vectors, and final quaternions before `SafeOrbitalLookRotation`, `Quaternion.LookRotation`, `Quaternion.AngleAxis`, or `ApplyGhostReapplyTransform`. On invalid rotation inputs, log and preserve current rotation / skip rotation assignment without failing an otherwise valid position. |
| 7b | `Source/Parsek/IGhostPositioner.cs`, `Source/Parsek/GhostPlaybackEngine.cs`, `Source/Parsek/ParsekFlight.cs`, `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`, `Source/Parsek/InGameTests/RuntimeTests.cs` | Convert `IGhostPositioner.PositionFromOrbit` to `bool TryPositionFromOrbit(...)` and `IGhostPositioner.PositionLoop` to `bool TryPositionLoop(...)`, or add equivalent failure-latched contracts. Update production and fake/test/in-game implementers (`SpawnPrimingPositioner`, `ReFlySettleHoldPositioner`, `TumblingParentShadowPositioner`, `PendingLoopBoundaryPositioner`, `Bug613RetireBranchPositioner`, and any additional compile errors) in the same commit so the interface change is build-safe. `ParsekFlight` reports orbit placement failure through that contract. `GhostPlaybackEngine` checks it at orbit-tail positioning, before activation/post-position work, inside `PositionGhostAtRecordingEndpoint`, inside `PositionLoadedGhostAtPlaybackUT` for hidden-tier prewarm / hidden priming / watch sync, and inside `PositionLoopAtPlaybackUT` for primary/overlap/endpoint loops. On failure, run an explicit cleanup/retirement branch that suppresses part events, stops visual FX/RCS/audio, hides or marks the ghost retired for the frame, skips activation/trace/appearance tracking/normal `ApplyFrameVisuals`, and prevents endpoint explosions/completion side effects from using a stale transform. |
| 8 | `Source/Parsek/ParsekFlight.cs` | Update `GhostPosMode.Orbit` reapply ([:1322](../../../Source/Parsek/ParsekFlight.cs:1322)) to call `OrbitResolution.TryComputeOrbitWorldPosition` with `e.orbitBody`, `e.orbitUT`, and `ResolveOrbitContinuityOffset(e, e.orbitUT)` before `ApplyGhostReapplyTransform`; skip/log if the helper fails so reapply cannot write NaN/Infinity or undo the surface clamp. |
| 9 | `Source/Parsek/ParsekFlight.cs`, `Source/Parsek/BackgroundRecorder.cs`, `Source/Parsek/FlightRecorder.cs`, `Source/Parsek/RecordedRelativeAnchorPoseResolver.cs`, `Source/Parsek/Rendering/ProductionAnchorWorldFrameResolver.cs`, `Source/Parsek/TrajectoryMath.cs`, `Source/Parsek/GhostMapPresence.cs` | Update recorded anchor/checkpoint/map-position world-position calls to use the zero-offset `TryComputeOrbitWorldPosition` wrapper before returning `AnchorPose` / checkpoint position / map world position. Return false/null and log if the helper rejects; do not return raw `orbit.getPositionAtUT(...)` directly. Do not apply the surface-clamp wrapper to `GhostMapPresence.BuildAndLoadGhostProtoVessel(..., OrbitSegment, ...)` because that path needs an `Orbit` for map vessel/orbit-line creation, not a world transform target. |
| 10 | `Source/Parsek/GhostPlaybackEngine.cs` | Site 3: convert `TryResolvePendingOrbitSegmentInterpolation` ([:6101](../../../Source/Parsek/GhostPlaybackEngine.cs:6101)) to a body-resolving pending-metadata validation helper with `context="pending-metadata"`. It preserves the body-only `InterpolationResult(..., seg.bodyName, 0.0)` contract but, in `ValidateAndLog` mode, rejects/logs non-finite elements, zero/tiny SMA, null/empty/unresolved body names (`missing-body`), and invalid body-radius resolver outputs as applicable. Remove `applySubSurfaceGuard` if call sites can be updated cleanly; otherwise preserve the parameter only as a no-op / ordering hint. It must not bypass zero-SMA or non-finite rejection. |
| 11 | `Source/Parsek.Tests/PlaybackDistanceResolverOrbitTests.cs` | New file. Pre-fix companion test (locks in the bracket-section fall-through hazard). Post-fix unit tests for site 1 (sub-orbital accept, degenerate reject — including each non-SMA element NaN case, orbital still works, log emission, finite-ordering core seam). |
| 12 | `Source/Parsek.Tests/PlaybackInterpolationLegacyOrbitTests.cs` | New file. Site 2 unit test against extracted orbit-acceptance/logging helper (sub-orbital legacy segment is usable; degenerate segments log and are rejected; no Unity `GameObject` required in xUnit). |
| 13 | `Source/Parsek.Tests/PendingPlaybackOrbitMetadataTests.cs` | New file. Site 3 unit tests: non-surface/no-shadow precedence reports orbit body and preserves `altitude=0.0`; active surface section logs skipped orbit precedence and preserves point/surface metadata; degenerate candidate logs/rejects. |
| 13b | `Source/Parsek.Tests/GhostMapPresenceOrbitResolutionTests.cs`, policy-focused tests as needed | New or existing map-presence test file. Covers `map-presence-position` finite/clamp behavior, `map-presence-proto` endpoint-segment and terminal-orbit validation/logging, `UpdateGhostOrbit` / `UpdateGhostOrbitForRecording` / `TryApplyOrbitToVessel` rejecting invalid update segments before `Orbit.SetOrbit`, returning failure for missing orbit driver/orbit, `SetOrbit`, `updateFromParameters`, and renderer-refresh failures, removing/retiring any existing map vessel so no stale orbit line remains visible, and not continuing into lifecycle counters/stale position reads/update logs/last-known state updates. Also cover upstream HasOrbitData/decision gates and `ParsekPlaybackPolicy.HandleGhostCreated` not bypassing terminal validation, no terrain-clamp invocation for orbit-line ProtoVessel construction or update, negative-terminal-SMA acceptance when otherwise valid, and endpoint source/conflict ordering matching `RecordingEndpointResolver` plus the explicit `sma=0` / negative / tiny / NaN outcomes above. |
| 13c | `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`, `Source/Parsek.Tests/ParsekFlightOrbitPositioningTests.cs` or equivalent | Add caller-contract coverage for `IGhostPositioner.TryPositionFromOrbit == false`, `IGhostPositioner.TryPositionLoop == false`, and `PositionGhostFromOrbitOnly == false`: previously active orbit ghosts get teardown/suppression instead of stale activation, no post-position visuals/FX/tracking, hidden-tier prewarm / loaded-ghost priming / watch-sync failures do not apply normal visuals or retarget based on stale transform, loop primary/overlap/endpoint failures suppress normal loop visuals, endpoint orbit failures suppress explosion/completion side effects for direct/checkpoint/last-segment endpoint paths, no chain orbit update, no success log, and false/fallback return. Include orbital rotation guard tests. |
| 13d | `Source/Parsek.Tests/FlightRecorderOrbitSegmentTests.cs`, `Source/Parsek.Tests/BackgroundRecorderTests.cs`, and/or equivalent focused seams/in-game tests | Cover recorder gates: a packed `LANDED` / `SPLASHED` / `PRELAUNCH` active vessel on an airless body skips `InitializeOnRailsOrbitSegment`, leaves `isOnRails=false`, logs the skip, and cannot later append an orbit segment through `FinalizeRecordingState` / `FinalizeOpenOrbitSegment`; false-alarm resume does not create a resumed orbit segment for surface/prelaunch/atmospheric vessels and downgrades/reopens as a non-orbit section whose first boundary/sample is sourced from the current live absolute capture, a validated finite checkpoint evaluation, or a logged deferred reopen; transition-to-background does not open a background orbit segment for atmospheric vessels and logs the skip; a background `PRELAUNCH` vessel initializes as surface/non-orbit data; `CheckpointAllVessels` and background SOI change do not reopen orbit segments for surface/prelaunch/atmospheric vessels. |
| 13e | `Source/Parsek.Tests/LegacySurfaceOrbitJunkTests.cs` or equivalent | Cover targeted legacy surface-junk rejection: legacy surface-only landed/splashed/prelaunch recordings with junk orbit segments that the old radius guard suppressed remain rejected/scrubbed by distance, legacy-position site 2, and pending-metadata readers after the SMA threshold is relaxed; valid sub-orbital exo recordings, including ones that later land, still pass. |
| 14 | `Source/Parsek.Tests/PlaybackDistanceResolverIntegrationTests.cs` | New file. End-to-end: synthetic recording, `ResolvePlaybackDistanceForEngine` reports plausible distance at the bug-trigger UT. |
| 15 | `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`, `Source/Parsek.Tests/BugfixRegressionTests.cs` | Update stale tests that assert the old radius-threshold behavior. |
| 16 | `CHANGELOG.md` | One-line user-facing entry under current version. |
| 17 | `docs/dev/todo-and-known-bugs.md` | Add entry; mark closed when shipped. |
| 18 | `AGENTS.md`, `.claude/CLAUDE.md` | Update the shared agent guidance for the canonical `OrbitResolution` resolver/factory path, `IGhostPositioner.TryPositionFromOrbit`, `IGhostPositioner.TryPositionLoop`, map-presence no-clamp validation, and recorder orbit-gate behavior in the same behavior commit. |

Steps 1-3 (extraction) may ship in their own commit before behavior changes. That extraction commit must preserve each old call site's behavior: old SMA threshold where it existed, old missing-body returns, old exception handling, old no-clamp world-position behavior, and no new skip/null outcomes, and it must stay build/test green. Steps 4-15 are the behavior change plus matching tests and must ship together so stale radius-threshold tests are updated in the same green commit. Steps 16-18 are documentation updates for the behavior change and must be staged in that same behavior commit per the repo's per-commit documentation rule.

`Orbit` / `CelestialBody` xUnit accessibility via `TestBodyRegistry.Install` plus `TestBodyRegistry.ResolveBodyByName` is the existing pattern used in [`SpawnSafetyNetTests.cs`](../../../Source/Parsek.Tests/SpawnSafetyNetTests.cs:930), [`Bug278FinalizeLimboTests.cs`](../../../Source/Parsek.Tests/Bug278FinalizeLimboTests.cs:722), etc. — verified to work for explicit body resolver and `body.Radius` lookups. `body.GetAltitude` / `GetWorldSurfacePosition` ordering is tested through `TryComputeOrbitWorldPositionCore` delegates, so pure xUnit does not need initialized KSP body transforms for the finite/clamp ordering checks.

## Validation

```bash
cd Source/Parsek.Tests && dotnet test --filter "FullyQualifiedName~PlaybackDistance|PlaybackInterpolation|PendingPlayback|OrbitResolution|AnchorPose"
cd Source/Parsek.Tests && dotnet test    # full suite, no regressions
cd Source/Parsek    && dotnet build      # auto-deploys to KSP
```

Verify deployed DLL per the `.claude/CLAUDE.md` recipe — UTF-16 grep for `orbit-resolver-reject` (the new log tag) on the GameData DLL.

Reload `logs/2026-05-10_2123/saves/s15` in KSP, confirm both ghosts visible at 1× rate during the exo on-rails section.

## Documentation

`CHANGELOG.md` (one line, user-facing):

> Fix: peer ghosts in sub-orbital exo sections no longer disappear during watch mode at 1× rate (the orbital-frame distance check now accepts sub-orbital arcs and uses orbit math instead of falling through to a flat-points interpolation that mis-read anchor-local metre offsets as planet coordinates).

`docs/dev/todo-and-known-bugs.md`: new entry, marked closed on merge.

`AGENTS.md` / `.claude/CLAUDE.md`: update for the new canonical `OrbitResolution` resolver/factory path, `IGhostPositioner.TryPositionFromOrbit`, `IGhostPositioner.TryPositionLoop`, map-presence no-clamp orbit validation, and recorder orbit-gate behavior.

## Checkpoint Strategy

Single PR, two green commits:

1. **Extraction** (mechanical no-op refactor): introduce `OrbitResolution.cs`, convert site 1 and stored-`OrbitSegment` playback/map-presence construction wrappers to the shared factory/resolver shape without changing thresholds, missing-body handling, exception handling, clamp behavior, or fallback outcomes. Reviewer confirms the shape before any rules change.
2. **Fix + tests + docs** (behavior change across resolver/sibling/anchor/map-presence/engine-loop paths plus orbit reapply): apply the new rule, route affected playback orbit-construction paths through `OrbitResolution`, add the shared finite-position/rotation guards, convert sites 2 and 3 to use the validation/logging helper, propagate orbit placement failure through engine/loop/loaded/endpoints, update stale tests, add focused unit/integration coverage, and stage `CHANGELOG.md` / `docs/dev/todo-and-known-bugs.md` plus any required `AGENTS.md` / `.claude/CLAUDE.md` contract updates in the same commit.

A reviewer can reject just the extraction shape (commit 1) and request the fallback `[InternalsVisibleTo]` seam without the behavior commit needing to change.
