# Multiplayer implementation - master plan

PLAN (2026-11-14), not started. Work starts after 1.1.0 is released, on the owner's go.

This folder holds the implementation plan for the competitive multiplayer mode ("the space
race"). The design is `docs/dev/design-competitive-multiplayer.md` (v2); the measured list of
prerefactors is `docs/dev/research/multiplayer-base-refactoring-2026-11-14.md`. Nothing here is
code. Every task below is one pull request with its own tests, docs and one clean review.

## 1. What is in this folder

| File | What it is | Read it when |
|------|------------|--------------|
| `README.md` | this file: the rules, the decisions, what starts first, the owner's questions | first |
| `task-index.md` | every task once, with its dependencies, its wave, the shared files, the PC use | picking the next task |
| `design-corrections.md` | 134 corrections to the design found while planning, and 26 questions for the owner | before the design is touched |
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
| R0 spikes | r0 | answers, not code: site positions, scenery copy, suits, difficulty panel, plaque window, sync program speed, what a mod can see of debug tools |
| R0b preparation | small, playback, map | base Parsek ready for a second ghost lane; no player-visible change except two small fixes (question Q-04) |
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
   R3W-31, R4-14 and R5-13 are dropped; each keeps a heading that says what replaced it. No
   task is renumbered. P2a-01 is a specification, not a pull request.
4. **Race code is inert outside a race save.** A single-player save gains no node, no file, no
   window and no cost from any task. The isolation gates (R1-06, R3W-27) and the "rivals change
   nothing" lanes prove it.
5. **Race formats.** R1-12 registers both format rows (`race-link`, `race-exchange`) and writes
   golden folder `v2`. R3E-04 registers nothing and adds golden `v3` at the same numbers. Both
   use T-01's driver. One pin class, `RaceFormatPinTests`.
6. **No thread.** Every file access is on the main thread until the owner allows the one copy
   worker (question Q-18, after spike S8). The plan builds the main-thread form first.
7. **New player-facing surfaces** are only the ones the owner asked for: the Multiplayer menu
   entry, the setup dialog, the Space Race window, the race section in Settings. R1-14 writes
   the exception into `.claude/CLAUDE.md`. Anything else needs a new ruling (question Q-21).

### Names fixed across the plans

| Thing | Name |
|-------|------|
| Seam verbs | `RaceSetup action=create|join`, `RaceCommand action=status|set-folder|sync|offline|online`, `ListHandles kind=sites|rivals|board` |
| Harness lanes | `RACE-0` to `RACE-13` (test-and-harness), `SITES-1` to `SITES-3` (r2) |
| Scene request handler | `ParsekRaceBoot.OnSceneRequest`, the only one; it calls `RaceSites.Reconcile` on every request |
| Race scenario child hook | `IRaceScenarioChild` |
| Date rule | `RaceCore/RaceCalendar.cs`, `TryAcceptDateRule`, reason `date-unit-not-day` |
| Import verdict | `RivalStandingVerdict`, declared by R3E-04, filled by R3E-18 |
| Milestone table | `RaceCore/MilestoneTable.cs`, owned by R3E-11; R4-03 is only the ladder filter |
| Debug completions | wire rows `DISCLOSURE` / `DEBUG_COMPLETION`, codec member `Standing.DebugCompletions`, player mark `DebugCompleted` |
| Launch areas | `SiteRow.LaunchAreas`, read through `LaunchAreaGeometry.Contains` |
| Line and label style | `PlaybackTrajectoryStyle` (R5-03); label prefix `"<display name> - "` |
| Ghost map name | `GhostMapPresence.ComposeGhostVesselName`: `Ghost: Ana - Kerbal X` |
| Empty catalog for the rival lane | `RecordingPlaybackCatalog.None` (R3W-09); there is no `RivalCatalog` |
| Screen messages | one posting site, the sync driver's, landed by R3E-22 |
| Log tags | plain: `RaceSync`, `RivalLane`, `RaceBoard`, listed in R1-06 |

### In-game test categories

One scene and one host lane per category. No other spec runs a batch of a category that
RACE-9, RACE-10 or RACE-11 pins.

| Category | Scene | Host lane |
|----------|-------|-----------|
| RaceMode, RaceSites, RaceExchange, SpaceRace, RaceLooks | SPACECENTER | RACE-9 |
| RaceSitesFlight, RaceFacts, RaceRivalLane, RaceRivalIsolation, SpaceRaceFlight, RaceLooksFlight | FLIGHT | RACE-10 |
| RaceSitesTrackingStation, RaceFactsTrackingStation, RaceRivalLaneTrackingStation, RaceRivalIsolationTrackingStation | TRACKSTATION | RACE-11 |
| RaceSitesStock | SPACECENTER, a single-player save loaded after a race save | SITES-2 |
| RaceSitesEditor | EDITOR | SITES-3 |

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
  are seen on the map, in the Tracking Station and as ghosts nearby (question Q-07).
- The Multiplayer entry stays behind a developer switch until rivals and the board exist
  (question Q-08).
- A milestone completed from the debug menu does not score, by the owner's decision D3. R3E-09
  records what spike S9 shows a mod can see; R6-01 drops those rows and marks the player.
- Races flown by the harness stay marked as tool-flown and never score. Scoring, holder
  changes, no retakes and the end date are proven without the game and with generated rival
  folders (T-16, T-17).
- Setup lives in Controllers (`ParsekRaceSetup.cs`) behind an interface the UI declares; the UI
  cannot create a save through the `RaceWorld` facade (DC-102).
- The race world is reconciled at every scene load request against the loaded game's mark, not
  only on the way to the main menu (DC-027).

## 5. What starts first

`task-index.md` puts the 207 pull requests of the plan (212 live tasks) into 21 waves: a task
is in wave 0 when it needs nothing but the owner's go, and in wave N when everything it needs
is in a lower wave. A wave is not a week; it only says what can run side by side.

**Day one (wave 0, 25 tasks).** S0 (the spike setup on the owner's PC), P12-01, P12-02,
P11-01, P11-02, P7-01, P1-01, P10-01, P2c-01, P2c-02, P2a-05, P2b-01, P3-01, P6-01, R1-07,
R3E-07, R3E-12, R4-08, R5-01, T-01 to T-05 and T-07. None of them is visible to a player.
All are written and unit-tested in the cloud; eight also need a lane or an in-game batch on
the PC before they merge (P10-01, P11-02, P2c-02, P2a-05, P3-01, R5-01, T-07, and R4-08 as an
option). They run as tracks, one agent per track, tasks of a track in order
(`task-index.md` C.2):

| Track | Tasks | Why first |
|-------|-------|-----------|
| ARCH | P12-01, P12-02, R1-01, P7-01, P1-01, P7-02, P1-02 | heads every critical path: the module rules R1 builds on |
| SETUP | P10-01, R1-02, R1-16, R1-07 | create-and-start-a-save, which R1-10 calls |
| ENGINE | P2c-02, P2c-03, P2c-04, P2c-01 | the engine seams the second lane needs |
| CATALOG | P2a-05, P2a-03, P2a-06, P2a-07, P2b-01 | the playback catalog port |
| MAP / CODEC | P3-01, P6-01, P6-02 | map primitive and trajectory decode |
| GUI | P11-01, P11-02, R4-08 | harness window pins derive; shared window chrome |
| FACTS | R3E-07, R3E-12 | data shapes for tool use and vessel ends |
| LOOKS | R5-01, R5-02 | kerbal ghost look options |
| HARNESS | T-03, T-04, T-06, T-05, T-07 | spec fields and the first race lane |
| TESTSIM | T-01, T-02 | the save-output driver and the fake transport |
| SPIKES | S0, then the day plan of `r0-spikes.md` | the owner's PC, about four days |

Tracks that edit the same file do not run side by side; C.2 lists the files
(`modules.toml`, `test_archview.py`, `hlib.py`, `GhostPlaybackEngine.cs`, `.claude/CLAUDE.md`).

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
| All in-game categories hosted in lanes | T-29 | 20 | the last rival task |

The longest chain is the exchange: spike S6, the R1 core, the standing codec, the rival store,
the rival engine. The ghost positioner change (P2b), the largest prerefactor, has three waves
of slack against it, so its long flight proof does not hold the feature back.

## 6. The owner's PC

Only one KSP runs a flight at a time, and the spikes need the same machine. `task-index.md`
section D lists every task that needs the PC, by wave. In short:

- **Spikes:** about 25 hours over four days (`r0-spikes.md`, day plan). They need a test copy
  of the game and a copy without the expansions (question Q-01).
- **The ghost positioner change (P2b):** ten pull requests proven by one flight run at the end
  (question Q-05 asks how long that run is).
- **Everything else:** one in-game test batch or one short lane per task that says "PC: yes".
  The cloud runs the unit tests, the architecture suite and the harness Python suites.

## 7. Questions for the owner

`design-corrections.md` part 2 lists 26 questions (Q-01 to Q-26), each with options and a
recommendation. They fall into four groups.

**Needed before work starts (5).** Q-01 a separate game copy for the spikes; Q-02 keep the
spike branch on GitHub; Q-03 which sync programs the file-speed test covers; Q-04 land two
small single-player fixes with the launch-pad rule; Q-05 how long the PC flies to prove the
ghost-position change.

**Assumed as recommended, the plans already follow them (7).** Q-06 P6 in parallel; Q-07 no
Watch camera on rivals in the first build; Q-08 menu entry hidden until R3 and R4; Q-22 rival
ghosts show their owner's name; Q-23 no atmosphere marker for rivals in R3; Q-24 harness races
never score; Q-25 two copies racing on one PC as an optional lane.

**Asked when their phase comes (9).** Q-09 who sets the length of a race day; Q-10 a race save
whose space center cannot be set up; Q-11 "allow other launch sites" turned on against the race
setting; Q-12 one player on two computers at once; Q-13 how the two saves of the "rivals change
nothing" test are compared; Q-14 announce a first no rival had reached; Q-15 no retakes and a
flight that does not count itself; Q-16 messages after the end date. Until answered, each plan
follows its recommendation.

**Asked after a spike (5).** Q-17 difficulty presets only (S6); Q-18 the copy worker (S8); Q-19
and Q-20 the replacement suits (S3); Q-21 a small dialog for a rival's plaque (S7).

Q-26 is answered by the owner's decision D3.

## 8. The design after this plan

The design is not edited by this plan. The first docs task of the implementation folds
`design-corrections.md` and the owner's answers into the design as v3, in one pull request with
two reviews, and regenerates its PDF. Until then, where the design and a plan disagree, the
correction named in the plan is the reading the task follows.
