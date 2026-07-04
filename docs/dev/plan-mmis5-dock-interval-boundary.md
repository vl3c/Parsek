# Plan: M-MIS-5 - Dock as an interval boundary + docked composition counts (START-TRIM-ONLY)

Status: RATIFIED (review-folded); P1 IMPLEMENTED 2026-07-04 on branch
`mmis5-dock-boundary` (P2a / P2b remain). All file:line
references verified against `main @ 7db479d94` (2026-07-04). Roadmap entry:
`docs/dev/todo-and-known-bugs.md:455-460` (M-MIS-5) + `:1660-1668` (gaps 1+2);
design doc: `docs/parsek-missions-design.md:409-410` (section 14.2).

## Review log

Clean-context plan review completed 2026-07-04 against `main @ 7db479d`; verdict
READY-WITH-EDITS (full verdict: `mmis5-plan-review-verdict.md`). All required edits
folded into this draft:

- C2 (blocker): D2 now subtracts structural-peel crew on rebased bases, with the named
  test `Undock_AfterDock_RemovesDepartingCrewFromRoster`; additive-fallback
  start-composition caveat acknowledged.
- C3a/C3b/C3c: "renumbering is impossible" claim rescoped; generation stamping made
  unconditional with constructor-default generation 1; extension ordered against the
  stale-dropper's valid-key set.
- C4/OQ2: D4 claim (c) rewritten honestly (realized cycle re-aligns to DispatchInterval;
  sub-span-cadence routes deliver more often); CHANGELOG sentence + in-game two-delivery
  timing check added; OQ2 recorded DECIDED (ship in P1).
- Missed-item 4: route test fixtures re-pointed at DOCK UTs before asserting (the
  undock-UT equivalence expires with M-MIS-5).
- Missed-item 5: SuppressLogging wrap sites enumerated in P1 task 2.
- C5: byte-identical pin test also pins vesselWindowCount + member-window values.
- C6: P2a detector scoped across both undocked-start reject sites.
- C7: ScenarioWriter note corrected (no mission writer exists in Generators today).
- OQ1 recorded RECOMMENDED-ORIGIN-UNDOCK (final decision stays with the P2b mini-plan).
- C1 confirmations (claw couple, optimizer BP guard, BG-to-BG scope) carried into D1's
  evidence so the build phase does not re-litigate them.

Ratified scope (recorded, not relitigated here):

- START-TRIM ONLY: emit the dock-merge interval boundary and fix the docked-composition
  label undercount, so a mission / route selection can START at a dock inside a
  recording tree's composition timeline.
- PARKED (out of scope): looping the ISOLATED docked A-B stretch (both pre-dock and
  post-undock excluded). After P1 the selection becomes mechanically expressible via the
  ordinary interval checkboxes; this plan adds no dedicated handling, validation, or
  tests for that shape, and no behavior is promised for it.
- Keep the recorded decision "no auto Mission lifecycle on dock"
  (`docs/dev/todo-and-known-bugs.md:458`): topology-only, zero store lifecycle changes.
- Downstream payoff: lift / narrow the logistics M4 documented limitation
  `MidRecordingStartTrimUnsupported = 9` (`Logistics/RouteAnalysisEngine.cs:67`).
  Phasing: P1 (Missions model) shippable alone; P2 (logistics lift) dependent.
- Constitutional gate: any mission whose selection does not use a dock boundary -
  especially the default whole-journey selection with empty `ExcludedIntervalKeys` -
  must produce an IDENTICAL composition render-window / loop-unit outcome
  (verdict + qualifications in section 6).

## 1. Behavior change in one sentence

A same-tree (or foreign-partner) dock stops being invisible to the interval model: the
continuing vessel's composition timeline gains an interval edge at every Dock / Board
merge UT, the docked interval's label rebases to the merged vessel's own combined
controllers + crew (fixing the undercount and the clamp-at-0 artifact), interval keys
stay stable for every existing selection, and - in Phase 2 - logistics can accept the
undock-to-undock shuttle runs it rejects today.

## 2. Worked shuttle example (the problem statement)

Freighter "Dromader", one recording tree, one physical vessel through-line:

```
launch KSC --> dock Depot-A (load ore) --> undock A --> transfer --> dock Depot-B
          --> undock B --> deorbit / recover
```

Composition intervals TODAY (edges only at structural-peel UTs, i.e. the undocks;
`MissionComposition.cs:134-143`):

```
I0 = [launch   .. undockA)   <- includes the docked-at-A loading stretch
I1 = [undockA  .. undockB)   <- includes the docked-at-B delivery stretch
I2 = [undockB  .. end)
```

The shuttle route the player wants is the cargo run `[dockA .. dockB]` (or
`[undockA .. dockB]`). Neither dock is an interval boundary, so:

- the run cannot START at dock A: the loading stretch is welded to the launch inside I0;
- logistics rejects the shape - today as the generic `UndockedStartOrigin = 6`
  (`RouteAnalysisEngine.cs:538-551`), with status 9 reserved but never emitted
  (`RouteAnalysisEngine.cs:55-64`, verified: no emit site exists);
- the I0 label reads from the HEAD leg's counts only (`MissionComposition.cs:156-160`),
  so a `pod x1, crew x1` freighter docked to a `probe x1` depot still reads
  `pod x1, crew x1` during the docked stretch (the undercount), and the later undock
  subtracts depot controllers that were never counted, silently hidden by the
  clamp-at-0 (`MissionComposition.cs:161-163` - the clamp exists as the roadmap says).

Composition intervals AFTER P1:

```
I0a = [launch  .. dockA)                I1a = [undockA .. dockB)
I0b = [dockA   .. undockA)  "Docked"    I1b = [dockB   .. undockB)  "Docked"
I2  = [undockB .. end)
```

Selection {exclude I0a} start-trims the vessel to dock A. P2 teaches logistics to derive
and accept exactly that selection.

## 3. Current-state findings (verified, with audit-vs-code discrepancies)

### 3.1 The composition model

- `MissionCompositionBuilder.BuildNode` walks the FULL continuation through-line into one
  run (`MissionComposition.cs:95-105`) via `MissionThroughLineBuilder.ContinuationSuccessor`
  (`MissionThroughLine.cs:124-148`): env-split `SequenceNextId` first, else the
  non-anchored, non-EVA branch child, preferring `IsBranchContinuation` (= the branch
  point's `ChildRecordingIds[0]`, set in `BuildBranchLinks`, `MissionStructure.cs:366-371`).
  A dock-merged child is `ChildRecordingIds[0]` of its Dock BP and is not an anchored
  offshoot, so it IS walked into the continuing vessel's run. Gap 1 confirmed: interval
  edges come only from structural-peel UTs (`MissionComposition.cs:134-143`, verified;
  the roadmap audit's ":132-143" anchor is one line off), never from a merge UT.
- The partner side: the second line to reach the merged child stops at the shared
  `visited` set (`MissionComposition.cs:89-99`), and the merged child is skipped as a
  peel because it equals that leg's `ContinuationSuccessor` (`MissionComposition.cs:122`).
  So the partner's line simply ends at the dock - correct, unchanged by this plan
  (cross-tree foreign-side looping stays M-MIS-8, `docs/parsek-missions-design.md:412-414`).
- Labels: interval controllers = head-leg counts minus structural peels at/before the
  interval start (`MissionComposition.cs:156-160`), clamped at 0 (`:161-163`); crew =
  head-leg roster minus crew peels (`:165-176`). Nothing ever ADDS what a merge brought
  in. Gap 2 confirmed. A kerbal that EVAs and re-boards stays subtracted forever
  (`:167-173` has no re-board path) - the same rebase mechanism fixes this.
- `OriginBranchPointType` is set on every BP child (`MissionStructure.cs:378`, exact
  match with the audit) and includes Dock / Board; `BranchEventName` already maps them
  to "Docked" / "Boarded" (`MissionComposition.cs:412-413`). All data needed for the
  edge already exists - no recorder or schema change.
- The merged child's own composition IS the combined vessel: `PopulateComposition`
  (`MissionStructure.cs:223-256`) reads `Recording.Controllers` / `StartCrew` /
  `CrewEndStates`, and the recorder captures controllers with a live vessel scan at
  recording start (`FlightRecorder.cs:6536`, `ControllerInfo.CaptureFromVessel(v)`).
  P1 carries a verification task for the dock-branch path specifically (D2 fallback).

### 3.2 Selection, keys, and consumers

- `MissionIntervalSelection.ComputeRenderWindows` (`MissionIntervalSelection.cs:35`,
  exact) unions `[min StartUT, max EndUT]` per `OwnerHeadId` over INCLUDED intervals
  (`:65-77`); children are walked regardless of the parent's inclusion (`:80-81`).
- Interval keys are POSITIONAL: `headLegId` for interval 0, `headLegId + "/seg" + i`
  for the i-th interval over the sorted edge list (`MissionComposition.cs:180-182`).
  Inserting a dock edge into that list would renumber every later `/segN` key.
- Composition consumers (complete list): `MissionLoopUnitBuilder` (`:139`, windows via
  `ComputeTrimmedMemberWindows` `:149-151` / `:1268-1269`), `MissionPeriodicity.
  ExtractConstraints` (routes through the same helper, `MissionPeriodicity.cs:347`),
  `MissionStore.ReconcileSelections` (valid-key set, `MissionStore.cs:160-169`),
  `MissionsWindowUI` (`:473`, rows + checkboxes), and `RouteBackingMission`
  (`Logistics/RouteBackingMission.cs:134/227/429`).
- `BuildSignature` folds the PERSISTED key strings, not derived composition
  (`MissionLoopUnitBuilder.cs:1360-1363`) - no cache perturbation from new edges alone.
- `MissionStore.ReconcileSelections` (`MissionStore.cs:118-179`, called from
  `ParsekScenario.cs:3262`) drops keys that no longer name ANY selectable node, with a
  Warn. It cannot detect a key that still exists but silently RETARGETS a different
  segment - which is exactly what plain renumbering would produce.
- M-MIS-9 precedent (`RouteBackingMission.ComputeAutoExcludedNewIntervalKeys`,
  `:375-489`): prong 1 = base-id rule (`StripSegMarker`, `:305-311`), prong 2 = UT
  end-trim re-derived against the CURRENT tree from the persisted `RecordedDockUT`
  (`:448-472`), precisely because "/segN" churn freezes key STRINGS, not UT windows.
  Guards: derives nothing when the creation-time excluded set or SourceRefs are empty
  (`:390-409`).
- `Mission` serialization: `excludedInterval` values in `Mission.Save/Load`
  (`Mission.cs:85-91`, `:93-128`) - keys are opaque strings, no schema constraint.

### 3.3 Logistics current state (and two doc-vs-code discrepancies)

- Status 9 is RESERVED, not emitted: enum `RouteAnalysisEngine.cs:67`, formatter
  `RouteCreationFormatters.cs:230-231`, and NO detector. Shuttle-family runs reject as
  `UndockedStartOrigin = 6` (`RouteAnalysisEngine.cs:538-551`, harvest path `:673+`).
  The audit's claim is confirmed.
- DISCREPANCY A: `RouteBuilder.cs:245-247` ("End at the DOCK so the docked-together
  combined vessel ... excluded") and the `RouteBackingMission` class doc + epsilon doc
  (`RouteBackingMission.cs:10-13`, `:71-79`, "for v0 this catches the dock-merged
  combined vessel, whose start IS the dock") describe behavior that DOES NOT happen
  today: because the merged child folds into the pre-dock interval (3.1), no interval
  starts at the dock, and the actual rendered window runs to the UNDOCK - pinned by
  `RouteBackingMissionTests` `Compute_MultiLegTree_KeepsLaunchToUndock_ExcludesPostUndock`
  (`Source/Parsek.Tests/Logistics/RouteBackingMissionTests.cs:125-160`, asserts
  `w.EndUT == UndockUT` at `:144`) and by
  `RouteBackingMissionLoopUnitTests.RouteMission_ThroughUnchangedBuild_YieldsOneUnit_TrimmedToLaunchUndock`
  (`:179`). M-MIS-5 makes the comments TRUE and flips those tests (D4).
- DISCREPANCY B: the roadmap fix sketch says "emit a merge-UT edge ... when its
  controller count increases" (`docs/dev/todo-and-known-bugs.md:1667`); this plan gates
  on the branch TYPE instead (D1) - a count gate is neither necessary (count-neutral
  merges exist) nor sufficient (it couples edge topology to label arithmetic).
- Route dispatch phase is owned by the route loop clock crossing detector, not the
  Mission anchor (`RouteBackingMission.cs:58-63`) - but the realized cycle length is
  `max(LoopIntervalSeconds, span)` (`MissionLoopUnitBuilder.cs:194-197`), so shrinking
  the span from undock-launch to dock-launch DOES shorten the realized cycle whenever
  `DispatchInterval < undock - launch` (every N=1 route). See D4 for the honest framing;
  see R2 for the dock-phase == span-end edge.

## 4. Design decisions

### D1 - Edge emission rule: gate on OriginBranchPointType Dock / Board, not on counts

In `BuildNode`, while walking the run, collect every run member (index >= 1) whose
`OriginBranchPointType` is `Dock` or `Board`; each contributes an interval edge at its
`StartUT`, clamped into `[runStart, runEnd]` and dedup'd exactly like peel edges
(`MissionComposition.cs:134-143` pattern). Rationale:

- The type is authoritative and already present for BOTH same-tree docks and
  foreign-partner docks (the Dock BP lives in the controller's tree either way, so the
  logistics shuttle case - depot unrecorded - is covered).
- A controller-count gate would miss count-neutral merges (probe docks probe) and adds
  no safety: the edge is a SELECTION boundary, not a label fact.
- Board is included: it is the other merge BP type (`MissionStructure.cs` BP family),
  costs nothing, and the same rebase fixes the re-boarded-kerbal roster (3.1).
- Coincident UTs: a merge UT equal to a structural edge or a run endpoint dedups away -
  structural identity wins, no zero-width interval, no key minted (pinned by test).
- Env-split continuations of the merged child (`OriginBranchPointType == null`) do not
  re-trigger; only the BP child does. Run HEADS that begin via Dock (a line whose first
  leg is the merged child) produce no extra edge (edge == runStart dedups).

Review-confirmed evidence (verdict C1 - settled, do not re-litigate at build time):

- Claw couples ARE covered: `ParsekFlight.OnPartCouple` (`ParsekFlight.cs:10202`)
  handles ANY `onPartCouple` with no docking-module filter, and KSP's grapple couples
  via `Part.Couple`, so a claw couple produces a Dock-typed BP. Both Dock and Board BPs
  are created by the ONE funnel `ParsekFlight.CreateMergeBranch` (`:5870-6118`), which
  always adds the BP to the controller's own tree (`:6056`) whether the partner is
  recorded (two-parent) or foreign/unrecorded (one-parent).
- The optimizer cannot merge across a BP: `RecordingOptimizer.CanAutoMerge` refuses
  (`RecordingOptimizer.cs:91`), so the pre-dock parent can never be glued to the merged
  child; env-split re-merges keep the first segment's id/`ParentBranchPointId`, so the
  BP child reference survives and the derived type is rebuilt every Build.
- The only genuinely uncovered dock shape is a BG-to-BG dock where neither side is the
  recorder's vessel: no merge BP and no merged leg exist at all. Recorder-side gap,
  outside this milestone's projection scope. Not a blocker.

### D2 - Label rebase: piecewise rebase at merge legs (fixes gap 2 + the clamp artifact)

For each interval, find the latest merge leg (D1 set) with `StartUT <= segStart`:

- If none: base = head-leg composition, subtract structural peels at/before segStart and
  crew peels by segEnd - byte-identical to today (`MissionComposition.cs:156-176`).
- If present: base = the merge leg's OWN composition (`PodCount/ProbeCount/SeatCount/
  CrewCount/CrewNames`, populated from the merged recording's fresh start capture,
  `MissionStructure.cs:223-256` + `FlightRecorder.cs:6536`); subtract only structural
  peels with UT in `(rebaseUT, segStart]` and crew peels with StartUT in
  `(rebaseUT, segEnd]`. Crew already gone before the dock is baked into the merged
  roster - no double subtraction.
- REQUIRED (verdict C2, blocker-level): when the base is a REBASED one, structural
  peels must subtract the peeled leg's `CrewNames`/`CrewCount` too, not just
  pod/probe/seat. Today's structural-peel subtraction touches only controllers
  (`MissionComposition.cs:157-160`); crew leaves the roster solely via EVA crew peels
  (`:165-176`). That is safe today ONLY because the head roster never contains a dock
  partner's crew. After D2, the rebased roster DOES contain the partner's crew, so a
  post-undock interval (rebase base = the dock merge leg, structural peel = the
  departing depot) would keep the departed crew in the roster - a crew overcount D2
  itself would introduce. Subtract the structural-peel leg's crew (remove its
  `CrewNames` from the surviving roster; decrement `CrewCount` on the nameless path)
  whenever the operating base is a merge-leg rebase. Pinned by the named test
  `Undock_AfterDock_RemovesDepartingCrewFromRoster` (P1 list).

This makes the docked interval read the combined vessel (undercount fixed) and makes the
post-undock subtraction operate against a base that actually contains the departing
piece, so the clamp at `MissionComposition.cs:161-163` stops being load-bearing (it
stays, as a defensive floor).

Fallback (fail-closed labels): if the merge leg carries zero controllers AND zero crew
(legacy / stale capture - the ":24-25 stale Controllers" caveat in the file header),
fall back to ADDITIVE: previous base plus the OTHER parent legs' compositions at the
merge, Verbose-logged with the leg id. Acknowledged residual inaccuracy (verdict C2):
the additive fallback adds the partner LEG's start composition, not its at-merge
composition - a depot that shed a probe mid-leg overcounts by that probe; accepted for
a logged fallback path. P1 carries a verification task that real dock-branch recordings
carry combined controllers (in-game log check, see 5.1).

### D3 - Positional-key stability: hybrid stable-key scheme + one-time selection reconcile

THE POSITION: dock edges must never renumber existing keys, and pre-M-MIS-5 selections
must never silently retarget.

- `/segN` ordinals are computed over STRUCTURAL edges ONLY - i.e. exactly today's edge
  list. Every existing key string keeps its exact meaning on every existing tree.
- A dock/board edge SUBDIVIDES a structural interval; sub-intervals after the first are
  keyed `<parentIntervalKey>@dockM` (M = 1-based ordinal of the merge edge inside that
  structural interval), where `<parentIntervalKey>` is `head` or `head/segN`. Examples:
  `rec-a@dock1`, `rec-a/seg2@dock1`. `StripSegMarker` (`RouteBackingMission.cs:305-311`)
  and `RootsAtUndockChild` (`:563-576`) already strip at the first `/seg` marker and
  keep working; a small extension strips `@dock` for bare-head parents (`rec-a@dock1`
  has no `/seg`), added with tests.
- Future edge kinds get their own suffix namespace. Scope of the stability claim
  (verdict C3a): renumbering-by-insertion is impossible for new edge KINDS, and every
  existing key is stable AT UPGRADE TIME on an unchanged tree. Under LATER tree growth,
  `/segN` renumbers exactly as today, and `@dockM` keys can additionally re-parent /
  renumber when a new structural peel subdivides their parent interval - same hazard
  class as today, same mitigations (loud stale-drop in `ReconcileSelections` + the
  M-MIS-9 prong-2 UT re-derivation).
- Pre-M-MIS-5 saves: an old excluded key `K` used to cover the WHOLE structural
  interval; post-change it covers only the pre-dock lead sub-interval, so the docked
  tail would silently re-render. Remedy: `Mission` gains a persisted
  `SelectionSchemaGeneration` int (absent/0 = pre-M-MIS-5, 1 = current;
  `Mission.Save/Load` `Mission.cs:85-128` + `Clone` `:61-70`).
  `MissionStore.ReconcileSelections` (`MissionStore.cs:118-179`), for generation-0
  missions, EXTENDS every excluded interval key to its `@dock` sub-siblings -
  semantics-preserving (the old selection excluded that whole span), Info-logged with
  counts. This is mutable selection state, not recorded flight data: the pre-1.0
  no-migration doctrine (recordings immutable, no compat paths) is not touched. Without
  the generation stamp the extension would be ambiguous against a NEW player who
  deliberately excluded the pre-dock half only - hence the flag.
- STAMPING IS UNCONDITIONAL (verdict C3b): the first post-upgrade reconcile stamps
  `SelectionSchemaGeneration = 1` on EVERY mission, including empty-exclusion missions
  (no extension work for those, stamp only). The hole if stamping were gated on having
  exclusions: a generation-0 empty-exclusion mission skipped by a reconcile that only
  processes missions with interval exclusions stays generation 0 forever; if the player
  then excludes only the pre-dock half post-upgrade, the NEXT load would wrongly extend
  that deliberate partial selection. Constructor-default generation = 1 on every
  creation path (the `Mission` field initializer covers the ctor, `EnsureDefaultsForTrees`,
  and every UI create path; `Clone` COPIES the source's generation); ONLY `Load` with
  the key absent yields 0.
- Ordering (verdict C3c): run the generation-0 extension against the SAME
  composition-derived valid-key set the stale-dropper uses, and extend BEFORE (or
  atomically with) stale-dropping, so a still-valid `K` gains its `@dock` sub-siblings
  before anything is removed.
- Routes need NO flag: every pre-M-MIS-5 route exclusion targets an interval starting at
  or after `RecordedDockUT`, and the reappearing `@dock` sub-intervals in that range are
  re-excluded by the EXISTING M-MIS-9 prong-2 UT end-trim (`RouteBackingMission.cs:448-472`)
  on the next derivation; intermediate-stop docked stretches were never excluded at
  creation, so nothing before the last dock drifts. Prong 2 stays; the renumbering
  hazard it was built against shrinks to "genuinely new keys past the dock", which it
  already handles. Stated caveats (verdict C3d): routes with EMPTY creation-time
  exclusions or empty SourceRefs skip the freeze entirely (`:390-409`) and keep
  whole-segment render - consistent, no drift; and prong 1 without the R3
  `StripSegMarker` extension would misclassify every `@dock` key as unknown-base, so
  R3 is load-bearing for the self-heal, not hygiene.
- REJECTED alternatives: (a) plain renumbering + rely on ReconcileSelections - it only
  drops VANISHED keys; renumbered keys stay valid strings and silently retarget
  (selection corruption; the multi-stop worked example loses its mid-route leg from the
  window max); (b) full UT-based re-key of everything (`/seg@<UT>`) - loud one-time
  reset of EVERY interval trim in every save, including dock-free trees; strictly more
  player pain than needed; (c) gating edge emission per-mission - forks the one
  composition model ("topology lives in one place") and contradicts the ratified scope.

### D4 - Route render end flips from last-undock to last-dock, in P1

With D1 edges emitted, `WalkAndClassify`'s `StartUT >= boundary` rule
(`RouteBackingMission.cs:539`) newly catches the `[lastDock .. undock)` sub-interval, so
the route's rendered window end moves from the last undock (today's pinned behavior,
Discrepancy A) to the last dock - which is what `RouteBuilder.cs:245-247` and the
`RouteBackingMission` class/epsilon docs have claimed all along. Position (OQ2 DECIDED
at review - ship in P1): ship the flip rather than suppress it, because (a) route
selections are dock-adjacent by construction and therefore OUTSIDE the
byte-identical-off gate by the gate's own definition; (b) suppressing it needs an
artificial terminal-undock boundary derivation that P2 would immediately delete; (c)
DELIVERY TIMING CHANGES - stated honestly (the draft's earlier "timing is unaffected"
claim was REFUTED in review, verdict C4): `transitDuration` is already dock-based
(`RouteBuilder.cs:165-169`), so `DispatchInterval = N x (dock - launch)`, while the
loop unit's realized cycle is `max(LoopIntervalSeconds, span)`
(`MissionLoopUnitBuilder.cs:194-197`). Today span = undock - launch, which exceeds
DispatchInterval whenever `DispatchInterval < undock - launch` (every N=1 route, since
the docked stretch is positive), so today's realized cycle is undock - launch. After
the flip span = dock - launch and the realized cycle re-aligns DOWN to
DispatchInterval: existing sub-span-cadence routes deliver MORE OFTEN, by the
docked-stretch duration. This closes the latent DispatchInterval-vs-realized-cycle
mismatch (the displayed cadence finally matches the realized one) and is the designed
behavior - but it is a player-visible delivery-timing change and ships with its own
CHANGELOG sentence (P1 task 5); (d) visually the ghost now retires at the dock instead
of sitting docked until the undock - the original playtest-follow-up intent.

Review-confirmed evidence (verdict C4 - settled, do not re-litigate at build time):

- Loop-clock convention sound for the new geometry: `ComputeDockCycleIndex` uses
  `loopUT >= recordedDockUT` (`RouteLoopClock.cs:154-157`, not strictly greater),
  `IsDockUTInSpan` is end-inclusive (`:127-130`), and at cadence == span the crossing
  for cycle k fires exactly once, one frame into cycle k+1 via
  `dockCycleIndex = cycleIndex - 1` (equality cases documented at `:140-148`).
- M4a interaction: `CollectTerminalUndockChildLegIds` picks the Undock BP NEAREST
  `segmentEndUT` (`:591-625`); with segmentEndUT = dock it still picks the terminal
  undock in normal geometries, and the pathological long-docked-stay shape (an earlier
  undock nearer than the terminal one) already misbehaves TODAY - pre-existing, not
  worsened by the flip.

Guard rails: the dock phase now lands exactly on the span end - R2's clock test is
mandatory in P1, plus one in-game route validation pass that times two consecutive
deliveries on an N=1 route and confirms the gap equals DispatchInterval (shorter than
the old undock-to-undock cycle by the docked stretch).

### D5 - Logistics lift shape (P2): detector first, acceptance second

- P2a (narrow): add a REAL detector for the shuttle family - route source path whose
  origin connection window's dock lies mid-tree with a preceding same-through-line
  stretch (now recognizable via the dock boundary + `RouteOriginProof`) - emitting
  `MidRecordingStartTrimUnsupported = 9` with an accurate reject text instead of the
  misleading generic 6 for that family. Zero behavior risk (a reject stays a reject),
  and it validates the boundary data end-to-end.
- P2b (lift): accept the shape - start-trim derivation
  (`ComputeExcludedIntervalKeys` gains a start-side counterpart: exclude selectable
  intervals ENDING at/before `segmentStartUT + epsilon`, symmetric epsilon rule),
  `RouteBuilder` plumbing (origin start UT instead of tree-root launch UT for the
  member window), the analysis origin gate accepting a docked-origin window mid-tree,
  and the M-MIS-9 freeze extended with a start-side UT prong. P2b is gated on its own
  short mini-plan (the analysis-engine restructure is the risk mass), and the maintainer
  can stop after P2a with the limitation honestly narrowed.

### D6 - Fail-closed rules

- Merge leg missing from `LegsById` or with unusable StartUT: no edge, Verbose log.
- Merge-leg composition empty: D2 additive fallback, Verbose log (never blank labels).
- Merge UT coincident with a structural edge / run endpoint: structural identity wins,
  no `@dock` key minted.
- Foreign-partner trees: unchanged (partner-side looping stays M-MIS-8).
- P2 analysis: every newly distinguishable-but-not-yet-supported shape keeps rejecting,
  with the most specific status available; no silent acceptance widening.

### D7 - No store lifecycle changes (restated recorded decision)

No machine-generated Mission rows on dock, no Mission auto-splits, no RecordingTree /
BranchPoint / Recording schema change, no recorder change. The entire P1 diff lives in
the pure projection layer (`MissionComposition.cs`, `MissionStore.cs`, `Mission.cs`) plus
route derivation tests, logging, and docs.

## 5. Implementation phases

### P1 - Missions model: composition edge + labels + selection stability (shippable alone)

Tasks:

1. `MissionComposition.cs`: collect merge legs during the run walk (D1); build
   structural edges exactly as today, then subdivide per merge edges; mint keys per D3;
   rebase labels per D2; boundary events - the pre-merge interval's `EndEvent` and the
   merge sub-interval's `StartEvent` resolve via `BranchEventName(mergeLeg.
   OriginBranchPointType, mergeLeg.OriginCause)` ("Docked" / "Boarded"), extending the
   `StructuralPeelEventAt` lookup (`MissionComposition.cs:263-269`) with a merge-edge
   lookup. Crew-peel attachment (`SegmentContaining`, `:281-287`) naturally lands on the
   subdivided interval - display-only, windows unaffected.
2. Logging: `MissionCompositionBuilder` logs nothing today and is rebuilt per tick by
   route callers; add a `SuppressLogging` static mirroring `MissionStructureBuilder.
   SuppressLogging` (`MissionStructure.cs:106-113`) and emit ONE Verbose batch summary
   per non-suppressed build: `tree=<id> intervals=N structuralEdges=K mergeEdges=M
   rebases=R additiveFallbacks=F` (batch-counting convention). Wire the new flag into
   the existing suppress-wrap sites (verdict missed-item 5):
   `MissionsWindowUI.GetCompositionRoots` / `GetLoopUnitSet` (the two try/finally
   suppress blocks at `MissionsWindowUI.cs:444-456` and `:506-524`, plus the
   periodicity-only wrap at `:1244-1259`) and `RouteOrchestrator.ResolveLoopUnit`
   (`RouteOrchestrator.cs:1656`, hot: called per orchestrator tick from `:652` and
   `:1605`); ALSO evaluate `RouteBackingMission`'s three per-derivation build sites
   (`RouteBackingMission.cs:134/227/429`) - they run per route derivation, so either
   wrap them or let the once-per-derivation summary stand deliberately (decide at
   build time, log-spam check in the playtest). ReconcileSelections extension logs
   Info once per migrated mission (keys extended count).
3. `Mission.cs` + `MissionStore.cs`: `SelectionSchemaGeneration` persisted value
   (field initializer = 1 so every creation path - ctor, `EnsureDefaultsForTrees`, UI
   creates - defaults to 1; `Clone` copies the generation; only `Load` with the key
   absent yields 0), generation-0 exclusion-extension pass in `ReconcileSelections`
   ordered per D3 (extend before/atomically with stale-dropping, same valid-key set),
   then stamp EVERY mission to 1 unconditionally - including empty-exclusion missions.
   Post-change checklist: `ParsekScenario` OnSave/OnLoad path is `Mission.Save/Load`
   itself (no separate wiring). Test coverage (corrected per verdict C7 - the
   `Tests/Generators/` ScenarioWriter has NO mission writer today; only
   RouteFixtureBuilder touches Mission): a `Mission.Save/Load` round-trip test for the
   new value + the `MissionStoreTests` reconcile tests below; OPTIONALLY add a MISSION
   node writer to ScenarioWriter if an end-to-end save fixture proves wanted.
   Synthetic-recording injection unaffected.
4. Route-side P1 alignment (D4): update the pinned expectations (Discrepancy A tests)
   and RE-POINT the undock-UT-based route fixtures at the dock UTs BEFORE asserting
   (verdict missed-item 4; exact fixture moves in the named-tests list below);
   verify `BuildRouteSourceRefs` still carries the dock-child leaf ref independently
   (`RouteBuilder.cs:242-243`; test comment `RouteBackingMissionTests:246-248`) since
   the merged child drops out of `ComputeMemberRecordingIds`' kept set; add the R2
   loop-clock edge test.
5. Docs in the same commits: CHANGELOG entry (user-facing, house style: one item, max
   two sentences): docked stretches become selectable intervals with correct
   composition labels; logistics routes now deliver at the displayed dispatch interval
   (the docked stretch no longer stretches the realized cycle - the D4 timing change,
   verdict C4); todo M-MIS-5 entry updated
   (P1 done, P2 pending); `docs/parsek-missions-design.md` 14.2 flipped + the 475 table
   row; `RouteBuilder.cs:245-247` / `RouteBackingMission` class-doc comments become true
   as written (verify wording, no code-comment drift left behind).

Named tests (P1):

- `MissionCompositionTests.Dock_EmitsIntervalEdgeAtMergeUT_ContinuingLineSplits`
- `MissionCompositionTests.Dock_DockedIntervalLabel_UsesMergedLegComposition`
- `MissionCompositionTests.Undock_AfterDock_SubtractsDepartingFromRebasedBase` (pins the
  clamp-at-0 artifact fix with a depot whose controllers exceed the head's)
- `MissionCompositionTests.Undock_AfterDock_RemovesDepartingCrewFromRoster` (verdict C2
  blocker: the structural peel after a rebase must subtract the departing leg's
  CrewNames/CrewCount, or the post-undock interval keeps the departed partner crew)
- `MissionCompositionTests.Board_ReboardedKerbal_RejoinsRosterAfterBoardEdge`
- `MissionCompositionTests.MergeEdge_CoincidentWithStructuralPeelUT_NoSubInterval`
- `MissionCompositionTests.DockEdges_NeverRenumberStructuralSegKeys` (key stability)
- `MissionCompositionTests.MergedLeg_EmptyComposition_AdditiveFallbackLogged`
- `MissionCompositionTests.IntervalSelection_DockTree_EmptyExclusions_WindowsEqualUnsplit`
- BYTE-IDENTICAL-OFF PIN (the named constitutional test):
  `MissionLoopUnitBuilderTests.Build_DockTree_NoExclusions_SpanMembersAndCadenceByteIdentical`
  - a dock+undock tree built twice (fixture pinned pre-change values): member set,
  spanStart/spanEnd, cadence, owner index all equal the unsplit outcome, AND (verdict
  C5) the pin also asserts `vesselWindowCount` plus each member window's numeric
  start/end VALUES - a start-trim bug would first surface in
  `ComputeTrimmedMemberWindows`, whose `rEnd <= rStart` drop
  (`MissionLoopUnitBuilder.cs:1293`) is what silently removes a member.
- `MissionLoopUnitBuilderTests.Build_ExcludePreDockIntervals_StartTrimsSpanToDockUT`
  (the headline capability, extends `Build_ExcludingLaunchInterval_StartTrimsSpanAndMemberWindow`
  at `MissionLoopUnitBuilderTests.cs:395`)
- `MissionStoreTests.ReconcileSelections_Generation0_ExtendsExclusionsAcrossDockSubIntervals`
- `MissionStoreTests.ReconcileSelections_Generation0_EmptyExclusions_StampedWithoutExtension`
  (pins the unconditional stamp, verdict C3b: no extension work, generation still
  flips 0 -> 1 so a later partial exclusion is never wrongly extended)
- `MissionStoreTests.ReconcileSelections_Generation1_PreservesMixedDockSelection`
- `Logistics/RouteBackingMissionTests.Compute_DockEdge_WindowEndsAtDock` (supersedes the
  `:144` undock-end assertion; keep the earlier-undock scoping test `:296` green)
- `Logistics/RouteBackingMissionLoopUnitTests.RouteMission_ThroughUnchangedBuild_TrimmedToLaunchDock`
  (renamed/flipped from `:179`) + the four M-MIS-9 freeze tests (`:325-466`). FIXTURE
  RE-POINT FIRST (verdict missed-item 4): these fixtures are undock-UT-based under an
  equivalence M-MIS-5 EXPIRES - the file's own note (`:133-147`) admits passing the
  undock UT instead of production's dock UT is invisible only because no selectable
  interval starts inside `(dock, undock)`, and D1 creates exactly such an interval.
  So the P1 task RE-POINTS the fixtures at the DOCK UTs and THEN asserts:
  `FrozenRoute`'s `WithDockBinding(3000.0, "docked")` and its
  `ComputeExcludedIntervalKeys(segmentEndUT: 3000.0)` move to the single-stop tree's
  dock UT 2000; the `:192` test's `segmentEndUT: 3000.0` likewise moves to 2000;
  `MultiStopSegmentEndUT = 3000` moves to the depot-B dock 2500. Expectations then
  update (`@dock` keys where the scenario is a dock re-peel). The draft's earlier
  "assert they still pass unchanged" phrasing was the green-but-blind trap the review
  flagged - passing-unchanged on undock-UT fixtures would no longer model production.
- `Logistics/RouteLoopClockTests.DockPhaseAtSpanEnd_CrossingFiresOncePerCycle` (R2)
- In-game: extend `LogisticsRouteOnMissionsRuntimeTests` (`:99/:283` call
  `ComputeExcludedIntervalKeys`) to the new expected sets; one new in-game composition
  assertion that a dock tree surfaces the "Docked" interval row (Missions category,
  Scene=FLIGHT, self-contained synthetic tree per the P11 house pattern).

P1 exit: full `dotnet test` green; in-game Ctrl+Shift+T pass; one manual route playtest
confirming the D4 flip (ghost retires at dock) AND the D4 timing change: time two
consecutive deliveries on an N=1 route and confirm the gap equals DispatchInterval
(shorter than the old undock-to-undock cycle by the docked stretch).

### P2 - Logistics lift (dependent on P1)

P2a - honest detection (small, low risk):

- Detector in `RouteAnalysisEngine`: classify the undocked-start family; where the
  origin proof + tree topology show a mid-tree docked origin window preceding the run
  start, emit status 9 with a detail naming the origin dock UT; otherwise keep 6.
  Placement (verdict C6): there are TWO undocked-start reject sites - the non-harvest
  gate at `RouteAnalysisEngine.cs:538-551` and the harvest-path refined gate at
  `:673+` (status assigned at `:709`). Either instrument BOTH, or explicitly scope the
  detector to the non-harvest gate and leave a pointing comment at the harvest site
  saying status 9 is deliberately not emitted there (decide at build time; do not
  silently cover only one). Formatter text at `RouteCreationFormatters.cs:230-231`
  updated to say the lift is selection-side and coming.
- Tests: `RouteAnalysisEngineTests.ShuttleStart_BetweenDocks_EmitsMidRecordingStartTrim`
  + a negative (`GenuineUndockedStart_StillStatus6`);
  `LogisticsRejectPresentationTests` text pin update.

P2b - acceptance (own mini-plan gate before build):

- `ComputeExcludedIntervalKeys` start-side counterpart + `RouteBuilder` origin-start
  plumbing + analysis origin-window scoping + M-MIS-9 start-side freeze prong + span /
  loop-clock phase re-derivation for a non-launch span start.
- Sketch tests: `RouteBackingMissionTests.ComputeExcluded_StartTrim_ExcludesPreOriginDock`,
  `RouteAnalysisEngineTests.StartDockedShuttle_WithOriginProof_Eligible`, in-game
  `LogisticsShuttleRuntimeTests` (synthetic undock-to-undock tree, full route creation +
  one loop cycle). Named precisely in the P2b mini-plan.
- OQ1 (section 8) must be answered before P2b starts.

## 6. Byte-identical-off analysis (the constitutional gate)

Verdict: the roadmap's union argument HOLDS in code for render windows and loop units
under empty exclusions, but it is INCOMPLETE as a full byte-identical claim - three
qualifications are required.

Holds because: `Accumulate` takes `[min StartUT, max EndUT]` over included intervals per
`OwnerHeadId` (`MissionIntervalSelection.cs:65-77`); subdividing an interval preserves
both endpoints, and merge edges are clamped into `[runStart, runEnd]` so run endpoints
never move; `OwnerHeadId` is unchanged (no new through-line); the loop unit consumes
composition ONLY through `ComputeTrimmedMemberWindows`
(`MissionLoopUnitBuilder.cs:149-151`, `:1268-1269`) and periodicity extraction routes
through the same helper (`MissionPeriodicity.cs:347`); `BuildSignature` folds persisted
key strings only (`:1360-1363`). With empty `ExcludedIntervalKeys`, every window, member
set, span, cadence, owner, and signature is unchanged.

Qualifications:

1. The argument is silent on KEY IDENTITY. For non-empty selections it holds only under
   D3's stable keys + the generation-0 reconcile; with naive renumbering, persisted keys
   retarget and the window min/max holders change (demonstrated: a pre-M-MIS-5
   multi-stop route selection loses its mid-route max holder and the rendered tail).
2. Route selections use dock-adjacent boundaries BY CONSTRUCTION and are therefore
   outside the gate: their rendered end intentionally moves undock -> dock (D4).
3. Display changes are in scope of the milestone, not violations: more rows in
   `MissionsWindowUI`, "Docked"/"Boarded" boundary events, rebased labels, crew-peel
   rows re-parenting to the subdivided interval, and terminal atom expansion
   (`MissionComposition.cs:246-253`) hanging off the post-dock last interval with
   rebased counts. None of these feed windows, members, spans, or signatures.

## 7. Risk register

- R1 (P1): merged-leg Controllers stale/empty on some recorder paths -> wrong rebase
  labels. Mitigation: D2 additive fallback + Verbose log; in-game verification greps the
  `StartRecording: captured N start controller part(s)` line (`FlightRecorder.cs:6537`)
  on a live dock.
- R2 (P1, from D4): route dock phase now equals the span end exactly; the loop-clock
  crossing (`IsDockCrossing`, audited sawtooth-safe for dock-after-guard) must fire
  exactly once per cycle when phase == cycle length. Dedicated unit test; if it fails,
  fix the clock comparison (half-open interval), never re-widen the render window.
- R3 (P1): `StripSegMarker` callers that assume `/seg` is the only suffix - audit all
  `IndexOf("/seg"` sites (`RouteBackingMission.cs:309/:569`) for `@dock`-only keys on a
  bare head; covered by the key-scheme tests. LOAD-BEARING, not hygiene (verdict C3d):
  M-MIS-9 prong 1 routes every persisted key through `StripSegMarker`; without the
  `@dock` extension every `@dock` key misclassifies as unknown-base and the route
  no-flag self-heal breaks.
- R4 (P1): periodicity / knob inputs for route-backing units shift with the D4 window
  end (a rendezvous section at the dock may fall on the boundary). Routes do not
  phase-lock today via the backing mission (cadence route-owned), but assert the M4b
  knob tests stay green and watch the playtest log for `phasing knob` lines.
- R5 (P2b): analysis-engine origin restructure touching the M1-M4 gate ordering - the
  reason the P2b mini-plan gate exists.
- R6: pre-M-MIS-5 saves with interval trims on dock-bearing trees see the generation-0
  reconcile mutate their exclusion sets (extension only, semantics-preserving); every
  OTHER generation-0 mission (including empty-exclusion ones) is stamped to 1 without
  extension work (C3b). A crash between reconcile and save re-runs it idempotently
  (extension is a superset union; the unconditional stamp is trivially idempotent;
  stamp written with the same save).

## 8. Open questions (maintainer)

- OQ1 (blocks P2b only) - review recommendation recorded: RECOMMENDED-ORIGIN-UNDOCK.
  Should a lifted shuttle route's rendered window START at the origin DOCK (docked
  loading visible, origin debit inside the rendered span) or at the origin UNDOCK
  (cargo run only)? The review recommends ORIGIN UNDOCK: (a) symmetry with D4 -
  rendered content = the in-flight legs, docked stretches excluded at both ends; (b)
  mechanically cheaper and less coupled - the undock is ALREADY a structural edge, and
  the origin debit is dispatch-time ledger work (the M1 model), not render work. Final
  decision is deferred to the P2b mini-plan; P2a is unaffected either way.
- OQ2 - DECIDED at plan review (2026-07-04): D4 ships the route end flip
  (undock -> dock) inside P1. The suppress-until-P2 alternative is REJECTED: it builds
  throwaway machinery (an artificial terminal-undock boundary derivation P2b would
  immediately delete) and leaves the shipped docs lying about behavior; the flip
  converts the latent DispatchInterval-vs-realized-cycle mismatch into consistency.
  Ships with the honest cadence-change framing, the CHANGELOG sentence, and the N=1
  two-delivery in-game timing check (all in D4 / P1 task 5 / P1 exit).

## 9. Deliverables checklist

- P1: `MissionComposition.cs`, `Mission.cs`, `MissionStore.cs` changes; route-side test
  flips + dock-UT fixture re-points; ~17 named xUnit tests + a `Mission.Save/Load`
  round-trip test + 2 in-game extensions; batch-summary logging with the enumerated
  SuppressLogging wrap sites; CHANGELOG (including the D4 delivery-timing sentence),
  todo, design-doc 14.2 updates. No serialization change beyond the one
  `SelectionSchemaGeneration` value; no recorder, schema, or store lifecycle change.
- P2a: detector + formatter + 3 tests + reject-text doc note.
- P2b: mini-plan first; scope above.
