# Design: Borrow-at-Launch / Repay-at-SOI-Exit Launch Alignment for Span>=Synodic Re-Aim Loops

Status: IMPLEMENTED (branch `launch-escape-seam-render`). Replaces the earlier flawed forward-launch-hold scheme.
Scope: render/playback-time only. Recorded data is IMMUTABLE; nothing here rewrites a `.prec` / `OrbitSegments` / any recording.
Failing class: mission `s15` / `Kerbal X #2`, recording `aa48920e` (an engaged re-aim loop, `cadence == span > synodic`, `DepartedFromHeliocentricPark == true`).

---

## 1. The seam

A re-aim loop replays a recorded interplanetary mission on a loop, re-solving the heliocentric transfer per loop so it still reaches the destination as the planets move. The recorded mission's leading legs are qualitatively different:

- The **launch ascent + Kerbin parking + escape** are all INSIDE the launch-body SOI. The body-fixed ascent is stored as lat/lon/alt and rendered every frame at the LIVE Kerbin rotation (`GhostTrajectoryPolylineRenderer.BuildLegVectorLine` -> `CelestialBody.GetWorldSurfacePosition`); the parking + escape are inertial conics frozen in their recorded orientation.
- The **heliocentric transfer** (and, for a heliocentric-park departure, the solar park + trans-target burn) is inertial / rotation-independent.

When the loop replays time-shifted, Kerbin has spun to a different orientation than at the recorded launch, so the body-fixed ascent renders rotated by the accumulated spin (observed 82-220 deg for s15) away from the frozen inertial escape conic, which did not rotate. The launch->escape handoff opens a visible map/tracking-station gap and the icon jumps across it at high warp.

This appears only when `cadence != synodic`. `PadAlignLaunch` (the existing `cadence == synodic` fix) snaps the global phase anchor to a whole sidereal day, but it deliberately bails when `cadence != synodic` because it cannot quantize the loop period to whole days without shifting which synodic window each loop catches (which would make the transfer miss the destination). This design is the complement for the regime PadAlignLaunch declines, gated on `!pad.Applied`.

---

## 2. The model: borrow at launch, repay at the SOI exit

For each loop N, the launch happens `delta_N` EARLIER than its nominal time `L_N = phaseAnchorUT + N*cadence`, then `delta_N` is repaid as a coast hold at the Kerbin-SOI-exit boundary. Net: the in-SOI replay is rotation-aligned (seam closed) and everything from the SOI exit onward is on the baseline `L_N` schedule, byte-for-byte unchanged (targeting + the Duna arrival hold untouched). No pad absence: the ghost briefly coasts at the SOI boundary in space.

### 2.1 delta_N (backward residual)

`GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds`:

```
T_sid = abs(launchBodyRotationPeriod)
Off_N = (phaseAnchorUT - spanStartUT) + N * cadence
delta_N = (Off_N mod T_sid + T_sid) mod T_sid          // in [0, T_sid)
```

Degenerate `T_sid` (NaN/Inf/<=0) or NaN anchor/spanStart -> 0 (no alignment), mirroring `ComputeArrivalAlignHoldSeconds`. It is a sawtooth in N bounded to `[0, T_sid)`, never growing with loop index, like `ComputePerLoopArrivalHoldSeconds`.

### 2.2 Alignment proof

Launch at `L_N - delta_N`. During the in-SOI replay the ghost renders recorded UT `recUT = spanStartUT + (currentUT - (L_N - delta_N))`. The body-fixed ascent placed via `GetWorldSurfacePosition` at the live rotation of `currentUT` coincides with the recorded orientation iff `currentUT - recUT` is a whole multiple of `T_sid`:

```
currentUT - recUT = (L_N - delta_N) - spanStartUT
                  = Off_N - delta_N
                  = Off_N - (Off_N mod T_sid)
                  = T_sid * floor(Off_N / T_sid)          // a whole multiple of T_sid
```

Constant across the whole in-SOI replay, so the body-fixed ascent/parking/escape coincide with the frozen inertial escape conic. The seam closes. This is pinned to the live-vs-recorded launch displacement `(phaseAnchorUT - spanStartUT) == d0 - recordedDepartureUT` (`ReaimWindowPlanner.cs:124`), the same quantity PadAlignLaunch snaps.

### 2.3 The SOI-exit UT

`ReaimClassifier.RecordedSoiExitUT = segs[helioIdx].startUT` (the start of the first common-ancestor / Sun segment, i.e. the launch-body SOI exit). For the s15 heliocentric-parking departure this is BEFORE `RecordedDepartureUT` (the trans-target burn from the solar park): the park + its burn are inertial / rotation-independent and lie AFTER the SOI exit, so the repay belongs at the SOI exit, not the departure burn. For a direct transfer the two coincide.

The builder gate requires `RecordedSoiExitUT` finite and strictly inside `(spanStartUT, RecordedArrivalUT)`; otherwise the launch alignment does not engage (faithful in-SOI render, seam remains).

---

## 3. Span-clock model (`TryComputeSpanLoopUT`)

Each cadence-cycle window `[0, cadence)` renders ONE instance launched `delta` earlier than its nominal `L` boundary. The early launch borrows `delta` from the PREVIOUS cycle's idle tail (for span>=synodic with loiter compression, `compressedSpan << cadence`, leaving a large parked idle tail per cycle). Two sub-regions, by `phaseInCycle` (raw cadence-clock phase, `cycleIndex = N`):

- **Region A** (`phaseInCycle < cadence - advNext`): render THIS cycle's instance N, launched at `L_N - delta_N` (in the prior window's tail). `phaseFromLaunch = phaseInCycle + delta_N`, so at `phaseInCycle == 0` the replay is already `delta_N` into flight, continuous with the prior window's region B for this same instance. The SOI-exit hold inserts `delta_N`.
- **Region B** (`phaseInCycle >= cadence - advNext`): render the NEXT instance (N+1) launched EARLY at `phaseInCycle = cadence - advNext` (borrowing `advNext = delta_{N+1}` from this cycle's parked tail). `phaseFromLaunch = phaseInCycle - (cadence - advNext)` (0 at the early launch); report `cycleIndex = N+1`; the SOI-exit hold inserts `advNext`. This makes the navigable launch time `L_{N+1} - delta_{N+1}` actually render the launch (at `loopUT == spanStart`, on the pad), with no pad absence.

The two regions hand off continuously at the cycle boundary (region B of cycle N at `phaseInCycle -> cadence` gives `phaseFromLaunch -> advNext... ` wait, gives `phaseFromLaunch -> delta_{N+1}` matching region A of cycle N+1 at `phaseInCycle = 0`).

### 3.1 Composition (two holds, one axis)

`effectiveSpan = compressedSpan + delta + hold`. The two holds are sequential insertions on one phase axis, applied in boundary order:

```
clampedPhase = min(phaseFromLaunch, effectiveSpan)
afterSoi     = ApplyArrivalHoldToPhase(clampedPhase, soiExitPhasePos, delta)   // repay
cutPhase     = ApplyArrivalHoldToPhase(afterSoi,      holdPhasePos,    hold)   // Duna arrival hold (unchanged)
loopUT       = DecompressSpanUT(spanStartUT + cutPhase, loiterCuts)
```

`soiExitPhasePos = CompressSpanUT(soiExitAtUT, loiterCuts) - spanStartUT`. The Duna `holdPhasePos` is measured in recorded/compressed-span phase and is unchanged by the SOI-exit insertion that lies strictly before it, so feeding `afterSoi` into the arrival hold at its unchanged position composes correctly. Both holds reduce to the identity when their hold is 0 / position is NaN, so a unit with neither is byte-identical to the pre-hold clock.

**Repay nets to zero.** Past the SOI-exit hold in region A, `loopUT = spanStart + (phaseFromLaunch - delta) = spanStart + phaseInCycle` = the BASELINE (not-engaged) loopUT at the same `currentUT`. So the heliocentric transfer, trans-target burn, Duna arrival, and arrival hold all play at baseline timing.

### 3.2 No (W_N - H_N) subtraction

Because the post-SOI portion plays at baseline timing, the SOI entry occurs at the same live UT as baseline, so the per-loop arrival hold `W_N` aligns the destination rotation phase correctly with NO compensation. The earlier forward-shift's `(W_N - H_N)` subtraction is removed.

### 3.3 The clamp / delta > slack edge

For replayed loop N the launching cycle is N-1 and `slack_{N-1} = cadence - compressedSpan - W_{N-1}` (the LAUNCHING cycle's inter-cycle idle gap, using the PREVIOUS instance's per-loop arrival hold). The advance is capped to that launching-cycle slack FIRST. This IS the `delta_N > slack_{N-1}` edge handling:

- When `delta_N <= slack_{N-1}` (the common case, e.g. s15 N=13: delta=5058 <= slack=5419), the early launch starts within the previous instance's idle tail (already parked at spanEnd), so there is no overlap with the previous instance's live replay - renders perfectly.
- When `delta_N > slack_{N-1}` (the zero-slack loops, where the per-loop destination arrival hold `W_{N-1}` fills the launching cycle), capping to slack used to shorten the borrow, leaving a residual rotation offset (`<= (T_sid - slack)/T_sid` of a turn) only on those loops, plus a one-shot WARN. **This residual is now closed by the BOUNDARY-OVERLAP launch render (§3.5).** The cap helper `ComputeCappedLaunchAdvanceSeconds` is kept (the diagnostic `residualDeg` reads it), but the clock and the Missions "Warp to..." display now read `ComputeBoundaryOverlapAdvanceSeconds`, which uses the FULL raw delta on the loops where the cap would have bitten and emits a secondary ghost so the early launch can render concurrently with the still-arriving previous instance.

### 3.5 Boundary-overlap launch render (the zero-slack resolution)

The borrow-at-launch / repay-at-SOI-exit model above closes the seam on loops with spare idle slack, but on ZERO-SLACK loops (where the per-loop destination arrival hold fills the launching cycle, `slack_{N-1} == 0`) the cap truncated the launch advance to 0 and the seam stayed open (`residualDeg` up to ~211 on s15 loop 19). The boundary-overlap launch render (`docs/dev/plan-launch-boundary-overlap.md`, Design B) closes it by rendering the early-launching NEXT instance as a SECONDARY ghost during the borrow window, so the launch can realign without being capped — the previous (primary) instance is far downstream near the destination by then, so the two ghosts sit at different places (no overlap with the previous instance's live replay). Render/playback-time only; recorded data is IMMUTABLE.

- **Gated, not blanket-uncapped.** `GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(...)` returns the OLD capped advance whenever `rawDelta <= cappedDelta` (the already-aligned slack&gt;0 loops, BYTE-IDENTICAL to before — no secondary), and the FULL raw delta only when `rawDelta > cappedDelta` (the residual-seam loops). The launch realigns fully (`residualDeg ~ 0`) on exactly the loops that needed it; every already-aligned loop and the targeting path stay byte-identical.
- **Dual-clock frame.** `GhostPlaybackLogic.ComputeSpanLoopFrame` returns ONE primary loopUT (the continuing instance N, region-A semantics for the whole cycle — the long-lived through-line the camera follows) plus an OPTIONAL secondary (instance N+1's early in-SOI launch) present only inside the borrow window AND only when the boundary overlap engages. `TryComputeSpanLoopUT` is the primary-only wrapper, so every existing caller is byte-identical. On an already-aligned loop the OLD single-output region-B early-launch flip is preserved (no secondary); on an engaged loop the primary stays on N and the secondary carries N+1 (so the camera is never yanked onto the fresh launch). The secondary of cycle N hands off seamlessly to the primary of cycle N+1 (same instance, same loopUT).
- **Borrowed storage, never the self-overlap machinery.** The engine positions the secondary in `overlapGhosts[i]` STORAGE via a dedicated `UpdateBoundaryOverlapSecondary` (audio-muted, flagged `isBoundaryOverlapSecondary`, demoted-shell lifecycle), NEVER `UpdateOverlapPlayback` / the overlap expiry block (`OnOverlapCameraAction` / `OnOverlapExpired` never fire from it). The map presence layer creates the secondary's ProtoVessel as an `overlapInstanceVessels[(recIdx, N+1)]` entry INSIDE `RunOverlapPerInstanceSweep` (so the reaper spares it during the borrow window and tears it down when it ends); its in-SOI escape conic resolves to a recorded Segment source, so it is loop-shift driven exactly like an overlap instance. The polyline renderer draws the secondary's in-SOI ascent leg as an additive non-ownership second head keyed on the secondary pid. The watch camera EXCLUDES any `isBoundaryOverlapSecondary` state at every selection site.
- Rejected alternatives (still rejected): a bounded SELF-overlap with the previous instance (would re-entangle targeting; the self-overlap path is OFF for re-aim), and a minimal-magnitude nearest-`T_sid` forward shift (reintroduces the pad-absence + arrival-budget problems). The remaining genuinely-uncloseable case (`rawDelta` itself exceeds what the boundary overlap can use — impossible for re-aim now) keeps the one-shot WARN as a defensive diagnostic.

**Unified capped advance (the SINGLE source of truth).** `GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(phaseAnchorUT, spanStartUT, spanEndUT, cadence, win, T_sid, loiterCuts, arrivalHoldSeconds, arrivalAlignPeriod)` returns `min(delta_win, slack_{win-1})`, mirroring `TryComputeSpanLoopUT`'s slack/clamp arithmetic EXACTLY (`compressedSpan`, the `W_{win-1}` clamp `if (W>0 && compressedSpan+W > cadence) W = max(0, cadence - compressedSpan)`, `slack = max(0, cadence - compressedSpan - W_{win-1})`). All three readers go through it so they agree on the SAME instance's advance:

- region B caps instance N+1 (launching in cycle N) to `slack_N` (= `slack_{(N+1)-1}`);
- region A caps the SAME instance N (launching in cycle N-1) to `slack_{N-1}` — NOT `slack_N`. This is the internal-consistency fix: region A and region B for one instance now use the SAME capped advance (the launching cycle's slack), so the cycle boundary is continuous even when `W_N != W_{N-1}` makes `slack_N != slack_{N-1}`. (Previously region A capped instance N to `slack_N`, so when the two slacks differed and `delta_N` exceeded them differently, the region-A render and the region-B launch instant used different advances — a discontinuity at the boundary.)
- `MissionsWindowUI.ComputeNextRelaunchUT` uses it for window n (and the fall-forward to n+1), so the navigable launch time matches the clock's actual launch instant.

### 3.4 Boundary rollback

The non-aligned clock's epsilon-tolerant boundary rollback (show the prior cycle's spanEnd at `phaseInCycle ~ 0`) is SKIPPED when the launch alignment is engaged: under borrow-repay the boundary is already continuous (region B of the prior window hands off to region A of this window at `phaseFromLaunch == delta`), and a rollback would wrongly show the prior instance's landed frame.

---

## 4. Targeting safety

The resolver computes `windowIndex` from `TryComputeSpanLoopUT(..., schedule:null)` with no launch-alignment args (the launch alignment defaults to not engaged there), so `windowIndex = floor((currentUT - phaseAnchorUT)/cadence)` is unchanged. The launch alignment never touches `phaseAnchorUT`, `cadence`, `FirstDepartureUT`, `SynodicPeriodSeconds`, or `windowIndex`. The heliocentric transfer is solved at `DepartureUTForWindow(windowIndex)` and the post-SOI replay plays at baseline timing, so the ghost traverses that frozen arc at the same live UT as baseline. Targeting is byte-identical.

---

## 5. Touch points

- `ReaimClassifier.cs` - `ReaimMissionPlan.RecordedSoiExitUT`, populated `= segs[helioIdx].startUT`.
- `GhostPlaybackLogic.cs`:
  - `ComputePerLoopLaunchAdvanceSeconds` (renamed from `ComputePerLoopLaunchHoldSeconds`; backward residual, uncapped delta_N).
  - `ComputeCappedLaunchAdvanceSeconds` (the SINGLE source of truth for the capped advance `min(delta_win, slack_{win-1})`; used by region A, region B, and `ComputeNextRelaunchUT`).
  - `LoopUnit.RecordedSoiExitUT` field.
  - `TryComputeSpanLoopUT` (new `soiExitAtUT` param; borrow-repay regions A/B; SOI-exit repay hold; BOTH regions cap their rendered instance's advance via `ComputeCappedLaunchAdvanceSeconds` to the LAUNCHING cycle's slack — region A instance N to slack_{N-1}, region B instance N+1 to slack_N — so they agree on the same instance; arrival hold unchanged; boundary rollback skipped when aligned; per-loop launch-advance Verbose + capped Warn, capped flag = `delta_win > slack_{win-1}`).
    - **Observability (seam quantification):** the per-loop launch-advance Verbose line and the capped Warn line both carry `rawDeltaN=<uncapped delta for the rendered instance>s residualDeg=<seam angle>`. `residualDeg = ((rawDeltaN - cappedAdvance) mod T_sid) / T_sid * 360`, normalized to `[0,360)` — the leftover launch-body rotation the cap could NOT align (the body-fixed-ascent vs inertial-escape-conic SEAM ANGLE this loop carries: 0 when not capped, large when capped). So a playtest reads, per capped loop, exactly how many degrees of seam remain. A region-B early launch ALSO emits a rate-limited (own per-mission key) Verbose `Reaim` `launch instant:` line naming the rendered instance's ACTUAL launch UT (`L_N - effectiveLaunchAdvance`) alongside the nominal `L_N` and the advance, so the Missions warp-to target can be compared against the real launch UT.
  - `DecideUnitMemberRender` (new `soiExitAtUT` param; `HiddenPreLaunchHold` removed - no pad absence).
  - `UnitMemberRenderDecision` (`HiddenPreLaunchHold` value removed).
  - `ResolveTrackingStationSampleUT` (forwards `unit.RecordedSoiExitUT`).
- `MissionLoopUnitBuilder.cs` - gate `!pad.Applied && plan.Supported && soiExitValid`; carries `launchHoldSoiExitUT`; Info line; ctor arg.
- `GhostPlaybackEngine.cs` - `UpdateUnitMemberPlayback`, the watch-clock resolver, and the loop-synced-debris parent clock forward `RecordedSoiExitUT`; `HiddenPreLaunchHold` engine branch + the watch-clock pre-launch-absence early-return removed.
- `ParsekKSC.cs` - `UpdateUnitMemberPlayback` forwards `RecordedSoiExitUT`; `HiddenPreLaunchHold` branch removed.
- `UI/MissionsWindowUI.cs` - `ComputeNextRelaunchUT` subtracts the CAPPED advance (`ComputeCappedLaunchAdvanceSeconds`, the same value the clock's region B launches at), so it returns the ACTUAL launch instant `L_N - capped_delta_N` (falls forward to window n+1 with the capped helper if the advanced launch already passed). It does NOT subtract a warp lead: the single 15 s warp lead is applied once at the warp site by `TimeJumpManager.ApplyJumpLead(relaunchUT, ...)` in `ShowMissionWarpToWindowConfirmation`, so the warp lands the player on the pad ~15 s before lift-off. Previously `ComputeNextRelaunchUT` ALSO subtracted `LaunchWarpLeadSeconds` (15 s) before `ApplyJumpLead` ran, double-leading the warp ~30 s early; the const was removed. (Earlier still it subtracted the UNCAPPED `delta_N`, which pointed a few hours too early when `delta_N > slack_{N-1}`.)
- `Display/GhostTrajectoryPolylineRenderer.cs` - no code change; honors the span clock via `ResolveTrackingStationSampleUT`, so the launch leg enters its window at `L_N - delta_N` and renders the in-SOI line at the aligned rotation.

`PadAlignLaunch` is unchanged and STAYS for `cadence == synodic`. `bodyFixedShift` is untouched (re-aim units set `relaunchSchedule = null`, so it is already 0).

---

## 6. Tests

- `LaunchHoldTests` - pure `ComputePerLoopLaunchAdvanceSeconds`: `(Off_N - delta_N)` aligns mod T_sid, range `[0,T_sid)`, already-aligned->0, degenerate/NaN->0, sawtooth `delta_{N+1} - delta_N == cadence mod T_sid`. Also `ComputeCappedLaunchAdvanceSeconds`: returns the raw delta under slack, clamps to slack when over, uses the LAUNCHING cycle's hold `W_{win-1}` and the compressed span (loiter cut), degenerate/NaN period and displacement -> 0.
- `LaunchHoldClockTests` - in-SOI rotation alignment through ascent/parking/escape (region A + region B), SOI-exit repay coast hold (held at the SOI-exit UT), post-SOI-and-onward byte-identical to baseline (the repay nets to zero), no pad absence (early launch at spanStart), targeting + cadence==synodic / not-engaged byte-identical, arrival hold unchanged, delta>slack capped, TS-sampler parity, AND region-A/B continuity: the loopUT is continuous at every cycle boundary whose launching cycle has a positive advance (both regions cap the same instance to the same launching-cycle slack), including the per-loop-varying-slack case.
- `LaunchHoldLoggingTests` - the per-loop Verbose `Reaim` launch-advance line fires with non-zero `delta_N` across two cycles (propagation proof); builder Info-line / ctor-wiring source-text gate.
- `GhostPlaybackEngineTests` - watch<->render<->TS clock parity: early launch at spanStart, post-SOI baseline, never absent below span.
- `MissionsWindowPeriodicityDisplayTests` - next-relaunch reports the ACTUAL launch instant `L_N - capped_delta_N` (no lead subtracted); the KEY integration test drives the actual `TryComputeSpanLoopUT` across a synthetic launch-aligned `cadence==span` unit (loiter cut + arrival hold so slack varies), finds the real region-B launch UT of an instance, and asserts `ComputeNextRelaunchUT(unit, before) == real_launch` for both an uncapped and a capped loop. The uncapped test also asserts `TimeJumpManager.ApplyJumpLead(ComputeNextRelaunchUT(...), now) == real_launch - 15s`, proving the single warp lead lives at the warp site.
- `ReaimClassifierTests` - `RecordedSoiExitUT` precedes `RecordedDepartureUT` for the s15 heliocentric-park chain.

---

## 7. In-game validation (s15 / Kerbal X #2, recording aa48920e), high warp, multiple loops

1. Seam collapses: launch->escape gap drops from 82-220 deg to ~0-3 deg.
2. Launch starts on the real pad, EARLIER than the nominal window. `ComputeNextRelaunchUT` returns the ACTUAL launch instant `L_N - ComputeBoundaryOverlapAdvanceSeconds(...)` (the FULL raw advance on the engaged zero-slack loops, the old capped advance on the aligned loops); the warp site applies a SINGLE 15 s lead via `ApplyJumpLead`, so warping forward lands on the pad ~15 s before lift-off. The `launch instant:` Verbose line names the real launch UT to compare against.
3. No pad absence: the ghost is never absent on the pad before launch.
4. SOI-exit coast hold visible: the ghost briefly holds at the SOI boundary, then continues on the heliocentric transfer at baseline timing.
5. Targeting unchanged: the transfer resolves for consecutive `windowIndex`; departure UT byte-identical to baseline.
6. Duna arrival hold unchanged: the deorbit lands at the recorded rotation phase, with no `Hlaunch` term in the per-loop arrival-hold line.
7. Flight + map + TS agree; no icon teleport at the now-closed launch->escape boundary.
8. `delta > slack` (the zero-slack loops, e.g. s15 loop 19 previously ~211 deg): the BOUNDARY-OVERLAP launch render now closes these. The per-loop Verbose line carries `boundaryOverlap=True secondaryActive=True residualDeg~0` on the engaged loops, the one-shot `boundary-overlap engaged` line fires, and the `launch-advance-capped` WARN no longer fires (the seam closes). During the borrow window TWO trajectories render: the previous instance arriving at/near Duna (primary, the watched through-line) and the new instance launching from Kerbin (secondary), each with its own map icon + trajectory; no flicker at the boundary handoff. Already-aligned (slack>0) loops show exactly one ghost / one map icon / no second head (byte-identical to before).

---

## 8. Open / residual

- **Seam-bridge near-meet artifact (resolved, render polish).** Closing the launch->escape seam revealed a spurious extra map/TS segment: the seam bridge. The bridge draws a fixed ~74 deg conic merge slice (`BridgeMergeSampleCount=60`) whose off-chord bulge is ~200-370 km regardless of the gap, so once the aligned ascent end lands within a few km of the escape conic (logged seam angles 0.31-4.59 deg, a ~3-54 km chord at Kerbin radius) it drew a disproportionate arc beside the correct trajectory. The pre-alignment launch leg sat 82-220 deg from the conic, so the 45 deg `BridgeMaxAngleRadians` gate skipped it; alignment moved it into the bridge's draw window. Fix: raise `BridgeMinAngleRadians` from 1e-4 rad (~0.006 deg, only a perfectly degenerate seam) to 5 deg (`GhostTrajectoryPolylineRenderer.cs`), so a near-meet handoff (the leg already meets the conic) skips the bridge and the leg and conic meet directly. The 5-45 deg moderate-misalignment range the bridge is designed for is preserved. A new `bridge-nearmeet` Verbose skip line ("leg already meets conic") names the angle for playtest confirmation. Tested in `GhostTrajectoryPolylineBuildTests.SeamBridge_MinAngleGate_NearMeetSkips_RealGapBridges`. **Correction (intervening-leg fix below):** the 26.77 deg bridge on `8538d9e1` cited here as "a genuine ~300 km gap that still draws" was itself the launch-shortcut BUG, not a legitimate gap; see the next bullet.
- **Intervening-ascent-leg shortcut (resolved, render polish).** This launch is recorded across TWO consecutive body-fixed legs: the launchpad ascent `8538d9e1` (treeOrder 0, ~0-70 km) and its continuation `aa48920e` (70 km -> parking -> escape). After the alignment work the continuation leg `aa48920e` meets the escape conic directly (its bridge skips via the 5 deg near-meet gate), but the PAD-ascent leg `8538d9e1` still armed a bridge at its end (angle ~26.7 deg, chord dev ~372 km) straight to the escape conic, shortcutting OVER the continuation leg that actually connects them - a redundant curved segment whose unwound lead-in rotated the conic relative to the bridge. Fix: in `DecideSeamBridges`, after the adjacent conic is selected for a candidate leg-end, the new pure `HasInterveningContinuationLeg` scans the chain-run bridge candidates (`bridgeLegScratch`, projected to `BridgeLegSpan`) for a same-body body-fixed leg whose START lies at/after this leg-end (1 s shared-boundary slack, `BridgeSeamSharedBoundaryToleranceSeconds`) and strictly before the conic's startUT; if one exists, this leg is an intermediate ascent leg, not the conic's immediate predecessor, so the bridge SKIPS (the continuation leg draws its own handoff, resolved by the near-meet gate). Mirrored on the start side (a conic feeding a leg preceded by another body-fixed leg). Immediate leg->conic bridges (no intervening leg) are unaffected. New `bridge-interveningleg` Verbose skip line ("intermediate ascent leg - continuation leg ... precedes the conic") names the intervening leg id + UTs. Tested in `GhostTrajectoryPolylineBuildTests.HasInterveningContinuationLeg_*` (launch-ascent chain skip, single-leg-then-conic preserved, different-body, start-side symmetry, null/empty).
- **spanStartUT == first body-fixed ascent sample.** The alignment is pinned to the instant the clock maps to `spanStartUT`. If aa48920e has pre-launch pad-sit idle frames before the first ascent sample, the target should be the first ascent point. Confirm in the s15 playtest.
- **Cross-SOI heliocentric-transfer encounter seam.** This design closes the body-fixed-ascent vs inertial-escape ROTATION seam only; it does not address the separate cross-SOI transfer encounter seam (the deferred patched-conic-chain rework). Confirm whether that residual is now the dominant remaining artifact.
- **No alignment-off setting.** Like PadAlignLaunch (no toggle), the launch alignment runs whenever re-aim is engaged AND `!pad.Applied` AND a valid SOI-exit boundary exists. Deferred.

---

## 9. Seam-render observability (the "visible launch is a few minutes off the countdown T-0" investigation)

With boundary-overlap engaged (zero-slack loops, e.g. s15 / Kerbal X #2 / aa48920e) the alignment is correct (`residualDeg ~ 0`) and the Missions warp-to / next-launch UT equals the clock launch instant, yet the VISIBLE launch (the secondary ghost icon + escape conic + ascent polyline appearing) is reported a few minutes off the countdown's T-0. Four rate-limited diagnostic lines (logging-only, ZERO behavior change) let the next playtest measure the gap exactly. Leading hypothesis: the secondary's MAP presence (icon + escape conic) is created via `CreateOverlapInstanceVessel`, which needs an accepted `OrbitSegment` source; the first recorded Kerbin `OrbitSegment` starts ~274 s (~4.6 min) AFTER `spanStart`, so the pre-Segment ascent window has no Segment and the icon/conic materializes that late even though the body-fixed ascent polyline can draw from `spanStart`.

Grep handles (all `[Parsek][VERBOSE]`, rate-limited per the keys noted):

1. **Secondary clock-launch** (the authoritative "the launch should be visible NOW" timestamp). Tag `Reaim`, `GhostPlaybackLogic.ComputeSpanLoopFrame` (in the `hasSecondary` diagnostics block), key `boundary-overlap-secondary-clock-launch.<phaseAnchorUT>.<spanStartUT>`:
   `boundary-overlap secondary clock-launch: secondaryCycle=<N+1> currentUT=<live> secondaryLoopUT=<loopUT> spanStart=<spanStart>`
2. **Secondary map-presence first-create** (the icon/conic create truth, INCLUDING the create-returned-null / no-accepted-source smoking-gun case). Tag `GhostMap`, `GhostMapPresence.TryEnsureBoundaryOverlapSecondaryInstance` (after `CreateOverlapInstanceVessel`), key `boundary-overlap-secondary-map-presence-<recIdx>-<secondaryCycle>`:
   `boundary-overlap secondary map-presence: created=<bool> currentUT=<live> secondaryLoopUT=<loopUT> source=<Segment|StateVector|None|...> segmentUT=<a-b or n/a> lagFromSpanStartSec=<secondaryLoopUT - spanStart> rec=#<i> "<name>" cycle=<N+1>`
   `created=false source=None` with a large `lagFromSpanStartSec` is the smoking gun (pre-Segment gap). `segmentUT` names the covering Segment span when one resolved.
3. **Secondary polyline first-draw** (whether the ascent LINE appears at the clock launch while the icon/conic lags). Tag `GhostMap`, `GhostTrajectoryPolylineRenderer.Driver` (second-head pass), key `boundary-overlap-secondary-polyline-first-draw.<recordingId>`:
   `boundary-overlap secondary polyline first-draw: currentUT=<live> headUT=<secondary head UT> secondaryLoopUT=<loopUT> rec=<id> secondaryCycle=<N+1> leg=<li>`
4. **Missions T-minus cell target** (confirms the displayed countdown targets the same advanced launch UT as `ComputeNextRelaunchUT` / the warp). Tag `Mission`, `MissionsWindowUI.BuildMissionPeriodicityDisplay` (after the unit is resolved), key `missions-tminus-cell.<phaseAnchorUT>.<spanStartUT>`:
   `missions T-minus cell: targetUT=<nextRelaunchUT> now=<now> tMinusSec=<target-now>`

Reading the gap: compare (1) `currentUT` (launch should be visible) against (2) `currentUT` at the first `created=true` (icon/conic actually appears) and (3) `currentUT` at the first polyline draw (ascent line appears). If (3) ~ (1) but (2) lags by minutes with `created=false source=None` lines in between, the pre-Segment map-presence gap is confirmed. (4) rules out a countdown-vs-warp mismatch.
