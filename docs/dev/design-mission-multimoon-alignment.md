# Design: Multi-Moon Destination Alignment (M-MIS-6, the looped "Jool-5" window cut)

Status: ACCEPTED (M-MIS-6). This is the "2+-moon mini star systems" deferred item from
`docs/parsek-missions-design.md` §14.4 and the "Jool-class many-moon destinations" deferral in
`docs/dev/design-mission-periodicity.md` (Scope and the explicit deferral boundary). It consumes
the SolveArrivalWindow production wiring built for the M-MIS-4 D8 landing+station dual (branch
`mmis4-solve-arrival-window`). Intra-SOI per-leg re-aim is explicitly OUT OF SCOPE — see §8.

## 1. Problem

A looped Jool-5 recording today loops FAITHFULLY only: `ReaimClassifier` supports the
Kerbin→Jool transfer (Jool is a direct Sun child), but `DestinationConstraintExtractor` fails
closed at 2+ SOI-entered moons (`MaxConstrainedMoons = 1`), so nothing aligns the moons across
the loop shift. Each moon-relative block self-anchors to the LIVE moon while the Jool-centric
inter-moon arcs replay inertially, so every encounter seam renders disconnected — the
Mun-desync mechanism, once per moon. The pre-M-MIS-6 decline is also SILENT for the no-station
shape (no amber), violating the never-silent norm.

## 2. What makes it tractable without new math (2026-06-10 investigation, corrected)

- (a) All encounters shift TOGETHER under one arrival hold: the recorded in-SOI timeline is
  rigid, so alignment needs the moons' JOINT CONFIGURATION to recur, not each moon
  independently.
- (b) Stock Laythe:Vall:Tylo are a near-exact 1:2:4 resonance. Measured from the stock SMAs
  (μ_Jool = 2.82528e14): P_Laythe = 52,980.9 s, P_Vall = 105,962.1 s, P_Tylo = 211,926.4 s;
  P_Vall − 2·P_Laythe = +0.3 s, P_Tylo − 4·P_Laythe = +2.8 s. The inner-three configuration
  recurs every ~T_Tylo (~211,926 s) to well within SOI tolerance.
- (c) The stock major moons are tidally locked (rotation period == orbital period), so a
  moon-landing rotation constraint collapses numerically into that moon's orbital phase — the
  same tidal collapse `MissionPeriodicity` / `DestinationArrivalSolver` already exploit for
  Ike/Duna. No special-casing is needed: the collapse is emergent from equal periods.
- (d) Bop/Pol are incommensurate with the inner three (P_Bop/P_Laythe ≈ 10.277,
  P_Pol/P_Laythe ≈ 17.023): a full 5-moon tight alignment is effectively non-recurring within
  any playable horizon.

**Honest correction to (b) — the drift ACCUMULATES (the finite aligned horizon).** The
configuration error at the m-th recurrence is ~m times the per-recurrence residual, so
alignment does not last forever. With the recurrence lattice anchored on Vall (see D2),
T_config = 2·P_Vall = 211,924.2 s and the per-T_config drifts are Laythe +0.6 s, Tylo −2.2 s
against SOI tolerances of 1,155 s (Laythe), 940 s (Vall, exact on the lattice) and 5,346 s
(Tylo). One Kerbin→Jool synodic window is ~10,090,900 s ≈ 47.6 T_configs, so the joint
configuration stays within SOI tolerance for roughly the first **~40 windows** (~44 Kerbin
years of looping), then leaves tolerance and does not return for centuries. (A naive
longest-period anchor — Tylo — wastes the loosest tolerance on the exact leg and sustains only
~8 windows; that is why D2 anchors by duty.) The design accepts this as a FINITE ALIGNED
HORIZON, computed and surfaced at build time (D6), never silent. Indefinite alignment of a
drifting multi-moon configuration is physically impossible with one hold — that is M-MIS-7
territory (per-leg re-aim), gated on this milestone's playtest.

## 3. Decisions

**D1 — T_config is a near-coincidence product, never a new derivation.** T_config = k·P_anchor
found by `MissionPeriodicity.TryFindNextScheduleK(anchorPeriod = P_anchor, others = the
remaining participant periods, tolerances = ScheduleToleranceSecondsFor(...), kStart = 1,
lookahead = bounded)` — the shipped zero-drift near-coincidence scan, reused verbatim. No
in-tolerance k within the bounded horizon ⇒ fail closed to faithful with an amber reason naming
the violating constraint and the best residual (the Bop/Pol outcome, finding (d)).

**D2 — the lattice anchor is the smallest-duty participant.** Anchor = the participant with the
smallest tolerance/period duty (the `SelectAnchorConstraintIndex` rationale, reused): the
anchor rides the T_config lattice EXACTLY (zero residual forever), so anchoring the tightest
constraint wastes the least tolerance and maximizes the aligned horizon (~40 vs ~8 windows for
stock inner-three; §2). For Laythe/Vall/Tylo the anchor is Vall and the scan accepts k = 2,
i.e. T_config = 2·P_Vall ≈ T_Tylo — the todo's predicted ~211,926 s.

**D3 — participants = destination-side constraints of the multi-moon set.** The SOI-entered
moons of the target within the included window (the extractor's existing MoonConfig rule —
`Orbital` constraints whose body's parent is the target), PLUS any landing-rotation
(`Rotation`) constraints on those constrained moons, PLUS the target's own DestRotation when
present. Rotations obey the shipped mode ladder: Drop filters transited rotations out
(`IsTransitedBodyRotation`, exactly as `SolveArrivalWindow` does); Loose/Tight pick the
rotation tolerance via `ScheduleToleranceSecondsFor`. Orbital (moon) constraints are never
dropped and carry the SOI tolerance (`SoiRadius/OrbitalVelocity`), mode-independent. Tidally
locked moon rotations equal their orbital periods and collapse into them for free (finding
(c)); a NON-locked moon rotation is just another period in the scan and honestly declines the
config when incommensurate — no special guard needed.

**D4 — all-or-nothing v1.** If ANY participant breaks the joint recurrence (Bop/Pol legs, a
non-locked moon rotation under Tight), the WHOLE configuration fails closed to faithful with an
amber reason naming the shape, periods and residuals — never a silent partial alignment.
The considered alternative — align the resonant subset and give the incommensurate legs Loose
tolerance / per-leg amber — is deliberately deferred: it introduces a new partially-aligned UI
state and per-leg semantics that belong with the M-MIS-7 playtest evidence.

**D5 — one hold, one period: T_config REPLACES T_rot/T_station for the multi-moon shape.**
The multi-moon shape (2+ constrained moons, NO station) takes a single per-loop hold in
[0, T_config) at the recorded SOI-entry boundary, exactly like the shipped W_N
destination-rotation hold with T_config substituted for T_rot. Moon-landing rotations and the
DestRotation fold INTO the T_config scan (D3) instead of taking their own hold. Station-bearing
multi-moon shapes stay fail-closed with amber: the shipped joint solve models exactly ONE
exact-hold lattice plus one tolerance-checked second period; a station adds a genuinely
incommensurate (player-chosen) period on top of the moon pack, which no single hold covers.

**D6 — the finite aligned horizon is computed, gated and surfaced.** Engage requires (i) the
T_config recurrence scan to accept (D1), (ii) T_config to fit the loop slack (the same
slack-clamp reasoning as the joint hold budget — the clock's defensive hold clamp must never
silently truncate the hold), and (iii) the hold-aware `DestinationArrivalSolver.
SolveArrivalWindow` pick (holdAlignPeriodSeconds = T_config) to land the FIRST window within
tolerance — the M-MIS-4 wiring, reused. Any miss fails closed with an amber reason carrying the
measured residuals. On engage, the aligned-window horizon (the count of consecutive
in-tolerance windows from k = 1, capped for reporting) is computed and logged in the ARRIVAL
HOLD Info line; loops past the horizon keep the lattice hold (bounded, slowly-growing
configuration error — strictly no worse than faithful's arbitrary phase) and the decline is
never silent: the horizon is in the engage log and the design docs. An in-UI horizon readout is
a playtest follow-up, not v1.

**D7 — the extractor emits, the planner decides (the IsJointLandingStation precedent).**
`DestinationConstraintExtractor` flips the 2+-moon early reject into EMISSION: the multi-moon
set stays `Supported` with all MoonConfigs in `Constraints` (contract widened from "0/1
MoonConfig" to "0..N MoonConfigs") and the constrained-moon landing rotations in a NEW separate
`MoonRotations` field (kept out of `Constraints` so every 0/1-moon consumer stays
byte-identical). Station+moon and moon-orbiting-station rejects are unchanged and now also
cover the station-bearing Jool-class shape (the moon-count early-return that used to shadow
them is gone). `ArrivalHoldPlanner.ComputeArrivalHold` routes `ConstrainedMoonCount >= 2` to
the new pure `ComputeMultiMoonConfigHold`, which owns the D1/D2/D6 gates and fails closed with
amber on every miss. The pre-M-MIS-6 SILENT no-station Jool-class decline becomes an amber —
the never-silent surfacing this milestone owes the UI.

**D8 — the loop clock is UNCHANGED.** The per-loop hold plumbing already carries an arbitrary
align period since M4c (`LoopUnit.ArrivalAlignPeriodSeconds`, the single-period
`ComputePerLoopArrivalHoldSeconds` sawtooth): the multi-moon hold ships as a plain single-period
hold with AlignPeriodSeconds = T_config. No new clock fields, no new span-clock dispatch, no
joint-secondary fields (those stay the D8 landing+station dual's). A zero base hold substitutes
one whole T_config to keep the clock's hold>0 gate engaged (the shipped joint-hold trick; the
per-loop formula mods it back to the true zero base, so no spurious dead time).

## 4. Reuse map (the no-new-solver mandate)

| Need | Reused primitive |
|---|---|
| Joint recurrence scan (T_config) | `MissionPeriodicity.TryFindNextScheduleK` + `CircularPhaseError` |
| Anchor selection rationale | `SelectAnchorConstraintIndex` (smallest duty) |
| Per-constraint tolerances | `MissionPeriodicity.ScheduleToleranceSecondsFor` (SOI formula for Orbital; mode ladder for Rotation) |
| Drop-mode rotation filter | `MissionPeriodicity.IsTransitedBodyRotation` (the SolveArrivalWindow filter semantics) |
| Window pick / first-window gate | `DestinationArrivalSolver.SolveArrivalWindow` hold-aware sampling (holdAlignPeriodSeconds = T_config, maxWholeHoldPeriods = 0) — the M-MIS-4 wiring |
| Base hold | `GhostPlaybackLogic.ComputeArrivalAlignHoldSeconds` |
| Per-loop hold clock | `GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds` via `LoopUnit.ArrivalAlignPeriodSeconds` (no change) |
| Slack clamp reasoning | the joint hold's loop-slack budget clamp (`ComputeJointArrivalHold`) |
| Amber surfacing | `ArrivalHoldResult.AmberReason` → `LoopUnit.ArrivalAmberReason` → Missions T- cell tint + tooltip (M4c plumbing) |

The only new pure code: the multi-moon planner branch (`ComputeMultiMoonConfigHold` —
participant assembly + gates + result), and a small reporting-only aligned-horizon counter in
`DestinationArrivalSolver` (a thin `CircularPhaseError` loop, not a solver).

## 5. What the extractor emits instead of the 2+-moon reject

`DestinationConstraintSet` for a multi-moon destination: `Supported = true`,
`ConstrainedMoonCount = N (>= 2)`, `Constraints = [DestRotation?] + N MoonConfigs` (solver
order), `MoonRotations = [Rotation constraints on constrained moons]` (new field, empty for
every other shape), station fields as today. Fail-closed shapes after the flip: station+moon
(any moon count), moon-orbiting station, degenerate periods — all with reasons. The
`MaxConstrainedMoons` cutoff constant is retired with the reject.

## 6. Tolerance model summary

- Moon orbital phase: SOI tolerance `SoiRadius/OrbitalVelocity` per moon (mode-independent) —
  stock: Laythe 1,155 s, Vall 940 s, Tylo 5,346 s, Bop 824 s, Pol 832 s.
- Moon landing rotation / DestRotation: the Off/Loose/Tight ladder — Drop removes it, Loose =
  5° of the rotation period, Tight = 0.25°. Tight shortens the aligned horizon honestly (the
  gate + horizon math see the tighter band; e.g. a Tight Laythe landing sustains ~1-2 windows).
- Resonant subsets pass the D1 scan (tight alignment); incommensurate participants fail it and
  amber per D4 (the "Loose or fail-closed" fork resolves to fail-closed in v1).

## 7. Test plan (failing-first discipline)

Written and verified FAILING before any implementation:
1. Resonant inner-three fixture (planner-level, stock-like synthetic Laythe/Vall/Tylo periods +
   SOI tolerances, deterministic pinned UTs): asserts the config hold engages with
   T_config ≈ 2·P_Vall (≈ T_Tylo) and — the encounter-alignment property — for each replayed
   loop N in the horizon, `CircularPhaseError(entryOffset0 + N·cadence + W_N, P_moon) <=
   tol_moon` for EVERY moon (the ArrivalAlignHoldTests re-derivation pattern). Fails on main:
   extractor fail-closed ⇒ hold never applies.
2. Incommensurate (Bop-like) fixture: asserts the clean fail-closed outcome WITH an amber
   reason naming the violator (fails on main: the no-station Jool-class decline is silent).
3. Builder-level E2E (MissionPeriodicityTests style, `WithSoiEntry` per moon): unit carries
   `ArrivalHoldSeconds > 0`, `ArrivalAlignPeriodSeconds ≈ T_config`, `kind=config` log.
4. Byte-identity pins: single-moon (Duna+Ike) shape unchanged with the new `MoonRotations`
   field populated; station+moon reject unchanged; Drop-mode multi-moon still aligns the moons
   (orbital constraints never dropped); slack-starved config ambers.

## 8. Out of scope — M-MIS-7 gate

Intra-SOI per-leg re-aim — re-solving moon-to-moon transfer legs inside the destination system
(Jool-centric Lambert re-solves, per-leg holds at each moon-SOI seam) — is M-MIS-7, GATED on
this milestone's in-game looped Jool tour playtest. It is the honest fix for what this design
deliberately does not attempt: indefinite alignment past the finite horizon (§2), non-resonant
moon packs, Bop/Pol legs (D4), and station-bearing multi-moon shapes (D5). Do not build it
speculatively; the playtest decides whether it is needed at all.
