# M4a: the VesselOrbital constraint (Tier 1 of M-MIS-4)

*Implementation plan for the first phase of `docs/dev/design-mission-phasing-alignment.md`
(M-MIS-4): same-parent station-rendezvous phase alignment. Branch
`mission-vesselorbital-tier1`.*

**Status: IMPLEMENTED** (2026-06-10, this branch). One deviation from 2.2: the live vessel
lookup goes through `FlightRecorder.FindVesselByPid` (the exact resolution loop playback
uses) rather than a raw `FlightGlobals.FindVessel` - it carries the ghost-map-vessel guard,
so a Parsek map proto ghost can never read as a live anchor, and it is xUnit-safe.

Scope per the design note section 5 + section 9 (M4a): constraint kind + extraction flip +
solver/tolerance + period-cell label + tests. NO engine changes, NO loiter knob (M4b), NO
cross-parent station hold (M4c). Honest standalone value: the constraint layer plus
occasional aligned windows; cadence stays rare until M4b.

## 1. Behavior change in one sentence

A looping mission whose included window rendezvouses with exactly ONE same-parent
closed-orbit vessel stops being blanket-rejected (`Support.UnsupportedRendezvous`) and
instead emits a `VesselOrbital` phase constraint that the existing phase-lock / joint
best-fit / zero-drift machinery schedules against, so relaunches happen when the pad AND
the station are back in their recorded configuration; every other rendezvous shape keeps
today's fail-closed faithful path with a specific logged reason.

## 2. Code changes

### 2.1 `MissionPeriodicity.cs`: types

- `ConstraintKind.VesselOrbital`: "the included span rendezvouses with another vessel; the
  vessel must sit at its recorded-relative orbital phase: repeats every anchor-vessel orbit
  period."
- `PhaseConstraint` gains `public uint AnchorVesselPid` (0 for non-vessel kinds). `BodyName`
  holds the ORBITED body (display + same-parent checks). `RelativeToParent` stays false.
  `ToString()` renders `VesselOrbital(pid@Body)`.
- `ConstraintExtraction` gains `public string DriftAmberReason` (null = none): the D3
  drift-amber surface, display-only, never affects `Support`.

### 2.2 Seam: vessel orbit lookup on `IBodyInfo`

`IBodyInfo` (the live seam `MissionPeriodicity.cs:212`) gains:

```
/// <summary>Resolves a vessel by persistentId to its CURRENT orbit. False when the vessel
/// does not exist in the save, has no orbit, or the orbit is not closed (ecc >= 1 /
/// degenerate). periodSeconds = elliptical orbital period; orbitBodyName = the body it
/// orbits. Loaded and on-rails vessels both resolve (design note D1).</summary>
bool TryGetVesselOrbit(uint vesselPid, out double periodSeconds, out string orbitBodyName);
```

- `FlightGlobalsBodyInfo` (`:1811`): resolve via `FlightGlobals.FindVessel(pid)` (the same
  lookup loop playback uses); read `vessel.orbit` (exists for packed/on-rails too); return
  false for null orbit, non-elliptical (ecc >= 1), or non-positive/NaN period.
- Six test fakes implement `IBodyInfo` (`MissionPeriodicityTests`, `MissionZeroDriftScheduleTests`,
  `ArrivalHoldPlannerTests`, `DestinationArrivalSolverTests`, `DestinationConstraintExtractorTests`,
  `ReaimClassifierTests`): each gains the method; default `return false` (no vessels) plus a
  configurable `Dictionary<uint,(double period, string body)>` on the fakes the new tests
  use. One seam, no parallel interface.

### 2.3 Extraction flip (`ExtractConstraints` + `HasRendezvousWithinWindow`)

Replace the boolean `sawRendezvous` scan with anchor COLLECTION:

- `HasRendezvousWithinWindow` becomes `CollectVesselAnchorsWithinWindow(rec, win, anchors)`:
  for each vessel-anchored Relative section overlapping the window (same predicate as
  today: `referenceFrame == Relative && (anchorVesselId != 0 || anchorRecordingId
  non-empty)`, parent-anchored recordings still excluded), record into a caller-owned
  `Dictionary<uint, VesselAnchorInfo>` keyed by `anchorVesselId`: earliest overlap UT,
  the `anchorRecordingId` (diagnostic + drift comparison), and a flag for pid==0 sections
  (anchor-recording-only, unresolvable live).
- After the member loop, classify (new pure helper
  `TryBuildVesselOrbitalConstraint(anchors, launchBody, ut0, bodyInfo, out PhaseConstraint c,
  out string rejectReason, out string driftAmber)`):
  0. `launchBody` null (an orbit-only / degenerate config with no resolvable launch body) ->
     reject ("no launch body to phase against") - fail closed; the same-parent check is
     meaningless without it.
  1. pid==0 anchors present, or 2+ DISTINCT pids -> reject (`UnsupportedRendezvous`,
     reasons "anchor pid unrecorded" / "multiple distinct vessel anchors (multi-rendezvous)").
  2. `TryGetVesselOrbit` fails -> reject ("anchor vessel not in save / no closed orbit").
  3. `orbitBodyName != launchBody` -> reject ("cross-parent station; Tier 2 / M4c") - the
     same-parent shape per the design note (LKO resupply: station orbits the launch body).
  4. Else EMIT `VesselOrbital` (period = live period, offset = earliest rendezvous UT - ut0,
     BodyName = orbitBodyName, AnchorVesselPid = pid); `Support` stays whatever the body
     rules computed (Supported unless cross-parent SOI entries etc. flagged it).
- Multiple Relative sections to the SAME pid collapse to the earliest overlap UT (timeline
  rigidity, design note 5.2).
- Drift amber (D3): when the collected `anchorRecordingId` resolves to a committed
  recording, find its non-predicted OrbitSegment covering the recorded rendezvous UT and
  compare its period (`ReaimLoiterCompressor.OrbitalPeriod(sma, mu(body))`) to the live
  period; relative delta > `StationDriftAmberRelTolerance = 0.02` (2%; a period delta that
  accumulates a full tolerance-width of phase error within ~one cadence) sets
  `DriftAmberReason = "station orbit drifted ~X% since recording"`. No recording / no
  covering segment -> no comparison, no amber. Never affects Support or the emitted period
  (live wins, design note 3.4).
- The "rendezvous outranks cross-parent" ordering (`:460`) is preserved for REJECTED shapes;
  an EMITTED VesselOrbital does not touch Support.

### 2.4 Tolerance + scheduler integration

- `ToleranceSecondsFor` (`:1159`): add the `VesselOrbital` branch FIRST:
  `c.PeriodSeconds * (StationPhaseToleranceDegrees / 360.0)`,
  `private const double StationPhaseToleranceDegrees = 1.0` (design note 5.3). Without this
  branch the constraint would silently fall into the planetary SOI formula
  (`SoiRadius(Kerbin)/OrbitalVelocity(Kerbin)`, a wildly-wrong huge tolerance).
- `ScheduleToleranceSecondsFor` needs no change (its Loose special-case is Rotation-only;
  everything else falls through to `ToleranceSecondsFor`).
- `SelectAnchorConstraintIndex` needs no change: selection is by DUTY (tolerance/period);
  pad duty ~7.0e-4 < station duty ~2.8e-3, so the pad stays the exact-pinned anchor for the
  canonical shape, per the design.
- `Solve` (`:561`): the dominant-pick helper `IsMoreDominant` (`:801-815`,
  `candOrbital`/`curOrbital` treat "not Orbital" as Rotation-like) and the method label
  (`:646`). Decision: in dominant selection a `VesselOrbital` ranks WITH Orbital (it is an
  intercept-style constraint, and the existing preference logic between Orbital and
  Rotation should treat it as Orbital); method label gains "single-vessel-orbital". Verify
  joint best-fit (`FindBestJointMultiple`) is kind-agnostic (period-based) - expected yes;
  confirm while implementing.
- `TryBuildRelaunchSchedule` is period/tolerance-generic; no change expected beyond tests.
- `DestinationConstraintExtractor` (`Reaim/`, `:87`): kind switch currently
  Rotation-vs-else; a VesselOrbital reaching it would be misread as a destination orbital
  constraint. M4a-supported missions are same-parent (never re-aim), but add the defensive
  explicit skip (VesselOrbital -> not a destination constraint) with a comment pointing at
  M4c.

### 2.5 UI: period-cell basis label

`MissionsWindowUI.BuildPeriodBasisLabel` (~`:1567`, currently Rotation -> "(B rot)" else
"(B window)"): `VesselOrbital` renders "(station window)". The label formatter is pure
(display tests exist: `MissionsWindowPeriodicityDisplayTests`); extend it + tests.
`DriftAmberReason`: thread through the periodicity display struct
(`ComputeMissionPeriodicity` result) and extend the existing amber-tint condition
(`ShouldTintTMinusAmber`, which keys on `Solution.WithinTolerance` / schedule flags) with
`DriftAmberReason != null`, reason as tooltip - the smallest change that surfaces it.

### 2.6 Logging

- `PhaseConstraint.ToString()` covers the new kind (extraction summaries pick it up free).
- Reject reasons flow through the existing `UnsupportedReason` + `LogSummary` (one line per
  build, already suppressible).
- Drift amber logs once per transition at Info (`[MissionPeriodicity]`), keyed per mission
  tag via `ParsekLog.VerboseOnChange`-style change detection on the reason string (Info on
  set/clear; no per-frame spam since extraction is signature-gated upstream).

## 3. Tests (xUnit; all pure, fakes extended per 2.2)

`MissionPeriodicityTests` additions (`RecordingBuilder.AddTrackSection` already supports
`anchorVesselId`, verified at RecordingBuilder.cs:389/:402 - no generator work needed):

1. Single same-parent anchor, resolvable closed orbit -> `VesselOrbital` emitted (period,
   offset, pid), Support == Supported.
2. Several Relative sections to the SAME pid -> one constraint at the EARLIEST overlap UT.
3. Two distinct pids -> UnsupportedRendezvous("multiple distinct").
4. pid==0 / anchor-recording-only section -> UnsupportedRendezvous("pid unrecorded").
5. `TryGetVesselOrbit` false (vanished / hyperbolic) -> UnsupportedRendezvous.
6. Cross-parent station (orbitBodyName != launchBody) -> UnsupportedRendezvous("Tier 2").
6b. Null launch body (orbit-only config with a vessel anchor) -> UnsupportedRendezvous
    ("no launch body"), never a constraint.
7. Parent-anchored (debris) recording still never counts as rendezvous (existing rule).
8. Tolerance: VesselOrbital tolerance == period * 1deg/360; does NOT use the SOI formula.
9. `SelectAnchorConstraintIndex`: pad + station -> pad anchored (duty ordering).
10. `TryBuildRelaunchSchedule` with Rotation(pad) + VesselOrbital(station): schedule built,
    station residual within tolerance at scheduled launches (synthetic commensurate and
    incommensurate period pairs).
11. Drift amber: recorded segment period vs live period beyond/within 2% -> reason
    set/null; Support unaffected.
12. Solve: dominant pick + method label with a lone VesselOrbital and with pad+station.
13. Display: basis label "(station window)" (`MissionsWindowPeriodicityDisplayTests`).
14. Log assertions (TestSinkForTesting): extraction summary contains
    `VesselOrbital(pid@Body)`; reject paths log their reasons.

Post-change checklist: no serialized-schema change (constraints are derived, never
persisted; `ConfigNode` untouched), no ParsekScenario OnSave/OnLoad impact. Full
`dotnet test` green.

## 4. Risks / watch items

- **Behavior flip for existing saves**: a previously-faithful looping rendezvous mission
  starts phase-locking after this change. Intended (the milestone's purpose) and amber/
  label-visible; first play unaffected; fail-closed shapes unchanged.
- **`FindVessel` semantics**: must resolve unloaded vessels; verify the exact
  `FlightGlobals.FindVessel(pid)` overload behavior at implementation time (loop playback
  already relies on the same resolution).
- **Routes**: route-backing missions run the same builder; a route on a rendezvous tree
  gains scheduling. Delivery markers are phase-independent (design note section 7), so
  this only improves the visual; no route code changes.
- **Solve dominant-pick audit** (2.4) is the one place a silent misclassification could
  hide; covered by test 12.

## 5. Docs (same commit)

- `CHANGELOG.md` (Unreleased): looped missions that rendezvous with a station now relaunch
  phase-locked to the station's orbit (one line, user-facing).
- `docs/dev/todo-and-known-bugs.md`: M-MIS-4 entry gains "M4a SHIPPED (branch ...)" with
  the emitted/rejected shape summary.
- `docs/dev/design-mission-phasing-alignment.md`: status header notes M4a implemented.
- This plan.
