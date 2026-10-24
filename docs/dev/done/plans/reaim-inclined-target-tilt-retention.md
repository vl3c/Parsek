# Re-aim inclined-target windows must resolve: retire the tilt gate's decline-to-faithful disposition

Branch `reaim-inclined-targets` (stacked on `eve-loop-lanes` / PR #1444, whose V8 lanes and
`eve-orbit-recorded` fixture are this plan's validation mechanism). Plan authored 2026-08-11 by a
clean-context planning pass over the measured V8 defect; load-bearing arithmetic INDEPENDENTLY
REPRODUCED by the supervising session (appendix A) before any implementation.

## 1. Problem statement and code-truth analysis

### What the tilt machinery actually computes

`ReaimTransferSynthesizer.TrySynthesizeTransfer` (`Source/Parsek/Reaim/ReaimTransferSynthesizer.cs:399-492`)
runs, after the sane/direction guards, on every solved conic:

1. `IsExcessiveTiltTransfer(inc, bound)` (:95-98) with `bound = max(launchInc, targetInc) + 0.5`
   (:81-86). For Kerbin->Eve: bound = 2.1 + 0.5 = **2.6 deg**.
2. If it fires, `ConstrainTransferPlaneIsSafe(r1, nTarget, targetInc, 0.5)` (:159-167), where
   `nTarget = normalize(r2 x v2Target)` (:108-117) is **Eve's orbital-plane normal -
   plane-invariant for a Kepler orbit** - and `AchievablePlaneInclinationDegrees(r1, nTarget)`
   (:133-149) projects that normal orthogonal to r-hat-1 and measures the angle from world +Y.
3. On gate failure: `return false` (:427-437) - **the entire candidate dies**, and when all
   candidates die the resolver renders the FAITHFUL recorded transfer for that window
   (`ReaimPlaybackResolver.cs:478-486`).

**The gate never sees r2's position along the orbit.** `nTarget` is constant for all 27 tof
candidates (same plane regardless of where Eve is on it), and r1 is fixed at departureUT for all
candidates. Reproduced from stock ephemeris independently (Sun mu=1.1723328e18; Kerbin
a=13,599,840,256 / e=0 / i=0; Eve a=9,832,684,544 / e=0.01 / i=2.1 deg / LAN=15 deg;
D0=26,616,878.032):

- **incAch = 1.2358 - reproduced to four decimals**, and provably constant across candidates.
  `1.2358 < 2.1 - 0.5`, so all 27 decline identically. The V8 log's "incAch CONSTANT at 1.2358"
  is forced arithmetic, not a numerical curiosity.
- Transfer angle at cycle 0 is **~160-178 deg** across the tof band (Hohmann-class; geomTof
  3,679,663 s is the exact half-period of the Kerbin-Eve transfer ellipse, recorded tof within
  1.7% of it).
- **Eve's out-of-ecliptic displacement at arrival is ~271 Mm - 3.2x Eve's SOI (85.1 Mm).** r1
  (Kerbin at D0, helio longitude ~141 deg, 54 deg off Eve's node line) sits ~400 Mm out of Eve's
  orbital plane.
- **The flatten the correction would apply misses Eve by 271-290 Mm ~ 3.2-3.4 SOI radii.** Had
  the gate passed and the correction fired, the corrected conic would have missed Eve entirely
  and failed the downstream encounter check anyway.

### The decisive fixture fact

The committed `eve-orbit-recorded` recording
(`harness/fixtures/saves/eve-orbit-recorded/Parsek/Recordings/75a6ab25a0f445219a82b7b841e44ba8.prec.txt`)
is a **broken-plane transfer**: post-ejection Sun leg `inc = 0.0021` deg (flat,
11,929,842 -> 12,022,849, ecc 0.1552, sma 11,769,252,918 - exactly the `rendOrbit` in the V8
raise line), then a **mid-course plane change at UT ~14.16M onto inc 2.0627 deg - Eve's own
plane** - final approach 2.0689, Eve SOI seam at 15,673,183.37, capture ecc 1.326. The player
reached Eve by matching Eve's plane mid-flight. A single center-to-center conic from this r1 can
never do that: r1 is 400 Mm off Eve's plane, so no conic through r1 lies in Eve's plane, and no
conic through both r1 and r2 has inclination under ~10 deg at this transfer angle. **The 2.6-deg
bound structurally excludes every conic that can encounter Eve at this window.**
(`ReplaceHeliocentricLeg` at `ReaimSegmentAssembler.cs:387-405` already collapses the recorded
two-conic Sun chain into the one synth arc, so a single-conic replacement is mechanically fine.)

### Hypothesis verdicts

**H1 - near-180 amplification: CONFIRMED mechanism, WRONG framing.** `v1 = (r2 - f*r1)/g`
(`UvLambert.cs:218`) puts v1 in span(r1, r2), so the conic's plane IS plane(r1, r2) by
construction; near 180 deg the small out-of-plane offset is amplified into a large plane
inclination. But this is not ill-conditioning error to be corrected: **for Eve the tilt is
load-bearing** - the amplified plane is the unique plane containing both endpoints, and any
flatter plane misses the SOI by 3x. The solver converged on all 27 candidates (every one reached
the tilt gate with a finite inc-before), so there is no solver defect here.

**H2 - the gate asks the wrong question: CONFIRMED, in two layers.** (a) Arithmetically, the
gate's inputs (r1, nTarget) are both candidate-invariant - it cannot see r2, proven by the
reproduced constant incAch. (b) Conceptually, requiring the transfer plane to match the *target
orbit's* inclination is a property even the *recorded* transfer only achieved via a mid-course
plane change; the necessary plane is plane(r1, r2), which the solver already delivers. Important
nuance: **as a correction-safety gate, the gate answered correctly** - flattening at this
geometry would miss (271 Mm > 85 Mm). The defect is the *disposition*: "cannot safely correct"
is implemented as "kill the candidate," when the un-corrected conic in hand was already sane,
prograde, and passes through Eve's center at arrival by construction.

**H3 - relationship to the Duna 1.027/1.043 endpoint gap: ORTHOGONAL.** Those figures
(2026-06-15) measure SOI-handoff endpoint/orientation structure of the center-to-center splice -
~96% endpoint (center vs SOI edge), ~4% asymptote orientation
(`docs/dev/todo-and-known-bugs.md`, root-cause block of the 2026-06-15 entry). The plane
treatment does not enter either term, and V4's census already reads the current Duna arrival
inside the sphere. This branch neither fixes nor risks that residual; option 3 remains its only
cure and stays out of scope. Precondition (b) stays untouched and open.

**H4 - is the 0.5-deg machinery a patch over solver ill-conditioning? NO.** The solver is
correct; the machinery is a *product* choice: near-coplanar targets (Duna, 0.06 deg real inc)
render more faithfully flattened onto the target plane, and the correction + gate + tolerance
were calibrated for exactly that. No "better plane construction" exists to obviate it -
plane(r1,r2) is the only plane a single conic can use. The machinery stays for the populations
it was built for; only its fail branch's disposition changes.

### Why this is the defect that matters

The measured consequence of decline-to-faithful is the program's own worst class: the ENGAGED
unit silently renders the recorded transfer one synodic late and the seam-endpoint oracle
measures the arc missing the moved Eve at ratio=4.6216 - the 2026-06-15 "no continuous
encounter" symptom through a new route, with `forceFaithfulLoopPlayback` off and no user choice
involved. Looped missions are the substrate for `Logistics/RouteOrchestrator.ResolveLoopUnit`;
an Eve route on today's code cannot ever render its transfer arriving.

## 2. Candidate designs

### Design A (RECOMMENDED): keep the gate, change the fail disposition - retain the un-corrected conic

When `IsExcessiveTiltTransfer` fires but `ConstrainTransferPlaneIsSafe` says the target plane is
unreachable from r1, **skip the correction instead of failing the candidate**: log
`tilt-correction ... state=retained reason=unreachable-plane`, increment a new
`RetainedTiltCount` (keep incrementing `UnreachablePlaneDeclineCount` too - see guards), and
fall through to the existing `CalculatePatch` + proximity encounter validation with the conic
already in hand. The encounter check remains the arbiter; a retained conic that does not enter
the SOI still declines exactly as today.

- **What it fixes:** Eve cycle-0 resolves at candidate step 0 (the conic passes through Eve's
  center at arrival by construction, so the proximity check is trivially inside the SOI, and
  since usedTof = recorded tof, the rendered arc sits at Eve's center at the mapped recorded
  seam instant - the oracle will read ratio ~ 0). Generalizes to any inclined target at off-node
  departure geometry: Moho/Eeloo adverse-phase windows also stop falling to faithful *when the
  tilted conic genuinely encounters* - claimed as unmeasured collateral improvement, not as a
  validated feature (open question 3).
- **Why Duna cannot regress:** for Duna the gate passes at every r1 phase (achievable inc ~
  target inc ~ 0.06 at all phases), so the fired-correction path is byte-identical; the changed
  branch is unreachable for Duna, and the in-game NEVER-UNREACHABLE invariant
  (`ReaimEndToEndInGameTest.cs:~384-417 (the Duna NEVER-UNREACHABLE invariant; anchor updated post-review)`) keeps guarding the .z-vs-.y frame bug unchanged (any
  Duna unreachable hit, retained or declined, is still the tell).
- **Blast radius:** ~20 lines in one method + one pure decision helper + log vocabulary +
  counters. No resolver, assembler, converter, solver, or render-path change. Fail-closed
  posture preserved: retained conics still face direction/sane/encounter guards; all *other*
  decline branches (degenerate-target, degenerate-rotation, sane-fail, handedness-flip,
  residual-tilt) keep declining (minimal variant; the uniform variant - retain on post-fire
  re-validation failures too - is safe in principle but unmeasured, deferred).
- **Option 3 relationship:** not needed. This is a window-resolution fix; the SOI-handoff kink
  (the accepted baseline) persists for Eve as it does for Duna, and precondition (b) stays
  untouched and open.
- **How it fails closed:** a retained conic that misses the SOI -> candidate fails at the
  encounter check -> next candidate -> faithful. Degenerate geometry -> existing declines.
  Nothing new renders without passing every existing downstream guard.

### Design B: r2-aware gate reformulation (fire/retain decided by predicted flatten miss)

Replace the `|incAch - targetInc| <= 0.5` criterion with the physically load-bearing quantity:
fire the correction only when the flatten's predicted arrival miss `|dot(r2, n_ach)|` <= margin
x target SOI; otherwise retain. Fixes H2's blindness at the root and is *equivalent for Duna*
(max miss ~21.7 Mm < 47.9 Mm SOI -> always fires) and *equivalent for Eve at this window*
(271 Mm > 85 Mm -> retains). Rejected for this branch: it replaces a calibrated,
in-game-validated gate rather than adding a fallback to it, re-opening the tolerance-calibration
question for no additional measured benefit over Design A. Recorded as a candidate follow-up
consolidation once Design A's retained population has field measurements.

### Design C: broken-plane synthesis (mimic the recorded two-conic transfer)

Synthesize flat-leg + mid-course plane change + in-target-plane leg, mirroring what the player
actually flew. Highest visual fidelity; also the largest blast radius by far: multi-segment
`ReplaceHeliocentricLeg` contract change, a second solve + patch point per window, new failure
modes, and it is a sibling of option 3's multi-leg synthesis with the same class of structural
doubts - it would need precondition-(b)-style groundwork. Rejected for this branch; noted as the
shape a future "transfer style fidelity" feature would take.

### Design D: bound from the recorded transfer's own inclination

`bound = max(current bound, recordedInc + tol)`. Killed by the fixture measurement: the recorded
legs are 0.0021 and 2.06 deg (the resolver's `RecordedHeliocentricInclination` at
`ReaimPlaybackResolver.cs:790-804` would return 0.0021 - the first in-window Sun segment), so
the bound barely moves and Eve still declines. Also fragile per-fixture. Rejected on evidence.

## 3. Experiments-first sequence (before the behavior change)

| # | Experiment | Where | Decision rule |
|---|---|---|---|
| E1 | Encode the Eve cycle-0 geometry model as headless xUnit: stock-constant ephemeris, D0, the 27-candidate band; assert (i) incAch is candidate-invariant and ~1.236, (ii) flatten miss > 3x Eve SOI, (iii) plane(r1,r2) inc > bound for every candidate | `Source/Parsek.Tests/ReaimTransferSynthesizerTests.cs` | If (ii) came out < SOI, the correction was applicable and the defect would be in the gate arithmetic -> pivot to Design B. Already computed twice (planning pass + independent supervisor reproduction, appendix A): 271-290 Mm vs 85.1 Mm -> Design A confirmed. The xUnit cell pins it in-repo. |
| E2 | Drive `UvLambert.Solve` headlessly on the modeled Eve endpoints (step-0 and band-edge candidates, launch-plane handedness normal supplied); assert convergence, prograde, v1 in span(r1,r2), terminal point = r2 | `Source/Parsek.Tests/UvLambertTests.cs` | If the solver failed at step 0 the fix would also need tof-search work. (The V8 logs already show all 27 candidates reached the tilt gate, so convergence is expected; the cell makes it a permanent guard.) |
| E3 | Fixture ground truth: pin the committed Eve recording's Sun-leg inclinations (0.0021 / 2.0627 / 2.0689) from the `.prec` chain - "the recorded transfer is broken-plane; no single conic can be style-faithful" as a repo fact | headless cell | Informational - kills Design D, scopes Design C out. |
| E4 | Pre-change baseline | none needed | The 5x bit-identical V8 runs (2026-08-11) are the baseline. |
| E5 | Post-change, in-game: Reaim/Periodicity category with the disposition change deployed; read the tilt-correction histogram: Duna fired/noop only, ZERO Duna retained/unreachable; Moho/Eeloo re-measured | in-game runner via harness | Any Duna `state=retained` or unreachable hit -> STOP, revert (gate arithmetic changed, not just disposition). |

## 4. Phased implementation

- **Phase 0 - pin the measurement (tests + docs only, no behavior change). LANDED 2026-08-11** (f923c91fd + the 210bc84cf builder correction). Commit E1 + E2 + E3
  cells; update the 2026-06-15 first-raise paragraph with the settled diagnosis; this plan doc.
  Full `dotnet test` green.
- **Phase 1 - the disposition change. LANDED 2026-08-11** (`DecideTiltDisposition` + `TiltDisposition`
  + `RecordRetainedTilt` + `RetainedTiltCount` in `ReaimTransferSynthesizer.cs`; the new
  `reaim window fell back faithful:` Warn in `ReaimPlaybackResolver.cs`; 8 headless cells; CHANGELOG +
  todo). `DeclinedCorrectionCount` is deliberately NOT incremented on a retain - a retain is not a
  decline - while `UnreachablePlaneDeclineCount` keeps counting the gate HIT so the Duna invariant's
  meaning is preserved. Pure `internal static`
  `DecideTiltDisposition(incBefore, bound, gateSafe) -> Fire | Retain | Decline`; wire
  unreachable-plane to Retain (skip correction, keep conic, continue to encounter validation);
  add `RetainedTiltCount`; keep `UnreachablePlaneDeclineCount` incrementing on the branch
  (renaming would break the Duna invariant's semantics; document the widened meaning); emit
  `state=retained reason=unreachable-plane` in the same field grammar. Headless cells over the
  measured Eve numbers (10.20-14.71 / 2.6 / 1.2358 / 2.1 -> Retain) and Duna's (gate-safe ->
  Fire). NEW (supervisor addition, open question 1 resolved): one grep-stable Warn when an
  ENGAGED unit falls back faithful for a window - a NEW token, so no lane contract moves.
  CHANGELOG + todo same commit.
- **Phase 2 - in-game validation cells. LANDED 2026-08-11.** Re-scope `AssertSaneWindowSegments`'
  tilt upper bound to the new contract (per-window against correction state; do NOT loosen the Duna
  bound); add a `KerbinToEve()` driver; re-baseline Moho/Eeloo. HARNESS TRAP: these land in the
  `Periodicity` category counted by `CommittedBatchTallySourceSyncTests` - re-pin the affected specs'
  `BATCH_COMPLETE v1 total=N` tallies in the same commit (`M2-periodicity-solver` 11/7 -> 12/8; a
  SECOND gate, `IngameCategoryInventoryDocTests`, also pins the same count in
  `docs/dev/autotest-ingame-category-inventory.md`). Deploy via
  `harness/provision/provision.py --profile stock-minimal`; verify the automation DLL, then fly
  the category.
  - **ADDENDUM (supervisor ruling, same phase): the Eve cell's first flight found a PRE-EXISTING
    defect and it was fixed here.** `TrySynthesizeTransfer`'s patched-conic fast path trusted
    `Orbit.UTsoi` unconditionally once stock promoted a target encounter; because r1 sits at the
    launch body's centre, `UTsoi` is often the LAUNCH SOI transition at `departureUT` - measured on
    six Kerbin->Eve windows at 4.6-23.4 Gm from Eve against an 85.1 Mm SOI, five of them plain
    `state=noop` candidates, i.e. orthogonal to this branch's disposition change and merely made
    reachable by it. `soiEntryUT` is the instant the seam/capture re-time consume, so leaving it
    would have poisoned Phase 3's V8 rendering read. Fixed by the pure `IsGenuineTargetSoiEntry`
    (strictly-after-departure AND within-SOI) plus fall-through to the existing proximity sweep -
    never a new decline. Full evidence, measured tables and guard inventory:
    `docs/dev/todo-and-known-bugs.md` -> SYNTH-SOI-ENTRY-FASTPATH-LAUNCH-TRANSITION.
- **Phase 3 - V8/V8T re-pin choreography (reading -> armed -> negative control). LANDED 2026-08-11** (86eb484c2; V8 _1242 baseline red by design -> _1244/_1245 readings -> _1246 control; V8T _1247 -> _1252/_1253; V8F _1250 byte-identical). The fix reds
  V8 BY DESIGN. V8 READING: trio to report-only, two consecutive clean runs, read the new tokens
  (`re-aimed transfer ready`, `state=retained reason=unreachable-plane`,
  `seam-endpoint summary evaluated=[1-9]` with `outsideSoi=0`); ARM those + FORBID
  `synth failed across`, `faithful this window`, the Sun->Eve raise; NEGATIVE CONTROL (e.g.
  temporarily require `outsideSoi=[1-9]`), confirm red, revert. V8T: `reaimed=False` flips ->
  reading -> re-pin. V8F: NO spec change (forced faithful bypasses the synth at
  `MissionLoopUnitBuilder.cs:627`); one confirm flight - any V8F drift = the change leaked past
  the knob -> stop.
- **Phase 4 - Duna-lane sweep + full suite. LANDED 2026-08-11** (all six lanes PASS attempt 1; dotnet test 19,923/0). Full `dotnet test`; fly V2/V4/V5/V6M/V6T/V7M; any
  Duna red is stop-and-revert.
- **Phase 5 (optional, separate PR) - census `maxRatio`.** Deferred: a sampler edit invalidates
  every census-pinned lane (2026-08-09 precedent).

## 5. Validation matrix

| Phase | Headless | In-game | Lanes | A red means |
|---|---|---|---|---|
| 0 | E1/E2/E3 + existing suites | - | - | planning arithmetic wrong -> re-derive |
| 1 | DecideTiltDisposition cells; existing synthesizer cells unchanged | - | - | disposition logic broken or gate arithmetic disturbed |
| 2 | BATCH tally sync | Reaim/Periodicity: Duna NEVER-UNREACHABLE, Duna FIRED, Eve driver, Moho/Eeloo re-baseline | - | Duna: revert. Eve: retained conic failing encounter (premise wrong) |
| 3 | - | - | V8 (re-pin), V8T (re-pin), V8F (confirm) | V8 reading != predicted: synth resolves differently than modeled. V8F drift: leak past the knob |
| 4 | full dotnet test | - | V2/V4/V5/V6M/V6T/V7M | Duna re-aim disturbed - hard stop |

Headless-vs-KSP boundary: `TryBuildLoopUnitForSelection` runs headless behind the fake body-info
seam (classify/plan/schedule), but the per-window solve is KSP-bound at three points
(`ReaimPlaybackResolver.FindBody`, `Orbit.UpdateFromStateVectors`,
`PatchedConics.CalculatePatch` + proximity fallback). The fix loop iterates headlessly at the
pure-decision and raw-Lambert levels; conic-level confirmation is the in-game Reaim category
plus the V8 lane.

## 6. Risk register and regression guards

- **Duna disturbance (top risk):** changed branch unreachable for Duna by arithmetic; the
  in-game NEVER-UNREACHABLE invariant byte-identical in meaning; V2/V4 pins. Watch:
  `state=fired` still present, zero `state=retained`, census unchanged.
- **Reverted-regression traps, avoided by construction:** no render-span trim (direct-path
  bounds stay NaN full-span); no draw/icon window extension over SOI legs; no recorded-data
  mutation; no new solver.
- **Extreme retained tilt (near-179 windows):** a retained conic crossing 90 deg flips
  `IsRetrogradeTransfer` -> direction-mismatch decline (fail-closed, correct). Sub-90 steep arcs
  render truthfully; product-taste note in CHANGELOG.
- **Window-to-window plane variance:** Eve's tilt is node-relative and does not recur with the
  synodic; the rendered plane will visibly differ per window. Truthful; CHANGELOG note.
- **Oracle caveat for non-step-0 windows:** usedTof != recorded tof re-times the arrival; the
  oracle's `reaimed-seam-instant-unknown` refusal covers the re-timed case (skip, not false
  raise), costing measurement, not correctness. Cycle 0 resolves at step 0, so V8's
  `outsideSoi=0` pin is safe; document beside the oracle's caveat (2).
- **Silent faithful fallback remains reachable** (Lambert non-convergence, degenerate
  branches). This fix removes the measured route, not the class; the new Warn (Phase 1) makes
  any remaining occurrence grep-loud.
- **Harness tally + DLL-deploy traps:** per `.claude/CLAUDE.md`.

## 7. Open questions -> supervisor dispositions (2026-08-11)

1. **Surface the per-window faithful fallback?** RESOLVED - YES, as a new grep-stable Warn in
   Phase 1 (new token; no lane contract moves). A UI indicator stays a separate product
   decision.
2. **Is a 10-15 deg (extreme: 40+) tilted rendered transfer acceptable?** RESOLVED - YES: it is
   the true conic that arrives; the alternative is a ghost missing by 4.6 SOI radii. CHANGELOG
   states the visible difference from the flown flat-then-plane-change profile.
3. **Claim scope?** RESOLVED - Eve only (measured + re-pinned lanes). Moho/Dres/Eeloo are
   unmeasured collateral: re-baseline their in-game cells, no lane-grade claims.
4. **Census maxRatio now or later?** RESOLVED - deferred to its own branch.

## Appendix A - independent verification (supervising session, 2026-08-11)

A from-scratch python model (stock ephemeris, Kepler solve, no shared code with the planning
pass) reproduced: incAch = 1.2358 deg (matches the live-measured constant to four decimals);
flatten miss = 288.5 Mm vs Eve SOI 85.1 Mm; plane(r1,r2) inclination ~18.5 deg at recorded tof
(live band 10.20-14.71 across candidates - same regime); transfer angle 175.0 deg; Eve
z-offset at arrival -271.1 Mm. Two independent derivations and the live product logs agree.
