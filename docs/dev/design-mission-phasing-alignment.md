# Mission Phasing Alignment: looped rendezvous + the general loiter re-timer (M-MIS-4)

*Design note for the M-MIS-4 milestone (`docs/dev/todo-and-known-bugs.md`): aligning a looped
mission's relaunch phase to a TARGET VESSEL (station resupply, the logistics headline
profile), and the general per-cycle phasing-loiter knob that makes those windows frequent.
Scoped, per the milestone, as a general Missions looping tool with the station route as the
prime consumer. This note records the decisions; implementation follows in separate phases.*

**Status:** decisions recorded; M4a (Tier 1 `VesselOrbital`, section 5) IMPLEMENTED on branch
`mission-vesselorbital-tier1` per `docs/dev/plans/mission-vesselorbital-tier1.md`; M4b (the
loiter knob) and M4c (Tier 2) not yet implemented. Parent doc: `docs/parsek-missions-design.md`
(sections 7.4 / 7.5 / 14). Consumers: logistics supply routes
(`docs/parsek-logistics-supply-routes-design.md`), M-MIS-2 P4 (the destination-loiter
re-timer shares the knob built here).

---

## 1. Problem

Three coupled gaps, all forms of "the loop relaunches at an arbitrary phase":

1. **Rendezvous missions never align.** Any Relative track section anchored to another
   vessel inside the included window flags the whole mission
   `Support.UnsupportedRendezvous` (`MissionPeriodicity.HasRendezvousWithinWindow`,
   `MissionPeriodicity.cs:1474`; consumed at `:351`/`:464`): no phase-lock, no re-aim, the
   loop relaunches at whatever phase the cadence lands on. The docked/approach Relative
   sections themselves render against the LIVE station (loop playback resolves the anchor
   through the live-PID contract, `Recording.LoopAnchorVesselId`), so the FINAL approach
   follows the station correctly - but every inertial leg BEFORE it (launch, phasing,
   transfer) replays at an arbitrary station phase, and the approach-to-station seam
   teleports each cycle. This is the rendezvous twin of the Mun desync.
2. **Recorded phasing loiters poison the loop.** A recorded LKO resupply that loitered ~100
   parking revs before rendezvous (the player's own phase-matching) replays all ~100 revs
   every cycle (days of dead time per loop) AND makes joint faithful windows astronomically
   rare (the pad + station + 100-rev geometry almost never recurs). The player's loiter
   existed precisely to phase-match an arbitrary launch time; the loop should re-derive it,
   not replay it.
3. **Logistics needs this.** Route delivery FIRES correctly today (the loop-clock
   `RecordedDockUT` marker is phase-independent), so v0 routes are functionally unaffected -
   but the route's VISUAL is the product: a resupply route whose ghost teleports onto the
   station every cycle undercuts the headline profile. Station resupply is the canonical
   route; this milestone is the faithfulness layer under it.

## 2. Decisions (summary)

| # | Decision |
|---|----------|
| D1 | Anchor-source policy: the anchor vessel must EXIST in the save (loaded or on-rails); the constraint reads its live orbit. A vanished anchor fails closed to faithful. No recorded-orbit derivation fallback (section 3) |
| D2 | Loop playback keeps the live-PID-only anchor contract; loops do NOT gain the recorded-anchor-trajectory fallback in this milestone (section 3.3) |
| D3 | Divergence policy: constraint re-derives from live every build; amber-flag when live vs recorded station orbit diverges past tolerance; composition/mass changes are ignored for phasing (section 3.4) |
| D4 | The phasing-loiter knob is a GENERAL Missions looping tool and FULLY AUTOMATIC: the kept-rev count is DERIVED from the solved relaunch schedule, never a player choice; no toggle ships (opt-out only on playtest demand); first play is always the faithful full recording (section 4) |
| D5 | Per-cycle kept-rev count k_N: a NEW small two-variable enumeration (pad window x kept revs) built ON the existing zero-drift primitives (anchor-pinned window scan + CircularPhaseError + schedule machinery); no new solver math; whole-rev quantization only (section 4.2) |
| D6 | k_N bounds: cutting down to 1 rev AND extending past the recorded rev count are both allowed, extension capped (default +10 revs); unreachable phase within bounds = amber + faithful phase (section 4.3) |
| D7 | Tier 1 = a new `VesselOrbital` constraint kind fed to the existing solver; `HasRendezvousWithinWindow` flips from blanket reject to extraction for the supported shape: exactly ONE same-parent closed-orbit vessel anchor; later same-vessel rendezvous events align automatically by timeline rigidity, which restricts knob cuts to before the FIRST rendezvous (sections 4.3, 5.2) |
| D8 | Tier 2 = the shipped per-loop arrival hold with T_station substituted for T_rot; M4c SHIPS with dual-constraint (landing rotation + station) rejection; wiring `DestinationArrivalSolver.SolveArrivalWindow` is a post-M4c follow-up, its first justified consumer (section 6) |
| D9 | Build order: Tier 1 first (smallest, immediate logistics value), then the knob (lifts the rare-window + dead-time problems, shared with M-MIS-2 P4), then Tier 2 (section 9) |

## 3. The moving-target / anchor-lifecycle policy (decided first, per the milestone)

A station is not a celestial body: it drifts (the player boosts it), changes (docked
traffic, assembly), and can vanish (recovered, deorbited). The recorded anchor data is the
recording's truth; the live vessel is the render truth. The policy below covers the three
anchor classes from the milestone investigation.

### 3.1 Anchor classes

- **(a) A recorded Parsek vessel** (the station has its own recording/tree): live PID
  usually resolves; the anchor recording's OrbitSegments exist as a derivation fallback.
- **(b) A save-unique LIVE-ONLY object with no recording**: rescue-contract derelicts,
  asteroids/comets (both are vessels; a claw grab is the couple). No recorded fallback
  exists. A VANISHED anchor (recovered pod, redirected asteroid) must fail closed.
- **(c) A station that changes BETWEEN loops**: multi-launch assembly, other docked
  traffic. Orbit drift and composition change are different things (3.4).

### 3.2 Constraint source (D1)

The `VesselOrbital` constraint's period (and phase reference) derives, per loop-unit build:

1. **The anchor vessel must EXIST in the save** (resolved via the same
   `FlightGlobals.FindVessel` path loop playback uses; loaded OR on-rails both count - an
   existing vessel always carries an orbit). Read T_station and the phase reference from
   its CURRENT orbit. The live station is where the approach will visually land, so the
   live orbit is the alignment truth.
2. **Fail closed otherwise** (vanished class-b anchors, recovered stations, anything that
   does not resolve): no constraint; the mission stays on the existing
   `UnsupportedRendezvous`-style faithful path with an amber reason. Never guess a period
   from stale Relative offsets, and never derive one from the anchor RECORDING's
   OrbitSegments: a window display computed from recorded data while the live anchor is
   gone would advertise an alignment whose approach member loop playback skips/retires
   (the anchor-unloaded contract), an incoherent UX. The recording-side
   `anchorRecordingId` stays a diagnostic (it names which recording the anchor was), not a
   derivation source.

Asteroid-redirect missions are one-shot by nature: looping one replays it faithfully (the
asteroid is class b; once moved/expended, the vanished-anchor rule applies). No re-aim of an
already-moved asteroid is attempted.

### 3.3 Loop playback anchor contract unchanged (D2)

Loop playback today resolves Relative sections through the live PID ONLY
(`Recording.LoopAnchorVesselId`; an unresolvable anchor skips/retires the member via the
engine's `loop-anchor-unloaded` / `unit-member-anchor-unloaded` paths). The recorded-anchor
trajectory fallback (`TrackSection.anchorRecordingId`) exists only on non-loop paths. This
milestone does NOT extend the recorded-anchor fallback to loops:

- The point of alignment is that the LIVE station is at its recorded-relative phase when
  the approach plays, which makes the live-PID contract MORE correct, not less.
- A recorded-anchor loop fallback would render an approach onto a ghost station while a
  live station sits elsewhere - worse than hiding.
- Revisit only if playtest shows missing finals that matter (then as its own decision).

### 3.4 Divergence and change-between-loops (D3)

- **Re-derivation cadence**: the constraint re-derives from the live orbit on every
  loop-unit build (builds are already signature-gated; the signature gains the anchor's
  orbit identity inputs, section 8).
- **Orbit drift**: when the live orbit's period diverges from the RECORDED rendezvous-time
  orbit (when class-a data exists to compare) past tolerance (default: the same relative
  tolerance family the periodicity solver already uses; concretely a period delta whose
  accumulated phase error over one cadence exceeds the station phase tolerance of section
  5.3), keep aligning to LIVE but surface amber in the period cell ("station drifted").
  Alignment to live is always at least as good as faithful replay.
- **Composition/mass change**: ignored for phasing. Phase alignment depends only on the
  anchor's orbit; a heavier or re-shaped station changes nothing in the constraint. The
  docked-approach VISUAL against a changed station is accepted (the recording is immutable;
  rendering it against the live station is the existing contract).
- **Player boosts the station mid-loop (within a cycle)**: the in-flight cycle finishes on
  the old schedule (units are rebuilt next build; the schedule snaps forward per the
  zero-drift scheduler's existing never-accumulate property). No special handling.

## 4. Design A: the general phasing-loiter knob (the per-cycle keepRevs re-timer)

### 4.1 What exists and what generalizes

`ReaimLoiterCompressor.ComputeCuts` (`ReaimLoiterCompressor.cs:58`) is ALREADY pure and
general: it detects same-body closed-orbit loiter runs in any startUT-sorted OrbitSegment
list and emits whole-period `LoopCut`s from the run START (exit phase preserved, seamless by
the whole-rev argument of `docs/dev/plans/reaim-loiter-compression.md` section 2.1), with
`keepRevs` as a parameter. Today it is invoked ONLY inside `MissionLoopUnitBuilder`'s
cross-parent re-aim branch (`MissionLoopUnitBuilder.cs:441`), with fixed
`keepRevs = DefaultKeepRevs = 1`, and the resulting cuts are a single per-unit list applied
identically to every cycle.

Two generalizations make it the shared knob:

1. **Promote the invocation** from re-aim-only to a general step for ANY looping mission
   with a detected compressible loiter (same-parent missions waiting in LKO for a Mun
   window have the identical dead-time + rare-window problems; the existing `Orbital(C)`
   constraint plays the role of `VesselOrbital`).
2. **Make the kept-rev count PER-CYCLE (k_N)**: the quantized subtractive twin of the
   per-loop arrival hold W_N. Each relaunch window chooses how many parking revs to keep so
   the phase target (station, or celestial window) is met at the exit burn.

### 4.2 The per-cycle solve (D5): reuse the near-coincidence search

Per window, solve (launch window w, kept revs k) so the constraint body/vessel sits at its
recorded-relative phase at the constraint UT, over the achievable set
`{w*T_anchor + k*T_park + const}`. Honest scope: this is a NEW (small) two-variable
enumeration, not a drop-in call of the existing search. The structure:

- OUTER: candidate pad windows, exactly the anchor-pinned whole-multiple scan
  `TryFindNextScheduleK` already implements (`MissionPeriodicity.cs:901`); the pad
  (T_anchor) stays exact at every candidate.
- INNER (new): for each pad window, enumerate k_N in the section-4.3 bounds and score the
  station/window phase residual with the existing `CircularPhaseError`
  (`MissionPeriodicity.cs:841`). Accept the first (w, k_N) within tolerance; else the
  bounded-best pair (min residual, ties to the earlier window then the smaller |k_N -
  recorded|), mirroring the existing scheduler's never-accumulate acceptance shape.

No new solver MATH is built (the milestone's reuse mandate: the window scan, the residual
metric, the schedule machinery, and the pure compressor are all existing primitives); the
new CODE is this inner enumeration plus the plumbing of the chosen k_N (4.5).

Whole-rev quantization is mandatory: an extra (or one fewer) whole rev on a closed orbit is
visually identical to the recorded loiter and keeps the cut/extension seam exact (same
argument as the shipped loiter compression). The residual phase error after quantization is
absorbed by the constraint tolerance (the station capture tolerance, section 5.3).

### 4.3 k_N bounds and failure (D6)

- Lower bound: `keepRevs >= 1` (never cut the loiter to zero; the recorded exit geometry
  needs at least the final rev).
- Upper bound: extension PAST the recorded rev count is allowed (windows that cutting alone
  cannot reach), capped at recordedRevs + `MaxExtraLoiterRevs` (default 10, a constant, not
  a setting). Justification for 10: an LKO parking rev is ~32-45 min, so the cap bounds the
  added per-cycle dead time to roughly 5-7 hours, about one pad day; a phase unreachable
  within +10 revs is usually reachable at one of the NEXT pad windows instead (the outer
  loop compensates), so a larger cap buys little and inflates worst-case cycle length.
  Extension inserts whole-period dead time on the SAME closed orbit, which is the
  loiter-shaped equivalent of the arrival hold and renders seamlessly by construction.
- Cut placement (rigidity guard, mirrors `ArrivalHoldPlanner`'s destination-side-cut
  rejection): knob cuts and extensions apply only to loiter runs that END BEFORE the
  mission's FIRST vessel-rendezvous UT (and, for re-aim missions, before the SOI-entry
  boundary as today). Cuts BETWEEN two same-vessel rendezvous events would break the
  timeline rigidity that auto-aligns the later events (5.2); those loiters stay
  uncompressed until the M-MIS-2 P4 re-timer handles re-timed interior loiters.
- Unreachable within bounds: amber in the period cell, launch on cadence with the faithful
  (uncompressed, k_N = recorded) loiter phase - fail closed, never a partial rev.

### 4.4 Fully automatic (D4), first play faithful

- The knob engages AUTOMATICALLY for a looping mission when `ComputeCuts` detects a
  compressible loiter, matching the shipped re-aim behavior (which already compresses with
  no toggle), and the kept-rev count is DERIVED per cycle from the solved relaunch
  schedule (4.2): the loiter is a consequence of the decided cadence/window, never an
  independent player choice.
- NO per-mission opt-out toggle ships. The recorded loiter was the player's own
  phase-matching instrument, not content; shipped re-aim has auto-compressed with no
  toggle since PR #982 without demand for one; and the project direction is to cull
  speculative settings (0.10.1 removed four settled ones). Correctness against
  mis-detected loiters is the job of the fail-closed detection rules plus the 4.3
  cut-placement guard, not a setting. An opt-out is an additive one-bool change to add
  later IF playtest produces a recording where automatic compression is wrong.
- The FIRST play (the pre-loop live flight and the first-play-floor render) is always the
  faithful full recording; compression and k_N apply to loop cycles only. This matches the
  existing first-play floor invariant.

### 4.5 Engine seam: per-cycle cuts (the one real engine change)

Today `LoopUnit.loiterCuts` is one list applied identically every cycle, and
`MissionRelaunchSchedule` (the zero-drift schedule) carries per-launch UTs. Per-cycle k_N
needs the cuts to vary by cycle. Design:

- Extend the relaunch-schedule entry with the chosen per-launch k_N AND its PRECOMPUTED
  per-launch cut list (cuts are derived once at unit build by the pure compressor, never in
  the hot clock path). The span clock (`GhostPlaybackLogic.TryComputeSpanLoopUT`) already
  takes schedule + cuts; the schedule path resolves "this launch's cuts" by the schedule
  index it already computes, an O(1) lookup. Uniform-cadence units and schedule entries
  without per-launch cuts keep the existing single-list path byte-identical.
- The chosen k_N surfaces through the loop state (`RouteLoopClock` /
  `TryGetRouteLoopState`) so routes can stamp it into the cycle id (M-MIS-11 item 3 is the
  typed-exposure hardening; this design only ensures the value is THERE).

This is the implementation-risk center of the milestone: it touches the span-clock contract
shared by flight/KSC/TS. It ships in the knob phase (M4b in section 9) with its own plan.

### 4.6 Relation to M-MIS-2 P4 (build the knob once)

M-MIS-2 P4's destination-loiter re-timer is the SAME knob pointed at the destination-side
loiter: today `ArrivalHoldPlanner.ComputeArrivalHold` REJECTS any destination with
post-arrival loiter cuts (`ArrivalHoldPlanner.cs:70-75`, the deferred L8 case) because a
destination-side cut breaks the entry-referenced hold's rigidity. With per-cycle k_N, the
destination loiter is RE-TIMED (k_N chosen so the deorbit lands at the recorded rotation
phase) instead of refused. The keepRevs API and the per-cycle schedule seam built here are
exactly what P4 consumes; P4 then only adds the destination-side constraint wiring.

## 5. Design B Tier 1: same-parent station alignment (`VesselOrbital`)

### 5.1 The constraint

New `ConstraintKind.VesselOrbital`. `PhaseConstraint` gains the anchor identity (the
anchor vessel PID + optional anchor recording id; `BodyName` stays the orbited body for
display). `PeriodSeconds` = T_station from the section-3 policy source;
`PhaseOffsetSeconds` = the recorded rendezvous UT minus UT0, as for existing kinds.

### 5.2 Extraction flip (D7)

`HasRendezvousWithinWindow` currently returns "reject" on the first vessel-anchored
Relative section. It becomes an EXTRACTOR for the supported shape and keeps rejecting the
rest:

Supported (extract `VesselOrbital`):
- Exactly ONE distinct vessel anchor across the included window. Several Relative sections
  anchored to the SAME vessel (approach, dock, undock, a later redock) collapse into ONE
  constraint at the FIRST rendezvous UT. This is correct, not lossy, by TIMELINE RIGIDITY:
  a replayed recording advances on its recorded clock, so the interval between the first
  rendezvous and any later same-vessel event is exactly as recorded; with the station's
  period unchanged (drift is 3.4's gate) the station's phase advance over that interval is
  also as recorded, so aligning the FIRST event aligns every later same-vessel event
  automatically - PROVIDED no loop-clock cuts land between them, which 4.3's cut-placement
  rule guarantees.
- The anchor resolves per section 3.2 (exists in the save).
- The anchor's orbit is CLOSED (elliptical) and around the SAME parent body the mission's
  constraint set already operates in (the LKO-resupply shape: pad Rotation(Kerbin) +
  VesselOrbital(station around Kerbin)).

Still rejected (fail closed, reason string preserved):
- Two or more DISTINCT vessel anchors in one window (multi-rendezvous tours).
- Cross-parent vessel anchors (station around the destination body): Tier 2.
- Unresolvable anchors (section 3.2 case 3) and non-closed anchor orbits.

### 5.3 Solving and tolerance

The `VesselOrbital` constraint feeds the EXISTING machinery unchanged: dominant-constraint
lock, joint best-fit, and the zero-drift schedule (`TryBuildRelaunchSchedule`) with the pad
as the exact anchor and the station as an "other" constraint. Window frequency, honestly
stated: WITHOUT the knob the schedule has no free variable, so a pad window must land
within the station tolerance by luck. For a ~6 h sidereal day, a ~33 min LKO orbit, and the
1-degree default tolerance, roughly 0.5% of pad windows qualify - an aligned launch every
~150-200 pad windows, i.e. tens of Kerbin days. Correct and fail-closed, but far too rare
for a route. WITH the section-4 knob, k_N is the free variable and nearly every pad window
becomes alignable (per-cycle dead-time cost bounded by 4.3), and the recorded-loiter span
inflation disappears with the same stroke. This is why the knob is load-bearing for the
logistics profile rather than an optimization.

Tolerance: a station phase tolerance expressed in seconds,
`T_station * (StationPhaseToleranceDegrees / 360)` with a default of 1 degree (for a 100 km
LKO orbit roughly 12 km of along-track error, comfortably inside what the recorded final
approach absorbs since the Relative section follows the live station anyway). Not a
player setting in v1; revisit with the Loose/Tight pattern only if playtest needs it.

### 5.4 UI

- Period cell basis label: `"~2.1h (station window)"` when the dominant/locking constraint
  is `VesselOrbital`; `"varies"` zero-drift display unchanged; amber states for drifted
  (3.4) and unreachable-k (4.3). No new toggles or controls (4.4).

## 6. Design B Tier 2: cross-parent station (destination-orbit rendezvous)

The shipped per-loop arrival hold already aligns ONE destination phase by inserting
loop-clock dead time at the heliocentric-to-capture boundary: `ComputeArrivalHold` aligns
the destination ROTATION (T_rot) for landings. Tier 2 substitutes the STATION'S orbital
period: a hold in `[0, T_station)` lands the SOI entry at the recorded station-relative
phase exactly as W_N lands the deorbit at the recorded rotation phase. T_station is short,
so the hold is cheap and achievable every window.

- `DestinationConstraintExtractor` learns the `VesselOrbital`-at-destination shape (anchor
  policy of section 3 applies unchanged; the live station around Duna is still class a/b).
- The rigidity precondition is the same: a destination-side loiter cut breaks the
  entry-referenced hold, so Tier 2 BEFORE the P4 re-timer keeps the existing
  fail-closed-on-destination-cuts guard; after P4 the re-timer replaces the refusal.
- A destination with BOTH a landing-rotation constraint AND a station constraint has no
  single hold satisfying both periods (D8). M4c SHIPS with this case fail-closed (amber,
  faithful); wiring `Reaim/DestinationArrivalSolver.SolveArrivalWindow` (built, unwired) as
  the multi-constraint window pick is a post-M4c follow-up, and Tier 2's dual-constraint
  case is its first justified consumer - do not wire it speculatively before that, and do
  not block M4c's single-constraint value on it.

## 7. What does NOT change

- Recorded data: immutable, as always. All re-timing is loop-clock-side (cuts, holds,
  schedules); first play is untouched.
- Loop playback's live-PID anchor contract (D2) and the engine's unresolvable-anchor
  retire/skip semantics.
- Route delivery semantics: delivery still fires at the `RecordedDockUT` loop-clock marker;
  this milestone changes WHEN cycles launch and how the loiter replays, not the marker.
- The faithful path: every unsupported/degenerate shape keeps today's behavior with a
  logged reason (fail closed to faithful, the Missions design principle 6).

## 8. Logging and observability

- `[MissionPeriodicity]` extraction lines gain the `VesselOrbital` constraint with anchor
  pid/recording id, period source (live/recorded), and the reject reasons for the
  still-unsupported shapes (batch-counted per build, suppressible via the existing
  `SuppressLogging` flags).
- `[Mission]` knob lines: loiter detected (runs, revs), per-window chosen k_N + residual
  (VerboseRateLimited per mission).
- Amber states (drifted anchor, unreachable k_N, dual-constraint destination) log once per
  transition at Info with the reason string the UI shows.
- Build-signature inputs grow (anchor orbit identity), so the signature gate keeps
  suppressing rebuild spam; the route-side resolve stays under the existing suppression
  flags.

## 9. Build order and phasing (D9)

- **M4a - Tier 1 `VesselOrbital`** (smallest; the constraint layer the knob solves
  against): constraint kind + extraction flip + solver/tolerance + period-cell label +
  tests. No engine changes; the zero-drift schedule already handles the added constraint.
  Honest standalone value: structural (extraction, amber states, display) plus
  occasional aligned windows - alignment stays RARE until M4b supplies the free variable
  (5.3), so M4b follows promptly.
- **M4b - the phasing-loiter knob** (the cadence unlock): general ComputeCuts invocation +
  per-cycle k_N solve + the per-cycle cuts engine seam (4.5) + k_N exposure to routes. Own
  plan doc before building (the span-clock contract change earns it). Shared deliverable
  with M-MIS-2 P4 (the keepRevs API).
- **M4c - Tier 2 destination-station hold**: extractor shape + T_station hold; wire
  `SolveArrivalWindow` only if the dual-constraint case is actually pursued.

Each phase lands as its own PR with unit tests (pure: extraction shapes, k_N solve/bounds,
tolerance math, hold substitution; the span-clock seam gets the full clock-test treatment
`MissionSpanClockTests` already models) plus an in-game scenario test per tier (synthetic
LKO-station resupply recording via the generators; the route runtime tests already cover the
delivery half).

## 10. Open questions (deliberately deferred)

- A per-mission compression opt-out ("Replay full loiter"): deliberately NOT shipped (4.4);
  add only on playtest evidence of a recording where automatic compression is wrong.
- Recorded-anchor trajectory fallback on LOOP paths (D2 says no for now; revisit on
  playtest evidence of missing finals).
- Multi-rendezvous windows (two distinct stations in one mission): rejected in v1; would
  need the multi-constraint window pick generalized beyond destination (post-M4c at the
  earliest).
- A player-facing station-phase tolerance setting (5.3 fixes a 1-degree default).
- Fleet/parallel shipments interaction (per-instance k_N) - parked with M-MIS-11 item 7.
