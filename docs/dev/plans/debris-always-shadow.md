# Always-shadow path for parent-anchored v12+ debris

> Follow-up to PR #800 (smooth-shadow route during tumble windows). After playtest, the user reports "still some ghost instability at certain points" on top of an overall improvement. The shadow-route path itself works; the remaining instability is at the route boundaries and in a pre-engage tail before the gate fires. This plan removes the gate's rendering authority for v12+ parent-anchored debris and routes the recorded `absoluteFrames` shadow unconditionally whenever shadow data covers the playback UT, falling back to the legacy relative-offset reconstruction (and finally to Hide) in a strict ladder.

## Symptom recap from `logs/2026-05-10_1449_post-shadow-route-playtest/KSP.log`

Two tumble events captured. Inside both shadow-routed windows the shadow path is rock-solid (`dM ≈ expectedDM` to the centimetre). Three boundary failure modes remain visible:

**(1) Pre-engage drift.** The angular-rate gate is reactive: `bracketDeg` and `rateDegPerSec` are computed over a 220 ms parent-rotation bracket, with the enter threshold at 150 °/s. Around UT 136.16 - 137.04 the parent is rotating at ~125 °/s — below threshold — and the legacy relative-offset reconstruction has been producing per-frame `dM = 60-85 m` (`expectedDM ≈ 2.8 m`) for ~880 ms before the gate finally fires. Smoking-gun frame:

```
phase=UpdatePath rec=2f8c3916 ghostIndex=1 frame=35750 currentUT=136.163
  mode=RecordedRelative sectionIndex=0
  beforeUT=134.703 afterUT=136.903 t=0.6636
  localOffset=(-1730.04,584.40,-346.48)
  anchorPos=(545.04,47.21,3855.84)
  final=(505.90,-1200.22,2478.52)
```

`beforeUT=134.703 afterUT=136.903` is a **2.2-second** debris bracket at the tail of section 0 — the optimizer thinned the recording's last samples. The legacy resolver linearly lerps `localOffset` across that 2.2 s gap while the parent rotates underneath, and the per-frame `Δ(localOffset)` magnitudes match the observed 60-85 m position deltas. The gate never sees this because its signal is parent rotation rate over a *separate* 220 ms bracket; the failure mode lives in the *debris* bracket span, not the parent.

**(2) Engage discontinuity.** At the gate-fire frame the ghost teleports from "where the legacy resolver thinks it should be" to "where the absolute-shadow says it actually is":

| Tumble | Ghost | offsetMeters | dM at engage | expectedDM |
|---|---|---|---|---|
| 1 | #1 | 1469.9 | 82.90 | 1.60 |
| 1 | #2 | 1505.4 | 85.28 | 1.61 |
| 1 | #5 |  165.4 |  9.67 | 2.46 |
| 2 | #3 |  550.4 | 37.95 | 2.42 |

Magnitude scales with anchor-offset radius — confirms the legacy reconstruction had been steadily diverging from the recorded debris path at the moment the gate fired. (First-tumble debris bracket here was tight, so the dominant source of the 80 m engage pop is the gate firing *between two debris-recording frames* and switching the rendering surface mid-step. Different mechanism from (1) — same fix.)

**(3) Release discontinuity.** Smaller pop (~9 m) at gate exit because parent has stopped tumbling and legacy/shadow now agree more closely.

## Root cause

The `RecordedRelative` resolver reconstructs `debris.worldPos(ut) = lerp(parent.pos[t1], parent.pos[t2], α) + slerp(parent.rot[t1], parent.rot[t2], α) · lerp(offset[t1], offset[t2], β)`. The `absoluteFrames` shadow is the recorder's snapshot of `debris.worldPos` at each Relative sample. They only reconstruct identically when:
- α and β are pinned to the same instant (rare — debris and parent samples drift),
- the parent is not rotating fast (slerp arc ≈ chord through the offset),
- and the *debris bracket span itself* is short enough that lerp across it doesn't outrun the recorded curvature.

The thin-tail 2.2 s debris bracket violates the third condition; high parent rate violates the second. Either one breaks the reconstruction; both stack at the boundaries the user sees.

The shadow track has neither lever-arm amplification nor parent-coupled interpolation, so chord error inside it is bounded by the shadow's own sample-to-sample curvature — which is the debris's actual recorded path, not a synthesized cross-product of two interpolations.

## Decision: always-shadow for v12+ parent-anchored debris

For recordings where `IsDebris && DebrisParentRecordingId != null` (the v12+ population) and the active Relative section has `absoluteFrames != null && absoluteFrames.Count >= 2` and the playback UT lies inside `[absoluteFrames[0].ut, absoluteFrames[last].ut]`, position the ghost via the shadow lerp **unconditionally**. Drop the angular-rate gate's authority over rendering. The gate continues to evaluate per-frame and continues to emit `anchor-rotation-interp-hold-engaged/-released` lines, but now its return value gates *only* FX suppression (plumes, RCS, audio, reentry FX) for the frame, not whether the mesh is rendered or hidden.

When shadow data does not cover the playback UT (older recordings without `absoluteFrames`, frames outside the section, frames before/after the shadow's first/last sample), fall through to the legacy relative-offset resolver. If the gate is *also* unreliable for that frame, use the existing Hide path as a final fallback. Strict ladder: **shadow → legacy → hide**, with the gate as a separate FX-suppression bit that runs alongside.

## What this fixes

- **Pre-engage drift**: legacy is never used while shadow covers, so the 2.2 s thin-tail bracket failure mode disappears for in-coverage UTs.
- **Engage discontinuity**: there is no engage — shadow is on every frame in coverage, so no rendering-surface switch.
- **Release discontinuity**: same — no release.
- **Loop / overlap branches**: same surfaces (shadow versus legacy versus hide) used consistently across `RenderInRangeGhost`, `PositionLoopAtPlaybackUT`, `UpdateOverlapPlayback`, `UpdateExpireAndPositionOverlaps`. No loop-only carve-outs.

## What this preserves

- **Live-anchor case** (`Recording.LoopAnchorVesselId != 0` Relative-loop playback) is excluded by `ShouldEvaluateAnchorRotationReliability` already (`ParsekFlight.cs:15119-15124` predicate is `IsDebris && DebrisParentRecordingId != null`). The new always-shadow path uses the same predicate, so live-anchor playback continues through the existing legacy resolver untouched.
- **Legacy v6/v11 debris** (no `DebrisParentRecordingId`) is unaffected; routes through the existing `LegacyDebrisShadowGate` (PR 3c) unchanged.
- **FX suppression during real tumbles** keeps working: the gate evaluator still runs, sets `state.anchorRotationShadowRoutedThisFrame` when the gate would have fired (new semantic: "FX should be suppressed this frame"), and the existing `LoopShadowFxBranch` switch in `GhostPlaybackEngine.cs:1850-` continues to tear down running FX when the bit is set. Steady-state always-shadow playback (no tumble) does not suppress FX — debris keeps its plumes / reentry visible during normal flight, which is the correct behaviour and matches today's no-gate-fired behaviour.
- **Hide fallback** stays available for the rare case where shadow does not cover AND gate fires AND legacy reconstruction would be visible chaos. Same `HideAnchorRotationUnreliableState` helper, same lifecycle.

## Concrete changes

### 1. New positioner step: try shadow always

Add a new method on the engine, `TryPositionViaAbsoluteShadowIfParentAnchoredDebris`, that:

1. Returns `false` immediately if `traj` is not v12+ parent-anchored debris (predicate identical to `ShouldEvaluateAnchorRotationReliability`).
2. Resolves the active Relative section at `playbackUT` via `TrajectoryMath.FindTrackSectionForUT`. Returns `false` if the section is not Relative or has no `absoluteFrames` (pre-v7 recording, or non-Relative section).
3. Returns `false` if `playbackUT` is outside `[absoluteFrames[0].ut, absoluteFrames[last].ut]` (with `1e-6` epsilon, matching `TryPositionFromRelativeAbsoluteShadow`'s existing coverage check at `ParsekFlight.cs:16710-16718`).
4. Calls `positioner.TryPositionFromRelativeAbsoluteShadow(...)`. Returns the positioner's result.

This method is called from each of the four engine sites that today call `TryRouteAnchorRotationUnreliable` *before* the gate runs:

| File | Line(s) | Branch |
|---|---|---|
| `GhostPlaybackEngine.cs` | 1148-1188 | `RenderInRangeGhost` non-loop |
| `GhostPlaybackEngine.cs` | 1822-1846 | `UpdateLoopPlayback` primary loop |
| `GhostPlaybackEngine.cs` | 2096-2120 | `UpdateOverlapPlayback` overlap-primary |
| `GhostPlaybackEngine.cs` | 2289-2310 | `UpdateExpireAndPositionOverlaps` overlap-loop |

At each site:
```csharp
state.anchorRetiredThisFrame = false;
state.anchorRotationShadowRoutedThisFrame = false;

bool shadowPositioned = TryPositionViaAbsoluteShadowIfParentAnchoredDebris(
    i, traj, state, visiblePlaybackUT, "non-loop", out double bracketBeforeUT, out double bracketAfterUT);

// Gate still evaluates -- its result is used only for FX suppression now.
bool fxSuppressed = TryEvaluateAnchorRotationFxSuppression(
    i, traj, f, state, visiblePlaybackUT, "non-loop");

if (shadowPositioned)
{
    state.anchorRotationShadowRoutedThisFrame = fxSuppressed;
    // Skip the legacy positioning chain. Pipeline runs Activate /
    // TrackGhostAppearance / ApplyFrameVisuals normally; ApplyFrameVisuals
    // honours fxSuppressed via the existing LoopShadowFxBranch path.
}
else
{
    // Legacy positioning chain runs (TryPositionRelativeSectionAtPlaybackUT,
    // PositionFromOrbit, InterpolateAndPosition, etc.).
    // If gate said unreliable AND legacy was used, fall back to Hide.
    if (fxSuppressed)
    {
        // Same Hide path as today: anchorRetiredThisFrame=true, mesh hidden,
        // FX torn down, exit-watch event fired if applicable.
        HideAnchorRotationUnreliableState(i, traj, state, visiblePlaybackUT, ctx.warpRate);
        // ... same exit-watch + GhostRenderTrace.EmitGuardSkip emit as today
    }
    // else: clean legacy frame, no suppression.
}
```

### 2. Split the existing router

`TryRouteAnchorRotationUnreliable` (`GhostPlaybackEngine.cs:2712`) currently does both decision *and* shadow-route attempt. Split:

- **`TryEvaluateAnchorRotationFxSuppression`**: exact same evaluator + hysteresis flow as today's first half, returns `bool` (formerly the `decision.Unreliable` after hysteresis). Owns the `anchor-rotation-interp-hold-engaged/-released` log lines (already emitted inside `TryEvaluateAnchorRotationReliability`'s callback so this is unchanged at the source level — only the engine-side caller plumbing renames).
- **`TryPositionViaAbsoluteShadowIfParentAnchoredDebris`**: new method described in §1.
- **Old `TryRouteAnchorRotationUnreliable` deleted.**

`TryRouteAnchorRotationToShadow` (`GhostPlaybackEngine.cs:2802`) survives as the inner helper called by the new always-shadow positioner: same coverage check, same `positioner.TryPositionFromRelativeAbsoluteShadow` call, same `anchor-rotation-shadow-route` log line (now fires on every covered frame instead of only inside hold windows). The line gets one extra field, `mode=always|gated`, distinguishing the always-shadow steady state from the gate-also-fired case (gate decision is available because `TryEvaluateAnchorRotationFxSuppression` runs alongside).

### 3. Trace observability

Two additions to `GhostRenderTrace.cs`:

- **`activeSurface` field on `EmitPostUpdate`** (`AfterUpdate` line): one of `legacy | shadow | hidden`. Lets a single log line attribute every frame to a path. Plumbed in via the same call-site changes already needed in §1.
- **`debrisBracketSeconds` on `BeginFrame`** (`FrameStart` line): `(afterUT - beforeUT)` from the resolved Relative section's debris-frame bracket at the playback UT. Default `0` if not Relative or not yet computed. Lets the wide-bracket pattern be searched directly in post-hoc analysis.

Both are surgical: existing log lines pick up two extra fields each; no new log line types.

### 4. Tests

#### Unit: `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`

New `[Collection("Sequential")]`-attached tests:

1. **`AlwaysShadow_RoutesShadow_WhenCoverageAvailable_AndGateInactive`** — synthesize a v12 parent-anchored debris recording with `frames` at 2.2 s cadence and `absoluteFrames` at 220 ms cadence; the parent recording is rotation-stable (gate would never fire). Drive a frame at a UT inside section coverage. Assert: positioner's `TryPositionFromRelativeAbsoluteShadow` was called; gate evaluated but didn't fire; `state.anchorRotationShadowRoutedThisFrame == false` (FX not suppressed in steady state); active surface is `shadow` (via either trace assertion or test-mock surface field).
2. **`AlwaysShadow_FallsThroughToLegacy_WhenAbsoluteFramesNull`** — same fixture, set `absoluteFrames = null`. Assert: shadow positioner not called; legacy `TryPositionRelativeSectionAtPlaybackUT` invoked; trace surface = `legacy`.
3. **`AlwaysShadow_FallsThroughToHide_WhenLegacyAndGateUnreliable`** — `absoluteFrames = null` AND mock gate returns `Unreliable=true`. Assert: shadow not called; legacy not run; `state.anchorRetiredThisFrame == true`; trace surface = `hidden`.
4. **`AlwaysShadow_OutOfCoverageUT_FallsBackToLegacy`** — `absoluteFrames` populated but playback UT past last sample. Assert: shadow positioner returned `false`; legacy ran; trace surface = `legacy`.
5. **`AlwaysShadow_GateFires_SuppressesFx_WhileShadowPositioned`** — shadow available + gate `Unreliable=true`. Assert: shadow ran (mesh stays visible); `state.anchorRotationShadowRoutedThisFrame == true`; `LoopShadowFxBranch` resolves to `ForcedShadowTeardown`.
6. **`AlwaysShadow_NotV12Debris_LegacyRoute`** — recording with `IsDebris=false` or `DebrisParentRecordingId=null`. Assert: shadow positioner not called even when `absoluteFrames` exists; legacy resolver runs.
7. **`AlwaysShadow_LiveAnchorLoop_NotInScope`** — `Recording.LoopAnchorVesselId != 0` (live-anchor loop). Assert: same as (6) — predicate gate keeps live-anchor playback on the legacy path.

The mock `SpawnPrimingPositioner` already added in PR #800 needs:
- `int LegacyPositionCalls`, `int ShadowPositionCalls`, `int HidePathCalls`
- `bool ShadowPositionShouldSucceed` (existing)
- new `bool TraceSurfaceCapture` recording the per-frame surface choice for assertion.

Fixture `MakeParentAnchoredDebrisWithShadowFrames` extended with optional `wideDebrisBracket: bool` parameter that emits `frames` at 2.2 s cadence to exercise the bracket-width regression specifically.

#### In-game: `Source/Parsek/InGameTests/RuntimeTests.cs`

`TumblingParentDebris_ShadowRoute_KeepsGhostVisibleAndStable` already exists from PR #800. Add a sibling:

- **`ParentAnchoredDebris_AlwaysShadow_StableThroughout`**: drive a parent-anchored debris ghost through three frames at UTs spanning the recorded section. Assert mesh stays active each frame, `state.anchorRotationShadowRoutedThisFrame == false` on all three (no gate fire), and per-frame position delta matches the `absoluteFrames` lerp output to within 0.01 m.

### 5. Documentation

- `CHANGELOG.md` — extend the v0.9.2 tumbling-parent entry: `Parent-anchored debris now plays back via the recorded absolute-position shadow track unconditionally during the recorded Relative section, eliminating the residual position pops at gate-engage and gate-release boundaries that remained after the smooth-route landing in the previous build. Recordings without shadow data fall back to the existing path.`
- `docs/dev/todo-and-known-bugs.md` — close Follow-up A and B (transition-blend, rotation-jitter) entries from the PR #800 plan and add a new entry for the surfaced "wide debris bracket at section tail" pattern as a recorder-side optimisation candidate (do not regress the always-shadow path on top of fixing it).
- `.claude/CLAUDE.md` and `AGENTS.md` — extend the Format-v7 paragraph to note the always-shadow rendering contract for v12+ parent-anchored debris: `Phase D contract is preserved (no live-anchor proxy); the shadow remains recording data and is the rendering source of truth whenever the recording's IsDebris && DebrisParentRecordingId != null AND the active Relative section's absoluteFrames covers the playback UT. The angular-rate gate (PR #793) is retained as the per-frame FX-suppression signal but no longer gates rendering. Hide remains available as the third-tier fallback when shadow is unavailable AND the gate is firing.`

## Risks and decisions made

**Risk 1: increased per-frame cost.** The shadow positioner runs on every parent-anchored-debris frame, not just inside tumble holds. Cost: one `InterpolateAndPosition` over `absoluteFrames` per such ghost per frame. Mitigation: this is the same call already made every frame today inside the legacy resolver path that the shadow replaces (the legacy resolver also walks `frames` and applies the offset transform). Net: comparable; the shadow lerp is cheaper than the slerp+lerp+matmul of the legacy path. No mitigation needed beyond confirming on-load.

**Risk 2: shadow path silently produces a different visual trajectory than legacy outside tumble windows.** Possible if recorder-side shadow sampling is misaligned. Verified in the playtest log: inside the smooth-route window every parent-anchored ghost showed `dM ≈ expectedDM` — shadow lerp tracks the recorded path correctly. The same shadow data is on disk for the entire section, so steady-state always-shadow renders the same trajectory the gate-shadow window already validated.

**Risk 3: FX-suppression semantic flip.** Today `state.anchorRotationShadowRoutedThisFrame` is set by `TryRouteAnchorRotationToShadow` when the gate fires AND shadow was used. After this PR it is set by the engine when the gate evaluator fires (independent of shadow vs legacy). The downstream readers (loop / overlap branches at lines 1836, 2106, 2300, plus the FX-branch resolver) interpret it as "suppress FX this frame," which matches the new semantic. Documented in the field's XML comment.

**Risk 4: legacy hide path becomes harder to exercise.** Hide is now reachable only when `absoluteFrames == null` AND the gate fires. The existing in-game test `TumblingParentDebris_ShadowRoute_KeepsGhostVisibleAndStable` covers the shadow-used branch; new unit test (3) above covers the legacy+hide fallback. Adequate.

**Risk 5: opus reviewer flagged that the previous router's `TryRouteAnchorRotationToShadow` set `state.anchorRotationShadowRoutedThisFrame = true` only when the gate fired, and several engine call sites depend on that flag.** Addressed above (§1, §2): the flag's setter moves to the engine call site and tracks the gate evaluator's result, not the shadow positioner's success — a deliberate semantic change reflected in the XML doc and in the unit tests' assertions.

## Out of scope

- Recording-side optimisation of thin-tail debris brackets. The 2.2 s gap at the tail of section 0 is a recorder optimiser artifact; tightening that would also help, but it is recording-format work and the always-shadow render path renders correct visuals regardless.
- Transition-blend between shadow and legacy at section boundaries (entering / exiting Relative coverage). With shadow now used uniformly inside Relative sections, the only remaining boundary is at section transitions — out of scope here, validated on first playtest, deferred to a follow-up if visible.
