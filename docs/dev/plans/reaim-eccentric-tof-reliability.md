# Re-aim Option 4, the rest: eccentric-target tof reliability (M-MIS-3 limitation B), validation fixtures, and the Gooding contingency

Status: PLAN. The plan itself ships docs-only in this PR; the implementation (stage B + harness parametrization + Moho/Eeloo fixtures) landed separately in PR #1148. In-game measurement / constant-pinning is still pending (see section 4.2 / open questions).
Branch: `reaim-eccentric-tof-reliability` (from `origin/main`).
Predecessor research: `docs/dev/plans/reaim-near-180-lambert-reliability.md` (the A+B analysis; on branch `reaim-lambert-reliability`).
Hard dependency: the near-180 handedness fix (limitation A), MERGED to main via PR #1140 (commit `0dd6bd3a6`). This plan builds ON TOP of what that fix ships. See section 2.

Note on source line numbers: stage A is now on main, and this plan branch was brought up to date with it (2026-06-14). Cites to the stage-B target files (`ReaimPlaybackResolver`, `TransferWindowMath`, `ReaimWindowPlanner`, `ReaimFeasibilityScan`) are unaffected by stage A (those files did not change). The `ReaimEndToEndInGameTest.cs` harness cites in section 4.2 are given against CURRENT main (post-stage-A).

## 0. One-paragraph summary

The research plan (`reaim-near-180-lambert-reliability.md`) split M-MIS-3 into (A) a near-180-degree handedness flip and (B) eccentric-target time-of-flight drift, and recommended a staged "Option 4": fix A first, then B, with a Gooding vendor only as a proven contingency. Limitation A is being fixed in a separate PR. This plan is the REST of Option 4: stage B (geometry-aware tof centering), the synthetic Moho and Eeloo validation fixtures that do not yet exist, and the two outstanding decisions (is the A solver change universal or gated; is the Gooding contingency still needed). The work reuses the in-repo MIT `TransferWindowMath` and `UvLambert`; no new solver is hand-rolled; fail-closed (faithful fallback, never a wrong conic) is preserved at every layer.

## 1. Problem statement and current state

### 1.1 The two M-MIS-3 limitations (verbatim source: `docs/dev/todo-and-known-bugs.md:112-118`)

- (A) Inclination / near-180: the recorded Duna playtest declined window 0 with `inc=180.00 resultRetrograde=True recordedRetrograde=False`. Root cause (verified against source in the research plan): `ReaimTransferSynthesizer.cs` PROJECTED the target endpoint r2 onto the launch plane to dodge the Lambert plane singularity; at a Hohmann window that flattened r1/r2 toward antiparallel, and `UvLambert.Solve` selected the prograde/retrograde branch from `sign(cross.z)` of a noise-dominated `r1 x r2`, returning the retrograde (inc=180) branch, which the direction guard then correctly rejected on every tof candidate -> faithful fallback. The same projection also capped re-aim to low-inclination targets (a high-inclination small-SOI target like Moho projected far outside its SOI, so the proximity encounter check failed).

- (B) Eccentricity: window spacing is pure synodic (`ReaimWindowPlanner.cs`), and the per-window Lambert reuses the RECORDED tof with a fixed +-6% search (`ReaimPlaybackResolver.cs`, `TofSearchStepFraction = 0.005` x `SearchMaxSteps = 12`). An eccentric target (Eeloo ecc ~0.26, Moho ~0.2) sits at a different radius each window (the phase angle recurs each synodic period but the true anomaly / radius does not - synodic recurrence is exact only for circular orbits), so the geometrically-required tof routinely leaves the +-6% band and the window declines to faithful. Acknowledged in `docs/dev/design-mission-periodicity.md:969-973` (Residual risks): "the recorded tof at one window may not reproduce the recording-time relative geometry, pushing the required tof outside the +-6 percent band and declining the window. Not modeled this phase; flag and likely fail closed."

### 1.2 What stage A actually ships (merged to main via PR #1140, commit `0dd6bd3a6`; read via `git show 0dd6bd3a6`)

IMPORTANT: stage A did NOT take the research plan's recommended "conditional projection" lever. It took the plane-normal hint that the research plan had explicitly dropped (section 4 "Note on the seam-widening plane-normal hint"). The implementer chose it together with un-projecting r2, which is the pairing the research plan said the hint needs to work. The concrete diff:

- `ITransferSolver.Solve` GAINED a `Vector3d planeNormal` parameter (`Source/Parsek/Reaim/ITransferSolver.cs`). The seam contract now is `Solve(double mu, Vector3d r1, Vector3d r2, double tof, bool prograde, Vector3d planeNormal, out Vector3d v1, out Vector3d v2)`. Its doc states: "a future Gooding-style vendor drops in behind this SAME interface carrying the normal it wants as its angular-momentum axis h." So the seam is now Gooding-ready by construction.
- `UvLambert.Solve` GAINED a plane-normal overload; the 7-arg overload forwards with `planeNormal = Vector3d.zero` (byte-identical legacy `cross.z` behaviour, proved by a new Curtis 5.2 byte-equality test). When a non-degenerate normal is supplied, branch handedness rides `dot(r1 x r2, planeNormal)` instead of `cross.z`. `MinSinTransferAngle` (the collinear hard bail) is UNCHANGED, so an exactly-collinear pair still fails closed.
- `ReaimTransferSynthesizer.TrySynthesizeTransfer` NO LONGER projects r2. It computes `launchPlaneNormal = r1 x v_launch` and passes it as the handedness axis with the RAW (un-projected) r2. `ProjectOntoPlane` is retained but unused (kept as a tested helper / documented contingency).
- CONSEQUENCE 1 (handedness, the reported bug): the Duna near-180 window now converges prograde at the nominal departure (step 0). The harness's `ObservedEdgeDepartureUT` test now HARD-asserts window 0 resolves prograde (`requireWindow0Resolve: true`). It also ADDS a `requireAllWindowsResolve` strong-contract switch, but that switch is passed FALSE at both call sites (verified: `ReaimEndToEndInGameTest.cs` lines 216 and 253), deliberately left soft pending a live in-game Periodicity SPACECENTER batch confirming the R/d map reads all-R. The reason it stays soft is load-bearing for THIS plan: a later-window decline can still come from the SEPARATE, still-open eccentric-drift tof-band mode (this plan's stage B), which the handedness fix does not address, so it must fall back cleanly rather than hard-fail. Stage A's todo UPDATE reclassifies SPECIFICALLY the near-180 retrograde-branch (inc=180) decline as a handedness flip (now resolves prograde); the "unresolvable-by-design" label therefore now applies only to genuinely-infeasible knife-edge geometry, NOT to the handedness case. Promoting `requireAllWindowsResolve` to true after a live all-R confirmation is pending stage-A follow-up work, not a green precondition.
- CONSEQUENCE 2 (inclination, M-MIS-3 requirement 1): because r2 is no longer projected, the transfer carries the target's real out-of-plane component and `TryFindTargetEncounterByProximity` aims at the target's ACTUAL position. The Moho SOI-miss cap described in `ReaimTransferSynthesizer.cs:144-150` (the deleted comment block) is GONE; an inclined target is now bounded only by the downstream proximity check, not by the projection. So M-MIS-3 requirement (1) is effectively addressed by stage A via a DIFFERENT mechanism than the todo text ("conditional projection") describes.
- Tests added by stage A: `UvLambertTests` (zero-normal byte-identical fallback; prograde/retrograde round-trip with a +z normal; collinear-still-fails-closed with a normal; and the core `Solve_AntipodalNear180_PlaneNormalSelectsPrograde_LegacyCrossZFlips` regression). `TransferSolverInterfaceTests` updated for the new signature. The harness `ObservedEdgeDeparture` test strengthened as above.

### 1.3 What therefore REMAINS for Option 4 (this plan)

1. Stage B: geometry-aware tof centering for eccentric targets (limitation B). Stage A does nothing for B; the tof search is still recorded-tof-centered +-6%.
2. The synthetic Moho AND Eeloo validation fixtures on the M-MIS-1 pinned-UT harness. These DO NOT EXIST yet (the harness drives Kerbin->Duna only). Authoring them is real, non-trivial work (see section 4.2 for why a naive fixture is a silent no-op).
3. Requirement (3): relax the shape-congruence expectation for eccentric targets in the harness assertions (varying sma/ecc per window is correct, not a defect).
4. Decision: is stage A's solver change the universal default or gated (section 5.1).
5. Decision: is the Option-4 stage-3 Gooding contingency still needed after A ships (section 5.2).

## 2. Relationship to stage A and to M-MIS-1 (read before designing)

- Stage A (plane-normal hint + un-projected r2) is MERGED to main (PR #1140, commit `0dd6bd3a6`); its approach is now fixed, so the Moho fixture's dependency on the un-projection having lifted the inclination cap is settled, not a moving target. (Were a future change to revert the un-projection, re-validate sections 4.2 and 5.)
- M-MIS-1 (PR #1116, COMPLETE) shipped the deterministic pinned-UT harness (`Source/Parsek/InGameTests/ReaimEndToEndInGameTest.cs`), the pure `ReaimFeasibilityScan` helpers, and `TransferWindowMath.LongitudeOfPeriapsisDegrees`. M-MIS-1 made an EXPLICIT decision: NO tof-search WIDENING (`todo:101`, `reaim-resolver-reliability.md` section 9): "The +-6% recorded-tof search resolves every window across the entire contiguous feasibility band ... Knife-edge window declines are hereby classified UNRESOLVABLE-BY-DESIGN ... Geometry-aware tof centering remains M-MIS-3 scope (eccentric/inclined targets), unchanged by this decision."
- The tension to respect: stage B is RE-CENTERING + bounded eccentricity-scaled banding, which M-MIS-1 explicitly deferred TO M-MIS-3, NOT the blanket tof WIDENING M-MIS-1 refused. The design must not regress the Duna in-band windows that resolve today at step 0, and must keep the eccentricity-scaled term bounded so it cannot reintroduce knife-edge widening for a low-eccentricity target. Section 4.1 makes "zero regression for low-ecc" a structural property, not a tuning hope.
- Stage A interacts with M-MIS-1: stage A reclassifies the M-MIS-1 ObservedEdgeDeparture window-2 (inc=180) decline as a handedness flip and hard-asserts window 0 now resolves prograde (the all-windows hard contract is still wired-but-FALSE pending live confirmation, section 1.2), and likely turns the M-MIS-1 sweep's `dep=43/44` retrograde-branch declines into resolves too. So after stage A, the DUNA sweep should decline LESS, and Duna (ecc 0.051) may no longer demonstrate limitation B at all. This is why B's validation needs genuinely eccentric targets (Eeloo, Moho), not Duna.
- Stage A ALREADY reconciled the docs it touched (verified in commit `0dd6bd3a6`): it renamed the M-MIS-3 todo entry to "High-eccentricity targets ...; inclination DONE", marked requirement (1) inclination DONE (projection removed; un-projection + plane-normal hint), and added the M-MIS-1 UPDATE note reclassifying the inc=180 decline. So the M-MIS-1 entry reconciliation is stage A's, not this plan's; this plan's doc-updates (section 10) are scoped to the eccentricity half. Do NOT re-do the inclination/M-MIS-1 doc edits.
- COORDINATION with PR #1139 (OPEN as of 2026-06-14, branch `fix-loiter-knob-destination-body`): the M-MIS-4 / M4b loiter-knob destination-body cadence fix. It is ORTHOGONAL to stage B and must not be conflated. #1139 changes the loiter-knob SCHEDULE layer (`MissionLoopUnitBuilder`, `MissionPeriodicity` / `BuildReaimPeriodCellDisplay`, `MissionsWindowUI`, and consumes `ReaimLoiterCompressor`) - WHEN the ghost relaunches and how many parking revolutions it keeps; stage B changes the per-window TRANSFER-SHAPE layer (`ReaimPlaybackResolver.BuildWindowSegments`) - WHICH heliocentric ellipse is solved. They COMPOSE: stage B reads `schedule.DepartureUTForWindow` / `schedule.TofSeconds` READ-ONLY and never edits the schedule, so the loiter knob picking the window/departure and stage B solving that window's transfer are independent. There is ZERO production-file overlap: #1139 touches none of stage B's solver files (`ReaimPlaybackResolver`, `TransferWindowMath`, `ReaimWindowPlanner`, `ReaimFeasibilityScan`, the harness), and stage B touches none of the loiter-knob files. The ONLY shared files are `docs/dev/todo-and-known-bugs.md` (#1139 edits the M-MIS-4 entry; stage B edits the M-MIS-3 entry - different sections) and `CHANGELOG.md` (different lines). Implications for the stage-B implementer: (1) do NOT touch the loiter-knob code (`MissionLoopUnitBuilder` / `ReaimLoiterCompressor` / `MissionPeriodicity`) - it is M-MIS-4 / M-MIS-2-P4 territory, not M-MIS-3; (2) rebase on current main before editing the two shared docs, expecting at most a trivial section reconciliation depending on whether #1139 merged first. The plan PR (#1141) itself shares NO file with #1139, so it cannot conflict.

## 3. Scope

### This plan's PR delivers (planning only here; the implementation PR delivers)

- Stage B: a geometry-aware tof center + bounded eccentricity-scaled band in `ReaimPlaybackResolver.BuildWindowSegments`, reusing `TransferWindowMath.HohmannTransferTimeSeconds`. Pure decision logic extracted to an `internal static` helper for xUnit coverage.
- Harness parametrization: generalize `ReaimEndToEndInGameTest` from hardcoded Kerbin/Duna to a `(launchBody, targetBody)` pair, so Moho and Eeloo fixtures reuse the pinned-scan / mid-band / band-edge / sweep machinery.
- Synthetic Moho (high-inclination + near-180 + moderate eccentricity) and Eeloo (high-eccentricity) in-game fixtures on the pinned-UT harness, authored measure-first (the failing case demonstrated before the knob, per the re-aim seam guidance and M-MIS-1's "measure before knob math").
- Requirement (3): the eccentric fixtures assert per-window resolution + orientation rotation but explicitly DO NOT assert sma/ecc shape congruence across windows.
- xUnit coverage for the pure tof-centering helper (center law, ecc-scaled band, bounded cap, zero-expansion at zero eccentricity, recorded-tof always reachable).
- Doc updates in the same commits (CHANGELOG one-liner, `todo-and-known-bugs.md` M-MIS-3 entry, this plan).

### Explicitly DEFERRED (gated on demonstrated need)

- Stage 3 Gooding vendor. Built ONLY if a synthetic Moho/Eeloo window PROVABLY still declines after stage B, AND the SPDX gate passes on a fetched copy (section 5.2). Not a planned stage.
- Any change to `ReaimWindowPlanner` synodic spacing. The cadence stays synodic (`todo:117`: "ReaimWindowPlanner synodic spacing itself needs no change"); only the per-window solve adapts.
- Destination-arrival seam refinement for the larger per-window arrival-UT drift that a wider tof band can introduce. That is M-MIS-2 (arrival hold) territory; this plan only NOTES the interaction (section 8).
- Multi-hop / deep-chain targets (Ike via Duna), gravity-assist, atmo-direct - already out of scope per `todo:197-199`.

## 4. Design

### 4.1 Stage B: geometry-aware tof centering

#### 4.1.1 Where, and what is in hand

The change is local to `ReaimPlaybackResolver.BuildWindowSegments` (`Source/Parsek/Reaim/ReaimPlaybackResolver.cs:139-250`), the tof-search loop at lines 179-211. The synthesizer docstring already anticipates this: `ReaimTransferSynthesizer.cs:80-81` says `tofSeconds` "should be the Hohmann time for THIS window's geometry (plan review M3), not the recorded tof" - but the resolver still feeds `schedule.TofSeconds` (the recorded tof). Stage B closes that gap on the resolver side.

In `BuildWindowSegments`, the bodies and parent are already resolved (`launchBody`, `targetBody`, both non-null past line 153), so the geometric inputs are in hand with no new plumbing:
- `aOrigin = launchBody.orbit.semiMajorAxis`
- `aTarget = targetBody.orbit.semiMajorAxis`
- `muParent = launchBody.referenceBody.gravParameter` (== parent of both; the synthesizer already asserts `targetBody.referenceBody == parent`)

#### 4.1.2 Render-span preservation (already structural - do NOT change it)

The transfer is rendered over the FULL recorded heliocentric span `[plan.RecordedDepartureUT, plan.RecordedArrivalUT]` via `ReaimSegmentAssembler.ReplaceHeliocentricLeg(..., double.NaN, double.NaN)` (resolver lines 233-236). The chosen `usedTofSeconds` only selects WHICH transfer ellipse is solved; it does not move the render span. `schedule.TofSeconds` (recorded) stays the source for the synodic schedule and span placement (`ReaimWindowPlanner` lines 27, 115). So stage B changes only the search center/band feeding `TrySynthesizeTransfer`; span coherence is preserved by construction. (Caveat: a larger `usedTofSeconds` deviation means the transfer ellipse's natural arrival UT diverges further from the recorded arrival UT it is drawn against - a render-seam consideration, section 8, not a span-placement bug.)

#### 4.1.3 The recommended search shape: recorded-tof step 0 + eccentricity-gated geometry-aware expansion

Two structural requirements pull in opposite directions: zero regression of windows that resolve today at step 0 (recorded tof), and reaching the drifted geometric tof for eccentric windows. The recommended shape satisfies both:

1. Keep step 0 = `schedule.TofSeconds` (the recorded tof), probed FIRST, UNCHANGED. Every window that resolves at step 0 today resolves identically. This is zero-regression BY CONSTRUCTION, not by tuning - and it preserves the M-MIS-1 contract directly (the recorded mission's own window 0 is the recorded geometry; it must always be reachable).
2. Add a geometry-aware center `geomTof` and an eccentricity-scaled half-width, and probe outward to cover `geomTof` and its band. For a LOW-but-nonzero-eccentricity target (Duna ecc 0.051) the eccentricity-scaled term is SMALL (a few percent: with the as-shipped `0.06 + 0.5*ecc` law, ~8.55%), so the band widens only modestly. Zero-regression does NOT depend on the Duna band staying byte-identical to +-6% - it is guaranteed by STEP 0 (the recorded tof is always probed first), so any window resolving today resolves identically and the modest widening only adds candidates reached AFTER the base band fails. This is the geometry-aware centering M-MIS-1 explicitly DEFERRED to M-MIS-3 ("Geometry-aware tof centering remains M-MIS-3 scope"), NOT the knife-edge widening M-MIS-1 refused; a live Duna in-game batch is the production fence, and suppressing low-ecc widening entirely via a deadband is a measure-first follow-up if that fence shows any issue. For a genuinely eccentric target (Eeloo/Moho) the band widens substantially toward the geometric tof, the sanctioned M-MIS-3 direction (2). (Only the exact `eTarget = 0` case is byte-identical to today - invariant (a) in 4.1.5.)

This is a strict EXTENSION of the existing search, anchored at the recorded tof, expanding toward the geometric center - NOT a bare recenter that could shift the band off a working window. It is the lowest-regression-risk reading of `todo:117` requirement (2) ("center ... on the tof implied by the ACTUAL window radii ... and/or scale the search band by target eccentricity"), using the band-scaling lever as the primary mechanism.

#### 4.1.4 The geometric center: SMA-based vs per-window radius-aware

Two candidates for `geomTof`, both REUSING `TransferWindowMath.HohmannTransferTimeSeconds(a, b, mu)` verbatim (the formula is `pi * sqrt(((a+b)/2)^3 / mu)`; for circular orbits the SMA IS the radius, so passing radii instead of SMAs is the same closed form):

- B-minimal (per-mission constant): `geomTof = HohmannTransferTimeSeconds(aOrigin, aTarget, muParent)`. Independent of window. Simplest; reuses the function exactly as the harness already does (`ReaimEndToEndInGameTest.BuildGeometryOrSkip` lines 414-416 compute the Duna tof this way). Recommended FIRST.
- B-radius-aware (per-window): use the actual radii at this window. `r1 = launchBody.orbit.getRelativePositionAtUT(D_k).magnitude`; estimate arrival with one fixed-point pass (`arrEst = D_k + HohmannTransferTimeSeconds(aOrigin, aTarget, muParent)`), `r2 = targetBody.orbit.getRelativePositionAtUT(arrEst).magnitude`, then `geomTof = HohmannTransferTimeSeconds(r1, r2, muParent)`. More accurate for a very eccentric target whose radius drifts across windows; still pure reuse of the same function (radii in place of SMAs). Escalate to this only if measurement (section 4.2) shows the SMA center plus the ecc band does not resolve every feasible Eeloo window.

D_k (`nominalDepartureUT = schedule.DepartureUTForWindow(windowIndex)`) is already computed at resolver line 144, so the radius reads are cheap and one-shot per window (the resolver caches by window).

#### 4.1.5 The eccentricity-scaled band (the bounded law - exact numbers are an open question, decided measure-first)

Half-width as a fraction of the center: `halfWidthFraction = clamp(baseFraction + eccGain * eTarget, baseFraction, maxFraction)`, where `eTarget = targetBody.orbit.eccentricity`, `baseFraction = 0.06` (today's +-6%, so low-ecc is unchanged), and `maxFraction` is a hard cap (candidate ~0.20, to be pinned by the Eeloo measurement) so the band cannot grow without bound and reintroduce knife-edge widening. The step grid stays fine (e.g. keep `TofSearchStepFraction = 0.005` and raise `SearchMaxSteps` so `SearchMaxSteps * step` reaches `maxFraction`), so the search resolution does not coarsen. The EXACT `eccGain`, `maxFraction`, step count, and whether the band is measured off the geometric center or off `max(geomTof, recordedTof)` are OPEN QUESTIONS to settle against the Eeloo fixture, not guessed in code (open questions 1-2).

Invariants the law MUST satisfy (these are the regression contract, testable on the pure helper):
- At `eTarget = 0`: the search is identical to today (center collapses to recorded tof region, band = +-6%). Provable zero regression for circular targets.
- The recorded tof is always within reach (window 0 = the recorded geometry always resolves).
- `halfWidthFraction <= maxFraction` always (bounded; no runaway widening).

#### 4.1.6 Where the center is computed: inline vs on the schedule

Two placements; pick during implementation:
- Inline in `BuildWindowSegments`: the bodies are in hand; smallest change; keeps `ReaimWindowSchedule` unchanged.
- A new `GeometricTofSeconds` (and/or per-window radius hook) field on `ReaimWindowSchedule`, set in `ReaimWindowPlanner.Plan` from the SMAs: keeps the resolver's hot path lean and makes the center unit-testable through the pure planner. But `Plan` does not currently take `muParent` or SMAs (only the two periods + recorded tof), so this widens its signature. The SMA-based center can be derived from periods + mu via Kepler's third law, but that is more machinery than B-minimal needs. Recommendation: compute inline for B-minimal; only add a schedule field if B-radius-aware proves necessary AND the radius reads want to move off the resolver hot path.

#### 4.1.7 Logging

Extend the existing Verbose lines (resolver lines 207-209 on decline, 244-248 on success) to log `recordedTof`, `geomTof`, `eTarget`, `halfWidthFraction`, and the `usedTofSeconds` deviation from BOTH centers, so a future "Eeloo declined" report can be diagnosed from KSP.log without a rebuild (per the logging-requirements doctrine: if it did not get logged, it did not happen). One-shot per window (cached), so Verbose is correct.

### 4.2 Stage 2: validation fixtures (Moho + Eeloo) on the M-MIS-1 harness

#### 4.2.1 The harness today (read `ReaimEndToEndInGameTest.cs`)

The harness runs at SPACECENTER, uses REAL stock bodies via `FlightGlobals.Bodies.Find(b => b.bodyName == "...")`, and drives `ReaimPlaybackResolver.TryResolveWindowSegments` for `WindowsToCheck = 5` consecutive synodic windows from a `PinnedScanBaseUT = 5_000_000.0`. Moho and Eeloo are stock bodies, so a fixture is a pinned departure + a synthetic member/plan for that target - not a new recording file. Four tests exist: strict mid-band (`CenterOfLongestRunIndex`), band-edge weak contract (`FirstSuccessIndex`), the observed-failure pin, and a manual-only feasibility sweep.

What must be parametrized (currently hardcoded to Kerbin/Duna; line numbers are current main, post-stage-A):
- `BuildGeometryOrSkip` (line 426): hardcodes "Kerbin"/"Duna" lookups and the Hohmann tof.
- `BuildMemberAndPlan` (line 493): hardcodes "Kerbin"/"Duna" body names and the parking/heliocentric/arrival segment SMAs+eccs.
- `AssertSaneWindowSegments` (line 528): hardcodes "Kerbin" parking + "Duna" arrival leg names.

Generalize these to take `(launchBodyName, targetBodyName)` and target-appropriate arrival-leg elements. The pinned-scan / mid-band / band-edge / sweep machinery and `ReaimFeasibilityScan` are target-agnostic and reused as-is.

#### 4.2.2 The critical trap: a naive eccentric fixture is a silent no-op

The harness uses the GEOMETRIC Hohmann tof as the stand-in for the recorded tof (the `ScanContext.TofSeconds` field, line 96, computed via `HohmannTransferTimeSeconds(...)` inside `BuildGeometryOrSkip`, and it is fed as `recordedTof` into `ReaimWindowPlanner.Plan` and as the search center). If an Eeloo fixture keeps that pattern, the synthetic recorded tof EQUALS stage B's geometric center, so stage B changes nothing and the fixture proves nothing. This is the non-trivial part the prompt flags: authoring these fixtures is real work, not free.

A fixture that actually exercises limitation B must:
1. Use a recorded tof that is a realistic recorded SAMPLE offset from the geometric center - i.e. the tof for the target at ONE specific true anomaly (the recording-time geometry), NOT the SMA-average Hohmann time. Concretely: pick the recorded departure UT, compute the actual `r2` at `departure + (SMA Hohmann tof)`, and either (a) derive the recorded tof from that single-window radius geometry, or (b) deliberately choose a departure where the target is near periapsis (short recorded tof) and drive windows where it has drifted to apoapsis (needs a longer tof), so the recorded +-6% band cannot reach the needed tof.
2. Demonstrate, with stage B DISABLED (or against the pre-stage-B build), that at least one window which has a real feasible geometric transfer DECLINES under the recorded-tof +-6% search (the failing case). Then assert stage B resolves it. This is the measure-first discipline (M-MIS-1 section 9, M-MIS-6 requirement 3): build the failing test, measure, then add the knob.

The decline MODE for an eccentric target is principally the non-sane (hyperbolic) conic: when the recorded tof is too short for a far (apoapsis) encounter radius, the Lambert ellipse degenerates to ecc >= 1 and `IsSaneTransferConic` (`ReaimTransferSynthesizer.cs:35-41`) rejects it across the whole +-6% band. (The near-180 retrograde mode is now stage A's; do not conflate.) The Eeloo fixture should drive enough windows that the target's radius swings across a meaningful fraction of `[a(1-e), a(1+e)]` so this mode actually fires; `WindowsToCheck` may need raising for the eccentric fixtures so the radius drift is observable, or the pinned departure chosen so an early window already sits at the unfavorable radius.

#### 4.2.3 Moho fixture (high-inclination + near-180 + moderate eccentricity)

Moho (inc ~7 deg, ecc ~0.2, small SOI) is the COMBINED case. It validates two things at once:
- Stage A's un-projection lift: with r2 un-projected and the launch-plane normal as the handedness axis, the inclined transfer must aim at Moho's actual position and the proximity check must find the encounter (the pre-stage-A projection would have missed Moho's SOI). This fixture is the in-game proof that requirement (1) is lifted by stage A's mechanism. (If stage A had NOT shipped, this fixture would decline; that is a useful canary on the A dependency.)
- Stage B's tof centering for Moho's eccentricity (~0.2): the same offset-recorded-tof construction as Eeloo.

Assert: window 0 resolves (the recorded departure); subsequent feasible windows resolve under stage B where they declined before; orientation rotates (longitude of periapsis, the M-MIS-1 robust metric); the transfer is prograde (inc < 90) even though it is now inclined a few degrees (do NOT assert near-zero inclination - that was the projection era's property and is gone).

#### 4.2.4 Eeloo fixture (high-eccentricity, the clean B isolate)

Eeloo (ecc ~0.26, inc ~6.15 deg) is the high-eccentricity isolate for limitation B. Same offset-recorded-tof construction (section 4.2.2). The strict-style test asserts every FEASIBLE window resolves under stage B; the measurement sweep records the per-window resolve/decline map both with and without the ecc band so the regression boundary is visible. Because Eeloo's synodic period with Kerbin is long and its eccentricity high, expect the recorded-tof +-6% search to decline several of the 5 windows pre-stage-B and resolve them post-stage-B; that delta IS the test.

#### 4.2.5 Requirement (3): relax shape-congruence for eccentric targets

The existing strict Duna test asserts ONLY orientation rotation (`lpeDelta > 1.0`) and a per-window sane conic; it records `firstEcc`/`firstSma` but does NOT assert they recur across windows, so it is already congruence-agnostic in its assertions. The eccentric fixtures must keep that posture and ADD an explicit comment/log that sma/ecc are EXPECTED to vary per window for an eccentric target (the congruent-window premise is shape-congruent only for circular targets; `todo:116` and the planner header both note this). Do not add a congruence assertion to the eccentric tests.

#### 4.2.6 Determinism

Reuse the M-MIS-1 determinism contract: pin the scan base (a fixed `PinnedScanBaseUT` per target, treated as part of the test contract), drive synthetic UTs from the schedule fields (never the live clock), and keep the cache-cleared re-solve equality check. Determinism is per-frame (KSP re-bases body epochs every frame, ~1e-15 noise flips only knife-edge scan entries); the eccentric tests must pick the band CENTER for any strict assertion (stable) and use the weak resolve-or-decline-cleanly contract at edges, exactly as the Duna tests do.

### 4.3 Stage 3 (CONTINGENCY ONLY): Gooding behind ITransferSolver

This is NOT a planned stage. Build it if and only if a synthetic Moho/Eeloo window PROVABLY still declines after stages 1-2 (section 5.2), because a converging solver cannot help a window whose geometry has no sane prograde elliptic transfer, and porting third-party math into the hottest correctness path with license bookkeeping to fix a hypothetical set is premature.

Ordering note (todo:117 requirement (4) is literally "widen the tof search FIRST and only then port the Gooding solver"): the B-radius-aware per-window centering (section 4.1.4) IS this plan's "widen the tof search first" step. Gooding is reached only AFTER B-minimal and B-radius-aware have both failed to resolve a provably-feasible window, so the build order (section 9 step 4) visibly satisfies requirement (4)'s ordering rather than skipping straight to the vendor.

If it triggers:
- The seam is already Gooding-ready: stage A added `Vector3d planeNormal` to `ITransferSolver.Solve`, and its doc explicitly anticipates a Gooding vendor consuming the normal as its angular-momentum axis h. So the vendor drops in as a new `ITransferSolver` implementation; no further seam change.
- SPDX gate (open question 4): stage A's commit message records that the MechJebLib Gooding/Izzo vendor path was license-verified - "permissive per-file SPDX on commit `c86723d`" of `MuMech/MechJeb2` - and recorded as a gated contingency, NOT vendored. So the gate has been checked once, against that pinned commit. Caveats this plan must keep honest: (a) MechJebLib is NOT cloned anywhere under the umbrella (verified 2026-06-13: no `Lambert/Gooding.cs`, `Lambert/Izzo.cs`, or `MechJebLib/` directory locally; the only `*mechjeb*` hits are unrelated KAC/PersistentRotation/RemoteTech files), so actually building the contingency means FETCHING `MuMech/MechJeb2` at the recorded `c86723d` (or whatever commit is vendored) and RE-confirming the per-file SPDX header on the EXACT copied commit before the file enters the repo - the verification is per-copied-commit, not "verified once forever". The expected permissive header is `Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+`; if it is absent on the copied commit, the file is GPLv3 and is DISQUALIFIED (Parsek is MIT) - the contingency dies there and we stay on the hinted UvLambert. Vendoring obligations if it passes: preserve the SPDX header + poliastro/MATLAB-FX attribution comments, add a NOTICE entry, drop the `DualV3`/STM overloads (they pull in Shepperd/Dual), swap the `V3` struct for `Vector3d`.
- Fail-closed wrapper: any Gooding port needs an explicit wrapper replicating UvLambert's contract - time-residual tolerance (relative to `sqrt(mu)*tof`), NaN/Inf velocity rejection, AND the `IsRetrogradeTransfer` direction guard re-run (Gooding can return a valid retrograde branch and carries no built-in self-check). Tests: `GoodingTransferSolverTests` mirroring `UvLambertTests` (Curtis 5.2 + round-trip + fail-closed), plus the lamberthub GPL solver used ONLY as an out-of-repo test ORACLE (its expected-velocity NUMBERS are uncopyrightable facts; its harness/fixtures must NOT be copied in).

## 5. Decisions

### 5.1 Is the stage-A solver change the universal default or gated?

DECISION: universal in the re-aim path; no setting, no gate, no toggle.

Rationale (two distinct claims, kept separate):
- SAFETY FOR EVERYONE ELSE: stage A's `UvLambert.Solve` plane-normal overload is a strict SUPERSET - `planeNormal = Vector3d.zero` / NaN reproduces the legacy `cross.z` path byte-for-byte (proven by the new Curtis 5.2 equality test), and `MinSinTransferAngle` (the collinear fail-closed bail) is untouched. This byte-identical 7-arg forwarder is what protects every NON-re-aim caller and the textbook tests; do not delete it.
- UNIVERSALITY FOR RE-AIM: the synthesizer ALWAYS passes a non-zero `launchPlaneNormal`, so the re-aim path is uniformly on the NEW stable-handedness path (it never exercises the fallback). The justification for that being universal rather than gated is that there is no behavioral regime where the legacy `cross.z` handedness is preferable for re-aim, so a gate would only add a way to mis-configure into the known-bad inc=180 branch.

### 5.2 Is the Gooding contingency still needed after A ships?

DECISION: not on current evidence; gated strictly on a provably-declining synthetic window after stages 1-2.

Rationale: stage A resolves the reported Duna near-180 decline (and, per its harness change, the M-MIS-1 ObservedEdgeDeparture window and likely the `dep=43/44` sweep declines) by stabilizing handedness, and lifts the Moho inclination cap by un-projecting r2. Stage B addresses the eccentric tof drift. If the Moho and Eeloo synthetic fixtures (section 4.2) resolve every FEASIBLE window after stages 1-2, there is no demonstrated residual that a multi-rev/Gooding solver would fix - and MechJebLib is not even cloned locally, so triggering the contingency carries a fetch + SPDX-verification cost that is unjustified without evidence. Re-open ONLY if a fixture surfaces a window with a real feasible prograde elliptic transfer that UvLambert (hinted) cannot find. Note the seam is already Gooding-ready (5.1's `planeNormal`), so deferring costs nothing structurally.

## 6. Tests

- Pure xUnit on the stage-B tof-centering helper: center law (SMA and, if built, radius-aware), ecc-scaled band, bounded cap (`<= maxFraction`), zero-expansion at `eTarget = 0` (regression contract), recorded-tof always within reach. Mirror the stage-A `UvLambertTests` style (explicit byte/round-trip/fail-closed assertions).
- In-game (`ReaimEndToEndInGameTest`, SPACECENTER, Periodicity category): parametrized Moho + Eeloo strict (feasible windows resolve), band-edge (resolve-or-decline-cleanly, deterministic), and manual-only sweep (the measure-first per-window map). Keep the existing Duna tests green (regression fence).
- Stage A's tests stay green (they are this plan's dependency, not its subject).
- Determinism + cache-vs-fresh equality preserved on the new fixtures (reuse `DriveWindowsResolveOrDeclineCleanly`'s re-solve check).

## 7. Fail-closed preservation (must hold at every layer)

- `UvLambert` time-residual check (`UvLambert.cs:168-179`) and `MinSinTransferAngle` collinear bail: unchanged.
- `IsSaneTransferConic` (ecc in [0,1), sma > 0) and `IsRetrogradeTransfer` direction guard in the synthesizer: unchanged; they remain the backstop that a wider tof band feeds candidates to.
- Resolver: a window that finds no sane, same-handedness, encountering conic across the (now ecc-scaled, bounded) tof search returns null segments -> the recorded faithful trajectory plays. Never a wrong conic.
- Stage B widens the SEARCH, not the ACCEPTANCE: every candidate still passes the full synthesizer guard chain. A wider band can only find MORE valid conics, never admit an invalid one.

## 8. Risks

- Regression of low-ecc (Duna) windows. Mitigated structurally by STEP-0-FIRST probing: the recorded tof is always the first candidate, so any window resolving today resolves identically regardless of band width (the widening only adds candidates reached AFTER the base band fails). A low-ecc target like Duna does widen modestly (ecc 0.051 -> ~8.55% with the as-shipped law), which is benign and bounded by the cap. The exact-`eTarget=0` identity is an xUnit assertion, the existing Duna in-game tests fence the tested windows, and a live Duna batch is the production fence; a low-ecc deadband is an easy follow-up if that fence shows any issue.
- Reintroducing the M-MIS-1 knife-edge widening. Mitigated by the hard `maxFraction` cap and the eccentricity gate (a low-ecc target gets no expansion). Decide `maxFraction` against the Eeloo measurement, not by intuition.
- Render-seam drift. A larger `usedTofSeconds` deviation (eccentric windows) means the transfer ellipse's natural arrival UT diverges further from the recorded arrival UT it is drawn against `[RecordedDepartureUT, RecordedArrivalUT]`. The geometry is still correct (the proximity check confirms the SOI encounter); the visual approach-to-arrival seam is owned by M-MIS-2's arrival hold. NOTE the interaction; do not try to fix the seam here.
- Fixture authoring under-scoped. The naive fixture is a silent no-op (section 4.2.2); budget the offset-recorded-tof construction + the pre-stage-B failing measurement as real work, gated by the measure-first discipline. A fixture that resolves with AND without stage B proves nothing.
- Stage A dependency drift. If stage A's final approach changes (e.g. reverts to conditional projection), the Moho fixture's "un-projection lifted the inclination cap" premise breaks; re-validate against stage A's merged diff before building.

## 9. Build order and stop-on-regression gates

Symptom-driven, each stage independently testable and revertible (the project "revert on regression" rule):

1. Stage A is LANDED (merged to main, PR #1140) - the reported bug is fixed. Before starting stage B, run the live in-game Periodicity SPACECENTER batch and confirm the Duna `ObservedEdgeDeparture` R/d map reads all-R, then promote stage A's `requireAllWindowsResolve` from FALSE to true (it is wired but soft on main; stage A hard-asserts only window 0). That promotion is stage-A follow-up, not a precondition this plan owns - but stage B should not start until the Duna near-180 windows are confirmed resolving, so a residual stage-B eccentric decline is not masked by a still-flipping handedness window.
2. Stage B-minimal (SMA geometric center + ecc-gated bounded band, recorded-tof step 0). Author the Eeloo failing fixture FIRST (measure the pre-stage-B decline), then add the knob. Gate: every Duna in-game test still green (no regression); the Eeloo feasible windows now resolve.
3. Moho fixture: validates stage A's inclination lift AND stage B's ecc centering on the combined case.
4. Only if a fixture window provably still declines: escalate to B-radius-aware (per-window radii), then - only if THAT still declines - the Gooding contingency (section 4.3 / 5.2).

A regression at any gate halts before the next stage.

## 10. Documentation updates (per commit)

- `CHANGELOG.md`: one user-facing line per shipped item (e.g. "Re-aim now re-plans transfers to eccentric targets (Moho, Eeloo) per synodic window."). No technical detail (house rule).
- `docs/dev/todo-and-known-bugs.md`: stage A ALREADY reconciled the M-MIS-3 inclination half + requirement (1) and the M-MIS-1 handedness reclassification (commit `0dd6bd3a6`); do NOT re-do those. This plan's STAGE-B PR updates the (renamed) M-MIS-3 entry for the eccentricity half only: mark (2) eccentricity tof centering done when stage B lands, mark (5) fixtures done, record the (3) shape-congruence relaxation, and the (4) Gooding contingency decision (deferred, gated). If the live all-R confirmation lands first, the `requireAllWindowsResolve` promotion + its M-MIS-1 note is a stage-A follow-up touch, not this PR's. PR #1139 (open) also edits this file (the M-MIS-4 entry) and `CHANGELOG.md`; rebase on current main first and expect at most a trivial section reconciliation (different entries / lines - see the PR #1139 coordination bullet in section 2).
- This plan: stage A is merged (PR #1140); keep the stage-A "what shipped" section (1.2) in sync only if a future change alters the shipped contract.

## 11. Open questions (settle measure-first, before coding the relevant stage)

1. Eccentricity band law constants: `eccGain`, `maxFraction` (cap), step count / `TofSearchStepFraction`, and whether the band is measured off the geometric center or off `max(geomTof, recordedTof)`. Pin against the Eeloo fixture measurement, not intuition.
2. Geometric center: SMA-based (B-minimal) vs per-window radius-aware (B-radius-aware). Start B-minimal; escalate only if a feasible Eeloo window still declines.
3. `WindowsToCheck` for the eccentric fixtures: does 5 windows swing the target's radius across enough of `[a(1-e), a(1+e)]` to fire the hyperbolic decline mode pre-stage-B, or must it be raised / the pinned departure chosen to start at an unfavorable radius?
4. Gooding SPDX RE-verification (only if stage 3 triggers): stage A already verified the permissive header on MechJeb2 `c86723d` (commit `0dd6bd3a6` message), but MechJebLib is not cloned locally, so building the contingency means fetching the exact commit to be vendored and RE-reading the per-file SPDX header on THAT copy; if absent the file is GPL and the contingency is disqualified. A fetch-then-read step, not a disk check, and per-copied-commit (not "verified once forever").
5. Should `usedTofSeconds` deviation feed the M-MIS-2 arrival hold (so the arrival seam tightens as the tof band widens), or stay strictly out of scope here? Likely out of scope; note for the M-MIS-2 owner.
