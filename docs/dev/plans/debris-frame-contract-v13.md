# Debris frame contract — v13

**Status:** clean rewrite (replaces all prior drafts), plus shared-module extraction in §3.6.
**Scope:** debris recording and rendering. No changes to non-debris recorder/renderer dispatch, Re-Fly merge/supersede flow, or the loop-anchor live-PID carve-out for non-debris. **One Re-Fly touch** (behaviour-preserving): the Re-Fly cadence resolver is refactored to use the shared `ProximitySamplingCadence` module — same thresholds, same tier semantics, same observable behaviour, just lifted into a reusable module so debris can use it too. See §3.6.
**Compatibility:** none. v11 / v12 recordings are rejected at load.

---

## 1. The design in one paragraph

Every debris sample writes **body-fixed (Absolute) world coordinates as the primary surface, always, at every distance**. When the debris is within **500 m** of its parent vessel, the recorder *additionally* writes **anchor-local-to-parent offsets** as a secondary surface on the same sample. The renderer reads body-fixed coordinates by default, and reads anchor-local through the recording chain when the chain ends in a loop-anchored ancestor (so that looped recordings — e.g. a tanker approach to a station, with debris jettisoned mid-approach — replay correctly against the station's current live orbital pose at each loop iteration). Sampling cadence scales with distance to parent, mirroring Re-Fly's tiered cadence: **full fidelity** inside 250 m, **half fidelity** 250–500 m (still inside the Relative window), **normal adaptive** beyond 500 m.

The relative-to-parent surface is **not experimental** — it has a concrete, mandatory consumer (loop scenarios) plus a sub-metre geometric-accuracy benefit at close range. Storage cost is bounded (only inside the 500 m bubble, with half-rate sampling in the outer 250 m), schema cost is zero (uses existing v7 Relative section layout), and rendering reads through the existing chain resolver path that already serves non-debris.

## 2. What does NOT change

- `Source/Parsek/AnchorDetector.cs` — `ShouldUseRelativeFrame`, `RelativeFrameRangeLimit`. The `candidateRecording.IsDebris → false` exclusion at `:240` stays. Update the comment at `:230-239` to reflect the new debris contract.
- `Source/Parsek/RelativeAnchorResolver.cs` — keeps the recorded-anchor DAG resolver. Two surgical changes only, both targeted at the loop-anchored-ancestor case for debris: see §3.4.
- All Re-Fly machinery: `RewindInvoker`, `SupersedeCommit`, `MergeJournalOrchestrator`, `ReFlySessionMarker`, the merge journal, ERS/ELS, `RecordingSupersedeRelation`.
- `Recording.LoopAnchorVesselId` — loop-anchor live-PID carve-out unchanged for non-debris. **Debris recordings always have `LoopAnchorVesselId == 0u`** (the v12 invariant is preserved under v13). Loop semantics for debris come from chaining through a loop-anchored ancestor's recording, not from inheriting the field. See §3.4.
- Non-debris recorder frame decisions and the v7 `absoluteFrames` field on `TrackSection`. Non-debris Relative sections continue to write the shadow per the existing v7 contract.
- Re-Fly tree sampling tier *thresholds* (`ReFlyTreeFullFidelityProximityRangeMeters`, `ReFlyTreeHalfFidelityProximityRangeMeters`) for non-debris. They stay at `250.0` / `500.0` (`FlightRecorder.cs:936-937`). The *resolver* that consumes them is refactored to delegate to the shared module per §3.6 — behaviour preserved.
- `IPlaybackTrajectory.DebrisParentRecordingId` interface property — still exposed for identification / ledger / map UI / diagnostics.
- `SessionMerger.cs` `DebrisParentRecordingId` and `absoluteFrames` handling — operates per-section, correct under the new contract.

If a change touches a file outside the debris-specific list in §6, that's a scope violation.

## 3. The contract

### 3.1 Recorder

**Frame, per sample, decided by distance to parent vessel:**

| Distance to parent | Section type | `frames` contents | `absoluteFrames` contents |
|---|---|---|---|
| `≤ 500 m` | `Relative` (anchored on parent recording) | anchor-local-to-parent offsets (the secondary surface) | body-fixed world coordinates (the primary surface) |
| `> 500 m` | `Absolute` | body-fixed world coordinates (the primary surface) | null |

Hysteresis on the frame-switching boundary: enter Relative at `≤ 500 m`, exit Relative at `> 550 m`. The 50 m hysteresis prevents flapping; playtest-tunable.

Body-fixed coordinates are written on every sample regardless of section type — they live in `frames` when the section is Absolute, in `absoluteFrames` when the section is Relative. So the renderer always has a body-fixed surface to read.

**Anchor identity for Relative sections:** the anchor is unconditionally `treeRec.DebrisParentRecordingId` (the parent's `RecordingId`). The `AnchorDetector.FindNearestRecordingAnchor` candidate-list / nearest-search is bypassed for debris — the parent is the anchor, never a sibling debris piece or nearby unrelated vessel.

**Distance source:** parent's live world position when its vessel is loaded (covers both packed and unpacked in-bubble parents — `parent.loaded` returns true for either). When the parent vessel is unloaded (out of bubble), distance is NaN and the recorder treats it as out-of-proximity (writes Absolute). The existing `CheckDebrisTTL` typically ends the debris recording within one tick of parent unload anyway.

**Cadence tiers (proximity-driven):** uses the shared `ProximitySamplingCadence` module described in §3.6 (single tier enum across recorders). Debris supplies its own thresholds:

```csharp
internal const double DebrisFullFidelityProximityRangeMeters = 250.0;
internal const double DebrisHalfFidelityProximityRangeMeters = 500.0;  // also: Relative section entry boundary
internal const double DebrisRelativeSectionExitMeters = 550.0;         // hysteresis exit for Relative section
```

The debris-side guards (`IsDebris && !string.IsNullOrEmpty(DebrisParentRecordingId)`) are applied before calling the shared `ProximitySamplingCadence.Resolve`. See §3.6 for the resolver shape and the wiring pseudocode.

| Distance | Section | Effective `maxSampleInterval` |
|---|---|---|
| `≤ 250 m` | Relative | `configuredMin` (Full) |
| `(250, 500] m` | Relative | `min(configuredMax, 2 × configuredMin)` (Half) |
| `(500, 550] m` (frame hysteresis exit) | Relative | falls through (None) — see note below |
| `> 550 m` | Absolute | falls through to `ResolveDebrisAwareMaxSampleInterval` (None / adaptive) |

The cadence Full→Half boundary at 250 m sits **inside** the Relative section (which extends to 500 m). The cadence Half→None boundary at 500 m **coincides with** the Relative section entry boundary. The frame hysteresis-exit zone (500–550 m) is a 50 m transit where the section is still Relative but the cadence has already dropped to None — both `frames` and `absoluteFrames` are still being written, just at the existing adaptive rate.

Tier boundaries have no hysteresis — sample-interval changes are continuous (per-sample selection, no on-disk artifact at boundary crossings).

Cadence composes with the existing `ResolveEffectiveMaxSampleInterval` helper chain. When the debris-proximity cadence is non-None, it **supersedes** the legacy `ResolveDebrisAwareMaxSampleInterval` cap (otherwise the legacy 0.5 s mid-range cap would clobber Half-tier intervals at Low density). When None (> 500 m), fall through to the existing adaptive path.

After the shared-module refactor (§3.6), the cadence-aware overload of `ResolveEffectiveMaxSampleInterval` takes two `ProximitySamplingTier` parameters — one for the Re-Fly tier, one for the debris tier — and resolves the sample interval through `ProximitySamplingCadence.ResolveSampleInterval`:

```csharp
internal static float ResolveEffectiveMaxSampleInterval(
    ProximitySamplingTier reFlyTreeTier,
    ProximitySamplingTier debrisTier,
    bool highFidelityActive,
    float configuredMax,
    float configuredMin)
{
    if (reFlyTreeTier != ProximitySamplingTier.None)
        return ProximitySamplingCadence.ResolveSampleInterval(
            reFlyTreeTier, configuredMin, configuredMax);
    if (debrisTier != ProximitySamplingTier.None)
        return ProximitySamplingCadence.ResolveSampleInterval(
            debrisTier, configuredMin, configuredMax);
    return ResolveEffectiveMaxSampleInterval(
        highFidelityActive, configuredMax, configuredMin);
}
```

The Re-Fly tier wins over the debris tier (Re-Fly is more specific). Captured in the 9-cell orthogonality matrix test in §7.1.

Re-Fly cadence has priority over debris cadence because Re-Fly is more specific (the player is actively re-flying); in practice the two are typically disjoint since debris is not the Re-Fly active recording. Tested by the orthogonality matrix in §7.

**BG recorder state extension** in `BackgroundVesselState` (`BackgroundRecorder.cs:192-200`):

```csharp
public ProximitySamplingTier debrisProximityTier = ProximitySamplingTier.None;
public double debrisProximityDistanceMeters = double.NaN;
public string debrisProximityReason;
```

**Sample-time wiring** in `OnBackgroundPhysicsFrame` (around `:1958-1967`): compute parent distance, resolve cadence tier via the shared `ProximitySamplingCadence.Resolve` (see §3.6), feed into the extended `ResolveEffectiveMaxSampleInterval`.

**Recorder-side `HighFidelityProximityRangeMeters` raise:** `FlightRecorder.cs:935` from `200.0` → `250.0`, aligning the parent-side density rule (parent's recorder keeps dense samples while split children are within range) with the debris-side full-fidelity tier. Both halves of the proximity pair sample at `configuredMin` simultaneously.

### 3.2 Renderer

Render dispatch by section type:

**Absolute section (`> 500 m`):** standard `IGhostPositioner.InterpolateAndPosition` (`ParsekFlight.cs:16563-16634`) — body-fixed lookup via `body.GetWorldSurfacePosition`. No anchor consulted. Same path solo-flight vessels use.

**Relative section (`≤ 500 m`):** standard `RelativeAnchorResolver` chain. The chain walks:
- Debris's anchor is its parent recording. Read parent's pose at UT t.
- If parent has its own Relative section at UT t → recurse to grandparent.
- If parent is loop-anchored (`LoopAnchorVesselId != 0u`) → resolve parent's pose through the live-anchor path: `liveAnchor.pose(now) × parent.recordedOffset`.
- If parent has Absolute section at UT t → use parent's recorded body-fixed position directly.
- Compose: `debris.world = parent.resolvedPose × debris.recordedOffsetToParent`.

The chain composes correctly for both loop and non-loop ancestors. No special-case dispatch on the debris side — the loop semantics live in the loop-anchored ancestor at the chain's root and propagate automatically.

**Fallback when chain fails to resolve** (parent recording deleted, parent recording finalized before debris UT): renderer reads `absoluteFrames` body-fixed shadow from the Relative section. This is the existing v7 fallback infrastructure, kept as a safety net.

### 3.3 Visibility coupling

Debris in Absolute sections (`> 500 m`) renders independent of parent state. If the parent is destroyed or its recording is deleted, debris continues to render along its body-fixed path until its own recording ends.

Debris in Relative sections (`≤ 500 m`) depends on the parent's recorded pose at playback UT. If the parent is unresolvable, the renderer falls back to the body-fixed shadow (`absoluteFrames`) inside the Relative section. The debris remains visible; it just renders at its recording-time world position rather than a chain-resolved position.

The existing `BackgroundRecorder.CheckDebrisTTL` (`:1300-1413`) live-recording-end conditions are preserved unchanged:
1. Parent recording missing from tree (`:1352-1361`).
2. Parent recording closed / superseded (`:1375-1388`).
3. Parent vessel on-rails / destroyed / unloaded (`:1390-1404`).

### 3.4 Loop-anchored ancestor composition (the canonical scenario)

A tanker T is loop-anchored to a station S (`T.LoopAnchorVesselId = S.persistentId`). T sheds a fairing F mid-approach. F is parent-anchored to T (`F.DebrisParentRecordingId = T.RecordingId`); F has no `LoopAnchorVesselId` of its own.

**Recording at separation event:**
- F is close to T (just separated) → first sample is within 500 m → F opens a Relative section anchored on T's recording.
- F's `frames` capture F's anchor-local-to-T offsets at each sample.
- F's `absoluteFrames` capture F's body-fixed world coordinates at each sample.

**Recording as F drifts away from T:**
- Inside 250 m of T: Full cadence (configuredMin interval).
- 250–500 m: Half cadence (2 × configuredMin), still in the Relative section. Anchor-local offsets continue to record (the chain still composes for loop scenarios out to 500 m).
- When F crosses 550 m (hysteresis exit), the recorder closes the Relative section and opens an Absolute section. F's `frames` (in the new Absolute section) capture F's body-fixed world coordinates.

**Playback at loop iteration N:**
- The station S is at live pose `Y_N` at the current loop UT.
- T renders via T's loop-anchored Relative path: `T_pose_N = Y_N × T.recordedOffsetToS`.
- For F's Relative section samples (UT t within the 0–500 m proximity window): the chain resolver walks `F → T`. T's pose at UT t comes from T's loop-anchored resolution, which yields `Y_N × T.recordedOffsetToS_at_t`. F's pose = `T_pose_N_at_t × F.recordedOffsetToT_at_t`. F follows T which follows S. **Correct across loop iterations.** Chain math is dense in 0–250 m (Full cadence) and half-rate in 250–500 m (Half cadence), but accurate at both rates.
- For F's Absolute section samples (UT t past the 550 m hysteresis exit): F renders at its body-fixed world coordinate. At loop iteration N with a long loop period, this can be far from station S; but F at that UT was *already* far from T at recording time, and visual coherence with the loop anchor is weak by that distance anyway. Accepted limitation.

**Resolver gate relaxation.** Currently `RelativeAnchorResolver.cs:301-310` and `:450-454` reject loop-anchored recordings as anchor targets with `loop-anchor-out-of-scope`, to prevent live-PID leakage into non-loop ghost math. Under v13 we **relax this for debris**: allow Relative anchoring on a loop-anchored recording when the anchored recording is `IsDebris && DebrisParentRecordingId == loopAnchoredRecording.RecordingId`. Non-debris anchoring on loop-anchored recordings stays rejected.

```csharp
// In RelativeAnchorResolver, at the existing rejection sites:
if (anchorRecording.LoopAnchorVesselId != 0u)
{
    // Existing rejection for non-debris stays.
    // New carve-out: debris parent-anchored on this loop-anchored
    // recording is allowed — the chain composes correctly through the
    // loop-anchored ancestor's live-anchor path.
    if (!(anchoredRecording.IsDebris
          && string.Equals(anchoredRecording.DebrisParentRecordingId,
                           anchorRecording.RecordingId,
                           StringComparison.Ordinal)))
    {
        return /* loop-anchor-out-of-scope rejection */;
    }
}
```

**Cascade:** T → F (debris) → G (debris-of-debris). G is parent-anchored to F. The chain G → F → T → live-anchor-of-T (S) composes naturally at each level. G doesn't need any special handling; whatever loop semantics exist at T propagate transparently through F to G.

**No `LoopAnchorVesselId` inheritance on debris.** The earlier draft proposed inheriting the field from parent at split; this redraft drops that mechanism. Loop semantics flow through the chain, not through field inheritance. Simpler, fewer state propagations, naturally handles cascades.

### 3.5 Cascade separations (non-loop case)

Standard chain: when debris piece A sheds debris piece B, B's `DebrisParentRecordingId` is set to A's `RecordingId` (the immediate parent, regardless of whether A is itself debris). At rendering, the chain B → A → A's parent → ... composes through whatever the chain's root looks like.

`MaxRecordingGeneration` at `BackgroundRecorder.cs:1629` caps cascade depth (existing rule). Beyond the cap, further debris-of-debris is silently dropped.

### 3.6 Shared proximity-cadence module

The Full/Half/None cadence tier resolver in §3.1 is the same shape the Re-Fly recorder already uses today (`FlightRecorder.ReFlyTreeSamplingCadence` at `:29`, `ResolveActiveReFlyTreeSamplingCadence` at `:752-819`, `ResolveReFlyTreeCadenceSampleInterval` at `:867-876`). Rather than duplicate this on the debris side and on every future recorder that needs proximity-tiered cadence, **extract it as a standalone module**.

**New file:** `Source/Parsek/ProximitySamplingCadence.cs`

```csharp
namespace Parsek
{
    internal enum ProximitySamplingTier
    {
        None = 0,  // Beyond half-fidelity range: caller falls through to its own adaptive logic.
        Half = 1,  // Inside half-fidelity range: 2 × min sample interval.
        Full = 2,  // Inside full-fidelity range: min sample interval.
    }

    internal static class ProximitySamplingCadence
    {
        /// <summary>
        /// Resolve the cadence tier for a given distance against caller-supplied
        /// thresholds. Pure function. Reusable by any recorder that wants
        /// proximity-driven cadence (Re-Fly, debris, future co-bubble recorders).
        /// </summary>
        internal static ProximitySamplingTier Resolve(
            double distanceMeters,
            double fullFidelityMaxMeters,
            double halfFidelityMaxMeters,
            out string reason)
        {
            reason = null;
            if (!IsFinite(distanceMeters))
            {
                reason = "distance-missing";
                return ProximitySamplingTier.None;
            }
            if (distanceMeters <= fullFidelityMaxMeters)
            {
                reason = "full";
                return ProximitySamplingTier.Full;
            }
            if (distanceMeters <= halfFidelityMaxMeters)
            {
                reason = "half";
                return ProximitySamplingTier.Half;
            }
            reason = "out-of-range";
            return ProximitySamplingTier.None;
        }

        /// <summary>
        /// Compose a tier with caller's configured min/max sample intervals.
        /// Full → configuredMin, Half → 2 × configuredMin, None → configuredMax.
        /// All values clamped to [0, configuredMax].
        /// </summary>
        internal static float ResolveSampleInterval(
            ProximitySamplingTier tier,
            float configuredMin,
            float configuredMax)
        {
            if (tier == ProximitySamplingTier.None)
                return configuredMax;
            float interval = Math.Max(0f, configuredMin);
            if (tier == ProximitySamplingTier.Half)
                interval *= 2f;
            return Math.Min(configuredMax, interval);
        }

        private static bool IsFinite(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
```

**Callers:**

- **Re-Fly** (`FlightRecorder.ResolveActiveReFlyTreeSamplingCadence` at `:752-819`): keep the Re-Fly-specific guards (marker null/empty checks, session-id check, tree-id check, ut-finite check), then delegate the tier resolution to `ProximitySamplingCadence.Resolve(proximityDistanceMeters, ReFlyTreeFullFidelityProximityRangeMeters, ReFlyTreeHalfFidelityProximityRangeMeters, out reason)`. The `ResolveReFlyTreeCadenceSampleInterval` helper at `:867-876` becomes a one-line forward to `ProximitySamplingCadence.ResolveSampleInterval`.
- **Debris** (new helper per §3.1): apply debris-specific guards (`IsDebris && DebrisParentRecordingId != null`), then delegate to `ProximitySamplingCadence.Resolve(parentDistanceMeters, DebrisFullFidelityProximityRangeMeters, DebrisHalfFidelityProximityRangeMeters, out reason)`.
- **Future recorders** (e.g. a co-bubble formation-flying recorder, an approach-corridor recorder) reuse the same module with their own thresholds and their own guards.

**Behaviour-preserving refactor for Re-Fly.** Same thresholds (250 m full, 500 m half), same tier semantics (None / Half / Full), same composition with `ResolveEffectiveMaxSampleInterval`. The Re-Fly cadence resolver becomes a guard-only wrapper around the shared module; no test output should change. Add regression tests asserting equality between the old and new resolvers on a sample of inputs.

**Naming note.** The existing `ReFlyTreeSamplingCadence` enum (`FlightRecorder.cs:29`) is renamed to the shared `ProximitySamplingTier`. Mechanical search-and-replace across the codebase; estimated 20–40 references including method signatures (`ResolveEffectiveMaxSampleInterval` takes the enum), per-state fields, log lines, test setups. The diff is large but every change is name-only.

**What stays Re-Fly-specific:** the *guards* (marker, session, tree validation) and the *thresholds* (250 / 500 m for Re-Fly trees). The shared module only owns the pure tier resolution and sample-interval composition.

**What this enables for debris:** the cadence wiring in §3.1 becomes:

```csharp
// In BackgroundRecorder.OnBackgroundPhysicsFrame:
ProximitySamplingTier debrisCadence = ProximitySamplingTier.None;
if (treeRec.IsDebris && !string.IsNullOrEmpty(treeRec.DebrisParentRecordingId))
{
    double parentDistance = ResolveDebrisParentDistanceMeters(treeRec, bgVessel);
    debrisCadence = ProximitySamplingCadence.Resolve(
        parentDistance,
        DebrisFullFidelityProximityRangeMeters,
        DebrisHalfFidelityProximityRangeMeters,
        out string reason);
    state.debrisProximityCadence = debrisCadence;
    state.debrisProximityDistanceMeters = parentDistance;
    state.debrisProximityReason = reason;
}
```

`ResolveDebrisProximitySamplingCadence` from §3.1 collapses to the inline guard + shared call above. The custom resolver is no longer needed.

## 4. Cleanup

### 4.1 What stays

These pieces are load-bearing under v13 and **are not deleted**:

| File / symbol | Why it stays |
|---|---|
| `Source/Parsek/DebrisRelativePlaybackPolicy.cs` | Coverage gating still applies for Relative debris sections (0–500 m window). Update `ShouldRetireFromDiagnostic` (`:191-199`) to NOT fire on `non-relative-section` (otherwise it would retire debris in the new Absolute sections). |
| `Source/Parsek/DebrisRelativeRecorderPolicy.cs` | Tail normalization for Relative sections still relevant inside the 0–500 m window. Audit each method (six call sites in `BackgroundRecorder` at `:1486, :2198, :2431, :2605, :4269, :6010` plus three in `RecordingFinalizationCacheApplier` at `:119, :162, :169`) for the v12 all-Relative assumption — they should iterate sections by type and treat Absolute debris sections as renderable surfaces. Rename `ShouldNormalizeParentAnchoredDebris` → `ShouldNormalizeDebrisRelativeTail` to reflect the narrower scope. |
| `IGhostPositioner.TryPositionFromRelativeAbsoluteShadow` (interface + impl at `ParsekFlight.cs:16717-16810`) | Used as the body-fixed shadow fallback when the chain resolver fails inside a Relative debris section (§3.2). |
| `BackgroundRecorder.UpdateBackgroundAnchorDetection` debris early-return (`:4295-4303`) and `ApplyDebrisAnchorContractToState` (`:4600-4685`) | Repurposed: now fires only when distance to parent is `≤ 500 m` (with hysteresis exit at 550 m). The unconditional "always Relative" shortcut is gated on the proximity check from §3.1. The third call site at `:4727` (structural-event seam in `ApplyBackgroundRelativeOffset`) keeps firing inside the proximity window. |
| `Recording.ApplyDebrisAnchorContract` and `Recording.DebrisParentRecordingId` | Still set at split time. Still consumed for identification (tree hierarchy, ledger, future UI). |

### 4.2 What gets deleted

| File / symbol | Why it goes |
|---|---|
| `Source/Parsek/LegacyDebrisShadowGate.cs` (entire file) | v11 retroactive fix; v11 recordings are no longer loadable under v13. |
| `Source/Parsek/TumblingParentInterpolationGate.cs` (entire file) | Only consumer was `ShouldEvaluateAnchorRotationReliability`. With Relative debris bounded to 500 m and recorded at proximity-tier cadence (Full inside 250 m, Half 250–500 m), the synced-rotation failure mode is bounded but not eliminated for high parent rotation rates. Accepted trade-off per §9 — if playtest reveals visible artifacts in the 250–500 m + tumbling-parent regime, the gate can be reinstated as a renderer-time shadow fallback or as a recorder-side rate filter. |
| `Source/Parsek/GhostPlaybackEngine.cs` `TryRouteAnchorRotationUnreliable` (`:2796-2909`), `TryRouteAnchorRotationToShadow` (`:2928-3006`), `AnchorRotationUnreliableRoute` enum | Router for the PR #803 always-shadow path. No longer needed: the renderer dispatches directly on section type (§3.2), and the body-fixed shadow is consulted only as a chain-failure fallback at a single call site inside the Relative-section dispatch. |
| `Source/Parsek/ParsekFlight.cs` `ShouldEvaluateAnchorRotationReliability` (`:15131-15150`), `ShouldEvaluateAnchorRotationReliabilityForTesting` (`:15128-15129`), `TryEvaluateAnchorRotationReliability` (`:15152`+) | Tumble-gate predicate; orphaned by the router deletion. |
| `Source/Parsek/ParsekFlight.cs` `TryUseLegacyDebrisShadowFallback` (`:22857-22905`), `TryRetireParentAnchoredDebrisOnRecordedAnchorMiss` (`:22907-22933`) | Legacy-v11-specific helpers. |
| `Source/Parsek/ParsekFlight.cs` call sites at `:22121, :22180, :22274` | `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible` branches. |
| `Source/Parsek/ParsekFlight.cs:22947-22961` | Debris-only guard inside `TryUseRelativeAbsoluteShadowFallback`. The wrapper itself stays (used by non-debris Relative recovery); just the inner debris-specific check goes. |
| `Source/Parsek/GhostPlaybackState.cs` fields `anchorRotationShadowRoutedThisFrame` (`:93`) and `parentAnchoredDebrisCoverageRetired` (`:99`) + reset in `ResetGhostState` (`:152-153`) | Both fields fed the deleted tumble-gate FX-suppression flow. |
| `Source/Parsek/GhostPlaybackEngine.cs` 17 read/write sites of `anchorRotationShadowRoutedThisFrame` (`:1148, :1249, :1256, :1418, :1661, :1847, :1861, :2124, :2134, :2223, :2320, :2331, :2380, :5203, :5242`) and 6 sites of `parentAnchoredDebrisCoverageRetired` (`:2977, :2983, :3031, :3136, :3163, :3223`) | Same. |
| `Source/Parsek/GhostPlaybackEvents.cs:89, :102` (`shadowRouted` parameter on the rendered-frame event) | Same. |
| `Source/Parsek/ParsekFlight.cs:16701` (XML doc reference to `anchorRotationShadowRoutedThisFrame`) | Same. |
| `Source/Parsek/IGhostPositioner.cs:78-80` (`TryPositionFromRelativeAbsoluteShadow` interface signature) — KEEP for the §3.2 fallback | (See §4.1 above; this is in the "stays" list. Listed here only to flag the implementation-time confusion risk: the symbol is named after the deleted always-shadow path but its remaining role is the chain-failure fallback.) |
| `Source/Parsek/BackgroundRecorder.cs:1868-1878` (Re-Fly settle suppression for debris) and `ShouldSuppressParentDebrisForReFlySettle` (`:2046-2053`) | Suppression rationale ("Relative section against a suppressed parent would leave the resolver unable to walk back to a parent pose") is gone — under v13, the body-fixed shadow is on every Relative debris sample, so resolver failure during settle falls through to shadow gracefully. The suppression now introduces unwanted sample gaps with no benefit. |
| `RelativeAnchorResolver.cs` `AnchorRotationReliabilityDecision` plumbing (~12 method signatures at `:194, :269, :435, :668, :1045, :1398, :1534, :1898, :2028, :2126, :2138, :2342`) | The decision's only consumer was the deleted tumble gate. Trim the parameter from the signatures. |
| Test files: `LegacyDebrisShadowGateTests.cs`, `TumblingParentInterpolationGateTests.cs` | Own the deleted code. |
| Test file `DebrisParentAnchorContractTests.cs` | Asserts v12 always-Relative-for-lifetime contract; needs full rewrite for v13 (Relative inside 500 m, Absolute outside). |
| Test cases at `RuntimeTests.cs:4278, 4390-4391, 4524, 4660` | Asserted deleted-field behaviour (`anchorRotationShadowRoutedThisFrame`); rewrite or remove. |

## 5. Format version

Three coordinated changes — the plain version bump alone is not sufficient because the existing rejection mechanism is a hard-coded allowlist.

### 5.1 Constants

`Source/Parsek/RecordingStore.cs` (`:105-114`):

```csharp
internal const int DebrisFrameContractFormatVersion = 13;
internal const int CurrentRecordingFormatVersion = DebrisFrameContractFormatVersion;
```

`Source/Parsek/TrajectorySidecarBinary.cs`:

```csharp
internal const int DebrisFrameContractBinaryVersion = RecordingStore.DebrisFrameContractFormatVersion;
internal const int CurrentBinaryVersion = DebrisFrameContractBinaryVersion;  // replaces :72
```

### 5.2 Binary codec rejection

`TrajectorySidecarBinary.IsSupportedBinaryVersion` at `:403-416` is currently a hard-coded allowlist accepting `LegacyBinaryVersion` through `DebrisParentRecordingBinaryVersion`. Replace with a min-version check:

```csharp
private static bool IsSupportedBinaryVersion(int version)
{
    return version >= DebrisFrameContractBinaryVersion;
}
```

The version-selection ladder at `:170-189` becomes unconditional v13:

```csharp
int binaryVersion = DebrisFrameContractBinaryVersion;
```

The reader's per-version conditional branches can be simplified now that only v13 is loadable.

### 5.3 ConfigNode codec rejection

`RecordingTreeRecordCodec.LoadRecordingFrom` (`Source/Parsek/RecordingTreeRecordCodec.cs:329`) has no version-rejection path today. Add an early-exit immediately after the format-version is parsed (`:443-449`):

```csharp
if (rec.RecordingFormatVersion < RecordingStore.DebrisFrameContractFormatVersion)
{
    ParsekLog.Warn("Codec",
        $"LoadRecordingFrom: rejecting recording {rec.RecordingId} with " +
        $"recordingFormatVersion={rec.RecordingFormatVersion} " +
        $"(< DebrisFrameContractFormatVersion={RecordingStore.DebrisFrameContractFormatVersion}). " +
        "v12 and earlier are no longer loadable.");
    rec.RecordingFormatVersion = 0;  // sentinel for caller to skip
    return;
}
```

Caller-side check at the `RecordingSidecarStore.cs` consumers around `:234, :543, :751, :1234`: if `rec.RecordingFormatVersion == 0` after `LoadRecordingFrom`, surface a user-visible "unsupported format" message rather than silently dropping the recording.

### 5.4 Update `.claude/CLAUDE.md` format-version table

Add the new constant; mark v12 and earlier as no-longer-loadable. Reference `TrajectorySidecarBinary.IsSupportedBinaryVersion` and `RecordingTreeRecordCodec.LoadRecordingFrom` as the load-gate sites.

## 6. Concrete touchpoints

### 6.1 Recorder

| File | Lines | Change |
|---|---|---|
| `BackgroundRecorder.cs` | `:4295-4303` | `UpdateBackgroundAnchorDetection` early-return: gate the `ApplyDebrisAnchorContractToState` invocation on `parentDistance ≤ DebrisRelativeSectionExitMeters` (`≤ 550 m` for hysteresis-aware "stay-Relative"; `≤ 500 m` for "enter-Relative"). Outside that range, fall through to the standard non-anchor flow which will open an Absolute section. |
| `BackgroundRecorder.cs` | `:4600-4685` (`ApplyDebrisAnchorContractToState`) | Body unchanged — still anchors on parent, still seeds Relative — but invocation is now gated by §3.1 proximity. |
| `BackgroundRecorder.cs` | `:4722-4728` (third call site in `ApplyBackgroundRelativeOffset`) | Same proximity gate as `:4295-4303`. Inside 550 m → call; outside → skip. |
| `BackgroundRecorder.cs` | `:3338-3460` (debris seed in `InitializeLoadedState`) | Compute initial distance to parent. `≤ 500 m`: seed Relative-to-parent (existing path). `> 500 m`: seed Absolute via `CreateAbsoluteTrajectoryPointFromVessel`. |
| `BackgroundRecorder.cs` | `:1958-1967` | Wire `DebrisProximitySamplingCadence` resolution per §3.1 pseudocode. |
| `BackgroundRecorder.cs` | new field block on `BackgroundVesselState` near `:192-200` | Add `debrisProximityCadence`, `debrisProximityDistanceMeters`, `debrisProximityReason`. |
| `BackgroundRecorder.cs` | new method | `ResolveDebrisParentDistanceMeters` static helper (debris-side guards + parent vessel lookup; cadence tier resolution itself delegates to the shared `ProximitySamplingCadence.Resolve`). |
| `BackgroundRecorder.cs` | `:1868-1878` and `:2046-2053` | Delete Re-Fly settle suppression for debris + `ShouldSuppressParentDebrisForReFlySettle` helper. |
| `BackgroundRecorder.cs` | `:4122-4128` (`ShouldPreferRootPartSurfacePoseForBackgroundSample`) | Keep; update comment to reference the v13 contract (root-part pose continuity still applies). |
| **New file:** `Source/Parsek/ProximitySamplingCadence.cs` | (new) | Shared module per §3.6. Holds `ProximitySamplingTier` enum and `ProximitySamplingCadence.Resolve` / `ProximitySamplingCadence.ResolveSampleInterval` static methods. |
| `FlightRecorder.cs` | `:29` (`ReFlyTreeSamplingCadence` enum) | Delete; replace all references with the shared `ProximitySamplingTier`. Mechanical rename across ~20-40 sites. |
| `FlightRecorder.cs` | `:752-819` (`ResolveActiveReFlyTreeSamplingCadence`) | Refactor: keep the Re-Fly-specific guards (marker / session / tree-id / ut-finite checks), then delegate tier resolution to `ProximitySamplingCadence.Resolve(proximityDistanceMeters, ReFlyTreeFullFidelityProximityRangeMeters, ReFlyTreeHalfFidelityProximityRangeMeters, out reason)`. Behaviour-preserving. |
| `FlightRecorder.cs` | `:867-876` (`ResolveReFlyTreeCadenceSampleInterval`) | Delete; replace call sites with `ProximitySamplingCadence.ResolveSampleInterval`. |
| `FlightRecorder.cs` | `:935` | `HighFidelityProximityRangeMeters = 250.0` (was `200.0`). |
| `FlightRecorder.cs` | `:821-865` | Update the cadence-aware `ResolveEffectiveMaxSampleInterval` and `ResolveEffectiveMinSampleInterval` overloads to take two `ProximitySamplingTier` parameters (one for Re-Fly, one for debris). Re-Fly wins over debris when both non-None. |
| `RecordingStore.cs` | `:105-114` | Add `DebrisFrameContractFormatVersion = 13`; bump `CurrentRecordingFormatVersion`. |

### 6.2 Renderer

| File | Lines | Change |
|---|---|---|
| `DebrisRelativePlaybackPolicy.cs` | `:191-199` (`ShouldRetireFromDiagnostic`) | Drop `"non-relative-section"` from the retire reasons. Under v13, debris in Absolute sections is the new majority case; coverage gate should NOT retire them. |
| `RelativeAnchorResolver.cs` | `:301-310` and `:450-454` | Relax loop-anchored rejection per §3.4: allow Relative anchoring on a loop-anchored recording when the anchored recording is `IsDebris && DebrisParentRecordingId == anchorRecording.RecordingId`. |
| `RelativeAnchorResolver.cs` | `:194, :269, :435, :668, :1045, :1398, :1534, :1898, :2028, :2126, :2138, :2342` | Trim `AnchorRotationReliabilityDecision` parameter from these signatures (the tumble gate's parameter, now orphaned by gate deletion). |
| `GhostPlaybackEngine.cs` | `:2796-3006` | Delete `TryRouteAnchorRotationUnreliable`, `TryRouteAnchorRotationToShadow`, `AnchorRotationUnreliableRoute` enum. |
| `GhostPlaybackEngine.cs` | `:1150-1216` (`RenderInRangeGhost`), `:3297-3339` (`PositionLoopAtPlaybackUT`) | Delete the `anchorRotationRoute` branches; engine falls through to standard section-type dispatch. |
| `GhostPlaybackEngine.cs` | `:1148, :1249, :1256, :1418, :1661, :1847, :1861, :2124, :2134, :2223, :2320, :2331, :2380, :5203, :5242` | Delete reads / writes of `state.anchorRotationShadowRoutedThisFrame`. |
| `GhostPlaybackEngine.cs` | `:2977, :2983, :3031, :3136, :3163, :3223` | Delete reads / writes of `state.parentAnchoredDebrisCoverageRetired`. |
| `GhostPlaybackEngine.cs` | `:2715, :2717` | Update the `ShouldRetireOutsideAuthoredRelativeCoverage` consultation — still relevant for Relative debris sections (0–500 m), but now never fires for Absolute debris sections (per the policy fix above). |
| `GhostPlaybackEngine.cs` | `:5562, :5652, :5668` | `TryResolveInitialStructuralSeedBridgeEndUT` call sites — keep (still relevant for the seed-to-first-sample bridge inside Relative sections at the 0–500 m window). |
| `GhostPlaybackState.cs` | `:93, :99, :152-153` | Delete `anchorRotationShadowRoutedThisFrame` field, `parentAnchoredDebrisCoverageRetired` field, and their resets. |
| `GhostPlaybackEvents.cs` | `:89, :102` | Delete `shadowRouted` parameter from the rendered-frame event. |
| `ParsekFlight.cs` | `:15117-15170` | Delete `ShouldEvaluateAnchorRotationReliability`, `ShouldEvaluateAnchorRotationReliabilityForTesting`, `TryEvaluateAnchorRotationReliability`. |
| `ParsekFlight.cs` | `:16717-16810` | Keep `TryPositionFromRelativeAbsoluteShadow` impl — it's the chain-failure fallback for Relative debris sections. Update the XML doc to reflect the new role (chain-failure fallback inside 0–500 m, not the always-shadow path). |
| `ParsekFlight.cs` | `:22857-22933` | Delete `TryUseLegacyDebrisShadowFallback` and `TryRetireParentAnchoredDebrisOnRecordedAnchorMiss`. |
| `ParsekFlight.cs` | `:22121, :22180, :22274` | Delete `LegacyDebrisShadowGate.IsLegacyDebrisShadowEligible` call sites. |
| `ParsekFlight.cs` | `:22947-22961` | Delete the debris-only guard inside `TryUseRelativeAbsoluteShadowFallback`. The wrapper itself stays. |
| `ParsekFlight.cs` | `:14883-14888` (Re-Fly anchor hold reading `DebrisParentRecordingId`) | Keep — debris is still tied to parent for Re-Fly identity even under v13. Update comment. |
| `IGhostPositioner.cs` | `:78-80` | Keep `TryPositionFromRelativeAbsoluteShadow` signature — used by §3.2 fallback. |
| `RecordingFinalizationCacheApplier.cs` | `:119, :162, :169` | Update for v13's mixed Absolute+Relative debris sections. `TryGetLastAuthoredUT` should consider Absolute sections too, not early-return after Relative-only inspection. |

### 6.3 Codec

| File | Lines | Change |
|---|---|---|
| `TrajectorySidecarBinary.cs` | `:31, :71-72` | Add `DebrisFrameContractBinaryVersion`; update `CurrentBinaryVersion`. |
| `TrajectorySidecarBinary.cs` | `:170-189` | Replace version-selection ladder with unconditional v13 stamp. |
| `TrajectorySidecarBinary.cs` | `:403-416` (`IsSupportedBinaryVersion`) | Replace with `version >= DebrisFrameContractBinaryVersion`. |
| `RecordingTreeRecordCodec.cs` | inside `LoadRecordingFrom` after `:443-449` | Add the `< DebrisFrameContractFormatVersion` rejection per §5.3. |
| `RecordingSidecarStore.cs` | call sites of `LoadRecordingFrom` around `:234, :543, :751, :1234` | Surface unsupported-format errors. |

### 6.4 Audited unchanged

| File | Verification |
|---|---|
| `AnchorDetector.cs` | `IsRecordingAnchorEligible` debris exclusion at `:240` stays. Comment at `:230-239` updated to reflect new contract. |
| `ParsekConfig.cs:30-65` | Unchanged. |
| `SessionMerger.cs` | `:149` carries `DebrisParentRecordingId`; `:803-1440` `absoluteFrames` merge operates per-section and is correct under v13 (debris Relative sections have shadow; debris Absolute sections don't). |
| `EffectiveState.cs:1095-1170` | `DebrisParentRecordingId` lookup for ERS/ELS continues to function. |
| `IPlaybackTrajectory.cs:74` | `DebrisParentRecordingId` getter stays. |
| `GhostMapPresence.cs` | Continues to read `DebrisParentRecordingId` for identification. |
| `ParsekFlight.cs:4880` | Only call site of `RegisterHighFidelityProximityVessel`. The 200→250 m raise propagates with no call-site change. |
| `BackgroundRecorder.cs:1629` | `MaxRecordingGeneration` cascade-cap enforcement unchanged. |
| `Recording.cs` `ApplyDebrisAnchorContract` | Signature unchanged; still sets `DebrisParentRecordingId`. |

### 6.5 Files deleted

- `Source/Parsek/LegacyDebrisShadowGate.cs`
- `Source/Parsek/TumblingParentInterpolationGate.cs`
- `Source/Parsek.Tests/LegacyDebrisShadowGateTests.cs`
- `Source/Parsek.Tests/TumblingParentInterpolationGateTests.cs`

### 6.6 Grep audits before merge

```bash
# Deleted symbols — zero hits expected:
grep -rn "LegacyDebrisShadowGate\|TumblingParentInterpolationGate" Source/Parsek/
grep -rn "TryRouteAnchorRotationUnreliable\|TryRouteAnchorRotationToShadow\|AnchorRotationUnreliableRoute" Source/Parsek/
grep -rn "ShouldEvaluateAnchorRotationReliability" Source/Parsek/
grep -rn "anchorRotationShadowRoutedThisFrame\|parentAnchoredDebrisCoverageRetired" Source/Parsek/
grep -rn "TryUseLegacyDebrisShadowFallback\|TryRetireParentAnchoredDebrisOnRecordedAnchorMiss" Source/Parsek/

# New symbols — expected hits:
grep -rn "ProximitySamplingCadence\|ProximitySamplingTier" Source/Parsek/   # shared module + every old ReFlyTreeSamplingCadence reference
grep -rn "DebrisFullFidelityProximityRangeMeters\|DebrisHalfFidelityProximityRangeMeters" Source/Parsek/
grep -rn "DebrisRelativeSectionExitMeters\|DebrisFrameContractFormatVersion\|DebrisFrameContractBinaryVersion" Source/Parsek/

# Old enum should be gone (renamed to ProximitySamplingTier):
grep -rn "ReFlyTreeSamplingCadence" Source/Parsek/   # zero hits

# Preserved symbols (still used):
grep -rn "DebrisRelativePlaybackPolicy\|DebrisRelativeRecorderPolicy" Source/Parsek/  # call sites at the gated proximity window
grep -rn "TryPositionFromRelativeAbsoluteShadow" Source/Parsek/  # fallback path inside Relative section dispatch

# Re-Fly scope-creep gate:
scripts/grep-audit-non-loop-live-pid.ps1
scripts/grep-audit-ers-els.ps1
```

## 7. Tests

### 7.1 Recorder unit tests

| Test | Setup | Assertion |
|---|---|---|
| `Debris_AtSeparation_WithinProximity_OpensRelative` | Debris created at 50 m from parent | First section is `Relative`, `anchorRecordingId == parent.RecordingId`; `absoluteFrames` populated alongside `frames` |
| `Debris_AtSeparation_BeyondProximity_OpensAbsolute` | Debris created at 600 m from parent (rare) | First section is `Absolute`; `absoluteFrames` null |
| `Debris_DriftsPastHysteresisExit_ClosesRelativeOpensAbsolute` | Debris drifts 490 → 560 m | At first sample where distance > 550 m: Relative closes, Absolute opens |
| `Debris_ReentersWithin500m_ReopensRelative` | Debris at 600 m drifts back to 490 m | Absolute closes, Relative opens, anchor pinned to parent |
| `Debris_HysteresisProtectsAgainstFlapping` | Debris hovering at 495-545 m | No section flip across the hysteresis band |
| `Debris_CadenceTier_Full_AtCloseRange` | Debris at 100 m | Effective `maxSampleInterval == configuredMin` |
| `Debris_CadenceTier_Half_AtMidRange` | Debris at 350 m | Effective `maxSampleInterval == min(configuredMax, 2 × configuredMin)` |
| `Debris_CadenceTier_None_AtFarRange` | Debris at 1500 m | Falls through to `ResolveDebrisAwareMaxSampleInterval` |
| `Debris_CadenceTier_ProportionalToDensity` | Debris at 100 m, Low vs High density | Sample-count ratio matches density-setting ratio |
| `Debris_CadenceTier_HalfNotClobberedByLegacyCap` | Debris at 350 m with Low density (`configuredMin = 0.5 s`) | Half-tier interval is 1.0 s, not capped at 0.5 s |
| `Debris_CadenceOrthogonality_Matrix` (9 combinations) | Cross-product `reFlyTier × debrisTier` (both `ProximitySamplingTier`) | Priority: Re-Fly Full > Re-Fly Half > debris Full > debris Half > existing fallback |
| `ProximitySamplingCadence_Resolve_PureFunction` (table-driven) | Distance × thresholds matrix | `Resolve(0, 250, 500) == Full`, `Resolve(100, 250, 500) == Full`, `Resolve(250, 250, 500) == Full`, `Resolve(251, 250, 500) == Half`, `Resolve(500, 250, 500) == Half`, `Resolve(501, 250, 500) == None`, `Resolve(NaN, ...) == None`, `Resolve(-1, ...) == Full` (negative distance counts as inside) |
| `ProximitySamplingCadence_ResolveSampleInterval_TierMath` | Full / Half / None × configuredMin / configuredMax matrix | `ResolveSampleInterval(Full, 0.1f, 0.5f) == 0.1f`, `ResolveSampleInterval(Half, 0.1f, 0.5f) == 0.2f`, `ResolveSampleInterval(Half, 0.3f, 0.5f) == 0.5f` (clamped to max), `ResolveSampleInterval(None, 0.1f, 0.5f) == 0.5f` |
| `ReFlyResolver_RefactorEqualsOldBehaviour` (regression test) | Sample of pre-refactor inputs → expected outputs | Re-Fly resolver via shared module produces identical results to the pre-refactor implementation across the full input space (marker present/absent, session/tree mismatches, distance buckets) |
| `Debris_AnchorIdentityAlwaysParent_InProximity` | Debris in 0–500 m with sibling debris closer than parent | Anchor remains parent (no nearest-search) |
| `Debris_BothSurfacesWrittenInsideProximity` | Debris at 100 m | `frames` contains anchor-local offsets; `absoluteFrames` contains body-fixed coords; same UT, same count |
| `Debris_DebrisParentRecordingId_StillSetOnSplit` | Standard separation | `child.DebrisParentRecordingId == parent.RecordingId` |
| `Debris_LoopAnchorVesselId_NotInherited` | Debris from loop-anchored parent | `child.LoopAnchorVesselId == 0u` (chain composition handles loop semantics) |
| `Debris_ParentDestroyed_StillRecordsAbsolute` | Parent destroyed mid-recording | Debris continues; samples after destruction are Absolute |
| `Debris_OnRails_NoTrackSection` | Debris transitions to packed | `BackgroundOnRailsState`, no TrackSection |
| `Debris_CascadeSeparation_ParentIdIsImmediateParent` | Debris A sheds B | `B.DebrisParentRecordingId == A.RecordingId` |
| `Debris_CascadeSeparation_DepthCapped` | Cascade depth > `MaxRecordingGeneration` | Further generations dropped (existing rule) |
| `Debris_ReFlySettleSuppression_Removed` | Debris of a Re-Fly settle parent | Samples continue (no suppression gap) |
| `Debris_BoundaryPointContinuity_AtFrameTransition` | Debris crosses 550 m, Relative → Absolute | Last `absoluteFrames` body-fixed position equals first new Absolute section `frames` within 0.1 m |

### 7.2 Renderer unit tests (`Source/Parsek.Tests/GhostPlaybackEngineTests.cs`)

| Test | Setup | Assertion |
|---|---|---|
| `Debris_AbsoluteSection_StandardDispatch` | Debris in Absolute section | `InterpolateAndPosition` invoked; no anchor lookup |
| `Debris_RelativeSection_ChainDispatch` | Debris in Relative section, parent resolvable | `RelativeAnchorResolver` invoked; chain returns `parent.recordedPose × debris.offsetToParent` |
| `Debris_RelativeSection_ChainFails_FallsBackToShadow` | Debris in Relative section, parent recording deleted | Falls back to `TryPositionFromRelativeAbsoluteShadow` reading `absoluteFrames` |
| `Debris_RelativeSection_LoopAnchoredAncestor_ResolvesThroughChain` | F (debris) in Relative on T (tanker, loop-anchored to S) | F's render position = `(S.livePose × T.recordedOffsetToS) × F.recordedOffsetToT` |
| `Debris_RelativeSection_LoopAnchoredAncestor_AcrossIterations` | F as above, sampled at loop iterations 1 and 5 | F's rendered position differs between iterations (follows S's live pose advance) |
| `Debris_ParentTumbling_AbsoluteSection_StillRenders` | Debris in Absolute, parent rotating at 200 °/s | Standard absolute path runs; debris remains visible (no tumble gate to fire) |
| `Debris_ParentDestroyed_AbsoluteSection_StillRenders` | Debris in Absolute, parent recording absent | Renders normally; no visibility coupling |
| `Debris_NotRetiredByCoverageGate_AbsoluteSection` | Debris in Absolute with `frames.Count > 0` | `ShouldRetireOutsideAuthoredRelativeCoverage` does NOT fire |
| `Debris_LongRange_NoSyncedSwing` | Multiple debris pieces all in Absolute, parent rotating | Per-frame position deltas independent of shared rotation channel |
| `RelativeAnchorResolver_DebrisChild_AnchoredOnLoopAnchoredParent_Allowed` | F (debris) anchored on T (loop-anchored) | Resolver returns success (carve-out per §3.4); non-debris anchored on loop-anchored still rejected (regression test) |

### 7.3 Format-version unit tests

| Test | Setup | Assertion |
|---|---|---|
| `V12Recording_LoadFails_BinaryCodec` | Synthetic v12 .prec | `Probe.Supported == false`; load rejected |
| `V11Recording_LoadFails_BinaryCodec` | Synthetic v11 .prec | Same |
| `V12Recording_LoadFails_ConfigNodeCodec` | Synthetic v12 ConfigNode | `LoadRecordingFrom` sets `rec.RecordingFormatVersion = 0`; caller skips |
| `V13Recording_RoundTrip` | Write v13, reload | Identical content; `RecordingFormatVersion == 13` |
| `V13Probe_FormatVersionMatchesRecording` | Newly-written v13 sidecar | `Probe.FormatVersion == rec.RecordingFormatVersion == 13` |

### 7.4 In-game tests

| Test | Scenario | Assertion |
|---|---|---|
| `Debris_SeparationEvent_BothSurfacesRecorded` | Booster separation; debris within proximity | Logs show Relative section open with `frames` + `absoluteFrames` populated |
| `Debris_SeparationEvent_DriftingAway_TransitionsToAbsolute` | Debris drifts past 550 m | Log shows `DEBRIS RELATIVE exit:` event; subsequent samples in Absolute section |
| `Debris_LoopAnchoredAncestor_FollowsLiveAnchor` | Tanker approach to station, loop the recording, fairing jettisoned mid-approach | Across loop iterations, fairing's rendered position follows the station's live orbital position (verified by frame trace at iterations 1, 3, 5) |
| `Debris_LongRange_TumblingParent_NoArtifact` | Parent at 200 °/s, debris > 550 m (Absolute) | Debris ghost renders smoothly (no synced-rotation channel) |
| `Debris_CloseRange_TumblingParent_ChainResolvesThroughShadowFallback` | Parent at 200 °/s, debris < 500 m (Relative), chain math experiences large per-frame error | Either chain math holds (parent dense samples) or shadow fallback engages; ghost remains visible |
| `Debris_MidRange_TumblingParent_HalfCadenceArtifact_VisibleOrNot` | Parent at 200 °/s, debris 250–500 m (Relative, Half cadence) | Document the visual artifact (if any) — this is the tunable regime per §9 |
| `Debris_PhysicsWarp_FrameBoundary_NoArtifact` | Debris crossing 550 m during 2× physics warp | Cadence transitions cleanly |
| `Debris_CadenceProportionalToDensity` | Same scenario at Low vs High density | Sample count in 0–250 m window scales with density |

In-game test files affected by field deletions (`anchorRotationShadowRoutedThisFrame`, `parentAnchoredDebrisCoverageRetired`): `RuntimeTests.cs:4278, 4390-4391, 4524, 4660` — rewrite or remove the shadow-route assertions.

### 7.5 Test fixture

`Source/Parsek.Tests/Generators/DebrisFrameContractRecordingFixture.cs`: builds a synthetic debris recording with:
- Mixed Absolute (outside proximity) and Relative (inside proximity) sections covering a drift sequence
- `frames` + `absoluteFrames` populated correctly inside Relative sections
- `DebrisParentRecordingId` set, no `LoopAnchorVesselId`
- Variant with loop-anchored parent (parent has `LoopAnchorVesselId != 0u`; debris has `DebrisParentRecordingId == parent.RecordingId`, `LoopAnchorVesselId == 0u`)

### 7.6 Existing test file disposition

| File | Action |
|---|---|
| `DebrisRelativePlaybackPolicyTests.cs` | Update (file kept, semantics narrowed) |
| `DebrisRelativeRecorderPolicyTests.cs` | Update (file kept, mixed-section handling) |
| `LegacyDebrisShadowGateTests.cs` | Delete (file deleted) |
| `TumblingParentInterpolationGateTests.cs` | Delete |
| `DebrisParentAnchorContractTests.cs` | Full rewrite for v13 (Relative-inside-500 m + Absolute-outside) |
| `ResolveReFlySettleStabilityTests.cs` | Review; delete cases that asserted `ShouldSuppressParentDebrisForReFlySettle` |
| `Bug362TerminalCrashDebrisTests.cs`, `BoosterStagingSplitTriggerTests.cs`, `BackgroundSplitTests.cs` | Review |
| `RuntimeTests.cs` (in-game) | Rewrite assertions on deleted fields |

## 8. Rollout

Land as a single PR or split per the project's preference:

1. **Format version bump + codec rejection** (commit 1). Constants + `IsSupportedBinaryVersion` prune + `LoadRecordingFrom` early-exit + `RecordingSidecarStore` consumer surface. After this commit, v12/v11 saves are rejected at load.
2. **Renderer cleanup** (commit 2). Delete the always-shadow router, the tumble-gate predicates and helpers, `LegacyDebrisShadowGate`, `TumblingParentInterpolationGate`, the two state fields and their 23 read/write sites. Trim `RelativeAnchorResolver` signatures. Compile clean; some debris-related tests fail (rewritten in commit 4).
3. **Resolver carve-out** (commit 3). Relax `RelativeAnchorResolver` loop-anchored rejection for debris-parent-anchored case (§3.4). Add the targeted regression test.
4. **Recorder change** (commit 4). Gate `ApplyDebrisAnchorContractToState` on proximity; simplify debris seed; add cadence tier resolver, state, and wiring; raise `HighFidelityProximityRangeMeters` to 250 m; remove Re-Fly settle suppression for debris.
5. **Tests** (commit 5): test fixture, recorder unit tests, renderer unit tests, format-version tests, in-game test rewrites.
6. **Docs** (commit 6): CHANGELOG; `.claude/CLAUDE.md` format-version table; close relevant `docs/dev/todo-and-known-bugs.md` entries.

Pre-merge:
- `dotnet test` all green
- Re-Fly in-game tests pass (scope-creep gate)
- §6.6 grep audits return expected results
- `scripts/grep-audit-*.ps1` clean
- Playtest: separation events, cadence transitions, the canonical tanker-approach-to-station looped scenario with mid-approach jettison, long-range drift, density-setting proportionality

## 9. Out of scope (follow-ups)

- **Future recorders using the shared cadence module.** The `ProximitySamplingCadence` module (§3.6) is reusable. Candidate future consumers: a co-bubble formation-flying recorder, an approach-corridor recorder near a station, a science-rover proximity recorder. Each supplies its own thresholds and guards. Not in scope for this PR but the module's API is designed for it.
- **BG-parent hi-fi sampling extension**: extending `RegisterHighFidelityProximityVessel` to BG-recorded parents of in-proximity debris. Useful if playtest reveals chain-math jitter due to sparse parent samples inside the 500 m window. The body-fixed shadow on the same Relative section is a safety net regardless.
- **Tunables for the 250–500 m Half-cadence Relative window**: this regime is the most synced-rotation-prone zone (lever arm up to 500 m × parent slerp at 2 × min cadence). If playtest shows visible swing during high-rate parent rotation in this zone, options to address:
  1. Drop the cadence in 250–500 m back to Full (uniform Full inside the whole Relative window) — doubles storage in the outer ring.
  2. Reinstate the tumble-gate as a renderer-time shadow fallback (PR #803-style routing) but limited to high parent rates inside the Relative window.
  3. Recorder-side rate filter: skip Relative-offset emission (keep only body-fixed shadow) when parent rate exceeds threshold inside 250–500 m.
  4. Shrink the Relative window back to 250 m total.
  None of these are needed if the user-visible artifact is acceptable — "we can tune later" per user direction.
- **Visibility decoupling product validation**: under v13 debris in Absolute renders independent of parent. If playtest prefers retiring debris when parent is destroyed, a follow-up adds the check.
- **`DebrisParentRecordingId` UI surfacing**: filter / group debris by parent in the recordings table.
- **Recorder denser sampling for fast-debris-rotation cases**: independent concern affecting debris attitude, not position.
- **Resolver carve-out generalization**: currently the loop-anchored-as-anchor allowance is debris-only. If non-debris cases emerge that legitimately want to chain through a loop-anchored ancestor, generalize the gate.

## 10. Plan evolution

Six drafts on the branch arrived at this design:

- `4b9a9bd` — first draft: proximity-gated 2300/2500 m, revert PR #803.
- `257ff31` — second draft (first review): keep PR #803 inside bounded Relative window.
- `17cb5db` — third draft (user feedback): absolute-primary always; tiered cadence; experimental 0–250 m Relative shadow.
- `4826ede` — fourth draft (first Opus review): drop experimental Relative; debris Absolute-only.
- `3d9a5fe` / `bc85214` — fifth draft (second Opus review + loop-anchored debris): debris inherits `LoopAnchorVesselId` from loop-anchored parent.
- `9662558` — sixth draft: user's insight that *every* debris recording within proximity should carry the relative-to-parent data; chain composition through loop-anchored ancestors handles the loop case naturally without `LoopAnchorVesselId` inheritance. No experimental framing — the relative surface has a concrete consumer (loop scenarios) and a real benefit (sub-metre geometric accuracy at close range). Relative window was 250 m.
- `ede8620` — seventh draft: Relative shadow window extended to 500 m total, with Half cadence in 250–500 m. Frame hysteresis exit moved 280 m → 550 m. §9 enumerates remediation options if playtest shows artifacts in the 250–500 m + tumbling-parent regime.
- **current** — eighth draft: proximity-cadence mechanism extracted as a shared `ProximitySamplingCadence` module (per user direction: "we should extract this proximity cadence mechanism as a module that can be used by any recorder"). The old `FlightRecorder.ReFlyTreeSamplingCadence` enum and resolver are refactored to delegate tier resolution to the shared module (behaviour-preserving; same thresholds, same tier semantics). Debris uses the same module with its own thresholds. Adds one Re-Fly touch — the cadence resolver — but observable behaviour is preserved by construction (regression test included).
