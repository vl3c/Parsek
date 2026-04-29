# Phase 5 Implementation Plan

## Co-Bubble Overlap Blend (Stage 5) — Parsek Ghost Trajectory Rendering Pipeline

**Worktree:** `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/`
**Branch:** `feat/cobubble-blend-phase5` (already created off `origin/main` @ `58df16e1`)
**Target version:** v0.9.1 (per `Source/Parsek/Properties/AssemblyInfo.cs`, current is `0.9.1.0`)

> **Status:** Implementation shipped in PR #643 across four commits (`2f430434` baseline, `db8fca10` review pass 1, `e2369a5e` review pass 2, `9de806f6` review pass 3). This plan has been updated post-implementation to reflect what actually shipped — specifically §1.1 (bubble-membership "at least one Background" gate), §2.2 (Cartesian vector helpers `LiftOffsetFromWorldToInertial` / `LowerOffsetFromInertialToWorld`, NOT lat/lon/alt position helpers), §3.4 (one primary per connected co-bubble component), §6.5 (lazy recompute persists peer .pann symmetrically; primary map refreshes via existing `RebuildFromMarker` flow). The original draft of those sections had bugs caught by clean-context review passes; the current text is the corrected design.

---

## 1. Co-Bubble Detection (Commit Time)

### 1.1 Bubble-Membership Source

**Discovery:** the recorder does *not* emit an explicit "in same bubble" marker, but it does emit `TrackSection.source` (`TrackSection.cs:61`) which is the `TrackSectionSource` enum (`TrackSection.cs:10`):

- `TrackSectionSource.Active` — focused vessel's `FlightRecorder` (focused = physics-active, in bubble for itself trivially)
- `TrackSectionSource.Background` — `BackgroundRecorder` `OnBackgroundPhysicsFrame` (loaded + `!packed` only — see `BackgroundRecorder.cs:1417-1424`, "Only process loaded/physics vessels (not packed).")
- `TrackSectionSource.Checkpoint` — orbital propagation, never in bubble

**Derivation rule (no recorder change required):**

> Two recordings A and B share a physics bubble during UT range `[t_start, t_end]` iff A has a section with `source ∈ {Active, Background}` whose `[startUT, endUT]` overlaps a section in B with `source ∈ {Active, Background}` AND **at least one side is `Background`** AND both sections have `referenceFrame == ReferenceFrame.Absolute` (RELATIVE/Checkpoint sections do not contribute).

The "at least one Background" gate is the real co-bubble-evidence test. KSP allows only ONE focused/Active vessel per physics scene, so two simultaneous `Active` sections at the same UT must be from two different recording sessions (a re-fly), not from one shared bubble. Without the gate, the detector would manufacture phantom traces between unrelated recordings — see review pass 1 P2-A. Valid combinations:

- `Active + Background` — one focused, one peer in bubble. Real co-bubble.
- `Background + Background` — both peers shadowing a third active focus. Real co-bubble.
- `Active + Active` — REJECTED. Different recording sessions; reusing UTs does not imply shared bubble.

Background sections exist only while a peer was loaded + unpacked while another vessel was Active in the same physics scene — that *is* the bubble definition. The frame guard excludes RELATIVE sections (those use a different anchor mechanism per §6.5/§7.4 — they already have sub-meter fidelity from the existing `ResolveAbsoluteShadowPlaybackFrames` code path, and a co-bubble blend on top would double-correct).

### 1.2 `CoBubbleOverlapDetector` Contract (new file)

`Source/Parsek/Rendering/CoBubbleOverlapDetector.cs`:

```
internal static class CoBubbleOverlapDetector
{
    internal struct OverlapWindow
    {
        public string RecordingA;
        public string RecordingB;
        public double StartUT;
        public double EndUT;
        public byte FrameTag;       // 0=body-fixed (atmo/surface), 1=inertial (exo*)
        public SegmentEnvironment PrimaryEnv;  // env class at time t — for diagnostics
        public string BodyName;     // primary segment's body
    }

    // Pure: walks recording pairs and emits windows. Body / referenceFrame /
    // env consistency checked per overlap; mismatched-body or frame-toggle
    // overlaps are split into multiple windows (HR-7).
    internal static List<OverlapWindow> Detect(
        IReadOnlyList<Recording> recordings,
        double minWindowDurationS = 0.5,        // §22 tunable, see §11
        double maxBubbleSeparationM = 2500.0);  // BubbleRadiusMetres const from RenderSessionState
}
```

**Algorithm:**

1. For each ordered pair `(A, B)` where `A.RecordingId < B.RecordingId` (Ordinal):
2. Walk both recordings' `TrackSections` and find the intersection of their `[startUT, endUT]` ranges restricted to sections where `source ∈ {Active, Background}` AND `referenceFrame == Absolute`.
3. For each candidate overlap range `[lo, hi]`, walk recorded samples on both sides and confirm they share `bodyName` (sample-by-sample; if body changes mid-overlap, split at the body change UT — HR-7).
4. Sample the absolute-distance every ~`coBubbleResampleHz` samples; if separation exceeds `maxBubbleSeparationM`, truncate the window at that UT (the §10.3 "bubble exit" boundary).
5. Determine `FrameTag` from the section's environment per §6.2: `Atmospheric / Surface*` → 0 (body-fixed), `ExoPropulsive / ExoBallistic` → 1 (inertial), `Approach` → 0.

**Output format:** flat list of `OverlapWindow` rows, sorted by (RecordingA, RecordingB, StartUT) for determinism (HR-3).

---

## 2. Trace Building

### 2.1 `coBubbleResampleHz` and Window Cap

The design doc's §17.3.1 ConfigurationHash table reserves `coBubbleResampleHz : float32` and `coBubbleBlendMaxWindow : float64 s` (currently zero-padded in `PannotationsSidecarBinary.ComputeConfigurationHash` at byte offsets [47..50] and [39..46]). §22.1/§22.3 leave concrete values to empirical tuning. **Phase 5 ships these defaults in a new struct `CoBubbleConfiguration`:**

```
internal struct CoBubbleConfiguration
{
    public float ResampleHz;                  // 4.0 Hz — matches the slowest typical
                                              // active-vessel sample rate; oversampling
                                              // in body-fixed atmo segments would burn
                                              // sidecar bytes for sub-mm fidelity gain.
    public double BlendMaxWindowSeconds;       // 600.0 s (10 minutes) — covers the
                                              // longest realistic close-formation
                                              // segment; longer windows fall back
                                              // to standalone via §10.3 boundaries.
    public double CrossfadeDurationSeconds;    // 1.5 s — short enough to be
                                              // imperceptible during exit, long enough
                                              // to mask sub-meter snap.

    internal static CoBubbleConfiguration Default => new CoBubbleConfiguration {
        ResampleHz = 4.0f,
        BlendMaxWindowSeconds = 600.0,
        CrossfadeDurationSeconds = 1.5,
    };
}
```

These values are committed to the canonical hash encoding (see §6 below) so changing them later flips `config-hash-drift` and invalidates every existing `.pann`'s co-bubble traces (HR-10).

### 2.2 Trace Math (§10.2)

For each `OverlapWindow`, build one `CoBubbleOffsetTrace`:

```
internal sealed class CoBubbleOffsetTrace
{
    public string PeerRecordingId;
    public int PeerSourceFormatVersion;       // peer .prec's CurrentRecordingFormatVersion
    public int PeerSidecarEpoch;              // peer .prec's SidecarEpoch
    public byte[] PeerContentSignature;       // 32-byte SHA-256
    public double StartUT;
    public double EndUT;
    public byte FrameTag;                     // 0=body-fixed, 1=inertial
    public byte PrimaryDesignation;           // 0=self, 1=peer (commit-time hint;
                                              // session-time selector is authoritative)
    public double[] UTs;                      // resampled at coBubbleResampleHz
    public float[] Dx, Dy, Dz;                // per-axis offset in primary's frame
}
```

**Per-sample trace value at UT `t`:**

The offset stored on disk is a Cartesian VECTOR (a translation between two vessels), not a position triple — so the trace math must use vector transforms, not the lat/lon/alt-position helpers. Mixing them produces a body-fixed offset that won't rotate with the body at playback or an inertial offset that can't be reconstructed from lifted lat/lon/alt triples — see review pass 1 P1-B.

1. Look up both vessels' world positions at `t` from their `.prec` raw samples via `body.GetWorldSurfacePosition(lat, lon, alt)` (linear interpolation between bracketing samples — same as `TrajectoryMath.InterpolatePoints`). World positions are body-fixed Cartesian by construction.
2. Compute the world-frame Cartesian translation `Δ_world(t) = peer_world − primary_world`.
3. Convert into the primary's chosen frame via dedicated vector helpers (NOT the lat/lon/alt-position helpers `LiftToInertial` / `LowerFromInertialToWorld`):
   - **body-fixed (FrameTag = 0)**: store `Δ_world` directly. Both peer_world and primary_world come from `GetWorldSurfacePosition` → already body-fixed; the body's rotation phase cancels in subtraction.
   - **inertial (FrameTag = 1)**: rotate the world-frame translation into the body's inertial frame at sample UT via the new `TrajectoryMath.FrameTransform.LiftOffsetFromWorldToInertial(Δ_world, body, t)` helper (added in Phase 5 baseline `2f430434`). Recovery at playback uses `LowerOffsetFromInertialToWorld(stored, body, playbackUT)`.
4. Store `(Dx, Dy, Dz) = Δ.x, Δ.y, Δ.z` as `float32`.

The vector helpers are pure rotations about the body's spin axis (KSP convention: `body.bodyTransform.up` with phase `(ut * 360 / rotationPeriod)°`). They do NOT call `GetWorldSurfacePosition` — that's a position lookup, irrelevant to translation transforms.

**Sample rate (§10.2):** "lower of the two (resampled)". Use `coBubbleResampleHz` as a cap: target rate = `min(A.section.sampleRateHz, B.section.sampleRateHz, coBubbleResampleHz)`.

### 2.3 Validity Boundaries (§10.3)

The window already terminates at: TIME_JUMP (split window at `SegmentEvent.TimeJump` UT), bubble-radius exceedance (separation > 2.5 km), recording end. **Additional boundaries to enforce in `Detect`:**

- Either side has a `BranchPoint` of type `Terminal | Breakup` inside the window → cap window at that UT.
- Either side has a `BranchPoint` of type `Dock | Undock | EVA | JointBreak` inside the window → split at that UT (the structural event invalidates the offset trace; the next window picks up after).
- Body change inside the window → split at the body change UT.

### 2.4 Per-Trace Peer Validation Fields (§17.3.1 "Per-Trace Peer Validation")

`peerSourceFormatVersion`, `peerSidecarEpoch`: read from peer `Recording.RecordingFormatVersion` and `Recording.SidecarEpoch` at trace-compute time.

`peerContentSignature`: SHA-256 over the peer's raw sample bytes covering `[startUT, endUT]`. Concrete encoding: a deterministic byte stream of every `TrajectoryPoint` from the peer's `Recording.Points` whose `ut ∈ [startUT, endUT]`, written as:

```
for each point in peer.Points where startUT <= p.ut <= endUT (in iteration order):
    write double p.ut
    write double p.latitude
    write double p.longitude
    write double p.altitude
    write float p.velocity.x, .y, .z
    write quaternion p.rotation
    write length-prefixed UTF-8 p.bodyName
```

Then `peerContentSignature = SHA256(stream)`. The encoding intentionally excludes derived fields (no resource deltas, no inventory) — only what would visibly change the trace if mutated. Helper `ComputePeerContentSignature(Recording peer, double startUT, double endUT)` lives in `CoBubbleOverlapDetector` and is unit-tested for determinism.

---

## 3. Designated Primary Selection (§10.1)

### 3.1 Where Selection Happens

**Decision: a static helper `CoBubblePrimarySelector` invoked once per session entry** *after* `AnchorPropagator.Run` finishes (so the DAG-ancestry tiebreaker has the final anchor map to read). Cache the result in a new `Dictionary<string, string>` (recordingId → designatedPrimaryRecordingId) on `RenderSessionState`.

Reason for not putting selection inside `AnchorPropagator`: the propagator's job is per-segment ε resolution, and Phase 5 selection consumes its output (which recordings have a LiveSeparation anchor closest in DAG ancestry). Keeping them separate also means turning `useCoBubbleBlend` off does not perturb propagator timing.

### 3.2 Selection Rules (§10.1)

For every co-bubble *pair* `(A, B)`:

1. **Live wins (always).** If A has any anchor with `Source == AnchorSource.LiveSeparation` (or A *is* the active re-fly recording, i.e., `A.RecordingId == marker.ActiveReFlyRecordingId`), A is primary. If B is, B is primary. (§7.11 reserves rank 1 for live; this rule is just §7.11 restated for the pair.)
2. **Closest-to-live in DAG ancestry.** Walk each side's DAG (via `RecordingTree.BranchPoints`) toward the live root. The recording whose minimum hop count to a `LiveSeparation`-anchored recording is smaller wins. Use the `AnchorPropagator`'s edge enumeration (re-export the edge-list builder as an `internal static` helper in `AnchorPropagator`).
3. **Earlier commit time wins.** Compare `Recording.StartUT` (or fall back to `RecordingTree.TreeOrder` when `StartUT` is equal). Lower wins.
4. **Higher sample rate during overlap wins.** Compare `TrackSection.sampleRateHz` for the section that owns the overlap-window's midpoint UT in each recording. Higher wins.
5. **Tiebreaker (HR-3 deterministic).** `string.CompareOrdinal(A.RecordingId, B.RecordingId) < 0` → A wins.

### 3.3 Stability Across Save/Load (§22.2)

Selection is purely a function of `(recordings, marker, anchor map, recording metadata)` — all session-only inputs that are deterministically rebuilt on every load by `RenderSessionState.RebuildFromMarker`. **No persistence needed**, but verify with a determinism unit test: same inputs twice → same primary map.

### 3.4 Public API in `RenderSessionState`

```
internal static bool TryGetDesignatedPrimary(string recordingId, out string primaryRecordingId);
internal static bool IsPrimary(string recordingId);   // primary for at least one peer
internal static IReadOnlyDictionary<string, string> PrimaryByPeer { get; }  // test seam
internal static void PutPrimaryAssignmentForTesting(string peerId, string primaryId);
```

**Connected-component constraint** (review pass 1 P2-A): a session-wide `Dictionary<recordingId, primaryRecordingId>` cannot represent independent pair/window assignments. The implementation enforces "one primary per connected co-bubble component": `CoBubblePrimarySelector` walks the co-bubble graph (vertices = recordings, edges = co-bubble pairs), finds connected components, and applies §10.1 rules to pick ONE primary per component. All other recordings in that component are non-primary; the blender always queries via the component's primary, regardless of which specific peer pair is being rendered.

In a 3-vessel formation A↔B, B↔C (or fully A↔B↔C), if A wins by §10.1, both B and C have A as primary. The non-primaries can still render correctly relative to each other through the same primary because their offsets compose: `B_world = A_world + offset(A, B)`, `C_world = A_world + offset(A, C)`, so `B − C = offset(A, B) − offset(A, C)`.

The map is built inside `RebuildFromMarker` after the propagator runs, by calling `CoBubblePrimarySelector.Resolve(recordings, anchors, traces)`.

---

## 4. CoBubbleBlender API

### 4.1 New File `Source/Parsek/Rendering/CoBubbleBlender.cs`

```
internal enum CoBubbleBlendStatus : byte
{
    Hit = 0,                     // offset valid, returned with full magnitude
    HitCrossfade = 1,            // offset valid but blended toward standalone
    MissNotInTrace = 2,          // recording has no co-bubble trace at all
    MissOutsideWindow = 3,       // ut outside any [startUT, endUT]
    MissCrossfadeOut = 4,        // past crossfade tail; standalone fully wins
    MissPrimaryNotResolved = 5,  // PrimaryByPeer has no entry (selection failed)
    MissPrimaryRecordingMissing = 6, // primary id resolves but recording absent
    MissPrimaryStandaloneFailed = 7, // P_render(t) sub-evaluation returned no value
    MissPeerValidationFailed = 8,    // per-trace peer cache key drift
    MissDisabledByFlag = 9,      // useCoBubbleBlend == false
    MissRecursionGuard = 10,     // primary recursion attempted (cycle defense)
}

internal static class CoBubbleBlender
{
    // Main consumer hook. Caller passes the peer ghost's recordingId and current
    // playback UT; on Hit / HitCrossfade, worldOffset is the world-frame translation
    // to ADD to the primary's P_render(t). Caller is responsible for computing
    // P_render(t) via the standalone Stages 1+2+3+4 path and adding the offset.
    internal static bool TryEvaluateOffset(
        string peerRecordingId,
        double ut,
        out Vector3d worldOffset,
        out CoBubbleBlendStatus status,
        out string primaryRecordingId);

    // Test seams (mirroring AnchorPropagator's seam pattern).
    internal static System.Func<string, double, byte?> FrameTagOverrideForTesting;
    internal static System.Func<string, CelestialBody> BodyResolverForTesting;
    internal static void ResetForTesting();
}
```

### 4.2 Body-Frame to World Lift at Playback

Stored offsets are in the **primary's** frame (per §10.2). Re-lift to world at playback:

- `FrameTag == 0` (body-fixed): the offset is already a body-frame Vector3d; world delta is just `Dx, Dy, Dz` (interpreted as a translation in the primary segment's body-fixed frame at UT, which equals the world-frame translation at that UT because `body.bodyTransform.rotation` factors out — both peer and primary world positions were taken in the same body's rotating frame).
- `FrameTag == 1` (inertial): the offset is in the primary's body-centered inertial frame. Apply the inverse of the inertial→world rotation at the playback UT — equivalent to calling `TrajectoryMath.FrameTransform.LowerFromInertialToWorld(0, lon=Dx, alt=Dz, body, ut)` rebased to a translation. **Concrete:** since the offset is a translation (not a position), the lift/lower amounts to a single rotation by `body.bodyTransform.rotation` (or its inverse) — encapsulate this as a new helper `internal static Vector3d LowerOffsetFromInertialToWorld(Vector3d offset, CelestialBody body, double ut)` next to the existing `LowerFromInertialToWorld`.

### 4.3 Crossfade at Window Exit

When the playback UT enters the last `crossfadeDurationSeconds` of a window:

```
double tailFraction = (window.EndUT - ut) / crossfadeDurationSeconds;
double blend = clamp01(tailFraction);     // 1 at start of crossfade, 0 at end
worldOffset = blend * worldOffsetFromTrace;
status = HitCrossfade;
```

Past the window end, `worldOffset = 0` and status is `MissCrossfadeOut`. The renderer adds nothing — Stages 1+3+4 alone position the ghost. This satisfies §6.5: "Crossfade is on rendered POSITION only — no data merging."

### 4.4 Trace Sample Lookup

Inside the blender, locate the right trace in `SectionAnnotationStore` (extended in §6 below) by `(peerRecordingId, ut)`:

1. `SectionAnnotationStore.TryGetCoBubbleTraces(peerRecordingId, out List<CoBubbleOffsetTrace>)`.
2. Linear scan for trace whose `[StartUT, EndUT]` contains UT (typical recording has a handful of windows; binary search optional).
3. Sample interpolation at UT: bracket `UTs` array, lerp `(Dx, Dy, Dz)`.
4. Apply per-trace peer validation (§17.3.1) before returning Hit.

---

## 5. ParsekFlight Integration

### 5.1 Insertion Site

`InterpolateAndPosition` (`ParsekFlight.cs:14925`). The Phase 5 hook lands **between the body-resolution step and the Phase 1+4 spline evaluation**, around line `:14982`. Concretely (pseudocode of the hook gate):

```csharp
// Phase 5 co-bubble blend (design doc §6.5, §10, §18 Phase 5).
// On hit: U_render(t) = P_render(t) + worldOffset, where P_render is computed
// via the standalone Stages 1+2+3+4 path against the PRIMARY's recording.
// On miss: fall through to existing Stages 1+2+3+4 against this recording.
// HR-15: P_render reads from the primary's recording, never live KSP state.
if (allowCoBubbleBlend(recordingId, targetUT,
        out Vector3d worldOffset, out string primaryRecordingId,
        out Parsek.Rendering.CoBubbleBlendStatus blendStatus))
{
    if (TryComputeStandalonePRenderForPrimary(
            primaryRecordingId, targetUT, bodyBefore, out Vector3d pRender))
    {
        Vector3d coBubblePos = pRender + worldOffset;
        // Position; rotation continues from the existing slerp.
        ghost.transform.position = coBubblePos;
        ghost.transform.rotation = bodyBefore.bodyTransform.rotation * interpolatedRot;
        // Bypass the spline + ε path; still register for LateUpdate
        // re-positioning and emit the velocity result.
        ghostPosEntries.Add(...); // mode = GhostPosMode.CoBubble
        interpResult = new InterpolationResult(...);
        RecordCoBubbleEvalForLogging();
        return;
    }
    // P_render failed — fall through to standalone, log Verbose.
    ParsekLog.Verbose("Pipeline-CoBubble",
        $"Trace miss: P_render failed for primary={primaryRecordingId} ut={targetUT}");
}

// ... existing Stages 1+2+3+4 path unchanged ...
```

### 5.2 `TryComputeStandalonePRenderForPrimary` (recursion containment)

A new internal helper that **recursively but with depth guard = 1** computes the primary's standalone position. It must NOT call `allowCoBubbleBlend` for the primary's recording — primaries always render standalone (per §6.5: "primaries always render standalone, never as co-bubble peers").

Implementation: extract the existing Stages 1+2+3+4 code path from `InterpolateAndPosition` into a new pure-static helper `TryComputeStandaloneWorldPositionPure(string recordingId, int sectionIndex, double targetUT, CelestialBody body, ...) : bool`. The Phase 5 hook calls this helper directly (no recursion through `InterpolateAndPosition` itself), so there is no risk of cycle.

For the section index lookup of the primary's recording, use `TrajectoryMath.FindTrackSectionForUT(primary.TrackSections, targetUT)` and the primary's own `Recording.Points` list. The helper interpolates the primary's nearest two points, applies its spline + ε, and returns world position.

### 5.3 LateUpdate

A new `GhostPosMode.CoBubble` enum value. In LateUpdate, the entry stores `(peerRecordingId, primaryRecordingId, pointUT)`; the LateUpdate path re-evaluates primary `P_render` (via `TryComputeStandaloneWorldPositionPure`) and adds the freshly-evaluated offset (`CoBubbleBlender.TryEvaluateOffset`) — same as the Phase 1+4 LateUpdate spline re-evaluation pattern (see `TryComputeLateUpdateSplineWorldPositionPure`, `:15245`). This ensures FloatingOrigin shifts don't desync the peer from the primary.

### 5.4 Live-Primary Subtlety (§10.4 / HR-15)

When the primary is the live re-fly recording (`primaryRecordingId == marker.ActiveReFlyRecordingId`):

- `TryComputeStandaloneWorldPositionPure` for the primary still reads from the *primary's recording's* `Points` list, NOT from the live `Vessel`'s runtime state.
- The primary's anchor ε already came from `RebuildFromMarker`'s frozen-once live read at the bp.UT. After that, the renderer never reads live state again (HR-15).
- Result: peer ghost follows primary's recorded canonical path + recorded offset. Player input on live primary does not move peer ghost. This matches the §10.4 contract and is explicitly tested (see §9 below).

---

## 6. `.pann` Schema + Persistence

### 6.1 Schema Extension

The `.pann` `CoBubbleOffsetTraces` block already has its layout declared in §17.3.1 (lines 830-843 of the design doc) and the writer currently emits `count = 0` (`PannotationsSidecarBinary.cs:482`). Phase 5 implements the populated read/write path.

**Per-entry schema (already in §17.3.1, Phase 5 implements it):**

```
peerRecordingId            : length-prefixed UTF-8 string
peerSourceFormatVersion    : int32
peerSidecarEpoch           : int32
peerContentSignature       : 32 bytes
startUT, endUT             : double, double
frameTag                   : byte
sampleCount                : int32
uts                        : double[sampleCount]
dx                         : float32[sampleCount]
dy                         : float32[sampleCount]
dz                         : float32[sampleCount]
primaryDesignation         : byte
```

**`MaxCoBubbleTraceEntries = 1000`** is already declared as a count cap (`PannotationsSidecarBinary.cs:81`). Add `MaxCoBubbleSamplesPerTrace = 100_000` as the per-trace UT-array cap, and per-element minimum byte cost = 20 (8 + 4 + 4 + 4 = double UT + 3×float dx/dy/dz) for the `ValidateCount` stream-length sanity check.

### 6.2 `PannotationsSidecarBinary` Code Changes

**Read path (`TryRead`):** replace the current `coBubbleCount != 0 → reject` line (`:364-368`) with a real reader producing `List<KeyValuePair<string, CoBubbleOffsetTrace>>` — keyed by source recording id (the recording the `.pann` belongs to is implicit; the trace's `peerRecordingId` is the *other* side). `TryRead`'s signature gains a new out parameter:

```csharp
internal static bool TryRead(
    string path, PannotationsSidecarProbe probe,
    out List<KeyValuePair<int, SmoothingSpline>> splines,
    out List<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates,
    out List<CoBubbleOffsetTrace> coBubbleTraces,         // new
    out string failureReason);
```

**Write path (`Write`):** replace the `writer.Write(0); // CoBubbleOffsetTraces` line (`:482`) with a real writer that consumes a new `IList<CoBubbleOffsetTrace> coBubbleTraces` parameter (defaults to null/empty for callers that haven't been updated).

**Bump `AlgorithmStampVersion` from 4 → 5** (line `:125`). Comment explains: "Phase 5 introduces co-bubble offset traces; existing v4 .pann files lack the populated block and must be invalidated via alg-stamp-drift on first load (HR-10)."

### 6.3 ConfigurationHash Encoding Update

The design doc's §17.3.1 ConfigurationHash table already lists `coBubbleBlendMaxWindow` (float64) and `coBubbleResampleHz` (float32) as reserved bytes [39..46] and [47..50] in `PannotationsSidecarBinary.ComputeConfigurationHash` (currently zero-padded). Phase 5 wires these from the new `CoBubbleConfiguration.Default`:

```csharp
w.Write((double)CoBubbleConfiguration.Default.BlendMaxWindowSeconds); // [39..46]
w.Write((float)CoBubbleConfiguration.Default.ResampleHz);             // [47..50]
```

Plus add a new byte at offset [52]: `useCoBubbleBlend` (see §7 below).

**`CanonicalEncodingLength` bumps 52 → 53.** The `ComputeConfigurationHash(SmoothingConfiguration)` legacy overload defaults `useCoBubbleBlend = true` for back-compat (mirrors the Phase 6 `useAnchorTaxonomy` overload pattern at `PannotationsSidecarBinary.cs:498-505`).

The `CrossfadeDurationSeconds` is *not* in the canonical hash because crossfade is purely a render-time visual decision; changing it doesn't invalidate stored traces. This is justified by analogy with the smoothing tension being in the hash (it does change stored coefficients) vs. the rendering crossfade being purely consumer-side.

### 6.4 Per-Trace Validation Flow (§17.3.1 "Per-Trace Peer Validation")

In `SmoothingPipeline.LoadOrCompute` (after the existing whole-file probe + drift check at `SmoothingPipeline.cs:360-421`), add a new partial-validation pass for the `CoBubbleOffsetTraces` block:

```csharp
foreach (CoBubbleOffsetTrace trace in coBubbleTraces) {
    Recording peer = ResolvePeer(trace.PeerRecordingId);
    string driftReason = ClassifyTraceDrift(trace, peer);
    if (driftReason == null) {
        SectionAnnotationStore.PutCoBubbleTrace(rec.RecordingId, trace);
    } else {
        ParsekLog.Info("Pipeline-CoBubble",
            $"Per-trace co-bubble invalidation: recordingId={rec.RecordingId} " +
            $"peerRecordingId={trace.PeerRecordingId} reason={driftReason}");
        // Discard this trace only; other traces in the block stay.
        // The detector will re-run on first co-render demand.
    }
}
```

Where `ClassifyTraceDrift` returns one of:

- `peer-missing` — peer recording not in `RecordingStore.CommittedRecordings` and not in the tree-local load set (review pass 2 P1-A: same-tree peers being hydrated in the same OnLoad pass are visible via the load set before they're committed)
- `peer-format-changed` — peer's `RecordingFormatVersion != trace.PeerSourceFormatVersion`
- `peer-epoch-changed` — peer's `SidecarEpoch != trace.PeerSidecarEpoch`
- `peer-content-mismatch` — recomputed peer content signature != stored
- `null` (accept) — including the **deferred-validation path** (review passes 3 + 4): when `peer.Points` is empty during validation (sequential OnLoad sidecar hydration: same-tree peers iterated AFTER the current recording have empty Points when its `.pann` is validated), `ClassifyTraceDrift` enqueues a `DeferredCoBubbleValidation(ownerRecordingId, peerRecordingId, startUT, endUT, expectedSignature)` and returns null. `ParsekScenario.OnLoad` invokes `SmoothingPipeline.RevalidateDeferredCoBubbleTraces(tree.Recordings)` after every recording in the tree has hydrated; the sweep recomputes the signature against the now-populated peer and drops mismatches with `Pipeline-CoBubble` Info `Per-trace co-bubble invalidation (post-hydration): owner={…} peer={…} startUT={…} endUT={…} reason=peer-content-mismatch` (or `reason=peer-still-not-hydrated` if the peer's Points list is still empty by sweep time, e.g. its `.prec` failed to load entirely). The runtime per-trace check in `CoBubbleBlender` only validates format / epoch — it does NOT recompute the signature — so without the post-hydration sweep an OnLoad-time deferral would silently leave stale offsets installed for the entire session.

### 6.5 Lazy Recompute on Demand

When `SmoothingPipeline.LoadOrCompute` recomputes for a recording (cache miss / alg-stamp drift / config-hash drift), it invokes `CoBubbleOverlapDetector.DetectAndStoreCoBubbleTracesForRecording(rec, treeLocalLoadSet)` after `FitAndStorePerSection`. This:

1. Scans the recording against every other known recording (load set + committed list) for overlap windows.
2. Builds traces and writes them via `SectionAnnotationStore.PutCoBubbleTraces` for BOTH sides (peer-symmetric in-memory storage).
3. The recompute path then calls `PersistPeerPannFiles(rec, peerRecordingsToPersist, expectedHash)` (review pass 3 P3-1) so the affected peers' `.pann` files are also rewritten — keeps on-disk state symmetric without waiting for each peer's own `LoadOrCompute` to fire.

**Primary-map refresh after lazy recompute** (review pass 1 P1-C originally; review pass 3 confirmed this works via existing infrastructure): newly-installed traces become visible to the next `RenderSessionState.RebuildFromMarker` call (scene transitions, re-fly entry, save/load), which re-invokes `CoBubblePrimarySelector.Resolve(recordings, anchors, traces)`. Phase 5 does NOT introduce a separate `RefreshPrimaryAssignmentsForPair` helper — the existing rebuild-driven refresh is sufficient because lazy recompute only happens during `LoadOrCompute` (which itself runs during scene transitions / OnLoad) and is therefore always followed by a `RebuildFromMarker` in the same flow.

If a future code path triggers lazy recompute mid-session WITHOUT a paired marker rebuild, that path must explicitly re-invoke the selector — but no such path exists in Phase 5.

The per-pair "already attempted this session" recompute guard is implicit: `LoadOrCompute` runs at most once per recording per session, so a permanently-missing peer doesn't trigger recompute attempts every frame.

---

## 7. Settings Flag — `useCoBubbleBlend`

### 7.1 Add to `ParsekSettings.cs`

Following the Phase 6 `useAnchorTaxonomy` pattern (`ParsekSettings.cs:151-164`):

```csharp
[GameParameters.CustomParameterUI("Use co-bubble blend",
    toolTip = "When on (Phase 5), close-formation ghosts blend toward sub-meter offsets stored in the recording. Off → standalone Stages 1-4 only.")]
public bool useCoBubbleBlend
{
    get { return _useCoBubbleBlend; }
    set {
        if (_useCoBubbleBlend == value) return;
        bool prev = _useCoBubbleBlend;
        _useCoBubbleBlend = value;
        NotifyUseCoBubbleBlendChanged(prev, value);
        if (ParsekSettingsPersistence.IsReconciled)
            ParsekSettingsPersistence.RecordUseCoBubbleBlend(value);
    }
}
private bool _useCoBubbleBlend = true;

internal static void NotifyUseCoBubbleBlendChanged(bool oldValue, bool newValue) {
    if (oldValue == newValue) return;
    ParsekLog.Info("Pipeline-CoBubble", $"useCoBubbleBlend: {oldValue}->{newValue}");
}
```

### 7.2 Add to `ParsekSettingsPersistence.cs`

Mirror the `useAnchorTaxonomy` pattern: `UseCoBubbleBlendKey` const, `storedUseCoBubbleBlend` nullable, `RecordUseCoBubbleBlend`, `GetStoredUseCoBubbleBlend`, `SetStoredUseCoBubbleBlendForTesting`, and the read/write of the key inside the existing `Save()` / `LoadIfNeeded()` paths.

### 7.3 Why a Flag Is Justified

Three reasons:

1. **Rollout bisection.** Phase 5 changes the position of the most-visible ghosts (close-formation peers). A flag lets a developer attribute a visual regression to the toggle moment; all prior phases (1, 2, 3, 4, 6) shipped with this exact pattern.
2. **HR-9 fallback.** Off → consumer falls through to standalone Stages 1+2+3+4. The codebase already exhibits visible-failure behavior at meter scale; off is a known-acceptable visual.
3. **Perf safety valve.** The Phase 5 hot path adds one offset lookup + one body-rotation per peer per frame. If a user reports lag in a 10-ghost formation scenario, the flag is a one-click rollback.

### 7.4 ConfigurationHash Inclusion

The flag participates in the `.pann` `ConfigurationHash` (offset [52], length 52→53). When flipped, every `.pann` that has co-bubble traces will fail `config-hash-drift` on the next load and recompute. This is correct because: when the flag is off, the `CoBubbleOffsetTraces` block writer emits an empty list; flipping to on without invalidating would leave a stale empty block masquerading as fresh.

---

## 8. Logging Contract (§19.2 Stage 5)

### 8.1 Stage 5 Log Lines (already specified in §19.2)

All under tag `Pipeline-CoBubble`:

| Event | Level | Dedup key |
|---|---|---|
| Primary selection | Info | `(recordingA, recordingB)` per session — fire once per selected pair |
| Blend window enter | Info | `(peer, primary, startUT)` — once per peer per window |
| Blend window exit | Info | `(peer, primary, exitUT, reason)` — once per window |
| Crossfade frame | Verbose | `VerboseRateLimited` shared key `"cobubble-crossfade"`, 5 s |
| Trace miss → standalone | Verbose | `VerboseRateLimited` per `(peerRecordingId, status)`, 30 s |
| Per-trace invalidation | Info | `(recordingId, peerRecordingId)` once per session per pair |
| `useCoBubbleBlend` flag flip | Info | n/a (per-flip) |

### 8.2 Per-Frame Counters

Extend the existing pipeline frame summary in `RecordSplineEvalForLogging` with a parallel `RecordCoBubbleEvalForLogging` that increments `s_coBubbleEvalCount` and contributes a `coBubbleEvals=N` field to the same 1-second `[Pipeline-Smoothing] frame summary` Verbose line (or a separate `[Pipeline-CoBubble] frame summary` line — design doc §19.3 lists `frameCoBubbleEvalCount` as one of the counters).

Decision: separate `[Pipeline-CoBubble]` frame summary line, dedup'd separately, so each pipeline stage owns its own per-second emission. Mirrors the Phase 1 pattern at `ParsekFlight.cs:15310-15330`.

### 8.3 Dedup State

Add to `RenderSessionState` (alongside the existing Phase 3/Phase 6 dedup sets at line `:85-101`):

```csharp
private static readonly HashSet<string> CoBubblePrimarySelectionLogged = new HashSet<string>();
private static readonly HashSet<string> CoBubbleWindowEnterLogged = new HashSet<string>();
private static readonly HashSet<string> CoBubbleWindowExitLogged = new HashSet<string>();
private static readonly HashSet<string> CoBubbleTraceInvalidationLogged = new HashSet<string>();

internal static void NotifyCoBubblePrimarySelection(...);
internal static void NotifyCoBubbleWindowEnter(...);
internal static void NotifyCoBubbleWindowExit(...);
internal static void NotifyCoBubbleTraceInvalidation(...);
```

Cleared by `ResetSessionDedupSetsLocked` (line `:267-274`) on every `RebuildFromMarker` and `Clear`.

---

## 9. Test Plan

### 9.1 New xUnit Test File: `Source/Parsek.Tests/Rendering/CoBubbleBlenderTests.cs`

Pattern: `[Collection("Sequential")]`, `IDisposable`, `ParsekLog.TestSinkForTesting` capture, `ResetForTesting()` in Dispose. Mirrors `AnchorPropagationTests.cs` shape.

Test cases (one `[Fact]` each, all with explicit "what makes it fail" comment):

1. **`PrimarySelection_LiveAlwaysWins`** — A has `LiveSeparation` anchor, B does not; A primary regardless of recording-id ordering. Failure: live not preferred → §7.11 priority broken.
2. **`PrimarySelection_DAGAncestryTiebreaker`** — neither has live; A is one DAG hop from a live anchor, B is two hops. A primary. Failure: hop-count comparison wrong direction.
3. **`PrimarySelection_EarlierStartUT`** — DAG-ancestry ties; A.StartUT < B.StartUT. A primary.
4. **`PrimarySelection_HigherSampleRate`** — StartUT ties; A's overlap-window section has sampleRateHz=10, B has 4. A primary.
5. **`PrimarySelection_StableTieBreaker`** — every rule ties; lower Ordinal recordingId wins. Failure: HR-3 violated.
6. **`PrimarySelection_DeterministicAcrossRebuilds`** — call `RebuildFromMarker` twice with same inputs; assert identical primary map. Failure: HR-3 violated.
7. **`SnapshotFreezing_LivePrimaryOffsetIndependentOfRuntime`** — set up live primary + peer; capture frozen pos; mutate the synthetic "live vessel" position via the test seam; call `TryEvaluateOffset` again. Offset must be identical. Failure: naive-relative trap (§3.4) re-introduced.
8. **`Crossfade_FullMagnitudeAtMidWindow`** — UT in the middle of [startUT, endUT]; `worldOffset ≈ trace value`, status `Hit`.
9. **`Crossfade_RampDownInTail`** — UT at endUT - 0.5s with crossfadeDuration=1.5s; offset magnitude = (1/3) × full; status `HitCrossfade`.
10. **`Crossfade_ZeroPastWindow`** — UT > endUT; status `MissCrossfadeOut`, offset zero.
11. **`Crossfade_ExitReasons_Separation`** — synthetic recordings where peers separate beyond bubble radius mid-overlap; `Detect` truncates window at separation UT; logged exit reason = `separation`.
12. **`Crossfade_ExitReasons_TimeJump`** — peer recording has `SegmentEvent.TimeJump` mid-overlap; window splits.
13. **`Crossfade_ExitReasons_Destruction`** — peer has terminal `BranchPoint` (`Breakup`/`Terminal`) mid-overlap; window truncated.
14. **`FrameTag_BodyFixedRoundTrip`** — synthetic Atmospheric overlap; trace built and re-lifted; offset within float32 precision of input.
15. **`FrameTag_InertialRoundTrip`** — synthetic ExoPropulsive overlap with body rotation; trace lifted to inertial at recording UT, lowered to world at playback UT (different UT). Offset matches input within numerical tolerance.
16. **`FrameTag_FrameMismatchSplits`** — primary segment is body-fixed, peer segment is inertial during overlap; `Detect` produces no trace OR splits at the frame boundary. (Per §10.2 wording, the trace is pinned by the *primary's* render frame; peer-side just lifts/lowers via primary's body. Pin the precise behavior in this test.)
17. **`Recursion_PRimaryStandaloneOnly`** — synthetic case where primary A is a peer of secondary recording C in a different pair; `TryEvaluateOffset` for A must return `MissNotInTrace` (primary always renders standalone) — no recursion through `CoBubbleBlender` to compute A.

### 9.2 New xUnit Test File: `Source/Parsek.Tests/Rendering/CoBubbleSidecarRoundTripTests.cs`

(Or extend the existing `PannotationsSidecarRoundTripTests.cs` — mirror pattern.)

1. **`Write_Read_RoundTrip_PreservesAllTraceFields`** — write a `.pann` with 3 traces (different frame tags, different peer ids), probe + read, assert byte-for-byte recovery of every field.
2. **`AlgStampMismatch_DiscardsWholeFile`** — write `.pann` with `AlgorithmStampVersion=4`; load with current stamp=5; whole file discarded, recompute scheduled.
3. **`PerTracePeerEpochMismatch_DiscardsOnlyTrace`** — peer has `SidecarEpoch=10` at trace-build time; mutate peer to `SidecarEpoch=11`; load. The affected trace is dropped, the whole file is *not* discarded, splines + anchor candidates remain accessible. Assert `[Pipeline-CoBubble] Per-trace co-bubble invalidation` Info log emitted with `reason=peer-epoch-changed`.
4. **`PerTracePeerFormatMismatch_DiscardsOnlyTrace`** — same shape, peer `RecordingFormatVersion` differs.
5. **`PerTracePeerContentSignatureMismatch_DiscardsOnlyTrace`** — peer raw bytes inside `[startUT, endUT]` mutated; signature recompute differs; trace dropped.
6. **`PerTracePeerMissing_DiscardsOnlyTrace`** — peer recording not in `RecordingStore.CommittedRecordings`; trace dropped, `reason=peer-missing`.
7. **`MalformedTraceCount_PayloadCorruptDiscardsWholeFile`** — write a `.pann` with `coBubbleCount = MaxCoBubbleTraceEntries + 1`; load fails with `payload-corrupt`, whole-file recompute.

### 9.3 New xUnit Test File: `Source/Parsek.Tests/Rendering/UseCoBubbleBlendSettingTests.cs`

Mirror `UseAnchorTaxonomySettingTests.cs` line-for-line:

1. **`UseCoBubbleBlend_DefaultsTrue`**
2. **`UseCoBubbleBlend_PersistsAcrossLoad`**
3. **`UseCoBubbleBlend_FlipLogsInfo`** — assert `[Pipeline-CoBubble] useCoBubbleBlend: false->true` Info line.
4. **`UseCoBubbleBlend_NoLogWhenUnchanged`**
5. **`UseCoBubbleBlend_ConfigHashChangesOnFlip`** — `PannotationsSidecarBinary.ComputeConfigurationHash(cfg, true)` ≠ `ComputeConfigurationHash(cfg, false)` — would catch the byte-52 omission.

### 9.4 Extend `SmoothingPipelineLoggingTests.cs` and Add `CoBubbleLoggingTests.cs`

Log-assertion tests (`ParsekLog.TestSinkForTesting`):

1. **`PrimarySelection_LogsOncePerPair`** — two pairs in scope; assert exactly two `Primary selection` Info lines.
2. **`WindowEnter_WindowExit_PairLog`** — drive a session through enter and exit; assert exactly one of each.
3. **`CrossfadeFrame_RateLimitedSharedKey`** — 100 simulated frames in crossfade; assert ≤ 25 verbose lines (5-second rate limit gives 5 lines per 25 s, etc.).
4. **`TraceMiss_LoggedOncePerStatusPerSession`** — repeated misses with `MissPrimaryNotResolved`; assert exactly 1 line per `(peerRecordingId, status)`.

### 9.5 In-Game Tests in `RuntimeTests.cs`

Following the `Pipeline_Anchor_LiveSeparation` pattern (`RuntimeTests.cs:13755`):

1. **`Pipeline_CoBubble_Live`** — synthetic live-primary re-fly with one ghost peer at known recorded relative offset; spawn live vessel at re-fly point, drive 5 seconds of playback, assert peer ghost's world position remains `live_recorded_world(ut) + recorded_offset(ut)` within 0.05 m. Mutate live vessel's runtime position mid-frame; assert peer ghost is unchanged (HR-15 audit).
2. **`Pipeline_CoBubble_GhostGhost`** — two ghost recordings in tight formation, no live; rewind before both. Drive 30 seconds of playback. Assert: (a) primary selection picks the deterministic winner (§10.1 rules), (b) peer's world position preserves the recorded offset within 0.05 m, (c) `[Pipeline-CoBubble] Primary selection` log emitted.

### 9.6 Synthetic Recording Fixture

`Source/Parsek.Tests/Fixtures/Pipeline/pipeline-cobubble-formation.prec` (per §20.5 Phase 5 row). Two parallel recordings, both ABSOLUTE-frame ExoBallistic sections, recorded relative offset = `(10.0, 0.0, 0.0)` meters in body-fixed frame, 60-second duration. Generated by extending `RecordingBuilder` with a new `WithCoBubbleSibling(Recording other, double startUT, double endUT, Vector3d offset)` helper — emits matching `Background`-source `TrackSection`s on both sides whose sample positions encode the desired offset.

---

## 10. CHANGELOG + todo doc updates

### 10.1 `CHANGELOG.md` — under `## 0.9.1` → new section `### Pipeline (Phase 5)`

```markdown
### Pipeline (Phase 5)

- Co-bubble overlap blend: ghosts that were close together in the original recording now stay
  centimetre-accurate to each other during playback. Decoupling debris stays attached to its
  parent vessel; formation flights preserve their recorded spacing across orbital motion.
  Player input on a live re-fly does not move the peer ghost — the peer follows the canonical
  recording, not the live state.
- New `useCoBubbleBlend` setting (default on) gates the new behaviour; off falls back to
  Phase 1-4 standalone rendering.
```

### 10.2 `docs/dev/todo-and-known-bugs.md`

Strike Phase 5 line; add follow-ups for Phase 7-9 to mention they may slightly improve co-bubble fidelity. Add new known-bug entry: "co-bubble traces are lazy on first co-render — first frame after entering a co-bubble window may show standalone-fidelity offset for one frame while detector populates."

### 10.3 `.pann` Algorithm Stamp Bump

`AlgorithmStampVersion = 5` bump in `PannotationsSidecarBinary.cs:125` — comment block to be appended explaining "Phase 5: CoBubbleOffsetTraces transitions from always-empty (count=0) to populated by CoBubbleOverlapDetector. Existing v4 .pann files lack the populated block and would force a runtime fallback for every consumer; the bump triggers alg-stamp-drift on first load (HR-10)."

---

## 11. Risks / Open Questions

### 11.1 Bubble-Membership Detection Confidence: HIGH

The `TrackSectionSource.{Active,Background}` derivation rule (§1.1 above) is well-supported by the existing `BackgroundRecorder` packed-vessel filter (`BackgroundRecorder.cs:1417-1424`). The only edge case is a vessel that loaded into the bubble for ≤ 1 sample interval — which produces a Background section but represents a single-sample overlap with no realistic blend benefit. The detector's `minWindowDurationS = 0.5` threshold rejects these.

**Risk:** if a future BG-recorded behaviour change emits Background sections without physics-active state, the rule breaks silently. Mitigation: Phase 5 unit tests assert that a synthetic Background section paired with the right body+frame produces a trace, and a Checkpoint section never does. A regression in `BackgroundRecorder` would be caught by the existing on-rails-no-tracksections invariant test plus our new Phase 5 fixture.

### 11.2 `coBubbleResampleHz` Value Choice

Picked 4 Hz as a starting point. The recorder's "Medium" sampling preset gives sample intervals of 0.2-3.0 s (`ParsekSettings.cs:188-201`); 4 Hz (= 0.25 s sample period) is at the upper end — sufficient for sub-meter relative geometry given that common-mode noise has already cancelled.

**Risk:** for high-velocity formation flying (orbital rendezvous closing rate), 4 Hz might alias velocity-induced offset oscillations. **Mitigation:** the value is in the `ConfigurationHash` so empirical bumping is one-line, and the canonical encoding rejects stale `.pann` automatically. Document the empirical-tuning point in §22.1 and on the `useCoBubbleBlend` flag tooltip.

### 11.3 Frame-Tag Mismatch Across Peers (§10.2 Wording)

§10.2 says "The trace's frame is pinned per-segment by the **primary's** render frame." If primary segment is body-fixed (Atmospheric) and peer segment is inertial (ExoPropulsive) during the overlap window, the trace is **stored in the primary's body-fixed frame** and the peer-side render lifts/lowers using the **primary's body rotation phase** at playback UT, NOT the peer's.

The `CoBubbleBlender.TryEvaluateOffset` API takes the peer's recordingId, but the lift-back-to-world step uses the primary's body and UT. The primary's body name is captured in `OverlapWindow.BodyName` and persisted on the trace (per §6 schema, the primary's frame tag is `frameTag` byte). **Pinned in the test plan as item 9.1.16 above — explicit assertion.**

### 11.4 Recursion Containment — Pinned Test Plan Item 9.1.17

A primary that is itself a peer of *another* primary (multi-tier formation) must NOT recurse through `CoBubbleBlender` for its primary's `P_render`. The implementation guard is structural: `TryComputeStandaloneWorldPositionPure` (§5.2) does not call `CoBubbleBlender` at all — it only walks the standalone Stages 1+2+3+4 path. So recursion depth = 1 by construction. Test 9.1.17 asserts this.

### 11.5 Designated Primary Stability vs. Live Vessel Going In/Out of Bubble

§22.2 open question. If during a 60-second co-bubble window the live vessel briefly leaves the bubble (out-of-physics-range debris case, §15.7), does primary designation change? **Phase 5 decision:** primary designation is computed once at session entry from the **recorded** state (same recordings, same anchor map → same primary). Live-vessel runtime physics state does NOT affect primary selection. This satisfies §22.2's "deterministic tie-breaking from recording metadata."

### 11.6 Performance — 1 KB/window Memory Budget

A 60-second window at 4 Hz = 240 samples × (8 + 12) bytes = ~5 KB per trace. 100 windows in a heavy multi-recording session = 500 KB in memory. Well within budget. Disk: similar. The detector's 1000-trace cap (`MaxCoBubbleTraceEntries`) bounds this at ~5 MB, also safe.

### 11.7 Test for Live-Primary `P_render` Reading From Recording, Not Runtime KSP State

Test 9.1.7 (`SnapshotFreezing_LivePrimaryOffsetIndependentOfRuntime`) plus in-game test `Pipeline_CoBubble_Live` (§9.5.1) cover this. The xUnit test mutates the synthetic live position via the test seam mid-frame; the in-game test exercises a real KSP `Vessel` with player input.

---

## 12. Step-by-Step Implementation Sequence

The Implement agent should land Phase 5 in the order below. Each commit is independently `dotnet test`-clean and ends with the required CHANGELOG/todo doc updates per `.claude/CLAUDE.md`. **No `Co-Authored-By` lines** in commit messages. **Stay in the `Parsek-ghost-trajectory-rendering-design/` worktree on `feat/cobubble-blend-phase5`.**

### Commit 1 — Co-bubble configuration + settings flag

Files touched:
- `Source/Parsek/PannotationsSidecarBinary.cs` — add `CoBubbleConfiguration` struct (Default with ResampleHz=4, BlendMaxWindow=600, CrossfadeDuration=1.5). Bump `CanonicalEncodingLength` 52→53. Wire the three values into `ComputeConfigurationHash` at offsets [39..50]. Add 1-byte `useCoBubbleBlend` at [52]. New 3-arg overload `ComputeConfigurationHash(SmoothingConfiguration, bool useAnchorTaxonomy, bool useCoBubbleBlend)` plus 2-arg back-compat overload. Bump `AlgorithmStampVersion = 5`.
- `Source/Parsek/ParsekSettings.cs` — add `useCoBubbleBlend` property + backing field + `NotifyUseCoBubbleBlendChanged`.
- `Source/Parsek/ParsekSettingsPersistence.cs` — add `UseCoBubbleBlendKey`, `storedUseCoBubbleBlend`, `RecordUseCoBubbleBlend`, `GetStoredUseCoBubbleBlend`, `SetStoredUseCoBubbleBlendForTesting`. Wire into the existing Save/Load paths.
- `Source/Parsek/Rendering/SmoothingPipeline.cs` — `CurrentConfigurationHash` reads the new flag through `ParsekSettings.Current?.useCoBubbleBlend ?? true` (dual-cache it alongside the existing useAnchorTaxonomy cache, line `:54-56`).
- `Source/Parsek.Tests/Rendering/UseCoBubbleBlendSettingTests.cs` — new (5 tests per §9.3).
- `Source/Parsek.Tests/Rendering/PannotationsSidecarRoundTripTests.cs` — extend `ConfigHash_Sensitivity` to assert byte-52 perturbation changes the hash.

Ends green: `dotnet test`. CHANGELOG entry under v0.9.1 → "Pipeline (Phase 5): added `useCoBubbleBlend` setting (default on)". Todo doc tracks Phase 5 as in-progress.

### Commit 2 — `CoBubbleOffsetTrace` data model + `.pann` schema implementation

Files touched:
- `Source/Parsek/Rendering/CoBubbleOffsetTrace.cs` — **new file**, the `internal sealed class CoBubbleOffsetTrace` per §2.2 above.
- `Source/Parsek/Rendering/SectionAnnotationStore.cs` — add `Dictionary<string, List<CoBubbleOffsetTrace>> CoBubbleTraces`, `PutCoBubbleTrace`, `TryGetCoBubbleTraces`, `RemoveCoBubbleTracesForRecording`. Extend `Clear` and `RemoveRecording` to clear the new map.
- `Source/Parsek/PannotationsSidecarBinary.cs` — replace the `writer.Write(0); // CoBubbleOffsetTraces` line at `:482` with a real writer; replace the `coBubbleCount != 0 → reject` reader at `:364-368` with a real reader. Extend `TryRead` signature with new out parameter `List<CoBubbleOffsetTrace> coBubbleTraces`. Add `MaxCoBubbleSamplesPerTrace = 100_000` cap.
- `Source/Parsek/Rendering/SmoothingPipeline.cs` — extend `LoadOrCompute` and `TryWritePann` to round-trip the new block; add the per-trace validation flow (§6.4) including the `[Pipeline-CoBubble] Per-trace co-bubble invalidation` Info log.
- `Source/Parsek.Tests/Rendering/CoBubbleSidecarRoundTripTests.cs` — new file (7 tests per §9.2).

Ends green. CHANGELOG: "Pipeline (Phase 5): `.pann` schema now persists co-bubble offset traces."

### Commit 3 — `CoBubbleOverlapDetector` (commit-time + lazy)

Files touched:
- `Source/Parsek/Rendering/CoBubbleOverlapDetector.cs` — **new file**. `Detect`, `BuildTrace`, `ComputePeerContentSignature`, `DetectAndPersist` per §1, §2.
- `Source/Parsek/Rendering/SmoothingPipeline.cs` — `FitAndStorePerSection` now also calls `CoBubbleOverlapDetector.Detect` at commit time when `useCoBubbleBlend` is on, and persists the resulting traces alongside splines/candidates. `LoadOrCompute` invokes `CoBubbleOverlapDetector.DetectAndPersist` lazily on first co-render demand (via a callback registered with `CoBubbleBlender`).
- `Source/Parsek.Tests/Rendering/CoBubbleOverlapDetectorTests.cs` — **new file**: pair detection across `Active`/`Background`/`Checkpoint` source matrix; minWindow filter; bubble-radius truncation; body-change split; TIME_JUMP split; structural-event split. Body-fixed and inertial frame round-trip.

Ends green. CHANGELOG updated to mention "co-bubble traces are detected at recording commit and lazy on first co-render."

### Commit 4 — `CoBubblePrimarySelector` + `RenderSessionState` integration

Files touched:
- `Source/Parsek/Rendering/CoBubblePrimarySelector.cs` — **new file**. `Resolve(recordings, anchors, traces) → Dictionary<string, string>` per §3.
- `Source/Parsek/Rendering/RenderSessionState.cs` — add `PrimaryByPeer` map + accessors + dedup sets per §3.4 / §8.3. Inside `RebuildFromMarker` (test overload), invoke `CoBubblePrimarySelector.Resolve` after the `AnchorPropagator.Run` call (line `:893-903`). Emit `[Pipeline-CoBubble] Primary selection` Info log per pair (dedup'd).
- `Source/Parsek/Rendering/AnchorPropagator.cs` — expose the existing edge-list builder as `internal static IEnumerable<Edge> EnumerateEdges(IReadOnlyList<RecordingTree>, IReadOnlyList<Recording>)` so the selector can compute DAG ancestry (§10.1 rule 2).
- `Source/Parsek.Tests/Rendering/CoBubbleBlenderTests.cs` — **new file**, partial (just the primary-selection group of tests, items 9.1.1–9.1.6).

Ends green. CHANGELOG: "Designated primary deterministic across save/load; live vessels always primary."

### Commit 5 — `CoBubbleBlender` + body-frame re-lift

Files touched:
- `Source/Parsek/Rendering/CoBubbleBlender.cs` — **new file**. `CoBubbleBlendStatus`, `TryEvaluateOffset`, frame-tag dispatch via the new `LowerOffsetFromInertialToWorld` helper.
- `Source/Parsek/TrajectoryMath.cs` — extend `FrameTransform` with `LowerOffsetFromInertialToWorld(Vector3d offset, CelestialBody body, double playbackUT) : Vector3d` (rotation-only inverse of inertial frame).
- `Source/Parsek.Tests/Rendering/CoBubbleBlenderTests.cs` — extend (items 9.1.7–9.1.17, including the snapshot-freeze test, crossfade, frame-tag round-trip, recursion containment).

Ends green. CHANGELOG: "Co-bubble blender produces world-frame offset for peer ghost render."

### Commit 6 — `ParsekFlight.InterpolateAndPosition` integration

Files touched:
- `Source/Parsek/ParsekFlight.cs` — extract Stages 1+2+3+4 standalone path into `internal static bool TryComputeStandaloneWorldPositionPure(...)`; add `allowCoBubbleBlend(...)` gate; insert the §5.1 hook around line `:14982`. Add `GhostPosMode.CoBubble`, extend the LateUpdate path to re-evaluate primary `P_render` + offset on entries with that mode. Add `RecordCoBubbleEvalForLogging` per §8.2.
- `Source/Parsek.Tests/Rendering/AnchorCorrectionConsumerHookTests.cs` — extend with `allowCoBubbleBlend` gate tests (flag off, no recording id, no primary in map, primary missing, P_render fails — each exercising one `CoBubbleBlendStatus` enum value).

Ends green. The xUnit suite covers the gate; in-game tests verify the integrated behaviour next.

### Commit 7 — In-game tests + synthetic fixture

Files touched:
- `Source/Parsek/InGameTests/RuntimeTests.cs` — add `Pipeline_CoBubble_Live` and `Pipeline_CoBubble_GhostGhost` per §9.5.
- `Source/Parsek.Tests/Generators/RecordingBuilder.cs` — add `WithCoBubbleSibling(...)` helper.
- `Source/Parsek.Tests/Fixtures/Pipeline/pipeline-cobubble-formation.prec` — generated fixture (regenerable via the `RegenerateFixtures` test target).
- `Source/Parsek/InGameTests/LogContractTests.cs` — add contract entries for the new `Pipeline-CoBubble` log lines (Info + Warn levels).

Ends green. CHANGELOG: "Phase 5 in-game tests: re-fly with co-bubble peer; ghost-ghost formation."

### Commit 8 — Documentation polish + Phase 5 done

Files touched:
- `CHANGELOG.md` — final consolidation of the Phase 5 section under v0.9.1.
- `docs/dev/todo-and-known-bugs.md` — mark Phase 5 complete; document the lazy-first-frame edge case (§11.6 above) as a known limitation.
- `docs/parsek-ghost-trajectory-rendering-design.md` — update §22.1 / §22.2 / §22.3 to mark them no-longer-deferred (concrete values shipped). Update §17.3.1 to reflect `AlgorithmStampVersion=5` and the populated `CoBubbleOffsetTraces` schema.

Final `dotnet test` green; manual smoke test in KSP via the `pipeline-cobubble-formation` fixture; verify the deployed DLL per the `.claude/CLAUDE.md` "Always verify the deployed DLL" recipe; confirm `[Pipeline-CoBubble]` log lines appear in `KSP.log` during the smoke test.

---

### Critical Files for Implementation

- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/Rendering/CoBubbleBlender.cs` (new)
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/Rendering/CoBubbleOverlapDetector.cs` (new)
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/PannotationsSidecarBinary.cs` (read/write `CoBubbleOffsetTraces`, `AlgorithmStampVersion 4→5`, `CanonicalEncodingLength 52→53`)
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/ParsekFlight.cs` (`InterpolateAndPosition` Phase 5 hook ~line 14982; `TryComputeStandaloneWorldPositionPure` extraction)
- `C:/Users/vlad3/Documents/Code/Parsek/Parsek-ghost-trajectory-rendering-design/Source/Parsek/Rendering/RenderSessionState.cs` (`PrimaryByPeer` map, dedup sets, primary-selector hook in `RebuildFromMarker`)
