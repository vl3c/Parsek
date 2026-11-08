<!-- Drafts are stored as *.toml.draft so no suite parses them; rename to .toml when moving into harness/scenarios. -->
# Moon flight-test drafts (Group A) - order of work

Plan: `../moon-flight-test-matrix-2026-11-07.md`, Group A. Branch `claude/project-thread-ni23y9`
(PR #2468, the lone-moon arrival hold).

What is where:

- **Committed on the branch (fly tonight):** the three forges
  `harness/scenarios/B36-duna-ike-continuation.toml`, `B37-eve-gilly-continuation.toml`,
  `B38-jool-laythe-continuation.toml`. Each flies B23 / B24 / B25's machine unchanged
  from the UNSTRIPPED interplanetary harvest (`duna-direct-recorded`, `eve-orbit-recorded`,
  `jool-orbit-recorded`) with NO StopRecording / DiscardTree, so the committed-spawned
  restore resumes the Kerbin -> planet tree and the moon hop is recorded into it.
- **Drafts in this folder (NOT committed):** `V31M`, `V31T` (Ike), `V32M`, `V32T` (Gilly),
  `V33M` (Laythe). They name fixtures that do not exist yet; committing them early reds
  the suites.

Every lane here is NEVER FLOWN.

## 1. Build and provision (on the Windows PC, from the branch worktree)

```
cd Parsek-<branch>/Source/Parsek && dotnet build
cd ../../harness && python provision/provision.py --profile stock-minimal
```

Verify the AUTOMATION DLL carries the PR's literal (UTF-16, a string literal):
`MOON HOLD keeps the destination loiter whole` (see `.claude/CLAUDE.md`, "Verify the
deployed DLL"). The forges themselves need no PR code; the loop lanes do.

## 2. Fly the forges, one at a time, B36 first

```
mkdir -p ../../logs/console/$(date +%F)
python run.py --id B36-duna-ike-continuation > ../../logs/console/$(date +%F)/B36-run1.log 2>&1
```

Then B37, then B38 (B38 is the longest: 5,400 s mission budget).

Before harvesting, read the forge's collected `KSP.log`:

- `startrecording recordingId=... already=true` (required; `already=false` is forbidden).
- `SOI change boundary suppressed in tree mode: <Planet> to <Moon> (tree=<source tree id>, activeRec=<id>)`.
  If `activeRec=` is the tree's ROOT recording id (`311d98e3...` / `75a6ab25...` /
  `ddcc6d00...`) AND the same load logged `Split recording '<name>' at section N (tree=<id>)`,
  the hop went onto the Kerbin ascent HEAD segment (B23 flight 1's shape, a recording with a
  multi-million-second gap): the restore prefers the tree's `activeRecordingId`, and an
  optimizer split does not move it. The forge still reads PASS. Harvest anyway, but expect
  the loop lane's reading to tell whether the loop builder copes; file a todo if not.
- The produced save should hold ONE committed tree (the report-only
  `recordings.structure committedTrees` facet).

## 3. Snapshot + harvest FIRST (before any other run on that template)

`run.py` already copies the produced save to `harness/results/<runId>_save/`. Harvest from
there, `--keep-parsek`, never `--force` (the situation gate is the only guard against a
clobbered save):

```
python tools/harvest_bdock_station.py --save-dir results/<B36 runId>_save --target-name duna-ike-recorded   --expect-situation ORBITING --keep-parsek
python tools/harvest_bdock_station.py --save-dir results/<B37 runId>_save --target-name eve-gilly-recorded  --expect-situation ORBITING --keep-parsek
python tools/harvest_bdock_station.py --save-dir results/<B38 runId>_save --target-name jool-laythe-recorded --expect-situation ORBITING --keep-parsek
```

(B23's own fixture `ike-orbit-recorded` was harvested the same way: `--keep-parsek` from
flight 2's produced save.)

## 4. Commit each fixture immediately

Commit `harness/fixtures/saves/<name>/` on the branch, with, in the same commit:

- its row in `harness/fixtures/saves/README.md` (provenance: forge id, run id, DLL);
- its payload pin in `harness/lib/test_saveparse.py::CommittedFixtureSweepTests.RECORDED_FIXTURES`
  (copy the `ike-orbit-recorded` / `laythe-orbit-recorded` entries' shape: PROVENANCE comment,
  tree / recording counts, the generation);
- the forge's status row in `docs/dev/autotest-status.md` moved from "Committed, not yet
  green" to "Live-proven" with the run id (and the section counts re-derived).

Run `cd harness && python -m unittest discover -s lib -q` before pushing.

## 5. Commit the loop drafts (V31M, V32M, V33M first)

Copy the drafts into `harness/scenarios/`, then, before committing each:

1. `[expectations.recordings] count`: set to `{N, N + 1}` where N is the harvested
   fixture's `.prec` count (`ls harness/fixtures/saves/<name>/Parsek/Recordings/*.prec | wc -l`).
2. Check the tree id in `MissionConfig` / `StartLoopPlayback` / the forbidden amber against
   the harvested save (`RECORDING_TREE id =` in `persistent.sfs`). It should be the
   source tree's id (ccb5e4af... / 16af6b5e... / 6ec68e87...); if the forge minted a new
   tree instead, the forge failed its own contract and nothing here should be committed.
3. Wire them (same commit): a `REVIEWED_UNTAGGED` entry each in `harness/lib/test_hlib.py`
   (`PendingOperatorTagHonestyTests`, next to `V28M` / `V29M` / `V30M`); a row each in
   `docs/dev/autotest-status.md` "Committed, not yet green" plus the total; the
   `MoonArrivalHold` row of `docs/dev/autotest-ingame-category-inventory.md` (lane names,
   bucket C -> A once green, the triage totals); each lane's name in
   `RENDERCOMPOSE_DECLARER_SPECS` in `harness/lib/test_hlib.py` (the drafts declare a bare
   `[expectations.renderComposition]`, which boots KSP with `PARSEK_RENDER_MANIFEST=1` for
   their `ExportRenderManifest` step). A dry run of the lib suite with the drafts and
   stand-in fixtures in place (2026-11-07) red exactly those four cells: the status total,
   `REVIEWED_UNTAGGED`, the declarer roster and the fixture-set sweep. Then run the harness suites and
   `KSPDIR=... bash scripts/cloud-test.sh -class Parsek.Tests.CommittedBatchTallySourceSyncTests`
   (or `dotnet test --filter CommittedBatchTallySourceSyncTests` on Windows), which checks
   every spec's `BATCH_COMPLETE ... total=1` against the `[InGameTest]` attributes.
4. GILLY (V32M / V32T): the drafts require the engage. If the reading declines, swap to
   the amber pin described in the draft header; never pin a guess.

## 6. Fly the loop lanes, then fill the TS jump

Fly `V31M`, `V32M`, `V33M` (each a reading run: build lines + the Space Center batch).
From each reading, fill the TS lane's `@ENCOUNTER_UT@` with a UT just past the replayed
moon SOI entry on the first loop cycle:

```
encounterUT = phaseAnchor + (recordedMoonEntryUT - spanStartUT - cutsBeforeIt) + hold + ~140 s
```

- `phaseAnchor=` and `cadence=` from the `ENGAGED re-aim` line;
- `hold=` and `recordedArrivalUT=` (the PLANET SOI entry) from the `ARRIVAL HOLD` line;
- `recordedMoonEntryUT` from the harvested fixture: in the moon-hop recording's
  `Parsek/Recordings/<id>.prec.txt`, the `startUT` of the first `TRACK_SECTION` whose
  points read `body = <Moon>`;
- `cutsBeforeIt` = the `cutSeconds=` on the ENGAGED line when every cut sits before the
  moon entry (the moon hold keeps the destination loiter whole, so post-entry cuts are
  dropped exactly when the `MOON HOLD keeps` line prints).

V14T / V16T derived their single jump the same way (their headers carry the worked
arithmetic). Then add the -180 / -60 / +140 bracket jumps to the M lanes in a second
revision and pin V17's lens there:
`shadow .*coverage=InSegment segIdx=\d+ treatment=TracedPath visible=True .*body=<Moon>`.
Commit V31T / V32T with the filled UT, then fly them.

`MOON HOLD keeps the destination loiter whole` stays a commented candidate in every draft:
require it only on a lane whose reading prints it (the forge's planet park must have run
two or more whole orbits before the hop, so that the loiter compressor cut time after the
planet SOI entry).

## Pins the drafts carry, and where each comes from

| Pin | Source |
|---|---|
| `ENGAGED re-aim Kerbin-><Planet> via Sun` | `MissionLoopUnitBuilder.cs`, the ENGAGED line (V3R / V8 / V13 pin the same) |
| `ARRIVAL HOLD dest=<Planet> kind=config Talign=<P_moon>s moons=1 ` | `MissionLoopUnitBuilder.cs`, the non-trim arrival-hold line; `Talign` = T_config = the lone moon's period (Ike 65,517.86 s, Gilly 388,587.38 s, Laythe 52,980.88 s) |
| `(aligns the moon orbital phases, mode=` | same line, the `ConfigMoonCount == 1` wording this PR added |
| `BATCH_COMPLETE v1 total=1 passed=1 failed=0 skipped=0 category=MoonArrivalHold scene=SPACECENTER` | `InGameTests/LoneMoonHoldInGameTest.cs`, one SPACECENTER cell |
| forbidden `MOON HOLD declined` / `Arrival amber SET: tree=<id>` | `ComputeArrivalHoldKeepingMoonLoiterWhole` / `LogArrivalAmberTransition` |
