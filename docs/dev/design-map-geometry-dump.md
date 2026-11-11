# Design: map geometry dump (`DumpMapGeometry`, `MapCamera`)

Status: BUILT 2026-11-11 on `claude/project-thread-zfo2hk`, xUnit- and hlib-covered, FIRST RUN LIVE
2026-11-11 by the picture lanes TP-1..TP-8 (85 views, all green). That run found orbit lines dumped
`active: true` with a stale frame's vertices; the fix (stock's fade opacity, "Stock's orbit-line
fade" below) and `MapCamera hold=` FLEW GREEN the same day (the eight lanes again, runs
`2026-11-11_2236` .. `_2250`: no drawn line off its conic, 39 lines faded, none with an unreadable
opacity). The seam-verb contracts (grammar, phases, refusals, roles)
are in `design-autotest-command-seam.md` under `#### DumpMapGeometry` and `#### MapCamera`; this
document owns the JSON file and how each field is read.

## The problem

The owner cannot judge by eye whether the map and Tracking Station draw a looped mission or a
supply route correctly (takeoffs, transfers, tilted orbits, parking orbits, landings, seams). The
existing instruments - `MapRenderProbe` parity and the render-composition manifest - speak in log
lines about DECISIONS. A screenshot shows the picture but cannot say which line is which, in which
body frame it was drawn, or which vertices the renderer actually received.

Two automation-only seam verbs close that:

- `DumpMapGeometry label=<label>` writes what the map DREW in one frame to
  `<KSP root>/Screenshots/<label>.map.json`, beside the `CaptureScreenshot` PNG of the same label.
  An offline tool (another worktree; not part of this change) turns it into pictures.
- `MapCamera focus=<body> [pitch=<deg>] [heading=<deg>] [distance=<metres>] [hold=<seconds>]`
  frames the stock map camera so a picture is taken from a standard angle (pitch 90 = straight
  down on the body, 0 = edge-on), optionally holding a few real seconds after it settles so stock's
  orbit-line fade has finished before the picture.

Both are inert unless the command seam is armed (`PARSEK_TEST_COMMANDS=1`), add no player-facing
UI, and neither needs `mapRenderTracing`.

## Files

| File | Role |
| --- | --- |
| `Source/Parsek/MapGeometryJson.cs` | Pure: the model, `MapGeometryDecisions` (per-ghost / per-line decisions), `MapGeometryJson` (`SchemaId`, caps, decimation, `ApplyCaps`, the hand-rolled writer). |
| `Source/Parsek/MapGeometryRecorder.cs` | Unity-side capture: reads one frame into the model; `OutputSuffix = ".map.json"`; writes through `FileIOUtils.SafeWriteBytes`. |
| `Source/Parsek/TestCommands/TestCommandDumpMapGeometry.cs` | Pure half of the dump verb: label parse (delegates to `CaptureScreenshot`), scene admission, poll decision, payload, log lines, reason tables. |
| `Source/Parsek/TestCommands/TestCommandMapCamera.cs` | Pure half of the camera verb: body resolution, arg parse and ranges, unit conversions, clamp report, settle decision, payload, log line, reason tables. |
| `Source/Parsek/TestCommands/ParsekTestCommandAddon.DumpMapGeometry.cs` / `.MapCamera.cs` | Thin appliers. |
| Read accessors added to existing files | `ShadowRenderDriver.TryGetLastIntent`, `GhostMapPresence.TryGetOverlapCycleForPid`, `GhostTrajectoryPolylineRenderer.VisitCachedLegs` / `VisitCachedForwardArcs` / `VisitCachedBridgeLines` / `IsForwardSecondaryDictKey`, `RouteTrajectoryLineRenderer.VisitCachedRouteLegs`, the marker-position tap on `MapMarkerRenderer` (`SetDrawnMarkerTapArmed`, `NoteDrawnMarker`, `CopyDrawnMarkers`) noted from `MapMarkerRenderer.DrawMarker` and `ParsekFlightMapMarkers.DrawMapMarkerAt`, and the arc-clip tap on `GhostOrbitArcPatch` (`SetDrawnArcTapArmed`, `TryGetDrawnArcClip`) noted where the clip returns. The seam-bridge cache entry (`BridgeLineEntry`) also records the leg, side and body of its last draw for the bridge visitor. |

Arch: the two root files are rule `TestCommands` (tooling) in `scripts/arch/modules.toml`.

## Frame coherence

The dump verb is TWO-PHASE. The applier arms the two read taps (marker positions, orbit arc
clips) and starts a coroutine on the
`DontDestroyOnLoad` seam addon that waits until a frame LATER than the arm frame (the arm frame's
OnGUI may have drawn markers before the tap was on) and then `WaitForEndOfFrame`. At that point
every map surface of that one frame has drawn: stock orbit lines (LateUpdate), Parsek's polylines
and forward arcs (camera OnPreCull), Parsek's IMGUI markers (OnGUI). Everything in the file is read
in that one synchronous pass: one `Time.frameCount`, one UT, one floating origin. The taps are
disarmed in a `finally` (and on the timeout), so they never outlive the capture; each note carries
its frame, and only the captured frame's notes are read. The completion poll answers OK once the
file is written, ERROR on a failed capture, ERROR `map-geometry-timeout` after the 60 s default.

## The contract

FIXED (an offline consumer codes against it). Reproduced verbatim from the brief (plus one line,
marked, naming the additive orbit-line keys), then the additive keys, then every field's source.

```
{
  "schema": "parsek-map-geometry/1",
  "label": "...",
  "scene": "FLIGHT" | "TRACKSTATION",
  "ut": <Planetarium UT>, "frame": <Time.frameCount>, "warpRate": <current rate>,
  "camera": {"target": "<name of the PlanetariumCamera target>", "pitchDeg": .., "headingDeg": .., "distance": ..},
  "axes": {"up": [x,y,z], "right": [x,y,z]},      // Planetarium.up and Planetarium.right expressed in the dump's axes, so a viewer can look "straight down" on the system
  "root": "Sun",
  "bodies": [ {"name": "...", "parent": "..." (omitted for the root), "radius": .., "soi": .. (omit when infinite),
               "pos": [x,y,z],                    // body centre minus ROOT centre, at the dump instant
               "orbit": [[x,y,z], ...]            // closed ring (64 points) of the body's own orbit relative to its PARENT centre; omitted for the root
              } ... ],                             // every CelestialBody
  "ghosts": [ {"recordingId": "...", "vesselName": "...", "pid": <ghost map vessel pid or 0>, "committedIndex": <int or -1>,
               "sampleUT": <the recording time this ghost is showing now, after the loop clock>,   // omit if unknown
               "loopCycle": <int>,                // omit if not looping / unknown
               "treatment": "<render treatment token, e.g. StockConic / TracedPath / SuppressedMarker / None>",
               "body": "<frame body of the ghost right now>",
               "icon": {"drawn": true|false, "pos": [x,y,z]},     // relative to "body" centre; pos omitted if unknown
               "lines": [ {"kind": "orbit" | "polyline" | "tail" | "bridge",   // "bridge" added 2026-11-11, see Conventions 6
                           "body": "<body the points are relative to>",
                           "active": true|false,
                           "points": [[x,y,z], ...],             // WHAT IS DRAWN, relative to this line's body centre
                           "ut": [t0, t1],                        // recording-time range the drawn piece covers, omit if unknown
                           "conic": {"sma":..,"ecc":..,"incDeg":..,"lanDeg":..,"argPeDeg":..,"meanAnomalyAtEpoch":..,"epoch":..},   // orbit lines only
                           "clipped": .., "opacity": .., "drawMode": "..",   // ADDITIVE, orbit lines only (see "Additive keys")
                           "reference": [[x,y,z], ...], "referenceKind": "recorded" | "intended"   // optional: the reference path the existing parity capture already has for this piece, same frame as points
                          } ... ] } ... ],
  "routeLines": [ {"routeId": "...", "routeName": "...", "legs": [ {"body": "...", "recordingId": "...", "points": [[x,y,z], ...]} ] } ],
  "counts": {"ghosts": n, "lines": n, "points": n, "truncated": false}
}
```

### Additive keys (a consumer that ignores them reads the contract unchanged)

- `camera.forward` `[x,y,z]`, `camera.up` `[x,y,z]`: the rendering camera's own world-space unit
  vectors (`PlanetariumCamera.Camera.transform.forward` / `.up`), in the dump's axes.
- `camera.fovDeg`: the rendering camera's vertical field of view (`Camera.fieldOfView`).
- `ghosts[].icon.source`: `"proto"` (stock's icon of the ghost's proto vessel), `"marker"`
  (Parsek's IMGUI marker), absent when nothing was drawn.
- `ghosts[].markerDecision`: `{"draw", "directorTracedPath", "polylineOwning", "iconSuppressed"}`,
  the Parsek marker decision for a proto ghost and its three inputs
  (`GhostMapPresence.ShouldDrawNonProtoMarkerForGhost`). Proto ghosts only.
- `lines[].clipped` (orbit lines, active only): `true` when Parsek's arc clip
  (`GhostOrbitArcPatch`) cut THIS frame's line to the `ut` window, so `points` are that window's
  arc; `false` when stock drew its own line (the window spans a full period or more, or the conic
  is degenerate / exactly parabolic), so `points` are stock's whole conic while `ut` is still the
  window. Absent on an inactive orbit line and on every other kind.
- `lines[].opacity` (orbit lines, drawn or not): stock's fade `OrbitRendererBase.lineOpacity`
  (0..1) in the capture frame, read through a Harmony `FieldRefAccess` on the protected field.
  Written on a not-drawn line too, so `active: false` with `opacity: 0` says WHY: stock faded the
  line out at this zoom. Absent when the field could not be read (the line is then decided by the
  older rule, and the capture's log line counts it in `orbitOpacityUnknown`).
- `lines[].drawMode` (orbit lines): the mode stock's `OrbitRendererBase.LateUpdate` hands
  `DrawOrbit` this frame, by its stock name: `OFF` while the orbit driver's `updateMode` is
  `IDLE`, else the renderer's `drawMode` (`OFF`, `REDRAW_ONLY`, `REDRAW_AND_FOLLOW`,
  `REDRAW_AND_RECALCULATE`; Parsek sets `REDRAW_AND_RECALCULATE` on every ghost renderer).
- `counts.unknownBodyItems`: items skipped because the body they name is not in `bodies` (see
  "Every named body is listed"). Always written; not a cap, so it does not set `truncated`.
- `counts.dropped`: `{"ghosts", "lines", "routes", "legs", "bodies"}`, the totals each hard cap
  dropped. Written only when `truncated` is true.

### Conventions (owner clarifications 2026-11-11)

1. **Axes.** Every vector is in Unity WORLD axes as-is, at the dump instant: a LEFT-HANDED frame
   (Unity's), with no conversion of any kind. `axes.up` / `axes.right` are `Planetarium.up` /
   `Planetarium.right` as stock holds them, so a viewer that wants "straight down on the system"
   looks along `-axes.up`.
2. **Camera frame.** `camera.forward`, `camera.up` and `camera.fovDeg` (above) let a viewer
   reproduce the screenshot's projection. Scaled space only translates and scales the world, so the
   camera's directions are in the same axes as every position.
3. **Open rings.** A body's `orbit` holds 64 points spread evenly over ONE period starting at the
   dump UT (point k at `ut + period * k / 64`); it does NOT repeat its first point at the end. A
   viewer closes the ring.
4. **Origins.** `bodies[].pos` is root-relative (body centre minus the root's centre, metres, same
   axes). Every other point is relative to the CENTRE of the body it names: a line's `points` and
   `reference` to the line's `body`, an icon's `pos` to the ghost's `body`, a route leg's `points`
   to the leg's `body`, a body's `orbit` to its `parent`. To place anything in the system, add the
   named body's `pos`.
5. **Line order.** A ghost's `lines` are in time order where their `ut` ranges are known: lines
   with a `ut` range first, sorted by start then end; then the lines with no range, in capture
   order (the stock orbit line, then polyline legs, then forward arcs, each in its renderer's cache
   order). Ties keep capture order (`MapGeometryDecisions.OrderLines`, a stable sort).
6. **Every named body is listed.** Every body a ghost, line, icon or leg names is present in
   `bodies`. An item naming any other body is skipped and counted in `counts.unknownBodyItems`: a
   line or route leg is dropped; a ghost keeps its entry and lines but loses its `body` and its
   icon `pos` (which would be relative to that body). `kind` is only `orbit`, `polyline`, `tail`
   or `bridge` (review 2026-11-11: a seam bridge is a fourth drawn piece; when one draws, the
   forward arc it joins is clipped back by the bridge's length, so a consumer that knew only three
   kinds would show a hole at every bridged seam).

### Where each field comes from

| Field | Source |
| --- | --- |
| `schema` | `MapGeometryJson.SchemaId` = `parsek-map-geometry/1`. |
| `label` | The verb's `label=` arg. |
| `scene` | `FLIGHT` or `TRACKSTATION`, the scene the verb was admitted in. |
| `ut` | `Planetarium.GetUniversalTime()` in the capture frame. |
| `frame` | `Time.frameCount` in the capture frame. |
| `warpRate` | `TimeWarp.CurrentRate` (omitted when there is no `TimeWarp`). |
| `camera.target` | `PlanetariumCamera.fetch.target.GetName()`. |
| `camera.pitchDeg` / `headingDeg` | `camPitch` / `camHdg` (radians in stock) converted to degrees. |
| `camera.distance` | `PlanetariumCamera.Distance` (scaled units) times `ScaledSpace.ScaleFactor`: metres from the camera pivot (the target). |
| `axes.up` / `axes.right` | `Planetarium.up` / `Planetarium.right`. |
| `root` | The first body in `FlightGlobals.Bodies` with no orbit driver / orbit / reference body, or whose reference body is itself (stock: the Sun). |
| `bodies[]` | Every `FlightGlobals.Bodies` entry, in that list's order. `name` = `bodyName`; `parent` = `referenceBody.bodyName` (omitted for the root); `radius` = `Radius`; `soi` = `sphereOfInfluence` (omitted when not finite); `pos` = `body.position - root.position`; `orbit` = 64 samples of `orbitDriver.orbit.getRelativePositionAtUT(t).xzy` (the Zup to world swizzle), omitted for the root and for a non-finite or non-positive period. |
| `ghosts[]` (proto) | One entry per pid in `GhostMapPresence.ghostMapVesselPids`, in ascending pid order. `pid` is that pid. `recordingId` = `GhostMapPresence.FindRecordingIdByVesselPid`, else the committed trajectory's id (`TryGetCommittedTrajectoryForPid`); `committedIndex` = that call's index (-1 when none); `vesselName` = the trajectory's vessel name, else the proto vessel's; `loopCycle` = `GhostMapPresence.TryGetOverlapCycleForPid` (overlap loop instances only). |
| `ghosts[]` (pid 0) | A recording whose Parsek polyline legs / forward arcs / marker drew but which has no proto vessel gets an entry with `pid` 0; `committedIndex` and `vesselName` come from `GhostMapPresence.TryGetCommittedRecordingById` (the render layer's own lookup). A marker key `<recordingId>#<cycle>` sets `loopCycle`. |
| `ghosts[].treatment` | `MapGeometryDecisions.TreatmentToken` over the Director's last intent (`ShadowRenderDriver.TryGetLastIntent`): a VISIBLE intent names its treatment (`StockConic` / `TracedPath`); else `SuppressedMarker` when the icon-suppression floor holds the ghost; else `None` for a hidden intent; else `NoIntent` (proto ghost, no intent) or `DriverOnly` (pid-0 entry). |
| `ghosts[].sampleUT` | The visible intent's `DriveUT` (the recording time the Director drives the ghost at, after the loop clock). Omitted otherwise. |
| `ghosts[].body` | The visible intent's `FrameBodyName`; else the proto vessel's orbit reference body; else (pid-0 entries) the first ACTIVE line's body; else (a marker-only entry: no intent, no proto orbit, no active line) the body whose sphere of influence contains the icon's world position (`MapGeometryDecisions.ContainingSoiBody`: of the bodies whose finite SOI contains the point, the one with the smallest SOI, else the root; KSP's own innermost-SOI rule). The marker tap carries no body of its own, so the SOI rule is the one used. |
| `ghosts[].icon` (proto) | `drawn` = the proto vessel's `orbitRenderer` is enabled and its `drawIcons` is not `NONE`; `pos` = `vessel.GetWorldPos3D() - body.position` (reported even when not drawn: where the icon sits). |
| `ghosts[].icon.pos` (both) | Resolved LAST: the capture keeps every icon's world position and subtracts the ghost body's centre only after the body fallbacks above have run, so a pid-0 entry (whose body comes from its lines or the SOI rule) keeps its marker position. |
| `ghosts[].icon` (marker) | A Parsek IMGUI marker drawn in the capture frame (the tap records the world position each marker funnel already computes, after the behind-camera check) replaces the proto reading on the entry whose key it carries (`recordingId#cycle` first, then the bare id for a non-loop or the recording's primary pid); `drawn` true, `source` `marker`, `pos` = marker world position minus the ghost body's centre. The flight `preview` marker is skipped. |
| `lines[]` kind `orbit` | The proto vessel's stock `orbitRenderer.OrbitLine`. `active` = stock DREW the line this frame (`MapGeometryDecisions.ClassifyOrbitLine`): the Vectrosity line is active AND the renderer is enabled AND `drawMode` is not `OFF` AND `opacity` > 0 (with `opacity` unreadable, the first three only); `points` filled only when active; `opacity` / `drawMode` always; `conic` from `vessel.orbitDriver.orbit` (KSP-native: degrees for inc / LAN / argPe, `meanAnomalyAtEpoch` radians, `epoch` UT). `ut` = the LIVE-frame window of the arc drawn, minus `GetGhostOrbitEpochShift` (recording time): the window `GhostOrbitArcPatch` clipped the line to in the captured frame (its tap), else, when stock drew the line, the same `GhostMapPresence.TryGetVisibleOrbitBoundsForGhostVessel` call the clip makes (`MapGeometryDecisions.TrySelectOrbitLineWindow`). The clip evaluates that window at `liveUT - shift` (legacy drive) or at the live UT on an orbit whose epoch carries the shift (Director drive); both mean recording time `liveUT - shift`. The clip's vertices are written into `points3[drawStart..drawEnd]`, so `points` are the clipped arc; `clipped` says whether the clip applied. No `reference`. |
| `lines[]` kind `polyline` | Each cached Parsek trajectory leg (`GhostTrajectoryPolylineRenderer.VisitCachedLegs`) reported when its mesh is active or it drew this frame or the previous one (`MapGeometryDecisions.ClassifyCachedLine`). `active` = the mesh is active; `points` are filled ONLY for an active line. `ut` = the leg's recorded span. `reference` = the leg's parity capture `parityRecordedRel` (`referenceKind` `recorded`), only when the leg is not anchored and its parity capture is from THIS frame's draw. |
| `lines[]` kind `tail` | Each cached forward arc (the chain drawn AHEAD of the ghost icon, `VisitCachedForwardArcs`), same reporting and points rule. When a seam bridge draws, the arc's `drawStart` / `drawEnd` are already pulled in by the bridge's length, so `points` are the piece left. `reference` = the arc's intended body-relative offsets (`referenceKind` `intended`). |
| `lines[]` kind `bridge` | Each cached seam bridge (`VisitCachedBridgeLines`, ordinal key order), the filler from a body-fixed leg's drawn end to the adjacent inertial conic; attached to the LEG's recording's entry. Same reporting and points rule. `body` = the body its scaled points are centred on (the joined conic's body). No `ut` (it blends two pieces and covers no recorded span of its own), no `reference`. |
| `points` (all lines, route legs) | The vertices Vectrosity hands the renderer: `VectorLine.points3[drawStart..drawEnd]` (clamped), each mapped back to metres relative to the body as drawn THIS frame by `RenderGeometrySampler.RenderedScaledVertexToBodyRelative(vertex, body.scaledBody.transform.position, ScaledSpace.ScaleFactor)`. A line whose body cannot be resolved keeps no points. |
| `routeLines[]` | `RouteTrajectoryLineRenderer.VisitCachedRouteLegs`, ACTIVE legs only (the route renderer's own sweep hides a leg a ghost owns or paints this frame); sorted by `routeId`; `routeName` from `RouteStore.CommittedRoutes`; leg `recordingId` = the member recording. |
| `counts` | From `MapGeometryJson.ApplyCaps`: `ghosts` written, `lines` and `points` over ghost lines AND route legs (body rings and references are context, not counted), `truncated` when any hard cap dropped an entry. |

### Absent and partial fields

A field that cannot be filled honestly is omitted, never invented: a non-finite scalar omits its
key; a non-finite point is dropped from its list. Specifically:

- `sampleUT` exists only for a ghost with a VISIBLE Director intent.
- `loopCycle` exists only for an overlap loop instance (proto) or a per-instance marker key.
- `ut` on an orbit line exists only when the arc clip drew a window this frame or
  `TryGetVisibleOrbitBoundsForGhostVessel` returns one; on a polyline / tail line it is always the
  cached span; a bridge never has one.
- An INACTIVE line keeps its entry with `active: false` and EMPTY `points` (every kind). An orbit
  line stock faded out (`opacity` 0) or drew in mode `OFF` is inactive in this sense, whatever the
  Vectrosity mesh flag says.
- `opacity` on an orbit line is absent only when stock's field could not be read.
- An icon `pos` is absent when the ghost ends with no body (no intent, orbit, active line, and no
  SOI the icon lies in).
- `vesselName`, `recordingId` or `body` may be absent for an entry whose render-layer lookup has
  none.

## Bounds

- Decimation (`MapGeometryJson.Decimate`): every point list - a line, a route leg, a reference, a
  body ring - keeps at most 512 points: output index i is source index
  `round(i * (n - 1) / 511)`, so the first and last point survive and the spread is even.
  Decimation is the documented resolution limit, not a truncation.
- Hard caps (whole entries, in document order): 512 bodies, 512 ghosts, 4096 lines, 262144 points
  (ghost lines plus route legs), 128 routes, 256 legs per route. Any drop sets
  `counts.truncated` and writes `counts.dropped`; the verb's OK payload carries `truncated=` and a
  WARN `dumpmapgeometry truncated ...` names every dropped total. Nothing is capped silently.
- Numbers: `ToString("R", CultureInfo.InvariantCulture)` everywhere (pinned under `de-DE`); NaN
  and Infinity are never written. The file is UTF-8 without a BOM.

## The verbs

Grammar, refusals and phases are specified in `design-autotest-command-seam.md`; in short:

- `DumpMapGeometry label=<label>`: REJECTED `label-arg-missing`, `label-arg-invalid`,
  `map-view-closed`, `not-in-map-scene`; ERROR `map-geometry-capture-failed reason=<token>`
  (`scene-changed`, `map-view-closed`, `threw-<ExceptionType>`), `map-geometry-timeout`. OK
  payload `label path scene bytes frame ghosts lines points truncated`.
- `MapCamera focus=<body> [pitch=<deg>] [heading=<deg>] [distance=<metres>] [hold=<seconds>]`:
  pitch [-90, 90], heading [-360, 360], distance (0, 1e13], hold [0, 10] real seconds (default
  0); REJECTED `mapcamera-focus-arg-missing`, `mapcamera-unknown-body`,
  `mapcamera-pitch-invalid`, `mapcamera-pitch-out-of-range`, `mapcamera-heading-invalid`,
  `mapcamera-heading-out-of-range`, `mapcamera-distance-invalid`,
  `mapcamera-distance-out-of-range`, `mapcamera-hold-invalid`, `mapcamera-hold-out-of-range`,
  `map-view-closed`, `not-in-map-scene`; ERROR `mapcamera-no-camera`,
  `mapcamera-focus-not-targetable`, `mapcamera-focus-not-applied`, `mapcamera-scene-lost`,
  `mapcamera-not-settled`. OK payload `focus pitch heading distance pitchClamped distanceClamped
  frames hold`, the values stock HOLDS after its own clamps, read back at the END of the hold.
  The hold is wall-clock time after the settle (see "Stock's orbit-line fade"); the settle gets
  the 60 s budget minus the hold, so settle plus hold never outruns the step budget.

Harness roles: `DumpMapGeometry` is `inert` on the tail axis (and C# `NonMutatingVerbs`);
`MapCamera` is `world-mutating` (it moves the live map camera, `EnterMapView`'s reasoning); both
are `recording` on the post-mission axis. hlib mirrors the vocabularies
(`MapGeometrySeamVerbSourceSyncTests`) and harvests `.map.json` with the run's shots
(`ARTIFACT_SHOTS_SUFFIXES`, pinned against `MapGeometryRecorder.OutputSuffix` by
`test_contact_sheet.py`).

## The capture's log line

Each capture logs ONE Info line, `[MapGeometry] capture frame=.. scene=.. bodies=.. ghosts=..
protoGhosts=.. legs=.. legRefs=.. arcs=.. bridges=.. markersDrawn=.. markerBodiesBySoi=.. routes=..
routeLegs=.. scaledOffset=(x,y,z) maxScaledCentreSkewM=.. iconLineMeasured=..
maxIconToOrbitLineM=<m> pid=<pid> orbitLinesFaded=.. orbitOpacityUnknown=..`
(`MapGeometryDecisions.FormatCaptureSummary`, invariant culture). It is the frame-offset check
under warp:

- `scaledOffset` is ScaledSpace's floating offset at the capture (scaled units; its private
  `totalOffset`, read as `-LocalToScaledSpace(0)`).
- `maxScaledCentreSkewM` is the max over bodies, in metres, of the gap between
  `scaledBody.transform.position` (the centre every drawn vertex is mapped against) and
  `LocalToScaledSpace(body.position)`. Non-zero means the drawn scaled frame and the world frame
  disagree in that frame.
- `maxIconToOrbitLineM` is, over proto ghosts with an ACTIVE orbit line, the max distance from the
  proto icon (`vessel.GetWorldPos3D()` relative to the orbit's body) to the nearest point of the
  drawn line's segments, with the worst ghost's pid; `none` when nothing was measured. A ghost icon
  sitting far off its own drawn orbit shows here without opening the file. An icon the drive
  clamps or wraps (past its window, the loiter circle) can legitimately read off the clipped arc,
  so read that pid's entry in the file before calling a large value a defect. A faded orbit line
  is not drawn, so it is not measured.
- `orbitLinesFaded` counts the proto orbit lines that were otherwise active (mesh active, renderer
  enabled, mode not `OFF`) at fade opacity 0, so dumped `active: false` with no points;
  `orbitOpacityUnknown` counts the lines decided by the older rule because the opacity field could
  not be read (0 on a stock install; non-zero means the field moved and the dump can again report
  a stale line as drawn).

## Stock's orbit-line fade (read from the KSP 1.12.5 IL)

The first live flights (TP-1..TP-8, 2026-11-11) dumped about a fifth of the ghosts' orbit lines
`active: true` with vertices that were not on the conic (a Mun hyperbola 38 Mm from the Mun, a
Duna orbit whose vertices were the old heliocentric transfer arc, a Kerbin orbit of the right shape
offset 50 Mm to 5e10 m). The IL of `Assembly-CSharp.dll` says why (`ikdasm`; offsets from that
listing, the obfuscator's dead `switch` blocks left out):

- `OrbitRendererBase.LateUpdate` calls `DrawOrbit(driver.updateMode == IDLE ? OFF : drawMode)`,
  then `DrawNodes()`.
- `OrbitRenderer.DrawOrbit(mode)` (the vessel subclass) returns early when the map is not
  enabled, there is no camera target or the line is missing; otherwise it sets
  `OrbitLine.active = GetActive(mode, isDebris, filtered)` (`mode != OFF && lineOpacity > 0 &&
  ...`, using the opacity BEFORE this frame's update) and calls the base `DrawOrbit` unless the
  vessel is debris without mouse-over or the map filter hides it.
- `OrbitRendererBase.DrawOrbit(mode)` (IL_001e..IL_0048): `SplineOpacityUpdate()`, then
  `if (lineOpacity == 0) return` and `if (mode == OFF) return`, BEFORE `SetColor(GetOrbitColour())`
  and the draw. Then by mode: `REDRAW_ONLY` and `REDRAW_AND_FOLLOW` call `DrawSpline()` only
  (IL_00a4 / IL_00ab); `REDRAW_AND_RECALCULATE` sets the line's `rectTransform.position` to zero
  and calls `UpdateSpline()` then `DrawSpline()` (IL_007c..IL_009e).
- `SplineOpacityUpdate` pins `lineOpacity` to 1 for the active vessel and a focused renderer.
  Otherwise it computes `CamVsSmaRatio = MapCamera.Distance / (|sma| * ScaledSpace.InverseScaleFactor)`
  (a landed vessel uses the body radius; an orbit `IsRenderableOrbit` rejects for the camera
  target's body reads 0) and moves `lineOpacity` toward 1 by `0.25 * Time.deltaTime` while the
  ratio is strictly inside (`lowerCamVsSmaRatio`, `upperCamVsSmaRatio`), else toward 0 by the same
  step (IL_0130..IL_01ed). A full fade takes 4 s of scaled time.
- `UpdateSpline` is the only writer of `points3` (the orbit's sampled positions through
  `ScaledSpace.LocalToScaledSpace`); `DrawSpline` uploads whatever `points3` holds (`Draw3D`). Its
  only other callers are `OrbitRendererBase.Start` (once) and `DrawSpline` itself, when
  `MapView.Draw3DLines` flips.

So at a zoom where stock fades a ghost's orbit out, nothing recomputes `points3`: it keeps the
last drawn frame's vertices, in that frame's scaled-space origin and for that frame's orbit, while
Parsek's `GhostOrbitLinePatch` LateUpdate Postfix (which runs after the base `DrawOrbit`) can still
set `line.active = true`. The mesh then keeps the last uploaded colour, whose alpha was the
near-zero opacity of the last faded draw, so nothing visible is drawn. The dump therefore decides
an orbit line drawn only when the mesh is active, the renderer enabled, the mode not `OFF` and the
opacity above 0, and it writes `opacity` so a reader sees the fade.

Two cases the rule cannot see, by construction:

- **A mode that redraws without recalculating.** `REDRAW_ONLY` / `REDRAW_AND_FOLLOW` at opacity
  above 0 draw `points3` as they are, so the line on screen IS stale. The dump writes those
  vertices (they are what was drawn) and names the mode in `drawMode`. Parsek sets
  `REDRAW_AND_RECALCULATE` on every ghost renderer (`GhostMapPresence`), so a ghost line in another
  mode is itself a finding.
- **A renderer stock skips before the base draw** (debris without mouse-over, a map filter): its
  opacity is not updated that frame, so it reads the last value. Ghost proto vessels take their
  recording's vessel type, so a debris ghost's orbit line is the one case to read with care.

Because the fade runs on `Time.deltaTime` (SCALED time), a picture taken right after a camera move
can catch a line mid-fade. `MapCamera hold=` waits WALL-CLOCK seconds after the settle: at 1x that
is the fade's own clock; under physics warp the fade runs `timeScale` times faster per real second
(the hold is then more than enough); at on-rails warp `timeScale` stays 1; with the game paused
(`timeScale` 0) the fade does not run at all and no hold finishes it. Game time moves during the
hold at whatever rate the game runs, so a view taken after a hold is that many seconds later than
the instant the lane jumped to.

## Live-game questions

The first live flights (TP-1..TP-8, 2026-11-11, before the fade fix) answered two of these and
raised the fade; the rest are still open. Read on the next run:

- The scaled-space inversion. The vertex mapping assumes the polyline / orbit meshes are drawn in
  scaled space with no rotation and that `body.scaledBody.transform.position` in the end-of-frame
  read is the same one the vertices were built against. A line that lands offset or scaled wrong in
  the picture means one of those does not hold.
- `MapCamera`'s settle. ANSWERED on the first flights: all 85 settled, none clamped
  (`pitchClamped=false distanceClamped=false` throughout). It answers once the camera-to-target
  distance is within 0.1 % of what stock holds, or the camera stayed still for 3 frames; the
  pitch / distance clamps stock applies in LateUpdate are only reported, never fought.
- `MapCamera hold=` and the opacity read. ANSWERED on the re-fly (runs `2026-11-11_2236` ..
  `_2250`): all 85 `mapcamera ok` lines carry `hold=4.5`, `orbitOpacityUnknown=0` on every
  capture, 39 orbit lines faded, and the page's conic check flags no drawn line. A drawn line may
  still sit just under 1 (`tp1-c1-arrival-mun-top`: 0.996, a fade-in not quite finished), which
  draws; a line at 0 < opacity < 1 well below that after the hold would mean 4.5 s is not enough.
- The proto icon's `drawn` reading (`orbitRenderer.enabled` plus `drawIcons`), which is a proxy for
  stock's icon draw, not a read of it. ANSWERED IN PART by TP-4's run `2026-11-11_2316`: the dump
  wrote `drawn: true` for a proto icon the screenshot does not show (`tp4-c1-arrival-duna-top`).
  Stock's `OrbitRendererBase.objectNode_OnUpdateIcon` sets the icon colour's alpha to
  `lineOpacity` (IL: `XKCDColors.A(nodeColor, lineOpacity)`; the vessel subclass does not override
  it), so an icon fades out with its orbit line. The map mirror page applies that rule itself
  (`map_mirror.proto_icon_faded`: a proto icon whose orbit line reads opacity 0 is "not drawn
  (stock fade)"); the dump still writes the proxy, and folding the opacity into `icon.drawn` is
  owed (todo MAP-PICTURE-LANES-FIRST-FLIGHT).
- The marker positions in FLIGHT come from the same world position the marker funnel projects;
  whether they agree with the PNG's marker to the pixel is unmeasured.
- The arc-clip tap. `clipped` and the orbit `ut` window rely on `GhostOrbitArcPatch`'s Prefix
  running in the captured frame. Stock `OrbitRendererBase.DrawOrbit` calls `UpdateSpline` on every
  DRAW in mode `REDRAW_AND_RECALCULATE` (the ghosts' mode) and on no frame at fade opacity 0
  (KSP 1.12.5 IL, "Stock's orbit-line fade"), so a drawn ghost line is re-clipped each frame. On
  the first flights every stale line read `clipped: false` with a partial arc: the clip had not
  run that frame because stock had faded the line out, which is the case the opacity now names.
- The seam bridges. The bridge visitor reads the leg / side / body the bridge's own draw recorded;
  a bridge that lands detached from its leg's end in the picture would mean the bridge cache and
  the leg cache were read from different frames.
