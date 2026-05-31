# Map-view trajectory polyline for non-orbital phases (v6)

> v6 ready for implementation review.

### v6 changes (since v5)

- MAJOR fix: `cachedLoopUnits` field is private; v5 sample code did not compile. v6 adds an internal `CurrentCachedLoopUnits` accessor to both scene controllers as a commit-2 deliverable and updates §2.1 sample code + §2.2 prose to use the accessor name.

### v5 changes (since v4)

- CRITICAL-1: added ERS audit allowlist + `[ERS-exempt]` file-level comment requirement (§2.2 / §4 / §6).
- CRITICAL-2: switched DDOL placement to `Awake()` with singleton dedup on `gameObject`, matching `TestRunnerShortcut` precedent (§2.1 / §2.2 / revision history corrected).
- CRITICAL-3: `cachedLoopUnits` resolved per-scene via scene detection (TS / FLIGHT controllers); transitional frames where the per-scene controller has not yet awoken DEFER the draw rather than substituting `LoopUnitSet.Empty` (the reviewer suggested Empty as fallback, but Empty would silently defeat the `renderHidden` filter; deferring one frame is safer) (§2.1 / §2.2).
- MAJOR-1: corrected `ClassifyAtmosphericMarkerSkip` signature to `(rec, recordingIndex, currentUT, suppressedIds)` returning `AtmosphericMarkerSkipReason`, and noted the extra filters it brings (§2.1 / §2.2).
- MAJOR-2: added Vectrosity reference deliverable to commit 2 in §4 (csproj `<Reference Include="Vectrosity">` with HintPath).
- MAJOR-3: subscribed Driver to `GameEvents.onGameStateLoad` and added explicit `Clear()` to invalidate the cache across save/load (§1.4 / §2.1 / §6).
- MAJOR-4: routed §5.3 in-game test through `RecordingBuilder` to avoid manual `TrackSection` struct init.
- MINOR-1: removed OQ#6 (already pinned by §1.3 cap).
- MINOR-2: title bumped to v5 + handoff paragraph replaced with one-line "ready for implementation review".
- OQ pin (scenes): TS + Flight only for v1; KSC scene deferred.
- OQ pin (persistence): `useGhostTrajectoryPolyline` persisted through `ParsekSettingsPersistence` like `useSmoothingSplines`.
- OQ pin (commit split): data structs + builder + tests in commit 1; Driver MonoBehaviour + Vectrosity reference + allowlist in commit 2.
- Removed stale `sampleCtx` shorthand from §2.1 pseudocode in favor of the explicit `ResolveTrackingStationSampleUT` signature.

Status: DRAFT v6 ready for implementation review. v1 superseded by clean-context Opus
review 2026-05-28; v2 / v3 / v4 / v5 / v6 iteratively folded subsequent reviews.

Branch: `map-trajectory-polyline` (off `origin/main` at `9b6d69c2`).

Revision history:
- 2026-05-28 v1 initial draft.
- 2026-05-28 v4 folded review-of-v3 + KSP disassembly verification:
  - CRITICAL (v3 hookup site): `RefreshTrackingStationGhosts` (`GhostMapPresence.cs:5482`)
    iterates `vesselsByRecordingIndex` (ghost-bearing only); `CheckPendingMapVessels`
    (`ParsekPlaybackPolicy.cs:1321`) iterates `pendingMapVessels` (deferred creates only).
    Atmospheric-only recordings -- the entire reason this feature exists -- are in
    NEITHER iterator. v4 hooks the Driver MonoBehaviour directly to
    `RecordingStore.CommittedRecordings` with the same suppression + loop-unit
    filters `DrawAtmosphericMarkers` / `DrawMapMarkers` use.
  - CRITICAL (DDOL placement) [RETRACTED in v5]: v4 claimed the KSPAddon doc-string
    requires `Start()` over `Awake()` for `DontDestroyOnLoad`. v5 retracts this and
    matches the only existing repo precedent: `TestRunnerShortcut.cs:51-59` calls
    `DontDestroyOnLoad(gameObject)` in `Awake()` with a singleton dedup guard. v5
    uses the same pattern (`Awake()` + `if (instance != null) { Destroy(gameObject);
    return; }` + `DontDestroyOnLoad(gameObject)`).
  - CRITICAL (layer mechanism): v3's `set.vectorLine.layer = targetLayer` is wrong.
    Stock `OrbitRendererBase.MakeLine` (`OrbitRendererBase.cs:406`) uses
    `l.rectTransform.gameObject.layer = layerMask`. The Vectrosity layer is on
    the underlying GameObject, accessed via `rectTransform`.
  - CRITICAL (cleanup): v3's `Clear()` was unspecified. Stock uses
    `VectorLine.Destroy(ref orbitLine)` (verified). v4 calls it on each cached entry
    before dropping the dict, else the Vectrosity GameObjects leak.
  - MAJOR (DottedLinesMaterial): closes Open Question 2. `MapView.DottedLinesMaterial`
    is a public static property on stock MapView -- dashed style is just material
    swap, no `MakeRoundedDottedLine` needed. v3's "if available, use; else solid"
    is obsolete.
  - MAJOR (downsample endpoint preservation): v3's "every (N/200)-th point" drops
    the last point when `N % step != 1`. v4 specs: keep first + last, uniform-stride
    pick (200-2) interior points.
  - MAJOR (content hash too coarse): a supersede-time TrackSection re-cut can preserve
    `(Points.Count, OrbitSegments.Count, TrackSections.Count, EndUT)`. v4 either XORs
    sample UTs (cheap) or routes through a per-Recording mutation counter exposed by
    `RecordingStore.MutateRecording`. Choose XOR for v1.
  - MAJOR (Driver LateUpdate filter): v3 unconditionally walked the cache. v4
    Driver.LateUpdate applies the SAME filter `DrawAtmosphericMarkers` uses
    (`CachedTrackingStationSuppressedIds` + `cachedLoopUnits` via
    `ResolveTrackingStationSampleUT` -> `renderHidden`).
  - MAJOR (commit 2 LineType): §4 contradicted §2 (Discrete vs Continuous). Fixed.
  - MAJOR (cost ratio): at 200-point cap, ratio is ~7x orbit-arc cost, not 17x.
  - MINOR (material alpha): mutating the shared `MapView.OrbitLinesMaterial` color
    dims every orbit line in the scene. v4 instantiates
    `new Material(MapView.OrbitLinesMaterial)` per VectorLine OR uses
    `VectorLine.SetColor` (per-line overlay, no material clone).
  - VERIFIED FROM KSP DISASSEMBLY:
    - `LineType.Continuous` confirmed (Vectrosity enum, 3 values).
    - `OrbitRendererBase.MakeLine` stock pattern: width = 5f, sets `texture` +
      `material` + `_FadeStrength` / `_FadeSign` shader floats, `continuousTexture
      = true`, `color = GetOrbitColour()`, layer via rectTransform.gameObject.layer,
      `UpdateImmediate = true`.
    - `MapView.OrbitLinesMaterial` is `public static Material` returning
      `fetch.orbitLinesMaterial`.
    - `MapView.DottedLinesMaterial` exists similarly.
    - `MapView.MapIsEnabled` is `public static bool { get; private set; }`.
    - `MapView.Draw3DLines` is `public static bool => fetch.draw3Dlines`.
    - `CelestialBody.position` is `Vector3d` (world coordinates per docstring).
    - `CelestialBody.GetRelSurfacePosition(double lat, double lon, double alt)`
      returns `Vector3d` (body-LOCAL, with overloads for normal and worldPos).
    - `CelestialBody.name => bodyName` (aliased; codebase convention uses `.name`).
    - `ScaledSpace.LocalToScaledSpace(Vector3d)` returns `Vector3d`; also has a
      `(Vector3d[], List<Vector3>)` overload (zero-alloc, matches `OrbitLine.points3`
      pattern).
    - `KSPAddon(Startup, bool once)`; `Startup.Instantly = -2`; doc-string says
      "call DontDestroyOnLoad(this) in your Start() function".
- 2026-05-28 v3 folded clean-context Opus review (2 CRITICAL + 5 MAJOR + 3 MINOR):
  - C1: replaced `KSPAddon.Startup.MainMenu` (which is wrong; codebase uses
    per-scene `Startup.Flight` / `Startup.TrackingStation` OR
    `Startup.Instantly` + `DontDestroyOnLoad`) with the
    `Instantly` + `DontDestroyOnLoad` pattern that `TestRunnerShortcut` uses.
  - C2: dropped snap-to-arc for v1. Mutating `bodyLocalPoints[M-1]` to the
    arc's back-projected world position violates the cache invariant; the
    sub-meter recorded gap is invisible at map zoom anyway.
  - M1: cost prose tightened (2 polyline ghosts vs 2 orbit ghosts; ratio
    consistent at parity; scales by leg count + point density).
  - M2: switched to `LineType.Continuous` with `drawStart`/`drawEnd` ranges
    per leg (matching the orbit-arc pattern at `GhostOrbitLinePatch.cs:427`).
    Avoids the 2x point-array inflation under `LineType.Discrete`.
  - M3: dropped scene-change `Clear()`. Single MonoBehaviour survives TS
    <-> Flight transitions via `DontDestroyOnLoad`; cache persists.
  - M4: `contentVersion` derived from cheap hash of `(Points.Count,
    OrbitSegments.Count, TrackSections.Count, EndUT)` recomputed per refresh
    tick. Avoids touching the save schema.
  - M5: `Recording.Points` outside-section fallback grouped by `bodyName`
    before leg construction (handles SOI crossings in the flat-Absolute
    fallback).
  - MINOR: `body.name` over `body.bodyName` for consistency with existing
    codebase usage; `ParsekLog.VerboseRateLimited` warn on missing body;
    setting toggle OFF calls `Clear()` to release cache memory; hard cap
    `MaxPolylinePointsPerRecording = 5000` (downsample uniformly above).
- 2026-05-28 v2 folded review:
  - CRITICAL: `TryResolveRecordingWorldPosition` location corrected
    (`ParsekTrackingStation.cs:1135`, NOT `TrajectoryMath`).
  - CRITICAL: per-point algorithm rewritten -- the existing resolver is
    single-UT bracket+clamp, and routing RELATIVE-section points through it
    silently switches to `bodyFixedFrames`. Polyline builder now walks
    `TrackSection`s and picks the source list per section's `referenceFrame`.
  - CRITICAL: cache representation switched from scaled-space `Vector3` to
    body-local `Vector3d`; per-frame ScaledSpace transform stays hot.
  - CRITICAL: cache invalidation rule rewritten -- loop epoch shift is NOT
    the right key; real invalidation = recording mutates / removed.
  - MAJOR: per-vessel `GhostOrbitLinePatch.Postfix` hookup replaced with
    scene-wide MonoBehaviour `LateUpdate` (atmospheric-only ghosts have no
    ProtoVessel; per-vessel patches never fire for them).
  - MAJOR: flight-scene hookup added symmetric to TS.
  - MAJOR: `MapView.OrbitLinesMaterial` Pascal-case + access path
    (decompilation doc line 721).
  - MAJOR: cache key changed from `uint pid` to `string RecordingId`
    (atmospheric-only recordings have pid=0).
  - MAJOR: cost analysis redone -- ~6x orbit-arc cost per active leg-set,
    not "+50%".
  - MINOR: layer 24/31 flip pinned to manual per-frame setter.

References:
- `docs/dev/todo-and-known-bugs.md` line 58 -- deferred item A
- `Source/Parsek/Patches/GhostOrbitLinePatch.cs`:
  - `GhostOrbitArcPatch.UpdateSpline` Prefix (lines 382-459) -- existing
    180-vert Vectrosity arc-clip pattern; reuses `OrbitLine.points3` /
    `drawStart` / `drawEnd`; this is the primitive surface the polyline
    builds on.
- `Source/Parsek/ParsekTrackingStation.cs`:
  - line 1135 `TryResolveRecordingWorldPosition` -- single-UT resolver
  - line 1196 `TrySelectTrackingStationFocusFrames` -- the per-UT list-picker
    (Absolute frames / Relative bodyFixedFrames / Recording.Points)
  - line 281-402 `DrawAtmosphericMarkers` -- the OnGUI marker pass
    (unchanged; polyline complements it)
- `Source/Parsek/TrackSection.cs`:
  - line 53 `referenceFrame` (`Absolute` / `Relative`)
  - line 58 `frames` (Absolute payload)
  - line 59 `bodyFixedFrames` (Relative payload)
- `Source/Parsek/MapMarkerRenderer.cs:98` -- existing string-keyed
  cache (`stickyMarkers`)
- `Source/Parsek/ParsekPlaybackPolicy.cs`:
  - line 1308 `CheckPendingMapVessels` -- flight-scene per-tick map driver
  - line 952 `MapOrbitUpdateIntervalSec` -- 0.5s rate limit
- `Source/Parsek/GhostMapPresence.cs`:
  - `RefreshTrackingStationGhosts` around lines 5476-5770 -- TS-scene per-tick
    driver (LifecycleCheckIntervalSec = 2.0s in `ParsekTrackingStation.cs:30`)
- `docs/dev/done/research/ksp-map-presence-api-decompilation.md`:
  - line 295 `MakeLine ... Uses MapView.OrbitLinesMaterial` (the static
    Pascal-case symbol; NOT `MapView.fetch.orbitLinesMaterial`)
  - line 721 `public static Material OrbitLinesMaterial`
  - line 297 -- `OrbitRendererBase.LateUpdate` flips layer per
    `MapView.Draw3DLines` (the polyline needs to do the same flip itself).
- `docs/dev/done/research/ghost-orbits-trajectories-investigation.md` -- the
  original investigation; this plan ships a hybrid (Approach 1 ProtoVessel +
  OrbitRenderer for orbital + new "Approach 1.5" Vectrosity-direct for
  non-orbital).
- `CLAUDE.md` Rotation/world-frame block -- RELATIVE-section
  `point.latitude/longitude/altitude` are metre offsets, NOT lat/lon/alt;
  raw reads are forbidden; must use `bodyFixedFrames` for Relative sections.
- Architectural rule from April 2026 reverts (`94dc8d0f` ... `30d62366` ...
  `07bf3a67`): non-orbital map rendering must NOT route through OrbitDriver
  state-vector roundtrip (bug #172). The polyline stays Vectrosity-direct;
  no new ProtoVessel.

---

## 0. The problem (recap)

Today the map view (flight + TS) renders ghosts as:

1. **Orbital phases** -- KSP's native `OrbitRenderer` line, arc-clipped to the
   recorded OrbitSegment by `GhostOrbitArcPatch.UpdateSpline` Prefix (180
   Vectrosity points per ghost per frame).
2. **Non-orbital phases** -- a single OnGUI icon per recording at the playback
   head's current world position, via `MapMarkerRenderer.DrawMarker`. No
   connecting line.

The visual gap between a leg's orbit-arc end and the next leg's atmospheric
marker has no bridge. The deferred-item text in
`todo-and-known-bugs.md` line 58 reads: *"the real 'sync' fix is a NEW map-view
ghost trajectory POLYLINE drawn through the recorded points for non-orbital
phases, bridging the arcs"*.

### 0.1 What "right" looks like

A static (per loop cycle) polyline through every non-orbital phase of every
active ghost, drawn from the recording's actual trajectory points, in the
same scaled-space the orbit lines use, with the same material
(`MapView.OrbitLinesMaterial`). End-to-end visual continuity: the polyline's
last point lands on the next OrbitSegment's first point (within sub-meter
precision). Atmospheric markers continue to draw the current playback head;
the polyline draws the full path.

---

## 1. The architectural constraints

### 1.1 Scenes in scope (v1)

**Tracking Station and Flight only for v1.** KSC map view is out of scope. The
Driver MonoBehaviour is still DDOL (single instance for the AppDomain) so the
cache persists across scene transitions, but its `LateUpdate` returns early in
any scene other than TRACKSTATION or FLIGHT. Rationale: KSC map-view rendering
would require hooking a third per-scene controller with no scenario coverage
today, and the deferred-item the polyline addresses is bridging atmospheric
phases for ghosts under map view (TS and Flight both have map view). KSC
support is a follow-up.

### 1.1.1 Three lines from the April 2026 reverts

From the failed atmospheric-icon experiments (`94dc8d0f` -> `30d62366`, two
reverts within hours):

1. **NO `OrbitDriver` / Keplerian roundtrip for non-orbital rendering.** Bug
   #172. The architectural rule: orbital -> ProtoVessel + native OrbitRenderer
   (Keplerian, no roundtrip); non-orbital -> direct from trajectory points.
2. **NO new ProtoVessel for the polyline.** The existing ghost ProtoVessel is
   for orbital phase only; a second ProtoVessel per ghost complicates
   `ghostMapVesselPids`, OrbitRenderer lifecycle, and scene-transition cleanup.
3. **Reuse KSP rendering primitives where possible.** `MapView.OrbitLinesMaterial`
   for visual consistency; Vectrosity `VectorLine` for the mesh; layer 24 (3D)
   or 31 (2D) per `MapView.Draw3DLines`.

### 1.2 Cost ceiling

The existing per-frame cost for an orbital ghost is ~180 vector ops + 180
ScaledSpace transforms per ghost in `GhostOrbitArcPatch.UpdateSpline` Prefix.
For an atmospheric leg with M recorded points (~200-1000 in practice -- the
sample rate is 0.1-3s per `ParsekSettings`), the analogous work is M
`body.GetWorldSurfacePosition` calls + M `ScaledSpace.LocalToScaledSpace`
transforms PER FRAME. Cache representation has to factor this:

- **Body-local `Vector3d[M]` per leg** (built ONCE per recording at
  cache-refresh time): the LAT/LON/ALT triples from the recorded
  `TrajectoryPoint`s are body-local; for Absolute sections we precompute
  `body.GetRelSurfacePosition(lat, lon, alt)` (body-local from spherical
  body-fixed coordinates) once and store the Vector3d. This is allocation-
  free per frame.
- **Per-frame hot path:** for each leg, M `body.position + cached_relative[i]`
  to get world position (one Vector3d add per point), then
  `ScaledSpace.LocalToScaledSpace` per point (one multiply-add per point),
  then submit to Vectrosity. Total per leg per frame: ~2M vector ops.

Worked case (full numbers in §3): 2 active ghosts x 3 non-orbital legs x
the `MaxPolylinePointsPerLeg = 200` cap (settled per OQ#1) yields ~2400
vector ops/frame + ~6 Vectrosity submits, against ~360 ops + 2 submits for
the existing 2-ghost orbital baseline -- roughly ~7x the orbit-arc CPU
cost. The uncapped worst case at ~500 points/leg would be ~6000 ops / ~17x
the baseline; the 200-point cap is what keeps the ratio in the ~7x range.
~14 ms/sec extra CPU at 60 fps (~1.4% of one core); cheap.

**Cap mechanism:** downsample at cache build time per §1.3 (keep first +
last, uniform-stride interior pick). Closed, not an open question.

### 1.3 RELATIVE-frame source-list policy (CRITICAL #2 from v1 review)

The reviewer caught a footgun: the existing
`TryResolveRecordingWorldPosition` is SINGLE-UT and routes RELATIVE sections
to `bodyFixedFrames` (a different point list with different per-point UTs
and a different population). Calling it M times for M `Recording.Points`
entries does NOT walk the recording -- it walks whatever list
`TrySelectTrackingStationFocusFrames` picks at each UT, which can FLIP
between section types between calls.

The polyline builder must walk `TrackSection`s explicitly. Per-section
policy:

- **`section.referenceFrame == Absolute`:** use `section.frames` (the
  per-section per-frame body-fixed `TrajectoryPoint` list). Each entry's
  `(latitude, longitude, altitude)` is real body-fixed spherical -- pass to
  `body.GetRelSurfacePosition` and cache the body-local `Vector3d`.

- **`section.referenceFrame == Relative` with non-null `bodyFixedFrames`:**
  use `section.bodyFixedFrames` (the body-fixed-coordinate primary surface
  per `CLAUDE.md`). Same body-fixed lat/lon/alt -> body-local Vector3d
  conversion.

- **`section.referenceFrame == Relative` with null or under-covering
  `bodyFixedFrames`:** SKIP this leg. Do NOT silently fall through to
  `Recording.Points` -- in RELATIVE sections those fields are anchor-local
  metre offsets along x/y/z, NOT lat/lon/alt. Reading them as lat/lon/alt
  produces a position deep inside the planet (the documented footgun in
  CLAUDE.md). Manual playtest will surface the missing leg; we extend the
  policy in a follow-up if it becomes user-visible (a parent-anchored
  rendezvous Relative section without body-fixed coverage is rare).

- **Outside any `TrackSection`** (typically: between sections, or before the
  first / after the last section's UT range; `Recording.Points` covers
  these as flat Absolute fallback): use `Recording.Points` directly. Per
  CLAUDE.md the flat `Recording.Points` list IS Absolute body-fixed when
  read outside any RELATIVE-section UT range. Cache as body-local Vector3d.

The leg-construction algorithm:

```csharp
internal static List<LegPolyline> BuildLegsForRecording(Recording rec)
{
    var legs = new List<LegPolyline>();
    // 1. Compute the union of all OrbitSegment [startUT, endUT] -- these are
    //    "orbital cover" intervals where the polyline is NOT drawn (the
    //    OrbitRenderer arc handles those).
    var orbitalIntervals = ComputeOrbitalCoverIntervals(rec.OrbitSegments);
    // 2. Walk TrackSections in order. For each section's [startUT, endUT],
    //    pick the source list per §1.3 policy, then walk its points filtering
    //    out any whose UT falls inside an orbitalInterval (these are
    //    represented by the arc; the polyline doesn't duplicate them).
    foreach (var section in rec.TrackSections)
    {
        var sourceList = ResolveSourceListForSection(rec, section);
        if (sourceList == null) continue; // RELATIVE without bodyFixedFrames
        var legPoints = sourceList.Where(p =>
            p.ut >= section.startUT && p.ut <= section.endUT
            && !orbitalIntervals.Contains(p.ut)).ToList();
        if (legPoints.Count < 2) continue;
        legs.Add(BuildLegFromBodyFixedPoints(legPoints, /* body */));
    }
    // 3. Walk Recording.Points entries OUTSIDE every section's [startUT, endUT]
    //    range (the pre-first-section and post-last-section flat-Absolute
    //    fallback). The flat Recording.Points list CAN span multiple bodies
    //    if the recording crossed an SOI without being inside a TrackSection
    //    at the crossing; group by consecutive same-body runs so each leg
    //    has exactly one body.
    var outsidePoints = rec.Points.Where(p =>
        !IsInsideAnySection(p.ut, rec.TrackSections)
        && !orbitalIntervals.Contains(p.ut)).ToList();
    foreach (var bodyRun in GroupConsecutiveByBody(outsidePoints))
    {
        if (bodyRun.Count >= 2)
            legs.Add(BuildLegFromBodyFixedPoints(bodyRun, bodyRun[0].bodyName));
    }
    return legs;
}
```

`BuildLegFromBodyFixedPoints` is pure: takes a list of `TrajectoryPoint`
guaranteed body-fixed-spherical AND single-body, produces a `LegPolyline`
containing the body-local `Vector3d[M]`, the body name, the leg's start/end
UTs, and a pre-allocated `Vector3[M]` scratch buffer for the per-frame
ScaledSpace output. Per-section body is `section.frames[0].bodyName` (or
`section.bodyFixedFrames[0].bodyName` for the Relative path); a section's
sample list is assumed body-coherent by recorder invariant.

`GroupConsecutiveByBody` is pure: walks the input list, emits a new sublist
on every `bodyName` change. Hard-cap each emitted sublist at
`MaxPolylinePointsPerLeg = 200`: keep the first and last points unchanged
(the visible endpoints anchor to the recorded leg start and end), then
uniform-stride pick (200 - 2) interior points from the (N - 2) interior
candidates so the leg is exactly 200 samples with first / last preserved.
Even with snap-to-arc dropped in v1 (§2.4), the visible endpoints still need
to be the recorded endpoints so the polyline visually meets the adjacent
arc and the playback-head marker at the leg start.

### 1.4 Cache invalidation (CRITICAL #4 from review)

The loop epoch shift is NOT a meaningful cache key. The body-local Vector3d
positions don't depend on the shift at all (they're recording-time body-
fixed coordinates). The shift only affects which UT the playback head is at,
i.e., WHERE on the polyline the marker draws -- but the polyline itself is
static.

Real invalidation triggers:

1. Recording's `Points` / `OrbitSegments` / `TrackSections` lists mutate
   (supersede commit, schema migration, recording load). Detected by
   computing a cheap content hash on every refresh tick and comparing
   against the cached value. v3 used
   `(Points.Count, OrbitSegments.Count, TrackSections.Count, EndUT)`; the
   reviewer caught that a supersede-time TrackSection re-cut can preserve
   all four. v4 strengthens to XOR each sample's UT (still cheap, catches
   in-place rewrites that preserve counts):

   ```csharp
   long hash = 0;
   hash ^= rec.Points.Count;
   hash ^= (long)rec.OrbitSegments.Count << 16;
   hash ^= (long)rec.TrackSections.Count << 32;
   hash ^= BitConverter.DoubleToInt64Bits(rec.EndUT);
   foreach (var p in rec.Points)
       hash ^= BitConverter.DoubleToInt64Bits(p.ut);
   foreach (var s in rec.TrackSections)
       hash ^= BitConverter.DoubleToInt64Bits(s.startUT)
             ^ BitConverter.DoubleToInt64Bits(s.endUT);
   ```

   Avoids touching the recording schema with a new field.
2. Recording is removed (chain handoff, supersede, delete).
3. `useGhostTrajectoryPolyline` setting toggle: ON -> rebuild lazily on next
   refresh; OFF -> the setter calls `Clear()`, which iterates the cache and
   calls `VectorLine.Destroy(ref set.vectorLine)` on each entry before
   dropping the dict (§2.1). Without the per-entry `Destroy` the Vectrosity
   GameObjects leak; toggling OFF must release them.
4. **Save / load round-trip (MAJOR-3).** `RecordingStore.ClearCommitted()`
   runs in `ParsekScenario.OnLoad`, so a new save's recordings can collide
   on `RecordingId` with the previous save's cached entries. The
   `XOR-of-UTs` hash is byte-stable across a load round-trip (same UTs
   produce the same hash), so the content-hash gate alone does not
   invalidate. The Driver subscribes to `GameEvents.onGameStateLoad` (and
   `GameEvents.onGameStateLoaded` if the host KSP version exposes a
   post-load companion) and calls `Clear()` from the handler. This drops
   every entry + `Destroy`s the underlying Vectrosity GameObjects before
   any cross-save same-id collision can hit the stale cache. The §6
   compatibility section notes the expected outcome (no stale geometry
   after a load).

The cache lives across loop cycles. A loop wrap does NOT invalidate it.

The cache also lives ACROSS SCENE TRANSITIONS (TS <-> Flight; KSC is out
of scope for v1, §1.1). The Driver MonoBehaviour uses `DontDestroyOnLoad`
(see §2.2) so the cache persists across scene loads within the same save;
the user expects to enter TS, scrub the loop, exit to Flight, and see the
same polylines without a rebuild cost.

---

## 2. Design

### 2.1 New file: `Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs`

The file MUST start with a file-level `[ERS-exempt]` comment plus a one-line
rationale, because the Driver's `LateUpdate` walks
`RecordingStore.CommittedRecordings` raw (§2.2). Without the exemption marker
+ the corresponding allowlist entry, `GrepAuditTests` (`scripts/grep-audit-ers-els.ps1`)
fails the CI build. Example file header:

```csharp
// [ERS-exempt] Non-orbital map polyline renderer reads
// RecordingStore.CommittedRecordings raw to draw physical-visibility map
// geometry for atmospheric-only recordings (no ledger / ERS routing
// applies; same physical-visibility scope as DrawAtmosphericMarkers).
```

Static class hosting:

- The per-recording polyline cache.
- A scene-wide MonoBehaviour Driver (singleton, registered via
  `KSPAddon(KSPAddon.Startup.Instantly, true /* once */) + DontDestroyOnLoad`
  on `gameObject` with a singleton dedup guard, matching the only existing
  repo precedent at `TestRunnerShortcut.cs:51-59`). Its `LateUpdate` submits
  cached lines per frame, no-op gated by `MapView.MapIsEnabled` so the
  per-frame cost is zero when the map isn't shown, plus a scene gate (§1.1)
  so it is a no-op outside TRACKSTATION / FLIGHT.
- `GameEvents.onGameStateLoad` (and the post-load companion event the host
  KSP version exposes) subscription on the Driver calls `Clear()` so that a
  load wipes stale Vectrosity GameObjects and stale `bodyLocalPoints`
  before any new save's recordings start hitting the cache (cross-save
  RecordingId collisions otherwise produce stale geometry; see §1.4 / §6).

```csharp
// [ERS-exempt] Non-orbital map polyline renderer reads
// RecordingStore.CommittedRecordings raw to draw physical-visibility map
// geometry for atmospheric-only recordings (no ledger / ERS routing
// applies; same physical-visibility scope as DrawAtmosphericMarkers).
internal static class GhostTrajectoryPolylineRenderer
{
    // RecordingId -> per-recording leg set. Atmospheric-only recordings have
    // pid=0 so PID is NOT a usable key; RecordingId (string) is. Matches the
    // pattern MapMarkerRenderer.stickyMarkers uses (`MapMarkerRenderer.cs:98`).
    private static readonly Dictionary<string, LegPolylineSet> polylineCache;

    internal struct LegPolyline
    {
        public Vector3d[] bodyLocalPoints;  // M points, body-LOCAL Vector3d
        public Vector3[] scratchScaledSpace; // M-element scratch buffer for
                                             // per-frame world+ScaledSpace
                                             // computation (zero-alloc hot path)
        public string bodyName;             // for live-body lookup per frame
        public double startUT;              // leg's first point's recorded UT
        public double endUT;                // leg's last point's recorded UT
    }

    internal struct LegPolylineSet
    {
        public LegPolyline[] legs;
        // ONE VectorLine per recording. Uses LineType.Continuous (NOT Discrete --
        // Discrete needs 2 entries per line-segment which would double the
        // points3 size). Legs are drawn as separate visible ranges via
        // `drawStart`/`drawEnd` per leg per frame (one Draw3D call per leg per
        // recording, sharing the same VectorLine object + material). Same
        // pattern GhostOrbitArcPatch.UpdateSpline uses at
        // `GhostOrbitLinePatch.cs:427` (line.drawStart = 0; line.drawEnd = ...).
        public Vectrosity.VectorLine vectorLine;
        // Computed hash from (Points.Count, OrbitSegments.Count,
        // TrackSections.Count, EndUT) at cache build time. Recomputed cheaply
        // every refresh tick; mismatch triggers rebuild. No Recording schema
        // change required.
        public long contentHash;
    }

    // Refreshes the cache for one recording: recomputes the content hash
    // (per §1.4), rebuilds bodyLocalPoints + the recording's VectorLine if
    // the hash changed, no-op otherwise. Called from the Driver's per-frame
    // walk over RecordingStore.CommittedRecordings (see §2.2). Not wired
    // into RefreshTrackingStationGhosts / CheckPendingMapVessels -- those
    // iterators don't cover atmospheric-only recordings.
    internal static void RefreshForRecording(Recording rec);

    // Called from RemoveGhostVesselForRecording + chain-removal paths.
    // Calls VectorLine.Destroy(ref set.vectorLine) on the cached entry
    // BEFORE dropping the dict entry; otherwise the Vectrosity GameObject
    // leaks.
    internal static void ReleaseForRecording(string recordingId);

    // Called from RemoveAllGhostVessels + the useGhostTrajectoryPolyline
    // setting toggle OFF path. Iterates every cached entry, calls
    // VectorLine.Destroy(ref set.vectorLine) per entry, then clears the
    // dict. Without the per-entry Destroy the Vectrosity GameObjects leak.
    internal static void Clear();

    // Scene-wide MonoBehaviour entry point. Single instance, lives across
    // scene transitions via DontDestroyOnLoad.
    [KSPAddon(KSPAddon.Startup.Instantly, true /* once */)]
    internal sealed class Driver : MonoBehaviour
    {
        private static Driver instance;

        // Matches the only existing repo precedent for KSPAddon-DDOL
        // (`TestRunnerShortcut.cs:51-59`): Awake-time DontDestroyOnLoad on
        // gameObject with a singleton dedup guard. v4 had recommended
        // Start() based on a misread of the KSPAddon doc-string; v5
        // retracts that and follows the repo pattern.
        private void Awake()
        {
            if (instance != null)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);

            // Cross-save guard: ClearCommitted() runs in
            // ParsekScenario.OnLoad; a same-RecordingId after load could
            // otherwise hit the stale cache with destroyed Vectrosity
            // GameObjects. Subscribe to onGameStateLoad (and the
            // post-load event the host KSP version exposes -- e.g.
            // onGameStateLoaded if available) so the cache flushes
            // across save/load. See §1.4 / §6 / MAJOR-3.
            GameEvents.onGameStateLoad.Add(OnGameStateLoad);
        }

        private void OnDestroy()
        {
            if (instance == this) instance = null;
            GameEvents.onGameStateLoad.Remove(OnGameStateLoad);
        }

        private void OnGameStateLoad(ConfigNode node)
        {
            // Drop every cached entry + Destroy the underlying Vectrosity
            // GameObjects so a same-RecordingId in the next-loaded save
            // does not draw with stale per-body Vector3d arrays.
            Clear();
        }

        private void LateUpdate()
        {
            // Scene gate -- v1 ships TRACKSTATION + FLIGHT only (§1.1).
            var scene = HighLogic.LoadedScene;
            if (scene != GameScenes.TRACKSTATION && scene != GameScenes.FLIGHT)
                return;

            if (!MapView.MapIsEnabled) return;
            if (!ParsekSettings.Current.useGhostTrajectoryPolyline) return;

            // Pull the per-frame filter inputs ONCE, outside the loop.
            var suppressed = GhostMapPresence.CachedTrackingStationSuppressedIds;
            int targetLayer = MapView.Draw3DLines ? 24 : 31;
            double currentUT = Planetarium.GetUniversalTime();

            // Resolve cachedLoopUnits per-scene. The underlying field is a
            // private per-scene instance member on three different
            // MonoBehaviours (`ParsekTrackingStation.cs:70`,
            // `ParsekFlight.cs:875`, `ParsekKSC.cs:56`) and is rebuilt per
            // scene; the DDOL singleton Driver has no direct handle to it.
            // v6 detects the current scene and reads the appropriate
            // controller's value via the commit-2-introduced internal
            // accessor `CurrentCachedLoopUnits` (the field itself stays
            // private; only the renderer reads via the accessor). If the
            // per-scene controller has not yet spun up (transitional frames
            // before its Awake), we DEFER the draw -- LoopUnitSet Empty
            // would defeat the renderHidden filter and is therefore not
            // safe to substitute as a real fallback. We log once per scene
            // at Verbose and return.
            LoopUnitSet loopUnits;
            if (scene == GameScenes.TRACKSTATION)
            {
                var tsCtl = FindObjectOfType<ParsekTrackingStation>();
                if (tsCtl == null)
                {
                    ParsekLog.VerboseRateLimited("GhostMap",
                        "polyline-defer-ts-controller",
                        "Deferring polyline draw: ParsekTrackingStation not yet awake.",
                        5.0);
                    return;
                }
                loopUnits = tsCtl.CurrentCachedLoopUnits;
            }
            else // FLIGHT
            {
                var flCtl = FindObjectOfType<ParsekFlight>();
                if (flCtl == null)
                {
                    ParsekLog.VerboseRateLimited("GhostMap",
                        "polyline-defer-flight-controller",
                        "Deferring polyline draw: ParsekFlight not yet awake.",
                        5.0);
                    return;
                }
                loopUnits = flCtl.CurrentCachedLoopUnits;
            }

            // Driver walks RecordingStore.CommittedRecordings directly
            // (atmospheric-only recordings are absent from the ghost /
            // pending-create iterators; see §2.2).
            var committed = RecordingStore.CommittedRecordings;
            for (int recordingIndex = 0; recordingIndex < committed.Count; recordingIndex++)
            {
                var rec = committed[recordingIndex];
                if (rec == null) continue;
                if (suppressed != null && suppressed.Contains(rec.RecordingId))
                    continue;

                // Resolve the effective playback UT via the same helper
                // DrawAtmosphericMarkers uses. Full signature (from
                // `GhostPlaybackLogic.cs:6986`):
                //   ResolveTrackingStationSampleUT(
                //       int recordingIndex, double startUT, double endUT,
                //       double currentUT, LoopUnitSet loopUnits,
                //       out bool renderHidden)
                double effUT = GhostPlaybackLogic.ResolveTrackingStationSampleUT(
                    recordingIndex,
                    rec.StartUT,
                    rec.EndUT,
                    currentUT,
                    loopUnits,
                    out bool renderHidden);
                if (renderHidden) continue;

                // Same skip classifications DrawAtmosphericMarkers uses.
                // Real signature (from `ParsekTrackingStation.cs:475-499`):
                //   AtmosphericMarkerSkipReason ClassifyAtmosphericMarkerSkip(
                //       Recording rec, int recordingIndex, double currentUT,
                //       HashSet<string> suppressedIds)
                // This call also drags in the additional filters
                // ClassifyAtmosphericMarkerSkip applies internally:
                // IsDebris filter, native-icon filter, OrbitSegmentActive
                // (skip if a stock orbit line is already covering the UT),
                // plus the no-committed-trajectory / missing-body checks.
                if (ClassifyAtmosphericMarkerSkip(rec, recordingIndex, effUT, suppressed)
                    != AtmosphericMarkerSkipReason.None)
                    continue;

                // Cheap content-hash gate: rebuild only if the recording's
                // content actually changed.
                RefreshForRecording(rec);

                if (!polylineCache.TryGetValue(rec.RecordingId, out var set))
                    continue;
                if (set.vectorLine == null || set.legs == null || set.legs.Length == 0)
                    continue;

                // Layer flip per MapView.Draw3DLines. Stock's
                // OrbitRendererBase.MakeLine does
                // `l.rectTransform.gameObject.layer = layerMask` (verified
                // KSP disassembly); we mirror that contract.
                set.vectorLine.rectTransform.gameObject.layer = targetLayer;

                foreach (var leg in set.legs)
                {
                    CelestialBody body = FlightGlobals.Bodies?
                        .Find(b => b.name == leg.bodyName);  // body.name (m1)
                    if (body == null)
                    {
                        ParsekLog.VerboseRateLimited("GhostMap",
                            "polyline-body-missing-" + leg.bodyName,
                            "Polyline body missing for leg, skipping: " + leg.bodyName,
                            5.0);
                        continue;
                    }
                    Vector3d bodyPos = body.position;
                    int M = leg.bodyLocalPoints.Length;
                    for (int i = 0; i < M; i++)
                    {
                        var world = bodyPos + leg.bodyLocalPoints[i];
                        leg.scratchScaledSpace[i] =
                            (Vector3)ScaledSpace.LocalToScaledSpace(world);
                    }
                    // Copy this leg's scratch into the shared VectorLine's
                    // points3 at a stable offset, then submit a Draw3D ranged
                    // to this leg's portion via drawStart/drawEnd.
                    CopyLegIntoVectorLine(set.vectorLine, leg.scratchScaledSpace,
                        leg.pointsStartIdx);
                    set.vectorLine.drawStart = leg.pointsStartIdx;
                    set.vectorLine.drawEnd = leg.pointsStartIdx + M - 1;
                }
            }
        }
    }
}
```

Key design points:

- **One `VectorLine` per recording, multi-leg via `drawStart`/`drawEnd`**
  (NOT per leg, NOT `LineType.Discrete`). Vectrosity's `LineType.Continuous`
  treats `points3` as a single polyline; we PACK all of a recording's legs
  consecutively in the shared `points3` array at stable per-leg offsets
  (`leg.pointsStartIdx`), and per frame submit `drawStart` / `drawEnd` ranged
  to the active leg's slice. One Draw3D per LEG per recording, but the
  VectorLine GameObject + material + GPU mesh are SHARED across the
  recording's legs (the per-leg cost is the points3 copy + the Draw3D
  submit; the mesh upload happens once when any leg's points change).
  Avoids the 2x point-array inflation under `LineType.Discrete`.
- **Pre-allocated `scratchScaledSpace` arrays per leg.** Zero per-frame alloc.
- **`bodyLocalPoints` are recording-time spherical-to-Cartesian conversions
  cached once.** Per-frame work is `body.position + bodyLocal` (one Vector3d
  add per point) + ScaledSpace transform.
- **Scene-wide MonoBehaviour, NOT per-vessel patch.** An atmospheric-only
  recording has NO ProtoVessel; `GhostOrbitLinePatch`'s per-vessel hooks
  never fire for it. The scene-wide LateUpdate runs once per frame regardless
  of ghost count, walks the cache, draws everything.

### 2.2 Hookup site (Driver-walks-CommittedRecordings)

v3 routed cache refresh through `RefreshTrackingStationGhosts`
(`GhostMapPresence.cs:5476`) for TS and `CheckPendingMapVessels`
(`ParsekPlaybackPolicy.cs:1308`) for flight. The clean-context reviewer
caught the fatal flaw: `RefreshTrackingStationGhosts` iterates
`vesselsByRecordingIndex` (ghost-bearing recordings only) and
`CheckPendingMapVessels` iterates `pendingMapVessels` (deferred-create only).
Atmospheric-only recordings -- the entire reason this feature exists -- live
in NEITHER iterator. Wiring there would be a silent no-op for the feature's
target population.

v4 hookup: the Driver MonoBehaviour itself walks
`RecordingStore.CommittedRecordings` per frame in `LateUpdate`, both
refreshing the cache and submitting the lines. This mirrors exactly the same
walk `DrawAtmosphericMarkers` (`ParsekTrackingStation.cs:281-402`, with the
inner per-recording loop starting at `:336`) and `DrawMapMarkers`
(`ParsekUI.cs:1062`) already perform, gated by:

- **Suppression set:** `GhostMapPresence.CachedTrackingStationSuppressedIds`
  (the same HashSet `DrawAtmosphericMarkers` reads). If a recording's id is
  in the set, skip it -- no refresh, no submit.
- **Loop-unit / renderHidden filter:** resolve the per-recording effective
  playback UT via the full
  `GhostPlaybackLogic.ResolveTrackingStationSampleUT(int recordingIndex,
  double startUT, double endUT, double currentUT, LoopUnitSet loopUnits,
  out bool renderHidden)` signature (`GhostPlaybackLogic.cs:6986`). If the
  `renderHidden` out-parameter is true, skip the polyline this frame (the
  marker pass is hiding the recording too).
- **Scene-specific `cachedLoopUnits` source:** the underlying
  `cachedLoopUnits` field is a private per-scene instance field on three
  different MonoBehaviours (`ParsekTrackingStation.cs:70`,
  `ParsekFlight.cs:875`, `ParsekKSC.cs:56`), rebuilt per scene. The DDOL
  Driver has no direct handle to them, so the Driver detects the current
  scene at frame start, looks up the controller via `FindObjectOfType<T>()`,
  and uses the commit-2-introduced internal accessor `CurrentCachedLoopUnits`
  on each scene controller; the underlying field stays private. In scenes
  where the matching per-scene controller has not yet awoken (e.g.
  transitional frames immediately after a scene load, before the controller's
  Awake runs), the polyline pass DEFERS the draw for that frame (no submit)
  rather than substituting `LoopUnitSet.Empty` -- using Empty would defeat
  the `renderHidden` filter entirely and is not a safe fallback. KSC is out
  of scope for v1 (§1.1) so its controller is not consulted.
- **Per-recording skip classifications:** the same
  `ClassifyAtmosphericMarkerSkip` decision tree `DrawAtmosphericMarkers` uses.
  Real signature (from `ParsekTrackingStation.cs:475-499`):
  `AtmosphericMarkerSkipReason ClassifyAtmosphericMarkerSkip(Recording rec,
  int recordingIndex, double currentUT, HashSet<string> suppressedIds)`. The
  caller checks `!= AtmosphericMarkerSkipReason.None` and continues. The
  helper already filters IsDebris, native-icon (recordings rendering through
  a stock icon path), and OrbitSegmentActive (a stock orbit arc covers the
  UT) in addition to the no-committed-trajectory / missing-body checks --
  the polyline inherits all of those filters for free. Reuse the helper
  directly; do not duplicate any of those rules.
- **Content-hash gate (cheap):** compare the recording's current content hash
  (per §1.4) against the cached entry's stored `contentHash`. Equal => reuse
  the cached `bodyLocalPoints`; differ => rebuild via
  `BuildLegsForRecording(rec)`. This means the per-frame walk is O(N
  recordings) for the hash check and O(M points) only for recordings that
  actually changed since last frame.

The Driver MonoBehaviour is registered via
`[KSPAddon(KSPAddon.Startup.Instantly, true)]` and calls
`DontDestroyOnLoad(gameObject)` in `Awake()` with a singleton dedup guard
matching the only existing repo precedent (`TestRunnerShortcut.cs:51-59`).
Single instance for the AppDomain lifetime. The cache lives ACROSS scene
transitions; entering TS, scrubbing the loop, exiting to Flight, and seeing
the same polylines without a rebuild is the expected behavior. KSP scene
load events fire `ParsekScenario.OnLoad` which calls
`RecordingStore.ClearCommitted()`, so the Driver subscribes to
`GameEvents.onGameStateLoad` and calls `Clear()` on entry to drop every
cached entry + Destroy the underlying Vectrosity GameObjects before any
new save's recordings start touching the cache (cross-save RecordingId
collisions otherwise produce stale geometry; see §1.4). The Driver both
refreshes the cache AND submits the lines -- there is no longer a separate
refresh hook in `RefreshTrackingStationGhosts` /
`CheckPendingMapVessels`.

**ERS audit allowlist gap.** `RecordingStore.CommittedRecordings` is the
ledger-routed surface; any raw read outside
`scripts/ers-els-audit-allowlist.txt` fails the `GrepAuditTests` CI gate.
The Driver's `LateUpdate` walks `CommittedRecordings` raw for physical
visibility (no ledger / ERS routing applies to map geometry, same scope as
`DrawAtmosphericMarkers`). The same commit that introduces
`Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs` MUST also:

1. Add the file to `scripts/ers-els-audit-allowlist.txt` with a one-line
   rationale of the form
   `# Phase X -- non-orbital map polyline reads CommittedRecordings for
   physical-visibility rendering (same scope as DrawAtmosphericMarkers);
   no ledger / ERS routing applies.`
2. Place a file-level `[ERS-exempt]` comment as the first lines of the
   new .cs file (see §2.1 example header).

Both are in commit 2 (the same commit that introduces the Driver
MonoBehaviour and the raw walk; commit 1's pure data structs and builder
do not read `CommittedRecordings`).

### 2.3 Visual style

- **Material:** `MapView.OrbitLinesMaterial` (the public static Pascal-case
  field per the decompilation doc line 721; NOT
  `MapView.fetch.orbitLinesMaterial`). Reference-shared with every stock
  orbit line in the scene -- mutating the material's color would dim every
  orbit line. v4 uses `VectorLine.SetColor(...)` for a per-line color
  overlay (Vectrosity applies per-line color independently of the shared
  material, no material clone required). The alternative would be
  `new Material(MapView.OrbitLinesMaterial)` per VectorLine; chosen
  `SetColor` for simplicity.
- **Color:** match the orbit arc's color, slightly dimmed (~60% alpha) via
  `VectorLine.SetColor` so the user can tell the polyline apart from a
  Keplerian arc without dimming stock orbit lines.
- **Style:** dashed is just a material swap -- `MapView.DottedLinesMaterial`
  is a public static stock property (verified KSP disassembly; closes
  OQ#2). Commit 3 swaps `vectorLine.material = MapView.DottedLinesMaterial`
  for the dashed look; commit 2 ships solid via `MapView.OrbitLinesMaterial`.
- **Width:** match orbit-arc width (1-2 px on 1080p).
- **Layer:** 24 in 3D mode, 31 in 2D mode. Flipped manually per frame in the
  Driver MonoBehaviour via
  `set.vectorLine.rectTransform.gameObject.layer = targetLayer` -- the same
  mechanism stock `OrbitRendererBase.MakeLine` uses
  (`l.rectTransform.gameObject.layer = layerMask`). Stock KSP's
  `OrbitRendererBase.LateUpdate` flips its own per decompilation doc line 297;
  we own our VectorLine so we flip ours.

### 2.4 No snap-to-arc in v1

v2 originally proposed mutating the polyline's terminal body-local point to
match the arc's first-point-world. The clean-context reviewer flagged this as
violating the cache invariant: `bodyLocalPoints[i] = body.GetRelSurfacePosition(
recorded_lat, recorded_lon, recorded_alt)` is recorded-frame body-fixed;
overwriting `bodyLocalPoints[M-1]` with a Kepler-derived back-projection is
correct only at cache-build-time UT and drifts from the arc as the body
rotates afterwards.

**Decision for v1: skip snap-to-arc entirely.** The recorded gap between the
last in-leg trajectory point and the OrbitSegment's first point is sub-meter
(both seeded from the same physics frame at adjacent UTs); at map zoom levels
(>= 100 km/pixel for typical orbit views) sub-meter is sub-pixel. If a future
playtest shows a visible gap, the snap can be re-introduced as a per-frame
override `Vector3d? snapToArcWorldOverride` recomputed from the live arc each
frame (NOT baked into the cache). Out of scope for v1.

---

## 3. Per-frame cost analysis (corrected)

Worked case: looped Mun mission, 2 active ghosts, 3 non-orbital legs each,
with `MaxPolylinePointsPerLeg = 200` (the v1-shipped cap; see §1.3 and OQ#1
which is settled).

- Polyline construction: 2 recordings * 3 legs * 200 points * (one
  `body.GetRelSurfacePosition` + one Vector3d store) = 1200 spherical->Cartesian
  conversions, performed ONCE per recording (not per frame). Cost is negligible
  at recording-load time.
- Per-frame rendering: 2 recordings * 3 legs * 200 points * 2 vector ops
  (`body.position + cached` + `ScaledSpace.LocalToScaledSpace`) = ~2400 vector
  ops per frame, plus ~6 `Draw3D()` submits (one per leg, sharing the
  recording's VectorLine via `drawStart`/`drawEnd`).
- Per-frame Vectrosity overhead: 2 mesh uploads (one per recording's shared
  VectorLine, since `points3` is rebuilt), 2 GL/SetPass calls, 2 transform
  pushes.

Existing orbit arc cost for ORBITAL ghosts (a parking-orbit ghost or two):
~180 vector ops + 1 Vectrosity submit per ghost = ~360 ops + 2 submits for
the same 2-ghost session.

**Polyline overhead at 200-point cap: ~2400 ops + ~6 submits per frame, vs
~360 ops + 2 submits for orbits.** Polyline is ~6.7x the orbit-arc CPU cost
in this scenario (~2400 / ~360).

The uncapped worst-case (legs averaging ~500 points each) would be ~6000
ops / ~17x the orbit-arc cost; the 200-point cap is what brings the ratio
down to ~7x. Cap at 200 is the default precisely so the ratio stays in this
range.

At 60 fps and ~100 ns per ScaledSpace transform: ~14 ms/sec extra CPU =
~1.4% of one core at the 200-point cap. Still cheap. At 100x time warp KSP
runs at lower fps (~30) and the cost halves. At 1000x+ warp the map view is
typically zoomed out so far the user wouldn't notice the per-frame cost.

The cost is NOT "free", "negligible", or "+50%" as v1 misstated. At the
200-point cap it's ~7x the existing orbit-arc cost; uncapped at ~500
points/leg it would climb to ~17x. Both stay well within frame budget.

---

## 4. Phase breakdown

Three commits.

**Commit 1: Per-leg polyline data structures + builder + tests.**
- `GhostTrajectoryPolylineRenderer.LegPolyline` / `LegPolylineSet` structs
  (no `VectorLine` field yet -- pure data; the Vectrosity reference is added
  in commit 2 when the Driver introduces `LineType.Continuous` usage).
- `BuildLegsForRecording(Recording)` pure helper + the per-section
  source-list policy from §1.3.
- The body-local-to-scratchScaledSpace conversion helper (pure modulo
  `body.position` lookup).
- Cache dict + `RefreshForRecording` / `ReleaseForRecording` / `Clear` methods.
- `ParsekSettings.useGhostTrajectoryPolyline` (`bool`, default `false`) plus
  the matching read / write entries in `ParsekSettingsPersistence` so the
  toggle persists across sessions, mirroring how `useSmoothingSplines` /
  `useAnchorCorrection` are wired today (`Source/Parsek/ParsekSettings.cs:77-100`).
- xUnit tests for the builder + cache (per §5.1) + persistence round-trip
  test for the new setting.
- No rendering wiring yet. No MonoBehaviour. No Driver. No Vectrosity reference.

**Commit 2: Driver MonoBehaviour + Vectrosity submission + scene hookups.**
- Add `<Reference Include="Vectrosity">` with
  `<HintPath>$(KSPRoot)/KSP_x64_Data/Managed/Vectrosity.dll</HintPath>` to
  `Source/Parsek/Parsek.csproj`. The existing repo does NOT reference
  Vectrosity (the only stock-Vectrosity touchpoint, `GhostOrbitLinePatch.cs`,
  reaches `OrbitLine` / `points3` through the Assembly-CSharp surface and
  never imports `Vectrosity`). The Driver's `Vectrosity.VectorLine` field
  and `VectorLine.Destroy(ref ...)` calls require the type at compile time;
  the DLL is shipped in `KSP_x64_Data/Managed/Vectrosity.dll`. If a data
  struct field added in commit 1 ends up needing the Vectrosity type at
  compile time (it currently does not -- the `vectorLine` field is added
  in commit 2 along with the Driver), move this reference-add to commit 1.
- The Driver MonoBehaviour class with `Awake` (`DontDestroyOnLoad(gameObject)`
  + singleton dedup, matching `TestRunnerShortcut.cs:51-59`), `OnDestroy`
  (unsubscribe + instance clear), `OnGameStateLoad` (calls `Clear()`), and
  `LateUpdate` (scene gate + map gate + per-recording walk).
- Expose internal accessor for `cachedLoopUnits` on `ParsekTrackingStation`
  and `ParsekFlight` so the renderer can read it without making the field
  public. Recommended pattern:
  ```csharp
  // In ParsekTrackingStation.cs and ParsekFlight.cs:
  internal LoopUnitSet CurrentCachedLoopUnits => cachedLoopUnits;
  ```
  Justification: field stays private; only the renderer reads via the
  accessor.
- KSPAddon registration via `[KSPAddon(KSPAddon.Startup.Instantly, true)]`.
- Vectrosity `VectorLine` construction per recording (one
  `LineType.Continuous` line packed with all legs at stable per-leg offsets;
  see §2.1 -- legs are drawn as ranged slices via `drawStart`/`drawEnd`).
- Driver MonoBehaviour walks `RecordingStore.CommittedRecordings` directly
  per frame (atmospheric-only recordings are absent from
  `RefreshTrackingStationGhosts` / `CheckPendingMapVessels`; see §2.2).
- Add `Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs` to
  `scripts/ers-els-audit-allowlist.txt` in the same commit with a one-line
  rationale (`# Phase X -- non-orbital map polyline reads
  CommittedRecordings for physical-visibility rendering, no ledger / ERS
  routing applies`). Place the file-level `[ERS-exempt]` comment as the
  first lines of the new .cs file. Without these two, `GrepAuditTests`
  fails the CI build (see §2.2 ERS audit allowlist gap).
- Driver subscribes to `GameEvents.onGameStateLoad` in `Awake` and calls
  `Clear()` from the handler (see §1.4 / §2.1 for the cross-save
  invalidation rationale).
- Layer flip (24/31) per frame via
  `set.vectorLine.rectTransform.gameObject.layer = targetLayer`.
- `useGhostTrajectoryPolyline` stays default OFF in this commit for
  review-only.
- xUnit integration test for the Draw3D submission via a Vectrosity test
  shim (per §5.2).

**Commit 3: Visual polish + flag flip.**
- Snap-to-arc deferred per §2.4 / OQ#2; not in v1 scope.
- Dashed line style via `vectorLine.material = MapView.DottedLinesMaterial`
  (verified stock API; see §2.3 / §8 verified-disassembly block).
- Flip `useGhostTrajectoryPolyline` default to ON.
- CHANGELOG.md entry under v0.10.0 (or v0.10.x at commit time).
- `docs/dev/todo-and-known-bugs.md` deferred-item-A flipped to "fixed".
- In-game test direct-invoking the cache builder (per §5.3).

Each commit ends with `dotnet test` green. Final commit ends with a
clean-context Opus code review on the full diff (visual rendering code has
many failure modes that unit tests can't fully cover -- per the `CLAUDE.md`
"Code Review Follow-Ups" guidance, a full review is required for new
runtime-only paths).

---

## 5. Test plan

### 5.1 Unit tests (`Source/Parsek.Tests/GhostTrajectoryPolylineBuildTests`)

`[Collection("Sequential")]` test class.

- `BuildLegs_NoTrackSections_OneLegCoveringRecordingPoints` -- pure flat
  `Recording.Points` recording.
- `BuildLegs_SingleAbsoluteSection_OneLegFromSectionFrames` -- one Absolute
  section, no OrbitSegments.
- `BuildLegs_OrbitSegmentOnly_NoLegs` -- one OrbitSegment, no points in
  legs.
- `BuildLegs_AbsoluteSectionThenOrbitSegment_OneLegBeforeOrbit` -- ascent leg.
- `BuildLegs_TwoOrbitSegmentsWithGap_OneLegBetween` -- capture-burn gap.
- `BuildLegs_RelativeSectionWithBodyFixedFrames_UsesBodyFixed` -- assert
  `bodyFixedFrames` is the source list (not `Recording.Points`).
- `BuildLegs_RelativeSectionWithoutBodyFixedFrames_SkipsLeg` -- assert no
  leg produced; the recording's atmospheric marker stays.
- `BuildLegs_RecordingMutates_CacheInvalidates` -- the `contentVersion` bump
  triggers rebuild.

### 5.2 Vectrosity integration tests (`GhostTrajectoryPolylineDrawTests`)

Test shim hooks `VectorLine.Draw3D` so the test captures which lines were
drawn each Driver `LateUpdate`.

- `Driver_LateUpdate_OneRecording_SubmitsOneVectorLine`.
- `Driver_LateUpdate_TwoRecordings_SubmitsTwoVectorLines`.
- `Driver_LateUpdate_MapViewDisabled_SubmitsNothing`.
- `Driver_LayerFlip_FollowsMapView_Draw3DLines`.
- `Driver_RecordingRemoved_StopsSubmittingForIt`.
- `Driver_AfterClear_SubmitsNothing`.

### 5.3 In-game test (`InGameTests/RuntimeTests.cs`)

Direct-invoke pattern per the v1 review (no attempt to drive
`RefreshTrackingStationGhosts` end-to-end; the existing TS span-clock test
at `RuntimeTests.cs:4451` is the precedent for "test the helper in
isolation"):

`TrackSection` is a struct with default-null list fields and a convention
forbids manual init (`Source/Parsek/TrackSection.cs:46-49` doc-string). Tests
must therefore go through `RecordingBuilder` (or whichever helper in
`Tests/Generators/` builds sections via `StartNewTrackSection` + the
recorder's normal append paths) so the section list fields are initialized
the same way the runtime does.

```csharp
[InGameTest(Category = "GhostMap", Scene = GameScenes.TRACKSTATION)]
public IEnumerator GhostTrajectoryPolyline_AbsoluteAscent_BuildsLegThroughPoints()
{
    // Build the recording through the same generator the unit tests use,
    // so TrackSection list fields are initialized via StartNewTrackSection
    // (not manual struct init -- forbidden per TrackSection.cs:46-49).
    var rec = new RecordingBuilder("test-polyline-1")
        .StartAbsoluteSection(startUT: 100, body: "Kerbin")
        .AppendFrame(ut: 100, latitude: -0.1,  longitude: -74.5, altitude: 70)
        .AppendFrame(ut: 200, latitude: -0.05, longitude: -74.5, altitude: 20000)
        .AppendFrame(ut: 600, latitude: 0.0,   longitude: -74.5, altitude: 100000)
        .EndSection(endUT: 600)
        .Build();

    var legs = GhostTrajectoryPolylineRenderer.BuildLegsForRecording(rec);
    InGameAssert.AreEqual(1, legs.Count, "expected one leg from one Absolute section");
    InGameAssert.AreEqual(3, legs[0].bodyLocalPoints.Length, "expected 3 points");
    InGameAssert.AreEqual("Kerbin", legs[0].bodyName);
    yield break;
}
```

If `RecordingBuilder` does not yet expose `StartAbsoluteSection` /
`AppendFrame` / `EndSection` helpers, commit 1 adds them in
`Tests/Generators/` rather than reverting to manual `TrackSection` init in
the test. If a truly unavoidable manual init shows up in a unit test (not
this in-game test), it MUST include an explicit comment
`// deliberately bypasses StartNewTrackSection for unit-test purposes` and
initialize ALL list fields (`frames`, `bodyFixedFrames`, `checkpoints`,
etc.).

### 5.4 Manual playtest

Load the user's "Kerbal X" save. Enter TS. Confirm a dashed line through the
Mun-mission's ascent, descent, return-ascent. Snap-to-arc check: the line's
end visually touches the orbit arc's start with no gap > a pixel. Frame rate
test: ground-truth 60 fps with 2 ghosts; after-fix should hold 55+ fps.

---

## 6. Backward-compatibility / what does NOT change

- OnGUI atmospheric marker pass (`DrawAtmosphericMarkers`, `DrawMapMarkers`,
  `MapMarkerRenderer`). Markers continue to draw at the playback head's
  current position. Polyline draws full path; marker is current position.
- Orbital rendering (ProtoVessel + native OrbitRenderer + `GhostOrbitArcPatch`).
  Byte-identical.
- `ghostMapVesselPids` invariant. No new ProtoVessel.
- `BuildSignature`. The polyline cache is a function of fields the digest
  already covers (`OrbitSegments` + `Points` + `TrackSections` + loop epoch
  shift -- though the last is irrelevant per §1.4).
- Recording schema. No new fields.
- Architectural rule from April 2026 reverts: non-orbital rendering does NOT
  route through OrbitDriver / Keplerian roundtrip.
- The sister B PR (terminal-orbit-line for loop members). Both PRs
  merge-order-independent; no shared code paths.
- KSC map view. KSC is out of scope for v1 (§1.1); the Driver's `LateUpdate`
  early-returns in any scene other than TRACKSTATION or FLIGHT, so KSC
  map-view draws nothing from the polyline. Same as the pre-feature
  baseline.
- `Recording.Points` total density is bounded only by recorder sample-rate,
  not by the renderer. A long survey recording could have 100k+ points
  across all sections, but `MaxPolylinePointsPerLeg = 200` caps each leg's
  contribution. Per-recording allocation is bounded by leg count, not raw
  point count -- the cache's `Vector3d[]` totals (legCount * 200) entries
  per recording. Revisit only if a real recording shows pathological leg
  counts.
- Vectrosity-GameObject survivability across scene transitions. The Driver
  is DDOL, so its parent GameObject survives scene loads, and the
  `VectorLine`s owned by the cache stay attached to the DDOL Driver's
  GameObject hierarchy (no per-scene re-parenting). Within the same save,
  TS <-> Flight transitions reuse the cache. Across save/load the Driver
  calls `Clear()` from the `GameEvents.onGameStateLoad` handler (§1.4
  invalidation rule 4 / §2.1) so cross-save same-`RecordingId` collisions
  cannot read stale per-body geometry. If implementation testing shows
  Vectrosity GameObjects do NOT survive scene transitions natively for any
  reason, the fallback is pessimistic Clear-on-every-scene-change
  (`GameEvents.onSceneSwitchRequested`), at the cost of a per-scene rebuild
  pass; commit 2 confirms survivability before settling on the DDOL-only
  contract.

---

## 7. Open questions to pin before commit 1

1. **Dashed style.** `MapView.DottedLinesMaterial` exists as a public static
   stock property (verified KSP disassembly, 2026-05-28). Commit 3 swaps
   `vectorLine.material = MapView.DottedLinesMaterial` for dashed. Closed.

2. **Snap-to-arc semantics for both edges.** Deferred to a follow-up commit
   / PR; not in v1 scope (§2.4 ships solid endpoints, sub-meter recorded
   gap is sub-pixel at map zoom). Re-open if manual playtest shows a
   visible gap.

3. **Setting placement.** New `useGhostTrajectoryPolyline` setting under
   Settings -> Ghost (where existing ghost-rendering toggles live).

4. **Cross-recording leg connecting two recordings in a chain.** A chain
   handoff at a Mun-takeoff splits one mission into two recordings; the
   atmospheric ascent is in rec[A], the post-handoff coast is in rec[B].
   Should the polyline draw across the handoff boundary, or do two separate
   polylines suffice? Two separate suffices for v1; visual continuity
   across handoff boundaries is a Phase-3 polish item per the original
   investigation doc.

5. **What happens when the user disables ghosts entirely?** The setting
   gate at the Driver's `LateUpdate` returns early. Confirm.

(OQ#6 on `Recording.Points` density was retired in v5 -- already pinned by
the `MaxPolylinePointsPerLeg = 200` cap in §1.3 and noted in §6 "what does
NOT change".)

---

## 8. References

(See also References block at top.)

- `Source/Parsek/Display/GhostTrajectoryPolylineRenderer.cs` -- new file,
  doesn't exist yet
- v1 of this plan (overwritten) -- pinned wrong resolver location,
  scaled-space cache, and per-vessel hookup; v2 supersedes
- Architectural rule established by April 2026 reverts (`94dc8d0f` ->
  `30d62366` -> `07bf3a67`): non-orbital map rendering does NOT route through
  OrbitDriver state-vector roundtrip (bug #172). The polyline stays
  Vectrosity-direct.

### Verified from KSP disassembly (Assembly-CSharp.dll, 2026-05-28)

- `Vectrosity.LineType` enum: `Continuous`, `Discrete`, `Points` (3 values).
- `OrbitRendererBase.MakeLine` stock pattern: `width = 5f`; sets
  `texture`, `material`, `_FadeStrength` / `_FadeSign` shader floats;
  `continuousTexture = true`; `color = GetOrbitColour()`; layer via
  `l.rectTransform.gameObject.layer = layerMask`; `UpdateImmediate = true`.
- `MapView.OrbitLinesMaterial` is `public static Material` returning
  `fetch.orbitLinesMaterial`. Reference-shared with every stock orbit
  line -- mutating its color dims them all.
- `MapView.DottedLinesMaterial` exists similarly as a public static stock
  property; dashed style is just a material reference swap (closes OQ#2).
- `MapView.MapIsEnabled` is `public static bool { get; private set; }`.
- `MapView.Draw3DLines` is `public static bool => fetch.draw3Dlines`.
- `CelestialBody.position` is `Vector3d` (world coordinates per doc-string).
- `CelestialBody.GetRelSurfacePosition(double lat, double lon, double alt)`
  returns `Vector3d` (body-LOCAL, with overloads for normal and worldPos).
- `CelestialBody.name => bodyName` (aliased; codebase convention uses
  `.name`, `bodyName` is the underlying field).
- `ScaledSpace.LocalToScaledSpace(Vector3d)` returns `Vector3d`; also a
  `(Vector3d[], List<Vector3>)` zero-alloc overload (matches the
  `OrbitLine.points3` pattern stock uses).
- `KSPAddon(Startup, bool once)`; `Startup.Instantly = -2`. v4 paraphrased
  the doc-string as requiring `Start()` (not `Awake()`) for
  `DontDestroyOnLoad`. v5 RETRACTS that prescription: the only existing
  repo precedent (`TestRunnerShortcut.cs:51-59`) puts
  `DontDestroyOnLoad(gameObject)` in `Awake()` with a singleton dedup
  guard, has shipped without issue, and is what v5 mandates. Either timing
  is workable for KSPAddons in practice (Unity dispatches both before the
  first scene update); v5 picks Awake purely to match the repo's only
  precedent.
- `VectorLine.Destroy(ref l)` is the stock cleanup pattern (used by
  `OrbitRendererBase.OnDestroy`). Required when releasing cache entries to
  avoid leaking Vectrosity GameObjects.
