# Spike S4a: where the KSC is and what reads it (<YYYY-MM-DD>)

Verdict: OWED (draft). The code, the run sheet and the summarizer are ready. Every measured
cell below is OWED until `PC-RUN.md` has been run on the owner's PC.

This draft becomes `docs/dev/research/mp-spike-S4a-<YYYY-MM-DD>/README.md` (plan 6.4). Fill
it from two files the run sheet produces: `summary-s4a.md` (step 14) and `turnback.txt`
(step 17).

## Question
The KSC `PQSCity` origin, its decal's fields, the facility and launch-site spawn points, the
pad and runway footprints, and the order of the scene and save events on each way into a
save. Feeds S1b (decal radius, true origin), S4b (what to move, when), P1-01 (offsets),
R2-01's `SiteRow.LaunchAreas` (7.7, OV-41) and the P1-03 VERIFY; and whether a race save
can be turned back at the main menu before its first scene loads (the refused load of
R2-02, question Q-10).

## Decides
- R1-04 and R2-01: is the new `Game` set at the scene load request (OV-51)?
- R2-05 and P1-03: does the circle cache hold the KSC pad and runway (OV-52)?
- R2-02: the turn-back at the main menu (Q-10).
- S1b: the decal radius and the true origin. S4b: what to move, and when.
- P1-01: offsets. R2-01: `SiteRow.LaunchAreas` (design 7.7, OV-41).

## Build
- `spike/mp-r0` at OWED (it must contain `spike/mp-r0-s4a`).
- Deployed DLL sha256: OWED (`$RS\deployed-dll-sha256.txt`).
- Instance `spike-r0`, KSP 1.12.5, DLCs: OWED, mods: OWED (from the instance manifest).
- Env, session 1: `PARSEK_SPIKE=S4a`, `PARSEK_TEST_COMMANDS=1`.
- Env, session 2: `PARSEK_SPIKE=S4a`, `PARSEK_SPIKE_S4A_TURNBACK=1`.

## Procedure as run
As in `spikes/r0/S4/PC-RUN.md`, steps 0 to 18. Record any change from the plan here.

Every save comes from the `spike-s0` career (`PC-SETUP.md` step 9): `SPIKE-S4A-ON` (other
launch sites allowed), `SPIKE-S4A-OFF` (not allowed), `SPIKE-REFUSE-1`, and the new career
`SPIKE-S4A-NEW`.

Session 1 covers the plan's five ways in, each followed by an automatic dump (the plan's
`DumpKscState`; see the corrections below):
1. Resume Saved.
2. Seam `LoadGame` (the main menu bypassed).
3. A launch from the VAB.
4. Quickload (F9).
5. Revert to Launch.
6. Seam `LoadGame` back.
7. A launch with other sites allowed.
8. New Game.

Session 2 is the turn-back of `SPIKE-REFUSE-1`, then a normal load from the same menu.

Changes from the plan:
- **No `DumpKscState` run by hand after each way in.** A Ctrl+Shift+T batch (`RunTests`
  by category) skips `AllowBatchExecution = false` tests, so the seam cannot run it. The
  armed `SpikeS4EventOrder` dumps by itself 3 s after `onLevelWasLoadedGUIReady` in the
  Space Center and at `onFlightReady`. `DumpKscState` stays as the hand fallback
  (Ctrl+Shift+T, single run).
- **The turn-back is armed by `PARSEK_SPIKE_S4A_TURNBACK=1`, not by the id
  `S4a-turnback`.** The spike keeps one registry line.
- **The turn-back matches the save folder as well as the title.** A copied folder keeps
  the source game's `Title`, so a title-only check would never fire on
  `SPIKE-REFUSE-1`.
- **New Game was run at the end of session 1** rather than as the second way in, so the
  three saved-game ways run against one career.

## Measurements

### Ways in (from `summary-s4a.md`, "Ways in")

| # | way | KSC `PQSCity.OnSetup` before `onGameStateLoad` | new `Game` current at `onGameSceneLoadRequested` | first event with it (if no) | `persistent.sfs` written before `Game.Start` |
|---|-----|------|------|------|------|
| 1 | resume | OWED | OWED | OWED | OWED |
| 2 | new | OWED | OWED | OWED | OWED |
| 3 | seam `LoadGame` | OWED | OWED | OWED | OWED |
| 4 | quickload | OWED | OWED | OWED | OWED |
| 5 | revert | OWED | OWED | OWED | OWED |
| - | launch (extra) | OWED | OWED | OWED | OWED |

Event order table per way: OWED. Paste the "Event order per way" block of
`summary-s4a.md`, ways 1 to 5.

### KSC state (from "KSC state per dump"; the values of dump d1, then any dump that differs)

| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| `SpaceCenter` lat / lon | OWED | present | OWED |
| `SpaceCenter.AreaRadius` | OWED | present | OWED |
| `PQSCity` KSC lat / lon / alt (fields) | OWED | present | OWED |
| `PQSCity` KSC transform lat / lon / alt | OWED | present | OWED |
| `PQSCity` KSC `repositionRadial` / `repositionRadiusOffset` / `reorientFinalAngle` | OWED | present | OWED |
| Decals within 1 deg of the pad (kind, radius, centre, `absolute`, `absoluteOffset`) | OWED | present | OWED |
| `SpaceCenterCamera2` `pqsName` / `initialPositionTransformName` / `altitudeInitial` | OWED | present | OWED |
| Facilities: names and spawn points | OWED | every entry has lat / lon / alt | OWED |
| Launch sites: names, `requiresPOIVisit`, spawn points | OWED | every entry has lat / lon / alt | OWED |
| Field completeness over all dumps | OWED | PASS | OWED |
| Ways covered | OWED | 5 of 5 | OWED |
| Hooks applied | OWED | `applied=15 missing=0` | OWED |

### Footprints (from "Footprints"; feeds `SiteRow.LaunchAreas`)

| source | site | colliders (aabb) | lat min..max | lon min..max | len x / y / z m | max centre drift deg |
|---|---|---|---|---|---|---|
| OWED | KSC overall | OWED | OWED | OWED | OWED | OWED |
| OWED | LaunchPad | OWED | OWED | OWED | OWED | OWED |
| OWED | Runway | OWED | OWED | OWED | OWED | OWED |
| OWED | each Making History site | OWED | OWED | OWED | OWED | OWED |

### Circle cache (from "Circles"; the P1-03 VERIFY)

| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| Circles in `cachedRuntimeCircles`, other sites allowed | OWED | - | - |
| Circles, other sites not allowed | OWED | - | - |
| Cache holds the KSC `LaunchPad` | OWED | yes or no | OWED |
| Cache holds the KSC `Runway` | OWED | yes or no | OWED |

### Turn-back (from `turnback.txt` and the step 16 note)

| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| `turn-back scene=` line logged | OWED | present | OWED |
| Scene loaded after the turn-back line | OWED | none | OWED |
| Message shown at the main menu (screenshot) | OWED | shown | OWED |
| Second save loads from the same menu | OWED | yes | OWED |
| `persistent.sfs` sha256 before / after | OWED / OWED | equal | OWED |
| If the hashes differ: content equal after re-serialization | OWED | yes (PARTIAL) | OWED |
| `TURN-BACK:` line | OWED | PASS or PARTIAL | OWED |

## What the design must change
OWED. Write one line for each answer that is "no" on any way in:
- KSC `PQSCity.OnSetup` runs before `onGameStateLoad`: S4b and R2-01 must re-run it after
  a move.
- The new `Game` is not current at `onGameSceneLoadRequested`: R1-04 moves the race gate
  to the first event the table names, falling back to `onGameStatePostLoad`.
- The cache holds the KSC pad or runway: R2-05 filters or rebuilds those entries when the
  home moves (OV-52). Otherwise R2-05 needs no filter.
- The turn-back is PARTIAL: R2-02 says "the save's content is untouched" instead of "the
  file is untouched". It is FAIL: R2-02 takes its fallback.

Known before the run (from the decompile, see the plan corrections):
- Plan 3.3 says every way into a saved game calls `Game.Start()`. The quickload upgrade
  pipeline (`QuickSaveLoad`'s `onQuickloadPipelineFinished`) calls
  `FlightDriver.StartAndFocusVessel(Game, int)` without `Game.Start()`. Way 4 shows
  whether that branch runs on a current save.

## Fallback taken
None planned. A missing field is a `hook missing` or `dump section ... failed` WARN line,
copied here as is.

## Raw artifacts
`<umbrella>/logs/spikes/S4a/<YYYY-MM-DD>/`:
- `session1-KSP.log`, `session2-KSP.log`, `session1-seam-responses.txt`.
- `csv/` (`event_order.csv`, `ksc_state.csv`, `footprints.csv`, `footprint_corners.csv`,
  `circles.csv`).
- `summary-s4a.md`, `turnback.txt`.
- `refuse-before.sfs`, `refuse-after.sfs` and their `.sha256` files.
- `turnback-message.png`, `deployed-dll-sha256.txt`.

These are spike saves only; none of the owner's own saves is read. Curated into this
folder: `summary-s4a.md`, `turnback.txt`, `turnback-message.png`.

## Follow-ups
OWED.
