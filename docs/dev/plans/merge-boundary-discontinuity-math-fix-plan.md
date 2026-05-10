# Merge boundary discontinuity: anchor-local metres for v6+ Relative sections

> Implementation note: Phase 1a of this plan landed in the
> `fix-merge-boundary-discontinuity-math` PR. Phase 1b gameplay projection
> siblings remain a separate follow-up, as planned.

## Verdict and recommendation

**The user's original framing — "teach `MergeTree` to quarantine sections whose
discontinuity exceeds a sane expected-distance threshold"
— rests on a faulty premise.** The cited 8 Mm / 16 Mm / 19 Mm "boundary
discontinuities" reported by the merger are not real position jumps in the
saved data. They are the output of a broken yardstick: `SessionMerger.
ComputeBoundaryDiscontinuity` (`Source/Parsek/SessionMerger.cs:787-821`) reads
the metre-scale anchor-local `(dx, dy, dz)` fields of v6+ Relative sections as
if they were degrees of latitude / longitude plus metres of altitude, then
multiplies through `body radius * π / 180` to get an "earth-distance" that has
no physical meaning. A legitimate 30 m anchor-local offset reports as
`30 * 600,000 * π / 180 ≈ 314 km` of bogus "north distance". This is exactly
the CLAUDE.md gotcha called out in the project's own KSP gotcha section: in
v6+ Relative sections, `latitude` / `longitude` / `altitude` are anchor-local
metres along x / y / z, **not** body-fixed lat / lon / alt.

Quarantining real recorded data against a broken yardstick would silently
delete legitimate Relative debris and active sections. **Fix the yardstick
first.** This plan therefore sequences the work so that no policy decision —
quarantine, hard-split, unrenderable flag, anything — runs against the broken
math. The merger math fix in Phase 1a stands alone, lands first, and is
validated on its own merits. The gameplay projection sibling fixes in Phase 1b
are a separate PR. Phase 2 is a pure data-collection step that re-evaluates
whether any further work is needed. Phase 3 is gated on Phase 2 evidence.

References to the same finding already in tree:

- `docs/dev/plans/recording-and-ghost-policies-refactor-plan.md:43` —
  "The megametre merger discontinuities are a **separate, independent bug**".
- `docs/dev/plans/recording-and-ghost-policies-refactor-plan.md:526` — same
  carve-out, in §"What this does NOT do".
- `docs/dev/plans/recording-and-ghost-policies-refactor-plan.md:657-661` —
  Known follow-up #1, the formal placeholder for this plan.
- `docs/dev/todo-and-known-bugs.md:157` item (5) — the original "quarantine"
  framing this plan supersedes.

## Evidence from in-game logs

Two playtest bundles in `C:/Users/vlad3/Documents/Code/Parsek/logs/` provide
the concrete numeric evidence that the math bug accounts for **all observed
megametre warnings** on this hardware. Those logs live outside this worktree;
the counts below are the empirical basis for this plan and must be re-counted
in Phase 2 rather than treated as a repo-internal invariant:

- `2026-05-06_2351_refly-phase-d-rewind-button-debris/KSP.log`: 31 warnings.
- `2026-05-06_2156_refly-probe-booster-broken/KSP.log`: 42 warnings.

Total **73 / 73 warnings (100%) are `prevRef=Relative nextRef=Relative`**.
Zero `Absolute`-vs-`Absolute`, zero cross-frame, zero on `prevSrc=Active
nextSrc=Background` or other source mixes. Every single warning is a
victim of the math bug. There is currently no evidence of any real
position-jump signal hiding under the noise.

Distribution within the 23 `cause=sample-skip` warnings in the 2351 bundle:

- `disc` range: **105,149 m to 16,479,040 m** (105 km to 16 Mm).
- `expectedFromVel` range: **8.74 m to 144.77 m** (single-digit to
  3-digit metres — the recorder believed motion was metre-scale).
- `disc / expectedFromVel` ratio range: **10,442x to 1,217,696x**;
  median ratio 172,172x.

Distribution within the 8 `cause=frame-mismatch` warnings (`dt < 0.05s`):

- `disc` range: **780,688 m to 19,299,100 m** (780 km to 19 Mm).
- `dt = 0.00s`, `expectedFromVel = 0.00m`. The classifier correctly
  short-circuits to `cause=frame-mismatch` because of `dtAbs < 0.05`,
  but **the WARN line still prints the broken disc number** (780k to
  19M metres) computed before classification. Phase 1a fixes the
  printed value too: with honest math, a same-anchor zero-dt pair at
  rest reads as ~0 m of anchor-local delta.

Recorder-side ground truth in the same 2351 bundle: `BgRecorder
RELATIVE sample: ... dx=6.85 dy=-1.07 dz=0.85 ... |offset|=6.98m`.
The recorder is correctly writing **single-to-double-digit metre
offsets** into `(latitude, longitude, altitude)` fields per the v6+
contract. The merger then reads e.g. `dy=-1.07` as `-1.07 degrees of
longitude`, multiplies by `600,000 * π / 180 * cos(lat)`, and produces
`~11,200 m` of phantom east-distance for one of those single-metre
deltas. The 7 m real delta becomes a ~70 km phantom — exactly the
order of magnitude of the WARN floor.

Save metadata (`saves/x6/persistent.sfs:702`) shows the affected
recordings are `recordingFormatVersion = 11`, which uses the v6+
anchor-local contract (`RelativeLocalFrameFormatVersion = 6`). All
samples in these recordings hit the buggy path.

A sibling bug is already confirmed: `Recording.maxDist` for the same
v11 debris recordings in `persistent.sfs` shows values like
`1,186,713.30` and `1,158,261.04`. `VesselSpawner.BackfillMaxDistance`
(`Source/Parsek/VesselSpawner.cs:3267-3316`) currently walks the flat
`rec.Points` list and feeds raw `latitude` / `longitude` / `altitude`
into `body.GetWorldSurfacePosition`. For v6+ Relative sections those
fields are anchor-local metres, so this is the same metres-as-degrees
bug outside the merger. It feeds gameplay decisions in
`ParsekFlight.cs` (`4387`, `15607`, `15631`, `15640`, `15662`,
`15790`), so the Phase 1b projection-sibling PR must fix it rather than leave
it as a follow-up.

These numbers ground the test fixtures in §"Test plan" below — Phase
1a unit tests can replay the recorder log line `dx=6.85 dy=-1.07
dz=0.85` and assert the corrected math returns ~7 m, vs. the broken
math's ~70 km.

## Phase 1a — merger math (first PR)

### Scope

Keep Phase 1a narrow. It fixes the merge boundary yardstick and the stale
`bdisc` interaction only:

- `SessionMerger.ComputeBoundaryDiscontinuity`, which inflates merge
  boundary diagnostics and healer classification.
- `SessionMerger.HealBackgroundActiveUnrecordedGapBoundaries`, only enough to
  stop trusting stale persisted `boundaryDiscontinuityMeters` and to avoid
  applying Relative-frame seams until a dual-list seam implementation is
  justified in Phase 3.

The Phase 1a fix is contained to `Source/Parsek/SessionMerger.cs` and its
tests. No schema bump, no codec change. The externally-observable effects are:

- WARN lines report honest distances.
- Cross-frame / cross-anchor boundaries that carry v7+ `absoluteFrames`
  shadows can be measured in body-fixed space instead of being silently
  suppressed.
- The existing Background→Active seam-healer no longer uses stale persisted
  `bdisc=` values from old saves. It recomputes the boundary measurement for
  the current merge.
- Relative-frame seam healing remains deferred: if a Relative boundary would
  require coordinated `frames` + `absoluteFrames` seam insertion, Phase 1a logs
  `relative-healer-deferred` and leaves the sections unchanged. That avoids
  changing the well-tested #580 healer path in the same PR as the math rewrite.

### Boundary-discontinuity consumers

`boundaryDiscontinuityMeters` is a struct field on `TrackSection`
(`Source/Parsek/TrackSection.cs:63`). It round-trips through both codecs
(`TrajectorySidecarBinary.cs:737, 770` and `TrajectoryTextSidecarCodec.cs:1563-1564, 1718`)
and has three known read sites:

- `SessionMerger.cs:454`, the WARN gate in merge diagnostics.
- `SessionMerger.cs:865-866`, the seam-healer shortcut that reuses a
  persisted boundary value before recomputing.
- `GhostRenderTrace.cs:599`, which copies it into a diagnostic context for
  trace logging.

There is still no playback gate, no quarantine gate, and no UI gate keyed on
this number, but it is not "trace-only": the merger itself reads it.
`GhostRenderTrace` only formats the value into trace output
(`boundaryDM=...` at `GhostRenderTrace.cs:615`); it does not feed a rendering
decision. Phase 1a
therefore recomputes boundary discontinuities after changing the math and must
not trust stale persisted `bdisc=` values from old saves as authoritative
inputs. Historical WARN lines and persisted text-codec diagnostics from saves
written before the fix will not match values produced after the fix. That
diagnostic mismatch is acceptable per pre-1.0 dev policy.

### Sibling projection classification

The CLAUDE.md gotcha applies to every code path that reads
`TrajectoryPoint.latitude` / `longitude` / `altitude` and projects those
fields with `body.GetWorldSurfacePosition(...)` or equivalent. Current grep
classification:

| Call site | Classification |
| --- | --- |
| `SessionMerger.ComputeBoundaryDiscontinuity` | **Fix in Phase 1a.** Dispatch by `referenceFrame` and `recordingFormatVersion`; never project ordinary v6+ Relative `(dx,dy,dz)`. |
| `VesselSpawner.SnapshotVessel` distance paths (`VesselSpawner.cs:3179, 3181, 3219`) | **Fix in Phase 1b.** `DistanceFromLaunch` must use `EnumerateBodyFixedTrajectorySamples(...)`: destroyed path uses first/last selected body-fixed samples; live path uses selected launch sample plus `vessel.GetWorldPos3D()`. Do not project raw `pending.Points[0]` / last flat point. |
| `VesselSpawner.ComputeMaxDistanceCore` / `BackfillMaxDistance` (`VesselSpawner.cs:3267-3316`) | **Fix in Phase 1b.** Replace raw flat `rec.Points` projection with `EnumerateBodyFixedTrajectorySamples(...)`. |
| `ParsekFlight.FinalizeIndividualRecording` (`ParsekFlight.cs:12055`) | **Fix in Phase 1b.** Remove the flat `rec.Points.Count >= 2` gate before max-distance backfill. |
| `ParsekTrackingStation.cs:1353` | **Already section-frame aware.** `TrySelectTrackingStationFocusFramesFromSection` uses `absoluteFrames` for Relative sections and blocks Relative sections without shadows before projecting. Keep this guarded. |
| `ParsekKSC.cs:1689` | **Already resolver-gated.** KSC playback dispatches Relative through `TrajectoryMath.ResolveRelativePlaybackPosition`; `TryLookupKscSurfacePose` is the Absolute/body-fixed lookup helper. Keep the helper contract explicit. |
| `ParsekFlight.cs:15178`, `FlightRecorder.cs:7545`, `RecordedRelativeAnchorPoseResolver.cs:162`, `ProductionAnchorWorldFrameResolver.cs:230, 458` | **Resolver callback contract.** These are `ResolveAbsoluteWorldPosition`-style callbacks for anchor resolution. Phase 1a/1b should add/retain contract comments or assertions when touching the caller: callers pass Absolute points / aligned shadows only; if a caller can pass ordinary Relative frames, route through the relative resolver instead. |
| `ParsekFlight.cs:17423-17512` | **Already fixed / section-frame aware.** The code comments explicitly guard the flat fallback and route Relative sections through `TryComputeStandaloneRelativeWorldPosition`; keep regression coverage. |
| `BackgroundRecorder.cs:3767, 4678, 5049` | **Recorder absolute-only contract.** These diagnostics / anchor-pose helpers operate on absolute captured points or absolute shadows. Phase 1a/1b should add a guard/comment if touched so ordinary Relative recorded points are never projected here. |
| `IncompleteBallisticSceneExitFinalizer.cs:1062` | **Absolute-only by construction.** Finalizer anchor points are body-fixed ballistic/state-vector anchors. Keep contract comments. |
| `GhostPlaybackEngine.cs:5634` | **Diagnostic only, needs guard.** Used for appearance/diagnostic world position from the first point; if the section is Relative, use `absoluteFrames` or skip the world diagnostic. Record as a follow-up unless Phase 1b already touches the same playback fixture. |
| `GhostMapPresence.cs:4247, 5317` | **Section-frame aware.** Map presence uses section/format dispatch and `ResolveStateVectorWorldPositionPure`; keep Absolute callback limited to body-fixed points. |
| `Rendering/CoBubbleOverlapDetector.cs:384` | **Fix in Phase 1b.** Production fallback interpolates flat `Recording.Points` during commit-time overlap detection and bypasses `IsBubbleEligibleSection`. Replace it with a section-frame-aware resolver or block Relative sections. |
| `Rendering/RenderSessionState.cs:1557` | **Callback contract.** `DefaultSurfaceLookup` is a body-fixed lookup callback used after upstream frame dispatch; keep it from receiving ordinary Relative frames. |
| `VesselGhoster.cs:840` | **Fix in Phase 1b.** Spawn-collision walkback projects `tipRecording.Points`; route it through section-aware body-fixed samples / shadows before `WalkbackAlongTrajectory` validates collision clearance. |

If a fresh grep turns up a flat `TrajectoryPoint` projection not listed here,
add it to this table with one of the same classifications before landing the
Phase 1a/1b implementation.

### Corrected math (function-by-function)

The fixed `ComputeBoundaryDiscontinuity` dispatches on
`(prev.referenceFrame, next.referenceFrame)` and first decides which
coordinate contract is honestly comparable at the boundary. It must also know
the recording format version for the sections being measured:

- For `recordingFormatVersion < RecordingStore.RelativeLocalFrameFormatVersion`
  (v5 and older), do **not** apply the v6+ anchor-local `(dx,dy,dz)` contract
  to `ReferenceFrame.Relative` sections. Legacy Relative payloads use the old
  playback path and are not safely measurable by this pure merger helper.
  Return `0` with a Verbose `legacy-relative-not-measurable` reason for any
  boundary where either side is legacy Relative.
- For `recordingFormatVersion >= RecordingStore.RelativeLocalFrameFormatVersion`
  (v6+), use the rules below.

- **Anchor-local contract**: both sections are `Relative` and have the same
  anchor identity.
- **Body-fixed shadow contract**: one or both sides are `Relative`, but every
  Relative side has a boundary `absoluteFrames` shadow point; Absolute sides
  use their ordinary boundary `frames` point.
- **Body-fixed Absolute contract**: both sides are `Absolute`.

If neither an anchor-local comparison nor a complete body-fixed/shadow
comparison is available, the function returns `0` and the caller logs the
boundary as "not measurable in raw form" at Verbose level. It must not invent
a distance by interpreting Relative `(dx,dy,dz)` as lat/lon/alt.

1. **Both `Absolute`.** Existing path is correct: `lat`, `lon`, `alt` are
   body-fixed. Keep the bodyRadius-scaled great-circle approximation at
   `SessionMerger.cs:806-820` verbatim. Same body name is implicit in the
   stitch (the merger does not stitch across body changes — body changes
   are step-3 in the optimizer split predicate per CLAUDE.md). If
   `lastPrev.bodyName != firstNext.bodyName`, return 0 with a Verbose
   "cross-body-stitch" log so future debugging has a footprint.

2. **Both `Relative`, same anchor identity.** Anchor-local subtraction is
   only valid when both ordinary boundary frames are effectively the same
   anchor pose sample. Each v6+ point stores
   `Inverse(anchor.rotation_at_ut) * (focusWorldPos_at_ut - anchorWorldPos_at_ut)`.
   If the anchor moves or rotates between `lastPrev.ut` and `firstNext.ut`,
   raw `(dx,dy,dz)` subtraction compares offsets expressed in two different
   anchor poses. Do **not** reuse the existing `dtAbs < 0.05s`
   frame-mismatch classifier threshold as a math-validity threshold: at
   10 deg/s anchor rotation, 50 ms is 0.5 deg, which produces about 8.7 m of
   apparent displacement error for a 1000 m offset.

   Therefore, if the strict same-anchor predicate passes **and**
   `abs(firstNext.ut - lastPrev.ut) <= 0.001s`, compute the Euclidean distance
   directly in anchor-local metres. Treat this as "same sampled anchor pose",
   not merely "same classifier bucket":
   ```
   dx = firstNext.latitude  - lastPrev.latitude
   dy = firstNext.longitude - lastPrev.longitude
   dz = firstNext.altitude  - lastPrev.altitude
   dist = sqrt(dx*dx + dy*dy + dz*dz)
   ```
   No body radius, no degree conversion, no cosine factor. The fields are
   already metres and, under the 1 ms gate, even a fast-rotating anchor's pose
   drift is below the 1 m WARN floor for ordinary offsets. For same-anchor
   Relative boundaries with `dtAbs > 0.001s`, fall through to the body-fixed
   shadow path in case (3).
   If usable aligned shadows are missing, return `0` with a Verbose
   same-anchor-shadow-required skip log; do not claim an anchor-local distance
   for the gap. Do **not** use raw
   `SessionMerger.AnchorIdentityKey` as the whole predicate: today it falls
   back to `pid:0` when both `anchorRecordingId` and `anchorVesselId` are
   absent, which can make two malformed modern Relative sections look like a
   valid same-anchor pair. Add an explicit same-anchor helper that returns
   true only when:

   - both sections have the same non-empty `anchorRecordingId`; or
   - neither section has `anchorRecordingId`, both have non-zero
     `anchorVesselId`, and the PIDs match.

   If both sections are Relative but the helper cannot prove a same anchor,
   dispatch to the absolute-shadow path in case (3), not to anchor-local
   subtraction.

3. **Both `Relative`, body-fixed shadow path.** Use this path for different
   anchor identities and for same-anchor boundaries whose `dtAbs > 0.001s`.
   Anchor-local offsets are not directly comparable across anchor frames, and
   same-anchor offsets over multi-second gaps are expressed in different
   sampled anchor poses. Phase 1a must first try the v7+ body-fixed shadow
   path:

   - Use `prev.absoluteFrames[prev.absoluteFrames.Count - 1]` for a Relative
     previous section.
   - Use `next.absoluteFrames[0]` for a Relative next section.
   - Verify the selected shadow point's `ut` matches the corresponding
     ordinary boundary frame's `ut` within a small epsilon. Do **not** require
     equality with `section.startUT` / `section.endUT`: existing sparse
     boundary shapes compare `prev.frames[^1]` to `next.frames[0]`, and those
     frame UTs can intentionally differ from section boundary UTs.
     `frames` and `absoluteFrames` are independent lists; a stale or trimmed
     shadow must not be treated as the boundary point.
   - Compute the same Absolute lat/lon/alt distance as case (1), including
     the same-body guard.

   If either shadow is missing, empty, or UT-misaligned, return `0` and log a
   reason-specific skip: cross-anchor no-shadow, same-anchor-large-dt
   shadow-required, or shadow-UT-mismatch. Do **not** call
   `RelativeAnchorResolver` in Phase 1a; the shadow path keeps the merger free
   of resolver state while still preserving signal for current v11/v12
   recordings.

4. **Cross-frame: `Absolute`-vs-`Relative` or `Relative`-vs-`Absolute`.**
   The previous blanket return-0 is too blunt for v7+ data. Phase 1a must try
   the same body-fixed shadow path:

   - Absolute side: ordinary boundary `frames` point.
   - Relative side: matching boundary `absoluteFrames` shadow point.
   - Shadow `ut` must match the Relative ordinary boundary frame within
     epsilon. It does not need to equal the section boundary UT.
   - Same-body guard before distance math.

   If the Relative side lacks a usable aligned shadow, return `0` and log
   "cross-frame absolute-relative; no usable absolute shadow; not measurable
   in raw form". This keeps legacy v5/v6-without-shadow data safe while
   making modern Relative boundaries measurable.

5. **`OrbitalCheckpoint` either side.** Checkpoints don't have `frames`
   in the same coordinate contract as Absolute/Relative trajectory samples.
   Add an explicit `prev.referenceFrame == OrbitalCheckpoint ||
   next.referenceFrame == OrbitalCheckpoint` guard that returns 0 even if a
   checkpoint section carries dense compatibility `frames`. Do not rely on
   the current empty-frames guard; the codec/rebuild path can preserve
   checkpoint frames for flat playback compatibility.

## Phase 1b — gameplay projection siblings (second PR)

This PR is separate from Phase 1a. It fixes gameplay-affecting flat
`TrajectoryPoint` projections that share the same v6+ Relative metres-as-
degrees bug. Do not bundle it with the merger math rewrite.

### `DistanceFromLaunch` / `MaxDistanceFromLaunch` corrected math

Phase 1b should introduce one canonical selector owned by `VesselSpawner`, e.g.
`EnumerateBodyFixedTrajectorySamples(Recording rec, BodyFixedSampleMode mode,
out BodyFixedSampleStats stats)`. It returns ordered body-fixed
`TrajectoryPoint`s selected from `TrackSections`: Absolute `frames`, and v6+
Relative aligned `absoluteFrames`; it never returns ordinary Relative frames.
`BodyFixedSampleMode` can distinguish all-body-fixed samples, launch/tail
distance samples, and Absolute-only live-state calls. `VesselGhoster` and
`CoBubbleOverlapDetector` should consume this selector or a small shared wrapper
around it, not fork their own frame dispatch.

`VesselSpawner` must stop walking raw `rec.Points` as if all points were
body-fixed when writing `DistanceFromLaunch` or `MaxDistanceFromLaunch`.
The confirmed bad projections are in `SnapshotVessel`: the destroyed path
projects `pending.Points[0]` and `pending.Points[^1]` for
`DistanceFromLaunch`, the live path projects `pending.Points[0]` for the
launch reference, and `ComputeMaxDistanceCore` projects all flat points for
`MaxDistanceFromLaunch`. `BackfillMaxDistance` calls that same core later.
`ParsekFlight.FinalizeIndividualRecording` only calls `BackfillMaxDistance`
when `MaxDistanceFromLaunch <= 0`, so an inflated value written by
`SnapshotVessel` would otherwise survive. Phase 1b must replace these raw
flat-point projections with a frame-aware body-fixed selector.

Also update the `ParsekFlight.FinalizeIndividualRecording` guard at
`ParsekFlight.cs:12055`: it currently requires `rec.Points.Count >= 2` before
calling `BackfillMaxDistance`. That flat-points gate conflicts with the new
track-section selector and can skip recordings whose `TrackSections` /
`absoluteFrames` are valid while the flat list is empty or stale. Move the
"enough body-fixed samples" decision into `VesselSpawner.BackfillMaxDistance`
or replace the caller guard with a cheap selector-aware predicate.

`BackfillMaxDistanceAbsoluteOnly` may share that iterator with
`includeRelativeShadows=false`, but its live-state "Absolute-only" semantics
and existing Relative-skip tests should remain intact unless the implementer
has a separate reason to broaden it.

Required behaviour:

- Choose the launch reference from the first usable body-fixed sample in
  track-section order: ordinary `frames` for Absolute sections, aligned
  `absoluteFrames` for Relative sections. If no body-fixed sample exists,
  leave `MaxDistanceFromLaunch` unchanged and log why.
- For destroyed recordings, compute `DistanceFromLaunch` from the first and
  last usable body-fixed samples selected from TrackSections / shadows. If the
  last ordinary point is Relative and has no aligned shadow, skip the distance
  update with a Verbose reason instead of projecting raw metres as degrees.
- For live `SnapshotVessel`, compute `DistanceFromLaunch` from the selected
  body-fixed launch sample to `vessel.GetWorldPos3D()`. The live vessel world
  position is already safe; the launch reference must not come from a raw flat
  Relative point.
- For Absolute sections, include ordinary `frames`.
- For v6+ Relative sections, include only `absoluteFrames` points whose `ut`
  aligns with the corresponding ordinary Relative frame. Skip ordinary
  Relative `(dx,dy,dz)` points completely.
- For legacy Relative sections without shadows, skip them and log a summary
  count; do not reinterpret their ordinary frames.
- Preserve the existing null/empty guards and one-shot summary logging, but
  include counters for `absoluteFrames` used, Relative ordinary frames skipped,
  stale shadows skipped, and body-resolution failures.
- `SnapshotVessel` destroyed/non-destroyed paths must call the same shared
  body-fixed selector for both `DistanceFromLaunch` and
  `MaxDistanceFromLaunch`; do not leave a second raw `rec.Points` projection
  path in `ComputeMaxDistanceCore` or in the distance calculation above it.
- `ParsekFlight.FinalizeIndividualRecording` must attempt backfill based on
  `MaxDistanceFromLaunch <= 0` and selector availability, not on flat
  `rec.Points.Count >= 2`.
- Keep the point-selection logic unit-testable without a live KSP scene. The
  shared `EnumerateBodyFixedTrajectorySamples(...)` selector can return the
  selected `TrajectoryPoint`s plus skip counters, while the
  production wrapper performs the `FlightGlobals.Bodies` lookup and
  `GetWorldSurfacePosition` projection. Headless xUnit tests pin the
  Absolute-vs-Relative/shadow selection; the in-game test pins the live KSP
  projection path.

This closes the confirmed `Recording.maxDist` sibling bug and prevents
distance diagnostics / pad-distance consumers in `ParsekFlight.cs` from seeing
phantom megametre distances.

### Spawn walkback and CoBubble projection fixes

Two additional gameplay-affecting production fallbacks must be fixed in the
same Phase 1b PR:

- `VesselGhoster.WalkbackAlongTrajectory` (`VesselGhoster.cs:830-848`) passes
  `tipRecording.Points` to `SpawnCollisionDetector.WalkbackAlongTrajectory`
  and projects each flat point through `GetWorldSurfacePosition`. For v6+
  Relative chain tips, this can validate spawn collision clearance against a
  position deep inside the planet. Replace the point stream with section-aware
  body-fixed samples (`frames` for Absolute, aligned `absoluteFrames` for
  Relative), or skip walkback with a logged reason when no body-fixed samples
  exist.
- `CoBubbleOverlapDetector.TrySampleWorld` (`CoBubbleOverlapDetector.cs:329-373`)
  has an `IsBubbleEligibleSection` Absolute-only gate, but the production
  fallback bypasses it by interpolating flat `Recording.Points`. Replace the
  fallback with a section-frame-aware resolver or block Relative sections
  before projecting.

Both fixes need focused xUnit coverage through their existing test seams and a
runtime/integration validation if the affected path depends on live
`FlightGlobals.Bodies`.

Phase 1b implementation order inside the PR:

1. Add and test `EnumerateBodyFixedTrajectorySamples(...)`.
2. Migrate `VesselSpawner` distance/max-distance and `ParsekFlight` finalize
   backfill gate to that selector.
3. Migrate `VesselGhoster` walkback to that selector.
4. Migrate `CoBubbleOverlapDetector.TrySampleWorld` to that selector or block
   Relative fallback sampling.

Keeping those steps in one PR is acceptable because they share the same
selector and bug class. If the selector API churns during implementation, split
after step 2 and leave steps 3-4 as the next PR with the same tests.

### Frame-mismatch diagnostic also benefits

Worth calling out explicitly: even when the classifier short-circuits
to `cause=frame-mismatch` because `dtAbs < 0.05` (`SessionMerger.cs:561`),
the WARN line still prints the `disc=` number computed by
`ComputeBoundaryDiscontinuity` *before* classification. The 2351
bundle has 8 such warnings, with disc values from 780,688 m to
19,299,100 m — all bogus, none of them healable (the healer at
`SessionMerger.cs:879` requires `cause == "unrecorded-gap"` so
frame-mismatch is unaffected). After Phase 1a, those same WARN lines
will print honest sub-metre or metre-scale numbers because the
underlying data is two same-anchor frames at effectively the same
UT (a sampler artifact, not a real jump). The classification stays
`frame-mismatch`; the printed value gets believable. This is a
diagnostic-only win but it materially reduces log noise during any
future investigation.

The 0.05s classifier threshold remains a classifier threshold only. It is not
used to justify same-anchor anchor-local subtraction. Same-anchor raw deltas
are measured only for nearly identical UTs (`<= 0.001s`) or through aligned
`absoluteFrames` shadows.

### Logging additions

`SessionMerger.cs:471-479` is the WARN-line site. Keep detailed logging out
of the pure two-section arithmetic helper unless the caller passes context.
Required Phase 1a logging changes:

- Append `ratio = disc / expected` to the WARN format string. One-liner;
  surfaces severity at-a-glance. If `expected` or `disc` is non-finite (for
  example `cause=invalid-data`), log `ratio=NaN` and keep the classifier's
  invalid-data reason visible. Otherwise, when `expected == 0`, log
  `ratio=inf` rather than printing NaN.
- Extend `ClassifyBoundaryDiscontinuity`'s invalid-data guard to include
  non-finite `discMeters`. Today it checks non-finite `dtSeconds` and velocity
  only; after Phase 1a, `NaN` or `Infinity` discontinuity measurements must
  produce `cause=invalid-data`, `expectedMeters=NaN`, and `ratio=NaN`.
- Add a Verbose line from caller sites that already have
  recording context (`LogMergeDiagnostics` and the seam-healer) when a
  measurable Relative boundary is processed:
  ```
  [Parsek][VERBOSE][Merger] boundary-disc-anchor-local
    recId={...} sectionIndex={i} anchor={anchorIdentityKey}
    dxA={...} dyA={...} dzA={...}
    dxB={...} dyB={...} dzB={...}
    dist={...} expected={...} ratio={...}
  ```
  For boundaries measured through `absoluteFrames`, use
  `boundary-disc-absolute-shadow` and log `prevPointSource` /
  `nextPointSource` (`frame` vs `absolute-shadow`) instead of anchor-local
  dx/dy/dz details.

  Do **not** use `VerboseRateLimited` here. `LogMergeDiagnostics` runs once
  per merge, not once per frame, and rate-limit keys can suppress legitimate
  diagnostics across repeated merges of the same recording. `LogMergeDiagnostics`
  must call the detailed measurement helper for every boundary before the
  existing `disc > 1` WARN branch, because not-measurable boundaries return
  `0` and would otherwise never log. Per CLAUDE.md every state transition +
  guard skip must be logged, but the log must be emitted where `recId` and
  `sectionIndex` are known; `ComputeBoundaryDiscontinuity(prev,next)` alone
  cannot log those fields. Use the detailed measurement helper described
  below so caller-side logs can distinguish "real zero distance" from "skipped
  because not measurable". The cross-anchor, cross-frame, same-anchor-large-dt,
  cross-body, missing-shadow, and shadow-UT-mismatch short-circuit paths each
  get a caller-side Verbose line.

### Method signatures (Phase 1a only — no source code in this plan)

The public test-facing entry point stays unchanged. Add small helpers rather
than expanding the merger's dependency surface:

- `internal static float ComputeBoundaryDiscontinuity(TrackSection prev, TrackSection next)`
  — compatibility/test wrapper that dispatches as current-format
  (`RecordingStore.CurrentRecordingFormatVersion`) data. Existing tests can
  continue to call it when they are intentionally testing current-format v6+
  semantics. To make missed production callers loud, the wrapper should emit
  a Verbose warning such as `boundary-disc-current-format-wrapper` with
  `caller=no-format-overload`, or be marked `[Obsolete]` if that is acceptable
  for the test assembly. Production code should not call this wrapper.
- New overload, e.g.
  `internal static float ComputeBoundaryDiscontinuity(TrackSection prev, TrackSection next, int recordingFormatVersion)`
  — real implementation entry point for merge-time callers. `MergeTree` must
  pass `srcRec.RecordingFormatVersion` through `ResolveOverlaps`,
  `RecomputeBoundaryDiscontinuities`, `LogMergeDiagnostics`, and
  `HealBackgroundActiveUnrecordedGapBoundaries`.
- New private/internal detailed helper, e.g.
  `private static BoundaryDiscontinuityMeasurement MeasureBoundaryDiscontinuity(TrackSection prev, TrackSection next, int recordingFormatVersion)`
  — returns `meters`, `branch` (`absolute`, `anchor-local`,
  `absolute-shadow`, `not-measurable`), `skipReason`, `dtSeconds`,
  `anchorKey`, and point-source labels.
  The existing `ComputeBoundaryDiscontinuity` wrapper returns only
  `measurement.meters`; `LogMergeDiagnostics` and the healer call the detailed
  helper when they need branch/reason fields for logs.
- New private helper, e.g.
  `private static bool HasSameRelativeAnchorIdentity(TrackSection prev, TrackSection next, out string anchorKey)`
  — proves same-anchor identity without treating missing modern anchors as
  `pid:0`.
- New private helper, e.g.
  `private static float ComputeRelativeAnchorLocalDelta(TrackSection prev, TrackSection next)`
  — pure metres-only Euclidean, called only after the strict same-anchor and
  small-dt gates pass.
- New private helper, e.g.
  `private static bool TryGetBodyFixedBoundaryPoint(TrackSection section, bool useLast, out TrajectoryPoint point, out string source)`
  — returns ordinary `frames` for Absolute sections and `absoluteFrames` for
  Relative sections. It returns false for Relative sections without a shadow
  or when the chosen shadow point's `ut` does not match the selected ordinary
  boundary frame's `ut` within epsilon.
- New private helper, e.g.
  `private static float ComputeBodyFixedBoundaryDelta(TrajectoryPoint prevPoint, TrajectoryPoint nextPoint)`
  — current Absolute lat/lon/alt distance math plus same-body guard.
- The classifier (`ClassifyBoundaryDiscontinuity` at
  `SessionMerger.cs:513-569`) and the seam-healer (
  `HealBackgroundActiveUnrecordedGapBoundaries` at
  `SessionMerger.cs:834-925`) consume `discMeters` as an input. Both
  benefit transparently from the fix — `expectedMeters = vMag * dtAbs`
  was always honest; only `discMeters` was lying. Phase 1a must force the
  healer to call the detailed measurement helper every time rather than using
  the current `next.boundaryDiscontinuityMeters > 0f ? persisted : recompute`
  shortcut. That closes the stale `bdisc=16Mm` path from old saves. Keep the
  existing Absolute #580 healer behaviour, but explicitly skip Relative
  boundaries with `reason=relative-healer-deferred`; dual `frames` +
  `absoluteFrames` seam insertion is Phase 3 work.
- `ResolveOverlaps(List<TrackSection> sections)` can remain as a current-format
  compatibility wrapper for tests, but production `MergeTree` must use a new
  overload such as `ResolveOverlaps(sections, srcRec.RecordingFormatVersion)`.
  The wrapper should not be the path used for real loaded recordings whose
  format may be v5 or older; it should log `ResolveOverlaps: current-format
  wrapper used` so an accidental production path is visible.

Production format-threading checklist for Phase 1a:

- `MergeTree` (`SessionMerger.cs:94`) calls
  `ResolveOverlaps(srcRec.TrackSections, srcRec.RecordingFormatVersion)`.
- `ResolveOverlaps(..., recordingFormatVersion)` recomputes output boundary
  discontinuities using that same version before returning (`SessionMerger.cs:772`
  today).
- `HealBackgroundActiveUnrecordedGapBoundaries` receives
  `srcRec.RecordingFormatVersion`; its stale-`bdisc` recompute and residual
  checks call the detailed measurement helper with that version
  (`SessionMerger.cs:865-913` today).
- `RecomputeBoundaryDiscontinuities` receives `recordingFormatVersion` for any
  explicit post-heal recompute (`SessionMerger.cs:99` and `927-940` today).
- `LogMergeDiagnostics` receives `recordingFormatVersion` and logs branch /
  skip reasons from the detailed helper, not from the current-format wrapper
  (`SessionMerger.cs:148` and `422-479` today).
- Add a focused test that builds a v5 Relative recording and runs the full
  `MergeTree` path, proving no production call silently fell back to the
  current-format wrapper.

### Files touched in Phase 1a / 1b

Phase 1a:

- `Source/Parsek/SessionMerger.cs` — `ComputeBoundaryDiscontinuity`
  rewrite, format-version threading, detailed measurement helpers, log-line
  additions, stale-`bdisc` healer recompute, and Relative-healer-deferred skip.
- `Source/Parsek.Tests/SessionMergerTests.cs` — new xUnit test cases
  (see §"Test plan" below).

Phase 1b:

- `Source/Parsek/VesselSpawner.cs` — owns
  `EnumerateBodyFixedTrajectorySamples(...)`; `SnapshotVessel` and
  `BackfillMaxDistance` use that selector for Absolute frames and aligned
  Relative `absoluteFrames`, never ordinary Relative `(dx,dy,dz)` points. If
  `BackfillMaxDistanceAbsoluteOnly` is refactored, preserve its Absolute-only
  behaviour unless the tests are intentionally updated.
- `Source/Parsek/ParsekFlight.cs` — finalize-time backfill guard updated so
  track-section / `absoluteFrames`-valid recordings are not skipped just
  because the flat `Points` list is empty or stale.
- `Source/Parsek/VesselGhoster.cs` — spawn-collision walkback uses
  section-aware body-fixed samples instead of flat Relative points.
- `Source/Parsek/Rendering/CoBubbleOverlapDetector.cs` — production
  `TrySampleWorld` fallback is section-frame-aware or blocks Relative sections
  before projection.
- `Source/Parsek.Tests/SceneExitInterceptorTests.cs` or a new focused
  `VesselSpawnerMaxDistanceTests.cs` — headless tests for shared
  max-distance body-fixed point selection and skip counters.
- Existing `VesselGhoster` / CoBubble tests, or new focused xUnit tests,
  covering section-aware walkback and CoBubble sampling.
- `Source/Parsek/InGameTests/RuntimeTests.cs` — one KSP-runtime validation
  for body lookup / `GetWorldSurfacePosition` max-distance behaviour, because
  the fixed path depends on live `FlightGlobals.Bodies` data.
- `CHANGELOG.md` — single-line entry under the current version.
- `docs/dev/todo-and-known-bugs.md` — strike item (5) at line 157 with
  pointer to this plan.
- `docs/dev/plans/recording-and-ghost-policies-refactor-plan.md` —
  mark Known follow-up #1 at lines 657-661 as in-progress / done.

## Phase 2 — re-collect data and re-evaluate

This phase has **no code changes**. After Phase 1a lands and is in a build
for at least one playtest session that exercises the Re-Fly + debris
scenario, the implementer collects fresh logs and re-counts the WARN
shape.

### Reproduction targets

The bundle that motivated item (5) is
`logs/2026-05-06_2351_refly-phase-d-rewind-button-debris` (cited at
`docs/dev/todo-and-known-bugs.md:147`). It contained 31
`MergeTree: boundary discontinuity` warnings with magnitudes from
`105148.80m` up to `16479040.00m`. The hypothesis is that **most or all
of those warnings will collapse to physically plausible `disc/expectedMeters`
ratios** once the math is honest, because the underlying data is mostly debris breakup samples
where the anchor is genuinely close to the focused vessel and the
Relative deltas are metre-scale.

To reproduce after Phase 1a, run a similar Re-Fly + debris-spawn scenario
(boost a stack with separators, decouple repeatedly to spawn debris,
trigger a Rewind, watch the merge logs):

1. Vehicle with at least 3 stages and a parent-anchored debris chain.
2. Launch, separate, observe debris go on-rails.
3. Rewind to staging.
4. Re-fly the rewound launch.
5. Observe `MergeTree: boundary discontinuity` warnings during the
   post-Re-Fly merge, captured by `python scripts/collect-logs.py
   refly-phase-1-fix-verify`.

### Decision criteria for Phase 3

The two motivating bundles produced 73 warnings, 100% of which are
victims of the math bug. The hypothesis is that **most of those will
disappear entirely** post-fix. Do not make the decision solely from absolute
metre thresholds derived from one user / one hardware setup. The old 300 m and
1 km numbers are useful log annotations, not cross-bundle pass/fail gates.

- **Victory (no Phase 3):** surviving warnings are either known false
  positives or have `disc / expectedMeters < 10x`, and no user-visible
  playback teleport is reported in the replay scenario. Record the absolute
  metres (`300m`, `1km`, etc.) in logs for intuition, but do not gate on those
  constants.

- **Need Phase 3:** any surviving warning with `disc / expectedMeters >= 10x`
  that is **not** a known false positive (cross-body, save-load teleport,
  finalizer hand-off — see §"False-positive list"), or any user-visible
  playback teleport regardless of ratio.

- **Mixed:** surviving warnings exceed the old absolute numbers but have
  `disc / expectedMeters < 10x` and no visible teleport. Keep them as
  Verbose/diagnostic evidence and defer Phase 3 until a real playback symptom
  arises.

The implementer writes a one-paragraph "Phase 2 outcome" addendum at
the bottom of this plan recording the decision.

## Phase 3 — surviving-jump remediation (gated; only if Phase 2 demands it)

Three approaches; pick one based on Phase 2 evidence. Each approach is
narrower and more disciplined than the original "quarantine" framing
because the Phase 1a yardstick now tells the truth.

### Approach X — extend the seam-healer (default recommendation if action is needed)

`SessionMerger.HealBackgroundActiveUnrecordedGapBoundaries`
(`SessionMerger.cs:834-925`) is already an in-place merge-time mutator
that:

- Iterates section pairs.
- Filters to Background→Active same-frame same-anchor `unrecorded-gap`
  pairs.
- Builds a shared seam point via `TryBuildBoundarySeamPoint`.
- Appends to prev tail, prepends to next head, re-runs
  `RecomputeBoundaryDiscontinuities`.
- Logs `MergeTree: healed unrecorded-gap ... #580` (Info).

Approach X extends the predicate to also accept:

- Relative Background→Active same-anchor boundaries, but only with an atomic
  dual-seam implementation: build both the ordinary `frames` seam and the
  body-fixed `absoluteFrames` seam against the same `boundaryUT` before
  mutating either section; apply to clones first; commit both sections only
  after both insertions and post-heal recomputation succeed. If the body-fixed
  seam is missing/stale/misaligned, leave originals untouched and log the
  reason.
- Active→Active same-frame same-anchor pairs with
  `cause == "unrecorded-gap"` (i.e. the disc is consistent with
  `vMag * dt * 2 + 5m` tolerance).
- Optionally, Background→Background and Active→Background mirror
  cases — but only if Phase 2 evidence shows them. Each direction
  needs its own re-evaluation of "is this sampling pattern
  equivalent enough to insert a shared seam".

It does NOT extend to:

- `cause == "sample-skip"` (real jumps), which by definition exceed
  the velocity-implied gap. Healing those would cover up real bugs.
- Cross-anchor Relative-vs-Relative pairs unless the implementation has a
  body-fixed `absoluteFrames` seam strategy and Phase 2 evidence specifically
  demands it.
- Cross-reference-frame pairs unless the Relative side has a usable
  `absoluteFrames` shadow and the seam insertion keeps all payloads in sync.

**Why this is the lowest-risk option:** the seam-healer pattern is
already in production for #580 and well-tested. The extension is one
new branch in the predicate; the rest is reuse. There is no schema
bump and no playback consumer change.

### Approach Y — `unrenderable` flag on `TrackSection`

Add a `bool unrenderable` field to `TrackSection` (alongside
`isBoundarySeam` at `TrackSection.cs:74`). Set it during merge for
sections whose **honest** disc exceeds an order-of-magnitude threshold
above the existing classifier envelope, e.g. `disc > expectedMeters * 2 + 5m`
or a Phase-2-justified `disc / expectedMeters` ratio when
`expectedMeters > 0`. Teach playback consumers
(at minimum `GhostPlaybackEngine`, `RelativeAnchorResolver`,
`ParsekFlight.InterpolateAndPositionRecorded*`,
`RecordingStore.RebuildPointsFromTrackSections`) to skip flagged
sections.

**Cost:**

- Schema bump: `TrackSection` schema increments. Per CLAUDE.md the
  named-version constants in `RecordingStore.cs:57-65` need a new
  entry, and both codecs (text and binary) need read/write for the
  new field with a default-false on legacy reads. Two codecs, one
  binary version constant, one read gate.
- Playback consumer changes: every read path that iterates
  `Recording.TrackSections` has to learn the flag.
- `RecordingStore.RebuildPointsFromTrackSections` flat-concatenates
  with no gap awareness today (per the agent context); a flagged
  section dropped from the flat list produces a UT gap that the
  flat-list playback path cannot render across. Either the rebuild
  has to fade across it (more changes) or the gap stays a hard
  teleport in flat playback.
- Ledger / stats / map / orbit-line consumers that read
  `Recording.Points` directly would need separate audits.

**This is the option originally proposed under "quarantine".** Argue
against it unless Phase 2 evidence demands it: it touches schema, it
touches every playback consumer, and it permanently bakes a "this
data is bad" judgement into the recording. With honest math the
class of true outliers is much smaller — most of the megametre
warnings vanish — and the few that remain may be better handled by
Approach X or Z.

### Approach Z — hard-split with UT gap + playback fade

When the merger sees an honest disc above the outlier threshold,
**don't stitch**. End the previous section at its last UT, start the
next section at its first UT, leave a UT gap. Teach the playback
engine to render the gap by fading the ghost out across the prev
tail, hiding during the gap, fading back in across the next head.

**Cost:** largest scope. The gap-aware rendering work is already
partially needed for other reasons (debris destruction tails, Re-Fly
gaps), but full implementation includes:

- `RecordingStore.RebuildPointsFromTrackSections` learns to insert
  gap markers.
- `GhostPlaybackEngine` learns to fade across markers.
- `GhostMapPresence` and tracking-station rendering learn to hide
  across markers.
- Map / orbit-line readers either skip across or hide.
- Save format unchanged (gaps are inferable from non-contiguous
  section UTs), but the existing implicit assumption that flat
  `Points` are dense breaks.

**Default recommendation if Phase 2 says we need action: Approach X.**

Rationale: the seam-healer pattern is already in tree, already tested,
and already addresses "consistent with velocity but the recorder
couldn't sample through it" gaps. Extending it to Active-vs-Active
captures the symptom — visible teleports during merge — without
schema work and without breaking flat-playback assumptions. Approach
Y and Z are larger, riskier, and pre-1.0 doesn't reward speculative
schema changes per the project's no-backwards-compat memory entry.

## Coordination with item (4)

Item (4) (parallel agent) introduces a structured
`RelativeAnchorResolveFailure` API on the resolver, with explicit
`Outcome == OutOfRecordedRange`, `RangeStartUT`, `RangeEndUT`,
`RequestedUT`, `HasAbsoluteShadow` fields.

- **Phase 1a of this plan is independent** of item (4). It is a local
  frame-contract fix for merger diagnostics and stale healer inputs, and ships
  as soon as it is reviewed.
- **Phase 1b is also resolver-independent**: it uses body-fixed Absolute
  frames / aligned `absoluteFrames` shadows for gameplay projection siblings.
- **Phase 3, if it goes ahead, may want item (4)'s API** — particularly
  Approach X if it ever needs to consult resolver state to evaluate
  cross-anchor stitches, and Approach Y to populate the `unrenderable`
  reasoning ("anchor-out-of-recorded-range across this UT range").
  Phase 3 lands after item (4)'s API is in.

The Phase 1a/1b fixes themselves do not call the resolver and do not need the
new API.

## False-positive list (informational, for Phase 3 readers)

Even with honest math, some boundary discontinuities legitimately
exceed `vMag * dt * 2 + 5m`. Document them so a future quarantine /
hard-split policy doesn't mistake them for data corruption.

- **Cross-anchor Relative-vs-Relative.** Phase 1a measures these through
  `absoluteFrames` when both sides have shadows and returns 0 only when no
  comparable shadow exists. A future Phase 3 Approach Y must not flag a
  section just because its anchor changed. Anchor changes are real,
  recorder-emitted state transitions.
- **Save-load teleport.** Already classified separately at
  `SessionMerger.cs:571-608`. Honest math doesn't change the
  classification; the teleport is still a real position jump.
- **SOI / body change.** No special-case today. KSP physics and
  reference frame change at SOI crossings; what looks like a
  position jump in the merger's coordinate-difference math is partly
  a coordinate-system change. Document for Phase 3.
- **Krakensbane / floating-origin shift.** Not currently special-
  cased. Krakensbane offsets are world-space frame shifts; the
  body-fixed lat/lon/alt fields should be invariant under
  Krakensbane (they're recorded relative to the body, not the
  world origin). If a recorder somewhere captures world-space and
  forgets to subtract floating-origin, that's a sibling bug.
- **Time-warp boundary.** The recorder's sample interval expands
  under warp; `dt` jumps and `expectedFromVel` (`vMag * dtAbs`) correctly
  expands with it. Should not produce false positives unless
  `vMag` is captured pre-warp.
- **Scene-exit finalizer extrapolation.** The
  `IncompleteBallisticSceneExitFinalizer` extends ballistic tails
  through atmosphere / terrain / SOI events. If finalizer-authored
  predicted points are stitched to recorder-authored authoritative
  points, the stitch boundary may show a real position step if
  the recorder's last sample lagged the predicted trajectory. This
  is an *intentional* recorder→finalizer hand-off; classify but
  do not quarantine.

## Test plan (Phase 1a / 1b)

Merger tests live in `Source/Parsek.Tests/SessionMergerTests.cs` (or a
new sibling file `SessionMergerBoundaryDiscontinuityTests.cs` if the
existing file is unwieldy). Max-distance tests live in
`Source/Parsek.Tests/SceneExitInterceptorTests.cs` or a new focused
`VesselSpawnerMaxDistanceTests.cs`. VesselGhoster / CoBubble projection tests
can extend existing focused tests or add sibling files under
`Source/Parsek.Tests/`. One runtime validation belongs in
`Source/Parsek/InGameTests/RuntimeTests.cs` for live KSP body projection.

### Phase 1a tests

1. **Same-anchor Relative-vs-Relative, 30 m offset → ~30 m.** Build
   two adjacent v6+ Relative sections sharing `anchorRecordingId`,
   with frames whose `(latitude, longitude, altitude)` fields are
   anchor-local metres differing by `(10, 20, 20)` between
   `lastPrev` and `firstNext`, and set the boundary UT gap to
   `<= 0.001s`. Expected: `disc ≈ 30.0` (sqrt(900)), not 314 km.
   **This test fails today and passes after the fix.**

1a. **Real-data fixture: `dx=6.85 dy=-1.07 dz=0.85` from the 2351
    bundle.** Recorder log
    `2026-05-06_2351_refly-phase-d-rewind-button-debris/KSP.log` line
    around `23:45:18.139` shows a real recorder-emitted RELATIVE
    sample with these offsets and `|offset|=6.98m`. Build a synthetic
    pair where `lastPrev` carries `(lat=0, lon=0, alt=0)` and
    `firstNext` carries `(lat=6.85, lon=-1.07, alt=0.85)` with a
    shared `anchorRecordingId` and a boundary UT gap `<= 0.001s`. Today's
    broken math produces ~70 km; the corrected synthetic delta is
    `sqrt(6.85^2 + 1.07^2 + 0.85^2)`,
    about `6.99m` / `7.00m` depending on rounding. Do not assert `6.98m`
    for this synthetic pair; that value came from the recorder's own source
    sample and rounding, not exactly from this zero-origin delta. This is the
    "lift the smoking gun out of the log and pin it as a unit test" fixture.
    The .prec sidecar binary parsing is left out of scope — the synthetic
    pair using the recorder-logged offsets is sufficient.

1b. **Real-data fixture: `disc=8147542.00m, expectedFromVel=144.77m`
    from the 2351 bundle WARN line at section[2] ut=16524.10.**
    The largest sample-skip warning in the bundle. Replay it as: build
    a same-anchor pair with an explicit previous anchor-local tail
    `(lat=0, lon=0, alt=0)` and next head `(lat=144.77, lon=0, alt=0)`.
    Also set the previous tail velocity and UT gap so
    `vMag * dtAbs == 144.77`, for example `prev.velocity =
    (144.77,0,0)` and `firstNext.ut - lastPrev.ut = 1.0s`. Because
    `dtAbs > 0.001s`, add aligned `absoluteFrames` shadows whose body-fixed
    boundary points are 144.77 m apart and verify the measurement branch is
    `absolute-shadow`, post-fix disc is ~145 m, and the classifier/log ratio
    is ~1.0. The ordinary anchor-local `(lat=144.77, lon=0, alt=0)` delta is
    present only to prove it is **not** used for a multi-second gap.
    Pre-fix the same input would compute as `144.77 * 600000 * π /
    180 ≈ 1.516 Mm` of phantom north-distance. This does not reproduce the
    exact 8 Mm WARN because the real warning also depends on the recorded
    latitude/cosine term, body radius, and the full source sample; this fixture
    asserts the corrected branch and magnitude, not the exact broken number.
1c. **Relative-vs-Relative with missing anchor identity does not use
    anchor-local subtraction.** Build two Relative sections with
    `anchorRecordingId = null`, `anchorVesselId = 0`, no shadows, and ordinary
    offsets that differ by 30 m. Expected: `disc == 0` and `branch =
    not-measurable`, not `30 m`. This pins the "do not treat `pid:0` as same
    anchor" guard.
1c-legacy. **Legacy v5 Relative is not interpreted as v6 anchor-local.** Build
    a Relative boundary with `recordingFormatVersion =
    RecordingStore.RelativeLocalFrameFormatVersion - 1` and ordinary offsets
    that would look like a 30 m v6 delta. Expected: `disc == 0`, branch
    `not-measurable`, skip reason `legacy-relative-not-measurable`. Add a
    current-format sibling proving the same ordinary data is measurable only
    when the recording format is v6+ and the small-dt gate passes.
1c-merge. **Full MergeTree threads recording format.** Build a v5 Relative
    recording and run `MergeTree`, not just `ComputeBoundaryDiscontinuity`.
    Expected: merge diagnostics and recomputed section `bdisc` use
    `legacy-relative-not-measurable`, and no log line indicates the
    current-format compatibility wrapper was used from production flow.
1d. **Same-anchor Relative large-dt requires body-fixed shadows.** Build two
    same-anchor Relative sections with ordinary offsets that differ by 30 m
    but `firstNext.ut - lastPrev.ut = 5.0s` and no `absoluteFrames`.
    Expected: `disc == 0`, branch `not-measurable`, skip reason
    `same-anchor-shadow-required` (or equivalent). This pins the
    anchor-pose-between-samples issue: raw anchor-local subtraction is not a
    physical displacement over multi-second gaps.
1d-50ms. **Same-anchor 50ms gap does not use raw subtraction.** Build two
    same-anchor Relative sections with a 50 ms UT gap and no
    `absoluteFrames`. Expected: `disc == 0`, branch `not-measurable`, skip
    reason `same-anchor-shadow-required`. This pins that the classifier's
    `dtAbs < 0.05s` threshold is not a math-validity threshold. Include a
    comment with the rotation-drift calculation: 10 deg/s over 50 ms at a
    1000 m offset produces about 8.7 m apparent error.
1e. **Same-anchor Relative large-dt with aligned shadows uses the shadow
    path.** Same as 1d but provide aligned `absoluteFrames` whose body-fixed
    boundary points differ by 42 m. Expected: `disc ≈ 42m`, branch
    `absolute-shadow`, and the log makes clear that same-anchor raw subtraction
    was bypassed because `dtAbs > 0.001s`.
2. **Cross-anchor Relative-vs-Relative without shadows.** Build two adjacent
   Relative sections with different `anchorRecordingId` and no
   `absoluteFrames`. Expected:
   `disc == 0`, Verbose log line "cross-anchor relative-vs-relative;
   not measurable in raw form".
2a. **Cross-anchor Relative-vs-Relative with shadows.** Build two adjacent
    Relative sections with different `anchorRecordingId`, metre-scale
    ordinary offsets that are not directly comparable, and body-fixed
    `absoluteFrames` whose boundary points differ by 42 m altitude.
    Expected: `disc ≈ 42 m`, measurement branch
    `absolute-shadow`, no resolver dependency.
2b. **Cross-anchor Relative-vs-Relative with stale shadows.** Same as 2a,
    but make the selected `absoluteFrames` boundary point UT differ from the
    selected ordinary boundary frame UT. Expected: `disc == 0`, branch
    `not-measurable`, skip reason `absolute-shadow-ut-mismatch`, and a Verbose
    caller-side log.
3. **Cross-frame Absolute-vs-Relative without shadow.** Returns 0 and logs
   missing-shadow / not-measurable.
3a. **Cross-frame Absolute-vs-Relative with shadow.** Absolute previous
    section followed by Relative next section with first `absoluteFrames`
    shadow. Expected: body-fixed distance from prev ordinary frame to next
    shadow frame.
3b. **Cross-frame Absolute-vs-Relative with stale shadow.** Relative shadow
    exists but its UT does not align with the selected Relative ordinary
    boundary frame. Expected: not-measurable, no WARN distance.
4. **Cross-frame Relative-vs-Absolute with shadow.** Same as 3a in reverse:
   previous Relative uses its last shadow, next Absolute uses its ordinary
   first frame.
5. **Both Absolute, same body.** Existing math; regression-protect
   that the disc is consistent with the body-radius great-circle
   approximation. ≈30 m delta in lat/lon should produce
   `≈ body_radius * 30 * π / 180` metres, *for an Absolute pair*
   where lat/lon are degrees. (This test asserts the existing
   formula is preserved for the legitimate case.)
6. **Both Absolute, different bodies.** Returns 0 with Verbose
   "cross-body-stitch" log.
7. **`prev.frames == null` / `next.frames == null`.** Returns 0 with
   the existing skip log.
7a. **OrbitalCheckpoint with non-empty frames.** Build an
    `OrbitalCheckpoint` section that carries dense compatibility `frames`
    followed by an Absolute section with a large apparent lat/lon/alt delta.
    Expected: `disc == 0`, explicit checkpoint skip branch. This prevents the
    old empty-frames assumption from re-entering the code.
8. **Frame-mismatch (dt < 0.05s) Relative same-anchor.** Disc
    computed honestly, classifier reports `cause = frame-mismatch`,
    WARN line includes `ratio` field.
9. **Relative healer is deferred, not partially applied.** Build a
   Background→Active Relative same-anchor pair with a `vMag * dt * 1.5`
   honest gap and matching `absoluteFrames` shadows. Expected: Phase 1a
   measures/classifies the boundary honestly, but does not mutate either
   section; it logs `relative-healer-deferred`. Dual `frames` +
   `absoluteFrames` seam insertion belongs to Phase 3.
9a. **Healer does not heal malformed missing-anchor Relative sections.** Build
    a Background→Active Relative pair with `anchorRecordingId = null`,
    `anchorVesselId = 0`, and usable aligned `absoluteFrames` whose body-fixed
    distance would otherwise classify as `unrecorded-gap`. Expected: no heal,
    no inserted seam, and a not-measurable / unproven-anchor Verbose log. This
    protects the healer from the current `AnchorIdentityKey` → `pid:0`
    fallback.
9b. **Healer ignores stale persisted `bdisc=`.** Build a boundary whose
    `next.boundaryDiscontinuityMeters` is a stale megametre value but whose
    recomputed Phase 1a measurement is small / not measurable. Expected:
    healer calls the detailed measurement helper, does not use the persisted
    shortcut, and does not misclassify as `sample-skip`.
10. **Log-capture for the new `boundary-disc-anchor-local` Verbose
    line.** Use the canonical `ParsekLog.TestSinkForTesting` pattern
    from `RewindLoggingTests.cs`; assert it by driving `MergeTree` /
    `LogMergeDiagnostics`, not the numeric `ComputeBoundaryDiscontinuity`
    wrapper or the detailed helper. The assertion must prove the log is
    emitted from caller context where `recId` and `sectionIndex` are
    available.
10a. **Log-capture for `boundary-disc-absolute-shadow`.** Cross-anchor or
     cross-frame fixture with shadows logs `prevPointSource` /
     `nextPointSource` and the measured distance.
10b. **Log-capture for not-measurable zero-distance skips.** Drive
     `MergeTree` with a missing-shadow or stale-shadow boundary and assert the
     Verbose skip log is emitted even though `disc == 0` and no WARN line is
     printed.
11. **Log-capture for the WARN `ratio` field.** Same pattern.
11a. **WARN ratio handles invalid data.** Drive a boundary that classifies as
     `cause=invalid-data` with non-finite `expectedFromVel`. Expected: WARN
     keeps `cause=invalid-data` and logs `ratio=NaN`, not `ratio=inf` or a
     misleading numeric value.
11b. **Classifier rejects non-finite discontinuity.** Drive
     `ClassifyBoundaryDiscontinuity` / merge diagnostics with `discMeters =
     NaN` and `discMeters = Infinity`. Expected: `cause=invalid-data`,
     `expectedMeters=NaN`, WARN/log ratio `NaN`, and no healer attempt.

### Phase 1b tests

12. **Shared max-distance selector skips ordinary Relative frames.** Build a recording
    with Relative ordinary frames whose `latitude` / `longitude` metre values
    would inflate to megametres if treated as degrees. Expected:
    the shared max-distance path ignores those ordinary Relative points and
    does not inflate `MaxDistanceFromLaunch`.
12a. **Shared max-distance selector includes aligned Relative shadows.** Same fixture,
     but add aligned `absoluteFrames` body-fixed points with a known distance
     from launch. In headless xUnit, assert the selected body-fixed samples
     and counters through the extracted iterator/projector seam; in the
     in-game test, assert `MaxDistanceFromLaunch` matches the body-fixed
     shadow distance and the summary log reports shadows used.
12b. **Shared max-distance selector skips stale Relative shadows.** Relative
     `absoluteFrames` exist but their UTs do not align with ordinary frames.
     Expected: stale shadows are skipped, the max distance is not inflated,
     and the summary log reports the stale-shadow count.
12b-snapshot. **SnapshotVessel max-distance path uses the same frame-aware
     core.** Build or seam-test the `SnapshotVessel`/`ComputeMaxDistanceCore`
     path so an already-written `MaxDistanceFromLaunch > 0` cannot bypass
     `BackfillMaxDistance` and preserve a raw flat-points inflation. Expected:
     the selected samples/counters match the `BackfillMaxDistance` path.
12b-distance. **SnapshotVessel distance path does not project raw Relative
     flat points.** Build destroyed and live snapshot fixtures where the first
     or last flat `Points` entry belongs to a Relative section but aligned
     `absoluteFrames` exist. Expected: destroyed `DistanceFromLaunch` uses the
     first/last body-fixed shadow samples; live `DistanceFromLaunch` uses the
     body-fixed launch sample plus `vessel.GetWorldPos3D()`. A missing shadow
     logs a skip reason and leaves the previous distance value unchanged.
12b-finalize. **FinalizeIndividualRecording backfill is not gated by flat
     points.** Build a recording with `MaxDistanceFromLaunch <= 0`, empty or
     stale `Points`, and valid `TrackSections` / aligned `absoluteFrames`.
     Expected: finalization still calls the selector-aware backfill path and
     writes/logs the correct max distance, or logs the selector-specific reason
     it could not.
12c. **Runtime max-distance validation.** Add one `RuntimeTests.cs` in-game
     test that constructs or exercises a recording with a Relative section and
     verifies the body lookup + `GetWorldSurfacePosition` path uses
     body-fixed shadows, not ordinary Relative metres. This covers the
     Unity/KSP body API that headless xUnit can only approximate.
12d. **VesselGhoster walkback uses body-fixed samples.** Build a chain-tip
     recording whose flat `Points` are Relative metres but whose
     `absoluteFrames` resolve to a known clear/blocked path. Expected:
     walkback collision checks use body-fixed samples / shadows, not raw flat
     points.
12e. **CoBubble production fallback is section-frame-aware.** Drive
     `TrySampleWorld` without the xUnit seam using a recording whose current
     section is Relative. Expected: it uses aligned `absoluteFrames` or blocks
     Relative sampling with a logged reason; it never projects raw flat
     `Recording.Points`.

### Existing test updates required

The Phase 1a implementation must update current `SessionMergerTests` whose old
expectations intentionally pin the behaviour being replaced:

- `ComputeBoundaryDiscontinuity_UsesLastPrevBody_NotFirstNext`
  (`SessionMergerTests.cs:2245`) should become a cross-body skip test:
  Absolute Mun→Kerbin returns `0` with a cross-body branch/log, not a Mun-radius
  distance.
- `ComputeBoundaryDiscontinuity_CrossReferenceFrame_ReturnsZero`
  (`SessionMergerTests.cs:509-522`) should be renamed/commented as the
  **no-shadow legacy** case and should assert the Verbose not-measurable
  branch. Add a sibling with aligned `absoluteFrames` proving cross-frame
  modern data measures through the shadow path instead of always returning 0.
  The legacy case should pass an explicit pre-v6 `recordingFormatVersion` if
  it is meant to pin old Relative semantics; do not rely on the current-format
  compatibility wrapper for legacy coverage.
- `ResolveOverlaps_CrossReferenceFrameBoundary_ZeroDiscontinuity`
  (`SessionMergerTests.cs:540-558`) needs the same split: keep the no-shadow
  zero case, add an aligned-shadow case, and assert the merge diagnostics log
  branch. Use the new `ResolveOverlaps(sections, recordingFormatVersion)`
  overload where the test is about legacy format behaviour.
- `MergeTree_BackgroundToActiveRelativeAnchorMismatch_SkipsHeal`
  (`SessionMergerTests.cs:1781`) and
  `MergeTree_BackgroundToActiveRelativeAnchorRecordingMismatch_SkipsHeal`
  (`SessionMergerTests.cs:1824`) should stop expecting a WARN from raw
  Relative offset comparison when no shadows are present. They should assert:
  no heal, no boundary-discontinuity WARN, and a Verbose not-measurable
  cross-anchor skip log.
- `MergeTree_BackgroundToActiveUnrecordedGap_LogsUnhealableSeam`
  (`SessionMergerTests.cs:1928`) should become a cross-body
  not-measurable/no-heal test. With the new explicit body guard, the boundary
  should not reach the seam-healer as a nonzero `unrecorded-gap`; assert no
  heal, no boundary-discontinuity WARN, and a cross-body Verbose skip log.

In-game coverage is required for the Phase 1b projection sibling fixes where
the path depends on live KSP body resolution and the Unity/KSP
`GetWorldSurfacePosition` implementation. The pure merger arithmetic,
detailed measurement branching, caller-side merge diagnostics, and
relative-healer-deferred behavior remain headless xUnit-testable through
`SessionMergerTests`.

## Documentation updates (Phase 1a / 1b)

Per CLAUDE.md "Documentation Updates — Per Commit, Not Per PR", the
Phase 1a commit must include:

- `CHANGELOG.md` under the current version: a single line:
  > Merge boundary discontinuity for Relative sections now uses anchor-local
  > metres only for same-UT same-anchor sections and v7+ absolute-shadow
  > points for cross-anchor / cross-frame sections, rather than the buggy
  > lat/lon-as-metres path. Eliminates spurious megametre WARN lines,
  > recomputes stale persisted `bdisc=` before healer decisions, and defers
  > Relative seam healing until a dual-list seam can be implemented safely.
- `docs/dev/todo-and-known-bugs.md`: strike item (5) of the work
  queue at line 157 with `~~ ... ~~` and append `(superseded by
  docs/dev/plans/merge-boundary-discontinuity-math-fix-plan.md;
  Phase 1a fixes the measurement bug, Phase 3 — gated on Phase 2 data
  collection — replaces the original quarantine framing)`.
- `docs/dev/plans/recording-and-ghost-policies-refactor-plan.md`:
  amend Known follow-up #1 at lines 657-661 with a
  `**Status:** Phase 1a in progress — see
  docs/dev/plans/merge-boundary-discontinuity-math-fix-plan.md` line.

Phase 1b commit must add/update the same docs with the projection sibling
status, e.g.:

- `CHANGELOG.md`: recording distance/max-distance, spawn walkback, and
  CoBubble overlap sampling now use section-aware Absolute/shadow points
  instead of ordinary Relative metre offsets.
- `docs/dev/todo-and-known-bugs.md`: note that Phase 1b closed the
  gameplay-affecting projection siblings discovered during the item (5)
  audit.

## Risk and rollback

### Phase 1a — merger math

**Risk profile: low-medium.**

- The merge-diagnostic change is contained to `SessionMerger` helper logic,
  one log-line site, and stale-input handling in the existing seam-healer.
- `boundaryDiscontinuityMeters` is consumed by `SessionMerger` itself for the
  WARN gate and healer shortcut, and by `GhostRenderTrace` for diagnostic
  copy. No playback gate, UI gate, or quarantine gate is keyed on it, but
  stale persisted `bdisc=` values must be recomputed before healer decisions.
- Relative seam healing is explicitly deferred in Phase 1a. This avoids
  partially mutating `frames` without `absoluteFrames` and keeps the #580
  Absolute healer path isolated from the math rewrite.
- Passing `RecordingFormatVersion` through the merger broadens method
  signatures but protects legacy v5-and-older Relative recordings from being
  misinterpreted as v6+ anchor-local data.
- Persisted `bdisc=` text-codec values from old saves will not match
  newly-computed values for Relative sections. Acceptable per
  pre-1.0 dev policy. No load-time semantic depends on the old
  values.

**Rollback:** revert `SessionMerger.cs` and `SessionMergerTests.cs`.

### Phase 1b — gameplay projection siblings

**Risk profile: medium.**

- The projection sibling fix touches gameplay-affecting paths:
  `VesselSpawner`, `ParsekFlight`, `VesselGhoster`, and
  `CoBubbleOverlapDetector`.
- The distance/max-distance change should reduce phantom distances for
  Relative debris recordings. Risk is that a legitimate legacy Relative-only
  recording without shadows may no longer contribute to backfilled distance;
  the code must log skipped counts so that is visible.
- Existing persisted recordings that already have inflated
  `DistanceFromLaunch` / `MaxDistanceFromLaunch > 0` from pre-fix writers are
  not automatically migrated by Phase 1b. Phase 1b fixes future writes and
  finalize-time backfill; repairing old saves requires an explicit recompute
  tool or follow-up load-time migration.

**Rollback:** revert `VesselSpawner.cs`, `ParsekFlight.cs`,
`VesselGhoster.cs`, `Rendering/CoBubbleOverlapDetector.cs`, and their test
updates.

### Phase 3 — varies by approach

- **Approach X**: low. Extends an existing healer; same rollback
  shape (revert the predicate change).
- **Approach Y**: medium-high. Schema bump, two codecs, multiple
  playback consumers. Rollback requires either keeping the field in
  the schema with an "always false" guarantee or doing a full
  schema revert + load-tolerance for the now-unknown field on save
  files written during the Approach-Y window.
- **Approach Z**: high. Touches playback rendering across multiple
  consumers. Rollback similar to Approach Y in scope.

## Out of scope

- Items (1), (2), (4), (6) of the original work queue at
  `docs/dev/todo-and-known-bugs.md:157` — separate plans.
- Item (3) (tighten Background Atmospheric sampling ceiling) — its
  own plan; reduces the *frequency* of `unrecorded-gap` warnings but
  doesn't change the math.
- Non-trajectory `body.GetWorldSurfacePosition` callers. Phase 1a/1b classify
  every flat `TrajectoryPoint` / `Recording.Points` projection hit in the
  table above; broader hardening beyond that class (for example ad hoc
  lat/lon UI helpers not fed by trajectory data) can be tracked separately.
- Format-version-aware `boundaryDiscontinuityMeters` re-computation
  on legacy saves loaded after the fix. The persisted value is
  diagnostic-only and is recomputed at next merge anyway.
- Cross-body and Krakensbane edge-case classification beyond the
  Verbose log line. If Phase 2 / Phase 3 evidence shows real
  symptoms there, write a separate plan.

## Open questions for the implementer

1. **Same-anchor predicate helper.** `AnchorIdentityKey` can still format
   log keys, but the measurement predicate must be the stricter
   `HasSameRelativeAnchorIdentity` helper described above so missing anchors
   do not collapse to `pid:0`.
2. **Cross-anchor Relative-vs-Relative without absolute shadows.**
   Phase 1a picks "return 0 + Verbose log" only for missing-shadow cases. If a
   reviewer prefers resolver-based world-space comparison for legacy data,
   pull it in only after item (4)'s API is available.
3. **Body-radius lookup for modded bodies.** The current
   `GetBodyRadius` at `SessionMerger.cs:52-59` falls back to Kerbin
   radius for unknown bodies. Phase 1a does not change this; if RSS /
   Kopernicus saves produce noisy WARN lines after the fix because
   modded bodies are using Kerbin's 600 km, raise it as its own
   follow-up.
