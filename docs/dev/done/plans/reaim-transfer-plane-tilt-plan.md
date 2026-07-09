# Re-aim heliocentric transfer plane tilt (Bug A) — fix plan

> Status: REVIEWED + REVISED plan. Implementation not started.
>
> RE-BASELINED against merged `origin/main` @ `3c6143b0e` (2026-06-24, a large split-giant-files-into-
> partials refactor, 67 commits). Verified the refactor left EVERY file this plan touches byte-identical
> — `ReaimTransferSynthesizer.cs`, `UvLambert.cs`, `ITransferSolver.cs`, `ReaimOrbitSegmentConverter.cs`,
> `InGameTests/ReaimEndToEndInGameTest.cs`, `Tests/UvLambertTests.cs` all unchanged; the target test home
> `Tests/ReaimTransferSynthesizerTests.cs` exists; every §3 line number below still matches. Full suite
> green at 16235 on the merged base. No plan content changed by the merge.
>
> This is the authoritative plan for Bug A. It supersedes the three candidate drafts in this
> directory by selecting and composing the winning approach after a candidate bake-off, a judgment
> pass, and three adversarial reviews. The review-driven revisions (the **achievable-plane gate**
> that fixes the `ConstrainTransferPlane` over-determination, the corrected geometry
> characterization, and the added guards/tests) are catalogued in §11 "Review resolutions".
>
> It RE-BASELINES — does not resume — the pre-`0dd6bd3a6` plan on branch `reaim-lambert-reliability`
> (`reaim-near-180-lambert-reliability.md`). That plan targeted a DIFFERENT symptom (the near-180
> DECLINE) on code where the `r2` projection was still present. `0dd6bd3a6` already shipped the
> decline fix (deleted the projection, threaded a launch-plane handedness normal through the seam),
> and the live symptom is now the plane TILT. See §10 for the reconciliation.

---

## 1. Problem statement and re-baselined mechanism

### 1.1 Symptom (the tilt)

A re-aimed Kerbin→Duna looping interplanetary transfer renders the heliocentric leg TILTED OUT OF
PLANE ("upwards"). Measured rendered Sun-relative inclinations across three consecutive synodic
windows of the same Duna mission:

| window | rendered inc | sma | ecc | log |
|--------|-------------|-----|-----|-----|
| loop1  | **2.3573°** | 17.18e9 | 0.2120 | `logs/2026-06-23_2129_duna-one-reaim-break/KSP.log:17112` |
| loop2  | **5.0573°** | 16.83e9 | 0.1983 | `…:21215` |
| later  | **0.1312°** | 16.72e9 | 0.1896 | `logs/2026-06-23_2259_duna-reaim-bugb-deployed/KSP.log:15810` |

Duna's real orbital inclination is ~0.06°, Kerbin's is 0.0°. The spurious EXCESS over Duna's plane
is therefore 2.30° / 5.00° / 0.07°. The three windows are each exactly one Kerbin–Duna synodic
period apart (gaps 19,653,075 s vs synodic 19,645,699 s, 0.04% match), and the inclination swings
38.5× between them with no monotonic trend. That erratic per-window swing is the textbook signature
of near-antiparallel ill-conditioning, NOT a fixed systematic offset.

A SEPARATE Bug B ("ends far from Duna", an arc-clip truncation) was already fixed and merged as
PR #1189 — this worktree is branched on top of that. Bug B is OUT OF SCOPE. This plan fixes ONLY the
plane tilt.

### 1.2 Verified mechanism (post-`0dd6bd3a6`)

`ReaimTransferSynthesizer.TrySynthesizeTransfer` (`Source/Parsek/Reaim/ReaimTransferSynthesizer.cs`)
builds heliocentric endpoints `r1` (launch body at departure, `:148-150`) and `r2` (target body at
arrival, `:151`), computes `launchPlaneNormal = r1 × v_launch` (`:180-182`), and solves Lambert
through the `ITransferSolver` seam (`TransferSolver.Solve(mu, r1, r2, tof, prograde,
launchPlaneNormal, out v1, out _)`, `:184`). It then builds the conic
(`transfer.UpdateFromStateVectors(r1.xzy, v1.xzy, parent, departureUT)`, `:193`) and runs
`IsSaneTransferConic` (`:195`) + the `IsRetrogradeTransfer` direction guard (`:212-218`) before
propagating through `PatchedConics.CalculatePatch` (`:226`).

The tilt has a single, fully verified cause:

1. **The transfer plane IS `plane(r1, r2)` by construction.** `UvLambert.Solve` returns
   `v1 = (r2 - f·r1) / g` (`UvLambert.cs:218`) — a linear combination of `r1` and `r2`. So
   `v1 ∈ span(r1, r2)`, and the conic's angular momentum `h = r1 × v1` lies along `r1 × r2`. The
   rendered inclination is fully determined by the orientation of the `(r1, r2)` plane.

2. **`r2` is raw / un-projected.** `0dd6bd3a6` deleted the old `r2 = ProjectOntoPlane(r2,
   launchPlaneNormal)` line. `r2` now carries the target's full out-of-ecliptic z-offset on both
   the direct and parking-override paths (the override swaps only `r1`).

3. **Near-antiparallel amplification (quantified — see §1.4).** A Kerbin→Duna Hohmann sweeps ~180°,
   so `r1` and `r2` are near-antiparallel and `r1 × r2` has near-zero magnitude whose DIRECTION is
   dominated by Duna's z-component DIVIDED BY the tiny in-plane chord-perpendicular distance. Even
   with NO numerical error, the *clean* `plane(r1,r2)` inclination for Duna's real 21.7 Mm z-offset
   reaches **3.4° at a 179° transfer angle** (vs Duna's real 0.06°) — a 57× amplification. This is
   the bug: the conic carries an amplified projection of the target's z-offset as plane tilt.

4. **`launchPlaneNormal` is handedness-only.** Inside `UvLambert` it is used ONLY as
   `handed = Dot(Cross(r1,r2), planeNormal)` to pick the prograde/retrograde branch sign
   (`UvLambert.cs:116-122`); it never enters `r1`, `r2`, the Newton iterate, or the returned `v1`.
   So it fixed the DECLINE (branch flip) but does nothing to constrain the solved PLANE — which is
   why the tilt persists WITH the normal active.

5. **The existing guards structurally miss it.** `IsRetrogradeTransfer` rejects only `inc > 90°`
   (`:77-80`); `IsSaneTransferConic` rejects only NaN/Inf/`ecc ≥ 1`/`sma ≤ 0` (`:35-41`). A 2–5°
   tilt passes both. The `synth geometry (proximity)` diagnostic is also blind: a Lambert solution
   threads both endpoints by construction (`xfer-vs-Duna@arrival=0m`) regardless of plane tilt.

The tilt flows downstream verbatim: `ReaimOrbitSegmentConverter.ToSegment` copies `orbit.inclination`
directly (`ReaimOrbitSegmentConverter.cs:21`), and `ReaimSegmentAssembler.ShiftInTime` touches only
`startUT/endUT/epoch`, never the plane elements. So correcting the `Orbit` at the synth chokepoint is
sufficient; the converter/assembler must NOT be touched.

### 1.3 The design tension (the crux)

The fix must flatten the SPURIOUS amplified tilt WITHOUT killing a target's REAL inclination. The
quantity we WANT the transfer to carry is the TARGET BODY's own orbital-plane inclination
(Duna ~0.06°, Moho ~7°), NOT the near-antiparallel-amplified `plane(r1,r2)` inclination. The
discriminator is therefore: **does the solved plane differ from the target's own orbital plane by
more than tolerance?** That difference is the spurious excess; everything that lands ON the target
plane is the real inclination we keep.

A FLAT absolute inclination cap (e.g. `inc ≤ 2°`) provably fails: Moho's real 7.0° exceeds Duna's
worst spurious 5.06°, so a flat cap would wrongly reject a legit Moho transfer AND wrongly accept the
2.36° Duna tilt. The discriminator must be **target-derived**.

### 1.4 Verified geometry (numerical, headless — see §5.1 fixtures)

The mechanism and every quantitative claim below were verified with a standalone vector model
(`/tmp/tilt_check*.py` during planning; the same constants seed the headless xUnit fixtures). The
load-bearing facts the fix relies on:

- **Clean `plane(r1,r2)` inclination grows toward 180°.** For a target at projected helio-longitude
  θ from `r1`, at its real maximum latitude (worst z-phase), the clean transfer-plane inclination is:

  | θ (deg) | Duna (real inc 0.06°) | Moho (real inc 7°) | Eve (2.1°) | Eeloo (6.15°) |
  |---------|----------------------|--------------------|-----------|---------------|
  | 150     | 0.12° | 13.8° | 4.2° | 12.2° |
  | 170     | 0.35° | 35.3° | 11.9° | 31.8° |
  | 178     | 1.72° | 74.1° | 46.4° | 72.1° |
  | 179     | 3.43° | 81.9° | 64.5° | 80.8° |

  **Consequence (the over-flatten reviewer's correct point):** the bound
  `max(launchInc,targetInc)+tol` is NOT an envelope for the CLEAN transfer-plane inc near 180° — that
  inc routinely EXCEEDS the bound for every target (it IS the bug). So `IsExcessiveTiltTransfer` fires
  on essentially every near-180 window, for Duna AND Moho. This is correct and intended: the guard's
  job is exactly to catch "the solved plane departed from the target plane".

- **Achievability when holding `r1` fixed.** A plane that CONTAINS the fixed departure point `r1` and
  whose normal is closest to the target normal `nTarget` is `n_ach = normalize(nTarget − (r̂·nTarget)r̂)`.
  Its inclination equals `nTarget`'s only when `r̂ ⟂ nTarget`; at adverse phase (r̂ near the
  node-perpendicular) it collapses toward 0°:

  | r1 phase vs target node | Duna achievable inc | Moho achievable inc |
  |-------------------------|---------------------|---------------------|
  | 0° (on node)            | 0.0600° | 7.000° |
  | 45°                     | 0.0424° | 4.96°  |
  | 90° (node-perp)         | 0.0000° | 0.00°  |

  For **Duna** (`nTarget ≈ ecliptic`) the achievable inc is within ~0.06° of target at ALL phases, so
  flattening onto `nTarget` is always correct. For **Moho** at adverse phase the achievable plane is
  far below Moho's real 7° — flattening there would re-introduce the deleted projection's
  low-inclination cap. **This is the genuine flaw in the naive `ConstrainTransferPlane`** and is
  resolved by the achievable-plane gate (§2.2.1).

---

## 2. Recommended approach

**Composition: a post-solve, target-derived, achievability-gated plane correction.** Inserted in
`TrySynthesizeTransfer` AFTER the conic is built and AFTER the existing sanity + direction guards,
BEFORE `CalculatePatch`. Three parts: a discrimination guard, an achievability gate, and the
correction body. Fail-closed throughout.

### 2.1 Discrimination rule (the guard spine)

```
InclinationToleranceDegrees = 0.5    // constant
InclinationBoundDegrees(launchInc, targetInc) = max(max(launchInc, targetInc), 0) + tol
IsExcessiveTiltTransfer(inc, bound)  = !double.IsNaN(inc) && inc <= 90 && inc > bound
```

`launchInc = launchBody.orbit.inclination`, `targetInc = targetBody.orbit.inclination`. The bound is
the **target's own orbital-plane inclination plus tolerance** — the inclination a correct re-aimed
transfer SHOULD carry. The `inc <= 90` clause keeps the predicate orthogonal to `IsRetrogradeTransfer`
(a retrograde conic is already declined upstream), so the correction only ever runs on a sane,
prograde conic.

This predicate FIRES on every near-180 window whose solved `plane(r1,r2)` inc exceeds the target's
real inclination (Duna 2.36°/5.06° ✓; the 0.13° later window is ≤ ~0.56° bound, no-op; Moho near-180
windows also fire, see §2.2.1). Firing is not the fix — it is the trigger for the achievability-gated
correction below. **This is the only construct that separates "solved plane departed from the target
plane" from "solved plane equals the target plane", because the threshold scales with the target's
own inclination rather than being absolute.**

### 2.2 Correction body (post-solve plane re-pinning, achievability-gated)

When `IsExcessiveTiltTransfer` is true, re-orient the SOLVED conic's plane onto the target body's own
well-conditioned orbital plane, rotating only `v1`'s transverse component and keeping `r1` fixed:

- **Intended normal:** `nTarget = normalize(r2 × v2Target)` where `v2Target =
  targetBody.orbit.getOrbitalVelocityAtUT(arrivalUT).xzy` (un-swizzled into the SAME `.xzy` Lambert
  frame as `r1`/`r2`). `r2 × v2Target` is the target body's orbital angular-momentum direction — a
  large, well-conditioned vector that carries the target's REAL inclination + RAAN. (The
  h-direction is plane-invariant for a Kepler orbit, so an eccentric target's true anomaly at arrival
  does not perturb it — relevant for Eeloo/Moho.)

#### 2.2.1 The achievability gate (resolves the over-determination blocker)

Holding `r1` fixed, the BEST achievable plane normal is `n_ach = normalize(nTarget − (r̂·nTarget)r̂)`,
where `r̂ = normalize(r1)`. Its inclination is `incAch = acos(|n_ach.y|/|n_ach|)·Rad2Deg`. **(FRAME, learned
during implementation: the un-swizzled `.xzy` frame the synth works in is KSP's WORLD frame, Y-up — the
reference-plane normal is +Y, NOT +Z. An early `.z` here read an ecliptic-coplanar Duna plane as ~90°,
declining every firing window to faithful in-game; corrected to `.y` and validated by a live intercept.
The headless tests originally encoded the same z-up convention, so they passed while wrong — frame
correctness can only be confirmed in-game, not by hand-made-vector unit tests.)** The correction is applied
ONLY when the achievable plane lands ON the target plane:

```
ApplyConstraintIsSafe(r1, nTarget, targetInc, tol) :=
    n_ach non-degenerate AND |incAch − targetInc| <= tol
```

- **Duna**: `nTarget ≈ ecliptic`, so `incAch ≈ targetInc (≈0.06°)` at ALL r1 phases → gate is ALWAYS
  satisfied → correction applies → spurious 2–5° collapses to ~0.06°. The Duna fix ships.
- **Moho at favorable phase** (r1 near the node line): `incAch ≈ 7°` → gate satisfied → correction
  lands on Moho's real plane.
- **Moho at adverse phase** (r1 node-perpendicular): `incAch` collapses toward 0° → `|incAch − 7| > tol`
  → gate FAILS → **decline to faithful** (never flatten Moho). Fail-closed.

This gate is the difference between this plan and the naive `ConstrainTransferPlane`: it refuses to
correct in exactly the geometries where holding `r1` fixed cannot reach the target plane. It NEVER
over-flattens an inclined target — at worst it declines that window to faithful replay. For the
reported Duna One bug the gate is a no-op (always passes); it is the safety net that makes the
correction sound for inclined targets too.

#### 2.2.2 The rotation

When the gate passes, keep the radial part of `v1` (`v_rad = (v1·r̂)·r̂`) and rotate only the
transverse part onto the intended plane: `that = normalize(nTarget × r̂)`,
`v1' = v_rad + |v_perp|·sign(v_perp·that)·that`. This preserves `|v1|` exactly (so sma/energy are
unchanged), preserves the prograde sense `launchPlaneNormal` already selected (via the
`sign(v_perp·that)` term), and keeps `r1` UNTOUCHED (no departure-seam shift). Note: because `r1` is
fixed, the resulting plane normal is `n_ach`, not exactly `nTarget` — but the gate guarantees
`n_ach` is within `tol` of `nTarget`, so the rendered inc lands within `tol` of `targetInc`.

#### 2.2.3 Post-correction re-validation (fail-closed)

Rebuild the conic from `(r1, v1')`, then re-run, IN ORDER:
1. `IsSaneTransferConic(ecc, sma)` — on fail, decline.
2. `IsRetrogradeTransfer(transfer.inclination)` and the direction-match check
   (`resultRetrograde == !prograde`) — on a handedness flip, decline. **(NEW per review — the
   transverse rotation's `sign(v_perp·that)` could in principle flip handedness; re-running the
   direction guard on the corrected conic closes that door.)**
3. `IsExcessiveTiltTransfer(transfer.inclination, bound)` — if STILL excessive, decline.

Degenerate `nTarget`/`r̂`/`v_perp` → decline. Decline means `return false` → the caller steps to the
next window or falls back to faithful replay (never renders the corrected-but-still-bad conic).

The downstream `PatchedConics.CalculatePatch` / proximity SOI check (unchanged) re-validates the
corrected arc and declines if it no longer threads the target SOI — composing with the PR #1189
arc-clip fix.

There is NO second Lambert solve and NO transfer-angle band threshold (the gate is the inclination
bound + achievability check, not a transfer-angle band).

---

## 3. Exact file / function edits

All edits are in **`Source/Parsek/Reaim/ReaimTransferSynthesizer.cs`** plus tests. The solver, the
seam, the resolver, the converter and the assembler are NOT modified.

### 3.1 `ReaimTransferSynthesizer.cs` — new pure helpers (siblings of `IsSaneTransferConic` etc.)

| add | what | why |
|-----|------|-----|
| `internal const double InclinationToleranceDegrees = 0.5;` | tolerance constant next to existing constants | single tuning point, named, testable |
| `internal static double InclinationBoundDegrees(double launchInc, double targetInc)` | `max(max(launchInc,targetInc),0)+tol`, NaN-safe | the target-derived bound; pure |
| `internal static bool IsExcessiveTiltTransfer(double inc, double bound)` | `!NaN && inc<=90 && inc>bound` | the spurious-vs-real discriminator; pure |
| `internal static Vector3d ComputeIntendedPlaneNormal(Vector3d r2, Vector3d v2Target)` | `normalize(r2 × v2Target)`; `Vector3d.zero` sentinel on degenerate input | the well-conditioned target-plane normal; pure |
| `internal static double AchievablePlaneInclinationDegrees(Vector3d r1, Vector3d nIntended)` | `n_ach = normalize(nIntended − (r̂·nIntended)r̂)`; inc of `n_ach`; `NaN` on degenerate | the achievability metric (the gate input); pure |
| `internal static bool ConstrainTransferPlaneIsSafe(Vector3d r1, Vector3d nIntended, double targetInc, double tol)` | `n_ach non-degenerate && |incAch − targetInc| <= tol` | the achievability GATE; pure |
| `internal static bool ConstrainTransferPlane(Vector3d r1, Vector3d v1, Vector3d nIntended, out Vector3d v1Out)` | the transverse-only rotation of `v1`; returns `false` (and `v1Out = v1`) on degenerate input | the correction body; pure `Vector3d` math |

These mirror the existing `internal static` pure-helper pattern, so they get direct headless xUnit
coverage.

### 3.2 `ReaimTransferSynthesizer.cs` — wire the correction into `TrySynthesizeTransfer`

Insert the correction step BETWEEN the `IsRetrogradeTransfer` direction guard (`:212-218`) and the
`transfer.StartUT = departureUT` / `CalculatePatch` block (`:223-227`). Sequence:

1. (unchanged) solve verbatim with raw `r2` + `launchPlaneNormal` → `UpdateFromStateVectors` →
   `IsSaneTransferConic` → `IsRetrogradeTransfer`.
2. **(new)** `bound = InclinationBoundDegrees(launchBody.orbit.inclination, targetBody.orbit.inclination)`.
3. **(new)** `if (IsExcessiveTiltTransfer(transfer.inclination, bound))`:
   - `nTarget = ComputeIntendedPlaneNormal(r2, targetBody.orbit.getOrbitalVelocityAtUT(arrivalUT).xzy)`;
     if `nTarget` is the zero sentinel → `failReason = "tilt correction: degenerate target plane"`;
     `return false`.
   - **achievability gate:** `if (!ConstrainTransferPlaneIsSafe(r1, nTarget, targetBody.orbit.inclination,
     InclinationToleranceDegrees))` → `failReason = "tilt correction: unreachable target plane (incAch=…
     targetInc=…)"`; `return false` (decline to faithful — this is the Moho-adverse-phase exit; for
     Duna it never trips).
   - `if (!ConstrainTransferPlane(r1, v1, nTarget, out var v1c))` → `failReason = "tilt correction:
     degenerate rotation"`; `return false`.
   - rebuild `transfer.UpdateFromStateVectors(r1.xzy, v1c.xzy, parent, departureUT)`.
   - re-run `IsSaneTransferConic`; on fail set a reason; `return false`.
   - **(NEW per review)** re-run `IsRetrogradeTransfer` + direction-match; on a handedness flip set
     `failReason = "tilt correction: handedness flip inc=…"`; `return false`.
   - re-run `IsExcessiveTiltTransfer(transfer.inclination, bound)`; if STILL excessive set
     `failReason = "tilt correction: residual tilt inc=… > bound=…"`; `return false`.
4. (unchanged) `CalculatePatch` proximity / SOI encounter validation runs on the corrected conic.

Why insert AFTER the direction guard: the correction must only ever run on an already-prograde,
already-sane conic, so `IsExcessiveTiltTransfer` never collides with the retrograde case.

**Parking-override note (per review):** on the `hasDepartureOverride` path `r1` is the park-end state,
not the launch-body center. `nTarget`/`r2` are UNCHANGED (the override swaps only `r1`/`launchPlaneNormal`),
so the correction math is identical, and the achievability gate self-protects: a park-end `r1` that
is far from the target plane simply declines that window to faithful. `r1` is never moved, so the
departure seam (position) is preserved; the gate prevents an over-rotation of the velocity direction.

### 3.3 `ReaimTransferSynthesizer.cs` — logging (mandatory per logging requirements)

- Extend the existing `synth geometry` line (`LogSynthGeometry`) to append `inc=` and `bound=`.
- Add a one-shot `Verbose` line, subsystem `ReaimSeam`, on the correction decision:
  `tilt-correction inc-before=… bound=… targetInc=… incAch=… inc-after=…
  state=fired|noop|declined reason=…`.
  `noop` when `IsExcessiveTiltTransfer` is false; `fired` after a successful re-pin + all re-checks;
  `declined` on any fail-closed return (with the specific reason: degenerate-target / unreachable-plane
  / degenerate-rotation / sane-fail / handedness-flip / residual-tilt). Grep `tilt-correction`.

### 3.4 What is explicitly NOT touched

- `ITransferSolver.cs`, `UvLambert.cs` — the seam and solver stay byte-identical (no swap; see §7).
- `ReaimPlaybackResolver.cs`, `TransferWindowMath.cs` — tof centering (limitation B) is orthogonal,
  OUT OF SCOPE.
- `ReaimOrbitSegmentConverter.cs`, `ReaimSegmentAssembler.cs` — pure plane pass-throughs.
- `ProjectOntoPlane` (`:58-64`) — left retained-but-uncalled; the recommended fix does NOT re-wire a
  pre-solve projection (the forbidden path; §4).

---

## 4. Why it does NOT reintroduce the decline, and does NOT over-flatten Moho

### 4.1 No decline regression

The decline (the symptom `0dd6bd3a6` fixed) was a SOLVER-INPUT phenomenon: the old unconditional
`r2 = ProjectOntoPlane(r2, launchPlaneNormal)` flattened `r2` toward antiparallel-to-`r1`, collapsing
`sin(transfer-angle)` onto the `MinSinTransferAngle` cliff (`UvLambert.cs:125`), so `UvLambert` bailed
/ returned retrograde (`inc=180`) and the window declined.

This fix touches NONE of that:

- It NEVER touches `r1`, `r2`, the transfer angle, the handedness axis, or the solve. The first solve
  runs on RAW `r2` + `launchPlaneNormal` exactly as `0dd6bd3a6` ships it, so `sin(dnu)` stays lifted
  off the cliff and the window RESOLVES prograde. The decline fix lives entirely UPSTREAM of the new
  post-solve step, structurally preserved.
- The correction is a post-solve rigid-transverse rotation on the OUTPUT `v1` of an already-converged,
  already-prograde conic. There is NO recomputation of `sin(transfer-angle)` and NO second Lambert
  solve, so it cannot re-collapse it or re-pick the retrograde branch.
- A NEW fail-closed decline path exists (degenerate / unreachable-plane / residual-over-bound /
  handedness-flip → faithful). That is the CORRECT posture, NOT the handedness decline. The §5.2
  "FIRED-not-DECLINED" assertions guard that the three known Duna windows CORRECT rather than silently
  drop to faithful (so "fixed by declining" can't masquerade as "fixed by correcting").

The existing in-game decline-regression guard (`requireWindow0Resolve:true` on `ObservedEdgeDepartureUT`)
keeps passing because the window resolves prograde BEFORE the correction runs.

**EXACT-180 / sin(dnu)-just-above-cliff (per review):** at the precise antiparallel window
`UvLambert.Solve` may itself return false at `:125` BEFORE the correction can run — the window declines
at the solver, exactly as today. The correction is post-solve, so it neither fixes nor worsens this
case; it only ever sees windows that already resolved.

### 4.2 No Moho over-flatten (the blocker, resolved)

The naive `ConstrainTransferPlane` (v1-transverse rotation alone) over-flattens an inclined target at
adverse r1 phase — verified: at r1 node-perpendicular it produces inc = 0.0° (§1.4). The
**achievability gate (§2.2.1)** is the fix: the correction applies ONLY when the best plane through the
fixed `r1` lands within `tol` of `nTarget`. Two independent protections result:

1. **The bound is target-derived,** so for a window where the solved inc already equals the target
   inc (≤ bound) the correction never fires (no-op pass-through).
2. **When it fires, the achievability gate refuses to apply at any phase that would flatten the target.**
   For Duna (`nTarget ≈ ecliptic`) the gate always passes and the correction collapses the spurious
   tilt to ~0.06°. For Moho the gate passes only when `r1` is near Moho's node (where the achievable
   plane IS Moho's 7° plane) and DECLINES otherwise. Worst case for an inclined target is a faithful
   replay, NEVER a flattened conic.

This is precisely why the achievable-plane gate is mandatory and why the naive "snap to `nTarget`"
claim from the pre-review draft was unsound. The corrected claim is: **the correction either lands the
plane within `tol` of the target's REAL plane, or it declines** — it never produces an in-between
flattened plane.

---

## 5. Test plan

Three layers, all with existing homes/fixtures. No new synthetic-recording authoring required.

### 5.1 Headless xUnit (no Unity scene) — `Source/Parsek.Tests/ReaimTransferSynthesizerTests.cs` (+ `UvLambertTests.cs` for the reproduction)

The tilt is reproducible in pure `Vector3d` math; `UvLambertTests` already runs
`Vector3d.Cross/Dot/PropagateTwoBody` headless (`:184-189`). New test method names (so the implementer
cannot drift):

1. **`Solve_NearAntiparallel_ProducesAmplifiedPlaneTilt`** (in `UvLambertTests.cs`): craft `r1` along
   +x, `r2` near-antiparallel (θ≈179°) with Duna's real z-offset (21.7 Mm at 20.7 Gm); `Solve` with the
   launch-plane normal; compute `inc = acos(|h.z|/|h|)` from `h = Cross(r1, v1)`; assert the RAW solve
   produces a multi-degree tilt (≈3.4°, proves the bug without KSP).
2. **`ConstrainTransferPlane_PinsPlaneAndPreservesSpeedHandedness`** (in `ReaimTransferSynthesizerTests.cs`):
   given `r1` IN the `nIntended` plane (r̂ ⟂ nIntended-node so the constraint is exact) and a `v1`
   whose `r1 × v1` tilts ~3° off `nIntended`, assert: `r1 × v1'` parallel to `nIntended` (within 1e-6°),
   `|v1'| == |v1|`, `v1'·r̂ == v1·r̂` (radial preserved), AND `sign((r1 × v1')·launchPlaneNormal) ==
   sign((r1 × v1)·launchPlaneNormal)` (**handedness preserved — NEW per review**). Degenerate
   `nIntended` → `false` + `v1` unchanged.
3. **`ConstrainTransferPlane_OffPlaneR1_RespectsAchievableBound`** (**NEW per review — exercises the
   over-determination explicitly**): vary r1 phase relative to `nIntended`'s node at 0/45/90/135°;
   assert the result inc equals `AchievablePlaneInclinationDegrees(r1, nIntended)` (NOT exact equality
   to `nIntended`), pinning that the result is `n_ach` and collapses to 0° at phi=90°. This is the test
   the naive draft's #2 would have hidden.
4. **`ConstrainTransferPlaneIsSafe_GatesDunaApplyMohoAdverseDecline`** (the load-bearing gate test):
   Duna `nTarget`≈ecliptic at phi 0/45/90/135/179 → ALL safe (apply); Moho `nTarget` inc 7° at phi 0
   and 179 → safe (apply, achievable≈7°), at phi 30/45/60/90/135 → UNsafe (decline). Mirrors the §1.4
   achievability table.
5. **`ComputeIntendedPlaneNormal_KnownGeometryAndDegenerate`**: known-inclination `r2`/`v2Target` →
   expected normal direction; zero/NaN `v2Target` → zero sentinel (covers both the zero-length AND the
   NaN-velocity degenerate path — per review).
6. **`IsExcessiveTiltTransfer_Theory`**: `(2.3573, 0.56)→true`, `(5.0573, 0.56)→true`,
   `(0.1312, 0.56)→false`, `(7.0, 7.5)→false`, `(9.0, 7.5)→true`, `(95, …)→false`, `(NaN, …)→false`.
   Comment + assertion: Moho's real 7.0° exceeds Duna's worst 5.06° spurious tilt, so only the
   target-derived bound separates them.
7. **`InclinationBoundDegrees_Theory`**: Kerbin~0/Duna~0.06→~0.56; Kerbin~0/Moho~7.0→~7.5;
   Kerbin~0/Eve~2.1→~2.6; Kerbin~0/Eeloo~6.15→~6.65; NaN handled.

Body inclinations are hardcoded constants in headless tests (`FlightGlobals` is absent).

### 5.2 In-game canaries — `Source/Parsek/InGameTests/ReaimEndToEndInGameTest.cs`

`AssertSaneWindowSegments` (`:1228`) reads the RENDERED quantity `segs[1].inclination` (the
`OrbitSegment`, populated from `orbit.inclination` via `ReaimOrbitSegmentConverter.cs:21`), and
`ctx.LaunchBody.orbit.inclination` / `ctx.TargetBody.orbit.inclination` are already reachable
(`:274`, `:471`). These pin the RIGHT quantity (rendered inc), not the tilt-blind endpoint-proximity
diagnostic.

1. **Duna tilt UPPER bound** — inside `AssertSaneWindowSegments`, after the existing
   `IsRetrogradeTransfer` assertion (`:1248-1250`), add (inside the resolve branch, so a window must
   both resolve AND be in-plane):
   `inc(segs[1]) <= max(ctx.LaunchBody.orbit.inclination, ctx.TargetBody.orbit.inclination) + tol`.
   Exercised by the existing Duna drivers; the loop1/loop2 consecutive synodic windows (today
   2.36°→5.06°) must both fall under the ~0.56° Duna bound.
2. **DECLINE-regression guard** — KEEP the existing `DriveWindowsResolveOrDeclineCleanly(ctx,
   ObservedEdgeDepartureUT, …, requireWindow0Resolve:true, …)` (`:403-404`). The pinned near-180
   window must still RESOLVE while now also passing the tilt bound.
3. **Duna FIRED-not-DECLINED** (**NEW per review**) — add an assertion (or test-side log grep on
   `tilt-correction state=fired`) that the Duna 2.36°/5.06° windows are corrected by FIRING, not by
   declining to faithful. Without it, a window could satisfy "inc ≤ bound" by declining (rendering the
   faithful replay, masking a silent regression). Implementation: surface a resolved-via-correction
   flag (e.g. a `TransferCorrectionState` out-param on the synth or a counter the in-game test reads),
   asserted non-zero for the known tilting windows.
4. **Moho — gate-no-op proof, NOT a fire-path proof** (**re-scoped per review**) — in the
   `KerbinToMoho()` driver path, the assertion's purpose is "the correction never WRONGLY over-flattens
   a legitimately-inclined target". Assert `inc(segs[1]) >= ctx.TargetBody.orbit.inclination - tol` on
   every RESOLVED Moho window (so a resolved Moho window keeps its real ~7°). Combined with the shared
   upper bound this brackets resolved Moho to ~[6.5°, 7.5°]. Crucially, the Moho fire-path correctness
   (lands on `n_ach`, not exact `nTarget`) is proven by the HEADLESS test #3/#4, NOT by an in-game
   exact-value assertion — because at adverse phase Moho DECLINES (no segments), so an in-game
   "[6.5,7.5]" bracket only ever sees the windows that resolved (favorable phase or no-op). Update the
   stale comment at `:1246-1247` ("the inc is a few degrees") — the near-180 Moho TRANSFER-plane inc is
   13–82° pre-correction; post-correction a RESOLVED window is ~7° or it declined.
5. **Eeloo coverage** (**NEW per review**) — `Reaim_KerbinToEeloo_StructuralContractAndWindow0`
   (`:446`) runs through the SAME `AssertSaneWindowSegments`, so the shared upper bound (max(launchInc,
   Eeloo~6.15)+tol ≈ 6.65°) applies. Eeloo is inclined + eccentric (the worst achievability case):
   confirm in the GATE that resolved Eeloo windows keep `inc ≥ Eeloo_inc − tol` and that adverse-phase
   Eeloo windows DECLINE cleanly rather than over-flatten. Document the expectation (no-op or
   fire-on-favorable / decline-on-adverse) so the Eeloo canary is not silently regressed.

### 5.3 What MUST stay green / untouched

- `UvLambertTests.cs` — Curtis 5.2 velocities, RK4 round-trips, off-phase fail-closed sweep, and
  `Solve_AntipodalNear180_PlaneNormalSelectsPrograde_LegacyCrossZFlips`. The solver body is unchanged
  → byte-identical.
- `TransferSolverInterfaceTests.cs` — seam delegation byte-identity + the default-type assert. No seam
  change.
- `ReaimTofSearchTests.cs` — limitation-B band-law invariants; the fix does not touch tof centering.
- The 5 existing `ProjectOntoPlane` tests — the helper is left in place; do NOT delete it.

### 5.4 Fixtures

None new. The existing `ReaimFixture` (Duna/Moho/Eeloo) + the pinned `ObservedEdgeDepartureUT`
near-180 harness + inline-vector headless tests cover all three layers. The headless tilt test authors
its own `r1`/`r2` inline (like `Solve_AntipodalNear180`), seeded with the §1.4 constants.

---

## 6. In-game validation

After deploy (`cd Parsek-reaim-plane-tilt/Source/Parsek && dotnet build`; hash-verify the deployed
`GameData/Parsek/Plugins/Parsek.dll` per CLAUDE.md before launch):

1. **Pinned near-180 window** — run the in-game test runner (Ctrl+Shift+T) over the Reaim category;
   confirm `Reaim_…_ObservedEdgeDeparture…` RESOLVES (decline-regression intact) AND the new tilt
   upper-bound assertion passes; confirm resolved `Reaim_KerbinToMoho_…` windows keep inc ~7°; confirm
   `Reaim_KerbinToEeloo_…` is not regressed.
2. **Duna One loop** — re-run the Duna One re-aim loop from the captured break
   (`logs/2026-06-23_2129` / `_2259` geometry) across ≥3 consecutive synodic windows. Grep:
   - `grep "update-segment .* body=Sun .* inc="` → every Sun-leg inc ≤ ~0.56° (was 2.36 / 5.06 / 0.13).
   - `grep "tilt-correction"` → `state=fired` on the previously-tilted windows with `inc-after ≤ bound`,
     `state=noop` on the already-in-plane 0.13° window, NO `state=declined` for the real recorded Duna
     windows (Duna's gate always passes).
   - confirm NO `transfer direction mismatch` / faithful-decline for the resolving Duna windows (the
     decline tell — if it appears, STOP and revert; §8).
   - visually: the heliocentric arc lies in the ecliptic at Duna-orbit map scale (no "upwards" tilt).
3. **Parking-path spot check** — re-run a `hasDepartureOverride` (two-burn departure) window; confirm
   no launch-seam shift (expected, `r1` is never moved) AND the corrected inc is still sane (the gate
   self-protects an off-plane park-end `r1`).

---

## 7. License posture

Parsek stays MIT. **No third-party code is pulled in.** The fix reuses only Parsek's own MIT
`UvLambert` (unchanged), the existing MIT `launchPlaneNormal` handedness (unchanged), and new original
pure `Vector3d` math (a transverse-component rotation + an achievability metric — Cross/Dot/normalize).
No hand-rolled new solver.

The one permissive vendored option — MechJebLib `Lambert/Gooding.cs` and `Izzo.cs`, per-file SPDX
header (`LicenseRef-PD-hp OR Unlicense OR CC0-1.0 OR 0BSD OR MIT-0 OR MIT OR LGPL-2.1+`) verified
present on the MechJeb2 `dev` HEAD — is **assessed and REJECTED on technical merit, not license**: any
single-rev Lambert returns `v1 ∈ span(r1,r2)` and takes the same external handedness normal, so
Gooding/Izzo would recompute the IDENTICAL `plane(r1,r2)` tilt from the same endpoints. A solver swap
cannot fix a plane-of-endpoints artifact. (If ever vendored: verify-at-exact-copied-commit applies;
Izzo carries a poliastro-MIT attribution NOTICE obligation.)

GPL/AGPL solvers (MechJeb2-the-repo, pykep, lamberthub, Vallado/CelesTrak) remain DISQUALIFIED for
in-repo vendoring. `lamberthub` (GPL) is usable ONLY as an out-of-repo numeric ORACLE to cross-check
the new near-180 plane-inclination test values (output velocities are uncopyrightable facts; its
harness/fixtures must not be copied in). Fail-closed posture preserved end to end.

---

## 8. Staged, stop-on-regression rollout

**Stage 0 — instrument (no behavior change).** Add the `tilt-correction` Verbose log + the
`inc=`/`bound=` additions to `synth geometry`, and the headless tilt-reproduction xUnit (#1). Deploy,
re-run the Duna One break, confirm the log shows `inc-before=2.36/5.06` at the failing windows.

**Stage 1 — the fix (minimum viable increment).** Add the constant + seven pure helpers (incl. the
achievability gate), wire the post-solve correction into `TrySynthesizeTransfer` on the DIRECT path,
and land:
- headless: #2–#7 (`ConstrainTransferPlane` speed/handedness-preserving + achievable-bound,
  `ConstrainTransferPlaneIsSafe` gate, `ComputeIntendedPlaneNormal`, `IsExcessiveTiltTransfer` theory,
  `InclinationBoundDegrees` theory).
- in-game: the Duna upper-bound assertion in `AssertSaneWindowSegments` + the FIRED-not-DECLINED
  assertion + KEEP the existing `requireWindow0Resolve:true` decline-regression guard.

  **GATE (all must hold):** Duna resolves AND inc ≤ bound across ≥2 consecutive synodic windows AND the
  tilting windows report `state=fired` (not `declined`); `ObservedEdgeDepartureUT` still resolves;
  `UvLambertTests` / `TransferSolverInterfaceTests` / `ReaimTofSearchTests` green.

  **STOP-ON-REGRESSION:** if any Duna window now DECLINES instead of resolving, that is the decline
  tell — REVERT to baseline and reassess; do NOT stack fixes on a broken state.

This Stage-1 direct-path increment fully fixes the reported Duna One bug. The achievability gate makes
the Moho/Eeloo paths safe-by-construction (decline rather than flatten), so the inclined-target
behavior is not a separable correctness risk — but its IN-GAME confirmation is Stage 2.

**Stage 2 — inclined-target + parking confirmation.** Add the Moho gate-no-op / lower-bound assertion,
the Eeloo coverage assertion, and the parking-override spot check. Confirm no `hasDepartureOverride`
window regresses and resolved Moho/Eeloo windows keep their real inclination (or decline cleanly at
adverse phase). **GATE:** resolved inclined-target windows keep `inc ≥ targetInc − tol`; no adverse
window over-flattens (it declines instead, visible as `tilt-correction state=declined
reason=unreachable-plane`).

**Fallback (named, behind the identical guard spine):** if the `v1`-transverse rotation's
achievable-vs-target residual is ever VISIBLE in-game on the Duna path (not expected — Duna achievable
≈ target at all phases), swap ONLY the correction BODY for a bounded single re-solve: clamp `r2`'s
out-of-plane excess down to the bound (sign-preserving, NOT zeroed) and re-Solve ONCE with the SAME
`launchPlaneNormal`. **HARD PRECONDITION (per review):** the clamped `r2'` must keep
`|sin(angle(r1,r2'))| ≥ MinSinTransferAngle`, and the re-solve MUST be gated by a post-re-solve
`IsRetrogradeTransfer` + direction-match check — a clamp that pushes `r2'` toward antiparallel-to-`r1`
would re-collapse `sin(dnu)` onto the very cliff `0dd6bd3a6` fixed and could silently return the
retrograde branch. If the re-solve does not resolve prograde, keep the rotation-form result or decline.
This fallback re-enters the decline conditioning, so it carries the higher risk and needs its OWN
decline-regression assertion before promotion from escalation to shipped; it is NOT the first move.

---

## 9. Open questions to resolve before / while coding

1. **`nTarget` frame + sign.** Confirm `r2 × v2Target` un-swizzles into the SAME `.xzy` Lambert frame
   as `r1`/`r2`/`launchPlaneNormal` and is oriented consistently. Verify with the headless gate +
   `ConstrainTransferPlane` tests first, then an in-game inc probe on a known window. (A frame/sign
   error silently cancels the correction.)
2. **Tolerance value (0.5°).** Must exceed any `inc(nTarget)`-vs-`orbit.inclination` numerical / RAAN
   jitter but stay below the smallest spurious tilt caught. Confirm post-correction Duna lands ≤ 0.56°,
   resolved Moho stays in [6.5°, 7.5°], and the 0.13° Duna window is a no-op.
3. **Achievable-vs-target residual on Duna.** Duna's achievable inc is within ~0.06° of target at all
   phases (§1.4), so the rendered inc lands ≤ `targetInc + (achievable residual)` ≈ 0.06°, well under
   bound. Confirm in-game that the `CalculatePatch` proximity/SOI still passes on every Duna window that
   resolves today (no silent drop to faithful after correction). If any does, switch to the §8 re-solve
   fallback (threads the corrected endpoint exactly).
4. **RAAN snap.** `n_ach` carries the target's RAAN as well as inclination; immaterial for Duna (planes
   within ~0.06°). For a resolved Moho window confirm in-game that the RAAN component does not introduce
   a visible SOI-handoff kink. If it does, consider a blended normal within the bound — but default to
   `n_ach` unless the Moho measurement disagrees. (Listed as a known coverage gap — no automated test,
   in-game eyeball only — so it is not forgotten if inclined-target re-aim is later promoted.)
5. **Retrograde-recorded transfers.** `IsExcessiveTiltTransfer`'s `inc <= 90` clause means a
   recorded-RETROGRADE mission (progradeWanted=false, transfer inc near 180°) is NEVER tilt-corrected —
   its spurious near-antiparallel tilt (mirrored around 180°) is unbounded. This is acceptable for v1
   (re-aim retrograde interplanetary missions are not a shipped scenario), but DOCUMENT it as a known
   limitation: if retrograde re-aim is ever enabled, the guard + correction must be mirrored
   (`inc >= 90` branch, target plane = retrograde target normal). No fixture today.
6. **Per-window vs window-invariant `nTarget`.** The target plane normal (h-direction) is plane-invariant
   for a Kepler orbit, so evaluating `nTarget` per-window at `arrivalUT` is correct even for an
   eccentric target (Eeloo/Moho) and introduces no per-window jitter.

---

## 10. Reconciliation with the prior plan (`reaim-lambert-reliability`)

The committed plan `reaim-near-180-lambert-reliability.md` on branch `reaim-lambert-reliability` is
PRIOR ART, not a resume target. It was written for the PRE-`0dd6bd3a6` code where the projection was
present and the symptom was the DECLINE. Re-mapping:

- **Stale framing.** Its §1.1–1.2 describe the decline. `0dd6bd3a6` deleted the projection and shipped
  the prior plan's own rejected "plane-normal hint" through the seam; the decline is fixed, the live
  symptom is the TILT.
- **Option 1 (flipped-bit re-solve)** — DROP as a tilt lever (both branches lie in `plane(r1,r2)`, so
  flipping handedness cannot change the plane).
- **Option 2 (conditional projection)** — RE-MAPPED to "a conditional, achievability-gated in-plane
  correction layered ON TOP OF the launchPlaneNormal handedness", realized as the post-solve gated
  re-pin — NOT an unconditional pre-solve `r2` flatten (forbidden: re-introduces the decline + caps
  inclined targets). The §8 re-solve fallback is the closest pre-solve-style realization, gated.
- **Option 3 / limitation B (geometry-aware tof)** — STILL VALID but ALREADY SHIPPED
  (`ReaimPlaybackResolver` + `TransferWindowMath`). Orthogonal to the tilt. OUT OF SCOPE.
- **License section** — CARRIED FORWARD (§7).
- **Gooding contingency** — re-mapped to NOT-a-tilt-lever; assessed-and-rejected-on-merit.
- **Open questions** — Q1/Q2 (guard-band threshold) re-cast as this plan's target-derived
  discrimination + achievability gate (the genuinely NEW work). Q5 (synthetic Moho fixture) satisfied
  by the §5.2 Moho gate assertion + §5.1 headless gate test.

The NEW work with no prior-plan antecedent: the spurious-vs-real DISCRIMINATION RULE
(`IsExcessiveTiltTransfer` against a target-derived bound), the post-solve plane re-pin onto the
target's own orbital plane, AND the **achievability gate** that makes the re-pin sound for
genuinely-inclined targets.

---

## 11. Review resolutions

Three adversarial reviews ran against the pre-revision draft. What each changed:

### 11.1 Blocker: `ConstrainTransferPlane` over-determination (over-flatten reviewer — "flawed")

**Claim:** holding `r1` fixed AND pinning the plane to `nTarget` is over-determined; at adverse r1
phase the v1-transverse rotation collapses to inc = 0° (the deleted projection's low-inclination cap),
so the draft's "snaps exactly to `nTarget`, Moho lands on 7°" was false.

**Verified TRUE** (planning computation, §1.4 achievability table + the phi=90° → 0.0° result). **This
was the genuine blocker.** Resolution: added the **achievability gate** (§2.2.1, `ConstrainTransferPlaneIsSafe`).
The correction now applies ONLY when the best plane through the fixed `r1` lands within `tol` of
`nTarget`; otherwise it DECLINES to faithful. This NEVER over-flattens an inclined target — worst case
is a faithful replay. The draft's universal "snaps to `nTarget`" claim is replaced by the honest "lands
within `tol` of the target plane, or declines."

**Claim:** the bound `max(launchInc,targetInc)+tol` is physically wrong because the near-180
transfer-PLANE inc (13–82° for Moho) is governed by transfer angle, not orbital inclination, so the
bound cannot separate spurious from real.

**Partially TRUE, re-framed.** Confirmed the clean transfer-plane inc DOES exceed the bound near 180°
for every target (§1.4 table). But this is not a flaw in the bound — it is the bug the guard is meant to
catch. The bound is the inclination a CORRECT re-aimed transfer SHOULD carry (the target's own plane),
NOT an envelope for the buggy `plane(r1,r2)`. `IsExcessiveTiltTransfer` correctly FIRES on near-180
windows for Duna AND Moho; the achievability gate then decides apply-vs-decline. §1.3, §2.1, and the
stale `:1246-1247` Moho comment are corrected. The reviewer's specific number (82° at theta=179) is for
the worst z-phase and matches the §1.4 table; the draft's "Moho real transfer inc ~7°" was wrong and is
removed.

### 11.2 reintroduces-decline reviewer ("needs-changes")

- **Required: invariant that the correction never turns a resolving-prograde-and-encountering window
  into a faithful decline silently.** Added §5.2 #3 (Duna FIRED-not-DECLINED assertion) + the
  `state=fired|declined` log facet, so "fixed by declining" cannot pass the suite.
- **Required: post-correction `IsRetrogradeTransfer` re-check.** Added to §2.2.3 / §3.2 step 3 (the
  re-validation now runs `IsSaneTransferConic` → `IsRetrogradeTransfer` + direction-match →
  `IsExcessiveTiltTransfer`).
- **Required: §8 fallback hard guard against the clamp re-hitting `MinSinTransferAngle`.** Added the
  explicit `|sin(angle(r1,r2'))| ≥ MinSinTransferAngle` precondition + post-re-solve direction guard to
  §8, plus its own decline-regression assertion requirement before promotion.
- **Missed edge: EXACT-180 solver-bail unchanged.** Documented in §4.1.
- **Test gap: handedness-preserved headless assertion.** Added to §5.1 #2.

### 11.3 impl-test-completeness reviewer ("needs-changes")

- **`ConstrainTransferPlane` math gap (`h ∝ n_ach`, not `nTarget`).** Resolved by the achievability gate
  + the corrected §2.2.2 wording (rendered plane is `n_ach`, gated within `tol` of `nTarget`) + the new
  headless test #3 that pins the result to `n_ach` (not exact `nTarget`).
- **Required: re-scope the Moho in-game assertion to a gate-no-op proof.** Done (§5.2 #4): the in-game
  Moho assertion proves "never wrongly over-flatten a resolved inclined target"; the fire-path
  correctness is proven HEADLESS (#3/#4) because adverse-phase Moho declines (no segments to assert on).
- **Required: name the exact test methods + files.** Done (§5.1 lists 7 named methods across
  `ReaimTransferSynthesizerTests.cs` + `UvLambertTests.cs`; §5.2 names the in-game insertion points).
- **Required: confirm the in-game assertion reads `segs[1].inclination` (rendered) not the proximity
  diagnostic.** Confirmed (§5.2 preamble; `OrbitSegment.inclination = orbit.inclination`).
- **Missed edge: Eeloo coverage through the shared `AssertSaneWindowSegments`.** Added §5.2 #5 (Eeloo
  inclined+eccentric, expected no-op / fire-on-favorable / decline-on-adverse).
- **Missed edge: degenerate NaN `v2Target` path.** Added to §5.1 #5.
- **Missed edge: parking-override path `r1` further from target plane.** Documented in §3.2 (gate
  self-protects) + §6 #3.

### 11.4 Items dismissed / deferred (with reason)

- **Rigid full-conic rotation (lands on `nTarget` exactly at all phases).** Evaluated during planning;
  REJECTED because it shifts `r1` by up to ~20 Gm at adverse phase (catastrophic departure-seam shift).
  The achievability-gated v1-transverse rotation keeps `r1` fixed and declines rather than shift.
- **Retrograde-recorded tilt (inc ≤ 90 clause excludes it).** Acknowledged as a known v1 limitation
  (§9.5), not a blocker — re-aim of retrograde interplanetary missions is not a shipped scenario; no
  fixture today. Documented so it is not silently forgotten.
- **Moho RAAN-kink.** Deferred to in-game eyeball (§9.4) — no automated coverage, flagged as a known
  gap. Acceptable because Duna (the shipped fix) is RAAN-immaterial and Moho is Stage 2.

### 11.5 Net effect on the recommendation

The recommended approach is UNCHANGED in spirit (post-solve, target-derived, excess-only plane
correction holding `r1` fixed) but is now SOUND for inclined targets via the added achievability gate —
the one real blocker. The Duna fix (the reported bug) is unaffected by the gate (it always passes for
Duna) and ships as Stage 1. No reviewer blocker remains that requires a human decision before coding:
the over-determination blocker is resolved in-plan by the gate, which is fail-closed (decline, never
flatten).
