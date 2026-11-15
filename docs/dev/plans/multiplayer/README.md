# Multiplayer implementation - master plan

PLAN (2026-11-14; the owner's answers and the milestone ladder of 2026-11-15 folded in), not
started. Work starts after
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
| `task-index.md` | every task once, with its dependencies, its wave, its milestone (section F), the shared files, the PC use | picking the next task |
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
| `test-and-harness.md` | generators, fake transport, seam verbs, harness lanes RACE-0 to RACE-20, the two scripted players (lanes RCA and RCB) and the integration lanes RCI; the one list of what only the owner proves (section 1) | all phases |

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

### Milestones

The phases say which plan a task comes from; the milestones say in what order the work becomes
something a player could see, and how that is proved without the owner's eyes. Milestone one,
MS-1, is the skeleton: two games, one folder, one flight, one board (owner, 2026-11-15: its
tasks are taken first). It is built in four steps, MS-1a to MS-1d. MS-2 to MS-9 are the rings
that widen it; MS-10 is the capstone, two scripted careers flown to the Mun in one race.

Each task belongs to the earliest milestone whose exit proof needs it (task-index section F
lists every milestone's tasks with their waves and its full exit proof; B.2 its critical path).
A milestone's exit proof flies in the wave after its last task, and milestones are signed off
in this order (rule 11). The labels are milestone names, not lane ids: the existing lane
`MS-1-mission-leg-trim-clone` and r4's message rows M1 to M10 have nothing to do with them.

| Milestone | Tasks | Waves | DO / SEE | Exit proof (task-index F has the full list) |
|---|---|---|---|---|
| MS-1a Test bench and prepared ground | 56 | 0 to 5 | The race test bench runs headless and the prepared ground is in; single-player Parsek is unchanged; the spike answers are in the design. | T-01, T-02, T-05 and T-38 cells; RACE-0 (T-07); `--tag race` (T-45); the campaign runner's replay (T-49); the prerefactor tiers and lanes; S13's reports |
| MS-1b A race save, created and joined | 17 | 0 to 8 | Through the seam, a race save is created and three players join it at slots 1 to 3; a test game says so; a single-player save stays byte-identical. | RACE-1 (T-13); RACE-9, its own race at slot 1 (T-29); RACE-20 and the inertness test (T-44, T-10); the format and race grep gates; the race overlay (T-48) |
| MS-1c Headless skeleton: two machines, one board | 18 | 0 to 14 | In xUnit alone, N simulated machines exchange standings through a fake synced folder and compute byte-equal boards in any arrival order. | `RaceFolderBuilderTests` (T-16), `RaceSyncSimulatorTests` (T-18), same files same board (T-31), the import and board tests (R3E-18, R4-04 with T-52) |
| MS-1d Live skeleton: two games, one folder, one flight, one board | 35 | 0 to 16 | Two real saves share one folder: A flies and exports, B syncs and sees A's ghost and A's row; the two scripted players trade a first and see each other's ghosts. | RACE-6 (T-28, T-39), RACE-3 (T-26), RACE-2 (T-24), RACE-10 (T-35); the P2b lanes; RCA-1, RCB-1, RCA-2 (T-53); the board oracle (T-51); RCI-1 to RCI-10 (T-55) |
| MS-2 The four sites | 19 | 4 to 11 | Each player's space center sits at the player's site; launches, recoveries and the editor follow it; stock Kerbin comes back after a race; scripted player B flies from slot 2. | SITES-1 to SITES-3 (R2-12 to R2-14); RACE-11 (T-36); RACE-16, RACE-17 (T-42); the R2-12 / T-50 schedule row (stage S3) |
| MS-3 Edit evidence and self-heal | 6 | 2 to 15 | A changed or deleted standing is caught by the witness log; offline exports copy out later; a foreign edit of the own subfolder heals. | R3E-19 and R3E-20 tests; R3E-24's cell on RACE-9; RACE-8 (T-25); the paired runner (T-54) |
| MS-4 The rival world beyond flight | 29 | 0 to 19 | Rivals everywhere outside the one flight: leftovers, map and Tracking Station objects, labels, flags, plaques and the ghost budget, none leaking into the career. | RACE-4 (T-27); the rival-lane and isolation cells on RACE-10 and RACE-11; RACE-2's Tracking Station step; RACE-12 over a harvested folder (T-33) |
| MS-5 Screens and messages | 17 | 0 to 18 | From the main menu a player creates or joins through the dialog, changes the Multiplayer settings and reads the board with holder-change messages. | RACE-7 (T-32); RACE-19 (T-43); `SpaceRace` and `SpaceRaceFlight` on RACE-9 and RACE-10; the R1 and R4 form and presentation tests; the census step (R4-12) |
| MS-6 Scoring rules | 13 | 0 to 18 | The board marks tools, debug completions, save marks, uncounted parts, a differing content table and locked settings; a test game's test-tools mark alone still scores. | R3E-08, R3E-09, R1-19 to R1-22, R6-01, R6-05, R6-06, R6-12 tests; `RaceFacts` on RACE-10; RACE-15 (T-41) |
| MS-7 Looks | 7 | 1 to 19 | Kerbals and EVA ghosts wear their player's look; a rival's look shows without Making History; the suit choice is blocked in a race save. | `RaceLooks` on RACE-9; `RaceLooksFlight` on RACE-10; the `GhostVisuals` cells on H15; the zip-listing cell; the single-player halves on RACE-20 |
| MS-8 End date and no retakes | 10 | 13 to 19 | No retakes, the end date with a final board and joint winners, late standings and the keep rule; a scripted player retakes by rewind and flies a Re-Fly against a rival. | the R6 and R3E-21 tests; T-37; RACE-14 (T-40); RCA-3r to RCA-3f and RCB-3r to RCB-3f (T-56, stage S4) |
| MS-9 Sync tuning (S8, last) | 4 | 17 to 19 | The poll numbers are tuned on real sync clients; the copy worker exists only with the owner's yes. | S8a and S8b; R6-10's tests and diagnostics budget; R3E-23's worker tests |
| MS-10 Two scripted careers to the Mun (capstone) | 2 | 16 to 18 | Both scripted players fly their whole careers to the Mun in one race from two sites, and the board oracle agrees at every stage. | RCA-1 to RCA-5 and RCB-1 to RCB-5 on two slots (T-57, stage S5) |
| unanchored | 8 | - | P3b-01 to P3b-04, R3W-26 and R5-06 wait for the Watch camera; R2-10 and R5-05 are conditional | - |

The two scripted players (test-and-harness section 4b) are a track through the ladder, not one
late milestone: player A replays the single-player career campaign on a race overlay, player B
replays it from another site and on another schedule, and each stage joins a milestone's exit
proof (S0 MS-1a and MS-1b, S1 and S2 MS-1d, S3 MS-2, S4 MS-8, S5 MS-10).

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
   (R5-14, added 2026-11-15; R1-19 to R1-22, R3E-25 and R6-12, added 2026-11-15 for the
   rulings on the tech tree and the counted parts; R1-23 and T-37 to T-59, added 2026-11-15
   for the milestones and test games). P2a-01 is a specification, not a pull request.
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
   categories have five such specs: the host lanes RACE-9 (T-29, wave 8), RACE-10 (T-35)
   and RACE-11 (T-36, both wave 9), one per scene, and the SITES lanes, which pin their own
   categories from wave 11 (`RaceSitesStock` in SITES-2, R2-13; `RaceSitesEditor` in
   SITES-3, R2-14). RACE-9 creates and joins its own race at slot 1; RACE-10 and RACE-11 move
   onto the staged race when the rival categories need rivals (R3W-09, R3W-20). A category no
   committed spec pins yet needs no re-pin when a test is added (R5-14's `RaceLooks` cell
   lands in wave 7, before RACE-9); once a spec pins it, a test added to it re-pins that
   spec's tally in the same pull request. A cell that needs no race save goes into an existing
   category on its existing host instead (`GhostVisuals` on H15, `GhostMap` on S1.6), so it
   flies the day it lands; the single-player half of a race cell runs on RACE-20 (T-44). Every
   race lane is tier operator with the tag `race` (T-45). A spec runs several `RunTests`
   steps only when each names one category and the spec sets `batchVacuityOptOut = true` with
   a reason (`hlib.py:8321-8343`).
8. **New player-facing surfaces** (the kinds `.claude/CLAUDE.md` forbids: windows, popups, badge
   counters, panels) are only the ones the owner asked for, the same list as design section 0: the
   main menu Multiplayer entry, the race setup dialog, the Space Race window, and, only if spike S7
   shows the stock plaque window cannot show a rival's text, one small popup for reading a rival's
   flag (question Q-21, owner, 2026-11-15). That is four, the last one conditional. R1-14 writes the
   first three into `.claude/CLAUDE.md` as one new exception bullet; R3W-25 adds the popup to that
   bullet only if S7 fails. The Route History window is an older single-player exception of
   `.claude/CLAUDE.md`, not one of these. Not new surfaces: the Multiplayer section of the existing
   Settings window, hover text inside the Space Race window, one-shot screen messages for events,
   the blocks on stock controls (the greyed "allow other launch sites" option, R2-08; the nine
   greyed progression settings, R1-21; the blocked suit choice, R5-14), which use the existing
   stock-control annotation exception, and the stock confirmation dialog that warns before a create,
   join or load of a game whose tree or part values differ (R1-20), the same stock dialog the setup
   already uses for confirmations. Anything else needs a new ruling.
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
      (`.github/workflows/tests.yml`, `pull_request:` with no branch filter). T-45, a wave 0
      task taken first on the branch, adds `multiplayer` to the workflow's `push:` branches,
      so every merge is checked as a whole too. The metrics job stays main only. The owner gives
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
      the final pull request carries the work of about 236 pull requests at once, which is
      why every lane is flown on its tip first.

11. **Testing in parallel** (2026-11-15). Feature work and test work run side by side; the
    milestones (section 2) are where they meet.
    1. Every feature task names its proof (an xUnit class, an in-game category, a lane) and
       merges with its unit tests.
    2. Work is gated by dependencies only. A task merges when its dependencies have merged and
       its own proof is green, whatever milestone it belongs to.
    3. A test-tooling task lands no later than one wave after the feature it proves. Measured:
       each tooling task the graph pairs with a feature (T-12, T-16, T-18, T-19, T-20, T-21,
       T-30) lands exactly one wave after it.
    4. A milestone's exit proof flies in the wave after its last task lands, and milestones are
       signed off in ladder order (MS-1a, MS-1b, MS-1c, MS-1d, MS-2 to MS-10), even where a
       later one closes in an earlier wave (MS-2 closes at wave 11, before MS-1c and MS-1d).
    5. A red exit proof freezes only the tasks whose dependency closure reaches into that
       milestone, until it is green again; everything else keeps merging. The measured freeze
       radius (task-index B.2 and F give the members):

    | A red exit proof of | Freezes, of the later milestones' tasks |
    |---|---|
    | MS-1a | 170 of 177 |
    | MS-1b | 138 of 160 |
    | MS-1c | 80 of 142 |
    | MS-1d | 50 of 107 |
    | MS-2 | 23 of 88 |
    | MS-3 | 5 of 82 |
    | MS-4 | 18 of 53 |
    | MS-5 | 19 of 36 |
    | MS-6 | 10 of 23 |
    | MS-7 | 0 of 16 |
    | MS-8 | 1 of 6 |
    | MS-9 | 0 of 2 |
    | MS-10 | 0 of 0 |

       The rejected alternative, the index rule (a feature task waits until the milestone two
       rungs below its own has closed), measured at 20 -> 39 waves, holding 130 tasks; waiting
       only on the milestones its own dependencies sit in still gives 29 waves.
    6. No task relies on a manual check where a headless test, an in-game test or a lane can
       prove it. A race-only in-game category waits for its host lane (SPACECENTER from wave 2
       to RACE-9 at wave 8; FLIGHT and TRACKSTATION from wave 6 to RACE-10 and RACE-11 at
       wave 9); the host's first flight runs every cell written so far, and a cell that needs
       no race save goes into an existing category instead (rule 7). What needs the owner's
       eyes or two PCs is listed once, in `test-and-harness.md` section 1, "What only the
       owner proves".

### Names fixed across the plans

| Thing | Name |
|-------|------|
| Seam verbs | `RaceSetup action=create|join`, `RaceCommand action=status|set-folder|sync|offline|online`, `ListHandles kind=sites|rivals|board` |
| Harness lanes | `RACE-0` to `RACE-20` (test-and-harness), the scripted players' `RCA-k` / `RCB-k` (generated by T-50), the integration lanes `RCI-1` to `RCI-10` (T-55), `SITES-1` to `SITES-3` (r2); every one tier operator with the tag `race` |
| Test game | the race manifest key `testGame` (R1-23), set only by the seam's `RaceSetup action=create`; "Test game" in the Space Race window header and the join summary; in a test game the test-tools mark alone does not stop a flight from scoring (R6-01) |
| Scene request handler | `ParsekRaceBoot.OnSceneRequest`, the only one; it calls `RaceSites.Reconcile` on every request |
| Refused load | `Patches/RaceLoadGatePatch.cs`, a prefix on `Game.Start()` that refuses a race save before its first scene (R2-02); a scene request cannot turn a load back |
| Race scenario child hook | `IRaceScenarioChild` |
| Date rule | `RaceCore/RaceCalendar.cs`, `TryAcceptDateRule`, reason `date-unit-not-second`; dates compare to the exact second, the calendar only formats them (owner, 2026-11-15) |
| Import verdict | `RivalStandingVerdict`, declared by R3E-04, filled by R3E-18 |
| Milestone table | `RaceCore/MilestoneTable.cs`, owned by R3E-11; R4-03 is only the ladder filter |
| Debug completions | wire rows `DISCLOSURE` / `DEBUG_COMPLETION`, codec member `Standing.DebugCompletions`, player mark `DebugCompleted` |
| Race content table | `RaceCore/RaceContentTables.cs`, id `race-content-1` (manifest and link key `contentTableId`), generated by R1-19's dev tool; refusals `content-table-unknown` and `content-differs` (R1-19, R1-20); creator setting `allowUnscoredPlay` (on by default) and the list `PART_SETS` (`making-history`, `breaking-ground`, `restock-plus`; none by default, assumed) |
| Uncounted parts and save marks | wire children `REC` / `UNCOUNTED { name ut why }` (`why` = `outside` or `changed`), rows `DISCLOSURE` / `SAVE_MARK { kind detail ut }` and key `contentDiffers` (R3E-09, R3E-25); claim statuses `UncountedParts` and `SaveMarked` (R6-12); tool kind `ContentDiffers = 7` |
| Launch areas | `SiteRow.LaunchAreas`, read through `LaunchAreaGeometry.Contains` |
| Line and label style | `PlaybackTrajectoryStyle` (R5-03); label prefix `"<display name> - "` |
| Ghost map name | `GhostMapPresence.ComposeGhostVesselName`: `Ghost: Ana - Kerbal X` |
| Empty catalog for the rival lane | `RecordingPlaybackCatalog.None` (R3W-09); there is no `RivalCatalog` |
| Race screen messages | Every race screen message, by task. Through one posting site, the sync driver's, landed by R3E-22, which logs `[RaceSync] Message posted:` for each: R3E-22's "<n> shared flights left the race"; R3E-18's clash note (a look or site taken by a later joiner, R1-09's rule) and "<name> joined the race"; R3E-20's heal message (files put back) and its two-computers message (the folder is being written from two computers); R4-07's holder changes; R6-08's two end-of-race messages. Outside the driver, each from its own task: R2-02's refused load (with R1-20's second reason, a tree or part values that differ); R2-08's refused launch, and its snap-back message ("Other launch sites stay off: ...") only if the stock toggle cannot be greyed; R1-21's snap-back message ("Progression settings stay as this race was created") for a setting it cannot grey; R1-22's launch warning ("This flight will not score: <part names>"); R3W-19's refused switch to a rival ghost. There are no others (R1-20's warning before a join or load is the stock confirmation dialog, not a screen message) |
| Log tags | plain: `RaceSync`, `RivalLane`, `RaceBoard`, listed in R1-06 |
| Race lane install | the `stock-tree` profile (the stock tech tree, so R1-20's check passes); `stock-minimal` loads CommunityTechTree and ProbesBeforeCrew, and the race lanes that run there do so on purpose: SITES-2 (R2-13) and RACE-15 (T-41) prove the warning path, RACE-17 (T-42) the refused load with play without scoring off; RACE-16 (T-42) runs on `modded-compat` to load a race save on a changed Kerbin; RACE-0 (T-07) and RACE-20 (T-44) load no race save; a lane answers a warning with the seam arg `unscoredConfirm=continue|cancel` (default `cancel`) on `RaceSetup action=create|join` and `LoadGame` (R1-20) |

### In-game test categories

One scene and one host lane per category. No other spec runs a batch of a category that
RACE-9, RACE-10 or RACE-11 pins. Cells that need no race save go into existing categories on
their existing hosts (`GhostVisuals` on H15, `GhostMap` on S1.6), and the single-player halves
of race cells run on RACE-20 (T-44), a single-player save.

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
- Test games (owner, 2026-11-15, replacing "races flown by the harness never score"): a game
  created through the command seam's create step is a test game (`testGame = True` in its
  manifest, R1-23), named "Test game" in the Space Race window header and the join summary.
  In a test game the test-tools mark alone does not stop a flight from scoring; every other
  mark applies. A normal game is unchanged: a flight flown with a test tool scores 0 (R3E-08
  says when a recording carries the mark). So the lanes and the two scripted players prove
  holder changes, retakes, no retakes and the end date on real flights; generated rival
  folders (T-16, T-17) stay for the faults and edges flights cannot make.
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
  default, the part sets (none ticked by default, assumed) and "allow play without scoring"
  (ticked by default), always four seats, no password, settings fixed after creation (R1-15,
  R1-17, R1-18). A player may change their own difficulty mid-game and a mark names the changed
  settings (R3E-15, R6-05), apart from the progression settings, which are locked (R1-21,
  design 7.12). A player who stops keeps the seat and points; nobody can be
  removed (R1-09).
- One reference table shipped with Parsek (`race-content-1`) holds the stock tech tree and the parts
  of the base game, Making History, Breaking Ground and ReStock+ with their gameplay values; the
  tree is always the stock tree in the first build (other trees come later). The creator setting
  "allow play without scoring", ticked by default and fixed after creation, decides what a game
  whose tree or counted-part values differ does: off, it does not create, join or load; on, one
  warning, and its flights and the nodes it buys do not score. Nothing else is blocked. One checkbox
  per part set (base always counted; none ticked by default, assumed until the owner answers, stated
  2026-11-15); a flight with a part outside the counted sets on its path scores 0, shown at 0 points
  with the parts in the hover; a later clean flight does not score it; the nine progression settings
  are locked (R1-19 to R1-22, R2-02 and R2-08 by edit, R3E-09, R3E-25, R6-02, R6-12).
- Messages only when a rival is involved (Q-14, R4-07); after the end only final results
  (Q-16, R6-08); two computers: put back twice, then stop and say so (Q-12, R3E-20).
- A cheat tool marks everything after it, also after it is switched off (R3E-09, R6-01).
  Rival ghosts are no CommNet relays and can be targeted; leftovers are flyable vessels only;
  no rival ghosts in the Space Center view; the Space Race window opens in Flight and the
  Space Center only (R3W, R4-09, R4-11).

## 5. What starts first

`task-index.md` puts the 236 pull requests of the plan (241 live tasks) into 20 waves: a task
is in wave 0 when it needs nothing but the owner's go, and in wave N when everything it needs
is in a lower wave. A wave is not a week; it only says what can run side by side. The
milestones (section 2) say what to take first: MS-1a, then the rest of the skeleton.

**Day one (MS-1a, 21 tasks with no dependency at all).** S0 (the spike setup on the owner's
PC), P12-01, P11-01, P11-02, P7-01, P1-01, P10-01, P2c-01, P2c-02, P2a-05, P2b-01, P3-01,
P6-01, T-01 to T-05, T-07, T-45 and T-49. None of them is visible to a player. All are written
and unit-tested in the cloud; seven also need a lane or an in-game batch on the PC before they
merge (P10-01, P11-02, P2c-02, P2a-05, P3-01, T-07, and T-49's one chain replay). Wave 0 holds
27 tasks in all: the other six (R1-02, R1-07, R3E-07, R3E-12, R4-08, R5-01) also need nothing,
but belong to later milestones and start when a track frees up. They run as tracks, one agent
per track, tasks of a track in order (`task-index.md` C.2):

| Track | Tasks | Why first |
|-------|-------|-----------|
| ARCH | P12-01, P7-01, P1-01, P7-02, P1-02 | heads every critical path: the `[forbidden]` list form R1-03 writes its edges in, and the floor files R1 builds on |
| SETUP | P10-01, R1-02, R1-16, R1-07 | create-and-start-a-save, which R1-10 calls |
| ENGINE | P2c-02, P2c-03, P2c-04, P2c-01, R3W-02 | the engine seams the second lane needs; R3W-02 registers `RaceWorld` |
| CATALOG | P2a-05, P2a-03, P2a-06, P2a-07, P2b-01 | the playback catalog port |
| MAP / CODEC | P3-01, P6-01, P6-02 | map primitive and trajectory decode |
| HARNESS | T-03, T-04, T-06, T-05, T-38, T-45, T-07 | spec fields, the gated leak scan, the `race` tag and the CI push trigger (rule 10), the first race lane |
| RUNNER | T-49, T-59, T-54 | the campaign chain runner the scripted players fly on; the harvest flag; paired lanes |
| TESTSIM | T-01, T-02 | the save-output driver and the fake transport |
| GUI | P11-01, P11-02, R4-08 | harness window pins derive; shared window chrome |
| FACTS | R3E-07, R3E-12 | data shapes for tool use and vessel ends (early starters of MS-6 and MS-4) |
| LOOKS | R5-01, R5-02 | kerbal ghost look options (cells on H15) |
| SPIKES | S0, then the day plan of `r0-spikes.md` | the owner's PC, about four days |

Tracks that edit the same file do not run side by side; C.2 lists the files
(`test_archview.py`, `hlib.py`, `GhostPlaybackEngine.cs`, `.claude/CLAUDE.md`,
`parallel_runner.py`). The supervisor runs at most four agents at once: ARCH, SETUP, ENGINE and
HARNESS first.

**What comes next.** MS-1b (a race save, created and joined) closes at wave 8, MS-1c (the
headless two-machine board) at wave 14 and MS-1d (the live skeleton) at wave 16; their chains
and every later milestone's are in `task-index.md` B.2. The longest chain is the exchange:
spike S6, the R1 core, the standing codec, the import, then the board or the rival lane. The
ghost positioner change (P2b), the largest prerefactor, ends at wave 10 inside MS-1d with slack
against R3W-09 (wave 14), so its long flight proof does not hold the feature back.

## 6. The owner's PC

The spikes and the harness lanes need the same machine. `task-index.md` section D lists every
task that needs the PC, by wave. In short:

- **Spikes:** about 25 hours over four days (`r0-spikes.md`, day plan), then about 3 hours for
  S8 (sync program speed) at the very end. They need a test copy of the game and a copy
  without the expansions (question Q-01).
- **The ghost positioner change (P2b):** ten pull requests proven at the end by a short
  representative set of 18 lanes plus the daily tier (question Q-05, answered 2026-11-14);
  the full run of every lane happens once, at release time.
- **The race lanes:** every one tier operator with the tag `race`, flown on request through
  `parallel_runner.py run --tag race` (T-45). Their first flights add about 2.1 hours one lane
  at a time (7465 s of estimates; 13 lane tasks not yet estimated), most of it the first change of
  hands (T-53) and the integration lanes (T-55); the two scripted careers to the Mun (T-57,
  MS-10) take about 2.6 hours on two slots. `task-index.md` D.4 has the time per milestone.
- **Everything else:** one in-game test batch or one short lane per task that says "PC: yes";
  a race-only category runs on its host lane, a cell that needs no race save on an existing
  lane. The cloud runs the unit tests, the architecture suite and the harness Python suites.
- **What only the owner proves:** one list, in `test-and-harness.md` section 1: real sync
  clients, clock skew, two operating systems, a race on two real PCs, how the screens feel,
  frame cost with real sync clients, and one real scoring flight in a normal game. A copy
  without the expansions, an older build and two installs are reached on one PC by the lanes
  and are not on it.

PC lanes run through the parallel runner (`harness/tools/parallel_runner.py`, on main) where it
applies: two game copies while the owner works at the PC, three when away. Its one measurement
so far is the daily tier, 12 min 35 s on three copies against about 25 minutes one lane at a
time, so lanes take about half as long on three copies (an estimate from that one ratio). It
skips by default the lanes that move the cursor or take the focus, and the full tier on it is
not yet measured. What still runs alone: the spikes' frame-cost sessions (S5's frame cost, S8b,
S11), which need an otherwise idle PC.

## 7. Questions for the owner

`design-corrections.md` part 2 lists 26 questions (Q-01 to Q-26), each with options and a
recommendation. Part 2 orders them by when the answer is needed; the groups below order them
by what the owner has to do, so Q-06 sits under "assumed" here. Twelve were answered on
2026-11-15; 10 remain open.

**Needed before work starts (2).** Q-01 a separate game copy for the spikes; Q-02 keep the
spike branch on GitHub.

**Assumed as recommended, the plans already follow them (3).** Q-06 P6 in parallel; Q-23 no
atmosphere marker for rivals in R3; Q-25 two copies racing on one PC as an optional lane.

**Asked when its phase comes (1).** Q-13 how the two saves of the "rivals change nothing" test
are compared. Until answered, the plan follows its recommendation.

**Asked after a spike (3).** Q-18 the copy worker (S8); Q-19 and Q-20 the replacement suits
(S3).

**Asked at the end, before S8 runs (1).** Q-03 which sync programs the file-speed test covers.
The owner (2026-11-14): "leave this at the end, its an external dependency". S8 is the last
piece of the plan, run after the race works end to end on a plain shared folder.

The rulings of 2026-11-15 on the tech tree and the counted parts (design 13.A) were not
numbered questions. One point of them is still assumed: the part sets ticked by default
(none; stated 2026-11-15, design 13.C). Trees other than stock, with the creator's tree as
everyone's reference, are later.

**Answered (16).** Q-04 (2026-11-14): fix both, in a separate work item outside this plan (the
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
show their owner's name and color; Q-24, overturned: a game the test tools create is a labelled
test game that scores despite the test-tools mark (section 4).

## 8. The design after this plan

Done on 2026-11-15: the design is v3. It folds in `design-corrections.md` (all but DC-037 and
DC-062, which were rejected) and the owner's answers of 2026-11-14 and 2026-11-15. The
corrections to the analysis document (DC-126 to DC-132) went into that document's addendum;
DC-133 and DC-134 are `.claude/CLAUDE.md` items that wait for the first code pull request
touching those areas. The design has a PDF since 2026-11-15,
`docs/pdf/parsek-multiplayer-design.pdf` (`scripts/build-design-pdfs.py` lists it), so a
change to the design reruns that script. `design-corrections.md` stays as the record of what changed and why;
its design line numbers are v2's. Later findings reach the design through the tasks that
already say so (S13 and R2-15 fold spike findings into it); where a plan file and the design
still disagree, the plan file names the correction the task follows. Like the rest of this
folder, that work lands on the `multiplayer` branch (rule 10).
