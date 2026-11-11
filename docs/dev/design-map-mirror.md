# The map mirror (`harness/tools/map_mirror.py`)

One self-contained HTML page that draws, to scale and from the numbers, every
Parsek trajectory line, orbit and ghost icon the map view or the tracking station
showed at one instant, beside the screenshot taken at that instant. It exists so
the owner can judge looped missions and supply routes on the map (takeoffs,
transfers, tilted orbits, parking orbits, landings, and the seams between the
pieces) without having to trust an impression of the game: the page shows the
geometry from several angles and prints the measured facts next to it.

The page passes no verdicts. Every number on it is a measurement of what the dump
says was drawn; deciding whether a 3 km seam is a bug is the reader's job.

Sibling of the GUI mirror (`docs/dev/design-gui-mirror.md`): same delivery shape
(a single file, no network, a rail of captures on the left, photographs inlined
under a size budget and the page refused rather than written over it, a
`--stamp` for byte-identical output, an index JSON beside it), much smaller
scope. It borrows `read_png`, `write_png`, `subsample`, `json_for_script` and
`run_sort_key` from `gui_mirror.py` by import.

## 1. Inputs

Per shots directory (`results/<runId>_shots`, where run.py's run id is
`<stamp>_<specId>[_run<N>][_a<N>]`; the spec id is the LANE):

* `<label>.map.json` - a `parsek-map-geometry/1` dump, written by the
  automation-only `DumpMapGeometry label=<label>` seam verb. The input contract
  (every key, its frame and its units) is owned by
  `docs/dev/design-map-geometry-dump.md`; the writer is
  `Source/Parsek/MapGeometryJson.cs`.
* `<label>.png` - the `CaptureScreenshot label=<label>` frame with the SAME
  label, when the lane took one. A capture with no PNG still renders.

Any other file in the directory is ignored. A directory not named like a shots
directory is its own run and lane (handy for a folder of hand-made dumps).

Reading rules:

* A dump whose `schema` is not `parsek-map-geometry/1`, that is not JSON, that
  has no `bodies` or `ghosts`, or that carries a vector which is not three finite
  numbers is UNREADABLE. It is listed in the page header, in the rail (red, with
  the reason) and in the index, with the field path named (for example
  `ghosts[0].lines[2].points[17][1] is 'x', expected a finite number`). Nothing
  is drawn for it.
* Every optional field may be absent (`parent` / `orbit` on the root, `soi`,
  `sampleUT`, `loopCycle`, `icon.pos`, a line's `ut`, `conic`, `reference`,
  `referenceKind`). Missing `camera` or `axes` blocks fall back to a top-down
  camera and +y up / +x right, with a note.
* Recoverable oddities become a NOTE on that capture, shown in amber above the
  drawings: a line or icon relative to a body that is not in `bodies` (not drawn
  or measured), an unknown line `kind` (drawn as a polyline), a `counts` value
  that disagrees with what the file holds, and `counts.truncated = true`.

## 2. Running it

```
python harness/tools/map_mirror.py --shots results/<runId>_shots [--shots ...] \
    --out map-mirror.html [--index map-mirror-index.json] \
    [--budget-mb 16] [--no-photos] [--stamp S] [-v]
```

`--shots` takes a glob (`"results/*_MAP-*_shots"`) and repeats. The page is
written only if it fits `--budget-mb`; otherwise the tool prints `REFUSED`,
writes nothing (not the index either) and exits 2. Photos are fitted first: the
originals are inlined when they fit what the page leaves of the budget, else
every photo is re-encoded down a ladder (colour floor 8 .. 32, subsample 1 .. 6)
until they do, and each re-encoded photo says so under it. `--no-photos` leaves
them out (the capture says the screenshot exists). Without `--stamp` the
generation stamp is the current UTC time; with one, two runs over the same
inputs produce byte-identical files.

Like the GUI mirror, the page claims no path inside a shots directory: it writes
where `--out` says. Do not commit generated pages.

## 3. The page

Left rail: one heading per lane, then the lane's captures in time order (run,
then game time, then frame), each with its label, scene, game time on the stock
Kerbin calendar (6 h days, 426 day years) and ghost count. Up / Down arrows step
through captures; `#cap=<runId>/<label>` deep-links one.

The selected capture shows a fact line (lane, run, scene, game time and raw ut,
frame, warp rate, camera target / pitch / heading / distance, counts), any notes,
a legend whose checkboxes hide a ghost in every view, then the views and the
measured tables.

### Views

Every view is an SVG computed in the browser from the embedded numbers, to
scale, orthographic. Wheel zooms about the pointer, drag pans, double-click (or
`reset`) returns to the opening framing; the edge-on and camera views also
rotate with shift-drag (azimuth, or heading and pitch). Each view prints a scale
bar and `1 px = <distance>` so a sub-pixel feature still reads its true size.

1. **The camera view beside the screenshot.** Looks the way the dump's `camera`
   block says the map camera looked: centred on `camera.target` (a body, or a
   ghost's `vesselName`, at its icon), basis from `pitchDeg` / `headingDeg` with
   KSP's PlanetariumCamera convention (yaw about up, then pitch about the
   pivot's right; pitch 90 is straight down), half-height
   `distance * tan(30 deg)` (an ASSUMED 60 deg vertical field of view; the dump
   carries no FOV). The panel takes the screenshot's aspect ratio and sits next
   to it. The real camera is perspective, so expect the framing to differ; the
   shapes and their relative placement are what to compare. Shift-drag nudges
   the angle if the convention reads wrong; the header shows the live angles.
2. **Close-ups**, one row per body that has a ghost line, icon or route leg
   relative to it, plus the parent of a moon that carries one (the root never
   gets one; it is the whole-system view). Left: straight down; right: edge-on
   (looking horizontally along the top-down view's screen-up, so screen x is the
   same in both and the two line up). The frame body is drawn to scale with its
   sphere-of-influence circle; its moons with their orbit rings, discs and SOI
   circles; and every line, icon and route leg relative to the body or any of
   its moons (moon-relative points converted through the bodies' `pos`). Fits:
   `fit lines` (the default: the lines and icons alone, so a 5 km hop beside a
   600 km planet fills the view and the disc edge shows the ground), `near body`
   (three body radii: ascents, parking orbits, landings), `fit SOI`.
3. **Whole system, top-down.** The root at the origin, every body's orbit ring
   and position, every line and icon. Fits: `fit all` (default), `fit lines`.

Axes: screen x is `axes.right` (made perpendicular to `axes.up`), screen y in the
top-down view is `F = right x up` by the component formula, which is what Unity's
`Vector3.Cross` computes; in Unity's left-handed world that is what a camera
looking down `-up` with `right` to its right sees, so a prograde orbit draws
counter-clockwise, as in game. If the dump turns out to use the other
handedness, every top-down picture is a mirror image; that is a contract
question, see section 6.

### Drawing conventions

* Each ghost has its own colour, the same in every capture of the page (keyed by
  `recordingId`). Route legs share one route colour.
* Stroke by kind: orbit solid, polyline thicker solid, tail dotted, route leg
  dash-dot. An inactive line (`active = false`) is faint. A stretch of any line
  or ring that passes BEHIND a body (in the view's own direction, against every
  body sphere) is fainter still, so the edge-on and camera views read like the
  game, where the planet hides it.
* The optional reference path is dashed in magenta.
* Every line and route leg has a filled dot at its first point and an open dot at
  its last: the seams.
* The icon is a filled diamond; an icon the dump says was NOT drawn this frame
  (`icon.drawn = false`) but whose position is known is a dashed hollow diamond.
* A body smaller than 2.5 px is drawn at 2.5 px with a dashed outline; its hover
  gives the true size.
* Labels that would overlap a label already placed are dropped (bodies of the
  frame first, then its moons, then ghost icons); hover still names everything.
* Hover any line, ring, dot, icon or body for: vessel, line index and kind,
  active or not, body, point count, time range, altitude range, conic summary,
  recording id; an endpoint's hover gives its altitude.

### Measured

One block per ghost and per route, facts only.

* **Icon to the nearest drawn point of its own lines**: the shortest distance
  from `icon.pos` to any segment of any of the ghost's lines (all lines,
  inactive included, converted to the ghost body's frame), and which line.
* Per line: kind, body, active, point count, time range and duration, **lowest**
  and **highest altitude** (vertex distance from the line body's centre minus
  its radius), **conic Pe / Ap altitude** when a conic is given
  (`sma (1 - e) - R` / `sma (1 + e) - R`, `open` when e >= 1), **gap to next
  line**, and the reference column.
* **Gap to next line**: the distance from this line's last point to the next
  line's first point. "Next" is time order when every line of the ghost has a
  `ut`, else dump order (the block says which). Lines relative to different
  bodies are converted to a common frame through the body `pos` values and the
  cell says `across bodies`.
* **Reference: max distance**: the largest distance from any drawn vertex to the
  reference polyline (one way: drawn to reference), with the reference kind.
* Per route leg: body, recording, points, lowest / highest altitude, gap to the
  next leg.

Hover a number for its exact value in metres. Distances print with three
significant figures in m / km / Mm / Gm, the unit chosen on the rounded value.

## 4. The index

`map-mirror-index.json` (`schema = "parsek-map-mirror-index/1"`): generation
stamp, capture and unreadable counts, then per lane its captures with id, run,
scene, ut, ghost / line / point counts, route line and leg counts, `truncated`,
whether a screenshot exists and whether it was inlined, the close-up bodies and
the notes; unreadable captures carry their reason, and the top-level
`unreadable` list repeats them with the file name.

## 5. Tests

`harness/lib/test_map_mirror.py` (under `lib/` because CI discovers `lib/`):
the projection and frame math, distance and altitude helpers and unit
formatting; reading both sample dumps (`harness/lib/testdata/map_mirror_sample.map.json`,
a Kerbin to Mun loop with hand-checkable features listed in the test module's
docstring, and `map_mirror_minimal.map.json`, required fields only); a wrong
schema and malformed values named by path; the measured values on the sample
against hand calculations; and the page: self-contained, deterministic with a
stamp, unreadable capture listed, no-PNG capture rendered, PNG inlined or
re-encoded to fit, budget refusal writing nothing, dump text unable to break out
of the page, and the page script parsing under `node --check` when node is on
PATH.

`CSharpWriterSourceSyncTests` checks, WHEN `Source/Parsek/MapGeometryJson.cs`
exists, that it declares `SchemaId = "parsek-map-geometry/1"` and still writes
every key the tool reads; a third cell checks that
`hlib.ARTIFACT_SHOTS_SUFFIXES` lists `.map.json` so the harvest copies dumps
into the shots directory. Until those land the cells SKIP with a `PENDING`
reason; they become strict once both halves are merged.

## 6. Limits and open questions

* The camera view is orthographic with an assumed 60 deg field of view, and its
  basis is derived from pitch and heading with a convention that has not yet
  been checked against a real capture. If it reads wrong, the cheap contract fix
  is for the dump to carry the camera's own forward and up vectors.
* Handedness: the top-down orientation assumes the dump writes Unity world axes
  (see section 3). Compare the first real capture's whole-system view with the
  tracking station: planets should run counter-clockwise.
* An orbit line is drawn exactly as its points are given; the page does not
  close a ring the dump left open. Whether an orbit line's points repeat the
  first point at the end decides whether its own first and last dots coincide,
  and its gap to the next line is measured from the LAST point.
* Body positions are the dump's single instant. A line relative to a moon is
  placed at the moon's position NOW, which is what the map draws, so a seam
  measured across bodies is the seam on screen at that instant.
* No pass / fail and no tolerance: the page measures, the reader decides.
