# Design (pre-code, FOR ADVERSARIAL REVIEW): re-aim self-overlap scheduling for span > synodic

*Status: DESIGN ONLY. No production code written. Milestone branch `reaim-self-overlap-scheduling`,
based on `origin/reaim-heliocentric-parking-departure` (PR #1166 - the classifier-engage fix). Ships
TOGETHER with / supersedes #1166. This is the substantial engine-integration milestone the s15
investigation escalated to after Approaches B (park compress) and C (decoupled window clock) were each
proven wrong (see `docs/dev/design-reaim-heliocentric-parking-departure.md` and the memory note
`project_s15_looped_duna_reaim_decline`).*

## 0. POST-REVIEW OUTCOME + INCREMENT PLAN (2026-06-16)

Two clean-context adversarial reviews + a self-confirmation against the code established:

- **The cycle == window identity (Section 2.2) is mathematically CORRECT** (verified against
  `ComputeMemberOverlapScheduleStartUT` + `GetActiveCycles` + `Plan.phaseAnchor`; `ceil(span/synodic)=2`).
- **The park re-phasing geometry is CORRECT**, with a corrected formula (Section 5 below):
  `LAN += Delta_lon(c)`, `Delta_lon(c) = omega_parent * (D_c - RecordedDepartureUT)`,
  `omega_parent = 360 / OrbitPeriod(launchBody)` deg/s, `D_c = schedule.DepartureUTForWindow(c)`. This
  is a PER-WINDOW ~48.5deg rotation; the 239deg the probe measured is a SYMPTOM of rendering at the
  wrong (span-clock) window, NOT the rotation target. `longitudeOfAscendingNode`/`inclination` are in
  DEGREES (`OrbitSegment`), reconstruction `new Orbit(inc,e,sma,lan,argPe,mEp,epoch,body)`, so the LAN
  add is exact for inc~0.
- **CONFIRMED BLOCKER (re-scopes the milestone):** the per-instance overlap map/TS presence
  (`overlapInstanceVessels`) has **NO per-frame orbit refresh** - `EnsureOverlapInstances` only
  creates/destroys, and `GhostOrbitIconDrivePatch` only `UpdateFromUT`s the already-seeded conic (it
  cannot change `referenceBody`). The per-frame body-transition refresh
  (`RefreshTrackingStationGhosts` -> `UpdateGhostOrbitForRecording`/`UpdateGhostOrbitFromStateVectors`)
  runs ONLY on the per-INDEX path. So the per-instance overlap presence has only ever rendered
  SAME-BODY loops (logistics routes, station loops); it CANNOT render a body-changing interplanetary
  ghost (Kerbin->Sun->Duna) per cycle. (`ShadowRenderDriver` is decision-only shadow gated on
  `mapRenderTracing`; it does NOT paint the orbital conic line - the stock `OrbitRenderer` on each
  ProtoVessel's `OrbitDriver` does, driven by `GhostOrbitIconDrivePatch`.) Approach A therefore
  requires BUILDING a new per-frame body-transition refresh for overlap map/TS instances, routed
  through a windowed resolver - substantial NEW work on the shared map-render path, NOT "reuse existing
  machinery."

**Maintainer decision (2026-06-16): park-rotation kernel first, then the full build.** Split into two
increments:

### Increment 1 (THIS PR - low-risk, regression-safe, shippable)
Park re-phasing on the EXISTING single-instance per-index re-aim path + the regression fence.
**Does NOT set the overlap gate** (Change 1) - the mission stays single-instance (`overlapCadence =
span`), so the broken per-instance overlap path is NOT triggered. The park rotation (Change 5) applies
inside `BuildWindowSegments` -> `ReplaceHeliocentricLeg`, which the per-index path already drives. Net
effect: the 239deg verbatim-park teleport is removed (park connects to the escape + transfer), and the
FIRST synodic window (k=0, where the span-clock window coincides with the synodic window) renders fully
correct. Later windows stay internally-consistent (park connected to transfer) but collectively drift
off live Kerbin by the span-vs-synodic error - that residual is what Increment 2 (overlap) fixes.
Scope: the pure rotation helpers + the assembler `parkDeltaLonDeg` param + the resolver wiring (gated on
`plan.DepartedFromHeliocentricPark` + a near-equatorial inc guard, fail-closed to faithful) +
P0 regression fence (span<synodic / direct transfer byte-identical) + logging + docs. Validates the
`Delta_lon` math in-game before the bigger build.

### Increment 2 (follow-up PR - the full self-overlap build)
Changes 1-4: builder overlap gate (`overlapCadence=synodic`), resolver re-key to `(member, window)` +
explicit-window overload, the NEW per-frame body-transition refresh for overlap map/TS instances routed
through the windowed resolver, synodic pad-align, the Q-OVERLAP-REMAP decision (loiter-cut/arrival-hold
on the overlap path), and the KSC forced-overlap behavior. This is where "transfer touches live Kerbin
at EVERY window + N per-cycle correct orbit lines/icons" is achieved. The OPEN QUESTIONS in Section 6
remain the Increment-2 agenda.

---

## 1. The established problem (do NOT re-litigate)

Save `s15`, looped mission "Kerbal X #2" (tree `ced78481`, transfer member `aa48920e`, committed member
index 44): a Kerbin->Duna landing whose recorded transfer departs from a **heliocentric PARKING orbit**
(escape into a near-circular co-orbital solar orbit, coast ~1.4 revs / ~152 days, then burn for Duna).
#1166's classifier now ENGAGES re-aim on this two-burn departure (good). But the render is broken
because the recorded loop SPAN exceeds the Kerbin-Duna SYNODIC period.

**Live numbers (from `logs/2026-06-15_2220_reaim-helio-park-render/KSP.log`, `ENGAGED re-aim` line):**

| quantity | value | note |
|---|---|---|
| `synodic` | 19,645,697 s | ~227 d (Kerbin-Duna) |
| `cadence` (CadenceSeconds) | 23,285,417 s | == span (~270 d); `= max(span, synodic)` |
| `span` | 23,285,417 s | spanEnd - spanStart |
| `D0` (FirstDepartureUT) | 2,580,716,597 | first synodic window |
| `phaseAnchor` | 2,566,923,174.68 | == `sched.PhaseAnchorUT` (NOT shifted) |
| `tof` | 9,384,036 s | ~108.6 d heliocentric transfer leg |
| `loiterCuts` | 1 (`cutSeconds=43964`) | on the **Duna destination loiter**, AFTER departure; `phaseAnchor` unchanged |
| span / synodic | **1.185** | `ceil = 2` concurrent instances |

The per-window transfers already SOLVE (`re-aimed transfer ready ... window=0 departUT=D0 ... segs=7
encounter=Duna`; `window=1 departUT=D0+synodic ...`). The defect is purely **WHEN** they render.

### Root cause (confirmed in `ReaimWindowPlanner.cs` + the log)

Re-aim's scheduler assumes `synodic >> span`:
- `ReaimWindowPlanner.Plan`: `cadence = max(span, synodic)` (`:106`); `DepartureUTForWindow(k) = D0 +
  k*synodic` (`:59`).
- `PadAlignLaunch` SKIPS on exactly `cadence != synodic` (`:190`).

When `span > synodic`: `cadence = span != synodic`. The engine relaunches the mission every `span` (the
unit cadence), so the transfer leg renders at `currentUT = D0 + k_engine*span`, which does NOT equal any
synodic window `D_k = D0 + k*synodic` for `k >= 1`. Result (all three symptoms from ONE root): the
re-aimed transfer renders `~142 deg*k` off live Kerbin (`k*(span-synodic) = k*3,639,720 s` of Kerbin
orbital motion), the launch is not pad-aligned, and the verbatim park renders `~239 deg` off live
Kerbin. EVERY real heliocentric-park Kerbin->Duna mission is `span > synodic` (the park alone is ~67% of
the synodic), so #1166's classifier-engage fires every such mission into this broken render.

### Why Approaches B and C are dead (do NOT retry)

- **B (compress the park below synodic): FATALLY FLAWED.** `MissionLoopUnitBuilder` consumes
  `effectiveCadence = Math.Max(span, sched.CadenceSeconds)` using the RAW `span` (`spanEndUT -
  spanStartUT`), which loiter cuts NEVER shrink (cuts only remap inside `TryComputeSpanLoopUT`).
  Compressing the park does not collapse the consumed cadence.
- **C (single-instance + decouple the resolver window clock to synodic): FLAWED.** The render TIME is
  governed by the ENGINE span clock (`TryComputeSpanLoopUT(... effectiveCadence=span ...)`), not the
  resolver's window-pick. Decoupling which-window-the-resolver-picks does not change WHEN the engine
  renders it (still `D0 + k_engine*span`), so the transfer stays ~142 deg off for `k >= 1`. Verified at
  the playtest's cycle-1 render the decoupled clock resolves to the SAME window as today (a no-op).

## 2. The chosen approach: A (self-overlap). The overlap cost is INHERENT.

To render the transfer at synodic windows, the engine must RELAUNCH the mission every synodic
(`overlapCadence = synodic`). Since `span > synodic`, consecutive instances OVERLAP (`ceil(1.185) = 2`
live at once) - unavoidable for a mission longer than its own transfer window. This is exactly the
self-overlapping-mission case Parsek already supports for non-re-aim loops.

### 2.1 KEY ARCHITECTURE DISCOVERY (de-risks the whole milestone)

There is ALREADY a **per-instance overlap map/Tracking-Station presence system**
(`GhostMapPresence.overlapInstanceVessels`, keyed by `(int recIdx, long cycle)`; design
`docs/dev/plans/maprender-overlap-per-instance.md`, "slice i"; mirrors `ParsekKSC.UpdateOverlapKsc`).
It creates **ONE map ProtoVessel per live overlap cycle**, runs in BOTH the flight map
(`UpdateFlightMapGhostLifecycle`) and the Tracking Station (`UpdateTrackingStationGhostLifecycle`) via
`RunOverlapPerInstanceSweep`, and ENGAGES on **MISSION-UNIT self-overlap** -
`IsUnitOverlapMember -> GhostPlaybackLogic.UnitMemberOverlaps(unit) == (span > 0 &&
OverlapCadenceSeconds < span)`. It is the SOLE create/destroy authority for overlap recordings (the
legacy per-index passes skip overlap indices).

So the moment the builder sets `OverlapCadenceSeconds = synodic` for this unit, the flight map and TS
already render N=2 per-cycle ProtoVessels with per-cycle orbit lines + icons. **The visible render
surface (map + TS) is per-instance-capable today; it just isn't fed re-aimed geometry per cycle.**

### 2.2 The cycle == window identity (the load-bearing math, VERIFY in review)

Overlap cycle `c` for this member launches (live) at:

```
scheduleStartUT  = ComputeMemberOverlapScheduleStartUT(phaseAnchor, spanStart, memberStart)
                 = phaseAnchor + (memberStart - spanStart)
cycleStartUT(c)  = scheduleStartUT + c * effectiveCadence
```

For the single-recording s15 mission `memberStart == spanStart`, so `scheduleStartUT == phaseAnchor`.
With `OverlapCadenceSeconds = synodic`, `effectiveCadence = ComputeEffectiveLaunchCadence(synodic, span,
cap=20) = synodic` (since `cadenceFloor = span/20 << synodic`). The instance's transfer departs (recorded
`RecordedDepartureUT` rendered at live time) at:

```
liveDepart(c) = cycleStartUT(c) + (RecordedDepartureUT - spanStart)
              = phaseAnchor + c*synodic + (RecordedDepartureUT - spanStart)
```

`Plan` sets `phaseAnchor = D0 - (RecordedDepartureUT - spanStart)`. Substituting:

```
liveDepart(c) = D0 - (RecDep - spanStart) + c*synodic + (RecDep - spanStart) = D0 + c*synodic = D_c
```

**So overlap-cycle index `c` departs at exactly the synodic window `D_c`: cycle index == re-aim window
index, an identity.** Confirmed against the log: window 0 `departUT = D0`, window 1 `departUT =
D0+synodic`. The resolver's job becomes "given window/cycle `c`, build window `c`'s transfer geometry."

## 3. The five changes (mapped to touch points)

### Change 1 - builder: overlapCadence = synodic, gated

`MissionLoopUnitBuilder.cs` re-aim branch (`:444-572`). Today: `effectiveCadence = Math.Max(span,
sched.CadenceSeconds)` and `effectiveOverlapCadence = effectiveCadence` (`:452-453`, no overlap).

Add, gated on `span > sched.SynodicPeriodSeconds && plan.DepartedFromHeliocentricPark`:
```
effectiveOverlapCadence = GhostPlaybackLogic.ComputeEffectiveLaunchCadence(
    sched.SynodicPeriodSeconds, span, GhostPlayback.MaxOverlapMissionInstances);
```
Leave `effectiveCadence` (CadenceSeconds, the span clock) = span (single-instance scenes / the engine's
no-overlap branch keep the full span). When the gate is false (`span <= synodic`, or a direct transfer),
`effectiveOverlapCadence` stays `= effectiveCadence` -> `UnitMemberOverlaps` false -> byte-identical to
today.

This is the existing `MaxOverlapMissionInstances` cap-clamp pattern (`:205-206`), applied to the synodic
period instead of the user period. `ceil(span/synodic) = 2 <= 20`, so no clamp bites.

### Change 2 - resolver: re-key cache to (member, window) + explicit-window overload

`ReaimPlaybackResolver.cs`. Today the cache is a single slot per `memberId` (`:35`,
`cacheByMember[memberId]`) and the window is derived from `currentUT` via `TryComputeSpanLoopUT`
(`:110-117`). Under overlap, two concurrent instances need TWO windows resolved at once.

- Re-key `cacheByMember` -> `cacheByMemberWindow` keyed `(string memberId, long window)`.
- Keep the existing `currentUT`-based `TryResolveWindowSegments` / `ResolveForFrame` (used by the
  flight per-member substitution and the per-INDEX non-overlap map path) - they compute the window from
  `currentUT` exactly as today, then read/fill the `(member, window)` cache. **Byte-identical for
  span < synodic** (one window in flight at a time).
- ADD an explicit-window overload `TryResolveWindowSegmentsForWindow(memberId, memberSegments, plan,
  schedule, long window, out segments)` that skips the span-clock window derivation and calls
  `BuildWindowSegments(memberId, ..., window)` directly, caching by `(member, window)`. The per-instance
  overlap map path calls this with `window = cycle`.

`BuildWindowSegments` is already pure in the window index (`:139`, `D_k =
schedule.DepartureUTForWindow(windowIndex)`); only the cache key + a new entry point change.

### Change 3 - per-instance overlap map/TS: feed re-aimed geometry per cycle

`GhostMapPresence.cs`. The per-instance overlap create (`CreateOverlapInstanceVessel`, `:11851`)
resolves the orbit source at the cycle's `effUT` via `ResolveMapPresenceGhostSource` but uses the RAW
recorded segment - it does NOT call `TryResolveReaimedCoveringSegment` (that swap lives only on the
per-INDEX path, `:12415`, because re-aim units never overlapped before).

- In `CreateOverlapInstanceVessel`, after `ResolveMapPresenceGhostSource` returns a `Segment` source,
  resolve the re-aimed covering segment for THIS cycle's window: call the new explicit-window resolver
  with `window = cycle`, search the returned list at `effUT` (mirror
  `TryResolveReaimedCoveringSegment`'s `FindOrbitSegmentForMapDisplay(effective, effUT)`), and if the
  re-aim owner has no covering segment at `effUT` (trim gap / declined window) SKIP the create (return
  null - keep the cycle pending), exactly as the per-index path does.
- Verify whether the per-instance path has a PER-FRAME orbit refresh as `effUT` advances across SOI
  boundaries (Kerbin escape -> Sun transfer -> Duna capture, where the orbit BODY changes). The
  create-only path seeds one orbit; a refresh site, if present, must ALSO route through the windowed
  resolver for `window = cycle`. **OPEN - see Q3.**
- The marker/polyline join (`TryGetLiveOverlapHeadUTs`, `:12144`) already yields per-cycle head UTs and
  is geometry-agnostic, so it needs no re-aim change.

The legacy per-INDEX re-aim path (`ResolveEffectiveMapOrbitSegments` / `TryResolveReaimedCoveringSegment`
at `currentUT`) stays for the NON-overlap (span < synodic) case, byte-identical. When the unit overlaps,
the per-index passes hand off to the per-instance sweep and skip the index (existing slice-i contract),
so there is no double-create.

### Change 4 - synodic pad-align for the span > synodic case

`ReaimWindowPlanner.PadAlignLaunch` (`:158`) skips when `cadence != synodic` (`:190`). Under overlap the
relevant cadence IS the synodic period (the overlap cadence), so the guard should pass when fed the
synodic cadence. Add a gated synodic-quantized pad-align: quantize `SynodicPeriodSeconds` + `D0` (and the
overlap cadence) to the launch body's sidereal day, leaving the span-clock `CadenceSeconds` = span. The
cycle==window identity (Section 2.2) is preserved because pad-align moves `phaseAnchor` and `D0` by the
SAME `delta` (`:210-211`), and the overlap cadence is set to the quantized synodic. **OPEN - exact
overload shape, Q4.**

### Change 5 - park LAN re-phasing into the live frame

The verbatim heliocentric park renders ~239 deg off live Kerbin (Sun-inertial, pinned to recorded
longitude, unlike the body-relative escape/capture legs). Rotate each park OrbitSegment by
`Delta_lon(k)` so it sits at the live Kerbin solar longitude:

```
Delta_lon(k) = omega_parent * loopShift(k)            // applied as LAN += Delta_lon (degrees)
omega_parent = 2*pi / bodyInfo.OrbitPeriod(launchBody) // NOT RotationPeriod
```

`Rz(LAN + d) = Rz(d) * Rz(LAN)` rotates a near-equatorial orbit's position at every preserved mean
anomaly by `d` about the parent reference-plane normal - exact for inc ~ 0. Apply inside
`ReaimSegmentAssembler.ReplaceHeliocentricLeg` in the `else`-branch park pass-through (`:127-129`),
BEFORE the `Sort` / `CoalesceSameOrbitFragments` (`:136-142`), so both park fragments get the same
`Delta_lon` and all renderers (flight mesh, map line, map icon, TS) read the one rotated list. Gate on
`plan.DepartedFromHeliocentricPark` + a near-equatorial inc guard; **fail-closed** (decline-to-faithful,
NEVER the 239 deg verbatim park) on degenerate `omega` / non-equatorial park. The recorded data is never
touched (render-time rotation on the synthesized live copy only). **OPEN - the exact `loopShift(k)`
reference + sign, Q1/Q2.**

DEFERRED (out of scope, only if trivial): `r1 = park-end` so the transfer departs from the rotated
park-end (closes the residual ~26 deg co-orbital-drift seam). Reviewers (#1166 cycle) rated it
high-risk: park-orbit velocity for the hardened near-180 `launchPlaneNormal` axis + tof re-centering +
a 6.1e9 m perturbation to a Lambert leaning on a proximity fallback. Ship A without it (26 deg << 239
deg); design `r1 = park-end` separately if the seam reads badly in playtest.

## 4. Regression fence (load-bearing, must stay green throughout)

- **P0 FIRST test:** a `span < synodic` re-aim mission (Duna One direct transfer,
  `DepartedFromHeliocentricPark == false`) -> the builder leaves `effectiveOverlapCadence ==
  effectiveCadence` (no overlap), no park rotation, the resolver resolves one window from `currentUT`
  exactly as today. Byte-identical. Pin the builder gate + the resolver currentUT path against this.
- Direct transfers never enter the new branch (the gate short-circuits at `span <= synodic`), so every
  working span < synodic mission (Duna One, Mun/Minmus same-parent, the M-MIS eccentric-target work,
  M4b/M4c station loops) stays untouched.
- The existing `ReaimClassifierTests` (+12 on #1166) and the resolver/assembler/window-planner suites
  must stay green.

## 5. Phased plan (failing-test-first per phase)

0. **P0 regression fence** (Section 4) - write FIRST, keep green.
1. **Builder gate** (Change 1) - unit test: gate true -> overlapCadence == synodic; gate false ->
   overlapCadence == effectiveCadence (byte-identical). Log assertion on a new `SELF-OVERLAP` line.
2. **Resolver re-key + explicit-window overload** (Change 2) - unit test: `(member, window)` cache
   isolates two windows; explicit-window overload returns window `c`'s geometry; currentUT overload
   unchanged.
3. **Per-instance overlap re-aim wiring** (Change 3) - the heavy one. Unit-test the pure decision
   (covering-segment-for-window) where possible; in-game `ReaimEndToEndInGameTest` extension for the
   live per-cycle resolution.
4. **Synodic pad-align** (Change 4) - unit test the new overload (quantize preserves cycle==window).
5. **Park rotation** (Change 5) - pure rotation-helper unit tests (rotate a synthetic park, assert the
   position rotates by `Delta_lon`; non-equatorial / degenerate -> fail-closed).
6. **In-game s15 re-fly** (the maintainer flies it; acceptance in Section 7).

## 6. OPEN QUESTIONS / BLOCKERS for the adversarial review to NAIL

The reviewers must return a verdict (BLOCKER / fix-and-proceed / accept) on each, with repo evidence:

- **Q-OVERLAP-REMAP (top risk).** `TryResolveUnitOverlapSchedule`'s doc (`GhostMapPresence.cs:11445`)
  states the overlap path uses the RAW schedule with `ComputeOverlapCyclePlaybackUT`, deliberately NOT
  the span-clock `ResolveTrackingStationSampleUT` path that applies **loiter-cut / arrival-hold /
  re-aim remapping** - because "re-aim / zero-drift units are non-overlapping by construction." Routing
  re-aim THROUGH overlap breaks that assumption. For s15 `loiterCuts=1 (cutSeconds=43964, the Duna
  destination loiter)`. Does the overlap path therefore drop the loiter-cut compression (Duna loiter
  plays full length) and any arrival-hold? Is that an acceptable behavior change for the
  render-correctness goal, or must the overlap effUT also decompress through the loiter cuts? Does the
  same apply to the FLIGHT engine overlap path (`UpdateOverlapPlayback`)? Decide: accept + document, or
  thread the remap into the overlap effUT.
- **Q1 / Q2 - park rotation math.** Pin the exact `loopShift(k)` and its reference times so the rotated
  park's position at each rendered UT equals the live-frame Kerbin solar longitude, AND reconcile it
  with the transfer's `shift = RecordedDepartureUT - D_k` (`ReaimPlaybackResolver.cs:224`). Confirm `LAN
  += Delta_lon` suffices for the near-circular co-orbital park (inc ~0) vs a full (inc, LAN, argPe, mEp)
  transform. Per-window (`Delta_lon` differs per cycle) - confirm it is computed per cycle in the
  per-instance path (where the rotation must be applied with `window = cycle`, NOT a single live shift).
- **Q3 - per-instance orbit refresh across SOI.** Does the per-instance overlap path
  (`CreateOverlapInstanceVessel` + any per-frame refresh) re-resolve the covering orbit segment as an
  instance's `effUT` crosses SOI boundaries (Kerbin -> Sun -> Duna, body change)? Name the refresh site
  (if any) and confirm the re-aim windowed resolution must be wired there too, not just at create. If
  there is NO per-frame body-transition refresh for overlap instances, how does an overlap interplanetary
  ghost transition bodies at all today (is the create-time covering segment the whole story, with the
  icon-drive patch handling the rest)?
- **Q4 - synodic pad-align overload.** Confirm the pad-align must quantize the SYNODIC/overlap cadence
  (not the span-clock CadenceSeconds), and that moving `phaseAnchor`+`D0` by the same delta preserves
  cycle==window. Is pad-align even necessary for the map render (the park ascent is body-fixed; does the
  map even show the launch ascent, or only the orbital legs)? If the ascent seam is not a map artifact,
  pad-align may be deferrable - decide.
- **Q5 - flight 3D-mesh residual (accept?).** The flight ENGINE overlap path
  (`UpdateOverlapPlayback`/`UpdateExpireAndPositionOverlaps`) positions every 3D mesh instance from the
  single `cachedTrajectories[i]` (one window from `SubstituteReaimTrajectories`). So the in-world 3D
  ghost meshes share one window's transfer orientation across instances. For an interplanetary mission
  these meshes are at map scale / far LOD (you are flying your own craft, not near the ghost), so the
  VISIBLE surface is the map/TS ProtoVessels (per-instance correct). Confirm the 3D-mesh residual is
  out of scope (matches "renders correctly on map / Tracking Station"), and that
  `SubstituteReaimTrajectories` does not CRASH or mis-key the re-keyed cache when the unit overlaps.
- **Q6 - cross-member chains.** The s15 mission is a single recording (`memberStart == spanStart`). A
  chained mission (separate launch / transfer / arrival / debris members) has `memberStart != spanStart`,
  so `scheduleStartUT = phaseAnchor + (memberStart - spanStart)`. Re-derive cycle==window for the
  transfer member in that case. Is the milestone scoped to single-member missions, or must the chain
  case work? (The task scope is the s15 single-recording case; confirm chains stay faithful / are not
  regressed.)
- **Q7 - KSC scene.** The Space Center scene has its own overlap path (`ParsekKSC.UpdateOverlapKsc` /
  `kscOverlapGhosts`). Does it engage on mission-unit self-overlap, and does it need the re-aim
  windowed resolution too, or is KSC out of scope (task = map / Tracking Station)?
- **Q8 - resolver cache unboundedness.** Re-keying to `(member, window)` makes the cache grow one entry
  per advancing window. `ReaimPlaybackResolver.Clear()` is called on every loop-unit rebuild
  (`DriveMissionLoopUnits` / `ParsekKSC` / `ParsekTrackingStation`), but between rebuilds at high warp
  the window advances every frame. Bound the cache (evict windows outside `[firstCycle-1, lastCycle]`)
  or confirm the rebuild cadence keeps it bounded.

## 7. In-game acceptance (the maintainer flies it)

Re-fly s15 "Kerbal X #2" (loop on), `mapRenderTracing` on:
- Launch pad-aligned (or the ascent seam confirmed not a map artifact, Q4).
- The re-aimed transfer touches live Kerbin at EVERY window (no `142 deg` drift, no icon-teleport from
  this cause); N=2 per-cycle map/TS icons + orbit lines, each on its own window's transfer.
- The park renders near live Kerbin (no `239 deg` gap), connected to the escape + transfer.
- The transfer reaches Duna.
- The working span < synodic missions running alongside (Duna One, Mun) are UNAFFECTED.
- (bonus) P4 `dest-trim` behavior on the Duna loiter is observed/explained given Q-OVERLAP-REMAP.

## 8. Discipline

Recorded data IMMUTABLE (render-time rotation only). Failing-test-first per phase; every new method
gets unit/log tests; verbose logging mandatory (a new `SELF-OVERLAP` Info line at the builder gate, a
per-cycle window line at the per-instance re-aim resolve). Do NOT modify PR #1155
(`reaim-dest-loiter-retimer`). The Kerbin-SOI warp-reseed icon-teleport + the body-fixed launch seam are
a SEPARATE session's scope. Land via the milestone branch + a PR that ships together with / supersedes
#1166.
