# M4c - Cross-parent station alignment (Tier 2 of M-MIS-4)

Status: IMPLEMENTED (2026-06-12, this branch). Implementation notes vs the plan: the
builder E2E tests (section 5 tests 14/15) and the signature digest test (test 17) live in
`MissionPeriodicityTests` rather than `MissionLoopUnitBuilderTests` - the Build-chain
fixtures (SurfaceLeg/OrbitLeg/WithSoiEntry/FakeBodyInfo.VesselOrbits) are there; the
existing `ShouldTintTMinusAmber_NotPhaseLocked_NeverTints` stays as-is (its inputs remain
true under the new gates) with the new bypass polarities pinned in two new tests.
Validation pending the maintainer's cross-parent playtest save. Parent design:
`docs/dev/design-mission-phasing-alignment.md` section 6 + decisions D8/D9. Prior phases:
M4a (`VesselOrbital` Tier 1, PR #1119, plan `docs/dev/plans/mission-vesselorbital-tier1.md`)
and M4b (per-cycle phasing-loiter knob, PR #1125, plan
`docs/dev/plans/mission-loiter-knob.md`) are merged. The shipped per-loop arrival hold
(`Reaim/ArrivalHoldPlanner.cs`, wired in `MissionLoopUnitBuilder.cs:536`, clock consumption
`GhostPlaybackLogic.ApplyArrivalHoldToPhase` / `ComputePerLoopArrivalHoldSeconds`) is the
substrate; plan `docs/dev/plans/reaim-destination-arrival-alignment.md`.

## 1. Behavior change in one sentence

A looping mission that rendezvouses with a station orbiting a body OTHER than the launch
body stops being blanket-rejected (`UnsupportedRendezvous` "Tier 2 / M4c"): a station around
a TRANSITED body now emits the same `VesselOrbital` constraint M4a built, which on a
same-parent mission (Mun/Minmus depot) feeds the existing zero-drift schedule + M4b knob
unchanged, and on a re-aim mission (Duna depot) drives the shipped per-loop arrival hold
with T_station substituted for T_rot - while a destination carrying BOTH a landing-rotation
constraint and a station constraint fails closed (amber + faithful, design D8), and every
other unsupported shape keeps today's fail-closed path with a logged reason.

## 2. Current state (verified against the code, 2026-06-12, main @ f8e136742)

- `MissionPeriodicity.ClassifyVesselOrbitalConstraint` (`MissionPeriodicity.cs:1862`)
  resolves anchors (self-partition, anchor-recording-id -> pid + launch guid,
  `TryGetVesselOrbit` guid identity gate) and REJECTS at rule 3 when
  `orbitBodyName != launchBody` with reason "...cross-parent station; Tier 2 / M4c".
  The reject sets `Support.UnsupportedRendezvous` at the `ExtractConstraints` step-5 call
  site (`:515-519`); an EMIT never touches Support (`:510-514`).
- For a cross-parent mission, Support is already `UnsupportedCrossParent` (set by the
  step-4 Orbital scan, `:489-495`) BEFORE step 5 runs. `Solve` returns
  `ShouldPhaseLock == false` for any non-Supported config, so the builder's re-aim branch
  (`MissionLoopUnitBuilder.cs:338`, gated on `!phaseLocked`) runs for BOTH
  `UnsupportedCrossParent` and `UnsupportedRendezvous`. `ReaimClassifier` reads only
  OrbitSegments, never Support, so a rendezvous reject does not stop the transfer re-aim -
  today the Duna-depot mission already re-aims its transfer but NOTHING aligns the station
  (orbit-only arrival: `destSet.HasLandingRotation` false -> `ComputeArrivalHold` returns
  None at `ArrivalHoldPlanner.cs:61-62`).
- `DestinationConstraintExtractor.ExtractDestinationConstraints` defensively SKIPS
  `ConstraintKind.VesselOrbital` (`DestinationConstraintExtractor.cs:92`, the M4a comment
  pointing here). Its only production caller is `ArrivalHoldPlanner.ComputeArrivalHold`
  (`:57-58`); `DestinationArrivalSolver.SolveArrivalWindow` exists but is UNWIRED and
  stays unwired (D8: post-M4c follow-up, do not wire).
- The hold pipeline: `ComputeArrivalHold` gates (Drop mode -> None; unsupported dest ->
  None; no landing rotation -> None; destination-side loiter cuts -> None at `:70-75`,
  the L8 rigidity guard), reads `tRot = bodyInfo.RotationPeriod(targetBody)`, computes
  `w = ComputeArrivalAlignHoldSeconds(recordedArrivalUT, liveEntryUT, tRot)`. The builder
  threads the result into `LoopUnit` (`arrivalHold.HoldSeconds/HoldAtUT/
  RotationPeriodSeconds` -> `ArrivalHoldSeconds/ArrivalHoldAtUT/DestRotationPeriodSeconds`,
  `MissionLoopUnitBuilder.cs:573-574`). The span clock applies the per-loop drift
  correction `ComputePerLoopArrivalHoldSeconds(w0, cycleIndex, cycleDuration, period)`
  (`GhostPlaybackLogic.cs:7410`) and inserts the hold via `ApplyArrivalHoldToPhase`
  (`:7465`). All three pure helpers are PERIOD-AGNOSTIC already - only the period
  SELECTION is rotation-specific.
- `LoopUnit.DestRotationPeriodSeconds` consumers: `GhostPlaybackEngine.cs:372/1883/2284`,
  `ParsekKSC.cs:1191`, `GhostPlaybackLogic.TryComputeSpanLoopUT` /
  `ComputeNewestMissionInstanceSpanLoopUT` (`arrivalHoldRotationPeriod` params,
  `:7284/:7591`). LoopUnits and schedules are TRANSIENT (rebuilt per build, never
  persisted) - no OnSave/OnLoad impact anywhere in this plan.
- UI: `MissionsWindowUI.ComputeMissionPeriodicity` re-runs Extract+Solve per frame
  (suppressed logging) AND resolves the REAL unit via `TryResolveMissionUnit`
  (`MissionsWindowUI.cs:1271`). The T- amber tint `ShouldTintTMinusAmber` (`:1408`)
  early-returns false when `!IsPhaseLockedConstrained` - which is ALWAYS false for re-aim
  missions - so the M4a `DriftAmberReason` channel alone cannot surface a re-aim-side
  amber; D8 needs a unit-carried reason (section 3.5).
- `TransitedBodyRotationMode` default is LOOSE (`ParsekSettings.cs:174`); Drop is the
  player's explicit "Off (frequent)" choice for transited-body ROTATION alignment. The
  unwired `DestinationArrivalSolver` drops ONLY transited-rotation constraints under Drop
  (`DestinationArrivalSolver.cs:107`), keeping orbital ones - precedent that Drop is a
  rotation-scoped switch, not a global alignment kill (section 3.3 decision).

## 3. Code changes

### 3.1 `MissionPeriodicity.cs`: classifier rule-3 split (the extraction flip)

`ClassifyVesselOrbitalConstraint` gains one parameter: the already-built constraint list
(`IReadOnlyList<PhaseConstraint> bodyConstraints` - at the step-5 call site this is
`result.Constraints`, complete after steps 3-4). Rule 3 becomes:

- `orbitBodyName == launchBody` -> emit (M4a, byte-identical).
- `orbitBodyName` has an emitted `Orbital` constraint (the mission's included window
  actually transits the station's body) -> EMIT the same `VesselOrbital` constraint
  (BodyName = orbitBodyName, period = live station period, offset = earliest rendezvous
  UT - ut0, pid). Support is NEVER touched on emit, so:
  - same-parent destination (Mun/Minmus depot): Support stays Supported -> phase-lock ->
    the zero-drift schedule + M4b knob solve pad + Orbital(B) + VesselOrbital jointly.
    NO new scheduling code: `ToleranceSecondsFor` already branches VesselOrbital first
    (period * 3deg/360), the knob's shiftable partition is offset-based and the
    rendezvous offset is after the phasing run by construction, `SelectAnchorConstraintIndex`
    keeps the pad anchored by duty, dominant-pick ranks VesselOrbital with Orbital (the
    longer-period Orbital(B) stays dominant, basis label "(B window)").
  - cross-parent destination (Duna depot): Support stays UnsupportedCrossParent -> the
    re-aim branch runs as today, and the emitted constraint reaches
    `ArrivalHoldPlanner.ComputeArrivalHold` through `extraction.Constraints` (3.2/3.3).
- else -> reject (fail closed), new reason: "rendezvous anchor pid=N orbits 'B', for
  which the mission emitted no Orbital constraint (not transited, or degenerate orbit
  data)". This covers the incoherent-trim shape (orbit segments of B excluded,
  rendezvous window kept) and the degenerate-period skip at `:473-478` - the reason
  wording covers both (a Sun-orbiting depot IS transited on every transfer but
  `OrbitPeriod(Sun)` is degenerate, so no Orbital(Sun) exists; review finding 9).

The anchor policy is UNCHANGED and applies to the new shape for free: the classifier
resolves the anchor BEFORE rule 3 (rules 1-2: self-partition, anchor-recording-id ->
recorded pid + launch guid, `TryGetVesselOrbit(pid, guid, ...)` live + closed-orbit +
launch-identity gate, vanished anchors -> Rejected -> faithful). A vanished cross-parent
station therefore degrades exactly per D1: `UnsupportedRendezvous`, re-aim transfer still
engages, no station constraint, no station hold, approach member retires via the
loop-anchor-unloaded contract.

The M4a "rendezvous reject outranks cross-parent for the report" ordering is preserved
(step 5 still overwrites Support only on Rejected).

### 3.2 `DestinationConstraintExtractor.cs`: the station shape + D8

`DestinationConstraintSet` gains:

- `public bool HasStation` - a `VesselOrbital` constraint whose `BodyName == targetBody`
  (the station orbits the re-aim destination).
- `public double StationPeriodSeconds` - that constraint's live `PeriodSeconds` (NaN when
  none).
- `public uint StationAnchorPid` - for logging.

Selection loop changes:

- `VesselOrbital` with `BodyName == targetBody` -> the station constraint (at most one
  exists: the classifier's exactly-one-foreign-anchor rule). NOT added to `Constraints`
  (that list stays "DestRotation + 0/1 MoonConfig, in solver order" for the unwired
  `SolveArrivalWindow`; its contract is untouched per D8).
- `VesselOrbital` orbiting a MOON of the target (`ReferenceBodyName(BodyName) ==
  targetBody`, e.g. an Ike depot on a Duna mission) -> a DESTINATION-SYSTEM station the
  hold cannot align (the hold aligns phases of period T measured at the SOI entry; a
  moon-orbiting station's recorded-relative geometry depends on the moon phase AND the
  station phase jointly): `Supported = false`, `HasStation = true` (so the amber fires,
  3.3), reason "station orbits 'M', a moon of destination 'B': in-system station
  alignment deferred". Review finding 8: the player-visible symptom is the same
  approach teleport D8 ambers for, so silent-skip would be inconsistent.
- Any other `VesselOrbital` (e.g. a launch-side Kerbin fuel depot on a re-aim mission)
  -> skipped as today (not a destination-system constraint), counted into the
  `dest-constraints` log line (`nonDestStations=N`). The launch-side depot phase is
  NOT aligned by the re-aim path (no machinery for it; the hold aligns the destination
  only) - a documented, logged, fail-open-to-faithful sub-shape.

Station fields (`HasStation`/`StationPeriodSeconds`/`StationAnchorPid`) are populated
IN THE LOOP alongside the existing `HasLandingRotation`/`ConstrainedMoonCount`
assignments, BEFORE any early return, so a station-bearing Jool-class destination still
carries them (the amber in 3.3 reads them off the failed set; review finding 3).

D8 fail-closed, evaluated after the loop; the existing `moonConfigs.Count >
MaxConstrainedMoons` Jool-class return stays FIRST and unchanged (its reason wins the
amber text for a station-bearing Jool destination), then:

- `HasStation && HasLandingRotation` -> `Supported = false`, reason
  "landing rotation + station rendezvous at 'B': no single arrival hold aligns both
  periods (deferred)".
- `HasStation && ConstrainedMoonCount > 0` -> `Supported = false`, reason
  "station rendezvous + constrained moon SOI at 'B': no single arrival hold aligns both
  periods (deferred)". This widens D8's letter (which names only landing+station) but
  follows its exact rationale: ONE hold aligns ONE period; a constrained moon is a second
  destination-side period the same way a landing rotation is. The shipped
  landing+moon-no-station combo (Duna One: hold T_rot, Ike rides the Duna-synchronous
  resonance) is NOT touched - byte-identical, regression-pinned (section 5).

`TransitedBodyRotationMode.Drop` does NOT rescue the dual case: D8's letter (dual ->
fail closed) wins over the 3.3 rotation-scoped-gate reasoning even though Drop disables
the only conflicting alignment - the extractor is mode-blind by design, and making D8
mode-aware is exactly the SolveArrivalWindow territory deferred post-M4c (review
finding 4; pinned by a Drop+dual test).

### 3.3 `ArrivalHoldPlanner.cs`: period selection + the Drop-gate refinement

`ArrivalHoldResult`: `RotationPeriodSeconds` RENAMED `AlignPeriodSeconds` (it now carries
T_station for station holds; misleading names are a documented project trap), plus
`public string AmberReason` (null = none; the D8 surface, set ONLY when a station
constraint is present but the hold fails closed on the dual-constraint rules) and
`public bool IsStationHold` (logging/display discrimination).

`ComputeArrivalHold` flow becomes:

1. `bodyInfo == null || targetBody empty || NaN inputs` -> None (unchanged).
2. Extract `destSet` (unchanged call).
3. `!destSet.Supported`:
   - if `destSet.HasStation` -> None WITH `AmberReason = destSet.Reason` (the D8 dual
     cases, the moon-orbiting destination station, and a station-bearing Jool-class
     destination all surface amber);
   - else -> None silently (the pre-existing Jool-class no-station path, byte-identical -
     no new amber for shapes M4c does not touch).
4. Pick the alignment target:
   - `destSet.HasStation` -> `tAlign = destSet.StationPeriodSeconds`. The Drop-mode gate
     does NOT apply: `TransitedBodyRotationMode` is the transited-body ROTATION alignment
     A/B (its Drop = "Off" refers to rotation handoff seams; the unwired solver's Drop
     handling at `DestinationArrivalSolver.cs:107` likewise drops only rotation
     constraints), and the station hold is a NEW automatic alignment with no toggle per
     design D4. Deliberate refinement of the literal "substitute T_station for T_rot"
     wording - flagged for review.
   - else `destSet.HasLandingRotation && mode != Drop` -> `tAlign =
     bodyInfo.RotationPeriod(targetBody)` (the shipped path; Drop still returns None for
     rotation holds, byte-identical).
   - else -> None.
5. Destination-side loiter-cut rigidity guard: UNCHANGED, applies to BOTH kinds (a
   destination-side cut breaks the entry-referenced hold regardless of which period it
   aligns; the M-MIS-2 P4 re-timer replaces this refusal later, not here).
6. Degenerate `tAlign` (NaN/Inf/<=0) -> None. `w = ComputeArrivalAlignHoldSeconds(
   recordedArrivalUT, liveEntryUT, tAlign)` -> result as today, with
   `AlignPeriodSeconds = tAlign`, `IsStationHold` set.

Note the gate ORDER change: the current code returns None on `mode == Drop` before
anything else; the mode test moves into step 4's rotation branch. Pinned by tests
(Drop + station-only -> hold computed; Drop + landing-only -> None, byte-identical).

### 3.4 Plumbing renames + the amber field (`GhostPlaybackLogic.cs`, engine, KSC, builder)

- `LoopUnit.DestRotationPeriodSeconds` -> `ArrivalAlignPeriodSeconds` (constructor param
  `destRotationPeriodSeconds` -> `arrivalAlignPeriodSeconds`); doc comment rewritten for
  the two kinds. Consumers updated mechanically: `GhostPlaybackEngine.cs:372/1883/2284`,
  `ParsekKSC.cs:1191`, the `TryComputeSpanLoopUT` (`:7284`) and `DecideUnitMemberRender`
  (`:7591`) params (`arrivalHoldRotationPeriod` -> `arrivalHoldAlignPeriod`), the
  `ResolveTrackingStationSampleUT` pass-through (`:7661`), the W_N rate-limited log line
  (`:7427` "Trot=" -> "Talign="), and the NAMED-argument call sites in
  `ArrivalAlignHoldTests.cs:355/374/396/410` (`arrivalHoldRotationPeriod:` breaks
  compilation on rename; review finding 5). The pure helpers
  (`ComputeArrivalAlignHoldSeconds`, `ComputePerLoopArrivalHoldSeconds`,
  `ApplyArrivalHoldToPhase`) are untouched - already period-agnostic.
- `LoopUnit` gains `internal string ArrivalAmberReason { get; }` (optional ctor param,
  default null) - the D8 display surface, transient like every other unit field.
- `MissionLoopUnitBuilder`: threads `arrivalHold.AmberReason` into the unit; the ARRIVAL
  HOLD Info line gains the kind + period
  (`kind={station|rotation} Talign=...s pid=...`); a NEW transition-logged Info line for
  the amber (M4a `LogDriftAmberTransition` pattern: static last-reason dict keyed by
  TREE ID - mission names are player-renamable via `MissionGroupLink`, so a name key
  would leak entries and fire spurious transitions on rename (review finding 10) - plus
  `ResetForTesting`, set/clear lines, suppressed builds neither log nor consume):
  `"Arrival amber SET: tree=T mission='X' <reason>"` / `"Arrival amber CLEARED: ..."`.

### 3.5 UI (`MissionsWindowUI.cs`): the D8 amber surface

- `MissionPeriodicityDisplay` gains `public string ArrivalAmberReason`, populated from
  the resolved unit (`result.UnitBuilt ? unit.ArrivalAmberReason : null`) - NOT from
  extraction, so the amber appears exactly when a real re-aim unit fails closed on D8
  (a mission whose re-aim declines entirely shows no misleading arrival amber).
  `TryResolveMissionUnit` rebuilds via `MissionLoopUnitBuilder.Build` with
  `FlightGlobalsBodyInfo.Instance` + the settings' TBR mode - the same inputs as the
  scene drivers - so the resolved unit deterministically carries the same amber
  (verified, `MissionsWindowUI.cs:488-528`).
- `ShouldTintTMinusAmber` gains `string arrivalAmberReason = null, bool isReaimUnit =
  false`. Logic: non-null arrival amber -> true (checked FIRST, before the
  `IsPhaseLockedConstrained` early-out, which is false for every re-aim mission); then
  drift amber -> true when `isPhaseLockedConstrained || isReaimUnit` - M4c makes
  `DriftAmberReason` reachable on re-aim missions for the first time (a drifted Duna
  depot emits, then drift-compares), and tooltip-without-tint there would repeat the
  exact inconsistency this section fixes for arrival amber (review finding 7); then the
  existing phase-locked gates byte-identical.
- Tooltip: the T- cell tooltip joins `DriftAmberReason` and `ArrivalAmberReason` with
  "; " when both are set (drift amber can legitimately coexist on an emitted-then-dual
  config).
- No new controls, no basis-label change: the same-parent depot's dominant constraint is
  the longer-period Orbital(B) ("(B window)" label), the re-aim cell keeps its
  "(<target> transfer)" basis (design 5.4 / D4).

### 3.6 Build signature: the anchor orbit identity (closing an M4a design debt)

Design section 8 requires "build-signature inputs grow (anchor orbit identity)"; M4a
never implemented it - `BuildSignature` / `AppendTransitedBodyDigest`
(`MissionLoopUnitBuilder.cs:726-868`) fold in only celestial-body geometry, and
`TryGetVesselOrbit` appears nowhere outside extraction (review finding 1, MAJOR). So a
boosted station changes NO signature input and the cached unit keeps a stale T_station
(schedule on M4a shapes, hold on M4c shapes) until an unrelated input moves.

Fix: a new `AppendStationAnchorDigest(sb, loopTree, committed, bodyInfo, ic)` appended
next to the transited-body digest (bodyInfo non-null only). Bounded per-tree scan:
collect the distinct anchor identities from the tree recordings' Relative TrackSections
(`anchorVesselId` != 0 -> (pid, null); `anchorRecordingId` -> the committed recording's
recorded pid + launch guid), then per distinct pid one
`TryGetVesselOrbit(pid, guid, ...)` probe, appending
`S:pid=<found>,<period quantized "F0">,<body>;`. Quantizing the period to whole seconds
prevents cache-busting churn from a LOADED vessel's per-frame numeric orbit noise while
catching any real boost (the station tolerance is ~20s at 3 degrees; the drift amber
threshold ~36s at 2% of an 1800s orbit). Residual accepted churn: a LIVE vessel matching
an anchor pid under active thrust sweeps integer-second period boundaries for the burn
duration - transient, bounded, and only while that tree loops (comment at the site).
A vanished/recovered station flips `<found>` -> rebuild -> the classifier rejects ->
faithful, closing the stale-hold-after-recovery hole the same way.

### 3.7 Logging (design section 8)

- Classifier emit/reject lines: free via `PhaseConstraint.ToString()` +
  `UnsupportedReason` + `LogSummary` (one line per build). New reject reason string per
  3.1.
- `dest-constraints` line gains `station=<pid>@<body> T=<period>` / `nonDestStations=N`.
- ARRIVAL HOLD line gains kind/period/pid (3.4). W_N rate-limited line relabeled
  "Talign=".
- Amber transitions once per change at Info (3.4). All numeric formatting
  InvariantCulture.

## 4. What does NOT change

- Recorded data (immutable, all re-timing loop-clock-side), first play (faithful floor),
  loop playback's live-PID anchor contract (D2) and unresolvable-anchor retire/skip.
- The re-aim transfer machinery (classifier, window planner, pad-align, loiter
  compression) and the M4b knob/schedule internals - shape A rides them with ZERO
  scheduling-code changes.
- `DestinationArrivalSolver.SolveArrivalWindow` stays UNWIRED (D8); its
  `DestinationConstraintSet.Constraints` input contract is untouched.
- The shipped landing-only hold (Duna One), including landing+moon: byte-identical.
- Route delivery semantics (`RecordedDockUT` marker, phase-independent).
- No serialized schema, no OnSave/OnLoad, no settings.

## 5. Tests (xUnit; pure, existing fakes extended)

`MissionPeriodicityTests` (M4c section):

1. Cross-parent TRANSITED station (mission has Orbital(B); station orbits B) -> EMITTED:
   VesselOrbital(BodyName=B, live period, pid, offset), Support REMAINS
   UnsupportedCrossParent (cross-parent dest) / Supported (same-parent dest, separate
   test) - the load-bearing "emit never touches Support" invariant, both polarities.
2. Station orbiting a NON-transited body -> Rejected with the new "never transits"
   reason (the existing `Extract_CrossParentStation_UnsupportedRendezvous` test at
   `:1064` updates to assert the new reason; its tree never transits Mun so it stays a
   reject). Same for the `LogSummary` reject-reason assertion (`:1311`).
3. M4a same-parent regression suite: green unchanged (rule-3 first branch identical).
4. Anchor policy on the new shape: vanished anchor / different-launch guid / anchor
   recording unresolvable -> Rejected (reuse the M4a fakes with `body: "Duna"` +
   an added Orbital(Duna) leg).
5. Same-parent depot schedule integration: pad Rotation(Kerbin) + Orbital(Mun) +
   VesselOrbital(@Mun) -> `TryBuildRelaunchSchedule` builds; scheduled launches keep the
   station residual within `ToleranceSecondsFor(VesselOrbital)`; with an LKO phasing run
   the knob enumerates d against BOTH shiftable constraints (extend an existing M4b knob
   test with the third constraint).

`DestinationConstraintExtractorTests`:

6. VesselOrbital@target -> HasStation + StationPeriodSeconds + pid; Constraints list
   unchanged (no station inside).
7. VesselOrbital@launch-side body -> skipped, `nonDestStations` logged, no station
   fields; VesselOrbital@moon-of-target -> Supported=false + HasStation + the
   "in-system station alignment deferred" reason.
8. D8: station + landing -> Supported=false, reason contains "no single arrival hold";
   station + moon -> Supported=false; landing + moon, NO station -> Supported
   (Duna One regression pin); station alone -> Supported + HasStation; station-bearing
   Jool-class (2+ moons) -> Supported=false with the Jool reason AND HasStation still
   true (fields populated before the early return).

`ArrivalHoldPlannerTests`:

9. Station-only destination -> hold computed from T_station: poison the fake's
   `RotationPeriod(targetBody)` (e.g. 999999) and assert the hold equals the T_station
   modular distance, `AlignPeriodSeconds == T_station`, `IsStationHold`.
10. D8 dual -> None + `AmberReason` set; Jool-class no-station -> None + AmberReason
    NULL (no new amber for untouched shapes).
11. Destination-side loiter cut + station -> None (rigidity guard covers the new kind).
12. Drop mode: station-only -> hold STILL computed; landing-only -> None (gate moved,
    not lost); Drop + station + landing (dual) -> None + AmberReason set (D8's letter
    wins, Drop does not rescue); degenerate T_station (NaN/0/negative) -> None.
13. Per-loop substitution: `ComputePerLoopArrivalHoldSeconds` with T_station (the
    existing math tests cover the period-agnostic formula; one test documents the
    station semantics end-to-end through `TryComputeSpanLoopUT` with
    `arrivalHoldAlignPeriod = T_station`, extending `ArrivalAlignHoldTests`; the four
    named-argument call sites there rename in the same commit).

`MissionLoopUnitBuilderTests` (review finding 2 - the headline chain gets an
end-to-end pin; the integration-only hazard is `plan.TargetBody` (ReaimClassifier)
vs the station constraint's `BodyName` (TryGetVesselOrbit) naming mismatch silently
no-opping the hold):

14. Builder E2E happy path: Kerbin launch + Sun coast + Duna capture segments +
    station@Duna in the body fake -> the built unit has `ArrivalHoldSeconds > 0`,
    `ArrivalAlignPeriodSeconds == T_station`, `ArrivalAmberReason == null`, and the
    ARRIVAL HOLD kind=station log line fired.
15. Builder E2E dual variant: same + a Duna landing leg (surface + orbit segments of
    Duna) -> hold 0, `ArrivalAmberReason` set on the unit, amber SET transition logged.

`MissionScheduleGuardTests` (NOT MissionsWindowPeriodicityDisplayTests - that file has
no ShouldTintTMinusAmber tests; review finding 6):

16. `ShouldTintTMinusAmber`: arrivalAmberReason non-null -> true even when
    `isPhaseLockedConstrained` false (the re-aim case); drift amber + isReaimUnit ->
    true; drift amber + neither phase-locked nor re-aim -> false; all-null -> existing
    behavior byte-identical. Extend/rename `ShouldTintTMinusAmber_NotPhaseLocked_NeverTints`
    (`:211-219`) so its name matches the new contract.

`MissionLoopUnitBuilderTests` signature digest (3.6):

17. Station anchor digest: same inputs -> identical signature; station period changed
    past 1s -> different; station removed from the fake (vanished) -> different;
    a non-looping tree's anchors contribute nothing.

Log-assertion tests (`ParsekLog.TestSinkForTesting`, `[Collection("Sequential")]` where
shared statics are touched): the amber transition set/clear lines (tree-id keyed); the
ARRIVAL HOLD kind=station line; the new classifier reject reason.

In-game: no new runtime test this phase - the live `TryGetVesselOrbit` seam is already
covered by the M4b MissionPhasing test, and a synthetic cross-parent station scenario
needs generator work the milestone does not justify (M4b precedent: note the gap).
Validation is the maintainer's cross-parent playtest save (station in Mun or Duna orbit
+ recorded Kerbin resupply that transfers, rendezvouses, docks).

## 6. Risks / watch items

- **Station-hold body-fixed rotation artifact:** the rotation hold makes body-fixed
  in-SOI arcs land at the recorded rotation phase BY CONSTRUCTION; a station hold instead
  leaves the destination's rotation arbitrary at the deferred replay, so body-fixed
  vacuum-burn point arcs (the M4b derotation discovery) render rotated by
  ((liveEntry + W_N) - recordedArrivalUT) mod T_rot, varying per loop. D8 keeps landings
  out, so the exposure is burn-arc map legs + markers only; the recorded approach itself
  is Relative (follows the live station). ACCEPTED for v1, playtest watch item; the
  candidate follow-up is extending the M4b body-fixed derotation channel to hold-shifted
  re-aim units (out of scope here).
- **Drop-gate refinement (3.3)** is a deliberate deviation from the literal D8 wording -
  reviewer should sanity-check the reasoning (rotation-scoped setting, D4 automaticity,
  DestinationArrivalSolver precedent).
- **D8 widening to station+moon (3.2)** - same rationale, same fail-closed direction;
  reviewer sanity-check.
- **RESOLVED by the post-M4c follow-up (2026-07-07, branch `mmis4-solve-arrival-window`):**
  the alignment-LOSING landing+station flip below is closed - the D8 dual now takes the
  JOINT arrival hold (SolveArrivalWindow wired; see the design doc D8 row and the M-MIS-4
  entry in todo-and-known-bugs.md).
- **Existing-save behavior flip:** previously-rejected cross-parent station missions
  start aligning (schedule or hold) after this change. Intended (the milestone's
  purpose); fail-closed shapes keep today's behavior; first play untouched. ONE
  alignment-LOSING direction exists (post-implementation review finding 1): a
  landing+station destination previously got the ROTATION hold (the old classifier
  rejected the station, so the extractor saw landing-only); under D8 it now gets NO hold
  plus the amber. Mandated by D8's letter ("amber + faithful") and test-pinned; the
  post-M4c SolveArrivalWindow follow-up is the path to aligning both.
- **T_station freshness:** WITHOUT 3.6 the build signature contains no station-orbit
  input (review finding 1: `BuildSignature` folds in only celestial geometry), so a
  boosted station would keep a stale hold/schedule until an unrelated input moved; 3.6
  closes this by folding the resolved anchor's live orbit identity into the per-tree
  digest. Drift amber (M4a) independently covers live-vs-RECORDED divergence. Note the
  design's own D3 limit stands: a boost that changes the station's PHASE but not its
  PERIOD is invisible to both surfaces (period-only comparisons) - the locked design's
  model, no action.
- **The classifier signature change** is internal; no test calls it directly (verified -
  tests go through `Extract`). Known breaking tests:
  `Extract_CrossParentStation_UnsupportedRendezvous` (`MissionPeriodicityTests.cs:1064`,
  reason assertion updates - its tree never transits Mun, so it stays a reject),
  `Extract_RejectedRendezvous_LogsReason` (`:1308`, same), and the
  `ArrivalAlignHoldTests` named-argument compile breaks (5/13 above). Nothing in
  `ArrivalHoldPlannerTests` or `DestinationConstraintExtractorTests` breaks
  (`Off_Drop_ReturnsNone` is landing-only and stays green under the moved gate; no test
  reads `RotationPeriodSeconds` off the result).

## 7. File touch list

- `Source/Parsek/MissionPeriodicity.cs` - rule-3 split + new reject reason + call-site
  param.
- `Source/Parsek/Reaim/DestinationConstraintExtractor.cs` - station fields + D8 + log.
- `Source/Parsek/Reaim/ArrivalHoldPlanner.cs` - period selection, gate order, renames,
  AmberReason.
- `Source/Parsek/GhostPlaybackLogic.cs` - LoopUnit renames + ArrivalAmberReason; clock
  param renames (`TryComputeSpanLoopUT:7284`, `DecideUnitMemberRender:7591`,
  `ResolveTrackingStationSampleUT:7661`); W_N log label.
- `Source/Parsek/GhostPlaybackEngine.cs`, `Source/Parsek/ParsekKSC.cs` - mechanical
  rename sites.
- `Source/Parsek/MissionLoopUnitBuilder.cs` - amber threading + hold-line kind + amber
  transition log (tree-id keyed) + `AppendStationAnchorDigest` (3.6).
- `Source/Parsek/UI/MissionsWindowUI.cs` - display field + tint + tooltip join.
- `Source/Parsek.Tests/` - per section 5 (`MissionPeriodicityTests`,
  `DestinationConstraintExtractorTests`, `ArrivalHoldPlannerTests`,
  `ArrivalAlignHoldTests`, `MissionLoopUnitBuilderTests`, `MissionScheduleGuardTests`).
- `CHANGELOG.md`, `docs/dev/todo-and-known-bugs.md` (M-MIS-4 entry), design doc status
  header, this plan.
