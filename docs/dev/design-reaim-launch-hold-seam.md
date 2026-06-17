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

`slack = cadence - compressedSpan - hold` (the previous instance's inter-cycle idle gap). The defense-in-depth `effectiveSpan <= cadence` clamp caps `delta` to `slack` FIRST. This IS the `delta > slack` edge handling:

- When `delta <= slack` (the common case, e.g. s15 N=13: delta=5058 <= slack=5419), the early launch starts within the previous instance's idle tail (already parked at spanEnd), so there is no overlap with the previous instance's live replay - renders perfectly.
- When `delta > slack` (rare), capping to slack shortens the borrow, leaving a residual rotation offset (`<= (T_sid - slack)/T_sid` of a turn) only on those loops, plus a one-shot WARN. Capping cannot reopen the seam the way the old forward `H_N` truncation did (the advance is bounded BY slack). Rejected alternatives: a bounded overlap with the previous instance (would resurrect a second live instance and re-entangle targeting; the overlap path is OFF for re-aim), and a minimal-magnitude nearest-`T_sid` shift (a forward shift reintroduces the pad-absence + arrival-budget problems).

### 3.4 Boundary rollback

The non-aligned clock's epsilon-tolerant boundary rollback (show the prior cycle's spanEnd at `phaseInCycle ~ 0`) is SKIPPED when the launch alignment is engaged: under borrow-repay the boundary is already continuous (region B of the prior window hands off to region A of this window at `phaseFromLaunch == delta`), and a rollback would wrongly show the prior instance's landed frame.

---

## 4. Targeting safety

The resolver computes `windowIndex` from `TryComputeSpanLoopUT(..., schedule:null)` with no launch-alignment args (the launch alignment defaults to not engaged there), so `windowIndex = floor((currentUT - phaseAnchorUT)/cadence)` is unchanged. The launch alignment never touches `phaseAnchorUT`, `cadence`, `FirstDepartureUT`, `SynodicPeriodSeconds`, or `windowIndex`. The heliocentric transfer is solved at `DepartureUTForWindow(windowIndex)` and the post-SOI replay plays at baseline timing, so the ghost traverses that frozen arc at the same live UT as baseline. Targeting is byte-identical.

---

## 5. Touch points

- `ReaimClassifier.cs` - `ReaimMissionPlan.RecordedSoiExitUT`, populated `= segs[helioIdx].startUT`.
- `GhostPlaybackLogic.cs`:
  - `ComputePerLoopLaunchAdvanceSeconds` (renamed from `ComputePerLoopLaunchHoldSeconds`; backward residual).
  - `LoopUnit.RecordedSoiExitUT` field.
  - `TryComputeSpanLoopUT` (new `soiExitAtUT` param; borrow-repay regions A/B; SOI-exit repay hold; clamp caps delta first; arrival hold unchanged; boundary rollback skipped when aligned; per-loop launch-advance Verbose + capped Warn).
  - `DecideUnitMemberRender` (new `soiExitAtUT` param; `HiddenPreLaunchHold` removed - no pad absence).
  - `UnitMemberRenderDecision` (`HiddenPreLaunchHold` value removed).
  - `ResolveTrackingStationSampleUT` (forwards `unit.RecordedSoiExitUT`).
- `MissionLoopUnitBuilder.cs` - gate `!pad.Applied && plan.Supported && soiExitValid`; carries `launchHoldSoiExitUT`; Info line; ctor arg.
- `GhostPlaybackEngine.cs` - `UpdateUnitMemberPlayback`, the watch-clock resolver, and the loop-synced-debris parent clock forward `RecordedSoiExitUT`; `HiddenPreLaunchHold` engine branch + the watch-clock pre-launch-absence early-return removed.
- `ParsekKSC.cs` - `UpdateUnitMemberPlayback` forwards `RecordedSoiExitUT`; `HiddenPreLaunchHold` branch removed.
- `UI/MissionsWindowUI.cs` - `ComputeNextRelaunchUT` subtracts `delta` so the navigable launch time reads `L_N - delta_N` (falls forward to window n+1 if the advanced launch already passed).
- `Display/GhostTrajectoryPolylineRenderer.cs` - no code change; honors the span clock via `ResolveTrackingStationSampleUT`, so the launch leg enters its window at `L_N - delta_N` and renders the in-SOI line at the aligned rotation.

`PadAlignLaunch` is unchanged and STAYS for `cadence == synodic`. `bodyFixedShift` is untouched (re-aim units set `relaunchSchedule = null`, so it is already 0).

---

## 6. Tests

- `LaunchHoldTests` - pure `ComputePerLoopLaunchAdvanceSeconds`: `(Off_N - delta_N)` aligns mod T_sid, range `[0,T_sid)`, already-aligned->0, degenerate/NaN->0, sawtooth `delta_{N+1} - delta_N == cadence mod T_sid`.
- `LaunchHoldClockTests` - in-SOI rotation alignment through ascent/parking/escape (region A + region B), SOI-exit repay coast hold (held at the SOI-exit UT), post-SOI-and-onward byte-identical to baseline (the repay nets to zero), no pad absence (early launch at spanStart), targeting + cadence==synodic / not-engaged byte-identical, arrival hold unchanged, delta>slack capped, TS-sampler parity.
- `LaunchHoldLoggingTests` - the per-loop Verbose `Reaim` launch-advance line fires with non-zero `delta_N` across two cycles (propagation proof); builder Info-line / ctor-wiring source-text gate.
- `GhostPlaybackEngineTests` - watch<->render<->TS clock parity: early launch at spanStart, post-SOI baseline, never absent below span.
- `MissionsWindowPeriodicityDisplayTests` - next-relaunch reports `L_N - delta_N`.
- `ReaimClassifierTests` - `RecordedSoiExitUT` precedes `RecordedDepartureUT` for the s15 heliocentric-park chain.

---

## 7. In-game validation (s15 / Kerbal X #2, recording aa48920e), high warp, multiple loops

1. Seam collapses: launch->escape gap drops from 82-220 deg to ~0-3 deg.
2. Launch starts on the real pad, EARLIER than the nominal window (the navigable / "Warp to..." UT is `L_N - delta_N`).
3. No pad absence: the ghost is never absent on the pad before launch.
4. SOI-exit coast hold visible: the ghost briefly holds at the SOI boundary, then continues on the heliocentric transfer at baseline timing.
5. Targeting unchanged: the transfer resolves for consecutive `windowIndex`; departure UT byte-identical to baseline.
6. Duna arrival hold unchanged: the deorbit lands at the recorded rotation phase, with no `Hlaunch` term in the per-loop arrival-hold line.
7. Flight + map + TS agree; no icon teleport at the now-closed launch->escape boundary.
8. `delta > slack` (if it occurs): the capped-advance Warn fires and only those loops carry a small residual seam.

---

## 8. Open / residual

- **spanStartUT == first body-fixed ascent sample.** The alignment is pinned to the instant the clock maps to `spanStartUT`. If aa48920e has pre-launch pad-sit idle frames before the first ascent sample, the target should be the first ascent point. Confirm in the s15 playtest.
- **Cross-SOI heliocentric-transfer encounter seam.** This design closes the body-fixed-ascent vs inertial-escape ROTATION seam only; it does not address the separate cross-SOI transfer encounter seam (the deferred patched-conic-chain rework). Confirm whether that residual is now the dominant remaining artifact.
- **No alignment-off setting.** Like PadAlignLaunch (no toggle), the launch alignment runs whenever re-aim is engaged AND `!pad.Applied` AND a valid SOI-exit boundary exists. Deferred.
