# Multiplayer implementation - master plan

PLAN (2026-11-14; the owner's answers of 2026-11-15 folded in), not started. Work starts after
1.0.2 is released, on the owner's go.
All of it lands on the branch `multiplayer`, not on main (section 3, rule 10).
(The refactored build this plan and the design call "1.1" ships as version 1.0.2, by the
owner's decision of 2026-11-14; "1.1" in these documents names that code structure.)
The multiplayer mode itself ships as version 1.1.0 (owner, 2026-11-14).
Source paths and line numbers were re-measured at main `ab1cc9478` (2026-11-15) after #2611,
#2619 and #2620 moved them; whoever picks up a task re-reads the code first.

This folder holds the implementation plan for the competitive multiplayer mode ("the space
race"). A race is one whole multiplayer game: a full Sandbox, Science or Career playthrough by
up to four players, scored across every milestone. It is not a contest for one chosen goal, and
there are no race types or presets (owner, 2026-11-15). The design is
`docs/dev/design-competitive-multiplayer.md` (v3); the measured list of
prerefactors is `docs/dev/research/multiplayer-base-refactoring-2026-11-14.md`. Nothing here is
code. Every task below is one pull request with its own tests, docs and one clean review.

## 1. What is in this folder

| File | What it is | Read it when |
|------|------------|--------------|
| `README.md` | this file: the rules, the decisions, what starts first, the owner's questions | first |
| `task-index.md` | every task once, with its dependencies, its wave, the shared files, the PC use | picking the next task |
| `design-corrections.md` | 138 corrections to the design found while planning, folded into design v3 on 2026-11-15 (kept as the record; its design line numbers are v2's), and 26 questions for the owner with their answers | to see why the design changed, or which questions are still open |
| `r0-spikes.md` | 17 spikes on the owner's PC that settle the open feasibility questions | before R1 and R2 |
| `r0b-prep-small.md` | prerefactors P12, P11, P7, P1, P10 | day one |
| `r0b-prep-playback.md` | prerefactors P2c, P2a, P2b (the second playback lane) | day one |
| `r0b-prep-map-watch-codec.md` | prerefactors P3, P3b (deferred), P6 | day one |
| `r1-mode-and-setup.md` | the Multiplayer mode, the race mark, create and join, the setup dialog | phase R1 |
| `r2-sites.md` | the four space centers, the camera, the launch-site rule | phase R2 |
| `r3-export-and-exchange.md` | standings, the shared folder, sync, import, disclosure facts | phase R3 |
| `r3-rival-world.md` | the rival store, rival ghosts, map presence, leftovers, flags | phase R3 |
| `r4-r6-board-and-rules.md` | the board, the ladder, the Space Race window, end date, no retakes | phases R4 and R6 |
| `r5-looks.md` | player colors, suits, tinted lines and labels | phase R5 |
| `test-and-harness.md` | generators, fake transport, seam verbs, harness lanes RACE-0 to RACE-13 | all phases |

Each plan file has the same ten sections: scope, what it needs, what it found in the code, the
shapes it adds, the tasks, tests, risks, what other plans must know, corrections to the design,
questions for the owner. A task names its change, its proof, the registers it must update, its
size, its dependencies and whether it needs the owner's PC.

## 2. The phases

| Phase | Plans | What exists when it is done |
|-------|-------|-----------------------------|
| R0 spikes | r0 | answers, not code: site positions, scenery copy, suits, difficulty panel, plaque window, what a mod can see of debug tools; sync program speed (S8) runs last, after the race works on a plain folder |
| R0b preparation | small, playback, map | base Parsek ready for a second ghost lane; no player-visible change (the two small launch pad fixes of question Q-04 land on main outside this plan) |
| R1 mode and setup | r1 | a race save can be created and joined; it plays like single player |
| R2 sites | r2 | each player starts at an own copy of the KSC; stock saves are untouched |
| R3 exchange and rivals | r3e, r3w | a player's flights reach the shared folder and show up for rivals as ghosts, leftovers and flags |
| R4 board | r4 | the Space Race window: milestones per player, points, messages |
| R5 looks | r5 | colors and suits per player on kerbals, ghosts, lines and labels |
| R6 rules | r4 | end date, no retakes, disclosure marks, the final board |

R7 (hardening of the anti-cheat) and R8 (recorded opponents and the solo race) of the design
are optional and not planned here. The formats reserve room for both from R3.

## 3. Rules every task follows

These bind every task. Where a plan file says otherwise, this section wins and the plan file is
wrong.

1. **One task, one pull request**, with unit tests, the docs it changes and one clean-context
   review before merge. A pull request merges when green. No task bumps the version, tags or
   releases.
2. **Nothing lands without a caller.** The architecture suite counts every production method
   that nothing reaches (`docs/dev/arch/README.md`, "Code facts"); a method only an xUnit test
   calls counts, and the count may not rise. So a port member, helper, overload or facade
   member lands in the pull request of its first caller. An in-game test cell or a seam verb
   in the same pull request is a valid first caller, and is preferred over joining two tasks.
   Where two tasks must share a pull request the plans say so; never more than two.
3. **Dropped tasks keep their ids.** R1-13, T-08, T-09, T-11, T-14, T-15, T-22, T-23, R3E-05,
   R3W-31, R4-14, R6-09, R5-13, P1-04, P12-02 and R1-01 are dropped; each keeps a heading that
   says what replaced it. No task is renumbered; a new task takes the next free id of its plan
   (R5-14, added 2026-11-15). P2a-01 is a specification, not a pull request.
4. **Race code is inert outside a race save.** A single-player save gains no node, no file, no
   window and no cost from any task. The isolation gates (R1-06, R3W-27) and the "rivals change
   nothing" lanes prove it.
5. **Race formats.** R1-12 registers both format rows (`race-link`, `race-exchange`) and writes
   the next free golden folder (expected today: `v2`). R3E-04 registers nothing and adds the
   golden folder after it (expected today: `v3`) at the same format numbers. Both use T-01's
   driver. One pin class, `RaceFormatPinTests`. Every number this plan writes into files or
   into an enum another build reads (a golden folder `v<N>`, a `GameActionType` id, an enum
   member saved by name and number, a registry dimension id such as `D19`, the
   `GhostPlaybackSkipReason.HostFiltered` value) is "the next free number when the task
   lands"; where a plan file names one (`v2`, `v3`, `VesselEnded = 42`, `HostFiltered = 17`,
   `D19`), read it as "expected today". Rule 10 says who renumbers.
6. **No thread.** Every file access is on the main thread until the owner allows the one copy
   worker (question Q-18, after spike S8, which runs last). The plan builds the main-thread
   form first, with the design's default poll numbers.
7. **In-game test categories and their lanes.** The first test of a new in-game category
   regenerates the lane catalog (`python harness/tools/lane_catalog.py`) in its own pull
   request. A spec that runs a batch pins the tally of every category it runs. The race
   categories have five such specs: the host lanes RACE-9 (T-29, wave 14), RACE-10 (T-35)
   and RACE-11 (T-36, both wave 15), one per scene, and the SITES lanes, which pin their own
   categories from wave 11 (`RaceSitesStock` in SITES-2, R2-13; `RaceSitesEditor` in
   SITES-3, R2-14). A category no committed spec pins yet needs no re-pin when a test is
   added (R5-14's `RaceLooks` cell lands in wave 7, before RACE-9); once a spec pins it, a
   test added to it re-pins that spec's tally in the same pull request.
8. **New player-facing surfaces** (the kinds `.claude/CLAUDE.md` forbids: windows, popups,
   badge counters, panels) are only the ones the owner asked for, the same list as design
   section 0: the main menu Multiplayer entry, the race setup dialog, the Space Race window,
   and, only if spike S7 shows the stock plaque window cannot show a rival's text, one small
   popup for reading a rival's flag (question Q-21, owner, 2026-11-15). That is four, the
   last one conditional. R1-14 writes the first three into `.claude/CLAUDE.md` as one new
   exception bullet; R3W-25 adds the popup to that bullet only if S7 fails. The Route
   History window is an older single-player exception of `.claude/CLAUDE.md`, not one of
   these. Not new surfaces: the Multiplayer section of the existing Settings window, hover
   text inside the Space Race window, one-shot screen messages for events, and the blocks on
   stock controls (the greyed "allow other launch sites" option, R2-08; the blocked suit
   choice, R5-14), which use the existing stock-control annotation exception. Anything else
   needs a new ruling.
9. **The folder is the module** (pull request #2611, on main ab1cc9478). A new source file is
   created in its module's folder, `Source/Parsek/<Module>/<Name>.cs`; a new module needs its
   rule, layer and atlas line in the same commit as its first file. A `.cs` file in the root
   of `Source/Parsek`, a rule whose folder is not on disk and a module with no file each red
   the architecture suite. The race modules register in R1-03 (`RaceCore`), R3W-02
   (`RaceWorld`) and R3E-02 (`RaceExport`), each with the design 15.4 `[forbidden]` edges whose
   two ends exist (design-corrections DC-135 to DC-138).

10. **One long-lived branch** (owner, 2026-11-15). All work of this plan lands on the branch
    `multiplayer`, cut from main when the owner gives the go. A task works on a sub-branch cut
    from `multiplayer` and opens its pull request against `multiplayer`. Nothing of this plan
    reaches main until one final pull request merges `multiplayer` into main. That holds for
    the prerefactors, the harness and tooling tasks and the spike tooling as well as the race
    code. Where a plan file says a task lands "on main", read "on `multiplayer`". What follows:
    - **Checks.** `tests` already runs for a pull request against any branch
      (`.github/workflows/tests.yml`, `pull_request:` with no branch filter). The first pull
      request on the branch adds `multiplayer` to the workflow's `push:` branches, so every
      merge is checked as a whole too. The metrics job stays main only. The owner gives
      `multiplayer` the branch protection main has (required `tests`).
    - **Keeping up with main.** Main keeps receiving fixes and releases. Main is merged into
      `multiplayer` after every release from main and at least once a week, as its own pull
      request against `multiplayer` with green tests: a merge commit, never a rebase, because
      the sub-branches hang off the branch. One agent owns that merge. Generated files (the
      pin files under `scripts/arch/`, the knot canaries, the lane catalog, pinned batch
      tallies) are regenerated by their tools after the merge, never merged by hand.
    - **Fixes players need now.** A defect in single-player Parsek found while working on the
      branch is fixed on main in its own pull request and reaches the branch with the next
      merge of main. It is never fixed only on the branch.
    - **Numbers main may take first.** At every merge of main into `multiplayer`, if main
      has taken a number the branch uses (rule 5: a golden folder, a `GameActionType` id, a
      saved enum value, a registry dimension), the BRANCH renumbers in that merge's pull
      request, never main. This is safe because no build from the branch is released, test
      saves made on the branch before a renumber are thrown away, and golden folders on the
      branch are not frozen until the final merge (their checksums are rewritten with the
      renumber). A first small pull request on main that reserves the ledger row id (as
      `GameAction.cs` reserves ids today) is an option the owner can choose later; it is mod
      code on main, which needs the owner's go, and it is not part of this plan.
    - **Version and changelog.** The branch carries main's version. Entries for race changes
      go under a `1.1.0` heading that exists only on the branch. No task bumps the version.
      The 1.1.0 section also carries the warning "do not open a race save with an older
      Parsek" (design 8, case 22), written by R6-10, the poll tuning, which runs after S8 (the
      owner put S8 last), in wave 19, the plan's last wave.
    - **`.claude/CLAUDE.md` on the branch.** Main edits that file often and many of its
      entries are single very long lines, so a race task adds its text only as new lines or
      new bullets (for example one bullet under the "ParsekHarmony.cs + Patches/" entry for a
      race patch), never as words inside an existing line; then `cp .claude/CLAUDE.md
      AGENTS.md`. A merge of main then conflicts only where both sides added lines.
    - **The plan itself.** From the cut on, this folder is edited on `multiplayer` only (task
      status, re-measured line numbers, answers). Main keeps the plan as it stood at the cut.
    - **The PC.** A lane or an in-game batch for a task flies from a worktree of its
      sub-branch. The spike branch (question Q-02) is cut from `multiplayer`.
    - **The final merge.** Main is merged into the branch one last time, every lane is flown
      on that tip (the full run of question Q-05), and one pull request merges `multiplayer`
      into main as a merge commit, not squashed, on the owner's go.
    - **What this rule costs.** The 44 live prerefactor tasks (small, playback, map) reach
      main only with the final merge, so single-player Parsek gets none of their fixes early;
      every merge of main can conflict in the hot files those tasks edit (task index C.1); and
      the final pull request carries the work of about 206 pull requests at once, which is
      why every lane is flown on its tip first.

### Names fixed across the plans

| Thing | Name |
|-------|------|
| Seam verbs | `RaceSetup action=create|join`, `RaceCommand action=status|set-folder|sync|offline|online`, `ListHandles kind=sites|rivals|board` |
| Harness lanes | `RACE-0` to `RACE-13` (test-and-harness), `SITES-1` to `SITES-3` (r2) |
| Scene request handler | `ParsekRaceBoot.OnSceneRequest`, the only one; it calls `RaceSites.Reconcile` on every request |
| Refused load | `Patches/RaceLoadGatePatch.cs`, a prefix on `Game.Start()` that refuses a race save before its first scene (R2-02); a scene request cannot turn a load back |
| Race scenario child hook | `IRaceScenarioChild` |
| Date rule | `RaceCore/RaceCalendar.cs`, `TryAcceptDateRule`, reason `date-unit-not-second`; dates compare to the exact second, the calendar only formats them (owner, 2026-11-15) |
| Import verdict | `RivalStandingVerdict`, declared by R3E-04, filled by R3E-18 |
| Milestone table | `RaceCore/MilestoneTable.cs`, owned by R3E-11; R4-03 is only the ladder filter |
| Debug completions | wire rows `DISCLOSURE` / `DEBUG_COMPLETION`, codec member `Standing.DebugCompletions`, player mark `DebugCompleted` |
| Launch areas | `SiteRow.LaunchAreas`, read through `LaunchAreaGeometry.Contains` |
| Line and label style | `PlaybackTrajectoryStyle` (R5-03); label prefix `"<display name> - "` |
| Ghost map name | `GhostMapPresence.ComposeGhostVesselName`: `Ghost: Ana - Kerbal X` |
| Empty catalog for the rival lane | `RecordingPlaybackCatalog.None` (R3W-09); there is no `RivalCatalog` |
| Race sync screen messages | one posting site, the sync driver's, landed by R3E-22, which also logs `[RaceSync] Message posted:`. It also posts R3E-18's clash note and "<name> joined the race" and R3E-20's two-computers message. The only other race screen messages are R2-02's refused load, R2-08's refused launch and R3W-19's refused switch to a rival ghost |
| Log tags | plain: `RaceSync`, `RivalLane`, `RaceBoard`, listed in R1-06 |

### In-game test categories

One scene and one host lane per category. No other spec runs a batch of a category that
RACE-9, RACE-10 or RACE-11 pins.

| Category | Scene | Host lane |
|----------|-------|-----------|
| RaceMode, RaceSites, RaceExchange, SpaceRace, RaceLooks | SPACECENTER | RACE-9 (T-29) |
| RaceSitesFlight, RaceFacts, RaceRivalLane, RaceRivalIsolation, SpaceRaceFlight, RaceLooksFlight | FLIGHT | RACE-10 (T-35) |
| RaceSitesTrackingStation, RaceFactsTrackingStation, RaceRivalLaneTrackingStation, RaceRivalIsolationTrackingStation | TRACKSTATION | RACE-11 (T-36) |
| RaceSitesStock | SPACECENTER, a single-player save loaded after a race save | SITES-2 (R2-13) |
| RaceSitesEditor | EDITOR | SITES-3 (R2-14) |

## 4. Decisions taken while joining the plans

The eleven plans were written side by side and then checked against each other. These are the
choices made where two plans disagreed. Each is a designer choice the owner can overrule; the
ones that change the design are in `design-corrections.md`.

- The playback catalog port stays in the `Recording` module; the rival lane gets an empty
  catalog. The design's "port on the floor" cannot hold (DC-111).
- R5-03 owns the style type and the engine arguments and lands before the rival lane's style
  source (R3W-15) (DC-118).
- The trajectory decode change (P6) is built in parallel and merged when green, outside the
  window in which the ghost positioner change (P2b) is proven by flights (question Q-06).
- The groundwork for the Watch camera on a rival flight (P3b) is not in the first build. Rivals
  are seen on the map, in the Tracking Station and as ghosts nearby (question Q-07, answered
  2026-11-14 by the owner: later).
- The Multiplayer entry is never hidden (question Q-08, owner, 2026-11-15): it is visible on
  the `multiplayer` branch, which no player sees before the final merge, and each R1 task a
  player can see writes its CHANGELOG entry under the branch's `1.1.0` heading (rule 10).
- A milestone completed from the debug menu does not score, by the owner's decision D3. R3E-09
  records what spike S9 shows a mod can see; R6-01 drops those rows and marks the player.
- Races flown by the harness stay marked as tool-flown and never score. Scoring, holder
  changes, no retakes and the end date are proven without the game and with generated rival
  folders (T-16, T-17).
- Setup lives in Controllers (`ParsekRaceSetup.cs`) behind an interface the UI declares; the UI
  cannot create a save through the `RaceWorld` facade (DC-102).
- The race world is reconciled at every scene load request against the loaded game's mark, not
  only on the way to the main menu (DC-027).

The owner's rulings of 2026-11-15 changed these plan points (each plan cites them as
"owner, 2026-11-15"):

- Dates compare to the exact second of game time, replacing whole days; only the very same second is
  shared (points counted in twelfths, design 7.8), and players level on total points at the end are
  joint winners. The calendar (the creator's day length, Q-09) only formats dates; cells and
  messages add the time of day when two dates fall on one day. R1-10, R4-01, R4-04, R4-07, R6-03,
  and the rows of r4 section 6.
- The race suit is locked to the player's race color, and the suit choice of the stock suit
  picker is blocked in a race save (only the suit choice: the same window still lets the
  player take off a kerbal's helmet and neck ring and change the suit light color): R5-10 and
  R5-11 change (no suit state is kept, so the `RACE_LOOK_DRESS` marker and its load-policy
  row are gone), and new task R5-14 blocks the suit choice.
- A race save whose space center cannot be built refuses to load (Q-10, R2-02); stock's
  "allow other launch sites" is greyed in a race created without them (Q-11, R2-08).
- The creator's choices: game name, mode, the full stock difficulty panel (presets as the
  fallback, Q-17), an optional end date, "no retakes" and shared launch sites both off by
  default, always four seats, no password, settings fixed after creation (R1-15, R1-17,
  R1-18). A player may change their own difficulty mid-game and a mark names the changed
  settings (R3E-15, R6-05). A player who stops keeps the seat and points; nobody can be
  removed (R1-09).
- Messages only when a rival is involved (Q-14, R4-07); after the end only final results
  (Q-16, R6-08); two computers: put back twice, then stop and say so (Q-12, R3E-20).
- A cheat tool marks everything after it, also after it is switched off (R3E-09, R6-01).
  Rival ghosts are no CommNet relays and can be targeted; leftovers are flyable vessels only;
  no rival ghosts in the Space Center view; the Space Race window opens in Flight and the
  Space Center only (R3W, R4-09, R4-11).

## 5. What starts first

`task-index.md` puts the 206 pull requests of the plan (211 live tasks) into 20 waves: a task
is in wave 0 when it needs nothing but the owner's go, and in wave N when everything it needs
is in a lower wave. A wave is not a week; it only says what can run side by side.

**Day one (wave 0, 25 tasks).** S0 (the spike setup on the owner's PC), P12-01, P11-01,
P11-02, P7-01, P1-01, P10-01, P2c-01, P2c-02, P2a-05, P2b-01, P3-01, P6-01, R1-02, R1-07,
R3E-07, R3E-12, R4-08, R5-01, T-01 to T-05 and T-07. None of them is visible to a player.
All are written and unit-tested in the cloud; eight also need a lane or an in-game batch on
the PC before they merge (P10-01, P11-02, P2c-02, P2a-05, P3-01, R5-01, T-07, and R4-08 as an
option). They run as tracks, one agent per track, tasks of a track in order
(`task-index.md` C.2):

| Track | Tasks | Why first |
|-------|-------|-----------|
| ARCH | P12-01, P7-01, P1-01, P7-02, P1-02 | heads every critical path: the `[forbidden]` list form R1-03 writes its edges in, and the floor files R1 builds on |
| SETUP | P10-01, R1-02, R1-16, R1-07 | create-and-start-a-save, which R1-10 calls |
| ENGINE | P2c-02, P2c-03, P2c-04, P2c-01, R3W-02 | the engine seams the second lane needs; R3W-02 registers `RaceWorld` |
| CATALOG | P2a-05, P2a-03, P2a-06, P2a-07, P2b-01 | the playback catalog port |
| MAP / CODEC | P3-01, P6-01, P6-02 | map primitive and trajectory decode |
| GUI | P11-01, P11-02, R4-08 | harness window pins derive; shared window chrome |
| FACTS | R3E-07, R3E-12 | data shapes for tool use and vessel ends |
| LOOKS | R5-01, R5-02 | kerbal ghost look options |
| HARNESS | T-03, T-04, T-06, T-05, T-07 | spec fields and the first race lane |
| TESTSIM | T-01, T-02 | the save-output driver and the fake transport |
| SPIKES | S0, then the day plan of `r0-spikes.md` | the owner's PC, about four days |

Tracks that edit the same file do not run side by side; C.2 lists the files
(`test_archview.py`, `hlib.py`, `GhostPlaybackEngine.cs`, `.claude/CLAUDE.md`).

**What bounds the milestones** (`task-index.md` B.2):

| Milestone | Task | Wave | Held by |
|-----------|------|------|---------|
| First race save created | R1-10 | 6 | spike S6, then the R1 core |
| The KSC moved to the player's site | R2-04 | 6 | spikes S4b and S5 |
| Menu entry and dialog | R1-14 to R1-18 | 9, 10 | create and join proven in lane RACE-1 |
| First standing exported | R3E-04 | 10 | R1-10 |
| A rival ghost plays in flight | R3W-09 | 14 | the export core and the rival store, not the playback prep (it ends at wave 10) |
| Board computed, then shown | R4-04, R4-10 | 13, 16 | the import (R3E-18) |
| Rivals on the map and in the Tracking Station | R3W-18, R3W-20 | 16, 18 | R3W-09 |
| No retakes and the end date proven | R6-11 | 18 | the board |
| Looks on rival ghosts | R5-09 | 19 | R3W-20 |
| Every scene's race categories hosted in a lane | T-29, then T-35 and T-36 | 14, 15 | the staged race (T-17, T-19); RACE-10 also R3W-09. A category joins its host when its first test lands |

The longest chain is the exchange: spike S6, the R1 core, the standing codec, the rival store,
the rival engine. The ghost positioner change (P2b), the largest prerefactor, has three waves
of slack against it, so its long flight proof does not hold the feature back.

## 6. The owner's PC

The spikes and the harness lanes need the same machine. `task-index.md` section D lists every
task that needs the PC, by wave. In short:

- **Spikes:** about 24 hours over four days (`r0-spikes.md`, day plan), then about 3 hours for
  S8 (sync program speed) at the very end. They need a test copy of the game and a copy
  without the expansions (question Q-01).
- **The ghost positioner change (P2b):** ten pull requests proven at the end by a short
  representative set of 18 lanes plus the daily tier (question Q-05, answered 2026-11-14);
  the full run of every lane happens once, at release time.
- **Everything else:** one in-game test batch or one short lane per task that says "PC: yes".
  The cloud runs the unit tests, the architecture suite and the harness Python suites.

PC lanes run through the parallel runner (`harness/tools/parallel_runner.py`, on main) where it
applies: two game copies while the owner works at the PC, three when away. Its one measurement
so far is the daily tier, 12 min 35 s on three copies against about 25 minutes one lane at a
time, so lanes take about half as long on three copies (an estimate from that one ratio). It
skips by default the lanes that move the cursor or take the focus, and the full tier on it is
not yet measured. What still runs alone: the spikes' frame-cost sessions (S5's frame cost, S8b,
S11), which need an otherwise idle PC, and the in-game test batches run by hand.

## 7. Questions for the owner

`design-corrections.md` part 2 lists 26 questions (Q-01 to Q-26), each with options and a
recommendation. Part 2 orders them by when the answer is needed; the groups below order them
by what the owner has to do, so Q-06 sits under "assumed" here. Eleven were answered on
2026-11-15; 11 remain open.

**Needed before work starts (2).** Q-01 a separate game copy for the spikes; Q-02 keep the
spike branch on GitHub.

**Assumed as recommended, the plans already follow them (4).** Q-06 P6 in parallel; Q-23 no
atmosphere marker for rivals in R3; Q-24 harness races never score; Q-25 two copies racing on
one PC as an optional lane.

**Asked when its phase comes (1).** Q-13 how the two saves of the "rivals change nothing" test
are compared. Until answered, the plan follows its recommendation.

**Asked after a spike (3).** Q-18 the copy worker (S8); Q-19 and Q-20 the replacement suits
(S3).

**Asked at the end, before S8 runs (1).** Q-03 which sync programs the file-speed test covers.
The owner (2026-11-14): "leave this at the end, its an external dependency". S8 is the last
piece of the plan, run after the race works end to end on a plain shared folder.

**Answered (15).** Q-04 (2026-11-14): fix both, in a separate work item outside this plan (the
launch pad fixes work); P1-04 is dropped and P1-02 is behavior-neutral. Q-05 (2026-11-14): no
run of every lane for P2b; a representative set of lanes rechecks the ghost system, and the full
run happens at release. Q-07 (2026-11-14): the Watch camera on rival flights comes later, not
in the first build. Q-26 by the owner's decision D3. On 2026-11-15 the owner answered: Q-08 the
menu entry is never hidden; Q-09 the day length is the creator's game setting (it now only
formats dates); Q-10 a race save whose space center cannot be built refuses to load; Q-11 the
race setting wins and the stock option is greyed; Q-12 put files back twice, then stop and say
so; Q-14 messages only when a rival is involved; Q-15 any real, clean rival flight you had seen
blocks you; Q-16 after the end only final results; Q-17 the full stock difficulty panel, with
the presets as the fallback; Q-21 one small plaque popup, only if S7 fails; Q-22 rival ghosts
show their owner's name and color.

## 8. The design after this plan

Done on 2026-11-15: the design is v3. It folds in `design-corrections.md` (all but DC-037 and
DC-062, which were rejected) and the owner's answers of 2026-11-14 and 2026-11-15. The
corrections to the analysis document (DC-126 to DC-132) went into that document's addendum;
DC-133 and DC-134 are `.claude/CLAUDE.md` items that wait for the first code pull request
touching those areas. The design has no PDF (`scripts/build-design-pdfs.py` does not list it),
so none was regenerated. `design-corrections.md` stays as the record of what changed and why;
its design line numbers are v2's. Later findings reach the design through the tasks that
already say so (S13 and R2-15 fold spike findings into it); where a plan file and the design
still disagree, the plan file names the correction the task follows. Like the rest of this
folder, that work lands on the `multiplayer` branch (rule 10).
