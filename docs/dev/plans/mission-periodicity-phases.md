# Plan: Mission Periodicity - phased, iterative implementation

Companion to `docs/dev/design-mission-periodicity.md` (the what/why). This is the
how: phases of increasing complexity, each independently shippable and testable, so
we can build and validate iteratively instead of one big drop. Accuracy is the
through-line - this is the foundation for supply routes, so each phase locks its
math with pure unit tests before any UI/playtest.

Vocabulary (from the design doc):
- **Constraint:** a phase requirement an included segment imposes - `Rotation(body)`
  (a surface/atmospheric segment must connect its hand-off to an inertial orbit of the
  same body over its ground spot; emitted only when both a surface segment AND an
  inertial orbit of that body are included) or `Orbital(body, relativeToParent)` (an
  SOI entry must reach the body where it will be). Each carries a period + a phase
  offset (the recorded UT of the segment relative to `UT0`).
- **`UT0`:** the recorded launch UT = the trimmed mission's span start.
- **`P`:** the recurrence period at which all the included config's constraints line
  up (best-fit within tolerance). Next faithful launch = smallest `UT0 + k*P >= now`.
- **Tolerance:** physics-derived (orbital: `~SOI_radius / orbital_velocity`;
  rotation: a small fraction of a degree).

General rules for every phase:
- Pure/static + `internal` for the math so it is directly unit-testable; Unity-bound
  pieces (reading `FlightGlobals` bodies, the UI) sit behind a thin seam.
- `dotnet test` green at the end of every phase; deployed-DLL verified after builds.
- Cross-parent (interplanetary) targets are DETECTED and reported "not yet supported"
  from Phase 1 until Phase 4 lands - never silently mis-scheduled.
- Affects ONLY looping Missions; non-looping and per-recording auto-loop ghosts are
  untouched throughout.

---

## Phase 0 - Pure constraint extraction (no wiring, no UI)

**Goal:** turn a trimmed mission config into its ordered constraint list. The heart
of the feature; everything else consumes this.

**Scope**
- New pure class (e.g. `MissionPeriodicity` / `MissionConstraintExtractor`):
  input = the `ComputeTrimmedMemberWindows` output (committed index -> window) +
  the committed recordings + a body-info provider; output = `List<PhaseConstraint>`.
- Per-segment frame classification from `OrbitSegment` body/bounds + TrackSection
  reference frames: surface/atmospheric -> `Rotation(B)` ONLY when the included set
  ALSO has an inertial-orbit segment of B (the surface<->inertial-orbit hand-off; a
  bare surface arc with no orbit of B is faithful at any time -> no constraint);
  inertial orbit -> no constraint of its own (it only enables that hand-off);
  SOI entry/transit (capture, transient flyby, or assist) -> `Orbital(C)`.
- Same-parent vs cross-parent detection on each `Orbital(C)` (compare `C.referenceBody`
  to the launch body's parent); cross-parent flagged `Unsupported` (Phase 4).
- Detect rendezvous/dock in the included span -> flag `Unsupported` (out of scope).
- Body data (`rotationPeriod`, `orbit.period`, `referenceBody`) behind an
  `IBodyInfo`-style seam so tests pass synthetic bodies (no `FlightGlobals`).

**Files:** new `MissionPeriodicity.cs` (or similar); read `OrbitSegment` / TrackSection
frame helpers; no changes to existing call sites yet.

**Tests:** synthetic body sequences ->
- single-body orbit, ascent trimmed -> empty constraint set;
- launch + Kerbin orbit -> one `Rotation(Kerbin)`;
- Mun mission -> `Rotation(Kerbin)` + `Orbital(Mun, same-parent)`;
- Mun landing-and-return -> two `Rotation(Kerbin)` (launch + reentry offsets) +
  `Orbital(Mun)`;
- cross-parent (Duna) -> `Unsupported` flag set;
- tidally-locked body -> rotation+orbital share period/phase.

**Exit:** constraint lists correct for all the above; suite green; zero behavior
change in game (nothing calls it).

---

## Phase 1 - Single-constraint phase-lock (first visible win)

**Goal:** phase-lock the simple, common cases so a looped Kerbin-orbit mission sits
over KSC and a looped Mun mission's transfer actually reaches the Mun.

**Scope (the Tier-1 contract from the design doc - lock the SINGLE dominant
constraint, never "leave unlocked"):**
- Pure `P` solver, Tier 1: 0 constraints -> `MinCycleDuration`; the dominant
  constraint determines `P` and the rest are KNOWINGLY dropped (logged):
  - exactly one `Rotation` -> `P = rotationPeriod`;
  - one direct-child `Orbital` (`C.referenceBody == launchBody`, e.g. Mun) -> `P =
    C.orbit.period`, even when a `Rotation(launchBody)` is also present (the flagship
    Mun case: lock the intercept, accept the launch-site rotation residual - this is
    the explicit Tier-1 simplification, NOT the Phase-2 best-fit);
  - a `Rotation`+direct-child-`Orbital` pair on a tidally-locked body (same period)
    -> that one period (collapses for free).
  Joint best-fit over MULTIPLE INDEPENDENT constraints (e.g. Mun landing-and-return's
  two Kerbin-rotation offsets, or rotation + a non-tidally-locked intercept where the
  residual matters) -> Phase 2; Phase 1 still locks the dominant intercept and logs
  the dropped constraint(s) as a Tier-1 residual.
- Next-window: smallest `k` with `UT0 + k*P >= now` (k may be negative for
  future-dated `UT0`); guard `rotationPeriod <= 0` (treat as no rotation constraint).
- Wire into `MissionLoopUnitBuilder.TryBuildMissionUnit`: snap `phaseAnchorUT` to the
  next window, quantize the cadence to a multiple of `P` (above the existing floors).
  Fold the constraint inputs (transited bodies + periods) into `BuildSignature`.
- Cross-parent (sibling/interplanetary) / rendezvous -> reported unsupported, fall
  back to today's arbitrary-phase behavior (so we never mis-schedule before Phase 4 /
  ever for rendezvous). This is the only "leave unlocked" case, and it is for configs
  we cannot yet schedule correctly - NOT for the supported Mun case.

**Files:** `MissionPeriodicity.cs` (solver); `MissionLoopUnitBuilder.cs`
(`TryBuildMissionUnit` does the cadence quantization + anchor snap, `BuildSignature`).
No `GhostPlaybackLogic` change (the quantization sits in the builder, matching the
design's "everything downstream is unchanged").

**Tests:** solver unit tests (each easy case + next-window `k` incl. future-dated);
a `MissionLoopUnitBuilderTests` case asserting the snapped anchor for a single-body
+ a same-parent Mun config.

**Exit (playtest):** looped Kerbin-orbit mission's map orbit sits over KSC; looped
Mun mission's transfer reaches the live Mun. Suite green. **Review checkpoint after
this phase** (it is the first behavior-changing wiring).

---

## Phase 2 - Joint best-fit + tolerance + over-constrained handling

**Goal:** correct `P` for any number of constraints, with an honest residual.

**Pre-phase spec needed:** the best-fit `P` algorithm must be pinned at Phase 2
PLANNING (not hand-waved): the objective is to minimize the max over constraints of
`dist( (UT0 + k*P + offset_i) mod period_i , recordedPhase_i )` (the phase OFFSETS
enter, not just the period ratios), searched over `P` (continued-fraction /
Stern-Brocot on the period ratios) and `k`, bounded by a max-`P`. Write the worked
algorithm + a couple of hand-checked numeric cases before coding.

**Scope**
- Generalize the solver to N constraints: joint best-fit over the constraint periods
  AND phase offsets (per the objective above), bounded by a max-`P` search.
- Physics-derived tolerance per constraint; compute the best-fit window's residual
  (max phase error across constraints) and whether it is within tolerance.
- Over-constrained (no exact joint window, e.g. Mun landing-and-return): return the
  best-fit window + residual; never refuse. Respect the FULL constraint set (do not
  silently drop one).

**Files:** `MissionPeriodicity.cs` (solver generalization + tolerance + residual);
`TryBuildMissionUnit` consumes residual (stores/exposes for the UI).

**Tests:** multi-constraint joint resonance; over-constrained best-fit + residual
sign/magnitude; tidally-locked collapse; tolerance boundary (just-within vs
just-outside). All pure.

**Exit:** Mun-return and multi-constraint configs yield a window + correct residual;
over-constrained never throws/refuses. Suite green. **Review checkpoint** (the math
core).

---

## Phase 3 - UI: "T- to launch window" column + quality readout

**Goal:** make the schedule visible and the accuracy legible.

**Scope**
- New "T- to launch window" column in the Missions tab: live countdown to the next
  faithful launch; reads "continuous" when `P = MinCycleDuration`.
- Period cell: show the snapped cadence + a label ("every Mun window ~1.6 d").
- Quality/residual readout (column tint or tooltip): green within tolerance,
  amber/flagged when the best achievable window still misses tolerance (so the user
  re-trims the config).
- Wait-for-window behavior: the loop parks until the snapped anchor (the span clock
  already early-returns before `phaseAnchorUT`). Decide decorative-orbit-during-wait
  vs nothing (start with the simpler: show nothing until launch; revisit).

**Files:** `UI/MissionsWindowUI.cs` (new column + readout); reuse the residual from
Phase 2.

**Tests:** pure formatting/countdown helpers (`internal static`); UI behavior is
playtest-verified.

**Exit (playtest):** user sees the next window + countdown; over-constrained shows
amber; single-body reads continuous.

---

## Phase 4 - Interplanetary (synodic) + multi-hop / gravity assists

**Goal:** the hard celestial cases.

**Scope**
- Cross-parent `Orbital(C)` constraint uses the **synodic period** of the launch
  body and C about their common parent (`1 / |1/T_launchParentOrbit - 1/T_C|`),
  not `C.orbit.period`. Also align the launch body's heliocentric position (the
  synodic alignment supplies this).
- Multi-hop transfers / gravity-assist chains: stack one constraint per transited
  body; the Phase 2 joint best-fit already composes them.
- Remove the Phase 1 "cross-parent not supported" gate.

**Files:** `MissionPeriodicity.cs` (synodic constraint + multi-hop); the solver is
unchanged (it already does joint best-fit).

**Tests:** Kerbin->Duna synodic window; a multi-hop / assist chain; verify the
synodic (not orbital) period is used for cross-parent.

**Exit (playtest):** a looped interplanetary mission schedules on the synodic window
and the transfer reaches the target. **Review checkpoint** (new celestial model).

---

## Phase 5 - Supply-routes foundation hookup

**Goal:** expose the accurate schedule as the API supply routes consume.

**Scope (defined fully when the supply-routes design lands):**
- A clean query: "next faithful launch UT for mission M (and its faithfulness /
  residual)", plus the recurrence `P`, surfaced for the route scheduler.
- Guarantee: a returned window actually hits its intercept within tolerance (or is
  flagged), so a route built on it cannot silently break.

**Exit:** supply-route scheduling can build on faithful mission windows. This phase
is the integration seam, not the supply-route feature itself.

---

## Sequencing notes

- Phases 0-2 are pure and low-risk (math + tests), no in-game behavior until Phase 1
  wires the builder. Phase 1 is the first playtestable milestone (and the first
  review checkpoint). Phase 3 is the first UI. Phase 4 is the hardest math. Phase 5
  waits on the supply-routes design.
- Ship incrementally: Phases 0+1 can be one PR (model + same-parent lock), Phase 2 a
  follow-up, Phase 3 a UI PR, Phase 4 its own PR. Keep cross-parent gated until
  Phase 4 so nothing mis-schedules in the meantime.
- Per the project workflow, insert clean-context reviews at the Phase 1, Phase 2, and
  Phase 4 checkpoints (behavior wiring, math core, new celestial model), not after
  every commit.

## References
- `docs/dev/design-mission-periodicity.md` - the design (model, constraints, decisions).
- `Source/Parsek/MissionLoopUnitBuilder.cs` - `ComputeTrimmedMemberWindows`,
  `TryBuildMissionUnit`, `BuildSignature`.
- `Source/Parsek/GhostPlaybackLogic.cs` - `TryComputeSpanLoopUT`,
  `ComputeEffectiveLaunchCadence`.
- `Source/Parsek/UI/MissionsWindowUI.cs` - the Missions tab (period cell; new T- column).
