# Spike S10a: relative stretches and their body-fixed copies (2026-11-16)

Verdict: PARTIAL (draft). Across the 74 committed fixture saves, every Relative section has a body-fixed copy with one sample per frame. Only 83.2 percent of relative seconds pass the strict "covers the section's UT range" test. The owner-saves run is still owed.

## Question
In real saves, how many `Relative` track sections have a complete body-fixed copy (at least two samples covering the section), a partial one, or none, by recorder source (active, background, parent-anchored debris, docking, EVA). Settles whether 15.5's "fewer than two samples: not drawn" rule is rare; waits: R3W-04.

## Decides
Design 15.5 ("No relative sections": the rival loader keeps only the body-fixed copy; a stretch with fewer than two stored positions is not drawn). Waiting task: R3W-04.

## Build
`spike/mp-r0-s10a` at d41d05270, on `spike/mp-r0` 8eca63455. No game, no DLL deploy: a manual xUnit test under mono (`scripts/cloud-test.sh`, refs `/workspace/ksp-refs`). Saves are read with the production `RecordingTree.Load` and `RecordingSidecarStore.LoadTrajectorySidecarForTesting`.

## Procedure as run
1. `Source/Parsek.Tests/Spikes/RelativeCopyCensus.cs` + `RelativeCopyCensusTests.cs` (14 unit cells, `Manual_RelativeCopyCensus_WritesCsv`, `Manual_DumpRecordingSections`).
2. Every save under `harness/fixtures/saves/` and `Source/Parsek.Tests/Fixtures/` that has a `persistent.sfs` and `Parsek/Recordings/*.prec`: 74 saves (list below), one run: `PARSEK_SPIKE_S10_SAVE="<74 paths, ';'>" PARSEK_SPIKE_OUT=/home/user/logs/spikes/S10/2026-11-16 KSPDIR=/workspace/ksp-refs scripts/cloud-test.sh -class Parsek.Tests.Spikes.RelativeCopyCensusTests` -> Total 21, Failed 0.
3. Loaded: 122 trees, 618 recordings, 617 sidecars, 1 missing (`recording-quarantine-ksc`, by design), 0 failed; 5789 sections, 351 Relative.
4. `python3 spikes/r0/S10/summarize_census.py /home/user/logs/spikes/S10/2026-11-16`.
5. Hand check (below). The owner-saves run (`PC-RUN.md`) is owed.

Change from the plan: `eva2-lko-crewed` and `eva-construct-pad` (and `eva3-pad-3crew`) are start states with no recordings. The only EVA relative sections are 2 in `cs-career-mun4`.

Definitions:
- complete: >= 2 samples, the first at or before `startUT` and the last at or after `endUT`, within 1 ms. Playback itself tests exactly (`BodyFixedPrimaryCoversPlaybackUT`).
- partial: >= 2 samples that leave part of the range uncovered.
- single: 1 sample. none: 0 samples.
- Source class, first match wins:
  1. eva: `EvaCrewName` is set.
  2. parent-anchored: `ParentAnchorRecordingId` is set (debris or a controlled-decoupled child).
  3. docking: the recording is a parent or child of a Dock or Undock branch point.
  4. Otherwise the section's own producer: active, background or checkpoint.
- copy = frames: the same sample count and the same first and last UT as the relative frames.

## Measurements
Fixture results by recorder source (74 saves; 45 have relative sections):

| source | sections | relative s | complete (n / s) | partial (n / s) | partial, copy = frames (n) | single | none | covered s | complete % of s | covered % of s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| docking | 32 | 449.80 | 7 / 59.08 | 25 / 390.72 | 25 | 0 | 0 | 430.80 | 13.1% | 95.8% |
| eva | 2 | 0.08 | 0 / 0.00 | 2 / 0.08 | 2 | 0 | 0 | 0.00 | 0.0% | 0.0% |
| parent-anchored | 317 | 1883.05 | 317 / 1883.05 | 0 / 0.00 | 0 | 0 | 0 | 1883.05 | 100.0% | 100.0% |
| **total** | 351 | 2332.93 | 324 / 1942.13 | 27 / 390.80 | 27 | 0 | 0 | 2313.85 | 83.2% | 99.2% |

By producer:
- Active: 16 sections, 124.96 s (8 docking, 8 controlled children).
- Background: 335 sections, 2207.97 s (309 debris, 24 docking, 2 EVA).
- All 351 sections have copy = frames. None is classed plain active or background.

Saves below 100% complete (the other 33 saves with relative sections are at 100%):

| save | sections | relative s | complete | partial | complete % | covered % |
|---|---:|---:|---:|---:|---:|---:|
| bdock-recorded | 13 | 67.70 | 12 | 1 | 98.6% | 99.9% |
| bdock-recorded-career | 13 | 67.70 | 12 | 1 | 98.6% | 99.9% |
| bdock-second-dock-recorded | 19 | 101.18 | 18 | 1 | 99.1% | 99.9% |
| cs-career-mun4 | 17 | 63.14 | 15 | 2 | 99.9% | 99.9% |
| depot-route-recorded | 22 | 251.33 | 17 | 5 | 40.1% | 97.7% |
| depot-route-recorded-career | 22 | 251.33 | 17 | 5 | 40.1% | 97.7% |
| interbody-route-recorded | 33 | 258.55 | 29 | 4 | 69.6% | 97.6% |
| rover-relay-c-recorded | 2 | 2.02 | 0 | 2 | 0.0% | 87.1% |
| rover-relay-c-recorded-career | 2 | 2.02 | 0 | 2 | 0.0% | 87.1% |
| rover-relay-recorded | 2 | 2.16 | 0 | 2 | 0.0% | 91.7% |
| rover-route-recorded | 1 | 1.02 | 0 | 1 | 0.0% | 86.3% |
| rover-route-recorded-career | 1 | 1.02 | 0 | 1 | 0.0% | 86.3% |

| Measure | Value | Pass number | Result |
|---|---|---|---|
| Fixtures: relative seconds with a complete copy | 83.2% (1942.13 / 2332.93 s) | >= 95% | below (see below) |
| Fixtures: relative seconds the copy covers | 99.2% | - | - |
| Fixtures: sections whose copy shadows every frame | 351 / 351 | - | - |
| Fixtures: sections with < 2 samples | 0 / 351 | - | - |
| Owner saves: complete % of relative s | OWED | >= 95% | OWED |

Why the fixtures miss 95%:
- All 27 partial sections have copy = frames.
- The 19.08 uncovered seconds are exactly the time between each section's last frame and its `endUT`, which is the next section's start. The frames' own end gaps also sum to 19.08 s, so neither surface has a sample there.
- Most such gaps are 0.02 to 0.16 s. Three are longer, all on undock sections: depot 5.62 s, interbody 3.82 s and 2.22 s.

Hand check, `bdock-recorded`, through the codec:
- `0821dac8...` section 0: Relative, 55.54..60.90, 24 frames and 24 body-fixed samples, the copy equal to the frames. The CSV row reads parent-anchored, complete; the `.prec.txt` holds 24 `BODY_FIXED_POINT` nodes. Match.
- `4af6cfd7...` section 1: 6 and 6 over 8950.608..8951.488, `endUT` 8951.548. The CSV row reads docking (Undock), partial, 0.06 s gap at the end, copy = frames. Match.

## What the design must change
Pending owner numbers. Fixtures alone:
- 15.5's "fewer than two positions: not drawn" is rare: 0 of 351 sections.
- The decision rule should count a section complete when its copy shadows every frame, and report the end-of-section gap separately, since the anchor path has no sample there either.

## Fallback taken
None.

## Raw artifacts
`/home/user/logs/spikes/S10/2026-11-16/` (cloud):
- `<save>/relative_census.csv` and `load_summary.csv` for each save.
- `summary-fixtures.md`.
- `handcheck/dump_*.txt`.
- Console: `/home/user/logs/console/2026-11-16/s10a-census-run2.log`.

Saves run: `Source/Parsek.Tests/Fixtures/` C2CareerPostFix, FormatGolden/v1, FormatGolden/v1/career, plus every `harness/fixtures/saves/` folder holding `.prec` files (71). 29 of the 74 hold no relative section.

### Owner saves (EMPTY: paste `summary-owner.md`)
`<< labels, counts and seconds only >>`

## Decision rule (from the plan)
If at least 95 percent of relative seconds in the owner's saves have a complete copy, R3W-04 keeps "not drawn" for the rest. If fewer, the report names the producers that skip the copy (the seam points of `FlightRecorder.PointCommit.cs` pass none, 3.2) and R3W-04 needs either a recorder change on `multiplayer` first (a new task for the owner to place; a defect single-player players need goes to main by rule 10) or the small anchor resolver.

## What the fixtures suggest / cannot show
Suggest:
- Both recorders write the copy on every relative stretch here: debris, controlled children, dock and undock stretches, and EVA boarding.
- The "seam points pass none" note did not show: the Active sections have exactly one shadow per frame.

Cannot show:
- Long hand-flown approaches or rendezvous that end without a dock: the longest stretch here is 85.5 s, and no stretch is classed plain active or background.
- A kerbal walking beside a lander: there is no such fixture.
- Older-build recordings, or quickload trims, optimizer splits and Re-Fly merges over a long career.
- Whether the end-of-section gaps are long in real play.

## Follow-ups
- S13: adopt the copy = frames rule, with the end-of-section gap reported apart.
- Run the owner saves per `PC-RUN.md`.
