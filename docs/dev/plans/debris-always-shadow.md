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

- **Live-anchor case** (`Recording.LoopAnchorVesselId != 0` Relative-loop playback) is excluded by tightening `ShouldEvaluateAnchorRotationReliability` (`ParsekFlight.cs:15119-15124`) to require `LoopAnchorVesselId == 0u` in addition to `IsDebris && DebrisParentRecordingId != null`. **This addition is required** — without it the always-shadow path bypasses the resolver-level `LoopAnchorVesselId != 0` short-circuit at `RelativeAnchorResolver.cs:288-297` ("loop-anchor-out-of-scope") that today keeps live-anchor recordings on legacy. Adding the check at the predicate level is a no-op for PR #800's gate (the resolver was returning false for those recordings anyway), but it correctly excludes them from the new shadow path. A regression test covers a recording with `IsDebris=true && DebrisParentRecordingId != null && LoopAnchorVesselId != 0`.
- **Legacy v6/v11 debris** (no `DebrisParentRecordingId`) is unaffected; routes through the existing `LegacyDebrisShadowGate` (PR 3c) unchanged.
- **FX suppression during real tumbles** keeps working: the gate evaluator still runs, the router sets `state.anchorRotationShadowRoutedThisFrame = fxSuppress` (the gate's per-frame fire bit) inside `TryRouteAnchorRotationToShadow` only when the shadow positioner succeeds. Downstream FX wiring at the four post-position sites reads that flag (not the route enum), so steady-state shadow render leaves the flag false and FX play normally; gated-mode shadow render sets it true and the existing `LoopShadowFxBranch` / `AdjustFxFlagsForShadowRoute` paths tear down running FX. The non-loop site at `GhostPlaybackEngine.cs:1247-1251` was previously reading `anchorRotationShadowed` (the route enum, true on every shadow frame) — that read is changed to `state.anchorRotationShadowRoutedThisFrame` so steady-state always-shadow does not silently suppress FX. The three loop / overlap sites already read the flag (lines 1836, 2106, 2300) and need no behavioural change.
- **Hide fallback** stays available for the rare case where shadow does not cover AND gate fires AND legacy reconstruction would be visible chaos. Same `HideAnchorRotationUnreliableState` helper, same lifecycle.

## Concrete changes

### 1. Refactor the router

`TryRouteAnchorRotationUnreliable` (`GhostPlaybackEngine.cs:2712`) is the right place to put the new ladder — it is the single function called by both the non-loop branch (`RenderInRangeGhost`, `GhostPlaybackEngine.cs:1150`) and the shared loop / overlap branch (`PositionLoopAtPlaybackUT`, `GhostPlaybackEngine.cs:3148`). Two router call sites total (not four — the four sites listed elsewhere in this plan are the post-position trace sites where `EmitPostUpdate` is called).

Pre-PR-#803 the router checked `decision.Unreliable` first and only tried shadow when the gate fired. Post-PR-#803:

```csharp
private AnchorRotationUnreliableRoute TryRouteAnchorRotationUnreliable(...)
{
    if (flags.tryEvaluateAnchorRotationReliability == null)
        return AnchorRotationUnreliableRoute.None;

    string playbackScope = BuildAnchorRotationHysteresisScope(state, phase);

    // Always evaluate the gate. Its result drives FX suppression even
    // when we render via shadow, and is the sole reason to fall back to
    // Hide when shadow coverage is unavailable.
    bool fxSuppress = false;
    AnchorRotationReliabilityDecision decision = default;
    if (flags.tryEvaluateAnchorRotationReliability(
            index, traj, playbackUT, playbackScope, out decision))
    {
        fxSuppress = decision.Unreliable;
    }

    // Tier 1: shadow render. Independent of gate.
    if (state != null
        && TryRouteAnchorRotationToShadow(
            index, traj, state, playbackUT, decision, fxSuppress, phase, playbackScope))
    {
        return AnchorRotationUnreliableRoute.ShadowPositioned;
    }

    // Tier 2: shadow not covering AND gate did not fire. Legacy runs.
    if (!fxSuppress)
        return AnchorRotationUnreliableRoute.None;

    // Tier 3: shadow not covering AND gate fired. Hide.
    HideAnchorRotationUnreliableState(...);
    // ... existing exit-watch + GhostRenderTrace.EmitGuardSkip emit ...
    return AnchorRotationUnreliableRoute.Hidden;
}
```

`TryRouteAnchorRotationToShadow` takes `fxSuppress` as a new parameter and writes it to `state.anchorRotationShadowRoutedThisFrame` (replacing today's unconditional `true`). The shadow-route log line gains `mode=gated|always` to distinguish steady-state shadow from real-tumble shadow.

`PositionLoopAtPlaybackUT` (`GhostPlaybackEngine.cs:3136`) now returns `AnchorRotationUnreliableRoute` instead of `void`, so the three loop / overlap call sites can derive their post-position trace surface from the route enum.

### 2. Plumb trace surface labels at the four post-position sites

The four `GhostRenderTrace.EmitPostUpdate` call sites pass an `activeSurface` argument derived from the route enum and the post-position retired flag, via a new pure helper `ResolveRenderSurface(route, retired)`:

| File | Line | Branch |
|---|---|---|
| `GhostPlaybackEngine.cs` | 1226 | `RenderInRangeGhost` non-loop |
| `GhostPlaybackEngine.cs` | 1838 | `UpdateLoopPlayback` primary loop |
| `GhostPlaybackEngine.cs` | 2109 | `UpdateOverlapPlayback` overlap-primary |
| `GhostPlaybackEngine.cs` | 2304 | `UpdateExpireAndPositionOverlaps` overlap-loop |

The non-loop site additionally changes its `AdjustFxFlagsForShadowRoute(... shadowRouted: anchorRotationShadowed)` call to read `shadowRouted: state.anchorRotationShadowRoutedThisFrame`, matching the three loop / overlap sites that already read the flag. Without this change, every steady-state shadow frame would suppress FX — the route enum is true whenever shadow was used, but FX should suppress only when the gate is also firing.

### 3. Trace observability

Two additions to `GhostRenderTrace.cs`:

- **`activeSurface` field on `EmitPostUpdate`** (`AfterUpdate` line): one of `legacy | shadow | hidden`. Lets a single log line attribute every frame to a path. Plumbed in via the same call-site changes already needed in §1.
- **`debrisBracketSeconds` on `BeginFrame`** (`FrameStart` line): `(afterUT - beforeUT)` from the resolved Relative section's debris-frame bracket at the playback UT. Default `0` if not Relative or not yet computed. Lets the wide-bracket pattern be searched directly in post-hoc analysis.

Both are surgical: existing log lines pick up two extra fields each; no new log line types.

### 4. Tests

#### Unit: `Source/Parsek.Tests/GhostPlaybackEngineTests.cs`

The success-path tests (gate inactive + shadow data → shadow render; gate active + shadow data → shadow render with FX suppression) need a real Unity GameObject because the engine's `state.ghost == null` short-circuit invokes Unity's overloaded `==` on real ghosts. xUnit can only express literal-null `state.ghost` values, which trips the short-circuit and bypasses the shadow positioner. Those tests live in `RuntimeTests.cs`.

xUnit tests cover the legacy / hide / no-predicate fallthrough branches and the surface-resolver helper:

1. **`AlwaysShadow_GateInactive_NoShadowData_FallsThroughToLegacy`** — Tier 2: older recording without `absoluteFrames`, gate inactive. Asserts shadow positioner not called; legacy `PositionLoop` invoked; FX not suppressed.
2. **`AlwaysShadow_GateActive_NoShadowData_FallsThroughToHide`** — Tier 3: no shadow data + gate fires. Asserts shadow positioner not called; legacy not run; `anchorRetiredThisFrame=true`; exit-watch event fires; the existing `anchor-rotation-unreliable` log line is asserted.
3. **`AlwaysShadow_NullGhostShortCircuit_GateInactive_FallsThroughToLegacy`** — engine's `state?.ghost == null` short-circuit pins: shadow attempt is bypassed; legacy runs.
4. **`AlwaysShadow_NotV12Debris_NoPredicate_FallsThroughToLegacy`** — `flags.tryEvaluateAnchorRotationReliability == null` (host predicate excluded the recording). Asserts shadow positioner not called even with `absoluteFrames` present; legacy runs.
5. **`AlwaysShadow_LiveAnchorRecording_ExcludedByPredicate`** — reviewer P1 regression: a recording with `IsDebris=true && DebrisParentRecordingId != null && LoopAnchorVesselId != 0` must be excluded by `ShouldEvaluateAnchorRotationReliabilityForTesting`. Symmetric positive case (no live anchor) verifies the predicate still admits the v12+ parent-anchored debris path.
6-9. **`ResolveRenderSurface_*`** — pure-helper coverage for the four-branch surface enum: `Shadow` regardless of retired, `Hidden` regardless of retired when route is Hidden, `Hidden` when route is None and retired, `Legacy` when route is None and not retired.

The mock `SpawnPrimingPositioner` from PR #800 already exposes `ShadowPositionCalls`, `PositionLoopCalls`, `InterpolateCalls`, `ShadowPositionShouldSucceed`, `PrimedShadowBracketBeforeUT/AfterUT`. No new mock fields needed.

#### In-game: `Source/Parsek/InGameTests/RuntimeTests.cs`

`TumblingParentDebris_ShadowRoute_KeepsGhostVisibleAndStable` from PR #800 covers the gate-active / `mode=gated` success path — same asserts continue to hold (the route still positions via shadow, the FX flag is still set true when the gate fires).

New sibling **`ParentAnchoredDebris_AlwaysShadow_StableThroughout`** covers the gate-inactive / `mode=always` success path: drives the engine with a recording that has covering `absoluteFrames` and a host predicate that returns `Unreliable=false`. Asserts mesh stays active, `state.anchorRotationShadowRoutedThisFrame == false` (FX not suppressed in steady state), `state.anchorRetiredThisFrame == false`, the shadow positioner ran exactly once, the legacy `PositionLoop` and `InterpolateAndPosition[Relative]` were not called, and the ghost transform reflects the shadow positioner's primed position end-to-end.

### 5. Documentation

- `CHANGELOG.md` — extend the v0.9.2 tumbling-parent entry: `Parent-anchored debris now plays back via the recorded absolute-position shadow track unconditionally during the recorded Relative section, eliminating the residual position pops at gate-engage and gate-release boundaries that remained after the smooth-route landing in the previous build. Recordings without shadow data fall back to the existing path.`
- `docs/dev/todo-and-known-bugs.md` — close Follow-up A and B (transition-blend, rotation-jitter) entries from the PR #800 plan and add a new entry for the surfaced "wide debris bracket at section tail" pattern as a recorder-side optimisation candidate (do not regress the always-shadow path on top of fixing it).
- `.claude/CLAUDE.md` and `AGENTS.md` — extend the Format-v7 paragraph to note the always-shadow rendering contract for v12+ parent-anchored debris: `Phase D contract is preserved (no live-anchor proxy); the shadow remains recording data and is the rendering source of truth whenever the recording's IsDebris && DebrisParentRecordingId != null AND the active Relative section's absoluteFrames covers the playback UT. The angular-rate gate (PR #793) is retained as the per-frame FX-suppression signal but no longer gates rendering. Hide remains available as the third-tier fallback when shadow is unavailable AND the gate is firing.`

## Risks and decisions made

**Risk 1: increased per-frame cost.** The shadow positioner runs on every parent-anchored-debris frame, not just inside tumble holds. Cost: one `InterpolateAndPosition` over `absoluteFrames` per such ghost per frame. Mitigation: this is the same call already made every frame today inside the legacy resolver path that the shadow replaces (the legacy resolver also walks `frames` and applies the offset transform). Net: comparable; the shadow lerp is cheaper than the slerp+lerp+matmul of the legacy path. No mitigation needed beyond confirming on-load.

**Risk 2: shadow path silently produces a different visual trajectory than legacy outside tumble windows.** Possible if recorder-side shadow sampling is misaligned. Verified in the playtest log: inside the smooth-route window every parent-anchored ghost showed `dM ≈ expectedDM` — shadow lerp tracks the recorded path correctly. The same shadow data is on disk for the entire section, so steady-state always-shadow renders the same trajectory the gate-shadow window already validated.

**Risk 3: FX-suppression semantic flip.** Pre-PR-#803 `state.anchorRotationShadowRoutedThisFrame` was set by `TryRouteAnchorRotationToShadow` to unconditional `true` when the gate fired AND shadow was used (the only path that set it). Post-PR-#803 it is set to the gate's `fxSuppress` bit only when shadow is used (route enum = `ShadowPositioned`); legacy and hide paths leave it at the per-frame `false` initialization. So the flag is `true` exactly when (shadow rendered AND gate fired) — the same boolean expression the four downstream FX-flag readers want. The non-loop reader at `GhostPlaybackEngine.cs:1247-1251` is migrated from the route enum (true on every shadow frame, including steady state — would over-suppress) to the flag, matching the three loop / overlap readers. Documented in the field's XML comment.

**Risk 4: legacy hide path becomes harder to exercise.** Hide is now reachable only when `absoluteFrames == null` AND the gate fires. The existing in-game test `TumblingParentDebris_ShadowRoute_KeepsGhostVisibleAndStable` covers the shadow-used branch; new unit test (3) above covers the legacy+hide fallback. Adequate.

**Risk 5: opus reviewer flagged that the previous router's `TryRouteAnchorRotationToShadow` set `state.anchorRotationShadowRoutedThisFrame = true` only when the gate fired, and several engine call sites depend on that flag.** Addressed above (§1, §2): the flag's setter moves to the engine call site and tracks the gate evaluator's result, not the shadow positioner's success — a deliberate semantic change reflected in the XML doc and in the unit tests' assertions.

## Out of scope

- Recording-side optimisation of thin-tail debris brackets. The 2.2 s gap at the tail of section 0 is a recorder optimiser artifact; tightening that would also help, but it is recording-format work and the always-shadow render path renders correct visuals regardless.
- Transition-blend between shadow and legacy at section boundaries (entering / exiting Relative coverage). With shadow now used uniformly inside Relative sections, the only remaining boundary is at section transitions — out of scope here, validated on first playtest, deferred to a follow-up if visible.
