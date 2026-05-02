# PR 708 playtest follow-up plan

**Status:** active follow-up work. Baseline before the frozen-alignment fix is committed and pushed as `d329ffac` (`Seed Re-Fly split children from live root`) on `ghost-anchor-recording-chain-v11`.
**Review baseline:** GPT-5.5 xhigh reviewed PR 708 through `abb598b7` (`Freeze Re-Fly display alignment per recording`) and found no P0/P1 blockers. The follow-up fixes keep distance checks projection-only, cover one-frame Absolute anchor sections over their section interval, cache pre-Re-Fly synthetic anchor recordings, clear frozen alignment on marker-scope/body changes, and warn on suspiciously large initial offsets.
**Branch:** `ghost-anchor-recording-chain-v11`.
**Scope:** remaining issues after Phases A-C of `ghost-anchor-recording-chain-plan.md`.
**Non-goals:** no legacy v7-v10 migration, no live-PID fallback for non-loop Relative playback, no Phase D behaviour deletion until the D.0 product gate is explicit.

---

## 1. First step after the next playtest

Collect a fresh bundle immediately after the test session:

```powershell
python scripts/collect-logs.py pr708-ghost-anchor-retest
```

Then inspect only evidence from that bundle before changing code. The first pass should answer these questions:

1. Did KSP load the intended PR 708 DLL?
   - Confirm the bundle git state points at `ghost-anchor-recording-chain-v11` head `20f43baa` or later.
   - Confirm the run contains v11 recording logs and new DAG-fence logs if candidates are skipped.

2. Did same-tree anchor cycles disappear?
   - Grep for `anchor-cycle-detected`.
   - Grep for `recording-anchor-dag-order-skip` and `bg-recording-anchor-dag-order-skip`.
   - Audit fresh `.prec.txt` sidecars for `Relative` sections and their `anchorRecordingId` values.

3. Are debris anchors semantically correct?
   - For each separation, list parent recording id, child/debris recording ids, `TreeOrder`, branch point UT, and each Relative section's `anchorRecordingId`.
   - Debris should prefer the parent or ancestor recording. It should not choose sibling debris merely because that sibling is closer.

4. Are the bad first frames from resolver failure or recorder boundary data?
   - Compare `[PlaybackTrace]` around each structural-event UT with `SeedRelativeBoundaryPoint` / `SeedBackgroundRelativeBoundaryPoint` logs.
   - Flag one-point sections, zero-point sections, and `seed-liveRootDist` spikes.

5. Is active Re-Fly inaccuracy still caused by display translation?
   - Grep for `TryGetReFlyTreeAnchorOffset`, Re-Fly tree offset logs, and Relative playback logs using `source=recorded`.
   - Compare live active vessel position, hidden recorded active pose, and visible sibling ghost pose over the same UT window.

6. Did terminal map/spawn fail again?
   - Grep `GhostMap`, `left-orbit-segments`, `PlaybackCompleted`, `deferred spawn`, `SpawnAtPosition`, `spawn-death`, `pressure`, and `ORBITING`.
   - Record the propagated altitude, body atmosphere depth, pressure if logged, terminal orbit periapsis, and current UT for every spawn attempt.

---

## 2. Fix track A - anchor selection semantics

The current pushed follow-up prevents same-tree cycles by requiring a same-tree anchor candidate to have an older `TreeOrder`. That is necessary but not sufficient. It can still let later debris choose earlier sibling debris.

Target rule:

1. Same-tree ancestor or parent anchors outrank same-tree sibling anchors.
2. Same replay point / same vessel lineage outranks generic nearby candidates.
3. Sealed recordings outrank mutable recordings.
4. Distance breaks ties only after stability and lineage.
5. If the only close candidates are sibling debris and the parent/ancestor is available, choose the parent/ancestor.
6. If no parent/ancestor is resolvable, prefer Absolute over a sibling anchor unless logs prove sibling anchoring is needed for a specific valid case.

Implementation shape:

- Extend `RecordingAnchorCandidate` or its rank key with an `AnchorLineageAffinity` enum:
  - `DirectParent`
  - `Ancestor`
  - `SameReplayPointOrSameVesselLineage`
  - `SameTreeSibling`
  - `OtherSameScope`
- Compute this from `RecordingTree` branch data, `ParentRecordingId` / continuation lineage fields, and `TreeOrder`.
- Apply the same rank in `FlightRecorder` and `BackgroundRecorder`.
- Keep the current DAG-order eligibility as a hard safety fence.
- Log the chosen rank and skipped higher-risk sibling candidates in one batch summary per selection pass.

Tests:

- Parent beats closer sibling.
- Ancestor beats closer sibling.
- Sibling is rejected or loses when parent is available.
- Different-tree candidate remains out of scope for v1.
- Deterministic tie break remains recording id / source / index.

Runtime gate:

- Fresh separation recording sidecars show debris Relative sections anchored to the parent/ancestor, or Absolute if no safe parent exists.
- No `anchor-cycle-detected` during watch playback.

---

## 3. Fix track B - separation boundary seeding and one-frame sections

The prior logs showed two jarring data shapes independent of cycles:

- Fixed narrow case: `logs/2026-05-02_1132_pr708-refly-long-init-behind/` showed a controlled child seed `1118.66m` behind the live root at separation. Controlled child recordings now replace that stale coalescer seed with a one-time live root-part seed when the direct seed-to-root distance or propagated residual exceeds the 50 m gate. This does not add ongoing relative-to-live-vessel playback.
- Remaining generic case: other child/debris boundary seeds can still be wrong if focus and anchor poses are sampled at different UTs.
- A parent absolute section with only one point for about half a second, causing visible freeze while the child moves quickly.

Target rule:

1. Boundary seeding must compare anchor and focus poses at the same UT.
2. Relative section entry must create a usable boundary pair, not a one-point section that freezes or hides.
3. A section with zero points is a bug unless it is an explicitly documented metadata-only seam.
4. If we cannot seed a Relative boundary at the same UT, force Absolute with a clear log reason.

Implementation shape:

- Audit `SeedRelativeBoundaryPoint`, `SeedBackgroundRelativeBoundaryPoint`, and section close/open order around structural events.
- Make the seed helper take an explicit `sampleUT` and resolve both focus and anchor pose at exactly that UT.
- Add a section finalization guard:
  - `Relative` sections with fewer than two usable points either merge into adjacent compatible sections or force a clean Absolute transition.
  - Zero-point sections are dropped with a Warn that includes recording id, section index, previous section, next section, startUT, and endUT.
- Add a `[PlaybackTrace]` diagnostic line or section-close log for short sections under one second.

Tests:

- Seed uses same UT for focus and anchor.
- Failed seed exits Relative to Absolute with the expected log.
- Zero-point section cleanup does not corrupt neighboring sections.
- One-point Relative section either gains a valid boundary point or is fenced to Absolute.

Runtime gate:

- In the separation window, `[PlaybackTrace] dM` stays continuous and no frame shows a kilometer-scale jump.
- Logs do not contain zero-point section warnings except in explicitly accepted seam cases.

Status note:

- Review follow-up: one-frame Relative sections already used section interval coverage; one-frame Absolute anchor sections now use the same interval rule so parent anchors do not disappear at split windows just because the section has one frame.

---

## 4. Fix track C - active Re-Fly ghost alignment

Current evidence from `logs/2026-05-02_1202_pr708-refly-anchor-com-vs-hidden-trajectory/`:

- The `d329ffac` seed replacement worked. The controlled child went from a stale seed `1122.31m` from the live root to a created child seed within `0.01m` of `Decoupler.2`.
- The recorder-side v11 contract is correct. During active Re-Fly, the new Relative recording selected a hidden ghost `anchorRecordingId` with `source=Ghost`, not a live COM / PID anchor.
- The display-side contract is still wrong for the desired behaviour. Playback logs `contract=ghost_world(t)=recorded_relative_offset(t)+live_active_anchor_world(now)`, so same-tree ghosts are translated every frame by current live vessel/root-part motion.

The current architecture has two competing behaviours:

- Phases A-C place non-loop Relative ghosts from recorded anchor chains.
- The old active Re-Fly tree lock still applies a per-frame display translation:

```text
displayOffset(t) = liveActiveAnchorWorld(now) - recordedActiveWorld(playbackUT)
```

The 2026-05-02 narrow follow-up stores the selected slot root-part PID on the Re-Fly marker and resolves `liveActiveAnchorWorld` from that live part when available, falling back to the previous vessel-world position only for legacy or unresolved markers. This fixes the visible COM-initialization offset without changing the recorded-side math or removing the working Re-Fly tree translation.

The next fix is the stabilized comparison overlay, not Phase D deletion. The rule is:

1. Recording data remains anchored to hidden ghost trajectories through `anchorRecordingId`.
2. The live vessel/root part is used only to initialize a display alignment for a given recording in a given Re-Fly session.
3. Once initialized, that recording's same-tree ghost display must not resample the live vessel; user input on the real vessel must not inject up/down translation into the ghost.

Implementation target:

- Replace the per-frame `(Time.frameCount, recordingId, currentUT)` memo in `TryGetReFlyTreeAnchorOffset` with a per-session, per-recording frozen display alignment.
- Capture alignment at the first frame the recording is renderable in the active Re-Fly session, not at Re-Fly invocation time. For a tree member that appears later at separation, the capture point is that first renderable frame.
- Compute the initial delta from the selected live root part when available, otherwise from the existing vessel-world fallback with an explicit log reason.
- Store the frozen alignment as a body-fixed Cartesian metre offset:

```text
bodyFixedOffset = inverse(body.bodyTransform.rotation) * (liveAnchorWorld0 - recordedAnchorWorld0)
worldOffset(t) = body.bodyTransform.rotation * bodyFixedOffset
```

Do not store raw world XYZ and do not use simple latitude / longitude / altitude subtraction; both drift or distort under body rotation.

- If a tree member never reaches a renderable capture point, prefer no offset over falling back to the old live-bobbing path. Log the skip.
- Clear frozen alignments on Re-Fly session change/end, retry/revert/discard, load-time marker validation failure, and scene/session cleanup. The cache key must include `SessionId` so a second Re-Fly attempt cannot inherit the first attempt's offsets.
- Remove the old per-frame live-anchor display calculation from the normal path. The default display path should log `contract=ghost_world(t)=recorded_world(t)+frozen_body_fixed_refly_offset`.

Tests:

- Pure math tests for the body-fixed alignment:
  - initial world delta is recovered at the same body rotation,
  - body rotation reprojects the stored offset instead of preserving stale world XYZ,
  - different session ids do not share cached offsets,
  - missing / non-finite recorded or live pose fails closed with a loggable reason.
- Playback tests for the Re-Fly offset helper:
  - the first successful call freezes the offset,
  - later live-anchor movement does not change the returned offset,
  - a later recording captures independently at its own first renderable UT,
  - session change invalidates the frozen value.
- Playback contract test that Relative resolver output is unchanged by the display alignment layer.

Runtime gate:

- Re-Fly a separated vessel while the sibling ghost is visible.
- The sibling ghost initializes at the separation point, then follows the hidden recorded trajectory plus the frozen body-fixed alignment.
- Moving the real vessel after initialization should not move the sibling ghost up/down.
- Logs must include `Re-Fly display alignment initialized` and `mode=frozen-body-fixed`; the old `per-frame` contract must not appear for the normal path.

Status note:

- Implemented through `abb598b7`, with review follow-up tightening: distance / LOD checks only project an already captured frozen alignment and cannot create one before the ghost is renderable. Frozen alignments are invalidated if the active live Re-Fly vessel changes SOI/body or if the marker scope changes despite the same session id; capture logs now warn when the initial offset exceeds `50m`.
- Follow-up from `logs/2026-05-02_1320_pr708-refly-optimizer-boundary-bad-init/`: optimizer-created chain successors inherit the nearest cached predecessor's frozen display alignment instead of capturing a second offset at the atmo/exo split. This keeps Re-Fly display alignment continuous across chain segments while still using the hidden recorded trajectory as the source of truth. Review follow-up: when a pending Re-Fly tree and stale committed tree share the same tree id, the pending topology wins for inherited alignment.
- Diagnostic follow-up: each display-alignment capture now logs `liveWorld`, `recordedWorld`, `live-recorded`, sampled section/frame timing, live and recorded velocities, `deltaAlongRecordedVelocityMeters`, and `impliedRecordedTimeOffsetSeconds`; cache hits also emit a rate-limited projected-offset line. Use these fields in the next log bundle to tell whether the remaining init error is capture timing, root-part fallback, recorded hidden-path drift, or repeated cache invalidation.
- Recorded-path de-bob rollback: the frozen offset removed later live-vessel motion, but the first local recorded-path de-bob experiment did not safely smooth bobbing already present in the hidden recorded active trajectory. `logs/2026-05-02_1546_pr708-refly-smaller-bob-40m-init/` showed the de-bob correction toggling between zero, 6-7m offsets, and kilometre-scale rejected candidates near optimizer boundaries. Runtime Re-Fly display alignment is back to `recorded_world(t)+frozen_body_fixed_refly_offset`; de-bob fields remain in the capture/projection logs as `runtime-disabled` diagnostics only.
- Final render-frame follow-up: `logs/2026-05-02_1605_pr708-refly-same-bugs-recurred/` showed the frozen display capture itself was small (`initialWorldOffsetMeters=4.56`) and stable, while `[PlaybackTrace]` for the same ghost alternated normal 2.1km/s recorded motion with 390-480m reset frames. Flight-view ghosts are now reapplied a second time in the camera pre-cull pass, after KSP LateUpdate/origin work but before the flight camera renders, and `Ghost camera pre-cull reapply` logs the final correction delta so the next run can prove whether the violent translation was a final-render frame-order issue.
- Observability follow-up: `[GhostRenderTrace]` now logs a correlated render transaction across guard skips, first render, Update positioning, LateUpdate reapply, camera pre-cull reapply, terrain clamp, Re-Fly alignment, and relative-anchor resolution. The trace is gated around first appearance, structural-event windows, Re-Fly alignment, section changes, large pose deltas, resolver misses, and terrain clamps so the next playtest can isolate whether a jump is introduced by recorded sampling, offset projection, FloatingOrigin timing, final render reapply, or a hide/retire guard.
- Render-contract follow-up from `logs/2026-05-02_1701_pr708-ghost-render-trace-check/`: `GhostRenderTrace` proved the upper-stage first render was near the selected root before a 4.4 km `anchorEps` was added on top of the frozen Re-Fly offset, and the optimizer boundary jumped when that epsilon disappeared on the orbit/checkpoint side. The render path now resolves the frozen Re-Fly display offset before anchor correction and suppresses anchor epsilon with `anchorCorrectionReason=refly-display-offset-active`; endpoint hold also routes checkpoint-backed orbit sections through orbit placement before the stale `PositionAtPoint` fallback.
- Follow-up from `logs/2026-05-02_1802_pr708-bobbing-exo-forward-back-regression/`: init was correct (`initialWorldOffsetMeters=4.63`, first render around `4m` from the live root), but the exo successor was worse only when `splineHit=true`, while the raw Hermite comparison stayed near `29m`. Active Re-Fly display-aligned PointInterp now suppresses smoothing splines in both Update and final reapply passes, leaving guarded Hermite/lerp as the render source. The same log showed an optimizer-created orbit-only `OrbitalCheckpoint` section falling through to stale point interpolation; checkpoint sections with only `ORBIT_SEGMENT` payload now render directly from the checkpoint orbit.
- Narrow init/smoothing follow-up: the render path now folds one first-render ghost-part pin into the frozen display alignment. If the visible ghost has the marker's selected root-part PID, that ghost part is aligned to the live selected root part once, cached in body-fixed coordinates, and never recomputed from the live vessel on later frames. Sparse PointInterp brackets also use a guarded cubic Hermite fallback from recorded endpoint velocities when the velocity chord and deviation checks pass; logs expose `reFlyGhostPartPinHit`, `Re-Fly ghost part pin applied`, `interpMode`, `hermiteReason`, and `hermiteDeviationMeters`.
- Deferred de-bob replacement: do not re-enable the local three-sample straight-line correction without a shape-safe recorded-trajectory smoothing design. The replacement needs boundary-aware sampling, monotonic/section-local constraints, and final-render diagnostics that compare the displayed ghost after LateUpdate against the selected live root part and the hidden recorded anchor path.
- Smoothing follow-up: the Absolute Atmospheric spline experiment was rolled back. The 2026-05-02 15:27 playtest still produced repeated 100-400m `PlaybackTrace` jumps inside a sparse 5-sample Atmospheric ascent section, so `SmoothingPipeline.ShouldFitSection` now excludes Atmospheric again and `PannotationsSidecarBinary.AlgorithmStampVersion` is bumped to v12 to discard unsafe v11 `.pann` caches on first load.
- Follow-up from `logs/2026-05-02_1457_pr708-refly-bad-init-unstable-after-redeploy/`: time-aware Catmull-Rom tangents remain in place for eligible ExoPropulsive/ExoBallistic splines, but that fix was insufficient for sparse Atmospheric sections. Regression coverage now pins both the tangent math (`CatmullRomFitTests.Evaluate_Pr708AtmosphericShortSegment_StaysWithinAdjacentControls`) and the rollback gate (`SmoothingPipelineTests.FitAndStorePerSection_Atmospheric_NotFitted`).
- Deferred product-behaviour note: the shared-capture-UT idea from the parallel discussion remains open. It can keep all same-tree ghosts on one display offset, but it changes the desirable "first visible split ghost initializes at the live separation point" behaviour. Current implementation keeps per-recording capture and attacks hidden-path noise instead.

---

## 4.1 Fix track C follow-up - Relative anchor continuity at optimizer splits

The same `logs/2026-05-02_1320_pr708-refly-optimizer-boundary-bad-init/` run showed a resolver miss at the split boundary:

```text
anchor-out-of-recorded-range for the original anchor recording after its optimizer-created segment ended
```

Target rule:

1. A Relative section anchored to a recording-chain member may continue through that anchor's same-tree, same-branch chain successor when the original anchor segment no longer covers the requested UT.
2. The continuation must stay inside the same `(TreeId, ChainId, ChainBranch)` scope.
3. The continuation must not revive live-PID anchoring, legacy migration, or cross-tree fallback.
4. If no same-chain successor covers the UT, the resolver still fails closed with the existing logged miss.

Implementation target:

- In `RelativeAnchorResolver`, when the anchor recording has track sections but no section covers the requested UT, search the focus tree, in-scope pending tree, and scoped provisional map for the nearest higher `ChainIndex` with the same `ChainId` and `ChainBranch` whose own `TrackSections` cover the UT. If committed and pending candidates tie on `ChainIndex`, prefer the pending candidate so the active Re-Fly topology overlays stale committed data during the load/resume window.
- Resolve through that successor using the normal resolver recursion so Absolute, Relative, and OrbitalCheckpoint sections keep their existing contracts.
- Keep cycle detection active and log `Anchor recording continued through same-chain successor` when the handoff happens.

Tests:

- Relative child anchored to the first optimizer half resolves at a UT covered only by the second half.
- The miss log `anchor-out-of-recorded-range` is absent for that same-chain handoff.
- Pending successor topology overrides stale committed topology with the same tree id and `ChainIndex`.
- Different branches are not inherited by the Re-Fly display alignment cache.

---

## 5. Fix track D - terminal map/end-spawn safety

The prior logs showed map presence ending, then real-vessel spawn attempts that KSP destroyed on rails because propagated terminal orbit positions were inside unsafe atmosphere.

Target rule:

1. Never spawn an ORBITING/on-rails real vessel at an unsafe atmospheric altitude.
2. Do not enter a repeated spawn-death retry loop.
3. Do not remove the map/held representation before real spawn succeeds.
4. If a terminal orbit cannot be safely materialized at the current UT, defer with a clear reason.

Implementation shape:

- Add a pure spawn-safety decision helper:

```csharp
TerminalSpawnSafetyDecision DecideTerminalOrbitSpawnSafety(
    CelestialBodyInfo body,
    OrbitSnapshot terminalOrbit,
    double currentUT,
    double propagatedAltitude,
    double periapsisAltitude);
```

- Decisions:
  - `SpawnNow`
  - `DeferUntilSafe`
  - `CannotSpawnSafely`
- Inputs must be easy to unit-test without live KSP.
- Runtime path may add pressure and situation details to logs.
- If current propagated altitude is below `body.atmosphereDepth + safetyMargin`, defer instead of spawning.
- If periapsis is inside atmosphere, either find a safe future true-anomaly/UT or keep the map representation and log `CannotSpawnSafely`.
- Keep or restore `GhostMapPresence` while spawn is pending/deferred.

Tests:

- Safe propagated orbit spawns normally.
- Unsafe atmospheric propagated orbit defers before any real spawn call.
- Repeated unsafe updates do not spawn repeatedly.
- Map presence is retained while terminal spawn is pending.
- Unsafe periapsis produces a clear cannot/defer decision.

Runtime gate:

- A terminal orbiting recording remains visible on the map until real spawn succeeds.
- KSP.log has no immediate on-rails destruction after spawn.
- Logs include terminal spawn safety decision, altitude, atmosphere depth, periapsis, UT, and recording id.

---

## 6. Work order after new logs

Do not start by landing Phase D. Use the logs to pick the smallest next fix.

1. If `anchor-cycle-detected` still appears:
   - Fix track A first.
   - Do not chase Re-Fly alignment until cycles are gone.

2. If cycles are gone but first frames jump near separation:
   - Fix track B first.
   - This is recorder data shape, not playback resolver policy.

3. If watch playback is stable but active Re-Fly remains inaccurate:
   - Decide track C product behaviour.
   - Either land Phase D recorded-coordinate mode, or implement the stabilized display offset.

4. If map/end spawn fails again:
   - Fix track D independently.
   - It does not depend on the Relative resolver work.

5. If all four issues appear:
   - Land A and B together only if tests are tightly scoped and the diff stays small.
   - Land D separately.
   - Keep C separate because it is a product-behaviour choice.

---

## 7. Validation checklist

Headless:

```powershell
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj --filter "FullyQualifiedName~AnchorDetectorTests|FullyQualifiedName~RelativeAnchorResolverTests"
dotnet test Source/Parsek.Tests/Parsek.Tests.csproj
```

Runtime:

- New recording, fresh KSP restart after build.
- Watch separation playback with `[PlaybackTrace]`.
- Re-Fly lower stage with upper-stage/debris ghost visible.
- Re-Fly upper stage with lower-stage/debris ghost visible.
- Map/KSC transition at terminal orbit playback completion.
- Tracking Station visibility before and after real spawn handoff.

Log gates:

- No non-loop live-PID Relative fallback.
- No `anchor-cycle-detected` for fresh v11 recordings.
- No missing `anchorRecordingId` on new v11 Relative sections.
- No zero-point section unless explicitly logged as a dropped seam.
- No on-rails atmospheric spawn death loop.
