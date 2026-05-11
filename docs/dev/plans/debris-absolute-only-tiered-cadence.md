# Absolute-only debris contract with proximity-tiered cadence

**Status:** v5 (post second Opus review + loop-anchored debris amendment)
**Scope:** debris recording and rendering only. No changes to non-debris recorder/renderer dispatch, no changes to non-debris use of the `RelativeAnchorResolver` chain, no changes to Re-Fly merge/supersede flow.
**Compatibility:** none. This plan describes the contract as it will exist after the imminent recording-format version reset. v12/v11 recordings are explicitly rejected at load.

---

## 1. Vision

Body-fixed (Absolute) world coordinates are the natural representation for debris trajectory. They have no anchor coupling, no lever-arm-amplified slerp errors, no shared-rotation channel across multi-piece separations, and they render correctly through the same standard `InterpolateAndPosition` path that solo-flight vessels use.

**The new contract for debris:**

- **Default (the overwhelmingly common case): every debris TrackSection is `ReferenceFrame.Absolute`.** No Relative debris sections exist. No `absoluteFrames` shadow. No anchor identity on the section.
- **Carve-out for loop-anchored parents** (§3.5): when a debris's parent recording is loop-anchored (`LoopAnchorVesselId != 0u`), the debris inherits the parent's `LoopAnchorVesselId` and records via the existing loop-anchored Relative path. The parent (`DebrisParentRecordingId`) and the loop anchor (the live vessel referenced by `LoopAnchorVesselId`) are typically *different* vessels — a canonical case is a tanker (the parent) approaching a station (the loop anchor) and shedding a fairing en route. This carve-out is rare (debris of a recording the player intentionally marked for loop playback) but mandatory for correctness: looped recordings replay against a live anchor whose orbital position advances between iterations, so debris in body-fixed coordinates would diverge from the loop anchor.
- **`Recording.DebrisParentRecordingId` retained for identification only** — tree hierarchy, ledger, diagnostics, future UI. Not consulted by recorder or renderer for frame or pose decisions.
- **Sampling cadence scales with distance to parent**, mirroring `ReFlyTreeSamplingCadence`:
  - 0–250 m → full fidelity (`configuredMin` interval)
  - 250–500 m → half fidelity (`2 × configuredMin`)
  - 500 m+ → normal adaptive cadence (existing path)
- **`FlightRecorder.HighFidelityProximityRangeMeters`** raised from `200.0` → `250.0` (`FlightRecorder.cs:935`) to match the new debris-side full-fidelity tier.
- **All v12 always-Relative debris machinery is removed**, with full call-site enumeration in §6. This includes:
  - `DebrisRelativePlaybackPolicy.cs`, `LegacyDebrisShadowGate.cs`, `DebrisRelativeRecorderPolicy.cs`, `TumblingParentInterpolationGate.cs`
  - `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow`
  - `TryRouteAnchorRotationUnreliable` / `TryRouteAnchorRotationToShadow`
  - `ShouldEvaluateAnchorRotationReliability` family
  - `AnchorRotationUnreliableRoute` enum
  - `BackgroundRecorder.ApplyDebrisAnchorContractToState` + its three call sites
  - All `state.anchorRotationShadowRoutedThisFrame` and `state.parentAnchoredDebrisCoverageRetired` field reads/writes
- **Format version bump to v13.** v12 and earlier are explicitly rejected at load via two changes detailed in §5.

## 2. What does NOT change

- `Source/Parsek/AnchorDetector.cs` — `ShouldUseRelativeFrame`, `RelativeFrameRangeLimit`, `IsRecordingAnchorEligible`. The `candidateRecording.IsDebris → false` exclusion at `:240` stays. The explanatory comment at `:230-239` references the v12 contract and should be updated to reflect the new contract (no longer cites the deleted `UpdateBackgroundAnchorDetection` early-return).
- `Source/Parsek/RelativeAnchorResolver.cs` — the recorded-anchor DAG resolver, except for the `AnchorRotationReliabilityDecision` parameter trim documented in §6.3 (the parameter exists today and threads through ~12 resolver signatures purely to feed the deleted gate; with the gate gone, the parameter is removed).
- All Re-Fly machinery: `RewindInvoker`, `SupersedeCommit`, `MergeJournalOrchestrator`, `ReFlySessionMarker`, the merge journal phases, ERS/ELS, `RecordingSupersedeRelation`.
- `Recording.LoopAnchorVesselId` — loop-anchor live-PID carve-out unchanged. **The v12 invariant "debris recordings always have `LoopAnchorVesselId == 0u`" is relaxed** under this plan; see §3.5. The carve-out itself (live-PID lookup at playback) is unchanged.
- Re-Fly tree sampling tiers (`ReFlyTreeFullFidelityProximityRangeMeters`, `ReFlyTreeHalfFidelityProximityRangeMeters`) for non-debris. They stay at `250.0` / `500.0` (`FlightRecorder.cs:936-937`).
- Non-debris recorder frame decisions (Absolute / Relative via `AnchorDetector.ShouldUseRelativeFrame` for non-debris vessels).
- The v7 `absoluteFrames` field on `TrackSection`. Non-debris Relative sections still write the shadow per existing contract; only debris stops emitting Relative sections (default case) or stops writing the shadow on its Relative sections (loop-anchored carve-out — see §3.5 note).
- `IPlaybackTrajectory.DebrisParentRecordingId` interface property (`IPlaybackTrajectory.cs:74`). Still exposed; consumed by ledger / map UI / diagnostics.
- `SessionMerger.cs` — its `absoluteFrames` and `DebrisParentRecordingId` handling operates per-section and is correct under the new contract (debris sections are Absolute and have no `absoluteFrames` to merge; the shadow-merge branch simply skips them).

If a change touches a file outside the debris-specific list in §6, that's a scope violation.

## 3. The contract

### 3.1 Recorder side (default, non-loop-anchored debris)

**Frame:** debris always writes `ReferenceFrame.Absolute`. Section anchor fields (`anchorRecordingId`, `anchorVesselId`) are null / 0. The `absoluteFrames` parallel list is not populated.

**Anchor identity for non-rendering purposes:** `Recording.DebrisParentRecordingId` is still set at split time. The existing `Recording.ApplyDebrisAnchorContract` (used at `BackgroundRecorder.cs:1130` and `:1263`) keeps its signature and behaviour — it still records *which* recording was the parent at separation. The field is data-only under v13.

**Cadence tiers (proximity-driven):**

New enum, mirrors `FlightRecorder.ReFlyTreeSamplingCadence` (`FlightRecorder.cs:29`):

```csharp
internal enum DebrisProximitySamplingCadence
{
    None = 0,  // Beyond 500 m: normal adaptive cadence applies.
    Half = 1,  // 250-500 m: 2 × min sample interval.
    Full = 2,  // 0-250 m: min sample interval.
}
```

New constants:

```csharp
internal const double DebrisFullFidelityProximityRangeMeters = 250.0;
internal const double DebrisHalfFidelityProximityRangeMeters = 500.0;
```

Resolver (mirrors `FlightRecorder.ResolveActiveReFlyTreeSamplingCadence` at `:752-819`):

```csharp
internal static DebrisProximitySamplingCadence ResolveDebrisProximitySamplingCadence(
    Recording debrisRecording,
    double parentDistanceMeters,
    out string reason)
{
    reason = null;
    if (debrisRecording == null
        || !debrisRecording.IsDebris
        || string.IsNullOrEmpty(debrisRecording.DebrisParentRecordingId))
    {
        reason = "not-parent-anchored-debris";
        return DebrisProximitySamplingCadence.None;
    }
    if (!IsFinite(parentDistanceMeters))
    {
        reason = "parent-distance-missing";
        return DebrisProximitySamplingCadence.None;
    }
    if (parentDistanceMeters <= DebrisFullFidelityProximityRangeMeters)
    {
        reason = "debris-proximity-full";
        return DebrisProximitySamplingCadence.Full;
    }
    if (parentDistanceMeters <= DebrisHalfFidelityProximityRangeMeters)
    {
        reason = "debris-proximity-half";
        return DebrisProximitySamplingCadence.Half;
    }
    reason = "debris-proximity-out-of-range";
    return DebrisProximitySamplingCadence.None;
}
```

Parent distance helper (precise filter — packed BG-loaded parents still have valid `worldPos`):

```csharp
internal static double ResolveDebrisParentDistanceMeters(
    Recording debrisRecording,
    Vessel debrisVessel)
{
    if (debrisRecording == null || debrisVessel == null) return double.NaN;
    Vessel parent = FlightRecorder.FindVesselByPid(debrisRecording.DebrisParentRecordingId != null
        ? ResolveParentVesselPid(debrisRecording.DebrisParentRecordingId) : 0u);
    if (parent == null || !parent.loaded) return double.NaN;
    // parent.loaded is true for both packed (on-rails inside the bubble)
    // and unpacked vessels; both have valid GetWorldPos3D().
    return Vector3d.Distance(parent.GetWorldPos3D(), debrisVessel.GetWorldPos3D());
}
```

`ResolveParentVesselPid` is a small lookup over `tree.Recordings` by RecordingId. If the parent's recording is missing from the tree, `parent` resolves to null and we return NaN, which makes `ResolveDebrisProximitySamplingCadence` return `None` (falls through to normal adaptive cadence). This is the right behaviour for an orphan debris: it samples at whatever the existing adaptive rule decides.

**Cadence composition with `ResolveDebrisAwareMaxSampleInterval`:** the new debris-proximity cadence **supersedes** the legacy debris-aware cap. The composition is:

```csharp
// In BackgroundRecorder.OnBackgroundPhysicsFrame (around :1958-1967):
DebrisProximitySamplingCadence debrisCadence = DebrisProximitySamplingCadence.None;
if (treeRec.IsDebris && !string.IsNullOrEmpty(treeRec.DebrisParentRecordingId))
{
    double parentDistance = ResolveDebrisParentDistanceMeters(treeRec, bgVessel);
    debrisCadence = ResolveDebrisProximitySamplingCadence(
        treeRec, parentDistance, out string reason);
    state.debrisProximityCadence = debrisCadence;
    state.debrisProximityDistanceMeters = parentDistance;
    state.debrisProximityReason = reason;
}

float effectiveMaxSampleInterval = FlightRecorder.ResolveEffectiveMaxSampleInterval(
    ReFlyTreeSamplingCadence.None,  // see §3.4 for Re-Fly vs debris priority
    debrisCadence,
    highFidelityActive,
    configuredMax,
    configuredMin);

if (debrisCadence == DebrisProximitySamplingCadence.None)
{
    // Beyond 500 m (or non-parent-anchored debris): fall through to the
    // existing debris-aware cap.
    effectiveMaxSampleInterval = ResolveDebrisAwareMaxSampleInterval(
        effectiveMaxSampleInterval, treeRec);
}
// When debris cadence is Full or Half, the proximity-tier interval IS
// the cap; don't compose with ResolveDebrisAwareMaxSampleInterval (its
// 0.5s mid-range cap would otherwise lengthen Half-tier intervals at
// Low density).
```

This explicitly avoids the 0.5s cap clobbering Half-tier intervals at Low density (the P1 issue from review).

**BG recorder state extension** (`BackgroundVesselState` in `BackgroundRecorder.cs:192-200`):

```csharp
// Add alongside existing highFidelitySamplingUntilUT / highFidelitySamplingReason:
public DebrisProximitySamplingCadence debrisProximityCadence = DebrisProximitySamplingCadence.None;
public double debrisProximityDistanceMeters = double.NaN;
public string debrisProximityReason;
```

**Extended `ResolveEffectiveMaxSampleInterval` / `ResolveEffectiveMinSampleInterval` signatures** at `FlightRecorder.cs:840-865` / `:821-838`: add the `DebrisProximitySamplingCadence debrisCadence` parameter to the cadence-aware overloads. Add a private helper `ResolveDebrisProximityCadenceSampleInterval` mirroring `ResolveReFlyTreeCadenceSampleInterval` at `:867-876`.

### 3.2 Renderer side

Debris renders through the standard absolute dispatch path used by solo-flight vessels:

- TrackSection is Absolute → engine calls `IGhostPositioner.InterpolateAndPosition` (`ParsekFlight.cs:16563-16634`) → `body.GetWorldSurfacePosition(lat, lon, alt)` → floating-origin correction → `Transform.position`.
- No anchor consulted.
- No tumble-parent gate.
- No coverage-retire on non-Relative section.
- No legacy v11/v12 shadow paths.

The renderer's existing pre-existing v12 debris-shadow path (`TryRouteAnchorRotationUnreliable`) becomes unreachable after the gate predicate is deleted; the entire router and its helpers are removed. See §6.3 for the full deletion enumeration.

**Loop-anchored debris (§3.5)** flows through the existing loop-anchored render path (`TryResolveLiveLoopAnchorPose` at `ParsekFlight.cs:21070-21160`). No new renderer code; the carve-out is entirely on the recorder side.

### 3.3 Visibility

Debris in Absolute renders independent of parent state. If the parent is destroyed or finalized post-recording, debris keeps rendering along its recorded body-fixed path until its own recording ends. No "bound to parent visibility" coupling exists at playback under the new contract.

**Live recording end-conditions for debris (`BackgroundRecorder.CheckDebrisTTL` at `:1300-1413`)** are preserved unchanged:

1. **Parent recording missing from tree** (`:1352-1361`) — ends the debris recording.
2. **Parent recording closed/superseded** (`:1375-1388`) — ends the debris recording.
3. **Parent vessel on-rails, destroyed, or unloaded** (`:1390-1404`) — ends the debris recording.

Plus end-conditions outside `CheckDebrisTTL`: debris destroyed (`OnVesselDestroyed`), scene exit (`OnSceneSwitch`), debris on-rails (`OnBackgroundPhysicsFrame` packed-state branch).

**Playback-time orphan handling:** when a player explicitly deletes a parent recording from the tree post-hoc, the debris recording's `DebrisParentRecordingId` becomes a dangling reference. The debris still renders fine (no parent needed for Absolute). The current behaviour is to leave the debris playing. If playtest feedback prefers retiring orphan-parent debris at playback, a follow-up PR can add the check; not in scope.

### 3.4 Re-Fly cadence vs debris cadence priority

In principle a debris recording could exist within an active Re-Fly tree if the player chose to re-fly the parent and the parent had debris. In practice the Re-Fly active recording is the parent (or a non-debris vessel), not the debris itself, so the Re-Fly cadence tier's `marker.ActiveReFlyRecordingId == debrisRecording.RecordingId` predicate is never satisfied for debris.

To future-proof, the `ResolveEffectiveMaxSampleInterval` priority order is:

1. Re-Fly cadence (if non-None) — wins
2. Debris-proximity cadence (if non-None) — wins next
3. Existing `highFidelityActive` / `configuredMax` fallback

This is encoded in the extended helper at `FlightRecorder.cs:840-865`. Tests in §7.1 cover all 9 combinations.

### 3.5 Carve-out: loop-anchored debris

**Terminology — two orthogonal references.** In this section, "parent" and "loop anchor" refer to two different things and must not be conflated:

- **Parent** = the recording pointed to by `DebrisParentRecordingId`. The vessel the debris was *separated from*. Identification only.
- **Loop anchor** = the live vessel pointed to by `LoopAnchorVesselId`. The vessel a loop-anchored recording is *positioned relative to* at playback. Rendering.

These are typically different vessels. A canonical example: a tanker craft (the parent) approaches a station (the loop anchor) intending to dock. The player loops this approach recording. During the approach, the tanker jettisons a fairing — that fairing's `DebrisParentRecordingId` is the tanker's recording, but the relevant loop anchor for visual continuity across loop iterations is the *station*, because that's what the tanker's recording loops around. The station's live orbital position advances between iterations; both the tanker and the jettisoned fairing must follow it.

**The inheritance rule.** If a debris piece is created from a parent whose recording has `LoopAnchorVesselId != 0u`, the debris inherits the parent's `LoopAnchorVesselId` (not the parent's identity — the live PID that the parent's recording was anchored to):

```csharp
// At BackgroundRecorder.RegisterChildRecordingsFromSplit (around :1130, :1263)
// or wherever ApplyDebrisAnchorContract is called:
if (parentRecording != null && parentRecording.LoopAnchorVesselId != 0u)
{
    childRecording.LoopAnchorVesselId = parentRecording.LoopAnchorVesselId;
}
```

The recorder then writes the debris through the **existing loop-anchored Relative path** — the same path non-debris loop-anchored recordings use today. Per-sample:
- The live anchor's pose is resolved via `FlightGlobals.FindVesselById(LoopAnchorVesselId)` — the station, in the canonical example.
- The debris's offset is computed relative to *that* live anchor's pose at sample time. **NOT relative to the parent** (the tanker, in the canonical example). The debris's `frames` will contain station-local offsets.
- The section is emitted as `ReferenceFrame.Relative` with `anchorVesselId = LoopAnchorVesselId`.
- The v7 `absoluteFrames` shadow is written per the existing v7 contract.

This means: at playback iteration N, both the tanker and the fairing render relative to the station's *current* (iteration-N) pose — `tanker.worldPos = station.livePose × tanker.recordedOffset` and `fairing.worldPos = station.livePose × fairing.recordedOffset`. The fairing's offset is its own — independently sampled against the station — not chained through the tanker.

Renderer dispatch for loop-anchored debris:
- Engine sees `LoopAnchorVesselId != 0u` → routes to `TryResolveLiveLoopAnchorPose` (`ParsekFlight.cs:21070-21160`).
- Live anchor pose × recorded offset → world position. Live anchor = the station (or whatever live vessel the parent was originally loop-anchored to). Recorded offset = the debris's own station-relative offset.
- **`DebrisParentRecordingId` is not consulted at render time** — it's still set on the recording (for identification, ledger, UI) but plays no role in placement. The renderer's only positional inputs are the live anchor's pose and the debris's own recorded offsets.
- The deleted `ShouldEvaluateAnchorRotationReliability` predicate excluded loop-anchored recordings anyway (`LoopAnchorVesselId == 0u` was a required condition), so its removal does not affect this path

**The cadence tier from §3.1 does NOT apply to loop-anchored debris.** The proximity computation is against the parent's recording-id, which has limited meaning for loop-anchored playback (the parent is itself loop-anchored to the same live vessel; "distance to parent" is dominated by their shared live-anchor motion). Loop-anchored debris uses the same cadence rules as the loop-anchored parent.

Cascade case: debris-of-debris under a loop-anchored ancestor propagates transparently. Tanker T is loop-anchored to station S → T sheds fairing F, F inherits T's `LoopAnchorVesselId == S.persistentId` → F sheds shard X, X inherits the same. All three (T, F, X) are positioned relative to S's live pose at each loop iteration; the chain of separations (T → F → X) is identification metadata only and never enters the placement math.

### 3.6 Cascade separations (debris-of-debris)

When debris piece A sheds debris piece B, B's `DebrisParentRecordingId` is set to A's `RecordingId` (the immediate parent), regardless of whether A is itself debris. The cadence resolver's `ResolveDebrisParentDistanceMeters` reads from whatever recording the id points to — debris or not. Cascade depth doesn't affect rendering (the renderer reads only the child's own samples).

`MaxRecordingGeneration` at the BG recorder (enforced at `BackgroundRecorder.cs:1629`) caps cascade depth. Beyond the cap, further debris-of-debris is silently dropped (existing rule).

## 4. Cadence-tier transitions

| Distance | Effective `maxSampleInterval` |
|---|---|
| `≤ 250 m` | `configuredMin` |
| `(250, 500] m` | `min(configuredMax, 2 × configuredMin)` |
| `> 500 m` | falls through to `ResolveDebrisAwareMaxSampleInterval` (existing path) |

No hysteresis on the tier boundaries (sample-interval changes are continuous; each sample picks an interval, no on-disk flapping). At orbital relative velocities (~1 km/s = 250 m crossed in 0.25 s) the tier-switch latency is bounded by one full-fidelity sample period.

## 5. Format version

Three coordinated changes are needed; the plan version-bump alone is not sufficient because the existing rejection mechanism is a hard-coded allowlist.

### 5.1 Constants

`Source/Parsek/RecordingStore.cs` (`:105-114`):

```csharp
// Add to the version-constant block:
internal const int DebrisAbsolutePrimaryFormatVersion = 13;
internal const int CurrentRecordingFormatVersion = DebrisAbsolutePrimaryFormatVersion;
```

`Source/Parsek/TrajectorySidecarBinary.cs`:

```csharp
internal const int DebrisAbsolutePrimaryBinaryVersion = RecordingStore.DebrisAbsolutePrimaryFormatVersion;
internal const int CurrentBinaryVersion = DebrisAbsolutePrimaryBinaryVersion;  // replaces line :72
```

### 5.2 Binary codec rejection

`TrajectorySidecarBinary.IsSupportedBinaryVersion` (`:403-416`) is currently a hard-coded allowlist accepting `LegacyBinaryVersion` through `DebrisParentRecordingBinaryVersion`. Replace the body with a single equality check:

```csharp
private static bool IsSupportedBinaryVersion(int version)
{
    return version == DebrisAbsolutePrimaryBinaryVersion;
}
```

Or equivalently with a min constant: `return version >= DebrisAbsolutePrimaryBinaryVersion;` — same effect for v13 since no later version exists yet.

The version-selection ladder at `:170-189` (currently a `>=` cascade ending at `DebrisParentRecordingBinaryVersion`) gets a new top rung:

```csharp
int binaryVersion = rec.RecordingFormatVersion >= DebrisAbsolutePrimaryBinaryVersion
    ? DebrisAbsolutePrimaryBinaryVersion
    : /* existing ladder for legacy versions becomes dead — see note below */;
```

Since `IsSupportedBinaryVersion` now rejects everything below v13, the ladder's legacy rungs are unreachable at write-time. Replace with an unconditional `binaryVersion = DebrisAbsolutePrimaryBinaryVersion;`. Reader can be similarly simplified (drop the per-version conditional branches now that only v13 is loadable).

This preserves the `Probe.FormatVersion == rec.RecordingFormatVersion` round-trip invariant for v13↔v13. Legacy ↔ v13 round-trip is intentionally broken — v12 saves are rejected at load.

### 5.3 ConfigNode codec rejection

`RecordingTreeRecordCodec.LoadRecordingFrom` (`Source/Parsek/RecordingTreeRecordCodec.cs:329`) has no version-rejection path today. The function parses `recordingFormatVersion` at `:443-449` defaulting to `0` on missing/invalid. The legacy-loop-migration branch at `:492-517` is the only pre-existing version-sensitive logic and it handles ancient versions, not future rejection.

Add an early-exit gate at the top of `LoadRecordingFrom`, immediately after the format version is parsed:

```csharp
if (rec.RecordingFormatVersion < RecordingStore.DebrisAbsolutePrimaryFormatVersion)
{
    ParsekLog.Warn("Codec",
        $"LoadRecordingFrom: rejecting recording {rec.RecordingId} with " +
        $"recordingFormatVersion={rec.RecordingFormatVersion} (< " +
        $"DebrisAbsolutePrimaryFormatVersion={RecordingStore.DebrisAbsolutePrimaryFormatVersion}). " +
        "v12 and earlier are no longer loadable.");
    rec.RecordingFormatVersion = 0;  // sentinel for caller to skip
    return;  // partial-state recording; caller checks rec.RecordingFormatVersion == 0
}
```

Caller-side check at `RecordingSidecarStore.cs` (the consumers around `:234, 543, 751, 1234` — verify exact lines): if `rec.RecordingFormatVersion == 0` after `LoadRecordingFrom`, skip the recording with a user-visible message rather than silently dropping it (which would look like missing trajectory data).

### 5.4 Update format-version table in `.claude/CLAUDE.md`

Add the new constant; mark v12 and earlier as no-longer-loadable. Reference `TrajectorySidecarBinary.IsSupportedBinaryVersion` and `RecordingTreeRecordCodec.LoadRecordingFrom` as the load-gate sites.

## 6. Concrete touchpoints

### 6.1 Files modified — recorder

| File | Lines | Change |
|---|---|---|
| `Source/Parsek/BackgroundRecorder.cs` | `:4295-4303` | Delete the `if (DebrisParentRecordingId != null) → ApplyDebrisAnchorContractToState; return;` early-return in `UpdateBackgroundAnchorDetection`. The function's debris-bypass becomes unnecessary because debris in Absolute uses standard anchor-detection (which returns "no anchor" for non-proximity scenarios, the typical debris case). Add a defensive `ParsekLog.Warn` if the function unexpectedly enters with a parent-anchored debris recording. |
| `Source/Parsek/BackgroundRecorder.cs` | `:4600-4685` (`ApplyDebrisAnchorContractToState`) | Delete entirely. |
| `Source/Parsek/BackgroundRecorder.cs` | `:4722-4728` (the third call site in `ApplyBackgroundRelativeOffset`, structural-event seam) | Delete the `if (treeRec != null && !string.IsNullOrEmpty(treeRec.DebrisParentRecordingId)) { ApplyDebrisAnchorContractToState(...); }` block. Absolute-only debris doesn't need a structural-event seam enforcement. |
| `Source/Parsek/BackgroundRecorder.cs` | `:3338-3460` (debris seed in `InitializeLoadedState`) | Simplify to always seed Absolute for non-loop-anchored debris. The Relative-seed branch and `ApplyBackgroundRelativeOffsetForAnchorPose` call become unreachable; use `CreateAbsoluteTrajectoryPointFromVessel` directly. (Loop-anchored debris still seeds Relative via the existing loop-anchored path.) |
| `Source/Parsek/BackgroundRecorder.cs` | `:1868-1878` (Re-Fly settle suppression for debris) + `:2046-2053` (`ShouldSuppressParentDebrisForReFlySettle`) | Delete both. The suppression's rationale ("a Relative section written against a suppressed parent would leave the resolver unable to walk back to a parent pose") doesn't apply to Absolute debris; sample gaps it introduces are now pure regression. |
| `Source/Parsek/BackgroundRecorder.cs` | `:4122-4128` (`ShouldPreferRootPartSurfacePoseForBackgroundSample`) | Keep the helper; update the comment to reference the new contract (root-part pose continuity between seed and ordinary samples — still applies under v13). |
| `Source/Parsek/BackgroundRecorder.cs` | `:1958-1967` (cadence wiring in `OnBackgroundPhysicsFrame`) | Insert the `DebrisProximitySamplingCadence` resolution per §3.1 pseudocode. Feed into the extended `ResolveEffectiveMaxSampleInterval` overload. |
| `Source/Parsek/BackgroundRecorder.cs` | new field block on `BackgroundVesselState` near `:192-200` | Add `debrisProximityCadence`, `debrisProximityDistanceMeters`, `debrisProximityReason` fields. |
| `Source/Parsek/BackgroundRecorder.cs` | new method | Add `ResolveDebrisProximitySamplingCadence` and `ResolveDebrisParentDistanceMeters` static helpers per §3.1. |
| `Source/Parsek/BackgroundRecorder.cs` | `:1130, :1263` (`Recording.ApplyDebrisAnchorContract` call sites) | Extend the contract: after setting `DebrisParentRecordingId`, also propagate `LoopAnchorVesselId` from parent if non-zero (per §3.5). |
| `Source/Parsek/FlightRecorder.cs` | `:935` | `HighFidelityProximityRangeMeters = 250.0` (was `200.0`). |
| `Source/Parsek/FlightRecorder.cs` | `:821-865` | Add `DebrisProximitySamplingCadence debrisCadence` parameter to the cadence-aware `ResolveEffectiveMaxSampleInterval` and `ResolveEffectiveMinSampleInterval` overloads. |
| `Source/Parsek/FlightRecorder.cs` | new private at `:867+` | Add `ResolveDebrisProximityCadenceSampleInterval` mirroring `ResolveReFlyTreeCadenceSampleInterval` at `:867-876`. |
| `Source/Parsek/RecordingStore.cs` | `:105-114` | Add `DebrisAbsolutePrimaryFormatVersion = 13`; bump `CurrentRecordingFormatVersion`. |
| `Source/Parsek/TrajectorySidecarBinary.cs` | `:31, :71-72` | Add `DebrisAbsolutePrimaryBinaryVersion`; update `CurrentBinaryVersion`. |
| `Source/Parsek/TrajectorySidecarBinary.cs` | `:170-189` | Replace version-selection ladder with unconditional v13 stamp. |
| `Source/Parsek/TrajectorySidecarBinary.cs` | `:403-416` (`IsSupportedBinaryVersion`) | Replace with single-value or `>=` check per §5.2. |
| `Source/Parsek/RecordingTreeRecordCodec.cs` | inside `LoadRecordingFrom` at `:329` (early after `:443-449` format-version parse) | Add the `< DebrisAbsolutePrimaryFormatVersion` rejection per §5.3. |
| `Source/Parsek/RecordingSidecarStore.cs` | consumer sites of `LoadRecordingFrom` | If `rec.RecordingFormatVersion == 0` after the call, surface a user-visible "unsupported format" message. (Exact lines depend on the existing call-site pattern; the implementer locates them via grep on `LoadRecordingFrom`.) |

### 6.2 Files deleted

| File | Reason |
|---|---|
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | All call sites cleaned up; no debris-specific coverage gating under v13. |
| `Source/Parsek/LegacyDebrisShadowGate.cs` | v11 retroactive fix; legacy recordings are no longer loadable. |
| `Source/Parsek/DebrisRelativeRecorderPolicy.cs` | Encoded v12 all-Relative tail normalization. |
| `Source/Parsek/TumblingParentInterpolationGate.cs` | Only consumer was the deleted `ShouldEvaluateAnchorRotationReliability` family. |
| `Source/Parsek.Tests/DebrisRelativePlaybackPolicyTests.cs` | Owns tests for the deleted file. |
| `Source/Parsek.Tests/DebrisRelativeRecorderPolicyTests.cs` | Owns tests for the deleted file. |
| `Source/Parsek.Tests/LegacyDebrisShadowGateTests.cs` | Owns tests for the deleted file. |
| `Source/Parsek.Tests/TumblingParentInterpolationGateTests.cs` | Owns tests for the deleted file. |

### 6.3 Files modified — renderer cleanup

| File | Lines | Action |
|---|---|---|
| `Source/Parsek/IGhostPositioner.cs` | `:78-80` | Remove `TryPositionFromRelativeAbsoluteShadow` method signature. |
| `Source/Parsek/ParsekFlight.cs` | `:16717-16810` | Remove `TryPositionFromRelativeAbsoluteShadow` implementation. |
| `Source/Parsek/ParsekFlight.cs` | `:15117-15170` | Remove `ShouldEvaluateAnchorRotationReliability`, `ShouldEvaluateAnchorRotationReliabilityForTesting`, `TryEvaluateAnchorRotationReliability`. |
| `Source/Parsek/ParsekFlight.cs` | `:22857-22905` (`TryUseLegacyDebrisShadowFallback`) | Remove the entire helper. |
| `Source/Parsek/ParsekFlight.cs` | `:22907-22933` (`TryRetireParentAnchoredDebrisOnRecordedAnchorMiss`) | Remove the entire helper. |
| `Source/Parsek/ParsekFlight.cs` | `:22947-22961` (the debris-only guard call inside `TryUseRelativeAbsoluteShadowFallback`) | Remove the inner debris-only guard. The wrapper itself stays (still used by non-debris Relative recovery at `:22148, :22207, :22291`). |
| `Source/Parsek/ParsekFlight.cs` | `:22121, :22180, :22274` | Remove the `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible` branches. |
| `Source/Parsek/ParsekFlight.cs` | `:17905, :18078, :20129, :22085` | Remove `DebrisRelativePlaybackPolicy.ShouldRetireOutsideAuthoredRelativeCoverage` calls. |
| `Source/Parsek/ParsekFlight.cs` | `:18087, :20138, :22088` | Remove `DebrisRelativePlaybackPolicy.ShouldSkipRecordedRelativeResolverForAuthoredFrameGap` calls. |
| `Source/Parsek/ParsekFlight.cs` | `:14883-14888` (Re-Fly anchor hold reading `DebrisParentRecordingId`) | Audit and either preserve (if still meaningful for Re-Fly accounting) or delete. Recommend preserve with a comment update — debris is still tied to parent for Re-Fly identity even under v13. |
| `Source/Parsek/ParsekFlight.cs` | `:16701` (XML doc reference to `anchorRotationShadowRoutedThisFrame`) | Delete the doc reference. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2796-2909` (`TryRouteAnchorRotationUnreliable`), `:2928-3006` (`TryRouteAnchorRotationToShadow`) | Remove both methods. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:1150-1216` (`RenderInRangeGhost`), `:3297-3339` (`PositionLoopAtPlaybackUT`) | Remove the `anchorRotationRoute` branches; engine falls through to standard positioning. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:1148, :1249, :1256, :1418, :1661, :1847, :1861, :2124, :2134, :2223, :2320, :2331, :2380, :5203, :5242` | Remove all reads / writes of `state.anchorRotationShadowRoutedThisFrame`. The FX-suppression branches that consumed this flag (camera, audio, plumes during tumble windows) become unreachable in the debris path because the tumble gate is gone. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2977, :2983, :3031, :3136, :3163, :3223` | Remove all reads / writes of `state.parentAnchoredDebrisCoverageRetired`. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2715, :2717` (debris coverage gate) | Remove the `ShouldRetireOutsideAuthoredRelativeCoverage` consultation. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:2760, :2819, :5404` | Remove `ShouldSkipRecordedRelativeResolverForAuthoredFrameGap` consultations. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:3133, :3160, :3215` | Same. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `:5562, :5652, :5668` (`TryResolveInitialStructuralSeedBridgeEndUT` call sites) | Remove the calls. Verify no other consumer of the helper exists; if not, the helper goes with `DebrisRelativePlaybackPolicy.cs`. |
| `Source/Parsek/GhostPlaybackEngine.cs` | `AnchorRotationUnreliableRoute` enum decl and all references | Remove. |
| `Source/Parsek/GhostPlaybackState.cs` | `:93` (`anchorRotationShadowRoutedThisFrame` field), `:99` (`parentAnchoredDebrisCoverageRetired` field), `:152` (reset in `ResetGhostState`) | Remove. |
| `Source/Parsek/GhostPlaybackEvents.cs` | `:89, :102` (`shadowRouted` doc + parameter) | Remove the `shadowRouted` parameter from the rendered-frame event. |
| `Source/Parsek/RecordingFinalizationCacheApplier.cs` | `:119` (`NormalizeParentAnchoredRelativeRecording` call) | Remove. |
| `Source/Parsek/RecordingFinalizationCacheApplier.cs` | `:162` (`TryGetLastRecorderPersistableAuthoredUT` call), `:169` (`ShouldNormalizeParentAnchoredDebris` check) | Remove the v12 parent-anchored-debris branch in `TryGetLastAuthoredUT`; fall through to standard last-frame UT resolution. |
| `Source/Parsek/Recording.cs` | `ApplyDebrisAnchorContract` | Keep signature. Update internal logic to also propagate `LoopAnchorVesselId` (per §3.5 — actually this propagation happens at the call site in `BackgroundRecorder`, not inside `ApplyDebrisAnchorContract`. The contract method continues to do exactly what it does today; the loop-anchor inheritance is a separate adjacent line in the caller.) |
| `Source/Parsek/RelativeAnchorResolver.cs` | `:194, :269, :435, :668, :1045, :1398, :1534, :1898, :2028, :2126, :2138, :2342` | Trim the `AnchorRotationReliabilityDecision` parameter from the ~12 method signatures. The parameter's only consumer was the deleted gate; no current call site uses it for routing. |

### 6.4 Files audited — no-op

| File | Verification |
|---|---|
| `Source/Parsek/AnchorDetector.cs` | `IsRecordingAnchorEligible` debris exclusion at `:240` stays. Update comment at `:230-239` to reflect removed `UpdateBackgroundAnchorDetection` early-return. |
| `Source/Parsek/ParsekConfig.cs:30-65` | Unchanged — debris doesn't use `RelativeFrame.EntryMeters` / `ExitMeters`. |
| `Source/Parsek/SessionMerger.cs` | `:149` (carries `DebrisParentRecordingId`) and `:803-1440` (`absoluteFrames` merge logic) both operate per-section; under v13 debris sections are Absolute with no `absoluteFrames`, so the shadow-merge branch is correctly skipped. No code change needed. |
| `Source/Parsek/EffectiveState.cs:1095-1170` | `DebrisParentRecordingId` lookup for ERS/ELS continues to function (the field is still set on the recording). |
| `Source/Parsek/IPlaybackTrajectory.cs:74` | `DebrisParentRecordingId` getter stays — consumed by ledger / map / future filter consumers. |
| `Source/Parsek/GhostMapPresence.cs` | If it references `DebrisParentRecordingId`, it's for identification — unchanged. |
| `ParsekFlight.cs:4880` | Only call site of `RegisterHighFidelityProximityVessel`. Constant raise to 250 m propagates with no call-site change. |

### 6.5 Grep audits before merge

```bash
# Verify all debris-shadow code paths are gone:
grep -rn "DebrisRelativePlaybackPolicy\|DebrisRelativeRecorderPolicy\|LegacyDebrisShadowGate" Source/Parsek/  # zero results
grep -rn "TryPositionFromRelativeAbsoluteShadow\|TryRouteAnchorRotationUnreliable\|TryRouteAnchorRotationToShadow" Source/Parsek/  # zero results
grep -rn "ShouldEvaluateAnchorRotationReliability\|AnchorRotationUnreliableRoute" Source/Parsek/  # zero results
grep -rn "anchorRotationShadowRoutedThisFrame\|parentAnchoredDebrisCoverageRetired" Source/Parsek/  # zero results
grep -rn "TumblingParentInterpolationGate" Source/Parsek/  # zero results
grep -rn "ApplyDebrisAnchorContractToState" Source/Parsek/  # zero results

# Verify expected v13 patterns:
grep -rn "DebrisProximitySamplingCadence\|DebrisFullFidelityProximityRangeMeters" Source/Parsek/  # >= 5 results
grep -rn "DebrisAbsolutePrimaryFormatVersion\|DebrisAbsolutePrimaryBinaryVersion" Source/Parsek/  # >= 4 results

# Verify non-debris uses are preserved:
grep -rn "absoluteFrames" Source/Parsek/  # non-debris call sites only (non-debris Relative sections still write shadow)
grep -rn "DebrisParentRecordingId" Source/Parsek/  # recorder + diagnostics + UI + ledger; no rendering-decision sites

# Verify Re-Fly isn't touched:
scripts/grep-audit-non-loop-live-pid.ps1
scripts/grep-audit-ers-els.ps1
```

## 7. Tests

### 7.1 Recorder unit tests

| Test | Setup | Assertion |
|---|---|---|
| `Debris_AlwaysOpensAbsoluteSection` | Debris (non-loop-anchored) created at any distance | First TrackSection is `Absolute`, `anchorRecordingId` is null |
| `Debris_NeverEmitsRelativeSection` | Non-loop-anchored debris drifts 50 → 5000 m | Every section is Absolute; no `absoluteFrames` populated |
| `Debris_LoopAnchored_OpensRelative` | Debris created from a loop-anchored parent | First section is Relative; `LoopAnchorVesselId` inherited from parent; `anchorVesselId == parent.LoopAnchorVesselId` |
| `Debris_LoopAnchored_RendersThroughLoopAnchorPath` | Loop-anchored debris at playback | `TryResolveLiveLoopAnchorPose` invoked; no debris-specific render code |
| `Debris_CascadeOfLoopAnchored_InheritsLoopAnchor` | Loop-anchored A sheds B sheds C | `C.LoopAnchorVesselId == A.LoopAnchorVesselId` |
| `Debris_CadenceTier_0to250m_UsesFullFidelity` | Debris at 100 m | Effective `maxSampleInterval` == `configuredMin` |
| `Debris_CadenceTier_250to500m_UsesHalfFidelity` | Debris at 350 m | Effective `maxSampleInterval` == `min(configuredMax, 2 × configuredMin)` |
| `Debris_CadenceTier_Beyond500m_NormalAdaptive` | Debris at 1500 m | Falls through to `ResolveDebrisAwareMaxSampleInterval` |
| `Debris_CadenceTier_ProportionalToDensitySetting` | Debris at 100 m, Low vs High density | Sample-count ratio matches density-setting ratio |
| `Debris_CadenceTier_HalfTier_NotClobberedByDebrisAwareCap` | Debris at 350 m with Low density (`configuredMin = 0.5 s`, so Half tier = 1.0 s) | Half-tier interval is 1.0 s, not capped at 0.5 s (the legacy `ResolveDebrisAwareMaxSampleInterval` 0.5 s cap is bypassed when proximity cadence is non-None) |
| `Debris_CadenceComposition_OrthogonalityMatrix` (table-driven, 9 cases) | Cross-product of `ReFlyTreeSamplingCadence ∈ {None, Half, Full}` × `DebrisProximitySamplingCadence ∈ {None, Half, Full}` | Priority order: Re-Fly Full > Re-Fly Half > debris Full > debris Half > existing fallback. Verify each cell. |
| `Debris_DebrisParentRecordingId_StillSetOnSplit` | Standard separation | `child.DebrisParentRecordingId == parent.RecordingId` |
| `Debris_ParentDestroyed_StillRecordsAbsolute` | Parent destroyed mid-recording | Debris recording continues; samples are Absolute |
| `Debris_OnRails_NoTrackSection` | Debris transitions to packed | `BackgroundOnRailsState`, no TrackSection (existing rule) |
| `Debris_CascadeSeparation_ParentIdIsImmediateParent` | Debris A sheds debris B | `B.DebrisParentRecordingId == A.RecordingId` |
| `Debris_CascadeSeparation_DepthCapped` | Cascade depth > `MaxRecordingGeneration` | Further generations dropped (existing rule, regression test) |
| `Debris_ReFlySettleSuppression_Removed` | Debris of a Re-Fly settle parent | Samples continue (no suppression gap) |
| `Debris_OrphanParentRecording_ResolverReturnsNaN` | Debris with `DebrisParentRecordingId` pointing to a missing recording | `ResolveDebrisParentDistanceMeters` returns NaN; cadence falls through to None; samples use existing adaptive rule |

### 7.2 Renderer unit tests (`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`)

| Test | Setup | Assertion |
|---|---|---|
| `Debris_AbsoluteSection_StandardDispatch` | Debris in Absolute (any distance) | `InterpolateAndPosition` invoked; no anchor lookup; no tumble-gate evaluation |
| `Debris_ParentTumbling_StillRenders` | Debris with parent rotating at 200 °/s | Standard absolute path runs; debris remains visible |
| `Debris_ParentDestroyed_StillRenders` | Debris with parent recording absent | Renders normally; no visibility coupling |
| `Debris_LongRange_NoSyncedSwing` | Multiple debris pieces from same parent, varying distances | Per-frame position deltas independent of any shared rotation channel |
| `Debris_LoopAnchored_RendersThroughLiveAnchor` | Loop-anchored debris during loop iteration 5 | Rendered position = `live anchor pose × recorded offset`; loop iteration N reflected in live anchor's current orbital position |

### 7.3 Format-version unit tests

| Test | Setup | Assertion |
|---|---|---|
| `V12Recording_LoadFails_BinaryCodec` | Synthetic v12 .prec | `TrajectorySidecarBinary` returns `Supported = false`; load is rejected |
| `V11Recording_LoadFails_BinaryCodec` | Synthetic v11 .prec | Same |
| `V12Recording_LoadFails_ConfigNodeCodec` | Synthetic v12 ConfigNode | `LoadRecordingFrom` sets `rec.RecordingFormatVersion = 0`; caller skips |
| `V13Recording_RoundTrip` | Write v13 recording, reload | Identical content; `RecordingFormatVersion == 13` |
| `V13Recording_ProbeFormatVersionMatchesRecording` | Newly-written v13 sidecar | `Probe.FormatVersion == rec.RecordingFormatVersion == 13` |

### 7.4 In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_SeparationEvent_CadenceTransitions` | Booster separation; fly out past 250 m, 500 m | `[Recorder]` logs show tier transitions Full → Half → None |
| `Debris_TumblingParent_NoArtifact` | Parent at 200 °/s, debris within 200 m | Debris ghost remains smooth (no shadow-routing infrastructure, just dense samples + standard absolute) |
| `Debris_PhysicsWarp_TierBoundary_NoArtifact` | Debris crossing 500 m during 2× physics warp | Cadence transitions cleanly |
| `Debris_AbsoluteSection_ParentSuperseded_ContinuesRendering` | Debris with parent re-flown (superseded) | Debris ghost renders unchanged |
| `Debris_LoopAnchored_LoopIterations_FollowsLiveAnchor` | Loop-anchored debris over 5 iterations | Each iteration's rendered position uses the live anchor's current pose |
| `Debris_CadenceProportionalToDensity` | Same scenario at Low vs High density | Sample count in 0–250 m window scales with density setting |

In-game test files affected by the field deletions (need adjustment when `anchorRotationShadowRoutedThisFrame` and `parentAnchoredDebrisCoverageRetired` go away): `RuntimeTests.cs:4278, 4390-4391, 4524, 4660`. The shadow-route assertions get rewritten to assert the absolute-path behaviour (or removed as obsolete).

### 7.5 Test fixture

`Source/Parsek.Tests/Generators/DebrisAbsolutePrimaryRecordingFixture.cs`: builds synthetic debris recording with multiple Absolute TrackSections, samples reflecting the three cadence tiers, `DebrisParentRecordingId` set. Variant for loop-anchored debris (sets `LoopAnchorVesselId` on the child, includes Relative section to live anchor).

### 7.6 Existing test files affected

| Test file | Disposition |
|---|---|
| `DebrisRelativePlaybackPolicyTests.cs` | Delete (file deleted) |
| `DebrisRelativeRecorderPolicyTests.cs` | Delete |
| `LegacyDebrisShadowGateTests.cs` | Delete |
| `TumblingParentInterpolationGateTests.cs` | Delete |
| `DebrisParentAnchorContractTests.cs` | Full rewrite (asserts v12 Relative-recording contract; must be updated for Absolute-only) |
| `ResolveReFlySettleStabilityTests.cs` | Review; if the test asserts the now-removed `ShouldSuppressParentDebrisForReFlySettle`, delete those cases |
| `Bug362TerminalCrashDebrisTests.cs` | Review |
| `BoosterStagingSplitTriggerTests.cs` | Review |
| `BackgroundSplitTests.cs` | Review (debris-seed paths changed) |
| `RuntimeTests.cs` lines `4278, 4390-4391, 4524, 4660` | Rewrite or remove (asserted deleted-field behaviour) |

## 8. Rollout

Land as a single PR or split into commits per the project's preference:

1. **Format version bump + codec gates** (commit 1). Constants in `RecordingStore.cs` and `TrajectorySidecarBinary.cs`; prune `IsSupportedBinaryVersion`; add `LoadRecordingFrom` early-exit. Add format-version unit tests (§7.3). After this commit, v12/v11 saves are rejected at load; new recordings still write v12-shape data because the recorder hasn't been updated.
2. **Renderer cleanup** (commit 2). Delete `DebrisRelativePlaybackPolicy`, `LegacyDebrisShadowGate`, `TumblingParentInterpolationGate`, `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow`, `ShouldEvaluateAnchorRotationReliability` family, router methods. Remove all `anchorRotationShadowRoutedThisFrame` / `parentAnchoredDebrisCoverageRetired` field references. Trim `RelativeAnchorResolver` signatures. Compile clean; expect debris-related test failures.
3. **Recorder change** (commit 3). Remove `ApplyDebrisAnchorContractToState` and all three call sites; remove Re-Fly settle suppression for debris; simplify debris seed; add `DebrisProximitySamplingCadence` resolver, state, and wiring; raise `HighFidelityProximityRangeMeters` to 250 m; propagate `LoopAnchorVesselId` to debris children of loop-anchored parents; delete `DebrisRelativeRecorderPolicy` and its call sites.
4. **Tests** (commit 4): new test fixture + unit tests from §7.
5. **Docs** (commit 5): CHANGELOG; update `.claude/CLAUDE.md` format-version table and debris-shadow notes; close relevant `docs/dev/todo-and-known-bugs.md` entries.

Pre-merge:
- `dotnet test` all green
- Re-Fly in-game tests pass (scope-creep gate)
- `scripts/grep-audit-non-loop-live-pid.ps1` and `scripts/grep-audit-ers-els.ps1` clean
- §6.5 grep audits return expected results (zero hits for deleted, expected hits for new)
- Playtest: separation events, cadence transitions, tumbling parent, long-range drift, density-setting proportionality, loop-anchored debris scenario

## 9. Out of scope (follow-ups)

- **BG-parent hi-fi sampling extension**: extending `RegisterHighFidelityProximityVessel` to BG-recorded parents of in-proximity debris. Not strictly needed because debris's own cadence tier governs its sample density, and the renderer reads only debris's own samples.
- **Visibility-decoupling playtest evaluation**: under v13 the "debris bound to parent visibility" rule is gone. If playtest feedback prefers re-introducing a debris-visibility check tied to parent existence at playback (e.g. retire debris when its parent recording is deleted from the tree), a follow-up PR can add it.
- **`DebrisParentRecordingId` UI surfacing**: filter debris by parent, group debris-of-rocket together. Field is set; UI is separate work.
- **Recorder denser sampling for fast-debris-rotation**: independent concern affecting debris attitude reconstruction, not position.
- **Loop-anchored debris cadence tier**: §3.5 leaves loop-anchored debris on the same cadence as the loop-anchored parent. If playtest shows looped debris needs its own proximity-tier, can be added; but proximity to parent is dominated by shared live-anchor motion in this case, so the tier's signal is weak.

## 10. Plan evolution

Five drafts on the branch:

- `4b9a9bd` — first draft: proximity-gated at 2300/2500 m, revert PR #803.
- `257ff31` — second draft (first review): keep PR #803 inside bounded Relative window.
- `17cb5db` — third draft (user feedback): absolute-primary always; tiered cadence; experimental 0–250 m Relative shadow.
- `4826ede` — fourth draft (first Opus review + version-reset directive): drop experimental Relative layer; debris is Absolute-only.
- **current** — fifth draft (second Opus review + loop-anchored debris amendment): explicit format-version rejection mechanism (P0); enumeration of every deleted call site (P0); loop-anchored debris carve-out as the one legitimate exception to "always Absolute"; `TumblingParentInterpolationGate` and `AnchorRotationReliabilityDecision` plumbing removed (P1); cadence composition with `ResolveDebrisAwareMaxSampleInterval` made explicit (P1); orthogonality matrix tests (P1); Re-Fly settle suppression for debris removed (P1); test files affected enumerated (P2).

The current plan is implementation-ready: every deletion has a file:line citation, every addition has a code shape, every test category has a setup/assertion table, and the format-version reset mechanism is spelled out.
