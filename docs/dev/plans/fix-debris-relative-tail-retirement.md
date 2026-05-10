# Fix Plan: Parent-Anchored Debris Relative Tail Retirement

Date: 2026-05-10

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-fix-debris-relative-tail-retirement`

Branch: `fix/debris-relative-tail-retirement`

Base: `77e41a3b` (`origin/main` after PR #806 follow-ups)

Evidence bundle:
`C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-10_1713`

Line numbers in this document are evidence anchors from the investigation pass.
Re-grep the symbols during implementation; do not depend on exact numbers
staying stable.

## Problem Statement

During ghost playback, four v12 debris recordings log thousands of resolver
misses:

`[Parsek][WARN][RelativeAnchorResolver] relative-anchor-unresolved: reason=anchor-out-of-recorded-range`

The affected debris recordings are:

- `a14b621b801844c0b7fe5771067d14a7`
- `e2b6aa4d05cc4c9e8c3a851de7e499bc`
- `e71f03224c614ce1ad76b2db3138d474`
- `9e7b0fafbe6d41b9aba9fa35be1c4683`

All four are v12 debris: the retained save has `recordingFormatVersion = 12`,
`isDebris = True`, and
`debrisParentRecordingId = 1354326de29a4af2891c52f151995c79`. They all anchor
their Relative section to the same parent recording, `1354326d...`
(`Kerbal X Probe`).

The parent recording is not actually out of recorded range. Its TrackSections
cover the warning UTs:

| parent section | startUT | endUT |
| ---: | ---: | ---: |
| 0 | 301.9000 | 317.4519 |
| 1 | 317.4519 | 333.0519 |
| 2 | 333.0519 | 351.2519 |
| 3 | 351.2519 | 456.0345 |
| 4 | 456.0345 | 456.5545 |
| 5 | 456.5545 | 466.2345 |

The `sectionIndex=0` in the warning is the child debris section index, not the
parent section index. The parent anchor pose resolves before and after the WARN
lines in `GhostRenderTrace`.

The actual defect is that each debris section's metadata says the section still
covers playback, but the section's authored frame lists have already ended:

| debris recording | section startUT | section endUT | first relative point | last relative point |
| --- | ---: | ---: | ---: | ---: |
| `a14b621b...` | 333.0519 | 376.2919 | 333.0519 | 344.8919 |
| `e2b6aa4d...` | 333.0519 | 376.3319 | 333.0519 | 344.8919 |
| `e71f0322...` | 351.2519 | 389.5319 | 351.2519 | 361.1719 |
| `9e7b0faf...` | 351.2519 | 389.5319 | 351.2519 | 361.1719 |

Playback UTs such as `344.9077`, `369.6305`, and `386.2305` are inside the
debris section's `startUT..endUT`, so the engine treats the section as live.
They are outside the child section's actual authored `frames` and
`absoluteFrames`, so the only remaining behavior is stale endpoint clamping or
resolver failure.

## User-Visible Effect

This is an observable ghost-rendering bug when the debris is visible:

- the debris ghost keeps rendering after its own recorded relative samples end;
- the last child local offset is frozen;
- the still-valid parent anchor keeps moving;
- the debris therefore drifts or jumps along a wrong trajectory;
- terminal explosion / cleanup can be emitted from that wrong stale position;
- logs are inflated by rate-limited per-frame retries.

It does not affect live KSP vessel physics, resource ledger state, crew state, or
save-state mutation. It affects playback, watch mode, Re-Fly visualization, and
diagnostic log signal.

## Root Cause

There are two separate but compounding problems.

### 1. Playback Coverage Predicate Is Too Coarse

`GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage`
currently only asks whether a Relative `TrackSection` covers the playback UT:

- `Source/Parsek/GhostPlaybackEngine.cs:2674-2689`

That misses the retained-data shape where the section range covers
`playbackUT`, but the section's actual authored relative frames and absolute
shadow frames do not.

Current render flow then proceeds:

1. `TryGetRelativeSectionAtUT` finds the debris section by section metadata.
2. `TryRouteAnchorRotationUnreliable` tries the v12 always-shadow path.
3. `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow` correctly rejects
   shadow playback outside actual `absoluteFrames` endpoint coverage
   (`ParsekFlight.cs:16740-16756`).
4. The engine falls back to the legacy recorded-relative path.
5. `ParsekFlight.InterpolateAndPositionRecordedRelative` calls
   `TrajectoryMath.FindWaypointIndex`, which deliberately clamps
   `targetUT >= lastPoint.ut` to the last segment
   (`TrajectoryMath.cs:521-522`).
6. The child offset remains the last authored value while the parent anchor
   continues moving.

This is the source of the large-delta `AfterUpdate` traces in the log.

### 2. A Resolver Call Site Still Sees the Bad Section

`RelativeAnchorResolver.TryResolveRelativeSectionPose` is stricter than the
main playback path: it calls `PointListCoversUT` and rejects any UT beyond the
child section's actual frame endpoints:

- `Source/Parsek/RelativeAnchorResolver.cs:2468`
- `Source/Parsek/RelativeAnchorResolver.cs:2527`
- `Source/Parsek/RelativeAnchorResolver.cs:2586-2597`

That behavior is correct. The WARN is not proving that the parent anchor is
gone; it is exposing that a caller is asking the resolver to compute a pose for
a child Relative section after the child's authored frames ended.

The fix should not make `RelativeAnchorResolver` accept section metadata as
coverage for multi-frame Relative sections. That would bless stale data. The fix
belongs at the parent-anchored debris coverage gate and at the recorder/finalizer
that wrote the over-long section span.

## Related Items

This is not the closed boundary-epsilon issue at
`docs/dev/todo-and-known-bugs.md:220+`. That item handled UTs at
`section.endUT + 1e-13..3e-12`; the failing UTs here are many seconds past the
child frame endpoints.

It overlaps the broader open debris trajectory issue at
`docs/dev/todo-and-known-bugs.md:328+`, but it is a narrower v12 edge case:
parent anchor coverage is valid, while child authored-frame coverage is not.

## Target Behavior

For v12+ parent-anchored debris (`IsDebris && DebrisParentRecordingId != null`):

1. Section metadata alone is not enough to keep a debris ghost alive.
2. The stale-tail retirement rule applies only to recorded-parent debris. Live
   loop-anchor debris (`LoopAnchorVesselId != 0`) must keep the existing
   live-anchor behavior and must not be retired by authored-frame coverage.
3. Playback is valid only when at least one recorded rendering surface covers the
   playback UT:
   - the Relative section's authored `frames`; or
   - the Relative section's `absoluteFrames` shadow.
4. Relative-frame coverage follows the resolver contract:
   - zero frames: not covered;
   - one Relative frame: covered across the section span when
     `SingleFrameCoversUT` would accept it;
   - multiple Relative frames: covered only from first frame UT through last
     frame UT.
5. Absolute-shadow coverage follows `TryPositionFromRelativeAbsoluteShadow`:
   no invented section-wide coverage for missing or non-interpolable shadow
   samples.
6. If neither frame list covers the playback UT, retire/hide the debris before
   invoking resolver, legacy relative reconstruction, point fallback, surface
   fallback, orbit fallback, zone activation, watch activation, or terminal FX.
7. Do not fall back to the last Relative sample for v12 parent-anchored debris.
8. Preserve legacy v11 debris behavior (`DebrisParentRecordingId == null`).
9. Preserve non-debris Relative behavior.
10. Preserve current v12 always-shadow behavior when `absoluteFrames` actually
   covers the playback UT.
11. Preserve relative reconstruction when `frames` covers the playback UT but
   `absoluteFrames` is absent or narrower.

## Proposed Implementation

### Phase 1: Add an Authored-Frame Coverage Predicate

Add a pure helper, preferably in `DebrisRelativePlaybackPolicy` so both engine
and positioner can share it:

```csharp
internal static bool ShouldRetireOutsideAuthoredRelativeCoverage(
    IPlaybackTrajectory traj,
    double playbackUT,
    out ParentAnchoredDebrisCoverageDiagnostic diagnostic)
```

The helper should:

- return false for null trajectories, non-debris, live-anchor loop debris
  (`LoopAnchorVesselId != 0`), and legacy debris with
  `DebrisParentRecordingId == null`;
- return true for v12 debris with an empty parent recording id, matching the
  existing fail-closed behavior;
- find the active TrackSection with `TrajectoryMath.FindTrackSectionForUT`;
- return true when no section covers the UT;
- return true when the covering section is not `ReferenceFrame.Relative`;
- resolve the actual Relative point list:
  - prefer `section.frames`;
  - if absent, use a section-bounded projection from `traj.Points`, not the
    whole flat list unfiltered;
- compute Relative coverage with resolver-compatible semantics:
  - zero frames fail closed;
  - one frame uses `SingleFrameCoversUT` semantics against
    `section.startUT/endUT`;
  - multiple frames use actual first/last frame endpoint UTs, not section
    `startUT/endUT`;
- compute shadow coverage with the same rules as
  `TryPositionFromRelativeAbsoluteShadow` so the predicate and actual renderer
  agree;
- fail closed when both `section.frames` and `section.absoluteFrames` are empty
  and no section-bounded `traj.Points` projection covers the UT;
- return false if either Relative frames or absolute shadow frames cover the UT;
- return true if neither covers.

Use the same epsilon already used for shadow endpoint checks
(`1e-6` is enough for this issue). Do not use a multi-second tolerance: that
would hide the exact bug.

Diagnostic fields should include:

- `sectionIndex`
- `sectionStartUT`
- `sectionEndUT`
- first/last Relative frame UT
- first/last absolute shadow frame UT
- `anchorRecordingId`
- decision reason, e.g. `no-relative-section`, `non-relative-section`,
  `relative-and-shadow-frames-out-of-range`, `covered-by-relative-frames`,
  `covered-by-absolute-shadow`

The helper must be side-effect free: no logging, no ghost mutation, and no
resolver calls. That lets the engine, positioner, co-bubble path, and tests ask
the same coverage question before they reach stale endpoint clamping or
`RelativeAnchorResolver`.

### Phase 2: Replace the Engine-Level Section-Only Gate

Change `GhostPlaybackEngine.ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage`
to delegate to the new authored-frame helper.

This preserves the existing call sites while tightening the definition of
coverage:

- non-loop render path before normal position fallback;
- loop positioning via `PositionLoopAtPlaybackUT`;
- overlap primary and overlap loop paths;
- endpoint coverage checks;
- direct watch-load and watch-sync coverage gates.

Before coding, verify each loop/overlap caller passes the section-mapped
`playbackUT`, not an unwrapped wall-clock loop UT. The current call sites to
check are:

| caller line on `138cc106` | path | expected UT |
| ---: | --- | --- |
| `1718` | loop/watch hidden-state handling | selected loop playback UT |
| `1988` | overlap primary spawn | overlap primary playback UT |
| `2022` | overlap loop spawn | overlap loop playback UT |
| `2249` | overlap endpoint/expiry handling | selected endpoint playback UT |
| `4096` | loop visual update | mapped loop playback UT |

If any caller passes wall-clock UT, map it before calling the authored-frame
predicate; otherwise every looped debris recording whose original section does
not cover the wall-clock UT would retire incorrectly.

The existing retirement machinery should remain the single output surface:

- `TryRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage`
- `TryHandleParentAnchoredDebrisCoverageRetired`
- `MarkParentAnchoredDebrisCoverageRetired`

Keep the existing top-level reason code
`parent-anchored-debris-outside-relative-coverage` for log-contract
compatibility. Add an authored-frame detail field such as
`coverageReason=relative-and-shadow-frames-out-of-range` plus diagnostic frame
ranges. Do not create a second retirement state unless tests prove the current
`parentAnchoredDebrisCoverageRetired` latch cannot distinguish the cases safely.

Keep the existing rate-limit key prefix
`parent-anchored-debris-outside-relative-coverage|...` unless the reason code is
renamed. If a rename becomes necessary, update both
`BuildParentAnchoredDebrisCoverageRetiredKey` and all log assertions/contracts
in `Source/Parsek.Tests/`, `scripts/validate-ksp-log.ps1`, and in-game
`LogContractTests` in the same commit.

Expected warning shape:

```text
recorded-relative-retired:
reason=parent-anchored-debris-outside-relative-coverage
coverageReason=relative-and-shadow-frames-out-of-range
recordingId=...
sectionIndex=0
playbackUT=369.63053222651592
sectionUT=[333.05190452576693,376.29190452578905]
relativeFrames=[333.05190452576693,344.891904525773]
absoluteFrames=[333.61190452576722,344.891904525773]
anchorRec=1354326de29a4af2891c52f151995c79
```

### Phase 3: Add a Positioner-Side Fail-Closed Guard

Even with the engine gate, add a local guard in
`ParsekFlight.InterpolateAndPositionRecordedRelative` before
`TrajectoryMath.FindWaypointIndex` is allowed to clamp past the final child
frame.

Rationale: `InterpolateAndPositionRecordedRelative` is the dangerous function
because it can turn `targetUT > frames[last].ut` into `t=1.0` and a stale local
offset. The function should fail closed for parent-anchored v12 debris if the
selected rendering surface is not covered.

Design:

- If
  `DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage(traj, targetUT, out diagnostic)`
  returns true, call `RetireUnresolvedRecordedRelative` before computing
  `FindWaypointIndex`.
- Use a distinct local failure reason such as `relative-frames-out-of-range`.
  This reason describes the child section's authored frames, not a parent-anchor
  resolver miss, and must not collide with existing
  `anchor-out-of-recorded-range` or `parent-anchor-miss` reasons.
- Do not attempt `TryUseRelativeAbsoluteShadowFallback` from this branch for v12
  parent-anchored debris. The engine-level always-shadow route is the only v12
  shadow route and it already requires actual shadow coverage.
- Leave legacy v11 debris and non-debris behavior unchanged.

This is an additional fail-closed layer ahead of `FindWaypointIndex`. The
existing post-failure retirement path in
`TryRetireParentAnchoredDebrisOnRecordedAnchorMiss` remains the catch-all for
true recorded-anchor misses inside `TryUseRelativeAbsoluteShadowFallback`; it is
not a replacement for this pre-clamp child-frame coverage check.

### Phase 4: Stop Calling the Resolver for Retired Debris

Use the same side-effect-free helper from Phase 1 before resolver-producing
paths that can ask for the child debris pose after authored coverage ended.

Bounded audit for PR 1:

| call site | resolver path today | planned behavior |
| --- | --- | --- |
| `ParsekFlight.TryComputeStandaloneRelativeWorldPosition` | calls `RelativeAnchorResolver.TryResolveRecordingPose` directly for the child recording | check authored coverage first; if retired, return false without trying resolver or shadow fallback because the helper already proved neither Relative frames nor shadow frames cover the UT |
| `ParsekFlight.TryResolveRelativeOffsetWorldPosition` | calls `TryResolveRecordedRelativeAnchorPose` after caller has already selected/interpolated a relative offset | caller must pass enough child-section context to check authored coverage before interpolation; stale-tail v12 debris returns false without resolver WARN |
| `ParsekFlight.InterpolateAndPositionRecordedRelative` | reaches `FindWaypointIndex`, then `TryResolveRecordedRelativeAnchorPose`, then `TryUseRelativeAbsoluteShadowFallback` | Phase 3 guard fires before clamping and before resolver/fallback |
| `ParsekFlight.ApplyGhostPosEntries` for `GhostPosMode.Relative` | may replay stored relative snapshots through the recorded-anchor pose helper | check the associated `relativeRecordingId`/section UT against authored coverage before resolving the anchor |
| co-bubble LateUpdate standalone lookups | routes through `TryComputeStandaloneWorldPositionForRecording` then `TryComputeStandaloneRelativeWorldPosition` | inherited from standalone guard; stale-tail v12 debris is treated as unavailable, not unresolved |
| active Re-Fly anchor-candidate scanning | can probe recorded relative candidates while comparing anchors | filter parent-anchored debris candidates whose authored coverage does not cover the probe UT |

For v12 parent-anchored debris outside authored coverage, those paths should
short-circuit with a quiet, structured miss before calling
`RelativeAnchorResolver.TryResolveRecordingPose` or
`TryResolveRelativeSectionPose`.

The goal is not to hide important diagnostics. The new retirement WARN should be
the diagnostic. Repeated `RelativeAnchorResolver` WARNs for the same stale child
section should disappear. The regression test should assert zero
`[RelativeAnchorResolver] relative-anchor-unresolved` lines over the stale-tail
fixture after the new retirement log fires.

### Phase 5: Fix the Recorder / Finalizer Invariant

Playback must be robust against old data, but new recordings should not write
v12 parent-anchored debris Relative sections whose metadata outlives every
recorded rendering surface.

Scope this invariant narrowly. Do not change non-debris, legacy debris,
Absolute sections, or valid endpoint-hold behavior elsewhere in playback.

Required invariant for saved v12 parent-anchored debris Relative sections:

```text
section.referenceFrame == Relative
recording.IsDebris == true
recording.DebrisParentRecordingId is non-empty

For every UT in [section.startUT, section.endUT],
at least one rendering surface is valid:
  - section.frames covers UT using resolver-compatible Relative coverage; or
  - section.absoluteFrames covers UT using shadow-renderer coverage.

If neither surface covers the section end, clamp section.endUT to the last UT
covered by either surface, or append a trustworthy terminal sample for the
surface being extended.
```

Single-frame Relative sections keep the resolver's `SingleFrameCoversUT`
contract. Single-frame absolute shadows do not gain new section-wide coverage
unless `TryPositionFromRelativeAbsoluteShadow` is explicitly changed and tested.

Fix direction:

1. Before refactoring, pin the culprit path. Write a small script or focused
   test that loads the four affected `.prec.txt` sidecars, walks each Relative
   TrackSection, and reports:
   - `section.startUT`
   - `section.endUT`
   - first/last `section.frames` UT
   - first/last `section.absoluteFrames` UT
   - whether the last sample came from an ordinary sample, structural seed,
     terminal event, on-rails flush, or background finalizer path when that
     source marker is available
2. Locate the closure path that sets the debris section `endUT` to terminal UT
   without appending a matching relative/shadow sample. Likely targets:
   - `BackgroundRecorder.EndDebrisRecording`
   - `BackgroundRecorder.FlushLoadedStateForOnRailsTransition`
   - `BackgroundRecorder.AppendStructuralEventSnapshot`
   - `BackgroundRecorder.ApplyBackgroundRelativeOffset`
   - the helper that persists finalized background TrackSections
3. If a trustworthy terminal pose is available at close time, append a final
   Relative sample and absolute shadow sample at that terminal UT before setting
   `section.endUT`.
4. If no trustworthy terminal pose is available, clamp `section.endUT` to the
   last authored sample and keep the recording's terminal/end-state metadata
   separate. The ghost should disappear at the last reliable point rather than
   keep moving with a frozen child offset.
5. Add a save-time validation warning for any v12 parent-anchored debris section
   where `section.endUT > max(lastRelativeFrameUT, lastAbsoluteShadowFrameUT) +
   epsilon` and neither surface's coverage contract accepts `section.endUT`.
   This should be WARN during development and can become VERBOSE once the
   invariant is proven stable.

Do not "fix" this by extending the child section with duplicated endpoint
samples. A duplicated local offset at a later UT recreates the visual bug while
making it look intentional.

## Test Plan

### Unit Tests

Add tests near the existing coverage tests in
`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`:

1. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_InsideSectionAfterLastRelativeAndShadowFrame_ReturnsTrue`
   - v12 debris
   - section `[333.05,376.29]`
   - `frames` and `absoluteFrames` ending at `344.89`
   - query `344.9077`
2. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_InsideSectionRelativeFramesCover_ReturnsFalse`
3. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_InsideSectionShadowFramesCover_ReturnsFalse`
4. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_InsideSectionNoFrames_ReturnsTrue`
5. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_LegacyDebrisStillFalse`
6. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_EmptyParentIdFailsClosed`
7. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_LiveAnchorLoopDebris_ReturnsFalse`
   - set `LoopAnchorVesselId != 0`
   - stale authored frames should not retire live-anchor debris
8. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_SingleRelativeFrameWithinSection_ReturnsFalse`
   - matches resolver `SingleFrameCoversUT`
9. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_SingleAbsoluteShadowFrame_ReturnsExpectedShadowContract`
   - pins current `TryPositionFromRelativeAbsoluteShadow` behavior
10. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_EmptyFramesButSectionProjectedFlatPointsCover_ReturnsFalse`
11. `ShouldRetireParentAnchoredDebrisOutsideRecordedRelativeCoverage_EmptyFramesAndNoProjectedPoints_ReturnsTrue`

Add positioner-level tests where the existing test seams allow it:

12. `InterpolateAndPositionRecordedRelative_ParentDebrisAfterLastFrame_RetiresInsteadOfClamping`
13. `InterpolateAndPositionRecordedRelative_ParentDebrisAfterLastFrame_DoesNotUseAbsoluteShadowFallback`
14. `InterpolateAndPositionRecordedRelative_ParentDebrisAfterLastFrame_UsesRelativeFramesWhenCovered`

Add resolver/logging tests:

15. Capture `ParsekLog.TestSinkForTesting`; run the stale-tail case through the
   engine path; assert one `recorded-relative-retired` warning and zero
   `[RelativeAnchorResolver] relative-anchor-unresolved` warnings.
16. Assert the retirement warning keeps
   `reason=parent-anchored-debris-outside-relative-coverage`, includes
   `coverageReason=relative-and-shadow-frames-out-of-range`, and uses the
   expected rate-limit key prefix.
17. Exercise the standalone/co-bubble path and assert it returns unavailable
   without emitting `RelativeAnchorResolver` warnings.
18. Exercise loop and overlap paths with section-mapped playback UTs; assert
   stale-tail debris retires, while the same recording does not retire if the UT
   is only outside wall-clock loop time but inside mapped section time.
19. Terminal/overlap side effects:
   - stale-tail destroyed debris must not fire terminal FX, camera events, or
     explosion cleanup from a clamped stale pose;
   - a shadow-covered endpoint still follows the normal terminal behavior.

Add codec/recorder invariant tests:

20. Text sidecar round-trip preserves clamped section end when no terminal sample
    is present.
21. A synthetic finalized debris section with terminal relative and shadow
    samples may keep `section.endUT == terminalUT`.
22. A finalized debris section without terminal samples clamps `section.endUT`
    to the last authored point.
23. PR 2 prep script/test loads the four affected retained sidecars and reports
    every section where metadata coverage outlives both authored rendering
    surfaces.

### Retained Log Reproduction

Use the retained sidecars from:

`C:\Users\vlad3\Documents\Code\Parsek\logs\2026-05-10_1713\parsek\Recordings`

Create a small test fixture or script-local test case that loads the four
affected `.prec.txt` sidecars, materializes their TrackSections, and asserts:

- old predicate: section-level coverage returns live;
- new predicate: authored-frame coverage returns retired;
- parent `1354326d...` coverage remains valid at the same UTs.

This pins the exact regression:

- `a14/e2b6` at `ut=344.90773193354192`
- `a14/e2b6` at `ut=369.63053222651592`
- `e71/9e7` at `ut=361.230532226514`
- `e71/9e7` at `ut=386.2305322265197`

### In-Game Validation

After unit tests pass:

1. Build and deploy:
   `dotnet build Source/Parsek/Parsek.csproj`
2. Run headless tests:
   `cd Source/Parsek.Tests && dotnet test`
3. Replay the retained scenario or nearest available reproduction.
4. Search `KSP.log` for:
   - `relative-anchor-unresolved`
   - `parent-anchored-debris-outside-relative-coverage`
   - `coverageReason=relative-and-shadow-frames-out-of-range`
   - `large-delta`
   - `RELATIVE recorded-anchor fallback to absolute shadow`
5. Expected result:
   - no repeated resolver WARNs for these four v12 debris recordings;
   - debris disappears once authored frames and shadow frames end;
   - no stale endpoint explosions at the post-frame terminal UT;
   - parent `1354326d...` ghost remains visible and correctly positioned.

## Documentation Updates

When implementing:

- Update `CHANGELOG.md` under the current version.
- Proposed CHANGELOG wording:
  `Fixed v12 parent-anchored debris ghosts continuing past their authored relative/shadow samples, which could render debris at stale offsets, emit terminal FX from the wrong position, and spam recorded-anchor resolver warnings.`
- Update `docs/dev/todo-and-known-bugs.md`:
  - leave the boundary-epsilon entry closed;
  - add and close a focused entry for "v12 debris section metadata outlives
    authored rendering frames";
  - reference the broader debris divergence entry as related but not fully
    solved.
- Proposed todo wording:
  `Done - v12 parent-anchored debris section metadata outlived authored frames: playback now retires parent-anchored debris when neither section.frames nor section.absoluteFrames covers the mapped playback UT, preventing stale endpoint clamping and repeated RelativeAnchorResolver warnings. This is separate from the closed boundary-epsilon resolver seam and from the still-open sparse in-range debris divergence work.`
- If the recorder invariant changes saved sidecar shape or validation rules,
  update `.claude/CLAUDE.md` and `AGENTS.md` only if the workflow or format
  contract changes.
- Once Phase 5 lands, add a one-line gotcha to `.claude/CLAUDE.md`:
  `For v12+ parent-anchored debris, Relative TrackSection metadata is not proof of renderable coverage; at least one authored rendering surface (section.frames or section.absoluteFrames) must cover the playback UT.`

## Risks and Guardrails

- Do not retire legacy v11 debris that intentionally uses absolute shadow
  fallback.
- Do not retire live-anchor loop debris (`LoopAnchorVesselId != 0`) with the
  recorded-parent stale-tail rule.
- Do not require both Relative frames and absolute shadow frames to cover the UT;
  either one is sufficient.
- Do not make `RelativeAnchorResolver.PointListCoversUT` tolerant of multi-second
  gaps.
- Do not treat section `endUT` as proof that child relative data exists.
- Do not rename existing log reason codes without updating tests, validation
  scripts, in-game log contracts, and rate-limit keys in the same commit.
- Do not suppress the resolver WARN globally; eliminate the invalid call path.
- Do not close or restart KSP as part of validation. If the game locks files,
  ask the user to close/restart it manually.

## Preferred PR Shape

PR 1: Playback fail-closed fix.

- New authored-frame coverage helper.
- Engine predicate replacement.
- Positioner fail-closed guard.
- Resolver call-site short-circuit where needed.
- Unit tests and retained-sidecar fixture.

PR 2: Recorder invariant fix.

- Clamp or append terminal samples correctly.
- Save-time validation warning.
- Recorder/finalizer tests.
- In-game validation.

Splitting keeps the player-visible stale ghost fix small and reviewable, while
the recorder/finalizer invariant can be handled with more focused runtime
evidence.
