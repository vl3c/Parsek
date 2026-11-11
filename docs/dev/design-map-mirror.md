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
  `sampleUT`, `loopCycle`, a ghost's `vesselName` / `recordingId` / `body`,
  `icon.pos`, `icon.source`, `markerDecision`, a line's `ut`, `conic`,
  `reference`, `referenceKind`, the camera's `forward` / `up` / `fovDeg`).
  Missing `camera` or `axes` blocks fall back to a top-down camera and +y up /
  +x right, with a note. A ghost with no vessel name (a pid-0 entry) is named by
  its recording id.
* What the writer emits that the page relies on (the dump design owns it):
  Unity world axes (left-handed); a body `orbit` ring of 64 points, OPEN (the
  page model closes it); lines already in time order where `ut` is known
  (`MapGeometryDecisions.OrderLines`: start, then end, then capture order;
  untimed lines last); an INACTIVE line (`active = false`) with EMPTY `points`;
  `pid` 0 for a ghost with no ghost map vessel and `committedIndex` -1 when the
  recording is not in the committed list; `counts.lines` / `counts.points` over
  ghost lines AND route legs (body rings and references are context).
* Recoverable oddities become a NOTE on that capture, shown in amber above the
  drawings: a line or icon relative to a body that is not in `bodies` (not drawn
  or measured), an unknown line `kind` (drawn as a polyline), a `counts` value
  that disagrees with what the file holds, `counts.unknownBodyItems` above 0
  (items the writer skipped for a body it could not name), `counts.truncated =
  true` with the `counts.dropped` numbers, a camera `forward` / `up` pair that is
  missing a half, zero or parallel (the camera view falls back to pitch and
  heading), and a `fovDeg` outside (0, 180).

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

### How to get the pictures

The picture lanes TP-1..TP-8 (`harness/scenarios/TP-*.toml`, tier `operator`)
exist to feed this page. Each copies a model lane's committed fixture, loop setup
and measured jump instants, and at each instant takes a few VIEWS:

| Lane | Subject (model lane) | Scene | Instants | Views |
| --- | --- | --- | --- | --- |
| TP-1 | Mun loop (V6M) | flight map | c1 transfer, arrival, park; c2 arrival | 12 |
| TP-2 | Mun loop (V6T) | Tracking Station | c1 transfer, arrival, park | 9 |
| TP-3 | Minmus loop, inclined (V7M) | flight map | c1 transfer, arrival, park; c2 arrival | 12 |
| TP-4 | Kerbin to Duna re-aim with landing (V25M) | flight map | transfer, arrival, descent, just before touchdown | 12 |
| TP-5 | Laythe to Vall (V17M) | flight map | c1 transfer, arrival, park; c2 arrival | 12 |
| TP-6 | Mun landing loop (V23M) | flight map | transfer, approach, descent, 60 s before touchdown; c2 the same | 14 |
| TP-7 | Kerbin depot route, route lines on (V18T) | Tracking Station | c1 near dock; c2 mid-leg, near dock | 9 |
| TP-8 | Inter-body routes (V26M) | flight map | the save clock (whole system and each route end) | 5 |

A view is three seam steps under one label: `MapCamera focus=<body> pitch=
heading= distance=` (the same frame on every run), `CaptureScreenshot label=L`
and `DumpMapGeometry label=L`. Labels read `tp<N>-<instant>-<view>`, for example
`tp1-c1-arrival-mun-top`. View kinds: `sys-top` (the Sun, straight down), a
parent-centred top-down that holds both ends of a transfer (`kerbin-top`,
`jool-top`, `duna-soi-top`), a close top-down on the ghost's body (`mun-top`,
`mun-low-top`, ...), and edge-on (`-edge`, pitch 0). Distances are metres from
the focus body's centre, about 2.2 times the radius the view must hold (the
body, its SOI or the orbit). A flight-map lane opens the map with `EnterMapView`
first. A Tracking Station lane reaches each instant in FLIGHT, saves it under its
own name and boots that save into the Tracking Station, because the fixtures'
new-game `intro_TS` lock lets only a 1x warp run there.

To get the pictures (flights run only when the operator asks):

```
cd harness
python run.py --id TP-1-mun-loop-map-pictures        # or --tag TP for all eight
python tools/map_mirror.py --shots "results/*_TP-*_shots" \
    --out ../../logs/map-mirror/<date>/map-mirror.html
```

Every run's PNGs and `.map.json` dumps land in `results/<runId>_shots/` through
the ordinary harvest, paired by label. Write the page under the umbrella's
`logs/`, never into the repository. To add a view, add the three steps to the
lane and its two required lines (`capturescreenshot ok label=L bytes=`,
`dumpmapgeometry ok label=L scene=<FLIGHT|TRACKSTATION> `); the label must be
unique in the lane, filename-safe and must not end in `.` or `_`.

## 3. The page

Left rail: one heading per lane, then the lane's captures in time order (run,
then game time, then frame), each with its label, scene, game time on the stock
Kerbin calendar (6 h days, 426 day years) and ghost count. Up / Down arrows step
through captures; `#cap=<runId>/<label>` deep-links one.

The selected capture shows a fact line (lane, run, scene, game time and raw ut,
frame, warp rate, camera target / pitch / heading / distance / field of view,
counts; in red when non-zero, the writer's unknown-body skips and dropped
entries; and how far the page's pitch / heading model lands from the recorded
camera frame, see section 6), any notes, a legend whose checkboxes hide a ghost
in every view, then the views and the measured tables.

### Views

Every view is an SVG computed in the browser from the embedded numbers, to
scale; the close-ups and the whole-system view are orthographic, the camera view
is perspective. Wheel zooms about the pointer, drag pans, double-click (or
`reset`) returns to the opening framing; the edge-on and camera views also
rotate with shift-drag (azimuth, or the camera about its pivot). Each view
prints a scale bar and `1 px = <distance>` (the camera view: at the pivot) so a
sub-pixel feature still reads its true size.

1. **The camera view beside the screenshot.** Looks the way the dump's `camera`
   block says the map camera looked, in perspective: the pivot is
   `camera.target` (a body, or a ghost's `vesselName`, at its icon), the eye
   sits `distance` behind it along the view direction, and the frame is the
   recorded `camera.forward` / `camera.up` (screen x = up x forward, Unity's
   right), with `fovDeg` as the vertical field of view, so `camera` framing puts
   `distance * tan(fovDeg / 2)` from the pivot at the panel's top edge. A dump
   without the recorded frame falls back to a basis from `pitchDeg` /
   `headingDeg` (yaw about up, then pitch about the pivot's right; pitch 90 is
   straight down) and, without `fovDeg`, an assumed 60 deg; the `camera` button's
   hover says which. A point at or behind the eye is not drawn. The panel takes
   the screenshot's aspect ratio and sits next to it. Shift-drag turns the
   camera about the pivot; the header shows the frame in use and how far below
   the system's horizontal plane it looks.
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
`Vector3.Cross` computes; in Unity's left-handed world (the dump's axes) that is
what a camera looking down `-up` with `right` to its right sees, so a prograde
orbit draws counter-clockwise, as in game. The same formula gives the camera
view's screen x from the recorded frame (`up x forward`), and a recorded camera
looking straight down `-up` with `F` on top reproduces the top-down basis
exactly (a unit test pins it).

### Drawing conventions

* Each ghost has its own colour, the same in every capture of the page (keyed by
  `recordingId`). Route legs share one route colour.
* Stroke by kind: orbit solid, polyline thicker solid, tail dotted, bridge (the
  short seam bridge Parsek draws between a traced leg and the following conic)
  thick short dashes, route leg dash-dot. An inactive line (`active = false`) is
  NOT drawn, even if it carries points: it is listed in the measured table only.
  A stretch of any line or ring that passes BEHIND a body (along the view ray:
  the fixed view direction, or in the camera view the ray from the eye, against
  every body sphere) is faint, so the edge-on and camera views read like the
  game, where the planet hides it.
* Body orbit rings are closed by the page (the writer sends 64 open points).
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
  body, point count, time range, altitude range, conic summary, recording id; an
  endpoint's hover gives its altitude. The treatment token in a ghost's header
  has a hover with its meaning.

### Measured

One block per ghost and per route, facts only. A ghost's header line carries its
recording id, `pid` (0 reads "no ghost map vessel"), committed index (-1 reads
"not in the committed list"), loop cycle, sample UT, treatment token, body,
whether the icon was drawn and its `source` (`proto` or `marker`), and the
`markerDecision` flags (draw, director traced path, polyline owning, icon
suppressed) when the dump carries them.

* **Icon to the nearest drawn point of its own lines**: the shortest distance
  from `icon.pos` to any segment of any of the ghost's DRAWN lines (active,
  with points, relative to a known body; converted to the ghost body's frame),
  and which line.
* Per line: kind, body, active (an inactive one reads "no (not drawn)" and gets
  no altitude, reference or gap figures; its conic, if any, still prints),
  point count, time range and duration, **lowest**
  and **highest altitude** (vertex distance from the line body's centre minus
  its radius), **conic Pe / Ap altitude** when a conic is given
  (`sma (1 - e) - R` / `sma (1 + e) - R`, `open` when e >= 1), **gap to next
  line**, and the reference column.
* **Gap to next line**: the distance from this line's last point to the next
  DRAWN line's first point (an inactive line is skipped, a bridge is a link like
  any other). "Next" is the writer's order: timed lines by start, then end, then
  dump position, then the untimed ones; the block says whether every drawn line
  had a time range. Lines relative to different bodies are converted to a common
  frame through the body `pos` values and the cell says `across bodies`.
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
formatting, the recorded camera frame and the open-ring close; reading both
sample dumps (`harness/lib/testdata/map_mirror_sample.map.json`, a Kerbin to
Mun loop in the writer's shape - open rings, an inactive stock orbit line, a
bridge, a pid-0 entry, icon sources, marker decisions, counts over legs - with
hand-checkable features listed in the test module's docstring, and
`map_mirror_minimal.map.json`, the always-written fields only); a wrong
schema and malformed values named by path; the measured values on the sample
against hand calculations; and the page: self-contained, deterministic with a
stamp, unreadable capture listed, no-PNG capture rendered, PNG inlined or
re-encoded to fit, budget refusal writing nothing, dump text unable to break out
of the page, and the page script parsing under `node --check` when node is on
PATH.

`CSharpWriterSourceSyncTests` (strict; it reads outside `harness/` on purpose)
checks that `Source/Parsek/MapGeometryJson.cs` declares `SchemaId =
"parsek-map-geometry/1"` and still writes every key the tool reads, that its
`dropped` block names the five kinds the header shows, that its `Kind*` line
kinds equal `map_mirror.LINE_KINDS` and each has a stroke style, that every
treatment token (the writer's four constants plus the Director's `Treatment`
enum in `MapRender/RenderSegment.cs`) has a meaning on the page, that
`MapGeometryRecorder.cs` still writes the icon sources `proto` and `marker`, and
that `hlib.ARTIFACT_SHOTS_SUFFIXES` lists `.map.json` (equal to
`hlib.DUMPMAPGEOMETRY_SUFFIX`) so the harvest copies dumps into the shots
directory.

## 6. Limits and open questions

* The camera view's pivot is the camera TARGET's centre (a body, or a ghost's
  icon); the dump does not record the eye's position, so a camera whose pivot
  sits off the target (stock SmoothDamp still settling) is drawn as if it did
  not. The `MapCamera` seam verb waits for the settle before it answers.
* The pitch / heading fallback's convention is unchecked against a real
  capture. Every dump from the shipped writer carries the recorded frame, so the
  fallback serves only older or hand-made files, and the header's "pitch/heading
  model vs recorded camera" angles measure the convention on the first real
  dump: near 0 means the fallback is right.
* Handedness: the dump writes Unity world axes (left-handed), which is what the
  top-down orientation assumes (section 3). On the first real capture the
  whole-system view should agree with the tracking station: planets run
  counter-clockwise.
* A ghost line is drawn exactly as its points are given; only BODY rings are
  closed by the page. Whether a stock orbit line's points repeat the first point
  at the end decides whether its own first and last dots coincide, and its gap
  to the next line is measured from the LAST point.
* Body positions are the dump's single instant. A line relative to a moon is
  placed at the moon's position NOW, which is what the map draws, so a seam
  measured across bodies is the seam on screen at that instant.
* No pass / fail and no tolerance: the page measures, the reader decides.
