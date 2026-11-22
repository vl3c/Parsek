# Mission Profile window (design)

Status: DESIGN, awaiting owner review (2026-11-22). Nothing is built yet. Roadmap Phase 17, first
deliverable. Mockup: the "Parsek - Profile" HTML page published with this doc (three missions from
committed fixtures). Inputs: `docs/dev/research/mission-profile-foundation-notes-2026-11-21.md`
(Fable review F1-F6), the split rule versions 2 / 3 (`RecordingOptimizer.SplitRules.cs`,
`RecordingOptimizer.BurnCuts.cs`, design section 14 / 14.1 of
`docs/dev/done/plans/optimizer-persistence-split.md`), the Mission Log (`StructureListWindowUI`,
`MissionStructureListBuilder`), and a web survey of real mission profiles (section 13).

## 0. Owner request and the UI-surface exception

Owner request (Vlad, 2026-11-22): "it will be in the Missions window, a button to the left of Log
(per mission), and it will open a window similar to mission log called Profile, which will contain
the tree like list of the elements of the Profile; I want to see a design first of what the Profile
will look like".

This is an owner ruling for a new window, an exception to the `.claude/CLAUDE.md` rule "No new
player-facing UI surfaces", in the same shape as the Route History window: the Profile is a log-table
window built from the Mission Log's chrome (`StructureListWindowUI`: the same table styles, column
header row, body box, row style, single-line hover strip and Close button), opened only from a
button on a mission row. It is not a precedent for other new windows. When the build lands, the
exception bullet goes into `.claude/CLAUDE.md` next to the Route History one (then
`cp .claude/CLAUDE.md AGENTS.md` and refresh the umbrella mirrors by hand if the merge is docs-only),
and `docs/dev/design-gui-inventory.md` gains the window (14 windows).

## 1. Goal and non-goals

Goal: for one mission, show WHAT THE FLIGHT DID as a short tree of named elements, the way a real
mission profile reads (Apollo by the Numbers: Ascent Phase, Earth Orbit Phase, Translunar Phase,
Lunar Orbit Phase, Transearth Phase, Recovery): phases (spans with a body, a start and a duration)
that contain events (instants: burns, separations, docking, EVA, SOI changes, touchdown), with the
key numbers per row (orbit, delta-v, duration).

The Mission Log stays what it is: the flat, complete, chronological event list (every staging,
dock, undock, EVA, terminal). The Profile is the structured summary over the same flight. The two
can be open at once.

Non-goals for this feature:
- No change to recordings, rows, cuts or roles. The Profile reads committed data only and writes
  nothing (no save-format change in the first build, section 8).
- No graphic phase diagram (the roadmap's later "graph with visuals"); the analyzer below is the
  data it will need.
- No planning (what a burn should be), only what was flown.
- The optimizer does not adopt the analyzer in this feature (section 10, phase P3).

## 2. What the player sees

### 2.1 The Profile button

On the Missions tab, each mission bar's line 1 gets a `Profile` button immediately LEFT of `Log`,
same style (`interactButtonStyle`) and the same Interact single width (`InteractButtonWidth`, 100),
in Basic and in Advanced:

- Basic: `[Profile] [Log]`, right-aligned in the name cell.
- Advanced: `[Clone] [Warp to...] [Profile] [Log]`. Line 2 of the loop grid keeps
  `[Delete] [Loop [x] every] [period]` under Clone / Warp / Log; the cell under Profile stays empty
  (the period stays under Log). The name cell's minimum width grows by one button plus one gap.
  (Open question Q1 offers the alternative of a two-button pair inside the Log slot.)

Tooltip (an existing-tooltip-shaped addition, budgeted by `TooltipEchoBudgetTests`):
"The mission's phases: orbits, burns, transfers, landings, docking and EVA, with the numbers."
Click: `ParsekLog.Info("UI", "Mission Profile button: tree=... mission=... name='...'")`, then
`parentUI.OpenProfileWindowForMission(treeId, missionId, name)`.

### 2.2 The window

- Title: `Parsek - Profile: <mission name>` (bare `Parsek - Profile` when untargeted), built by the
  same pure `BuildWindowTitle(prefix, name)` the Log uses.
- Scenes: wherever the Missions window draws (FLIGHT, SPACECENTER).
- First open: to the right of the main window, cascaded 80 px down / right of the Log's default
  rect so both can be open without covering each other. Default 960 x 420, min 480 x 160,
  resizable from the corner like the Log.
- Body: the log table (boxed column header row, scroll view, row style), then the single-line hover
  strip (`TooltipEchoBox`), then `Close`. Read-only.
- Reopening it from another mission's `Profile` retargets it (one Profile window at a time, like
  the Log).
- Empty state (no recorded flight, or every vessel excluded): one muted row,
  "This mission has no recorded flight." (the Log's text).
- Follows KSP UI Scale through `ParsekUiScale.Window` inside the host scope; takes a camera input
  lock while hovered (its own lock id); logs its position changes through `LogWindowPosition`.

### 2.3 Columns

| Column | Width | Content |
|---|---|---|
| Time | 100 | First row: the launch date (`KSPUtil.PrintDateCompact`, the Log's first cell). Every other row: `T+h:mm:ss` from the mission start (the Log's `StructureTimeFormatter`, hours unbounded). The mission clock is the same for child lines (Apollo GET / MET). |
| Element | expands | The tree: indent + caret (phases and vessel headers) + the element label (section 3). |
| Location | 150 | `Kerbin, Launch Pad`, `Kerbin orbit`, `Mun`, `Mun, Midlands`, `Kerbin -> Mun` for a transfer (the Log's `StructureLocationFormatter` words). |
| Duration | 80 | Phases and burns: `3m 52s`, `2h 26m`, `4d 2h` (KSP 6-hour days). Instant events: empty. |
| Delta-v | 70 | Burns: `862 m/s`, prefixed `~` when estimated (section 4.6). Phases: the sum of their burns. Vessel header: the line total. Empty when no burn or unknown. |
| Orbit / result | 190 | The orbit a coast holds or a burn produced: `83 x 77 km, 0.1 deg` (Ap x Pe above the surface, inclination). Hyperbolic: `escape, Pe 38 km`. Encounters: `Mun encounter, Pe 229 km`. Surface: `landed 3,854 m`. End rows: the outcome. |

Units: km above 10 km, m below; delta-v in m/s, whole numbers; inclination one decimal; the in-game cell prints the
degree sign (stock KSP does; this doc writes "deg" to stay ASCII). All numbers
InvariantCulture (a unit test reads them).

### 2.4 Hover text

Every row carries a one-line tooltip shown in the strip: the full sentence behind the clipped
cells, for example

- burn: `Trans-Mun injection: 34 s burn at 82 km, about 862 m/s (estimated from the orbits before and after). Orbit after: Ap 11,524 km, Pe 82 km, 0.9 deg; Mun encounter.`
- phase: `Mun orbit: 2h 26m, 229 x 229 km then 45 x 30 km, 2 burns (131 m/s), 2 EVAs.`
- data gap: `Orbit after the burn is unknown: the vessel stayed in physics range until it landed, and no orbit sample was recorded.`
- vessel header: `Kerbal X Probe: separated at T+0:05:01 (decoupled); 1 phase; ends Orbiting Kerbin.`
- re-flown span: `Flown again from the separation at T+0:05:01 (Re-Fly). The first attempt is not shown.`

### 2.5 Collapse

Phase rows and vessel-line headers have a caret; events have none. Everything is expanded on first
open. Collapse state lives in the window for the session (keyed by mission id and element key), is
not saved, and survives a rebuild when the element key still exists. A collapsed phase shows its
event count in the Duration-column hover ("4 events").

## 3. The profile model

A mission profile is a TREE of three row kinds:

1. Vessel line (header row): one physical vessel through the mission (section 5). Label: the
   vessel name, then the crew in parentheses (`Kerbal X (Jebediah, Bill, Bob)`), an EVA kerbal as
   `EVA: Jebediah Kerman`. The mission's first launched vessel is the root line.
2. Phase: a span of one line, on one body (or body to body for a transfer), with a start, a
   duration and the burns and events inside it. Phases of one line tile its time with no gaps and
   no overlaps.
3. Event: an instant (or a short burn) inside a phase.

A child vessel line hangs under the EVENT that created it (separation, undock, EVA), one indent
deeper, followed by its own phases. Its rows keep the mission clock. When a child line ends by
docking into or boarding another line, its last row says so; the host line shows the matching
`Docked: <vessel>` / `Boarded: <kerbal>` event and continues. Debris never becomes a line (as in
the Missions tab).

### 3.1 Phase kinds

| Kind | Label | Starts | Ends |
|---|---|---|---|
| Prelaunch | (folded into Launch) | recording start on a launch site | liftoff |
| Ascent | `Ascent` | liftoff (surface -> air / approach) | atmosphere exit (atmosphere body) or the burn that reaches orbit (airless) |
| Orbit insertion | `Orbit insertion` | atmosphere exit with a falling orbit | end of the burn that reaches orbit |
| Direct ascent to escape | `Injection` | atmosphere exit | end of the burn that leaves the SOI with no parking orbit (v3 role `injection`) |
| Orbit | `<Body> orbit` | an orbit is reached (insertion, capture, after a rendezvous) | the burn that leaves it, or the line's end |
| Transfer | `Transfer to <Body>` / `Return to <Body>` | start of the departure burn (out of an orbit) or end of an injection | the capture burn's end, the atmosphere entry, or the landing approach |
| Flyby | `<Body> flyby` | SOI entry with no capture | SOI exit (nested inside the transfer) |
| Rendezvous | `Rendezvous with <partner>` | the first burn in the orbit phase that ends with a dock (section 4.5) | the dock |
| Docked | `Docked to <partner>` | dock | undock, or the line's end |
| Descent | `Descent` (airless) / `Deorbit` (atmosphere) | start of the burn that drops Pe below the surface / atmosphere | touchdown (airless) / atmosphere entry |
| Reentry | `Reentry` | first atmosphere entry of the final descent (skip dips fold in as `Skip` events) | touchdown, splashdown or destruction |
| Impact | `Impact` | a descent or reentry that ends in destruction | destruction |
| Aerocapture | `Aerocapture` (a Capture) | atmosphere entry on an open orbit that exits on a closed one (4.3 rule aerocapture) | atmosphere exit, or the end of the periapsis-raise burn that closes it |
| Aerobraking | `Aerobraking` (one event per pass) | first braking pass from a closed orbit | the last pass's exit (passes with only coasts between are one phase) |
| Atmosphere pass | `Atmospheric flyby` / `Atmosphere pass` | a pass that exits still open, or whose orbit after nothing decides | atmosphere exit |
| Surface stay | `On the surface` | touchdown | liftoff, or the line's end |
| Flight | `Flight` | takeoff (atmosphere body, never reaches space) | landing |
| Suborbital hop | `Suborbital flight` | liftoff (reaches space, never an orbit) | landing / entry |
| Drive | `Drive` | surface motion longer than 30 s and 50 m (rover) | stop |
| EVA | `EVA` | the EVA kerbal line's start | boarding, or the line's end |
| Unclassified space | `In space` | any space stretch the rules cannot name | next phase (logged, section 9) |

### 3.2 Event kinds

| Kind | Label | Source |
|---|---|---|
| Launch | `Launch` (location: the launch site) | recording start with `LaunchSiteName` / Prelaunch |
| Liftoff / Takeoff | `Liftoff` | first leave-surface boundary |
| Stage separation | `Stage separation (n pieces)` | JointBreak / Breakup branch points whose children are all debris, grouped per batch (the Log's batching) |
| Separation | `Separated: <vessel>` | branch point to a controlled child (a child line opens below) |
| Burn | named by what it did (4.4): `Circularization burn`, `Trans-Mun injection`, `Course correction`, `Mun orbit insertion`, `Orbit change`, `Deorbit burn`, `Phasing burn`, `Approach burn`, `Landing burn`, `Plane change` | engine part events + `ExoPropulsive` physics sections, gaps up to 10 s merged |
| Atmosphere exit / entry | `Atmosphere exit` / `Atmosphere entry` | env boundary |
| SOI change | `Entered Mun SOI` / `Left Mun SOI` | section body change |
| Docking / Undocking | `Docked: <partner>` / `Undocked: <partner>` | Dock / Undock branch points (cross-tree partner names via the Log's partner resolver) |
| EVA / Board | `EVA: <kerbal>` / `Boarded: <kerbal>` | EVA / Board branch points (the kerbal's line opens below the EVA row) |
| Parachutes | `Parachutes deployed` | first `ParachuteSemiDeployed` / `ParachuteDeployed` of a reentry |
| Gear | `Landing legs down` | first `GearDeployed` of a descent |
| Touchdown / Splashdown | `Touchdown` / `Splashdown` | surface boundary at the end of a descent / reentry |
| Recovery | `Recovered` | terminal Recovered |
| End | `End: Orbiting Kerbin`, `End: Landed (Mun, Midlands)`, `End: Destroyed`, `End: docked into <vessel>` | terminal state (the Log's `FormatEndLabel` words) |

Fairing jettison, lights, solar panels, antennas, RCS, throttle and robotics are Log material and
never profile rows (the research's "leave out low-value log events").

## 4. Deriving the elements (the phase analyzer)

### 4.1 Contract

One pure static analyzer, `MissionPhaseAnalyzer.Analyze(VesselLineInput line) -> PhasePlan`, in
the Recording module (model layer, so the optimizer can call it later without a backward layer
edge, F1). Input: one vessel line's pieces in UT order (each piece: its `TrackSections` with their
environment, frame, body, UT span, min / max altitude and on-rails checkpoint `OrbitSegment`s; its
`PartEvents`; its terminal state and terminal orbit; `StartSituation`, `LaunchSiteName`,
`StartBodyName`, `EndBiome`), plus the line's branch points (type, UT, cause, partner). Output: the
ordered phases, each with its events, burns and orbit facts and the rule and evidence that
named it, plus the "cut list" and role per piece
the optimizer will want later (computed, unused in this feature). No Unity calls, no statics, no
store reads, no logging inside the core (the caller logs one summary).

The analyzer NEVER reads `SegmentRole`, `SegmentPhase` or row boundaries as input (F1: roles become
outputs). A recording judged by split rule version 1 (never cut) and the same flight cut into ten
version-3 rows produce the same phases (the row-independence property, a test in 11.2).

### 4.2 Why not a forward pass (owner note, 2026-11-22)

A span's meaning often depends on what comes AFTER it, so a conditional forward pass that labels
each stretch as it arrives is wrong by construction. The owner's example: a vessel coming from
deep space touches an atmosphere for the first time. That one atmosphere touch can be

- an orbital capture (aerocapture: open orbit before, the vessel climbs out on a closed orbit),
- a trajectory adjustment (an aerobraking pass: closed orbit before and after, apoapsis lower;
  or an open orbit that stays open, a braking flyby),
- the first dip of a skip entry (it climbs out on an orbit whose periapsis is still inside the
  atmosphere and comes back down with no burn in between),
- the start of a direct descent (it never climbs out).

Only the rest of the line decides which, and the same holds elsewhere: a burn in orbit is a
phasing burn only if a dock follows in that orbit; an SOI pass is a flyby or an arrival depending
on a later capture; a takeoff is a launch or a flight depending on whether the vessel reaches
space; a descent ends in a landing or an impact; a coast after a burn is a transfer only if an
encounter or escape follows. Committed recordings are complete, so the analyzer has full
hindsight over the line and uses it. (The optimizer's split rules cannot: they must give the same
cut when a flight is flown again with a different future, which is why F3 stamps the stock
patch-end transition. The profile is recomputed on every build, so hindsight is safe for it.)

### 4.3 Three stages: segment, interpret, group

Stage 1, segmentation into neutral spans. The line's sections (de-duplicated by UT, the later
piece winning where two pieces share an instant) become a sequence of spans, each with facts
only and no meaning:

- regime: `Surface` (SurfaceMobile / SurfaceStationary), `Air` (Atmospheric), `Low` (Approach),
  `Coast` (ExoBallistic, and ExoPropulsive on an on-rails `OrbitalCheckpoint` section, where a
  packed vessel cannot thrust), `Burn` (ExoPropulsive in a physics frame, or engine ignite /
  shutdown part events inside Air or Low, runs merged across gaps up to 10 s; a burn under 0.5 s
  and 1 m/s is dropped as an RCS tap);
- body, UT span, min / max altitude (stored per section; points are never read);
- the orbit at each end when known: on-rails checkpoint elements of the span or the nearest one
  of the same body with no burn between, the terminal orbit at the line's end, else unknown
  (never point velocities: the recorded point velocity is surface-relative near the planet, F3);
- the orbit kind from those elements, with stock's own situations: falling (Pe below the
  atmosphere top, or below the surface on an airless body), closed (Pe above that, Ap inside the
  SOI), open (escape or hyperbolic);
- boundary facts between spans: SOI change, atmosphere entry / exit, low-band entry / exit,
  touchdown / liftoff, and the branch points at that UT (separation, dock, undock, EVA, board).

Row cuts, roles and recording boundaries are not facts; they never reach stage 2 (F1).

Stage 2, interpretation over the whole line. A rule set reads the span sequence with context on
both sides, like a pattern matcher over tokens, not a state machine fed one span at a time:

- Each rule is a named pattern over a window of spans: what must hold before (orbit kind, body,
  the previous interpretation), the core spans it claims, and what must hold after (the next
  orbit kind, the next boundary, a later dock, the line's outcome). Example, the atmosphere-pass
  family, all claiming one `Air` run entered from space:

  | Rule | Before | After the Air run | Reads as |
  |---|---|---|---|
  | direct-entry | any | no exit: touchdown, splashdown or destruction | Reentry (or Impact) |
  | skip-entry | falling or open | exit, then a new entry on the same body with no burn and no closed-orbit evidence between | Reentry; the pass becomes a `Skip` event inside it |
  | aerocapture | open (hyperbolic / escape) | exit with a closed orbit (from a checkpoint, or from the next pass recurring with no burn, or from a periapsis-raise burn that closes it) | Capture (`Aerocapture`), then `<Body> orbit` |
  | aerobraking | closed | exit with a closed orbit, Ap lower | `Aerobraking pass`, grouped with its neighbours in stage 3 |
  | braking-flyby | open | exit, still open | `Atmospheric flyby` inside the transfer |
  | unknown-pass | any | exit, orbit after unknown and nothing later decides | `Atmosphere pass` (neutral; the hover says why) |

- Patterns are tried in priority order per span; the longest consistent match wins, and a span
  claimed by a higher rule is not re-claimed. Evidence order is fixed: stored elements
  (checkpoints, terminal orbit) beat inferred structure (a recurring pass with no burn between),
  which beats nothing; when nothing decides, the neutral label is used, never a guess.
- The same mechanism covers every other context-dependent case: a burn in a closed orbit followed
  by a dock in that orbit is a `Phasing burn` / `Approach burn` and opens a Rendezvous; a coast in
  another body's SOI with no capture before the SOI exit is a Flyby; a takeoff that never reaches
  space is a Flight (aircraft) or a hop; a descent ending in destruction is an Impact; a burn whose
  coast leads to an encounter or escape is an injection, otherwise an orbit change.
- Rules are data (one table of rule objects with a name, a priority, a `Match(spans, i)` and an
  `Apply`), each unit-tested on its own, and the interpreter records which rule claimed each span
  and on what evidence, so the hover and the log can name it
  (`aerocapture: open before, closed after (checkpoint)`).

Stage 3, grouping into phases. Interpreted spans merge into the phases of section 3.1: a launch's
Surface + Air spans become Ascent; consecutive aerobraking passes on one body with only coasts
between become one `Aerobraking` phase with one event per pass; skip dips fold into the Reentry;
a transfer collects its injection, corrections, flybys and SOI changes up to the capture; the
events of 3.2 are placed inside the phase that covers their UT. Phases tile the line's time.

What each phase is, once interpreted:

- Ascent: liftoff to the atmosphere exit on an atmosphere body; on an airless body to the end of
  the burn that reaches an orbit. A line that starts in flight (a separated child, an undock, a
  resumed switch segment) has no Launch and starts in whatever its first spans interpret as.
- Orbit insertion after a falling atmosphere exit, until the burn that reaches orbit ends; or
  Injection when that burn leaves the SOI; or Suborbital flight when no burn comes.
- Orbit: burns that keep the orbit closed are events inside it (orbit change, plane change,
  phasing); a burn whose coast leads to an encounter or escape opens a Transfer at its start; a
  burn that makes the orbit falling opens Descent / Deorbit at its start.
- Transfer (`Return to <Body>` when it ends at the start body's parent): corrections, SOI changes
  and nested Flybys inside; it ends at the capture (burn or aerocapture), at a direct entry
  (Reentry), or at the low band of an airless body (Descent).
- Deorbit ends at the atmosphere entry; Descent ends at touchdown; Reentry ends at touchdown or
  splashdown; Surface stay runs to the next liftoff or the line's end, with Drives nested.
- Rendezvous and Docked: section 4.5.
- Anything no rule can claim is `In space` on its body, with the reason in the hover and in the
  log (section 9).

### 4.4 Naming burns

A burn's label comes from the orbit kinds on its two sides and where it sits:

| Before -> after | Label |
|---|---|
| falling -> in orbit (after an atmosphere exit) | `Circularization burn` |
| in orbit -> encounter / leaving (out of the parent's SOI) | `Trans-<Body> injection` (e.g. Trans-Mun) or `Escape burn` |
| in transfer -> still in transfer | `Course correction` |
| leaving / encounter -> in orbit (after an SOI entry) | `<Body> orbit insertion` |
| in orbit -> in orbit | `Orbit change` (`Plane change` when inclination moves more than 2 deg and Ap / Pe less than 5 %), `Phasing burn` inside a Rendezvous |
| in orbit -> falling | `Deorbit burn` (atmosphere) / `Descent burn` (airless) |
| inside the low band / reentry, ending at touchdown | `Landing burn` |
| Rendezvous after the phasing burns | `Approach burn` (a run of several within 10 minutes collapses into one row: `Approach burns (3)`) |
| any burn whose orbit sides are unknown | `Burn` |

### 4.5 Rendezvous and docking

A Dock branch point into this line (same tree, or a cross-tree partner the Missions tab already
shows as `Docked partner`) makes the analyzer look back inside the Orbit phase that ends with the
dock: the Rendezvous starts at the first burn after the last orbit change that was NOT followed
by a closer approach, which in the first build is simply the first burn in that Orbit phase after
the partner exists in the same SOI (the partner's recorded line, or its launch UT for a cross-tree
partner). Its burns read `Phasing burn` / `Approach burn`. When no burn exists (the partner came
to us), the Rendezvous is omitted and only the Docking event shows. Known limit: an unrelated
orbit change between the partner's arrival and the dock folds into the Rendezvous; P2's closest
approach from the two recorded trajectories (section 10) fixes it. After the dock: `Docked to
<partner>` until the Undock (or the end); the combined vessel keeps the host line's name, with
the partner named in the hover.

### 4.6 Numbers

- Orbit: Ap and Pe above the body's surface (equatorial radius), inclination, from checkpoint
  elements (`OrbitSegment` semi-major axis, eccentricity, inclination) or the terminal orbit.
- Burn duration: first to last burning sample of the merged run.
- Delta-v (estimate, first build): when the orbit just before and just after a burn are both known
  from checkpoints / terminal orbit and the burn is shorter than 120 s, propagate both conics to
  the burn's midpoint UT through `TwoBodyOrbit` and take the magnitude of the velocity difference
  (impulsive approximation; one synchronous pass per burn, no Zup caching). Shown with `~`. Phase
  and line totals sum the burns and keep the `~` when any part is estimated. Ascent and landing
  burns have no orbit on one side and show no delta-v in the first build. Real figures need
  mass and specific impulse, which are not recorded (F3); with F3's per-section stamps (P2) the
  column shows the expended delta-v without `~`.
- Mission summary (the root header's hover): total duration, total delta-v (with how much is
  known), bodies visited, landing sites, EVA count and total time, outcome per line.

### 4.7 Data gaps

The analyzer names what it could not read instead of guessing: `orbit unknown (no on-rails
sample)` (a vessel kept in physics range, as in the Mun landing's capture), `delta-v unknown (no
orbit on one side)`, `section missing` (a hydration hole). The cell shows `-`, the hover the
reason, and the build summary counts them (section 9).

## 5. Vessel lines (the walker)

`VesselLineWalker.Build(RecordingTree tree, MissionView view) -> List<VesselLine>` in the Missions
module (features layer: it needs `VesselIdentity` and `EffectiveState`), F2's one walker. A line is
the ordered pieces of one physical vessel through the mission, following all the "same vessel
continues" mechanisms in one place:

- chain pieces (`ChainId` / `ChainIndex`, holes tolerated: the next piece is the next HIGHER index
  of the same chain branch, the B5 helper);
- the parent recording running past a branch point with `ParentContinues`, and the inference for
  branch points written before it existed (F6);
- switch continuations and same-pid children (`VesselIdentity`);
- Re-Fly: the effective tip (`EffectiveState.EffectiveTipRecordingId` / `WalkSlotVessel`), so a
  superseded TIP is replaced by its fork and HEAD + fork read as one line; the first phase after the
  rewind point carries the re-flown mark (hover, 2.4);
- docking: the absorbed vessel's line ENDS at the dock (`End: docked into <host>`); the host line
  continues through the merged recording; at an undock the departing vessel opens a child line
  under the `Undocked` event (Parsek's undock creates new recordings for both sides; the walker
  keeps the host on the side that carries the host's identity, else the side with the host's
  root part, else the side the recorder focused).

Inputs are the committed, EFFECTIVE recordings only (`EffectiveState.ComputeERS()`; no raw
`CommittedRecordings` read, the ERS/ELS grep gate stays green). Debris lines are dropped (they
appear as Stage separation counts). The mission's include set applies exactly as in the Log: a
vessel whose intervals the mission excludes is dropped with its subtree, so a Clone over the same
tree with a different include set reads its own Profile. Cross-tree partners (the Missions tab's
`Docked partner` rows) are named, never expanded: their own mission has its own Profile.

`ReaimClassifier` moves to this walker in a later change (todo
REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS); this feature only builds it.

## 6. Window behaviour

- Build: on open, and on a Layout event when the change signature moves:
  `RecordingStore.StateVersion`, the ERS cache key (store + supersede version), `DisplayNames.Version`,
  the mission's name and include set (the Log's `ComputeChangeSignature` plus the ERS term).
  Never per frame. A rebuild logs `Profile window rebuilt on change: ... rows=a->b`.
- Cache: the analyzer's `PhasePlan` per line is cached by the ordered piece ids plus
  `StateVersion`, so a rename or an include toggle re-flattens rows without re-analysing.
- Scene change: the window keeps its target across FLIGHT <-> SPACECENTER like the Log (it is
  owned by `ParsekUI`); a target tree that no longer exists closes it with an Info line (the Route
  History's deleted-route rule).
- Re-Fly: during an active Re-Fly session the Profile shows the committed state (the provisional
  is not committed); after the merge the rebuild shows the fork. Discard changes nothing.
- Loops, clones and supply routes: the Profile describes the recorded flight once; a looped mission
  says `Loops every <period>` in the root hover only.
- Basic and Advanced: the same window and columns.

## 7. Seams for automation and the gallery

- `UiAction op=target window=profile mission=<id>` opens it on a mission and reports the row count
  (the Log's `op=target` contract, `StepCountForTesting`).
- A gallery state `profile-mun-return` built from the real model types fed through the real
  analyzer over a synthetic recording (`UI/Gallery/`, no store reads), plus `profile-empty`.
- The GUI census lanes (GUI-1 / GUI-2) photograph it opened on their densest mission; the window
  is added to the seam's window table, `TestCommandUiAction.cs` vocabulary and `hlib` (the
  `GuiCensusSeamVerbTests` byte-equality cells move in the same commit).

## 8. Derived versus stored

Everything in the first build is derived at view time from data already committed: sections,
checkpoints, part events, branch points, terminal state and orbit, launch site, biomes, names. No
new key, no new node, no format bump, no ledger row, no change to what is recorded. The collapse
state is window memory only.

Stored data comes only with P2 (F3): one trajectory sidecar format bump adding an extensible
per-section extension block (raw osculating Pe / Ap / inc / ecc and the stock patch-end transition
at space-section open and close; delta-v expended and mass per powered section), with the upgrade
step and a new `FormatGolden` folder per `docs/dev/design-save-format-policy.md`. That is a separate
design and PR; the Profile reads the stamps when present and falls back to section 4.6 when not.

Cost: the analyzer walks sections, checkpoints and part events, never trajectory points (it reads
the stored per-section min / max altitude). A 6-hour Mun flight is about 60 sections and 300 part
events: microseconds, once per build.

## 9. Logging

- `[UI]` Info: button click (tree, mission, name), window opened (tree, mission, lines, rows,
  title), rebuilt on change (rows before -> after), closed, closed for a deleted tree.
- `[Profile]` Verbose, one summary per build: `lines=N phases=N events=N burns=N dvKnown=a/b
  orbitUnknown=N unclassified=N reflown=N excludedLines=N` (batch counters, no per-row lines).
- `[Profile]` Verbose per line (bounded by line count, under 20 in practice): line id, pieces,
  first and last UT, phase kinds in order, gaps by reason.
- `[Profile]` InfoRateLimited when a line has an `In space` (unclassified) phase, keyed by line id
  and reason, so a new reason prints at once.
- Analyzer core: no logging (pure); the builder logs from the returned plan.

## 10. Code layout and phases

| Piece | File | Module / layer |
|---|---|---|
| Phase analyzer (F1), pure | `Recording/MissionPhaseAnalyzer.cs` (+ `.Burns.cs`, `.Orbits.cs`) | Recording / model |
| Element kinds + labels table (F4's seed: kind -> label, phase class, profile element) | `Recording/ProfileElementKinds.cs` | Recording / model |
| Vessel-line walker (F2), pure over the tree + ERS | `Missions/VesselLineWalker.cs` | Missions / features |
| Profile builder: lines + plans -> flattened rows with depth, cells, hovers | `Missions/MissionProfileBuilder.cs` | Missions / features |
| Window | `UI/MissionProfileWindowUI.cs` (chrome shared with `StructureListWindowUI` through a small extracted helper, not a third mode of that class: the tree, carets and six columns would branch every draw method) | UI / presentation |
| Button | `UI/MissionsWindowUI.MissionRows.cs` | UI |
| Gallery builder | `UI/Gallery/GuiMock.Profile.cs` | UI |

`Missions` is a headless module: the walker and builder add no leak (no live type), the window
stays in UI. A new file in a module folder passes `move_to_module_folders.py --check`.

Phases:
- P1 (this feature): analyzer, walker, builder, window, button, tests, docs. Derived only.
- P2: F3 recorder stamps (format bump) -> exact delta-v, stamped encounter / escape, closest approach
  for Rendezvous; F5 terminal state from the stamp.
- P3: the optimizer takes its cut list and roles from the analyzer (F1), `CanAutoMerge` asks it,
  loopability and labels read the F4 table; ReaimClassifier uses the walker. A split-rule version
  bump, its own design.
- P4: the graphic profile (Apollo-style phase diagram) from the same `PhasePlan`.

## 11. Tests

### 11.1 Unit (xUnit, pure)
- Each stage-2 rule on its own, positive and negative: the atmosphere-pass family (direct entry,
  skip entry, aerocapture from a checkpoint and from a recurring pass, aerobraking campaign of
  several passes, braking flyby, unknown pass), phasing vs orbit change (a dock follows or not),
  flyby vs arrival, launch vs flight vs hop, landing vs impact; plus the priority / longest-match
  and evidence-order cases (a checkpoint overrides an inferred recurrence).
- Analyzer per phase kind over `RecordingBuilder` synthetic lines: pad -> ascent -> insertion ->
  orbit; orbit -> transfer -> capture; transfer with corrections and a flyby; deorbit -> reentry ->
  parachutes -> splashdown -> recovered; Mun descent -> landing burn -> touchdown -> stay -> ascent;
  aerobraking pass vs aerocapture; injection with no parking orbit; aircraft flight; rover drive;
  rendezvous with and without burns; docked span; EVA line; unknown orbit gaps; a start in flight.
- Burn merge (10 s gap), RCS-tap drop, on-rails ExoPropulsive read as coast, engine-event burns in
  the air / low band.
- Delta-v estimate against hand-computed vis-viva cases (circularization, Hohmann leg, capture),
  the `~` marker, the 120 s cap, unknown sides.
- Walker: chain holes, ParentContinues and its inference, switch continuation, supersede fork
  (HEAD + fork one line), dock ends a line, undock opens a child, include set drops a subtree,
  debris dropped, cross-tree partner named not expanded, ERS-only reads.
- Builder: row order and depth, collapse keys stable across a rebuild, cell formats
  (InvariantCulture under `de-DE`), hover sentences, empty state, title.
- Log assertions on the summary and the rate-limited unclassified line (`ParsekLog.TestSinkForTesting`).

### 11.2 Fixture (xUnit over committed saves)
- `cs-career-mun4` (CS Mun Science), `mun-landing-recorded` (Kerbal X), `bdock-recorded`
  (Kerbal X #2): the expected phase and event list of Appendix A, pinned as text.
- Row independence: `mun-orbit-recorded` (version 1, one row) against `mun-orbit-recorded-v3`
  (version 3, cut rows) produce identical phases and events for the shared span.
- Atmosphere passes on real data: every committed recorded fixture with an atmosphere entry from
  space (cs-career-mun4's reentry is one; candidates to read at build time: `duna-direct-recorded`,
  `kerbin-return-recorded`, the Eve / Laythe fixtures, and flights from the `graze-*-probe-stack`
  hosts), each pinned to the rule that claimed it. Where no committed fixture holds a skip entry or
  an aerocapture, a harness lane records one (a new fixture), since synthetic lines alone would
  test the rules against our own assumptions.
- A sweep over every committed recorded fixture: the analyzer never throws, every line's phases
  tile its time, and the unclassified count is reported (report-only at first, a ceiling once read).

### 11.3 UI and gates
- Source gate: the `Profile` button is drawn immediately before the `Log` button in
  `DrawMissionNameCell` and opens `OpenProfileWindowForMission`.
- `BuildWindowTitle` / default rect / min size cells (the `StructureListWindowTitleTests` shape).
- `ParsekUiScaleTests` covers the new window's `ParsekUiScale.Window` call.
- `TooltipEchoBudgetTests` covers the button tooltip.
- In-game test `MissionProfile` category (SPACECENTER): open the Profile on a committed fixture
  mission, assert the rendered row count equals the builder's and the window rect is on screen.
- Harness: the GUI census lanes capture the window; no new flight lane is needed (the inputs are
  committed fixtures).

## 12. Docs that move with the build

`CHANGELOG.md` (one player line: "New Profile window on each mission: the flight's phases, burns,
orbits, docking and EVA."), `docs/dev/todo-and-known-bugs.md` (Phase 17 P1 entry and the P2 / P3
follow-ups), `docs/dev/design-gui-inventory.md` (14 windows, the button, the tooltip),
`.claude/CLAUDE.md` exception bullet + `AGENTS.md`, `docs/roadmap.md` Phase 17 status,
`docs/dev/arch` pins only if a ratchet moves, the design PDF table if this doc joins it.

## 13. Research notes (2026-11-22)

- Apollo by the Numbers splits each mission into phase pages (Ascent, Earth Orbit, Translunar,
  Lunar Orbit / Surface, Transearth, Recovery) whose tables carry Event, GET, altitude, event
  duration, velocity change, apolune, perilune; the mission timeline itself is flat with vehicle
  prefixes (S-IVB, CSM, LM, CSM/LM). The flight plan uses parallel lanes per vehicle; the Apollo
  Flight Journal splits by phase and moves the LM surface stay to its own journal.
  (nasa.gov SP-4029 Apollo 11 Summary, Lunar Orbit Phase, Timeline; archives.gov flight plan;
  apollojournals.org AFJ index.)
- Artemis I uses one flat table keyed by flight day, with burns, separations, entry and
  splashdown, some rows carrying duration and delta-v (blogs.esa.int/orion; nasa.gov Artemis I
  timeline).
- KSP tools are flat logs (The Kaptain's Log: launch, staging, dock, orbit closed / escaped, SOI
  change, landed, EVA, recovered) or planners (Flight Plan, maneuver planners); none shows a phase
  tree across several vessels.
- Taken from it: phases contain events; one row per burn with duration and delta-v (not ignition /
  cutoff pairs); the mission clock everywhere; child vessels as branches anchored on the event that
  made them, docking ends a branch; a mission summary; leave out low-value log events.

## 14. Open questions for the owner

- Q1 Button: a full-width `Profile` button left of `Log` (this doc), or `Profile` and `Log` as a
  two-button pair inside the existing Log slot (no layout growth, narrower labels)?
- Q2 Delta-v in the first build: show the conic estimate with `~` (this doc), or leave the column
  empty until the P2 recorder stamps give exact figures?
- Q3 Stage separations: keep them as events (this doc, like Apollo's S-IC separation rows), or
  leave staging to the Log only?
- Q4 Re-Fly: show only the flown-again path with a hover mark (this doc), or also list the
  superseded first attempt greyed under it?
- Q5 Child lines: nested under the separation / undock / EVA event (this doc), or listed after the
  parent line ends (flatter, less indentation on long missions)?
- Q6 Default collapse: everything expanded (this doc), or child vessel lines collapsed by default?
- Q7 Phase names: `Trans-Mun injection` / `Mun orbit insertion` (Apollo vocabulary, this doc), or
  plainer `Transfer burn` / `Capture burn`?

## Appendix A. Worked profiles from committed fixtures

Numbers read from the fixtures' `.prec.txt` mirrors (section UTs, checkpoint elements, part events);
delta-v figures are the 4.6 estimate computed by hand (vis-viva at the burn radius), `-` where 4.7
applies.

### A.1 CS Mun Science (`cs-career-mun4`): Mun orbit, two EVAs, return, recovery

| Time | Element | Location | Duration | Delta-v | Orbit / result |
|---|---|---|---|---|---|
| Y1, D06, 03:13 | CS Mun Science (Jebediah) | Kerbin, Launch Pad | 18h 22m | ~1,850 | Recovered |
| T+0:00:01 | Ascent | Kerbin | 3m 52s | - | to 70 km, Ap 80 km |
| T+0:01:10 | Stage separation (1 piece) | Kerbin | | | |
| T+0:02:40 | Stage separation (1 piece) | Kerbin | | | |
| T+0:03:54 | Orbit insertion | Kerbin | 1m 48s | ~362 | 83 x 77 km, 0.1 deg |
| T+0:05:24 | Circularization burn | Kerbin | 18s | ~362 | 83 x 77 km |
| T+0:05:42 | Kerbin orbit | Kerbin orbit | 4m 1s | | 83 x 77 km, 0.1 deg |
| T+0:09:44 | Transfer to Mun | Kerbin -> Mun | 8h 37m | ~1,096 | Mun orbit |
| T+0:09:44 | Trans-Mun injection | Kerbin | 34s | ~862 | Ap 11,524 km, Pe 82 km |
| T+2:29:23 | Course correction | Kerbin | 8s | ~14 | Ap 11,046 km, Pe 178 km |
| T+7:03:27 | Entered Mun SOI | Mun | | | Pe 229 km |
| T+8:46:35 | Mun orbit insertion | Mun | 11s | ~220 | 229 x 229 km, 1.0 deg |
| T+8:46:46 | Mun orbit | Mun orbit | 2h 26m | ~131 | 45 x 30 km |
| T+8:47:48 | EVA: Jebediah Kerman | Mun orbit | 2s | | boarded |
| T+9:59:29 | Orbit change | Mun orbit | 8s | ~64 | 229 x 30 km |
| T+10:38:16 | Orbit change | Mun orbit | 8s | ~67 | 45 x 30 km |
| T+10:38:26 | EVA: Jebediah Kerman | Mun orbit | 2s | | boarded |
| T+11:13:05 | Return to Kerbin | Mun -> Kerbin | 7h 4m | ~262 | Pe 27 km |
| T+11:13:05 | Trans-Kerbin injection | Mun | 11s | ~262 | escape, Pe 38 km |
| T+12:51:59 | Left Mun SOI | Kerbin | | | Ap 10,820 km, Pe 27 km |
| T+12:51:59 | Stage separation (1 piece) | Kerbin | | | |
| T+18:17:32 | Reentry | Kerbin | 4m 30s | | |
| T+18:20:11 | Parachutes deployed | Kerbin | | | |
| T+18:22:02 | Touchdown | Kerbin | | | landed 1,072 m |
| T+18:22:03 | Recovered | Kerbin | | | |

### A.2 Kerbal X (`mun-landing-recorded`): Mun landing

| Time | Element | Location | Duration | Delta-v | Orbit / result |
|---|---|---|---|---|---|
| Y1, D01, 00:00 | Kerbal X | Kerbin, Launch Pad | 6h 27m | ~1,998 | Landed, Mun |
| T+0:00:04 | Ascent (3 stage separations) | Kerbin | 2m 52s | - | to 70 km, Ap 80 km |
| T+0:02:57 | Orbit insertion | Kerbin | 1m 27s | ~736 | 84 x 76 km, 0.1 deg |
| T+0:04:25 | Kerbin orbit | Kerbin orbit | 26m 12s | | 84 x 76 km |
| T+0:30:37 | Transfer to Mun | Kerbin -> Mun | 5h 28m | ~1,262 | |
| T+0:30:37 | Trans-Mun injection | Kerbin | 25s | ~856 | Ap 11,480 km, Pe 77 km |
| T+1:21:02 | Course correction (stage separation) | Kerbin | 19s | ~120 | Mun encounter |
| T+2:12:07 | Course correction | Kerbin | 1s | ~9 | Mun encounter |
| T+4:33:10 | Entered Mun SOI | Mun | | | Pe 140 km |
| T+5:58:54 | Mun orbit insertion | Mun | 23s | ~277 | orbit unknown (about 140 km) |
| T+5:59:18 | Mun orbit | Mun orbit | 3m 1s | | about 140 km |
| T+6:02:19 | Descent | Mun | 22m 48s | - | |
| T+6:02:19 | Descent burn | Mun | 4s | - | |
| T+6:24:28 | Landing legs down | Mun | | | |
| T+6:25:08 | Touchdown | Mun | | | landed 3,854 m |
| T+6:25:08 | On the surface | Mun | 1m 54s | | End: Landed |

### A.3 Kerbal X #2 (`bdock-recorded`, second tree): probe separation, rendezvous, docking

| Time | Element | Location | Duration | Delta-v | Orbit / result |
|---|---|---|---|---|---|
| Y1, D01, 00:06 | Kerbal X (Bill, Bob, Valentina) | Kerbin, Launch Pad | 2h 22m | ~610 | Orbiting Kerbin |
| T+0:00:00 | Ascent (3 stage separations) | Kerbin | 2m 56s | - | to 70 km, Ap 90 km |
| T+0:02:57 | Orbit insertion | Kerbin | 2m 4s | ~592 | 95 x 84 km |
| T+0:05:01 | Separated: Kerbal X Probe | Kerbin orbit | | | |
| T+0:05:01 | (child) Kerbal X Probe | Kerbin orbit | 2h 17m | | End: Orbiting Kerbin, 95 x 84 km |
| T+0:05:01 | Kerbin orbit | Kerbin orbit | 1h 57m | | 95 x 84 km |
| T+2:02:33 | Rendezvous with Kerbal X (mission 'Kerbal X') | Kerbin orbit | 20m 4s | ~18 | |
| T+2:02:33 | Phasing burn | Kerbin orbit | 8s | ~18 | 117 x 85 km |
| T+2:17:16 | Approach burns (3) | Kerbin orbit | 20s | - | |
| T+2:22:38 | Docked: Kerbal X (mission 'Kerbal X') | Kerbin orbit | | | |
| T+2:22:38 | Docked to Kerbal X (mission 'Kerbal X') | Kerbin orbit | 1s | | |
| T+2:22:39 | Undocked: Kerbal X (mission 'Kerbal X') | Kerbin orbit | | | partner departs |
| T+2:22:40 | End: Orbiting Kerbin | Kerbin orbit | | | |
