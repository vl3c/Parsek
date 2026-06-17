# Design: Per-Loop Pre-Launch Hold to Close the Launch->Escape Seam in Span>=Synodic Re-Aim Loops

Status: Proposed (design only; no code in this doc)
Target: `docs/dev/design-reaim-launch-hold-seam.md`
Scope: render/playback-time only. Recorded data is IMMUTABLE; nothing here rewrites a `.prec` / `OrbitSegments` / any recording.
Failing class: mission `s15` / `Kerbal X #2`, recording `aa48920e` (an engaged re-aim loop whose recorded span exceeds the Kerbin->destination synodic period).

This revision settles the disputes raised in review against the actual code, and folds in two simplifications the maintainer asked for:

- The launch hold is a **launch-TIME shift**: the loop's launch instant becomes `L_N + H_N`. Before that instant the ghost is **absent** (it has not launched yet, exactly as a ghost is absent before any first launch). There is no "show the ghost on the pad and wait"; the corrected launch UT is simply what playback warps to.
- The Duna-side arrival wait **already holds-until-aligned**, so the launch hold does NOT regress it and does NOT require advancing two arrival-hold producers. Because the launch hold defers the whole loop (SOI entry included) by `H_N`, and the existing per-loop arrival hold self-aligns the destination rotation phase, the compensation is a **single in-clock subtraction** applied where the per-loop arrival `W_N` is computed: `W_N_effective = ((W_N - H_N) mod T_align + T_align) mod T_align`.

One reviewer claim (a wrong-epoch absolute `recordedLaunchUT`) is corrected: the residual is pinned to the live-vs-recorded launch displacement `(phaseAnchorUT - spanStartUT)`. One reviewer claim (the launch hold must be threaded through ~10 call sites) is partially corrected: only the unpacked-arg call sites need changes; the map/TS/KSC surfaces read the hold from the `LoopUnit` internally. The evidence is in section 10.

---

## 1. Problem statement

### 1.1 The seam

A re-aim loop replays a recorded interplanetary mission on a loop, re-solving the heliocentric transfer per loop so it still reaches the destination as the planets move (`ReaimPlaybackResolver.BuildWindowSegments`, `ReaimPlaybackResolver.cs:139`). The recorded mission has two qualitatively different leading legs:

- The **launch ascent** is **body-fixed**: stored as `TrajectoryPoint` lat/lon/alt and rendered, every frame, via `CelestialBody.GetWorldSurfacePosition(lat, lon, alt)` at the **LIVE** Kerbin rotation. In the map/TS line this is `GhostTrajectoryPolylineRenderer.BuildLegVectorLine` (the real draw at `GhostTrajectoryPolylineRenderer.cs:2403-2406`):
  ```csharp
  Vector3d world = body.GetWorldSurfacePosition(leg.lats[i], leg.lons[i], leg.alts[i]);
  leg.scratchScaledSpace[i] = bodyCentreScaled + (Vector3)((world - bodyPos) * invScale);
  ```
  The raw recorded `leg.lons[i]` is passed with no rotation correction. (`BodyFixedLongitudeAtUT` at `GhostTrajectoryPolylineRenderer.cs:1374` exists only inside the rate-limited **diagnostic** `EmitOneSidedBracketDiagnostic`, not the draw.)

- The **escape out of Kerbin SOI** is an **inertial Kepler conic** (`OrbitSegment`), frozen in its recorded inertial orientation and rendered through the orbit-line / arc pipeline.

When the loop replays time-shifted, Kerbin has spun to a different orientation than at the recorded launch UT. The body-fixed launch line therefore renders rotated by the accumulated spin (`(launchReplayUT - recordedLaunchUT) * 360 / T_sidereal` mod 360, observed at 82-220 deg for s15) away from the frozen escape conic, which did **not** rotate. The launch->escape handoff opens a visible gap in map + tracking-station views, and the map icon jumps across that gap at high time warp.

### 1.2 The regime: span >= synodic

The seam only appears when the mission's loop cadence is **not** equal to the Kerbin->destination synodic period.

`ReaimWindowPlanner.Plan` (`ReaimWindowPlanner.cs:88-137`) computes:
```
synodic      = TransferWindowMath.SynodicPeriodSeconds(originPeriod, targetPeriod)   // line 103
spanDuration = spanEndUT - spanStartUT                                               // line 120
cadence      = synodic > spanDuration ? synodic : spanDuration                       // line 123
```
For the normal interplanetary case the synodic period (~2 Kerbin years for Duna) dwarfs the recorded span, so `cadence == synodic`. For `s15` / `Kerbal X #2` the recorded mission **lasts longer than its own transfer window**, so `spanDuration > synodic` and `cadence == spanDuration != synodic`.

`MissionLoopUnitBuilder` then takes `effectiveCadence = Math.Max(span, sched.CadenceSeconds)` (`MissionLoopUnitBuilder.cs:452`), confirming the span-dominated cadence on the live clock.

### 1.3 Why PadAlignLaunch bails

`ReaimWindowPlanner.PadAlignLaunch` (`ReaimWindowPlanner.cs:175-234`) is the existing fix for exactly this seam in the `cadence == synodic` case. It snaps the **global** phase anchor (and quantizes both `CadenceSeconds` and `SynodicPeriodSeconds`) to a whole sidereal day so the launch pad replays at the recorded rotation phase. The aligned quantity is the launch displacement `offset = phaseAnchorUT - spanStartUT` (line 211), snapped to a whole sidereal day (line 212). But it deliberately bails when cadence and synodic differ:

```csharp
// ReaimWindowPlanner.cs:207-208
if (Math.Abs(cadenceSeconds - synodicPeriodSeconds) > 1.0)
    return r;   // Applied = false, identity schedule
```

The reason (documented in the method summary at `ReaimWindowPlanner.cs:200-208`): the playback resolver derives the loop window index from the **cadence clock** but reads the transfer-geometry departure from `SynodicPeriodSeconds`. Those two MUST share one period for the window-index<->departure map to stay 1:1. PadAlignLaunch enforces that by setting `CadenceSeconds == SynodicPeriodSeconds == quantizedSynodic` (`ReaimWindowPlanner.cs:229-230`). When `cadence != synodic` it cannot do that without forcing the loop period to whole days, which would shift **which synodic window each loop catches** and make the transfer miss the destination. So it refuses, and the span>=synodic loop is left with the seam.

PadAlignLaunch is correct and STAYS for `cadence == synodic`. This design adds a complementary, per-loop mechanism for the `cadence != synodic` regime that PadAlignLaunch declines.

---

## 2. The launch-hold approach

### 2.1 Core idea

For each loop, **shift the loop's launch time** by a residual `<= 1` Kerbin sidereal day: the loop launches at `L_N + H_N` instead of `L_N`. By the time the pad reaches that instant it has rotated back to the **same inertial orientation** it had at the recorded launch. The recorded launch->SOI-exit trajectory then replays **verbatim** from that launch instant. Before the launch instant the loop's ghost is simply **absent** (it has not launched yet), exactly as a ghost is absent before any first launch.

Because the pad is realigned to the recorded inertial orientation:
- the body-fixed launch line (drawn at the now-aligned live rotation) renders **both** at the real current pad **AND** coincident with the frozen recorded escape conic, closing the seam; and
- the launch starts at the **real pad** (this is what the maintainer wants; the rejected derotation alternative would have rendered the launch off-pad at the recorded-pad inertial position).

### 2.2 The hold residual math (pinned to the live-vs-recorded launch displacement)

The pad-alignment quantity is the **launch displacement**, NOT an absolute recorded-launch epoch. From `ReaimWindowPlanner.Plan`:
```
phaseAnchor = d0 - (recordedDepartureUT - spanStartUT)     // ReaimWindowPlanner.cs:124
```
so `phaseAnchorUT - spanStartUT == d0 - recordedDepartureUT`. The recorded launch ascent begins at the recorded UT that the span clock maps to `spanStartUT` (the loop clock resolves `loopUT == spanStartUT` at `phaseInCycle == 0`, `GhostPlaybackLogic.cs:7403-7404`). PadAlignLaunch already aligns exactly `offset = phaseAnchorUT - spanStartUT` to a whole sidereal day (`ReaimWindowPlanner.cs:211-212`). The per-loop launch hold aligns the SAME quantity, per loop.

Let:
- `T_sid` = the launch body's sidereal rotation period = `Math.Abs(bodyInfo.RotationPeriod(plan.LaunchBody))` (Kerbin ~= 21600 s ~= 6 h). The `Math.Abs` matches PadAlignLaunch's retrograde handling (`ReaimWindowPlanner.cs:194`).
- For replayed loop `N` (the resolved `cycleIndex`), the loop's **unshifted** launch displacement from the recorded launch is `Off_N = (phaseAnchorUT + N * cadence) - spanStartUT`. (The loop's unshifted live launch instant is `L_N = phaseAnchorUT + N * cadence`; subtracting `spanStartUT` puts it in the same displacement units PadAlignLaunch snaps. Equivalently `Off_N = (phaseAnchorUT - spanStartUT) + N * cadence`.)

The pad is rotation-aligned with the recorded launch iff `Off_N` is a whole number of `T_sid`. The minimal **forward** hold (positive dead time) that achieves this is:

```
H_N = ( ( -Off_N ) mod T_sid + T_sid ) mod T_sid          ; H_N in [0, T_sid)
```

After the hold, the loop's launch displacement is `Off_N + H_N`, a whole number of `T_sid`, so the live pad rotation equals the recorded pad rotation.

This is the same shape as the arrival hold's reference value `ComputeArrivalAlignHoldSeconds` (`GhostPlaybackLogic.cs:7133-7144`, `m = (recordedArrivalUT - entryLiveUT) mod T; if m<0 m+=T`) and the per-loop sawtooth of `ComputePerLoopArrivalHoldSeconds` (`GhostPlaybackLogic.cs:7163-7172`, the double-mod-plus-`T` normalization that keeps the result in `[0,T)` for any sign and any `N`). Like `W_N`, `H_N` is a sawtooth in `N` bounded to `[0, T_sid)`; it never grows with loop index.

The hold is computed **per loop, AFTER the loop's `cycleIndex` is resolved** (exactly like the arrival hold), so it never changes cadence, synodic, the phase anchor, or the cycle index.

### 2.3 How the verbatim replay then coincides

The launch-time shift defers the loop's launch (the **launch boundary** at `spanStartUT`) by `H_N`, pushing the entire recorded launch->SOI-exit->...->arrival sequence later in live time by `H_N`. The recorded launch ascent then replays at a live UT whose launch displacement is a whole number of `T_sid` from the recorded launch. At that instant:
- `GetWorldSurfacePosition(lat, lon, alt)` places each body-fixed launch point at the recorded inertial orientation (the pad is back where it was); and
- the recorded escape `OrbitSegment` (an inertial conic) is the **same recorded conic**, replayed verbatim.

So the body-fixed launch line and the inertial escape conic now sit on one rotation basis and meet at the SOI exit, exactly as they did in the original recording. The seam collapses.

This is identical in spirit to how PadAlignLaunch closes the seam for `cadence == synodic` (snap the launch displacement to a whole sidereal day), but applied per-loop with a residual instead of globally with one anchor+cadence knob.

---

## 3. Freedom #1 worked out: render the escape verbatim to SOI exit; do not re-aim the exit direction

The maintainer states: "We do not care about the direction we exit Kerbin SOI." The recorded escape (launch -> Kerbin parking/loiter -> SOI exit) is rendered **verbatim** from launch to the SOI-exit point. We do **not** re-aim or rotate the in-SOI escape direction.

This is consistent with the precedent already used for the launch -> Kerbin loiter/parking -> SOI-exit rendering today:

- `ReaimPlaybackResolver` re-aims **only** the heliocentric (common-ancestor) leg. The pre-check at `ReaimPlaybackResolver.cs:104-106` skips any member with no heliocentric leg in the transfer window (a launch / arrival / debris leg) entirely and keeps its faithful body-relative segments. Only the heliocentric leg over `[RecordedDepartureUT, RecordedArrivalUT]` is replaced (`BuildWindowSegments`); the member's recorded body-relative segments (the launch ascent, the Kerbin parking orbit, the escape hyperbola) pass through verbatim.

- For `s15` / `Kerbal X #2`, the transfer departs from a heliocentric **parking orbit** co-orbital with Kerbin (`DepartedFromHeliocentricPark == true`, see `ReaimClassifier.cs`), not directly from the SOI exit. The escape into that park is rendered verbatim; only the trans-target transfer is re-aimed.

- The escape leg is `ecc >= 1` (a hyperbolic SOI exit). By `ForwardRenderWindow.IsFullLoopClosedOrbit` (`ForwardRenderWindow.cs:44-59`, the first branch `if (seg.eccentricity >= 1.0) return false`), a hyperbolic/parabolic arc is NEVER a full-loop closed orbit, so it is always part of the **open forward run** and is drawn. A **closed** parking orbit (`ecc < 1 && span >= period`, line 47-58) would instead be drawn whole and would hide the rotation offset (the ellipse looks the same at any spin phase); the **open** escape arc is what exposes the offset, which is why the seam is visible on the escape and not on a closed park.

Net effect: the launch hold does not introduce any new re-aim of the escape. It only changes **when** (in live time) the verbatim launch->escape sequence plays, choosing the per-loop instant where the live pad rotation reproduces the recorded one. Where the ghost happens to exit the SOI (its inertial direction) is whatever was recorded; that is acceptable by freedom #1.

---

## 4. It is a launch-TIME shift (ghost absent before launch, not a rendered pad-wait); and why not a phase nudge

The launch hold is a **launch-time shift**: the loop's launch instant becomes `L_N + H_N`, and the recorded launch->escape sequence starts at that instant. BEFORE that instant the ghost is **absent** - it has not launched yet, exactly as a ghost is absent before any first launch. There is NO "show the ghost on the pad, warp to launch time, then wait"; the corrected launch UT is simply what playback resolves to and warps toward. This is a forward shift modeled on the existing per-loop **arrival hold**'s deferral, not a negative time-nudge in the phase map.

### 4.1 The deferral mechanism we mirror, and the absence we need

The arrival hold is applied inside the shared span clock `TryComputeSpanLoopUT` (`GhostPlaybackLogic.cs:7308-7506`):

1. Compute `cycleIndex` and `phaseInCycle` from the cadence clock (`GhostPlaybackLogic.cs:7413-7415`) BEFORE any hold.
2. Override the constant base hold `W_0` with the per-loop `W_N = ComputePerLoopArrivalHoldSeconds(w0, cycleIndex, cadence, T_align)` (`GhostPlaybackLogic.cs:7444-7466`).
3. `effectiveSpan = compressedSpan + hold` (`GhostPlaybackLogic.cs:7474`); the hold is INSERTED at `holdPhasePos = CompressSpanUT(arrivalHoldAtUT) - spanStartUT` (`GhostPlaybackLogic.cs:7473`).
4. `ApplyArrivalHoldToPhase(clampedPhase, holdPhasePos, hold)` (`GhostPlaybackLogic.cs:7186-7196`) maps the phase across that mid-span boundary and resumes the recorded sequence deferred by `hold`.

The launch hold reuses the same **deferral** of everything after the launch boundary: it subtracts `H_N` from the working phase so the verbatim recorded span replays from `spanStartUT` deferred by `H_N` (section 6.2). What it adds is the **pre-launch absence**: for `phaseInCycle < H_N` the ghost must render as ABSENT (it has not launched).

The cleanest way to get that absence is to map `phaseInCycle in [0, H_N)` to a resolved `loopUT` **below** the member's activation start, so the EXISTING pre-activation absence path hides the ghost: `GhostPlaybackEngine.cs:2368-2369`, `if (spanLoopUT < memberActivationStartUT)` where `memberActivationStartUT = ResolveGhostActivationStartUT(traj)`. That path hides the member WITHOUT destroying it and carries a keep-watched-owner-alive fallback (`GhostPlaybackEngine.cs:2388-2403`), so the camera stays anchored through the pre-launch window.

CRITICAL NUANCE the implementation MUST preserve: do **not** map the pre-launch window to `loopUT == spanStartUT`. That is the frozen-on-pad pose, and `2368-2369` does NOT hide it (`spanStartUT` is not `< memberActivationStartUT`); `DecideUnitMemberRender` returns `Render` whenever the resolved `spanLoopUT` is in the member window (`GhostPlaybackLogic.cs:7644-7647`), so a phase-0-clamped resolve would render the ghost on the pad. The pre-launch window must resolve BELOW the member window so the ghost is absent. Section 10 carries the open item: verify that a resolved `loopUT` below the member window hits the `2368-2369` absence path and NOT the "member outside its window" DESTROY path (`GhostPlaybackEngine.cs:~2355`); if the window check would instead destroy, an explicit pre-launch "hidden, do not destroy" routing is still required - but framed as ABSENCE (the ghost has not launched), NOT as a pad-wait.

### 4.2 Why a phase-map launch nudge does NOT work

A prior design review proved that a **negative time-nudge** at the launch in the phase map is fatal:

- `ApplyArrivalHoldToPhase` relies on a **mid-span freeze affordance**: the boundary at `holdPhasePos > 0` has recorded span both before and after it, so the phase can be held at the boundary and resumed. The launch has no symmetric affordance before it.
- A nudge that tries to pull the launch EARLIER (negative) clamps the ascent to `spanStart` and freezes it: `TryComputeSpanLoopUT` clamps `clampedPhase = phaseInCycle >= effectiveSpan ? effectiveSpan : phaseInCycle` and returns `loopUT = spanStartUT` for `phaseInCycle <= 0` (`GhostPlaybackLogic.cs:7403-7404, 7497`). There is no "negative phase" the clock can express; the ascent would stick at the first frame.

A **forward launch-time shift** sidesteps this entirely: phase `< H_N` resolves below the member window (the ghost is absent, having not launched), and phase `>= H_N` plays the verbatim recorded span from `spanStartUT`, launch at `L_N + H_N`.

---

## 5. Targeting-safety proof

Claim: the `<= 1`-sidereal-day launch hold preserves the window-index<->departure 1:1 map, leaves the synthesized transfer geometry byte-identical, and defers the departure by `<= T_sid` (well inside a transfer launch window's slack). Because it defers the whole loop (SOI entry included) by `H_N` and the existing arrival wait holds-until-aligned, the destination alignment self-corrects with a single in-clock subtraction at the per-loop `W_N` computation (section 5.4 / the section 6 wiring).

### 5.1 The departure shifts by the same residual, and the geometry is byte-identical

Because the recorded launch->departure coast is replayed **verbatim** (only the launch boundary's dead time changes), the entire post-launch sequence (parking, heliocentric park, trans-target burn at the recorded departure) defers by exactly `H_N` in live time. So the live trans-target departure shifts by `H_N <= T_sid` (~6 h for Kerbin).

The STRONGER guarantee (not just a slack tolerance): the synthesized transfer geometry is byte-identical with or without the launch hold. The resolver computes `windowIndex` from raw `currentUT` via `TryComputeSpanLoopUT(..., schedule: null)` with NO hold args (`ReaimPlaybackResolver.cs:110-117`, `windowIndex = cycleIndex` at line 117), then solves the Lambert at `nominalDepartureUT = schedule.DepartureUTForWindow(windowIndex) = D0 + windowIndex * synodic` (`ReaimPlaybackResolver.cs:144`, `ReaimWindowPlanner.cs:61-64`). None of `windowIndex`, `nominalDepartureUT`, or the solved arc depends on any hold. The ghost merely TRAVERSES the same frozen arc `H_N` later in live time. So the transfer cannot mis-target; the ~6 h "within slack" argument is a secondary comfort, not the load-bearing reason.

### 5.2 The window-index<->departure 1:1 map is preserved

This is the invariant PadAlignLaunch protects (`ReaimWindowPlanner.cs:200-208`). It is preserved by construction because the launch hold is computed **per-loop, AFTER the cycle index is resolved**, exactly like the arrival hold:

- `cycleIndex` is computed from the **cadence clock** BEFORE any hold (`TryComputeSpanLoopUT`, `GhostPlaybackLogic.cs:7413-7415`). The launch hold (like the arrival hold) is applied to the **phase within the cycle**, not to the cycle index.
- The resolver's `windowIndex` is the pure `floor((currentUT - phaseAnchorUT) / cadence)` (via `TryComputeSpanLoopUT(..., schedule: null)`, no hold params). Adding a launch hold does NOT change `phaseAnchorUT`, `cadence`, or this formula.
- `DepartureUTForWindow(windowIndex) = FirstDepartureUT + k * SynodicPeriodSeconds`. The launch hold touches none of `FirstDepartureUT`, `SynodicPeriodSeconds`, or `windowIndex`.

So the launch hold defers the **rendered** (verbatim) launch->departure sequence in live time by `H_N`, but does NOT change which synodic window the loop catches, which transfer geometry is solved, or the cadence/anchor. This is the same decoupling the arrival hold relies on: `ReaimPlaybackResolver` resolves the window from the **unheld** cadence clock; `TryComputeSpanLoopUT` (with the hold args) resolves the **rendered** phase.

### 5.3 Park->escape re-phase residual is negligible

The heliocentric-park re-phase pins the park to `RelaunchUTForWindow(windowIndex) = D0 + window * cadence` (`ReaimWindowPlanner.cs:76-79`), the UN-held relaunch time, while the held ghost replays the park at `RelaunchUTForWindow + H_N`. The park's longitude-of-node re-phase is therefore computed for a UT `H_N` earlier than the actual render UT. Magnitude: the launch body's orbital angular rate over `H_N <= T_sid` (~6 h for Kerbin, orbital period ~9.20e6 s) is at most `360 * 21600 / 9.20e6 ~= 0.84 deg`, far below the seam scale. This is an accepted sub-degree residual, listed alongside the pre-existing park->transfer Increment-2 residual in section 10. (If a future, larger `T_sid` ever made this matter, the only resolver change needed is to pass `RelaunchUTForWindow(windowIndex) + H_N` to the park re-phase; the transfer geometry stays on the synodic clock either way.)

### 5.4 The Duna arrival wait already holds-until-aligned: one in-clock subtraction self-corrects it

The destination-side arrival wait already holds-until-aligned, so the launch hold does **not** regress it; it needs a single, one-line self-correction at the per-loop `W_N` computation. This is NOT a "fix it in two producers" problem.

The two arrival-hold helpers:
- `ComputeArrivalAlignHoldSeconds(recordedArrivalUT, entryLiveUT, T)` = `(recordedArrivalUT - entryLiveUT) mod T`, normalized to `[0, T)` (`GhostPlaybackLogic.cs:7133-7144`). This is the base hold that aligns the destination rotation phase at the in-SOI entry for one loop.
- `ComputePerLoopArrivalHoldSeconds(w0, cycleIndex, cadence, T)` = `((w0 - N*(cadence mod T)) mod T + T) mod T` (`GhostPlaybackLogic.cs:7163-7172`). It is **open-loop in N** (the comment at `GhostPlaybackLogic.cs:7443` says "no feedback"): it self-aligns the destination rotation phase against the per-loop cadence drift, but it does NOT know about an added launch delay. It is applied INSIDE `TryComputeSpanLoopUT` (`GhostPlaybackLogic.cs:7444-7466`), where `cycleIndex` and `cadence` (`cycleDuration`) are already in hand.

The launch hold defers the WHOLE loop (the SOI entry included) by `H_N`, so the ghost's actual SOI entry occurs `H_N` later than the unheld reference the per-loop `W_N` is computed against. Because the arrival wait holds-until-aligned, the destination alignment self-corrects if we shift the alignment target by `-H_N` at exactly the point `W_N` is computed:

```
W_N_effective = ((W_N - H_N) mod T_align + T_align) mod T_align
```

Derivation: the launch shift adds `-H_N` to the alignment target, and `(w0 - N*(cadence mod T) - H_N) mod T = (W_N - H_N) mod T`. So a single in-clock subtraction makes the EXISTING hold-until-aligned arrival wait self-correct at the ACTUAL (launch-shifted) SOI entry - "we have a wait mechanism when we enter Duna SOI which holds until correct alignment all the way to landing." For a landing on Duna (`T_rot ~= 65518 s`) a max launch hold of `T_sid = 21600 s` is up to `0.33` of a destination rotation, so without the subtraction the entry would land at a different rotation phase; with the subtraction the wait absorbs it.

Where it lives: entirely inside `TryComputeSpanLoopUT`, next to the existing `W_N` computation (`GhostPlaybackLogic.cs:7444-7466`), where `H_N` is also computed per `cycleIndex` (section 6.2). Compute `H_N` first, then subtract it from `W_N` with the `[0, T_align)` normalization above. The builder-side producers are **UNCHANGED**: `ArrivalHoldPlanner.ComputeArrivalHold` (`MissionLoopUnitBuilder.cs:618`) still produces the per-mission base `w0` and `T_align`, and `DestinationLoiterTrim.TrySolveDestinationLoiterTrim` (`MissionLoopUnitBuilder.cs:576`, the M-MIS-2 P4 path) is not touched for the launch-shift compensation. All launch-shift compensation lives in the one in-clock subtraction.

Open item (kept in section 10): confirm during implementation that `DestinationLoiterTrim`'s arrival output flows through the same unit `arrivalHoldSeconds -> ComputePerLoopArrivalHoldSeconds` clock path, so the single subtraction covers it. If it instead applies as a loiter cut, verify the `H_N` subtraction still composes. The unit test in section 9 pins the alignment-level invariant (destination phase at the held entry congruent to recorded) so a regression here fails the build.

---

## 6. Exact code touch-points

All additions are render/playback-time. No recording is mutated.

### 6.1 The pure residual method (mirror `ComputePerLoopArrivalHoldSeconds`)

Add to `GhostPlaybackLogic.cs`, adjacent to `ComputeArrivalAlignHoldSeconds` / `ComputePerLoopArrivalHoldSeconds`:

```csharp
// Proposed signature (pure, internal static, xUnit-testable; no Unity):
internal static double ComputePerLoopLaunchHoldSeconds(
    double phaseAnchorUT, double spanStartUT, long cycleIndex, double cadence,
    double launchBodyRotationPeriod)
```

Behavior:
- `T_sid = Math.Abs(launchBodyRotationPeriod)`. Degenerate `T_sid` (NaN / Inf / <= 0) -> return 0 (no hold; non-rotating launch body).
- `Off_N = (phaseAnchorUT - spanStartUT) + cycleIndex * cadence` (the loop's unshifted launch displacement from the recorded launch; this is the SAME quantity PadAlignLaunch snaps, `ReaimWindowPlanner.cs:211`, extended per loop).
- `H_N = ((-Off_N) mod T_sid + T_sid) mod T_sid`, in `[0, T_sid)`.
- Return 0 when `phaseAnchorUT` or `spanStartUT` is NaN (matching the `ComputeArrivalAlignHoldSeconds` NaN guard at `GhostPlaybackLogic.cs:7138`).

This mirrors `ComputePerLoopArrivalHoldSeconds` (sawtooth in `N`, double-mod-plus-`T_sid` bounded to `[0, T_sid)`). It deliberately does NOT take an absolute `recordedLaunchUT`: the alignment target is the live-vs-recorded launch displacement `(phaseAnchorUT - spanStartUT)`, which is the only well-defined cross-epoch quantity (`phaseAnchorUT - spanStartUT == d0 - recordedDepartureUT` by `ReaimWindowPlanner.cs:124`). Using an absolute recorded-launch UT would mix epochs and is the wrong-epoch bug the review flagged.

### 6.2 Wiring the launch-time shift into the span clock (absence before launch)

`LoopUnit` (constructor at `GhostPlaybackLogic.cs:6819-6835`) gains two fields mirroring the `ArrivalHold*` trio:
- `LaunchBodyRotationPeriodSeconds` (the `T_sid` input; NaN/degenerate -> no hold), and
- a boolean `LaunchHoldEngaged` (true only when re-aim engaged AND PadAlignLaunch did not apply AND a body-fixed launch leg exists; see 6.3). Storing `T_sid` + a gate is enough; `phaseAnchorUT / spanStartUT / cadence` are already on the unit.

In `TryComputeSpanLoopUT`, AFTER `cycleIndex` is computed (`GhostPlaybackLogic.cs:7414`) and BEFORE the existing arrival-hold block:

1. Compute `H_launch = ComputePerLoopLaunchHoldSeconds(phaseAnchorUT, spanStartUT, cycleIndex, cycleDuration, launchBodyRotationPeriodSeconds)` (0 when the launch hold is not engaged).
2. Pre-launch absence: for `phaseInCycle < H_launch` the loop has not launched yet, so the ghost must render ABSENT. The cleanest mechanism resolves the pre-launch window to a `loopUT` BELOW the member's activation start so the EXISTING pre-activation absence path hides it: `GhostPlaybackEngine.cs:2368-2369`, `if (spanLoopUT < memberActivationStartUT)` (hides without destroying, with the keep-watched-owner-alive fallback at `2388-2403`). Concretely, map `phaseInCycle in [0, H_launch)` to a `loopUT < spanStartUT` (a pre-launch UT below the member window) rather than to `spanStartUT`. Do NOT resolve it to `loopUT == spanStartUT`: that is the frozen-on-pad pose, which `2368-2369` does NOT hide (`DecideUnitMemberRender` returns `Render` for a `spanLoopUT` in the member window, `GhostPlaybackLogic.cs:7644-7647`). If, during implementation, a `loopUT` below the member window would instead hit the "member outside its window" DESTROY path (`GhostPlaybackEngine.cs:~2355`) rather than the absence path, add an explicit pre-launch "hidden, do not destroy" routing (a `bool isPreLaunchHold` out-parameter on `TryComputeSpanLoopUT` mirroring `isInInterCycleTail`, mapped by `DecideUnitMemberRender` to a hide) - but framed as ABSENCE (the ghost has not launched), NOT a pad-wait. Section 10 carries this verification as an open item.
3. Post-launch deferral: for `phaseInCycle >= H_launch`, subtract `H_launch` from the working phase so the verbatim recorded span replays from `spanStartUT` deferred by `H_launch` (launch at `L_N + H_launch`). The simplest implementation is a flat offset applied to `phaseInCycle` before the existing arrival-hold / loiter-cut composition: `phaseInCycle -= H_launch`. The arrival hold's single mid-span insertion (`ApplyArrivalHoldToPhase` at `holdPhasePos > 0`) is untouched; its per-loop `W_N` is self-corrected by the single subtraction `W_N_effective = ((W_N - H_launch) mod T_align + T_align) mod T_align` per 5.4. This composes the launch shift (a pre-phase offset + the pre-launch absence) with the arrival hold (still the single-boundary mid-span insertion) WITHOUT generalizing `ApplyArrivalHoldToPhase` to two boundaries.
4. Defense-in-depth bound: extend the existing `effectiveSpan <= cadence` clamp (`GhostPlaybackLogic.cs:7471-7472`) to cover the sum `compressedSpan + arrivalHold + launchHold`. For the re-aim case `cadence = max(span, synodic) >> span + 2*T_sid`, so the bound is never tight; enforce it anyway (shared clock). The clamp must be ordered so it never silently truncates the in-SOI replay: cap the LAUNCH hold first (it is the discretionary, bounded-by-`T_sid` quantity), preserving the full in-SOI replay window.

Why not a phase-anchor-equivalent engine-side start delay (the alternative): it would either change `phaseAnchorUT` (breaking 5.2) or re-derive the cycle index, duplicating the span clock. Keeping all loop-timing inside the one shared clock keeps the invariants where they are already proven.

### 6.3 Where the hold is built (MissionLoopUnitBuilder)

In the re-aim ENGAGED block (`MissionLoopUnitBuilder.cs:444-637`), AFTER `phaseAnchorUT`, `effectiveCadence`, and `loiterCuts` are final (post `cutBeforeDeparture` at `:501-505` and post `PadAlignLaunch` at `:516-541`) and alongside the arrival-hold build (`:560-636`):

- Gate: set `LaunchHoldEngaged = true` ONLY when re-aim is engaged AND `!pad.Applied` AND the unit has a body-fixed launch leg adjacent to an inertial escape (the seam precondition). The `!pad.Applied` test reads the `pad` result from the call at `:520`; when PadAlignLaunch applied (`cadence == synodic`), the pad is already globally aligned and the launch hold must be 0 (no double-correction). The non-rotating-body case is double-covered: PadAlignLaunch returns `Applied=false` for a degenerate body (`ReaimWindowPlanner.cs:195-198`) AND `ComputePerLoopLaunchHoldSeconds` returns 0 for a degenerate `T_sid`, so the gate ordering is safe either way. The body-fixed-launch-leg precondition prevents a no-op hold (and a pointless window-slack consumption) for a re-aim unit that has no pad to align (a member starting already in orbit, or a chained continuation with no launch ascent); reuse the leg classification already available to the polyline renderer / segment assembler, or gate on `DepartedFromHeliocentricPark` plus the presence of a launch-body surface leg at `spanStart`.
- Resolve `T_sid = bodyInfo.RotationPeriod(plan.LaunchBody)` (the same call PadAlignLaunch consumes at `MissionLoopUnitBuilder.cs:515`).
- Pass `T_sid` and the gate into the `LoopUnit` constructor alongside the arrival-hold args (`:665-669`).
- The arrival-hold producers are UNCHANGED. `ArrivalHoldPlanner.ComputeArrivalHold` (`:618`) and `DestinationLoiterTrim.TrySolveDestinationLoiterTrim` (`:576`, the M-MIS-2 P4 path) still compute the per-mission base `w0` and `T_align` exactly as today. The launch-shift compensation is the single in-clock subtraction `W_N_effective = ((W_N - H_launch) mod T_align + T_align) mod T_align`, computed inside `TryComputeSpanLoopUT` next to the existing `W_N` computation (where both `H_launch` and `cycleIndex` are in hand); the existing hold-until-aligned arrival wait then self-corrects at the launch-shifted SOI entry (5.4). No producer-side change is needed.
- Emit an Info log mirroring the PAD-ALIGN and ARRIVAL HOLD lines (`:532-540`, `:605-613`), tag `Reaim`, gated on `LaunchHoldEngaged && !SuppressLogging`, carrying `siderealDay`, `phaseAnchor`, `cadence`, and that the launch hold engaged because PadAlignLaunch declined (cadence != synodic). The per-loop `H_N` is logged from the span clock (6.4).

### 6.4 Engine per-frame render + logging

- The flight engine `UpdateUnitMemberPlayback` (`GhostPlaybackEngine.cs:2280-2284`) calls `DecideUnitMemberRender` with UNPACKED `unit.ArrivalHoldSeconds / ArrivalHoldAtUT / ArrivalAlignPeriodSeconds`. This direct call site must also pass the new launch-hold inputs (`unit.LaunchBodyRotationPeriodSeconds`, `unit.LaunchHoldEngaged`). When the pre-launch window resolves to a `loopUT` below the member activation start (the preferred mechanism, 6.2), the existing pre-activation absence path at `:2368-2369` already hides the ghost without destroying it, including the keep-watched-owner-alive fallback at `:2388-2403`, so the camera stays anchored (hidden, not destroyed) through the `< 6 h` pre-launch window. ONLY if a sub-window-start `loopUT` would route to the "member outside its window" DESTROY path (`:~2355`) instead is the explicit `HiddenPreLaunchHold` decision required, handled in the `decision != Render` block (`:2308-2360`) with the same keep-watched-owner-alive fallback (`:2337-2352`). Either way the ghost is ABSENT during the pre-launch window (it has not launched), not frozen on the pad.
- `ParsekKSC.UpdateUnitMemberPlayback` (`ParsekKSC.cs:1187`) is the second direct unpacked call site and needs the same two args forwarded.
- The map / TS / flight-map surfaces route through `ResolveTrackingStationSampleUT`, which reads the hold args FROM the `LoopUnit` internally (`GhostPlaybackLogic.cs:7696-7698`) and forwards them into `DecideUnitMemberRender`. Adding the launch-hold reads there (same internal forwarding) makes every surface that takes the whole `units` set (the polyline renderer, `GhostMapPresence`, `ParsekUI`, `ParsekTrackingStation`, `MapRender/ChainSampler`, `MapRender/TrackingStationScene`) inherit the pre-launch `loopUT` (below the member window) + `renderHidden=true` during the absence with NO external-caller change. (This corrects the continuity reviewer's "10 call sites" framing: only the two unpacked-arg call sites above need edits; the rest read from the unit. See section 10.) `ResolveTrackingStationSampleUT` already maps any non-`Render` decision to `renderHidden=true` (`:7700-7704`), so the pre-launch absence (whether via the `:2368-2369` path or the contingency `HiddenPreLaunchHold` decision) suppresses the icon + line + marker automatically.
- `bodyFixedShift` (`GhostPlaybackEngine.cs:2455-2462`) is UNTOUCHED: it is gated on `unit.RelaunchSchedule != null && HasPhasingKnob`, and re-aim units set `relaunchSchedule = null` (`MissionLoopUnitBuilder.cs:454`), so it is already 0 for re-aim. The launch hold does not use it (this is also why the superseded derotation approach was a no-op; see 8).
- Add a rate-limited Verbose `Reaim` line in `TryComputeSpanLoopUT`'s launch-hold branch, gated on `LaunchHoldEngaged && T_sid finite/positive` (mirror the arrival hold's `hold > 0` + finite-period gate at `:7444/7452`), keyed on mission identity (`phaseAnchorUT + spanStartUT`, NOT `cycleIndex`, so the key set stays bounded by mission count), carrying `cycleIndex`, `T_sid`, and `H_N` - exactly the pattern the per-loop arrival hold uses (`:7456-7464`). Do not emit for units where the launch hold is not engaged (avoids a per-mission line for pad-aligned / non-rotating units).

### 6.5 Map / TS coherence (free, via the shared clock)

The map / TS render reads the same span clock through `ResolveTrackingStationSampleUT`, which forwards the unit's hold fields into `DecideUnitMemberRender`. The polyline launch leg's head-UT gate (`GhostTrajectoryPolylineRenderer.cs:3117`) enters the launch leg window only at the shifted launch UT (`L_N + H_launch`), at which point the live pad rotation matches the recorded one and the body-fixed launch line (`GhostTrajectoryPolylineRenderer.cs:2403-2406`) coincides with the inertial escape conic. Flight, map, and TS agree because they share one clock; before launch all three render the ghost absent (the pre-launch `loopUT` resolves below the member window -> `renderHidden`, or the contingency `HiddenPreLaunchHold` -> `renderHidden`).

### 6.6 PadAlignLaunch stays for cadence==synodic

No change to `ReaimWindowPlanner.PadAlignLaunch`. It still applies (and still bails for `cadence != synodic`). The launch hold is the explicit complement for the regime PadAlignLaunch declines, gated on `!pad.Applied`.

---

## 7. Edge cases / guardrails

- **Non-rotating / degenerate-rotation launch body**: `T_sid <= 0` / NaN / Inf -> `ComputePerLoopLaunchHoldSeconds` returns 0 (no hold). Matches PadAlignLaunch's non-rotating return (`ReaimWindowPlanner.cs:195-198`) and `ComputeArrivalAlignHoldSeconds` (`GhostPlaybackLogic.cs:7136`). No pad realignment possible, no hold; behavior reverts to today's seam (no regression, no new corruption).
- **Unit with no body-fixed launch leg**: `LaunchHoldEngaged = false` (6.3 gate), `H_N = 0`. A member starting already in orbit, or a chained continuation with no ascent, never incurs a hold and never consumes window slack for no visual benefit.
- **cadence == synodic (unchanged)**: PadAlignLaunch applies; `!pad.Applied` is false so `LaunchHoldEngaged = false`, `H_N = 0`. No double-correction. The pad is aligned globally by PadAlignLaunch's whole-sidereal-day anchor snap.
- **First loop (N = 0)**: `cycleIndex == 0`, `Off_0 = phaseAnchorUT - spanStartUT`, `H_0` is the minimal forward residual aligning the first loop's pad. No special case.
- **High loop index N**: `H_N` is `((-Off_N) mod T_sid + T_sid) mod T_sid`, always in `[0, T_sid)`; it never grows with `N` (sawtooth), bounded `< 6 h` for Kerbin. Same boundedness as the per-loop arrival hold (`GhostPlaybackLogic.cs:7170-7171`).
- **Interaction with the inter-loop gap**: for span>=synodic, `cadence = span` and the loop is back-to-back (`isInInterCycleTail` false; `GhostPlaybackLogic.cs:7482-7491`). The launch hold of `<= 6 h` is tiny relative to the multi-year span and never pushes `effectiveSpan` past `cadence` (the extended defense-in-depth bound still enforced). If a future producer has `cadence > span` (a real inter-cycle tail), the launch hold consumes a sliver of the tail's idle time; still bounded `< T_sid`.
- **What the ghost shows before launch**: nothing. The loop has not launched yet, so for `phaseInCycle < H_N` the ghost is ABSENT - the pre-launch window resolves to a `loopUT` below the member activation start and the existing absence path (`GhostPlaybackEngine.cs:2368-2369`) hides it without destroying it (icon + line + marker via the shared clock; the keep-watched-owner-alive fallback keeps the camera anchored). This is the same absence a ghost has before any first launch, NOT a frozen-on-pad pose (which is exactly what mapping the window to `loopUT == spanStartUT` would produce, and is avoided per 4.1 / 6.2).
- **Composition with arrival hold + destination loiter trim**: the LAUNCH-time shift composes as a pre-phase offset + pre-launch absence (phase 0); the ARRIVAL hold stays the single-boundary insertion at the heliocentric->capture boundary (`holdPhasePos > 0`); loiter cuts / `DestinationLoiterTrim` (`MissionLoopUnitBuilder.cs:576-615`) remove recorded-span time. The arrival hold's per-loop `W_N` is self-corrected by the single subtraction `((W_N - H_launch) mod T_align + T_align) mod T_align` (5.4) so the existing hold-until-aligned wait lands the deorbit at the recorded rotation phase. The `effectiveSpan = compressedSpan + arrivalHold + launchHold <= cadence` bound covers the sum, capping the launch hold first.
- **`DepartedFromHeliocentricPark` (true for s15)**: the park re-phase pins the park to `RelaunchUTForWindow` (`ReaimWindowPlanner.cs:76-79`); the launch hold defers the whole verbatim launch->park->burn sequence by `H_N`, with the sub-degree park re-phase residual quantified in 5.3.

---

## 8. Superseded approach (why it was wrong)

A previous design+review recommended derotating the body-fixed ascent onto the recorded inertial frame using the existing `bodyFixedShiftSeconds` lever (`GhostPlaybackEngine.cs:2455-2462`), reusing `ComputeScheduledBodyFixedShiftSeconds`. That is wrong for three confirmed reasons:

1. **It is a no-op for re-aim.** `ComputeScheduledBodyFixedShiftSeconds = (currentUT - launchUT) - (loopUT - spanStartUT)` (`GhostPlaybackLogic.cs:7240-7244`) computes the M4b loiter-trim residual, which is ~0 for a uniform (no-loiter-trim) re-aim replay. Worse, the engine only computes it when `unit.RelaunchSchedule != null && unit.RelaunchSchedule.HasPhasingKnob` (`GhostPlaybackEngine.cs:2456`), but re-aim units set `relaunchSchedule = null` (`MissionLoopUnitBuilder.cs:454`), so `bodyFixedShift` is **always 0** for re-aim. It would never fire.

2. **The lever never reaches the surface that shows the seam.** The map/TS launch LINE is drawn by `GhostTrajectoryPolylineRenderer`, which has **zero** references to `bodyFixedShift` (grepping `GhostTrajectoryPolylineRenderer.cs` for `bodyFixedShift` returns no matches; the draw at `GhostTrajectoryPolylineRenderer.cs:2403-2406` uses raw recorded `leg.lons[i]`). An engine-side shift can never reach the body-fixed line.

3. **Even if wired, it renders the launch OFF the real pad.** Derotation would place the launch at the recorded-pad inertial position, not the live pad. The maintainer explicitly rejected an off-pad launch. The launch hold keeps the launch ON the real pad (the pad rotates back to the recorded orientation, so live pad == recorded inertial orientation at the held UT).

Do not revive the derotation approach.

---

## 9. Test plan

### 9.1 Pure unit tests (`Source/Parsek.Tests/`) for `ComputePerLoopLaunchHoldSeconds`

- **Realigns mod T_sid**: for arbitrary `phaseAnchorUT`, `spanStartUT`, `cadence`, and `N`, `((phaseAnchorUT - spanStartUT) + N*cadence + H_N) mod T_sid == 0` (within float tol).
- **Range**: `H_N in [0, T_sid)` for any `N` (including large `N`, negative inner term).
- **Already aligned -> 0**: when `(phaseAnchorUT - spanStartUT) + N*cadence` is an exact whole multiple of `T_sid`, `H_N == 0`.
- **Non-rotating launch body -> 0**: `T_sid <= 0` / NaN / Inf -> 0.
- **NaN inputs -> 0**: `phaseAnchorUT` or `spanStartUT` NaN -> 0 (matching `ComputeArrivalAlignHoldSeconds`).
- **Sawtooth in N**: `H_{N+1} - H_N == (-cadence) mod T_sid` (the per-loop drift), bounded.

### 9.2 Span-clock composition tests (`TryComputeSpanLoopUT`, in `MissionSpanClockTests.cs`)

- **cadence==synodic untouched / no double-correction**: with `LaunchHoldEngaged = false` (PadAlignLaunch applied), the clock is byte-identical to today.
- **Targeting invariants byte-identical**: with the launch hold engaged, `cycleIndex` from the resolver's window read `TryComputeSpanLoopUT(..., schedule: null)` (`ReaimPlaybackResolver.cs:110-117`) is unchanged vs. no launch hold; `DepartureUTForWindow(windowIndex)` is byte-identical. Pin the resolver call site as the contract boundary: assert `windowIndex` and `nominalDepartureUT` do not move when the launch hold is added (default arg vs. engaged).
- **Pre-launch ABSENCE (not frozen-on-pad)**: for `phaseInCycle < H_N` the resolved `loopUT` is BELOW the member window (so the member renders absent via `GhostPlaybackEngine.cs:2368-2369`), NOT `Render` at `spanStartUT`; assert the resolved `loopUT < spanStartUT` (or, if the contingency routing is used, the decision is `HiddenPreLaunchHold`). At `phaseInCycle == H_N` the ascent starts at `loopUT == spanStartUT`; at `phaseInCycle > H_N` `loopUT == spanStartUT + (phaseInCycle - H_N)` (verbatim, deferred). Probe assertion: `loopUT(currentUT, with-shift) == loopUT(currentUT - H_N, no-shift)` for a probe UT past the launch window.
- **Arrival wait self-corrects (5.4 regression fence)**: with a nonzero launch hold, the destination-side rotation phase at the launch-shifted SOI-entry replay UT is still congruent to `recordedArrivalUT mod T_align` (this test FAILS if the single subtraction `W_N_effective = ((W_N - H_launch) mod T_align + T_align) mod T_align` is not applied at the per-loop `W_N` computation). Confirm the same subtraction covers the `DestinationLoiterTrim` arrival output when it flows through the unit `arrivalHoldSeconds -> ComputePerLoopArrivalHoldSeconds` clock path.
- **Composition with arrival hold + loiter cuts**: launch hold + arrival hold + cuts compose; `effectiveSpan <= cadence` enforced; the launch hold is capped first so the in-SOI replay is never truncated.

### 9.3 Log-assertion test (canonical capture pattern, `RewindLoggingTests`-style)

- Capture `ParsekLog.TestSinkForTesting`; build a span>=synodic re-aim unit (PadAlignLaunch declined); assert the new launch-hold Info line (tag `Reaim`, `siderealDay=...`, "PadAlignLaunch declined -> per-loop launch hold engaged") and the per-loop Verbose `Reaim` line (`cycleIndex`, `T_sid`, `H_N`) fire for at least two cycles with a NON-ZERO `H_N` on at least one cycle (so `H_N` is seen to step and the value is proven to propagate through `TryComputeSpanLoopUT`, not just to be returned by the pure helper in isolation - the PR #885/#890 "prove the patch mutated something" lesson).
- Use `[Collection("Sequential")]` (shared `ParsekLog` static).

### 9.4 Cross-surface parity test

- Call `ResolveTrackingStationSampleUT` with the launch-hold-carrying unit and assert identical `loopUT` + `renderHidden` across the icon-call-site and line-call-site arg shapes for the same `currentUT` within and past the hold window (proves "they share a clock" is actually byte-identical, not just nominally shared).

### 9.5 In-game validation checklist (s15 / `Kerbal X #2`, recording `aa48920e`)

Run with the s15 mission looping at a span>=synodic cadence; observe across multiple loops at high time warp:

1. **Seam collapses to single-digit degrees**: the launch->escape handoff gap (map + TS) drops from 82-220 deg to ~0-3 deg (compare `MapRenderProbe` orbit-vs-leg longitude numbers).
2. **Launch starts on the real pad**: the body-fixed launch line begins at the live KSC pad position (NOT off-pad).
3. **Ghost absent before launch**: for the `< 6 h` pre-launch window the ghost icon + line + marker are absent (the ghost has not launched yet, exactly as before any first launch - not frozen on the pad); the camera, if watching, stays anchored (no watch-drop snap).
4. **Departure within window for multiple loops**: `ReaimPlayback` Verbose lines show the transfer still resolves (encounter found) for several consecutive `windowIndex` values; the `H_N` deferral does not change `windowIndex` or the solved departure.
5. **Arrival hold still fires AND still aligns**: the per-loop arrival-hold `Reaim` line still emits AND the destination rotation/station phase at the deorbit is still aligned (NOT regressed by the launch deferral - the existing hold-until-aligned wait self-correcting via the 5.4 single subtraction in-game).
6. **Flight + map + TS agree**: switch among flight, map, and tracking station mid-loop; the launch->escape geometry is coincident in all three.
7. **No icon teleport across the (now-closed) seam at high warp**: `MapRenderProbe` emits no `icon-jump` anomaly at the launch->escape boundary.

---

## 10. Open questions / residual risks

- **Arrival wait self-correction (single in-clock subtraction).** Verified against `ComputeArrivalAlignHoldSeconds` (`GhostPlaybackLogic.cs:7133-7144`) and `ComputePerLoopArrivalHoldSeconds` (`GhostPlaybackLogic.cs:7163-7172`, open-loop in N, "no feedback" comment at `:7443`): the arrival wait already holds-until-aligned, so the launch shift does NOT regress it. The compensation is a single subtraction `W_N_effective = ((W_N - H_launch) mod T_align + T_align) mod T_align` at the per-loop `W_N` computation inside `TryComputeSpanLoopUT` (`:7444-7466`); no builder-side producer change. OPEN: confirm during implementation that `DestinationLoiterTrim.TrySolveDestinationLoiterTrim`'s arrival output flows through the same unit `arrivalHoldSeconds -> ComputePerLoopArrivalHoldSeconds` clock path so the single subtraction covers it; if it instead applies as a loiter cut, verify the `H_launch` subtraction still composes (only its `ArrivalHoldResult` output shape and wiring at `MissionLoopUnitBuilder.cs:576-601` were read, not its internal reference math).
- **Pre-launch absence vs frozen-on-pad.** Verified against the pre-activation absence path (`GhostPlaybackEngine.cs:2368-2369`, `spanLoopUT < memberActivationStartUT`, hides without destroying, keep-watched-owner-alive fallback at `:2388-2403`) and `DecideUnitMemberRender` (`:7644-7647`, returns `Render` for a `loopUT` in the member window). Preferred mechanism: map the pre-launch window (`phaseInCycle < H_launch`) to a `loopUT` BELOW the member activation start so `:2368-2369` renders it absent (the ghost has not launched). Do NOT map it to `loopUT == spanStartUT` (the frozen-on-pad pose, which `:2368-2369` does NOT hide). OPEN / UNVERIFIED: that a `loopUT` below the member window hits the `:2368-2369` absence path and NOT the "member outside its window" DESTROY path (`GhostPlaybackEngine.cs:~2355`). If it would route to destroy, add the contingency explicit `HiddenPreLaunchHold` decision + an `isPreLaunchHold` out-param on `TryComputeSpanLoopUT`, framed as ABSENCE (the ghost has not launched), not a pad-wait. Item 3 of the in-game checklist confirms the absence either way.
- **Call-site count (review claim PARTIALLY CORRECTED).** Verified: `ResolveTrackingStationSampleUT` reads the hold args from the `LoopUnit` internally (`GhostPlaybackLogic.cs:7696-7698`), so the map/TS/KSC/polyline surfaces that pass the whole `units` set inherit the launch shift with no change. Only `GhostPlaybackEngine.cs:2280-2284` and `ParsekKSC.cs:1187` call `DecideUnitMemberRender` with UNPACKED args and need the two new launch-hold args forwarded (plus `HiddenPreLaunchHold` handling only if the contingency routing above is required). The reviewer's "10 forwarding sites" is the count of `ResolveTrackingStationSampleUT` callers, which do NOT need per-site changes. Still verify during implementation that no other call site passes unpacked arrival-hold args (grep `ArrivalHoldSeconds` at call sites).
- **Epoch (review claim CORRECTED).** Verified: `phaseAnchorUT - spanStartUT == d0 - recordedDepartureUT` (`ReaimWindowPlanner.cs:124`), and PadAlignLaunch snaps exactly `offset = phaseAnchorUT - spanStartUT` (`:211`). The residual is pinned to that displacement, NOT an absolute `recordedLaunchUT`. The draft's absolute-`recordedLaunchUT` framing was a wrong-epoch hazard and has been removed.
- **`recordedLaunchUT` provenance / pre-launch idle frames.** The displacement formulation aligns the instant the span clock maps to `spanStartUT` (the loop's `phaseInCycle == 0`). If a recording has pre-launch pad-sit idle frames before the first body-fixed ascent sample, the alignment target should be the first ascent point, not `spanStartUT`. UNVERIFIED against recording `aa48920e`. Confirm `spanStartUT` is the first body-fixed ascent sample for s15 before relying on the single-instant alignment; the `MissionLoopUnitBuilder.cs` span computation (`:171-178`, `spanStartUT = min member StartUT`) and the member-window construction should be cross-checked against the actual recording.
- **Park->transfer Increment-2 residual.** The launch hold closes the launch->escape seam but does NOT address the separate park->transfer / transfer->target seam in the span>synodic case (the pre-existing residual at `ReaimPlaybackResolver` around the park re-phase / window overlap). Confirm in the s15 playtest whether that residual is now the dominant remaining artifact and whether it needs a follow-up. The new sub-degree park re-phase residual from the `H_N` deferral (5.3) is additive but negligible (~0.84 deg max for Kerbin).
- **Watch-camera before launch.** When the pre-launch window resolves below the member activation start, the existing absence path's keep-watched-owner-alive fallback (`GhostPlaybackEngine.cs:2388-2403`) already holds the camera (hidden, not destroyed) through the `< 6 h` pre-launch window. If the contingency `HiddenPreLaunchHold` routing is used instead, wire the same fallback there (mirror `GhostPlaybackEngine.cs:2337-2352`). UNVERIFIED in-game; item 3 of the checklist covers it.
- **Defense-in-depth clamp ordering with two holds.** The current clamp (`GhostPlaybackLogic.cs:7471-7472`) handles a single hold. Extending it to `compressedSpan + arrivalHold + launchHold` and capping the launch hold first is specified but UNVERIFIED (the two-hold path does not exist yet). The clamp can never trip for the real re-aim case (`cadence >> span + 2*T_sid`), but the bound is enforced because the clock is shared by all playback.
- **No alignment-off setting.** Like PadAlignLaunch (no toggle), the launch hold runs whenever re-aim is engaged AND `!pad.Applied` AND a launch leg exists. If a global "disable launch-pad alignment" escape hatch is later wanted, gate both PadAlignLaunch and the launch hold on it together. Decision deferred; not required for the fix.
