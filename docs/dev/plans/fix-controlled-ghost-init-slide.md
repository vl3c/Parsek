# Fix Plan: Controlled-Vessel Ghost Initial Slide

Date: 2026-05-10 (rev. 4)

Worktree: `C:\Users\vlad3\Documents\Code\Parsek-fix-controlled-ghost-init-slide`

Branch: `fix-controlled-ghost-init-slide`

Scope: a low-risk visual polish for an Absolute-section ghost's first visible frame in watch-mode playback. **Phase 1 is a permanent observability investment** in `GhostRenderTrace` and the activation-decision logging path — the gaps it closes outlast this specific fix and benefit any future investigation of activation-window symptoms (deferred-spawn glitches, watch resume, warp-exit pops, similar slide reports). Phase 2 is investigation; Phase 3 is the eventual fix, scoped from what Phase 1's enhanced trace reveals.

Does NOT change the recorder, the reference-frame contract, anchor eligibility, or any Re-Fly path.

## Revision History

- **rev. 1:** proposed a new `ShouldHoldInitialClampWindowActivationHidden` gate plus `InitialClampStabilizationSeconds`. Two reviewers (internal Opus + external) landed the same kill: the engine positions first then decides hide/activate within one frame ([`GhostPlaybackEngine.cs:1177` → `:1253` → `:1285`](Source/Parsek/GhostPlaybackEngine.cs)) AND the gate was fed `visiblePlaybackUT` (clamp-resolved, not raw `playbackUT`). The proposed shape couldn't deliver the stated invariant and the tests would have missed it.
- **rev. 2:** dropped the new helper / constant / reason tag. Two-phase plan: ad-hoc diagnostic logging commit, then bump `InitialActivationHiddenMinimumFrames` 2 → 3 if the catch-up hypothesis confirmed.
- **rev. 3:** per user direction, Phase 1 became a permanent enhancement to `GhostRenderTrace` plus a new structured `ActivationDecision` phase emit in the activation-flow logging path. Investigation in Phase 2 uses the enhanced tracer; Phase 3 ships only after the mechanism is empirically pinned.
- **rev. 4 (this draft):** four review findings addressed. P2#1 — `EmitPostUpdate` has four call sites, not one (non-loop, primary loop, overlap-primary loop, overlap), each with its own raw / visible UT semantics; the plan now defines per-path raw UT and updates all four sites. P2#2 — `EmitPostUpdate` fires BEFORE the activation-decision branch in `RenderInRangeGhost`, so opening the activation-transition detailed window inside `EmitActivationDecision` is too late for the activation frame's own `AfterUpdate` row; the plan now characterizes the activation frame via `ActivationDecision` alone and reserves the window for SUBSEQUENT frames. P2#3 — `SynchronizeLoadedGhostForWatch` ([`GhostPlaybackEngine.cs:5142`](Source/Parsek/GhostPlaybackEngine.cs:5142)) also runs the activation hide/activate split on the watch-resume path; the plan now instruments it. P3#4 — `IsDetailedWindowOpen` is private; tests now use a new internal `IsDetailedWindowOpenForTesting` helper.

## Problem

In watch-mode playback (not Re-Fly), when a non-debris controlled-vessel ghost first becomes visible after activation, the user can briefly perceive a small "slide into position." Eventual position is correct; the issue is the visible transition. User request: hide during catch-up / preparation, show only when stable.

## Concrete Evidence

`logs/2026-05-10_1713/KSP.log`. Watch session opened on Kerbal X #0 at 17:07:29. Probe ghost #8 (`32d9674c…`, controlled vessel, first track section Absolute) lifecycle around 17:10:04:

| frame | wallclock | currentUT | playbackUT | active | root pos                          | dM     | note |
| ----: | --------- | --------- | ---------- | ------ | --------------------------------- | -----: | ---- |
| 80869 | 04.437    | 456.564   | **456.555** (clamped) | false | `(-64256.03, 3023.68, -137238.20)` | 0.00 m | spawn; `activation-settle`; primer set |
| 80870 | 04.442    | 456.564   | **456.555** (clamped) | false | same                              | 0.00 m | hide via `minimum-frames` |
| 80871 | 04.451    | 456.584   | **456.584** (un-clamped) | false → true | `(-64262.30, 3024.53, -137294.62)` | **56.78 m** | hide releases AND playback advances in same frame |
| 80872 | 04.459    | 456.584   | 456.584 | true | same                              | 0.00 m | stable, same physics tick |
| 80874 | 04.476    | 456.604   | 456.604 | true | `(-64266.54, 3025.11, -137332.90)` | 38.47 m | natural per-physics-tick advance |

Appearance#1 at frame 80871: `firstFrameClamped=F activationStart=456.55 activationLead=0.03 recordingStart-root=(6.27, -0.85, 56.43)` — the catch-up from the clamped pose to the un-clamped pose (~57 m) lands in the same frame the ghost activates.

## Working Hypothesis (Unconfirmed Without Phase 1)

[`InitialVisibleFrameClampWindowSeconds = 0.02 s`](Source/Parsek/ParsekConfig.cs:179) is shorter than `InitialActivationHiddenMinimumFrames = 2` rendered frames at 50 Hz physics (~0.04 s). The hide gate releases AFTER the clamp would, so the activation frame is the same frame the position transitions from clamped to un-clamped pose. The user's perceived "slide" is consistent with this.

**But** the existing trace does not directly evidence this — it shows the position deltas and visibility flags but does not surface clamp-state, lead, or hide-reason as fields aligned by frame number. The trace data we have is ambiguous between "the catch-up jump is what the user sees" and "the catch-up jump happens but is invisible; the user sees something else (FX construction, mesh build, render order)." Phase 1 closes that ambiguity before Phase 3 ships a fix.

## Existing Tracer Surface (Inventory)

Before naming gaps, here's what [`GhostRenderTrace.cs`](Source/Parsek/GhostRenderTrace.cs) and surrounding code already provide:

- `BeginFrame` → `phase=FrameStart` line: section context, source, env, frame counts.
- `EmitPostUpdate` → `phase=AfterUpdate` line: `path`, `reason`, `retired`, `active`, `surface`, `pos`, `rot`, `dM`, `expectedDM`, `velocity`, `body`, `alt`.
- `EmitPhase` → `phase=LateUpdate|UpdatePath|RenderInterp` lines: free-form context.
- `EmitGuardSkip` → `phase=GuardSkip`.
- Detailed-window opens on `first-seen` (4 s), `section-change` (2 s), `structural-event` (5 s), `large-delta` (5 s), `guard-skip` (5 s), `reapply-large-delta`, `terrain-clamp`, `relative-resolver-miss`. Outside these windows, traces are gated to non-noise.
- Per-recording state: `lastRenderedPosition`, `lastPlaybackUT`, `firstSeenUT`, `lastSectionIndex`, `hadVisibleRenderersLastFrame`.
- Activation-decision logging (separate from the structured tracer): `ParsekLog.VerboseRateLimited` lines `initial-activation-hidden-{i}` and `appearance#{N}` ([`GhostPlaybackEngine.cs:1263, 5947`](Source/Parsek/GhostPlaybackEngine.cs:1263)).

## Observability Gaps (What Phase 1 Adds)

1. **Activation-decision is not in the structured trace.** Hide/activate happens at engine line 1252-1293; reason and `framesRemaining` go through `ParsekLog.VerboseRateLimited` independently. Correlating the activation decision with the frame's transform / clamp / lead requires manual stitching across two log streams. Closing this means a new tracer phase emit.
2. **Clamp state is invisible in `AfterUpdate`.** The `playbackUT` field is the visible (clamp-resolved) UT. There is no field for raw `playbackUT`, no field for `activationLead`, no flag for "did `ResolveVisiblePlaybackUT` clamp this frame." Anyone reading a trace cannot tell from one line whether the clamp fired.
3. **No detailed window opens on hidden→visible transition.** The `first-seen` 4-second window catches the user's repro by accident (activation falls within 4 s of first-seen), but a deferred spawn at warp end or a watch-resume activation hours into a session falls outside any open window and is traced thinly.
4. **First-visible delta is not isolated.** The `dM` field mixes hidden→hidden (~0), hidden→visible (the catch-up jump), and visible→visible (normal per-tick motion). To identify "the user sees a slide on the first visible frame," an investigator needs the delta between the LAST hidden transform and the FIRST visible transform as its own field, not buried in a generic per-frame delta that requires correlating `active` flips by hand.

## Phase 1 — Tracer Enhancements (Permanent)

### 1a. New tracer phase: `ActivationDecision`

Add `GhostRenderTrace.EmitActivationDecision(...)` called from EVERY engine path that runs the activation hide/activate split. Three call sites:

- `RenderInRangeGhost` non-loop path ([`GhostPlaybackEngine.cs:1252-1293`](Source/Parsek/GhostPlaybackEngine.cs:1252)). Both branches.
- `SynchronizeLoadedGhostForWatch` ([`GhostPlaybackEngine.cs:5142-5161`](Source/Parsek/GhostPlaybackEngine.cs:5142)). Both branches. This is the watch-resume path — explicitly one of the symptom classes Phase 1's observability is meant to cover.
- Loop paths via `PositionLoopAtPlaybackUT` and `UpdateExpireAndPositionOverlaps` are out of scope for `ActivationDecision` for v1: their state machine is intertwined with cycle wraparound and overlap accounting; instrumenting them would be a larger surface than the user's "lowest-risk" framing wants. Documented under §Out of Scope.

Fires every frame a deferred ghost is in the activation-hidden / activation-decision window (i.e. while `state.deferVisibilityUntilPlaybackSync` is true and `appearanceCount == 0`, plus one frame after the transition for a definitive last activation log). Fields:

```
phase=ActivationDecision rec=… ghostIndex=N frame=N currentUT=… playbackUT=…
  rawPlaybackUT=…              # from RenderInRangeGhost's currentUT before ResolveVisiblePlaybackUT
  visiblePlaybackUT=…          # from ResolveVisiblePlaybackUT — equals playbackUT of AfterUpdate this frame
  activationStart=…
  activationLead=…             # rawPlaybackUT - activationStart
  visibleLead=…                # visiblePlaybackUT - activationStart (≤ clampWindow while clamped, == activationLead after)
  clampFired=true|false        # visiblePlaybackUT != rawPlaybackUT
  hidden=true|false            # ShouldHoldInitialActivationHiddenThisFrame return value
  hideReason=…                 # the reason string (activation-settle / minimum-frames / relative-start / etc.) or "(none)"
  framesRemaining=N            # state.initialRelativeActivationHiddenFramesRemaining (post-decrement)
  transition=hidden|visible|first-visible
  prevHiddenPos=(x,y,z)        # state.lastHiddenPosition (NaN until first hidden frame writes it)
  hiddenPoseDelta=…            # |currentPos - lastHiddenPosition|, only meaningful when transition=first-visible
```

Where the existing `Verbose` "initial activation hidden" line (engine 1263-1280) overlaps with this phase, demote that line to redundant and consider removing in a follow-up — it carries strictly less info than the new phase emit and was the reason hide-reason is not in the structured trace today.

### 1b. Extend `AfterUpdate` with raw / lead / clamped fields

Append three fields to `EmitPostUpdate`'s output line:

```
… rawPlaybackUT=…  visibleLead=…  clampFired=true|false
```

`EmitPostUpdate` has FOUR call sites in the engine, each with its own raw-UT semantics. The plan defines them per-path and updates each call site:

| Call site | Method | Existing `playbackUT` arg (visible) | `rawPlaybackUT` to pass | Notes |
| --- | --- | --- | --- | --- |
| [`GhostPlaybackEngine.cs:1226`](Source/Parsek/GhostPlaybackEngine.cs:1226) | `RenderInRangeGhost` (non-loop) | `visiblePlaybackUT` (post-`ResolveVisiblePlaybackUT`) | `ctx.currentUT` | Clamp may fire while `deferVisibilityUntilPlaybackSync` is true. |
| [`GhostPlaybackEngine.cs:1838`](Source/Parsek/GhostPlaybackEngine.cs:1838) | `PositionLoopAtPlaybackUT` (primary loop) | `loopUT` (already cycle-mapped) | `loopUT` itself if `ResolveVisiblePlaybackUT` is NOT called on this path; otherwise the pre-resolve loop UT | Verify against the actual code: if no clamp resolution runs here, raw == visible and `clampFired` is always false for loop rows. |
| [`GhostPlaybackEngine.cs:2109`](Source/Parsek/GhostPlaybackEngine.cs:2109) | `UpdateLoopingPlayback` overlap-primary | `loopUT` | same as above | Same caveat. |
| [`GhostPlaybackEngine.cs:2304`](Source/Parsek/GhostPlaybackEngine.cs:2304) | `UpdateExpireAndPositionOverlaps` overlap | `loopUT` | same as above | Same caveat. |

**Implementation note:** before threading `rawPlaybackUT` through each path, walk `PositionLoopAtPlaybackUT` and `UpdateExpireAndPositionOverlaps` to confirm whether `ResolveVisiblePlaybackUT` runs there at all. If it does NOT, document `clampFired=false` as the always-true-for-loop-paths invariant rather than synthesizing a fake clamp field. If it DOES, capture the pre-resolve UT separately and pass it. **Do not pass `ctx.currentUT` blindly to the loop call sites** — `ctx.currentUT` is the raw FRAME UT, not the loop-cycle-mapped UT, and would make every loop row report `clampFired=true` spuriously. Existing `playbackUT` field semantics unchanged.

### 1c. Open a detailed window on activation transition (for SUBSEQUENT frames)

The activation-decision branch in `RenderInRangeGhost` runs AFTER `EmitPostUpdate` ([call order: `:1226` → `:1252-1293`](Source/Parsek/GhostPlaybackEngine.cs:1226)). So if this plan tried to open the detailed window inside `EmitActivationDecision` and use it to ungate the SAME frame's `AfterUpdate`, the window would open too late — `EmitPostUpdate` for the activation frame has already evaluated its gate.

The plan handles this by NOT trying to ungate the activation frame's `AfterUpdate`. The activation frame is fully characterized by `EmitActivationDecision` itself, which carries pose, lead, clamp state, hide reason, and `hiddenPoseDelta` — strictly more activation-flow information than `AfterUpdate`'s line on its own. The detailed window opened on `transition=first-visible` is for the NEXT 1.0 s of frames — the `AfterUpdate` and `LateUpdate` rows that follow the activation, where post-activation render artifacts (FX, mesh settling, render-order quirks) would manifest.

Implementation: inside `EmitActivationDecision`, when `transition=first-visible`, open a detailed window of `ActivationTransitionWindowSeconds = 1.0` at `currentUT`, tagged `activation-transition`. Reuses `OpenDetailedWindow` infrastructure.

Alternative considered and rejected: predicting the activation transition BEFORE `EmitPostUpdate` so the window opens early enough to ungate the activation frame's `AfterUpdate`. This requires either calling `ShouldHoldInitialActivationHiddenThisFrame` twice per frame (the gate has side effects via `ConsumeInitialRelativeHiddenFrame`, so the second call would corrupt counter state) or refactoring the gate into a peek/commit pair. Both options expand the surgery surface beyond the user's "lowest-risk" framing for what is fundamentally an observability change. Rejected; the activation frame's coverage via `ActivationDecision` alone is sufficient.

### 1d. Track last-hidden pose per state

Add `state.lastHiddenPosition` (Vector3) and `state.lastHiddenPositionFrame` (int) to `TraceState` in `GhostRenderTrace.cs`. Update at the end of every `ActivationDecision` emit where `transition=hidden`. Read on the `first-visible` transition to compute and emit `hiddenPoseDelta`.

### Implementation surface

- `Source/Parsek/GhostRenderTrace.cs` — new `EmitActivationDecision` method, two new fields on `TraceState` (`lastHiddenPosition`, `lastHiddenPositionFrame`), one new constant `ActivationTransitionWindowSeconds = 1.0`, three new fields appended to the existing `AfterUpdate` line. Extend `EmitPostUpdate`'s signature with `double rawPlaybackUT` (positional, after `playbackUT`). Add a new internal `IsDetailedWindowOpenForTesting(string recordingId, double currentUT)` helper exposing the existing private `IsDetailedWindowOpen` for unit tests (P3#4).
- `Source/Parsek/GhostPlaybackEngine.cs` — call `EmitActivationDecision` from BOTH branches at lines 1252-1293 (`RenderInRangeGhost` non-loop) AND from BOTH branches at lines 5142-5161 (`SynchronizeLoadedGhostForWatch`). Each call passes the path-appropriate raw UT, the resolved visible UT, the `initialActivationHidden` boolean, the hide reason string (`null` for the watch-sync path, which doesn't capture a reason today — see Phase 1 caveat below), and `state.initialRelativeActivationHiddenFramesRemaining`. Update all FOUR `EmitPostUpdate` call sites to pass the per-path raw UT per the table in §1b. Per-frame work overhead is negligible — emits fire only when the tracer's `IsEnabled` toggle is on.

**Watch-sync hide-reason caveat:** `SynchronizeLoadedGhostForWatch` at line 5143 calls `ShouldHoldInitialActivationHiddenThisFrame(traj, state, playbackUT, out string _)` and discards the reason. Phase 1 changes that to capture the reason into a local and pass it through to `EmitActivationDecision`. Behaviour-neutral — only the discarded `out` parameter is now consumed.

### Phase 1 tests

- `Source/Parsek.Tests/GhostRenderTraceTests.cs` (new file or add to an existing one — grep for `GhostRenderTrace` test coverage first):
  - `EmitActivationDecision_HiddenFrame_EmitsExpectedFields` — drive a synthetic state through a hidden frame, capture the emitted line via `ParsekLog.TestSinkForTesting`, assert all the field tokens are present and well-formed.
  - `EmitActivationDecision_FirstVisibleTransition_EmitsHiddenPoseDelta` — drive two hidden frames at distinct positions, then a visible frame, assert `transition=first-visible` and `hiddenPoseDelta` reflects the delta from the last hidden frame.
  - `EmitActivationDecision_OpensDetailedWindowOnTransition` — assert via the new `IsDetailedWindowOpenForTesting(recordingId, currentUT)` helper that the window is open at and after the first-visible transition until `ActivationTransitionWindowSeconds` elapses, and CLOSED on the activation frame's `EmitPostUpdate` (since the window opens after `EmitPostUpdate` on the activation frame — that's the documented limitation).
  - `EmitPostUpdate_AppendsRawPlaybackUTAndLeadAndClamped` — drive a frame where `rawPlaybackUT != playbackUT` (clamp firing), assert the three new fields appear with expected values.
- `Source/Parsek.Tests/GhostPlaybackEngineTests.cs` — extend an existing `RenderInRangeGhost`-touching test (or add one) that asserts `EmitActivationDecision` is called once per frame with the right `hidden` and `hideReason` for the first three frames of an Absolute-section spawn. Add a parallel test for `SynchronizeLoadedGhostForWatch` exercising both branches.

### Phase 1 risks

- **Adds tokens to the `AfterUpdate` line.** Downstream log consumers parse `AfterUpdate` lines positionally OR by token name. `scripts/validate-ksp-log.ps1`, in-game test runner log assertions, and any external grep tooling need a quick check. Tokens are appended (not reordered), so positional parsers may tolerate it; named-token parsers will need the new fields ignored or consumed.
- **New phase token `ActivationDecision`** — same concern, downstream consumers that filter by phase name need to learn it (or ignore it).
- **Tracer is gated by `IsEnabled`** (the diagnostics setting), so default behavior outside diagnostics mode is unchanged. Cost is paid only when the user opts in.
- **`EmitPostUpdate` signature change** — FOUR call sites in the engine (per §1b table), each requires the path-appropriate raw UT. Mechanical update once the per-path raw-UT semantics are confirmed by source walk; loop paths must NOT receive `ctx.currentUT` blindly (would synthesize spurious `clampFired=true`).

### Phase 1 validation

1. `dotnet test` green.
2. Build, deploy DLL, verify deployment per `.claude/CLAUDE.md` recipe.
3. Reproduce against the `s14` save from `logs/2026-05-10_1713`. Open Settings → Diagnostics → enable ghost render tracing. Replay the watch session through UT 456.555.
4. Capture log bundle via `scripts/collect-logs.py phase1-tracer-validation`.
5. Confirm `phase=ActivationDecision` lines appear for ghost #8 across frames 80869-80872, with `hideReason` set per frame, `clampFired` true on hidden frames and false on visible frames, `hiddenPoseDelta` populated on the `transition=first-visible` line.
6. Run a smoke pass with the in-game test runner (`Ctrl+Shift+T`) — no new failures expected; the new traces are additive.

## Phase 2 — Investigate (uses enhanced tracer)

With Phase 1 deployed, capture a fresh log bundle replaying the `s14` save through the same probe-decouple → watch-frame-80871 sequence. From the new `ActivationDecision` lines and the extended `AfterUpdate` lines, answer:

- **Q1.** On the activation frame (transition=first-visible), what is `hiddenPoseDelta`? If it equals the existing `dM` (~57 m), the catch-up is co-located with activation as hypothesized. If it's near zero, the position was already stable for at least one hidden frame and the slide is downstream of position.
- **Q2.** On the activation frame, what is `clampFired`? If still true, the gate released while the clamp was still firing — distinct sub-mechanism. If false (clamp had already released on a prior hidden frame or this frame), the catch-up arithmetic is what we expect.
- **Q3.** On the frames immediately after activation, are there `dM` values inconsistent with `expectedDM` (i.e. the visible motion is not just orbital lerp)? If yes, suspect FX / mesh / bounding-box settling.

Decision matrix:

| Q1 result | Q2 result | Q3 result | Conclusion → Phase 3 path |
| --- | --- | --- | --- |
| ~57 m | true | dM ≈ expectedDM | catch-up co-located with activation, clamp still firing at activation. Path: extend hide past clamp release. |
| ~57 m | false | dM ≈ expectedDM | catch-up bled into activation frame after clamp release. Path: bump `InitialActivationHiddenMinimumFrames` 2 → 3 (rev. 2's choice). |
| ~0 m | either | dM ≠ expectedDM | position stable, motion artifact downstream. Path: separate investigation worktree (FX / mesh / render order). |
| ~0 m | either | dM ≈ expectedDM | no real slide measurable; user perception may be warp / framerate transient. Path: no fix; document and close. |

## Phase 3 — Fix (shape contingent on Phase 2)

Reserved. The leading candidate from rev. 2 (`InitialActivationHiddenMinimumFrames = 2 → 3` in [`ParsekConfig.cs:219`](Source/Parsek/ParsekConfig.cs:219)) maps to one of the rows above and would ship as a one-constant change with the test rewrites described in rev. 2. Other rows yield different fix shapes and would update this document with the actual chosen path before implementation.

Acknowledged limit per external reviewer P1: the constant-bump shape does NOT strictly guarantee "first visible transform equals previous hidden transform" under adversarial Unity timing (e.g. when `Time.fixedDeltaTime ≥ Unity Update period`). The strict guarantee requires a stateful "un-clamped pose has been written for at least one prior hidden frame" condition tracked across frames. That stateful approach is held as an escalation path if Phase 3's chosen fix proves insufficient in field testing.

## Out of Scope

- Recorder-side change to give controlled-vessel children a Relative-anchor section against their parent (deeper architectural change explicitly deferred by `docs/dev/plans/ghost-anchor-recording-chain-plan.md` §106 / §151's "still-being-appended active provisional recordings are not valid anchor targets in v1").
- Re-Fly settle stability (PR #792). Watch playback does not trigger `anchorReFlyUnstable`.
- Debris-specific paths (PR #776, #770, #803). The Phase 3 candidate fix applies to all activation-settle ghosts, but debris flows through their own gates first (`debris-seed-bridge`, `relative-start`) and don't reach activation-settle.
- Stateful "un-clamped pose stabilized" tracking. Held as escalation if Phase 3 chosen fix is insufficient.
- Tightening or widening `InitialVisibleFrameClampWindowSeconds = 0.02`. Tuning of the existing clamp is a separate question.
- Removing the existing `ParsekLog.VerboseRateLimited` "initial activation hidden" line. Once `EmitActivationDecision` is in place and field-tested, that older line is redundant; a follow-up cleanup commit can remove it. Not in scope for this fix.
- `EmitActivationDecision` instrumentation of the loop / overlap / overlap-primary call sites in `PositionLoopAtPlaybackUT` and `UpdateExpireAndPositionOverlaps`. Loop activation is intertwined with cycle wraparound and overlap accounting; instrumenting it deterministically requires reasoning about per-cycle activation state that the v1 surface does not need. If a future symptom report points at a looped recording's activation behaviour, instrument those paths in a follow-up. The §1b `AfterUpdate` field additions DO cover those paths (since `EmitPostUpdate` is called from all four).

## Review Notes

- rev. 3 integrated internal Opus + external review feedback from rev. 1 → rev. 2 plus user direction on permanent observability.
- rev. 4 (this draft) integrates four further external review findings: P2#1 (`EmitPostUpdate` four call sites with per-path raw-UT semantics), P2#2 (window-open ordering — drop the activation-frame `AfterUpdate` ungating claim), P2#3 (`SynchronizeLoadedGhostForWatch` instrumentation), P3#4 (private `IsDetailedWindowOpen` → new `IsDetailedWindowOpenForTesting`).
- Phase 1 is reviewer-eligible on its own (engine state field additions, tracer signature change with four call-site updates, new phase token, watch-sync instrumentation). Per `.claude/CLAUDE.md` Code Review Follow-Ups: a tracer enhancement that touches a shared API surface across multiple call sites qualifies as risky-enough for one full review pass before merge. Phase 3's eventual fix likely qualifies for a separate review pass depending on shape.
