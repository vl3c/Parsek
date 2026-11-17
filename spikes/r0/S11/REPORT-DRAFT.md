# Spike S11: ghost cost per frame at race scale (<YYYY-MM-DD>)

Verdict: MEASURING SPIKE (no pass number of its own; R3W-14 sets the cap from these numbers).
<one sentence after the run: ms per ghost in flight and map view, and the first-open time>

DRAFT written before the PC run (2026-11-17). Every `<...>` is filled from the run folder
`<umbrella>/logs/spikes/S11/<date>/`; each Measurements row names the log line or CSV column
it is read from.

## Question

What N ghosts cost per frame in flight and map view, so R3W-14 (A5) can set the cap of its
ghost budget for "up to three more players' worth of ghosts" (15.13 per-frame row). Rivals
are seen on the map, in the Tracking Station and as ghosts nearby; watching a rival flight
is deferred with P3b, so no step here uses the Watch camera.

## Decides

- Design 15.13 ("Cost: what the feature may spend"), the per-frame row, and the cap of the
  rival ghost budget (R3W-14, A5).
- Design 7.6 "Flights load lazily": whether opening a rival flight's trajectory file on the
  main thread when its ghost is first needed fits a frame, or needs spreading over frames.

## Build

spike/mp-r0 at <sha> (`spike-commit.txt`); deployed DLL sha256 <hash>
(`deployed-dll-sha256.txt`); instance `spike-r0` (re-provisioned, profile `spike-r0`: a copy
of stock-minimal with the owner's own graphics settings); KSP 1.12.5, DLCs <yes/no>, mods
from the instance manifest <list>. Screen, quality, vsync, frame cap, GPU, CPU, RAM: the
`[Spike.S11] settings run=...` line and `settings.csv` <copy the values>.

Code: `Source/Parsek/InGameTests/Spikes/S11/` (`SpikeS11Cost.cs` the sweep, pure
`SpikeS11Plan.cs` and `SpikeS11FirstOpen.cs`), injection preset `spike-s11-ghosts`
(`Source/Parsek.Tests/Spikes/SpikeS11Fixture.cs`), lane
`harness/scenarios/SPIKE-S11-ghost-cost.toml`, summary `spikes/r0/S11/summarize_cost.py`.

## Procedure as run

1. Build the spike DLL, re-provision `spike-r0` with it, dry-run the lane
   (`spikes/r0/S11/PC-RUN.md` steps 1-3).
2. Lane run `asis` (`PARSEK_SPIKE=S11`): the harness stages `pad-runway-pair` with 64
   ghost-only recordings (lit three-part rockets circling the pad 40 to 110 m out, 30 to
   100 m above it) and a copy of the largest committed trajectory sidecar pair (a Jool tour
   flight, 250106 + 23151 bytes) in `<save>/SpikeS11/`, loads it in flight, and runs
   `SpikeS11.GhostCostSweep` isolated. The test times the first open of that sidecar
   (attempt 1) and five warm opens, places the flight camera 300 m from the pad pitched
   down 0.35 rad, then for N = 0, 8, 16, 32, 64 enables the first N recordings with the
   player's playback toggle, waits until the engine has built exactly N ghosts, settles 120
   frames, measures 600 frames in flight view, screenshots, enters map view, settles 120
   frames, measures 600 frames, screenshots, exits.
3. Lane run `uncapped` (`PARSEK_SPIKE_S11_PASS=uncapped`): the same with vsync off and no
   frame cap during the sweep (both restored after).
4. Summary over both runs; the owner checked each flight screenshot for the pad and N
   rockets (`shots-check.txt`).

Changes from the plan: <none, or e.g. PARSEK_SPIKE_S11_FRAMES=300 because ...>. Two passes
instead of one: the plan's "owner's normal graphics" is the `asis` pass; the `uncapped` pass
is added because a vsync / frame cap hides the cost below the cap.

## Measurements

Frame time is `Time.unscaledDeltaTime` (the whole frame: game, render and any vsync wait).
Engine time is Parsek's own per-frame playback stopwatch
(`DiagnosticsState.playbackBudget.totalMicroseconds`). Slopes are ordinary least squares over
the five windows of one view, x = ghosts the engine had built.

| Measure | Value | Pass number | Result |
|---------|-------|-------------|--------|
| Frame mean, flight, N = 0 / 8 / 16 / 32 / 64, asis | <ms> | none (measuring) | log `step ... view=flight n=<N> ... meanMs=`; `steps.csv` `meanMs` |
| Frame p99, flight, N = 0 / 8 / 16 / 32 / 64, asis | <ms> | none | log `p99Ms=`; `steps.csv` `p99Ms` |
| Frame mean, map, N = 0 / 8 / 16 / 32 / 64, asis | <ms> | none | log `step ... view=map n=<N> ... meanMs=`; `steps.csv` `meanMs` |
| Frame p99, map, N = 0 / 8 / 16 / 32 / 64, asis | <ms> | none | log `p99Ms=`; `steps.csv` `p99Ms` |
| Same four rows, uncapped | <ms> | none | the `uncapped/` run, same columns |
| ms per ghost, flight, frame mean (asis / uncapped) | <ms> | none | log `fit ... view=flight metric=meanMs ... slopePerGhost=`; `fit.csv` `slopePerGhost` |
| ms per ghost, flight, frame p99 (asis / uncapped) | <ms> | none | log `fit ... view=flight metric=p99Ms`; `fit.csv` |
| ms per ghost, map, frame mean (asis / uncapped) | <ms> | none | log `fit ... view=map metric=meanMs`; `fit.csv` |
| ms per ghost, map, frame p99 (asis / uncapped) | <ms> | none | log `fit ... view=map metric=p99Ms`; `fit.csv` |
| Fit quality (r2) of each slope | <r2> | none | log `fit ... r2=`; `fit.csv` `r2` |
| Parsek engine time per frame, N = 0 / 64, flight and map | <us> | none | log `engineMeanUs=` / `engineP99Us=`; `steps.csv` `engineMeanUs`, `engineP99Us` |
| Parsek engine us per ghost, flight and map | <us> | none | log `fit ... metric=engineMeanUs ... slopePerGhost=`; `fit.csv` |
| Frames per second at N = 64, flight, asis | <fps> | none | log `fps=`; `steps.csv` `meanFps` |
| Managed GC: gen0 collections and managed delta over a window, N = 64 | <n> / <KB> | none | log `gen0=` / `managedDeltaKb=`; `steps.csv` `gen0`, `managedDeltaKb` |
| Engine FX at N = 64 (ghosts with engine FX, particle systems) | <n> / <n> | none | log `engineFx=`; `steps.csv` `engineFxGhosts`, `engineParticleSystems` |
| Spawn cost to build 8 / 16 / 32 more ghosts (sum, worst frame) | <us> | none | log `spawnUs=` / `spawnMaxFrameUs=`; `steps.csv` `spawnUs`, `spawnMaxFrameUs`, `spawnWaitFrames` |
| Sidecar first open, trajectory (probe + decode), attempt 1 | <ms> | one frame (16.7 ms at 60 fps) is the reading R3W-14 needs | log `firstopen attempt=1 kind=trajectory ... totalMs=`; `firstopen.csv` `totalMs` |
| Sidecar first open, ghost snapshot (`_ghost.craft`), attempt 1 | <ms> | as above | log `firstopen attempt=1 kind=snapshot ... totalMs=`; `firstopen.csv` |
| Sidecar warm open, attempts 2-6 (mean / max) | <ms> | none | log `firstopen attempt=2..6`; `firstopen.csv` |
| Bare file read of the same .prec (I/O alone) | <ms> | none | log `firstopen ... kind=readbytes ... totalMs=`; `firstopen.csv` |
| Every window counted (built = N, none left over, 600 frames, all N in view) | <20/20> | 20 / 20 | log `step ... valid=true`; `steps.csv` `valid`; `summary-both.md` |
| Screenshots show the pad and N rockets (flight) | <10/10> | 10 / 10 | `shots-check.txt` (owner) |

Derived for R3W-14 (filled after the run): three more players' worth of ghosts at <G> ghosts
each costs about <3 x G x slope> ms per frame in flight and <...> in map view at the p99.

## What the design must change

<15.13 per-frame row: old wording, new wording with the measured ms per ghost; 7.6 lazy open:
"fits one frame" or "spread over frames", with the measured first-open time; or "nothing">

## Fallback taken

None (the plan names none for S11).

## Raw artifacts

`<umbrella>/logs/spikes/S11/<YYYY-MM-DD>/`: `spike-commit.txt`, `build-dll.txt`,
`deployed-dll-sha256.txt`, `dev-dll-before.txt`, `dev-dll-after.txt`, `asis/` and
`uncapped/` (each: `KSP.log`, `parsek-test-results.txt`, `s11-lines.txt`, `summary.md`,
`parsek-spike-S11/{steps,frames,fit,firstopen,settings}.csv` and `shots/*.png`),
`summary-both.md`, `shots-check.txt`; console captures `<umbrella>/logs/console/<date>/spike-S11-*.log`.
Curated into this folder: `summary-both.md` and two screenshots (flight n=8, flight n=64).

## What cannot be known without the game

Everything in the Measurements table. The cloud session proved only the pure parts: the
statistics and fit, the layout (every ghost 40 to 110 m from the pad and at least 10 m from
every other ghost), the formatting under de-DE, the injection into a copy of the host save
(64 trees, 64 trajectory sidecars, 64 ghost snapshots, no vessel snapshot), and that the
first-open helper decodes the committed Jool sidecar pair with Parsek's own readers under
mono. The mono timing of that decode is NOT the KSP number.

Limits of the reading, to state in the verdict:
- The sidecar first open is the first open of that FILE in the KSP process, after the save
  load already ran the same readers on the 64 injected sidecars, so the reader code is
  JIT-warm; the file was written by the injector minutes before, so it is most likely in the
  OS file cache. It measures decode cost on the main thread, not a cold disk read.
- Ghost audio is silent on the instance (`MASTER_VOLUME = 0`) but the sources still run.
- `onScreen` is a viewport check of each ghost's root position, not occlusion; the
  screenshots are the visual check.
- `verboseLogging` is on (the player default), so the cost includes Parsek's own
  rate-limited log lines.

## Follow-ups

<new todo or owner question, if any>
