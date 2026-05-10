# Controlled-Vessel Ghost Initial Slide - Next Investigation Plan

Worktree: `C:\Users\vlad3\Documents\Code\Parsek\Parsek-controlled-ghost-slide`

Branch: `fix-controlled-ghost-slide`

Base: `origin/main` at `c84010d8` (`Merge pull request #818 from vl3c/fix-tail-orbit-state-vector-frame`)

## Scope

This phase is investigation and planning only. Do not ship a production or test-code fix from the retained logs alone: the one retained log with the exact original probe symptom predates the structured activation trace, while the later log has the trace but does not reproduce the same controlled-probe failure on the active fork.

Prior plan: `docs/dev/plans/fix-controlled-ghost-init-slide.md`. Phase 1 observability from that plan is present on `origin/main`.

## Current Code Surface

- `GhostPlaybackEngine.RenderInRangeGhost` calls `ResolveVisiblePlaybackUT` before positioning, emits `AfterUpdate`, then runs the hide/activate branch and emits `ActivationDecision` on non-retired frames (`Source/Parsek/GhostPlaybackEngine.cs:1138-1278`).
- `ResolveVisiblePlaybackUT` clamps the first visible playback UT to `activationStartUT` only inside `InitialVisibleFrameClampWindowSeconds = 0.02` (`Source/Parsek/GhostPlaybackEngine.cs:5550-5580`, `Source/Parsek/ParsekConfig.cs:179`).
- `ShouldHoldInitialActivationHiddenThisFrame` primes `InitialActivationHiddenMinimumFrames = 2`, consumes one count on the priming frame, and consumes the remaining count on the next hidden frame (`Source/Parsek/GhostPlaybackEngine.cs:5777-5844`, `Source/Parsek/ParsekConfig.cs:219`).
- `GhostRenderTrace.EmitActivationDecision` logs `rawPlaybackUT`, `visiblePlaybackUT`, `activationLead`, `visibleLead`, `clampFired`, `hidden`, `hideReason`, `framesRemaining`, `transition`, `prevHiddenPos`, and `hiddenPoseDelta` (`Source/Parsek/GhostRenderTrace.cs:408-537`).

## Retained Log Evidence

### `logs/2026-05-10_1713`: original probe symptom, but no structured fields

Verified: `rg "phase=ActivationDecision|rawPlaybackUT=|visibleLead=|clampFired=" logs/2026-05-10_1713/KSP.log` returns no matches.

Exact original controlled-probe trace:

- `KSP.log:119457-119461`: ghost #8 `"Kerbal X Probe"` (`rec=32d9674c9bad4b5091c78aaa761eb11e`) spawns at frame `80869`, section `sec=0`, `ref=Absolute`, `env=Atmospheric`, `source=Background`; hidden by the old line only: `initial activation hidden: reason=activation-settle ut=456.555 activationStart=456.555 ... minFrames=2`.
- `KSP.log:127042-127044`: same ghost #8 later emits `phase=AfterUpdate ... reason=large-delta ... active=true ... dM=555.35 expectedDM=41.97`, with `mode=PointInterp`, `pointFrameSource=flat-points`, and `sectionIndex=1`.

Interpretation: this is the best evidence that the user-visible controlled-probe slide existed, but it cannot answer whether `hiddenPoseDelta`, `clampFired`, or activation release timing caused it because those fields did not exist in that run. It also occurs well after the initial hidden line, so the retained slice could be mixing activation, chain transfer, and later section transition effects.

### `logs/2026-05-10_2123`: observability present, but target probe is not failing

Verified: `phase=ActivationDecision` appears 60 times in this log. The original probe lineage is mostly suppressed/superseded in the later run:

- `KSP.log:89007`: prior probe `e9a1525140194b1cafc4f1bc65133211` is skipped as `reason=superseded-by-relation`.

Clean active controlled-probe fork:

- `KSP.log:88985-88989`: `rec_f1363fc127ab47a28812ce4be6515453` starts in `ref=OrbitalCheckpoint`, `source=Checkpoint`, `mode=Orbit`; first hidden frame has `clampFired=true`, `visibleLead=0.000`, `hideReason=activation-settle`.
- `KSP.log:89008-89011`: second hidden frame has `dM=45.58 expectedDM=48.90`, `clampFired=false`, `hideReason=minimum-frames`.
- `KSP.log:89018-89023`: first visible transition has `hiddenPoseDelta=0.000`, `clampFired=false`, and the `GhostAppearance` row is on `activeFrame=OrbitalCheckpoint`.

Interpretation: for the active controlled-probe fork in the observed run, the structured trace falsifies the "visible activation slide" hypothesis. The ghost was already at the same pose on the last hidden and first visible frames.

Related parent jump:

- `KSP.log:20131-20135`: parent `e19eb61d7559437089274698edb5618a` first hidden frame: `active=false`, `dM=0`, `clampFired=false`, `hideReason=activation-settle`.
- `KSP.log:20151-20154`: second hidden frame: `dM=70.96 expectedDM=69.84`, `clampFired=false`, `hideReason=minimum-frames`.
- `KSP.log:20164`: `ReFlySettle FloatingOrigin.setOffset ... magnitude=524.14` occurs on the activation frame.
- `KSP.log:20165-20168`: first visible transition: `dM=489.22 expectedDM=34.92`, `clampFired=false`, `hiddenPoseDelta=489.225`, `prevHiddenPos=(-404.78,0.16,-213.22)`, current position `(29.45,-0.01,12.13)`.

Interpretation: this is a real first-visible pose jump, but it is on the parent `Kerbal X`, not the active controlled probe. It is also coincident with a `ReFlySettle` origin shift and co-bubble path, so it should not be used to choose a controlled-probe-only fix without a fresh target repro.

## Decision Matrix

| Fresh evidence result | Conclusion | Next action |
| --- | --- | --- |
| Active controlled-probe first-visible has `hiddenPoseDelta <= 1m`, `clampFired=false`, and post-activation `dM ~= expectedDM` | No controlled-probe activation-slide bug reproduced on current main | Do not change production code. Close with evidence, or move investigation to parent/co-bubble/origin-shift artifacts if user still sees motion. |
| Active controlled-probe first-visible has large `hiddenPoseDelta`, `clampFired=true` | Gate releases while first-visible clamp is still active | Extend hidden activation until one post-clamp frame has been written. Prefer stateful stable-pose gate over raw constant tuning. |
| Active controlled-probe first-visible has large `hiddenPoseDelta`, `clampFired=false`, and previous hidden frame had normal `dM ~= expectedDM` | Catch-up happens between last hidden and first visible after clamp release | Low-risk candidate: bump `InitialActivationHiddenMinimumFrames` from 2 to 3, but validate against frame-rate variance. |
| Active controlled-probe first-visible has large `hiddenPoseDelta` coincident with `ReFlySettle FloatingOrigin.setOffset` or co-bubble correction | Origin/correction shift, not activation clamp | Add an origin-shift/correction-aware hide hold or pursue co-bubble/ReFlySettle-specific fix. Do not solve with a global min-frame bump unless it empirically absorbs the shift. |
| Only parent/non-target recordings show the jump; active controlled probe remains clean | Symptom moved or was superseded in repro | Plan a separate parent/co-bubble watch investigation. Controlled-probe fix remains unproven. |
| Target controlled probe is suppressed, superseded, or lacks `ActivationDecision` rows | Evidence insufficient | Rerun the repro with tracing enabled and target recording active. |

## Proposed Fix Options

1. No production change.
   Use if fresh active controlled-probe evidence matches the clean `rec_f136...` pattern. This is currently the only defensible outcome from retained `2123`.

2. Bump `InitialActivationHiddenMinimumFrames` from 2 to 3.
   Smallest implementation if fresh evidence shows large first-visible `hiddenPoseDelta` after clamp release with otherwise normal per-frame motion. Risk: a constant bump does not prove stability under lower frame rates or same-frame floating-origin shifts.

3. Stateful "stable hidden pose" release.
   Track the last hidden activation pose and require one hidden frame where the next pose delta is within an expected-motion threshold before activating. Stronger than a constant bump and directly targets the desired invariant, but touches activation state and needs careful tests around relative starts, debris seed bridge, watch sync, and repeated scene reloads.

4. Origin-shift/correction-aware hold.
   If fresh target evidence shows a `FloatingOrigin.setOffset`, co-bubble correction, or ReFlySettle correction on the activation frame, hold visibility through that frame and release only after the corrected pose has survived one hidden render. This is narrower than a global min-frame bump but depends on plumbing a reliable "large origin/correction shift this frame" signal into the activation gate.

## Required Fresh Repro

Minimum useful repro:

1. Use a save/session that still reproduces the controlled-probe watch slide on `origin/main` with Phase 1 observability.
2. Enable `Settings -> Diagnostics -> Ghost render tracing`.
3. Drive the original sequence through probe separation and watch transfer, keeping the active controlled probe unsuppressed. If using `s15`, verify the target is not skipped by `superseded-by-relation`.
4. Capture logs with `python scripts/collect-logs.py controlled-ghost-slide-repro`.
5. Extract the target recording's first activation window:
   - `phase=FrameStart`
   - `phase=UpdatePath`
   - `phase=AfterUpdate`
   - `phase=ActivationDecision`
   - `GhostAppearance`
   - nearby `ReFlySettle FloatingOrigin.setOffset`
   - nearby `GuardSkip reason=superseded-by-relation`

Acceptance for a fix PR: the same scenario must show `transition=first-visible hiddenPoseDelta` below the chosen threshold and no visible-frame `dM` outlier above expected motion for the target controlled probe.

## Tests and Validation for a Later Fix PR

- Unit tests for the chosen activation gate shape in `GhostPlaybackEngine` helper seams:
  - existing two-frame hidden contract remains unchanged or deliberately updated;
  - clamp-fired case stays hidden until clamp clears;
  - stable-pose/stateful gate requires a prior hidden stable pose;
  - watch-sync path has the same activation decision semantics or an explicitly documented exception;
  - retired-frame carve-out still emits no `ActivationDecision`.
- `GhostRenderTraceTests` updates if field semantics change, especially `hiddenPoseDelta`, `clampFired`, and transition-window behavior.
- Run `dotnet test Source/Parsek.Tests/Parsek.Tests.csproj` from the fix worktree.
- If runtime behavior changes, run the relevant in-game test slice and keep `KSP.log` plus `parsek-test-results.txt` evidence. Do not close or restart KSP from an agent session.

## Risk and Rollback

- Activation gates affect every ghost, not just controlled probe children. Too much hiding can make ghosts appear late after warp, watch resume, relative starts, debris seed bridges, or loop/overlap activation.
- Constant tuning is easy to roll back but may be frame-rate-sensitive.
- Stateful stable-pose gating is more correct but increases state complexity and could strand a ghost hidden if the threshold is too strict.
- Origin-shift-aware gating is narrower but requires high-confidence signal plumbing; a false positive delays visibility, while a false negative leaves the slide.
- Rollback for any later code fix should be a single revert of the activation-gate change while keeping the observability from Phase 1.

## Current Recommendation

Do not implement a production/test-code fix from the retained logs alone. The active controlled-probe fork `rec_f136...` activates cleanly from `OrbitalCheckpoint`; the original `1713` probe symptom lacks the new fields; and the only structured large first-visible jump in `2123` is the parent `e19eb61d...`, coincident with `ReFlySettle` and co-bubble correction. The next PR should either capture a fresh target repro or explicitly re-scope to the parent/co-bubble/origin-shift jump.
