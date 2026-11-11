# Design: map geometry dump (`DumpMapGeometry`, `MapCamera`)

Status: BUILT 2026-11-11 on `claude/project-thread-zfo2hk`, xUnit- and hlib-covered, NOT yet run
in a live game and wired to no lane. The seam-verb contracts (grammar, phases, refusals, roles)
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
- `MapCamera focus=<body> [pitch=<deg>] [heading=<deg>] [distance=<metres>]` frames the stock map
  camera so a picture is taken from a standard angle (pitch 90 = straight down on the body, 0 =
  edge-on).

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
| Read accessors added to existing files | `ShadowRenderDriver.TryGetLastIntent`, `GhostMapPresence.TryGetOverlapCycleForPid`, `GhostTrajectoryPolylineRenderer.VisitCachedLegs` / `VisitCachedForwardArcs` / `IsForwardSecondaryDictKey`, `RouteTrajectoryLineRenderer.VisitCachedRouteLegs`, and the marker-position tap on `MapMarkerRenderer` (`SetDrawnMarkerTapArmed`, `NoteDrawnMarker`, `CopyDrawnMarkers`) noted from `MapMarkerRenderer.DrawMarker` and `ParsekFlightMapMarkers.DrawMapMarkerAt`. |

Arch: the two root files are rule `TestCommands` (tooling) in `scripts/arch/modules.toml`.

## Frame coherence

The dump verb is TWO-PHASE. The applier arms the marker tap and starts a coroutine on the
`DontDestroyOnLoad` seam addon that waits until a frame LATER than the arm frame (the arm frame's
OnGUI may have drawn markers before the tap was on) and then `WaitForEndOfFrame`. At that point
every map surface of that one frame has drawn: stock orbit lines (LateUpdate), Parsek's polylines
and forward arcs (camera OnPreCull), Parsek's IMGUI markers (OnGUI). Everything in the file is read
in that one synchronous pass: one `Time.frameCount`, one UT, one floating origin. The tap is
disarmed in a `finally`, so it never outlives the capture. The completion poll answers OK once the
file is written, ERROR on a failed capture, ERROR `map-geometry-timeout` after the 60 s default.

## The contract

FIXED (an offline consumer codes against it). Reproduced verbatim from the brief, then the
additive keys, then every field's source.

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
               "lines": [ {"kind": "orbit" | "polyline" | "tail",
                           "body": "<body the points are relative to>",
                           "active": true|false,
                           "points": [[x,y,z], ...],             // WHAT IS DRAWN, relative to this line's body centre
                           "ut": [t0, t1],                        // recording-time range the drawn piece covers, omit if unknown
                           "conic": {"sma":..,"ecc":..,"incDeg":..,"lanDeg":..,"argPeDeg":..,"meanAnomalyAtEpoch":..,"epoch":..},   // orbit lines only
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
   icon `pos` (which would be relative to that body). `kind` is only `orbit`, `polyline` or `tail`.

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
| `ghosts[].body` | The visible intent's `FrameBodyName`; else the proto vessel's orbit reference body; else (pid-0 entries) the first active line's body. |
| `ghosts[].icon` (proto) | `drawn` = the proto vessel's `orbitRenderer` is enabled and its `drawIcons` is not `NONE`; `pos` = `vessel.GetWorldPos3D() - body.position` (reported even when not drawn: where the icon sits). |
| `ghosts[].icon` (marker) | A Parsek IMGUI marker drawn in the capture frame (the tap records the world position each marker funnel already computes, after the behind-camera check) replaces the proto reading on the entry whose key it carries (`recordingId#cycle` first, then the bare id for a non-loop or the recording's primary pid); `drawn` true, `source` `marker`, `pos` = marker world position minus the ghost body's centre. The flight `preview` marker is skipped. |
| `lines[]` kind `orbit` | The proto vessel's stock `orbitRenderer.OrbitLine`. `active` = the Vectrosity line is active AND the renderer is enabled; `points` filled only when active; `conic` from `vessel.orbitDriver.orbit` (KSP-native: degrees for inc / LAN / argPe, `meanAnomalyAtEpoch` radians, `epoch` UT); `ut` = `GhostMapPresence.ghostOrbitBounds` minus `GetGhostOrbitEpochShift` (recording time). No `reference`. |
| `lines[]` kind `polyline` | Each cached Parsek trajectory leg (`GhostTrajectoryPolylineRenderer.VisitCachedLegs`) reported when its mesh is active or it drew this frame or the previous one. `ut` = the leg's recorded span. `reference` = the leg's parity capture `parityRecordedRel` (`referenceKind` `recorded`), only when the leg is not anchored and its parity capture is from THIS frame's draw. |
| `lines[]` kind `tail` | Each cached forward arc (the chain drawn AHEAD of the ghost icon, `VisitCachedForwardArcs`), same reporting rule. `reference` = the arc's intended body-relative offsets (`referenceKind` `intended`). |
| `points` (all lines, route legs) | The vertices Vectrosity hands the renderer: `VectorLine.points3[drawStart..drawEnd]` (clamped), each mapped back to metres relative to the body as drawn THIS frame by `RenderGeometrySampler.RenderedScaledVertexToBodyRelative(vertex, body.scaledBody.transform.position, ScaledSpace.ScaleFactor)`. A line whose body cannot be resolved keeps no points. |
| `routeLines[]` | `RouteTrajectoryLineRenderer.VisitCachedRouteLegs`, ACTIVE legs only (the route renderer's own sweep hides a leg a ghost owns or paints this frame); sorted by `routeId`; `routeName` from `RouteStore.CommittedRoutes`; leg `recordingId` = the member recording. |
| `counts` | From `MapGeometryJson.ApplyCaps`: `ghosts` written, `lines` and `points` over ghost lines AND route legs (body rings and references are context, not counted), `truncated` when any hard cap dropped an entry. |

### Absent and partial fields

A field that cannot be filled honestly is omitted, never invented: a non-finite scalar omits its
key; a non-finite point is dropped from its list. Specifically:

- `sampleUT` exists only for a ghost with a VISIBLE Director intent.
- `loopCycle` exists only for an overlap loop instance (proto) or a per-instance marker key.
- `ut` on an orbit line exists only when `GhostMapPresence` holds that ghost's orbit bounds; on a
  polyline / tail line it is always the cached span.
- An INACTIVE line keeps its entry with `active: false` and EMPTY `points`.
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
- `MapCamera focus=<body> [pitch=<deg>] [heading=<deg>] [distance=<metres>]`: pitch [-90, 90],
  heading [-360, 360], distance (0, 1e13]; REJECTED `mapcamera-focus-arg-missing`,
  `mapcamera-unknown-body`, `mapcamera-pitch-invalid`, `mapcamera-pitch-out-of-range`,
  `mapcamera-heading-invalid`, `mapcamera-heading-out-of-range`, `mapcamera-distance-invalid`,
  `mapcamera-distance-out-of-range`, `map-view-closed`, `not-in-map-scene`; ERROR
  `mapcamera-no-camera`, `mapcamera-focus-not-targetable`, `mapcamera-focus-not-applied`,
  `mapcamera-scene-lost`, `mapcamera-not-settled`. OK payload `focus pitch heading distance
  pitchClamped distanceClamped frames`, the values stock HOLDS after its own clamps.

Harness roles: `DumpMapGeometry` is `inert` on the tail axis (and C# `NonMutatingVerbs`);
`MapCamera` is `world-mutating` (it moves the live map camera, `EnterMapView`'s reasoning); both
are `recording` on the post-mission axis. hlib mirrors the vocabularies
(`MapGeometrySeamVerbSourceSyncTests`) and harvests `.map.json` with the run's shots
(`ARTIFACT_SHOTS_SUFFIXES`, pinned against `MapGeometryRecorder.OutputSuffix` by
`test_contact_sheet.py`).

## Not yet proven (live-game questions)

The verbs have only run under xUnit (pure halves) and the harness unit suite. On the first live
run, read these:

- The scaled-space inversion. The vertex mapping assumes the polyline / orbit meshes are drawn in
  scaled space with no rotation and that `body.scaledBody.transform.position` in the end-of-frame
  read is the same one the vertices were built against. A line that lands offset or scaled wrong in
  the picture means one of those does not hold.
- `MapCamera`'s settle. It answers once the camera-to-target distance is within 0.1 % of what
  stock holds, or the camera stayed still for 3 frames; a camera that keeps creeping (pivot
  SmoothDamp) could read `mapcamera-not-settled`, and the pitch / distance clamps stock applies in
  LateUpdate are only reported, never fought.
- The proto icon's `drawn` reading (`orbitRenderer.enabled` plus `drawIcons`), which is a proxy for
  stock's icon draw, not a read of it.
- The marker positions in FLIGHT come from the same world position the marker funnel projects;
  whether they agree with the PNG's marker to the pixel is unmeasured.
