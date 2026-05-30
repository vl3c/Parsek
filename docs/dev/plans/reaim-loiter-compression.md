# Re-aim loiter compression (launch-window alignment for interplanetary supply routes)

Status: DESIGN (pre-implementation, revision 2 after Opus review). Extends
`docs/dev/plans/reaim-interplanetary-transfers.md`; supersedes that doc's loop-timeline model for the
re-aim path.

## 1. Problem

A re-aim loop today replays the recorded mission's **entire span every synodic window**, including all
the time the player spent **loitering** (waiting for the transfer window). A real Kerbin->Duna recording
can contain arbitrary dead time:

> launch -> ~1 month in LKO -> burn -> ~1 year in a high parking orbit -> burn -> transfer burn -> ~79
> day heliocentric coast -> Duna SOI -> capture orbit -> descent -> land.

For a looped supply route this is useless: the ghost launches now and loiters for over a year before
departing. We need the looped launch **aligned to the transfer window** -- ascend, park ~1 orbit, burn --
no dead loiter. The loiter is an artifact of the original flight; the loop relaunches **at** the window,
so the loiter is redundant.

## 2. Core principle: compress repeated orbits, keep single-pass arcs

Every orbit arc is one of two kinds:

- **Travel** -- a single (partial) pass that gets you somewhere: ascent, transfer ellipse, flyby,
  descent. Traversed once (`duration < period`, or non-elliptic). **KEEP** at full duration.
- **Loiter** -- a closed orbit traversed repeatedly while waiting (LKO / high parking). Traversed many
  times (`duration > N * period`). **COMPRESS** to ~N revolutions.

The discriminator is revolution count. Period `T = 2*pi*sqrt(a^3 / mu_body)`. A transfer arc has
`duration < T` (never completes a revolution) -> never trips the loiter test -> kept. Hyperbolic /
parabolic arcs (flybys, escapes) have no period -> single-pass -> kept.

`N` = revolutions kept before the next maneuver. Locked at **N = 1** (minimal).

### 2.1 Seamlessness (the load-bearing correctness property -- VERIFIED)

**Cutting a whole number of orbital periods is position- and velocity-continuous in BOTH render paths,
for both Sun-relative and body-relative loiters.** This was challenged in review and verified:

KSP's `Orbit.getPositionAtUT(UT) = getRelativePositionAtUT(UT) + referenceBody.position`, where
`referenceBody.position` is the body's **LIVE** transform position, NOT a recursive evaluation at `UT`.
Only the relative (local Kepler phase) term is time-evaluated. So a ghost on a body-relative orbit is
drawn at its **recorded phase relative to the LIVE body** in both paths:
- Flight (`ParsekFlight.PositionGhostFromOrbit`, `:21929`): unshifted epoch, sampled at `spanLoopUT`
  -> `relative(spanLoopUT) + liveBody`.
- Map / TS (`GhostMapPresence`, `:6961`/`:7101`): epoch reseeded by `(liveUT - effUT)`, sampled at
  `liveUT` -> recorded-phase `relative` + `liveBody`.

A whole-period cut removes **recorded** time: at the cut frame the recorded sample UT jumps by `k*T`
while **live** time advances one frame `dt` (the live clock does NOT jump). Across the cut:
- relative local position: identical (same mean anomaly, `k` whole periods),
- `referenceBody.position`: continuous (advances by `dt`, never by `k*T`).

Ghost world position = identical-relative + continuous-live-body -> continuous -> **seamless**. (The
challenge wrongly assumed the live clock jumped over the excised interval; only the recorded clock does.
This same fact is why the existing re-aim "body-relative legs follow their live body" assumption holds.)

**Exactness rider (feeds detection, section 4):** the seam is exact only if the cut is an EXACT integer
multiple of the segment's period `T(a)`. If `a` drifts across a long loiter (decay / apsidal drift), a
nominal `k*T` is slightly off a true revolution and leaves a tiny phase residual. The cut therefore
must be snapped to a true whole-revolution boundary of the segment being cut (section 4.3), not a raw
`k*T`. With clean closed segments it is exactly seamless.

## 3. Classifier rework: anchor at the target SOI entry (fixes a real bug)

### 3.1 The bug in the current classifier

`ReaimClassifier` sets `RecordedDepartureUT = helio.startUT` -- the FIRST strict-ancestor (Sun) segment
start (`ReaimClassifier.cs:169`). If a closed heliocentric orbit precedes the transfer, this is the
loiter's start, not the transfer's, and `RecordedTransferTofSeconds = arrival.startUT - helio.startUT`
(`:171`) then includes the loiter time -> a wildly wrong tof fed to the Lambert solve. Even without a
heliocentric loiter, "first ancestor arc" is the wrong anchor in principle.

### 3.2 The algorithm

The target SOI entry is unambiguous and already produced: `RecordedArrivalUT = arrival.startUT`
(`:170`) is the first target-body segment start = the encounter. Anchor there and walk the flattened
`GatherMemberOrbitSegments` list (startUT-sorted across all members) backwards:

1. **Anchor:** `arrivalSeg` = first target-body segment after the heliocentric phase; `RecordedArrivalUT
   = arrivalSeg.startUT`.
2. **Transfer run:** the maximal contiguous run of common-ancestor (Sun) segments ending at
   `RecordedArrivalUT`, EXCLUDING any leading closed-loiter (`duration >= T`) Sun segments. "Ending at"
   means UT-contiguity (`lastSunSeg.endUT ~= arrivalSeg.startUT` within epsilon, the SOI boundary), NOT
   merely list-adjacency -- so an interleaved debris segment from another member in the flattened,
   startUT-sorted `GatherMemberOrbitSegments` list cannot break run detection; walk back by UT adjacency.
   The transfer + its mid-course-correction coasts are all `duration < T` partial arcs (REVISION 1b of
   the parent doc already collapses corrections); a leading `duration >= T` closed Sun segment in the run
   is a **heliocentric loiter**, not transfer. `RecordedDepartureUT` = the start of the first NON-loiter
   Sun segment of the run (the transfer departure).
3. **Launch-body SOI exit:** the segment immediately before the transfer run. v1 supports **direct
   departure from the launch body** -- that segment must be a launch-body orbit (the parking orbit). If
   it is instead a closed heliocentric loiter (the transfer departs from a solar parking orbit, not the
   launch body), v1 **declines re-aim** (returns unsupported -> faithful): the Lambert solve assumes
   `r1 = launchBody.position`, which is invalid for a heliocentric-parking departure. Heliocentric-loiter
   departures are deferred to v2. (This is the rare case; players normally loiter in launch-body orbit
   then burn direct.)
4. `RecordedTransferTofSeconds = RecordedArrivalUT - RecordedDepartureUT` (the transfer run duration,
   excluding any leading heliocentric loiter -- correct tof for the Lambert solve).

This keeps the window planner / synthesizer unchanged (they key off `RecordedDepartureUT` /
`RecordedArrivalUT` / tof). v1 scope: **launch-body-side loiters + a direct transfer.** A heliocentric
parking departure is detected and declined, not mis-aimed.

## 4. Loiter detection (the top correctness gate)

The only way compression goes wrong is a **mis-detected** cut: a non-whole-period cut teleports the
ghost; an under-detected loiter silently does nothing. Detection must be robust to how the recorder
chunked the loiter.

### 4.1 Unified rule: contiguous same-body elliptical run

A loiter is a **maximal contiguous run of same-body, non-predicted, elliptical OrbitSegments** (one long
segment OR many ~1-rev chunks -- the 83-segment recording is the chunked form) whose **total duration >
(N + frac) * T_rep**, where `T_rep` is the run's representative period (period from the run's
median / first segment `a`). This single rule subsumes both forms the v1 draft listed separately and
closes the gap between them (a loiter chopped into sub-period chunks is still caught by the run's TOTAL
duration). The run must be contiguous in UT and same body; a body change or a non-orbit (burn /
atmospheric) section ends the run.

**Semi-major-axis discontinuity guard (REQUIRED -- review M3, the one over-merge hole).** A run ALSO ends
at a significant `a` (energy) step between adjacent same-body segments -- i.e. a maneuver to a different
orbit (a 200 km parking orbit -> a 2000 km phasing orbit) even when no `ExoPropulsive` section boundary
was emitted (long / off-rails burns may land in Points, not a classified section). Threshold: relative
`a` change between adjacent segments > a few percent ends the run. This is NOT the `(a,e,i,LAN,argPe)`
element-equality tolerance we deliberately removed (precession / decay are gradual, sub-percent per rev,
so they do NOT trip it; a deliberate orbit-raise is a step change that does). Its sole purpose: keep
`T_rep` valid for the WHOLE run so the cut (section 4.3) lands on a true period of the segment it cuts.
Without this guard, body+contiguity would merge an orbit-raise into one run, `T_rep` would be wrong for
half of it, and the cut would NOT be a whole period -> the exact teleport section 2.1 forbids. (The 4.3
period-snap is well-posed only because this guard keeps `a` near-constant within a run.)

Travel arcs (transfer, ascent, descent) are runs whose total `duration < T_rep` -> not loiters.

### 4.2 Tolerance / robustness

- Same-orbit chunking does NOT require exact element match (a precessing / decaying LKO drifts each
  rev). It requires only **same body + contiguous + elliptical**; the run's total duration vs `T_rep`
  decides. This avoids the open "(a,e,i,LAN,argPe) tolerance" question entirely -- we group by body +
  contiguity, not by element equality, so drift cannot defeat detection.
- **Station-keeping-interrupted loiter:** a burn (ExoPropulsive TrackSection) between two slightly
  different orbits ends the run; each interval compresses independently. A year-long loiter with monthly
  station-keeping thus compresses per-interval (~12 kept revs), not globally. Documented v1 limit
  (acceptable: the dominant dead time -- the contiguous coast within each interval -- is still removed).
- **Body-change false run:** an SOI transition (Kerbin->Sun) ends the run, so the parking loiter and the
  heliocentric arc are never merged.

### 4.3 Cut computation (exact whole periods)

For a detected loiter run spanning recorded `[runStart, runEnd]` with representative period `T`:
- `wholeRevs = floor((runEnd - runStart) / T)`; if `wholeRevs <= N`, no cut.
- `cutLength = (wholeRevs - N) * T` -- an exact integer number of periods.
- `cut = [runStart, runStart + cutLength]` (excise the first `wholeRevs - N` periods; keep the last
  `N revs + remainder` ending at `runEnd`, so the exit phase = the recorded exit = phase-correct, and
  the resume point `runStart + cutLength` is `wholeRevs - N` periods after `runStart` = same position =
  seamless entry).
- **Snap:** because `T` is from the snapshot `a`, recompute `cutLength` so `runStart + cutLength` lands
  on the segment's nearest true periapsis/anomaly-repeat (eliminates the `a`-drift residual of 2.1).

## 5. Implementation: gated uniform-path remap in the shared span clock

Decision (review M1, REVERSED after implementation): modify `TryComputeSpanLoopUT` via a gated
uniform-path remap. The original M1 preferred pre-baked per-member compressed segments on the premise
that compression is "local to the loiter-owning member" -- **that premise is false for a chain.** A cut
is a deletion of recorded-UT range, and every member after it in UT (the faithful Duna landing Points
member, ride-along debris) must shift earlier; in the engine EVERY unit member, faithful Points members
included, is positioned at the shared `spanLoopUT` (`GhostPlaybackEngine.cs:2395`). So pre-baking would
have to re-time the faithful Points members too (a Points-shifting wrapper per member type, plus a
second consistency surface between the unit's compressed windows and each wrapper's shifted Points). The
remap does that global shift **once, for every member type** (Points, surface, orbit all read
`spanLoopUT`), so the cut belongs in the shared clock.

### 5.1 The remap (the only clock change)

Inside `TryComputeSpanLoopUT`'s UNIFORM branch only (after the `schedule != null` early-return), with a
`loiterCuts` parameter (neutral `GhostPlaybackLogic.LoopCut` -- no `Parsek.Reaim` reference in the
standalone-target engine):
- the active duration is the **compressed span** `effectiveSpan = span - totalCut`; `phaseInCycle` wraps
  over `effectiveSpan` (not `span`);
- the clamped compressed phase is remapped to a recorded `loopUT` that SKIPS the cut intervals:
  `loopUT = DecompressSpanUT(spanStartUT + clampedPhase, cuts)` (the inverse of `CompressSpanUT`, mapping
  a cut's collapse point to the cut END so the loop resumes after the excised whole periods);
- `isInInterCycleTail = phaseInCycle >= effectiveSpan`.

`cycleIndex` / `elapsed` / the boundary-epsilon rollback are UNCHANGED (a cut lives within one cycle, so
the cadence = synodic is cut-independent; the rollback still emits the recorded `spanEndUT`, which equals
`DecompressSpanUT(spanStart + effectiveSpan)` by construction). Empty/null cuts => `effectiveSpan == span`
and the remap is the identity, so every non-re-aim caller is byte-identical.

### 5.2 Build-time + live (almost nothing else changes)

- `MissionLoopUnitBuilder` (re-aim engaged): compute the cuts (section 4) across the gathered mission
  segments; set `phaseAnchorUT = sched.PhaseAnchorUT + (RecordedDepartureUT - comp(RecordedDepartureUT))`
  (the launch shifts LATER by the cut excised before the transfer departure, so it lands ~1 orbit before
  the unchanged absolute synodic window); attach the cuts to the `LoopUnit`. The span and member windows
  stay RECORDED (the remap gates against them in recorded UT).
- The per-window resolver (`ReaimPlaybackResolver`) is UNCHANGED: it operates on the recorded segments +
  the uncompressed plan, and the cut-skipping recorded `loopUT` naturally skips the loiter (a whole number
  of periods, so the orbital phase is continuous -- seamless, section 2.1). It consumes only the
  cut-independent `cycleIndex`, so the window resolution is unaffected.
- Members render at recorded UTs (the cut interval is simply never sampled), so faithful Points members
  (ascent / landing / debris) render unchanged, just earlier in live time -- the chain stays coherent
  with no per-member compression.

### 5.3 The three guards (reviewer sign-off conditions, all tested)

1. **Gate strictly + empty = hard identity.** The cut is honored only in the uniform branch; the
   `schedule != null` path ignores it (re-aim always passes `schedule = null`).
   `TryComputeSpanLoopUT_EmptyOrNullCuts_BitIdenticalToNoCompression` sweeps `currentUT` and asserts
   bit-identical `(loopUT, cycleIndex, tail)`.
2. **Remap order + boundary scale.** Cut AFTER the clamp, wrapping over the compressed span; the
   boundary-rollback `spanEndUT` is on the same recorded-UT scale as the compressed-span-end play frame.
   `TryComputeSpanLoopUT_WithCut_WrapsOverCompressedSpan_SkipsCut_BoundaryMatches`.
3. **Member-window gate stays recorded; straddle is intended.** A member window straddling a cut renders
   its non-cut portions and never samples inside the cut, with `Render` throughout.
   `DecideUnitMemberRender_MemberWindowStraddlingCut_RendersNonCutPortions_NeverInsideCut`.

## 6. Render contract (review C2) -- what is actually visible

A re-aim member renders **from `OrbitSegments` only**: `ReaimedTrajectory` presents `Points`,
`TrackSections`, `PartEvents`, `FlagEvents` EMPTY (`ReaimedTrajectory.cs:57-60`). Consequences:
- The **loiters ARE OrbitSegments**, so they render, so compressing them is observable and worth doing.
  This is the core payoff and it is delivered.
- **Ascent / descent are Points** (off-rails atmospheric), so they render ONLY through a member that is
  NOT re-aimed. Per the parent doc REVISION 2 (real missions are chains), the ascent is a **separate
  committed member with no heliocentric leg** -> passes through as raw `inner` -> its Points render
  faithfully, and its (orbit-less) timeline is governed by its compressed member window. So "ascend,
  then the transfer" reads correctly in the chain case.
- In a **single continuous recording** (ascent + transfer in ONE member), that member is wrapped in
  `ReaimedTrajectory` -> its ascent Points are dropped; only its orbit segments render. The "ascend"
  visual is absent (the dominant visual -- parking + transfer + capture orbit -- still renders, compressed
  and re-aimed). This is an accepted v1 limitation, identical to the parent doc's existing orbit-only
  contract; loiter compression does not make it worse.

Net: loiter compression targets the orbit-segment loiters that DO render; the ascent visual depends on
the chain decomposition and is out of scope here.

## 7. Scope

Re-aim loops only (`unit.IsReaim`). Faithful same-parent loops keep loiters (a loiter there can BE the
phasing that aligns the encounter; compressing it would make the encounter miss). v1 within re-aim:
launch-body-side loiters + direct transfer; heliocentric-parking departures and post-arrival target-side
loiter compression deferred (sections 3.2, 10).

## 8. Edge cases

- **Heliocentric loiter before the transfer** -- detected (closed Sun run before the transfer arc) and
  the departure-from-solar-parking case is DECLINED (faithful), not mis-aimed (section 3.2). This is the
  case that breaks the current forward classifier.
- **Burns between loiters** -- end the run; re-aim renders orbit-only so the burn itself isn't drawn;
  each orbit keeps ~1 rev.
- **No loiter** -- empty cut list; compressed span == recorded span; behaves as current re-aim exactly.
- **Watch camera** (review minor, now benign) -- a fully-cut member window is the same "hidden outside
  window" path the engine already handles (`GhostPlaybackEngine.cs:2330` keep-watched guards); no
  teleport (2.1), so no new failure mode -- but confirm the guards tolerate a member whose compressed
  window is empty.
- **Save/load** -- the cut list + compressed segments are fully DERIVED at build (rebuilt on
  `BuildSignature` change, which already folds in segment bodies/periods); nothing persisted, consistent
  with the parent doc. `N` is a compile-time constant (not in the signature) -- fine until it is ever
  made configurable.

## 9. Testing

- **Unit (pure):** the contiguous-same-body-run loiter detector (single long segment; chunked run;
  hyperbolic/parabolic skip; body-change run termination; station-keeping split; gradual-drift split
  anchored to the run's first `a`); cut computation (`wholeRevs = floor(D/T)`,
  `cutLength = (wholeRevs - N)*T`, exact-period snap); the recorded<->compressed UT map
  (`CompressSpanUT` monotonic / empty == identity; `DecompressSpanUT` inverse / skips to the cut END);
  the reworked classifier (transfer = run ending at SOI entry; heliocentric-loiter decline;
  `RecordedDepartureUT`/tof correct with a leading Sun loiter; >1-rev-run decline; real-sma data).
- **Shared clock (the three guards, section 5.3):** empty/null cuts byte-identical to the
  pre-compression clock; remap order + compressed-span wrap + boundary-rollback UT-scale agreement; a
  member window straddling a cut renders its non-cut portions and never samples inside the cut. These
  replace the old "shared clock untouched -> trivially true" claim, which no longer holds (the remap is a
  deliberate, gated clock change).
- **Regression:** every non-re-aim loop is byte-identical via guard 1 (the cut parameter is identity on
  the empty list that every faithful / overlap / zero-drift / KSC / TS caller passes).
- **In-game:** a synthetic mission with an LKO loiter + a high-orbit loiter + a transfer; assert the
  compressed span, the window-aligned launch, and (canary) world-position continuity across a cut
  boundary (sample the same orbit at the two cut-boundary recorded UTs at one live frame apart, assert
  world delta ~ one-frame body motion, not a jump).

## 10. Open questions / deferred

1. **Post-arrival target-side loiter** (capture orbit before descent) -- compress in a follow-up (same
   mechanism, Duna-relative; deferred to keep v1 to the inbound side).
2. **Heliocentric-parking departure** -- deferred (section 3.2): needs the Lambert `r1` to be the solar
   parking position, not the launch body. Declined (faithful) in v1.
3. **Station-keeping-interrupted loiter** -- v1 compresses per-interval, not globally (section 4.2).
   Acceptable; revisit only if a real recording shows it matters.
4. **`N` = 1** -- confirm 1 rev reads well; make configurable only if playtest wants it.
