# Flight-scene ghost census (plan, not built)

Status: PLAN ONLY, written 2026-11-27 with todo MISSION-LOOP-DEBRIS-DORMANT-AS-HISTORICAL.
Build it later, on the owner's go. Nothing here ships with the looped-debris fix.

## 1. Why

The 1.0.3 regressions the owner found after release (looped booster debris missing,
parts missing on ghosts) both flew green through every lane that watched the same
flight. The reason is the shape of what the harness can read:

- `GhostRenderTrace` (`ghostRenderTracing`) writes a line when a ghost mesh is SPAWNED
  or DESTROYED, and `harness/lib/ghostlife.py` counts those lines. A ghost that is
  never spawned writes nothing, so the tracer cannot say a ghost is MISSING; a lane
  only catches an absence if its author pinned a per-vessel floor for that exact
  population and that exact path (GS-8 / GS-9 pin the debris only for the
  Rewind-to-Launch first run).
- The M-A7 render-composition manifest (`docs/dev/design-autotest-render-composition.md`)
  describes the map, Tracking Station and KSC pipelines, never the flight-scene mesh.
- No line states how many of a ghost's parts are visible, so a ghost built with half
  its parts reads exactly like a whole one.

The census closes both gaps with one periodic line that states, for the flight scene,
what SHOULD be drawn next to what IS drawn, with the reason for every difference.

## 2. The producer

One gated line from the flight playback host (`ParsekFlightPlaybackHost`), behind the
existing `ghostRenderTracing` setting, never on in a player build by default:

    [Parsek][INFO][GhostCensus] ut=<F1> expected=<n> drawn=<n> skipped=<n> missing=<n>
        rows=<idx>:<recId8>:<state>:<parts>/<partsTotal>[:<reason>];...

- Cadence: on CHANGE of the row set (a ghost appears, leaves, changes state or part
  count), plus a heartbeat every 10 s of real time, so a long steady coast costs one
  line per 10 s. Rate keys follow the batch-counting convention (one summary line,
  never one per ghost per frame).
- `expected`: every committed recording whose playback window (its own, or its loop
  clock, including loop-synced children through `GhostPlaybackLogic.ResolveLoopingLike`)
  contains the current UT and that is not designed-hidden.
- `state` per row: `drawn` (a mesh exists and is active), `hidden` (a mesh exists but is
  hidden by warp, distance or zone, with the engine's reason), `skipped` (the flight
  host's own `GhostPlaybackSkipReason` token, the same one the skip-state line prints),
  or `missing` (expected, not skipped, and no mesh). `missing` is the census' reason to
  exist: it is the one state no line prints today.
- `parts` / `partsTotal`: the visible part GameObjects of the ghost against the part
  count of the snapshot it was built from (`GhostVisualBuilder`), so a partial build
  reads `14/32` instead of looking whole. Counted at spawn and on a part-event apply,
  never per frame (the visual design principle: threshold crossings, not sampling).

The computation reuses what the flight host already derives in `ComputePlaybackFlags`
(the skip reason, the loop term) and the engine's `ghostStates`; it adds no new
per-frame pass over the committed list when tracing is off.

## 3. The verifier row

A new pure module `harness/lib/flightcensus.py`, shaped like `ghostlife.py`: a tolerant
parser over the collected KSP.log, measured facets recorded unconditionally, a spec
block `[expectations.flightCensus]` validated from `hlib.validate_spec`, REPORT-ONLY
unless the block declares `gating = true`, with the armed set pinned in `test_hlib.py`
the same way `GHOSTLIFE_ARMED_SPECS` is.

Keys (first cut):

- `missing = { max = 0 }`: the default and the point; any `missing` row reds a gated lane.
- `vessels = { "<name>" = { drawn = { min = N } } }`: a floor per population, read at
  the peak of each heartbeat window.
- `partsWhole = true`: every `drawn` row of the named vessels must read `parts ==
  partsTotal` once spawned (part events that hide parts by design are applied first,
  so a decoupled part leaves the total too).
- `allowedSkipReasons = [...]`: a skip reason outside the list reds, so a new dormancy
  path cannot hide a population silently.

## 4. Where it runs first

Turn the setting on (most already have `ghostRenderTracing=true`) and declare the block
report-only on: GS-8, GS-9 and GS-12 (the staged Kerbal X under watch, first run and
loop copies), WL-1 and LDL-1 (the loaded-save loop), and the V*M player-loop lanes that
already loop a loaded fixture (V12, V22M, V23M, GS-13). Arm each after a reading run
whose facets match, per the standard discipline.

## 5. Cost and risk

- Off by default; when on, one line per change plus one per 10 s.
- `missing` depends on `expected` being right. A wrong `expected` (a designed-hidden
  ghost the census does not know about) reads as a false red, so the first cut lists
  every designed-hidden reason it honours in the module docstring, and each lane stays
  report-only until a reading run shows `missing=0` on a correct flight.
- The part count must come from the built ghost, never from the recording, or it would
  agree with itself by construction.

## 6. Not in scope

The map and Tracking Station census (M-A7 already covers those pipelines), KSC ghosts
(no loop-synced debris path there today; see LOOP-FALL-KSC-TS-SINGLE-COPY), and any
player-facing surface.
