# Design: Mission Periodicity (launch-window phase-locked looping)

*Status: design note for a feature that follows the Mission Abstraction +
whole-mission looping work (PR #958). It does NOT change the recording format or
the recorded trajectory; it changes only WHEN a looped mission relaunches, so the
replay lines up with the live sky. Builds directly on `MissionLoopUnitBuilder`,
the span clock in `GhostPlaybackLogic`, and the map-presence loop fix. To ship as
its own PR after #958 merges.*

---

## Why this exists

Looping a mission as a unit (#958) replays the recorded trajectory faithfully
relative to its own recorded clock, but it launches each replay at an arbitrary
time (the UT the loop was enabled, `Mission.LoopAnchorUT`, used as the span
clock's `phaseAnchor`). For a mission that stays on one body that is fine. For a
mission that depends on celestial geometry (a launch into orbit, and especially
an inter-body transfer), an arbitrary launch time means the replay no longer
matches the live solar system:

- The recorded **orbit** is stored in the parent body's **inertial frame** at a
  fixed orientation (Keplerian `inclination` / `LAN` / `argumentOfPeriapsis`).
  Replayed at a different time it is drawn at that same recorded orientation, but
  the **launch body has rotated**, so the orbit's periapsis (originally over the
  launch site) no longer sits over the live launch site, and the surface-relative
  ascent (which renders at the launch site's *current* world position) does not
  connect to the orbit's recorded insertion point.
- The recorded **transfer** reaches the **target body's recorded position** at the
  recorded encounter time. Replayed at a different time the **target body is at a
  different orbital phase**, so the transfer ellipse's far point aims at empty
  space, no intercept happens, and the target-relative arc (the Mun approach +
  landing) renders at the target's *current* location, disconnected from the
  transfer.

### Concrete evidence (playtest 2026-05-26, `logs/2026-05-26_2329_mun-mission-map-desync`)

The "Kerbal X" Mun-landing tree, looped, member #15 replays a recorded Kerbin
transfer ellipse (`sma=6,274,896 ecc=0.873 peri=799 km`) then a recorded Mun
approach hyperbola (`sma=-720,778 ecc=1.380 peri=273 km`), with a Kerbin -> Mun
SOI handoff baked into the recording. The loop ran at an arbitrary
`phaseAnchor=54042.92` / `5861.38`, unrelated to the recorded launch UT
(`span start = 3991.76`). There is zero launch-window / re-aim / phase-align logic
in the codebase today (grep count 0). The result on the map and in the Tracking
Station: the orbit is not over KSC and the transfer does not reach the Mun.

This is INHERENT to replaying a celestial-phase-dependent trajectory at the wrong
time. It is not a regression, and it is not caused by the map-presence epoch-shift
fix (`loopEpochShiftSeconds`) - that fix only places the ghost at the right phase
ALONG its recorded ellipse; it cannot move the live target body or re-orient the
recorded inertial ellipse.

---

## The goal

For each replay of a looped mission to be geometrically faithful, the launch must
happen at a UT where the bodies the mission depends on are back in (close to)
their recorded-launch configuration. Faithful launch times are:

```
UT_launch = UT0 + k * P          (k = 0, 1, 2, ...)
```

where:

- `UT0` = the recorded launch UT = the mission owner member's recorded `StartUT`
  (the earliest member after the span sort in `MissionLoopUnitBuilder`; the span
  start).
- `P` = the celestial-geometry repeat period for the mission: the period at which
  every body the mission depends on returns to its recorded-launch phase.

The loop should relaunch on a cadence that is a multiple of `P`, with the span
clock's `phaseAnchor` aligned so cycle 0 lands on a faithful `UT0 + k*P`.

---

## What determines `P`

`P` is set by which bodies and frames the LOOPED replay actually depends on, and
that depends on the mission CONFIGURATION - which composition intervals are checked
(`Mission.ExcludedIntervalKeys`). So `P` is derived from the trimmed member set
(`MissionLoopUnitBuilder.ComputeTrimmedMemberWindows`, the same source of truth the
span + cadence already use), NOT from the full recorded tree. All periods are read
from the live universe at build time (never hardcoded - planet packs like RSS change
them).

Each INCLUDED segment contributes only the phase constraint its own frame imposes:

1. **Surface / atmospheric segment on a rotating body B** (launch ascent, landing,
   surface ops) -> constrains **B's rotation phase**, repeating every
   `B.rotationPeriod` (Kerbin sidereal day ~21,549 s). This is what realigns the
   segment over its ground location AND connects an ascent to its inertial orbit
   (or an orbit to its descent).

2. **Inertial orbit segment around body B** -> by itself imposes **no** phase
   constraint (B is always there; an inertial orbit is faithful at any UT). It
   inherits B's rotation constraint ONLY when an included surface / atmospheric
   segment of B is adjacent (the ascent->orbit / orbit->descent hand-off must line
   up over the launch / landing site).

3. **SOI entry into body C** (an intercept inside the included span) -> constrains
   **C's orbital phase**, repeating every `C.orbit.period` (Mun ~138,984 s). Because
   the transfer time (launch -> encounter) is fixed in the recording, aligning C's
   *mean anomaly* at the encounter is equivalent to constraining the launch UT modulo
   `C.orbit.period`. The transited bodies are read from the SOI changes (the `body`
   field across `OrbitSegments`) WITHIN the included segments only.

`P` is the joint resonance (least common period) of just the constraints the
included segments impose. An empty constraint set (e.g. looping a bare Kerbin orbit
with the ascent trimmed off) collapses `P` to `MinCycleDuration` - faithful at any
time. The constraint periods are generally **incommensurate**, so an exact common
period rarely exists; the joint period is a best-fit: the smallest `P` for which
`P mod` each constraint period is within a chosen tolerance.

### Worked examples (same recorded Mun-landing tree, different configs)

- **Launch + Kerbin orbit only** (Mun transfer / landing intervals unchecked): only
  Kerbin's rotation matters -> `P = Kerbin.rotationPeriod` (~21,549 s). The Mun is
  not in the included span, so its phase is irrelevant.
- **Full mission** (through the Mun landing): Kerbin rotation AND the Mun's orbital
  phase must both line up -> `P = joint(Kerbin rotation, Mun orbit)` - the long
  launch-window cadence.
- **Orbital coast only** (ascent + all surface segments trimmed off): no
  rotating-surface segment is included, so nothing constrains the phase ->
  `P = MinCycleDuration`, loop as fast as you like.

### Important consequences

- Because `P` is **config-dependent**, it must be recomputed whenever the included
  segments change and folded into the loop-unit rebuild signature alongside
  `ExcludedIntervalKeys` (which `BuildSignature` already hashes).
- For a config that reaches another body, the long `P` is the **physically real
  launch-window cadence** - you cannot faithfully launch to the Mun more often than
  Mun windows recur - so it is correct, not a limitation of the feature.
- This **naturally bounds the instance count**: a faithful inter-body cadence is
  long, so overlap (period < span) rarely engages and only a few instances are ever
  live. The separate "limit ghosts per render distance instead of per whole mission
  span" idea (see Relationship to other work) is therefore parked behind this;
  revisit it only if frequent same-body looping still needs it.

---

## Proposed design (two tiers)

### Tier 1 - intercept-period phase lock (the visible fix)

Compute `P` per looping mission and phase-lock the loop:

- **Config that stays in one body's SOI** (no intercept in the included span):
  `P = the launch/landing body's rotationPeriod` when an included surface /
  atmospheric segment must line up, else `MinCycleDuration` (a bare inertial orbit
  with no surface segment imposes no phase constraint).
- **Config that reaches another body:** `P` = the dominant intercept period (the
  target / last transited body's orbital period within the included span), so the
  transfer actually reaches the target. Accept a small residual launch-body-rotation
  offset (the orbit may sit slightly off the live launch site) for Tier 1; the
  intercept is the dominant visual break.

Both cases derive their bodies/frames from the INCLUDED segments (the trimmed member
set), so changing which intervals are checked re-derives `P`.

Mechanics:

- Quantize the effective loop cadence to the nearest multiple of `P` at or above
  the existing floors (`LoopTiming.MinCycleDuration` and the overlap-cap floor
  from `ComputeEffectiveLaunchCadence`).
- Align `phaseAnchor` to `UT0 + k*P` (the first faithful launch at or after the UT
  the loop was enabled) instead of the raw enable UT, so every cycle launches
  faithfully.
- Everything downstream (the span clock `TryComputeSpanLoopUT`, per-member windows,
  the map epoch-shift) is unchanged - it already replays correctly relative to
  `phaseAnchor`; we are only choosing a geometry-aware `phaseAnchor` + cadence.

### Tier 2 - joint best-fit + multi-transfer (polish)

- Joint best-fit of launch-body rotation AND target orbital phase so the orbit
  also sits over the launch site (ascent connects to the orbit).
- Multi-transfer missions (Kerbin -> Mun -> Minmus, interplanetary), where several
  bodies' phases must align: product resonance, even longer / more approximate.
- A tolerance / quality readout so the user knows how close the best-fit window is.

---

## Where it plugs into the existing code

- **`MissionLoopUnitBuilder.TryBuildMissionUnit`** already computes `spanStartUT`
  (= `UT0`), the cadence, and the `phaseAnchorUT`. This is where `P` is computed
  and where `phaseAnchor` + the quantized cadence are derived. The body sequence
  comes from the member recordings' `OrbitSegments` (`body` field) and the launch
  surface; `launchBody.rotationPeriod` and `transitBody.orbit.period` are read from
  `FlightGlobals` / the body's `Orbit` at build time.
- **`MissionLoopUnitBuilder.BuildSignature`** must fold in `P` (or its inputs) so
  the cached unit rebuilds when the body geometry or the faithful/free mode
  changes.
- **`GhostPlaybackLogic.ComputeEffectiveLaunchCadence`** (or a wrapper) gains the
  `P`-quantization step on top of the existing `MinCycleDuration` / overlap-cap
  floors.
- **`Mission`** gains the faithful/free mode flag (persisted, cloned, reset) if we
  go with a toggle (see Open questions).
- No change to the recording format, `Recording`, the span clock, the map
  epoch-shift, or the watch handoff.

---

## UX

- The Missions tab period cell currently shows the effective launch cadence
  (tinted when the overlap cap raised it). Under phase lock it would show the
  cadence snapped to a multiple of `P`, and ideally why (for example "every Mun
  window ~1.6 d").
- If we keep a faithful/free toggle, it is a per-mission control on the mission
  header row (next to Loop), defaulting to faithful.
- For inter-body missions the displayed cadence will be long; the UI should make
  clear this is the launch-window cadence, not an arbitrary cap.

---

## Key decisions (locked unless re-opened)

- **Replay as-is; do not re-aim the trajectory.** We choose WHEN to launch the
  recorded mission; we do NOT transform / re-plan the recorded inertial trajectory
  to intercept the target's *current* position. Re-aiming would mean re-solving the
  transfer per launch (a different mission each time) and is explicitly out of
  scope.
- **Read all periods at runtime** from the live bodies (`rotationPeriod`,
  `orbit.period`); never hardcode stock values. Planet packs must work.
- **Build on #958.** This depends on `MissionLoopUnitBuilder`, the span clock, and
  the map epoch-shift already shipped there. Branch base: `design-mission-abstractions`
  (rebase onto `main` after #958 merges).
- **No recording-format change.** The body sequence + `UT0` are already derivable
  from existing recorded data.

## Open questions

1. **Faithful-only vs faithful/free toggle.** Should phase lock always apply
   (cadence always snapped to `P`), or should there be a per-mission "faithful /
   free (decorative)" mode where free keeps the current arbitrary-cadence behavior
   (frequent, but trajectories do not intercept)? Default if toggled: faithful.
2. **Exact vs approximate for inter-body.** Accept an approximately-faithful window
   (small residual phase error, much more usable) or insist on exact-only (rare,
   very long `P`)? A tolerance parameter; what default?
3. **Tier 1 residual.** Is the Tier-1 launch-site offset (intercept aligned but the
   orbit slightly off the live launch site) acceptable to ship before Tier 2, or do
   we need the joint best-fit from the start for it to look right?
4. **Cadence quantization UX.** Snap the user's typed period up to the nearest
   multiple of `P` silently, show the snapped value tinted (like the overlap cap),
   or surface `P` explicitly ("next window in X")?
5. **Multi-transfer / interplanetary** missions: ship Tier 1 (lock to the final
   target's period) first and treat multi-body joint alignment as Tier 2, or scope
   it in from the start?

---

## Relationship to other work

- **PR #958 (Mission abstraction + looping)** is the base. The known limitation it
  documents (looped inter-body missions do not phase-align to the live sky) is what
  this feature closes.
- **Render-distance instance cap** ("limit ghosts per render distance, e.g. 20 per
  ~50 km / `LoopSimplifiedMeters`, instead of `span/20` in
  `GhostPlaybackLogic.ComputeEffectiveLaunchCadence`"): parked behind this. Faithful
  inter-body cadence is naturally long, so the instance count stays small without
  changing the cap. Revisit only if frequent same-body looping still needs it.

## Risks / edge cases

- **Incommensurate periods:** no exact common period; the joint best-fit needs a
  tolerance and a max-`P` search bound (continued-fraction / best-rational
  approximation of the period ratio).
- **No-target missions:** if the mission never leaves the launch body's SOI, there
  is no orbital term and `P = rotationPeriod`. Pure on-surface missions that never
  reach orbit may not even need rotation alignment (no inertial arc to connect);
  detect "has an orbital segment" before applying rotation lock.
- **Multi-SOI / gravity assists:** more than one transited body; each adds a term.
- **Tidally locked / odd-rotation bodies:** use the actual `rotationPeriod`
  (Mun's equals its orbital period); do not assume.
- **Phase-anchor re-snap on enable:** snapping `phaseAnchor` forward to the next
  faithful window means the first replay may not start immediately on enable; the
  UI should make the wait legible (or start the span clock parked until the first
  window).

## References

- `docs/dev/design-mission-abstractions.md` - the Mission hierarchy + looping this
  builds on (especially "Mission-level looping (span-clock integration)").
- `docs/dev/todo-and-known-bugs.md` - the deferred TODO + the shipped known
  limitation this closes.
- `Source/Parsek/MissionLoopUnitBuilder.cs` - `TryBuildMissionUnit` (span,
  cadence, phase anchor), `BuildSignature`.
- `Source/Parsek/GhostPlaybackLogic.cs` - `TryComputeSpanLoopUT`,
  `ComputeEffectiveLaunchCadence`, the span clock.
- `Source/Parsek/GhostMapPresence.cs` / `ParsekPlaybackPolicy.cs` - the map-presence
  loop fix (`loopEpochShiftSeconds`) that places the ghost at the right phase along
  the recorded ellipse (orthogonal to, and unaffected by, this feature).
