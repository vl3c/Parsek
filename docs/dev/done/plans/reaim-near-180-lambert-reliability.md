# Re-aim transfer solver reliability near the 180-degree singularity (M-MIS-3, limitation A) plus eccentric-target tof drift (limitation B)

## 1. Problem statement and exact failure mechanism

### 1.1 Symptom
The 2026-06-13 Duna playtest declined the first loop window:

```
[ReaimPlayback] window=0 ... synth failed across tof +-501430s search
  (transfer direction mismatch inc=180.00 deg, resultRetrograde=True recordedRetrograde=False)
  - faithful this window
```

The window fell back to the stale faithful transfer: the ghost flies to where the target WAS at record time, not where it IS this synodic window. The mission is correct (the player flew it), so a clean re-aim exists; the pipeline cannot produce it at this geometry.

### 1.2 Mechanism (this is NOT a Lambert convergence failure) - verified against source
1. `ReaimTransferSynthesizer.TrySynthesizeTransfer` (`Source/Parsek/Reaim/ReaimTransferSynthesizer.cs:83`) builds heliocentric endpoints `r1` (launch body at departure, `:127`) and `r2` (target body at arrival, `:128`).
2. To dodge the near-180 Lambert plane singularity it PROJECTS r2 into the launch body's orbital plane (`ProjectOntoPlane`, `:50-56`; applied `:153`) BEFORE the solve. At a Hohmann window the transfer angle is driven toward exactly 180 deg, so after projection r1 and r2 are very nearly collinear and antiparallel.
3. The solve goes through the seam `TransferSolver.Solve(mu, r1, r2, tof, prograde, out v1, out _)` (`:155`) - note the second velocity `v2` is discarded into `out _`. `UvLambert.Solve` (`Source/Parsek/Reaim/UvLambert.cs:68`) picks the prograde/retrograde branch purely from `sign(cross.z)` of `r1 x r2` (`:86-91`). With r1,r2 near-antiparallel that cross product is vanishingly small and its z sign is floating-point noise. The near-180 hard bail `MinSinTransferAngle` (`:94`) is far too tight to fire here, so UvLambert proceeds, converges (its time-residual fail-closed at `:168-179` PASSES: the conic genuinely flies r1->r2 in tof), and returns the retrograde branch (`inclination = 180.00`).
4. `UpdateFromStateVectors` builds the conic (`:164`); `IsSaneTransferConic` accepts it (`:35-41`). The direction guard then correctly rejects it (`:182-188`): `resultRetrograde = IsRetrogradeTransfer(180.00) = true` (`:67-70`), `!prograde = false`, mismatch -> return false. The exact log string is built at `:185-187`.
5. `ReaimPlaybackResolver.BuildWindowSegments` runs a fixed-departure, +-6% tof-only search (`Source/Parsek/Reaim/ReaimPlaybackResolver.cs:189-204`): `TofSearchStepFraction = 0.005`, `SearchMaxSteps = 12`. Departure is pinned at D_k and every candidate r2 is re-projected onto the same antipodal geometry, so `sign(cross.z)` stays noise-driven-negative across all 25 candidates. The loop exhausts, `transferOrbit` stays null, `:205-211` logs the failure with band width `12 * 0.005 * TofSeconds ~= 501430s` (matching `+-501430s`).

Conclusion: the singularity is NOT in UvLambert convergence; it is in handedness selection from a degenerate `r1 x r2`, created by the projection meant to stabilize it. The direction guard is the intended safety net firing correctly; the defect is that nothing in the pipeline produces the prograde branch at the exact-180 window, so the guard fires on every candidate.

### 1.3 Two distinct M-MIS-3 limitations
Per `docs/dev/todo-and-known-bugs.md:112-118`, M-MIS-3 owns two separable problems:
- (A) Near-180 / handedness flip (the observed Duna decline): branch selection from a noise-dominated cross product after projection. THIS is the reported, must-fix bug.
- (B) Per-window tof drift for eccentric targets (Moho/Eeloo-class): the target sits at a different radius each window, so the geometrically-required tof routinely leaves the +-6% recorded-tof band. Not the Duna failure, but the second half of M-MIS-3.

A complete M-MIS-3 fixes both; the minimum viable fix addresses (A) and fails (B) closed.

### 1.4 What the documented M-MIS-3 directions actually say (verified verbatim)
This plan was reconciled against the real requirements text. Important corrections to any prior framing:
- Documented direction (1) for inclination/near-180 is literally: "make the projection CONDITIONAL - true 3D r2 except inside a guard band around the ~180-deg singularity (or skip projection whenever the out-of-plane offset `a*sin(inc)` exceeds a fraction of the target SOI)." There is NO "plane-normal hint" or seam-widening anywhere in the requirements. Conditional projection is the sanctioned (A) lever and is treated as the primary candidate here.
- Documented direction (4): "for residual near-180-deg windows, widen the tof search first and only then port the Gooding multi-rev solver behind `ITransferSolver`." This ordering is Gooding-vs-tof. It is NOT a statement that tof recentering should precede the handedness fix. Gooding is a last-resort contingency, not a planned stage.
- Documented direction (2) is the geometry-aware tof centering for eccentricity (limitation B), explicitly reusing `TransferWindowMath`.

### 1.5 The seam
`ITransferSolver.Solve(double mu, Vector3d r1, Vector3d r2, double tof, bool prograde, out Vector3d v1, out Vector3d v2)` (`Source/Parsek/Reaim/ITransferSolver.cs`); default `UvLambertTransferSolver.Default` (`ReaimTransferSynthesizer.cs:29`); single call site `:155`. Contract: pure double math, no Unity, fail-closed, consistent SI units, must keep `UvLambertTests` (Curtis 5.2 + round-trips + degenerate fail-closed) green. A better Lambert called through the CURRENT signature still receives the same near-antipodal r1/r2 and the same single `prograde` bit; swapping the solver alone cannot fix (A) because the degeneracy is created upstream by the projection. The cheapest (A) fixes therefore live in the synthesizer/resolver, not behind the seam.

## 2. License and reuse constraints (gating) - confirmed correct

Parsek is MIT (`LICENSE`, README:94). MIT into GPL is fine; pulling GPL/LGPL/CC-BY-SA source INTO Parsek and staying MIT is not. This disqualifies MechJeb-the-repo (GPLv3), pykep (GPLv3), lamberthub (GPLv3), Vallado/CelesTrak (AGPL), and GPL KSP mods (KAC's only orbital math is GPL-3 MuMech and has no Lambert).

The one viable vendored solver: `MechJebLib/Lambert/Izzo.cs` and `Gooding.cs` carry per-file permissive SPDX headers (`Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+`) that supersede MechJeb's repo-wide GPLv3 FOR THOSE FILES. Elect MIT or 0BSD. They depend only on a `V3` struct (swap for Parsek `Vector3d`); the `DualV3` STM overloads must be dropped (they pull in `Shepperd`/`Dual`), the scalar `Solve` is self-contained. License obligations if vendored: preserve the SPDX header + poliastro/MATLAB-FX attribution comments, add a NOTICE entry, and VERIFY the header is present on the exact copied commit (if absent on that commit the file is GPL and is disqualified). This gate must be done by reading the actual file at the copied commit, never assumed.

`lamberthub` (GPL) is usable only as an out-of-repo test ORACLE - its expected-velocity OUTPUT NUMBERS baked into a committed test file are uncopyrightable facts, but its test HARNESS code/fixtures must NOT be copied into the repo.

All non-Gooding options reuse Parsek's own MIT `UvLambert` or the in-repo MIT `TransferWindowMath` (KAC-derived, attributed). No new solver is hand-rolled in any option (standing rule + the M-MIS-3 "reuse, never a new solver" mandate).

## 3. Options

### Option 1 -- Flipped-bit re-solve in the synthesizer (smallest possible (A) fix)
Approach. When the direction guard fires (`ReaimTransferSynthesizer.cs:183-188`), instead of returning false immediately, re-call `TransferSolver.Solve` once with the flipped `prograde` bit, rebuild the conic, and re-check direction + sanity + the SOI encounter. At a near-180 window the prograde and retrograde branches ARE the two candidates; if the first comes back retrograde, the flipped request yields the prograde one. No seam change, no new solver, no projection change.

Reuse / license. Parsek MIT `UvLambert` unchanged; MIT, no vendored code, no seam churn.

Fixes. (A) near-180 handedness: PARTIAL-to-YES, with a caveat - both near-180 branches are ill-conditioned, so the flipped request may return a prograde-but-degenerate conic that `IsSaneTransferConic` or the downstream encounter check (`:196-199`) still rejects. It is fail-closed (faithful fallback) but may silently not resolve. (B) tof drift: NO.

Effort. Very low (~10-15 lines + tests in the synthesizer).

Risk. Low. Touches one decision branch; fail-closed preserved end to end. Main subtlety: the flip must re-run ALL downstream guards, not just the direction check, and must not loop.

Test strategy. xUnit on the flip branch (a stubbed `ITransferSolver` returning retrograde-then-prograde must produce a resolved prograde conic; returning retrograde-then-still-bad must fall through to false). In-game: re-run the pinned Duna `ObservedEdgeDeparture` harness window and confirm `window=0` resolves.

### Option 2 -- Conditional projection (the documented direction (1)) -- RECOMMENDED core of (A)
Approach. The projection at `ReaimTransferSynthesizer.cs:153` is what CREATES the antipodal degeneracy by flattening r2 onto r1's plane. Make it conditional, exactly as the requirements specify: use the TRUE 3D r2 outside a singularity guard band (so `r1 x r2` is well-conditioned and handedness is stable from the real out-of-plane component), and keep the projection only inside the guard band around ~180 deg OR skip projection whenever the out-of-plane offset `a*sin(inc)` exceeds a fraction of the target SOI (where projection guarantees an SOI miss anyway). The synthesizer already documents this exact limitation at `:144-150`. This directly attacks the root cause and is strictly cheaper than any seam-widening: it touches only `:151-153`, with NO `ITransferSolver` signature change, NO `UvLambertTests`/`TransferSolverInterfaceTests` churn, and NO implementer fan-out.

Reuse / license. Parsek MIT `UvLambert` unchanged + the already-computed `launchPlaneNormal` (`:151`); MIT, no vendored code, no seam change.

Fixes. (A) near-180 handedness: YES for the inclination half AND restores a well-conditioned `r1 x r2` outside the band (the Duna case has a real, if tiny, out-of-plane component once unprojected, giving a stable sign). It also lifts the documented high-inclination SOI-miss limitation (Moho), which Option 1 does not. (B) tof drift: NO.

Effort. Low-medium. Localized to the projection decision + a guard-band/SOI-fraction predicate, both pure and unit-testable.

Risk. Low-medium. The projection comment (`:130-150`) is emphatic that the projection MUST be kept to stabilize the branch. The reconciliation: conditional = keep the projection INSIDE the guard band (preserving today's stabilization for the truly-degenerate case) and use true-3D r2 OUTSIDE it (where the cross product is already well-conditioned). The band threshold must be picked so the in-band cases that resolve today at step 0 do not regress. Open question: does the exact-180 Duna window fall INSIDE the guard band (where projection still degenerates handedness) - if so, conditional projection alone may need Option 1's flipped-bit re-solve as the in-band companion. These two options compose cleanly and are the recommended (A) pair.

Test strategy. Pure xUnit on the projection-decision predicate (in-band -> projected, out-of-band -> true 3D; SOI-fraction threshold). A near-180 case with a small real inclination must produce the prograde branch with true-3D r2. In-game: the pinned Duna `ObservedEdgeDeparture` window resolves; add a synthetic Moho high-inclination window (authoring required, see Open Question 6) and confirm the SOI-miss decline is lifted; existing in-band feasibility windows must NOT regress.

### Option 3 -- Geometry-aware tof centering (the documented direction (2), reuse TransferWindowMath)
Approach. Address limitation (B). Replace the recorded-tof-centered +-6% search at `ReaimPlaybackResolver.cs:189-204` with a search CENTERED on the tof implied by THIS window's actual radii, from the in-repo `TransferWindowMath.HohmannTransferTimeSeconds(aOrigin, aTarget, muParent)`. The synthesizer docstring already says `tofSeconds` "should be the Hohmann time for THIS window's geometry" (`:80-81`) but the resolver still feeds recorded tof. Center on the geometric Hohmann time and scale the band by target eccentricity so eccentric targets stay in range.

Reuse / license. In-repo MIT `TransferWindowMath` (KAC-derived, attributed); MIT, no new solver, no vendored code, no seam change.

Fixes. (A) near-180 handedness: NO (and explicitly do not rely on it for (A) - at the synodic optimum the geometric Hohmann tof IS the ~180-deg point, so recentering there does not escape the singularity). (B) tof drift: YES (the actual eccentricity half of M-MIS-3).

Effort. Low-medium for the centering helper itself; BUT the validation requires authoring deterministic synthetic Moho AND Eeloo windows on the M-MIS-1 pinned-UT harness, which do not exist yet (Open Question 6) - fold that fixture work into the estimate or this option is understated.

Risk. Medium. `docs/dev/todo-and-known-bugs.md:101` records an explicit DECISION against tof-search WIDENING for the M-MIS-1 knife-edge declines ("a real recorded departure lies inside the band by definition"). This option is RECENTERING (on geometric tof) + ecc-scaled banding for eccentric targets where recorded tof is genuinely wrong this window, which is the sanctioned direction (2) - but it sits adjacent to that refused widening and must not regress the in-band cases that resolve at step 0. The ecc band-scaling law must be bounded so it does not reintroduce knife-edge widening.

Test strategy. Pure xUnit on the centering helper (geometric center matches `HohmannTransferTimeSeconds`; band scales with ecc; bounded). Regression guard: every currently-resolving M-MIS-1 feasibility-band window still resolves. In-game: synthetic Eeloo/Moho eccentric windows resolve where recorded-tof centering missed; the pinned Duna window must NOT regress.

### Option 4 -- Conditional-projection + flipped-bit re-solve for (A), then geometry-aware tof for (B), Gooding only as a proven contingency (RECOMMENDED)
Approach. Combine the documented directions in the order the SYMPTOM dictates:
1. Stage 1 (the reported bug): conditional projection [Option 2] as the (A) core, with flipped-bit re-solve [Option 1] as the in-band companion. Validate the Duna `ObservedEdgeDeparture` window resolves.
2. Stage 2 (the eccentric half): geometry-aware tof centering [Option 3]. Validate no in-band regression and that synthetic Eeloo/Moho windows resolve.
3. Stage 3 (contingency ONLY): if and only if a synthetic Moho/Eeloo window PROVABLY still declines after stages 1-2, vendor permissive `MechJebLib/Lambert/Gooding.cs` behind `ITransferSolver` (per the SPDX gate) and route only the residual band to it. This is not a planned stage; it is gated on demonstrated need, because if stages 1-2 resolve every known case there is no evidence any residual window needs Gooding, and porting third-party math into the hottest correctness path with license bookkeeping to fix a hypothetical set is premature.
Throughout: fail-closed is preserved (UvLambert time-residual `:168-179`, the synthesizer direction + sanity + encounter guards, and faithful fallback). Any Gooding port, if it ever happens, needs an EXPLICIT wrapper that replicates UvLambert's time-residual contract (relative tolerance to `sqrt(mu)*tof`), rejects NaN/Inf velocities, AND re-runs the `IsRetrogradeTransfer` direction guard (Gooding can return a valid retrograde branch and carries no built-in self-check).

Reuse / license. Parsek MIT `UvLambert` + Parsek MIT `TransferWindowMath`; vendored permissive Gooding only if the contingency triggers. All reuse mandates honored; no hand-rolled solver.

Fixes. (A) near-180 handedness: YES (conditional projection + flipped-bit re-solve). (B) tof drift: YES (geometry-aware tof). Residual near-180: Gooding contingency if proven.

Effort. Medium. Stages 1-2 are the bulk; stage 3 likely never fires.

Risk. Medium, but each stage is independently testable and revertible (the project "revert on regression" rule). Build in the symptom-driven order so the reported bug is fixed FIRST and a regression at any stage halts before the next.

Test strategy. Union of Options 1-3 plus the staged in-game gates: textbook `UvLambertTests` green; flipped-bit and conditional-projection xUnit; ecc-tof centering xUnit; on the M-MIS-1 pinned-UT harness, the deterministic Duna `ObservedEdgeDeparture` (near-180) window resolves, synthetic Moho (high-inc + near-180) and Eeloo (high-ecc tof drift) windows resolve, and existing in-band feasibility windows do not regress. If stage 3 triggers: `GoodingTransferSolverTests` (Curtis 5.2 + round-trip + fail-closed, mirroring `UvLambertTests`) plus the out-of-repo lamberthub oracle for the port.

## 4. Recommendation

Option 4, built in the symptom-driven staged order, with a hard stop-on-regression gate between stages.

Rationale:
- It fixes the REPORTED bug first. Section 1.2 proves the Duna decline is a handedness flip; Option 3 admits tof recentering is at best partial for (A) and "not guaranteed at the synodic optimum where the geometric Hohmann time IS the 180-deg point." Therefore the handedness fix (conditional projection + flipped-bit re-solve) is stage 1, and geometry-aware tof is the eccentric-target follow-up. Earlier framing that put tof recentering first misapplied direction (4) (a Gooding-vs-tof ordering) to the wrong pair.
- It uses the SANCTIONED (A) lever. The documented direction (1) is conditional projection, not a plane-normal hint (which appears nowhere in the requirements). The conditional-projection path is also the cheapest real (A) fix (no seam change, no implementer fan-out) and directly removes the degeneracy the projection itself creates.
- It maximizes reuse of trusted in-repo MIT code (`UvLambert`, `TransferWindowMath`) and confines any vendored Gooding port to a contingency that fires only on demonstrated need - best reliability-per-effort while satisfying the reuse mandate, the seam contract, and MIT compatibility.
- Fail-closed is preserved at every layer, so no option here can render a plausible-but-wrong conic.

If scope must shrink: ship stage 1 alone first (conditional projection + flipped-bit re-solve) - it directly fixes the reported Duna decline at lowest risk and is the highest-value single increment. Add stage 2 (eccentric tof) as the follow-up. Stage 3 (Gooding) only if a synthetic window provably still declines.

Note on the seam-widening "plane-normal hint": it was the centerpiece of an earlier draft but is dropped here. It is not a documented direction; near 180 deg the hint reduces to essentially the same reference-normal axis that is already noise-dominated UNLESS paired with the pre-projection (un-projected) r2, which the synthesizer does not currently thread to the seam; and conditional projection achieves the same end (well-conditioned `r1 x r2`) without a signature change or implementer fan-out. If conditional projection plus flipped-bit re-solve ever proves insufficient in the guard band, revisit the hint as a deliberate seam extension at that point - not before.

## 5. Open questions
1. Guard-band threshold for conditional projection. What `sin(transfer angle)` (or `a*sin(inc)` / target-SOI-fraction) threshold switches between true-3D r2 and projected r2, chosen so the in-band windows that resolve today at step 0 do not regress? Decide before coding stage 1.
2. Does the exact-180 Duna window fall INSIDE that guard band? If yes, conditional projection alone still degenerates handedness in-band and the flipped-bit re-solve is the load-bearing in-band fix; if no, conditional projection alone suffices and the flip is belt-and-suspenders. Measure on the pinned `ObservedEdgeDeparture` UT first.
3. Does the flipped-bit re-solve produce a conic that PASSES the downstream SOI encounter check (`:196-199`), not merely one that picks the prograde branch? Both near-180 branches are ill-conditioned; validate the flip yields an encounter-valid conic, else it silently does nothing (fail-closed, but no fix).
4. Eccentricity band-scaling law. What function of target eccentricity scales the tof search half-width, and how is it bounded so it does not reintroduce the M-MIS-1 knife-edge widening explicitly decided against (`todo:101`)?
5. Synthetic test fixtures. The M-MIS-3 validation requires deterministic synthetic Moho AND Eeloo windows on the M-MIS-1 pinned-UT harness; these do not exist yet and authoring them is non-trivial work that must be folded into the stage-1/stage-2 effort, not treated as free.
6. Gooding SPDX verification (only if stage 3 triggers). Confirm the permissive per-file SPDX header is present on the exact `Gooding.cs` commit to be vendored by READING the file at that commit; if absent, the file is GPL and the contingency is disqualified (stay on hinted/conditional UvLambert).
7. Should the discarded `v2` (currently `out _` at `:155`) be captured? The returned conic's angular momentum `r1 x v1` already reveals handedness independent of the degenerate `r1 x r2`; it is read implicitly via `transfer.inclination` at `:182`. Capturing `v2` is also a prerequisite if a future objective switches to v_inf matching (the prior "reaim-arrival-timing" direction). Out of scope for the (A) fix but worth noting as a zero-cost observability/diagnostic seam.
