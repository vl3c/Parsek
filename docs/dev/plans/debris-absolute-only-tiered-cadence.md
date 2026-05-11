# Absolute-only debris contract with proximity-tiered cadence

**Status:** redraft (replaces prior drafts; final review before implementation)
**Scope:** debris recording and rendering only. No changes to Re-Fly machinery, non-debris recorder/renderer, `RelativeAnchorResolver`, or loop-anchor live-PID handling.
**Compatibility:** none. This plan describes the contract as it will exist after the imminent recording-format version reset. Legacy v12/v11 debris recordings are not loadable under the new format; no migration path is provided.

---

## 1. Vision

Body-fixed (Absolute) world coordinates are the natural representation for debris trajectory. They have no anchor coupling, no lever-arm-amplified slerp errors, no shared-rotation channel across multi-piece separations, and they render correctly through the same standard `InterpolateAndPosition` path that solo-flight vessels use. Debris doesn't need Relative-to-parent reconstruction at any distance: at close range, sufficient sampling density gives sub-metre rendering precision through dense interpolation; at long range, no anchor coupling is needed at all.

**The new contract:**

- **Every debris TrackSection is `ReferenceFrame.Absolute`.** No Relative debris sections exist. No `absoluteFrames` shadow track. No anchor identity stored on debris sections.
- **`Recording.DebrisParentRecordingId` is retained for identification only** — it records *which* recording was the parent at separation, useful for ledger relationships, group hierarchy, and future debris-specific UI. It is not consulted by the renderer.
- **Sampling cadence scales with distance to parent**, mirroring Re-Fly tree tiers exactly:
  - 0–250 m → full fidelity (configured `minSampleInterval`)
  - 250–500 m → half fidelity (`2 × minSampleInterval`)
  - 500 m+ → normal adaptive cadence
- **`FlightRecorder.HighFidelityProximityRangeMeters`** raised from `200.0` → `250.0` (`FlightRecorder.cs:935`) to align the parent-side density rule with the new debris-side full-fidelity tier.
- **All v12 always-Relative debris machinery is removed**: `DebrisRelativePlaybackPolicy`, `LegacyDebrisShadowGate`, `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow`, `TryRouteAnchorRotationUnreliable`, `TryRouteAnchorRotationToShadow`, `ShouldEvaluateAnchorRotationReliability`, `TryEvaluateAnchorRotationReliability`, and the `BackgroundRecorder.ApplyDebrisAnchorContractToState` unconditional Relative shortcut at `BackgroundRecorder.cs:4286-4303`.

The result is a smaller surface area, a simpler renderer dispatch, and no debris-specific render path at all — debris and a solo-flying vessel render through identical code.

## 2. What does NOT change

- `Source/Parsek/AnchorDetector.cs` — `ShouldUseRelativeFrame`, `RelativeFrameRangeLimit`, `IsRecordingAnchorEligible`. The `candidateRecording.IsDebris → false` exclusion at `:240` stays (still prevents non-debris from anchoring to debris in case any non-debris ever has a debris piece in proximity).
- `Source/Parsek/RelativeAnchorResolver.cs` — the recorded-anchor DAG resolver. Debris no longer participates as either anchor or anchored vessel under the new contract.
- All Re-Fly machinery: `RewindInvoker`, `SupersedeCommit`, `MergeJournalOrchestrator`, `ReFlySessionMarker`, the merge journal phases, ERS/ELS, `RecordingSupersedeRelation`.
- `Recording.LoopAnchorVesselId` — loop-anchor live-PID carve-out unchanged. **Invariant: debris recordings have `LoopAnchorVesselId == 0u` always**; debris is excluded from loop-anchored playback by construction (no spec-supported path sets the field on debris).
- Re-Fly tree sampling tiers (`ReFlyTreeFullFidelityProximityRangeMeters`, `ReFlyTreeHalfFidelityProximityRangeMeters`) for non-debris. They stay at `250.0` / `500.0` (`FlightRecorder.cs:936-937`).
- Non-debris recorder frame decisions (Absolute / Relative via `AnchorDetector.ShouldUseRelativeFrame` for non-debris vessels).
- The v7 `absoluteFrames` field on `TrackSection`. Non-debris Relative sections still write the shadow per existing contract. Only debris stops using it.

If a change touches a file outside the debris-specific list in §7, that's a scope violation.

## 3. The contract

### 3.1 Recorder side

**Frame:** debris always writes `ReferenceFrame.Absolute`. Section anchor fields (`anchorRecordingId`, `anchorVesselId`) are null / 0 on debris sections. The `absoluteFrames` parallel list is not populated.

**Anchor identity for non-rendering purposes:** `Recording.DebrisParentRecordingId` is still set on the recording at split time (existing `Recording.ApplyDebrisAnchorContract` keeps its signature and behaviour). The field is now data-only — no recorder or renderer code path consults it for frame or pose decisions. It remains useful for:
- `RecordingTree` hierarchy and group display
- Ledger relationships (insurance payouts, fault chains)
- Future debris-specific UI (e.g. "show only debris of this rocket")
- Diagnostic logs

**Cadence tiers (proximity-driven):**

```csharp
// New enum, mirrors FlightRecorder.ReFlyTreeSamplingCadence (FlightRecorder.cs:29).
// Lives in BackgroundRecorder or a shared static class.
internal enum DebrisProximitySamplingCadence
{
    None = 0,  // Beyond 500 m: normal adaptive cadence applies.
    Half = 1,  // 250-500 m: 2 × min sample interval.
    Full = 2,  // 0-250 m: min sample interval.
}
```

```csharp
// New constants in BackgroundRecorder (or shared with FlightRecorder).
internal const double DebrisFullFidelityProximityRangeMeters = 250.0;
internal const double DebrisHalfFidelityProximityRangeMeters = 500.0;
```

```csharp
// Resolver, called per-sample on the BG recorder path.
// Mirrors FlightRecorder.ResolveActiveReFlyTreeSamplingCadence (:752-819).
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

The cadence value composes with the existing `maxSampleInterval` resolution exactly as `ReFlyTreeSamplingCadence` does. Extend the existing helper signature:

```csharp
// FlightRecorder.cs:840-876 currently has:
//   internal static float ResolveEffectiveMaxSampleInterval(
//       bool highFidelityActive, float configuredMax, float configuredMin)
//   internal static float ResolveEffectiveMaxSampleInterval(
//       ReFlyTreeSamplingCadence reFlyTreeCadence,
//       bool highFidelityActive, float configuredMax, float configuredMin)
//
// Add a sibling overload (or extend the existing one to take both cadence
// enums — simpler since they're orthogonal):

internal static float ResolveEffectiveMaxSampleInterval(
    ReFlyTreeSamplingCadence reFlyTreeCadence,
    DebrisProximitySamplingCadence debrisCadence,
    bool highFidelityActive,
    float configuredMax,
    float configuredMin)
{
    // Re-Fly cadence wins over debris cadence (a Re-Fly tree containing debris
    // is being actively re-flown by the player; that flag is more specific
    // than the debris proximity tier).
    if (reFlyTreeCadence != ReFlyTreeSamplingCadence.None)
        return ResolveReFlyTreeCadenceSampleInterval(
            reFlyTreeCadence, configuredMax, configuredMin);
    if (debrisCadence != DebrisProximitySamplingCadence.None)
        return ResolveDebrisProximityCadenceSampleInterval(
            debrisCadence, configuredMax, configuredMin);
    return ResolveEffectiveMaxSampleInterval(
        highFidelityActive, configuredMax, configuredMin);
}

private static float ResolveDebrisProximityCadenceSampleInterval(
    DebrisProximitySamplingCadence debrisCadence,
    float configuredMax,
    float configuredMin)
{
    float configuredMinClamped = Math.Max(0f, configuredMin);
    if (debrisCadence == DebrisProximitySamplingCadence.Half)
        configuredMinClamped *= 2f;
    return Math.Min(configuredMax, configuredMinClamped);
}
```

Mirror the same signature change on `ResolveEffectiveMinSampleInterval` (`FlightRecorder.cs:821-838`).

**BG recorder state extension** (in `BackgroundVesselState` at `BackgroundRecorder.cs`):

```csharp
// Existing fields nearby:
// public double highFidelitySamplingUntilUT = double.NaN;
// public string highFidelitySamplingReason;

// Add for debris proximity tier diagnostics:
public DebrisProximitySamplingCadence debrisProximityCadence = DebrisProximitySamplingCadence.None;
public double debrisProximityDistanceMeters = double.NaN;
public string debrisProximityReason;
```

**BG recorder sample-time evaluation:** in `OnBackgroundPhysicsFrame` (or its per-sample helper, around `BackgroundRecorder.cs:1900-2050`), before invoking the cadence resolver, compute distance to parent if this is a debris recording:

```csharp
DebrisProximitySamplingCadence debrisCadence = DebrisProximitySamplingCadence.None;
double parentDistance = double.NaN;
if (treeRec.IsDebris && !string.IsNullOrEmpty(treeRec.DebrisParentRecordingId))
{
    parentDistance = ResolveDebrisParentDistanceMeters(treeRec, bgVessel, ut);
    debrisCadence = ResolveDebrisProximitySamplingCadence(
        treeRec, parentDistance, out string debrisReason);
    state.debrisProximityCadence = debrisCadence;
    state.debrisProximityDistanceMeters = parentDistance;
    state.debrisProximityReason = debrisReason;
}

// ... feeds the existing call:
effectiveMaxSampleInterval = FlightRecorder.ResolveEffectiveMaxSampleInterval(
    /* reFlyTreeCadence */ ReFlyTreeSamplingCadence.None,
    debrisCadence,
    highFidelityActive,
    configuredMax,
    configuredMin);
```

`ResolveDebrisParentDistanceMeters` is a new helper that mirrors the FG parent-distance resolution pattern: prefer the parent's live vessel pose when loaded; fall back to NaN (which makes `ResolveDebrisProximitySamplingCadence` return `None`, harmlessly).

**Removed code on the BG recorder side:**

- `BackgroundRecorder.UpdateBackgroundAnchorDetection` (`:4286-4303`): drop the `if (!string.IsNullOrEmpty(treeRec.DebrisParentRecordingId)) { ApplyDebrisAnchorContractToState(...); return; }` early-return entirely. The remaining body — `AnchorDetector.FindNearestRecordingAnchor` candidate search — continues to apply for non-debris. For debris, the new contract writes Absolute always, so anchor detection is irrelevant; the method's debris call should never fire. Add a defensive log at top: `if (treeRec.IsDebris && !string.IsNullOrEmpty(treeRec.DebrisParentRecordingId)) ParsekLog.Warn("BgRecorder", "UpdateBackgroundAnchorDetection unexpectedly entered for parent-anchored debris");`.
- `BackgroundRecorder.ApplyDebrisAnchorContractToState` (`:4600-4685`): delete entirely.
- `BackgroundRecorder.cs:3338-3460` debris seed in `InitializeLoadedState`: the Relative-seed branch and `ApplyBackgroundRelativeOffsetForAnchorPose` call become unreachable for debris. Simplify the seed code path to always seed Absolute for debris (existing helper `CreateAbsoluteTrajectoryPointFromVessel` already exists; use directly).

### 3.2 Renderer side

Debris renders through the standard absolute dispatch path used by solo-flight vessels:

- TrackSection is Absolute → engine calls `IGhostPositioner.InterpolateAndPosition` (`ParsekFlight.cs:16563-16634`) → `body.GetWorldSurfacePosition(lat, lon, alt)` → floating-origin correction → `Transform.position`.
- No anchor consulted.
- No tumble-parent gate.
- No coverage-retire on non-Relative section.
- No legacy v11/v12 shadow paths.

**Removed code on the renderer side:**

| File | Action |
|---|---|
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | Delete entire file |
| `Source/Parsek/LegacyDebrisShadowGate.cs` | Delete entire file |
| `Source/Parsek/IGhostPositioner.cs:78-80` | Remove `TryPositionFromRelativeAbsoluteShadow` from interface |
| `Source/Parsek/ParsekFlight.cs:16717-16810` | Remove the `TryPositionFromRelativeAbsoluteShadow` implementation |
| `Source/Parsek/ParsekFlight.cs:15117-15170` | Remove `ShouldEvaluateAnchorRotationReliability`, `ShouldEvaluateAnchorRotationReliabilityForTesting`, `TryEvaluateAnchorRotationReliability` |
| `Source/Parsek/GhostPlaybackEngine.cs:2796-3006` | Remove `TryRouteAnchorRotationUnreliable`, `TryRouteAnchorRotationToShadow` |
| `Source/Parsek/GhostPlaybackEngine.cs:1150-1216` | Remove the `anchorRotationRoute` branch in `RenderInRangeGhost` (the engine falls through to standard positioning without the route consultation) |
| `Source/Parsek/GhostPlaybackEngine.cs:3297-3339` | Remove the `anchorRotationRoute` consultation in `PositionLoopAtPlaybackUT` |
| `Source/Parsek/GhostPlaybackEngine.cs` | Remove `AnchorRotationUnreliableRoute` enum and all references |

Call sites that consume `DebrisRelativePlaybackPolicy` / `LegacyDebrisShadowGate`:

| File:line | Action |
|---|---|
| `GhostPlaybackEngine.cs:2715, 2717` | Remove the `ShouldRetireOutsideAuthoredRelativeCoverage` consultation |
| `GhostPlaybackEngine.cs:2760, 2819, 5404` | Remove `ShouldSkipRecordedRelativeResolverForAuthoredFrameGap` consultations and their authored-gap shadow routing |
| `GhostPlaybackEngine.cs:3133, 3160, 3215` | Same |
| `GhostPlaybackEngine.cs:5562, 5652, 5668` | `TryResolveInitialStructuralSeedBridgeEndUT` hides a v12-specific structural-seed bridge artifact. Under the new contract debris seeds are body-fixed and need no bridging. Remove the call sites; if no other consumer exists, remove the helper too. |
| `ParsekFlight.cs:17905, 18078, 20129, 22085` | Remove `ShouldRetireOutsideAuthoredRelativeCoverage` calls |
| `ParsekFlight.cs:18087, 20138, 22088` | Remove `ShouldSkipRecordedRelativeResolverForAuthoredFrameGap` calls |
| `ParsekFlight.cs:22121, 22180, 22274` | Remove the `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible` branches |

After this cleanup the debris render path is identical to the solo-flight render path. The only debris-specific check that remains in the engine is whatever consults `Recording.IsDebris` for non-position purposes (visibility filters in `GhostPlaybackEngine` zone policies, FX gating, ledger queries) — those are out of scope.

### 3.3 Visibility

Debris in Absolute renders independent of parent state. If the parent is destroyed or finalized, debris keeps rendering along its recorded body-fixed path until its own recording ends. No "bound to parent visibility" coupling exists in the new contract.

The existing `BackgroundRecorder.CheckDebrisTTL` (`:1390-1404`) still ends a debris recording when the parent vessel goes on-rails. This is preserved because the recorder needs *some* TTL to avoid open-ended debris recordings consuming storage forever. Triggers preserved: parent on-rails, parent destroyed, debris destroyed, scene exit, debris on-rails (existing rules).

### 3.4 Cascade separations (debris-of-debris)

If a debris piece itself sheds further debris (a cascade), the new debris's `DebrisParentRecordingId` is set to its **immediate parent**'s recording id, regardless of whether the immediate parent is itself a debris recording. The parent-distance computation in `ResolveDebrisParentDistanceMeters` reads from whatever recording the id points to — debris or not. Cascade depth does not affect rendering (the renderer reads only the debris's own Absolute samples).

`MaxRecordingGeneration` at the BG recorder caps cascade depth (default 1, per the existing rule in `recording-and-ghost-policies-refactor-plan.md` Decision §11). Beyond the cap, further debris-of-debris is silently dropped.

## 4. Cadence-tier transitions

The tier resolver is evaluated per sample. A debris recording moves between Full → Half → None as it drifts:

| Distance | Effective `maxSampleInterval` |
|---|---|
| `≤ 250 m` | `configuredMin` |
| `(250, 500] m` | `min(configuredMax, 2 × configuredMin)` |
| `> 500 m` | falls through to existing adaptive logic (`ResolveDebrisAwareMaxSampleInterval` at `BackgroundRecorder.cs:5269`, unchanged) |

No hysteresis on the tier boundaries. Sample-interval changes are continuous — each sample picks an interval, no on-disk flapping artifact at boundary crossing. At orbital relative velocities (1 km/s = 250 m crossed in 0.25 s) the tier-switch latency is bounded by one full-fidelity sample period (50–100 ms on Medium density). No visible artifact.

## 5. `HighFidelityProximityRangeMeters` raise

`FlightRecorder.cs:935`:

```csharp
internal const double HighFidelityProximityRangeMeters = 250.0;  // was 200.0
```

This is the parent-side density rule. Used at:
- `FlightRecorder.cs:711-716` (`IsHighFidelityProximityActive`)
- `FlightRecorder.cs:7196-7204` (`SelectNearestHighFidelityProximityMeters` loop)
- Log lines at `:907, :926`

All call sites consume the constant directly; the raise propagates without further edits.

**When the constant is still used:**

| Scenario | Fires? |
|---|---|
| Focused vessel separates → child (debris or controllable) stays within 250 m | Yes (parent keeps dense samples) |
| Two vessels approach with no split history | No (no `RegisterHighFidelityProximityVessel` call site) |
| Player switches focus off parent after separation | No (mechanism is per-FlightRecorder; parent becomes BG) |

Symmetric with the new debris-side full-fidelity tier (0–250 m). Both sides of the proximity pair sample at `configuredMin` simultaneously when parent stays focused.

The deferred BG-parent extension (a BG-side equivalent of `RegisterHighFidelityProximityVessel`) is documented but not in scope; see §9.

## 6. Format version

Bump the recording format constant in `Source/Parsek/RecordingStore.cs`:

```csharp
// Add to the version-constant block at :105-114:
internal const int DebrisAbsolutePrimaryFormatVersion = 13;
// Update CurrentRecordingFormatVersion to the new value:
internal const int CurrentRecordingFormatVersion = DebrisAbsolutePrimaryFormatVersion;
```

Binary stamp follows the same convention:

```csharp
internal const int DebrisAbsolutePrimaryBinaryVersion = 13;
```

No schema change on disk — the layout is the same; debris recordings simply never produce Relative sections under v13. The version bump is the diagnostic marker: a v13 recording is guaranteed to have only Absolute debris sections; v12 and earlier cannot be loaded.

Codec changes:

- `Source/Parsek/RecordingTreeRecordCodec.cs`: bump version stamp at write; reject v12 and earlier at load (existing `versionMismatch` / `unsupportedFormat` paths cover the rejection).
- `Source/Parsek/RecordingPrecBinaryCodec.cs`: same.

Update the format-version table in `.claude/CLAUDE.md` to add the new constant and mark v12 / v11 as no-longer-loadable.

## 7. Concrete touchpoints

### 7.1 Files modified

| File | What |
|---|---|
| `Source/Parsek/BackgroundRecorder.cs` | Remove `ApplyDebrisAnchorContractToState` (`:4600-4685`); remove debris early-return in `UpdateBackgroundAnchorDetection` (`:4286-4303`); simplify debris seed in `InitializeLoadedState` (`:3338-3460`) to always-Absolute; add `DebrisProximitySamplingCadence` resolver and state fields; wire into the per-sample cadence resolution; extend `ResolveDebrisAwareMaxSampleInterval` (`:5269`) to compose with the new tier |
| `Source/Parsek/FlightRecorder.cs` | Raise `HighFidelityProximityRangeMeters` from `200.0` → `250.0` (`:935`); add the new `DebrisProximitySamplingCadence` parameter to `ResolveEffectiveMaxSampleInterval` and `ResolveEffectiveMinSampleInterval` overloads (`:821-876`) |
| `Source/Parsek/RecordingStore.cs` | Bump `CurrentRecordingFormatVersion` to 13; add `DebrisAbsolutePrimaryFormatVersion`, `DebrisAbsolutePrimaryBinaryVersion` constants |
| `Source/Parsek/RecordingTreeRecordCodec.cs` | Write v13 version stamp; reject < v13 at read |
| `Source/Parsek/RecordingPrecBinaryCodec.cs` | Same |

### 7.2 Files deleted

| File | Reason |
|---|---|
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | All renderer call sites cleaned up; no debris-specific coverage gating under the new contract |
| `Source/Parsek/LegacyDebrisShadowGate.cs` | v11 retroactive fix; legacy recordings are no longer loadable |
| `Source/Parsek/DebrisRelativeRecorderPolicy.cs` | Encoded v12 all-Relative tail normalization; no longer relevant |
| Their xUnit test files (if present) | Owned tests for the deleted files |

### 7.3 Files modified — cleanup only

| File | Action |
|---|---|
| `Source/Parsek/IGhostPositioner.cs` | Remove `TryPositionFromRelativeAbsoluteShadow` method signature (`:78-80`) |
| `Source/Parsek/ParsekFlight.cs` | Remove `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow` impl (`:16717-16810`); remove `ShouldEvaluateAnchorRotationReliability` family (`:15117-15170`) |
| `Source/Parsek/GhostPlaybackEngine.cs` | Remove `TryRouteAnchorRotationUnreliable`, `TryRouteAnchorRotationToShadow`, `AnchorRotationUnreliableRoute` enum, and route-consulting branches in `RenderInRangeGhost` and `PositionLoopAtPlaybackUT` |
| `Source/Parsek/RecordingFinalizationCacheApplier.cs` | Remove v12 parent-anchored-debris branch in `TryGetLastAuthoredUT` (`:119-180`); fall through to standard last-frame UT resolution |
| `Source/Parsek/Recording.cs` | `ApplyDebrisAnchorContract` keeps signature (still sets `DebrisParentRecordingId`); no schema change |

### 7.4 No-op / verified-unchanged

| File | Verification |
|---|---|
| `Source/Parsek/AnchorDetector.cs` | `IsRecordingAnchorEligible` debris exclusion at `:240` stays |
| `Source/Parsek/RelativeAnchorResolver.cs` | Unchanged — debris doesn't enter resolver chain |
| `Source/Parsek/ParsekConfig.cs:30-65` | Unchanged — debris doesn't use `RelativeFrame.EntryMeters` / `ExitMeters` |

### 7.5 Grep audits before merge

```bash
# Verify no remaining debris-shadow code paths:
grep -rn "absoluteFrames" Source/Parsek/  # should be non-debris call sites only
grep -rn "DebrisParentRecordingId" Source/Parsek/  # only recorder + diagnostics
grep -rn "anchorRotationRoute\|anchorRotationShadow\|AnchorRotationUnreliable" Source/Parsek/  # zero results
grep -rn "IsLegacyDebrisShadowEligible\|TryPositionFromRelativeAbsoluteShadow" Source/Parsek/  # zero results
grep -rn "DebrisRelativePlaybackPolicy\|DebrisRelativeRecorderPolicy\|LegacyDebrisShadowGate" Source/Parsek/  # zero results

# Verify Re-Fly isn't touched:
scripts/grep-audit-non-loop-live-pid.ps1
scripts/grep-audit-ers-els.ps1
```

## 8. Tests

### 8.1 Recorder unit tests (`Source/Parsek.Tests/`)

| Test | Setup | Assertion |
|---|---|---|
| `Debris_AlwaysOpensAbsoluteSection` | Debris created at any distance from parent | First TrackSection is `Absolute`, `anchorRecordingId` is null |
| `Debris_NeverEmitsRelativeSection` | Debris drifts 50 → 5000 m | Every TrackSection is `Absolute`; no `absoluteFrames` populated on any section |
| `Debris_CadenceTier_0to250m_UsesFullFidelity` | Debris at 100 m | Effective `maxSampleInterval` == `configuredMin` |
| `Debris_CadenceTier_250to500m_UsesHalfFidelity` | Debris at 350 m | Effective `maxSampleInterval` == `min(configuredMax, 2 × configuredMin)` |
| `Debris_CadenceTier_Beyond500m_NormalAdaptive` | Debris at 1500 m | Effective `maxSampleInterval` falls through to `ResolveDebrisAwareMaxSampleInterval` |
| `Debris_CadenceTier_ProportionalToDensitySetting` | Same debris at 100 m, density = Low vs High | Sample-count ratio matches density-setting ratio |
| `Debris_CadenceTier_ReFlyTierWinsOverDebrisTier` | Debris that's also in an active Re-Fly tree; both tiers would apply | `ReFlyTreeSamplingCadence` takes precedence |
| `Debris_DebrisParentRecordingId_StillSetOnSplit` | Standard separation event | `child.DebrisParentRecordingId == parent.RecordingId` |
| `Debris_ParentDestroyed_StillRecordsAbsolute` | Parent destroyed mid-recording | Debris recording continues; subsequent samples are Absolute |
| `Debris_OnRails_NoTrackSection` | Debris transitions to packed | `BackgroundOnRailsState`, no TrackSection (existing rule, regression test) |
| `Debris_CascadeSeparation_ParentIdIsImmediateParent` | Debris A → sheds debris B | `B.DebrisParentRecordingId == A.RecordingId` |
| `Debris_CascadeSeparation_DepthCapped` | Debris cascade depth > `MaxRecordingGeneration` | Further generations silently dropped (existing rule, regression test) |

### 8.2 Renderer unit tests (`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`)

| Test | Setup | Assertion |
|---|---|---|
| `Debris_AbsoluteSection_StandardDispatch` | Debris in Absolute (any distance) | `InterpolateAndPosition` invoked; no anchor lookup; no tumble-gate evaluation |
| `Debris_ParentTumbling_StillRenders` | Debris with parent rotating at 200 °/s | Standard absolute path runs; debris remains visible (no Hidden route) |
| `Debris_ParentDestroyed_StillRenders` | Debris with parent recording absent | Renders normally; no visibility coupling |
| `Debris_LongRange_NoSyncedSwing` | Multiple debris pieces all from same parent, varying distances | Per-frame position deltas independent of any shared rotation channel |
| `V12Recording_LoadFails` | Synthetic v12 .prec | Codec returns `unsupportedFormat`; no partial-load |

### 8.3 Format-version unit tests (`Source/Parsek.Tests/`)

| Test | Setup | Assertion |
|---|---|---|
| `V12Recording_LoadFails` | Synthetic v12 .prec file | Codec returns `unsupportedFormat`; no partial-load |
| `V11Recording_LoadFails` | Synthetic v11 .prec file | Same |
| `V13Recording_RoundTrip` | Write v13 recording, reload | Identical content; `RecordingFormatVersion == 13` |

### 8.4 In-game tests (`Source/Parsek/InGameTests/RuntimeTests.cs`)

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_SeparationEvent_CadenceTransitions` | Booster separation; fly out past 250 m, 500 m | `[Recorder]` logs show tier transitions Full → Half → None |
| `Debris_TumblingParent_NoArtifact` | Parent at 200 °/s, debris within 200 m | Debris ghost remains smooth; no synced-rotation swing |
| `Debris_PhysicsWarp_TierBoundary_NoArtifact` | Debris crossing 500 m during 2× physics warp | Cadence transitions cleanly |
| `Debris_AbsoluteSection_ParentSuperseded_ContinuesRendering` | Debris recording; parent re-flown (superseded) | Debris ghost renders unchanged (no parent dependency) |
| `Debris_CadenceProportionalToDensity` | Same scenario at Low vs High density | Sample count in 0–250 m window scales with density setting |

### 8.5 Test fixture

`Source/Parsek.Tests/Generators/DebrisAbsolutePrimaryRecordingFixture.cs`: builds a synthetic debris recording with:
- Multiple TrackSections, all Absolute, no `absoluteFrames`
- Sample timestamps reflecting the three cadence tiers (dense in 0–250 m, sparser in 250–500 m, normal beyond)
- `DebrisParentRecordingId` set
- Optional parent-tumble window for the synced-swing regression test (§8.2 row 4)

Used by the regression tests and as the canonical example for the codec round-trip test.

## 9. Out of scope (follow-ups)

- **BG-parent hi-fi sampling**: extending `RegisterHighFidelityProximityVessel` to BG-recorded parents of in-proximity debris. Not strictly needed because the debris's own cadence tier governs its sample density, and the renderer reads only debris's own samples.
- **Visibility decoupling product validation**: the new contract removes "debris bound to parent visibility" silently. If playtest feedback prefers the v12 behaviour ("when the rocket explodes, all its debris ghosts vanish"), a follow-up PR can re-introduce a debris-visibility check tied to parent existence.
- **`DebrisParentRecordingId` UI surfacing**: filter debris by parent in the recordings table, group debris-of-rocket together. Field is set; UI use is separate work.
- **Recorder denser sampling for fast-debris-rotation**: independent concern affecting attitude reconstruction, not position.

## 10. Rollout

Land as a single PR or split into commits if review prefers:

1. **Format version bump + codec gate** (commit 1). Old recordings stop loading; new recordings produce v13 stamp. Run codec round-trip tests.
2. **Renderer cleanup** (commit 2). Delete `DebrisRelativePlaybackPolicy`, `LegacyDebrisShadowGate`, `TryPositionFromRelativeAbsoluteShadow`, `TryRouteAnchorRotationUnreliable`, `ShouldEvaluateAnchorRotationReliability` family. Remove call sites. Compile + run xUnit; debris-related test failures are expected and get rewritten under §8.2.
3. **Recorder change** (commit 3). Remove `ApplyDebrisAnchorContractToState`; remove debris early-return in `UpdateBackgroundAnchorDetection`; simplify debris seed in `InitializeLoadedState`; add `DebrisProximitySamplingCadence` resolver, state, and wiring; raise `HighFidelityProximityRangeMeters` to 250 m; delete `DebrisRelativeRecorderPolicy`.
4. **Tests** (commit 4): new test fixture plus unit tests from §8.1 / §8.2 / §8.3.
5. **Docs** (commit 5): CHANGELOG entry; update `.claude/CLAUDE.md` format-version table and the debris-shadow notes; close the relevant entries in `docs/dev/todo-and-known-bugs.md`.

Pre-merge:
- `dotnet test` all green
- Re-Fly in-game tests pass (scope-creep gate)
- `scripts/grep-audit-*.ps1` clean
- §7.5 grep audits return zero hits for deleted symbols
- Playtest: separation event, cadence transitions, tumbling parent, long-range drift, density-setting proportionality

## 11. Plan evolution

This plan went through four drafts on the branch:

- `4b9a9bd` — first draft: proximity-gated at 2300/2500 m, revert PR #803, standard `RelativeAnchorResolver` chain inside Relative.
- `257ff31` — second draft (post first review): keep PR #803 inside the bounded Relative window; surgical renderer guards for the new Absolute debris sections.
- `17cb5db` — third draft (post user feedback): absolute-primary always; tiered cadence (0–250 / 250–500 / 500+); experimental Relative shadow inside 0–250 m for playtest evaluation.
- **current** — fourth draft (post Opus review + version-reset directive): drop the experimental Relative layer entirely; debris is Absolute-only; format version bumps to v13; all v12 always-Relative debris machinery removed; legacy v12/v11 recordings rejected at load.

The current plan is the simplest contract that satisfies the design goals (no synced-rotation pathology, proximity-tier-driven render smoothness, no parent-cadence coupling for debris) without the overhead of preserving a layer kept only "in case we want to evaluate it later."
