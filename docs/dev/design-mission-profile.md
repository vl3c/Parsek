# Mission Profile window (design)

Status: P1a built and live-proven; the P1b core (analyzer, lines, builder, row model) built
2026-11-22 and the window, buttons, seam op, gallery states and census captures built 2026-11-23
(sections 10.1, 10.2); the census captures and the mirror epochs owe their flights. Roadmap Phase 17, first
deliverable. Mockup: the "Parsek Profile Mockup" HTML page published with this doc (three missions
from committed fixtures). Inputs: `docs/dev/research/mission-profile-foundation-notes-2026-11-21.md`
(Fable review F1-F6), the split rule versions 2 / 3 (`RecordingOptimizer.SplitRules.cs`,
`RecordingOptimizer.BurnCuts.cs`, design section 14 / 14.1 of
`docs/dev/done/plans/optimizer-persistence-split.md`), the Mission Log (`StructureListWindowUI`,
`MissionStructureListBuilder`), a web survey of real mission profiles (section 13), and two design
reviews (Opus and Fable, 2026-11-22, section 15).

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

Owner design note (2026-11-22): the classifier cannot be a simple conditional forward pass, because
a phase depends on more than the span in front of it; coming from deep space and touching the
atmosphere for the first time can be an orbital capture, a trajectory adjustment, the start of a
skip, or a direct descent. Section 4 is built around that.

## 0.1 What earlier plans and rulings already decided

A sweep of the roadmap, the design docs, `docs/dev/research/`, `docs/dev/done/` plans, the open and
archived todo volumes, `docs/dev/autotest-roadmap.md` and the umbrella `docs/handoff/` folders for
"mission profile", "profile", "Phase 17", "profile generator" and related terms (2026-11-22). The
todo volumes and autotest docs use "mission profile" only for harness flight scripts, and
`docs/parsek-logistics-supply-routes-design.md:1668` only in passing; neither decides anything
here. What does decide something:

| Source | Decision | Used here |
|---|---|---|
| `docs/roadmap.md:651-662` (Phase 17, PR #2717) | Read every recording of a mission from launch to its last ending; a NASA / Apollo-style profile, first as a text report, later as a graph with a phase diagram. Contents: phases in order, a mission-elapsed-time sequence of events (staging, engine start / cutoff, fairing and decoupler separations, docking, EVAs, parachutes, crashes), burns (time, duration, engine, delta-v, orbit before / after), a delta-v budget (planned versus used), the vessel and stage tree with how each piece ended, key numbers (max altitude, speed, max-Q / heating if recorded, duration). | The window is the text report; the graph is P4. Phases, burns, orbit before / after, the vessel tree, the mission clock and the duration are in. Where this design narrows the roadmap list it says so in the open questions (Q3, Q8-Q10), not silently. |
| `docs/dev/research/mission-profile-foundation-notes-2026-11-21.md:26-38` (F1) | Optimizer rows cannot be the profile elements: route sources, loop ranges, superseded / held / version-1 rows are never cut; no rendezvous row; the capture burn sits inside the transfer row. Phase 17 must classify a whole vessel line from sections, branch points and part events. | Section 4: the analyzer never reads rows or roles. |
| same file `:42-44` (F1), `:45-48` (F2), `:57-60` (F4) | One pure analyzer returning the ordered elements, the cut list and the role per piece; one vessel-line walker on `VesselIdentity` / `EffectiveState` (ReaimClassifier to use it); one role registry, `space` kept as "unclassified space". | Sections 4.1, 5, 10. The cut list is NOT produced in P1 (review finding, section 15); `In space` is the unclassified phase. |
| same file `:49-56` (F3), `:61-62` (F5), `:63-64` (F6) | Burns with delta-v and orbit before / after are not derivable from today's data near a planet; one sidecar format bump with an extension block is the fix; aerobraking vs aerocapture naming can wait. | Superseded for delta-v by the owner's 2026-11-22 decision: the game's own delta-v is recorded as additive snapshots (8.1, no format bump); the orbit stamps of F3 stay P2; the profile names aerocapture itself (rows keep `aerobraking`). |
| same file `:66-83` (orbit-kind model) | Stock's own situations (falling / in orbit / leaving), judge a burn in the body where it ends, never cut at an SOI line, staging gaps up to 10 s, a burn broken by on-rails time is two burns, unknown means no claim, lookahead changes rows when a flight is flown again. | Section 4.3 stage 1 facts; the hindsight note in 4.2. |
| `docs/handoff/recording-split-review/atmosphere-pass-analysis.md:95-98` (owner, 2026-11-20) | Rows must read like the elements of a mission profile, in the Phase 17 vocabulary (ascent, coast, capture, descent + landing). | The phase names match the row roles where both exist. |
| same file `:46-70` and `docs/dev/done/plans/optimizer-persistence-split.md:989-998` (version 2) | An atmosphere pass is classified by how it ENDS: on the ground = final descent from its first entry; back in space for over 2 minutes = a braking pass; hops out under 2 minutes belong to the same pass (a skip). An aerobraking campaign is one element; aerocapture is arrival / capture. | Section 4.3 stratum (c) uses the same 2-minute hop and the same "how it ends" reading. |
| same file `:99-127` and `:128-167`, answered `:121-127`, `:219-221` (owner, 2026-11-20) | Ascent ends at the atmosphere exit, then a distinct circularization element; a departure burn opens the transfer, the capture burn closes it (transfer = departure burn + coast + capture burn); deorbit element on every body ("Yes, deorbit row"); airless descent from the deorbit burn; reentry from the first atmosphere touch; an SOI change is never a boundary (ruling Q9). | Phase kinds 3.1 (`Circularization`, `Transfer`, `Deorbit`, `Descent`, `Reentry`), SOI changes as events. |
| same file `:197-198` (Fable second opinion) | Drag cannot lift the low point out of the atmosphere, so an aerocapture or aerobraking element runs until the raise burn ends. | Stratum (c) ends braking phases at the raise burn. |
| `docs/dev/done/plans/optimizer-persistence-split.md:1010-1047` (version 3) | Orbit kind evidence order (the coast's own SOI change, on-rails checkpoint elements or the terminal orbit, an SOI crossed with no burn, atmosphere / ground boundaries); point velocities never read; a burn with no evidence on both sides is never judged; a burn is a run of physics `ExoPropulsive` sections with gaps up to 10 s, ended by an on-rails gap or an SOI change. | Stage 1 facts and the burn definition reuse the BurnCuts helpers (section 4.3). |
| `.claude/CLAUDE.md` "No new player-facing UI surfaces" and its Route History exception | New windows need an owner ruling; the Route History reuses the Log's template and is not a precedent. | Section 0: the owner's 2026-11-22 request is that ruling, recorded the same way. |
| `docs/handoff/recording-split-review/STATUS.md:122-126` (owner, 2026-11-21) | The profile feature, its analyzer, line walker and format bump were deferred out of the split task into this feature. | This doc owns them; P1 builds the analyzer and the line view, P2 the format bump. |

## 1. Goal and non-goals

Goal: for one mission, show WHAT THE FLIGHT DID as a short tree of named elements, the way a real
mission profile reads (Apollo by the Numbers: Ascent Phase, Earth Orbit Phase, Translunar Phase,
Lunar Orbit Phase, Transearth Phase, Recovery): phases (spans with a body, a start and a duration)
that contain events (instants and burns: separations, docking, EVA, SOI changes, touchdown), with
the key numbers per row (orbit, delta-v, duration).

The Mission Log stays what it is: the flat, complete, chronological event list (every staging,
dock, undock, EVA, terminal). The Profile is the structured summary over the same flight. The two
can be open at once.

Non-goals for this feature:
- No change to rows, cuts or roles, and nothing the replay reads. The one recording change is the
  additive delta-v snapshot node (8.1); the window itself reads committed data only and writes nothing.
- No optimizer cut list or role output (P3, with its own causal-evidence contract, section 10).
- No graphic phase diagram (the roadmap's later "graph with visuals"); the analyzer below is the
  data it will need.
- No planning (what a burn should be), only what was flown.
- No actions. Every row control stays on the Missions tab: Fly, Stash and Seal per vessel, the
  include checkboxes (vessel, interval, chapter, partner journey) and Go to on a docked partner. The
  Profile cannot take the include checkboxes over: its phases are cut at burns and atmosphere passes,
  the include keys at separations and docks, so a phase checkbox would need a new saved key kind
  (Missions-tree analysis 2026-11-22, `missions-tree-vs-profile.md` in the session notes).

## 2. What the player sees

### 2.1 The Profile button

Owner decision (2026-11-22): "Profile above, Log under it (if they don't fit, otherwise Profile to
the left)", refined after the variants mockup: "the Clone / Delete and Profile / Log buttons can have
the same width, and Warp to... can be larger so Loop .. fits under it".

- Basic: the same column as Advanced: `[Profile]` right-aligned on line 1, `[Log]` under it on line 2
  (owner decision 2026-11-22, "for the basic view, i think we can place Log button under Profile
  also"), both at the column C width (72 px), so the two modes put the buttons in the same place.
  Line 2 of a Basic bar keeps `[Run by route]` (when route-bound) to the left of Log.
- Advanced (layout A, as built 2026-11-23 after the Clone / Delete removal of PR #2781 took
  column A): two columns,
  - line 1: `[B: Warp to...] [C: Profile]`
  - line 2: `[B: Loop [x] every [10] sec] [C: Log]`
  - column C is 72 px (`LoopGridColumnCWidth`, the same in Basic), column B 150 px
    (`LoopGridColumnBWidth` = `LoopCellWidth`) so the whole loop row fits under Warp: "Loop" 28,
    the checkbox slot 20, "every" 34, then the period slot (`LoopPeriodSlotWidth` 68: value field
    26, gap 4, unit button 38); a locked period is one label of the period slot. 4 px gap
    (`LoopGridGap`); 226 px in all. "Looped by route" fills column B.
    `MissionsTabColumnSequenceTests` pins the widths and that Profile and Log are drawn outside
    the Advanced branch.

Tooltip (an existing-tooltip-shaped addition, budgeted by `TooltipEchoBudgetTests`):
"The mission's phases: orbits, burns, transfers, landings, docking and EVA, with the numbers."
Click: `ParsekLog.Info("UI", "Mission Profile button: tree=... mission=... name='...'")`, then
`parentUI.OpenProfileWindowForMission(treeId, missionId, name)`.

### 2.2 The window

- Title: `Parsek - Profile: <mission name>` (bare `Parsek - Profile` when untargeted), built by the
  same pure `BuildWindowTitle(prefix, name)` the Log uses.
- Scenes: wherever the Missions window draws (FLIGHT, SPACECENTER). Like the Log, it does not
  survive a scene change: `ParsekUI` is rebuilt per scene (`ParsekFlight.cs:1240`,
  `ParsekKSC.cs:167`) and the window state is instance fields.
- First open: to the right of the main window, cascaded 80 px down / right of the Log's default
  rect so both can be open without covering each other. Default 960 x 420, min 480 x 160,
  resizable from the corner like the Log.
- Body: the log table (boxed column header row, scroll view, row style), then the single-line hover
  strip (`TooltipEchoBox`), then `Close`. Read-only.
- Reopening it from another mission's `Profile` retargets it (one Profile window at a time, like
  the Log).
- Empty state (no recorded flight, or every interval excluded): one muted row,
  "This mission has no recorded flight." (the Log's text).
- Follows KSP UI Scale through `ParsekUiScale.Window` inside the host scope; takes a camera input
  lock while hovered (its own lock id); logs its position changes through `LogWindowPosition`.

### 2.3 Columns

| Column | Width | Content |
|---|---|---|
| Time | 100 | First row: the launch date (the Log's first cell, through the same date-formatter delegate `FormatStepTimes` takes, so the pure builder never calls `KSPUtil`). Every other row: `T+h:mm:ss` from the mission start (`StructureTimeFormatter.FormatElapsed`, floored, hours unbounded). Child lines keep the mission clock (Apollo GET / MET). |
| Element | expands | The tree: indent + caret (phases and vessel headers) + the element label (section 3). |
| Location | 150 | Where it happens, never a repeat of the label; on an event only when it differs from its phase's location or names a site or biome (owner decision 2026-11-22): `Kerbin, Launch Pad`, `Kerbin`, `Mun`, `Kerbin, Shores`, `Kerbin -> Mun` for a transfer (the Log's `StructureLocationFormatter` words). An orbit phase is labelled `Kerbin orbit` and located `Kerbin`. |
| Duration | 80 | Phases and burns: `3m 52s`, `2h 26m`, `4d 2h` (KSP 6-hour days). Instant events: empty. |
| Spent | 74 | Drawn in the house orange, a new palette entry `ParsekUI.StatusColorKind.Orange` = (1, 0.6, 0.25), #ff9940, between the countdown amber #ffcc66 and the error red (owner decision 2026-11-22: "use an orange color for the spent deltav values"); the `~` of an estimate in the same orange. Delta-v spent: per burn, per phase (the sum of its burns, ascent and landing included) and per vessel line, from the game's own delta-v recorded at each step (section 8.1). Flights recorded before that: burns only, `~` estimate (4.6) or `-`. |
| Left | 74 | Delta-v left after the burn, at the phase end, at a staging that dropped fuel, and at the line's end (8.1). Empty for flights recorded before that. |
| Orbit / result | 190 | The orbit a coast holds or a burn produced: `83 x 77 km, 0.1 deg` (Ap x Pe above the surface, inclination). Hyperbolic: `escape, Pe 38 km`. Encounters: `Mun encounter, Pe 229 km`. Surface: `landed 3,854 m`. End rows: the outcome. |

Units: km above 10 km, m below; delta-v in m/s, whole numbers; inclination one decimal; the in-game
cell prints the degree sign (stock KSP does; this doc writes "deg" to stay ASCII). All numbers
InvariantCulture (a unit test reads them).

### 2.4 Hover text

Every row carries a one-line tooltip shown in the strip: the full sentence behind the clipped cells
and, for an interpreted element, the rule and evidence that named it, for example

- burn: `Transfer burn: 29 s at 82 km on the LV-T45, 862 m/s spent, 1,022 m/s left. Orbit after: Ap 11,524 km, Pe 82 km, 0.9 deg; Mun encounter.`
- phase: `Mun orbit: 2h 26m, 229 x 229 km then 45 x 30 km, 2 burns, 2 EVAs.`
- inferred: `Capture burn: 23 s at periapsis. Orbit after inferred from the altitude holding at 140 km for 3 minutes; no orbit sample, so no delta-v.`
- data gap: `Orbit after the burn is unknown: the vessel stayed in physics range and no orbit sample was recorded.`
- vessel header: `Kerbal X Probe: separated at T+0:05:01 (decoupled); 1 phase; ends Orbiting Kerbin. Aboard: probe x1.`
  (the line's final composition, the Missions tab's `Aboard:` label of the vessel row's last interval;
  a re-flown line reads the fork's row; an EVA line has none)
- re-flown: `Flown again from here (Re-Fly at T+0:05:01). The first attempt is not shown.`
- excluded: `Part of this span is not in the mission: excluded on the Missions tab (Advanced).` The
  cut applies in Basic too, as the Log and Watch already apply it (default taken 2026-11-22, owner may
  override); only the wording names where it was set.

### 2.5 Collapse

Phase rows and vessel-line headers have a caret; events have none. Collapse state lives in the window
for the session (keyed by mission id and element key), is not saved, and survives a rebuild when the
element key still exists. Everything is expanded on open (owner decision 2026-11-22).

## 3. The profile model

A mission profile is a TREE. Its first row is a collapsible `Summary` (owner decision 2026-11-22)
with one row per fact: bodies visited and landing / splashdown sites with biome; crew per vessel with
EVA count and total EVA time and each kerbal's outcome; career results (science, funds and
reputation from the ledger rows tagged to the mission's recordings, through ELS); craft facts (part
count and stage count at launch, the vessels the mission produced and how each ended, the debris
count). Speed, max-Q and heating are out (owner decision). Below the summary, three row kinds:

1. Vessel line (header row): one physical vessel through the mission (section 5). Label: the vessel
   name, then the crew in parentheses (`Kerbal X (Jebediah, Bill, Bob)`), an EVA kerbal as
   `EVA: Jebediah Kerman`. The mission's first launched vessel is the root line.
2. Phase: a span of one line, on one body (or body to body for a transfer), with a start, a
   duration and the burns and events inside it. Phases of one line tile its time with no gaps and no
   overlaps AT EACH LEVEL: a sub-phase (a Flyby inside a Transfer, a Drive inside a Surface stay)
   tiles inside its parent phase.
3. Event: an instant or a burn inside a phase.

A child vessel line is nested under the EVENT that created it (separation, undock, EVA), one indent
deeper, followed by its own phases (owner decision 2026-11-22). It keeps the mission clock, so the
Time column steps back while reading a child; the indent and the caret carry the reading.

Short EVAs collapse: an EVA kerbal line whose only content is a board back into the same vessel
within 2 minutes is one event row, `EVA: Jebediah Kerman (2s), boarded`, not a child line. Debris
never becomes a line (as in the Missions tab).

### 3.1 Phase kinds

| Kind | Label | Starts | Ends |
|---|---|---|---|
| Prelaunch | (folded into Launch) | recording start on a launch site | liftoff |
| Ascent | `Ascent` | liftoff | atmosphere exit (atmosphere body); on an airless body the low-band exit, or the burn that reaches orbit when it ends inside the band |
| Circularization | `Circularization` | the ascent's end with the orbit still falling | end of the burn that reaches orbit |
| Injection | `Injection` | the ascent's end | end of the burn that leaves the SOI or makes an encounter with no parking orbit (v3 role `injection`) |
| Orbit | `<Body> orbit` | an orbit is reached (circularization, capture, an SOI exit into a bound clear orbit, the end of a raise burn) | the burn that leaves it, or the line's end |
| Transfer | `Transfer to <Body>` / `Return to <launch body>` | start of the departure burn (out of an orbit) or end of an injection | the capture (burn or aerocapture), a direct entry, the low band of an airless body, or an SOI exit into a bound clear orbit with no later encounter |
| Flyby (sub-phase) | `<Body> flyby` | SOI entry with no capture before the SOI exit | SOI exit (inside the transfer) |
| Rendezvous | `Rendezvous with <partner>` | the first burn in the orbit phase that ends with a dock (section 4.5) | the dock |
| Docked | `Docked to <partner>` / `Docked: <partner> aboard` | dock | undock, or the line's end |
| Deorbit | `Deorbit` (atmosphere body) | start of the burn that drops Pe into the atmosphere | first atmosphere entry |
| Descent | `Descent` (airless body) | start of the burn that drops Pe below the surface, or the transfer's arrival at the low band | touchdown |
| Reentry | `Reentry` | first atmosphere entry of the final descent (skip dips fold in as `Skip` events) | touchdown, splashdown, or destruction |
| Aerocapture | `Aerocapture` | first atmosphere entry on an escaping orbit | end of the raise burn that lifts Pe out of the atmosphere (or the next phase) |
| Aerobraking | `Aerobraking` (one event per pass) | first braking pass from a bound orbit with Pe in the atmosphere | end of the raise burn, or the final pass's entry (then Reentry) |
| Atmosphere pass | `Atmospheric flyby` / `Atmosphere pass` | a pass that exits still escaping, or whose orbit after nothing decides | atmosphere exit |
| Surface stay | `On the surface` | touchdown | liftoff, or the line's end |
| Drive (sub-phase) | `Drive` | surface motion over 30 s and 50 m inside a Surface stay, with Surface on both sides (a takeoff or landing roll is not a drive); stop-go runs within 60 s merge | stop |
| Flight | `Flight` | takeoff on an atmosphere body that never reaches space | landing |
| Suborbital hop | `Suborbital flight` | liftoff that reaches space (or, airless, leaves the low band) but never an orbit, with no injection | landing or entry |
| EVA | `EVA`, `On the surface (EVA)`, `Jetpack flight` | the EVA kerbal line's start | boarding, or the line's end (EVA lines use their own small rule set, 4.3) |
| Unclassified space | `In space` | any space stretch no rule can claim | next phase (logged, section 9) |

### 3.2 Event kinds

| Kind | Label | Source |
|---|---|---|
| Launch | `Launch` (location: the launch site) | recording start with `LaunchSiteName` / Prelaunch |
| Liftoff / Takeoff | `Liftoff` | first leave-surface boundary |
| Stage separation | `Stage separation (n pieces)`; the hover names each piece's ending (crashed, burned up, landed, in orbit) from its debris recording's terminal state, and the delta-v the dropped fuel took with it (owner decision 2026-11-22) | JointBreak / Breakup branch points whose children are all debris, grouped per batch (the Log's batching; `debrisCount`) |
| Separation | `Separated: <vessel>` | branch point to a controlled child (a child line opens) |
| Breakup | `Breakup (n pieces, m vessels)` | a Breakup branch point with controlled survivors (their lines open) |
| Burn | named by what it did (4.4, plain names: owner decision 2026-11-22) | the burn definition of 4.3 |
| Atmosphere exit / entry | `Atmosphere exit` / `Atmosphere entry` | env boundary (physics sections) or the conic crossing the atmosphere top (on-rails sections) |
| SOI change | `Entered Mun SOI` / `Left Mun SOI` | body change between sections |
| Docking / Undocking | `Docked: <partner>` / `Undocked: <partner>` | Dock / Undock branch points (partner names via the Log's partner resolver) |
| Docked by another mission | `Docked: <partner> (<other mission>)` / `Undocked: <partner> (<other mission>)` | a dock another mission recorded onto this mission's vessel: it lives in the other tree, so the line builder reads it from the same source the Missions tab's `Docked partner` rows read; named, never expanded (default taken 2026-11-22, owner may override). Built: `MissionCrossTreeDock.FindLinks` plus the partner journey walk for the undock, over the request's `CommittedTrees` / `TreeMissionName` (filled by the UI; null: none); placed at its UT in the deepest phase covering it, or after the line's End row with the hover `<label>: docked by another mission after this recording ended; not recorded here.` when this recording had stopped; an excluded interval drops it; a Board claim reads `Boarded: <kerbal> (mission '<m>')` |
| Switched to | `Switched to <vessel>` | the first recording of a vessel that joined the tree at a vessel switch (`VesselSwitchContinuation`); its line starts there, top level unless its identity already has a line, which it then continues |
| EVA / Board | `EVA: <kerbal>` / `Boarded: <kerbal>` | EVA / Board branch points |
| Ground parts | `Part placed: <part>` / `Inventory transfer` | `GroundPartPlaced` / `InventoryTransfer` branch points, on the EVA line that made them |
| Parachutes | `Parachutes deployed` | first `ParachuteSemiDeployed` / `ParachuteDeployed` of a reentry |
| Touchdown / Splashdown | `Touchdown` / `Splashdown` | surface boundary at the end of a descent / reentry (splashdown when the terminal or the boundary point says splashed) |
| Re-flown | `Flown again from here` | the rewind UT of a supersede bridge (section 5) |
| Recovery | `Recovered` | terminal Recovered |
| End | `End: Orbiting Kerbin`, `End: Landed`, `End: Destroyed` | terminal state (the Log's `FormatEndLabel` words); none for a leg that ends Docked / Boarded into a line that continues |

Fairing jettison, landing gear, lights, solar panels, antennas, RCS, throttle and robotics are Log
material, never profile rows (Q10, Q14).

## 4. Deriving the elements (the phase analyzer)

### 4.1 Contract

One pure static analyzer, `MissionPhaseAnalyzer.Analyze(VesselLineInput line) -> PhasePlan`, in the
Recording module (model layer, next to the BurnCuts helpers it reuses, so a later optimizer change
can call it without a backward layer edge). Input:

- one vessel line's pieces in UT order (section 5), each with its `TrackSections`, its `PartEvents`,
  its terminal state, its terminal orbit with `TerminalOrbitEpoch`, `StartSituation`,
  `LaunchSiteName`, `StartBodyName`, `EndBiome`, and the line's branch points (type, UT, cause,
  partner, debris count);
- the UT spans excluded by the mission's include set (section 5);
- a body-facts provider: the existing `RecordingOptimizer.BodyFacts` (radius, atmosphere depth, SOI
  radius, gravitational parameter), filled from the live `CelestialBody` by the caller and from the
  stock table headless, so the analyzer itself names no live type.

Output: the ordered phases with their sub-phases, events and burns, the orbit facts, and for every
element the rule that named it and the evidence tier it used (stored / inferred, 4.3), plus the data
gaps (4.7). No Unity calls, no statics, no store reads, no logging inside the core (the caller logs
one summary from the plan). No cut list and no roles (P3).

The analyzer NEVER reads `SegmentRole`, `SegmentPhase` or row boundaries (F1). The same flight as one
version-1 recording and re-cut into rows produces the same plan (row independence, 11.2).

### 4.2 Why not a forward pass

A span's meaning often depends on what comes AFTER it, so a conditional forward pass that labels each
stretch as it arrives is wrong by construction. The owner's example: a vessel coming from deep space
touches an atmosphere for the first time. That one touch can be an orbital capture (it climbs out
bound), a trajectory adjustment (a braking pass on an orbit already bound, or a pass that leaves it
still escaping), the first dip of a skip entry (it hops out for under 2 minutes and comes back down),
or the start of a direct descent (it never climbs out). Only the rest of the line decides which, and
the same holds elsewhere: a burn in orbit is a phasing burn only if a dock follows in that orbit; an
SOI pass is a flyby or an arrival depending on a later capture; a takeoff is a launch or a flight
depending on whether the vessel reaches space; a transfer ellipse whose periapsis is under the
surface is an encounter, not a descent, when an SOI change comes first; a descent ends in a landing
or an impact.

Committed recordings are complete, so the profile reads each line with full hindsight. The optimizer
cannot: its cuts must not move when a flight's future is flown again (a Re-Fly replaces everything
after the rewind point, and missions store interval keys on rows). So hindsight is allowed in the
profile's LABELS, and the day the optimizer reads anything from this analyzer (P3), it reads a
separate boundary layer computed from causal evidence only, with a test that truncating a line never
moves an earlier boundary (section 10).

### 4.3 Three stages: segment, interpret, group

Stage 1, segmentation into neutral spans. The line's sections become a sequence of spans with facts
only and no meaning. Pieces that share an instant (a chain boundary, a parent and a continuation) are
de-duplicated by UT, the later piece winning. Facts:

- Regime: `Surface` (SurfaceMobile / SurfaceStationary), `Air` (Atmospheric), `Low` (Approach),
  `Coast`, `Burn`. A physics section takes its regime from its environment. An on-rails
  `OrbitalCheckpoint` section is always a coast (a packed vessel cannot thrust), and its Air / Coast
  split comes from its conic against the atmosphere top, never from its environment label, because a
  background on-rails span carries one fixed label (`packedInsideAtmosphere`, CLAUDE.md "On-rails BG
  vessels").
- Body: from the section's first checkpoint (`checkpoints[0].bodyName`) or first frame point
  (`frames[0].bodyName`), the optimizer's own rule (`RecordingOptimizer.cs:2954`); `TrackSection` has
  no body field.
- Points read, and only these: the first frame point of each section (body), the frame points at a
  burn's start and end (altitude for the hover), the first and last point of a Surface run (drive
  distance, biome fallback, splashed or landed), and the point at each touchdown. Never point
  velocities (surface-relative near the planet, F3). Per-section min / max altitude is used only as a
  bound: `SplitAtUT` copies a whole section's values to both halves (`RecordingOptimizer.cs:2569`,
  `:2591`).
- Burns, with ONE definition shared with BurnCuts (reuse its run builder, `ExtendBurn`): a run of
  physics `ExoPropulsive` sections plus engine ignite / shutdown part events in `Air` and `Low`, gaps
  up to 10 s merged, ended by an on-rails gap or an SOI change; a burn continuous across the
  atmosphere exit stays one burn. A firing lit on the ground or during the climb (only floor spans
  between it and the surface) belongs to the Ascent, whose Spent is the fall in Left across it: its
  burn row starts where the space run does (the atmosphere exit, or the low-band exit on an airless
  body), so no engine time is counted in two phases (review finding 2026-11-22: a pad-to-orbit
  firing was filed in Prelaunch and counted there and again in the Ascent). Burn time is engine time (ignite to last shutdown); the section span
  is used only when no engine event exists (RCS-only, old data). Under 0.5 s and 1 m/s is an RCS tap,
  dropped.
- Orbit facts at each span end, two axes instead of one kind (the review's fix for "every orbit
  after an aero pass reads falling"):
  - energy: `bound` (closed, Ap inside the SOI) or `escaping` (open, or Ap beyond the SOI);
  - periapsis: `clear` (above the atmosphere top, or the surface on an airless body), `in-air`
    (inside the atmosphere), `sub-surface` (below the surface);
  - plus `encounter` (hindsight): the coast ends in an SOI change before it reaches periapsis or the
    atmosphere. Encounter beats any periapsis reading, so a Mun transfer ellipse with Pe under
    Kerbin's surface is a transfer, not a descent (mun-landing-recorded's corrections leave Pe at
    -201 km and -229 km).
  - Stock's three kinds map onto these: in orbit = bound + clear; falling = in-air or sub-surface
    without encounter; leaving = escaping or encounter. `ClassifyOrbitKind`
    (`RecordingOptimizer.BurnCuts.cs:577`) gains the axes as a richer result; the optimizer keeps
    reading its three-kind projection, unchanged.
- Evidence tiers, strongest first:
  - stored: elements of a non-predicted on-rails checkpoint in the span (`isPredicted` segments are
    extrapolations, never flown facts, and are skipped as the optimizer does,
    `RecordingOptimizer.BurnCuts.cs:476`); the terminal orbit only when `TerminalOrbitEpoch` is at
    or after the end of the line's last burn (bdock-recorded's root carries the phasing burn's
    elements as its terminal orbit while three approach burns follow);
  - structural: the line's own boundaries (an SOI change, an atmosphere or ground entry, an
    atmosphere exit implies the orbit was falling there), and an altitude that holds within 2 % for
    longer than a quarter of the circular period at that radius, or within 0.1 % for two minutes
    (bound and clear, inferred);
  - none: unknown.
- Boundary facts between spans: SOI change, atmosphere entry / exit, low-band entry / exit,
  touchdown / liftoff, the branch points at that UT, and the excluded spans.

Row cuts, roles and recording boundaries are not facts; they never reach stage 2.

Stage 2, interpretation over the whole line. Rules are applied in STRATA; each stratum reads only the
stage-1 facts and the results of lower strata, never its own output, so nothing is circular and
nothing is order-dependent inside a stratum:

- (a) Orbit resolution: fill every span end's orbit facts from the strongest tier available, in both
  directions (an unknown coast between two known ones with no burn between takes their common
  reading); record the tier.
- (b) Anchors: liftoff, touchdown, splashdown, SOI changes, atmosphere entries and exits, dock,
  undock, EVA, board, separation, breakup, terminal. These are fixed instants every later stratum
  hangs on.
- (c) Atmosphere passes. A PASS is an atmosphere stretch entered from space; hops back out under
  2 minutes belong to the same pass (the version-2 rule, so a skip is one pass); a run of passes with
  only coasts between them and no burn is a GROUP. Drag cannot lift the low point out of the
  atmosphere, so after a braking pass the periapsis stays in-air until a burn raises it. A group is
  read by the energy before its first pass and by how it ENDS:

  | Rule | Before the first pass | How the group ends | Reads as |
  |---|---|---|---|
  | direct-entry | any | one pass, no exit: touchdown, splashdown, destruction | Reentry (or Impact) from the first entry |
  | skip-entry | any | every hop out under 2 minutes, then the ground | Reentry from the first entry, each hop a `Skip` event |
  | aerocapture | escaping | exits bound (stored or structural: the next pass recurs with no burn), then a raise burn or the line's end in space | `Aerocapture` from the first entry to the end of the raise burn, then `<Body> orbit` |
  | aerobraking | bound, Pe in-air (a capture burn or an earlier pass put it there) | exits bound, passes recur, then a raise burn | `Aerobraking` (one `Pass n` event each) to the end of the raise burn, then `<Body> orbit` |
  | brake-then-land | escaping or bound in-air | passes with long hops out, the last one has no exit | `Aerocapture` / `Aerobraking` for the passes before the last, Reentry from the last entry |
  | braking-flyby | escaping | exits still escaping | `Atmospheric flyby` (sub-phase of the transfer) |
  | unknown-pass | any | exits, orbit after unknown, nothing later decides | `Atmosphere pass` (neutral; the hover says why) |

  The owner's example reads directly off this table: the first touch coming from deep space is a
  capture (aerocapture), an adjustment (braking-flyby, or aerobraking when the orbit was already
  bound), the start of a skip (skip-entry) or of a direct descent (direct-entry); only the end of its
  group decides.
- (d) Burns: each burn is named from the resolved orbit facts on its two sides and the anchors around
  it (table 4.4), including the hindsight cases (a burn followed by a dock in the same orbit is a
  phasing or approach burn; a burn whose coast ends in an encounter or escape is an injection).
- (e) Grouping (stage 3, below).

Inside a stratum, rules are tried in a fixed priority order; length breaks a tie only between two
rules of the SAME priority (the longer match wins). A span claimed by a rule is not claimed again. A
totality check runs after (e): every span is claimed exactly once, else the leftover becomes
`In space` with a logged reason. Rules are data (one table of rule objects: name, stratum, priority,
`Match(facts, i)`, `Apply`), each unit-tested alone, and every claim records rule and evidence tier
for the hover and the log.

Stage 3, grouping into phases (stratum e). Interpreted spans merge into the phases of 3.1: a launch's
Surface + Air spans become Ascent; consecutive braking passes become one Aerobraking phase with one
event per pass; skip dips fold into the Reentry; a transfer collects its injection, corrections,
flyby sub-phases and SOI changes up to its end; burns and the events of 3.2 are placed in the phase
that covers their UT. Phases tile the line at each level. Endings that are not a landing: a line
destroyed during ascent or descent ends that phase with `End: Destroyed` (and an `Impact` phase when
it fell from space); a breakup with survivors is an event whose survivors' lines open there.

EVA lines skip strata (c) and (d): a kerbal is not a vessel the orbit rules describe (versions 2 and
3 of the split rules exclude EVA for the same reason). Their own rules: `EVA` in space, `On the
surface (EVA)` from a touchdown, `Jetpack flight` for a surface-to-surface hop, then `Boarded:
<vessel>` or the end. Ground-part and inventory branch points are events on that line.

### 4.4 Naming burns

| Before -> after (resolved facts) | Label |
|---|---|
| falling -> bound clear, after an ascent | `Circularization burn` |
| bound clear -> encounter / escaping | `Transfer burn` (out to another body), `Return burn` (back toward the launch body), `Escape burn` (no encounter) |
| falling -> encounter / escaping, after an ascent | `Injection burn` |
| inside a transfer, the encounter or escape unchanged | `Correction` |
| escaping / encounter -> bound, after an SOI entry | `Capture burn` |
| escaping -> bound in-air | `Capture burn (aerobraking orbit)` |
| bound in-air -> bound clear, after an atmosphere pass | `Periapsis raise` (ends an aerocapture / aerobraking phase) |
| bound clear -> bound clear | `Orbit change`; `Plane change` when inclination moves over 2 deg and Ap / Pe under 5 %; `Phasing burn` / `Approach burn` inside a Rendezvous (several approach burns within 10 minutes collapse into one row, `Approach burns (3)`) |
| bound clear -> falling | `Deorbit burn` (atmosphere body) / `Descent burn` (airless) |
| in the low band or the reentry, ending at touchdown | `Landing burn` |
| one side unknown and no structural fact decides | `Burn` |

### 4.5 Rendezvous and docking

A Dock branch point involving this line makes stratum (d) look back inside the Orbit phase that ends
with the dock: the Rendezvous starts at the first burn in that phase after the partner exists in the
same SOI (its own line's start, or its launch UT for a cross-tree partner). When no burn exists (the
partner came to us), there is no Rendezvous, only the Docking event. Known limit: an unrelated orbit
change between the partner's arrival and the dock folds into the Rendezvous; P2's closest approach
from the two recorded trajectories fixes it.

At the dock, either ship can be the one KSP keeps. The merged recording takes the DOMINANT vessel's
identity (`VesselIdentity.cs:150-170`): in bdock-recorded the mission's own Kerbal X is absorbed into
the partner (its leg ends `terminalState = Docked`; the merged leg carries the partner's pid and
guid). The profile follows the mission's own ship either way: its line shows `Docked to <partner>`
(own ship absorbed) or `Docked: <partner> aboard` (partner absorbed) until the undock, and resumes
after it on the side `VesselIdentity` links to the own ship (identity edge 6, the re-pidded undock
continuation; `MissionStructureListBuilder.ResolveUndockOwnChild` gives the same side). The partner's
side after the undock is named, never expanded, when it belongs to another mission.

### 4.6 Numbers

- Orbit: Ap and Pe above the body's surface (equatorial radius), inclination, from stored elements;
  an inferred orbit prints `about 140 km` and no elements.
- Burn duration: engine time (4.3). The hover names the engines that fired (part titles via the same
  resolver delegate the Log uses).
- Delta-v: recorded figures first (section 8.1): spent per burn = left before - left after (vacuum
  figures), per phase and per line the sum of their burns, left read straight from the snapshot. For
  flights recorded before 8.1 exists (every committed recording and fixture today) the fallback is an
  estimate per burn: when the orbit just before and just after a burn are both STORED (not inferred)
  and the burn is shorter than 120 s, propagate both conics to the burn's midpoint UT and take the
  magnitude of the velocity difference (impulsive approximation; a few percent at most for the burns
  this applies to). Propagation goes through `TwoBodyOrbit.TryCreateFromSegmentStoredFrame`, which
  reads no live `Planetarium.Zup`; a velocity-difference magnitude does not depend on the frame, so
  the core stays pure. CLAUDE.md names `TrajectoryMath.BoundaryFrame` as that variant's only reader;
  the build adds this reader to that sentence. Estimates show `~`, carry no Left value and are never
  summed (a total without ascent and landing would read as the mission's delta-v); `-` with a reason
  where neither source exists.
- Mission summary: the Summary block of section 3 (bodies and landing sites, crew and EVA time,
  career results, craft facts) plus the total duration in the root row. Maximum altitude appears only
  as the result of a phase that shows no orbit: a suborbital flight, an aircraft flight, an aborted
  ascent, a flyby (`reached 85 km`; owner decision 2026-11-22), from the frame points at the
  per-section maximum.

### 4.7 Data gaps

The analyzer names what it could not read instead of guessing: `orbit unknown (no orbit sample)`,
`orbit inferred from altitude`, `delta-v unknown (no stored orbit on one side)`, `section missing`.
The cell shows `-` or the inferred wording, the hover the reason, and the build summary counts them.

## 5. Vessel lines

Lines are built on the model the Missions tab already uses, so the Profile's lines agree with the
tab's vessel rows and include keys, and no second "same vessel" rule exists. The line builder
starts from `MissionVesselRowBuilder.Build` (one row per physical vessel / EVA kerbal, depth =
separation lineage, own-side undocks, docked partner rows and the interval keys already resolved)
rather than a second walker, and a fixture test pins that the Profile's line set equals the tab's
vessel-row set on every committed mission fixture (Missions-tree analysis 2026-11-22). Underneath,
that builder uses:

- `MissionStructure` (`MissionStructureBuilder.Build(tree)`, the controlled-leg graph), the through
  line from `MissionThroughLineBuilder.ContinuationSuccessor` (it follows `IsBranchContinuation`, the
  recorder's child order), and `VesselIdentity.Resolve` with the link rows and `partnerLegIds` from
  `MissionVesselNaming` (whose legs are a partner's, not ours). The UI asks for these the way the
  Log does (`GetMissionView` returns `(MissionStructure, MissionThroughLineView)`).
- A line is one identity component in time order: chain pieces (the next piece is the next HIGHER
  `ChainIndex` of the chain branch, holes tolerated), continuations past branch points
  (`ParentContinues` and its inference), switch continuations, and the dock / undock handling of 4.5.
- Re-Fly: the UI computes the effective recording id set where ERS reads are allowed and passes it
  in, with the supersede rows (TIP -> fork). The line builder bridges HEAD to the fork through those
  rows and puts a `Flown again from here` event at the rewind UT. `EffectiveState.WalkSlotVessel` is
  not a line source (it stops at dock, undock and breakups and does not follow supersede,
  `EffectiveState.cs:1359-1398`). The Missions module never calls `ComputeERS` itself
  (`ParsekScenario.Instance` would be a headless leak).
- During an active Re-Fly session, ERS hides the re-flown subtree (`SessionSuppressedSubtree`,
  `EffectiveState.cs:21`), while the Log reads the raw tree and still shows it. The Profile follows
  the Log during the session (the committed attempt) and shows only the flown-again path after the
  merge (owner decision 2026-11-22), so the id set the UI passes ignores the session suppression.
- Include set: per INTERVAL and non-cascading, exactly the Missions contract
  (`MissionVesselRows.cs:25`, `MissionIntervalSelection.cs:14`): an excluded interval removes the
  events inside it and clips the phases that overlap it (the clipped phase's hover says so); a phase
  entirely inside excluded intervals is dropped; a line with nothing left is dropped. A child line is
  not dropped because its parent's interval is.
- Debris lines are dropped (they appear as Stage separation counts). Cross-tree partners (the tab's
  `Docked partner` rows) are named, never expanded: their own mission has its own Profile.

`ReaimClassifier` moving to this line model (todo REAIM-CLASSIFIER-FRAGILE-TO-MEMBER-SPLITS) is a
later change.

## 6. Window behaviour

- Build: on open, and on a Layout event when the change signature moves: `RecordingStore.StateVersion`,
  the ERS cache key (store + supersede version), `DisplayNames.Version`, the mission's name and include
  set (the Log's `ComputeChangeSignature` plus the ERS term). Never per frame. A rebuild logs
  `Profile window rebuilt on change: ... rows=a->b`.
- Cache: the analyzer's `PhasePlan` per line is cached by the ordered piece ids plus `StateVersion`,
  so a rename or an include toggle re-flattens rows without re-analysing.
- A target tree that no longer exists closes the window with an Info line (the Route History's
  deleted-route rule). A scene change closes it (2.2).
- Loops, clones and supply routes: the Profile describes the recorded flight once; a looped mission
  says `Loops every <period>` in the root hover only.
- Basic and Advanced: the same window and columns.

## 7. Seams for automation and the gallery

- `UiAction op=target window=profile mission=<id>` opens it on a mission and reports the row count
  (the Log's `op=target` contract, `StepCountForTesting`).
- A gallery state `profile-mun-return` built from the real model types fed through the real analyzer
  over a synthetic recording (`UI/Gallery/`, no store reads), plus `profile-empty`.
- The GUI census lanes (GUI-1 / GUI-2) photograph it opened on their densest mission; the window is
  added to the seam's window table, `TestCommandUiAction.cs` vocabulary and `hlib` (the
  `GuiCensusSeamVerbTests` byte-equality cells move in the same commit).
- The GUI mirror (`harness/tools/gui_mirror.py`, `docs/dev/design-gui-mirror.md`) is updated in P1b
  (owner request 2026-11-22): `LAYOUT_EPOCHS` gets a `profile` entry and a new `missions` instant (the
  button column changes), the census lanes are re-flown so the mirror shows the Missions tab with the
  new buttons (Basic and Advanced) and the Profile window opened on a mission, the gallery states
  `profile-mun-return` / `profile-empty` are photographed as mocked captures, and the page is rebuilt
  from that census. The screenshots and the rebuilt page are sent to the owner with the P1b PR.

## 8. Derived versus stored

Apart from the delta-v snapshots (8.1), everything is derived at view time from data already committed: sections, checkpoints, part
events, branch points, terminal state and orbit, launch site, biomes, names, and the few point reads
listed in 4.3. No format bump, no ledger row, nothing the replay reads.
The collapse state is window memory only.

Stored data comes only with P2 (F3): one trajectory sidecar format bump adding an extensible
per-section extension block (raw osculating Pe / Ap / inc / ecc and the stock patch-end transition at
space-section open and close; delta-v expended and mass per powered section), with the upgrade step
and a new `FormatGolden` folder per `docs/dev/design-save-format-policy.md`. That is a separate design
and PR; the Profile reads the stamps when present and falls back to 4.6 when not.

Cost: sections, checkpoints, part events and a handful of points per section. A 6-hour Mun flight is
about 60 sections and 300 part events: microseconds, once per build.

### 8.1 Recorded delta-v (owner decision 2026-11-22)

Owner: "we should record how much delta-V the vessel and stages had at every step (launch, before
burn, after burn etc.) and use the real figures provided by the game". So the recorder stores the
game's own delta-v readout (the stock Delta-V app, `Vessel.VesselDeltaV`) as snapshots:

- When: at recording start (prelaunch, or the first frame of a continuation, which names the split,
  merge, switch or resume that started it), at each burn start and end (the recorder's own
  any-engine-thrusting edge, read from the engine set its ignite / shutdown polling keeps; the
  Profile groups these edges into burns by the definition of 4.3), at each staging
  (`onStageActivate`), at each separation, dock, undock, EVA and board (the focused child's start),
  and at the recording's end. Event-driven, a few dozen per flight, never per frame (the recording
  design principle). The game recomputes delta-v a few frames after a change, and after a vessel
  event only once `GameSettings.DELTAV_VESSEL_EVENT_DELAY_SECS` (of `Time.time`) has passed, so a
  snapshot that describes the vessel AFTER an event waits that delay plus 0.25 s and then for
  `VesselDeltaV.IsReady` with no simulation running before it reads (members checked with
  `ilspycmd`: `TotalDeltaVVac`, `TotalDeltaVASL`, `TotalDeltaVActual`, `SimulationRunning`,
  `OperatingStageInfo` of `DeltaVStageInfo` with `stage`, `deltaVinVac`, `stageBurnTime`), records
  the UT it was taken, and is written as unknown 3 s after that wait. The recorder also keeps its
  LAST READY READING of the focused vessel (the totals and stages, re-read on every physics tick on
  which stock is ready; one reading in memory, nothing stored per frame). A snapshot that describes
  the vessel BEFORE an event is written from it at once when it is at most 1 s of game time older
  than the event (`DeltaVSnapshotTracker.CachedReadingMaxAgeSeconds`), stamped with the reading's
  own UT and logged `source=cached age=Ns`. The bound bridges the few ticks while stock's simulation
  runs (the reading is normally one tick old) and stays under stock's event delay, so a reading from
  before an EARLIER event whose figures are still pending is refused; past it nothing is written.
  Three reads use it: a staging writes a `PreStaging` snapshot from it (only from it: without a
  recent reading, or while an earlier staging's settled read is still pending, none is written)
  next to the settled `Staging` read about 1.25 s later, so a staging drop is the pair's difference
  and never the fuel burned since an older snapshot (review finding 2026-11-22: DV-1's run
  `2026-11-22_1918` staged at UT 49.36, wrote its Staging read at 50.62, and the snapshot before it
  was the Start at 36.54); a burn start is written from the reading of the tick before ignition, and
  waits for the first ready tick only when no recent reading exists; the end reads live and falls
  back to the kept reading when stock is not ready at the stop tick (the chain-boundary stop right
  after a staging read Unknown in that run). A fresh recording start, which follows no vessel
  event, reads on the first ready tick (a launch ignites in the same second, and a settled read
  would describe the vessel after that burn: DV-1's first flight read Start 0.8 s after the first
  burn start); a burn start that arrives while a burn end is still
  pending withdraws that end and files nothing (a throttle flicker, not a new burn: DV-1's first
  flight wrote four burn starts inside one second at MechJeb's cut-off); the end reads at once (there is no later frame) and takes the last computed figures without the
  `FlightGlobals.ready` gate, because stock clears that flag in `FlightGlobals.OnSceneChange` on the
  same `onGameSceneLoadRequested` that stops the recorder at scene exit (review finding, 2026-11-22). A request of a kind already pending keeps the
  first. `GameSettings.DELTAV_CALCULATIONS_ENABLED` off means no snapshots. As built (P1a): an end
  written at a chain-boundary stop that resumes as a false alarm (a debris-only split) is removed
  again, and requests still pending at that stop complete after the resume; a vessel switch or the
  move to the background recorder ends the focused span with no end snapshot (stock has no figures
  for a vessel the player is not flying; the switch back starts a new span); at most 400 snapshots per
  recording (one Warn when reached). End marks a stop, not the recording's last snapshot: a resumed
  committed recording keeps its earlier End and adds a Resume after it. The boring-tail trim moves
  snapshots past the new end onto it (no burn happens in a trimmed tail) instead of dropping the End.
  Code: `Recording/DeltaVSnapshot*.cs`,
  `Recording/FlightRecorder.DeltaV.cs`.
- What: UT, the trigger kind, total vacuum delta-v, total delta-v at the current pressure, and per
  stage the stage number with its vacuum delta-v (and burn time). Vacuum figures drive Spent and Left,
  because the at-pressure figure changes with altitude while no fuel is spent; the at-pressure total
  rides along for the ascent hover.
- Which vessels: every recorded vessel the game computes delta-v for (owner decision). Decompiled
  fact (KSP 1.12.5, `VesselDeltaV.FixedUpdate`): the stock calculation returns early unless the vessel
  is `FlightGlobals.ActiveVessel`, loaded and `FlightGlobals.ready`, so in practice only the vessel
  the player is flying has figures. The focused recorder (`FlightRecorder`) takes the snapshots; a
  background-recorded vessel takes none until the player switches to it (its switch continuation is
  then focused). Debris and EVA kerbals record none. A separated probe flown later gets real figures
  for the part flown; its first span shows the estimate or `-`.
- Spent and Left: Left is the snapshot after an element; a burn's Spent is the vacuum left before
  minus after, less the drops of the stagings between the two reads; a staging that drops fuel
  lowers Left without anything being spent, and the `Stage separation` row shows that drop in its
  hover. A drop is the `PreStaging` read minus the `Staging` read after it (every pair of a batched
  row summed); it still holds the fuel burned in the second or two between them. A staging with no
  complete pair (flights recorded before the pre-staging read, an Unknown or late settled read, a
  separation fired outside staging) has an unknown drop, and every Spent across it is unknown (`-`,
  gap `delta-v unknown (no reading just before a staging)`) rather than a split of the fall in Left
  between burned and dropped fuel; a burn then falls back to the estimate of 4.6. The stock Delta-V app can be switched off in
  the game settings; then no figures exist and the Profile falls back to the estimate of 4.6.
- Storage: an optional `DELTAV` child node of each recording's metadata node, one `SNAPSHOT` value
  line per snapshot (`ut,trigger,Ok,vacuum,seaLevel,actual,stage:vacuum:burnTime;...` or
  `ut,trigger,Unknown`, invariant culture). A new optional child node is ADDITIVE under
  `docs/dev/design-save-format-policy.md` (no format bump, no golden folder; older builds ignore it and
  drop it on their next save, which is acceptable for display data). It is small (a few dozen short
  lines per recording), which keeps the `.sfs` metadata light. Optimizer splits and merges carry the
  snapshots with the UT range they fall in (`SplitAtUT` copies each snapshot to the half that holds its
  UT; a merge concatenates), so rows stay independent of the profile.
- Old flights: no snapshots; the Profile uses the estimate (owner decision). Snapshots never change
  what is replayed: ghosts, spawns and the ledger do not read them.

This is the one part of the feature that changes what is recorded, so it lands first (phase P1a,
section 10): every flight flown after it ships carries real figures.

## 9. Logging

- `[UI]` Info: button click (tree, mission, name), window opened (tree, mission, lines, rows, title),
  rebuilt on change (rows before -> after), closed, closed for a deleted tree.
- `[Profile]` Verbose, one summary per build: `lines=N phases=N events=N burns=N dvEstimated=a/b
  inferred=N orbitUnknown=N unclassified=N reflown=N clippedByInclude=N` (batch counters).
- `[Profile]` Verbose per line (bounded by the line count, under 20 in practice): line id, pieces,
  first and last UT, phase kinds in order with the rule that named each, gaps by reason.
- `[Profile]` InfoRateLimited when a line has an `In space` phase, keyed by line id and reason.
- Recorder: one `[DeltaV]` Verbose line per snapshot (recording id, trigger, UT, vacuum total,
  stage count, ready or timed out) and one Info summary per recording at commit (snapshots, unknowns).
- Analyzer core: no logging (pure). The new `Profile` and `DeltaV` tags go into `scripts/arch/log_tags.json`
  (`python scripts/arch/logtags.py ... --write`) in the build PR.

## 10. Code layout and phases

| Piece | File | Module / layer |
|---|---|---|
| Phase analyzer, pure (stage 1 facts, strata, grouping) | `Recording/MissionPhaseAnalyzer.cs` (+ `.Facts.cs`, `.Passes.cs`, `.Burns.cs`, `.Grouping.cs`, `.Placement.cs`) | Recording / model |
| Element kinds and phase classes (F4's seed, no player words) | `Recording/ProfileElementKinds.cs` | Recording / model |
| Player labels for the kinds | `Missions/ProfileLabels.cs` | Missions / features |
| Line builder over `MissionStructure` + through line + `VesselIdentity` | `Missions/MissionProfileLines.cs` | Missions / features |
| Profile builder: lines + plans -> rows with depth, cells, hovers | `Missions/MissionProfileBuilder.cs` (+ `.Rows.cs`, `.Summary.cs`); row model `Missions/MissionProfileRows.cs` | Missions / features |
| Window | `UI/MissionProfileWindowUI.cs` (chrome shared with `StructureListWindowUI` through a small extracted helper, not a third mode of that class: the tree, carets and six columns would branch every draw method) | UI / presentation |
| Button | `UI/MissionsWindowUI.MissionRows.cs` | UI |
| Gallery builder | `UI/Gallery/GuiMock.Profile.cs` | UI |

The Missions code receives body facts, the effective id set, a date formatter and a part-title
resolver from the UI, so it adds no headless leak (`scripts/arch/headless_pins.toml`).

Phases:
- P1a (lands first): delta-v snapshots in the recorder and the `DELTAV` node (8.1), with its unit
  tests, the generator support and one harness lane that flies to orbit and checks the snapshots.
- P1b: analyzer, line builder, profile builder, window, buttons, tests, docs. Reads snapshots when
  present, the estimate otherwise.
- P2: F3 recorder orbit stamps (format bump): raw osculating elements and the stock patch-end
  transition per space section, so orbits after physics-range burns are stored rather than inferred,
  and a closest approach for the Rendezvous; F5 terminal state from the stamp.
- P3: the optimizer reads a CAUSAL boundary layer from the analyzer (no hindsight; truncation test),
  loopability and labels read the F4 table; ReaimClassifier uses the line model. A split-rule version
  bump, its own design.
- P4: the graphic profile (Apollo-style phase diagram) from the same `PhasePlan`.

### 10.1 As built: the P1b core (2026-11-22)

The pure core is built (todo MISSION-PROFILE-P1B): the analyzer, the line builder, the profile
builder and the row view model the window draws. The window, the buttons, the seam op, the gallery,
the census lanes and the mirror are not. The one seam the window calls is
`MissionProfileBuilder.BuildProfile(MissionProfileRequest)`: the request carries the mission, the
tree, the window's cached structure / through line / vessel rows / vessel names / partner legs (each
built the tab's way when absent), the ERS id set and the supersede rows, the body facts, the date
formatter, the part-title and merge-partner resolvers and the ELS career totals. It returns a
`MissionProfile` whose `Rows` are `MissionProfileRow`s (kind, depth, collapse key and parent key,
caret, the seven cells as strings, the hover sentence, and the flags `HasSpent`, `SpentIsEstimate`,
`IsInferred`, `IsClipped`, `IsReflown`, `HasDataGap`, `IsUnclassified`), the Summary row first.

Where the build reads the design differently, against the real fixtures:

- Files: the analyzer has a fifth partial, `.Placement.cs` (event placement, include clipping,
  totality, counts); the row model is `Missions/MissionProfileRows.cs`; the builder is split into
  `.Rows.cs` (cells) and `.Summary.cs` (hovers and the Summary block).
- The burn definition is shared by making `RecordingOptimizer.ExtendBurn` a wrapper over a new
  `ExtendBurnRun(IList<TrackSection>, int)` (and `IsBurnSection` internal), so the analyzer runs the
  same run builder over a whole line's sections. Burn times are engine times (ignite to last
  shutdown), so a burn row starts up to a second before its powered section.
- Held altitude (4.3): besides the 2 % for a quarter period, an altitude that holds within 0.1 % of
  its radius for two minutes is inferred bound and clear. mun-landing-recorded's capture coast holds
  140 km to 8 m for three minutes, a sixth of the quarter period the looser rule asks for.
- Periapsis raise (4.4) needs the last floor contact to be an atmosphere pass; otherwise bound
  in-air -> bound clear is the circularization of a launch whose ascent coast dips in the air.
- A line that starts in space on an orbit with Pe in the atmosphere reads by its energy: escaping is
  a Transfer, bound is an Orbit (it only dips in); an orbit that is truly falling and has no ascent or
  deorbit before it stays `In space` (logged).
- A launch destroyed in the air before reaching space is an Ascent with rule `aborted-ascent`
  ("reached N km"), not a Flight; a line that starts airborne with no space after it is a Flight.
- A surface stay under 10 s at a line's end folds into the phase before it (a recovery right after
  touchdown: A.1's Touchdown and Recovered sit in the Reentry).
- Rendezvous (4.5) starts at the first in-orbit burn of the orbit phase that ends with the dock; the
  partner's own start UT is not an input yet, so no partner-exists check.
- The Docked phase row stands for the dock instant (no separate `Docked:` event row at its start);
  the undock is its closing event.
- Encounter wording: a burn reads `<Body> encounter, Pe N km` only when its own coast ends in that
  SOI; a burn that leaves the body for its parent reads `escape, Pe N km`; a coast whose encounter
  comes through later corrections reads its own Ap / Pe.
- The `Docked by another mission` event of 3.2 reads the request's `CommittedTrees` (null: none) and
  names the other tree's docking vessel; bdock-recorded's first mission shows the second mission's
  dock and undock after its End row.
- The tab and the Profile build their vessel rows through one entry,
  `MissionVesselRowBuilder.BuildForMission`; the fixture tests derive the tab's rows from the tab's
  own inputs and compare a full row signature, and a source gate pins both callers.
- Review fixes (2026-11-22): table 4.4's predicates are named methods (`MatchesCircularization`,
  ...) with the name as data (`BurnRule.Kind`, `NameFrom` only for departure and deorbit), so
  `BurnRules` is at complexity 16 instead of 54. A staging drop is read only from the
  `PreStaging` / `Staging` pair of 8.1 (`DeltaVSnapshotList.TryGetStagingDrop` over the batch's
  UT span; the stage batch anchor carries its last decouple as `EndUT`), and every Spent is the
  fall in Left between two reads less the paired drops between them (`SpentBetween`), unknown when
  a staging between them has no pair. An ascent firing carried into space starts its burn row at
  the exit (4.3), and the Ascent ends there when that burn circularizes or injects with no coast
  between (`CloseAscentAtBurn`). Known limit: with no snapshot at the atmosphere exit, the split of
  Spent between the Ascent and that burn is only as fine as the snapshots (the total is right).

Appendix A against the build: the event rows follow Q14 (Liftoff, Atmosphere exit and Atmosphere
entry are rows; an event's Location is blank unless it differs from its phase's or names a biome),
stagings are rows (Q3), not folded into the Ascent label as A.2 / A.3 wrote them; Duration uses KSP
6-hour days as 2.3 says (A.1's 8h 37m transfer reads `1d 2h`, its 18h 22m mission `3d 0h`); EVA
lengths are floored (`1s`, not `2s`); inclinations print on burn rows too. The estimates the build
computes (11.1, stored-frame vectors) match the appendix's hand values within 2 % except A.1's first
Correction (~35 m/s against the hand ~14: the burn has a radial part). A.2's first Correction reads
`Ap 11,100 km, Pe -201 km` (the encounter belongs to the coast after the second Correction). A.3's
approach burns and final orbit read the stored terminal orbit (`117 x 102 km`) instead of `-` /
`about 117 km`, and its final Kerbin orbit is 0.9 s (`0s`). The fixture tests pin the rows as built.

### 10.2 As built: the window, buttons, seam and gallery (2026-11-23)

- Window: `UI/MissionProfileWindowUI.cs`, one instance in `ParsekWindowSet`, reached by `ParsekUI`
  through `IMissionProfileWindow` (`UI/ParsekUIWindowContracts.cs`), drawn from
  `DrawStructureWindowIfOpen` after the two Log instances. The frame (rect seed, resize, opaque
  style, `ParsekUiScale.Window`, position log, camera lock while hovered) is
  `UI/LogTableWindowFrame.cs`, which the Mission Log / Route History now draw through too; each
  window keeps its own table, hover strip and Close. Not in the Basic close set, like the Log.
- Rebuild: on open and on a Layout event when `ComputeChangeSignature` moves (the Log's signature
  over `StateVersion`, the mission, its name and include set and `DisplayNames.Version`, plus
  `ParsekScenario.SupersedeStateVersion`); a deleted tree closes the window with an Info line. The
  per-line `PhasePlan` cache of section 6 is not built: a rebuild re-analyses (one `[Profile]`
  summary per rebuild, never per frame).
- The request the window fills: structure, vessel names and partner legs the Log's way, the
  effective ids as the tree's visible recordings with the session suppression ignored
  (`EffectiveRecordingIdsOf` over `EffectiveState.IsVisible`), the scenario's supersede rows, live
  body facts (`RecordingOptimizer.ResolveBodyFacts`), the Log's date formatter, part-title and
  dock-partner resolvers, the committed trees and their mission names, and the career totals summed
  from ELS rows tagged to the effective ids (`SumCareer`: science, funds and reputation earnings,
  spendings, penalties, milestones and contract results).
- Draw: seven columns of 2.3; the Element cell carries the depth indent and a caret slot (a
  clickable glyph on phases and line headers); Summary and line headers bold, the Empty row muted,
  Spent in `StatusColorKind.Orange` whenever the row has a figure (`DrawsSpentInOrange`, the `~`
  included); every cell carries the row's hover for the single-line strip. Collapse state per
  mission id + row key, cleared on open, kept across rebuilds.
- Seam: `UiAction op=target window=profile mission=<name or id>` (`TestCommandUiAction.ProfileWindow`,
  table row after `routehistory`; `steps=` is the row count), plus open / close / rect / find on
  the window like the Log. Gallery: `UI/Gallery/GuiMock.Profile.cs`, states
  `profile.mission.mun-return` (one detached recording through the real builder with recorded
  delta-v snapshots) and `profile.mission.empty`, installed by `op=mock window=profile`.
- Census: GUI-1 and GUI-4 capture the Profile in Advanced and Basic; GUI-4 also photographs the
  two gallery states; the mirror's `missions` and `profile` epochs are placeholders until those
  flights.

## 11. Tests

### 11.1 Unit (xUnit, pure)
- Each stage-2 rule alone, positive and negative: the atmosphere-pass table row by row (direct entry,
  skip entry with hops under and over 2 minutes, aerocapture with stored and structural evidence,
  an aerobraking campaign of several passes ending in a raise burn and one ending in a landing,
  braking flyby, unknown pass); encounter beating a sub-surface periapsis; phasing vs orbit change (a
  dock follows or not); flyby vs arrival; an SOI exit into a bound orbit; launch vs flight vs hop on an
  atmosphere body and an airless one; landing vs impact; crash during ascent; breakup with survivors.
- Strata: no stratum reads its own output (a rule table check), priority before length, totality (every
  span claimed once) over every synthetic line and every committed fixture.
- Evidence: terminal orbit ignored when its epoch is before the last burn; predicted segments skipped;
  on-rails sections classified by their conic, not their label; inferred orbit from a held altitude.
- Burns: the shared definition (10 s gap, on-rails gap ends a burn, SOI change ends a burn, continuous
  across the atmosphere exit, engine time, RCS tap dropped); labels per table 4.4.
- Delta-v: values computed by the code over stored-frame conics pinned against independent
  hand-checks (circularization, Hohmann leg, capture, a radial correction), the `~`, the 120 s cap,
  `-` on inferred sides; InvariantCulture under `de-DE`.
- Lines: chain holes, ParentContinues and its inference, switch continuation, supersede bridge with
  the re-flown event at the rewind UT, own ship absorbed at a dock and resumed at undock (and the
  partner absorbed case), per-interval include clipping, debris dropped, cross-tree partner named.
- EVA lines: short EVA collapse, EVA in space, surface EVA, jetpack hop, part placed.
- Builder: row order and depth, collapse keys stable across a rebuild, location never equal to the
  label, hover sentences, empty state, title.
- Log assertions on the summary, the per-line line and the rate-limited unclassified line.

- Delta-v snapshots (P1a): trigger set (start, burn start / end, staging, separation, dock, undock,
  end), the settle wait and its timeout (unknown), background loaded vs unloaded, debris and EVA
  excluded, the `DELTAV` node round trip, an older reader ignoring the node, `SplitAtUT` and merge
  carrying snapshots by UT, Spent / Left arithmetic including a staging that drops fuel, the
  Delta-V-app-off fallback; generators (`RecordingBuilder`) can write snapshots.

### 11.2 Fixture (xUnit over committed saves)
- `cs-career-mun4` (CS Mun Science), `mun-landing-recorded` (Kerbal X), `bdock-recorded` (Kerbal X #2):
  the expected element list of Appendix A, pinned as text.
- Row independence: one recording (mun-landing-recorded's single version-1 Kerbal X) analysed whole
  and re-cut inside the test with `RecordingOptimizer.SplitAtUT` at every version-3 cut and at points
  inside a burn and inside a coast with checkpoint evidence spanning the cut: identical plans. (The
  committed `mun-orbit-recorded` / `-v3` pair are different flights and cannot serve.)
- Atmosphere passes on real data: every committed recorded fixture with an atmosphere entry from
  space (cs-career-mun4's reentry is one; candidates to read at build time: `duna-direct-recorded`,
  `kerbin-return-recorded`, the Eve / Laythe fixtures, and flights from the `graze-*-probe-stack`
  hosts), each pinned to the rule that claimed it. Where no committed fixture holds a skip entry, an
  aerocapture or an aerobraking campaign, a harness lane records one (a new fixture): synthetic lines
  alone would test the rules against our own assumptions.
- A sweep over every committed recorded fixture: no throw, totality holds, the unclassified and
  inferred counts reported (report-only at first, a ceiling once read).

### 11.3 UI and gates
- Source gate: `Profile` is drawn immediately before `Log` in `DrawMissionNameCell` and opens
  `OpenProfileWindowForMission`; `MissionsTabColumnSequenceTests` re-pinned for the new loop-grid column.
- `BuildWindowTitle` / default rect / min size cells (the `StructureListWindowTitleTests` shape).
- `ParsekUiScaleTests` covers the new window's `ParsekUiScale.Window` call.
- `TooltipEchoBudgetTests` covers the button tooltip.
- Architecture: the headless pins and layer pins stay unchanged (or are re-pinned with a reason).
- In-game test `MissionProfile` category (SPACECENTER): open the Profile on a committed fixture
  mission, assert the rendered row count equals the builder's and the window rect is on screen.
- Harness: the GUI census lanes capture the window; one P1a lane flies a launch to orbit and asserts
  the `DELTAV` snapshots (count window, Left falling at each burn, a staging drop); plus the new
  atmosphere-pass recording lane(s) of 11.2 if no fixture holds those shapes.

## 12. Docs that move with the build

`CHANGELOG.md` (one player line: "New Profile window on each mission: the flight's phases, burns,
orbits, docking and EVA."), `docs/dev/todo-and-known-bugs.md` (Phase 17 P1 entry and the P2 / P3
follow-ups), `docs/dev/design-gui-inventory.md` (14 windows, the button, the tooltip),
`.claude/CLAUDE.md` exception bullet + the `TryCreateFromSegmentStoredFrame` reader sentence +
`AGENTS.md`, `docs/roadmap.md` Phase 17 status (and its delta-v budget line: "spent and left", no
"planned", owner decision), `scripts/arch/log_tags.json`, the design PDF table if
this doc joins it.

## 13. Research notes (2026-11-22)

- Apollo by the Numbers splits each mission into phase pages (Ascent, Earth Orbit, Translunar, Lunar
  Orbit / Surface, Transearth, Recovery) whose tables carry Event, GET, altitude, event duration,
  velocity change, apolune, perilune; the mission timeline itself is flat with vehicle prefixes
  (S-IVB, CSM, LM, CSM/LM). The flight plan uses parallel lanes per vehicle; the Apollo Flight Journal
  splits by phase and moves the LM surface stay to its own journal. (nasa.gov SP-4029 Apollo 11
  Summary, Lunar Orbit Phase, Timeline; archives.gov flight plan; apollojournals.org AFJ index.)
- Artemis I uses one flat table keyed by flight day, with burns, separations, entry and splashdown,
  some rows carrying duration and delta-v (blogs.esa.int/orion; nasa.gov Artemis I timeline).
- KSP tools are flat logs (The Kaptain's Log: launch, staging, dock, orbit closed / escaped, SOI
  change, landed, EVA, recovered) or planners (Flight Plan, maneuver planners); none shows a phase
  tree across several vessels.
- Taken from it: phases contain events; one row per burn with duration and delta-v (not ignition /
  cutoff pairs); the mission clock everywhere; child vessels as branches anchored on the event that
  made them; a mission summary; leave out low-value log events.

## 14. Owner decisions and open questions

Interview of 2026-11-22 (owner answers, quoted where the wording matters):

| # | Question | Decision |
|---|---|---|
| Q1 | Button placement | "Profile above, Log under it (if they don't fit, otherwise Profile to the left)"; after the variants: layout A with equal widths for Clone / Delete / Profile / Log and a wider Warp column; Basic puts Log under Profile in the same column (2.1). |
| Q2 | Delta-v | "record how much delta-V the vessel and stages had at every step ... and use the real figures provided by the game" (8.1), for every recorded vessel the game computes it for; old flights show the `~` estimate. |
| Q3 | Stage separations | Event rows. |
| Q4 | Re-Fly | Like the Log during a session; the flown-again path after the merge. |
| Q5 | Child vessels | Nested under the event that made them. |
| Q6 | Default collapse | All expanded. |
| Q7 | Names | Plain: Transfer burn, Capture burn, Return burn, Correction (4.4). |
| Q8 | Delta-v budget | Spent and left; no "planned" figure, to be removed from the roadmap. |
| Q9 | Debris | Each piece's ending in the stage separation hover. |
| Q10 | Key numbers | Speed, max-Q and heating are out ("not relevant"). |
| Q12 | Summary contents | Bodies and landing sites, crew and EVA time, career results, craft facts. |
| Q13 | Columns | Time, Element, Location, Duration, Spent, Left, Orbit / result. |

| Q11 | Maximum altitude | Only as the result of a phase that shows no orbit (4.6). |
| Q14 | Redundant elements | Remove `Landing legs down` rows; events show a Location only where it differs from their phase or names a site or biome. Liftoff, Atmosphere exit and the vessel row's Spent / Left stay. |

No questions remain open for P1a / P1b.

## 15. Review record (2026-11-22)

Two independent reviews of the first draft (Opus, Fable). Every finding was checked against the code
and folded in; where a reviewer's point is a preference rather than a defect it became an open
question.

| Finding | Where fixed |
|---|---|
| One-axis orbit kind makes every post-pass orbit "falling", so aerobraking / aerocapture never match and a campaign reads as a skip (Fable 1) | 4.3 two axes + stratum (c) |
| No encounter kind: Mun transfer ellipses with sub-surface Pe read as descents (Opus 2) | 4.3 `encounter` beats periapsis |
| Stage 2 circular and two tie-breakers with no precedence (Fable 2) | 4.3 strata, priority then length, totality check |
| Hindsight analyzer emitting the optimizer's cut list (Fable 3) | 4.1 / 4.2 / 10: no cut list in P1; P3 causal layer + truncation test |
| Dock model wrong: the own ship is the absorbed side in bdock-recorded; a new walker would disagree with the Missions tab (Opus 1, Fable 4) | 4.5, 5: lines on `MissionStructure` / through line / `VesselIdentity` |
| Terminal orbit is not end-of-line evidence when burns follow it (Opus 3) | 4.3 evidence tiers |
| ERS hides the session's subtree; the Log reads the raw tree; `ComputeERS` from Missions is a leak (Opus 4) | 5 (UI passes the id set), Q4 |
| Body facts and the date formatter are leaks in Missions (Opus 5) | 4.1, 10 |
| `TrackSection` has no body; points are read (Opus 6, Fable 5) | 4.3 exact point reads |
| On-rails environment label unreliable (Opus 7) | 4.3 regime from the conic |
| Include set is per interval, non-cascading (Opus 8) | 5 |
| Row-independence fixture pair are different flights (Opus 9) | 11.2 re-cut one recording in the test |
| Two burn definitions (Opus 10) | 4.3 one definition shared with BurnCuts |
| Loop grid has no room (Opus 11) | 2.1, Q1 |
| Log does not keep its target across scenes (Opus 12) | 2.2, 6 |
| Live-Zup conic API, predicted segments, `BodyFacts`, reuse `ClassifyOrbitKind` (Fable 5) | 4.3, 4.6 |
| Delta-v totals misleading; A.2 capture `~277` with no orbit after (Fable 6, Opus minor) | 2.3, 4.6, Appendix A |
| Missing cases: SOI exit into orbit, `Return to` keyed on launch body, structural tier, airless ascent / hops, burn across the atmosphere exit, drive vs roll, EVA bypass, ground-part branch points, crash, breakup, flyby tiling (Fable 7) | 3.1, 3.2, 4.3 |
| Minor: re-flown mark at the rewind UT, Location repeating the label, phase vs burn double numbers, short EVA rule, EVA leg ending Boarded prints no End, `MissionView` type, split pieces' copied altitudes, labels in the model layer, `[Profile]` tag, appendix arithmetic (stage pieces, T+ flooring, landing burn) | 2.3, 3, 3.2, 4.3, 5, 9, 10, Appendix A |

## Appendix A. Worked profiles from committed fixtures

Written before the build; section 10.1 lists where the built rows differ (the fixture tests pin
those). Numbers read from the fixtures' `.prec.txt` mirrors and `persistent.sfs` (section UTs, checkpoint
elements, part events, branch points). The fixtures predate 8.1, so these tables show the estimate
fallback (Spent column, burns only, Left empty); the mockup's "new flight" view shows the recorded
layout with illustrative values. Times are floored like `FormatElapsed`. Delta-v values are
hand estimates (vis-viva at the burn radius) to show the shape; the build pins the values its own
stored-frame vector computation returns (11.1), which can differ for burns with a radial part.

### A.1 CS Mun Science (`cs-career-mun4`): Mun orbit, two EVAs, return, recovery

| Time | Element | Location | Duration | Spent | Orbit / result |
|---|---|---|---|---|---|
| Y1, D06, 03:13 | CS Mun Science (Jebediah) | Kerbin, Launch Pad | 18h 22m | | Recovered |
| T+0:00:01 | Ascent | Kerbin | 3m 52s | | to 70 km, Ap 80 km |
| T+0:01:10 | Stage separation (2 pieces) | Kerbin | | | |
| T+0:02:40 | Stage separation (1 piece) | Kerbin | | | |
| T+0:03:53 | Circularization | Kerbin | 1m 48s | | 83 x 77 km, 0.1 deg |
| T+0:05:24 | Circularization burn | Kerbin | 18s | ~362 | 83 x 77 km |
| T+0:05:42 | Kerbin orbit | Kerbin | 4m 1s | | 83 x 77 km, 0.1 deg |
| T+0:09:43 | Transfer to Mun | Kerbin -> Mun | 8h 37m | | Mun orbit |
| T+0:09:43 | Transfer burn | Kerbin | 28s | ~860 | Ap 11,524 km, Pe 82 km |
| T+2:29:22 | Correction | Kerbin | 3s | ~14 | Mun encounter, Pe 229 km |
| T+7:03:27 | Entered Mun SOI | Mun | | | Pe 229 km |
| T+8:46:35 | Capture burn | Mun | 11s | ~220 | 229 x 229 km, 1.0 deg |
| T+8:46:46 | Mun orbit | Mun | 2h 26m | | 45 x 30 km |
| T+8:47:48 | EVA: Jebediah Kerman (2s), boarded | Mun | | | |
| T+9:59:29 | Orbit change | Mun | 8s | ~64 | 229 x 30 km |
| T+10:38:16 | Orbit change | Mun | 8s | ~67 | 45 x 30 km |
| T+10:38:26 | EVA: Jebediah Kerman (2s), boarded | Mun | | | |
| T+11:13:05 | Return to Kerbin | Mun -> Kerbin | 7h 4m | | Pe 27 km |
| T+11:13:05 | Return burn | Mun | 11s | ~262 | escape, Pe 38 km |
| T+12:51:59 | Left Mun SOI | Kerbin | | | Ap 10,820 km, Pe 27 km |
| T+12:51:59 | Stage separation (1 piece) | Kerbin | | | |
| T+18:17:32 | Reentry | Kerbin | 4m 30s | | direct entry |
| T+18:20:11 | Parachutes deployed | Kerbin | | | |
| T+18:22:02 | Touchdown | Kerbin, Shores | | | landed 1,072 m |
| T+18:22:02 | Recovered | Kerbin, Shores | | | |

### A.2 Kerbal X (`mun-landing-recorded`): Mun landing

| Time | Element | Location | Duration | Spent | Orbit / result |
|---|---|---|---|---|---|
| Y1, D01, 00:00 | Kerbal X (Jebediah) | Kerbin, Launch Pad | 6h 27m | | Landed, Mun |
| T+0:00:04 | Ascent (3 stage separations, 2 pieces each) | Kerbin | 2m 52s | | to 70 km, Ap 80 km |
| T+0:02:57 | Circularization | Kerbin | 1m 27s | | 84 x 76 km, 0.1 deg |
| T+0:03:57 | Circularization burn | Kerbin | 27s | ~736 | 84 x 76 km |
| T+0:04:25 | Kerbin orbit | Kerbin | 26m 12s | | 84 x 76 km |
| T+0:30:37 | Transfer to Mun | Kerbin -> Mun | 5h 28m | | about 140 km Mun orbit |
| T+0:30:37 | Transfer burn | Kerbin | 25s | ~856 | Ap 11,480 km, Pe 77 km |
| T+1:21:02 | Correction | Kerbin | 19s | ~120 | Mun encounter (Kerbin Pe -201 km) |
| T+1:21:06 | Stage separation (1 piece) | Kerbin | | | |
| T+2:12:07 | Correction | Kerbin | 1s | ~9 | Mun encounter |
| T+4:33:10 | Entered Mun SOI | Mun | | | Pe 140 km |
| T+5:58:54 | Capture burn | Mun | 23s | - | about 140 km (inferred) |
| T+5:59:18 | Mun orbit | Mun | 3m 1s | | about 140 km (inferred) |
| T+6:02:19 | Descent | Mun | 22m 48s | | |
| T+6:02:19 | Descent burn | Mun | 4s | - | falling (coast to the surface) |
| T+6:24:11 | Landing burn | Mun | 51s | - | |
| T+6:25:08 | Touchdown | Mun | | | landed 3,854 m |
| T+6:25:08 | On the surface | Mun | 1m 54s | | |
| T+6:27:03 | End: Landed | Mun | | | |

### A.3 Kerbal X #2 (`bdock-recorded`, second tree): probe separation, rendezvous, docking

| Time | Element | Location | Duration | Spent | Orbit / result |
|---|---|---|---|---|---|
| Y1, D01, 00:06 | Kerbal X (Bill, Bob, Valentina) | Kerbin, Launch Pad | 2h 22m | | Orbiting Kerbin |
| T+0:00:00 | Ascent (3 stage separations) | Kerbin | 2m 56s | | to 70 km, Ap 90 km |
| T+0:02:57 | Circularization | Kerbin | 2m 4s | | 95 x 84 km |
| T+0:04:39 | Circularization burn | Kerbin | 22s | ~592 | 95 x 84 km |
| T+0:05:01 | Kerbin orbit | Kerbin | 1h 57m | | 95 x 84 km |
| T+0:05:01 | Separated: Kerbal X Probe | Kerbin | | | child line: Kerbin orbit 95 x 84 km for 2h 17m, End: Orbiting Kerbin |
| T+2:02:33 | Rendezvous with Kerbal X (mission 'Kerbal X') | Kerbin | 20m 4s | | |
| T+2:02:33 | Phasing burn | Kerbin | 8s | ~18 | 117 x 85 km |
| T+2:17:16 | Approach burns (3) | Kerbin | 20s | - | |
| T+2:22:38 | Docked to Kerbal X (mission 'Kerbal X') | Kerbin | 1s | | own ship absorbed into the partner |
| T+2:22:39 | Undocked: Kerbal X (mission 'Kerbal X') | Kerbin | | | partner departs; own ship resumes |
| T+2:22:39 | Kerbin orbit | Kerbin | 1s | | about 117 km |
| T+2:22:40 | End: Orbiting Kerbin | Kerbin | | | |
