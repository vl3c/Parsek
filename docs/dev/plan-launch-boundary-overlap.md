Below is the final, reviewed, implementation-ready plan. It is feasible. I read the code to settle every disputed point; where a reviewer was wrong I say so with file/line evidence.

---

# FINAL PLAN: Boundary-Overlap Launch Render (close the zero-slack launch->escape seam)

Status: DESIGN ONLY, reviewed. Branch `launch-escape-seam-render`. Scope: render/playback-time only; recorded data IMMUTABLE.
Failing class: zero-slack loops of mission s15 / Kerbal X #2 (recording aa48920e), `cadence == span > synodic`, `launchHoldEngaged == true`. When the per-loop Duna arrival hold `W_{N-1}` fills the launching cycle, `slack_{N-1} == 0`, `ComputeCappedLaunchAdvanceSeconds` caps the launch advance to 0, no realignment happens, and the launch->escape seam stays open (`residualDeg` up to ~211 at loop 19).

## Verdict and approach choice: ADOPT DESIGN B (with one correction). Design A is rejected.

The two reviews both pick B and both are right. I confirmed the two decisive facts against the code:

1. **Design A inverts the primary, which yanks the camera.** Today region B of `TryComputeSpanLoopUT` already does `cycleIndex += 1` and returns the early-launching instance N+1 as the *single* output (`GhostPlaybackLogic.cs:7704-7716`). That single output feeds every primary consumer: the watch retarget (`LogUnitTransitionIfChanged`, `GhostPlaybackEngine.cs:2304-2306`), the cycle-change ghost rebuild (`:2456-2462`), the TS sampler (`ResolveTrackingStationSampleUT`, `GhostPlaybackLogic.cs:8009`), the map-presence single-instance create (`ResolveMapPresenceSampleUT`, `GhostMapPresence.cs:11201`), and the polyline head (`GhostTrajectoryPolylineRenderer.cs:3124`). The cap currently keeps that early flip benign (`delta <= slack`, a tiny window). With the cap removed, Design A's "primary = N+1, secondary = N" drags every primary reader onto the fresh launch up to `T_sid` (~6h) before the cycle boundary  -  an active camera/through-line regression. Design B's "primary = continuing instance N, secondary = N+1 launch" keeps all primary readers on the long-lived through-line with zero new logic. CONFIRMED.

2. **Design A routes the wrong instance through the wrong map path.** Design A's secondary is instance N, mid-heliocentric-transfer  -  geometry that MUST be re-aimed. But `CreateOverlapInstanceVessel` (`GhostMapPresence.cs:11851`) resolves its orbit via `ResolveMapPresenceGhostSource` + `ApplyOrbitToVessel` on the RAW recorded covering segment and does NOT apply the re-aim swap (`TryResolveReaimedCoveringSegment`); that swap exists ONLY on the single-instance create path (`GhostMapPresence.cs:12415-12419`). Routing the re-aimed transfer through the overlap-instance path would render the recorded transfer at the wrong heliocentric position. Design B's secondary is instance N+1's in-SOI launch+escape, whose escape conic is the recorded Kerbin body-relative segment (`ReaimSegmentAssembler` passes body-relative legs through unchanged, `ReaimSegmentAssembler.cs:19,266`)  -  exactly what the raw-segment overlap-instance path produces correctly. CONFIRMED.

**One correction to Design B (a real correctness improvement, not a nitpick): keep the boundary overlap gated on `delta_N > slack_{N-1}`, shipping the secondary ONLY on loops that actually need it.** Both reviewers raised this as the conservative fallback (engine risk #2, render risk re slack>0). I make it the *default*, not the fallback, because it is provably zero-regression-surface on the working majority of loops and it does not weaken the fix: every loop with a residual seam (`residualDeg > 0`) gets the boundary overlap; every already-aligned loop is byte-identical to today. Details in section 7.

---

## 1. The dual-clock model (Design B section 2, adopted)

`TryComputeSpanLoopUT` keeps returning ONE primary loopUT for the whole cycle and gains an OPTIONAL secondary during the borrow window:

- **Primary** = the continuing instance N (region-A formula extended across the whole cycle): `phaseFromLaunch = phaseInCycle + delta_N`, SOI-repay + arrival-hold composed exactly as region A does today. Valid for the whole cycle. This is the long-lived through-line the camera follows; near the borrow window it is far downstream (post-SOI, near Duna).
- **Secondary** (present only inside the borrow window `phaseInCycle >= cadence - advNext`, when the overlap gate is on) = the next instance N+1's early launch: `phaseFromLaunch = phaseInCycle - (cadence - advNext)` (0 at the early launch), its own SOI-exit repay, `cycleIndex = N+1`. This is the just-launched in-SOI ascent/escape, far from instance N.

This CHANGES today's behavior in the borrow window: today region B switches the single output to N+1; with this plan the primary stays on N and the secondary carries N+1. That is the whole point and it is what keeps the camera on the through-line.

**Cycle-boundary continuity (Design B section 2.5, verified):** at `phaseInCycle -> cadence`, secondary `phaseFromLaunch -> advNext`; at `phaseInCycle = 0` of cycle N+1 the new primary has `phaseFromLaunch = 0 + delta_{N+1}`. Equal. The secondary of cycle N hands off seamlessly to the primary of cycle N+1 (same instance, same loopUT). The boundary-rollback skip at `GhostPlaybackLogic.cs:7813` already keys on `launchAdvance > 0` and is unchanged.

---

## 2. Clock layer  -  `GhostPlaybackLogic.cs`

### 2.1 New struct + new entry point, leave `TryComputeSpanLoopUT` byte-identical for the primary

Add a `SpanLoopFrame` struct (near the `LoopUnit` definition) and a new pure `ComputeSpanLoopFrame` that is the body of today's `TryComputeSpanLoopUT` with two changes:

- `launchAdvance` (region A) and `advNext` (region B) use a NEW `ComputeBoundaryOverlapAdvanceSeconds` instead of `ComputeCappedLaunchAdvanceSeconds`. See 2.2 for what that helper computes (it is NOT simply uncapped  -  it applies the `delta > slack` gate).
- Region B no longer mutates the primary's `cycleIndex`. The primary stays instance N for the whole cycle (region-A formula). Region B's early launch is emitted as the SECONDARY (`HasSecondary`, `SecondaryLoopUT`, `SecondaryCycleIndex = N+1`), computed by the region-B formula with its own SOI-exit repay.

```
internal readonly struct SpanLoopFrame {
    bool   Resolved;
    double LoopUT;               // primary (continuing instance N), region-A semantics
    long   CycleIndex;           // primary cycle = N
    bool   IsInInterCycleTail;
    bool   HasSecondary;         // a concurrent early-launch instance is live this frame
    double SecondaryLoopUT;      // instance N+1 early-launch loopUT
    long   SecondaryCycleIndex;  // = N+1
}
internal static SpanLoopFrame ComputeSpanLoopFrame(/* same args as TryComputeSpanLoopUT */)
```

`TryComputeSpanLoopUT` becomes a thin wrapper: calls `ComputeSpanLoopFrame`, returns `(Resolved, LoopUT, CycleIndex, IsInInterCycleTail)`  -  the PRIMARY only. Every one of the existing callers (verified: `ReaimPlaybackResolver.TryResolveWindowSegments` with `schedule:null`, `DecideUnitMemberRender`, the watch clock, KSC, loop-synced-debris) is then byte-identical when the overlap gate is off, and on slack>0 loops (where the gated advance equals today's capped advance) the primary equals today's region-A render. This is the targeting/regression fence (section 8).

### 2.2 The advance helper: gated, not blanket-uncapped

Add `ComputeBoundaryOverlapAdvanceSeconds(phaseAnchorUT, spanStartUT, spanEndUT, cadence, win, T_sid, loiterCuts, arrivalHoldSeconds, arrivalAlignPeriod)`:

```
rawDelta    = ComputePerLoopLaunchAdvanceSeconds(..., win, ...)   // delta_win in [0, T_sid)
cappedDelta = ComputeCappedLaunchAdvanceSeconds(..., win, ...)     // min(rawDelta, slack_{win-1})
// Boundary overlap engages ONLY when the cap actually bites (the residual-seam loops).
return (rawDelta > cappedDelta + 1e-9) ? rawDelta : cappedDelta;
```

- On a slack>0 loop where `rawDelta <= slack`, `cappedDelta == rawDelta`, so this returns the same value as today: **byte-identical primary, no secondary** (region B's `advNext` equals today's `advNext`, the early launch starts inside the previous instance's parked idle tail exactly as it does today, single-instance). Zero regression surface.
- On a zero/low-slack loop where `rawDelta > slack`, this returns the FULL `rawDelta` (uncapped), the seam closes, and a secondary is emitted for the overlap window `[cadence - rawDelta, cadence)`.

Bound: `rawDelta < T_sid` by construction (`ComputePerLoopLaunchAdvanceSeconds` returns `[0, T_sid)`), so the borrow window is `<= T_sid` ~6h. No new constant is needed (Design A's `BoundaryOverlapMaxLeadSeconds = +Inf` was meaningless; `T_sid` is already the natural bound  -  I drop that constant).

`ComputeCappedLaunchAdvanceSeconds` is KEPT: it is the source of truth for "where the secondary's early launch starts" (region B still launches the secondary at `phaseInCycle = cadence - advNext`, and the diagnostic `residualDeg = (rawDelta - cappedDelta)` reads it). It is NOT renamed (Design B's rename-to-uncapped was rejected by render risk #4 as silently changing other readers; I keep it intact and add the new helper alongside  -  Design A's "keep-and-add" stance is correct here).

### 2.3 Diagnostics

The existing per-loop Verbose line (`GhostPlaybackLogic.cs:7767-7774`) stays. With the gated helper: on a now-engaged loop, `capped=false` and `residualDeg ~ 0` for the PRIMARY (the advance is the full delta); add a `secondaryActive=true secondaryLoopUT=...` field when `HasSecondary`. The `launch-advance-capped` WARN no longer fires on engaged loops (the seam closes); keep it firing on the (now impossible for re-aim, but defensive) genuinely-uncloseable case. Add a one-shot per-mission Verbose `boundary-overlap engaged` line. All rate-limited per mission identity (`phaseAnchorUT + spanStartUT`), per the per-frame purity contract.

### 2.4 `DecideBoundaryOverlapSecondaryRender`

Add a sibling of `DecideUnitMemberRender` (`GhostPlaybackLogic.cs:7945`) that calls `ComputeSpanLoopFrame`, and when `HasSecondary`, runs `IsLoopUTInMemberWindow(SecondaryLoopUT, memberStart, memberEnd)` (`:7909`) to return `Render` / `HiddenOutsideWindow` / `NoSecondary`. This is the testable seam for the engine + map + polyline secondary dispatch, and it gives per-member independence for free: in a multi-member mission the ascent member resolves the secondary in-window while the arrival member resolves the primary in-window.

### 2.5 `ResolveTrackingStationSampleFrame`

Add a sibling of `ResolveTrackingStationSampleUT` (`:8009`) returning `(primaryHidden, primaryUT, hasSecondary, secondaryUT)` via `DecideBoundaryOverlapSecondaryRender`. `ResolveTrackingStationSampleUT` stays as the primary-only wrapper so non-dual callers are byte-identical.

---

## 3. Engine layer  -  `GhostPlaybackEngine.cs`

### 3.1 Secondary render in the no-overlap unit-member branch

In `UpdateUnitMemberPlayback`, the `!UnitMemberOverlaps(unit)` branch (`:2290+`), after the primary's `RenderInRangeGhost` (`:2484`), call `DecideBoundaryOverlapSecondaryRender`. When it returns `Render` AND `unit.LaunchHoldEngaged`, position ONE secondary ghost in `overlapGhosts[i]` at `SecondaryLoopUT` via a new `UpdateBoundaryOverlapSecondary` (3.2). When it does not, tear down any secondary.

### 3.2 The unconditional overlap teardown must be gated (engine blocker #2  -  CONFIRMED VALID)

The current code at `GhostPlaybackEngine.cs:2448-2452` unconditionally destroys `overlapGhosts[i]` on every render frame of a unit member (it encodes "a unit member never carries overlap ghosts"), and the hide/warp branches (`:2151, 2188, 2217, 2314, 2363, 2370, 2414, 2421, 2438`) also call `DestroyAllOverlapGhosts(i)`. A secondary placed in `overlapGhosts[i]` this frame would be destroyed next frame, thrashing every frame across the ~6h window.

Fix:
- Replace the unconditional teardown at `:2448-2452` with: tear down `overlapGhosts[i]` UNLESS this frame resolved a live boundary-overlap secondary for index `i`. Compute the secondary decision BEFORE this point so the gate is available.
- The hide/warp/skip branches keep tearing the secondary down (when the member or its primary hides, no secondary should show either)  -  those are correct as-is for a hidden primary.
- The secondary lives in `overlapGhosts[i]` but is driven by `UpdateBoundaryOverlapSecondary`, NOT `UpdateOverlapPlayback` / `UpdateExpireAndPositionOverlaps`.

### 3.3 `UpdateBoundaryOverlapSecondary`  -  a thin adapter, never the overlap expiry path

```
UpdateBoundaryOverlapSecondary(i, traj, flags, ctx, secondaryLoopUT, secondaryCycle, suppress...) {
  // Warp suppression: the secondary is the in-SOI launching ghost (moving); hide its MESH at high warp.
  if (suppressGhosts && ShouldSuppressGhostMeshAtWarp(ctx.warpRate, traj, secondaryLoopUT)) { DestroyAllOverlapGhosts(i); return; }
  overlaps = overlapGhosts[i] (create if missing)
  // exactly one secondary, keyed by secondaryCycle; rebuild on cycle change
  sec = (overlaps.Count>0 && overlaps[0]?.loopCycleIndex==secondaryCycle) ? overlaps[0] : null;
  if (sec==null) { DestroyAllOverlapGhosts(i); sec = SpawnSecondary(...); sec.isBoundaryOverlapSecondary = true; overlaps.Add(sec); }
  PositionSecondaryAt(i, traj, flags, ctx, sec, secondaryLoopUT, suppressVisualFx);  // factored from the overlap-entry positioning block :3919-3960
}
```

- `SpawnSecondary` reuses `CreatePendingSpawnState(traj, secondaryLoopUT, PendingSpawnLifecycle.OverlapPrimaryEnter, flags)` + `EnsureGhostVisualsLoaded`. `OverlapPrimaryEnter` is the demoted-shell lifecycle that fires no ghost-created/camera side effects (`:3656-3664`), and the secondary's audio is muted via `MuteAllAudio` (`:3666`).
- `PositionSecondaryAt` factors the existing overlap-entry positioning block (`:3919-3960`): `ResolvePlaybackDistance` -> `ApplyZoneRendering` -> `PositionLoopAtPlaybackUT` -> `EmitPostPositionUpdate` -> `ApplyFrameVisuals`, at the explicit `secondaryLoopUT`. **It does NOT run the expiry block (`:3854-3914`).** That is the load-bearing isolation: the expiry block fires `OnOverlapCameraAction` (ExplosionHold) + `OnOverlapExpired` (`:3888-3908`). The secondary never expires that way  -  it is destroyed by `HasSecondary` going false at the boundary, where it has already become the next primary (engine risk #5  -  CONFIRMED VALID, addressed by routing through a dedicated positioner).

### 3.4 Watch/camera isolation (render blocker #3  -  CONFIRMED VALID)

Add `bool isBoundaryOverlapSecondary` to `GhostPlaybackState`. The watch camera keys on `(protectedIndex, protectedLoopCycleIndex)` (verified at `ShouldExitWatchForCoverageRetiredState`/`Cycle`, `:4834-4857`, and the overlap pin at `:2238-2243`). Exclude any state with `isBoundaryOverlapSecondary == true` at the three selection sites: the handoff retarget in `LogUnitTransitionIfChanged`, `ValidateWatchedGhostStillActive`, and the `protectedLoopCycleIndex` pin. Because the no-overlap unit-member handoff already runs off the PRIMARY clock (`:2304`, unchanged), the secondary is simply never offered to the camera. Stamping `sec.loopCycleIndex = secondaryCycle` is fine for the cycle-change rebuild key as long as the `isBoundaryOverlapSecondary` flag is the thing the watch sites check (do not rely on the cycle value to keep it un-watched).

### 3.5 KSC + TS

`ParsekKSC.UpdateUnitMemberPlayback` mirrors 3.1-3.3 ONLY if KSC ever renders the launch leg. KSC is a single-vessel scene with no map; the seam is a map/TS artifact. **Verify (read `ParsekKSC.cs`) that KSC has no map surface; if so, no KSC change** (it forwards the primary only). The Tracking Station has no flight engine  -  its secondary comes through the map-presence layer (section 5), not the engine.

---

## 4. Polyline / conic layer  -  `Display/GhostTrajectoryPolylineRenderer.cs`

The launch->escape seam is half polyline (in-SOI body-fixed ascent) + half conic (escape OrbitSegment). The ascent is a polyline leg; the escape conic is the proto-vessel orbit line (section 5).

### 4.1 Second head in the decide pass

In the `Driver` per-recording loop (`:3124`), after the primary `headUT = ResolveTrackingStationSampleUT(...)`, resolve `ResolveTrackingStationSampleFrame(...)` to get `(hasSecondary, secondaryHeadUT)`. Run the per-leg `ShouldDrawLegAtHeadUT` gate a SECOND time at `secondaryHeadUT`; for any in-window leg NOT already drawn for the primary, enqueue an ADDITIVE `PendingLegDraw` keyed on the secondary's ghost pid, excluded from `anyDrawn` / the `drewNonOrbitalLegRecordings` ownership publish  -  exactly the forward-additive mechanism that already exists (`DecideForwardWindowForRecording`, the `forward=true` exclusion). No new VectorLine, no new leg cache.

### 4.2 Disjoint-leg guarantee + defensive guard (render blocker #2  -  VALID, addressed)

The primary head (instance N, far downstream / Duna approach) and the secondary head (instance N+1, in-SOI ascent) select DISJOINT legs because they are months apart in recorded-span phase. Each leg has exactly ONE cached VectorLine; drawing the SAME leg twice in one frame is a no-op redraw. Two cases:
- The ascent leg is geometrically identical for N and N+1 (both ascend from the same recorded pad track at the same recorded rotation  -  that IS the alignment), so if both heads ever landed in the ascent leg's window it would draw once and be correct anyway.
- Defensive guard (Design B section 11.1, kept): if the two heads land in the SAME leg window (only possible if `compressedSpan < T_sid`, which never happens for an interplanetary transfer  -  `soiExitValid` requires the SOI exit strictly inside the span and the heliocentric tof is months), draw only the primary's leg. One-line `if`.

### 4.3 Bridge-near-meet re-validation (render risk  -  KEEP ON THE WATCH LIST)

The seam-bridge `BridgeMinAngleRadians = 5deg` gate (design-reaim-launch-hold-seam.md section 8) was tuned for the CAPPED residual. On a previously-capped (`residualDeg ~211`) loop the now-closed seam (`residualDeg ~0`) lands the ascent leg on the conic, so the near-meet gate should SKIP the bridge (correct). This is the same behavior the section 8 fix already validated for slack>0 loops; the only new exposure is that zero-slack loops now reach that regime too. Add an in-game assertion that no spurious bridge slice draws at the closed seam on a previously-capped loop (section 9).

---

## 5. Map / TS presence layer  -  `GhostMapPresence.cs`

The secondary needs a ProtoVessel for its icon + escape-conic orbit line during the borrow window. Reuse `overlapInstanceVessels[(recIdx, cycle)]` (`:925`)  -  the proven per-instance map store  -  seeded from the span-clock secondary UT.

### 5.1 A boundary-secondary branch INSIDE the existing sweep (engine blocker #3  -  CONFIRMED VALID, addressed)

The reaper in `RunOverlapPerInstanceSweep` (`:11738-11746`) destroys any `overlapInstanceVessels` entry whose recIdx is not in `drivenThisFrame`. A launch-hold unit is non-overlap (`ShouldDriveOverlapPerInstance` false), so a separate sweep's entry would be reaped every frame.

Fix: do NOT add a separate sweep. Inside `RunOverlapPerInstanceSweep`'s per-recording loop, after the `ShouldDriveOverlapPerInstance` check, ALSO check the boundary-secondary condition: if `unit.LaunchHoldEngaged` and `ResolveTrackingStationSampleFrame` returns a live in-window secondary for this index, ensure ONE `overlapInstanceVessels[(recIdx, secondaryCycle)]` (via `CreateOverlapInstanceVessel` seeded at `effUT = secondaryLoopUT`, `loopEpochShiftSeconds = currentUT - secondaryLoopUT`) and ADD `i` to `drivenThisFrame`. Then the existing reaper spares it, and tears it down automatically the frame the borrow window ends. This reuses the create/seed/reap plumbing verbatim and needs no new reaper logic.

### 5.2 The secondary's conic resolves correctly via the raw-segment path (render blocker #1  -  the reason B works, render blocker #4  -  open item to verify)

`CreateOverlapInstanceVessel` (`:11851`) uses `ResolveMapPresenceGhostSource` on the RAW recorded segment, NO re-aim swap. For Design A's secondary (re-aimed transfer) this would be wrong; for Design B's secondary (instance N+1's in-SOI body-relative escape conic) the raw recorded segment IS correct (the escape leg passes through `ReaimSegmentAssembler` unchanged). So the overlap-instance path is the RIGHT path for the B secondary  -  no re-aim swap needed, no extension of `CreateOverlapInstanceVessel`.

OPEN VERIFICATION (engine blocker #4 / render blocker #4 / render risk #5, all valid): the secondary loopUT spans `[spanStart .. soiExit]`. `CreateOverlapInstanceVessel` returns null + logs "no map-visible orbit this cycle yet" if `IsMapCreateAcceptedSource(source)` is false (`:11877-11893`). Before coding, confirm against aa48920e that the post-ascent in-SOI escape window resolves to an accepted `Segment` (the recorded Kerbin escape conic) and NOT a window-clamped `StateVector`/`TerminalOrbit`. If the early sub-orbital ascent has no `OrbitSegment`, the secondary's icon must anchor to the polyline (`TryAnchorMarkerToPolyline`) until the loopUT reaches the escape Segment, and the escape conic appears only once inside the Segment window  -  which is still inside the borrow window. This is a DATA verification step (grep the aa48920e `.prec` OrbitSegments + the `overlap-instance-no-source` log on a playtest), NOT a code unknown. If it resolves StateVector, the seeded-epoch + stock-propagation contract (`:11947-11961`) applies and the loop-shift drive does not  -  so we MUST confirm Segment before relying on the path. This is the single gating verification before implementation; the plan is sound iff the escape window is a Segment source (overwhelmingly likely, since the recorded escape is a captured inertial conic).

### 5.3 Single-ownership reconciliation

The launch-hold unit's PRIMARY stays in `vesselsByRecordingIndex` (non-overlap), keyed by recording index. The SECONDARY lives in `overlapInstanceVessels[(recIdx, N+1)]`. No double-ownership. Audit `GetGhostVesselPidForRecording` / `GetNewestOverlapInstancePidForRecording` (`:9809-9827, :12069`): the boundary-secondary pid must NEVER shadow the primary's `vesselsByRecordingIndex` pid for the polyline-owner / marker-suppression / TS-Fly lookups (the primary is the authoritative ghost for the recording index). The polyline secondary leg (4.1) keys on the SECONDARY pid explicitly so its proto orbit line is hidden when its leg draws; the primary ownership publish is unchanged.

---

## 6. Missions UI  -  `UI/MissionsWindowUI.cs`

`ComputeNextRelaunchUT` (`:1487`) subtracts `ComputeCappedLaunchAdvanceSeconds` (`:1537,1544`). With the gated helper the actual launch is `L_N - ComputeBoundaryOverlapAdvanceSeconds(...)`. Switch both subtractions to `ComputeBoundaryOverlapAdvanceSeconds` so the navigable "warp to next launch" lands on the real pad lift-off. On slack>0 loops this equals today's value (no change); on engaged loops it correctly points to the earlier (uncapped) launch. The single 15s `ApplyJumpLead` at the warp site (`:1035,1062`) is unchanged. `MissionsWindowPeriodicityDisplayTests` updates to assert the engaged-loop value (section 9). This swap MUST land in the same commit as the clock change (engine risk #3, render risk #4) so clock and UI never disagree.

---

## 7. Targeting safety + no-regression (the byte-identical contract)

- **Targeting `windowIndex` is untouched.** `ReaimPlaybackResolver.TryResolveWindowSegments` resolves the window via `TryComputeSpanLoopUT(..., schedule:null)` (`ReaimPlaybackResolver.cs:110-117`), which never reads `launchHoldEngaged` and returns `floor((currentUT - phaseAnchorUT)/cadence)`. The `TryComputeSpanLoopUT` wrapper stays byte-identical for that call (it returns the primary, and with `schedule:null` + not-engaged the advance is 0). `phaseAnchorUT`, `cadence`, `FirstDepartureUT`, `SynodicPeriodSeconds` are set by the builder before any clock logic and are not touched.
- **Already-aligned (slack>0) loops are byte-identical.** `ComputeBoundaryOverlapAdvanceSeconds` returns the SAME value as `ComputeCappedLaunchAdvanceSeconds` whenever `rawDelta <= slack` (the `delta > slack` gate is false), so the primary loopUT, region B's early launch instant, and `HasSecondary == false` all match today. No extra ghost, no extra map vessel, no polyline second head. This is the strongest no-regression statement and it is why the gate is the default, not a fallback.
- **Downstream byte-identity (the repay nets to zero, design section 3.1).** Each instance inserts its own SOI-exit repay via `ApplyArrivalHoldToPhase(clampedPhase, soiExitPhasePos, effectiveLaunchAdvance)` (`GhostPlaybackLogic.cs:7833`), so each instance's post-SOI timeline is on baseline. The secondary only renders instance N+1's in-SOI pre-SOI-exit ascent; the primary's post-SOI transfer/Duna arrival/arrival hold/landing are unchanged.
- **section 3.1 effectiveSpan re-derivation (Design B section 7, the load-bearing check).** On a zero-slack loop with the cap removed, the PRIMARY's `effectiveSpan = compressedSpan + delta + hold = cadence + delta > cadence`. The clamp `clampedPhase = min(phaseFromLaunch, effectiveSpan)` (`:7832`) then never truncates the primary within the cycle: the primary's `phaseFromLaunch = phaseInCycle + delta <= cadence + delta = effectiveSpan`. The primary is ALLOWED to run past where it used to park because its idle tail is now occupied by the secondary's flight, not a truncation; the boundary handoff (section 1) guarantees `primary.phaseFromLaunch -> cadence + delta` exactly as `secondary -> delta`. This is the invariant the test plan pins (section 9, test 2).
- **Self-overlap path untouched.** `UnitMemberOverlaps(unit)` stays false for re-aim (`MissionLoopUnitBuilder.cs:462-463`, `effectiveOverlapCadence = effectiveCadence >= span`); the engine's overlap branch (`:2179`) is never entered. `UpdateBoundaryOverlapSecondary` is a distinct path that borrows the `overlapGhosts[i]` / `overlapInstanceVessels` STORAGE but never calls `UpdateOverlapPlayback` / `ComputeOverlapCyclePlaybackUT` / `GetActiveCycles`. Non-re-aim units have `LaunchHoldEngaged == false`, so the secondary path is dead code for them.

---

## 8. Builder  -  `MissionLoopUnitBuilder.cs` (no functional change)

The launch-hold gate (`!pad.Applied && plan.Supported && soiExitValid`) and `effectiveOverlapCadence = effectiveCadence` (keeping the unit single-instance / non-self-overlap) are unchanged. Boundary overlap is a render-time consequence of `LaunchHoldEngaged`, decided in the clock. Optional: update the Info line to note the seam now closes on all loops.

---

## 9. Test plan (xUnit + in-game, per CLAUDE.md)

**Pure unit (`LaunchHoldClockTests` + new `BoundaryOverlapClockTests`):**
1. `ComputeBoundaryOverlapAdvanceSeconds`: returns `cappedDelta` when `rawDelta <= slack` (slack>0 loop), returns full `rawDelta` when `rawDelta > slack` (zero-slack loop); degenerate/NaN -> 0.
2. **Load-bearing section 7 invariant**: zero-slack loop (`compressedSpan + W == cadence`) -> `HasSecondary == true` inside `[cadence - rawDelta, cadence)`, `false` outside; primary `effectiveSpan = cadence + delta` and `clampedPhase` never truncates the primary within the cycle; at the boundary `secondaryLoopUT == next-cycle primaryLoopUT` (continuity); the rotation alignment closes (`(currentUT - secondaryRecUT) mod T_sid == 0`, i.e. `residualDeg == 0`) for the previously-capped loop 19.
3. **Byte-identity fence**: `TryComputeSpanLoopUT` wrapper == today's output for (a) not-engaged, (b) cadence==synodic / pad-applied, (c) slack>0 loops' PRIMARY and region-B early launch instant; `TryComputeSpanLoopUT(schedule:null)` `windowIndex` unchanged across the change.
4. `DecideBoundaryOverlapSecondaryRender`: multi-member mission  -  ascent member resolves the secondary in-window while the arrival member resolves the primary (per-member independence); secondary cycle == N+1 while primary cycle == N.

**Logging (`LaunchHoldLoggingTests`):** the per-loop Verbose carries `secondaryActive=true` + both loopUTs and `residualDeg ~ 0` on a zero-slack loop; the one-shot `boundary-overlap engaged` line fires; the `launch-advance-capped` WARN no longer fires for the engaged loop.

**Engine (`GhostPlaybackEngineTests` / new `BoundaryOverlapEngineTests`):** the no-overlap re-aim member spawns a primary + exactly ONE `overlapGhosts[i]` secondary during the borrow window, empty outside it (the gated teardown survives the per-frame re-run); the secondary is audio-muted, has `isBoundaryOverlapSecondary == true`, and never fires `OnOverlapCameraAction` / `OnOverlapExpired`; the watch never binds to it across the window and the boundary handoff; the self-overlap path is untouched (regression fence). Watch clock resolves the primary only.

**Map (mirror the `OverlapPerInstance` cycle-set tests):** the boundary-secondary branch inside `RunOverlapPerInstanceSweep` creates exactly one `overlapInstanceVessels[(recIdx, N+1)]` during the window (seeded at the span-clock secondary UT, registered in `drivenThisFrame` so the reaper spares it) and reaps it after; the primary stays in `vesselsByRecordingIndex`; `GetGhostVesselPidForRecording` still returns the PRIMARY pid (no shadowing).

**Renderer (`GhostTrajectoryPolylineBuildTests`):** the per-leg secondary-head gate enqueues an additive (non-ownership) `PendingLegDraw` keyed on the secondary pid only when `hasSecondary`; the same-leg defensive guard draws only the primary; the seam-bridge near-meet gate still applies to the closed seam.

**Missions UI (`MissionsWindowPeriodicityDisplayTests`):** `ComputeNextRelaunchUT` returns `L_N - rawDelta_N` on an engaged loop and `L_N - cappedDelta_N` (== today) on a slack>0 loop; `ApplyJumpLead` single-15s-lead assertion unchanged.

**In-game (s15 / Kerbal X #2, aa48920e, high warp, multiple loops  -  playtest + `RuntimeTests`):**
- **Pre-coding data check (5.2 gate):** confirm the secondary's in-SOI escape window resolves to a `Segment` source (no `overlap-instance-no-source` spam for the boundary-secondary pid).
- The seam closes on ALL loops including previously-capped zero-slack ones (`residualDeg ~ 0` for every loop; loop 19 was ~211).
- During the borrow window TWO trajectories render: the previous instance arriving at/near Duna (primary) and the new instance launching from Kerbin (secondary), both with map icon + trajectory; no flicker at the boundary handoff; the secondary becomes the watched primary seamlessly within `<= T_sid`.
- Targeting/departure byte-identical (consecutive `windowIndex` transfers resolve); Duna arrival hold lands at the recorded rotation phase, unchanged.
- No regression on slack>0 loops (18, 20: exactly one ghost, one map icon, no second head); `overlapInstanceVessels` count returns to baseline after the window.
- No spurious seam-bridge slice at the now-closed seam (4.3).

---

## 10. Exact touch points (file:method)

- `GhostPlaybackLogic.cs`: `SpanLoopFrame` struct (new, near `LoopUnit`); `ComputeSpanLoopFrame` (new, body of today's `TryComputeSpanLoopUT` with region B emitting a secondary instead of mutating the primary's cycle); `TryComputeSpanLoopUT` (`:7441`, becomes a primary-only wrapper); `ComputeBoundaryOverlapAdvanceSeconds` (new, gated helper, 2.2); swap `ComputeCappedLaunchAdvanceSeconds` -> `ComputeBoundaryOverlapAdvanceSeconds` at `:7575/7577` (`launchAdvance`) and `:7675/7677` (`advNext`); `ComputeCappedLaunchAdvanceSeconds` KEPT (`:7279`); `DecideBoundaryOverlapSecondaryRender` (new, from `:7945`); `ResolveTrackingStationSampleFrame` (new, from `:8009`); diagnostics `:7725-7800` (add `secondaryActive`/`boundary-overlap engaged`).
- `GhostPlaybackEngine.cs`: `UpdateUnitMemberPlayback` (`:2290+`, secondary pass + GATE the teardown at `:2448-2452`); `UpdateBoundaryOverlapSecondary` + `SpawnSecondary` + `PositionSecondaryAt` (new, reuse `CreatePendingSpawnState` `:6259`, `EnsureGhostVisualsLoaded`, the positioning block `:3919-3960`; NEVER the expiry block `:3854-3914`); `GhostPlaybackState.isBoundaryOverlapSecondary` (new field); exclude it at the watch sites (`:2238-2243`, `ValidateWatchedGhostStillActive`, `LogUnitTransitionIfChanged`).
- `GhostMapPresence.cs`: a boundary-secondary branch INSIDE `RunOverlapPerInstanceSweep` (`:11692`, register the index in `drivenThisFrame` + call `CreateOverlapInstanceVessel` seeded at `secondaryLoopUT`); audit `GetGhostVesselPidForRecording`/`GetNewestOverlapInstancePidForRecording` (`:9809-9827, :12069`) so the secondary pid never shadows the primary.
- `Display/GhostTrajectoryPolylineRenderer.cs`: `Driver` decide pass (`:3124+`, second head via `ResolveTrackingStationSampleFrame`, additive `PendingLegDraw` keyed on the secondary pid, same-leg defensive guard).
- `UI/MissionsWindowUI.cs`: `ComputeNextRelaunchUT` (`:1487`, subtract `ComputeBoundaryOverlapAdvanceSeconds`).
- `ParsekKSC.cs`: verify no map surface -> no change (else mirror 3.1-3.3).
- `MissionLoopUnitBuilder.cs`: no functional change (optional Info line).
- Docs: `docs/dev/design-reaim-launch-hold-seam.md` (section 3.3 cap-removal -> boundary overlap, section 8 residual, seam-closes-on-all-loops), `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md`.

---

## 11. Single gating verification before implementation

The ONE thing that could invalidate the map half of the plan is section 5.2: the secondary's in-SOI escape window must resolve to a `Segment` source in `CreateOverlapInstanceVessel`. This is a data check on aa48920e (grep the `.prec` OrbitSegments for a Kerbin escape conic covering the post-ascent window; confirm no `overlap-instance-no-source` skip on a playtest). It is overwhelmingly likely to pass (the recorded escape is a captured inertial conic) and there is a graceful fallback (polyline-anchored icon until the Segment window), but it is the gate to clear first. The clock/engine/polyline halves are sound independent of it.

**Conclusion: the fix is feasible via Design B with the `delta > slack` engagement gate. It closes the seam on all loops (including zero-slack), is byte-identical on already-aligned loops and on the targeting path, reuses the proven `overlapGhosts` / `overlapInstanceVessels` storage without touching the self-overlap or overlap-expiry machinery, and keeps the camera on the long-lived through-line.**
