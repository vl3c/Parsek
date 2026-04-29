I have enough context. Let me also confirm the existing reserved-block reader for OutlierFlagsList and how the schema needs to update.

Note the existing reader: in `TryRead`, `outlierCount` is checked and rejected if non-zero. That guard must be relaxed in Phase 8 to actually parse the entries. Let me now produce the comprehensive plan.

# Phase 8 Implementation Plan

Worktree: `C:/Users/vlad3/Documents/Code/Parsek/Parsek-phase8-outliers/` on `feat/outlier-rejection-phase8` (off `origin/feat/cobubble-blend-phase5` HEAD `db8fca10`).

Phase 8 adds environment-aware outlier rejection to the smoothing prelude (design doc §14, §18 Phase 8, §19.2 Outlier Rejection, §20.5 Phase 8 row, §17.3.1 `OutlierFlagsList` schema). It is `.pann`-only — no `.prec` schema bump (design doc §17.3.2 explicitly excludes Phase 8). The classifier runs BEFORE `SmoothingSpline.Fit`, the bitset is persisted in the new `OutlierFlagsList` block, and the spline `Fit` consumes the bitset to skip rejected samples.

## 1. Tuneable Thresholds

### 1.1 Where they live

A new `internal static class` `Parsek.Rendering.OutlierThresholds` (declared inside `OutlierClassifier.cs`, mirroring how `SmoothingConfiguration` lives next to `PannotationsSidecarBinary`). It is a pure-data POCO with a `Default` factory. It is canonically encoded into the ConfigurationHash so any tuning bumps the cache key (HR-10).

### 1.2 Per-environment maximum acceleration (m/s²)

These are upper bounds — anything above is implausible and is rejected as a kraken event, NOT a real burn. The numbers below reflect what KSP physics CAN produce in normal play, with comfortable headroom so a real player flight never trips a classifier.

| Environment        | Default ceiling | Justification                                                                                                                                                         |
|--------------------|-----------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Atmospheric`      | 500 m/s² (~51g) | Solid boosters + heavy lift can burst above 30g momentarily on staging; 500 m/s² is well above the 30g (294 m/s²) chemistry-rocket ceiling but low enough to catch a 1000+ m/s² kraken-tick. |
| `ExoPropulsive`    | 200 m/s² (~20g) | High-TWR Vector engines on a near-empty stage; 20g is well above realistic; krakens hit thousands.                                                                  |
| `ExoBallistic`     | 50 m/s² (~5g)   | Unpowered ballistic — should equal local gravity. Kerbin surface gravity is 9.81 m/s²; SOI bodies range 1.6–17 m/s². 50 cap accommodates near-surface ExoBallistic above the densest world without rejecting real samples.  |
| `SurfaceMobile`    | 30 m/s² (~3g)   | Rovers; over-cap is a wheel-collider explosion or kraken slide.                                                                                                       |
| `SurfaceStationary`| 10 m/s² (~1g)   | Should be ~0. Anything above is a physics jitter; a strict 10 m/s² catches micro-krakens without rejecting normal landing-jitter samples.                            |
| `Approach`         | 50 m/s² (~5g)   | Same as ExoBallistic — Approach is unpowered descent on airless body.                                                                                                 |

Reviewer test: each value should be > the realistic max for that regime by ~2x and << kraken-event magnitudes (typically 1000+ m/s² instantaneous).

### 1.3 Bubble-radius threshold (single-tick position delta cap)

Default `2500 m`. KSP's physics-bubble radius is ~2500 m; vessels separating beyond this go on-rails. A single-tick `|pos_t - pos_{t-1}|` exceeding 2500 m is almost certainly a kraken teleport. This is independent of the time-delta — the bubble radius is the absolute max distance any vessel can move between adjacent samples without going on-rails first.

Field: `float MaxSingleTickPositionDeltaMeters` (default 2500.0f).

### 1.4 Altitude bounds

`float AltitudeFloorMeters` (default `-100.0f`) — KSP terrain mesh shifts up to a few tens of metres between sessions; -100 m floor accommodates that without rejecting real submarine / underwater-base recordings.

`float AltitudeCeilingMargin` (default `1000.0f`) — added to `body.sphereOfInfluence`. The ceiling is per-body: `sphereOfInfluence + 1000.0f`. The classifier resolves the body via `CelestialBody.sphereOfInfluence` at classify time. If the body cannot be resolved (test harness or bad bodyName), the altitude classifier is a no-op for that sample (HR-9: log Verbose, do not reject).

### 1.5 Cluster-rate threshold (§14.3)

Default `0.20` (20%). If `rejectedCount / sampleCount` for a section exceeds this, the section gets a `Cluster` bit set on its `classifierMask`, the section is rendered but logged at Warn as low-fidelity. Same default and rationale as the §14.3 spec.

Field: `float ClusterRateThreshold` (default 0.20f).

### 1.6 ConfigurationHash byte layout

Currently the canonical encoding has `outlierAccelAtmospheric` (4 bytes, [21..24]) and `outlierAccelExo` (4 bytes, [25..28]) reserved as zero, and `useAnchorTaxonomy` at [51], `useCoBubbleBlend` at [52]. Phase 8 makes the reserved bytes real AND extends the encoding to cover all six environment ceilings + the bubble radius + altitude bounds + cluster rate + the new `useOutlierRejection` flag.

New layout (the existing two reserved float32s become `Atmospheric` and `ExoPropulsive`; add four new float32s for the rest of the environments and three more floats + cluster rate):

```
[21..24]  outlierAccelAtmospheric    (float32)  was reserved 0
[25..28]  outlierAccelExoPropulsive  (float32)  was reserved 0   (rename: drops "Exo" generic — split below)
[29..38]  anchorPriorityVector        (byte[10])    unchanged
[39..46]  coBubbleBlendMaxWindow      (double)      unchanged
[47..50]  coBubbleResampleHz          (float32)     unchanged
[51]      useAnchorTaxonomy           (byte)        unchanged
[52]      useCoBubbleBlend            (byte)        unchanged
[53..56]  outlierAccelExoBallistic   (float32)  NEW
[57..60]  outlierAccelSurfaceMobile  (float32)  NEW
[61..64]  outlierAccelSurfaceStationary (float32) NEW
[65..68]  outlierAccelApproach        (float32) NEW
[69..72]  outlierBubbleRadius         (float32) NEW
[73..76]  outlierAltitudeFloor        (float32) NEW
[77..80]  outlierAltitudeCeilingMargin (float32) NEW
[81..84]  outlierClusterRate          (float32) NEW
[85]      useOutlierRejection         (byte)    NEW
```

`CanonicalEncodingLength` 53 → 86.

Decision: do NOT rename the existing `outlierAccelExo` to `outlierAccelExoPropulsive` in the hash docs unless the refactor lands here; just append the new ExoBallistic / SurfaceMobile / SurfaceStationary / Approach floats so the existing offsets [21..28] keep their meaning and the hash flips deterministically. (Either reading is HR-10-compatible — both produce a hash drift on first Phase 8 build.)

The four-arg overload `ComputeConfigurationHash(SmoothingConfiguration, OutlierThresholds, useAnchorTaxonomy, useCoBubbleBlend, useOutlierRejection)` becomes the production signature; the existing two-arg / three-arg overloads stay as backward-compat for Phase 1-5 tests, defaulting `OutlierThresholds.Default`, `useOutlierRejection: true`. The Phase 5 tests using two- and three-arg overloads keep compiling.

## 2. OutlierClassifier API

`Source/Parsek/Rendering/OutlierClassifier.cs` (new). Pure static, deterministic (HR-3).

```csharp
internal static class OutlierClassifier
{
    [Flags] internal enum ClassifierBit : byte
    {
        None             = 0,
        Acceleration     = 1 << 0,
        BubbleRadius     = 1 << 1,
        AltitudeOutOfRange = 1 << 2,
        Cluster          = 1 << 3, // section-wide bit, not per-sample
    }

    /// <summary>Returns one bit per sample (true = reject) plus aggregate metadata.</summary>
    internal static OutlierFlags Classify(
        Recording rec,
        int sectionIndex,
        OutlierThresholds thresholds,
        Func<string, CelestialBody> bodyResolver = null);
}
```

### 2.1 Per-sample classifier loop

For each sample `i` in `section.frames`:

1. **Acceleration**: if `i > 0`, compute `a` from velocity delta when both `frames[i].velocity != Vector3.zero` and `frames[i-1].velocity != Vector3.zero` AND `dt > 0`; else compute via numerical 2nd derivative of position (`a ≈ 2 * |p_i - p_{i-1}| / dt²`); else skip the test for that sample. If `a > thresholds.AccelCeilingForEnvironment(section.environment)`, set `Acceleration` bit and log Verbose with `value vs threshold`.

   - TrajectoryPoint carries `Vector3 velocity` (line 20 of TrajectoryPoint.cs). It defaults to `Vector3.zero` when unset; treat that as "not available" and fall back to the position-delta path.
   - `dt = frames[i].ut - frames[i-1].ut`. If `dt <= 0`, skip (the spline pre-fit will reject non-monotonic UT separately; we don't double-flag).
   - Position used for the numerical derivative: `body.GetWorldSurfacePosition(lat, lon, alt)` would require live KSP, so use the geographic distance approximation `sqrt((dlat * R)² + (dlon * R * cos(lat))² + dalt²)` where `R = body.Radius`. If body is null, fall back to flat lat/lon-degree distance × `60000` (rough metres-per-degree) — only for the test harness path; production resolves the body. Alternative: cache per-sample world position from the section frames.

2. **Bubble-radius**: if `i > 0`, compute `|p_i - p_{i-1}|` (same metric as above). If > `thresholds.MaxSingleTickPositionDeltaMeters`, set `BubbleRadius` bit.

3. **Altitude-out-of-range**: resolve `body = bodyResolver(frames[i].bodyName)`. If `body == null`, no-op (don't reject). Else if `frames[i].altitude < thresholds.AltitudeFloorMeters` OR `frames[i].altitude > body.sphereOfInfluence + thresholds.AltitudeCeilingMargin`, set `AltitudeOutOfRange` bit.

### 2.2 First-and-last-sample edge handling

**Decision: skip delta-based classifiers (Acceleration, BubbleRadius) for `i == 0` AND for `i == count - 1`.** Rationale: applying a one-sided delta (next-sample only for index 0; prev-sample only for the last) would double-charge that single delta when the inner sample is also being checked, doubling false-positive probability at endpoints. Endpoints already get conservative coverage from the inner samples' checks against them. The Altitude classifier still applies at the endpoints (no neighbour needed).

Endpoint handling is documented in OutlierClassifier with a one-line comment, and tested in `OutlierClassifier_FirstAndLastSamples_NoNeighborChecksSkipped`.

### 2.3 Section-wide cluster check

After per-sample classification, count rejected samples for this section. If `rejectedCount > 0 && (rejectedCount / count) > thresholds.ClusterRateThreshold`, set `Cluster` bit on the section's `classifierMask`. The cluster bit is a section attribute, not a per-sample bit — it lives in `OutlierFlags.ClassifierMask`, not in the bitmap.

### 2.4 RELATIVE / OrbitalCheckpoint sections

`Classify` returns an `OutlierFlags` with `RejectedCount = 0` and an empty bitmap when `section.referenceFrame != Absolute` OR `section.environment` is not one of the six environments listed above. RELATIVE sections store metre-offsets in `latitude/longitude/altitude` — not lat/lon/alt — so geographic-distance calculations would mis-classify everything. OrbitalCheckpoint has no `frames`. Both are skipped silently per HR-7.

The pipeline orchestrator (§5 below) only invokes Classify on Phase-1-eligible sections (Absolute + ExoPropulsive/ExoBallistic). Phase 8 does NOT widen the eligibility set to other environments — that's deferred to whenever those environments get smoothing-spline support.

### 2.5 HR-1: classifier never mutates Recording

Verified by an HR-1 audit unit test that calls Classify, asserts `Recording.Points` reference and contents are unchanged, and asserts `TrackSection.frames` reference and contents are unchanged.

## 3. OutlierFlags Annotation Type

`Source/Parsek/Rendering/OutlierFlags.cs` (new).

```csharp
internal sealed class OutlierFlags
{
    public int SectionIndex;           // matches the .pann schema field
    public byte ClassifierMask;        // bitwise OR of which classifiers fired (incl. Cluster)
    public byte[] PackedBitmap;        // (sampleCount + 7) / 8 bytes; 0 = kept, 1 = rejected
    public int RejectedCount;          // count of true bits
    public int SampleCount;            // length of the section's frames at classify time

    // Helpers — pure, no allocations beyond return value.
    internal bool IsRejected(int sampleIndex);
    internal IEnumerable<int> RejectedSampleIndices { get; }

    internal static OutlierFlags Empty(int sectionIndex, int sampleCount);  // all-kept, mask=None
    internal static byte[] BuildPackedBitmap(bool[] perSample);
    internal static bool[] UnpackBitmap(byte[] packed, int sampleCount);
}
```

### Bit packing convention

For sampleIndex `s`, byte is `s / 8`, bit is `s % 8` (LSB-first). Bit value 1 = rejected.

Round-trip is tested directly: pack → byte[] → unpack → bool[] → assert original. Tested at lengths 0, 1, 7, 8, 9, 100, 1000.

The `SampleCount` field is in-memory only — NOT persisted (the schema doesn't carry it; the classifier always re-supplies it from `section.frames.Count` on read). On read the `OutlierFlags` reconstructs `SampleCount` from `section.frames.Count` at install time so `IsRejected` bounds-checks correctly.

## 4. Spline Fit Integration

`Source/Parsek/Rendering/SmoothingSpline.cs` is a POCO struct with no logic — the actual `Fit` lives in `TrajectoryMath.CatmullRomFit.Fit` (TrajectoryMath.cs:727).

### Modify `TrajectoryMath.CatmullRomFit.Fit`

Add an optional parameter:

```csharp
internal static SmoothingSpline Fit(
    IList<TrajectoryPoint> samples,
    double tension,
    out string failureReason,
    OutlierFlags rejected = null);   // NEW; null = legacy behavior
```

Behaviour change:

- When `rejected == null`, identical to today.
- When `rejected != null`, the inner loop iterates samples but **skips any sample whose `rejected.IsRejected(i)` is true**. The skipped samples do not contribute knots / controls. The remaining samples are fitted exactly as today.
- After filtering: if effective sample count < `MinSamplesPerSection` (4), the fit returns `default(SmoothingSpline)` with `IsValid = false` and `failureReason = $"after-rejection sample count {effectiveCount} < min 4 (rejected {rejected.RejectedCount} of {samples.Count})"`. The orchestrator (§5) sees this as a fit-failure and emits Pipeline-Smoothing Warn → falls back to legacy bracket per existing HR-9 path.

Backward compat: existing tests calling `Fit(samples, tension, out fr)` continue to pass `null` for the new parameter (default). The Phase 1 and Phase 4 SmoothingPipelineTests stay green.

The lifted-inertial path in `SmoothingPipeline.LiftFramesToInertial` builds a transient `List<TrajectoryPoint>` from `section.frames`. Phase 8 must pass the *same* `OutlierFlags` to `Fit` because the bitmap indexes by section-frame index, and the lifted list preserves index order 1:1. So `Fit(liftedFrames, tension, out fr, rejected: outlierFlags)` works with no remapping.

## 5. SmoothingPipeline Integration

### 5.1 `FitAndStorePerSection` — classifier runs BEFORE Fit

Inside the per-section loop (currently SmoothingPipeline.cs:127), after `ShouldFitSection(section)` passes and after the inertial body resolution but BEFORE `LiftFramesToInertial` and the `Fit` call:

```csharp
OutlierFlags outliers = null;
if (ResolveUseOutlierRejection())
{
    outliers = OutlierClassifier.Classify(
        rec, i, OutlierThresholds.Default,
        bodyResolver: ResolveBody);
    if (outliers != null && outliers.RejectedCount > 0)
        SectionAnnotationStore.PutOutlierFlags(recordingId, i, outliers);
    EmitPerSectionOutlierLogs(recordingId, i, section, outliers);
}
```

Then pass `outliers` into the `Fit` call:

```csharp
SmoothingSpline spline = TrajectoryMath.CatmullRomFit.Fit(
    samplesForFit,
    SmoothingConfiguration.Default.Tension,
    out string failureReason,
    rejected: outliers);
```

Empty / all-kept flags (`outliers.RejectedCount == 0`) are NOT stored in `SectionAnnotationStore` — the absence of an entry is the canonical "no outliers in this section" representation, mirroring the pattern used for `AnchorCandidates` (where empty arrays are dropped at write time per SectionAnnotationStore.cs:91-99). This keeps the `.pann OutlierFlagsList` block compact and matches the schema's natural "count = 0 in steady state" expectation.

### 5.2 Logging

Three lines per §19.2 Outlier Rejection table:

- **Sample rejected** (Verbose, `Pipeline-Outlier`) — emitted inside `OutlierClassifier.Classify` per rejected sample. Format:
  `Sample rejected: recordingId={id} sectionIndex={i} sampleIndex={s} classifier={Acceleration|BubbleRadius|AltitudeOutOfRange} value={v} threshold={t}`. Bounded — sections rarely have hundreds of rejections, so use plain `Verbose`, not `VerboseRateLimited`. Cap the per-section emission at, say, 50 lines and replace overflow with a single summary line `"sample-reject log capped: section had {n} additional rejections (showing first 50)"` to keep KSP.log bounded against pathological inputs.

- **Per-section rejection summary** (Info, `Pipeline-Outlier`) — emitted once per section AFTER Classify, regardless of `RejectedCount`. Format:
  `Per-section rejection summary: recordingId={id} sectionIndex={i} env={env} sampleCount={n} rejectedCount={r} accel={a} bubble={b} altitude={alt} cluster={c}`. Where `accel/bubble/altitude` are per-classifier counts (computed during classify). When rejectedCount = 0, the Info line still emits with all-zero counters — that's positive evidence that classification ran (HR-9: visible evidence the path executed).

- **Cluster threshold exceeded** (Warn, `Pipeline-Outlier`) — emitted when the Cluster bit is set on classifierMask. Format:
  `Cluster threshold exceeded → low-fidelity tag: recordingId={id} sectionIndex={i} rejectionRate={r/n} threshold={th}`. Dedup per session per `(recordingId, sectionIndex)` so a flag-flip recompute does not double-log. Use a `HashSet<string>` lock-protected lazy member of `SmoothingPipeline` (mirroring `s_frameDecisionLogged` from Phase 4 line 74), with the same FrameDecisionLoggedCap-style bound.

### 5.3 `LoadOrCompute` recompute path

When `.pann` is present and matches all cache-key fields, `TryRead` (extended in §7) deserializes `OutlierFlagsList` entries into a `List<KeyValuePair<int, OutlierFlags>>`. Each entry routes through `SectionAnnotationStore.PutOutlierFlags(recordingId, sectionIndex, flags)`. The `Pipeline-Sidecar` Verbose "Pannotations read OK" line is extended to include `outlierFlagsCount={n}`.

When `.pann` is absent / drifted, `LoadOrCompute → FitAndStorePerSection` runs Classify implicitly via the modified loop in 5.1, so the lazy-recompute path needs no separate logic.

### 5.4 Interaction with §9.1 propagation helper (`TryEvaluatePerSegmentWorldPositions`)

The §9.1 propagation helper at `RenderSessionState.cs:1222` reads `recordedWorld` from the first sample at-or-after the boundary UT (`TryFindFirstPointAtOrAfter`). If that sample is itself flagged outlier, the propagator computes `(recordedWorld - smoothedWorld)` from a kraken sample, producing a distorted ε.

**Phase 8 decision: address this in a follow-up Phase 8 P1, NOT in the initial commit.** Rationale:

- `TryEvaluatePerSegmentWorldPositions` consumes a flat `Recording.Points` list, not section-indexed frames. Mapping a `Recording.Points` index back to a `(sectionIndex, sampleIndex)` for outlier-bitmap lookup is non-trivial and out-of-scope for the kraken-spike spec.
- The smoothed component of the propagation term is already clean (the spline was fit through cleaned samples per §5.1), so the residual error from a kraken `recordedWorld` is bounded by the kraken's offset (single-sample, finite by bubble-radius classifier — at most ~2500 m if the bubble cap fired, which still distorts the ε but does not crash the renderer).
- The §9.1 helper is gated behind `useAnchorTaxonomy`. Phase 6 ships with no kraken-fixture coverage of §9.1's interaction; Phase 8's done-condition (kraken sample is rejected by the spline fit) is satisfied without touching §9.1.

Add a `TODO(Phase 8 follow-up)` comment in `RenderSessionState.TryEvaluatePerSegmentWorldPositions` near `TryFindFirstPointAtOrAfter` documenting that an outlier-aware boundary search is required for full §9.1 fidelity at boundaries that land on kraken samples. Open a follow-up issue.

### 5.5 `ResetForTesting` extension

`SmoothingPipeline.ResetForTesting` clears the new outlier-cluster Warn dedup set and the new `UseOutlierRejectionResolverForTesting` test seam. `SectionAnnotationStore.ResetForTesting` already clears via `Clear()` — extend `Clear()` to clear the new outlier flags map.

## 6. SectionAnnotationStore Map

Add a fourth dict alongside `Splines`, `Candidates`, `CoBubbleTraces`:

```csharp
private static readonly Dictionary<string, Dictionary<int, OutlierFlags>> OutlierFlagsByRecording
    = new Dictionary<string, Dictionary<int, OutlierFlags>>(StringComparer.Ordinal);
```

API (mirrors `PutSmoothingSpline / TryGetSmoothingSpline / GetSplineCountForRecording` exactly):

```csharp
internal static void PutOutlierFlags(string recordingId, int sectionIndex, OutlierFlags flags);
internal static bool TryGetOutlierFlags(string recordingId, int sectionIndex, out OutlierFlags flags);
internal static int GetOutlierFlagsCountForRecording(string recordingId);
```

Lifecycle:
- `RemoveRecording(recordingId)` clears the recording's outlier entries (already extends to the new dict — add `OutlierFlagsByRecording.Remove(recordingId)` at SectionAnnotationStore.cs:222).
- `Clear()` clears all four dicts (add `OutlierFlagsByRecording.Clear()` at line 232).

Synchronisation: same `Lock` object used today.

The Phase 1 HR-10 RemoveRecording-before-fit guard at `SmoothingPipeline.cs:120-121` already covers the new map via `RemoveRecording`. No additional clearing needed in the orchestrator.

## 7. .pann Schema + Persistence

### 7.1 Reader changes (`PannotationsSidecarBinary.TryRead`)

Currently at line 402-410 the reader reads `outlierCount` and rejects any non-zero value as malformed. **Replace** that block with a real reader that follows the schema declared in §17.3.1 lines 816-822:

```
OutlierFlagsList:
  count : int32
  entries[count] :
    sectionIndex   : int32
    classifierMask : byte
    packedBitmap   : length-prefixed byte[]    (BinaryReader.ReadBytes after ReadInt32)
    rejectedCount  : int32
```

Add a new out-parameter list `out List<KeyValuePair<int, OutlierFlags>> outlierFlags` to BOTH `TryRead` overloads (the two-out and three-out kept for back-compat). The simplest design: add a fourth `out` parameter on the existing three-out overload, deprecate the two-out as legacy (it discards outlier flags). The two-out overload already discards co-bubble traces via `out _`; add a similar `out _` for outlier flags. The three-out overload becomes a thin wrapper around the four-out.

Min bytes per entry: `int sectionIndex (4) + byte classifierMask (1) + length-prefix int (4) + byte rejected payload (≥0) + int rejectedCount (4) = 13` bytes (assuming empty bitmap). The existing `MaxOutlierFlagsEntries = 10_000` cap stays. Per-bitmap byte cap: `(MaxKnotsPerSpline + 7) / 8 = 12500` is a generous upper bound — section has at most a few thousand frames.

`ValidateCount` is invoked twice: once for the entries count (cap MaxOutlierFlagsEntries, min 13 bytes/entry), once for each entry's bitmap byte length (cap (MaxKnotsPerSpline + 7) / 8, min 1 byte/element).

### 7.2 Writer changes (`PannotationsSidecarBinary.Write`)

Add a fifth optional parameter `IList<KeyValuePair<int, OutlierFlags>> outlierFlags = null`. Emit the block in declared order — currently OutlierFlagsList writes count = 0 at line 602; replace with:

```csharp
int outlierEntryCount = outlierFlags?.Count ?? 0;
writer.Write(outlierEntryCount);
if (outlierFlags != null)
{
    for (int i = 0; i < outlierFlags.Count; i++)
    {
        OutlierFlags f = outlierFlags[i].Value;
        if (f == null)
            throw new InvalidOperationException(
                $"OutlierFlagsList[{i}] is null — caller must drop empty entries before write");
        if (f.PackedBitmap == null)
            throw new ArgumentException(
                $"OutlierFlagsList[{i}].PackedBitmap is null");
        writer.Write(outlierFlags[i].Key);   // sectionIndex
        writer.Write(f.ClassifierMask);
        writer.Write(f.PackedBitmap.Length);
        writer.Write(f.PackedBitmap);
        writer.Write(f.RejectedCount);
    }
}
```

The block remains in §17.3.1 declared order: SmoothingSplineList → OutlierFlagsList → AnchorCandidatesList → CoBubbleOffsetTraces. The reader and writer must match.

### 7.3 SmoothingPipeline write path

In `TryWritePann` at SmoothingPipeline.cs:770, gather outlier flags alongside splines and anchor candidates:

```csharp
var outlierFlags = new List<KeyValuePair<int, OutlierFlags>>();
if (rec.TrackSections != null)
{
    for (int i = 0; i < rec.TrackSections.Count; i++)
    {
        if (SectionAnnotationStore.TryGetOutlierFlags(recordingId, i, out OutlierFlags flags)
            && flags != null && flags.PackedBitmap != null)
        {
            outlierFlags.Add(new KeyValuePair<int, OutlierFlags>(i, flags));
        }
    }
}
```

Then add the new arg to the `PannotationsSidecarBinary.Write` call (and its log line — extend the Verbose write summary with `outlierFlagsCount={n}`).

### 7.4 AlgorithmStampVersion bump 6 → 7

Add a new comment block at PannotationsSidecarBinary.cs:172 documenting the bump:

> Bumped to 7 in Phase 8: OutlierFlagsList block transitions from always-empty (writer emitted count=0; reader rejected any non-zero count) to populated by `OutlierClassifier`. The new ConfigurationHash also gains real outlier threshold bytes plus the `useOutlierRejection` flag at offset [85]. Existing v6 .pann files lack populated outlier flags; the bump triggers alg-stamp-drift on first load so stale files are discarded and recomputed (HR-10).

Set `internal const int AlgorithmStampVersion = 7`.

### 7.5 ConfigurationHash canonical encoding length

`CanonicalEncodingLength` 53 → 86. The existing two-, three-, and four-arg overloads of `ComputeConfigurationHash` get a fifth param `bool useOutlierRejection = true` (default). Production callers (orchestrator) wire all five.

Update `ComputeConfigurationHash` body to:
1. Pull outlierAccelAtmospheric/ExoPropulsive from the new `OutlierThresholds` arg (replacing the reserved-zero floats at [21..28]).
2. Append the four new env-acceleration floats + bubble + altitude floor + altitude ceiling margin + cluster rate floats + useOutlierRejection byte at the end.

The Phase 1-5 tests that use the no-arg / two-arg / three-arg overloads keep compiling because the new params default to `OutlierThresholds.Default` and `useOutlierRejection: true`. They still compute a different hash (because the encoding is now 86 bytes including thresholds), but Phase-5-or-earlier tests assert hash-changes-on-flip, not specific byte values, so they stay green.

### 7.6 Per-block invalidation discipline

The `OutlierFlagsList` block, like splines and anchor candidates, is whole-file invalidated on any cache-key drift (`alg-stamp-drift` / `config-hash-drift` / `epoch-drift` / `format-drift` / `recording-id-mismatch`). It is NOT per-block invalidated. Per-block invalidation is reserved for the Phase 5 co-bubble traces only (because their cache key includes per-trace peer state). The Phase 8 reader does not need any per-entry validation.

### 7.7 `SmoothingPipeline.LoadOrCompute` install path

When `TryRead` returns the four-out tuple successfully (probe + cache-key match), iterate the outlier flags list and route to `SectionAnnotationStore.PutOutlierFlags`. The HR-10 `RemoveRecording` clear at SmoothingPipeline.cs:395 already removes the outlier map entries (via the extended `RemoveRecording` in §6).

Update the Pipeline-Sidecar Verbose "Pannotations read OK" log line at SmoothingPipeline.cs:434 to include `outlierFlagsCount={count}`.

## 8. Settings Flag

### 8.1 `useOutlierRejection` in `ParsekSettings.cs`

Default `true`. Mirror `useCoBubbleBlend`'s shape (Phase 5):

```csharp
[GameParameters.CustomParameterUI("Use outlier rejection",
    toolTip = "When on (Phase 8), kraken-event samples are rejected before smoothing. Off → spline fits raw samples including kraken spikes.")]
public bool useOutlierRejection
{
    get { return _useOutlierRejection; }
    set
    {
        if (_useOutlierRejection == value) return;
        bool prev = _useOutlierRejection;
        _useOutlierRejection = value;
        NotifyUseOutlierRejectionChanged(prev, value);
        if (ParsekSettingsPersistence.IsReconciled)
            ParsekSettingsPersistence.RecordUseOutlierRejection(value);
    }
}
private bool _useOutlierRejection = true;

internal static void NotifyUseOutlierRejectionChanged(bool oldValue, bool newValue)
{
    if (oldValue == newValue) return;
    ParsekLog.Info("Pipeline-Outlier", $"useOutlierRejection: {oldValue}->{newValue}");
}
```

### 8.2 `ParsekSettingsPersistence.cs`

Add the parallel storage / load / save / reset surface:

- `private const string UseOutlierRejectionKey = "useOutlierRejection";`
- `private static bool? storedUseOutlierRejection;`
- LoadIfNeeded reads the key (mirrors Phase 5 pattern at lines 174-183).
- Loaded log line includes `useOutlierRejection=...`.
- `ApplyTo` reconciliation block (mirrors lines 260-266).
- `RecordUseOutlierRejection(bool)` (mirrors lines 364-382).
- `Save` body adds `if (storedUseOutlierRejection.HasValue) root.AddValue(UseOutlierRejectionKey, ...);`.
- `ResetForTesting` clears `storedUseOutlierRejection = null`.
- Test-only `GetStoredUseOutlierRejection()` and `SetStoredUseOutlierRejectionForTesting(bool?)` accessors at the bottom (mirrors lines 579 / 615 patterns).

### 8.3 `SmoothingPipeline.ResolveUseOutlierRejection`

Pattern matches `ResolveUseCoBubbleBlend` at SmoothingPipeline.cs:750:

```csharp
internal static System.Func<bool> UseOutlierRejectionResolverForTesting;

internal static bool ResolveUseOutlierRejection()
{
    var seam = UseOutlierRejectionResolverForTesting;
    if (seam != null) return seam();
    ParsekSettings settings = ParsekSettings.Current;
    return settings?.useOutlierRejection ?? true;
}
```

`CurrentConfigurationHash` (line 712) extends to read all three rollout flags + outlier thresholds; cache invalidates when ANY changes:

```csharp
private static byte[] s_cachedConfigurationHash;
private static bool s_cachedConfigurationHashAnchorFlag;
private static bool s_cachedConfigurationHashCoBubbleFlag;
private static bool s_cachedConfigurationHashOutlierFlag;  // NEW

// In CurrentConfigurationHash:
bool outlierFlag = ResolveUseOutlierRejection();
// extend the cache validity check, recompute path passes the flag in
```

`ResetForTesting` clears `UseOutlierRejectionResolverForTesting = null` and `s_cachedConfigurationHashOutlierFlag`.

## 9. Logging Contract (§19.2 Outlier Rejection)

Already covered in §5.2 above. Summary:

| Event | Level | Tag | Where emitted |
|---|---|---|---|
| Sample rejected | Verbose | `Pipeline-Outlier` | `OutlierClassifier.Classify`, capped at 50/section |
| Per-section rejection summary | Info | `Pipeline-Outlier` | `SmoothingPipeline.FitAndStorePerSection`, every classified section |
| Cluster threshold exceeded → low-fidelity tag | Warn | `Pipeline-Outlier` | `SmoothingPipeline.FitAndStorePerSection`, dedup-keyed per `(recordingId, sectionIndex)` per session |
| `useOutlierRejection` flip | Info | `Pipeline-Outlier` | `ParsekSettings.NotifyUseOutlierRejectionChanged` |

LogContractTests (`Source/Parsek.Tests/LogValidation/`) gets a new rule grepping for these four lines in active test recordings to lock the format.

## 10. Test Plan

### 10.1 New xUnit test files

`Source/Parsek.Tests/Rendering/`:

- **`OutlierClassifierTests.cs`** — covers classifier correctness:
  - `OutlierClassifier_Atmospheric_Rejects50gAccel_KeepsRealRocketBurn` — synthetic Atmospheric section: one sample with implied `a = 1000 m/s²` → rejected; adjacent samples with `a = 50 m/s²` (5g) → kept; assert classifier mask has Acceleration bit; assert RejectedCount == 1.
  - `OutlierClassifier_ExoBallistic_RejectsKrakenAccel` — orbital ExoBallistic with one 1000 m/s² spike → rejected; adjacent samples at ~9.81 m/s² (Kerbin-surface gravity) → kept.
  - `OutlierClassifier_ExoPropulsive_AcceptsHighTwrBurn` — sample at 100 m/s² (10g) ExoPropulsive burn → kept (under 200 cap); sample at 500 m/s² → rejected.
  - `OutlierClassifier_BubbleRadius_RejectsTeleport` — adjacent sample distance 5000 m at dt=0.1s → BubbleRadius bit set; distance 1000 m → not rejected by BubbleRadius.
  - `OutlierClassifier_AltitudeOOR_RejectsBelowSurface` — altitude -200 m → rejected (below -100 m floor); altitude -50 m → kept.
  - `OutlierClassifier_AltitudeOOR_RejectsAboveSOI` — altitude > body.sphereOfInfluence + 1000 → rejected; below the cap → kept. Use TestBodyRegistry.CreateBody with a configured SOI.
  - `OutlierClassifier_AltitudeOOR_NullBody_NoRejection` — bodyResolver returns null → AltitudeOutOfRange never sets, even for absurd altitudes.
  - `OutlierClassifier_Cluster_FlagsSection_When25PercentRejected` — 4 of 16 samples rejected → Cluster bit set on classifierMask. 2 of 16 → Cluster bit not set.
  - `OutlierClassifier_FirstAndLastSamples_NoNeighborChecksSkipped` — endpoint samples have no delta-based bit set even when their successor / predecessor is far away. Altitude bit still applies at endpoints.
  - `OutlierClassifier_Determinism_SameInputSameOutput` — call Classify twice; assert byte-equal outputs (HR-3 pin).
  - `OutlierClassifier_HR1_DoesNotMutateRecording` — call Classify; assert `Recording.Points` reference and contents are unchanged; `TrackSection.frames` unchanged. (HR-1 audit.)
  - `OutlierClassifier_RelativeFrame_ReturnsEmptyFlags` — RELATIVE-frame section → returns flags with RejectedCount == 0 (HR-7).
  - `OutlierClassifier_VelocityFallback_ToPositionDerivative_WhenVelocityZero` — sample with `velocity = Vector3.zero` falls back to position 2nd derivative.
  - `OutlierClassifier_NonMonotonicUT_SkipsAccelTest` — dt ≤ 0 → skip the test (no rejection).

- **`OutlierFlagsTests.cs`** — packing helpers + struct round-trip:
  - `PackedBitmap_RoundTrip_AtVariousLengths` — lengths 0, 1, 7, 8, 9, 100, 1000.
  - `IsRejected_BoundsCheck` — out-of-range index returns false (or throws — pick one and document).
  - `Empty_AllFalse` — `OutlierFlags.Empty(...)` returns RejectedCount=0, mask=None, packed bytes all zero.

- **`OutlierFlagsSidecarRoundTripTests.cs`** — `.pann` round-trip:
  - `Write_Read_RoundTrip_PreservesAllFields` — writes a `.pann` with 3 sections of varied bitmap lengths and classifier masks; reads back; asserts every field matches byte-for-byte.
  - `Write_Read_EmptyOutlierList_StillReadable` — writes with an empty list; round-trip succeeds; reader returns empty list.
  - `Read_NegativeBitmapLength_RejectedAsCorrupt` — corrupt the bitmap byte-length to -1; assert `TryRead` returns false with `failureReason` containing `outlier-flags-bitmap`.
  - `Read_OversizedEntryCount_RejectedAsCorrupt` — entry count > MaxOutlierFlagsEntries → reject.
  - `AlgorithmStampDrift_V6_To_V7_DiscardsOldFile` — write a `.pann` with v6 alg stamp; load; assert `alg-stamp-drift` Pipeline-Sidecar Info line and recompute path triggered.

- **`UseOutlierRejectionSettingTests.cs`** — exact copy of `UseCoBubbleBlendSettingTests.cs` retargeted to the new flag:
  - `UseOutlierRejection_DefaultsTrue`
  - `UseOutlierRejection_PersistsAcrossLoad`
  - `UseOutlierRejection_FlipLogsInfo` — assert `Pipeline-Outlier` Info line `useOutlierRejection: false->true`
  - `UseOutlierRejection_NoLogWhenUnchanged`
  - `UseOutlierRejection_ConfigHashChangesOnFlip` — flag-on hash != flag-off hash
  - `UseOutlierRejection_DirectAssignThroughProperty_LogsInfo`

### 10.2 Extensions to existing test files

- **`Source/Parsek.Tests/Rendering/SmoothingPipelineTests.cs`** (Phase 1 tests):
  - `FitAndStorePerSection_RunsClassifierAndPersistsFlags` — section with one injected kraken sample → spline is fit through cleaned set, OutlierFlags stored under recordingId / sectionIndex.
  - `FitAndStorePerSection_ClassifierOff_NoFlagsStored` — `UseOutlierRejectionResolverForTesting = () => false`; classifier never runs; `SectionAnnotationStore.TryGetOutlierFlags` returns false for the section.
  - `FitAndStorePerSection_KrakenSampleSkipped_SplineFitsThroughNeighbors` — assert spline value at the kraken UT lies within tolerance of the line-fit through the neighbours, NOT at the kraken's lat/lon/alt.
  - `FitAndStorePerSection_TooManyOutliers_FitFails_FallsBackToLegacy` — section of 5 samples, 4 rejected → fit fails (need ≥4 after rejection), Pipeline-Smoothing Warn emitted, no spline stored.
  - `LoadOrCompute_PreservesOutlierFlags_AcrossRoundTrip` — write, clear store, load → flags re-installed.
  - `ConfigHash_OutlierThresholdFlipChangesHash` — pin HR-10 freshness for the new threshold bytes.

- **`Source/Parsek.Tests/Rendering/SectionAnnotationStoreTests.cs`**:
  - `PutOutlierFlags_TryGet_RoundTrip`
  - `PutOutlierFlags_OverwritesExisting`
  - `RemoveRecording_ClearsOutlierFlags`
  - `Clear_ClearsAllFourMaps`
  - `GetOutlierFlagsCountForRecording_ReturnsZeroWhenAbsent`

- **`Source/Parsek.Tests/Rendering/SmoothingSplineTests.cs`** (or wherever `CatmullRomFit.Fit` is unit-tested directly):
  - `Fit_WithFlags_SkipsRejectedSamples` — pass an OutlierFlags marking sample 5 as rejected; assert spline knot count = sampleCount - 1 and the kraken's lat/lon/alt is not in the controls.
  - `Fit_WithFlags_AfterRejectionTooFewSamples_ReturnsInvalid` — 5 samples, 4 rejected → invalid spline + populated failureReason.
  - `Fit_WithoutFlags_LegacyBehaviorUnchanged` — backward-compat regression pin.

- **`Source/Parsek.Tests/Rendering/SmoothingPipelineLoggingTests.cs`**:
  - `Outlier_PerSampleVerbose_Emitted` — synthetic kraken section; assert log line `[Pipeline-Outlier]` containing `Sample rejected` and `classifier=Acceleration`.
  - `Outlier_PerSectionInfo_AlwaysEmitted` — even a clean section emits the per-section summary Info line.
  - `Outlier_ClusterWarn_Emitted_When25PercentRejected` — 4-of-16 section → Warn line with `rejectionRate=0.25`.
  - `Outlier_ClusterWarn_DedupedAcrossRecompute` — call FitAndStorePerSection twice for the same recording → Warn fires once.

- **`Source/Parsek.Tests/Rendering/PannotationsSidecarRoundTripTests.cs`** (Phase 1 round-trip):
  - `RoundTrip_OutlierFlagsList_MatchesSchema` — byte-level read of the produced `.pann` confirming the field order matches §17.3.1.
  - `Read_LegacyV6File_DiscardedViaAlgStampDrift`.

### 10.3 In-game test (§20.5 Phase 8)

`Source/Parsek/InGameTests/RuntimeTests.cs` — new method:

```csharp
[InGameTest(Category = "Pipeline-Outlier", Scene = GameScenes.FLIGHT,
    Description = "Kraken-spike sample is rejected; spline fits through neighbors")]
public IEnumerator Pipeline_Outlier_Kraken()
{
    // 1. Build synthetic Recording with one ExoBallistic section of, say, 12 samples.
    //    Sample 5 is the kraken: lat += 50°, alt += 100000 m, velocity huge.
    // 2. SmoothingPipeline.FitAndStorePerSection(rec).
    // 3. Assert SectionAnnotationStore.TryGetOutlierFlags returns flags with RejectedCount == 1, sample 5 set.
    // 4. SmoothingSpline cached for the section; Evaluate at kraken UT.
    // 5. Assert |spline(krakenUT) - linearInterp(neighbour samples 4 and 6)| < tolerance (e.g. 100 m).
    //    Tolerance is loose because Catmull-Rom isn't strictly linear; the assertion is "spline doesn't deflect
    //    toward the kraken's lat += 50°".
}
```

### 10.4 Synthetic recording fixture

`Source/Parsek.Tests/Generators/RecordingBuilder.cs` already supports building sections + frames; add a `WithKrakenInjection(double krakenUT, Vector3 latLonAltDelta)` helper that finds the sample bracketing krakenUT, mutates its lat/lon/alt by the delta, and bumps its velocity to ~kraken magnitude. This fixture is consumed by Pipeline_Outlier_Kraken AND by `SyntheticRecordingTests.InjectAllRecordings` for a new `pipeline-outlier-kraken` recording (per design doc §20.5 Phase 8, fixture name `pipeline-outlier-kraken`).

`SyntheticRecordingTests.cs:5701` (`InjectAllRecordings`) gets a new entry that builds and persists a `pipeline-outlier-kraken.prec` recording using the helper. The test count constant updates accordingly.

### 10.5 LogContractTests

`Source/Parsek.Tests/LogValidation/` — add a rule asserting that any test session that includes a Phase 8 fixture emits at least one `Pipeline-Outlier Per-section rejection summary` Info line and zero un-tagged "outlier" lines.

## 11. CHANGELOG + todo doc updates

### 11.1 CHANGELOG

Add a new entry under `## 0.9.1 → ### Internals` (matching the placement of the Phase 5 / Phase 6 entries at lines 36-38):

> - **Ghost trajectory rendering Phase 8: outlier rejection.** Kraken-event single-frame teleports are rejected before the smoothing spline is fit so spline curves no longer deflect through physics-glitch samples. The classifier is environment-aware: per-environment acceleration ceilings (Atmospheric / ExoPropulsive / ExoBallistic / SurfaceMobile / SurfaceStationary / Approach), bubble-radius single-tick position-delta cap (2500 m), and altitude bounds against `body.sphereOfInfluence`. Sections where over 20% of samples are rejected are flagged low-fidelity in diagnostics. New `useOutlierRejection` setting (default on) gates the new behaviour and falls back to fitting raw samples when off; the flag plus all threshold constants participate in the `.pann` `ConfigurationHash`. New `.pann OutlierFlagsList` block carries the per-section packed bitmap; `AlgorithmStampVersion` bumped from v6 to v7 so older `.pann` files are discarded and recomputed on first load (HR-10).

### 11.2 todo doc

`docs/dev/todo-and-known-bugs.md` — strikethrough any open Phase 8 line items and add:

> - ~~Phase 8 outlier rejection — kraken-event resilience (design doc §14, §18 Phase 8). Section 22.1 threshold tuning is intentionally still "deferred"; Phase 8 ships baseline values that catch krakens without false-positives on real flights, with empirical tuning to follow.~~

Note: a follow-up todo entry is added for the §9.1 propagation helper outlier-aware boundary search (deferred from this phase per §5.4).

## 12. Risks / Open Questions

- **Threshold tuning deferred (§22.1).** Phase 8 ships the baseline values listed in §1, justified against KSP physics. Empirical tuning is a follow-up. Tests assert the contract (acceleration cap rejects krakens, accepts real burns) NOT the constants — so re-tuning won't break the suite.

- **First-and-last-sample edge handling.** Decided: skip delta-based classifiers at endpoints. Rationale and trade-off documented in §2.2 above. Alternative — one-sided delta — was rejected because doubling the per-delta probability of a false positive at endpoints is unwanted.

- **Acceleration source: velocity vector vs position 2nd derivative.** Decided: prefer velocity (TrajectoryPoint.velocity is captured by KSP at sample time per Recording.cs comments). Fall back to position 2nd derivative when velocity is zero (sentinel for "not available"). The 2nd-derivative fallback has 4× sensitivity to position noise, so its threshold is the same — accepting that some real samples near the threshold may flip into false-rejection. Mitigation: `MinSamplesPerSection = 4` already gates the spline; if too many samples reject, fit fails and the section falls back to the legacy bracket via the existing Pipeline-Smoothing Warn path (HR-9 visible failure).

- **Interaction with §9.1 propagation helper.** Deferred — see §5.4. A `TODO` comment is added in `RenderSessionState.TryEvaluatePerSegmentWorldPositions`. Phase 8's done-condition (kraken sample rejected from spline) is satisfied without touching §9.1.

- **Multi-outlier cluster Warn timing.** Fired at commit time inside `FitAndStorePerSection`, dedup-keyed per `(recordingId, sectionIndex)` per session via a HashSet protected by a lock. A flag-flip recompute does NOT re-emit the Warn. This matches Phase 4's `s_frameDecisionLogged` pattern.

- **HR-9 audit.** Every code path emits a log:
  - Successful classification → Pipeline-Outlier Info per-section summary (always).
  - Per-sample reject → Pipeline-Outlier Verbose (always, capped).
  - Cluster trip → Pipeline-Outlier Warn (always, deduped per session).
  - After-rejection sample-count too low → Pipeline-Smoothing Warn (existing) + skip spline → consumer falls back to legacy bracket (existing path).
  - Missing body for altitude check → no log (intentional: no rejection, no failure to surface). Documented in OutlierClassifier comment.
  - Classifier off → no per-section summary. Acceptable: the classifier-off path is the legacy path; logging it would be noise.

- **HR-10 audit.** ConfigurationHash gains 33 new bytes covering all thresholds + the rollout flag. AlgorithmStampVersion bumps 6 → 7. Both bumps invalidate v6 `.pann` files via the existing alg-stamp-drift / config-hash-drift paths.

- **HR-7 audit.** Classifier dispatches by `section.environment` for acceleration thresholds. Cluster rate is per-section (not per-recording).

- **HR-1 audit.** Classifier reads from `section.frames` and never writes. Tested in `OutlierClassifier_HR1_DoesNotMutateRecording`.

- **HR-3 audit.** Classifier is pure-static. Same input → same output. Tested in `OutlierClassifier_Determinism_SameInputSameOutput`.

## 13. Step-by-Step Implementation Sequence

The implementer should land the work in this order, committing after each step. Each commit message follows the existing Phase-N pattern from CHANGELOG (e.g. `"Phase 8: ..."`); no `Co-Authored-By` lines.

### Step 1 — Threshold + flag plumbing (HR-10 cache key)

Files:
- `Source/Parsek/Rendering/OutlierClassifier.cs` — new file, declares `OutlierThresholds` struct only (Classify body added in Step 2).
- `Source/Parsek/PannotationsSidecarBinary.cs` — extend `ComputeConfigurationHash` to take `OutlierThresholds + useOutlierRejection`; bump `CanonicalEncodingLength` 53 → 86; emit the new bytes; bump `AlgorithmStampVersion` 6 → 7. Existing two-/three-/four-arg overloads default `OutlierThresholds.Default` and `useOutlierRejection: true`.
- `Source/Parsek/ParsekSettings.cs` — add `useOutlierRejection` flag + `NotifyUseOutlierRejectionChanged`.
- `Source/Parsek/ParsekSettingsPersistence.cs` — full Load / Save / Apply / Record / Reset surface for the new flag.
- `Source/Parsek/Rendering/SmoothingPipeline.cs` — add `UseOutlierRejectionResolverForTesting` seam, `ResolveUseOutlierRejection`, extend `CurrentConfigurationHash` cache validity to include the outlier flag.

Tests:
- `UseOutlierRejectionSettingTests.cs` — full file (mirrors `UseCoBubbleBlendSettingTests`).
- Extend `SmoothingPipelineTests.ConfigHash_OutlierThresholdFlipChangesHash`.

Commit: `Phase 8: outlier-rejection rollout flag + ConfigurationHash bytes`.

### Step 2 — `OutlierFlags` POCO + `OutlierClassifier.Classify`

Files:
- `Source/Parsek/Rendering/OutlierFlags.cs` — new file (POCO + helpers).
- `Source/Parsek/Rendering/OutlierClassifier.cs` — populate Classify body, including per-sample Verbose log and per-section Info log emit (Warn cluster log moves to Step 4 in the orchestrator).

Tests:
- `OutlierFlagsTests.cs` — full file.
- `OutlierClassifierTests.cs` — full file.

Commit: `Phase 8: OutlierClassifier + OutlierFlags POCO`.

### Step 3 — `SectionAnnotationStore` extension

Files:
- `Source/Parsek/Rendering/SectionAnnotationStore.cs` — add OutlierFlagsByRecording map + Put/TryGet/RemoveRecording/Clear/ResetForTesting / GetOutlierFlagsCountForRecording.

Tests:
- Extend `SectionAnnotationStoreTests.cs`.

Commit: `Phase 8: SectionAnnotationStore outlier-flags map`.

### Step 4 — Spline `Fit` consumes flags + orchestrator wiring

Files:
- `Source/Parsek/TrajectoryMath.cs` — extend `CatmullRomFit.Fit` with optional `OutlierFlags rejected = null`. Filter samples in the loop. Update minimum-sample-count check.
- `Source/Parsek/Rendering/SmoothingPipeline.cs` — `FitAndStorePerSection` calls `OutlierClassifier.Classify` before fit (gated on `useOutlierRejection`); persists flags via `SectionAnnotationStore.PutOutlierFlags`; passes flags into `Fit`. Add cluster Warn dedup set + emission. Log line update for per-section summary handled inside Classify; cluster Warn handled inside the orchestrator (since dedup is per-session).

Tests:
- Extend `SmoothingSplineTests.cs` (Fit-with-flags tests).
- Extend `SmoothingPipelineTests.cs` (FitAndStorePerSection tests).
- Extend `SmoothingPipelineLoggingTests.cs` (per-sample / per-section / cluster Warn).

Commit: `Phase 8: classify + fit-with-rejection wiring`.

### Step 5 — `.pann` schema persistence

Files:
- `Source/Parsek/PannotationsSidecarBinary.cs` — add reader for OutlierFlagsList block (replacing the count!=0 reject); add writer for OutlierFlagsList block; new four-out / five-out TryRead overloads (back-compat preserved); new five-arg Write overload.
- `Source/Parsek/Rendering/SmoothingPipeline.cs` — `TryWritePann` gathers outlier flags; `LoadOrCompute` installs them after read; extend Verbose write/read log lines with `outlierFlagsCount={n}`.

Tests:
- `OutlierFlagsSidecarRoundTripTests.cs` — full file.
- Extend `PannotationsSidecarRoundTripTests.cs`.

Commit: `Phase 8: .pann OutlierFlagsList block read/write + alg-stamp-drift`.

### Step 6 — In-game test + synthetic fixture + LogContract

Files:
- `Source/Parsek/InGameTests/RuntimeTests.cs` — add `Pipeline_Outlier_Kraken` IEnumerator test.
- `Source/Parsek.Tests/Generators/RecordingBuilder.cs` — add `WithKrakenInjection` helper.
- `Source/Parsek.Tests/SyntheticRecordingTests.cs` — register `pipeline-outlier-kraken.prec` in `InjectAllRecordings`.
- `Source/Parsek.Tests/LogValidation/` — extend.

Tests: covered by the above.

Commit: `Phase 8: in-game kraken test + pipeline-outlier-kraken fixture`.

### Step 7 — CHANGELOG + todo updates

Files:
- `CHANGELOG.md` — new entry under 0.9.1 Internals.
- `docs/dev/todo-and-known-bugs.md` — strikethrough closure entries; add follow-up for §9.1 outlier-aware boundary.

Commit: `Phase 8: CHANGELOG + todo`.

After Step 7, run `dotnet test` from `Source/Parsek.Tests` → all tests pass. Verify the deployed DLL via the `.claude/CLAUDE.md` recipe before declaring done.

### Critical Files for Implementation

- C:/Users/vlad3/Documents/Code/Parsek/Parsek-phase8-outliers/Source/Parsek/Rendering/SmoothingPipeline.cs
- C:/Users/vlad3/Documents/Code/Parsek/Parsek-phase8-outliers/Source/Parsek/PannotationsSidecarBinary.cs
- C:/Users/vlad3/Documents/Code/Parsek/Parsek-phase8-outliers/Source/Parsek/TrajectoryMath.cs
- C:/Users/vlad3/Documents/Code/Parsek/Parsek-phase8-outliers/Source/Parsek/Rendering/SectionAnnotationStore.cs
- C:/Users/vlad3/Documents/Code/Parsek/Parsek-phase8-outliers/Source/Parsek/ParsekSettings.cs
